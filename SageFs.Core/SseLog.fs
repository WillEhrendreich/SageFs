namespace SageFs

/// What `SseLog.Attach` hands a connection: what it missed, where it now stands, and how to leave.
type SseAttachment =
  { /// Replay what was missed, or resync from snapshots and say why.
    CatchUp: SseReplay.CatchUp
    /// The newest id issued at the moment of attaching. Everything up to it is in `CatchUp` or
    /// covered by a snapshot; everything after it arrives through the observer. A connection
    /// writes it as an id-only frame once its catch-up is on the wire.
    Position: LogPosition
    Detach: unit -> unit }

/// The daemon's one sequencer. Every frame the daemon pushes goes through `Publish`, which gives
/// it the next id and tells every connection that may see it. A connection that comes back calls
/// `Attach` with the last id it saw.
///
/// Recording a frame and telling the observers is one step under one lock, and so is working out
/// what an attaching connection missed and registering it. That is the whole point: with two steps
/// a frame published between them would be in neither the replay nor the live feed, and a client
/// would never know it had missed it. An observer is called under the lock, so it must be quick
/// and must not throw; the daemon's is a write into a bounded channel.
type SseLog(epoch: string, limits: ReplayLimits) =
  let gate = obj ()
  let mutable buffer = SseReplay.create epoch limits
  let mutable observers : Map<int, StreamScope * (Replayable -> unit)> = Map.empty
  let mutable nextObserver = 0

  member _.Publish(frame: SseFrame) : Replayable =
    lock gate (fun () ->
      let item, buffer' = SseReplay.record frame buffer
      buffer <- buffer'
      for entry in observers do
        let stream, onFrame = entry.Value
        match SseFrame.visibleTo stream frame with
        | true -> onFrame item
        | false -> ()
      item)

  member _.Attach(stream: StreamScope, lastEventId: string option, onFrame: Replayable -> unit) : SseAttachment =
    lock gate (fun () ->
      let catchUp = SseReplay.catchUp stream lastEventId buffer
      let id = nextObserver
      nextObserver <- id + 1
      observers <- Map.add id (stream, onFrame) observers
      { CatchUp = catchUp
        Position = SseReplay.position buffer
        Detach = fun () -> lock gate (fun () -> observers <- Map.remove id observers) })

module SseLog =
  /// How much a reconnecting client can be caught up on. 2,000 frames is minutes of a busy test run;
  /// 16 MiB is the most memory the daemon will hold for it, since one test batch can be a megabyte.
  /// A client that was away longer than this gets the snapshots it always got, and is told why.
  let productionLimits : ReplayLimits = { MaxFrames = 2000; MaxBytes = 16 <<< 20 }

  /// A log for one run of the daemon. The epoch is new every time, so an id a client kept from an
  /// earlier run is never taken for one of this run's.
  let startNew () : SseLog =
    SseLog(System.Guid.NewGuid().ToString("N").Substring(0, 8), productionLimits)
