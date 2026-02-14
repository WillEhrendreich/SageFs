namespace SageFs

/// Unified error type for the entire SageFs system.
/// Every Result<..., SageFsError> across all layers uses this single DU.
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
  // ── Worker communication ──
  | WorkerCommunicationFailed of sessionId: string * reason: string
  | WorkerSpawnFailed of reason: string
  | PipeClosed
  // ── Eval/reset operations ──
  | EvalFailed of reason: string
  | ResetFailed of reason: string
  | HardResetFailed of reason: string
  | ScriptLoadFailed of reason: string
  // ── Warm-up ──
  | WarmupOpenFailed of name: string * reason: string
  // ── Restart policy ──
  | RestartLimitExceeded of restartCount: int * windowMinutes: float
  // ── Infrastructure ──
  | DaemonStartFailed of reason: string
  | Unexpected of exn

module SageFsError =
  let describe = function
    | SageFsError.ToolNotAvailable(toolName, state, available) ->
      let alternatives = available |> String.concat ", "
      sprintf "Cannot %s: session is %s. Available: %s"
        toolName (SessionState.label state) alternatives
    | SageFsError.SessionNotFound id ->
      sprintf "Session '%s' not found. Use list_sessions to see available sessions." id
    | SageFsError.NoActiveSessions ->
      "No active sessions. Use create_session to start one."
    | SageFsError.AmbiguousSessions descriptions ->
      let listing = descriptions |> String.concat "\n"
      sprintf "Multiple sessions active. Specify sessionId:\n%s" listing
    | SageFsError.SessionCreationFailed reason ->
      sprintf "Failed to create session: %s" reason
    | SageFsError.SessionStopFailed(id, reason) ->
      sprintf "Failed to stop session '%s': %s" id reason
    | SageFsError.WorkerCommunicationFailed(id, reason) ->
      sprintf "Cannot reach session '%s': %s" id reason
    | SageFsError.WorkerSpawnFailed reason ->
      sprintf "Failed to start worker: %s" reason
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
    | SageFsError.WarmupOpenFailed(name, reason) ->
      sprintf "Failed to open '%s' during warm-up: %s" name reason
    | SageFsError.RestartLimitExceeded(count, windowMin) ->
      sprintf "Worker restarted %d times within %.0f minutes. Giving up." count windowMin
    | SageFsError.DaemonStartFailed reason ->
      sprintf "Failed to start daemon: %s" reason
    | SageFsError.Unexpected exn ->
      sprintf "Unexpected error: %s" exn.Message
