/-!
# Formal Specification: RingBuffer

This file contains:
1. A Lean 4 type mirroring the F# `RingBuffer<'T>` record
2. A pure functional implementation model for `create`, `push`, `tryGet`,
   `clear`, and `toList`
3. Stated and proved propositions covering the invariants from the informal spec

> 🔬 Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.
> Source: `SageFs.Core/RingBuffer.fs`
>
> **Model abstractions**: The F# `push` mutates the `Items` array in place
> before returning a new record. This Lean model uses `Array.set` to produce
> a new array, capturing the same observable input-to-output semantics while
> ignoring in-place mutation and structural sharing.
>
> **Lean 4 API notes (v4.30.0-rc2)**:
> - `Array.set (xs : Array α) (i : Nat) (v : α) (h : i < xs.size) : Array α`
> - `Array.size_set (h : i < xs.size) : (xs.set i v h).size = xs.size`
> - `Array.getElem_set_self (h : i < xs.size) : (xs.set i v h)[i] = v`
-/

-- ============================================================
-- Type definition
-- ============================================================

/-- A fixed-capacity ring buffer holding up to `capacity` items.
    Items are indexed by age: 0 = most recent, 1 = one step back, etc.
    `head` is the index of the most-recently-pushed slot.
    `count` is the number of valid items currently stored.
    `total` is the total number of pushes ever performed. -/
structure RingBuffer (α : Type) where
  items    : Array α
  head     : Nat
  count    : Nat
  total    : Nat
  deriving Repr

-- ============================================================
-- Well-formedness predicate
-- ============================================================

/-- A ring buffer is well-formed when its structural invariants hold. -/
def WellFormed (buf : RingBuffer α) : Prop :=
  0 < buf.items.size ∧
  buf.head < buf.items.size ∧
  buf.count ≤ buf.items.size ∧
  buf.count ≤ buf.total

-- ============================================================
-- Implementation model
-- ============================================================

/-- Create an empty ring buffer with the given capacity.
    Precondition (captured by caller): `cap > 0`. -/
def rbCreate (default : α) (cap : Nat) (_ : 0 < cap) : RingBuffer α :=
  { items := Array.replicate cap default
    head  := 0
    count := 0
    total := 0 }

/-- Push a new item. If full, the oldest item is evicted.
    `newHead = (head + cap - 1) % cap` moves head one step back (ring buffer
    is most-recent-first, so head moves towards lower indices). -/
def rbPush (x : α) (buf : RingBuffer α) : RingBuffer α :=
  let newHead := (buf.head + buf.items.size - 1) % buf.items.size
  if h : newHead < buf.items.size then
    { items := buf.items.set newHead x h
      head  := newHead
      count := Nat.min (buf.count + 1) buf.items.size
      total := buf.total + 1 }
  else
    -- unreachable when cap > 0; keeps totality
    buf

/-- Get an item by age (0 = most recent, 1 = one step back, etc.).
    Returns `none` if `age ≥ count`. -/
