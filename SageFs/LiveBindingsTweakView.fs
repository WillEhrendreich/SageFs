/// How a live binding's row shows what it knows about its file, and the knob it offers. Stage 1 of saving tweaked values back to
/// source: the persistence state (`BindingTweak.PersistenceState`) is said in words by ONE exhaustive function, so a state cannot be
/// added without deciding how it reads, and a row that cannot be tweaked says WHY instead of showing a control that does nothing.
///
/// The controls are Datastar's typed helpers and nothing else. The client counts steps (a drag, the step buttons and the arrow
/// keys are all just a count in one signal per row), the page waits for the settle (one write per settle, with the hash the row
/// last showed), and the daemon turns the count into the literal with the same pure function the tests pin
/// (`BindingTweak.Stepping.literalAfter`). What the person is dragging lives in a signal, so the one `#main` morph never undoes
/// it; what the file holds is the server's, so the morph after a write shows the truth.
module SageFs.Server.LiveBindingsTweakView

open System
open System.Globalization
open System.IO
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Features
open SageFs.Features.Tweak
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Server.DashboardFragments

// ── the page's signals ──

/// The signals a row's control stages before it posts, and the names of a row's own. Per-row names are written from the row's text
/// as lowercase hex, so two different rows never share one (a/b_c and a_b/c do not collide).
module TweakSignals =
  let [<Literal>] Row = "twRow"
  let [<Literal>] File = "twFile"
  let [<Literal>] Address = "twAddress"
  let [<Literal>] Seen = "twSeen"
  let [<Literal>] SeenText = "twSeenText"
  let [<Literal>] Verb = "twVerb"
  let [<Literal>] Value = "twValue"
  /// Where the knob posts. A dashboard route, because the page posts to the origin it was served from.
  let [<Literal>] Endpoint = "/dashboard/tweak"
  /// How many steps a drag, the buttons or the keys may count from where the row stands, either way.
  let [<Literal>] MostSteps = 100

  /// The initial value of every staging signal, for the page shell.
  let initial : XmlAttribute list =
    [ Ds.signal (Row, "")
      Ds.signal (File, "")
      Ds.signal (Address, "")
      Ds.signal (Seen, "")
      Ds.signal (SeenText, "")
      Ds.signal (Verb, "")
      Ds.signal (Value, "") ]

  let hexOf (text: string) : string = Convert.ToHexStringLower(Text.Encoding.UTF8.GetBytes text)

  let named (prefix: string) (key: RowKey) : string = sprintf "%s_%s" prefix (hexOf (RowKey.text key))

  let stepsOf (key: RowKey) : string = named "twSteps" key
  let fieldOf (key: RowKey) : string = named "twField" key
  let busyOf (key: RowKey) : string = named "twBusy" key
  let stripId (key: RowKey) : string = named "tw" key
  let sliderId (key: RowKey) : string = named "twr" key
  let readoutId (key: RowKey) : string = named "twv" key

// ── said in words ──

[<RequireQualifiedAccess>]
type Tone =
  | Good
  | Notice
  | Bad
  | Quiet
  /// Something to know that is not a problem and not a warning: the program moved a value on its own.
  | Info

module Tone =
  /// One class per tone, from one exhaustive function.
  let cssClass (tone: Tone) : string =
    match tone with
    | Tone.Good -> "live-tone-good"
    | Tone.Info -> "live-tone-info"
    | Tone.Notice -> "live-tone-notice"
    | Tone.Bad -> "live-tone-bad"
    | Tone.Quiet -> "live-tone-quiet"

type Described = { Label: string; Tone: Tone; Detail: string }

/// Where an expression sits, for a person: the file's name and the line.
let whereOf (place: SourceRef) : string =
  let name = Path.GetFileName place.File
  match place.Span.Line with
  | 0 -> name
  | line -> sprintf "%s:%d" name line

/// Where an expression sits, in a sentence: the file's name and the line, spelled out (`Tuning.fs line 3`).
let whereInWords (place: SourceRef) : string =
  let name = Path.GetFileName place.File
  match place.Span.Line with
  | 0 -> name
  | line -> sprintf "%s line %d" name line

/// A hash, short enough to read and long enough to compare.
let shortHash (hash: string) : string = if hash.Length > 8 then hash.Substring(0, 8) else hash

/// The words on a badge. One function, so the badge on a row and the line that explains it in the legend are the same text.
let tagLabel (tag: StateTag) : string =
  match tag with
  | StateTag.InSource -> "in the file"
  | StateTag.NotInAFile -> "not in a file"
  | StateTag.FilesUnknown -> "files unknown"
  | StateTag.NotSpelled -> "not spelled in the file"
  | StateTag.Held -> "held"
  | StateTag.Formula -> "formula"
  | StateTag.MadeOfParts -> "has parts"
  | StateTag.Ambiguous -> "ambiguous"
  | StateTag.Tweaked -> "tweaked"
  | StateTag.Evolved -> "evolved"
  // What is true about the running app, not a comparison: "differs from the file" sat over a sentence that said "Saved to the
  // file", and read as the file not having been written.
  // "Not running" read as the program being stopped (two reviewers), so the tag says the file's value has not been applied.
  | StateTag.DiffersFromFile -> "saved, not applied"
  | StateTag.FileChanged -> "file changed"
  | StateTag.Refused -> "refused"
  | StateTag.Writing -> "writing"

/// What a badge means, in one plain line: the badge's title and its line in the legend.
let tagMeaning (tag: StateTag) : string =
  match tag with
  | StateTag.InSource -> "A file holds this value, and the running program has the same one."
  | StateTag.NotInAFile -> "No file of the project defines this value. It exists only in the session, so there is nothing to edit."
  | StateTag.FilesUnknown -> "The project's files could not all be read, so the pane cannot tell whether a file defines this."
  | StateTag.NotSpelled -> "This part of the value is not written out in the file, so there is nothing here to edit."
  | StateTag.Held -> "Reading this would run your code, so it has not been read. The run button reads it."
  | StateTag.Formula -> "The file works this value out from other values. You can edit the formula."
  | StateTag.MadeOfParts -> "This value is made of smaller values. Each one is a row of its own."
  | StateTag.Ambiguous -> "More than one place in the files defines this name, so the pane will not pick one."
  | StateTag.Tweaked -> "The running app holds a value that is not in the file yet."
  | StateTag.Evolved -> "The program changed this value itself after it was set."
  | StateTag.DiffersFromFile -> "The file and the running program hold different values."
  | StateTag.FileChanged -> "The file changed after this row was drawn, so nothing was written."
  | StateTag.Refused -> "The last edit was refused. The sentence on the row says why."
  | StateTag.Writing -> "An edit is being written to the file."

