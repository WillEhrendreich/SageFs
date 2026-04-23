# Informal Specification: `RingBuffer`

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

**Source file**: `SageFs.Core/RingBuffer.fs`
**Module**: `SageFs.RingBuffer`
**Phase**: 2 — Informal spec

---

## Purpose

`RingBuffer<'T>` is a fixed-capacity circular buffer designed for time-travel model
snapshots. It holds at most `capacity` items. When full, pushing a new item evicts the
oldest one. Items are accessed by **age**: age 0 = most recent, age 1 = one step back,
and so on.

The module provides a purely functional interface: every mutating operation returns a
new `RingBuffer<'T>` record. The underlying array is mutated in place for performance
(structural sharing of F# records), but the observable semantics are fully functional.

---

## Type Definition

```fsharp
type RingBuffer<'T> = {
  Items: 'T array      // Physical storage, length = capacity
  Head: int            // Index of the most-recently-pushed slot
  Count: int           // Number of valid items currently in the buffer
  TotalPushed: int64   // Total items ever pushed (including evicted)
}
```

### Internal representation invariants

These hold for every well-formed `RingBuffer`:
- `0 < Items.Length` (capacity is always positive — enforced by `create`)
- `0 ≤ Head < Items.Length`
- `0 ≤ Count ≤ Items.Length`
- `TotalPushed ≥ 0`
- `TotalPushed ≥ Count` (pushed at least as many as are stored)
- `TotalPushed - Count ≥ 0` — the eviction count is non-negative

---

## Operations

### `create (capacity: int) : RingBuffer<'T>`

**Precondition**: `capacity > 0`

**Postcondition**:
- Returns a ring buffer with `Count = 0`, `Head = 0`, `TotalPushed = 0`
- `Items.Length = capacity`
- Raises `ArgumentException "Ring buffer capacity must be positive"` if
  `capacity ≤ 0`

---

### `push (item: 'T) (buf: RingBuffer<'T>) : RingBuffer<'T>`

**Precondition**: `buf` is well-formed

**Postcondition** (let `cap = capacity buf`, `n = count buf`):
- Returns a new buffer with `capacity = cap` (unchanged)
- `totalPushed result = buf.TotalPushed + 1`
- `count result = min (n + 1) cap`
- The pushed item is retrievable as `tryGet 0 result = Some item`
- If `n > 0` then `tryGet k result = tryGet (k-1) buf` for all `1 ≤ k < count result`
  (i.e., previously-stored items age by one)
- The oldest item is evicted if the buffer was full (`n = cap`)

**Index arithmetic**: `newHead = (buf.Head + cap - 1) % cap`

---

### `tryGet (age: int) (buf: RingBuffer<'T>) : 'T option`

**Precondition**: none (safe for any `age`)

**Postcondition**:
- Returns `None` if `age < 0` or `age ≥ buf.Count`
- Returns `Some buf.Items[(buf.Head + age) % cap]` otherwise

**Examples**:
- `tryGet 0 buf` = most recently pushed item, or `None` if empty
- `tryGet (-1) buf` = `None`
- `tryGet (count buf) buf` = `None`

---

### `current (buf: RingBuffer<'T>) : 'T option`
Equivalent to `tryGet 0 buf`.

### `previous (buf: RingBuffer<'T>) : 'T option`
Equivalent to `tryGet 1 buf`.

---

### `count (buf: RingBuffer<'T>) : int`
Returns `buf.Count`. Always in `[0, capacity buf]`.

### `capacity (buf: RingBuffer<'T>) : int`
Returns `buf.Items.Length`. Always positive.

### `isEmpty (buf: RingBuffer<'T>) : bool`
Returns `buf.Count = 0`. Equivalent to `count buf = 0`.

### `isFull (buf: RingBuffer<'T>) : bool`
Returns `buf.Count = buf.Items.Length`. Equivalent to `count buf = capacity buf`.

### `totalPushed (buf: RingBuffer<'T>) : int64`
Total pushes ever. Monotonically non-decreasing.

### `evictedCount (buf: RingBuffer<'T>) : int64`
Returns `buf.TotalPushed - buf.Count`. Always non-negative.

---

### `toSeq / toList (buf: RingBuffer<'T>) : 'T seq / 'T list`

**Postcondition**:
- Yields items in **most-recent-first** order: `[item_0, item_1, ..., item_{count-1}]`
  where `item_k = (tryGet k buf).Value`
- `(toList buf).Length = count buf`
- For `0 ≤ k < count buf`: `(toList buf).[k] = (tryGet k buf).Value`

---

### `clear (buf: RingBuffer<'T>) : RingBuffer<'T>`

**Postcondition**:
- `count result = 0`
- `capacity result = capacity buf` (preserved)
- `totalPushed result = buf.TotalPushed` (preserved — history of how many were ever pushed)
- `isEmpty result = true`

---

## Invariants (Summary)

These hold at all times for a ring buffer produced by the public API:

| # | Invariant | Expression |
|---|-----------|-----------|
| I1 | Non-negative count | `0 ≤ count buf` |
| I2 | Count bounded by capacity | `count buf ≤ capacity buf` |
| I3 | TotalPushed ≥ count | `totalPushed buf ≥ int64 (count buf)` |
| I4 | Eviction accounting | `evictedCount buf = totalPushed buf - int64 (count buf)` |
| I5 | List length matches count | `(toList buf).Length = count buf` |
| I6 | Most recent accessible | `push x buf \|> tryGet 0 = Some x` |
| I7 | TotalPushed monotone | After push: `totalPushed new = totalPushed old + 1` |
| I8 | Capacity preserved by push/clear | `capacity (push x buf) = capacity buf` |

---

## Edge Cases

- **Capacity 1**: The buffer holds exactly one item at a time. Every push evicts the previous item.
- **Empty buffer**: `tryGet 0`, `current`, `previous` all return `None`.
- **Exact capacity fill**: After exactly `capacity` pushes, `count = capacity`, `isFull = true`, `evictedCount = 0`.
- **Wrap-around**: After more than `capacity` pushes, the `Head` index wraps around. The index arithmetic `(Head + age) % capacity` must correctly identify the right slot.
- **`totalPushed` overflow**: For very long-running sessions, `TotalPushed` is `int64` so overflow only occurs after 2^63 pushes — negligible in practice.

---

## Concrete Examples

```fsharp
// Create a buffer of capacity 3, push 5 items
let buf = create 3 |> pushMany [1; 2; 3; 4; 5]
toList buf     = [5; 4; 3]     // Most recent 3
count buf      = 3
totalPushed buf = 5L
evictedCount buf = 2L

// Capacity 1
let b1 = create 1 |> push 10 |> push 20
current b1     = Some 20
previous b1    = None
count b1       = 1

// Empty
let empty = create 5
current empty  = None
count empty    = 0
```

---

## Inferred Intent

The ring buffer is designed for **time-travel debugging**: editors hold a window of recent model snapshots and can step backwards through them. The design choices follow:

- **Most-recent-first** ordering: editors step backwards, so index 0 = current, 1 = one undo
- **Fixed capacity**: prevents unbounded memory growth; `capacity` is typically small (e.g., 50 snapshots)
- **`TotalPushed` preserved on `clear`**: even after clearing the snapshot history, the total push count is preserved for audit/diagnostic purposes

---

## Open Questions

1. **Mutation vs. functional semantics**: `push` mutates `Items` in place before returning a new record. If the old `RingBuffer` value is retained, its `Items` array is shared with the new record and may now contain stale data. Is this intentional? Or should `push` copy the array for true immutability? This affects the safety of time-travel if old snapshots are kept.

2. **Thread safety**: The module has no synchronisation. Is `RingBuffer<'T>` intended to be used from a single thread only?

3. **`clear` semantics for `TotalPushed`**: Is it correct that `totalPushed` is preserved across `clear`? The `evictedCount` after `clear` will equal the pre-clear `totalPushed`, which may be surprising if callers interpret `evictedCount` as "how many have been evicted since last clear".
