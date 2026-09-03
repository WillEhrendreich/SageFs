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
    match stats.EvalCount = 0 with
    | true ->
      { EvalCount = 1
        TotalDuration = duration
        MinDuration = duration
        MaxDuration = duration }
    | false ->
      { EvalCount = stats.EvalCount + 1
        TotalDuration = stats.TotalDuration + duration
        MinDuration = min stats.MinDuration duration
        MaxDuration = max stats.MaxDuration duration }

  let averageDuration (stats: EvalStats) =
    match stats.EvalCount = 0 with
    | true -> TimeSpan.Zero
    | false -> TimeSpan.FromTicks(stats.TotalDuration.Ticks / int64 stats.EvalCount)

/// Pure function: given a session state, returns the list of tool names
/// that are valid to invoke. Agents should only call listed tools.
let availableTools (state: SessionState) : string list =
  match state with
  | Uninitialized ->
    [ "get_fsi_status"; "get_friction_report"; "get_available_projects"
      "list_sessions"; "create_session" ]
  | WarmingUp ->
    [ "get_fsi_status"; "get_recent_fsi_events"; "get_friction_report"
      "get_available_projects"; "list_sessions"; "create_session" ]
  | Ready ->
    [ "send_fsharp_code"
      "get_fsi_status"
      "get_recent_fsi_events"
      "get_friction_report"
      "check_fsharp_code"
      "targeted_verify"
      "list_tests"
      "explain_test_failure"
      "list_sessions"
      "switch_session"
      "create_session"
      "get_available_projects"
      "reset_fsi_session"
      "hard_reset_fsi_session"
      "cancel_eval" ]
  | Evaluating ->
    [ "cancel_eval"
      "get_fsi_status"
      "get_recent_fsi_events"
      "get_friction_report"
      "get_available_projects"
      "list_sessions"
      "check_fsharp_code" ]
  | Faulted ->
    [ "get_fsi_status"
      "get_recent_fsi_events"
      "get_friction_report"
      "get_available_projects"
      "list_sessions"
      "create_session"
      "reset_fsi_session"
      "hard_reset_fsi_session" ]

/// Check if a tool is available in the current state.
/// Returns Ok () if available, Error with SageFsError.ToolNotAvailable.
let checkToolAvailability (state: SessionState) (toolName: string) : Result<unit, SageFsError> =
  let tools = availableTools state
  match tools |> List.contains toolName with
  | true ->
    Ok ()
  | false ->
    Error (SageFsError.ToolNotAvailable(toolName, state, tools))

// ── Tool-call gating domain ──────────────────────────────────────────────
//
// The MCP server gates every `tools/call` invocation against THIS model. A
// tool is classified either:
//   - `StateGated`     — its availability is derived from `availableTools` for
//                        the session's CURRENT lifecycle state (Ready/Eval…).
//   - `AlwaysAvailable`— it has no session-state dependence and must remain
//                        callable in every state (and before any session
//                        exists). Monitoring/session-listing tools plus the
//                        non-session surface (friction telemetry, hot-reload
//                        toggles, stop_session) belong here.
// The gate logic itself never special-cases a tool name — it consults only
// this declaration table, so a newly registered tool is either declared or it
// fails closed (and the registration-integrity tests force the declaration).

[<RequireQualifiedAccess>]
type ToolGate =
  | AlwaysAvailable
  | StateGated

/// Every MCP-registered tool must be declared here (one entry per tool name).
let private gatingDomain : Map<string, ToolGate> =
  Map.ofList [
    // Tools with no session-state dependence.
    "get_fsi_status", ToolGate.AlwaysAvailable
    "get_friction_report", ToolGate.AlwaysAvailable
    "get_available_projects", ToolGate.AlwaysAvailable
    "list_sessions", ToolGate.AlwaysAvailable
    "get_friction_summary", ToolGate.AlwaysAvailable
    "report_friction", ToolGate.AlwaysAvailable
    "enable_hot_reload", ToolGate.AlwaysAvailable
    "disable_hot_reload", ToolGate.AlwaysAvailable
    "stop_session", ToolGate.AlwaysAvailable
    // State-gated tools — availability derives from availableTools for the
    // session's current lifecycle state.
    "send_fsharp_code", ToolGate.StateGated
    "get_recent_fsi_events", ToolGate.StateGated
    "check_fsharp_code", ToolGate.StateGated
    "targeted_verify", ToolGate.StateGated
    "list_tests", ToolGate.StateGated
    "explain_test_failure", ToolGate.StateGated
    "switch_session", ToolGate.StateGated
    "create_session", ToolGate.StateGated
    "reset_fsi_session", ToolGate.StateGated
    "hard_reset_fsi_session", ToolGate.StateGated
    "cancel_eval", ToolGate.StateGated
  ]

/// Look up a tool's gating classification. `None` means the tool is not
/// declared — the call fails closed whenever a session state is known.
let toolGate (toolName: string) : ToolGate option =
  Map.tryFind toolName gatingDomain

/// All declared tool names (must equal the registered MCP tool set).
let declaredGateTools : string list =
  gatingDomain |> Map.toList |> List.map fst

/// Decide whether a `tools/call` for `toolName` may proceed when the targeted
/// session is in `state`. Pure — no I/O, no session resolution.
let checkToolCallAllowed (state: SessionState) (toolName: string) : Result<unit, SageFsError> =
  match toolGate toolName with
  | Some ToolGate.AlwaysAvailable ->
    Ok ()
  | Some ToolGate.StateGated ->
    checkToolAvailability state toolName
  | None ->
    // Fail closed: a tool that is not declared in the gating domain has no
    // availability policy, so it must never bypass the model.
    Error (SageFsError.ToolNotAvailable(toolName, state, availableTools state))
