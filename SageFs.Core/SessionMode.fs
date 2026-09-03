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
  UpdateSessionStatus: SessionId -> SessionStatus -> Task<unit>
  /// Notify that a worker died unexpectedly (pipe broken mid-request).
  /// Closes the race window between pipe failure and proc.Exited event firing.
  NotifyWorkerDied: SessionId -> unit
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
  }
