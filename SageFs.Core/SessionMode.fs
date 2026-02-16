namespace SageFs

open System.Threading.Tasks
open SageFs.WorkerProtocol

/// Functions a daemon provides for managing worker sessions.
/// Pure data — no actor, no transport, just function signatures.
type SessionManagementOps = {
  CreateSession: string list -> string -> Task<Result<string, SageFsError>>
  ListSessions: unit -> Task<string>
  StopSession: string -> Task<Result<string, SageFsError>>
  /// Get the session proxy for routing commands to a specific worker.
  GetProxy: SessionId -> Task<SessionProxy option>
  /// Get the SessionInfo for a specific session.
  GetSessionInfo: SessionId -> Task<SessionInfo option>
}

module SessionManagementOps =
  /// A no-op stub for testing — all operations return sensible defaults.
  let stub : SessionManagementOps = {
    CreateSession = fun _ _ -> Task.FromResult(Result.Error (SageFsError.SessionCreationFailed "Not available"))
    ListSessions = fun () -> Task.FromResult("No sessions")
    StopSession = fun _ -> Task.FromResult(Result.Error (SageFsError.SessionCreationFailed "Not available"))
    GetProxy = fun _ -> Task.FromResult(None)
    GetSessionInfo = fun _ -> Task.FromResult(None)
  }
