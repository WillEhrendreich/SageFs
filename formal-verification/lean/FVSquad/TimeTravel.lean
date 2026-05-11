/-!
# Formal Specification: TimeTravel

This file contains:
1. Lean 4 types mirroring `TimeTravel.TimeTravelMode` and `TimeTravel.TimeTravelState<'Model>`
2. Pure functional implementation models for navigation operations
3. Stated and proved propositions covering the informal spec invariants

> 🔬 Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.
> Source: `SageFs.Core/TimeTravel.fs`
>
> **Model abstractions**:
> - The `SnapshotRing` is abstracted as a plain `Nat` (count) plus a capacity `Nat`.
> - `record` increments count (up to capacity); snapshot contents are not modelled.
> - `setEnabled`, `recentLabels`, `formatStatus` are not modelled (transparent delegates).
>
> No Mathlib.  Pure Lean 4 stdlib only (network firewalled in sandbox/CI).
-/

namespace TimeTravel

-- ─────────────────────────────────────────────────────────────────────────────
-- Types
-- ─────────────────────────────────────────────────────────────────────────────

/-- Whether we are viewing live state or a historical snapshot.
    `age` in `Viewing` is always ≥ 1. -/
inductive Mode where
  | Live    : Mode
  | Viewing : Nat → Mode
  deriving Repr, DecidableEq

/-- Abstract time-travel state. -/
structure TTState where
  mode     : Mode
  count    : Nat    -- snapshots in ring
  capacity : Nat    -- max ring capacity
  deriving Repr, DecidableEq

-- ─────────────────────────────────────────────────────────────────────────────
-- Operations
-- ─────────────────────────────────────────────────────────────────────────────

def create (cap : Nat) : TTState :=
  { mode := Mode.Live, count := 0, capacity := cap }

def isLive (s : TTState) : Bool :=
  match s.mode with
  | Mode.Live      => true
  | Mode.Viewing _ => false

def viewingAge (s : TTState) : Option Nat :=
  match s.mode with
  | Mode.Live      => none
  | Mode.Viewing n => some n

/-- Record a snapshot in Live mode; no-op in Viewing mode. -/
def record (s : TTState) : TTState :=
  match s.mode with
  | Mode.Viewing _ => s
  | Mode.Live      => { s with count := Nat.min (s.count + 1) s.capacity }

def stepBack (s : TTState) : TTState :=
  match s.mode with
  | Mode.Live =>
    if s.count > 1 then { s with mode := Mode.Viewing 1 }
    else s
  | Mode.Viewing age =>
    if age + 1 < s.count then { s with mode := Mode.Viewing (age + 1) }
    else s

def stepForward (s : TTState) : TTState :=
  match s.mode with
  | Mode.Live => s
  | Mode.Viewing age =>
    if age ≤ 1 then { s with mode := Mode.Live }
    else { s with mode := Mode.Viewing (age - 1) }

def goLive (s : TTState) : TTState :=
  { s with mode := Mode.Live }

-- ─────────────────────────────────────────────────────────────────────────────
-- Basic behavioural theorems
-- ─────────────────────────────────────────────────────────────────────────────

theorem create_isLive (cap : Nat) : isLive (create cap) = true := by
  simp [create, isLive]

theorem create_count_zero (cap : Nat) : (create cap).count = 0 := by
  simp [create]

theorem create_mode_live (cap : Nat) : (create cap).mode = Mode.Live := by
  simp [create]

theorem isLive_live (s : TTState) (h : s.mode = Mode.Live) : isLive s = true := by
  simp [isLive, h]

theorem isLive_viewing (s : TTState) (age : Nat) (h : s.mode = Mode.Viewing age) :
    isLive s = false := by
  simp [isLive, h]

theorem viewingAge_live (s : TTState) (h : s.mode = Mode.Live) : viewingAge s = none := by
  simp [viewingAge, h]

theorem viewingAge_viewing (s : TTState) (age : Nat) (h : s.mode = Mode.Viewing age) :
    viewingAge s = some age := by
  simp [viewingAge, h]

-- ─────────────────────────────────────────────────────────────────────────────
-- record theorems
-- ─────────────────────────────────────────────────────────────────────────────

theorem record_viewing_noop (s : TTState) (age : Nat) (h : s.mode = Mode.Viewing age) :
    record s = s := by
  simp [record, h]

theorem record_mode_unchanged (s : TTState) (h : s.mode = Mode.Live) :
    (record s).mode = Mode.Live := by
  simp [record, h]

theorem record_live_below_capacity (s : TTState) (h : s.mode = Mode.Live)
    (hlt : s.count < s.capacity) : (record s).count = s.count + 1 := by
  simp [record, h]
  have : s.count + 1 ≤ s.capacity := hlt
  exact Nat.min_eq_left this

theorem record_live_at_capacity (s : TTState) (h : s.mode = Mode.Live)
    (heq : s.count = s.capacity) : (record s).count = s.capacity := by
  simp [record, h, heq]

-- ─────────────────────────────────────────────────────────────────────────────
-- stepBack theorems
-- ─────────────────────────────────────────────────────────────────────────────

