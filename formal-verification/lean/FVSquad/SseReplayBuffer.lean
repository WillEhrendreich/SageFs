/-!
# Formal Specification: SseReplayBuffer

This file contains:
1. A minimal Lean 4 model of `SseReplayBuffer` abstracting away event content
2. Core operations: `sseCreate`, `ssePush`, `sseReplayFrom`
3. Proved propositions covering seqId monotonicity, well-formedness preservation,
   and the four exhaustive cases of `replayFrom`

> 🔬 Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.
> Source: `SageFs.Core/SseReplayBuffer.fs`
>
> **Model abstractions**:
> - Event content (`EventType`, `Payload`, `Timestamp`) is abstracted away; only
>   the structural state (`total`, `count`, `cap`) matters for correctness.
> - The inner `RingBuffer` is represented as `(total, count, cap)` — the three
>   fields that drive replay logic. The actual item array is omitted.
> - `int64` seqIds are modelled as `Nat` (non-negative, unbounded).
> - `DateTimeOffset` timestamps are omitted (no FV-relevant properties).

> **Lean 4 API notes (v4.30.0-rc2)**:
> - stdlib-only: use `theorem` not `lemma`; no `split_ifs` (use `split` instead).
> - `Nat.min_def` : `Nat.min a b = if a ≤ b then a else b`
> - Pattern: `simp only [Nat.min_def]; split <;> omega` for min arithmetic.
-/

-- ============================================================
-- Type definitions
-- ============================================================

/-- Abstract buffer state: only total pushes, retained count, and capacity matter.
    Mirrors `SseReplayBuffer.Buffer` (which wraps `RingBuffer<SequencedSseEvent>`). -/
structure SseBuffer where
  total : Nat   -- total events ever pushed (= seqId of last event, 0 if none)
  count : Nat   -- events currently retained in ring (≤ min(total, cap))
  cap   : Nat   -- ring capacity (fixed at creation)
  deriving Repr

/-- Well-formedness invariant for an SseBuffer. -/
def SseWellFormed (b : SseBuffer) : Prop :=
  0 < b.cap ∧ b.count ≤ b.cap ∧ b.count ≤ b.total

/-- Result of a reconnect replay attempt.
    `Replayed n` means `n` events are available to the client (count abstracted).
    `GapDetected firstAvail` means the oldest retained seqId is `firstAvail`. -/
inductive ReplayResult where
  | Replayed   (n          : Nat) : ReplayResult
  | GapDetected (firstAvail : Nat) : ReplayResult
  deriving Repr, DecidableEq

-- ============================================================
-- Operations
-- ============================================================

/-- Create a fresh buffer with a positive capacity. -/
def sseCreate (cap : Nat) (_ : 0 < cap) : SseBuffer :=
  { total := 0, count := 0, cap := cap }

/-- Push one event. Returns the assigned seqId and the updated buffer. -/
def ssePush (b : SseBuffer) : Nat × SseBuffer :=
  let seqId    := b.total + 1
  let newCount := Nat.min (b.count + 1) b.cap
  (seqId, { b with total := b.total + 1, count := newCount })

/-- Replay events the client has not yet seen.
    `lastSeen = 0` means the client has seen nothing.
    Mirrors `SseReplayBuffer.replayFrom`. -/
def sseReplayFrom (lastSeen : Nat) (b : SseBuffer) : ReplayResult :=
  if lastSeen ≥ b.total then
    .Replayed 0
  else if b.count = 0 then
    .GapDetected (b.total + 1)
  else
    let oldestAvailable := b.total - b.count + 1
    let firstWanted     := lastSeen + 1
    if firstWanted < oldestAvailable then
      .GapDetected oldestAvailable
    else
      .Replayed (b.total - lastSeen)

/-- Apply `ssePush` n times to a buffer (for inductive reasoning). -/
def applyPushN (n : Nat) (b : SseBuffer) : SseBuffer :=
  match n with
  | 0     => b
  | n + 1 => (ssePush (applyPushN n b)).2

-- ============================================================
-- SeqId / push theorems
-- ============================================================

/-- A freshly created buffer has total = 0. -/
theorem sseCreate_total_zero (cap : Nat) (h : 0 < cap) :
    (sseCreate cap h).total = 0 := rfl

/-- A freshly created buffer is well-formed. -/
theorem sseCreate_wf (cap : Nat) (h : 0 < cap) :
    SseWellFormed (sseCreate cap h) :=
  ⟨h, Nat.zero_le _, Nat.zero_le _⟩

/-- The seqId returned by `push` equals `b.total + 1`. -/
theorem ssePush_seqId (b : SseBuffer) :
    (ssePush b).1 = b.total + 1 := rfl

/-- After a push, the new `total` equals the old `total + 1`. -/
theorem ssePush_total (b : SseBuffer) :
    (ssePush b).2.total = b.total + 1 := rfl

/-- The seqId returned equals the new total. -/
theorem ssePush_seqId_eq_new_total (b : SseBuffer) :
    (ssePush b).1 = (ssePush b).2.total := rfl

/-- After a push, `count ≤ cap` (unconditionally). -/
theorem ssePush_count_le_cap (b : SseBuffer) :
    (ssePush b).2.count ≤ (ssePush b).2.cap := by
  simp only [ssePush, Nat.min_def]
  split <;> omega

