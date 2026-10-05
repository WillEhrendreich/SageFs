/// What a live binding's row in the dashboard knows about the source file it may be written back to, as pure data and pure
/// decisions. Stage 1 of "save live tweaked values back to source": the row says honestly where its value lives, offers the
/// knob only where a knob can work, and says WHY where it cannot. The file is written through the nudge door
/// (`SageFs.Features.Tweak.Nudge`); nothing here touches a file, a clock or a session.
///
/// MAPPING. The live-bindings data carries a binding's name, its type and its value tree. It carries no file, no line and no
/// module: FSI's bound values are the top-level `let`s of the interactions the session ran, and a file the session loaded is a
/// module whose values are not among them. So the only link between a row and a source expression is the NAME, matched against
/// what the session's own files declare (`SourceIndex`). The match never guesses: no file declaring the name, several
/// declaring it, and a file that cannot be read are three different facts (`SourceFacts`), and none of them picks a first hit.
///
/// STATE. `PersistenceState` is the one closed answer to "where does this value live, and what can I do about it". It is
/// derived from three facts (`derive`): what the live value is, what the files say, and what the last action on the row did.
/// It is exhaustive on purpose: a state added here cannot be left undrawn, because the dashboard renders it with one
/// exhaustive match.
///
/// SEAM FOR STAGE 2. Stage 2 (apply a value to the running app before saving it) adds the states that need that mechanism: a
/// tweak the file does not hold yet, a saved tweak, and a value the app copied at startup. They are NOT in this file, because
/// nothing in stage 1 can tell them apart from `DiffersFromFile`. When they arrive they join `PersistenceState` and the one
/// render in the dashboard stops compiling until it draws them.
module SageFs.Features.Tweak.BindingTweak

open System
open System.Globalization
open System.Text.RegularExpressions
open SageFs
open SageFs.Features
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge

// ── a place in a file ──

/// An expression in one of the session's files, as the door's `inspect` reported it: enough to show it, to write it (the hash
/// is the `seen` the door checks), and to say where it sits.
type SourceRef =
  { File: string
    Address: TweakAddress
    Text: string
    Hash: string
    Span: SourceSpan
    Kind: ItemKind }

module SourceRef =
  let ofItem (file: string) (item: InspectedItem) : SourceRef =
    { File = file; Address = item.Address; Text = item.Text; Hash = item.Hash; Span = item.Span; Kind = item.Kind }

  /// The address in the spelling the door takes (`Game.Tuning.tuning/{JumpVelocity}`).
  let addressText (source: SourceRef) : string = NudgeAddress.format source.Address

// ── the live side ──

/// What a row's live value IS, as far as a row can tell without running anything.
[<RequireQualifiedAccess>]
type LiveShape =
  /// A value with no parts: the text the walk printed for it.
  | Scalar of preview: string
  /// A value made of parts (a record, a list, a union with fields...). Its parts have rows of their own.
  | Container
  /// Listed, not read: reading it runs your code.
  | NotRead
  /// The walk could not say (a cycle, a cut-off branch).
  | Unreadable

module LiveShape =
  let ofNode (node: LiveValueTree.LiveValueNode) : LiveShape =
    match node.Kind, node.Children with
    | LiveValueTree.NodeKind.Leaf, _ -> LiveShape.Scalar node.Preview
    | LiveValueTree.NodeKind.NotEvaluated _, _ -> LiveShape.NotRead
    // A union case with no fields prints as its name, which is exactly how a case literal is spelled.
    | LiveValueTree.NodeKind.Union, [] -> LiveShape.Scalar node.Preview
    | LiveValueTree.NodeKind.Cycle, _
    | LiveValueTree.NodeKind.Truncated, _ -> LiveShape.Unreadable
    | _ -> LiveShape.Container

  /// How the live-values walk prints each literal kind. The walk's own printer (`LiveValueTree`'s scalar preview) is the other half
  /// of this: a literal equals the live value when it prints as the live value does.
  let printed (value: LiteralValue) : string =
    match value with
    | LiteralValue.Bool true -> "true"
    | LiteralValue.Bool false -> "false"
    | LiteralValue.Integer i -> i.ToString(CultureInfo.InvariantCulture)
    | LiteralValue.Real r -> sprintf "%g" r
    | LiteralValue.Char c -> sprintf "'%c'" c
    | LiteralValue.Text s -> sprintf "\"%s\"" s
    | LiteralValue.Case name -> name

  /// Whether the file's literal and the live value are the same value.
  let equalsLiteral (value: LiteralValue) (preview: string) : bool = String.Equals(printed value, preview, StringComparison.Ordinal)

