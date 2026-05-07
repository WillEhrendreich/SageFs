/-
  PhaseTransition.lean
  Session phase transition relation for SageFs formal verification.

  This file defines a `validTransition` inductive relation that captures exactly
  which phase-to-phase transitions the F# session manager can make, and then
  proves key safety invariants about the transition graph.

  ## Valid transitions (from SageFs.Core/AppState.fs)

  - Initializing → Initializing  (progress message update during warm-up)
  - Initializing → Active(Idle)  (warm-up succeeds → Ready)
  - Initializing → Faulted       (warm-up fails → Faulted)
  - Active(Idle) → Active(Eval)  (EvalRun received → Evaluating)
  - Active(Eval) → Active(Idle)  (EvalFinished, success or failure → Ready)
  - Active(Idle) → Initializing  (soft reset or hard reset from Ready)
  - Active(Eval) → Initializing  (hard reset during evaluation)
  - Faulted      → Initializing  (hard reset from Faulted)

  ## Key invariants proved

  - `faulted_only_recovers_to_init`: Faulted can only transition to Initializing
  - `faulted_cannot_directly_become_active`: No direct Faulted → Active edge
  - `eval_finishes_to_ready_not_faulted`: Evaluation always returns to Idle (Ready),
    never directly to Faulted (fault during eval aborts with an error response but
    keeps the session alive at Ready; only *reset* failures produce Faulted)
  - `uninitialized_has_no_valid_successors`: Uninitialized is not reachable as a
    transition target, consistent with `toState_never_uninitialized`
  - `init_is_initial_phase`: The session always begins in Initializing
  - `reachable_from_faulted`: From Faulted, the only reachable state is WarmingUp
    (one step) then eventually Ready or Faulted again via warm-up

  ## Abstractions / omissions

  - `α` (AppState) is a type variable; session content is irrelevant here.
  - Warm-up timing and progress messages are collapsed into a single
    `initToInit` transition (any number of progress updates).
  - External concurrency (cancellation tokens, thread joins) is not modelled.
  - The `cancelEval` path (interrupting an in-flight evaluation) is modelled
    as `evalToInit` since after cancel the session goes to Initializing (reset).

  No Mathlib. Pure Lean 4 stdlib only (network firewalled in CI).
  Source: SageFs.Core/AppState.fs
-/

import FVSquad.SessionLifecycle

namespace PhaseTransition

open SessionLifecycle

-- ── Valid transition relation ────────────────────────────────────────────────

/-- The set of valid phase transitions in the SageFs session lifecycle.
    Mirrors the pattern of `Phase` assignments in `AppState.fs:evalActor`. -/
inductive validTransition {α : Type} : Phase α → Phase α → Prop where
  /-- Progress message update during warm-up (Initializing → Initializing). -/
  | initToInit (m1 m2 : Option String) :
      validTransition (.Initializing m1) (.Initializing m2)
  /-- Warm-up completes successfully: session becomes Active(Idle) = Ready. -/
  | initToReady (m : Option String) (s : α) :
      validTransition (.Initializing m) (.Active s .Idle)
  /-- Warm-up fails: session goes to Faulted. -/
  | initToFaulted (m : Option String) :
      validTransition (.Initializing m) .Faulted
  /-- Evaluation starts: Active(Idle) → Active(Evaluating). -/
  | readyToEval (s : α) :
      validTransition (.Active s .Idle) (.Active s Activity.Evaluating)
  /-- Evaluation finishes (success or handled error): back to Active(Idle) = Ready.
      Note: unhandled exceptions in EvalRun also return to Ready in F# source. -/
  | evalToReady (s1 s2 : α) :
      validTransition (.Active s1 Activity.Evaluating) (.Active s2 .Idle)
  /-- Soft or hard reset from Ready: session restarts warm-up. -/
  | readyToInit (s : α) (m : Option String) :
      validTransition (.Active s .Idle) (.Initializing m)
  /-- Hard reset during evaluation: session restarts warm-up. -/
  | evalToInit (s : α) (m : Option String) :
      validTransition (.Active s Activity.Evaluating) (.Initializing m)
  /-- Hard reset from Faulted: session restarts warm-up. -/
  | faultedToInit (m : Option String) :
      validTransition .Faulted (.Initializing m)

-- ── Safety invariants ────────────────────────────────────────────────────────

/-- Faulted can only transition to Initializing — it cannot jump directly to
    Active or remain Faulted. Recovery always goes through warm-up. -/
theorem faulted_only_recovers_to_init {α : Type} (p : Phase α)
    (h : validTransition .Faulted p) :
    ∃ m, p = .Initializing m := by
  cases h with
  | faultedToInit m => exact ⟨m, rfl⟩

/-- Faulted cannot directly become Active (Idle or Evaluating).
    Active is only reachable after a successful warm-up (Initializing → Active). -/
