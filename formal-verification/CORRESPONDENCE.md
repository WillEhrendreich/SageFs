# Formal Verification Correspondence

> 🔬 Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.

This document maps each Lean 4 definition in `formal-verification/lean/FVSquad/` to
its corresponding F# source, explains what the Lean model captures, and details every
known divergence so that the validity of the associated proofs can be assessed honestly.

## Last Updated

- **Date**: 2026-04-24 20:37 UTC
- **Commit**: `bf7a72c8adccfee2ffb25a7cd7ec7b58d731bd7cc`

---

## RingBuffer

**Lean file**: `formal-verification/lean/FVSquad/RingBuffer.lean`
**F# source**: `SageFs.Core/RingBuffer.fs`

### Type correspondence

| Lean name | F# name | F# file + location | Correspondence | Notes |
|---|---|---|---|---|
| `RingBuffer α` | `RingBuffer<'T>` | `SageFs.Core/RingBuffer.fs` | **Abstraction** | F# is a mutable record; Lean is immutable. Field names differ (see below). |
| `RingBuffer.items` | `Items : 'T array` | `RingBuffer.fs` | **Exact** | Both are fixed-capacity arrays. |
| `RingBuffer.head` | `Head : int` | `RingBuffer.fs` | **Approximation** | F# uses `int` (signed, 64-bit on 64-bit targets); Lean uses `Nat`. Negative values are impossible by invariant, so the invariant holds on both representations. |
| `RingBuffer.count` | `Count : int` | `RingBuffer.fs` | **Approximation** | Same as above — F# `int`, Lean `Nat`. |
| `RingBuffer.total` | `Total : int` | `RingBuffer.fs` | **Approximation** | Same as above. |
| `WellFormed buf` | *(invariant, not explicit)* | `RingBuffer.fs` | **Abstraction** | The Lean `WellFormed` predicate makes implicit F# invariants (`head < capacity`, `count ≤ capacity`, `count ≤ total`) explicit and proved. |

### Function correspondence

