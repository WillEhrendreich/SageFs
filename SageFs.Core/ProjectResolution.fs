namespace SageFs

open System
open System.IO
open SageFs.WorkerProtocol

/// Whether a session's project request was ever earned an honest Ready.
///
/// `create_session` with `projects: []` does not mean "no projects" — it
/// means "auto-discover whatever solution/project sits in the working
/// directory" (see `ProjectLoading.loadSolution`). That single API shape
/// covers two facts a user or agent needs told apart:
///   - nothing was ever asked for, and nothing exists to auto-discover — a
///     genuine scratch REPL, which is fine and usable as-is.
///   - something was named, or something WAS there to auto-discover, and
///     loading it resolved to zero usable projects — a real defect (a bad
///     path, a project that fails to build, a TFM mismatch, a repo-local SDK
///     the loader ignored). Two of these bit us in one day: multi-targeted
///     references resolving to the wrong TFM, and a repo-local SDK being
///     ignored. Both presented as "Ready", loadedProjects: [] — a session
///     that reported healthy while every subsequent eval failed with
///     "not defined".
///
/// `SessionManager`'s `WorkerReportedReady` handler is the one place a
/// worker's self-reported Ready becomes the daemon's registered truth (see
/// `SessionStatus.Ready` in `WorkerProtocol.fs`, trusted verbatim there
/// before this module existed). This DU is what that handler classifies the
/// worker's own report against BEFORE trusting it — so `Ready` in the
/// daemon's own registry, and therefore in every reader of it
/// (`get_fsi_status`, `/api/sessions`, `/health`, the dashboard — they all
/// derive from `SessionLifecycleStatus`/`SessionHealth.classify`), only ever
/// means what it says.
[<RequireQualifiedAccess>]
type ProjectResolution =
  /// Nothing was named, and nothing exists to auto-discover in the working
  /// directory. A deliberate or accidental scratch session either way — bare
  /// FSI is exactly what was on offer, so there is nothing to be broken.
  | NoneRequested
  /// Something was named or discoverable, and at least one project actually
  /// resolved. The ordinary, quiet case.
  | Resolved
  /// Something was named or discoverable, and NONE of it resolved.
  /// `Requested` is exactly what was asked for (explicit paths, or the
  /// auto-discovered candidates found on disk) so the reader can act on it —
  /// check the path, build the project, check the TFM.
  | RequestedButUnresolved of requested: string list

