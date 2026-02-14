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
}

/// How the MCP layer routes session operations.
/// Embedded: single in-process FSI session, no session manager.
/// Daemon: session supervisor manages additional worker sessions.
[<RequireQualifiedAccess>]
type SessionMode =
  | Embedded
  | Daemon of SessionManagementOps

module SessionMode =
  /// Extract session management ops, or error if embedded.
  let requireDaemon (mode: SessionMode) : Result<SessionManagementOps, SageFsError> =
    match mode with
    | SessionMode.Daemon ops -> Result.Ok ops
    | SessionMode.Embedded ->
      Result.Error (
        SageFsError.SessionCreationFailed
          "Session management requires daemon mode (SageFs -d)")
