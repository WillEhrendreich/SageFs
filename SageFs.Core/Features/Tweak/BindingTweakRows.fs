/// The pane's rows, assembled: for every live binding (and every record field under one the files hold), the state, the control,
/// the reload its last write caused and the row's own undo. A pure function of four things the daemon holds, so a render and a
/// test see the same rows: what the session's files say (`SourceIndex`), the walked values, what was done to each row
/// (`Memory`), and what the session's last reload says.
///
/// `Memory` is the daemon's record of what the dashboard's own actions did, folded by `finished`: a write in flight, a refusal
/// that stays on the row until it is acted on again, the newest write per file (for undo and redo), and the reload verdict that
/// stood when each row was last written. It is a value, not a store: the shell keeps the newest one.
module SageFs.Features.Tweak.BindingTweakRows

open System
open SageFs
open SageFs.Features
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak

/// The reload verdict that stood when a row was written, and when, so the row can say what the app did with THAT write.
type LastWrite =
  { File: string
    Baseline: SessionReload
    At: int64 }

type Memory =
  { Outcomes: Map<RowKey, RowOutcome>
    Writes: Map<RowKey, LastWrite>
    Trails: Map<string, FileTrail>
    /// The value last pushed into the running app for a row, as the walk prints it, while the file does not hold it yet. A save
    /// (or an undo or redo) of the row ends it: from then on the file is the record.
    Pushes: Map<RowKey, string>
    /// For a row the pane has seen in step with its file, the hash of the file's expression at that moment (`Baseline`).
    Seen: Map<RowKey, string> }

module RowKey =
  let top (binding: string) : RowKey = { Binding = binding; Labels = [] }

  let field (parent: RowKey) (label: string) : RowKey = { parent with Labels = parent.Labels @ [ label ] }

  /// The steps that reach a row from its binding: record fields, one per label.
  let steps (key: RowKey) : PathStep list = key.Labels |> List.map PathStep.RecordField

  /// The row an address belongs to, when it is a binding or record fields down from one (the only rows the pane maps).
  let ofAddress (address: TweakAddress) : RowKey option =
    let fields = address.Path |> List.choose (function PathStep.RecordField name -> Some name | _ -> None)
    match fields.Length = address.Path.Length with
    | true -> Some { Binding = address.BindingName; Labels = fields }
    | false -> None

  /// The text a client sends back to say which row it means (its address, one `/` step per record field).
  let text (key: RowKey) : string = String.concat "/" (key.Binding :: key.Labels)

