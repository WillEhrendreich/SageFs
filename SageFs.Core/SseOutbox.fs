namespace SageFs

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open SageFs.Utils

/// What waits in an outbox. A frame from the log is queued as the log's own `Replayable`, shared by
/// every connection, and turned into its text when the connection's writer reaches it: the log
/// publishes under its lock, and formatting a megabyte frame once per connection there would hold
/// every other publisher behind it.
[<RequireQualifiedAccess>]
type OutboxEntry =
  | Text of string
  | Logged of Replayable

module OutboxEntry =
  /// What the entry holds in memory. A text is UTF-16, so two bytes a char; a logged frame is
  /// shared with the log, so it weighs what the log says (its UTF-8 size).
  let weight (entry: OutboxEntry) : int =
    match entry with
    | OutboxEntry.Text text -> text.Length * 2
    | OutboxEntry.Logged item -> item.Bytes

  let toWire (entry: OutboxEntry) : string =
    match entry with
    | OutboxEntry.Text text -> text
    | OutboxEntry.Logged item -> Replayable.toWire item

/// One `/events` connection's queue of frames waiting to be written. The log fills it from the
/// thread that published, and one reader drains it onto the socket, so a slow client never holds
/// up a publisher.
///
/// When it is full the NEW frame is refused and the outbox closes, so the connection ends. That is
/// on purpose: every frame carries an id, and a client that reconnects with the last id it saw is
/// replayed what it missed. The old queue dropped its oldest frame without a word, which with ids
/// would be a hole no client could see.
///
/// Full means two things: `capacity` frames, or `maxBytes` of them. A count alone let a client that
/// stopped reading hold 1,024 frames of 400 KB each. A frame bigger than `maxBytes` is still
/// accepted when nothing is waiting, because refusing it would mean no client could ever get it.
type SseOutbox(capacity: int, maxBytes: int) =
  let channel =
    Channel.CreateBounded<OutboxEntry>(
      BoundedChannelOptions(capacity, FullMode = BoundedChannelFullMode.Wait, SingleReader = true))
  let mutable overflowed = 0
  let mutable queuedBytes = 0

  let refuse () : unit =
    Instrumentation.sseEventsDropped.Add(1L)
    Interlocked.Exchange(&overflowed, 1) |> ignore
    channel.Writer.TryComplete() |> ignore

  let release (weight: int) : unit = Interlocked.Add(&queuedBytes, -weight) |> ignore

  let offer (entry: OutboxEntry) : unit =
    let weight = OutboxEntry.weight entry
    let held = Volatile.Read(&queuedBytes)
    match held > 0 && held > maxBytes - weight with
    | true -> refuse ()
    | false ->
      Interlocked.Add(&queuedBytes, weight) |> ignore
      match channel.Writer.TryWrite entry with
      | true -> ()
      | false ->
        release weight
        refuse ()

  new(capacity: int) = SseOutbox(capacity, Int32.MaxValue)

  /// A comment line that keeps an idle connection open.
  static member KeepaliveFrame : string = ": keepalive\n\n"

  member _.Offer(frame: string) : unit = offer (OutboxEntry.Text frame)

  /// Queue a frame from the log without copying it; its text is built when it is written.
  member _.OfferLogged(item: Replayable) : unit = offer (OutboxEntry.Logged item)

  /// Not a frame: dropped when the queue is full, and never an overflow.
  member _.Keepalive() : unit =
    let entry = OutboxEntry.Text SseOutbox.KeepaliveFrame
    let weight = OutboxEntry.weight entry
    Interlocked.Add(&queuedBytes, weight) |> ignore
    match channel.Writer.TryWrite entry with
    | true -> ()
    | false -> release weight

  /// End the drain once what is queued has been written.
  member _.Close() : unit = channel.Writer.TryComplete() |> ignore

  member _.Overflowed : bool = Volatile.Read(&overflowed) = 1

  /// The next queued entry if there is one, without waiting. Taking it gives its room back. For the reader
  /// that owns this outbox only: it is a single-reader channel, and the catch-up and the drain that follows
  /// it run on one request, one after the other.
  member _.TryTake() : OutboxEntry option =
    match channel.Reader.TryRead() with
    | true, entry ->
      release (OutboxEntry.weight entry)
      Some entry
    | false, _ -> None

  /// The next frame's text, waiting for one. Reading gives its room back. Throws
  /// `ChannelClosedException` once the outbox is closed and empty.
  member _.ReadAsync(ct: CancellationToken) : Task<string> =
    task {
      let! entry = channel.Reader.ReadAsync ct
      release (OutboxEntry.weight entry)
      return OutboxEntry.toWire entry
    }

module SseOutbox =
  /// Frames one connection may have queued before it is cut off to resume from its last id.
  let capacity : int = 1024

  /// Memory one connection may have queued before it is cut off the same way: twice what the log
  /// holds (`SseLog.productionLimits`), since a queued frame can outlive its place in the log.
  let maxBytes : int = 32 <<< 20

  [<RequireQualifiedAccess>]
  type DrainEnd =
    /// The outbox was closed after the last frame was written.
    | Closed
    /// The client fell behind, so the connection ends and the client resumes by id.
    | Overflowed
    /// The client went away: cancelled, or a write failed.
    | ClientLeft

  /// Writes what is queued RIGHT NOW, in order, and does not wait for more. A catch-up calls this between
  /// the files it projects, so a daemon that keeps publishing while a long catch-up writes does not fill
  /// the queue and have the connection cut the moment the catch-up ends (the reconnect then wrote the
  /// same catch-up again).
  ///
  /// A logged frame goes out as its own text with no `id:` line. The client's last id must not move past
  /// frames it has not got the snapshot for: a connection cut in the middle of the catch-up resumes from
  /// the id it last had in full, is replayed these frames again, and is sent the snapshots again. The
  /// newest id written is returned so the id-only frame at the end of the catch-up can name it
  /// (`SseReplay.announcedAfterCatchUp`).
  let drainQueued (outbox: SseOutbox) (write: string -> Task) : Task<EventId option> =
    task {
      let mutable newest = None
      let mutable more = true
      while more do
        match outbox.TryTake() with
        | None -> more <- false
        | Some (OutboxEntry.Logged item) ->
          do! write item.Frame.Wire
          newest <- Some item.Id
        | Some (OutboxEntry.Text text) -> do! write text
      return newest
    }

  /// Writes queued frames, in order, until the outbox ends or the client leaves.
  let drain (outbox: SseOutbox) (write: string -> Task) (ct: CancellationToken) : Task<DrainEnd> =
    task {
      let mutable ended = None
      while Option.isNone ended do
        try
          let! frame = outbox.ReadAsync ct
          do! write frame
        with
        | :? OperationCanceledException -> ended <- Some DrainEnd.ClientLeft
        | :? IO.IOException as ex ->
          Instrumentation.sseWriteErrors.Add(1L)
          Log.debug "[SSE] Client disconnected (IOException): %s" ex.Message
          ended <- Some DrainEnd.ClientLeft
        | :? ObjectDisposedException as ex ->
          Instrumentation.sseWriteErrors.Add(1L)
          Log.debug "[SSE] Client disconnected (ObjectDisposed): %s" ex.Message
          ended <- Some DrainEnd.ClientLeft
        | :? ChannelClosedException ->
          ended <-
            Some (match outbox.Overflowed with
                  | true -> DrainEnd.Overflowed
                  | false -> DrainEnd.Closed)
      return ended.Value
    }