/// The one exhaustive saying of a state: a short label for the chip, a tone, and a sentence that says what is true and what to do.
let describe (state: PersistenceState) : Described =
  match state with
  | PersistenceState.InSource place ->
    { Label = tagLabel StateTag.InSource
      Tone = Tone.Good
      Detail = sprintf "%s is %s in %s. A change here is written to that file." (SourceRef.addressText place) place.Text (whereOf place) }
  | PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt ->
    { Label = tagLabel StateTag.NotInAFile
      Tone = Tone.Quiet
      Detail = "Bound in the REPL. No file of this session holds a literal, a record field or a formula by this name, so there is nothing to save it to." }
  | PersistenceState.NotInAFile(NotInFileWhy.OwnedFilesUnknown reason) ->
    { Label = tagLabel StateTag.FilesUnknown
      Tone = Tone.Notice
      Detail =
        sprintf "The session's files could not all be read, so the pane cannot say whether one holds this. The problem: %s. This row has no control until the files read cleanly, so fix the file it names." reason }
  | PersistenceState.NotInAFile(NotInFileWhy.NotSpelledInTheFile owner) ->
    { Label = tagLabel StateTag.NotSpelled
      Tone = Tone.Quiet
      Detail = sprintf "%s is in %s, but this part is not a literal or a record field the file spells, so there is nothing here to write." (SourceRef.addressText owner) (whereOf owner) }
  | PersistenceState.Held ->
    { Label = tagLabel StateTag.Held
      Tone = Tone.Quiet
      Detail = "Reading it runs your code, so it has not been read. Run it with the button, under a deadline." }
  | PersistenceState.Derived(place, DerivedWhy.AFormula) ->
    { Label = tagLabel StateTag.Formula
      Tone = Tone.Quiet
      // One short line: the field beside it already shows the expression and the column beside that names the file.
      Detail = sprintf "Worked out from other values in %s. Edit the formula; it is not type-checked, so a reload reports an error." (whereOf place) }
  | PersistenceState.Derived(place, DerivedWhy.AContainer) ->
    { Label = tagLabel StateTag.MadeOfParts
      Tone = Tone.Quiet
      Detail = sprintf "%s in %s. Its parts are the rows below: the ones the file spells as literals can be changed." (SourceRef.addressText place) (whereOf place) }
  | PersistenceState.Ambiguous places ->
    let where = places |> List.map whereOf |> String.concat ", "
    { Label = tagLabel StateTag.Ambiguous
      Tone = Tone.Bad
      Detail =
        sprintf "%d places declare this name: %s. The pane will not pick one. Remove or rename all but one, or edit the file by hand." places.Length where }
  | PersistenceState.Tweaked(live, place) ->
    { Label = tagLabel StateTag.Tweaked
      Tone = Tone.Notice
      Detail =
        sprintf "The running app holds %s, which was pushed into it, and %s still says %s. The file does not have the tweak yet." live (whereOf place) place.Text }
  | PersistenceState.Evolved(live, EvolvedBy.OvertookTweak pushed, place) ->
    { Label = tagLabel StateTag.Evolved
      Tone = Tone.Info
      Detail =
        sprintf "You pushed %s, and the program has since changed it to %s. %s still says %s. The pane does not write the program's value into the file, because that would record the program's change as your decision." pushed live (whereInWords place) place.Text }
  | PersistenceState.Evolved(live, EvolvedBy.MovedWhileRunning, place) ->
    { Label = tagLabel StateTag.Evolved
      Tone = Tone.Info
      Detail =
        sprintf "The running value is %s and %s says %s: something assigned it while the app ran. The pane does not write that value into the file, because it would record the program's change as your decision." live (whereInWords place) place.Text }
  | PersistenceState.DiffersFromFile(live, place) ->
    { Label = tagLabel StateTag.DiffersFromFile
      Tone = Tone.Notice
      Detail = sprintf "%s holds %s. The running program has %s." (whereInWords place) place.Text live }
  | PersistenceState.StaleAddress stale ->
    let nowText =
      match stale.Now with
      | StaleNow.Edited(text, _) -> sprintf "the file now holds %s" text
      | StaleNow.MovedTo candidate -> sprintf "the same expression is now at %s" (NudgeAddress.format candidate)
      | StaleNow.Gone reason -> sprintf "that expression is gone: %s" reason
    { Label = tagLabel StateTag.FileChanged
      Tone = Tone.Bad
      Detail = sprintf "The file changed after this row was drawn. The row showed %s, and %s. Nothing was written." stale.Seen.Text nowText }
  | PersistenceState.Refused(refusal, _) ->
    { Label = tagLabel StateTag.Refused
      Tone = Tone.Bad
      Detail = sprintf "%s %s" (NudgeRefusal.rule refusal) (NudgeRefusal.nextAction refusal) }
  | PersistenceState.Writing(kind, place) ->
    let what =
      match kind with
      | WriteKind.SetTo text -> sprintf "Setting %s to %s" (SourceRef.addressText place) text
      | WriteKind.UndoStep -> sprintf "Undoing the last write in %s" (Path.GetFileName place.File)
      | WriteKind.RedoStep -> sprintf "Putting the undone write back in %s" (Path.GetFileName place.File)
    { Label = tagLabel StateTag.Writing
      Tone = Tone.Notice
      Detail = sprintf "%s in %s…" what (whereOf place) }

/// Whether the numbers on a row need a word each. A row whose running value and file value agree shows one number and needs none. A
/// row where they differ shows two: the value in the heading is what the running program holds and the value in the knob is what the
/// file holds, and each carries its word, so which number the program uses is said in one place and never left to be guessed.
[<RequireQualifiedAccess>]
type NumberLabels =
  | OneNumber
  | RunningAndFile