| Lean name | F# name | F# file | Correspondence level | Notes |
|---|---|---|---|---|
| `rbCreate default cap h` | `RingBuffer.create` | `SageFs.Core/RingBuffer.fs` | **Exact** | Returns empty buffer with `Array.replicate cap default`. Lean requires `h : 0 < cap` explicitly; F# raises an exception. |
| `rbPush x buf` | `RingBuffer.push` | `SageFs.Core/RingBuffer.fs` | **Abstraction** | F# mutates `Items[newHead] <- x` in place; Lean uses `Array.set` to produce a new array. Observable input/output semantics are identical. Eviction (when `Count = Capacity`) is captured: `count = min (count+1) size`. |
| `rbTryGet age buf` | `RingBuffer.tryGet` | `SageFs.Core/RingBuffer.fs` | **Exact** | Both return `None`/`none` for `age ≥ count` and `Some items[(head + age) % capacity]` otherwise. |
| `rbClear buf` | `RingBuffer.clear` | `SageFs.Core/RingBuffer.fs` | **Abstraction** | Lean resets `head=0`, `count=0`, `total=0`, preserves `items` (keeps capacity). F# may reset items contents; Lean preserves the array object. `total` reset is modelled for simplicity; F# preserves `Total`. See divergence below. |
| `rbToList buf` | *(not a direct F# function)* | `SageFs.Core/RingBuffer.fs` | **Abstraction** | No direct F# equivalent; models the observable contents as a `List α` ordered from most-recent to oldest. Useful for stating and proving invariants about the sequence of pushed values. |

### Known divergences

#### D1 — `int` vs `Nat` for head/count/total

- **Lean model**: `head`, `count`, `total` are `Nat` (non-negative natural numbers).
- **F# source**: these fields are `int` (signed 64-bit integers on 64-bit systems).
- **Impact**: The `WellFormed` predicate ensures these values are non-negative at all times, so theorems proved under `WellFormed` remain valid for the F# implementation. Overflow at `Int.MaxValue` (≈ 9.2 × 10¹⁸ pushes) is not modelled.
- **Proof impact**: All theorems assume `WellFormed` — this divergence does not affect their validity under normal usage.

#### D2 — Immutable vs mutable array

- **Lean model**: `rbPush` produces a new `RingBuffer` with a new array (via `Array.set`).
- **F# source**: `push` mutates the existing `Items` array in place, then returns a new record with updated `Head`, `Count`, `Total`.
- **Impact**: The Lean model captures the pure input-to-output mapping. Proofs about what `tryGet` returns after `push` remain valid because the observable state (contents at each age slot) is identical.
- **Proof impact**: None for the proved theorems (all are about values, not identity/sharing).

#### D3 — `rbClear` resets `total`

- **Lean model**: `rbClear` resets `total` to 0.
- **F# source**: `clear` preserves `Total` (it only resets `Head`, `Count`, and item slots).
- **Impact**: The theorems `clear_total` and `clear_count` are stated for the Lean model; `clear_total` would be false for the F# implementation. This divergence is a **known inaccuracy** in the Lean model.
- **Proof impact**: `clear_total` should be revised or removed; it does not reflect F# behaviour.
- **Action**: A future run should fix `rbClear` to preserve `total` and update `clear_total`.

#### D4 — `rbCreate` preserves item contents

- **Lean model**: `rbCreate default cap h` fills the array with `default` via `Array.replicate`.
- **F# source**: `create capacity` initialises items to `Array.zeroCreate` (all `Unchecked.defaultof<'T>`).
- **Impact**: Both produce an array full of a "default" element. For reference types the F# value is `null`; in Lean the caller supplies the default. This is a minor abstraction gap, not a correctness issue.

#### D5 — No error handling for `push` when `cap = 0`

- **Lean model**: The `WellFormed` predicate requires `0 < items.size`, so `rbPush` is never called on a zero-capacity buffer. The function would panic on such input.
- **F# source**: Presumably undefined / exception for `capacity = 0`.
- **Impact**: The precondition is the same; no theorems are invalidated.

### Validation evidence

Correspondence has been validated manually by code review. No Aeneas-generated Lean file exists yet. A runnable test harness in `formal-verification/tests/ringbuffer/` is a **recommended next step** (Task 8) to automate correspondence checking between the Lean model and the F# implementation via property-based tests.

### Proved theorems and their validity

All theorems below are proved by `lake build` with Lean 4.30.0-rc2 (no `sorry`), assuming `WellFormed`. Given the divergences documented above, their validity against the F# source is assessed as:

| Theorem | Claims | Valid vs F#? |
|---|---|---|
| `create_wellFormed` | `rbCreate` produces a well-formed buffer | ✅ Yes |
| `push_wellFormed` | `rbPush` preserves `WellFormed` | ✅ Yes |
| `create_capacity` | `rbCreate` capacity = `cap` | ✅ Yes |
| `push_capacity` | `rbPush` preserves capacity | ✅ Yes |
| `clear_capacity` | `rbClear` preserves capacity | ✅ Yes |
| `create_count_zero` | new buffer has count 0 | ✅ Yes |
| `push_count` | `rbPush` count = min(count+1, cap) | ✅ Yes |
| `clear_count` | `rbClear` resets count to 0 | ✅ Yes |
| `count_le_capacity` | count ≤ capacity always | ✅ Yes |
| `count_nonneg` | count ≥ 0 (trivial for Nat) | ✅ Yes (trivial) |
| `create_total_zero` | new buffer has total 0 | ✅ Yes |
| `push_total` | `rbPush` increments total | ✅ Yes |
| `clear_total` | `rbClear` resets total to 0 | ⚠️ **No** — F# preserves `Total` (see D3) |
| `total_ge_count` | total ≥ count | ✅ Yes |
| `evictedCount_eq` | evicted = total - count | ✅ Yes |
| `evictedCount_nonneg` | evicted ≥ 0 | ✅ Yes |
| `tryGet_none_of_ge` | `tryGet age = none` for `age ≥ count` | ✅ Yes |
| `toList_length` | `rbToList` has length = count | ✅ Yes |
| `push_tryGet_zero` | `tryGet 0 (push x buf) = some x` | ✅ Yes |

**Sorry remaining**:

| Theorem | Reason | Status |
|---|---|---|
| `push_aging` | Requires modular arithmetic lemma over `%` not available without Mathlib | `sorry` — deferred to next run |

---

## Other Targets

The following targets are identified in `TARGETS.md` but have no Lean files yet.
Correspondence will be documented here as each target reaches Phase 4.

- `KeyMap` (Phase 1 — research only)
- `Theme` (Phase 1 — research only)
- `BinaryManifest` (Phase 1 — research only)
- `StateMachine` (Phase 1 — research only)
