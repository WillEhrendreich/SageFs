/// The changes strip of the live bindings pane: what the session holds that the files do not, and what the program changed on its own,
/// as one model the strip draws and the "Save all" and "Revert all" buttons act on. Pure: a function of the pane's rows.
///
/// The two lists never mix. A tweak is a value the person tried and the file does not hold yet, so saving it records a decision the
/// person made. An evolved value is one the program changed, so writing it into the file would record the program's change as the
/// person's decision; the strip counts it and does nothing to it. Save all and Revert all are built from the tweaks only, and a test
/// holds that across every combination of rows.
module SageFs.Features.Tweak.ChangesStrip

open SageFs
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Features.Tweak.PushCode

/// A value tried in the session that the file does not hold yet.
type Tweak =
  { Row: RowKey
    Place: SourceRef
    /// What the file says now, as written there.
    Before: string
    /// What the session holds, as the walk prints it.
    After: string
    Via: PushVia }

/// A value the program changed after it was made. Counted and shown, never written by the strip.
type Evolution =
  { Row: RowKey
    Place: SourceRef
    /// What the file says, which is where the value started.
    Initial: string
    Now: string }

type Changes = { Tweaks: Tweak list; Evolved: Evolution list }

module Changes =
  let empty : Changes = { Tweaks = []; Evolved = [] }

  let isEmpty (changes: Changes) : bool = List.isEmpty changes.Tweaks && List.isEmpty changes.Evolved

  /// The tweak a row is, when it is one. Every state is named, so a state added later has to say which side it is on.
  let tweakOf (key: RowKey) (row: RowView) : Tweak option =
    match row.State with
    | PersistenceState.Tweaked(live, place) -> Some({ Row = key; Place = place; Before = place.Text; After = live; Via = row.Via }: Tweak)
    | PersistenceState.InSource _
    | PersistenceState.NotInAFile _
    | PersistenceState.Held
    | PersistenceState.Derived _
    | PersistenceState.Ambiguous _
    | PersistenceState.Evolved _
    | PersistenceState.DiffersFromFile _
    | PersistenceState.StaleAddress _
    | PersistenceState.Refused _
    | PersistenceState.Writing _ -> None

  /// The value the program changed a row to, when it is one.
  let evolutionOf (key: RowKey) (row: RowView) : Evolution option =
    match row.State with
    | PersistenceState.Evolved(live, _, place) -> Some({ Row = key; Place = place; Initial = place.Text; Now = live }: Evolution)
    | PersistenceState.InSource _
    | PersistenceState.NotInAFile _
    | PersistenceState.Held
    | PersistenceState.Derived _
    | PersistenceState.Ambiguous _
    | PersistenceState.Tweaked _
    | PersistenceState.DiffersFromFile _
    | PersistenceState.StaleAddress _
    | PersistenceState.Refused _
    | PersistenceState.Writing _ -> None

  /// The tweaks and the evolved values of a view, in file order and then row order, so the strip does not reshuffle between pushes.
  let ofView (view: TweakView) : Changes =
    let rows = view.Rows |> Map.toList
    { Tweaks =
        rows
        |> List.choose (fun (key, row) -> tweakOf key row)
        |> List.sortBy (fun (t: Tweak) -> t.Place.File, RowKey.text t.Row)
      Evolved =
        rows
        |> List.choose (fun (key, row) -> evolutionOf key row)
        |> List.sortBy (fun (e: Evolution) -> e.Place.File, RowKey.text e.Row) }

  /// The tweaks grouped by the file they would be written to, in file order.
  let byFile (tweaks: Tweak list) : (string * Tweak list) list =
    tweaks |> List.groupBy (fun t -> t.Place.File) |> List.sortBy fst

/// What one request of a batch does.
[<RequireQualifiedAccess>]
type BatchKind =
  /// Write the tweak into the file, through the same door a knob uses.
  | Save
  /// Put the file's value back into the session.
  | Revert

module BatchKind =
  let token (kind: BatchKind) : string =
    match kind with
    | BatchKind.Save -> "save"
    | BatchKind.Revert -> "revert"

  let all : BatchKind list = [ BatchKind.Save; BatchKind.Revert ]

  let ofToken (text: string) : Result<BatchKind, ConfigError> =
    match all |> List.tryFind (fun kind -> token kind = text) with
    | Some kind -> Ok kind
    | None -> Error(NotAMember(text, all |> List.map token))

/// One request the strip posts for one tweak. Strings only, so the page can carry the whole batch in one signal.
type BatchItem =
  { Kind: string
    Row: string
    File: string
    Address: string
    Seen: string
    SeenText: string
    /// For a save, the text the door takes. Empty for a revert.
    Value: string
    Via: string }

module Batch =
  let private itemOf (kind: BatchKind) (tweak: Tweak) : BatchItem =
    { Kind = BatchKind.token kind
      Row = RowKey.text tweak.Row
      File = tweak.Place.File
      Address = SourceRef.addressText tweak.Place
      Seen = tweak.Place.Hash
      SeenText = tweak.Place.Text
      Value =
        match kind with
        | BatchKind.Save -> doorText tweak.Place.Text tweak.After
        | BatchKind.Revert -> ""
      Via = PushVia.token tweak.Via }

  /// Save every tweak. An evolved value is not in `Changes.Tweaks`, so it cannot be in this.
  let saveAll (changes: Changes) : BatchItem list = changes.Tweaks |> List.map (itemOf BatchKind.Save)

  /// Put the file's value back for every tweak, and for nothing else.
  let revertAll (changes: Changes) : BatchItem list = changes.Tweaks |> List.map (itemOf BatchKind.Revert)

/// The one line a strip says first: how many tweaks are not saved and how many values the program changed, each only when there is one.
let summary (changes: Changes) : string =
  let plural (n: int) (one: string) (many: string) =
    match n with
    | 1 -> one
    | _ -> many
  let tweaks =
    match changes.Tweaks.Length with
    | 0 -> []
    | n -> [ sprintf "%d %s not saved" n (plural n "tweak" "tweaks") ]
  let evolved =
    match changes.Evolved.Length with
    | 0 -> []
    | n -> [ sprintf "%d %s the program changed" n (plural n "value" "values") ]
  String.concat " · " (tweaks @ evolved)
