# Informal Specification: RestartPolicy

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
> Source: `SageFs.Core/RestartPolicy.fs`

## Purpose

`RestartPolicy` is a pure, deterministic Erlang-style supervision strategy. Given a
policy configuration and a per-session restart state, it decides whether to restart a
failed worker (with an exponential backoff delay) or to give up permanently. A reset
window prevents permanent give-up after well-spaced transient failures.

## Types

### `Policy`
| Field | Type | Meaning |
|-------|------|---------|
| `MaxRestarts` | `int` | Maximum restarts before giving up |
| `BackoffBase` | `TimeSpan` | Base delay for exponential backoff |
| `BackoffMax` | `TimeSpan` | Maximum delay cap |
| `ResetWindow` | `TimeSpan` | Window after which the restart count resets |

### `State`
| Field | Type | Meaning |
|-------|------|---------|
| `RestartCount` | `int` | Restarts in the current window |
| `LastRestartAt` | `DateTime option` | When the last restart occurred |
| `WindowStart` | `DateTime option` | Start of the current counting window |

### `Decision`
- `Restart of delay: TimeSpan` — restart with the given backoff delay
- `GiveUp of SageFsError` — permanently stop restarting

## Functions

### `nextBackoff`
```fsharp
nextBackoff : Policy → int → TimeSpan
```
Computes the backoff delay for a given restart count:
- `count ≤ 0` → `BackoffBase`
- `count ≥ 1` → `min(BackoffBase * 2^(min(count,20) - 1), BackoffMax)`

**Key properties**:
1. Result is always ≥ `BackoffBase` (when `BackoffBase ≤ BackoffMax`)
2. Result is always ≤ `BackoffMax` (for `count ≥ 1`)
3. For `count = 0`: result = `BackoffBase`
4. Monotonically non-decreasing in `count` (capped at `BackoffMax`)

### `decide`
```fsharp
decide : Policy → State → DateTime → Decision × State
```
Pure decision function. Rules applied in order:
1. **Window reset**: if `now - state.WindowStart > policy.ResetWindow`, the effective
   count is reset to 0 (transient failures spaced out by the reset window are forgiven).
2. **Give up**: if `effectiveCount ≥ policy.MaxRestarts` → `GiveUp` (count preserved).
3. **Restart**: otherwise → `Restart(nextBackoff policy (effectiveCount + 1))`,
   incrementing the count and preserving / initialising `WindowStart`.

**Key properties**:
1. `effectiveCount ≥ maxRestarts` → first component is `GiveUp`
2. `effectiveCount < maxRestarts` → first component is `Restart _`
3. After `Restart`, new state count = `effectiveCount + 1`
4. After `GiveUp`, new state count = `effectiveCount` (unchanged)
5. New count after `Restart` is `≤ maxRestarts`
6. `maxRestarts = 0` → always `GiveUp`
7. `GiveUp` state is idempotent: calling `decide` on a GiveUp state gives GiveUp
8. Window expiry resets the effective count to 0 before applying rules

## Preconditions

- `policy.MaxRestarts ≥ 0` (always satisfied for `int`)
- `BackoffBase ≤ BackoffMax` for backoff monotonicity to hold
- `state.RestartCount ≥ 0` (always satisfied for `int`)

## Postconditions

- The returned state count is always `≥ 0`
- After `Restart`: returned state count = prior effective count + 1
- After `GiveUp`: returned state count = prior effective count

## Invariants

- `decide` is a pure function: same inputs → same outputs
- `decide` never increases count beyond `maxRestarts`

## Edge Cases

- `maxRestarts = 0`: always gives up immediately
- `RestartCount = 0`, no window: first restart always succeeds
- Window expired with `count = maxRestarts`: resets to 0, then restarts (count becomes 1)
- Very large `count ≥ 20`: capped at `2^19 * base` before `BackoffMax` cap

## Lean Model Abstractions

The Lean model uses:
- `Nat` instead of `int` (non-negative values only — matching invariants)
- `Bool windowExpired` instead of `DateTime` comparison (abstracts away time arithmetic)
- `Unit` instead of `SageFsError` (we don't model the error payload)
- Integer ms instead of `TimeSpan` (capturing pure numeric backoff semantics)

## Open Questions

1. Should `nextBackoff count=0` and `count=1` both return `BackoffBase`? The current
   F# code returns `base * 2^0 = base` for count=1, same as count=0. Is this intentional?
2. Is `BackoffBase ≤ BackoffMax` a documented precondition? The default policy has
   base=1s, max=30s (well-formed), but no explicit check enforces this.
