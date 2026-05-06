/-
  Composition.lean
  Cross-file composition theorems for SageFs formal verification.

  This file imports from SessionLifecycle.lean and Affordances.lean to prove
  system-level properties that span the session lifecycle and MCP tool affordances.
  It is the first file in the FVSquad library that creates cross-module theorems,
  addressing the gap identified in CRITIQUE.md §"No cross-file composition theorems".

  Key theorems:
  - `stateToSessionState` establishes a canonical isomorphism between
    `SessionLifecycle.State` and `Affordances.SessionState` (structurally identical types).
  - `active_idle_can_send_code` — a session in Phase.Active _ .Idle maps to Ready,
    which has "send_fsharp_code" available: the evaluation gate is correct end-to-end.
  - `evaluating_cannot_send_code` — a session in Phase.Active _ .Evaluating cannot
    accept new code: the reentrancy guard is correct end-to-end.
  - `faulted_can_hard_reset` — a Faulted session always has "hard_reset_fsi_session",
    confirming recovery is always possible from Faulted.
  - `warming_up_cannot_send_code` — a WarmingUp session cannot send code.
  - `uninitialized_cannot_send_code` — an Uninitialized session cannot send code.
  - `send_fsharp_code_iff_ready_phase` — the direct phase-level gate: code submission
    is available iff the Phase is Active-Idle (the only Ready projection).

  Abstractions:
  - AppState is abstracted to a type variable α (same convention as SessionLifecycle.lean).
  - No Mathlib. Pure Lean 4 stdlib only (network firewalled on CI).

  Sources:
    SageFs.Core/AppState.fs, SageFs.Core/SessionState.fs, SageFs.Core/Affordances.fs
-/

import FVSquad.SessionLifecycle
import FVSquad.Affordances

namespace SessionComposition

open SessionLifecycle
open SessionState   -- Affordances.lean opens SessionState at the top level

-- ── Bridge: SessionLifecycle.State ↔ Affordances.SessionState ──────────────

/-- Canonical conversion from the lifecycle state to the affordances state.
    Both types are structurally identical 5-case inductives that mirror F# SessionState;
    this bridge makes the connection explicit and lets us compose theorems from both
    files. -/
def stateToSessionState : SessionLifecycle.State → SessionState
  | SessionLifecycle.State.Uninitialized => SessionState.Uninitialized
  | SessionLifecycle.State.WarmingUp     => SessionState.WarmingUp
  | SessionLifecycle.State.Ready         => SessionState.Ready
  | SessionLifecycle.State.Evaluating    => SessionState.Evaluating
  | SessionLifecycle.State.Faulted       => SessionState.Faulted

/-- The bridge is injective: distinct lifecycle states map to distinct affordance states. -/
theorem stateToSessionState_injective : Function.Injective stateToSessionState := by
  intro a b h
  cases a <;> cases b <;> first | rfl | (simp [stateToSessionState] at h)

/-- The bridge is surjective: every affordance state has a preimage. -/
theorem stateToSessionState_surjective : Function.Surjective stateToSessionState := by
  intro b
  cases b
  · exact ⟨SessionLifecycle.State.Uninitialized, rfl⟩
  · exact ⟨SessionLifecycle.State.WarmingUp, rfl⟩
  · exact ⟨SessionLifecycle.State.Ready, rfl⟩
  · exact ⟨SessionLifecycle.State.Evaluating, rfl⟩
  · exact ⟨SessionLifecycle.State.Faulted, rfl⟩

/-- The bridge is a bijection: injective and surjective. -/
theorem stateToSessionState_bijective :
    Function.Injective stateToSessionState ∧ Function.Surjective stateToSessionState :=
  ⟨stateToSessionState_injective, stateToSessionState_surjective⟩

-- ── Composition: Phase → State → Affordances ────────────────────────────────

/-- If the session phase projects to Ready (i.e. Phase.Active _ .Idle),
    then "send_fsharp_code" is available in the corresponding affordance state.
    This closes the loop: SessionLifecycle.lean proves when a Phase is Ready;
    Affordances.lean proves Ready allows code submission.
    Together: Active-Idle sessions can send F# code. -/
theorem active_idle_can_send_code {α : Type} (p : Phase α)
    (h : toState p = SessionLifecycle.State.Ready) :
    checkToolAvailability (stateToSessionState (toState p)) "send_fsharp_code" := by
  simp only [h, stateToSessionState, checkToolAvailability, availableTools]
  decide

/-- A session in Phase.Active _ .Evaluating cannot accept new code submissions.
    This proves the reentrancy guard end-to-end:
    SessionLifecycle ensures Evaluating maps to State.Evaluating;
    Affordances ensures State.Evaluating does not include "send_fsharp_code". -/
theorem evaluating_cannot_send_code {α : Type} (p : Phase α)
    (h : toState p = SessionLifecycle.State.Evaluating) :
    ¬checkToolAvailability (stateToSessionState (toState p)) "send_fsharp_code" := by
  simp only [h, stateToSessionState, checkToolAvailability, availableTools]
  decide

