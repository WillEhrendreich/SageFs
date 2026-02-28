namespace SageFs

open Microsoft.Extensions.Logging

/// Unified error type for the entire SageFs system.
/// Every Result<..., SageFsError> across all layers uses this single DU.
/// NO wildcard matches in module functions — compiler catches missing cases.
[<RequireQualifiedAccess>]
type SageFsError =
  // ── Tool availability ──
  | ToolNotAvailable of toolName: string * currentState: SessionState * availableTools: string list
  // ── Session operations ──
  | SessionNotFound of sessionId: string
  | NoActiveSessions
  | AmbiguousSessions of sessionDescriptions: string list
  | SessionCreationFailed of reason: string
  | SessionStopFailed of sessionId: string * reason: string
  | SessionSwitchFailed of sessionId: string * reason: string
  // ── Worker communication ──
  | WorkerCommunicationFailed of sessionId: string * reason: string
  | WorkerSpawnFailed of reason: string
  | WorkerTimeout of sessionId: string * operation: string * timeoutSec: float
  | WorkerHttpError of sessionId: string * endpoint: string * statusCode: int
  | PipeClosed
  // ── Eval/reset/check operations ──
  | EvalFailed of reason: string
  | ResetFailed of reason: string
  | HardResetFailed of reason: string
  | ScriptLoadFailed of reason: string
  | CheckFailed of reason: string
  | CompletionFailed of sessionId: string * reason: string
  | CancelFailed of reason: string
  // ── Warm-up ──
  | WarmupOpenFailed of name: string * reason: string
  | WarmupContextFailed of sessionId: string * reason: string
  // ── Hot reload ──
  | HotReloadFailed of path: string * reason: string
  | HotReloadStateError of sessionId: string * reason: string
  // ── Event store ──
  | EventAppendFailed of stream: string * expectedVersion: int * reason: string
  | EventFetchFailed of stream: string * reason: string
  // ── Restart policy ──
  | RestartLimitExceeded of restartCount: int * windowMinutes: float
  // ── Infrastructure ──
  | DaemonStartFailed of reason: string
  | DaemonNotRunning
  | PortInUse of port: int
  | ConnectionStringMissing
  | SseConnectionError of reason: string
  | JsonParseError of context: string * reason: string
  | Unexpected of exn