module ProjectResolution =

  /// Lists the `.fsproj`/`.sln`/`.slnx` files directly under `dir` — the
  /// SAME two-glob shape `ProjectLoading.loadSolution` uses for its own
  /// auto-discovery (solutions win over a bare `.fsproj` scan; see
  /// `loadSolution`'s own comment: "When projects are given explicitly,
  /// don't auto-discover .sln files. Only auto-discover when neither
  /// projects nor solutions is specified"). Kept here, independent of
  /// `ProjectLoading`, purely as a directory listing — it never invokes
  /// MSBuild and never needs to: this module only asks "was there
  /// something to find", not "does it load".
  let listCandidatesOnDisk (dir: string) : string list =
    try
      let entries = Directory.EnumerateFiles dir |> Seq.toList
      let solutions =
        entries
        |> List.filter (fun s ->
          s.EndsWith(".sln", StringComparison.Ordinal) || s.EndsWith(".slnx", StringComparison.Ordinal))
      match solutions with
      | _ :: _ -> solutions
      | [] -> entries |> List.filter (fun s -> s.EndsWith(".fsproj", StringComparison.Ordinal))
    with _ ->
      // A directory that can't be listed (removed, permission denied) is
      // advisory-only here — the primary signal is always the caller's own
      // explicit request, checked first in `classify` below. Fail open on
      // this fallback rather than turning a listing error into a false
      // "requested but unresolved" fault.
      []

  /// Pure decision: `listCandidates` is injected so this is unit-testable
  /// without a filesystem, and so production and tests can never drift on
  /// what "candidates on disk" means.
  ///
  /// `explicitlyRequested` is exactly what the caller named (`create_session
  /// projects=[...]`); an empty list means "auto-discover", so the
  /// directory listing is consulted ONLY then — an explicit non-empty
  /// request is authoritative and is never second-guessed by what's on
  /// disk.
  let classify
    (listCandidates: string -> string list)
    (workingDir: string)
    (explicitlyRequested: string list)
    (resolvedCount: int)
    : ProjectResolution =
    let requested =
      match explicitlyRequested with
      | _ :: _ -> explicitlyRequested
      | [] -> listCandidates workingDir
    match requested, resolvedCount with
    | [], _ -> ProjectResolution.NoneRequested
    | rs, 0 -> ProjectResolution.RequestedButUnresolved rs
    | _, _ -> ProjectResolution.Resolved

  /// `classify` wired to the real filesystem — what production calls.
  let classifyOnDisk (workingDir: string) (explicitlyRequested: string list) (resolvedCount: int) : ProjectResolution =
    classify listCandidatesOnDisk workingDir explicitlyRequested resolvedCount

  /// One line explaining the fault, naming what was asked for and what to
  /// do about it — the same doctrine as every other failure in this repo:
  /// say what it was and what to do.
  let unresolvedReason (requested: string list) : string =
    sprintf
      "Session requested %s but none of it resolved to a loadable project (loadedProjects: []). Check the path(s) exist, that the project builds (dotnet build), and that its target framework matches what SageFs expects. Requested: %s"
      (match requested with [ _ ] -> "a project" | _ -> "projects")
      (String.concat ", " requested)

  /// Whether a worker's raw, live `Ready` report is trustworthy YET.
  ///
  /// `WorkerReportedReady` (SessionManager.fs) is the ONE place that commits
  /// `SessionLifecycleStatus.Ready` and `ProjectRoles` together, atomically,
  /// after this module's `classify` gate passes — that pairing is never torn.
  /// But three OTHER readers (`get_fsi_status`, `/api/sessions`, `/health`)
  /// each poll the worker process directly for freshness, and a worker
  /// answers `Ready` the instant IT thinks it is done — independent of, and
  /// often earlier than, the daemon's own mailbox getting around to running
  /// that gate. A raw `Ready` taken at face value here is exactly the bug:
  /// "Ready" becomes observable (and, via get_fsi_status's write-back,
  /// PERSISTED into the registry) with `loadedProjects` still empty.
  ///
  /// This only judges the EXPLICIT-request case (mirrors `classify`'s
  /// `_ :: _ -> requested` branch) using facts already held in memory on
  /// every hot poll — no disk IO. The auto-discover case (`requested = []`)
  /// needs a directory listing to tell "nothing to find" apart from "not
  /// resolved yet", which `classifyOnDisk` already does once, for real, on
  /// the worker's own warmup path; that path remains the sole source of
  /// truth for auto-discovered sessions. This guard exists only to stop an
  /// EXPLICIT request's raw `Ready` from leaking out (or getting persisted)
  /// before it is earned — not to re-run classification on every read.
  let earnedReady (requested: string list) (resolvedCount: int) (reported: SessionStatus) : bool =
    match reported, requested with
    | SessionStatus.Ready, (_ :: _) -> resolvedCount > 0
    | _ -> true

  /// The outcome of reconciling a live worker report against the registry
  /// through the earned-Ready gate above.
  [<RequireQualifiedAccess>]
  type ReconciledStatus =
    /// The report was trustworthy — either it wasn't claiming an unearned
    /// Ready, or Ready has genuinely already been earned. Reconciled exactly
    /// as `SessionLifecycleStatus.ofWorkerReport` always has.
    | Reconciled of SessionLifecycleStatus
    /// The worker claims Ready but `ProjectRoles` doesn't yet carry what was
    /// explicitly requested — Ready has not been earned. Carries the
    /// registry's OWN current status unchanged: callers must keep showing
    /// that (never the raw report) and must NEVER write this back into the
    /// registry — only `WorkerReportedReady`'s own gate may ever commit the
    /// Ready + ProjectRoles pairing.
    | NotYetEarned of current: SessionLifecycleStatus

  /// `SessionLifecycleStatus.ofWorkerReport`, gated by `earnedReady` — the
  /// one function every Ready-observing surface (`get_fsi_status`,
  /// `/api/sessions`, `/health`) should reconcile a live worker report
  /// through, so none of them can ever display (or persist) "Ready" a beat
  /// before `loadedProjects` is populated for an explicit request.
  let reconcile
    (requested: string list)
    (resolvedCount: int)
    (current: SessionLifecycleStatus)
    (reported: SessionStatus)
    : ReconciledStatus =
    match earnedReady requested resolvedCount reported with
    | true -> ReconciledStatus.Reconciled (SessionLifecycleStatus.ofWorkerReport current reported)
    | false -> ReconciledStatus.NotYetEarned current
