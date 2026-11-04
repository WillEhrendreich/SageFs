namespace SageFs

/// Names one frame on one run of the daemon: `<epoch>:<seq>`. The epoch changes every time the
/// daemon starts, because seqs start again at 1 then. Without it, a client holding `7` from the
/// last run would be told "you have missed nothing" by a daemon that is on its third frame.
type EventId = { Epoch: string; Seq: int64 }

/// Why a `Last-Event-ID` header could not be read as an id.
[<RequireQualifiedAccess>]
type EventIdRefusal =
  | Blank
  /// Not `<epoch>:<seq>` with an epoch and a seq of 1 or more. Carries the raw text.
  | NotEpochColonSeq of raw: string

module EventId =
  let toWire (id: EventId) : string = sprintf "%s:%d" id.Epoch id.Seq

  /// A frame with an id and no event: a client that reads it moves its last id and dispatches
  /// nothing. The daemon writes one after a snapshot or a replay, so a client that is current
  /// through seq N says so, whether or not a frame it may see had that number.
  let toIdOnlyFrame (id: EventId) : string = sprintf "id: %s\n\n" (toWire id)

  /// The seq is whatever follows the LAST colon, so an epoch may contain colons itself.
  /// Seqs start at 1, so `0` and negatives are refused: a client that has seen nothing sends no header.
  let tryParse (raw: string) : Result<EventId, EventIdRefusal> =
    match System.String.IsNullOrWhiteSpace raw with
    | true -> Result.Error EventIdRefusal.Blank
    | false ->
      match raw.LastIndexOf ':' with
      | at when at <= 0 || at = raw.Length - 1 -> Result.Error (EventIdRefusal.NotEpochColonSeq raw)
      | at ->
        match System.Int64.TryParse(raw.Substring(at + 1)) with
        | true, seq when seq >= 1L -> Ok { Epoch = raw.Substring(0, at); Seq = seq }
        | _ -> Result.Error (EventIdRefusal.NotEpochColonSeq raw)

/// A frame the buffer kept, with the id it was sent under and its size on the wire.
type Replayable = { Id: EventId; Frame: SseFrame; Bytes: int }

module Replayable =
  /// The frame as a client reads it: its id on the line before its own text, which the
  /// EventSource format allows in any order and which leaves the frame's text whole.
  let toWire (item: Replayable) : string = sprintf "id: %s\n%s" (EventId.toWire item.Id) item.Frame.Wire

/// How far the daemon's numbering has got, and the id to name it by.
[<RequireQualifiedAccess>]
type LogPosition =
  /// No frame has been published on this run yet, so there is no id to name.
  | Empty
  /// The newest id issued.
  | At of EventId

/// How much history to keep. Both bounds apply: a frame can be a megabyte, so a count alone
/// would let a hundred of them hold a hundred megabytes.
type ReplayLimits = { MaxFrames: int; MaxBytes: int }

