/-
  Affordances.lean
  Formal specification for SageFs.Core.Affordances.availableTools.

  The F# source maps each SessionState (5 cases) to a fixed list of MCP tool
  names (strings).  Because the domain is finite and all lists are concrete,
  every property stated here is provable by `decide` or `native_decide`.

  Key abstractions:
  - `SageFsError.ToolNotAvailable` payload (toolName, state, availableTools)
    is simplified to `checkToolAvailability returning Bool false`
  - `EvalStats` (timing statistics) is out of scope — unrelated to tool gating

  No Mathlib.  Pure Lean 4 stdlib only (network firewalled on CI).
-/

-- ── Type definitions ────────────────────────────────────────────────────────

/-- Session lifecycle state (mirrors F# `SessionState` DU). -/
inductive SessionState where
  | Uninitialized
  | WarmingUp
  | Ready
  | Evaluating
  | Faulted
  deriving DecidableEq, Repr

open SessionState

-- ── Core function ───────────────────────────────────────────────────────────

/--
  Returns the list of MCP tool names valid in the given session state.
  Mirrors F# `Affordances.availableTools`.
-/
def availableTools : SessionState → List String
  | Uninitialized =>
      [ "get_fsi_status", "get_friction_report", "get_available_projects",
        "list_sessions", "create_session" ]
  | WarmingUp =>
      [ "get_fsi_status", "get_recent_fsi_events", "get_friction_report",
        "get_available_projects", "list_sessions", "create_session" ]
  | Ready =>
      [ "send_fsharp_code", "get_fsi_status", "get_recent_fsi_events",
        "get_friction_report", "check_fsharp_code", "targeted_verify",
        "list_tests", "explain_test_failure", "list_sessions",
        "switch_session", "create_session", "get_available_projects",
        "reset_fsi_session", "hard_reset_fsi_session", "cancel_eval" ]
  | Evaluating =>
      [ "cancel_eval", "get_fsi_status", "get_recent_fsi_events",
        "get_friction_report", "get_available_projects", "list_sessions",
        "check_fsharp_code" ]
  | Faulted =>
      [ "get_fsi_status", "get_recent_fsi_events", "get_friction_report",
        "get_available_projects", "list_sessions", "create_session",
        "reset_fsi_session", "hard_reset_fsi_session" ]

/--
  Returns `true` iff the named tool is available in the given state.
  Mirrors F# `Affordances.checkToolAvailability` (simplified to Bool).
-/
def checkToolAvailability (state : SessionState) (toolName : String) : Bool :=
  (availableTools state).contains toolName

-- ── Basic sanity: non-empty lists ──────────────────────────────────────────

/-- Every state exposes at least one tool. -/
theorem availableTools_nonempty (s : SessionState) :
    (availableTools s).length > 0 := by
  cases s <;> decide

-- ── Always-available tools (present in every state) ─────────────────────────

/-- `get_fsi_status` is available in every state. -/
theorem get_fsi_status_always (s : SessionState) :
    "get_fsi_status" ∈ availableTools s := by
  cases s <;> decide

/-- `get_friction_report` is available in every state. -/
theorem get_friction_report_always (s : SessionState) :
    "get_friction_report" ∈ availableTools s := by
  cases s <;> decide

/-- `get_available_projects` is available in every state. -/
theorem get_available_projects_always (s : SessionState) :
    "get_available_projects" ∈ availableTools s := by
  cases s <;> decide

/-- `list_sessions` is available in every state. -/
theorem list_sessions_always (s : SessionState) :
    "list_sessions" ∈ availableTools s := by
  cases s <;> decide

-- ── Code execution gated exclusively on Ready ───────────────────────────────

/--
  `send_fsharp_code` is available if and only if the session is `Ready`.
  This is the primary safety constraint: code can only be sent when FSI is ready.
-/
theorem send_fsharp_code_iff_ready (s : SessionState) :
    "send_fsharp_code" ∈ availableTools s ↔ s = Ready := by
  cases s <;> decide

/-- `targeted_verify` only in Ready. -/
theorem targeted_verify_iff_ready (s : SessionState) :
    "targeted_verify" ∈ availableTools s ↔ s = Ready := by
  cases s <;> decide

/-- `list_tests` only in Ready. -/
theorem list_tests_iff_ready (s : SessionState) :
    "list_tests" ∈ availableTools s ↔ s = Ready := by
  cases s <;> decide

/-- `explain_test_failure` only in Ready. -/
theorem explain_test_failure_iff_ready (s : SessionState) :
    "explain_test_failure" ∈ availableTools s ↔ s = Ready := by
  cases s <;> decide

/-- `switch_session` only in Ready. -/
theorem switch_session_iff_ready (s : SessionState) :
    "switch_session" ∈ availableTools s ↔ s = Ready := by
  cases s <;> decide

-- ── Cancellation policy ──────────────────────────────────────────────────────

/--
  `cancel_eval` is available when `Ready` or `Evaluating`, and nowhere else.
  This allows pre-emptive cancellation before and during evaluation.
-/
theorem cancel_eval_iff_ready_or_evaluating (s : SessionState) :
    "cancel_eval" ∈ availableTools s ↔ (s = Ready ∨ s = Evaluating) := by
  cases s <;> decide

-- ── Reset safety constraints ─────────────────────────────────────────────────

/--
  `reset_fsi_session` is NOT available when `Evaluating`.
  This prevents dangerous session resets while code is actively running.
-/
theorem reset_not_during_eval :
    ¬ ("reset_fsi_session" ∈ availableTools Evaluating) := by
  decide

/--
  `hard_reset_fsi_session` is available only in `Ready` or `Faulted`.
  Recovery resets are allowed only when the FSI is idle or has crashed.
-/
theorem hard_reset_iff_ready_or_faulted (s : SessionState) :
    "hard_reset_fsi_session" ∈ availableTools s ↔ (s = Ready ∨ s = Faulted) := by
  cases s <;> decide

/--
  `hard_reset_fsi_session` is NOT available when `Evaluating`.
-/
theorem hard_reset_not_during_eval :
    ¬ ("hard_reset_fsi_session" ∈ availableTools Evaluating) := by
  decide

-- ── Session creation policy ───────────────────────────────────────────────────

/--
  `create_session` is NOT available when `Evaluating`.
  New sessions cannot be created while the current one is busy evaluating.
-/
theorem create_session_not_during_eval :
    ¬ ("create_session" ∈ availableTools Evaluating) := by
  decide

-- ── No-duplicate invariant ────────────────────────────────────────────────────

/--
  No tool name appears twice in any state's list.
  This ensures `checkToolAvailability` is unambiguous.
-/
theorem availableTools_nodup (s : SessionState) :
    (availableTools s).Nodup := by
  cases s <;> decide

-- ── checkToolAvailability correctness ────────────────────────────────────────

/--
  `checkToolAvailability` returns `true` iff the tool is in the list for that state.
  This connects the Bool gate to the membership specification.
-/
theorem checkToolAvailability_iff_mem (s : SessionState) (t : String) :
    checkToolAvailability s t = true ↔ t ∈ availableTools s :=
  ⟨List.mem_of_elem_eq_true, List.elem_eq_true_of_mem⟩

/--
  `send_fsharp_code` is denied in every non-Ready state.
-/
theorem send_code_denied_unless_ready (s : SessionState) (h : s ≠ Ready) :
    checkToolAvailability s "send_fsharp_code" = false := by
  cases s <;> simp_all <;> decide

/--
  `hard_reset_fsi_session` is always denied when `Evaluating`.
-/
theorem hard_reset_denied_when_evaluating :
    checkToolAvailability Evaluating "hard_reset_fsi_session" = false := by
  decide