module NumberLabels =
  let ofState (state: PersistenceState) : NumberLabels =
    match state with
    | PersistenceState.Tweaked _
    | PersistenceState.Evolved _
    | PersistenceState.DiffersFromFile _ -> NumberLabels.RunningAndFile
    | PersistenceState.InSource _
    | PersistenceState.NotInAFile _
    | PersistenceState.Held
    | PersistenceState.Derived _
    | PersistenceState.Ambiguous _
    | PersistenceState.StaleAddress _
    | PersistenceState.Refused _
    | PersistenceState.Writing _ -> NumberLabels.OneNumber

  /// The word beside the value in the heading.
  let runningWord : string = "running"
  /// The word beside the number in the knob.
  let fileWord : string = "in the file"
  let runningMeaning : string = "The value the running program holds."
  let fileMeaning : string = "The value in the file. The control below changes it."

/// What a person can do next about a row, in terms of the controls that row really draws. Each case names a control that is on the
/// screen or says plainly that none is, so a sentence built from it never points at a control that does not exist.
[<RequireQualifiedAccess>]
type NextStep =
  | NoNextStep
  /// The Undo button on the row takes back the last write. To run the file's value instead, the person evaluates the binding again or
  /// uses Rebuild and reload on the session card.
  | UndoOrReload
  /// Nothing on this page applies the file's value to the running program: the person evaluates the binding again or uses Rebuild and
  /// reload on the session card.
  | ReloadOnly
  /// The control on the row sets what the file says.
  | UseTheControl
  /// The control on the row now starts from what the file holds.
  | UseTheControlAgain

module NextStep =
  /// The next step for a row, from its state and the controls it draws now.
  let ofRow (view: RowView) : NextStep =
    let knob = TweakView.rowHasKnob view
    match view.State with
    | PersistenceState.DiffersFromFile _ ->
      match view.Undo with
      | HistoryStep.StepAvailable -> NextStep.UndoOrReload
      | HistoryStep.OtherRowFirst _
      | HistoryStep.NothingToStep -> NextStep.ReloadOnly
    | PersistenceState.Tweaked _
    | PersistenceState.Evolved _ -> if knob then NextStep.UseTheControl else NextStep.NoNextStep
    | PersistenceState.StaleAddress _ -> if knob then NextStep.UseTheControlAgain else NextStep.NoNextStep
    | PersistenceState.InSource _
    | PersistenceState.NotInAFile _
    | PersistenceState.Held
    | PersistenceState.Derived _
    | PersistenceState.Ambiguous _
    | PersistenceState.Refused _
    | PersistenceState.Writing _ -> NextStep.NoNextStep

  let sentence (step: NextStep) : string =
    match step with
    | NextStep.NoNextStep -> ""
    // One remedy in plain words. The Undo button, when there is one, is on the row and says what it does, so the sentence does not
    // explain it a second time.
    | NextStep.UndoOrReload
    | NextStep.ReloadOnly ->
      "To run the file's value, evaluate the binding again with λ Evaluate, or press Rebuild and reload."
    | NextStep.UseTheControl -> "The control on this row sets what the file says."
    | NextStep.UseTheControlAgain -> "The control on this row now starts from what the file holds. Change it again from there."

/// Why a row's file value cannot be applied to the session by evaluating it.
[<RequireQualifiedAccess>]
type ApplyRefusal =
  /// A record field or list item is not a binding of its own, so evaluating it alone would not rebind anything.
  | PartOfAValue
  | SpansLines
  /// A `;;` inside the text would end the statement before the expression does.
  | CarriesStatementEnd

module ApplyRefusal =
  let text (refusal: ApplyRefusal) : string =
    match refusal with
    | ApplyRefusal.PartOfAValue -> "A part of a value cannot be applied alone. Evaluate the whole binding, or press Rebuild and reload."
    | ApplyRefusal.SpansLines -> "The file's expression spans lines, so it cannot be evaluated from here. Evaluate the binding yourself, or press Rebuild and reload."
    | ApplyRefusal.CarriesStatementEnd -> "The file's expression contains ';;', so it cannot be evaluated from here. Press Rebuild and reload."

/// Whether a row offers to apply the file's value to the running session, and the code that would.
[<RequireQualifiedAccess>]
type ApplyOffer =
  | Offered of code: string
  | NotOffered

/// A file nothing watches never reaches the session, so the row offers to evaluate what the file holds. This is what a REPL session
/// wants: its bindings are not compiled code that hot reload can patch, they are values the session was handed, and rebinding the
/// name to the file's expression is how the session takes the file's value.
module ApplyToSession =
  /// Where the button posts. Its own route, because it evaluates code in the session and writes nothing to the file.
  let [<Literal>] Endpoint = "/dashboard/tweak/apply"
  let words : string = "Apply to this session"
  /// The row's one next step when the button is offered: the button is the step, so the sentence points at it and at nothing else.
  let sentence : string = "To run the file's value, press Apply to this session."

  /// The statement that rebinds the row's name to the file's expression.
  let codeFor (key: RowKey) (fileText: string) : Result<string, ApplyRefusal> =
    let text = fileText.Trim()
    match key.Labels, text.Contains "\n", text.Contains ";;" with
    | _ :: _, _, _ -> Error ApplyRefusal.PartOfAValue
    | [], true, _ -> Error ApplyRefusal.SpansLines
    | [], false, true -> Error ApplyRefusal.CarriesStatementEnd
    | [], false, false -> Ok(sprintf "let %s = %s;;" key.Binding text)

  let offerFor (key: RowKey) (view: RowView) : ApplyOffer =
    match view.State with
    | PersistenceState.DiffersFromFile(_, file) ->
      match view.Watching, view.Place with
      | WatchStatus.NotWatched, PlaceOf.Placed _ ->
        match codeFor key file.Text with
        | Ok code -> ApplyOffer.Offered code
        | Error _ -> ApplyOffer.NotOffered
      | WatchStatus.NotWatched, PlaceOf.NotPlaced
      | WatchStatus.Watched, _ -> ApplyOffer.NotOffered
    | PersistenceState.InSource _
    | PersistenceState.NotInAFile _
    | PersistenceState.Held
    | PersistenceState.Derived _
    | PersistenceState.Ambiguous _
    | PersistenceState.Tweaked _
    | PersistenceState.Evolved _
    | PersistenceState.StaleAddress _
    | PersistenceState.Refused _
    | PersistenceState.Writing _ -> ApplyOffer.NotOffered