module Memory =
  let empty : Memory = { Outcomes = Map.empty; Writes = Map.empty; Trails = Map.empty; Pushes = Map.empty; Seen = Map.empty }

  let baselineOf (memory: Memory) (key: RowKey) : Baseline =
    match Map.tryFind key memory.Seen with
    | Some hash -> Baseline.InStepAt hash
    | None -> Baseline.NeverInStep

  /// The revision of the file a place is in, as this index read it.
  let revisionOf (index: SourceIndex) (place: SourceRef) : FileRevision =
    match SourceIndex.fileOf index place.File with
    | file :: _ -> file.Revision
    | [] -> FileRevision.Unrevised

  /// Take note of every row that is in step with its file in this view, so a later difference can be told from a file edit. A
  /// row not in step keeps what it had: the baseline is the last time they agreed, not the latest look. A file that cannot be
  /// stamped gives no baseline, because nothing could later say it had not moved.
  let observe (index: SourceIndex) (view: TweakView) (memory: Memory) : Memory =
    let seen =
      view.Rows
      |> Map.fold
           (fun acc key row ->
             match row.State with
             | PersistenceState.InSource place ->
               match revisionOf index place with
               | FileRevision.Revised stamp -> Map.add key stamp acc
               | FileRevision.Unrevised -> Map.remove key acc
             | PersistenceState.NotInAFile _
             | PersistenceState.Held
             | PersistenceState.Derived _
             | PersistenceState.Ambiguous _
             | PersistenceState.Tweaked _
             | PersistenceState.Evolved _
             | PersistenceState.DiffersFromFile _
             | PersistenceState.StaleAddress _
             | PersistenceState.Refused _
             | PersistenceState.Writing _ -> acc)
           memory.Seen
    { memory with Seen = seen }

  let pushOf (memory: Memory) (key: RowKey) : PushedValue =
    match Map.tryFind key memory.Pushes with
    | Some printed -> PushedValue.Pushed printed
    | None -> PushedValue.NoPush

  /// A value was pushed into the running app for this row. The newest push is the one that counts.
  let pushed (key: RowKey) (printed: string) (memory: Memory) : Memory = { memory with Pushes = Map.add key printed memory.Pushes }

  /// The person reverted the tweak: nothing is pushed any more.
  let unpushed (key: RowKey) (memory: Memory) : Memory = { memory with Pushes = Map.remove key memory.Pushes }

  let outcomeOf (memory: Memory) (key: RowKey) : RowOutcome =
    match Map.tryFind key memory.Outcomes with
    | Some outcome -> outcome
    | None -> RowOutcome.NoOutcome

  let trailOf (memory: Memory) (file: string) : FileTrail =
    match Map.tryFind file memory.Trails with
    | Some trail -> trail
    | None -> FileTrail.empty

  /// A write is on its way: the row shows it at once, before the door answers.
  let started (key: RowKey) (kind: WriteKind) (attempted: SourceRef) (memory: Memory) : Memory =
    { memory with Outcomes = Map.add key (RowOutcome.InFlight(kind, attempted)) memory.Outcomes }

  /// The door answered. A refusal stays on the row (with the expression the row was showing) until the row is acted on again;
  /// an answer that changed the file clears it, and records the write: for the file's undo trail, and for the reload to watch.
  let finished
    (key: RowKey)
    (attempted: SourceRef)
    (result: Result<Ran, NudgeRefusal>)
    (baseline: SessionReload)
    (now: int64)
    (memory: Memory)
    : Memory =
    let cleared = { memory with Outcomes = Map.remove key memory.Outcomes }
    match result with
    | Error refusal -> { memory with Outcomes = Map.add key (RowOutcome.Rejected(refusal, attempted)) memory.Outcomes }
    | Ok ran ->
      let landed (receipt: Receipt) (move: FileTrail -> FileTrail) =
        // A write to the address the clicked row showed is that row's, even when the address is a callee's literal and no row is named
        // for it. An undo or redo can land on another row's write, so that one is found by its address.
        let target =
          match receipt.Address = attempted.Address with
          | true -> key
          | false -> RowKey.ofAddress receipt.Address |> Option.defaultValue key
        { cleared with
            Trails = Map.add receipt.File (move (trailOf cleared receipt.File)) cleared.Trails
            Writes = Map.add target { File = receipt.File; Baseline = baseline; At = now } cleared.Writes
            Pushes = Map.remove target cleared.Pushes }
      match ran.Outcome with
      | NudgeOutcome.Written receipt -> landed receipt (FileTrail.wrote (NudgeAddress.format receipt.Address))
      | NudgeOutcome.Undone receipt -> landed receipt FileTrail.undid
      | NudgeOutcome.Redone receipt -> landed receipt FileTrail.redid
      | NudgeOutcome.Unchanged _
      | NudgeOutcome.Inspected _ -> cleared

  /// Forget everything about a row's outcome (the person dismissed it, or the row is gone).
  let forgetRow (key: RowKey) (memory: Memory) : Memory = { memory with Outcomes = Map.remove key memory.Outcomes }

