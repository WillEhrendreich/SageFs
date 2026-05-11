# Informal Specification: TimeTravel

**Source**: `SageFs.Core/TimeTravel.fs`
**Target**: `TimeTravel.TimeTravelState` and its navigation operations
**Phase**: 2 → 3

---

## Purpose

`TimeTravel` wraps a `SnapshotRing` (a ring-buffer of model snapshots) with a
navigation cursor (`TimeTravelMode`) so the user can step backwards and forwards
through the history of model states for debugging.  While in `Viewing` mode the
ring is frozen — new `record` calls are silently ignored.  `Live` mode is the
normal operating state.

---

## Types

### `TimeTravelMode`
A discriminated union with two cases:
- `Live` — the system is tracking the present state.
- `Viewing of age: int` — the user has navigated `age` steps into the past
  (age ≥ 1).

### `TimeTravelState<'Model>`
A product type containing:
- `Ring` — a `SnapshotRing<'Model>` (a ring-buffer of snapshots).
- `Mode` — the current `TimeTravelMode`.

---

## Preconditions

- A `TimeTravelState` created by `create` starts in `Live` mode with an empty ring.
- `Viewing age` should always carry `age ≥ 1`; `age = 0` is the "present" and is
  represented by `Live`, not `Viewing 0`.

---

## Postconditions / Per-Operation Specs

### `create config`
- Returns a `TimeTravelState` with `Mode = Live` and an empty ring.

### `isLive state`
- Returns `true` iff `state.Mode = Live`.

### `viewingAge state`
- Returns `None` iff `state.Mode = Live`.
- Returns `Some age` iff `state.Mode = Viewing age`.

### `record msgLabel updateMs model state`
- **Live mode**: records the snapshot into the ring and returns a new state with
  the ring updated; mode remains `Live`.
- **Viewing mode**: returns `state` unchanged (the ring is frozen while browsing).

### `stepBack state`
- **Live, ring has ≤ 1 snapshots**: no-op (cannot step back, not enough history).
- **Live, ring has > 1 snapshots**: transitions to `Viewing 1`.
- **Viewing age, age + 1 < count**: increments age → `Viewing (age + 1)`.
- **Viewing age, age + 1 ≥ count**: no-op (already at oldest available snapshot).

### `stepForward state`
- **Live**: no-op (already at the present).
- **Viewing 1**: transitions back to `Live`.
- **Viewing age (age > 1)**: decrements age → `Viewing (age - 1)`.

### `goLive state`
- Always returns a state with `Mode = Live`; ring is unchanged.

### `currentModel state`
- **Live**: returns `navigateTo 0 ring` (most recent snapshot), which may be
  `None` if the ring is empty.
- **Viewing age**: returns `navigateTo age ring`.

### `formatStatus state`
- **Live, empty ring**: returns `None`.
- **Live, non-empty**: returns `Some "⏱ N snapshots"` where N = count.
- **Viewing age**: returns `Some "⏮ -age (Alt+→ fwd | Alt+Home live)"`.

### `setEnabled enabled state`
- Delegates to `ModelSnapshot.setEnabled`; does not change `Mode`.

### `recentLabels maxCount state`
- Delegates to `ModelSnapshot.recentLabels`; does not change state.

---

## Invariants

1. **Age ≥ 1 in Viewing mode**: `Viewing age` implies `age ≥ 1`.
2. **Viewing age ≤ count − 1**: to be in `Viewing age`, we require `age < count`
   (the snapshot at that age must exist).  Note: this is a _logical_ invariant —
   it holds when `stepBack` is used correctly, but `TimeTravelState` itself does
   not enforce it structurally.
3. **Ring frozen in Viewing mode**: `record` is a no-op when `Mode = Viewing _`.
4. **Round-trip identity (forward after back)**:
   - Starting from `Live` with count ≥ 2: `stepForward (stepBack s) = s`.
   - Starting from `Viewing age` with age + 1 < count: `stepForward (stepBack s) = s`.
5. **`goLive` is idempotent**: `goLive (goLive s) = goLive s`.
6. **`goLive ∘ stepBack ∘ stepBack ≠ id`** in general (we can travel back multiple
   steps and `goLive` snaps us directly to `Live` without unwinding).

---

## Edge Cases

- Empty ring with `stepBack`: no-op (count = 0 ≤ 1).
- Single snapshot with `stepBack`: no-op (count = 1 ≤ 1).
- `stepForward` when already `Live`: no-op.
- `record` when `Viewing`: silently discarded — history does not grow while browsing.
- `currentModel` on empty ring: returns `None`.

---

## Examples

```
let s0 = create defaultConfig          -- Live, 0 snapshots
let s1 = record "msg1" 0.1 m1 s0      -- Live, 1 snapshot
let s2 = record "msg2" 0.2 m2 s1      -- Live, 2 snapshots
let s3 = stepBack s2                   -- Viewing 1, 2 snapshots
let s4 = stepBack s3                   -- Viewing 1 (count=2, age+1=2 = count → no-op)
let s5 = stepForward s3                -- Live, 2 snapshots  (round-trip ✓)
let s6 = goLive s3                     -- Live, 2 snapshots
let s7 = record "msg3" 0.3 m3 s3      -- Viewing 1, unchanged (frozen)
```

---

## Open Questions

- Should `Viewing 0` be considered equivalent to `Live`?  The code suggests
  `navigateTo 0` always means "most recent", so if we allow `Viewing 0` it would
  show the same model as `Live`.  Current code prevents `Viewing 0` from being
  created by navigation, but there is no structural check.
- The `stepBack` guard is `age + 1 < count` (strict), meaning we can never be
  `Viewing (count - 1)` — the oldest snapshot is always one step out of reach when
  count > 0.  Is this intentional?  (Actually re-reading: from `Live` we go to
  `Viewing 1`, and then from `Viewing k` we go to `Viewing (k+1)` if `k+1 < count`.
  So with count = 3, max age = 2 = count - 1.  That means from `Live` with count = 3
  we can reach `Viewing 1` then `Viewing 2`, and `2 + 1 = 3 = count` so no further.
  The oldest reachable age is `count - 1`, which IS accessible.  So the invariant
  is age ≤ count - 1.)
