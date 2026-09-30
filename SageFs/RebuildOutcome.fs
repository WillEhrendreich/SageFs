namespace SageFs

open System

/// The outcome of the last rebuild an agent requested for a session. A
/// build-first rebuild that fails keeps the live worker serving, so no fault
/// event ever fires. `get_session_status` (where hard_reset points agents) is
/// the one place the outcome, and the compiler errors, are reported.
///
/// Split out of Mcp.fs, which is over its line budget: this is a type and the
/// pure functions over it, with no orchestration in them. It is a top-level
/// `SageFs` type so everything that says `RebuildOutcome.X` still finds it.
[<RequireQualifiedAccess>]
type RebuildOutcome =
  | InProgress of startedAt: DateTime
  | Succeeded of finishedAt: DateTime
  /// The build failed; the session keeps serving its previous build.
  | FailedStillServing of error: SageFsError * finishedAt: DateTime
  /// The build failed and no worker is serving the session.
  | FailedNotServing of error: SageFsError * finishedAt: DateTime

module RebuildOutcome =
  /// Classify a finished rebuild from the owner's reply and the session's
  /// registry status afterwards — the SessionManager decides whether the
  /// session still serves; this only reads its verdict.
  let ofResult (finishedAt: DateTime) (result: Result<string, SageFsError>) (after: WorkerProtocol.SessionLifecycleStatus option) =
    match result, after with
    | Ok _, _ -> RebuildOutcome.Succeeded finishedAt
    | Error e, Some (WorkerProtocol.SessionLifecycleStatus.Ready _ | WorkerProtocol.SessionLifecycleStatus.Evaluating _ | WorkerProtocol.SessionLifecycleStatus.Building _) ->
      RebuildOutcome.FailedStillServing (e, finishedAt)
    | Error e, _ -> RebuildOutcome.FailedNotServing (e, finishedAt)

  /// The payload's name for what this outcome is. Exhaustive, so a new
  /// outcome cannot be added without deciding how it is reported.
  let kind (outcome: RebuildOutcome) : SessionStatusPayload.RestartKind =
    match outcome with
    | RebuildOutcome.InProgress _ -> SessionStatusPayload.RestartKind.InProgress
    | RebuildOutcome.Succeeded _ -> SessionStatusPayload.RestartKind.Succeeded
    | RebuildOutcome.FailedStillServing _ -> SessionStatusPayload.RestartKind.FailedStillServing
    | RebuildOutcome.FailedNotServing _ -> SessionStatusPayload.RestartKind.FailedNotServing

  let private clock (at: DateTime) = at.ToLocalTime().ToString("HH:mm:ss")

  /// This daemon's own loaded SageFs.Core version (reflected in-process —
  /// never the session's; see `describe`'s Succeeded case).
  let private daemonCoreVersion () : string =
    typeof<SageFsError>.Assembly.GetName().Version
    |> Option.ofObj |> Option.map (fun v -> v.ToString()) |> Option.defaultValue "unknown"

  /// F5b Phase 2, corrected: used to report the DAEMON's own SageFs.Core as
  /// THE SESSION's — an ordinary session skips adoption, so its worker
  /// loads whatever SageFs.Core.dll its own build produced, routinely
  /// newer than the daemon right after `rebuild=true` (confirmed live:
  /// daemon 0.6.782.0, worker 0.6.789+). `sessionCoreVersion` is now ground
  /// truth from the worker (`WorkerMain.workerCoreVersion`, on
  /// `WorkerStatusSnapshot.CoreVersion`), named separately from the daemon.
  let describe (now: DateTime) (sessionCoreVersion: string option) = function
    | RebuildOutcome.InProgress startedAt ->
      sprintf "🔨 Rebuild in progress (%.0fs) — the current worker keeps serving until the new build is ready." (now - startedAt).TotalSeconds
    | RebuildOutcome.Succeeded finishedAt ->
      let daemon = daemonCoreVersion ()
      let agreement =
        match sessionCoreVersion with
        | Some v when v = daemon -> "same build as the daemon"
        | Some _ -> sprintf "differs from the daemon's own SageFs.Core %s — expected right after a rebuild" daemon
        | None -> "daemon's own SageFs.Core version could not be compared"
      sprintf "✅ Last rebuild succeeded at %s — the session's SageFs.Core %s is now loaded (respawned + re-adopted), %s." (clock finishedAt) (sessionCoreVersion |> Option.defaultValue "unknown") agreement
    | RebuildOutcome.FailedStillServing (error, finishedAt) ->
      sprintf "⚠️ Last rebuild failed at %s — still serving the previous build.\n%s" (clock finishedAt) (SageFsError.describeForAgent error)
    | RebuildOutcome.FailedNotServing (error, finishedAt) ->
      sprintf "🔴 Last rebuild failed at %s — no worker is serving this session.\n%s" (clock finishedAt) (SageFsError.describeForAgent error)
