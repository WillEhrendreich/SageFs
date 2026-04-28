/-
  RetryPolicy.lean
  Formal specification for SageFs.Core.RetryPolicy.

  The F# source implements linear backoff with jitter using System.Random.Shared.Next.
  This model abstracts away the jitter, capturing the deterministic core:
    baseDelay(config, attempt) = config.baseDelayMs * (attempt + 1)

  Key abstractions:
  - `exn`            → `Bool` (isRetryable predicate already applied to exception)
  - Jitter (±50%)    → omitted; proofs apply to the deterministic base-delay formula
  - `int`            → `Nat`  (non-negative assumption made explicit)
  - `GiveUp of exn`  → `RTOutcome.giveUp` (exception payload dropped)

  No Mathlib.  Pure Lean 4 stdlib only (network firewalled on CI).
-/

-- ── Type definitions ────────────────────────────────────────────────────────

/-- Retry configuration (mirrors F# RetryConfig record). -/
structure RTConfig where
  maxRetries  : Nat  -- maximum number of retry attempts (0 = no retries allowed)
  baseDelayMs : Nat  -- base delay in milliseconds
  deriving Repr

/--
  Outcome of a retry decision (mirrors F# RetryOutcome).
  Note: `success` is included for completeness but is never returned by `rtDecide`.
  `giveUp` abstracts away the `exn` payload.
-/
inductive RTOutcome
  | success
  | retryAfter (delayMs : Nat)
  | giveUp
  deriving Repr, DecidableEq

-- ── Pure implementation model ────────────────────────────────────────────────

/--
  Deterministic base-delay formula (jitter abstracted away).

  F# semantics: `config.BaseDelayMs * (attempt + 1)`
  The jitter (±50% via System.Random.Shared) is omitted here; this model
  captures the monotone, linear growth property of the base delay.
-/
def baseDelay (config : RTConfig) (attempt : Nat) : Nat :=
  config.baseDelayMs * (attempt + 1)

/-- Whether more retries remain for the given attempt index. -/
def shouldRetry (config : RTConfig) (attempt : Nat) : Bool :=
  attempt < config.maxRetries

/--
  Pure retry decision.

  `isRetryable` is a Bool representing the caller-supplied predicate applied to
  the exception.  When false, gives up immediately regardless of attempt count.
  When true and attempts remain, retries with the deterministic base delay.
  When true but all attempts are exhausted, gives up.
-/
def rtDecide (config : RTConfig) (attempt : Nat) (isRetryable : Bool) : RTOutcome :=
  if !isRetryable then
    RTOutcome.giveUp
  else if shouldRetry config attempt then
    RTOutcome.retryAfter (baseDelay config attempt)
  else
    RTOutcome.giveUp

-- ── Theorems ─────────────────────────────────────────────────────────────────

-- Non-retryable exceptions always give up.
theorem rtDecide_giveup_non_retryable (config : RTConfig) (attempt : Nat) :
    rtDecide config attempt false = RTOutcome.giveUp := by
  simp [rtDecide]

-- When retryable and attempts remain, the decision is retryAfter.
theorem rtDecide_retry_when_available (config : RTConfig) (attempt : Nat)
    (h : attempt < config.maxRetries) :
    rtDecide config attempt true = RTOutcome.retryAfter (baseDelay config attempt) := by
  simp [rtDecide, shouldRetry, h]

-- When retryable but all attempts exhausted, the decision is giveUp.
theorem rtDecide_giveup_exhausted (config : RTConfig) (attempt : Nat)
    (h : config.maxRetries ≤ attempt) :
    rtDecide config attempt true = RTOutcome.giveUp := by
  simp [rtDecide, shouldRetry]
  omega

-- The base delay at attempt 0 equals baseDelayMs.
theorem baseDelay_zero (config : RTConfig) :
    baseDelay config 0 = config.baseDelayMs := by
  simp [baseDelay]

-- The base delay equals baseDelayMs * (attempt + 1).
theorem baseDelay_formula (config : RTConfig) (attempt : Nat) :
    baseDelay config attempt = config.baseDelayMs * (attempt + 1) := by
  simp [baseDelay]

-- The base delay is monotonically non-decreasing in attempt.
theorem baseDelay_mono (config : RTConfig) (a b : Nat) (h : a ≤ b) :
    baseDelay config a ≤ baseDelay config b := by
  simp [baseDelay]
  apply Nat.mul_le_mul_left
  omega

-- When baseDelayMs > 0, the delay is always positive.
theorem baseDelay_pos (config : RTConfig) (attempt : Nat)
    (h : 0 < config.baseDelayMs) :
    0 < baseDelay config attempt := by
  simp only [baseDelay]
  -- simp reduces the goal to 0 < config.baseDelayMs after recognising attempt+1 > 0
  -- But the full goal is 0 < config.baseDelayMs * (attempt + 1).
  exact Nat.mul_pos h (Nat.succ_pos attempt)

-- When maxRetries = 0, every retryable exception gives up immediately.
theorem rtDecide_zero_retries (config : RTConfig) (attempt : Nat)
    (hmax : config.maxRetries = 0) :
    rtDecide config attempt true = RTOutcome.giveUp := by
  apply rtDecide_giveup_exhausted
  omega

-- shouldRetry is false when attempt ≥ maxRetries.
theorem shouldRetry_false_of_ge (config : RTConfig) (attempt : Nat)
    (h : config.maxRetries ≤ attempt) :
    shouldRetry config attempt = false := by
  simp [shouldRetry]
  omega

-- shouldRetry is true when attempt < maxRetries.
theorem shouldRetry_true_of_lt (config : RTConfig) (attempt : Nat)
    (h : attempt < config.maxRetries) :
    shouldRetry config attempt = true := by
  simp [shouldRetry, h]

-- shouldRetry is monotonically decreasing: if (attempt+1) is retryable, so is attempt.
theorem shouldRetry_pred (config : RTConfig) (attempt : Nat)
    (h : shouldRetry config (attempt + 1) = true) :
    shouldRetry config attempt = true := by
  simp [shouldRetry] at *
  omega

-- When retryable and attempts remain, the delay equals baseDelayMs * (attempt + 1).
theorem rtDecide_delay_formula (config : RTConfig) (attempt : Nat)
    (h : attempt < config.maxRetries) :
    rtDecide config attempt true =
      RTOutcome.retryAfter (config.baseDelayMs * (attempt + 1)) := by
  rw [rtDecide_retry_when_available config attempt h]
  simp [baseDelay]

-- The first retry (attempt=0) has delay equal to baseDelayMs.
theorem rtDecide_first_retry_delay (config : RTConfig)
    (h : 0 < config.maxRetries) :
    rtDecide config 0 true = RTOutcome.retryAfter config.baseDelayMs := by
  rw [rtDecide_retry_when_available config 0 h]
  simp [baseDelay]

-- ── Sanity checks ────────────────────────────────────────────────────────────

#eval rtDecide { maxRetries := 3, baseDelayMs := 50 } 0 true
-- expected: retryAfter 50

#eval rtDecide { maxRetries := 3, baseDelayMs := 50 } 2 true
-- expected: retryAfter 150

#eval rtDecide { maxRetries := 3, baseDelayMs := 50 } 3 true
-- expected: giveUp

#eval rtDecide { maxRetries := 3, baseDelayMs := 50 } 0 false
-- expected: giveUp

#eval rtDecide { maxRetries := 0, baseDelayMs := 50 } 0 true
-- expected: giveUp