def rbTryGet (age : Nat) (buf : RingBuffer α) : Option α :=
  if age < buf.count then
    let cap := buf.items.size
    if hcap : 0 < cap then
      some (buf.items[(buf.head + age) % cap]'(Nat.mod_lt _ hcap))
    else none
  else none

/-- Clear: reset count and head, preserve total pushed count. -/
def rbClear (default : α) (buf : RingBuffer α) : RingBuffer α :=
  { items := Array.replicate buf.items.size default
    head  := 0
    count := 0
    total := buf.total }

/-- Convert to list (most-recent first). -/
def rbToList (buf : RingBuffer α) : List α :=
  (List.range buf.count).filterMap (fun age => rbTryGet age buf)

-- ============================================================
-- Helper lemmas
-- ============================================================

private theorem rbPush_newHead_lt (buf : RingBuffer α) (hcap : 0 < buf.items.size) :
    (buf.head + buf.items.size - 1) % buf.items.size < buf.items.size :=
  Nat.mod_lt _ hcap

private theorem rbPush_items_size (x : α) (buf : RingBuffer α) (hcap : 0 < buf.items.size) :
    (rbPush x buf).items.size = buf.items.size := by
  unfold rbPush
  have hh := rbPush_newHead_lt buf hcap
  simp only [dif_pos hh, Array.size_set hh]

private theorem rbTryGet_some_of_lt (buf : RingBuffer α) (hcap : 0 < buf.items.size)
    (age : Nat) (hage : age < buf.count) :
    ∃ v, rbTryGet age buf = some v := by
  unfold rbTryGet
  simp only [if_pos hage, dif_pos hcap]
  exact ⟨_, rfl⟩

-- ============================================================
-- Theorems: WellFormed preservation
-- ============================================================

theorem create_wellFormed (default : α) (cap : Nat) (hcap : 0 < cap) :
    WellFormed (rbCreate default cap hcap) := by
  unfold WellFormed rbCreate
  simp [Array.size_replicate, hcap]

theorem push_wellFormed (x : α) (buf : RingBuffer α) (hw : WellFormed buf) :
    WellFormed (rbPush x buf) := by
  obtain ⟨hcap, hhd, hcnt, htot⟩ := hw
  unfold WellFormed rbPush
  have hh := rbPush_newHead_lt buf hcap
  simp only [dif_pos hh, Array.size_set hh]
  have hmin_le : Nat.min (buf.count + 1) buf.items.size ≤ buf.count + 1 :=
    Nat.min_le_left _ _
  exact ⟨hcap, hh, Nat.min_le_right _ _, by omega⟩

-- ============================================================
-- Theorems: Capacity
-- ============================================================

theorem create_capacity (default : α) (cap : Nat) (hcap : 0 < cap) :
    (rbCreate default cap hcap).items.size = cap := by
  simp [rbCreate, Array.size_replicate]

theorem push_capacity (x : α) (buf : RingBuffer α) (hw : WellFormed buf) :
    (rbPush x buf).items.size = buf.items.size :=
  rbPush_items_size x buf hw.1

theorem clear_capacity (default : α) (buf : RingBuffer α) :
    (rbClear default buf).items.size = buf.items.size := by
  simp [rbClear, Array.size_replicate]

-- ============================================================
-- Theorems: Count
-- ============================================================

theorem create_count_zero (default : α) (cap : Nat) (hcap : 0 < cap) :
    (rbCreate default cap hcap).count = 0 := by
  simp [rbCreate]

theorem push_count (x : α) (buf : RingBuffer α) (hw : WellFormed buf) :
    (rbPush x buf).count = Nat.min (buf.count + 1) buf.items.size := by
  obtain ⟨hcap, _, _, _⟩ := hw
  unfold rbPush
  simp only [dif_pos (rbPush_newHead_lt buf hcap)]

theorem clear_count (default : α) (buf : RingBuffer α) :
    (rbClear default buf).count = 0 := by
  simp [rbClear]

theorem count_le_capacity (buf : RingBuffer α) (hw : WellFormed buf) :
    buf.count ≤ buf.items.size :=
  hw.2.2.1

theorem count_nonneg (buf : RingBuffer α) : 0 ≤ buf.count := Nat.zero_le _

-- ============================================================
-- Theorems: Total pushed
-- ============================================================

theorem create_total_zero (default : α) (cap : Nat) (hcap : 0 < cap) :
    (rbCreate default cap hcap).total = 0 := by
  simp [rbCreate]

theorem push_total (x : α) (buf : RingBuffer α) (hw : WellFormed buf) :
    (rbPush x buf).total = buf.total + 1 := by
  obtain ⟨hcap, _, _, _⟩ := hw
  unfold rbPush
  simp only [dif_pos (rbPush_newHead_lt buf hcap)]

theorem clear_total (default : α) (buf : RingBuffer α) :
    (rbClear default buf).total = buf.total := by
  simp [rbClear]

-- ============================================================
-- Theorems: Eviction accounting
-- ============================================================

theorem total_ge_count (buf : RingBuffer α) (hw : WellFormed buf) :
    buf.count ≤ buf.total :=
  hw.2.2.2

/-- evictedCount = total - count (as integers, always non-negative) -/
def evictedCount (buf : RingBuffer α) : Int :=
  (Int.ofNat buf.total) - (Int.ofNat buf.count)

theorem evictedCount_eq (buf : RingBuffer α) :
    evictedCount buf = Int.ofNat buf.total - Int.ofNat buf.count := by
  simp [evictedCount]

theorem evictedCount_nonneg (buf : RingBuffer α) (hw : WellFormed buf) :
    0 ≤ evictedCount buf := by
  unfold evictedCount
  exact Int.sub_nonneg_of_le (Int.ofNat_le.mpr hw.2.2.2)

-- ============================================================
-- Theorems: tryGet bounds
-- ============================================================

theorem tryGet_none_of_ge (buf : RingBuffer α) (age : Nat)
    (h : buf.count ≤ age) :
    rbTryGet age buf = none := by
  unfold rbTryGet
  simp [Nat.not_lt.mpr h]

-- ============================================================
-- Theorems: toList length
-- ============================================================

theorem toList_length (buf : RingBuffer α) (hw : WellFormed buf) :
    (rbToList buf).length = buf.count := by
  unfold rbToList
  -- Every element in List.range buf.count gives Some, so filterMap = map
  suffices h : ∀ (l : List Nat), (∀ a ∈ l, a < buf.count) →
      (l.filterMap (fun age => rbTryGet age buf)).length = l.length by
    have := h (List.range buf.count) (by simp [List.mem_range])
    simp [List.length_range] at this
    exact this
  intro l hl
  induction l with
  | nil => simp
  | cons a t ih =>
    have ha : a < buf.count := hl a List.mem_cons_self
    obtain ⟨v, hv⟩ := rbTryGet_some_of_lt buf hw.1 a ha
    simp only [List.filterMap_cons, hv, List.length_cons]
    congr 1
    apply ih
    intro x hx
    exact hl x (List.mem_cons.mpr (Or.inr hx))

-- ============================================================
-- Helper lemmas for push_aging
-- ============================================================

/-- `(a % n + b) % n = (a + b) % n` — mod distributes over addition. -/
private theorem mod_add_left (a b n : Nat) (hn : 0 < n) : (a % n + b) % n = (a + b) % n := by
  rw [Nat.add_mod (a % n) b n, Nat.mod_eq_of_lt (Nat.mod_lt a hn), ← Nat.add_mod]

/-- After push, the ring-buffer index for age `k+1` equals the index for age `k`
    in the original buffer. -/
private theorem index_after_push (h k n : Nat) (hn : 0 < n) :
    ((h + n - 1) % n + (k + 1)) % n = (h + k) % n := by
  rw [mod_add_left _ _ _ hn]
  have : h + n - 1 + (k + 1) = h + k + n := by omega
  rw [this, Nat.add_mod_right]

/-- When `k + 1 < n`, the post-push head index differs from the age-`k` slot,
    so the push does not overwrite the element we want to read. -/
private theorem newHead_ne_index (h k n : Nat) (hn : 0 < n) (hk2 : k + 1 < n) (hh : h < n) :
    (h + n - 1) % n ≠ (h + k) % n := by
  intro heq
  rcases Nat.eq_zero_or_pos h with rfl | hh0
  · -- h = 0: newHead = n − 1, age-k index = k
    simp only [Nat.zero_add] at heq
    rw [Nat.mod_eq_of_lt (by omega : n - 1 < n),
        Nat.mod_eq_of_lt (by omega : k < n)] at heq
    omega
  · -- h > 0: newHead = h − 1
    have hnH : h + n - 1 = h - 1 + n := by omega
    rw [hnH, Nat.add_mod_right, Nat.mod_eq_of_lt (by omega : h - 1 < n)] at heq
    rcases Nat.lt_or_ge (h + k) n with hlt | hge
    · rw [Nat.mod_eq_of_lt hlt] at heq; omega
    · have hlt2 : h + k - n < n := by omega
      rw [show h + k = h + k - n + n from by omega,
          Nat.add_mod_right, Nat.mod_eq_of_lt hlt2] at heq
      omega

-- ============================================================
-- Theorems: push semantics
-- ============================================================

theorem push_tryGet_zero (x : α) (buf : RingBuffer α) (hw : WellFormed buf) :
    rbTryGet 0 (rbPush x buf) = some x := by
  obtain ⟨hcap, hhd, hcnt, htot⟩ := hw
  have hh := rbPush_newHead_lt buf hcap
  -- First reduce rbPush to a concrete struct (avoids nested dif in goal)
  have hpush : rbPush x buf =
      { items := buf.items.set ((buf.head + buf.items.size - 1) % buf.items.size) x hh
        head  := (buf.head + buf.items.size - 1) % buf.items.size
        count := (buf.count + 1).min buf.items.size
        total := buf.total + 1 } := by
    simp [rbPush, dif_pos hh]
  rw [hpush]
  unfold rbTryGet
  have hpos : 0 < (buf.count + 1).min buf.items.size :=
    Nat.lt_min.mpr ⟨Nat.succ_pos _, hcap⟩
  simp only [Array.size_set hh, if_pos hpos, dif_pos hcap,
             Nat.add_zero, Nat.mod_eq_of_lt hh, Array.getElem_set_self hh]

/-- push ages prior items: `tryGet (k+1) (push x buf) = tryGet k buf`
    for all `k < buf.count` where `k + 1 < capacity` (the item is not evicted).

    Note: when `buf.count = capacity` and `k = capacity − 1`, the push evicts the
    oldest item, so `tryGet k buf` returns `some v` but `tryGet (k+1) (push x buf)`
    returns `none`.  The extra hypothesis `hk2` excludes that edge case. -/
theorem push_aging (x : α) (buf : RingBuffer α) (hw : WellFormed buf)
    (k : Nat) (hk : k < buf.count) (hk2 : k + 1 < buf.items.size) :
    rbTryGet (k + 1) (rbPush x buf) = rbTryGet k buf := by
  obtain ⟨hcap, hhd, hcnt, htot⟩ := hw
  have hh := rbPush_newHead_lt buf hcap
  -- k+1 is within the pushed buffer's count
  have hk1_lt : k + 1 < (buf.count + 1).min buf.items.size :=
    Nat.lt_min.mpr ⟨by omega, hk2⟩
  -- the age-k index in the original buffer
  have hidx : (buf.head + k) % buf.items.size < buf.items.size := Nat.mod_lt _ hcap
  -- index for age k+1 after push equals index for age k before push
  have hindex_eq :
      ((buf.head + buf.items.size - 1) % buf.items.size + (k + 1)) % buf.items.size =
      (buf.head + k) % buf.items.size :=
    index_after_push buf.head k buf.items.size hcap
  -- the push writes to a different slot than the age-k slot
  have hne :
      (buf.head + buf.items.size - 1) % buf.items.size ≠
      (buf.head + k) % buf.items.size :=
    newHead_ne_index buf.head k buf.items.size hcap hk2 hhd
  -- expand rbPush
  have hpush : rbPush x buf =
      { items := buf.items.set ((buf.head + buf.items.size - 1) % buf.items.size) x hh
        head  := (buf.head + buf.items.size - 1) % buf.items.size
        count := (buf.count + 1).min buf.items.size
        total := buf.total + 1 } := by
    simp [rbPush, dif_pos hh]
  -- simplify RHS
  have hrhs : rbTryGet k buf =
      some (buf.items[(buf.head + k) % buf.items.size]'hidx) := by
    unfold rbTryGet; simp [if_pos hk, dif_pos hcap]
  rw [hrhs, hpush]
  -- simplify LHS
  unfold rbTryGet
  simp only [Array.size_set hh, if_pos hk1_lt, dif_pos hcap]
  congr 1
  -- rewrite the index using hindex_eq (simp handles dependent rewrites)
  simp only [hindex_eq]
  -- the push does not touch this slot
  exact Array.getElem_set_ne hh hidx hne