/// What a row owes to a call, said once: which callee builds the value and how many places call it, or why the call was not
/// followed. A row not built by a call, and a right-hand side that is not a call at all, say nothing. A write here is not a problem,
/// only something to know, so it takes the notice colour and never the warning one.
let describeThrough (through: ThroughCall) : Described option =
  let said (tone: Tone) (detail: string) : Described option = Some { Label = "call"; Tone = tone; Detail = detail }
  match through with
  | ThroughCall.NoCall -> None
  | ThroughCall.Through followed ->
    match followed.Callers with
    | CallerCount.Counted 1 ->
      said Tone.Quiet (sprintf "%s builds this value. Only one place calls %s, so changing it changes nothing else." followed.Callee followed.Callee)
    | CallerCount.Counted 0 ->
      said Tone.Info (sprintf "%s builds this value. No call to %s was found in the files, so changing it changes whatever calls it." followed.Callee followed.Callee)
    | CallerCount.Counted n ->
      said Tone.Info (sprintf "%s builds this value. %d places call %s, so changing it changes all of them." followed.Callee n followed.Callee)
    | CallerCount.NotCounted reason ->
      said Tone.Info (sprintf "%s builds this value. Every place that calls %s gets the change. The calls could not be counted: %s" followed.Callee followed.Callee reason)
  | ThroughCall.CouldNotFollow refusal ->
    match refusal with
    | Refusal.NotAPlainCall -> None
    | Refusal.NoCalleeInFiles name ->
      said Tone.Quiet (sprintf "Built by %s, which no file of the session declares, so there is nothing here to change." name)
    | Refusal.CalleeFilesUnknown reason ->
      said Tone.Quiet (sprintf "Built by a function call, and the session's files could not all be read, so the function cannot be found: %s" reason)
    | Refusal.CalleeDeclaredSeveralTimes(name, places) ->
      said Tone.Quiet (sprintf "%d places declare %s: %s. The pane will not pick one, so there is nothing to change here." places.Length name (places |> List.map whereOf |> String.concat ", "))
    | Refusal.CalleeNotRead(name, file) ->
      said Tone.Quiet (sprintf "%s is declared in %s, which has not been read, so what it builds is not known." name (Path.GetFileName file))
    | Refusal.ResultNotSpelled callee ->
      said Tone.Quiet (sprintf "%s (%s) does not spell this part as a plain value, so there is nothing to change here." callee.Address.BindingName (whereOf callee))
    | Refusal.ResultIsAnExpression expression ->
      said Tone.Quiet (sprintf "%s spells this part as %s (%s), an expression that can use the call's arguments, so there is no plain value to change." expression.Address.BindingName expression.Text (whereOf expression))

/// Whether a state's sentence is written under the row. A value that is simply in its file, and a record that is made of the rows
/// below it, are fine as they are: their sentence is the chip's tooltip, so a long tree is not a wall of repeated text. Everything
/// a person has to act on or understand (why there is no knob, what differs, what was refused) is on the row.
[<RequireQualifiedAccess>]
type DetailShown =
  | OnTheRow
  | InTheTooltipOnly

let detailShown (state: PersistenceState) : DetailShown =
  match state with
  // A row that differs from its file labels both numbers ("running", "file") and says the next step in its own line, so its sentence
  // would say a third time what the row already shows. A formula's expression is in the field beside its tag.
  | PersistenceState.InSource _
  | PersistenceState.DiffersFromFile _
  | PersistenceState.Derived(_, DerivedWhy.AFormula)
  | PersistenceState.Derived(_, DerivedWhy.AContainer) -> DetailShown.InTheTooltipOnly
  | PersistenceState.NotInAFile _
  | PersistenceState.Held
  | PersistenceState.Ambiguous _
  | PersistenceState.Tweaked _
  | PersistenceState.Evolved _
  | PersistenceState.StaleAddress _
  | PersistenceState.Refused _
  | PersistenceState.Writing _ -> DetailShown.OnTheRow

/// Whether a row draws a strip under it, or only a marker. A binding no file holds is the common case in a REPL session, and the
/// title bar says it once with a count, so a strip saying it on every row would cost a line each. The marker keeps the state
/// readable (`data-state`) for a browser test or an editor, and keeps the id the morph finds the row by when the state changes.
[<RequireQualifiedAccess>]
type StripShown =
  | Strip
  | Marker

let stripShown (state: PersistenceState) : StripShown =
  match state with
  | PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt -> StripShown.Marker
  | PersistenceState.InSource _
  | PersistenceState.NotInAFile(NotInFileWhy.OwnedFilesUnknown _)
  | PersistenceState.NotInAFile(NotInFileWhy.NotSpelledInTheFile _)
  | PersistenceState.Held
  | PersistenceState.Derived _
  | PersistenceState.Ambiguous _
  | PersistenceState.Tweaked _
  | PersistenceState.Evolved _
  | PersistenceState.DiffersFromFile _
  | PersistenceState.StaleAddress _
  | PersistenceState.Refused _
  | PersistenceState.Writing _ -> StripShown.Strip

/// Where a row's badge is drawn. A row whose strip is drawn carries its badge in the strip. A row whose strip is only a marker draws
/// no strip text at all, so its badge goes on the row's own line: every row the chip in the title bar counts shows the badge the
/// chip is talking about, and the badge costs no line of its own.
[<RequireQualifiedAccess>]
type BadgePlace =
  | InTheStrip
  | OnTheRow

let badgePlace (state: PersistenceState) : BadgePlace =
  match stripShown state with
  | StripShown.Strip -> BadgePlace.InTheStrip
  | StripShown.Marker -> BadgePlace.OnTheRow

/// The title a badge carries: what the badge means in plain words, and for a state whose sentence is not drawn on the row, that
/// sentence too, so the row's own facts stay one hover away.
let badgeTitle (state: PersistenceState) : string =
  let meaning = tagMeaning (StateTag.ofState state)
  match detailShown state with
  | DetailShown.OnTheRow -> meaning
  | DetailShown.InTheTooltipOnly -> sprintf "%s %s" meaning (describe state).Detail

