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
    Redo = HistoryStep.NothingToStep
    Through = ThroughCall.NoCall }

let private strip (view: RowView) = renderStrip key view Placement.OnLeafRow |> decoded

/// The text inside the first element that carries this test id, whatever order its attributes come in.
let textOfTestId (testId: string) (html: string) : string option =
  let m = Regex.Match(html, sprintf "data-testid=\"%s\"[^>]*>([^<]*)<" (Regex.Escape testId))
  match m.Success with
  | true -> Some m.Groups.[1].Value
  | false -> None

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
    PersistenceState.Tweaked("12", gravity)
    PersistenceState.Evolved("12", EvolvedBy.OvertookTweak "11", gravity)
    PersistenceState.Evolved("12", EvolvedBy.MovedWhileRunning, gravity)
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
      described.Detail |> Expect.stringContains "the running value" "12"
      described.Detail |> Expect.stringContains "the file's value" "9.8"
      described.Detail |> Expect.stringContains "where, as words a person reads" (whereInWords gravity)

    testCase "a REPL-only value says there is nothing to save it to" <| fun _ ->
      (describe (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt)).Detail |> Expect.stringContains "nothing to save to" "nothing to save it to"

    testPropertyWithConfig propConfig "whatever the facts, the derived state is always described, and never with an empty reason"
      (Prop.forAll (arbitrary (Gen.zip3 genLive genSource genOutcome)) (fun (live, source, outcome) ->
        let described = describe (PersistenceState.derive live source outcome)
        not (String.IsNullOrWhiteSpace described.Label) && not (String.IsNullOrWhiteSpace described.Detail)))

    testCase "WHY — every reload verdict is said as a sentence a person can read without knowing the verdict's name, and the name and the declarations a patch named are the tooltip" <| fun _ ->
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
        | ReloadLine.Line(text, _, more) ->
          text.Contains(ReloadCase.token case) |> Expect.isFalse (sprintf "the row never shows the raw name %s" (ReloadCase.token case))
          text.Contains "Game.Tuning.gravity" |> Expect.isFalse "the row does not carry the declaration"
          String.IsNullOrWhiteSpace text |> Expect.isFalse (sprintf "%s is said" (ReloadCase.token case))
          more |> Expect.stringContains (sprintf "%s is named in the tooltip" (ReloadCase.token case)) (ReloadCase.token case)
          more |> Expect.stringContains "the declaration is named in the tooltip" "Game.Tuning.gravity"
        | ReloadLine.NoLine -> failtestf "%s is not said" (ReloadCase.token case)
      describeReload RowReload.NoWriteYet |> Expect.equal "no write, nothing to say" ReloadLine.NoLine

    testCase "WHY — a write that the running app did not take never reads as a contradiction: no verdict is both applied and never entered" <| fun _ ->
      for case in ReloadCase.all do
        let text, tone = reloadMeaning case
        text.Contains "applied" |> Expect.isFalse (sprintf "%s does not say applied beside a refusal to run" (ReloadCase.token case))
        // A verdict that failed the write is bad news, and one that worked is good news: the tone follows what happened.
        match case, tone with
        | (ReloadCase.CompileFailed | ReloadCase.EvalFailed | ReloadCase.RestartRequired), Tone.Bad -> ()
        | (ReloadCase.CompileFailed | ReloadCase.EvalFailed | ReloadCase.RestartRequired), other -> failtestf "%s is %A" (ReloadCase.token case) other
        | ReloadCase.Patched, Tone.Good -> ()
        | ReloadCase.Patched, other -> failtestf "a patched write is %A" other
        | _ -> ()

    testPropertyWithConfig propConfig "no sentence the pane says about a row carries a raw case name or a backtick, for any state"
      (Prop.forAll (arbitrary (Gen.zip3 genLive genSource genOutcome)) (fun (live, source, outcome) ->
        let state = PersistenceState.derive live source outcome
        let described = describe state
        let raw = [ PersistenceState.token state; "NeverEntered"; "PatchPending"; "RestartRequired"; "DiffersFromFile"; "StaleAddress" ]
        not (described.Detail.Contains "`") && not (raw |> List.exists described.Detail.Contains)))

    testCase "WHY — every state and every next step is said without a raw case name or a backtick, whichever way the row's controls and history stand" <| fun _ ->
      let raw = [ "NeverEntered"; "PatchPending"; "RestartRequired"; "DiffersFromFile"; "StaleAddress"; "InSource"; "NotInAFile"; "Evolved"; "Tweaked" ]
      for state in states do
        for control in [ Control.NoControl; Control.RealStepper(9.8, Step.Fraction 1) ] do
          for undo in [ HistoryStep.StepAvailable; HistoryStep.NothingToStep; HistoryStep.OtherRowFirst "Game.Tuning.maxHealth" ] do
            let view = { rowOf state control with Undo = undo }
            let spoken = [ (describe state).Detail; (describe state).Label; badgeTitle state; tagMeaning (StateTag.ofState state); NextStep.sentence (NextStep.ofRow view) ]
            for sentence in spoken do
              sentence.Contains "`" |> Expect.isFalse (sprintf "%s has no backtick: %s" (PersistenceState.token state) sentence)
              for name in raw do
                sentence.Contains name |> Expect.isFalse (sprintf "%s does not say the case name %s: %s" (PersistenceState.token state) name sentence)

    testCase "WHY — a row whose file and running value differ labels the knob's number as the file's, and the strip never prints a third line of values" <| fun _ ->
      for state in states do
        match NumberLabels.ofState state with
        | NumberLabels.RunningAndFile ->
          match state with
          | PersistenceState.Tweaked _
          | PersistenceState.Evolved _
          | PersistenceState.DiffersFromFile _ -> ()
          | other -> failtestf "%s is labelled as two numbers" (PersistenceState.token other)
        | NumberLabels.OneNumber ->
          match state with
          | PersistenceState.Tweaked _
          | PersistenceState.Evolved _
          | PersistenceState.DiffersFromFile _ -> failtestf "%s has two numbers and does not label them" (PersistenceState.token state)
          | _ -> ()
      let differs = PersistenceState.DiffersFromFile("9.8", { gravity with Text = "9.9" })
      let view = { rowOf differs (Control.RealStepper(9.9, Step.Fraction 1)) with Place = PlaceOf.Placed { gravity with Text = "9.9" } }
      let html = strip view
      textOfTestId "tweak-file-tag" html |> Expect.equal "the knob's number carries the file's word" (Some NumberLabels.fileWord)
      html.Contains "tweak-values" |> Expect.isFalse "no separate line repeats the numbers"
      let inStep = strip (rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1)))
      inStep.Contains "tweak-file-tag" |> Expect.isFalse "a row in step has one number and needs no word"
      NumberLabels.runningWord |> Expect.notEqual "the two words differ" NumberLabels.fileWord

    testCase "the next step for a row that differs from the file is a button that exists: Undo when it can take the write back, and otherwise a plain statement of what cannot be done from here" <| fun _ ->
      let differs = PersistenceState.DiffersFromFile("9.8", { gravity with Text = "9.9" })
      let rowWith undo = { rowOf differs (Control.RealStepper(9.9, Step.Fraction 1)) with Undo = undo }
      NextStep.ofRow (rowWith HistoryStep.StepAvailable) |> Expect.equal "undo is available" NextStep.UndoOrReload
      NextStep.ofRow (rowWith HistoryStep.NothingToStep) |> Expect.equal "nothing to undo" NextStep.ReloadOnly
      NextStep.ofRow (rowWith (HistoryStep.OtherRowFirst "x")) |> Expect.equal "another row's write is on top" NextStep.ReloadOnly
      strip (rowWith HistoryStep.StepAvailable) |> Expect.stringContains "the step is on the row" "data-testid=\"tweak-next\""
      for step in [ NextStep.UndoOrReload; NextStep.ReloadOnly ] do
        NextStep.sentence step |> Expect.stringContains "both name evaluating again" "evaluate the binding again"
        NextStep.sentence step |> Expect.stringContains "both name Rebuild and reload" "Rebuild and reload"

    testPropertyWithConfig propConfig "WHY — a next step never points at a control the row does not draw: Undo only when the undo button is enabled, the row's control only when it has one"
      (Prop.forAll (arbitrary (Gen.zip3 (Gen.elements states) (Gen.elements [ Control.NoControl; Control.RealStepper(9.8, Step.Fraction 1); Control.Toggle true ]) (Gen.elements [ HistoryStep.StepAvailable; HistoryStep.NothingToStep; HistoryStep.OtherRowFirst "x" ])))
        (fun (state, control, undo) ->
          let view = { rowOf state control with Undo = undo }
          let html = strip view
          match NextStep.ofRow view with
          | NextStep.UndoOrReload -> html.Contains "data-testid=\"tweak-undo\"" && not (html.Contains "tweak-undo-why")
          | NextStep.UseTheControl
          | NextStep.UseTheControlAgain -> html.Contains "<input"
          | NextStep.ReloadOnly
          | NextStep.NoNextStep -> true))
  ]