theorem stepBack_live_noop (s : TTState) (h : s.mode = Mode.Live) (hle : s.count ≤ 1) :
    stepBack s = s := by
  simp [stepBack, h]; omega

theorem stepBack_live_to_viewing_1 (s : TTState) (h : s.mode = Mode.Live)
    (hgt : s.count > 1) : (stepBack s).mode = Mode.Viewing 1 := by
  simp [stepBack, h, hgt]

theorem stepBack_viewing_increments (s : TTState) (age : Nat)
    (h : s.mode = Mode.Viewing age) (hlt : age + 1 < s.count) :
    (stepBack s).mode = Mode.Viewing (age + 1) := by
  simp [stepBack, h, hlt]

theorem stepBack_viewing_oldest_noop (s : TTState) (age : Nat)
    (h : s.mode = Mode.Viewing age) (hge : age + 1 ≥ s.count) :
    stepBack s = s := by
  simp [stepBack, h]; omega

theorem stepBack_count_unchanged (s : TTState) : (stepBack s).count = s.count := by
  simp only [stepBack]
  split
  · split
    · simp
    · rfl
  · split
    · simp
    · rfl

-- ─────────────────────────────────────────────────────────────────────────────
-- stepForward theorems
-- ─────────────────────────────────────────────────────────────────────────────

theorem stepForward_live_noop (s : TTState) (h : s.mode = Mode.Live) :
    stepForward s = s := by
  simp [stepForward, h]

theorem stepForward_viewing_1_to_live (s : TTState) (h : s.mode = Mode.Viewing 1) :
    (stepForward s).mode = Mode.Live := by
  simp [stepForward, h]

theorem stepForward_viewing_decrements (s : TTState) (age : Nat)
    (h : s.mode = Mode.Viewing age) (hgt : age ≥ 2) :
    (stepForward s).mode = Mode.Viewing (age - 1) := by
  simp only [stepForward, h]
  have hne : ¬ (age ≤ 1) := by omega
  simp [hne]

theorem stepForward_count_unchanged (s : TTState) : (stepForward s).count = s.count := by
  simp only [stepForward]
  split
  · rfl
  · split
    · simp
    · simp

-- ─────────────────────────────────────────────────────────────────────────────
-- goLive theorems
-- ─────────────────────────────────────────────────────────────────────────────

theorem goLive_is_live (s : TTState) : (goLive s).mode = Mode.Live := by
  simp [goLive]

theorem goLive_idempotent (s : TTState) : goLive (goLive s) = goLive s := by
  simp [goLive]

theorem goLive_count_unchanged (s : TTState) : (goLive s).count = s.count := by
  simp [goLive]

-- ─────────────────────────────────────────────────────────────────────────────
-- Round-trip theorems
-- ─────────────────────────────────────────────────────────────────────────────

/-- stepForward ∘ stepBack is identity when starting Live with count > 1. -/
theorem stepForward_stepBack_live_roundtrip (s : TTState)
    (hlive : s.mode = Mode.Live) (hgt : s.count > 1) :
    stepForward (stepBack s) = s := by
  cases s with
  | mk m c cap =>
    simp only at hlive
    simp only [stepBack, hlive, ↓reduceIte, hgt, stepForward]
    simp

/-- stepForward ∘ stepBack is identity when Viewing with room. -/
theorem stepForward_stepBack_viewing_roundtrip (s : TTState) (age : Nat)
    (hview : s.mode = Mode.Viewing age) (hlt : age + 1 < s.count) (hage : age ≥ 1) :
    stepForward (stepBack s) = s := by
  cases s with
  | mk m c cap =>
    simp only at hview
    have hne : ¬ (age + 1 ≤ 1) := by omega
    simp only [stepBack, hview, hlt, ↓reduceIte, stepForward, hne, ↓reduceIte]
    congr 1

/-- goLive after stepBack is always Live. -/
theorem goLive_after_stepBack_is_live (s : TTState) :
    (goLive (stepBack s)).mode = Mode.Live := by
  simp [goLive]

/-- record while Viewing is stable (idempotent/no-op). -/
theorem record_viewing_stable (s : TTState) (age : Nat) (h : s.mode = Mode.Viewing age) :
    record (record s) = s := by
  simp [record, h]

/-- A freshly created state cannot step back (count = 0). -/
theorem create_stepBack_noop (cap : Nat) : stepBack (create cap) = create cap := by
  simp [stepBack, create]

/-- A freshly created state stepForward is a no-op. -/
theorem create_stepForward_noop (cap : Nat) :
    stepForward (create cap) = create cap := by
  simp [stepForward, create]

/-- After exactly one record, stepBack is still a no-op (count = 1). -/
theorem one_record_stepBack_noop (cap : Nat) (h : 1 ≤ cap) :
    stepBack (record (create cap)) = record (create cap) := by
  simp only [record, create, stepBack]
  have hmin : Nat.min 1 cap = 1 := Nat.min_eq_left h
  simp [hmin]

end TimeTravel
