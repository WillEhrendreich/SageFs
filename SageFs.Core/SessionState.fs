namespace SageFs

/// Session lifecycle states — determines which MCP tools are available.
type SessionState =
  | Uninitialized
  | WarmingUp
  | Ready
  | Evaluating
  | Faulted

module SessionState =
  let label = function
    | Uninitialized -> "Uninitialized"
    | WarmingUp -> "WarmingUp"
    | Ready -> "Ready"
    | Evaluating -> "Evaluating"
    | Faulted -> "Faulted"
