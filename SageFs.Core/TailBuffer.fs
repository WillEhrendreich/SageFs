namespace SageFs

/// Why a `TailBuffer` could not be created.
[<RequireQualifiedAccess>]
type TailCapacityError =
  /// A buffer that can hold nothing has no tail to report. Refused up front
  /// instead of crashing inside the array allocation.
  | NotPositive of requested: int

/// A bounded, mutable tail: it keeps the LAST `capacity` items pushed and
/// forgets the rest.
///
/// Data layout: one contiguous array allocated once plus a write index. A push
/// is a single slot write and an index bump, O(1), with no allocation beyond
/// the item itself (a string the caller already owns). A snapshot is one array
/// copy, oldest first.
///
/// This is deliberately not `SageFs.RingBuffer.RingBuffer`. That one is
/// copy-on-write (every push copies the backing array) because it hands out
/// immutable time-travel snapshots. A tail fed by a chatty child process needs
/// the opposite trade: cheap pushes, on a thread that must not fall behind the
/// pipe it is draining.
///
/// Thread-safe. One thread pushes (the stderr reader) while another may take a
/// snapshot (the failure handler); the lock is uncontended in the common case.
[<Sealed>]
type TailBuffer<'T> private (items: 'T array) =
  let gate = obj ()
  // Index the NEXT push writes to.
  let mutable next = 0
  let mutable held = 0

  static member TryCreate(capacity: int) : Result<TailBuffer<'T>, TailCapacityError> =
    match capacity > 0 with
    | true -> Ok (TailBuffer<'T>(Array.zeroCreate capacity))
    | false -> Error (TailCapacityError.NotPositive capacity)

  member _.Capacity : int = items.Length

  member _.Count : int = lock gate (fun () -> held)

  /// Append one item, evicting the oldest when full.
  member _.Push(item: 'T) : unit =
    lock gate (fun () ->
      items.[next] <- item
      next <- (next + 1) % items.Length
      held <- min (held + 1) items.Length)

  /// The last `n` items held, oldest first, as a fresh array. `n` beyond what
  /// is held returns everything held; `n <= 0` returns an empty array.
  member _.SnapshotLast(n: int) : 'T array =
    lock gate (fun () ->
      let take = min (max n 0) held
      let out : 'T array = Array.zeroCreate take
      let start = (next - take + items.Length) % items.Length
      for i in 0 .. take - 1 do
        out.[i] <- items.[(start + i) % items.Length]
      out)

  /// Everything held, oldest first, as a fresh array.
  member this.Snapshot() : 'T array = this.SnapshotLast items.Length

/// The daemon's view of one worker's stderr: a bounded tail, not an
/// ever-growing queue. Read only when a worker exits or goes silent during
/// warmup, so what it must hold is the END of the output, where the reason a
/// process died is.
module StderrTail =

  /// Lines of worker stderr the daemon keeps per worker.
  [<Literal>]
  let capacity = 200

  /// Lines of that tail put into a failure reason. Enough to see the
  /// exception and its first frames without flooding a status line.
  [<Literal>]
  let summaryLineCount = 20

  let create () : TailBuffer<string> =
    match TailBuffer<string>.TryCreate capacity with
    | Ok tail -> tail
    | Error err ->
      // Unreachable: `capacity` is a positive literal. Loud rather than silent
      // if someone ever edits it to a non-positive number.
      invalidOp (sprintf "StderrTail.capacity must be positive: %A" err)

  /// The last `summaryLineCount` lines, newline-joined; "" when nothing was
  /// captured (callers use that to keep their "no stderr" wording).
  let summary (tail: TailBuffer<string>) : string =
    tail.SnapshotLast summaryLineCount |> String.concat "\n"
