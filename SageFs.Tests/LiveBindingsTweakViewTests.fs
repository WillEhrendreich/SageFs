/// How a live binding's row shows what it knows about its file and what it lets a person do about it. The state has ONE exhaustive
/// render, so every state says something in words (never a disabled control with no reason), every control is Datastar's typed
/// helpers and never a hand-written attribute, and what a person might type is shown as text, never as markup.
module SageFs.Tests.LiveBindingsTweakViewTests

open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Server
open SageFs.Server.LiveBindingsTweakView
open SageFs.Tests.SharedGenerators
open SageFs.Tests.BindingTweakTests

let private decoded (node: XmlNode) = Net.WebUtility.HtmlDecode(renderNode node)

let private place (text: string) (kind: ItemKind) : SourceRef =
  { File = "/work/game/Tuning.fs"
    Address = addressOf "gravity" []
    Text = text
    Hash = String('a', 64)
    Span = { Line = 3; Column = 14; EndLine = 3; EndColumn = 17 }
    Kind = kind }

let private gravity = place "9.8" (ItemKind.Knob(LiteralValue.Real 9.8))
let private key = RowKey.top "gravity"

let private rowOf (state: PersistenceState) (control: Control) : RowView =
  { State = state
    Control = control
    Place = PlaceOf.Placed gravity
    Watching = WatchStatus.Watched
    Reload = RowReload.NoWriteYet
    Undo = HistoryStep.NothingToStep
    Redo = HistoryStep.NothingToStep }

let private strip (view: RowView) = renderStrip key view Placement.OnLeafRow |> decoded

let private states : PersistenceState list =
  [ PersistenceState.InSource gravity
    PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt
    PersistenceState.NotInAFile(NotInFileWhy.OwnedFilesUnknown "b.fs: does not parse")
    PersistenceState.NotInAFile(NotInFileWhy.NotSpelledInTheFile gravity)
    PersistenceState.Held
    PersistenceState.Derived(gravity, DerivedWhy.AFormula)
    PersistenceState.Derived(gravity, DerivedWhy.AContainer)
    PersistenceState.Ambiguous [ gravity; { gravity with File = "/work/game/Other.fs"; Span = { gravity.Span with Line = 9 } } ]
    PersistenceState.DiffersFromFile("12", gravity)
    PersistenceState.StaleAddress { Seen = gravity; Now = StaleNow.Edited("9.9", String('b', 64)) }
    PersistenceState.StaleAddress { Seen = gravity; Now = StaleNow.MovedTo(addressOf "g2" []) }
    PersistenceState.StaleAddress { Seen = gravity; Now = StaleNow.Gone "the binding is not in the file any more" }
    PersistenceState.Refused(NudgeRefusal.LiteralNotReadable(LiteralKindName.Real, "abc"), gravity)
    PersistenceState.Writing(WriteKind.SetTo "10.4", gravity)
    PersistenceState.Writing(WriteKind.UndoStep, gravity) ]

