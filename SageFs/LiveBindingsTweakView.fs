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

module Tone =
  /// One class per tone, from one exhaustive function.
  let cssClass (tone: Tone) : string =
    match tone with
    | Tone.Good -> "live-tone-good"
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

/// A hash, short enough to read and long enough to compare.
let shortHash (hash: string) : string = if hash.Length > 8 then hash.Substring(0, 8) else hash

/// The one exhaustive saying of a state: a short label for the chip, a tone, and a sentence that says what is true and what to do.
let describe (state: PersistenceState) : Described =
  match state with
  | PersistenceState.InSource place ->
    { Label = "in source"
      Tone = Tone.Good
      Detail = sprintf "%s is %s in %s. A change here is written to that file." (SourceRef.addressText place) place.Text (whereOf place) }
  | PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt ->
    { Label = "REPL only"
      Tone = Tone.Quiet
      Detail = "Bound in the REPL. No file of this session holds a literal, a record field or a formula by this name, so there is nothing to save it to." }
  | PersistenceState.NotInAFile(NotInFileWhy.OwnedFilesUnknown reason) ->
    { Label = "files unknown"
      Tone = Tone.Notice
      Detail = sprintf "The session's files could not all be read, so the pane cannot say whether one holds this: %s" reason }
  | PersistenceState.NotInAFile(NotInFileWhy.NotSpelledInTheFile owner) ->
    { Label = "not spelled in the file"
      Tone = Tone.Quiet
      Detail = sprintf "%s is in %s, but this part is not a literal or a record field the file spells, so there is nothing here to write." (SourceRef.addressText owner) (whereOf owner) }
  | PersistenceState.Held ->
    { Label = "held"
      Tone = Tone.Quiet
      Detail = "Reading it runs your code, so it has not been read. Run it with the button, under a deadline." }
  | PersistenceState.Derived(place, DerivedWhy.AFormula) ->
    { Label = "formula"
      Tone = Tone.Quiet
      Detail =
        sprintf "%s spells it as %s. Edit it as an expression: the file is parsed, not type-checked, and the reload says if it does not compile." (whereOf place) place.Text }
  | PersistenceState.Derived(place, DerivedWhy.AContainer) ->
    { Label = "made of parts"
      Tone = Tone.Quiet
      Detail = sprintf "%s in %s. Its parts are the rows below: the ones the file spells as literals can be changed." (SourceRef.addressText place) (whereOf place) }
  | PersistenceState.Ambiguous places ->
    let where = places |> List.map whereOf |> String.concat ", "
    { Label = "ambiguous"
      Tone = Tone.Bad
      Detail =
        sprintf "%d places declare this name: %s. The pane will not pick one. Remove or rename all but one, or edit the file by hand." places.Length where }
  | PersistenceState.DiffersFromFile(live, place) ->
    { Label = "differs from the file"
      Tone = Tone.Notice
      Detail =
        sprintf "The REPL holds %s and the file says %s (%s). The knob changes the file. Evaluate the binding again, or let hot reload take the write, to bring the two back in step." live place.Text (whereOf place) }
  | PersistenceState.StaleAddress stale ->
    let nowText =
      match stale.Now with
      | StaleNow.Edited(text, hash) -> sprintf "it now holds %s (hash %s)" text (shortHash hash)
      | StaleNow.MovedTo candidate -> sprintf "it moved: the same expression is now at %s" (NudgeAddress.format candidate)
      | StaleNow.Gone reason -> sprintf "it is gone: %s" reason
    { Label = "stale"
      Tone = Tone.Bad
      Detail =
        sprintf "The file changed under this row. You saw %s (hash %s); %s. Nothing was written. Read it again and try from what is there." stale.Seen.Text (shortHash stale.Seen.Hash) nowText }
  | PersistenceState.Refused(refusal, _) ->
    { Label = "refused"
      Tone = Tone.Bad
      Detail = sprintf "%s %s" (NudgeRefusal.rule refusal) (NudgeRefusal.nextAction refusal) }
  | PersistenceState.Writing(kind, place) ->
    let what =
      match kind with
      | WriteKind.SetTo text -> sprintf "Setting %s to %s" (SourceRef.addressText place) text
      | WriteKind.UndoStep -> sprintf "Undoing the last write in %s" (Path.GetFileName place.File)
      | WriteKind.RedoStep -> sprintf "Putting the undone write back in %s" (Path.GetFileName place.File)
    { Label = "writing"
      Tone = Tone.Notice
      Detail = sprintf "%s in %s…" what (whereOf place) }