[<RequireQualifiedAccess>]
type ReloadLine =
  | NoLine
  /// The line, its tone, and the longer text for the tooltip (the worker's own wording of what happened and what to do).
  | Line of text: string * Tone * more: string

/// What a reload verdict comes to for the one value that was written, as a person would tell it. Two verdicts that a person cannot
/// tell apart are one situation with one sentence: a patch that is applied and has not been seen running yet (`PatchPending`) and
/// one that is applied and never ran (`NeverEntered`) both leave the app showing the old value, and the verdict's own name is the
/// tooltip. Every situation speaks about the running app, never about the session's build, which the header speaks for.
[<RequireQualifiedAccess>]
type ReloadSituation =
  | AppRunsIt
  | PatchedNotRun
  | AppRestarted
  | NothingChanged
  | NeedsRestart
  | DidNotCompile
  | EvalDidNotFinish
  | LiveValueKept

module ReloadSituation =
  let ofCase (case: ReloadCase) : ReloadSituation =
    match case with
    | ReloadCase.Patched -> ReloadSituation.AppRunsIt
    | ReloadCase.PatchPending
    | ReloadCase.NeverEntered -> ReloadSituation.PatchedNotRun
    | ReloadCase.Restarted -> ReloadSituation.AppRestarted
    | ReloadCase.NoEffect -> ReloadSituation.NothingChanged
    | ReloadCase.RestartRequired -> ReloadSituation.NeedsRestart
    | ReloadCase.CompileFailed -> ReloadSituation.DidNotCompile
    | ReloadCase.EvalFailed -> ReloadSituation.EvalDidNotFinish
    | ReloadCase.KeptLiveState -> ReloadSituation.LiveValueKept

  /// The one sentence for a situation, and its tone. Each begins with what the write did (it is in the file), then what hot reload did
  /// with it in the running app, and ends with what to do when something has to be done.
  let sentence (situation: ReloadSituation) : string * Tone =
    match situation with
    | ReloadSituation.AppRunsIt -> "Saved to the file, and the running app is using the change.", Tone.Good
    | ReloadSituation.PatchedNotRun ->
      "Saved to the file. Hot reload patched the app, but the patched code has not run yet, so the app still shows the old value.", Tone.Notice
    | ReloadSituation.AppRestarted -> "Saved to the file. Hot reload restarted the app to take it.", Tone.Notice
    | ReloadSituation.NothingChanged -> "Saved to the file. Hot reload found nothing in the running app to change.", Tone.Quiet
    | ReloadSituation.NeedsRestart -> "Saved to the file, but the app has to be restarted to take it. Restart the app.", Tone.Bad
    | ReloadSituation.DidNotCompile -> "Saved to the file, but it did not compile, so the app keeps its last working code. Fix the file or undo the write.", Tone.Bad
    | ReloadSituation.EvalDidNotFinish ->
      "Saved to the file, but evaluating it did not finish, so the app keeps its last working code. Fix the file or undo the write.", Tone.Bad
    | ReloadSituation.LiveValueKept -> "Saved to the file. A live value was kept instead of being reset.", Tone.Notice

/// What a reload verdict means for the one value that was written, as a sentence a person reads without knowing the verdict's name.
let reloadMeaning (case: ReloadCase) : string * Tone = ReloadSituation.sentence (ReloadSituation.ofCase case)

/// What the app did with the row's write, in words. The sentence says what happened and never names the verdict's case; the case, the
/// declarations a patch named and the worker's own longer wording are the tooltip.
let describeReload (reload: RowReload) : ReloadLine =
  match reload with
  | RowReload.NoWriteYet -> ReloadLine.NoLine
  | RowReload.Watching(_, ReloadWatch.AwaitingReload) -> ReloadLine.Line("Saved to the file. Waiting for the app to take it.", Tone.Quiet, "")
  | RowReload.Watching(_, ReloadWatch.Compiling) -> ReloadLine.Line("Saved to the file. Hot reload is compiling it.", Tone.Notice, "")
  | RowReload.Watching(_, ReloadWatch.NoNewReport) ->
    ReloadLine.Line(
      "Saved to the file. Hot reload has not said whether the app took it.",
      Tone.Quiet,
      "Hot reload may not be running, or its verdict is the same as the last one's, which cannot be told apart from no verdict."
    )
  | RowReload.Watching(_, ReloadWatch.Reported facts) ->
    let named =
      match facts.Declarations with
      | [] -> ""
      | declarations -> sprintf "Changed: %s. " (String.concat ", " (declarations |> List.truncate 3))
    let meaning, tone = reloadMeaning facts.Case
    ReloadLine.Line(meaning, tone, sprintf "%s: %s%s %s" (ReloadCase.token facts.Case) named facts.Message facts.SuggestedAction)

// ── script text ──

/// The inside of a single-quoted JS string literal for text that came from a file or a person. A newline, a quote, a backslash and
/// the two line separators JS treats as line ends are all escaped, so the literal is always one line and always the same text.
let jsInner (text: string) : string =
  text
    .Replace("\\", "\\\\")
    .Replace("'", "\\'")
    .Replace("\n", "\\n")
    .Replace("\r", "\\r")
    .Replace("\t", "\\t")
    .Replace(string (char 0x2028), "\\u2028")
    .Replace(string (char 0x2029), "\\u2029")

/// A single-quoted JS string literal for that text.
let jsText (text: string) : string = "'" + jsInner text + "'"

/// A signal's initial value from text that came from a file or a person. Falco's `Ds.signal` writes a string as `'value'` and
/// escapes nothing, so a quote in the text would end the attribute (and a `'` the string): the text goes in as the inside of a JS
/// literal, escaped once for the script and once for the attribute.
let signalFromText (name: string) (text: string) : XmlAttribute = Ds.signal (name, attrEnc (jsInner text))

// ── the controls ──

[<RequireQualifiedAccess>]
type Placement =
  /// A row with no parts: the controls can be clicked.
  | OnLeafRow
  /// Inside a `<summary>`: a click there toggles the details, so the state is said and nothing is offered.
  | InsideSummary

let invariant (value: float) : string = value.ToString("R", CultureInfo.InvariantCulture)