[<Tests>]
let describeTests =
  testList "a row's state, said in words: one exhaustive render" [

    testCase "every state has a label, a tone and a sentence, and the sentences carry the facts a person needs" <| fun _ ->
      for state in states do
        let described = describe state
        (String.IsNullOrWhiteSpace described.Label) |> Expect.isFalse (sprintf "%s has a label" (PersistenceState.token state))
        (String.IsNullOrWhiteSpace described.Detail) |> Expect.isFalse (sprintf "%s says why, in a sentence" (PersistenceState.token state))

    testCase "a refusal carries the door's rule and its next action, verbatim" <| fun _ ->
      let refusal = NudgeRefusal.LiteralNotReadable(LiteralKindName.Real, "abc")
      let described = describe (PersistenceState.Refused(refusal, gravity))
      described.Detail |> Expect.stringContains "the rule" (NudgeRefusal.rule refusal)
      described.Detail |> Expect.stringContains "the next action" (NudgeRefusal.nextAction refusal)
      described.Tone |> Expect.equal "a refusal is bad news" Tone.Bad

    testCase "a stale address shows both versions, and says nothing was written" <| fun _ ->
      let described = describe (PersistenceState.StaleAddress { Seen = gravity; Now = StaleNow.Edited("9.9", String('b', 64)) })
      described.Detail |> Expect.stringContains "what the row showed" "9.8"
      described.Detail |> Expect.stringContains "what the file holds now" "9.9"
      described.Detail |> Expect.stringContains "that nothing was written" "Nothing was written"

    testCase "an ambiguous name lists every place it is declared and picks none" <| fun _ ->
      let other = { gravity with File = "/work/game/Other.fs"; Span = { gravity.Span with Line = 9 } }
      let described = describe (PersistenceState.Ambiguous [ gravity; other ])
      described.Detail |> Expect.stringContains "the first place" "Tuning.fs:3"
      described.Detail |> Expect.stringContains "the second place" "Other.fs:9"

    testCase "a value that differs from the file names both values and the file's place" <| fun _ ->
      let described = describe (PersistenceState.DiffersFromFile("12", gravity))
      described.Detail |> Expect.stringContains "the REPL's value" "12"
      described.Detail |> Expect.stringContains "the file's value" "9.8"
      described.Detail |> Expect.stringContains "where" "Tuning.fs:3"

    testCase "a REPL-only value says there is nothing to save it to" <| fun _ ->
      (describe (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt)).Detail |> Expect.stringContains "nothing to save to" "nothing to save it to"

    testPropertyWithConfig propConfig "whatever the facts, the derived state is always described, and never with an empty reason"
      (Prop.forAll (arbitrary (Gen.zip3 genLive genSource genOutcome)) (fun (live, source, outcome) ->
        let described = describe (PersistenceState.derive live source outcome)
        not (String.IsNullOrWhiteSpace described.Label) && not (String.IsNullOrWhiteSpace described.Detail)))

    testCase "every reload verdict is said in words: the case, and the declarations a patch named" <| fun _ ->
      let facts case : ReloadFacts =
        { Case = case
          Patched = 1
          Considered = 1
          Message = "m"
          SuggestedAction = ""
          Mechanism = ReloadOutcome.PatchMechanism.NoPatch
          Declarations = [ "Game.Tuning.gravity" ]
          Callers = CallerState.CallersState.CallersNotReported }
      for case in ReloadCase.all do
        match describeReload (RowReload.Watching("Tuning.fs", ReloadWatch.Reported(facts case))) with
        | ReloadLine.Line(text, _) ->
          text |> Expect.stringContains (sprintf "%s is named" (ReloadCase.token case)) (ReloadCase.token case)
          text |> Expect.stringContains "the declaration is named" "Game.Tuning.gravity"
        | ReloadLine.NoLine -> failtestf "%s is not said" (ReloadCase.token case)
      describeReload RowReload.NoWriteYet |> Expect.equal "no write, nothing to say" ReloadLine.NoLine
  ]

[<Tests>]
let escapingTests =
  testList "text that came from a file or a person is shown as text and spliced into script as a string" [

    testPropertyWithConfig propConfig "a JS string literal never carries a raw newline, an unescaped quote or a backslash that escapes nothing"
      (Prop.forAll (arbitrary (Gen.elements [ "plain"; "it's"; "a\nb"; "tab\there"; "back\\slash"; "line sep"; "\"quoted\""; "</script>"; "$x = 1; alert(1)"; "" ])) (fun text ->
        let literal = jsText text
        literal.StartsWith "'" && literal.EndsWith "'"
        && not (literal.Contains "\n") && not (literal.Contains "\r") && not (literal.Contains " ")
        // Undo the escapes, in the order a JS parser would, and the original text comes back.
        && Regex.Replace(literal.Substring(1, literal.Length - 2), @"\\(u2028|u2029|[\\'nrt])", fun m ->
             match m.Groups.[1].Value with
             | "n" -> "\n"
             | "r" -> "\r"
             | "t" -> "\t"
             | "u2028" -> " "
             | "u2029" -> " "
             | other -> other) = text))

    testCase "a hostile file text never lands as markup, in the chip, the sentence, the field or the script" <| fun _ ->
      for text in [ "<img src=x onerror=alert(1)>"; "a\" onfocus=\"alert(1)"; "it's \"quoted\"\nand multi-line" ] do
        let hostile = place text (ItemKind.Knob(LiteralValue.Text text))
        for control in [ Control.LiteralField(LiteralKindName.Text, text); Control.ExpressionField text ] do
          let view = { rowOf (PersistenceState.InSource hostile) control with Place = PlaceOf.Placed hostile }
          let raw = renderStrip key view Placement.OnLeafRow |> renderNode
          raw.Contains "<img" |> Expect.isFalse "never injected as an element"
          raw.Contains "\" onfocus=\"" |> Expect.isFalse "a quote in the text never ends an attribute"
          // A newline is harmless in text and in a title, but in a script it would end the string literal.
          Regex.IsMatch(raw, "data-(on|signals|show)[^=]*=\"[^\"]*\n") |> Expect.isFalse "a newline never reaches a script raw"
  ]