/// Whether a state's sentence is written under the row. A value that is simply in its file, and a record that is made of the rows
/// below it, are fine as they are: their sentence is the chip's tooltip, so a long tree is not a wall of repeated text. Everything
/// a person has to act on or understand (why there is no knob, what differs, what was refused) is on the row.
[<RequireQualifiedAccess>]
type DetailShown =
  | OnTheRow
  | InTheTooltipOnly

let detailShown (state: PersistenceState) : DetailShown =
  match state with
  | PersistenceState.InSource _
  | PersistenceState.Derived(_, DerivedWhy.AContainer) -> DetailShown.InTheTooltipOnly
  | PersistenceState.NotInAFile _
  | PersistenceState.Held
  | PersistenceState.Derived(_, DerivedWhy.AFormula)
  | PersistenceState.Ambiguous _
  | PersistenceState.DiffersFromFile _
  | PersistenceState.StaleAddress _
  | PersistenceState.Refused _
  | PersistenceState.Writing _ -> DetailShown.OnTheRow

[<RequireQualifiedAccess>]
type ReloadLine =
  | NoLine
  /// The line, its tone, and the longer text for the tooltip (the worker's own wording of what happened and what to do).
  | Line of text: string * Tone * more: string

/// What a reload verdict means for the one value that was written, in a few words.
let reloadMeaning (case: ReloadCase) : string * Tone =
  match case with
  | ReloadCase.AssetsRebuilt -> "the browser assets were rebuilt", Tone.Good
  | ReloadCase.Unchanged -> "the saved output did not change", Tone.Quiet
  | ReloadCase.Patched -> "the app is running the new code", Tone.Good
  | ReloadCase.PatchPending -> "applied, and the new code has not run yet", Tone.Notice
  | ReloadCase.NeverEntered -> "applied, and the new code was never entered", Tone.Notice
  | ReloadCase.Restarted -> "the app restarted to take it", Tone.Notice
  | ReloadCase.NoEffect -> "nothing in the running process changed", Tone.Quiet
  | ReloadCase.RestartRequired -> "the app needs a restart to take it", Tone.Bad
  | ReloadCase.CompileFailed -> "the file did not compile, and the app kept its last code", Tone.Bad
  | ReloadCase.KeptLiveState -> "a live value was kept instead of reset", Tone.Notice

/// What the app did with the row's write, in words. A verdict names its case and, when a patch named them, the declarations; the
/// worker's own longer wording is the tooltip.
let describeReload (reload: RowReload) : ReloadLine =
  match reload with
  | RowReload.NoWriteYet -> ReloadLine.NoLine
  | RowReload.Watching(_, ReloadWatch.AwaitingReload) -> ReloadLine.Line("waiting for the app to take it", Tone.Quiet, "")
  | RowReload.Watching(_, ReloadWatch.Compiling) -> ReloadLine.Line("hot reload is compiling it", Tone.Notice, "")
  | RowReload.Watching(_, ReloadWatch.NoNewReport) ->
    ReloadLine.Line(
      "no new reload report arrived",
      Tone.Quiet,
      "Hot reload may not be running, or its verdict is the same as the last one's, which cannot be told apart from no verdict."
    )
  | RowReload.Watching(_, ReloadWatch.Reported facts) ->
    let named =
      match facts.Declarations with
      | [] -> ""
      | declarations -> sprintf " (%s)" (String.concat ", " (declarations |> List.truncate 3))
    let meaning, tone = reloadMeaning facts.Case
    ReloadLine.Line(sprintf "%s%s: %s" (ReloadCase.token facts.Case) named meaning, tone, sprintf "%s %s" facts.Message facts.SuggestedAction)

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

/// The script that stages a row's request in the page's signals and posts it. `valueJs` is a JS expression for the value.
let stagePostJs (key: RowKey) (place: SourceRef) (verb: string) (valueJs: string) : string =
  sprintf
    "$%s = %s; $%s = %s; $%s = %s; $%s = %s; $%s = %s; $%s = %s; $%s = %s; %s"
    TweakSignals.Row (jsText (RowKey.text key))
    TweakSignals.File (jsText place.File)
    TweakSignals.Address (jsText (SourceRef.addressText place))
    TweakSignals.Seen (jsText place.Hash)
    TweakSignals.SeenText (jsText place.Text)
    TweakSignals.Verb (jsText verb)
    TweakSignals.Value valueJs
    (Ds.post TweakSignals.Endpoint)

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
          testid "tweak-readout"
          Attr.create "aria-live" "polite"
          Ds.text landing ]
        [ textEnc current ]
      stepButton key "+" (sprintf "Step %s up" label) "tweak-step-up" 1 ]

