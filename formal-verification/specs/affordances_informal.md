# Informal Specification: `Affordances.availableTools`

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Source

- **F# file**: `SageFs.Core/Affordances.fs`
- **Types**: `SessionState` (`SageFs.Core/SessionState.fs`), `SageFsError` (`SageFs.Core/SageFsError.fs`)

---

## Purpose

`availableTools (state: SessionState) : string list`

Returns the list of MCP tool names that are valid for an agent to invoke
when the SageFs daemon is in the given session lifecycle state. The list is
used by `checkToolAvailability` to gate tool invocations and return an
error when a tool is called in a state that does not support it.

This function is security-relevant: a tool invoked outside its allowed state
may trigger dangerous side effects (e.g., resetting an active session while
code is evaluating, or sending code when FSI is not ready).

---

## Preconditions

- `state` is one of the five `SessionState` values:
  `Uninitialized`, `WarmingUp`, `Ready`, `Evaluating`, `Faulted`.
- No external state is read; the function is pure.

---

## Postconditions

1. **Non-empty result**: for every state, the returned list contains at least one tool name.
2. **Determinism**: given the same state, always returns the same list (referentially transparent).
3. **Finite domain**: the total set of tool names across all states is fixed and small (~15 names).

### Tool availability by state

| Tool | Uninit | WarmUp | Ready | Evaluat | Faulted |
|------|:------:|:------:|:-----:|:-------:|:-------:|
| `get_fsi_status` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `get_friction_report` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `get_available_projects` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `list_sessions` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `get_recent_fsi_events` | | ✓ | ✓ | ✓ | ✓ |
| `create_session` | ✓ | ✓ | ✓ | | ✓ |
| `check_fsharp_code` | | | ✓ | ✓ | |
| `cancel_eval` | | | ✓ | ✓ | |
| `reset_fsi_session` | | | ✓ | | ✓ |
| `hard_reset_fsi_session` | | | ✓ | | ✓ |
| `send_fsharp_code` | | | ✓ | | |
| `targeted_verify` | | | ✓ | | |
| `list_tests` | | | ✓ | | |
| `explain_test_failure` | | | ✓ | | |
| `switch_session` | | | ✓ | | |

---

## Invariants

1. **Monitoring tools always available**: `get_fsi_status`, `get_friction_report`,
   `get_available_projects`, and `list_sessions` are present in every state's list.

2. **Code-execution tools gated on Ready**: `send_fsharp_code`, `targeted_verify`,
   `list_tests`, `explain_test_failure`, and `switch_session` appear **only** when
   state = `Ready`.

3. **Cancellation available during and after start**: `cancel_eval` is available
   in `Ready` and `Evaluating`, but not in any other state.

4. **Reset operations unavailable during evaluation**: `reset_fsi_session` and
   `hard_reset_fsi_session` are not available in `Evaluating`, preventing dangerous
   resets while code is actively running.

5. **Hard reset for recovery**: `hard_reset_fsi_session` is available only in
   `Ready` and `Faulted`, meaning it can only be triggered when idle or recovering.

6. **No creation during evaluation**: `create_session` is not available in `Evaluating`.

---

## Edge Cases

- **Uninitialized**: only basic query and session-creation tools; no code execution,
  no FSI-event log (FSI not yet started).
- **WarmingUp**: FSI events now available (FSI process starting), but still no code execution.
- **Evaluating**: only safe observation and cancellation allowed; no writes, no resets.
- **Faulted**: recovery tools available; code execution not allowed until reset.

---

## Examples

```
availableTools Uninitialized
  → ["get_fsi_status"; "get_friction_report"; "get_available_projects";
     "list_sessions"; "create_session"]

availableTools Ready
  → ["send_fsharp_code"; "get_fsi_status"; "get_recent_fsi_events";
     "get_friction_report"; "check_fsharp_code"; "targeted_verify";
     "list_tests"; "explain_test_failure"; "list_sessions";
     "switch_session"; "create_session"; "get_available_projects";
     "reset_fsi_session"; "hard_reset_fsi_session"; "cancel_eval"]

checkToolAvailability Uninitialized "send_fsharp_code"
  → Error (ToolNotAvailable("send_fsharp_code", Uninitialized, [...]))

checkToolAvailability Ready "send_fsharp_code"
  → Ok ()
```

---

## Inferred Intent

The function encodes a safety policy: agents (AI or human) querying the MCP
server should be constrained to the available tools for the current state.
The design prevents:
- Sending code to an uninitialised FSI process
- Resetting the session while code is evaluating
- Creating duplicate sessions mid-evaluation

The tool list is ordered by importance/priority within each state, though order
has no semantic significance for `checkToolAvailability`.

---

## Open Questions

1. **No duplicates guarantee**: the lists appear to have no duplicate tool names,
   but this is not explicitly documented. Should it be an invariant?
2. **Sublist relationship**: is `availableTools Uninitialized` a subset of
   `availableTools WarmingUp`? Not exactly (WarmingUp adds `get_recent_fsi_events`
   but both have `create_session`). The subset ordering is partial, not total.
3. **EvalStats**: the `EvalStats` type and module are defined in the same file but
   are unrelated to `availableTools` — they track evaluation timing. No FV planned.