let private count (needle: string) (html: string) = html.Split([| needle |], StringSplitOptions.None).Length - 1

[<Tests>]
let controlTests =
  testList "the knob: a control per kind, typed Datastar helpers, and never a mystery" [

    testCase "a real is a drag handle, step buttons, a readout and the arrow keys; the steps are one signal; the write waits for the settle" <| fun _ ->
      let html = strip (rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1)))
      html |> Expect.stringContains "a range input for the drag handle" "type=\"range\""
      html |> Expect.stringContains "the steps are bound to a signal" "data-bind:tw-steps"
      html |> Expect.stringContains "one write per settle" "debounce"
      html |> Expect.stringContains "step down" "data-testid=\"tweak-step-down\""
      html |> Expect.stringContains "step up" "data-testid=\"tweak-step-up\""
      html |> Expect.stringContains "the readout follows the signal" "data-text"
      html |> Expect.stringContains "the endpoint the write posts to" TweakSignals.Endpoint
      html |> Expect.stringContains "the state is readable by a test" "data-state=\"InSource\""
      (count "session-btn" html, 2) |> Expect.isGreaterThanOrEqual "uniform square buttons"

    testCase "an integer steps by whole units, a bool is a toggle, a string and a formula are fields with Enter and Escape" <| fun _ ->
      let integer = place "100" (ItemKind.Knob(LiteralValue.Integer 100L))
      strip { rowOf (PersistenceState.InSource integer) (Control.IntegerStepper 100L) with Place = PlaceOf.Placed integer } |> Expect.stringContains "int" "type=\"range\""
      let toggle = place "true" (ItemKind.Knob(LiteralValue.Bool true))
      let toggled = strip { rowOf (PersistenceState.InSource toggle) (Control.Toggle true) with Place = PlaceOf.Placed toggle }
      toggled |> Expect.stringContains "a checkbox" "type=\"checkbox\""
      toggled |> Expect.stringContains "that says it is a switch" "role=\"switch\""
      let text = place "\"Nudge\"" (ItemKind.Knob(LiteralValue.Text "Nudge"))
      let field = strip { rowOf (PersistenceState.InSource text) (Control.LiteralField(LiteralKindName.Text, "Nudge")) with Place = PlaceOf.Placed text }
      field |> Expect.stringContains "a text field" "type=\"text\""
      field |> Expect.stringContains "Enter applies" "Enter"
      field |> Expect.stringContains "Escape cancels" "Escape"
      let formula = place "gravity * 2.0" ItemKind.Formula
      let expression = strip { rowOf (PersistenceState.Derived(formula, DerivedWhy.AFormula)) (Control.ExpressionField "gravity * 2.0") with Place = PlaceOf.Placed formula }
      expression |> Expect.stringContains "an expression field" "data-testid=\"tweak-expression\""

    testPropertyWithConfig propConfig "a row with no control shows WHY in words and offers no input at all: never a disabled mystery"
      (Prop.forAll (arbitrary (Gen.zip3 genLive genSource genOutcome)) (fun (live, source, outcome) ->
        let state = PersistenceState.derive live source outcome
        let control = Control.ofState state source
        match control with
        | Control.NoControl ->
          let html = strip { rowOf state control with Place = PlaceOf.ofFacts source }
          not (html.Contains "<input") && html.Contains (describe state).Label
        | _ -> true))

    testCase "undo and redo are per row: the pair appears once there is history, a later write in the file disables an earlier row's undo and says whose turn it is" <| fun _ ->
      let base' = rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1))
      count "tweak-undo" (strip base') |> Expect.equal "no history, no pair" 0
      let withHistory = strip { base' with Undo = HistoryStep.StepAvailable; Redo = HistoryStep.NothingToStep }
      withHistory |> Expect.stringContains "undo" "data-testid=\"tweak-undo\""
      withHistory |> Expect.stringContains "redo" "data-testid=\"tweak-redo\""
      let waiting = strip { base' with Undo = HistoryStep.OtherRowFirst "Game.Tuning.maxHealth" }
      waiting |> Expect.stringContains "says whose turn it is" "Game.Tuning.maxHealth"
      waiting |> Expect.stringContains "and the button is disabled in the markup" "disabled"

    testCase "the reload the write caused is shown on the row, and a file hot reload is not watching says so" <| fun _ ->
      let view = { rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1)) with Reload = RowReload.Watching("Tuning.fs", ReloadWatch.NoNewReport); Watching = WatchStatus.NotWatched }
      let html = strip view
      html |> Expect.stringContains "the reload line" "data-testid=\"tweak-reload\""
      html |> Expect.stringContains "the not-watched note" "not watching"

    testCase "a value that is simply in its file keeps its sentence in the tooltip, and anything a person must act on or understand keeps it on the row" <| fun _ ->
      strip (rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1))) |> fun html -> html.Contains "tweak-detail" |> Expect.isFalse "in source: no wall of text"
      strip (rowOf (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt) Control.NoControl) |> Expect.stringContains "REPL only says why on the row" "data-testid=\"tweak-detail\""
      for state in states do
        match state with
        | PersistenceState.InSource _
        | PersistenceState.Derived(_, DerivedWhy.AContainer) -> ()
        | other -> detailShown other |> Expect.equal (sprintf "%s explains itself on the row" (PersistenceState.token other)) DetailShown.OnTheRow

    testCase "a row in a summary (a record, a list) shows its state in words but never a control: a click there would toggle the details" <| fun _ ->
      let html = renderStrip key (rowOf (PersistenceState.Derived(gravity, DerivedWhy.AContainer)) Control.NoControl) Placement.InsideSummary |> decoded
      html.Contains "<input" |> Expect.isFalse "no input inside a summary"
      html.Contains "<button" |> Expect.isFalse "no button inside a summary"
      html |> Expect.stringContains "but the state is said" "data-state=\"Derived\""

    testCase "nothing is hand-written: no raw on* attribute, and every element that keeps a client attribute has an id" <| fun _ ->
      for state in states do
        let control = match state with | PersistenceState.InSource _ -> Control.RealStepper(9.8, Step.Fraction 1) | _ -> Control.NoControl
        let raw = renderStrip key (rowOf state control) Placement.OnLeafRow |> renderNode
        Regex.IsMatch(raw, @"\son[a-z]+=") |> Expect.isFalse "no raw onclick or onchange"
        for tag in raw.Split('<') |> Array.filter (fun t -> t.Contains "data-preserve-attr") do
          tag.Contains " id=\"" |> Expect.isTrue (sprintf "a preserved attribute needs an id: <%s" (tag.Substring(0, min 80 tag.Length)))
  ]