/// What a reconnecting client missed, as a pure function of what was recorded. The daemon
/// records every frame it sends, in the order it sends them; a client that comes back says the
/// last id it saw, and `catchUp` answers with the frames after it or with the reason it cannot.
module SseReplay =
  type Buffer =
    { Epoch: string
      Limits: ReplayLimits
      /// Keyed by seq, so the oldest is first.
      Frames: Map<int64, Replayable>
      /// How many frames are kept. `Map.count` walks the whole tree, and `trim` asks on every record and
      /// every eviction step while the log's lock is held.
      Count: int
      Bytes: int
      /// The newest seq ever issued, kept or not.
      Latest: int64 }

  [<RequireQualifiedAccess>]
  type ResyncReason =
    /// The request carried no `Last-Event-ID`.
    | FirstConnection
    | IdUnreadable of EventIdRefusal
    /// The id is from an earlier run of the daemon.
    | DaemonRestarted of theirs: string
    /// The frame after their id has been dropped to stay inside the limits.
    | Evicted of wanted: int64 * firstAvailable: int64
    /// A seq this run has not issued yet.
    | AheadOfThisRun of theirs: int64 * latest: int64

  [<RequireQualifiedAccess>]
  type CatchUp =
    /// Everything after their id that this stream may see, oldest first. May be empty.
    | Replay of Replayable list
    /// Replay cannot be trusted, so send the snapshots the daemon has always sent on connect.
    | Resync of ResyncReason

  let create (epoch: string) (limits: ReplayLimits) : Buffer =
    { Epoch = epoch; Limits = limits; Frames = Map.empty; Count = 0; Bytes = 0; Latest = 0L }

  let rec trim (buffer: Buffer) : Buffer =
    let over = buffer.Count > buffer.Limits.MaxFrames || buffer.Bytes > buffer.Limits.MaxBytes
    match over, Seq.tryHead buffer.Frames with
    | true, Some oldest ->
      trim
        { buffer with
            Frames = Map.remove oldest.Key buffer.Frames
            Count = buffer.Count - 1
            Bytes = buffer.Bytes - oldest.Value.Bytes }
    | _ -> buffer

  /// Spends the next seq on this frame and keeps it if the limits allow. A frame bigger than
  /// the byte bound evicts itself, and its seq stays spent so a client that saw it is not lied to.
  let record (frame: SseFrame) (buffer: Buffer) : Replayable * Buffer =
    let seq = buffer.Latest + 1L
    let item =
      { Id = { Epoch = buffer.Epoch; Seq = seq }
        Frame = frame
        Bytes = System.Text.Encoding.UTF8.GetByteCount frame.Wire }
    item,
    trim
      { buffer with
          Frames = Map.add seq item buffer.Frames
          Count = buffer.Count + 1
          Bytes = buffer.Bytes + item.Bytes
          Latest = seq }

  /// The newest id issued, or `Empty` before the first frame.
  let position (buffer: Buffer) : LogPosition =
    match buffer.Latest with
    | 0L -> LogPosition.Empty
    | latest -> LogPosition.At { Epoch = buffer.Epoch; Seq = latest }

  /// The position a catch-up announces when it is done. A catch-up lets queued live frames out as it goes
  /// (without their ids, so a connection cut mid-snapshot still resumes from the id it last had in full),
  /// so the client has already been sent frames past the position the connection attached at, and
  /// announcing the attach position would move its last id backwards. It is the newer of the two.
  let announcedAfterCatchUp (position: LogPosition) (writtenAhead: EventId option) : LogPosition =
    match position, writtenAhead with
    | LogPosition.At at, Some ahead when ahead.Seq > at.Seq -> LogPosition.At ahead
    | LogPosition.Empty, Some ahead -> LogPosition.At ahead
    | LogPosition.At _, Some _
    | LogPosition.At _, None
    | LogPosition.Empty, None -> position

  /// The oldest seq still held, or the next one to be issued when nothing is.
  let firstAvailable (buffer: Buffer) : int64 =
    match Seq.tryHead buffer.Frames with
    | Some oldest -> oldest.Key
    | None -> buffer.Latest + 1L

  let catchUp (stream: StreamScope) (lastEventId: string option) (buffer: Buffer) : CatchUp =
    match lastEventId with
    | None -> CatchUp.Resync ResyncReason.FirstConnection
    | Some raw ->
      match EventId.tryParse raw with
      | Result.Error refusal -> CatchUp.Resync (ResyncReason.IdUnreadable refusal)
      | Ok theirs when theirs.Epoch <> buffer.Epoch -> CatchUp.Resync (ResyncReason.DaemonRestarted theirs.Epoch)
      | Ok theirs when theirs.Seq > buffer.Latest -> CatchUp.Resync (ResyncReason.AheadOfThisRun (theirs.Seq, buffer.Latest))
      | Ok theirs ->
        let wanted = theirs.Seq + 1L
        let first = firstAvailable buffer
        match wanted < first with
        | true -> CatchUp.Resync (ResyncReason.Evicted (wanted, first))
        | false ->
          CatchUp.Replay
            [ for entry in buffer.Frames do
                if entry.Key >= wanted && SseFrame.visibleTo stream entry.Value.Frame then
                  yield entry.Value ]
