namespace SageFs

open System

/// The outcome of the last rebuild of a session. A build-first rebuild that
/// fails keeps the live worker serving, so no fault event ever fires, and a
/// rebuild whose build has not finished looks exactly like one that failed
/// quietly. `SessionManager` is the one owner that knows, so it records the
/// outcome on the session (`SessionInfo.Rebuild`) and every surface (MCP
/// status, /api/sessions, the dashboard) reads it from there.
///
/// It lives in Core, not the daemon project, because the manager writes it.
/// A top-level `SageFs` type so everything that says `RebuildOutcome.X` still
/// finds it.
[<RequireQualifiedAccess>]
type RebuildOutcome =
  | InProgress of startedAt: DateTime
  | Succeeded of finishedAt: DateTime
  /// The build failed; the session keeps serving its previous build.
  | FailedStillServing of error: SageFsError * finishedAt: DateTime
  /// The build failed and no worker is serving the session.
  | FailedNotServing of error: SageFsError * finishedAt: DateTime

/// What a session's rebuild history says. Nothing recorded is its own case: a
/// session nobody rebuilt must not be reported as a rebuild that succeeded.
[<RequireQualifiedAccess>]
type LastRebuild =
  | NeverRebuilt
  | Latest of RebuildOutcome

module RebuildOutcome =
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
