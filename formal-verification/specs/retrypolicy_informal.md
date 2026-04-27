# RetryPolicy — Informal Specification

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Target

**F# source**: `SageFs.Core/RetryPolicy.fs`
**Key functions**: `backoffMs`, `shouldRetry`, `decide`

---

## Purpose

`RetryPolicy` encapsulates the decision logic for retrying failed operations.
Given a retry configuration, an attempt number, and an exception, it decides
whether to:
- **GiveUp** — stop retrying (exception is non-retryable, or attempts exhausted)
- **RetryAfter delayMs** — retry after a computed back-off delay
- **Success** — never returned by `decide` directly (used elsewhere in the system)

The back-off formula is **linear with jitter**:
```
baseDelay(attempt) = config.BaseDelayMs * (attempt + 1)
actualDelay = baseDelay ± 50%   (using System.Random.Shared)
```

---

## Types

```fsharp
type RetryConfig = {
  MaxRetries: int        -- maximum number of retry attempts (0 = no retries)
  BaseDelayMs: int<ms>   -- base delay in milliseconds
}

type RetryOutcome =
  | Success
  | RetryAfter of delayMs: int<ms>
  | GiveUp of exn
```

---

## Preconditions

- `config.MaxRetries ≥ 0` (natural number; F# uses `int` but semantically non-negative)
- `config.BaseDelayMs ≥ 0` (zero is valid; means no delay)
- `attempt ≥ 0` (attempt index; 0 = first attempt)

---

## Postconditions

### `shouldRetry`

```
shouldRetry config attempt = (attempt < config.MaxRetries)
```

- Returns `true` if and only if `attempt < config.MaxRetries`
- Pure boolean predicate; no side effects

### `backoffMs`

```
backoffMs config attempt ∈ [ baseDelay/2 .. baseDelay * 3/2 )
  where baseDelay = config.BaseDelayMs * (attempt + 1)
```

- The base delay is `config.BaseDelayMs * (attempt + 1)` — linear in attempt
- With jitter (±50%), actual delay is in range `[baseDelay/2, 3*baseDelay/2)`
- When `baseDelay = 0` (jitterRange = 0), the result is exactly `0`
- The delay is **non-negative** for all valid inputs
- The delay is **monotonically non-decreasing** in `attempt` (in expectation, without jitter)

### `decide`

```
decide isRetryable config attempt ex =
  | GiveUp _   if  isRetryable ex = false
  | GiveUp _   if  isRetryable ex = true  ∧  attempt ≥ config.MaxRetries
  | RetryAfter if  isRetryable ex = true  ∧  attempt < config.MaxRetries
```

- Non-retryable exception → **always** GiveUp (regardless of attempt count)
- Retryable exception but exhausted → GiveUp
- Retryable exception with attempts remaining → RetryAfter with a back-off delay

---

## Invariants

1. **Decisiveness**: `decide` always returns a value (total function)
2. **Non-retryable implies GiveUp**: `¬isRetryable ex → decide _ config _ ex = GiveUp ex`
3. **Exhaustion implies GiveUp**: `attempt ≥ config.MaxRetries → decide isRetryable config attempt ex` may still be `RetryAfter` if `isRetryable ex = false`, but when retryable, `attempt ≥ maxRetries → GiveUp`
4. **Retry delay grows with attempt**: `baseDelay(n) = baseDelayMs * (n+1)` is strictly increasing when `baseDelayMs > 0`
5. **Zero maxRetries → immediate GiveUp for retryable**: when `config.MaxRetries = 0`, all retryable exceptions give up immediately

---

## Edge Cases

| Scenario | Expected behaviour |
|----------|--------------------|
| `maxRetries = 0`, retryable | GiveUp immediately |
| `attempt = 0`, retryable | RetryAfter baseDelayMs (first attempt) |
| `baseDelayMs = 0` | RetryAfter 0 (no delay) |
| non-retryable, any attempt | GiveUp |
| `attempt = maxRetries - 1`, retryable | RetryAfter (last retry) |
| `attempt = maxRetries`, retryable | GiveUp (exhausted) |

---

## Inferred Intent

The design intent is a simple retry gate that:
- Delegates the "is this worth retrying?" question to a caller-supplied predicate
- Implements linear back-off (not exponential) to avoid thundering herds
- Adds jitter to spread retries across time when multiple callers use the same config
- Provides a hard stop after `MaxRetries` attempts

The jitter is non-deterministic, which means `decide` is not a pure function in F# —
but the *deterministic core* (the base-delay formula and the GiveUp/RetryAfter
branching logic) is pure and amenable to formal verification.

---

## Open Questions

1. **Negative MaxRetries**: The F# type is `int`, not `uint`. What should happen if
   `MaxRetries < 0`? Currently `shouldRetry` with `attempt = 0` would return `false`
   immediately. Is this intentional?
2. **Attempt overflow**: `attempt` is `int` in F#. What happens when `attempt`
   reaches `Int32.MaxValue`? The `backoffMs` formula `BaseDelayMs * (attempt + 1)`
   could overflow. Is there a cap?
3. **Success outcome**: `RetryOutcome.Success` is defined but never returned by
   `decide`. Is it used elsewhere, or is it a placeholder?

---

## Examples

| `isRetryable` | `attempt` | `maxRetries` | `baseDelayMs` | result |
|:---:|:---:|:---:|:---:|:---|
| false | any | any | any | GiveUp |
| true | 0 | 3 | 50 | RetryAfter ~50ms (±50% jitter) |
| true | 2 | 3 | 50 | RetryAfter ~150ms (±50% jitter) |
| true | 3 | 3 | 50 | GiveUp |
| true | 0 | 0 | 50 | GiveUp |

---

## Lean Modelling Notes

- `exn` → `Bool` (isRetryable predicate already applied)
- Jitter abstracted away; Lean model uses deterministic `baseDelay = baseDelayMs * (attempt+1)`
- `int` → `Nat` (non-negative assumption made explicit)
- `GiveUp of exn` → `RTOutcome.giveUp` (exception payload dropped)
- `Success` included as `RTOutcome.success` for completeness but not returned by `rtDecide`
