/-
  SessionLifecycle.lean
  Formal specification for the SageFs session lifecycle state machine.

  Targets:
  - `SessionPhase`   (Initializing / Active / Faulted) — rich internal phase
  - `SessionActivity` (Idle / Evaluating) — activity within an active session
  - `SessionState`   (Uninitialized / WarmingUp / Ready / Evaluating / Faulted)
  - `toState`        — projects SessionPhase onto the external SessionState
  - `tryAppState`    — extracts AppState (abstracted as α) when Active
  - `State.label`    — string label for each SessionState value

  Key finding:
  - `SessionState.Uninitialized` is UNREACHABLE via `toState` —
    `SessionPhase` has no Uninitialized case; Initializing _ maps to WarmingUp.

  Abstractions:
  - `AppState` is abstracted to a type variable `α`; its internal fields are
    irrelevant to the lifecycle state machine properties verified here.

  Note: `Activity.Evaluating` and `State.Evaluating` are distinct constructors.
  We use qualified names throughout to avoid ambiguity.

  No Mathlib. Pure Lean 4 stdlib only (network firewalled in sandbox/CI).
  Source: SageFs.Core/AppState.fs, SageFs.Core/SessionState.fs
-/

namespace SessionLifecycle

-- ── Type definitions ────────────────────────────────────────────────────────

/-- Whether an active session is idle or currently evaluating code.
    Only meaningful when `Phase = Active`. Mirrors F# `SessionActivity`. -/
inductive Activity where
  | Idle
  | Evaluating
  deriving DecidableEq, Repr

/-- Rich session lifecycle phase — makes impossible states unrepresentable.
    `AppState` (abstracted as `α`) is only present when `Active`.
    Mirrors F# `SessionPhase` in SageFs.Core/AppState.fs. -/
inductive Phase (α : Type) where
  | Initializing (msg : Option String)
  | Active (st : α) (act : Activity)
  | Faulted
  deriving Repr

/-- Session state for external consumers (MCP, dashboard).
    Mirrors F# `SessionState` in SageFs.Core/SessionState.fs. -/
inductive State where
  | Uninitialized
  | WarmingUp
  | Ready
  | Evaluating
  | Faulted
  deriving DecidableEq, Repr

-- ── Implementation models ────────────────────────────────────────────────────

/-- Map a lifecycle phase to the externally-visible session state.
    Mirrors F# `SessionPhase.toSessionState`. -/
def toState {α : Type} : Phase α → State
  | .Initializing _ => State.WarmingUp
  | .Active _ .Idle => State.Ready
  | .Active _ .Evaluating => State.Evaluating
  | .Faulted => State.Faulted

/-- Extract the application state when the session is active; `none` otherwise.
    Mirrors F# `SessionPhase.tryAppState`. -/
def tryAppState {α : Type} : Phase α → Option α
  | .Active st _ => some st
  | .Initializing _ => none
  | .Faulted => none

/-- String label for each session state.
    Mirrors F# `SessionState.label`. -/
def State.label : State → String
  | State.Uninitialized => "Uninitialized"
  | State.WarmingUp => "WarmingUp"
  | State.Ready => "Ready"
  | State.Evaluating => "Evaluating"
  | State.Faulted => "Faulted"

-- ── Spec: toState — individual case theorems ─────────────────────────────────

/-- Initializing phase maps to WarmingUp state. -/
theorem toState_initializing {α : Type} (msg : Option String) :
    toState (.Initializing (α := α) msg) = State.WarmingUp := by
  simp [toState]

/-- Active-Idle phase maps to Ready state. -/
theorem toState_active_idle {α : Type} (s : α) :
    toState (.Active s .Idle) = State.Ready := by
  simp [toState]

/-- Active-Evaluating phase maps to Evaluating state. -/
theorem toState_active_evaluating {α : Type} (s : α) :
    toState (.Active s Activity.Evaluating) = State.Evaluating := by
  simp [toState]

/-- Faulted phase maps to Faulted state. -/
theorem toState_faulted {α : Type} :
    toState (.Faulted : Phase α) = State.Faulted := by
  simp [toState]

-- ── Spec: tryAppState — individual case theorems ─────────────────────────────

/-- tryAppState returns Some for any Active phase. -/
theorem tryAppState_active {α : Type} (s : α) (a : Activity) :
    tryAppState (.Active s a) = some s := by
  simp [tryAppState]

/-- tryAppState returns None for Initializing. -/
theorem tryAppState_initializing {α : Type} (msg : Option String) :
    tryAppState (.Initializing (α := α) msg) = none := by
  simp [tryAppState]