/// The script that stages a row's request in the page's signals and posts it to `endpoint`. `valueJs` is a JS expression for the value.
let stagePostTo (endpoint: string) (key: RowKey) (place: SourceRef) (verb: string) (valueJs: string) : string =
  sprintf
    "$%s = %s; $%s = %s; $%s = %s; $%s = %s; $%s = %s; $%s = %s; $%s = %s; %s"
    TweakSignals.Row (jsText (RowKey.text key))
    TweakSignals.File (jsText place.File)
    TweakSignals.Address (jsText (SourceRef.addressText place))
    TweakSignals.Seen (jsText place.Hash)
    TweakSignals.SeenText (jsText place.Text)
    TweakSignals.Verb (jsText verb)
    TweakSignals.Value valueJs
    (Ds.post endpoint)

/// The script for the knob's own route.
let stagePostJs (key: RowKey) (place: SourceRef) (verb: string) (valueJs: string) : string =
  stagePostTo TweakSignals.Endpoint key place verb valueJs

/// The same, as an attribute value: the script is escaped once, here.
let stageAndPost (key: RowKey) (place: SourceRef) (verb: string) (valueJs: string) : string =
  attrEnc (stagePostJs key place verb valueJs)

/// One write per settle: the handler runs once, a moment after the last change event, not on every one. Datastar's own modifier
/// (`data-on:change__debounce.300ms`), written by the typed helper. The pause is the one the as-you-type typecheck waits after
/// the last keystroke (`Timeouts.liveTestFcsDebounce`): short enough to feel direct, long enough that five quick presses are one write.
let settled : OnEventModifier list =
  [ OnEventModifier.Debounce { TimeSpan = Timeouts.liveTestFcsDebounce; Leading = false; NoTrailing = false } ]

let stepButton (key: RowKey) (glyph: string) (label: string) (testName: string) (delta: int) : XmlNode =
  Elem.button
    [ Attr.class' "session-btn live-step"
      Attr.type' "button"
      testid testName
      Attr.create "aria-label" (attrEnc label)
      Attr.title (attrEnc label)
      Ds.attr' ("disabled", sprintf "$%s" (TweakSignals.busyOf key))
      Ds.onClick (
        attrEnc (
          sprintf
            "$%s = Math.max(-%d, Math.min(%d, Number($%s || 0) + (%d))); document.getElementById(%s).dispatchEvent(new Event('change'))"
            (TweakSignals.stepsOf key) TweakSignals.MostSteps TweakSignals.MostSteps (TweakSignals.stepsOf key) delta
            (jsText (TweakSignals.sliderId key))
        )
      ) ]
    [ Text.raw glyph ]

/// A number: a drag handle that counts steps from where the row stands, the two step buttons, and a readout that shows where the
/// count would land. The arrow keys are the range input's own. One write per settle.
let stepper (key: RowKey) (place: SourceRef) (current: string) (landing: string) (label: string) : XmlNode =
  let steps = TweakSignals.stepsOf key
  Elem.span
    [ Attr.class' "live-knob"
      Attr.create "role" "group"
      Attr.create "aria-label" (attrEnc (sprintf "Change %s" label))
      Ds.signal (steps, 0) ]
    [ stepButton key "−" (sprintf "Step %s down" label) "tweak-step-down" -1
      Elem.input
        [ Attr.id (TweakSignals.sliderId key)
          Attr.class' "live-slider"
          Attr.type' "range"
          Attr.create "min" (sprintf "-%d" TweakSignals.MostSteps)
          Attr.create "max" (string TweakSignals.MostSteps)
          Attr.create "step" "1"
          testid "tweak-slider"
          Attr.create "aria-label" (attrEnc (sprintf "Drag to change %s. Arrow keys step it. It is written when you let go." label))
          Ds.bind steps
          Ds.indicator (TweakSignals.busyOf key)
          Ds.attr' ("disabled", sprintf "$%s" (TweakSignals.busyOf key))
          Ds.onEvent ("change", stageAndPost key place "steps" (sprintf "String(Number($%s || 0))" steps), settled) ]
      Elem.span
        [ Attr.id (TweakSignals.readoutId key)
          Attr.class' "live-readout"
          Attr.title "The value in the file"
          testid "tweak-readout"
          Attr.create "aria-live" "polite"
          Ds.text landing ]
        [ textEnc current ]
      stepButton key "+" (sprintf "Step %s up" label) "tweak-step-up" 1 ]

/// The word a switch shows for a value. The file spells a bool `true` or `false`, so the switch says the same.
let switchWord (value: bool) : string =
  match value with
  | true -> "true"
  | false -> "false"

