/-
  RestartPolicy.lean
  Formal specification for SageFs.Core.RestartPolicy / RetryPolicy.

  The F# source implements Erlang-style exponential backoff with a sliding
  time window reset.  This model abstracts TimeSpan/DateTime to plain Nat
  (milliseconds) and replaces SageFsError with Unit, so all functions are
  pure and termination is structurally obvious.

  No Mathlib.  Pure Lean 4 stdlib only (network firewalled on CI).
-/

-- ── Type definitions ────────────────────────────────────────────────────────

/-- Restart policy configuration (mirrors F# RestartPolicy record). -/
structure RPPolicy where
  baseMs      : Nat  -- base back-off delay (milliseconds)
  maxMs       : Nat  -- maximum back-off delay (≥ baseMs for sensible policies)
  maxRestarts : Nat  -- maximum consecutive restart count before giving up
  deriving Repr

/-- Mutable restart state. -/
structure RPState where
  count         : Nat   -- consecutive restart count
  windowExpired : Bool  -- whether the sliding reset window has elapsed
  deriving Repr

/-- Outcome of one call to rbDecide. -/
inductive RPDecision
  | restart (delayMs : Nat)
  | giveUp
  deriving Repr, DecidableEq

-- ── Pure implementation model ────────────────────────────────────────────────

/--
  Compute the back-off delay for the *next* restart attempt.

  F# semantics (from RestartPolicy.fs):
    count = 0 → baseMs
    count ≥ 1 → min(baseMs * 2^(min(count,20)-1), maxMs)

  We keep exact semantics here so proofs reflect the real implementation.
-/
def nextBackoffMs (policy : RPPolicy) (count : Nat) : Nat :=
  if count = 0 then
    policy.baseMs
  else
    min (policy.baseMs * 2 ^ (min count 20 - 1)) policy.maxMs

/--
  Decide whether to restart or give up, given current policy and state.

  Returns the decision paired with the *updated* RPState.
  The model treats window-expiry as a single Bool field so we avoid
  DateTime arithmetic entirely.
-/
def rbDecide (policy : RPPolicy) (state : RPState) : RPDecision × RPState :=
  let ec : Nat := if state.windowExpired then 0 else state.count
  if ec ≥ policy.maxRestarts then
    (RPDecision.giveUp, { count := ec, windowExpired := false })
  else
    ( RPDecision.restart (nextBackoffMs policy ec)
    , { count := ec + 1, windowExpired := false } )

/-- Effective restart count: reset to 0 when the window has expired. -/
def effectiveCount (state : RPState) : Nat :=
  if state.windowExpired then 0 else state.count

-- ── Theorems ─────────────────────────────────────────────────────────────────

-- nextBackoffMs: base case
theorem nextBackoffMs_zero (policy : RPPolicy) :
    nextBackoffMs policy 0 = policy.baseMs := by
  simp [nextBackoffMs]

-- nextBackoffMs: the result never exceeds maxMs
theorem nextBackoffMs_le_max (policy : RPPolicy) (count : Nat) (h : 0 < count) :
    nextBackoffMs policy count ≤ policy.maxMs := by
  have hne : ¬(count = 0) := by omega
  simp only [nextBackoffMs, if_neg hne]
  exact Nat.min_le_right _ _

-- Helper: 2^n ≥ 1 for all n (proved by induction, no Mathlib needed).
private theorem one_le_two_pow (n : Nat) : 1 ≤ 2 ^ n := by
  induction n with
  | zero => simp
  | succ k ih =>
    simp only [Nat.pow_succ]
    omega

-- nextBackoffMs: the result is at least baseMs (when policy.baseMs ≤ policy.maxMs)
theorem nextBackoffMs_ge_base (policy : RPPolicy) (count : Nat) (h : 0 < count)
    (hpol : policy.baseMs ≤ policy.maxMs) :
    policy.baseMs ≤ nextBackoffMs policy count := by
  have hne : ¬(count = 0) := by omega
  simp only [nextBackoffMs, if_neg hne]
  -- Goal: policy.baseMs ≤ min (policy.baseMs * 2^(min count 20 - 1)) policy.maxMs
  -- We prove both: baseMs ≤ baseMs * 2^n  and  baseMs ≤ maxMs
  rw [Nat.le_min]
  refine ⟨?_, hpol⟩
  -- baseMs ≤ baseMs * 2^(min count 20 - 1)
  -- Since 2^n ≥ 1, we have baseMs * 1 ≤ baseMs * 2^n, i.e. baseMs ≤ baseMs * 2^n.
  have hpow : 1 ≤ 2 ^ (min count 20 - 1) := one_le_two_pow _
  calc policy.baseMs = policy.baseMs * 1 := (Nat.mul_one _).symm
    _ ≤ policy.baseMs * 2 ^ (min count 20 - 1) :=
        Nat.mul_le_mul_left _ hpow

-- ── rbDecide correctness theorems ───────────────────────────────────────────

-- When effectiveCount ≥ maxRestarts, decision is giveUp.
theorem decide_giveup_at_limit (policy : RPPolicy) (state : RPState)
    (h : effectiveCount state ≥ policy.maxRestarts) :
    (rbDecide policy state).1 = RPDecision.giveUp := by
  simp only [rbDecide, effectiveCount] at *
  cases hwe : state.windowExpired <;> simp [hwe] at *
  · -- false: ec = state.count, h : state.count ≥ policy.maxRestarts
    simp [if_pos h]
  · -- true: ec = 0, h : 0 ≥ policy.maxRestarts
    simp [if_pos h]

-- When effectiveCount < maxRestarts, decision is restart with some delay.
theorem decide_restart_below_limit (policy : RPPolicy) (state : RPState)
    (h : effectiveCount state < policy.maxRestarts) :
    ∃ delay, (rbDecide policy state).1 = RPDecision.restart delay := by
  simp only [rbDecide, effectiveCount] at *
  cases hwe : state.windowExpired
  · -- false: ec = state.count, h : state.count < policy.maxRestarts
    simp [hwe] at *
    -- goal condition normalises to policy.maxRestarts ≤ state.count
    simp [if_neg (show ¬(policy.maxRestarts ≤ state.count) from by omega)]
  · -- true: ec = 0, h : 0 < policy.maxRestarts → policy.maxRestarts ≠ 0
    simp [hwe] at *
    -- goal condition normalises to policy.maxRestarts = 0
    simp [if_neg (show ¬(policy.maxRestarts = 0) from by omega)]

-- After any decision, the state's windowExpired flag is cleared.
theorem decide_clears_window (policy : RPPolicy) (state : RPState) :
    (rbDecide policy state).2.windowExpired = false := by
  -- Destruct state so Bool values are concrete; `if false/true then ...` reduces by def.
  rcases state with ⟨cnt, we⟩
  simp only [rbDecide]
  cases we
  · -- we = false: if false then 0 else cnt = cnt definitionally
    simp only [Bool.false_eq_true, if_false]
    split <;> rfl
  · -- we = true: if true then 0 else cnt = 0 definitionally
    simp only [if_true]
    split <;> rfl

-- When effectiveCount ≥ maxRestarts, giveUp is idempotent:
-- calling decide again on the returned state also gives giveUp.
theorem decide_giveup_idempotent (policy : RPPolicy) (state : RPState)
    (h : effectiveCount state ≥ policy.maxRestarts) :
    effectiveCount (rbDecide policy state).2 ≥ policy.maxRestarts := by
  -- decide_clears_window tells us .2.windowExpired = false,
  -- so effectiveCount .2 = .2.count.  We prove .2.count ≥ maxRestarts by cases.
  have hcw : (rbDecide policy state).2.windowExpired = false :=
    decide_clears_window policy state
  simp only [effectiveCount, hcw, Bool.false_eq_true, if_false]
  -- goal: (rbDecide policy state).2.count ≥ policy.maxRestarts
  rcases state with ⟨cnt, we⟩
  simp only [effectiveCount] at h
  cases we
  · -- false: effectiveCount = cnt, h : policy.maxRestarts ≤ cnt
    simp only [Bool.false_eq_true, if_false] at h
    simp only [rbDecide, Bool.false_eq_true, if_false]
    rw [if_pos h]
    exact h
  · -- true: effectiveCount = 0, h : policy.maxRestarts ≤ 0
    simp only [if_true] at h
    have h0 : policy.maxRestarts = 0 := Nat.le_zero.mp h
    simp only [rbDecide, if_true]
    rw [if_pos (show 0 ≥ policy.maxRestarts from Nat.le_zero.mpr h0)]
    exact Nat.le_zero.mpr h0

-- The count in the restart branch is effectiveCount + 1.
theorem decide_restart_increments_count (policy : RPPolicy) (state : RPState)
    (h : effectiveCount state < policy.maxRestarts) :
    (rbDecide policy state).2.count = effectiveCount state + 1 := by
  simp only [rbDecide, effectiveCount] at *
  cases hwe : state.windowExpired
  · -- false: ec = state.count
    simp [hwe] at *
    simp [if_neg (show ¬(policy.maxRestarts ≤ state.count) from by omega)]
  · -- true: ec = 0; goal condition normalises to policy.maxRestarts = 0
    simp [hwe] at *
    simp [if_neg (show ¬(policy.maxRestarts = 0) from by omega)]

-- ── Well-formed policy lemma ─────────────────────────────────────────────────

/-- A policy is well-formed when its delays are ordered. -/
def RPPolicy.WellFormed (p : RPPolicy) : Prop :=
  p.baseMs ≤ p.maxMs

-- For a well-formed policy with count > 0, delay is ≤ maxMs.
theorem wf_policy_delay_bounded (policy : RPPolicy) (h : policy.WellFormed)
    (count : Nat) (hc : 0 < count) :
    nextBackoffMs policy count ≤ policy.maxMs :=
  nextBackoffMs_le_max policy count hc

-- ── Sanity checks ────────────────────────────────────────────────────────────

#eval nextBackoffMs { baseMs := 1000, maxMs := 30000, maxRestarts := 5 } 0
-- expected: 1000

#eval nextBackoffMs { baseMs := 1000, maxMs := 30000, maxRestarts := 5 } 1
-- expected: 1000  (2^0 = 1, so 1000 * 1 = 1000)

#eval nextBackoffMs { baseMs := 1000, maxMs := 30000, maxRestarts := 5 } 2
-- expected: 2000  (2^1 = 2, so 1000 * 2 = 2000)

#eval nextBackoffMs { baseMs := 1000, maxMs := 30000, maxRestarts := 5 } 5
-- expected: 16000  (2^4 = 16, so 1000 * 16 = 16000)

#eval rbDecide
  { baseMs := 1000, maxMs := 30000, maxRestarts := 3 }
  { count := 2, windowExpired := false }
-- expected: (restart 2000, { count := 3, windowExpired := false })

#eval rbDecide
  { baseMs := 1000, maxMs := 30000, maxRestarts := 3 }
  { count := 3, windowExpired := false }
-- expected: (giveUp, { count := 3, windowExpired := false })

#eval rbDecide
  { baseMs := 1000, maxMs := 30000, maxRestarts := 3 }
  { count := 3, windowExpired := true }
-- expected: (restart 1000, { count := 1, windowExpired := false })