// ── what the files say about a row ──

/// What the session's files say about ONE row. Never a guess: each way the answer can fail to be one expression is its own case.
[<RequireQualifiedAccess>]
type SourceFacts =
  /// The session's project files were read and none holds a literal, a record field or a formula under this name. (A binding the
  /// door lists no point of, such as a bare reference, cannot be told from an absent one: there is nothing in it to write.)
  | NoFileBindsIt
  /// The files could not all be read (one does not parse, the list of files is not known), so "no file" cannot be said.
  | FilesUnknown of reason: string
  /// Exactly one expression.
  | OneSource of SourceRef
  /// Two or more places declare it (two files, or the same name twice in a script). The row names every one and picks none.
  | ManySources of SourceRef list
  /// The binding is in one file, and this part of its value is not an expression the file spells (a record field filled by a call
  /// that is not a literal or a formula the address model reaches).
  | PartNotSpelled of owner: SourceRef

/// Where a row's single source is, when it has exactly one.
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
    | SourceFacts.PartNotSpelled _ -> PlaceOf.NotPlaced

/// A file the session owns, inspected through the door. `Items` is the door's listing of the file's tweakable points: every
/// literal, and every record field. A binding whose right-hand side is not itself a literal (a record, a formula) has no item
/// of its own in that listing, so its whole right-hand side is read separately (`Wholes`) for the names the pane is asked about.
type FileInspection =
  { File: string
    Watching: WatchStatus
    Items: SourceRef list
    Wholes: SourceRef list
    UndoSteps: int
    RedoSteps: int }

/// The session's inspected files, and the files that could not be read. Built by the shell that does the reading.
type SourceIndex =
  { Files: FileInspection list
    Unreadable: (string * string) list }

/// One declaration of a name in one file and module: the expressions that are its whole right-hand side (none when it has not been read).
type Declaration =
  { File: string
    ModulePath: string list
    Owners: SourceRef list }

module SourceIndex =
  let empty : SourceIndex = { Files = []; Unreadable = [] }

  /// The whole right-hand sides still to be read: for each of `names`, every binding the listing shows a part of but not the whole
  /// of. The shell asks the door for these addresses (one `inspect` each) and puts the answers in `Wholes`.
  let wholesToRead (names: Set<string>) (items: SourceRef list) : TweakAddress list =
    let wholeKeys =
      items
      |> List.filter (fun r -> List.isEmpty r.Address.Path)
      |> List.map (fun r -> r.Address.ModulePath, r.Address.BindingName)
      |> Set.ofList
    items
    |> List.filter (fun r -> Set.contains r.Address.BindingName names)
    |> List.map (fun r -> r.Address.ModulePath, r.Address.BindingName)
    |> List.distinct
    |> List.filter (fun key -> not (Set.contains key wholeKeys))
    |> List.map (fun (modulePath, name) -> { ModulePath = modulePath; BindingName = name; Path = [] })

  let private unreadableReason (index: SourceIndex) : string =
    index.Unreadable |> List.map (fun (file, why) -> sprintf "%s: %s" file why) |> String.concat "; "

  /// Every place a name is declared, by file and module. A name declared twice in one script shows as two owners, told apart by where they sit.
  let declarations (index: SourceIndex) (binding: string) : Declaration list =
    index.Files
    |> List.collect (fun file ->
      let named = (file.Items @ file.Wholes) |> List.filter (fun r -> r.Address.BindingName = binding)
      named
      |> List.map (fun r -> r.Address.ModulePath)
      |> List.distinct
      |> List.map (fun modulePath ->
        let owners =
          named
          |> List.filter (fun r -> r.Address.ModulePath = modulePath && List.isEmpty r.Address.Path)
          |> List.distinctBy (fun r -> r.Span)
        { File = file.File; ModulePath = modulePath; Owners = owners }))

  /// The facts for a top-level binding, or for a part of it reached by `steps` (record fields). The binding's own declarations decide
  /// between none, one and several; only a binding in exactly one place has parts to look up, and they are looked up in that
  /// place and no other.
  let factsFor (index: SourceIndex) (binding: string) (steps: PathStep list) : SourceFacts =
    match declarations index binding with
    | [] ->
      match index.Unreadable with
      | [] -> SourceFacts.NoFileBindsIt
      | _ -> SourceFacts.FilesUnknown(unreadableReason index)
    | declared ->
      match declared |> List.filter (fun d -> List.isEmpty d.Owners) with
      | unread :: _ -> SourceFacts.FilesUnknown(sprintf "the whole expression of %s in %s was not read" binding unread.File)
      | [] ->
        match declared |> List.collect (fun d -> d.Owners) with
        | [] -> SourceFacts.NoFileBindsIt
        // The binding itself is declared in several places, so its parts are too: every one is named and none is picked.
        | _ :: _ :: _ as several -> SourceFacts.ManySources several
        | [ owner ] ->
          match steps with
          | [] -> SourceFacts.OneSource owner
          | _ ->
            let part =
              index.Files
              |> List.filter (fun file -> String.Equals(file.File, owner.File, StringComparison.Ordinal))
              |> List.collect (fun file -> file.Items)
              |> List.filter (fun item ->
                item.Address.ModulePath = owner.Address.ModulePath
                && item.Address.BindingName = binding
                && item.Address.Path = steps)
            match part with
            | [] -> SourceFacts.PartNotSpelled owner
            | [ one ] -> SourceFacts.OneSource one
            | several -> SourceFacts.ManySources several

  let fileOf (index: SourceIndex) (file: string) : FileInspection list =
    index.Files |> List.filter (fun f -> String.Equals(f.File, file, StringComparison.Ordinal))

