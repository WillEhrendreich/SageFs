namespace SageFs

open SageFs.WorkerProtocol

/// Whether a session's explicit target was earned an honest Ready.
///
/// `SessionProjectTarget` is the closed create contract: project paths,
/// solution paths, and an explicit `Bare`. There is no empty list meaning
/// "discover whatever is in the directory". Two readiness facts remain:
///   - Bare intentionally loads nothing and is a valid scratch REPL.
///   - A named project/solution that resolves to zero usable projects is a
///     real failure, even if the worker process itself reports Ready.
///
/// `SessionManager`'s `WorkerReportedReady` handler is the one place a
/// worker's self-reported Ready becomes daemon registry truth. This module is
/// the pure gate that runs before trusting it.
[<RequireQualifiedAccess>]
type ProjectResolution =
  /// The caller explicitly requested a bare REPL. Nothing was supposed to
  /// load, so zero resolved projects is expected.
  | NoneRequested
  /// At least one explicitly named project/solution resolved.
  | Resolved
  /// One or more targets were named, but none resolved. The exact requested
  /// paths are retained so the failure can say what to check.
  | RequestedButUnresolved of requested: string list

module ProjectResolution =

  /// Classify the target and the worker's resolved-project count. Pure: this
  /// performs no filesystem discovery and no implicit empty-list handling.
  let classify (targets: SessionProjectTarget list) (resolvedCount: int) : ProjectResolution =
    match targets with
    | [ SessionProjectTarget.Bare ] -> ProjectResolution.NoneRequested
    | _ ->
      match resolvedCount with
      | 0 -> ProjectResolution.RequestedButUnresolved (SessionProjectTarget.paths targets)
      | _ -> ProjectResolution.Resolved

  let classifyOnDisk (_workingDir: string) (targets: SessionProjectTarget list) (resolvedCount: int) : ProjectResolution =
    classify targets resolvedCount

  /// One line explaining the fault, naming what was asked for and what to
  /// do about it.
  let unresolvedReason (requested: string list) : string =
    sprintf
      "Session requested %s but none of it resolved to a loadable project (loadedProjects: []). Check the path(s) exist, that the project builds (dotnet build), and that its target framework matches what SageFs expects. Requested: %s"
      (match requested with [ _ ] -> "a project" | _ -> "projects")
      (String.concat ", " requested)

  /// Whether a worker's raw Ready report is trustworthy yet.
  /// A Bare target is intentionally ungated; a named target must have at least
  /// one resolved project before Ready can become registry truth.
  let earnedReady (targets: SessionProjectTarget list) (resolvedCount: int) (reported: SessionStatus) : bool =
    match targets, reported with
    | [ SessionProjectTarget.Bare ], _ -> true
    | _, SessionStatus.Ready -> resolvedCount > 0
    | _ -> true

  /// The outcome of reconciling a live worker report against the registry
  /// through the earned-Ready gate above.
  [<RequireQualifiedAccess>]
  type ReconciledStatus =
    | Reconciled of SessionLifecycleStatus
    | NotYetEarned of current: SessionLifecycleStatus

  /// `SessionLifecycleStatus.ofWorkerReport`, gated by `earnedReady`.
  let reconcile
    (targets: SessionProjectTarget list)
    (resolvedCount: int)
    (current: SessionLifecycleStatus)
    (reported: SessionStatus)
    : ReconciledStatus =
    match earnedReady targets resolvedCount reported with
    | true -> ReconciledStatus.Reconciled (SessionLifecycleStatus.ofWorkerReport current reported)
    | false -> ReconciledStatus.NotYetEarned current
