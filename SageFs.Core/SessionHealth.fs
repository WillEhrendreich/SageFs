namespace SageFs

open SageFs.WorkerProtocol
open SageFs.ProjectLoading
open SageFs.WarmUp

/// A session's actual, user-meaningful usability — distinct from
/// `SessionLifecycleStatus`/`SessionStatus`, which describe only whether the
/// WORKER PROCESS is alive. The repo's own VS Code client already admits the
/// gap (`sagefs-vscode/src/SessionsTreePure.fs`: "`Ready` only ever meant
/// 'the worker process is alive'"), and two independent audits measured the
/// consequence: a session can be Ready, green, `faultReason: null`, having
/// loaded nothing — every symbol lookup then fails with "not defined" — and
/// nothing in the daemon log or status ever says so.
///
/// `SessionHealth` is computed ONCE, PURELY, from facts the daemon already
/// has (worker lifecycle status, what the session actually loaded, what
/// warmup reported), and surfaced on every client-facing session view so
/// "Ready" stops silently meaning "usable."
[<RequireQualifiedAccess>]
type SessionHealth =
  /// Worker lifecycle hasn't reached a judgeable state yet (starting,
  /// building, restarting) — health cannot be assessed yet.
  | Starting
  /// Worker is Ready/Evaluating and what warmup expected to load, loaded.
  /// The common case — must stay quiet, not noisy.
  | Healthy
  /// Worker is Ready/Evaluating, but something a user would expect to work
  /// will not. `reason` names what is wrong and what to do about it.
  | Degraded of reason: string
  /// Worker is not usable at all (faulted or stopped). `reason` names why.
  | Failed of reason: string

module SessionHealth =

  let label = function
    | SessionHealth.Starting -> "Starting"
    | SessionHealth.Healthy -> "Healthy"
    | SessionHealth.Degraded _ -> "Degraded"
    | SessionHealth.Failed _ -> "Failed"

  /// The reason string, when the verdict carries one.
  let reason = function
    | SessionHealth.Starting
    | SessionHealth.Healthy -> None
    | SessionHealth.Degraded r
    | SessionHealth.Failed r -> Some r

  /// One advisory line for text status output (get_fsi_status), matching the
  /// convention already used there for rebuild/self-host advisory lines.
  /// `None` for the quiet, common-case verdicts — never adds noise to Healthy.
  let describeForAgent (health: SessionHealth) : string option =
    match health with
    | SessionHealth.Starting
    | SessionHealth.Healthy -> None
    | SessionHealth.Degraded r -> Some (sprintf "⚠️  Health: Degraded — %s" r)
    | SessionHealth.Failed r -> Some (sprintf "❌ Health: Failed — %s" r)

  /// JSON-friendly projection for HTTP APIs (`/api/sessions`).
  let toJson (health: SessionHealth) =
    {| status = label health; reason = reason health |}

  /// Reuses the real warmup failure data (name + the actual error message
  /// warmup recorded) rather than inventing new prose that could drift from
  /// it — the auto-open failure text already exists and is good.
  let private autoOpenFailureReason (failures: WarmupOpenFailure list) =
    let detail =
      failures
      |> List.map (fun f -> sprintf "%s — %s" f.Name f.ErrorMessage)
      |> String.concat "; "
    sprintf "Warmup reported %d failed open(s): %s" (List.length failures) detail

  let private nothingLoadedReason =
    "Session is Ready but nothing was loaded: 0 assemblies, 0 namespaces opened, though a project was resolved. " +
    "The project likely has never been built — run `dotnet build` on it, then hard_reset_fsi_session (rebuild=true)."

  /// The one place a session's actual usability is decided. Pure — no IO, no
  /// worker/daemon dependency — so the dashboard, MCP, and any future client
  /// all classify the exact same three facts into the exact same verdict.
  ///
  /// The single Degraded trigger is "a project was expected but NOTHING
  /// usable came out of warmup" — zero assemblies loaded AND zero namespaces
  /// opened. Both measured cases fit this exactly ("Warmup: 0 assemblies, 0
  /// opened, 1 failed" and the silent un-built-project variant). Live
  /// verification (see SessionHealthTests.fs) also proved the OBVIOUS-looking
  /// alternative — "any recorded FailedOpens is Degraded" — is wrong and
  /// noisy: a normal, freshly-`dotnet build`-ed single-entry-point project
  /// legitimately has nothing for auto-open to open (there is no other
  /// module/namespace to discover), so warmup records the exact same
  /// "no namespaces/modules were found to open" failure text as the broken
  /// case — yet its assembly DID load and evaluating fully-qualified code
  /// against it succeeds. The assemblies-loaded fact is what tells them
  /// apart, not the presence of a warmup failure entry.
  ///
  /// A bare/empty session (`projectRoles = []`) that loaded nothing is NOT
  /// degraded either — nothing was expected, so nothing is wrong.
  let classify
    (status: SessionLifecycleStatus)
    (projectRoles: ClassifiedProject list)
    (warmup: WarmupContext option)
    : SessionHealth =
    match status with
    | SessionLifecycleStatus.Faulted reasonOpt ->
      SessionHealth.Failed (reasonOpt |> Option.defaultValue "Session faulted for an unspecified reason.")
    | SessionLifecycleStatus.Stopped ->
      SessionHealth.Failed "Session is stopped."
    | SessionLifecycleStatus.Starting _
    | SessionLifecycleStatus.Building _
    | SessionLifecycleStatus.Restarting _ ->
      SessionHealth.Starting
    | SessionLifecycleStatus.Ready _
    | SessionLifecycleStatus.Evaluating _ ->
      match warmup with
      // No warmup data available (yet) — nothing to be suspicious about;
      // don't invent a problem the daemon can't yet see.
      | None -> SessionHealth.Healthy
      | Some ctx ->
        let nothingLoaded =
          not (List.isEmpty projectRoles)
          && List.isEmpty ctx.AssembliesLoaded
          && List.isEmpty ctx.NamespacesOpened
        match nothingLoaded with
        | false -> SessionHealth.Healthy
        | true ->
          // Reuse the real warmup failure text when warmup recorded one
          // (both measured cases did) rather than inventing new prose;
          // fall back to a generic reason only if warmup somehow reported
          // no failure at all despite loading nothing.
          match ctx.FailedOpens with
          | _ :: _ as failures -> SessionHealth.Degraded (autoOpenFailureReason failures)
          | [] -> SessionHealth.Degraded nothingLoadedReason
