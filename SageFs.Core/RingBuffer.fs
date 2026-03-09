namespace SageFs

/// Generic fixed-capacity ring buffer for time-travel model snapshots.
/// Structural sharing via F# immutable records means snapshots that
/// differ in only a few fields share most of their memory.
module RingBuffer =

  /// A fixed-capacity ring buffer holding up to `capacity` items.
  /// Items are accessed by age: 0 = most recent, 1 = previous, etc.
  type RingBuffer<'T> = {
    Items: 'T array
    Head: int
    Count: int
    TotalPushed: int64
  }

  /// Create an empty ring buffer with the given capacity.
  let create (capacity: int) : RingBuffer<'T> =
    match capacity > 0 with
    | true ->
      { Items = Array.zeroCreate capacity
        Head = 0
        Count = 0
        TotalPushed = 0L }
    | false ->
      invalidArg "capacity" "Ring buffer capacity must be positive"

  /// Push a new item into the buffer. If full, the oldest item is evicted.
  let push (item: 'T) (buf: RingBuffer<'T>) : RingBuffer<'T> =
    let capacity = buf.Items.Length
    let newHead = (buf.Head + capacity - 1) % capacity
    buf.Items.[newHead] <- item
    { buf with
        Head = newHead
        Count = min (buf.Count + 1) capacity
        TotalPushed = buf.TotalPushed + 1L }

  /// Get an item by age (0 = most recent, 1 = previous, etc.).
  /// Returns None if age exceeds available history.
  let tryGet (age: int) (buf: RingBuffer<'T>) : 'T option =
    match age >= 0 && age < buf.Count with
    | true ->
      let idx = (buf.Head + age) % buf.Items.Length
      Some buf.Items.[idx]
    | false -> None

  /// Get the most recent item, or None if empty.
  let current (buf: RingBuffer<'T>) : 'T option =
    tryGet 0 buf

  /// Get the previous item (age = 1), or None if fewer than 2 items.
  let previous (buf: RingBuffer<'T>) : 'T option =
    tryGet 1 buf

  /// Number of items currently in the buffer.
  let count (buf: RingBuffer<'T>) : int = buf.Count

  /// Maximum capacity.
  let capacity (buf: RingBuffer<'T>) : int = buf.Items.Length

  /// Whether the buffer is empty.
  let isEmpty (buf: RingBuffer<'T>) : bool = buf.Count = 0

  /// Whether the buffer is full (next push will evict oldest).
  let isFull (buf: RingBuffer<'T>) : bool = buf.Count = buf.Items.Length

  /// Total number of items ever pushed (including evicted).
  let totalPushed (buf: RingBuffer<'T>) : int64 = buf.TotalPushed

  /// Number of items that have been evicted.
  let evictedCount (buf: RingBuffer<'T>) : int64 =
    buf.TotalPushed - int64 buf.Count

  /// Iterate items from most recent to oldest.
  let toSeq (buf: RingBuffer<'T>) : 'T seq =
    seq {
      for age in 0 .. buf.Count - 1 do
        let idx = (buf.Head + age) % buf.Items.Length
        yield buf.Items.[idx]
    }

  /// Convert to list (most recent first).
  let toList (buf: RingBuffer<'T>) : 'T list =
    toSeq buf |> Seq.toList

  /// Apply a function to each item (most recent to oldest).
  let iter (f: 'T -> unit) (buf: RingBuffer<'T>) : unit =
    for age in 0 .. buf.Count - 1 do
      let idx = (buf.Head + age) % buf.Items.Length
      f buf.Items.[idx]

  /// Map over all items, preserving order (most recent first).
  let map (f: 'T -> 'U) (buf: RingBuffer<'T>) : 'U list =
    [ for age in 0 .. buf.Count - 1 do
        let idx = (buf.Head + age) % buf.Items.Length
        yield f buf.Items.[idx] ]

  /// Fold over all items from most recent to oldest.
  let fold (f: 'State -> 'T -> 'State) (state: 'State) (buf: RingBuffer<'T>) : 'State =
    let mutable acc = state
    for age in 0 .. buf.Count - 1 do
      let idx = (buf.Head + age) % buf.Items.Length
      acc <- f acc buf.Items.[idx]
    acc

  /// Clear the buffer, keeping the same capacity.
  let clear (buf: RingBuffer<'T>) : RingBuffer<'T> =
    { Items = Array.zeroCreate buf.Items.Length
      Head = 0
      Count = 0
      TotalPushed = buf.TotalPushed }