/// A switch: the checkbox and, beside it, the word for where it stands, so on and off are told apart by reading and not by the shade of
/// a box. The word and `aria-checked` follow the row's own signal, so they change the moment it is switched; the server's next push
/// sets the signal to what the file holds.
let toggle (key: RowKey) (place: SourceRef) (value: bool) (label: string) : XmlNode =
  let signal = TweakSignals.fieldOf key
  let wordExpr = sprintf "$%s ? 'true' : 'false'" signal
  Elem.label [ Attr.class' "live-knob live-switch"; Ds.signal (signal, value) ] [
    Elem.input
      ([ Attr.id (TweakSignals.sliderId key)
         Attr.class' "live-toggle"
         Attr.type' "checkbox"
         Attr.create "role" "switch"
         Attr.create "aria-checked" (switchWord value)
         Ds.attr' ("aria-checked", wordExpr)
         testid "tweak-toggle"
         Attr.create "aria-label" (attrEnc (sprintf "Switch %s" label))
         Ds.bind signal
         Ds.indicator (TweakSignals.busyOf key)
         Ds.attr' ("disabled", sprintf "$%s" (TweakSignals.busyOf key))
         Ds.onEvent ("change", stageAndPost key place "set" "event.target.checked ? 'true' : 'false'") ]
       @ (match value with
          | true -> [ Attr.create "checked" "" ]
          | false -> []))
    Elem.span [ Attr.class' "live-switch-word"; testid "tweak-toggle-word"; Ds.text wordExpr ] [ textEnc (switchWord value) ]
  ]

/// A field for a string, a character, a union case or an expression. Enter applies, Escape puts back what the file holds, and a hint
/// shows while what is typed differs from it.
let field (key: RowKey) (place: SourceRef) (value: string) (verb: string) (testName: string) (label: string) : XmlNode =
  let signal = TweakSignals.fieldOf key
  let hint = sprintf "%s-hint" (TweakSignals.sliderId key)
  Elem.span [ Attr.class' "live-knob live-field-wrap" ] [
    Elem.input
      [ Attr.id (TweakSignals.sliderId key)
        Attr.class' "live-field"
        Attr.type' "text"
        Attr.create "spellcheck" "false"
        Attr.create "autocomplete" "off"
        testid testName
        Attr.create "aria-label" (attrEnc (sprintf "%s. Enter applies it, Escape puts back what the file holds." label))
        signalFromText signal value
        Ds.bind signal
        Ds.indicator (TweakSignals.busyOf key)
        Ds.attr' ("disabled", sprintf "$%s" (TweakSignals.busyOf key))
        Ds.onEvent (
          "keydown",
          attrEnc (
            sprintf
              "if (event.key === 'Enter') { event.preventDefault(); %s } else if (event.key === 'Escape') { $%s = %s; event.target.blur() }"
              (stagePostJs key place verb (sprintf "$%s" signal))
              signal (jsText value)
          )
        ) ]
    Elem.span
      [ Attr.id hint
        Attr.class' "live-field-hint"
        Attr.style "display:none"
        Ds.preserveAttr "style"
        Ds.show (attrEnc (sprintf "$%s !== %s" signal (jsText value))) ]
      [ Text.raw "Enter to apply, Escape to cancel" ]
  ]

/// The word on an undo or redo button, from the verb the button posts: `undo` is shown `Undo`.
let historyWord (verb: string) : string =
  match verb with
  | "" -> verb
  | word -> string (Char.ToUpperInvariant word.[0]) + word.Substring 1

/// Why an undo or redo is not available, in words that stay on screen. A step that can be taken has no reason.
let historyWhy (verb: string) (step: HistoryStep) : string option =
  match step with
  | HistoryStep.StepAvailable -> None
  | HistoryStep.OtherRowFirst address ->
    Some(sprintf "%s waits: the newest write in this file is to %s, so that one comes first." (historyWord verb) address)
  // No write in the history means no button at all (`historyNodes`), so there is nothing to explain.
  | HistoryStep.NothingToStep -> None

/// An undo or redo button with its word on it. A button that a later write blocks keeps its word and its reason is written next to it
/// (`historyReason`), because a title shows only to a pointer.
let historyButton (key: RowKey) (place: SourceRef) (glyph: string) (verb: string) (testName: string) (step: HistoryStep) : XmlNode =
  let common =
    [ Attr.class' "session-btn session-btn-labeled live-history"
      Attr.type' "button"
      testid testName ]
  let content = [ Text.raw glyph; Elem.span [ Attr.class' "session-btn-label" ] [ textEnc (historyWord verb) ] ]
  match step with
  | HistoryStep.StepAvailable ->
    Elem.button
      (common
       @ [ Attr.create "aria-label" (attrEnc (sprintf "%s the last write to this value" (historyWord verb)))
           Attr.title (attrEnc (sprintf "%s the last write to this value" (historyWord verb)))
           Ds.indicator (TweakSignals.busyOf key)
           Ds.attr' ("disabled", sprintf "$%s" (TweakSignals.busyOf key))
           Ds.onClick (stageAndPost key place verb "''") ])
      content
  | HistoryStep.OtherRowFirst _
  | HistoryStep.NothingToStep ->
    Elem.button
      (common
       @ [ Attr.create "disabled" ""
           Attr.title (attrEnc (historyWhy verb step |> Option.defaultValue "")) ])
      content

/// The reason a history button cannot be pressed, as text beside the buttons. Nothing when the step is available.
let historyReason (verb: string) (step: HistoryStep) : XmlNode list =
  match historyWhy verb step with
  | Some why -> [ Elem.span [ Attr.class' "live-history-why live-tone-quiet"; testid (sprintf "tweak-%s-why" verb) ] [ textEnc why ] ]
  | None -> []

let controlNodes (key: RowKey) (view: RowView) : XmlNode list =
  match view.Place with
  | PlaceOf.NotPlaced -> []
  | PlaceOf.Placed place ->
    let label = RowKey.text key
    match view.Control with
    | Control.RealStepper(value, step) ->
      // A real is shown with at least one decimal, the way F# spells it (`80.0`), so the box agrees with the value text in the row.
      let places = max 1 (Step.places step)
      let size = invariant (Step.size step)
      let landing = sprintf "(%s + Number($%s || 0) * %s).toFixed(%d)" (invariant value) (TweakSignals.stepsOf key) size places
      [ stepper key place (value.ToString("F" + string places, CultureInfo.InvariantCulture)) landing label ]
    | Control.IntegerStepper value ->
      let landing = sprintf "String(%d + Number($%s || 0))" value (TweakSignals.stepsOf key)
      [ stepper key place (string value) landing label ]
    | Control.Toggle value -> [ toggle key place value label ]
    | Control.LiteralField(_, value) -> [ field key place value "set" "tweak-field" (sprintf "New value for %s" label) ]
    | Control.ExpressionField text -> [ field key place text "expression" "tweak-expression" (sprintf "Expression for %s" label) ]
    | Control.NoControl -> []

/// Undo and redo appear only where there is something for them to act on, and the same way on every row: a row with no write in its
/// history has neither, a row with a write to take back has Undo, and a row with an undone write has Redo. A step that a later write
/// blocks keeps its button and says why in text, and no step says "nothing to do" beside a button that was never needed.
let historyNodes (key: RowKey) (view: RowView) : XmlNode list =
  match view.Place with
  | PlaceOf.NotPlaced -> []
  | PlaceOf.Placed place ->
    [ match view.Undo with
      | HistoryStep.NothingToStep -> ()
      | undo ->
        yield historyButton key place "↶" "undo" "tweak-undo" undo
        yield! historyReason "undo" undo
      match view.Redo with
      | HistoryStep.NothingToStep -> ()
      | redo ->
        yield historyButton key place "↷" "redo" "tweak-redo" redo
        yield! historyReason "redo" redo ]

/// The button that applies the file's value to the session, where the row offers it. It evaluates code in the session and changes
/// nothing in the file, and its title says both.
let applyNodes (key: RowKey) (view: RowView) : XmlNode list =
  match view.Place, ApplyToSession.offerFor key view with
  | PlaceOf.Placed place, ApplyOffer.Offered code ->
    [ Elem.button
        [ Attr.class' "session-btn session-btn-labeled live-history live-apply"
          Attr.type' "button"
          testid "tweak-apply"
          Attr.create "aria-label" (attrEnc (sprintf "%s: evaluates %s in the session" ApplyToSession.words code))
          Attr.title (attrEnc (sprintf "Evaluates %s in the running session. The file is not changed." code))
          Ds.indicator (TweakSignals.busyOf key)
          Ds.attr' ("disabled", sprintf "$%s" (TweakSignals.busyOf key))
          Ds.onClick (attrEnc (stagePostTo ApplyToSession.Endpoint key place "apply" "''")) ]
        [ Text.raw "▶"; Elem.span [ Attr.class' "session-btn-label" ] [ textEnc ApplyToSession.words ] ] ]
  | PlaceOf.Placed _, ApplyOffer.NotOffered
  | PlaceOf.NotPlaced, _ -> []

/// What a row that was written to a file nothing is watching says. Only a row that was written says it: said under every row of the
/// file before any write, it was ten copies of one sentence. The pane says the same fact once, ahead of any edit, in its caption
/// (`PaneNote.ChangesGoToAnUnwatchedFile`).
let notWatchedSentence (file: string) : string =
  sprintf "Saved to the file. Nothing is watching %s, so the running session still has the old value." (Path.GetFileName file)

let reloadNodes (view: RowView) : XmlNode list =
  let watching : XmlNode list =
    match view.Watching, view.Place with
    | WatchStatus.NotWatched, PlaceOf.Placed place ->
      [ Elem.span [ Attr.class' "live-reload live-tone-notice"; testid "tweak-watching" ] [ textEnc (notWatchedSentence place.File) ] ]
    | WatchStatus.NotWatched, PlaceOf.NotPlaced
    | WatchStatus.Watched, _ -> []
  let line =
    match describeReload view.Reload with
    | ReloadLine.NoLine -> []
    | ReloadLine.Line(text, tone, more) ->
      [ Elem.span
          [ Attr.class' (sprintf "live-reload %s" (Tone.cssClass tone))
            testid "tweak-reload"
            Attr.title (attrEnc more) ]
          [ textEnc text ] ]
  match view.Watching, view.Reload with
  // A row nobody has written to says nothing, watched or not: the pane's caption says a file is not watched, once.
  | WatchStatus.NotWatched, RowReload.NoWriteYet -> []
  // Written, and nothing is watching the file: no verdict from hot reload can be about this file, so none is shown. One sentence says
  // the file is ahead, for as long as it is. When the session holds the file's value (an Apply, or the person evaluated it), "the
  // running session still has the old value" would be false, and nothing is said.
  | WatchStatus.NotWatched, RowReload.Watching _ ->
    match NumberLabels.ofState view.State with
    | NumberLabels.RunningAndFile -> watching
    | NumberLabels.OneNumber -> []
  | WatchStatus.Watched, (RowReload.NoWriteYet | RowReload.Watching _) -> line

/// The strip under a row: the state as a chip and a sentence, the knob where the state allows one, the row's undo and redo, and what
/// the app did with its last write. Every row the pane has something to say about gets one, so no row is silent about its file.
let renderStrip (key: RowKey) (view: RowView) (placement: Placement) : XmlNode =
  let described = describe view.State
  let whereChip =
    match view.Place with
    | PlaceOf.Placed place -> [ Elem.span [ Attr.class' "live-where"; Attr.title (attrEnc place.File) ] [ textEnc (whereOf place) ] ]
    | PlaceOf.NotPlaced -> []
  let controls =
    match placement with
    | Placement.OnLeafRow ->
      let knob = controlNodes key view
      // While the file and the running program disagree, the knob's number is the file's, and it says so.
      let fileTag =
        match NumberLabels.ofState view.State, knob with
        | NumberLabels.RunningAndFile, _ :: _ ->
          [ Elem.span [ Attr.class' "live-number-tag"; Attr.title NumberLabels.fileMeaning; testid "tweak-file-tag" ] [ textEnc NumberLabels.fileWord ] ]
        | NumberLabels.RunningAndFile, []
        | NumberLabels.OneNumber, _ -> []
      fileTag @ knob @ historyNodes key view @ applyNodes key view
    | Placement.InsideSummary -> []
  let reload =
    match placement with
    | Placement.OnLeafRow -> reloadNodes view
    | Placement.InsideSummary -> []
  let shown = stripShown view.State
  Elem.div
    [ Attr.id (TweakSignals.stripId key)
      Attr.class' (match shown with StripShown.Strip -> "live-tweak" | StripShown.Marker -> "live-tweak live-tweak-marker")
      testid "tweak-row"
      Attr.create "data-state" (PersistenceState.token view.State)
      Attr.create "data-row" (attrEnc (RowKey.text key)) ]
    (match shown with
     | StripShown.Marker -> []
     | StripShown.Strip ->
       [ yield
           Elem.span
             [ Attr.class' (sprintf "live-state %s" (Tone.cssClass described.Tone))
               testid "tweak-state"
               Attr.title (attrEnc (badgeTitle view.State)) ]
             [ textEnc described.Label ]
         yield! whereChip
         yield! controls
         yield! reload
         match detailShown view.State with
         | DetailShown.OnTheRow -> yield Elem.div [ Attr.class' "live-detail"; testid "tweak-detail" ] [ textEnc described.Detail ]
         | DetailShown.InTheTooltipOnly -> ()
         match NextStep.ofRow view, ApplyToSession.offerFor key view with
         | NextStep.NoNextStep, _ -> ()
         | _, ApplyOffer.Offered _ -> yield Elem.div [ Attr.class' "live-detail"; testid "tweak-next" ] [ textEnc ApplyToSession.sentence ]
         | step, ApplyOffer.NotOffered -> yield Elem.div [ Attr.class' "live-detail"; testid "tweak-next" ] [ textEnc (NextStep.sentence step) ]
         match describeThrough view.Through with
         | Some through -> yield Elem.div [ Attr.class' (sprintf "live-detail %s" (Tone.cssClass through.Tone)); testid "tweak-through" ] [ textEnc through.Detail ]
         | None -> () ])