/-- tryAppState returns None for Faulted. -/
theorem tryAppState_faulted {α : Type} :
    tryAppState (.Faulted : Phase α) = none := by
  simp [tryAppState]

-- ── Spec: key lifecycle invariants ───────────────────────────────────────────

/-- The `Uninitialized` state is unreachable via `toState`.
    `SessionPhase` has no Uninitialized case; Initializing _ maps to WarmingUp. -/
theorem toState_never_uninitialized {α : Type} (p : Phase α) :
    toState p ≠ State.Uninitialized := by
  cases p with
  | Initializing msg => simp [toState]
  | Active s a => cases a <;> simp [toState]
  | Faulted => simp [toState]

/-- `tryAppState` returns `Some` exactly when the phase is `Active`. -/
theorem tryAppState_some_iff_active {α : Type} (p : Phase α) :
    tryAppState p ≠ none ↔ ∃ s a, p = .Active s a := by
  constructor
  · intro h
    cases p with
    | Initializing msg => simp [tryAppState] at h
    | Active s a => exact ⟨s, a, rfl⟩
    | Faulted => simp [tryAppState] at h
  · rintro ⟨s, a, rfl⟩
    simp [tryAppState]

/-- Phase is `Active _ Idle` iff state is `Ready`. -/
theorem ready_iff_active_idle {α : Type} (p : Phase α) :
    toState p = State.Ready ↔ ∃ s, p = .Active s .Idle := by
  constructor
  · intro h
    cases p with
    | Initializing msg => simp [toState] at h
    | Active s a =>
      cases a with
      | Idle => exact ⟨s, rfl⟩
      | Evaluating => simp [toState] at h
    | Faulted => simp [toState] at h
  · rintro ⟨s, rfl⟩
    simp [toState]

/-- Phase is `Active _ Evaluating` iff state is `Evaluating`. -/
theorem evaluating_iff_active_evaluating {α : Type} (p : Phase α) :
    toState p = State.Evaluating ↔ ∃ s, p = .Active s Activity.Evaluating := by
  constructor
  · intro h
    cases p with
    | Initializing msg => simp [toState] at h
    | Active s a =>
      cases a with
      | Idle => simp [toState] at h
      | Evaluating => exact ⟨s, rfl⟩
    | Faulted => simp [toState] at h
  · rintro ⟨s, rfl⟩
    simp [toState]

/-- Phase is `Faulted` iff state is `Faulted`. -/
theorem faulted_iff {α : Type} (p : Phase α) :
    toState p = State.Faulted ↔ p = .Faulted := by
  constructor
  · intro h
    cases p with
    | Initializing msg => simp [toState] at h
    | Active s a => cases a <;> simp [toState] at h
    | Faulted => rfl
  · rintro rfl
    simp [toState]

/-- Phase is `Initializing _` iff state is `WarmingUp`. -/
theorem warming_up_iff {α : Type} (p : Phase α) :
    toState p = State.WarmingUp ↔ ∃ m, p = .Initializing m := by
  constructor
  · intro h
    cases p with
    | Initializing msg => exact ⟨msg, rfl⟩
    | Active s a => cases a <;> simp [toState] at h
    | Faulted => simp [toState] at h
  · rintro ⟨m, rfl⟩
    simp [toState]

/-- If `tryAppState` returns `Some`, the state is `Ready` or `Evaluating`. -/
theorem tryAppState_some_implies_active_state {α : Type} (p : Phase α) (s : α) :
    tryAppState p = some s →
    toState p = State.Ready ∨ toState p = State.Evaluating := by
  intro h
  cases p with
  | Initializing msg => simp [tryAppState] at h
  | Active s' a =>
    cases a with
    | Idle => left; simp [toState]
    | Evaluating => right; simp [toState]
  | Faulted => simp [tryAppState] at h

/-- If the state is `Ready` or `Evaluating`, `tryAppState` returns `Some`. -/
theorem active_state_implies_tryAppState_some {α : Type} (p : Phase α) :
    (toState p = State.Ready ∨ toState p = State.Evaluating) →
    ∃ s, tryAppState p = some s := by
  intro h
  cases p with
  | Initializing msg => simp [toState] at h
  | Active s a => exact ⟨s, by simp [tryAppState]⟩
  | Faulted => simp [toState] at h

/-- `State.label` is injective: distinct states have distinct labels. -/
theorem State.label_injective : Function.Injective State.label := by
  intro a b h
  cases a <;> cases b <;> simp [State.label] at h ⊢

end SessionLifecycle