[<Tests>]
let applyToSessionTests =
  testList "applying the file's value to a session nothing watches" [
    let filePlace = { gravity with Text = "9.9" }
    let differs = PersistenceState.DiffersFromFile("9.8", filePlace)
    let rowOfFile watching state =
      { rowOf state (Control.RealStepper(9.9, Step.Fraction 1)) with Place = PlaceOf.Placed filePlace; Watching = watching }
    let part = { Binding = "feel"; Labels = [ "JumpVelocity" ] }

    testCase "WHY — the code is the binding rebound to the file's expression, so the session holds what the file holds" <| fun _ ->
      ApplyToSession.codeFor key "9.9" |> Expect.equal "one line, one statement" (Ok "let gravity = 9.9;;")
      ApplyToSession.codeFor key "  gravity * 2.0 " |> Expect.equal "an expression, trimmed" (Ok "let gravity = gravity * 2.0;;")

    testCase "WHY — what a rebinding cannot honestly do is refused with its reason: a part of a value, a spanning expression, a statement end inside" <| fun _ ->
      ApplyToSession.codeFor part "13.2" |> Expect.equal "a field cannot be rebound alone" (Error ApplyRefusal.PartOfAValue)
      ApplyToSession.codeFor key "1.0 +\n 2.0" |> Expect.equal "it spans lines" (Error ApplyRefusal.SpansLines)
      ApplyToSession.codeFor key "1.0;; 2.0" |> Expect.equal "it would end the statement early" (Error ApplyRefusal.CarriesStatementEnd)
      for refusal in [ ApplyRefusal.PartOfAValue; ApplyRefusal.SpansLines; ApplyRefusal.CarriesStatementEnd ] do
        ApplyRefusal.text refusal |> Expect.isNotEmpty "every refusal has words"

    testCase "WHY — the button is offered only where it is the answer: the file's value differs, the file is not watched, and the row is a whole binding" <| fun _ ->
      let offer watching state key' =
        match ApplyToSession.offerFor key' (rowOfFile watching state) with
        | ApplyOffer.Offered _ -> "offered"
        | ApplyOffer.NotOffered -> "not offered"
      offer WatchStatus.NotWatched differs key |> Expect.equal "unwatched and differing" "offered"
      offer WatchStatus.Watched differs key |> Expect.equal "a watched file reaches the session by itself" "not offered"
      offer WatchStatus.NotWatched (PersistenceState.InSource filePlace) key |> Expect.equal "nothing differs" "not offered"
      offer WatchStatus.NotWatched differs part |> Expect.equal "a part of a value" "not offered"

    testCase "WHY — the row draws the button with its words and its own test id, and no second sentence repeats what the button says" <| fun _ ->
      let drawn = strip (rowOfFile WatchStatus.NotWatched differs)
      drawn |> Expect.stringContains "its test id" "data-testid=\"tweak-apply\""
      drawn |> Expect.stringContains "its words" ApplyToSession.words
      drawn |> Expect.stringContains "it posts to its own route" ApplyToSession.Endpoint
      drawn.Contains "tweak-next" |> Expect.isFalse "the button is the next step, so there is no sentence for it"
      let watched = strip (rowOfFile WatchStatus.Watched differs)
      watched.Contains "tweak-apply" |> Expect.isFalse "a watched file has no button"

    testCase "WHY — the route reads the session, the page and the file's text from the signals the row staged, and turns away what the row would not have offered" <| fun _ ->
      let asked (row: string) (text: string) : string =
        use doc =
          System.Text.Json.JsonDocument.Parse(
            sprintf "{\"%s\":\"0a2b3c4d\",\"%s\":\"client-1\",\"%s\":\"%s\",\"%s\":\"%s\"}" DashboardTypes.Signals.ViewingSessionId DashboardTypes.Signals.ClientId TweakSignals.Row row TweakSignals.SeenText text)
        match LiveBindingsTweakRoute.applyAskedOf doc.RootElement with
        | LiveBindingsTweakRoute.ApplyAsked.Apply(_, clientId, code) -> sprintf "apply %s: %s" clientId code
        | LiveBindingsTweakRoute.ApplyAsked.NotAnApply reason -> sprintf "refused: %s" reason
      asked "gravity" "9.9" |> Expect.equal "a whole binding" "apply client-1: let gravity = 9.9;;"
      asked "feel/JumpVelocity" "13.2" |> Expect.equal "a part of a value, in the refusal's own words" (sprintf "refused: %s" (ApplyRefusal.text ApplyRefusal.PartOfAValue))
      (asked "" "9.9").StartsWith "refused: " |> Expect.isTrue "no row is not a request"
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

let private callee = { gravity with Address = addressOf "initPlayer" []; Text = "{ Health = 100 }"; Span = { gravity.Span with Line = 5 } }

let private followedWith (callers: CallerCount) : ThroughCall =
  ThroughCall.Through { Callee = "initPlayer"; Place = { gravity with Address = addressOf "initPlayer" [ PathStep.RecordField "Health" ] }; Callers = callers }

/// Every way a call can fail to be followed, one of each.
let private refusals : Refusal list =
  [ Refusal.NotAPlainCall
    Refusal.NoCalleeInFiles "initPlayer"
    Refusal.CalleeFilesUnknown "Other.fs: the file does not parse"
    Refusal.CalleeDeclaredSeveralTimes("initPlayer", [ callee; { callee with File = "/work/game/Other.fs" } ])
    Refusal.CalleeNotRead("initPlayer", "/work/game/State.fs")
    Refusal.ResultNotSpelled callee
    Refusal.ResultIsAnExpression { callee with Text = "hp"; Address = addressOf "initPlayer" [ PathStep.RecordField "Health" ] } ]

[<Tests>]
let throughTests =
  testList "what a row owes to a call is said in words" [

    testCase "WHY — a row whose literal is in a callee says which callee builds it and how many calls the write reaches" <| fun _ ->
      let said (callers: CallerCount) = (describeThrough (followedWith callers)).Value
      (said (CallerCount.Counted 3)).Detail |> Expect.stringContains "the callee and the number" "initPlayer"
      (said (CallerCount.Counted 3)).Detail |> Expect.stringContains "the number" "3 places"
      (said (CallerCount.Counted 3)).Detail |> Expect.stringContains "and what the change does" "changes all of them"
      (said (CallerCount.Counted 3)).Tone |> Expect.equal "more than one place is something to know, in the notice colour and never the warning one" Tone.Info
      (said (CallerCount.Counted 1)).Detail |> Expect.stringContains "one place says only this one" "Only one place"
      (said (CallerCount.Counted 1)).Tone |> Expect.equal "one call is nothing to warn about" Tone.Quiet
      for callers in [ CallerCount.Counted 0; CallerCount.Counted 1; CallerCount.Counted 3; CallerCount.NotCounted "x" ] do
        let detail = (said callers).Detail
        detail.Contains "literal" |> Expect.isFalse "no code vocabulary: the pane talks about values, not literals"
        let tone = (said callers).Tone
        (tone = Tone.Notice || tone = Tone.Bad) |> Expect.isFalse "never the amber or red that mean a person must act"
      (said (CallerCount.NotCounted "Other.fs does not parse")).Detail |> Expect.stringContains "the count is not invented" "could not be counted"
      (said (CallerCount.NotCounted "Other.fs does not parse")).Detail |> Expect.stringContains "and says why" "Other.fs does not parse"

    testCase "WHY — every refusal says something, and says the one thing that explains it (the callee, the reason or the expression), so no refusal reads as an empty row" <| fun _ ->
      let explainedBy (refusal: Refusal) : string =
        match refusal with
        | Refusal.NotAPlainCall -> ""
        | Refusal.NoCalleeInFiles name -> name
        | Refusal.CalleeFilesUnknown reason -> reason
        | Refusal.CalleeDeclaredSeveralTimes(name, _) -> name
        | Refusal.CalleeNotRead(name, _) -> name
        | Refusal.ResultNotSpelled owner -> owner.Address.BindingName
        | Refusal.ResultIsAnExpression expression -> expression.Text
      for refusal in refusals do
        match describeThrough (ThroughCall.CouldNotFollow refusal), refusal with
        | None, Refusal.NotAPlainCall -> ()
        | None, other -> failtestf "%A said nothing" other
        | Some said, Refusal.NotAPlainCall -> failtestf "a record is not a call, yet it said %s" said.Detail
        | Some said, _ ->
          said.Detail |> Expect.stringContains "the one thing that explains it" (explainedBy refusal)
          said.Tone |> Expect.equal "an explanation, not a failure" Tone.Quiet

    testCase "WHY — a callee declared in two places names both, and picks neither" <| fun _ ->
      let said = (describeThrough (ThroughCall.CouldNotFollow(Refusal.CalleeDeclaredSeveralTimes("initPlayer", [ callee; { callee with File = "/work/game/Other.fs" } ])))).Value
      said.Detail |> Expect.stringContains "the first file" "Tuning.fs"
      said.Detail |> Expect.stringContains "the second file" "Other.fs"
      said.Detail |> Expect.stringContains "says it will not pick" "will not pick"

    testCase "WHY — a row that is not built by a call says nothing about one" <| fun _ ->
      describeThrough ThroughCall.NoCall |> Expect.isNone "nothing to add"

    testCase "WHY — a refusal that has an expression names it, so the row can say what it is instead of calling it a parameter it cannot know" <| fun _ ->
      (describeThrough (ThroughCall.CouldNotFollow(Refusal.ResultIsAnExpression { callee with Text = "hp" }))).Value.Detail
      |> Expect.stringContains "the expression" "hp"

    testCase "the strip carries the consequence on its own line, with a test id, and a row with no call has none" <| fun _ ->
      let withThrough = { rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1)) with Through = followedWith (CallerCount.Counted 3) }
      renderStrip key withThrough Placement.OnLeafRow |> decoded |> Expect.stringContains "the consequence" "data-testid=\"tweak-through\""
      (strip (rowOf (PersistenceState.InSource gravity) Control.NoControl)).Contains "tweak-through" |> Expect.isFalse "no call, no line"
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
          // A binding no file holds is said once in the title bar, so its row is a marker with no words and no input.
          match stripShown state with
          | StripShown.Marker -> not (html.Contains "<input") && not (html.Contains "live-state")
          | StripShown.Strip -> not (html.Contains "<input") && html.Contains (describe state).Label
        | _ -> true))

    testCase "undo and redo only where they have something to act on, the same on every row: none without a write, Undo with one to take back, Redo with one undone" <| fun _ ->
      let base' = rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1))
      count "tweak-undo" (strip base') |> Expect.equal "no history, no undo" 0
      count "tweak-redo" (strip base') |> Expect.equal "no history, no redo" 0
      let withUndo = strip { base' with Undo = HistoryStep.StepAvailable; Redo = HistoryStep.NothingToStep }
      withUndo |> Expect.stringContains "undo" "data-testid=\"tweak-undo\""
      withUndo.Contains "tweak-redo" |> Expect.isFalse "nothing undone, so no redo, and no sentence about it"
      let withRedo = strip { base' with Undo = HistoryStep.NothingToStep; Redo = HistoryStep.StepAvailable }
      withRedo |> Expect.stringContains "redo" "data-testid=\"tweak-redo\""
      withRedo.Contains "tweak-undo" |> Expect.isFalse "nothing to take back, so no undo"
      let waiting = strip { base' with Undo = HistoryStep.OtherRowFirst "Game.Tuning.maxHealth" }
      waiting |> Expect.stringContains "says whose turn it is" "Game.Tuning.maxHealth"
      waiting |> Expect.stringContains "and the button is disabled in the markup" "disabled"

    testCase "WHY — undo and redo carry their words, and a step a later write blocks says why in text on the row, not only in a tooltip; a step with nothing to act on says nothing" <| fun _ ->
      let base' = rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1))
      let html = strip { base' with Undo = HistoryStep.StepAvailable; Redo = HistoryStep.StepAvailable }
      html |> Expect.stringContains "the Undo word is on the button" (sprintf ">%s<" (historyWord "undo"))
      html |> Expect.stringContains "the Redo word is on the button" (sprintf ">%s<" (historyWord "redo"))
      html.Contains "-why" |> Expect.isFalse "available steps need no reason"
      let blocked = strip { base' with Undo = HistoryStep.OtherRowFirst "Game.Tuning.maxHealth"; Redo = HistoryStep.NothingToStep }
      blocked |> Expect.stringContains "whose turn it is, as text" "data-testid=\"tweak-undo-why\""
      blocked.Contains "tweak-redo" |> Expect.isFalse "no redo, so no sentence about one"
      for verb in [ "undo"; "redo" ] do
        historyWhy verb HistoryStep.StepAvailable |> Expect.isNone "a step that can be taken has no reason"
        historyWhy verb HistoryStep.NothingToStep |> Expect.isNone "a step that is not drawn has no reason either"
        (historyWhy verb (HistoryStep.OtherRowFirst "Game.Tuning.maxHealth")).Value |> Expect.stringContains "a blocked step names the verb" (historyWord verb)

    testCase "WHY — a bool is a switch you can read: the checkbox says it is a switch and whether it is on, and the word for where it stands is text beside it" <| fun _ ->
      for value in [ true; false ] do
        let toggle = place (switchWord value) (ItemKind.Knob(LiteralValue.Bool value))
        let html = strip { rowOf (PersistenceState.InSource toggle) (Control.Toggle value) with Place = PlaceOf.Placed toggle }
        html |> Expect.stringContains "a switch" "role=\"switch\""
        html |> Expect.stringContains "its state for a screen reader" (sprintf "aria-checked=\"%s\"" (switchWord value))
        textOfTestId "tweak-toggle-word" html |> Expect.equal "its state in text" (Some(switchWord value))
        html.Contains " checked" |> Expect.equal "the checked attribute follows the value" value
      switchWord true |> Expect.notEqual "the two words differ" (switchWord false)

    testPropertyWithConfig propConfig "WHY — a real's box and the row's value text are one spelling of one number: a number with up to one decimal reads the same in both"
      (Prop.forAll (arbitrary (Gen.choose (-9999, 9999) |> Gen.map (fun tenths -> float tenths / 10.0))) (fun value ->
        let literal = place (FSharpFloat.text value) (ItemKind.Knob(LiteralValue.Real value))
        let step = Step.ofRealText literal.Text
        let html = strip { rowOf (PersistenceState.InSource literal) (Control.RealStepper(value, step)) with Place = PlaceOf.Placed literal }
        textOfTestId "tweak-readout" html = Some(FSharpFloat.text value)))

    testCase "the reload the write caused is shown on the row; a file hot reload is not watching says so once, in one voice, and says the write happened if it did" <| fun _ ->
      let row = rowOf (PersistenceState.DiffersFromFile("9.8", { gravity with Text = "9.9" })) (Control.RealStepper(9.9, Step.Fraction 1))
      let notWatched = { row with Reload = RowReload.Watching("Tuning.fs", ReloadWatch.NoNewReport); Watching = WatchStatus.NotWatched }
      let html = strip notWatched
      html |> Expect.stringContains "the not-watched sentence" "data-testid=\"tweak-watching\""
      html.Contains "tweak-reload" |> Expect.isFalse "no second sentence about a verdict that cannot come"
      // "The running session still has the old value" is true only while the file is ahead of the session. Once the session holds the
      // file's value (the row is in its file, after an Apply or an eval), the sentence would be a lie, and so would any hot reload
      // verdict for a file nothing watches: both go.
      let applied = { notWatched with State = PersistenceState.InSource gravity }
      strip applied |> fun after -> after.Contains "tweak-watching" |> Expect.isFalse "the session has the file's value, so no sentence says it does not"
      let nothingToChange : ReloadFacts =
        { Case = ReloadCase.NoEffect
          Patched = 0
          Considered = 1
          Message = "m"
          SuggestedAction = ""
          Mechanism = Features.ReloadOutcome.PatchMechanism.NoPatch
          Declarations = []
          Callers = Features.CallerState.CallersState.CallersNotReported }
      let reported = { applied with Reload = RowReload.Watching("Tuning.fs", ReloadWatch.Reported nothingToChange) }
      strip reported |> fun after -> after.Contains "tweak-reload" |> Expect.isFalse "a verdict from hot reload means nothing for a file nothing watches"
      textOfTestId "tweak-watching" html |> Expect.equal "it says the write happened" (Some(notWatchedSentence gravity.File "9.8" "9.9"))
      // A row nobody has written says nothing about the file not being watched: said under every row of a file, once any row was
      // written, it was ten copies of one sentence. The pane says it once, in its edit caption.
      textOfTestId "tweak-watching" (strip { row with Watching = WatchStatus.NotWatched })
      |> Expect.isNone "before any write the row is silent"
      let watched = strip { row with Reload = RowReload.Watching("Tuning.fs", ReloadWatch.NoNewReport); Watching = WatchStatus.Watched }
      watched |> Expect.stringContains "a watched file shows the reload line" "data-testid=\"tweak-reload\""
      watched.Contains "tweak-watching" |> Expect.isFalse "and no not-watched sentence"

    testCase "a value that is simply in its file keeps its sentence in the tooltip, and anything a person must act on or understand keeps it on the row" <| fun _ ->
      strip (rowOf (PersistenceState.InSource gravity) (Control.RealStepper(9.8, Step.Fraction 1))) |> fun html -> html.Contains "tweak-detail" |> Expect.isFalse "in source: no wall of text"
      strip (rowOf (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt) Control.NoControl) |> fun html -> html.Contains "tweak-detail" |> Expect.isFalse "REPL only is said once in the title bar, not on every row"
      for state in states do
        match state with
        // A row that differs from its file labels both numbers and says the next step, and a formula's expression is in the field
        // beside the tag: their sentences repeated what was already on the row, so they are the tooltip's.
        | PersistenceState.InSource _
        | PersistenceState.DiffersFromFile _
        | PersistenceState.Derived(_, DerivedWhy.AFormula)
        | PersistenceState.Derived(_, DerivedWhy.AContainer) ->
          detailShown state |> Expect.equal (sprintf "%s keeps its sentence in the tooltip" (PersistenceState.token state)) DetailShown.InTheTooltipOnly
        | other -> detailShown other |> Expect.equal (sprintf "%s explains itself on the row" (PersistenceState.token other)) DetailShown.OnTheRow

    testCase "WHY — a REPL-only row draws a marker with its state and nothing visible, because the title bar says it once and ten rows saying it again cost ten lines" <| fun _ ->
      for state in states do
        match state with
        | PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt -> stripShown state |> Expect.equal "a marker" StripShown.Marker
        | other -> stripShown other |> Expect.equal (sprintf "%s draws its strip" (PersistenceState.token other)) StripShown.Strip
      let html = strip (rowOf (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt) Control.NoControl)
      html |> Expect.stringContains "the state is still readable by a browser or an editor" "data-state=\"NotInAFile\""
      html |> Expect.stringContains "under the row's own test id" "data-testid=\"tweak-row\""
      html |> Expect.stringContains "and the morph can still find it by its id" (sprintf "id=\"%s\"" (TweakSignals.stripId key))
      html.Contains "live-state" |> Expect.isFalse "no chip"
      html.Contains "REPL only" |> Expect.isFalse "no words on the row"

    testCase "TweakView.replOnlyCount counts the top-level bindings no file holds, and nothing else" <| fun _ ->
      let view : TweakView =
        { Rows =
            Map.ofList
              [ RowKey.top "speed", rowOf (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt) Control.NoControl
                RowKey.top "scores", rowOf (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt) Control.NoControl
                RowKey.top "gravity", rowOf (PersistenceState.InSource gravity) Control.NoControl
                RowKey.field (RowKey.top "scores") "x", rowOf (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt) Control.NoControl ] }
      TweakView.replOnlyCount view [ "speed"; "scores"; "gravity"; "unlisted" ] |> Expect.equal "two bindings, not the field, not the file-held one" 2
      TweakView.replOnlyCount TweakView.none [ "speed" ] |> Expect.equal "a session whose files were not asked says nothing" 0

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
  Dock.renderDockWith (SageFs.View.recommended SageFs.ViewWorkflow.Repl) None view DockPanes.SessionInView "abcd1234" (Dock.WalkedBindings(paneView bindings)) None
  |> renderNode

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

type NumbersRig = { Mass: float; Drag: float }

[<Tests>]
let numberAgreementTests =
  testList "one value is one text, wherever the pane draws it" [

    testCase "WHY — a record's parent text and the child row's box print the same float the same way (80.0, not 80 in one and 80.0 in the other)" <| fun _ ->
      let walked = LiveValueTree.buildValueNodeIn LiveValueTree.WalkMode.Safe "rig" (box { Mass = 80.0; Drag = 0.25 })
      let mass = walked.Children |> List.find (fun child -> child.Label = "Mass")
      walked.Preview |> Expect.stringContains "the parent shows the child's text" (sprintf "Mass = %s" mass.Preview)
      mass.Preview |> Expect.equal "the walk prints a whole float with its point" "80.0"
      // The file spells it `80.0`; the row's box shows the same text the child row's value shows.
      let literal = place "80.0" (ItemKind.Knob(LiteralValue.Real 80.0))
      let html = strip { rowOf (PersistenceState.InSource literal) (Control.RealStepper(80.0, Step.ofRealText literal.Text)) with Place = PlaceOf.Placed literal }
      textOfTestId "tweak-readout" html |> Expect.equal "the box agrees with the value text" (Some mass.Preview)

    testPropertyWithConfig propConfig "a literal the file spells equals the live value the walk prints for it, for every float, so a row in step is never reported as differing over formatting"
      (Prop.forAll (arbitrary (Gen.map2 (fun whole tenths -> float whole + float tenths / 10.0) (Gen.choose (-500, 500)) (Gen.choose (0, 9)))) (fun value ->
        let walked = LiveValueTree.buildValueNodeIn LiveValueTree.WalkMode.Safe "x" (box value)
        LiveShape.equalsLiteral (LiteralValue.Real value) walked.Preview))
  ]

[<Tests>]
let situationAndTagTests =
  testList "one sentence per reload situation, and one badge vocabulary for the row and the legend" [

    testCase "WHY — a patch that is applied and not seen running and one that never ran are one situation with one sentence, because a person cannot tell them apart" <| fun _ ->
      ReloadSituation.ofCase ReloadCase.PatchPending |> Expect.equal "pending and never entered are the same situation" (ReloadSituation.ofCase ReloadCase.NeverEntered)
      reloadMeaning ReloadCase.PatchPending |> Expect.equal "so they read the same" (reloadMeaning ReloadCase.NeverEntered)

    testCase "WHY — no reload sentence says the app has the new code, because the header can say the session runs older code beside it; each begins with the write and none names the verdict" <| fun _ ->
      for case in ReloadCase.all do
        let text, _ = reloadMeaning case
        text.StartsWith "Saved to the file" |> Expect.isTrue (sprintf "%s begins with what was written" (ReloadCase.token case))
        for forbidden in [ "new code"; "has the new"; "applied"; ReloadCase.token case; "`" ] do
          text.Contains forbidden |> Expect.isFalse (sprintf "%s does not say %s: %s" (ReloadCase.token case) forbidden text)

    testCase "a reload that went wrong is bad news and says what to do: restart the app, or fix the file or undo the write" <| fun _ ->
      for case in [ ReloadCase.RestartRequired; ReloadCase.CompileFailed; ReloadCase.EvalFailed ] do
        let text, tone = reloadMeaning case
        tone |> Expect.equal (sprintf "%s is bad news" (ReloadCase.token case)) Tone.Bad
        (text.Contains "Restart the app" || text.Contains "undo the write") |> Expect.isTrue (sprintf "%s names a step: %s" (ReloadCase.token case) text)
      reloadMeaning ReloadCase.Patched |> snd |> Expect.equal "a patch the app runs is good news" Tone.Good

    testCase "every badge: the tags are exactly the cases of the closed set, each with its own words and its own plain line" <| fun _ ->
      StateTag.all.Length |> Expect.equal "the list names every case" (Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<StateTag>).Length)
      (StateTag.all |> List.distinct).Length |> Expect.equal "no tag is listed twice" StateTag.all.Length
      (StateTag.all |> List.map tagLabel |> List.distinct).Length |> Expect.equal "no two tags share a badge" StateTag.all.Length
      for tag in StateTag.all do
        String.IsNullOrWhiteSpace(tagLabel tag) |> Expect.isFalse (sprintf "%A has a label" tag)
        String.IsNullOrWhiteSpace(tagMeaning tag) |> Expect.isFalse (sprintf "%A has a meaning" tag)
        (tagMeaning tag).Contains "`" |> Expect.isFalse (sprintf "%A has no backtick" tag)

    testPropertyWithConfig propConfig "WHY — the badge a row wears is the tag's label, for any state, so the legend that lists the tags can never be missing one"
      (Prop.forAll (arbitrary (Gen.zip3 genLive genSource genOutcome)) (fun (live, source, outcome) ->
        let state = PersistenceState.derive live source outcome
        let tag = StateTag.ofState state
        (describe state).Label = tagLabel tag && List.contains tag StateTag.all && (badgeTitle state).Contains(tagMeaning tag)))

    testCase "every state of the fixture list has a badge title that starts with what the badge means" <| fun _ ->
      for state in states do
        (badgeTitle state).StartsWith(tagMeaning (StateTag.ofState state)) |> Expect.isTrue (sprintf "%s: %s" (PersistenceState.token state) (badgeTitle state))

    testCase "WHY — a binding no file defines wears its badge on its own line, because its strip draws nothing and the title bar's count has to match rows a person can see" <| fun _ ->
      for state in states do
        match state with
        | PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt -> badgePlace state |> Expect.equal "on the row" BadgePlace.OnTheRow
        | other -> badgePlace other |> Expect.equal (sprintf "%s is in its strip" (PersistenceState.token other)) BadgePlace.InTheStrip
  ]