/-- A Faulted session can always trigger a hard reset.
    This proves recovery is always possible from Faulted:
    SessionLifecycle ensures Faulted maps to State.Faulted;
    Affordances ensures State.Faulted includes "hard_reset_fsi_session". -/
theorem faulted_can_hard_reset {α : Type} (p : Phase α)
    (h : toState p = SessionLifecycle.State.Faulted) :
    checkToolAvailability (stateToSessionState (toState p)) "hard_reset_fsi_session" := by
  simp only [h, stateToSessionState, checkToolAvailability, availableTools]
  decide

/-- A WarmingUp session cannot send code.
    Proves that the system correctly blocks code submission before the session
    is fully initialised. -/
theorem warming_up_cannot_send_code {α : Type} (p : Phase α)
    (h : toState p = SessionLifecycle.State.WarmingUp) :
    ¬checkToolAvailability (stateToSessionState (toState p)) "send_fsharp_code" := by
  simp only [h, stateToSessionState, checkToolAvailability, availableTools]
  decide

/-- An Uninitialized state cannot send code. -/
theorem uninitialized_cannot_send_code :
    ¬checkToolAvailability (stateToSessionState SessionLifecycle.State.Uninitialized)
      "send_fsharp_code" := by
  simp [stateToSessionState, checkToolAvailability, availableTools]

-- ── Direct phase-level gate theorem ─────────────────────────────────────────

/-- The phase-level code-submission gate:
    "send_fsharp_code" is available in the phase's affordance state
    iff the phase is Active with Idle activity (the only Ready projection).
    This is the central composition theorem: it unifies the lifecycle model
    and the affordances model into a single statement about the F# source. -/
theorem send_fsharp_code_iff_ready_phase {α : Type} (p : Phase α) :
    checkToolAvailability (stateToSessionState (toState p)) "send_fsharp_code" ↔
    ∃ s, p = Phase.Active s Activity.Idle := by
  constructor
  · intro h
    cases p with
    | Initializing msg =>
      simp [toState, stateToSessionState, checkToolAvailability, availableTools] at h
    | Active s a =>
      cases a with
      | Idle => exact ⟨s, rfl⟩
      | Evaluating =>
        simp [toState, stateToSessionState, checkToolAvailability, availableTools] at h
    | Faulted =>
      simp [toState, stateToSessionState, checkToolAvailability, availableTools] at h
  · rintro ⟨s, rfl⟩
    simp [toState, stateToSessionState, checkToolAvailability, availableTools]

-- ── Cancel eval availability ─────────────────────────────────────────────────

/-- "cancel_eval" is available precisely when the session is Evaluating or Ready,
    verified across both modules. -/
theorem cancel_eval_available_iff_evaluating_or_ready {α : Type} (p : Phase α) :
    checkToolAvailability (stateToSessionState (toState p)) "cancel_eval" ↔
    (toState p = SessionLifecycle.State.Evaluating ∨
     toState p = SessionLifecycle.State.Ready) := by
  cases p with
  | Initializing msg =>
    simp [toState, stateToSessionState, checkToolAvailability, availableTools]
  | Active s a =>
    cases a with
    | Idle =>
      simp [toState, stateToSessionState, checkToolAvailability, availableTools]
    | Evaluating =>
      simp [toState, stateToSessionState, checkToolAvailability, availableTools]
  | Faulted =>
    simp [toState, stateToSessionState, checkToolAvailability, availableTools]

-- ── Hard reset availability ───────────────────────────────────────────────────

/-- "hard_reset_fsi_session" is available exactly in Ready and Faulted states. -/
theorem hard_reset_available_iff_ready_or_faulted {α : Type} (p : Phase α) :
    checkToolAvailability (stateToSessionState (toState p)) "hard_reset_fsi_session" ↔
    (toState p = SessionLifecycle.State.Ready ∨
     toState p = SessionLifecycle.State.Faulted) := by
  cases p with
  | Initializing msg =>
    simp [toState, stateToSessionState, checkToolAvailability, availableTools]
  | Active s a =>
    cases a with
    | Idle =>
      simp [toState, stateToSessionState, checkToolAvailability, availableTools]
    | Evaluating =>
      simp [toState, stateToSessionState, checkToolAvailability, availableTools]
  | Faulted =>
    simp [toState, stateToSessionState, checkToolAvailability, availableTools]

-- ── Complete tool availability by phase ──────────────────────────────────────

/-- The bridge preserves the non-empty tool list: every phase has at least one
    available tool, regardless of state. -/
theorem tools_always_available {α : Type} (p : Phase α) :
    (availableTools (stateToSessionState (toState p))).length > 0 := by
  cases p with
  | Initializing msg => simp [toState, stateToSessionState, availableTools]
  | Active s a =>
    cases a with
    | Idle => simp [toState, stateToSessionState, availableTools]
    | Evaluating => simp [toState, stateToSessionState, availableTools]
  | Faulted => simp [toState, stateToSessionState, availableTools]

end SessionComposition
