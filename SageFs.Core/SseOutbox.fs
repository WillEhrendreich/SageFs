namespace SageFs

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open SageFs.Utils

/// One `/events` connection's queue of frames waiting to be written. The log fills it from the
/// thread that published, and one reader drains it onto the socket, so a slow client never holds
/// up a publisher.
///
/// When it is full the NEW frame is refused and the outbox closes, so the connection ends. That is
/// on purpose: every frame carries an id, and a client that reconnects with the last id it saw is
/// replayed what it missed. The old queue dropped its oldest frame without a word, which with ids
/// would be a hole no client could see.
type SseOutbox(capacity: int) =
  let channel =
    Channel.CreateBounded<string>(
      BoundedChannelOptions(capacity, FullMode = BoundedChannelFullMode.Wait, SingleReader = true))
  let mutable overflowed = 0

  /// A comment line that keeps an idle connection open.
  static member KeepaliveFrame : string = ": keepalive\n\n"

  member _.Offer(frame: string) : unit =
    match channel.Writer.TryWrite frame with
    | true -> ()
    | false ->
      Instrumentation.sseEventsDropped.Add(1L)
      Interlocked.Exchange(&overflowed, 1) |> ignore
      channel.Writer.TryComplete() |> ignore

  /// Not a frame: dropped when the queue is full, and never an overflow.
  member _.Keepalive() : unit = channel.Writer.TryWrite SseOutbox.KeepaliveFrame |> ignore

  /// End the drain once what is queued has been written.
  member _.Close() : unit = channel.Writer.TryComplete() |> ignore

  member _.Overflowed : bool = Volatile.Read(&overflowed) = 1

  member _.Reader : ChannelReader<string> = channel.Reader

module SseOutbox =
  /// Frames one connection may have queued before it is cut off to resume from its last id.
  let capacity : int = 1024

  [<RequireQualifiedAccess>]
  type DrainEnd =
    /// The outbox was closed after the last frame was written.
    | Closed
    /// The client fell behind, so the connection ends and the client resumes by id.
    | Overflowed
    /// The client went away: cancelled, or a write failed.
    | ClientLeft

  /// Writes queued frames, in order, until the outbox ends or the client leaves.
  let drain (outbox: SseOutbox) (write: string -> Task) (ct: CancellationToken) : Task<DrainEnd> =
    task {
      let mutable ended = None
      while Option.isNone ended do
        try
          let! frame = outbox.Reader.ReadAsync ct
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
