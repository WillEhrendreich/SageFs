namespace SageFs

open System.Threading.Tasks
open SageFs.WorkerProtocol

/// Functions a daemon provides for managing worker sessions.
/// Pure data — no actor, no transport, just function signatures.
type SessionManagementOps = {
  CreateSession: string list -> string -> WorkflowTypes.SessionWorkflow -> Task<Result<string, SageFsError>>
  ListSessions: unit -> Task<string>
  StopSession: string -> Task<Result<string, SageFsError>>
  /// Purge — stop the session AND remove its entry from the .sagefm manifest (gone from the resume picker too).
  /// For corrupted state, this is the equivalent of deleting obj/bin folders.
  PurgeSession: string -> Task<Result<string, SageFsError>>
  /// Stop worker, optionally rebuild, respawn with same session ID.
  /// Solves CLR assembly identity cache: fresh process = fresh assemblies.
  RestartSession: SessionId -> bool -> Task<Result<string, SageFsError>>
  /// Get the session proxy for routing commands to a specific worker.
  GetProxy: SessionId -> Task<SessionProxy option>
  /// Get the SessionInfo for a specific session.
  GetSessionInfo: SessionId -> Task<SessionInfo option>
  /// Get all active sessions with their metadata.
  GetAllSessions: unit -> Task<SessionInfo list>
  /// Update the daemon-side snapshot status for an existing session.
  /// Used when the worker changes phase without a full process restart.
  UpdateSessionStatus: SessionId -> SessionLifecycleStatus -> Task<unit>
  /// Notify that a worker died unexpectedly (pipe broken mid-request).
  /// Closes the race window between pipe failure and proc.Exited event firing.
  NotifyWorkerDied: SessionId -> unit
  /// Ask the session's owner to run `project` ("Run App"). The owner decides:
  /// it refuses while an app is running or starting, and an accepted run gets
  /// the generation every later step of it must carry.
  ClaimRun: SessionId -> string -> Task<Result<AppRun.RunClaim, SageFsError>>
  /// Ask the owner to stop the app. The stop takes a new generation, so no
  /// step of an earlier run can be applied after it.
  ClaimStop: SessionId -> Task<Result<AppRun.StopClaim, SageFsError>>
  /// Record one step of a run, applied only while its generation owns the app.
  AdvanceRun: SessionId -> AppRun.RunGeneration -> AppRun.AppRunState -> Task<AppRun.StepOutcome>
  /// Record that run `runId` ended — applied only while that run is still current.
  EndAppRun: SessionId -> AppRun.RunGeneration -> string -> AppRun.AppRunState -> Task<AppRun.RunEnd>
  /// Wait until the session's worker is Ready (e.g. after a workflow restart).
  AwaitReady: SessionId -> System.TimeSpan -> Task<Result<unit, SageFsError>>
  /// Switch the workflow for a session.
  SwitchWorkflow: string -> WorkflowTypes.SessionWorkflow -> Task<Result<string, SageFsError>>
}

module SessionManagementOps =
  /// A no-op stub for testing — all operations return sensible defaults.
  let stub : SessionManagementOps = {
    CreateSession = fun _ _ _ -> Task.FromResult(Result.Error (SageFsError.SessionCreationFailed "Not available"))
    ListSessions = fun () -> Task.FromResult("No sessions")
    StopSession = fun _ -> Task.FromResult(Result.Error (SageFsError.SessionCreationFailed "Not available"))
    PurgeSession = fun _ -> Task.FromResult(Result.Error (SageFsError.SessionCreationFailed "Not available"))
    RestartSession = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
    GetProxy = fun _ -> Task.FromResult(None)
    GetSessionInfo = fun _ -> Task.FromResult(None)
    GetAllSessions = fun () -> Task.FromResult([])
    UpdateSessionStatus = fun _ _ -> Task.FromResult(())
    NotifyWorkerDied = fun _ -> ()
    ClaimRun = fun sid _ -> Task.FromResult(Result.Error (SageFsError.SessionNotFound (SessionId.value sid)))
    ClaimStop = fun sid -> Task.FromResult(Result.Error (SageFsError.SessionNotFound (SessionId.value sid)))
    AdvanceRun = fun _ _ _ -> Task.FromResult(AppRun.StepOutcome.Stale AppRun.AppRunState.NotRunning)
    EndAppRun = fun _ _ _ _ -> Task.FromResult(AppRun.RunEnd.NotCurrent)
    AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
    SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
  }