// ── what the row's last action did ──

[<RequireQualifiedAccess>]
type WriteKind =
  | SetTo of text: string
  | UndoStep
  | RedoStep

/// The last action on a row, as the shell recorded it. `Rejected` keeps the expression the row was showing when it was refused,
/// so a stale address can show both versions.
[<RequireQualifiedAccess>]
type RowOutcome =
  | NoOutcome
  | InFlight of WriteKind * attempted: SourceRef
  | Rejected of NudgeRefusal * attempted: SourceRef

// ── the persistence state ──

[<RequireQualifiedAccess>]
type NotInFileWhy =
  /// The value was bound in the REPL: no file of the session declares its name, so there is nothing to save it to.
  | NoOwnedFileBindsIt
  /// The files could not be read, so the pane cannot say whether one declares it.
  | OwnedFilesUnknown of reason: string
  /// The binding is in a file, and this part of its value is not spelled there as something a write can reach.
  | NotSpelledInTheFile of owner: SourceRef

[<RequireQualifiedAccess>]
type DerivedWhy =
  /// The file spells it as a formula (`gravity * 2.0`): editable as an expression, not scrubbed as a literal.
  | AFormula
  /// A value made of parts: the parts are the rows, and the whole has nothing to scrub.
  | AContainer

/// What the file holds now, when it is not what the row was showing.
[<RequireQualifiedAccess>]
type StaleNow =
  /// The expression is still there, with other text.
  | Edited of text: string * hash: string
  /// The address no longer resolves, and the same expression is at this one. Offered, never taken.
  | MovedTo of candidate: TweakAddress
  /// The binding or the path inside it is gone.
  | Gone of reason: string

type Stale = { Seen: SourceRef; Now: StaleNow }

[<RequireQualifiedAccess>]
type PersistenceState =
  /// The file has this expression and the running value is what it says.
  | InSource of SourceRef
  /// There is no file to save to. Says which of the reasons it is.
  | NotInAFile of NotInFileWhy
  /// Reading it runs your code, so the pane has not.
  | Held
  /// Computed or composed: not a leaf a knob scrubs.
  | Derived of SourceRef * DerivedWhy
  /// More than one place declares this name. Every one is listed; none is picked.
  | Ambiguous of SourceRef list
  /// A file declares it, and the REPL holds another value. Nothing here knows which came last.
  | DiffersFromFile of live: string * SourceRef
  /// The file changed under the row. Both versions, never a guess.
  | StaleAddress of Stale
  /// The door said no, with its rule and what to do next.
  | Refused of NudgeRefusal * SourceRef
  /// A write is in flight.
  | Writing of WriteKind * SourceRef

