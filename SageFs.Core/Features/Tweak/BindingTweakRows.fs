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
    Trails: Map<string, FileTrail> }

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
  let empty : Memory = { Outcomes = Map.empty; Writes = Map.empty; Trails = Map.empty }

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
        let target = RowKey.ofAddress receipt.Address |> Option.defaultValue key
        { cleared with
            Trails = Map.add receipt.File (move (trailOf cleared receipt.File)) cleared.Trails
            Writes = Map.add target { File = receipt.File; Baseline = baseline; At = now } cleared.Writes }
      match ran.Outcome with
      | NudgeOutcome.Written receipt -> landed receipt (FileTrail.wrote (NudgeAddress.format receipt.Address))
      | NudgeOutcome.Undone receipt -> landed receipt FileTrail.undid
      | NudgeOutcome.Redone receipt -> landed receipt FileTrail.redid
      | NudgeOutcome.Unchanged _
      | NudgeOutcome.Inspected _ -> cleared

  /// Forget everything about a row's outcome (the person dismissed it, or the row is gone).
  let forgetRow (key: RowKey) (memory: Memory) : Memory = { memory with Outcomes = Map.remove key memory.Outcomes }

[<RequireQualifiedAccess>]
type PlaceOf =
  | Placed of SourceRef
  | NotPlaced

module PlaceOf =
  let ofFacts (facts: SourceFacts) : PlaceOf =
    match facts with
    | SourceFacts.OneSource place -> PlaceOf.Placed place
    | SourceFacts.NoFileBindsIt
    | SourceFacts.FilesUnknown _
    | SourceFacts.ManySources _
    | SourceFacts.PartNotSpelled _
    | SourceFacts.PartNotMapped _ -> PlaceOf.NotPlaced

module Rows =
  let private watchingOf (index: SourceIndex) (place: PlaceOf) : WatchStatus =
    match place with
    | PlaceOf.NotPlaced -> WatchStatus.NotWatched
    | PlaceOf.Placed source ->
      match SourceIndex.fileOf index source.File with
      | file :: _ -> file.Watching
      | [] -> WatchStatus.NotWatched

  let private reloadOf (memory: Memory) (current: SessionReload) (now: int64) (patience: TimeSpan) (key: RowKey) : RowReload =
    match Map.tryFind key memory.Writes with
    | None -> RowReload.NoWriteYet
    | Some write ->
      let waited = TimeSpan.FromTicks(max 0L (now - write.At))
      RowReload.Watching(write.File, ReloadWatch.ofSession write.Baseline current waited patience)

  let private viewOf
    (index: SourceIndex)
    (memory: Memory)
    (current: SessionReload)
    (now: int64)
    (patience: TimeSpan)
    (key: RowKey)
    (live: LiveShape)
    (facts: SourceFacts)
    : RowView =
    let state = PersistenceState.derive live facts (Memory.outcomeOf memory key)
    let place = PlaceOf.ofFacts facts
    let trail =
      match place with
      | PlaceOf.Placed source -> Memory.trailOf memory source.File
      | PlaceOf.NotPlaced -> FileTrail.empty
    let address =
      match place with
      | PlaceOf.Placed source -> SourceRef.addressText source
      | PlaceOf.NotPlaced -> RowKey.text key
    { State = state
      Control = Control.ofState state facts
      Watching = watchingOf index place
      Reload = reloadOf memory current now patience key
      Undo = FileTrail.undoFor address trail
      Redo = FileTrail.redoFor address trail }

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
        | SourceFacts.PartNotSpelled _
        | SourceFacts.PartNotMapped _ -> [ own ])
    { Rows = Map.ofList rows }

  /// The names a pane asks the files about: its top-level bindings.
  let namesOf (snapshot: LiveValueTree.LiveValueSnapshot) : Set<string> =
    snapshot.Bindings |> List.map (fun binding -> binding.Name) |> Set.ofList