let toggle (key: RowKey) (place: SourceRef) (value: bool) (label: string) : XmlNode =
  Elem.input
    ([ Attr.id (TweakSignals.sliderId key)
       Attr.class' "live-toggle"
       Attr.type' "checkbox"
       Attr.create "role" "switch"
       testid "tweak-toggle"
       Attr.create "aria-label" (attrEnc (sprintf "Switch %s" label))
       Ds.indicator (TweakSignals.busyOf key)
       Ds.attr' ("disabled", sprintf "$%s" (TweakSignals.busyOf key))
       Ds.onEvent ("change", stageAndPost key place "set" "event.target.checked ? 'true' : 'false'") ]
     @ (match value with
        | true -> [ Attr.create "checked" "" ]
        | false -> []))

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

let historyButton (key: RowKey) (place: SourceRef) (glyph: string) (verb: string) (testName: string) (step: HistoryStep) : XmlNode =
  let common =
    [ Attr.class' "session-btn live-history"
      Attr.type' "button"
      testid testName ]
  match step with
  | HistoryStep.StepAvailable ->
    Elem.button
      (common
       @ [ Attr.create "aria-label" (attrEnc (sprintf "%s the last write to this value" verb))
           Attr.title (attrEnc (sprintf "%s the last write to this value" verb))
           Ds.indicator (TweakSignals.busyOf key)
           Ds.attr' ("disabled", sprintf "$%s" (TweakSignals.busyOf key))
           Ds.onClick (stageAndPost key place verb "''") ])
      [ Text.raw glyph ]
  | HistoryStep.OtherRowFirst address ->
    Elem.button
      (common
       @ [ Attr.create "disabled" ""
           Attr.title (attrEnc (sprintf "A later write in this file is on top: %s comes first." address)) ])
      [ Text.raw glyph ]
  | HistoryStep.NothingToStep ->
    Elem.button (common @ [ Attr.create "disabled" ""; Attr.title (attrEnc (sprintf "Nothing to %s." verb)) ]) [ Text.raw glyph ]

let controlNodes (key: RowKey) (view: RowView) : XmlNode list =
  match view.Place with
  | PlaceOf.NotPlaced -> []
  | PlaceOf.Placed place ->
    let label = RowKey.text key
    match view.Control with
    | Control.RealStepper(value, step) ->
      let places = Step.places step
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

let historyNodes (key: RowKey) (view: RowView) : XmlNode list =
  match view.Place, view.Undo, view.Redo with
  | PlaceOf.NotPlaced, _, _ -> []
  | PlaceOf.Placed _, HistoryStep.NothingToStep, HistoryStep.NothingToStep -> []
  | PlaceOf.Placed place, undo, redo ->
    [ historyButton key place "↶" "undo" "tweak-undo" undo
      historyButton key place "↷" "redo" "tweak-redo" redo ]

let reloadNodes (view: RowView) : XmlNode list =
  let notWatched =
    match view.Watching, view.Place with
    | WatchStatus.NotWatched, PlaceOf.Placed place ->
      [ Elem.span [ Attr.class' "live-reload live-tone-notice"; testid "tweak-watching" ] [
          textEnc (sprintf "hot reload is not watching %s, so the running app will not follow a write" (Path.GetFileName place.File)) ] ]
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
  line @ notWatched

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
    | Placement.OnLeafRow -> controlNodes key view @ historyNodes key view
    | Placement.InsideSummary -> []
  let reload =
    match placement with
    | Placement.OnLeafRow -> reloadNodes view
    | Placement.InsideSummary -> []
  Elem.div
    [ Attr.id (TweakSignals.stripId key)
      Attr.class' "live-tweak"
      testid "tweak-row"
      Attr.create "data-state" (PersistenceState.token view.State)
      Attr.create "data-row" (attrEnc (RowKey.text key)) ]
    [ yield
        Elem.span
          [ Attr.class' (sprintf "live-state %s" (Tone.cssClass described.Tone))
            testid "tweak-state"
            Attr.title (attrEnc described.Detail) ]
          [ textEnc described.Label ]
      yield! whereChip
      yield! controls
      yield! reload
      match detailShown view.State with
      | DetailShown.OnTheRow -> yield Elem.div [ Attr.class' "live-detail"; testid "tweak-detail" ] [ textEnc described.Detail ]
      | DetailShown.InTheTooltipOnly -> () ]