/-- `count` is non-decreasing under push (requires count ≤ cap from WF). -/
theorem ssePush_count_mono (b : SseBuffer) (hwf : SseWellFormed b) :
    b.count ≤ (ssePush b).2.count := by
  obtain ⟨_, hcc, _⟩ := hwf
  simp only [ssePush, Nat.min_def]
  split <;> omega

/-- Push preserves well-formedness. -/
theorem ssePush_wf (b : SseBuffer) (hwf : SseWellFormed b) :
    SseWellFormed (ssePush b).2 := by
  obtain ⟨hcap, hcc, hct⟩ := hwf
  refine ⟨hcap, ssePush_count_le_cap b, ?_⟩
  simp only [ssePush, Nat.min_def]
  split <;> omega

/-- After `n` pushes from any buffer, total increases by `n`. -/
theorem applyPushN_total (b : SseBuffer) (n : Nat) :
    (applyPushN n b).total = b.total + n := by
  induction n with
  | zero      => simp [applyPushN]
  | succ k ih => simp only [applyPushN, ssePush]; omega

/-- After `n` pushes from a fresh buffer, total = n. -/
theorem sseCreate_push_n_total (cap : Nat) (hcap : 0 < cap) (n : Nat) :
    (applyPushN n (sseCreate cap hcap)).total = n := by
  rw [applyPushN_total]; simp [sseCreate]

/-- The first seqId issued from a fresh buffer is 1. -/
theorem seqId_one_based (cap : Nat) (hcap : 0 < cap) :
    (ssePush (sseCreate cap hcap)).1 = 1 := rfl

-- ============================================================
-- replayFrom shape theorems
-- ============================================================

/-- Case 1: client is up-to-date → empty replay. -/
theorem replayFrom_up_to_date (b : SseBuffer) (lastSeen : Nat)
    (h : lastSeen ≥ b.total) :
    sseReplayFrom lastSeen b = .Replayed 0 := by
  unfold sseReplayFrom; rw [if_pos h]

/-- Case 2: ring empty, client not current → GapDetected(total + 1). -/
theorem replayFrom_empty_ring (b : SseBuffer) (lastSeen : Nat)
    (htotal : lastSeen < b.total) (hcnt : b.count = 0) :
    sseReplayFrom lastSeen b = .GapDetected (b.total + 1) := by
  unfold sseReplayFrom
  rw [if_neg (by omega), if_pos hcnt]

/-- Case 3: gap detected → GapDetected(oldestAvailable). -/
theorem replayFrom_gap (b : SseBuffer) (lastSeen : Nat)
    (htotal : lastSeen < b.total)
    (hcnt   : 0 < b.count)
    (hgap   : lastSeen + 1 < b.total - b.count + 1) :
    sseReplayFrom lastSeen b = .GapDetected (b.total - b.count + 1) := by
  unfold sseReplayFrom
  rw [if_neg (by omega), if_neg (by omega), if_pos hgap]

/-- Case 4: no gap → Replayed(total − lastSeen). -/
theorem replayFrom_no_gap (b : SseBuffer) (lastSeen : Nat)
    (htotal : lastSeen < b.total)
    (hcnt   : 0 < b.count)
    (hnogap : lastSeen + 1 ≥ b.total - b.count + 1) :
    sseReplayFrom lastSeen b = .Replayed (b.total - lastSeen) := by
  unfold sseReplayFrom
  rw [if_neg (by omega), if_neg (by omega), if_neg (by omega)]

-- ============================================================
-- replayFrom consequence theorems
-- ============================================================

/-- In the no-gap case, the replayed count is positive. -/
theorem replayFrom_no_gap_count_pos (b : SseBuffer) (lastSeen : Nat)
    (htotal : lastSeen < b.total) :
    0 < b.total - lastSeen := by omega

/-- In the no-gap case, replayed count ≤ ring retained count. -/
theorem replayFrom_no_gap_count_le_ring (b : SseBuffer) (lastSeen : Nat)
    (hwf    : SseWellFormed b)
    (hnogap : lastSeen + 1 ≥ b.total - b.count + 1) :
    b.total - lastSeen ≤ b.count := by
  obtain ⟨_, _, hct⟩ := hwf; omega

/-- A fresh buffer always returns an empty replay regardless of lastSeen. -/
theorem replayFrom_fresh_always_empty (cap : Nat) (hcap : 0 < cap) (lastSeen : Nat) :
    sseReplayFrom lastSeen (sseCreate cap hcap) = .Replayed 0 :=
  replayFrom_up_to_date _ _ (by simp [sseCreate])

/-- After one push from a fresh buffer, replaying from 0 returns exactly 1 event. -/
theorem replayFrom_after_one_push (cap : Nat) (hcap : 0 < cap) :
    sseReplayFrom 0 (ssePush (sseCreate cap hcap)).2 = .Replayed 1 := by
  have hmin1 : Nat.min 1 cap = 1 := by
    simp only [Nat.min_def]; split <;> omega
  have hbuf : (ssePush (sseCreate cap hcap)).2 = { total := 1, count := 1, cap := cap } := by
    simp [ssePush, sseCreate, hmin1]
  rw [hbuf]
  rfl