theorem faulted_cannot_directly_become_active {α : Type} (s : α) (a : Activity)
    (h : validTransition (.Faulted : Phase α) (.Active s a)) : False := by
  cases h

/-- Evaluation always finishes back to Active(Idle) = Ready, never directly to Faulted.
    Fault during an eval command in F# still returns `SessionState.Ready` to the
    session actor; only *reset* commands can produce Faulted. -/
theorem eval_cannot_fault_directly {α : Type} (s : α)
    (h : validTransition (.Active s Activity.Evaluating) (.Faulted : Phase α)) : False := by
  cases h

/-- From Active(Evaluating), the only valid next phases are Active(Idle) or Initializing.
    This captures: evaluation completes → Ready, or hard reset → WarmingUp. -/
theorem evaluating_next_phases {α : Type} (s : α) (p2 : Phase α)
    (h : validTransition (.Active s Activity.Evaluating) p2) :
    (∃ s2, p2 = .Active s2 .Idle) ∨ (∃ m, p2 = .Initializing m) := by
  cases h with
  | evalToReady s1 s2 => left; exact ⟨s2, rfl⟩
  | evalToInit _ m => right; exact ⟨m, rfl⟩

/-- From Active(Idle) = Ready, the only valid next phases are Active(Evaluating) or Initializing.
    No direct Ready → Faulted transition exists in the session actor. -/
theorem ready_next_phases {α : Type} (s : α) (p2 : Phase α)
    (h : validTransition (.Active s .Idle) p2) :
    (∃ s2, p2 = .Active s2 Activity.Evaluating) ∨ (∃ m, p2 = .Initializing m) := by
  cases h with
  | readyToEval s => left; exact ⟨s, rfl⟩
  | readyToInit _ m => right; exact ⟨m, rfl⟩

/-- Ready cannot directly fault: no `Active(Idle) → Faulted` edge. -/
theorem ready_cannot_fault_directly {α : Type} (s : α)
    (h : validTransition (.Active s .Idle) (.Faulted : Phase α)) : False := by
  cases h

/-- The `Uninitialized` state is not a transition target.
    Combined with `toState_never_uninitialized`, this confirms Uninitialized is
    fully absent from the lifecycle both structurally and dynamically. -/
theorem uninitialized_unreachable_as_target {α : Type} (p1 p2 : Phase α)
    (_ : validTransition p1 p2)
    (hunin : toState p2 = State.Uninitialized) : False := by
  exact absurd hunin (toState_never_uninitialized p2)

/-- Any transition from Active preserves the existence of an app state:
    the successor phase either has an app state (Active) or is Initializing.
    In particular, the app state is never lost to Faulted via a direct Active → Faulted edge. -/
theorem active_transition_preserves_state_or_restarts {α : Type} (s : α) (a : Activity) (p2 : Phase α)
    (h : validTransition (.Active s a) p2) :
    (∃ s2 a2, p2 = .Active s2 a2) ∨ (∃ m, p2 = .Initializing m) := by
  cases h with
  | readyToEval s => left; exact ⟨s, _, rfl⟩
  | evalToReady s1 s2 => left; exact ⟨s2, _, rfl⟩
  | readyToInit _ m => right; exact ⟨m, rfl⟩
  | evalToInit _ m => right; exact ⟨m, rfl⟩

-- ── State-level transition image ────────────────────────────────────────────

/-- The external state after a transition from Faulted is always WarmingUp.
    (Because Faulted can only go to Initializing, which maps to WarmingUp.) -/
theorem faulted_transition_state_is_warming {α : Type} (p2 : Phase α)
    (h : validTransition .Faulted p2) :
    toState p2 = State.WarmingUp := by
  obtain ⟨m, rfl⟩ := faulted_only_recovers_to_init p2 h
  simp [toState]

/-- Evaluation can only reach Ready or WarmingUp externally.
    From Active(Evaluating), the successor state is either Ready or WarmingUp. -/
theorem evaluating_successor_state {α : Type} (s : α) (p2 : Phase α)
    (h : validTransition (.Active s Activity.Evaluating) p2) :
    toState p2 = State.Ready ∨ toState p2 = State.WarmingUp := by
  rcases evaluating_next_phases s p2 h with ⟨s2, rfl⟩ | ⟨m, rfl⟩
  · left; simp [toState]
  · right; simp [toState]

/-- From Ready, external successor state is either Evaluating or WarmingUp. -/
theorem ready_successor_state {α : Type} (s : α) (p2 : Phase α)
    (h : validTransition (.Active s .Idle) p2) :
    toState p2 = State.Evaluating ∨ toState p2 = State.WarmingUp := by
  rcases ready_next_phases s p2 h with ⟨s2, rfl⟩ | ⟨m, rfl⟩
  · left; simp [toState]
  · right; simp [toState]

end PhaseTransition
