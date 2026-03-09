namespace SageFs

open SageFs.Measures

/// Time-travel model snapshots using a ring buffer.
/// Captures model state after each update for debugging and visualization.
module ModelSnapshot =

  /// A snapshot of the model at a point in time.
  type Snapshot<'Model> = {
    Model: 'Model
    MsgLabel: string
    Timestamp: System.DateTimeOffset
    UpdateMs: float<ms>
    SequenceNumber: int64
  }

  /// Configuration for the snapshot ring.
  [<Struct>]
  type SnapshotConfig = {
    Capacity: int
    Enabled: bool
  }

  /// Default config: 100 snapshots, enabled.
  let defaultConfig = { Capacity = 100; Enabled = true }

  /// The snapshot ring state.
  type SnapshotRing<'Model> = {
    Ring: RingBuffer.RingBuffer<Snapshot<'Model>>
    Config: SnapshotConfig
    NextSequence: int64
  }

  /// Create a new snapshot ring with the given config.
  let create (config: SnapshotConfig) : SnapshotRing<'Model> =
    { Ring = RingBuffer.create config.Capacity
      Config = config
      NextSequence = 0L }

  /// Record a model snapshot after an update.
  let record
    (msgLabel: string)
    (updateMs: float<ms>)
    (model: 'Model)
    (ring: SnapshotRing<'Model>)
    : SnapshotRing<'Model> =
    match ring.Config.Enabled with
    | false -> ring
    | true ->
      let snapshot = {
        Model = model
        MsgLabel = msgLabel
        Timestamp = System.DateTimeOffset.UtcNow
        UpdateMs = updateMs
        SequenceNumber = ring.NextSequence
      }
      { ring with
          Ring = RingBuffer.push snapshot ring.Ring
          NextSequence = ring.NextSequence + 1L }

  /// Get the most recent snapshot.
  let current (ring: SnapshotRing<'Model>) : Snapshot<'Model> option =
    RingBuffer.current ring.Ring

  /// Get a snapshot by age (0 = most recent).
  let tryGet (age: int) (ring: SnapshotRing<'Model>) : Snapshot<'Model> option =
    RingBuffer.tryGet age ring.Ring

  /// Number of snapshots in the ring.
  let count (ring: SnapshotRing<'Model>) : int =
    RingBuffer.count ring.Ring

  /// Total snapshots ever recorded.
  let totalRecorded (ring: SnapshotRing<'Model>) : int64 =
    ring.NextSequence

  /// Navigate to a specific age, returning the model at that point.
  let navigateTo (age: int) (ring: SnapshotRing<'Model>) : 'Model option =
    ring |> tryGet age |> Option.map (fun s -> s.Model)

  /// Get a summary of the snapshot history for display.
  let summary (ring: SnapshotRing<'Model>) : string =
    let c = count ring
    let total = totalRecorded ring
    match c with
    | 0 -> "No snapshots"
    | _ ->
      let latest = RingBuffer.current ring.Ring |> Option.get
      sprintf "%d/%d snapshots (latest: %s @ %.1fms)"
        c ring.Config.Capacity latest.MsgLabel (rawMsf latest.UpdateMs)

  /// List recent snapshot labels (most recent first).
  let recentLabels (maxCount: int) (ring: SnapshotRing<'Model>) : string list =
    ring.Ring
    |> RingBuffer.toSeq
    |> Seq.truncate maxCount
    |> Seq.map (fun s -> sprintf "#%d %s [%.1fms]" s.SequenceNumber s.MsgLabel (rawMsf s.UpdateMs))
    |> Seq.toList

  /// Enable or disable snapshot recording.
  let setEnabled (enabled: bool) (ring: SnapshotRing<'Model>) : SnapshotRing<'Model> =
    { ring with Config = { ring.Config with Enabled = enabled } }