module Rows =
  let watchingOf (index: SourceIndex) (place: PlaceOf) : WatchStatus =
    match place with
    | PlaceOf.NotPlaced -> WatchStatus.NotWatched
    | PlaceOf.Placed source ->
      match SourceIndex.fileOf index source.File with
      | file :: _ -> file.Watching
      | [] -> WatchStatus.NotWatched

  let reloadOf (memory: Memory) (current: SessionReload) (now: int64) (patience: TimeSpan) (key: RowKey) : RowReload =
    match Map.tryFind key memory.Writes with
    | None -> RowReload.NoWriteYet
    | Some write ->
      let waited = TimeSpan.FromTicks(max 0L (now - write.At))
      RowReload.Watching(write.File, ReloadWatch.ofSession write.Baseline current waited patience)

  /// A part the file does not spell, of a binding the file builds with a call, is followed to the literal in the callee, and that
  /// literal is then the row's source. Any other facts stand as they are. A call that cannot be followed keeps the facts and says why.
  let throughCall (index: SourceIndex) (steps: PathStep list) (facts: SourceFacts) : SourceFacts * ThroughCall =
    match facts with
    | SourceFacts.PartNotSpelled owner ->
      match CallFollow.follow index owner steps with
      | Outcome.Followed followed -> SourceFacts.OneSource followed.Place, ThroughCall.Through followed
      | Outcome.Refused Refusal.NotAPlainCall -> facts, ThroughCall.NoCall
      | Outcome.Refused refusal -> facts, ThroughCall.CouldNotFollow refusal
    | SourceFacts.NoFileBindsIt
    | SourceFacts.FilesUnknown _
    | SourceFacts.OneSource _
    | SourceFacts.ManySources _ -> facts, ThroughCall.NoCall

  let viewOf
    (index: SourceIndex)
    (memory: Memory)
    (current: SessionReload)
    (now: int64)
    (patience: TimeSpan)
    (key: RowKey)
    (live: LiveShape)
    (found: SourceFacts)
    : RowView =
    let facts, through = throughCall index (RowKey.steps key) found
    let place = PlaceOf.ofFacts facts
    // The walk does not report whether a place is assignable yet, so storage is not known and a row is `Evolved` only from a push.
    let fileNow =
      match place with
      | PlaceOf.Placed source -> Memory.revisionOf index source
      | PlaceOf.NotPlaced -> FileRevision.Unrevised
    let provenance =
      { Pushed = Memory.pushOf memory key
        Storage = Storage.NotKnown
        Baseline = Memory.baselineOf memory key
        FileNow = fileNow }
    let state, control = stateAndControlWith provenance live facts (Memory.outcomeOf memory key)
    let trail =
      match place with
      | PlaceOf.Placed source -> Memory.trailOf memory source.File
      | PlaceOf.NotPlaced -> FileTrail.empty
    let address =
      match place with
      | PlaceOf.Placed source -> SourceRef.addressText source
      | PlaceOf.NotPlaced -> RowKey.text key
    { State = state
      Control = control
      Place = place
      Watching = watchingOf index place
      Reload = reloadOf memory current now patience key
      Undo = FileTrail.undoFor address trail
      Redo = FileTrail.redoFor address trail
      Through = through }

  /// Record fields down from a binding the files hold in exactly one place. Only records are walked: a list's, a tuple's or a map's
  /// items are not mapped, so they get no row, and the binding's own row says it is a container.
  let rec private fieldRows
    (rowOf: RowKey -> LiveShape -> SourceFacts -> RowView)
    (index: SourceIndex)
    (parent: RowKey)
    (node: LiveValueTree.LiveValueNode)
    : (RowKey * RowView) list =
    match node.Kind with
    | LiveValueTree.NodeKind.Record ->
      node.Children
      |> List.collect (fun child ->
        let key = RowKey.field parent child.Label
        let facts = SourceIndex.factsFor index parent.Binding (RowKey.steps key)
        (key, rowOf key (LiveShape.ofNode child) facts) :: fieldRows rowOf index key child)
    | _ -> []

  /// Every row the pane has something to say about for this session's walked values.
  let build
    (index: SourceIndex)
    (snapshot: LiveValueTree.LiveValueSnapshot)
    (memory: Memory)
    (current: SessionReload)
    (now: int64)
    (patience: TimeSpan)
    : TweakView =
    let rowOf = viewOf index memory current now patience
    let rows =
      snapshot.Bindings
      |> List.collect (fun binding ->
        let top = RowKey.top binding.Name
        let topFacts = SourceIndex.factsFor index binding.Name []
        let own = top, rowOf top (LiveShape.ofNode binding.Root) topFacts
        match topFacts with
        | SourceFacts.OneSource _ -> own :: fieldRows (fun key live facts -> rowOf key live facts) index top binding.Root
        | SourceFacts.NoFileBindsIt
        | SourceFacts.FilesUnknown _
        | SourceFacts.ManySources _
        | SourceFacts.PartNotSpelled _ -> [ own ])
    { Rows = Map.ofList rows }

/// The names a pane asks the files about: its top-level bindings.
let namesOf (snapshot: LiveValueTree.LiveValueSnapshot) : Set<string> =
  snapshot.Bindings |> List.map (fun binding -> binding.Name) |> Set.ofList