module SageFsError =
  let describe = function
    | SageFsError.ToolNotAvailable(toolName, state, available) ->
      sprintf "Cannot %s: session is %s. Available: %s"
        toolName (SessionState.label state) (available |> String.concat ", ")
    | SageFsError.SessionNotFound id ->
      sprintf "Session '%s' not found. Use list_sessions to see available sessions." id
    | SageFsError.NoActiveSessions ->
      "No active sessions. Use create_session to start one."
    | SageFsError.AmbiguousSessions descriptions ->
      sprintf "Multiple sessions active. Specify sessionId:\n%s" (descriptions |> String.concat "\n")
    | SageFsError.SessionCreationFailed reason ->
      sprintf "Failed to create session: %s" reason
    | SageFsError.SessionStopFailed(id, reason) ->
      sprintf "Failed to stop session '%s': %s" id reason
    | SageFsError.SessionSwitchFailed(id, reason) ->
      sprintf "Failed to switch to session '%s': %s" id reason
    | SageFsError.WorkerCommunicationFailed(id, reason) ->
      sprintf "Cannot reach session '%s': %s" id reason
    | SageFsError.WorkerSpawnFailed reason ->
      sprintf "Failed to start worker: %s" reason
    | SageFsError.WorkerTimeout(id, operation, sec) ->
      sprintf "Session '%s' timed out during %s after %.1fs" id operation sec
    | SageFsError.WorkerHttpError(id, endpoint, status) ->
      sprintf "Session '%s' returned HTTP %d for %s" id status endpoint
    | SageFsError.PipeClosed ->
      "Pipe closed unexpectedly"
    | SageFsError.EvalFailed reason ->
      sprintf "Evaluation failed: %s" reason
    | SageFsError.ResetFailed reason ->
      sprintf "Reset failed: %s" reason
    | SageFsError.HardResetFailed reason ->
      sprintf "Hard reset failed: %s" reason
    | SageFsError.ScriptLoadFailed reason ->
      sprintf "Script load failed: %s" reason
    | SageFsError.CheckFailed reason ->
      sprintf "Type check failed: %s" reason
    | SageFsError.CompletionFailed(id, reason) ->
      sprintf "Code completion failed for session '%s': %s" id reason
    | SageFsError.CancelFailed reason ->
      sprintf "Cancel failed: %s" reason
    | SageFsError.WarmupOpenFailed(name, reason) ->
      sprintf "Failed to open '%s' during warm-up: %s" name reason
    | SageFsError.WarmupContextFailed(id, reason) ->
      sprintf "Failed to get warmup context for session '%s': %s" id reason
    | SageFsError.HotReloadFailed(path, reason) ->
      sprintf "Hot reload failed for '%s': %s" path reason
    | SageFsError.HotReloadStateError(id, reason) ->
      sprintf "Hot reload state error in session '%s': %s" id reason
    | SageFsError.EventAppendFailed(stream, ver, reason) ->
      sprintf "Failed to append to stream '%s' at version %d: %s" stream ver reason
    | SageFsError.EventFetchFailed(stream, reason) ->
      sprintf "Failed to fetch from stream '%s': %s" stream reason
    | SageFsError.RestartLimitExceeded(count, windowMin) ->
      sprintf "Worker restarted %d times within %.0f minutes. Giving up." count windowMin
    | SageFsError.DaemonStartFailed reason ->
      sprintf "Failed to start daemon: %s" reason
    | SageFsError.DaemonNotRunning ->
      "SageFs daemon is not running. Start it with 'sagefs --proj <project>'."
    | SageFsError.PortInUse port ->
      sprintf "Port %d is already in use" port
    | SageFsError.ConnectionStringMissing ->
      "Database connection string not configured"
    | SageFsError.SseConnectionError reason ->
      sprintf "SSE connection failed: %s" reason
    | SageFsError.JsonParseError(context, reason) ->
      sprintf "JSON parse error in %s: %s" context reason
    | SageFsError.Unexpected exn ->
      sprintf "Unexpected error: %s" exn.Message

  let toLogLevel = function
    // Critical — system-level failures, daemon can't operate
    | SageFsError.DaemonStartFailed _ -> LogLevel.Critical
    | SageFsError.PortInUse _ -> LogLevel.Critical
    | SageFsError.ConnectionStringMissing -> LogLevel.Critical
    | SageFsError.RestartLimitExceeded _ -> LogLevel.Critical
    // Error — operation failed, user action needed
    | SageFsError.WorkerSpawnFailed _ -> LogLevel.Error
    | SageFsError.WorkerCommunicationFailed _ -> LogLevel.Error
    | SageFsError.WorkerTimeout _ -> LogLevel.Error
    | SageFsError.WorkerHttpError _ -> LogLevel.Error
    | SageFsError.PipeClosed -> LogLevel.Error
    | SageFsError.SessionCreationFailed _ -> LogLevel.Error
    | SageFsError.EvalFailed _ -> LogLevel.Error
    | SageFsError.ResetFailed _ -> LogLevel.Error
    | SageFsError.HardResetFailed _ -> LogLevel.Error
    | SageFsError.ScriptLoadFailed _ -> LogLevel.Error
    | SageFsError.HotReloadFailed _ -> LogLevel.Error
    | SageFsError.EventAppendFailed _ -> LogLevel.Error
    | SageFsError.EventFetchFailed _ -> LogLevel.Error
    | SageFsError.SseConnectionError _ -> LogLevel.Error
    | SageFsError.Unexpected _ -> LogLevel.Error
    // Warning — degraded but recoverable
    | SageFsError.SessionStopFailed _ -> LogLevel.Warning
    | SageFsError.SessionSwitchFailed _ -> LogLevel.Warning
    | SageFsError.CheckFailed _ -> LogLevel.Warning
    | SageFsError.CompletionFailed _ -> LogLevel.Warning
    | SageFsError.CancelFailed _ -> LogLevel.Warning
    | SageFsError.WarmupOpenFailed _ -> LogLevel.Warning
    | SageFsError.WarmupContextFailed _ -> LogLevel.Warning
    | SageFsError.HotReloadStateError _ -> LogLevel.Warning
    | SageFsError.JsonParseError _ -> LogLevel.Warning
    // Information — expected conditions, not bugs
    | SageFsError.ToolNotAvailable _ -> LogLevel.Information
    | SageFsError.SessionNotFound _ -> LogLevel.Information
    | SageFsError.NoActiveSessions -> LogLevel.Information
    | SageFsError.AmbiguousSessions _ -> LogLevel.Information
    | SageFsError.DaemonNotRunning -> LogLevel.Information

  let toHttpStatus = function
    // 404 Not Found
    | SageFsError.SessionNotFound _ -> 404
    | SageFsError.NoActiveSessions -> 404
    | SageFsError.DaemonNotRunning -> 404
    // 400 Bad Request
    | SageFsError.AmbiguousSessions _ -> 400
    | SageFsError.JsonParseError _ -> 400
    | SageFsError.ToolNotAvailable _ -> 400
    // 409 Conflict
    | SageFsError.PortInUse _ -> 409
    | SageFsError.RestartLimitExceeded _ -> 409
    | SageFsError.ConnectionStringMissing -> 409
    // 504 Gateway Timeout
    | SageFsError.WorkerTimeout _ -> 504
    // 502 Bad Gateway
    | SageFsError.WorkerHttpError _ -> 502
    | SageFsError.WorkerCommunicationFailed _ -> 502
    | SageFsError.PipeClosed -> 502
    | SageFsError.WorkerSpawnFailed _ -> 502
    | SageFsError.SseConnectionError _ -> 502
    // 500 Internal Server Error
    | SageFsError.SessionCreationFailed _ -> 500
    | SageFsError.SessionStopFailed _ -> 500
    | SageFsError.SessionSwitchFailed _ -> 500
    | SageFsError.EvalFailed _ -> 500
    | SageFsError.ResetFailed _ -> 500
    | SageFsError.HardResetFailed _ -> 500
    | SageFsError.ScriptLoadFailed _ -> 500
    | SageFsError.CheckFailed _ -> 500
    | SageFsError.CompletionFailed _ -> 500
    | SageFsError.CancelFailed _ -> 500
    | SageFsError.WarmupOpenFailed _ -> 500
    | SageFsError.WarmupContextFailed _ -> 500
    | SageFsError.HotReloadFailed _ -> 500
    | SageFsError.HotReloadStateError _ -> 500
    | SageFsError.EventAppendFailed _ -> 500
    | SageFsError.EventFetchFailed _ -> 500
    | SageFsError.DaemonStartFailed _ -> 500
    | SageFsError.Unexpected _ -> 500