module PersistenceState =
  /// One distinct token per case, for tests and for a `data-state` attribute a browser test can read.
  let token (state: PersistenceState) : string =
    match state with
    | PersistenceState.InSource _ -> "InSource"
    | PersistenceState.NotInAFile _ -> "NotInAFile"
    | PersistenceState.Held -> "Held"
    | PersistenceState.Derived _ -> "Derived"
    | PersistenceState.Ambiguous _ -> "Ambiguous"
    | PersistenceState.DiffersFromFile _ -> "DiffersFromFile"
    | PersistenceState.StaleAddress _ -> "StaleAddress"
    | PersistenceState.Refused _ -> "Refused"
    | PersistenceState.Writing _ -> "Writing"

  /// A stale outcome stops applying when the file is back to the text the row was showing.
  let private staleStillApplies (source: SourceFacts) (attempted: SourceRef) : bool =
    match source with
    | SourceFacts.OneSource now -> now.Hash <> attempted.Hash
    | SourceFacts.NoFileBindsIt
    | SourceFacts.FilesUnknown _
    | SourceFacts.ManySources _
    | SourceFacts.PartNotSpelled _ -> true

  let private ofRefusal (refusal: NudgeRefusal) (attempted: SourceRef) : PersistenceState =
    match refusal with
    | NudgeRefusal.SourceMoved(_, actual, currentText) ->
      PersistenceState.StaleAddress { Seen = attempted; Now = StaleNow.Edited(currentText, actual) }
    | NudgeRefusal.AddressMoved(_, candidate) -> PersistenceState.StaleAddress { Seen = attempted; Now = StaleNow.MovedTo candidate }
    | NudgeRefusal.AddressGone why ->
      PersistenceState.StaleAddress { Seen = attempted; Now = StaleNow.Gone(NudgeRefusal.describeResolve why) }
    | other -> PersistenceState.Refused(other, attempted)

  let private ofSource (live: LiveShape) (source: SourceFacts) : PersistenceState =
    match source with
    | SourceFacts.NoFileBindsIt -> PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt
    | SourceFacts.FilesUnknown reason -> PersistenceState.NotInAFile(NotInFileWhy.OwnedFilesUnknown reason)
    | SourceFacts.ManySources places -> PersistenceState.Ambiguous places
    | SourceFacts.PartNotSpelled owner -> PersistenceState.NotInAFile(NotInFileWhy.NotSpelledInTheFile owner)
    | SourceFacts.OneSource place ->
      match live, place.Kind with
      | LiveShape.Container, _
      | LiveShape.Unreadable, _ -> PersistenceState.Derived(place, DerivedWhy.AContainer)
      | LiveShape.Scalar _, ItemKind.Formula -> PersistenceState.Derived(place, DerivedWhy.AFormula)
      | LiveShape.Scalar preview, ItemKind.Knob value ->
        match LiveShape.equalsLiteral value preview with
        | true -> PersistenceState.InSource place
        | false -> PersistenceState.DiffersFromFile(preview, place)
      | LiveShape.NotRead, _ -> PersistenceState.Held

  /// The state of a row, from what it is, what the files say about it and what was last done to it. A value that runs code to read
  /// is `Held` whatever else is true: nothing about it is known until someone clicks.
  let derive (live: LiveShape) (source: SourceFacts) (outcome: RowOutcome) : PersistenceState =
    match live with
    | LiveShape.NotRead -> PersistenceState.Held
    | LiveShape.Scalar _
    | LiveShape.Container
    | LiveShape.Unreadable ->
      match outcome with
      | RowOutcome.InFlight(kind, attempted) -> PersistenceState.Writing(kind, attempted)
      | RowOutcome.Rejected(refusal, attempted) ->
        match ofRefusal refusal attempted with
        | PersistenceState.StaleAddress _ when not (staleStillApplies source attempted) -> ofSource live source
        | refused -> refused
      | RowOutcome.NoOutcome -> ofSource live source

// ── steps: the knob's arithmetic ──

/// How far one step moves a number, taken from how the author spelled it: `1.0` steps by 0.1, `0.12` by 0.01, `10` by 1, `0x1F` by 1.
[<RequireQualifiedAccess>]
type Step =
  | Whole
  /// `Decimals` places after the point, and a step of one unit in the last of them.
  | Fraction of decimals: int

