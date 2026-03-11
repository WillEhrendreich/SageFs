namespace SageFs

open System

/// Sequenced SSE event stored in the replay buffer
type SequencedSseEvent = {
  SeqId: int64
  EventType: string
  Payload: string
  Timestamp: DateTimeOffset
}

/// Result of attempting to replay from a Last-Event-Id
[<RequireQualifiedAccess>]
type ReplayResult =
  | Replayed of events: SequencedSseEvent list
  | GapDetected of firstAvailable: int64

module SseReplayBuffer =
  open SageFs.RingBuffer

  type Buffer = {
    Ring: RingBuffer<SequencedSseEvent>
  }

  /// Create a new replay buffer with the given capacity.
  let create (capacity: int) : Buffer =
    { Ring = RingBuffer.create capacity }

  /// Push a new SSE event into the buffer.
  /// Returns the assigned monotonic seqId and the updated buffer.
  let push (eventType: string) (payload: string) (buffer: Buffer) : int64 * Buffer =
    let seqId = buffer.Ring.TotalPushed + 1L
    let event = {
      SeqId = seqId
      EventType = eventType
      Payload = payload
      Timestamp = DateTimeOffset.UtcNow
    }
    let newRing = RingBuffer.push event buffer.Ring
    (seqId, { Ring = newRing })

  /// Replay events that occurred after the given lastSeenId.
  /// Returns Replayed with the missed events (oldest first),
  /// or GapDetected if events have been evicted from the ring buffer.
  let replayFrom (lastSeenId: int64) (buffer: Buffer) : ReplayResult =
    let ring = buffer.Ring
    let total = RingBuffer.totalPushed ring
    let cnt = RingBuffer.count ring
    match lastSeenId >= total with
    | true -> ReplayResult.Replayed []
    | false ->
      match cnt with
      | 0 -> ReplayResult.GapDetected (total + 1L)
      | _ ->
        let oldestAvailable = total - int64 cnt + 1L
        let firstWanted = lastSeenId + 1L
        match firstWanted < oldestAvailable with
        | true -> ReplayResult.GapDetected oldestAvailable
        | false ->
          let events =
            [ for seqId in firstWanted .. total do
                let age = int (total - seqId)
                match RingBuffer.tryGet age ring with
                | Some evt -> yield evt
                | None -> () ]
          ReplayResult.Replayed events

  /// Prepend the SSE `id:` field to an already-formatted SSE frame.
  let formatWithId (seqId: int64) (sseFrame: string) : string =
    sprintf "id: %d\n%s" seqId sseFrame

  /// Get the current sequence number (total events pushed).
  let currentSeqId (buffer: Buffer) : int64 =
    RingBuffer.totalPushed buffer.Ring

  /// Get the number of events currently in the buffer.
  let count (buffer: Buffer) : int =
    RingBuffer.count buffer.Ring

  /// Get the buffer capacity.
  let capacity (buffer: Buffer) : int =
    RingBuffer.capacity buffer.Ring
