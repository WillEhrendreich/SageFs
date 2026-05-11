# Informal Specification: SseReplayBuffer

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
> Source: `SageFs.Core/SseReplayBuffer.fs`

---

## Purpose

`SseReplayBuffer` provides a bounded circular store for SSE (Server-Sent Events)
that supports replay from a client-supplied `Last-Event-ID`. When an SSE client
reconnects, it sends the ID of the last event it received; the buffer returns all
events the client missed (if still retained), or reports that a gap occurred (some
events were already evicted).

The buffer wraps the existing `RingBuffer<SequencedSseEvent>` and assigns monotonically
increasing sequence IDs (seqIds) to every event it stores.

---

## Key Types

### `SequencedSseEvent`
A record holding `SeqId : int64`, `EventType : string`, `Payload : string`,
`Timestamp : DateTimeOffset`. For correctness purposes, only `SeqId` matters — the
content fields are irrelevant to replay logic.

### `ReplayResult`
```
| Replayed   of events: SequencedSseEvent list   -- 0 or more caught-up events
| GapDetected of firstAvailable: int64           -- oldest seqId still in buffer
```

### `Buffer`
A wrapper over `RingBuffer<SequencedSseEvent>`. The ring's `TotalPushed` counter
serves as the current seqId counter.

---

## Preconditions

- **Capacity > 0**: The ring buffer must be created with a positive capacity.
  (If capacity = 0 the ring is always empty and every push would be a no-op.)
- **Non-negative seqIds**: SeqIds start at 1 and only increase.
- **`lastSeenId ≥ 0`**: Clients supply 0 to mean "I have seen nothing".

---

## `push` — assign seqId and store

**Signature**: `push eventType payload buffer → (seqId, newBuffer)`

**Postconditions**:
1. `seqId = buffer.Ring.TotalPushed + 1`
2. `newBuffer.Ring.TotalPushed = buffer.Ring.TotalPushed + 1`
3. `count newBuffer ≤ capacity newBuffer` (ring does not overflow)
4. If the ring was full, the oldest event is evicted (count stays at capacity).
5. If the ring was not full, count increases by 1.

**SeqId monotonicity**: SeqIds are strictly increasing — no two pushes produce
the same seqId.

---

## `replayFrom` — reconnect replay

**Signature**: `replayFrom lastSeenId buffer → ReplayResult`

Let:
- `total = RingBuffer.totalPushed ring`
- `cnt = RingBuffer.count ring`
- `oldestAvailable = total - cnt + 1` (seqId of oldest retained event)
- `firstWanted = lastSeenId + 1`

**Cases**:

### Case 1 — Up-to-date (`lastSeenId ≥ total`)
The client is already current. Nothing to replay.
→ **`Replayed []`**

### Case 2 — Empty ring, not current (`cnt = 0 ∧ lastSeenId < total`)
Events have been pushed but none are retained (pathological: capacity = 0 or all
evicted). Cannot replay anything.
→ **`GapDetected (total + 1)`**
Note: `total + 1` is the seqId of the *next* event that will be pushed, not an
existing one.

### Case 3 — Gap detected (`cnt > 0 ∧ firstWanted < oldestAvailable`)
The client missed events that have been evicted.
→ **`GapDetected oldestAvailable`**

### Case 4 — No gap (`cnt > 0 ∧ firstWanted ≥ oldestAvailable ∧ lastSeenId < total`)
All missed events are still in the ring. Returns exactly `total - lastSeenId` events
in seqId order (oldest first). Each event's `SeqId` matches its position in the
sequence.
→ **`Replayed events`** where `|events| = total - lastSeenId`

---

## Invariants

1. **SeqId = TotalPushed**: `currentSeqId buffer = buffer.Ring.TotalPushed`
2. **SeqId 1-based**: A fresh buffer (total = 0) returns seqId = 1 on first push.
3. **Monotonic seqIds**: `seqId(push n+1) = seqId(push n) + 1`
4. **Count bounded by capacity**: `0 ≤ count ≤ capacity` always.
5. **Count bounded by total**: `count ≤ total` always (can't retain more than were pushed).
6. **OldestAvailable lower bound**: `oldestAvailable ≥ 1` when any events exist.
7. **Replay count bounded**: In Case 4, `|events| ≤ cnt` (can't replay more than retained).

---

## Edge Cases

- **Client at seqId 0** (`lastSeenId = 0`): firstWanted = 1. If total > 0 and
  cnt = total (buffer not full), returns all events. If cnt < total, gap detected.
- **Single-event buffer** (capacity = 1): at most 1 event retained; any client
  lagging by more than 1 event gets GapDetected.
- **Never-pushed buffer** (total = 0): any `lastSeenId ≥ 0` is ≥ total, so
  always returns `Replayed []`.
- **All events in ring** (`cnt = total`): no gap is possible; Case 4 always applies
  when `lastSeenId < total`.

---

## Examples

| total | cnt | lastSeenId | Result |
|-------|-----|------------|--------|
| 5 | 3 | 5 | `Replayed []` (up-to-date) |
| 5 | 3 | 6 | `Replayed []` (future, treated as up-to-date) |
| 5 | 0 | 3 | `GapDetected 6` (empty ring) |
| 5 | 3 | 1 | `GapDetected 3` (oldest=3, firstWanted=2 < 3) |
| 5 | 3 | 2 | `Replayed [e3, e4, e5]` — 3 events, ids 3..5 |
| 5 | 3 | 3 | `Replayed [e4, e5]` — 2 events |
| 5 | 3 | 4 | `Replayed [e5]` — 1 event |
| 0 | 0 | 0 | `Replayed []` (no events ever pushed) |

---

## Inferred Intent

The buffer is designed for SSE reconnection resilience: clients that miss events
due to a brief disconnect can catch up efficiently. The GapDetected case is an
honest failure: the buffer admits it cannot fully replay the missed window and
tells the client where it can start fresh. The seqId scheme ensures clients can
detect gaps themselves via the HTTP `Last-Event-ID` mechanism.

---

## Open Questions

1. **`lastSeenId` in Case 2 when `cnt = 0`**: The returned `GapDetected (total + 1)`
   refers to a future event. Should it instead be `GapDetected total` (last pushed)?
   The intent seems to be "I cannot give you anything; reconnect when new events arrive."
   Maintainer clarification welcome.
2. **Non-integer `lastSeenId`**: The spec assumes non-negative integer seqIds. The
   F# code uses `int64` but does not guard against negative values. Negative values
   satisfy `lastSeenId ≥ total` only when `total = 0`, otherwise they fall into
   Case 4 with potentially incorrect `total - lastSeenId` arithmetic. Is defensive
   clamping needed?