module Step =
  let private afterPoint = Regex(@"^-?[0-9_]*\.([0-9_]*)", RegexOptions.Compiled)

  /// The decimals a real literal spells (`1.0` is 1, `0.12` is 2, `5.` and `1e3` are 0, an underscore is not a digit).
  let decimalsOf (text: string) : int =
    let m = afterPoint.Match text
    match m.Success with
    | false -> 0
    | true -> m.Groups.[1].Value |> Seq.filter Char.IsAsciiDigit |> Seq.length

  /// The step for a real literal's text: one unit in its last decimal, and a whole unit when it has none.
  let ofRealText (text: string) : Step =
    match decimalsOf text with
    | 0 -> Step.Whole
    | places -> Step.Fraction places

  /// The size of one step, as a number.
  let size (step: Step) : float =
    match step with
    | Step.Whole -> 1.0
    | Step.Fraction places -> Math.Pow(10.0, float -places)

  /// The places a result is rounded to, so `0.1 + 0.2` is `0.3` and not `0.30000000000000004`.
  let places (step: Step) : int =
    match step with
    | Step.Whole -> 0
    | Step.Fraction p -> p

/// The control a row offers, from what the file spells. A kind without a control is the state's job to explain.
[<RequireQualifiedAccess>]
type Control =
  /// A real: a drag handle, step buttons and the arrow keys.
  | RealStepper of value: float * step: Step
  /// An integer (decimal, hex, octal or binary: the door keeps the base).
  | IntegerStepper of value: int64
  | Toggle of value: bool
  /// A string, a character or a union case: a field. It holds the VALUE the door reads back (a string without its quotes, a
  /// case by its name), because that is what the door takes.
  | LiteralField of kind: LiteralKindName * value: string
  /// A formula, edited as an F# expression.
  | ExpressionField of text: string
  | NoControl

module Control =
  let ofSource (source: SourceRef) : Control =
    match source.Kind with
    | ItemKind.Formula -> Control.ExpressionField source.Text
    | ItemKind.Knob(LiteralValue.Real value) -> Control.RealStepper(value, Step.ofRealText source.Text)
    | ItemKind.Knob(LiteralValue.Integer value) -> Control.IntegerStepper value
    | ItemKind.Knob(LiteralValue.Bool value) -> Control.Toggle value
    | ItemKind.Knob(LiteralValue.Char c as value) -> Control.LiteralField(LiteralKindName.ofValue value, string c)
    | ItemKind.Knob(LiteralValue.Text s as value) -> Control.LiteralField(LiteralKindName.ofValue value, s)
    | ItemKind.Knob(LiteralValue.Case name as value) -> Control.LiteralField(LiteralKindName.ofValue value, name)

  /// The control a state allows. A write in flight offers none (the buttons wait for the answer); a refusal and a stale address
  /// offer the control of the expression the file holds NOW, so the person can try again from what is true.
  let ofState (state: PersistenceState) (source: SourceFacts) : Control =
    let current () =
      match source with
      | SourceFacts.OneSource now -> ofSource now
      | SourceFacts.NoFileBindsIt
      | SourceFacts.FilesUnknown _
      | SourceFacts.ManySources _
      | SourceFacts.PartNotSpelled _ -> Control.NoControl
    match state with
    | PersistenceState.InSource place -> ofSource place
    | PersistenceState.Derived(place, DerivedWhy.AFormula) -> Control.ExpressionField place.Text
    | PersistenceState.Derived(_, DerivedWhy.AContainer) -> Control.NoControl
    | PersistenceState.Refused _
    | PersistenceState.StaleAddress _ -> current ()
    | PersistenceState.NotInAFile _
    | PersistenceState.Held
    | PersistenceState.Ambiguous _
    | PersistenceState.DiffersFromFile _
    | PersistenceState.Writing _ -> Control.NoControl

[<RequireQualifiedAccess>]
type StepRefusal =
  /// The step would leave the numbers a literal can spell.
  | OutOfRange

