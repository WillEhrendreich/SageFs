namespace SageFs

open SageFs.Measures

/// Time-travel debugging: navigate through model history using
/// the ring buffer of immutable snapshots. F#'s structural sharing
/// means snapshots that differ in only a few fields share memory.
module TimeTravel =

  /// Whether we're viewing live state or a historical snapshot.
  [<Struct>]
  type TimeTravelMode =
    | Live
    | Viewing of age: int

  /// Full time-travel state: snapshot ring + navigation position.
  type TimeTravelState<'Model> = {
    Ring: ModelSnapshot.SnapshotRing<'Model>
    Mode: TimeTravelMode
  }

  /// Create a new time-travel state with the given snapshot config.
  let create (config: ModelSnapshot.SnapshotConfig) : TimeTravelState<'Model> =
    { Ring = ModelSnapshot.create config
      Mode = Live }

  /// Whether we're in live mode (not viewing history).
  let isLive (state: TimeTravelState<'Model>) : bool =
    match state.Mode with
    | Live -> true
    | Viewing _ -> false

  /// The age being viewed, or None if live.
  let viewingAge (state: TimeTravelState<'Model>) : int option =
    match state.Mode with
    | Live -> None
    | Viewing age -> Some age

  /// Number of snapshots in the ring.
  let snapshotCount (state: TimeTravelState<'Model>) : int =
    ModelSnapshot.count state.Ring

  /// Record a model snapshot. Only records in live mode —
  /// while time-traveling, the ring is frozen.
  let record
    (msgLabel: string)
    (updateMs: float<ms>)
    (model: 'Model)
    (state: TimeTravelState<'Model>)
    : TimeTravelState<'Model> =
    match state.Mode with
    | Viewing _ -> state
    | Live ->
      { state with Ring = ModelSnapshot.record msgLabel updateMs model state.Ring }

  /// Step back in time (view an older snapshot).
  /// From live: goes to age 1 (previous snapshot).
  /// From viewing: increments age by 1.
  /// No-op if at the oldest available snapshot or if ring has < 2 snapshots.
  let stepBack (state: TimeTravelState<'Model>) : TimeTravelState<'Model> =
    let count = ModelSnapshot.count state.Ring
    match state.Mode with
    | Live ->
      match count > 1 with
      | true -> { state with Mode = Viewing 1 }
      | false -> state
    | Viewing age ->
      match age + 1 < count with
      | true -> { state with Mode = Viewing (age + 1) }
      | false -> state

  /// Step forward in time (toward the present).
  /// From viewing age 1: returns to live.
  /// From viewing age N: goes to age N-1.
  /// No-op if already live.
  let stepForward (state: TimeTravelState<'Model>) : TimeTravelState<'Model> =
    match state.Mode with
    | Live -> state
    | Viewing age ->
      match age <= 1 with
      | true -> { state with Mode = Live }
      | false -> { state with Mode = Viewing (age - 1) }

  /// Return to live mode from any viewing position.
  let goLive (state: TimeTravelState<'Model>) : TimeTravelState<'Model> =
    { state with Mode = Live }

  /// Get the model at the current viewing position.
  /// Live mode returns the most recent snapshot (age 0).
  /// Viewing mode returns the snapshot at the viewed age.
  let currentModel (state: TimeTravelState<'Model>) : 'Model option =
    match state.Mode with
    | Live -> ModelSnapshot.navigateTo 0 state.Ring
    | Viewing age -> ModelSnapshot.navigateTo age state.Ring

  /// Format a status string for display. Returns None if no snapshots exist.
  let formatStatus (state: TimeTravelState<'Model>) : string option =
    let count = ModelSnapshot.count state.Ring
    match state.Mode with
    | Live ->
      match count with
      | 0 -> None
      | n -> Some (sprintf "⏱ %d snapshots" n)
    | Viewing age ->
      Some (sprintf "⏮ -%d (Alt+→=fwd Esc=live)" age)

  /// Enable or disable snapshot recording.
  let setEnabled (enabled: bool) (state: TimeTravelState<'Model>) : TimeTravelState<'Model> =
    { state with Ring = ModelSnapshot.setEnabled enabled state.Ring }

  /// Get recent snapshot labels for display.
  let recentLabels (maxCount: int) (state: TimeTravelState<'Model>) : string list =
    ModelSnapshot.recentLabels maxCount state.Ring