// ── in the dock ──

module Dock = SageFs.Server.LiveBindingsDock

let private node label kind (preview: string) children : LiveValueTree.LiveValueNode =
  { Label = label; TypeName = "T"; Preview = preview; Kind = kind; Children = children; BestEffort = false; Depth = 0 }

let private binding name kind (preview: string) children : LiveValueTree.LiveBindingValue =
  { Name = name; TypeSignature = "T"; Root = node name kind preview children }

let private paneView (bindings: LiveValueTree.LiveBindingValue list) : LiveBindingsPane.PaneView =
  { Snapshot = { SessionId = "abcd1234"; Generation = 1L; Bindings = bindings; Truncated = false; CapturedAt = DateTimeOffset.UnixEpoch }
    Notes = { Mode = ValueWalk.standard; Click = LiveBindingsPane.NoClickYet } }

let private tweaks (rows: (RowKey * RowView) list) : TweakView = { Rows = Map.ofList rows }

let private dockWith (view: TweakView) (bindings: LiveValueTree.LiveBindingValue list) : string =
  Dock.renderDockWith view DockPanes.SessionInView "abcd1234" (Dock.WalkedBindings(paneView bindings)) |> renderNode

[<Tests>]
let dockIntegrationTests =
  testList "the knob in the dock: one strip per row the pane has something to say about, and nothing else changes" [

    testCase "a leaf row gets its strip with its control, and a record's summary row gets a strip with no control in it" <| fun _ ->
      let leaf = binding "gravity" LiveValueTree.NodeKind.Leaf "9.8" []
      let record =
        binding "tuning" LiveValueTree.NodeKind.Record "{ A = 1 }" [ node "A" LiveValueTree.NodeKind.Leaf "1" [] ]
      let view =
        tweaks
          [ RowKey.top "gravity", rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1))
            RowKey.top "tuning", rowOf (PersistenceState.Derived(gravity, DerivedWhy.AContainer)) Control.NoControl
            RowKey.field (RowKey.top "tuning") "A", rowOf (PersistenceState.InSource gravity) (Control.IntegerStepper 1L) ]
      let html = dockWith view [ leaf; record ]
      count "data-testid=\"tweak-row\"" html |> Expect.equal "a strip per mapped row: the leaf, the record and its field" 3
      html |> Expect.stringContains "the leaf's knob" "data-testid=\"tweak-slider\""
      html |> Expect.stringContains "the field's knob too" (TweakSignals.stepsOf (RowKey.field (RowKey.top "tuning") "A"))

    testCase "a dock with no tweak view draws exactly what it drew before: no strip, no signal, no script" <| fun _ ->
      let html =
        Dock.renderDock DockPanes.SessionInView "abcd1234" (Dock.WalkedBindings(paneView [ binding "gravity" LiveValueTree.NodeKind.Leaf "9.8" [] ]))
        |> renderNode
      html.Contains "tweak-row" |> Expect.isFalse "no strip"
      html.Contains "twSteps" |> Expect.isFalse "no per-row signal"

    testCase "the staging signals are declared once in the page shell with the dock's own" <| fun _ ->
      let shell = Dock.DockSignals.initial |> List.map (fun attribute -> renderNode (Elem.div [ attribute ] [])) |> String.concat ""
      for name in [ TweakSignals.Row; TweakSignals.File; TweakSignals.Address; TweakSignals.Seen; TweakSignals.SeenText; TweakSignals.Verb; TweakSignals.Value ] do
        // Datastar writes a camelCase signal in kebab case in the attribute.
        let kebab = Regex.Replace(name, "([A-Z])", fun m -> "-" + m.Value.ToLowerInvariant())
        shell |> Expect.stringContains (sprintf "%s is declared" name) (sprintf "data-signals:%s" kebab)

    testCase "every element whose attribute the client keeps has an id, in the whole dock with strips" <| fun _ ->
      let view = tweaks [ RowKey.top "gravity", rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1)) ]
      let html = dockWith view [ binding "gravity" LiveValueTree.NodeKind.Leaf "9.8" [] ]
      for tag in html.Split('<') |> Array.filter (fun t -> t.Contains "data-preserve-attr") do
        tag.Contains " id=\"" |> Expect.isTrue (sprintf "a preserved attribute needs an id: <%s" (tag.Substring(0, min 80 tag.Length)))
  ]