module Stepping =
  /// The literal text to send to move a number `steps` steps from where it is (negative is down). Rounded to the literal's own
  /// decimals, so the text is a number the author could have typed. The door reads it as the literal's own kind and keeps the style.
  let literalAfter (control: Control) (steps: int) : Result<string, StepRefusal> =
    match control with
    | Control.RealStepper(value, step) ->
      let moved = Math.Round(value + float steps * Step.size step, Step.places step)
      match Double.IsFinite moved with
      | true -> Ok(moved.ToString("R", CultureInfo.InvariantCulture))
      | false -> Error StepRefusal.OutOfRange
    | Control.IntegerStepper value ->
      try Ok((Checked.(+) value (int64 steps)).ToString(CultureInfo.InvariantCulture))
      with :? OverflowException -> Error StepRefusal.OutOfRange
    | Control.Toggle _
    | Control.LiteralField _
    | Control.ExpressionField _
    | Control.NoControl -> Error StepRefusal.OutOfRange

// ── the row's own history ──

/// The addresses this daemon has written in one file, newest last, and the ones undone. The door's undo is per file, so a row's
/// undo is exact only while that row's write is the newest: the trail says whose turn it is.
type FileTrail = { Applied: string list; Undone: string list }

[<RequireQualifiedAccess>]
type HistoryStep =
  | StepAvailable
  /// A later write in this file is on top; undoing it comes first.
  | OtherRowFirst of address: string
  | NothingToStep

module FileTrail =
  let empty : FileTrail = { Applied = []; Undone = [] }

  /// A write puts itself on top and ends any redo.
  let wrote (address: string) (trail: FileTrail) : FileTrail = { Applied = trail.Applied @ [ address ]; Undone = [] }

  let undid (trail: FileTrail) : FileTrail =
    match List.rev trail.Applied with
    | [] -> trail
    | newest :: olderReversed -> { Applied = List.rev olderReversed; Undone = newest :: trail.Undone }

  let redid (trail: FileTrail) : FileTrail =
    match trail.Undone with
    | [] -> trail
    | next :: rest -> { Applied = trail.Applied @ [ next ]; Undone = rest }

  let undoFor (address: string) (trail: FileTrail) : HistoryStep =
    match List.tryLast trail.Applied with
    | None -> HistoryStep.NothingToStep
    | Some newest when newest = address -> HistoryStep.StepAvailable
    | Some newest -> HistoryStep.OtherRowFirst newest

  let redoFor (address: string) (trail: FileTrail) : HistoryStep =
    match trail.Undone with
    | [] -> HistoryStep.NothingToStep
    | next :: _ when next = address -> HistoryStep.StepAvailable
    | next :: _ -> HistoryStep.OtherRowFirst next

// ── what the running app did with the write ──

/// What the row shows about the reload its last write caused. The session keeps the last verdict; a write is followed by a verdict
/// that is not the one before it. Two saves with the same verdict cannot be told apart by the verdict alone, so a write that has
/// seen no new verdict within the patience says that, instead of waiting forever.
[<RequireQualifiedAccess>]
type ReloadWatch =
  | AwaitingReload
  | Compiling
  | Reported of ReloadFacts
  | NoNewReport

[<RequireQualifiedAccess>]
type RowReload =
  | NoWriteYet
  | Watching of file: string * ReloadWatch

module ReloadWatch =
  let ofSession (baseline: SessionReload) (current: SessionReload) (waited: TimeSpan) (patience: TimeSpan) : ReloadWatch =
    match current with
    | SessionReload.Compiling _ -> ReloadWatch.Compiling
    | SessionReload.Finished facts when current <> baseline -> ReloadWatch.Reported facts
    | SessionReload.Finished _
    | SessionReload.NoReloadYet ->
      match waited >= patience with
      | true -> ReloadWatch.NoNewReport
      | false -> ReloadWatch.AwaitingReload

// ── one row, whole ──

/// Which row: a top-level binding, or a record field reached by the labels down from it.
type RowKey = { Binding: string; Labels: string list }

type RowView =
  { State: PersistenceState
    Control: Control
    /// The row's single source as it is NOW (what a write is addressed to), when it has one. It is the file's current expression
    /// even when the state is a stale or refused one about an earlier look.
    Place: PlaceOf
    Watching: WatchStatus
    Reload: RowReload
    Undo: HistoryStep
    Redo: HistoryStep }

/// Every row the pane has something to say about. A row not in the map has nothing to say (a part of a REPL-only value: its binding says it).
type TweakView = { Rows: Map<RowKey, RowView> }

module TweakView =
  let none : TweakView = { Rows = Map.empty }
  let tryRow (view: TweakView) (key: RowKey) : RowView option = Map.tryFind key view.Rows
