module SageFs.Affordances

open System

/// Tracks eval count and timing statistics (immutable, pure updates).
type EvalStats = {
  EvalCount: int
  TotalDuration: TimeSpan
  MinDuration: TimeSpan
  MaxDuration: TimeSpan
}

module EvalStats =
  let empty = {
    EvalCount = 0
    TotalDuration = TimeSpan.Zero
    MinDuration = TimeSpan.Zero
    MaxDuration = TimeSpan.Zero
  }

  let record (duration: TimeSpan) (stats: EvalStats) =
    if stats.EvalCount = 0 then
      { EvalCount = 1
        TotalDuration = duration
        MinDuration = duration
        MaxDuration = duration }
    else
      { EvalCount = stats.EvalCount + 1
        TotalDuration = stats.TotalDuration + duration
        MinDuration = min stats.MinDuration duration
        MaxDuration = max stats.MaxDuration duration }

  let averageDuration (stats: EvalStats) =
    if stats.EvalCount = 0 then TimeSpan.Zero
    else TimeSpan.FromTicks(stats.TotalDuration.Ticks / int64 stats.EvalCount)

/// Pure function: given a session state, returns the list of tool names
/// that are valid to invoke. Agents should only call listed tools.
let availableTools (state: SessionState) : string list =
  match state with
  | Uninitialized ->
    [ "get_fsi_status" ]
  | WarmingUp ->
    [ "get_fsi_status"; "get_recent_fsi_events" ]
  | Ready ->
    [ "send_fsharp_code"
      "load_fsharp_script"
      "get_fsi_status"
      "get_startup_info"
      "get_recent_fsi_events"
      "get_completions"
      "check_fsharp_code"
      "reset_fsi_session"
      "hard_reset_fsi_session"
      "cancel_eval" ]
  | Evaluating ->
    [ "cancel_eval"
      "get_fsi_status"
      "get_recent_fsi_events"
      "get_completions"
      "check_fsharp_code" ]
  | Faulted ->
    [ "get_fsi_status"
      "get_recent_fsi_events"
      "reset_fsi_session"
      "hard_reset_fsi_session" ]

/// Check if a tool is available in the current state.
/// Returns Ok () if available, Error with SageFsError.ToolNotAvailable.
let checkToolAvailability (state: SessionState) (toolName: string) : Result<unit, SageFsError> =
  let tools = availableTools state
  if tools |> List.contains toolName then
    Ok ()
  else
    Error (SageFsError.ToolNotAvailable(toolName, state, tools))
