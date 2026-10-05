/// What a live binding's row knows about the source file it may be written back to. The persistence state is ONE closed answer
/// derived from three facts (what the value is, what the files say, what the last action did), so the properties here are about
/// the derivation: it never guesses between two places, a write in flight beats everything, a value that runs code stays held, and
/// the knob's arithmetic lands on a number the author could have typed.
module SageFs.Tests.BindingTweakTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.TweakLog
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak
open SageFs.Features.Tweak.BindingTweak
open SageFs.Tests.SharedGenerators

let tuningFile =
  """module Game.Tuning

let gravity = 9.8
let maxHealth = 100
let mask = 0x1F
let hard = true
let title = "Nudge"
let mode = Hard
let jump = gravity * 2.0

let tuning =
  { JumpVelocity = 13.2
    MaxHealth = 100
    Scaled = scale 3 4.5 }
"""

let spanAt (line: int) : SourceSpan = { Line = line; Column = 0; EndLine = line; EndColumn = 1 }

let addressOf (binding: string) (path: PathStep list) : TweakAddress = { ModulePath = [ "Game"; "Tuning" ]; BindingName = binding; Path = path }

let sourceAt (file: string) (binding: string) (text: string) (kind: ItemKind) : SourceRef =
  { File = file
    Address = addressOf binding []
    Text = text
    Hash = Convert.ToHexStringLower(Text.Encoding.UTF8.GetBytes(file + binding + text)).PadRight(64, '0').Substring(0, 64)
    Span = spanAt 3
    Kind = kind }

let real (file: string) (binding: string) (text: string) : SourceRef =
  sourceAt file binding text (ItemKind.Knob(LiteralValue.Real(Double.Parse(text, Globalization.CultureInfo.InvariantCulture))))

let fileOf (name: string) (items: SourceRef list) : FileInspection =
  { File = name; Watching = WatchStatus.Watched; Items = items; Wholes = []; UndoSteps = 0; RedoSteps = 0 }

let inspectedWith (file: string) (text: string) (target: InspectTarget) : SourceRef list =
  match inspect file text EventLog.empty target with
  | Ok(NudgeOutcome.Inspected inspection) -> inspection.Items |> List.map (SourceRef.ofItem file)
  | other -> failtestf "inspect did not list the file: %A" other

/// The door's own inspection of a file's text, as the pane's index holds it: the listing, and the whole right-hand side of every
/// binding named in `names` that the listing carries only parts of.
let inspectedFile (file: string) (text: string) (names: string list) : FileInspection =
  let items = inspectedWith file text InspectTarget.WholeFile
  let wholes =
    SourceIndex.wholesToRead (Set.ofList names) items
    |> List.collect (fun address -> inspectedWith file text (InspectTarget.OneAddress address))
  { fileOf file items with Wholes = wholes }

let indexOf (files: FileInspection list) : SourceIndex = { Files = files; Unreadable = [] }

let anySource (kind: ItemKind) : SourceRef = sourceAt "a.fs" "x" "1" kind

// ── generators ──

let genPreview : Gen<string> = Gen.elements [ "9.8"; "100"; "true"; "\"Nudge\""; "'c'"; "Hard"; "null"; "<error>" ]

let genLive : Gen<LiveShape> =
  Gen.oneof [ genPreview |> Gen.map LiveShape.Scalar; Gen.constant LiveShape.Container; Gen.constant LiveShape.NotRead; Gen.constant LiveShape.Unreadable ]

let genKind : Gen<ItemKind> =
  Gen.oneof
    [ Gen.elements [ 0.0; 9.8; 13.2; -4.5 ] |> Gen.map (fun v -> ItemKind.Knob(LiteralValue.Real v))
      Gen.elements [ 0L; 100L; -7L ] |> Gen.map (fun v -> ItemKind.Knob(LiteralValue.Integer v))
      Gen.elements [ true; false ] |> Gen.map (fun v -> ItemKind.Knob(LiteralValue.Bool v))
      Gen.elements [ "Nudge"; "" ] |> Gen.map (fun v -> ItemKind.Knob(LiteralValue.Text v))
      Gen.elements [ "Hard"; "Easy" ] |> Gen.map (fun v -> ItemKind.Knob(LiteralValue.Case v))
      Gen.constant ItemKind.Formula ]

let genRef : Gen<SourceRef> =
  gen {
    let! kind = genKind
    let! file = Gen.elements [ "a.fs"; "b.fs" ]
    let! text = Gen.elements [ "1"; "9.8"; "x * 2.0"; "true" ]
    return sourceAt file "x" text kind
  }

let genSource : Gen<SourceFacts> =
  Gen.oneof
    [ Gen.constant SourceFacts.NoFileBindsIt
      Gen.elements [ "a.fs: does not parse" ] |> Gen.map SourceFacts.FilesUnknown
      genRef |> Gen.map SourceFacts.OneSource
      Gen.listOfLength 2 genRef |> Gen.map SourceFacts.ManySources
      genRef |> Gen.map SourceFacts.PartNotSpelled ]

let genRefusal : Gen<NudgeRefusal> =
  Gen.elements
    [ NudgeRefusal.NothingToUndo
      NudgeRefusal.NothingToRedo
      NudgeRefusal.NotALiteral "gravity * 2.0"
      NudgeRefusal.LiteralNotReadable(LiteralKindName.Real, "abc")
      NudgeRefusal.SourceMoved("a", "b", "9.9")
      NudgeRefusal.AddressMoved(addressOf "x" [], addressOf "y" [])
      NudgeRefusal.AddressGone(ResolveError.BindingRemoved(addressOf "x" [])) ]

let genOutcome : Gen<RowOutcome> =
  Gen.oneof
    [ Gen.constant RowOutcome.NoOutcome
      Gen.zip (Gen.elements [ WriteKind.SetTo "10.0"; WriteKind.UndoStep; WriteKind.RedoStep ]) genRef |> Gen.map RowOutcome.InFlight
      Gen.zip genRefusal genRef |> Gen.map RowOutcome.Rejected ]

let arbitrary (gen: Gen<'a>) = Arb.fromGen gen

let walkedPreview (value: obj) : string = (LiveValueTree.buildValueNode "x" value).Preview

[<Tests>]
let liveShapeTests =
  testList "a row's live value: what it is, and when it equals the file's literal" [

    testCase "a leaf is a scalar, a held value is not read, a part-bearing value is a container, a cut branch is unreadable" <| fun _ ->
      let node kind children : LiveValueTree.LiveValueNode =
        { Label = "x"; TypeName = "t"; Preview = "p"; Kind = kind; Children = children; BestEffort = false; Depth = 0 }
      LiveShape.ofNode (node LiveValueTree.NodeKind.Leaf []) |> Expect.equal "leaf" (LiveShape.Scalar "p")
      LiveShape.ofNode (node (LiveValueTree.NodeKind.NotEvaluated LiveValueTree.NotEvaluatedReason.GetterRunsCode) []) |> Expect.equal "held" LiveShape.NotRead
      LiveShape.ofNode (node LiveValueTree.NodeKind.Record [ node LiveValueTree.NodeKind.Leaf [] ]) |> Expect.equal "record" LiveShape.Container
      LiveShape.ofNode (node LiveValueTree.NodeKind.Union []) |> Expect.equal "a case with no fields is spelled as its name" (LiveShape.Scalar "p")
      LiveShape.ofNode (node LiveValueTree.NodeKind.Union [ node LiveValueTree.NodeKind.Leaf [] ]) |> Expect.equal "a case with fields is a container" LiveShape.Container
      LiveShape.ofNode (node LiveValueTree.NodeKind.Cycle []) |> Expect.equal "cycle" LiveShape.Unreadable
      LiveShape.ofNode (node LiveValueTree.NodeKind.Truncated []) |> Expect.equal "truncated" LiveShape.Unreadable

    // The contract with the walk's own printer: whatever the real walk prints for a value, the file's literal of that value prints the same.
    testPropertyWithConfig propConfig "a literal prints exactly as the live walk prints the same value"
      (Prop.forAll (arbitrary (Gen.elements [ 9.8; 0.12; 13.2; -4.5; 100.0; 0.0; 1.5e-3; 12345.678 ])) (fun v ->
        LiveShape.printed (LiteralValue.Real v) = walkedPreview (box v)))

    testPropertyWithConfig propConfig "an integer, a bool, a string and a char print as the live walk prints them"
      (Prop.forAll (arbitrary (Gen.choose (-100000, 100000))) (fun i ->
        LiveShape.printed (LiteralValue.Integer(int64 i)) = walkedPreview (box i)
        && LiveShape.printed (LiteralValue.Bool(i % 2 = 0)) = walkedPreview (box (i % 2 = 0))
        && LiveShape.printed (LiteralValue.Text(string i)) = walkedPreview (box (string i))
        && LiveShape.printed (LiteralValue.Char 'q') = walkedPreview (box 'q')))

    testCase "a case literal equals a union value that prints as its name" <| fun _ ->
      LiveShape.equalsLiteral (LiteralValue.Case "Hard") "Hard" |> Expect.isTrue "same name"
      LiveShape.equalsLiteral (LiteralValue.Case "Hard") "Easy" |> Expect.isFalse "other name"
  ]

[<Tests>]
let sourceIndexTests =
  testList "what the session's files say about a binding's name" [

    testCase "no file declares it: the REPL bound it, and nothing is unknown" <| fun _ ->
      let index = indexOf [ fileOf "a.fs" [ real "a.fs" "gravity" "9.8" ] ]
      SourceIndex.factsFor index "speed" [] |> Expect.equal "no file" SourceFacts.NoFileBindsIt

    testCase "one declaration is one source" <| fun _ ->
      let gravity = real "a.fs" "gravity" "9.8"
      SourceIndex.factsFor (indexOf [ fileOf "a.fs" [ gravity ] ]) "gravity" [] |> Expect.equal "one" (SourceFacts.OneSource gravity)

    testCase "two files declaring the name is ambiguity, listing both and picking neither" <| fun _ ->
      let a = real "a.fs" "gravity" "9.8"
      let b = real "b.fs" "gravity" "9.8"
      match SourceIndex.factsFor (indexOf [ fileOf "a.fs" [ a ]; fileOf "b.fs" [ b ] ]) "gravity" [] with
      | SourceFacts.ManySources places -> places |> Expect.equal "both, in order" [ a; b ]
      | other -> failtestf "expected the ambiguity to be named, got %A" other

    testCase "the same name twice in one file is also ambiguity" <| fun _ ->
      let first = real "a.fs" "gravity" "9.8"
      let second = { real "a.fs" "gravity" "12.0" with Span = spanAt 9 }
      match SourceIndex.factsFor (indexOf [ fileOf "a.fs" [ first; second ] ]) "gravity" [] with
      | SourceFacts.ManySources places -> places.Length |> Expect.equal "both" 2
      | other -> failtestf "expected the ambiguity to be named, got %A" other

    testCase "a file that cannot be read, with no declaration found elsewhere, means NO FILE cannot be said" <| fun _ ->
      let index = { Files = [ fileOf "a.fs" [] ]; Unreadable = [ "b.fs", "the file does not parse" ] }
      match SourceIndex.factsFor index "gravity" [] with
      | SourceFacts.FilesUnknown reason -> reason |> Expect.stringContains "names the file and why" "b.fs: the file does not parse"
      | other -> failtestf "expected FilesUnknown, got %A" other

    testCase "a declaration found in a readable file is still one source though another file is unreadable" <| fun _ ->
      let gravity = real "a.fs" "gravity" "9.8"
      let index = { Files = [ fileOf "a.fs" [ gravity ] ]; Unreadable = [ "b.fs", "the file does not parse" ] }
      SourceIndex.factsFor index "gravity" [] |> Expect.equal "the unreadable file does not unsettle a finding" (SourceFacts.OneSource gravity)

    testCase "the door's own inspection of a real file: a binding, a record field and a field the file does not spell as a literal" <| fun _ ->
      let index = indexOf [ inspectedFile "Tuning.fs" tuningFile [ "gravity"; "tuning" ] ]
      match SourceIndex.factsFor index "gravity" [] with
      | SourceFacts.OneSource gravity -> gravity.Text |> Expect.equal "the literal's text" "9.8"
      | other -> failtestf "gravity: %A" other
      match SourceIndex.factsFor index "tuning" [ PathStep.RecordField "JumpVelocity" ] with
      | SourceFacts.OneSource field -> field.Text |> Expect.equal "the field's text" "13.2"
      | other -> failtestf "tuning/{JumpVelocity}: %A" other
      match SourceIndex.factsFor index "tuning" [ PathStep.RecordField "NoSuchField" ] with
      | SourceFacts.PartNotSpelled owner -> SourceRef.addressText owner |> Expect.equal "owner named" "Game.Tuning.tuning"
      | other -> failtestf "a field that is not there: %A" other

    testPropertyWithConfig propConfig "a part is looked up in the one file that declares the binding, never in another"
      (Prop.forAll (arbitrary (Gen.elements [ "a.fs"; "b.fs" ])) (fun owner ->
        let other = if owner = "a.fs" then "b.fs" else "a.fs"
        let ownerFile = inspectedFile owner "module Game.Tuning\nlet tuning =\n  { A = 1.5 }\n" [ "tuning" ]
        let otherFile = inspectedFile other "module Other\nlet unrelated =\n  { A = 9.5 }\n" [ "tuning" ]
        let index = indexOf [ ownerFile; otherFile ]
        match SourceIndex.factsFor index "tuning" [ PathStep.RecordField "A" ] with
        | SourceFacts.OneSource part -> part.File = owner && part.Text = "1.5"
        | _ -> false))

    testCase "a record binding has no item of its own in the listing: its whole right-hand side is read for the names asked about, and only those" <| fun _ ->
      let items = inspectedWith "Tuning.fs" tuningFile InspectTarget.WholeFile
      SourceIndex.wholesToRead (Set.ofList [ "tuning"; "gravity"; "elsewhere" ]) items
      |> List.map NudgeAddress.format
      |> Expect.equal "the record, and not the literal that is listed whole, nor a name the file lacks" [ "Game.Tuning.tuning" ]
      let whole = inspectedWith "Tuning.fs" tuningFile (InspectTarget.OneAddress(addressOf "tuning" []))
      whole |> List.map (fun r -> r.Kind) |> Expect.equal "a record is a formula as far as a knob goes" [ ItemKind.Formula ]

    testCase "a declaration whose whole expression was not read is not guessed at: the files are unknown for it" <| fun _ ->
      let unread = indexOf [ fileOf "Tuning.fs" (inspectedWith "Tuning.fs" tuningFile InspectTarget.WholeFile) ]
      match SourceIndex.factsFor unread "tuning" [] with
      | SourceFacts.FilesUnknown reason -> reason |> Expect.stringContains "names the binding and the file" "tuning in Tuning.fs"
      | other -> failtestf "expected FilesUnknown, got %A" other

    testCase "a script that binds a name twice names both declarations, because the door can only ever reach the first" <| fun _ ->
      let index = indexOf [ inspectedFile "s.fsx" "let gravity = 9.8\nlet gravity = 12.0\n" [ "gravity" ] ]
      match SourceIndex.factsFor index "gravity" [] with
      | SourceFacts.ManySources places -> places |> List.map (fun p -> p.Text) |> Expect.equal "both, in order" [ "9.8"; "12.0" ]
      | other -> failtestf "expected the ambiguity to be named, got %A" other
  ]

[<Tests>]
let deriveTests =
  testList "the persistence state: one answer, derived and never guessed" [

    testPropertyWithConfig propConfig "a value that runs code to read is Held, whatever the files and the last action say"
      (Prop.forAll (arbitrary (Gen.zip genSource genOutcome)) (fun (source, outcome) ->
        PersistenceState.derive LiveShape.NotRead source outcome = PersistenceState.Held))

    testPropertyWithConfig propConfig "a write in flight is Writing, whatever else is true of a readable value"
      (Prop.forAll (arbitrary (Gen.zip3 genLive genSource (Gen.zip (Gen.elements [ WriteKind.UndoStep; WriteKind.SetTo "1" ]) genRef))) (fun (live, source, (kind, attempted)) ->
        match live with
        | LiveShape.NotRead -> true
        | _ -> PersistenceState.derive live source (RowOutcome.InFlight(kind, attempted)) = PersistenceState.Writing(kind, attempted)))

    testPropertyWithConfig propConfig "two places are ambiguous even when the live value equals one of them: it never picks the first"
      (Prop.forAll (arbitrary (Gen.zip genPreview (Gen.listOfLength 2 genRef))) (fun (preview, places) ->
        PersistenceState.derive (LiveShape.Scalar preview) (SourceFacts.ManySources places) RowOutcome.NoOutcome = PersistenceState.Ambiguous places))

    testPropertyWithConfig propConfig "the derivation is total: every combination of facts gives exactly one state with a token"
      (Prop.forAll (arbitrary (Gen.zip3 genLive genSource genOutcome)) (fun (live, source, outcome) ->
        PersistenceState.token (PersistenceState.derive live source outcome) <> ""))

    testCase "a literal equal to the live value is in source, and a different live value differs from the file, naming both" <| fun _ ->
      let gravity = real "a.fs" "gravity" "9.8"
      PersistenceState.derive (LiveShape.Scalar "9.8") (SourceFacts.OneSource gravity) RowOutcome.NoOutcome
      |> Expect.equal "equal" (PersistenceState.InSource gravity)
      PersistenceState.derive (LiveShape.Scalar "12") (SourceFacts.OneSource gravity) RowOutcome.NoOutcome
      |> Expect.equal "the REPL holds another value" (PersistenceState.DiffersFromFile("12", gravity))

    testCase "a formula in the file is derived, a container is derived, and neither claims to equal the file" <| fun _ ->
      let formula = sourceAt "a.fs" "jump" "gravity * 2.0" ItemKind.Formula
      PersistenceState.derive (LiveShape.Scalar "19.6") (SourceFacts.OneSource formula) RowOutcome.NoOutcome
      |> Expect.equal "a formula" (PersistenceState.Derived(formula, DerivedWhy.AFormula))
      PersistenceState.derive LiveShape.Container (SourceFacts.OneSource formula) RowOutcome.NoOutcome
      |> Expect.equal "a container" (PersistenceState.Derived(formula, DerivedWhy.AContainer))

    testCase "no file, unknown files and parts the file does not spell are three different not-in-a-file reasons" <| fun _ ->
      let owner = real "a.fs" "tuning" "1.0"
      let state source = PersistenceState.derive (LiveShape.Scalar "1") source RowOutcome.NoOutcome
      state SourceFacts.NoFileBindsIt |> Expect.equal "repl" (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt)
      state (SourceFacts.FilesUnknown "x") |> Expect.equal "unknown" (PersistenceState.NotInAFile(NotInFileWhy.OwnedFilesUnknown "x"))
      state (SourceFacts.PartNotSpelled owner) |> Expect.equal "not spelled" (PersistenceState.NotInAFile(NotInFileWhy.NotSpelledInTheFile owner))

    testCase "a refusal because the expression moved is a stale address showing both versions, never a plain refusal" <| fun _ ->
      let seen = real "a.fs" "gravity" "9.8"
      let now = { seen with Text = "9.9"; Hash = String('c', 64) }
      PersistenceState.derive (LiveShape.Scalar "9.8") (SourceFacts.OneSource now) (RowOutcome.Rejected(NudgeRefusal.SourceMoved(seen.Hash, now.Hash, "9.9"), seen))
      |> Expect.equal "both versions" (PersistenceState.StaleAddress { Seen = seen; Now = StaleNow.Edited("9.9", now.Hash) })
      PersistenceState.derive (LiveShape.Scalar "9.8") (SourceFacts.OneSource now) (RowOutcome.Rejected(NudgeRefusal.AddressMoved(seen.Address, addressOf "g2" []), seen))
      |> Expect.equal "offered, not taken" (PersistenceState.StaleAddress { Seen = seen; Now = StaleNow.MovedTo(addressOf "g2" []) })

    testCase "a stale outcome stops applying once the file is back to the text the row showed" <| fun _ ->
      let seen = real "a.fs" "gravity" "9.8"
      PersistenceState.derive (LiveShape.Scalar "9.8") (SourceFacts.OneSource seen) (RowOutcome.Rejected(NudgeRefusal.SourceMoved(seen.Hash, "other", "9.9"), seen))
      |> Expect.equal "back to what was shown" (PersistenceState.InSource seen)

    testCase "any other refusal is Refused, carrying the refusal itself so its rule and next action can be shown" <| fun _ ->
      let place = real "a.fs" "gravity" "9.8"
      let refusal = NudgeRefusal.LiteralNotReadable(LiteralKindName.Real, "abc")
      match PersistenceState.derive (LiveShape.Scalar "9.8") (SourceFacts.OneSource place) (RowOutcome.Rejected(refusal, place)) with
      | PersistenceState.Refused(carried, at) ->
        carried |> Expect.equal "the same refusal" refusal
        at |> Expect.equal "where" place
        NudgeRefusal.rule carried |> Expect.stringContains "its rule" "abc"
        NudgeRefusal.nextAction carried |> Expect.stringContains "its next action" "13.2"
      | other -> failtestf "expected Refused, got %A" other
  ]

[<Tests>]
let controlTests =
  testList "the control a row offers, and the arithmetic of a step" [

    testCase "each literal kind gets its own control, and a formula gets an expression field" <| fun _ ->
      let file = inspectedFile "Tuning.fs" tuningFile [ "jump"; "tuning" ]
      let control name =
        match SourceIndex.factsFor (indexOf [ file ]) name [] with
        | SourceFacts.OneSource place -> Control.ofSource place
        | other -> failtestf "%s: %A" name other
      control "gravity" |> Expect.equal "real" (Control.RealStepper(9.8, Step.Fraction 1))
      control "maxHealth" |> Expect.equal "int" (Control.IntegerStepper 100L)
      control "mask" |> Expect.equal "hex is an integer" (Control.IntegerStepper 31L)
      control "hard" |> Expect.equal "bool" (Control.Toggle true)
      // The field shows the VALUE the door reads back (a string without its quotes), not the spelling in the file.
      control "title" |> Expect.equal "string" (Control.LiteralField(LiteralKindName.Text, "Nudge"))
      control "mode" |> Expect.equal "case" (Control.LiteralField(LiteralKindName.UnionCase, "Hard"))
      control "jump" |> Expect.equal "formula" (Control.ExpressionField "gravity * 2.0")

    testCase "the step comes from the literal's own spelling: 1.0 by 0.1, 0.12 by 0.01, 10 and 5. by whole units" <| fun _ ->
      Step.ofRealText "1.0" |> Expect.equal "one decimal" (Step.Fraction 1)
      Step.ofRealText "0.12" |> Expect.equal "two decimals" (Step.Fraction 2)
      Step.ofRealText "12.5<m/s>" |> Expect.equal "the unit is not a decimal" (Step.Fraction 1)
      Step.ofRealText "12_345.25" |> Expect.equal "an underscore is not a digit" (Step.Fraction 2)
      Step.ofRealText "5." |> Expect.equal "nothing after the point" Step.Whole
      Step.ofRealText "1e3" |> Expect.equal "an exponent spelling has none" Step.Whole
      Step.size (Step.Fraction 2) |> Expect.floatClose "one hundredth" Accuracy.high 0.01

    testCase "only a writable state offers a control; a write in flight and a REPL-only value offer none" <| fun _ ->
      let place = real "a.fs" "gravity" "9.8"
      Control.ofState (PersistenceState.InSource place) (SourceFacts.OneSource place) |> Expect.equal "in source" (Control.RealStepper(9.8, Step.Fraction 1))
      Control.ofState (PersistenceState.Writing(WriteKind.UndoStep, place)) (SourceFacts.OneSource place) |> Expect.equal "writing" Control.NoControl
      Control.ofState (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt) SourceFacts.NoFileBindsIt |> Expect.equal "repl" Control.NoControl
      Control.ofState (PersistenceState.Ambiguous [ place; place ]) (SourceFacts.ManySources [ place; place ]) |> Expect.equal "ambiguous" Control.NoControl
      Control.ofState (PersistenceState.DiffersFromFile("12", place)) (SourceFacts.OneSource place)
      |> Expect.equal "the control still changes the file's one expression, so tuning in a loop keeps working" (Control.RealStepper(9.8, Step.Fraction 1))
      Control.ofState (PersistenceState.Held) SourceFacts.NoFileBindsIt |> Expect.equal "held" Control.NoControl

    testCase "after a refusal the control is the one for the expression the file holds NOW" <| fun _ ->
      let seen = real "a.fs" "gravity" "9.8"
      let now = real "a.fs" "gravity" "9.9"
      let state = PersistenceState.StaleAddress { Seen = seen; Now = StaleNow.Edited("9.9", now.Hash) }
      Control.ofState state (SourceFacts.OneSource now) |> Expect.equal "from now" (Control.RealStepper(9.9, Step.Fraction 1))

    testPropertyWithConfig propConfig "stepping up n and back down n lands on the number it started from, never 0.30000000000000004"
      (Prop.forAll (arbitrary (Gen.zip3 (Gen.choose (-5000, 5000)) (Gen.choose (0, 4)) (Gen.choose (1, 40)))) (fun (units, decimals, n) ->
        let value = Math.Round(float units / Math.Pow(10.0, float decimals), decimals)
        let step = if decimals = 0 then Step.Whole else Step.Fraction decimals
        let there = Stepping.literalAfter (Control.RealStepper(value, step)) n
        match there with
        | Error _ -> false
        | Ok text ->
          let moved = Double.Parse(text, Globalization.CultureInfo.InvariantCulture)
          match Stepping.literalAfter (Control.RealStepper(moved, step)) -n with
          | Error _ -> false
          | Ok backText -> Double.Parse(backText, Globalization.CultureInfo.InvariantCulture) = value))

    testPropertyWithConfig propConfig "a step moves a real by exactly n units of its last decimal"
      (Prop.forAll (arbitrary (Gen.zip3 (Gen.choose (-5000, 5000)) (Gen.choose (1, 4)) (Gen.choose (-30, 30)))) (fun (units, decimals, n) ->
        let scale = Math.Pow(10.0, float decimals)
        let value = Math.Round(float units / scale, decimals)
        match Stepping.literalAfter (Control.RealStepper(value, Step.Fraction decimals)) n with
        | Error _ -> false
        | Ok text ->
          let moved = Double.Parse(text, Globalization.CultureInfo.InvariantCulture)
          Math.Round(moved * scale) = float (int64 units + int64 n)))

    testPropertyWithConfig propConfig "an integer moves by exactly n, and past the edge of an int64 it refuses instead of wrapping"
      (Prop.forAll (arbitrary (Gen.zip (Gen.choose (-100000, 100000)) (Gen.choose (-50, 50)))) (fun (value, n) ->
        Stepping.literalAfter (Control.IntegerStepper(int64 value)) n = Ok(string (value + n))
        && Stepping.literalAfter (Control.IntegerStepper Int64.MaxValue) 1 = Error StepRefusal.OutOfRange
        && Stepping.literalAfter (Control.IntegerStepper Int64.MinValue) -1 = Error StepRefusal.OutOfRange))

    testCase "a control with nothing to step refuses to step" <| fun _ ->
      Stepping.literalAfter (Control.Toggle true) 1 |> Expect.equal "toggle" (Error StepRefusal.OutOfRange)
      Stepping.literalAfter Control.NoControl 1 |> Expect.equal "none" (Error StepRefusal.OutOfRange)
  ]

[<Tests>]
let trailTests =
  testList "a row's undo and redo: exact only while its write is the newest in the file" [

    let trailOf (addresses: string list) = addresses |> List.fold (fun trail a -> FileTrail.wrote a trail) FileTrail.empty

    testCase "the newest write can be undone, an older one waits its turn, and an empty trail has nothing" <| fun _ ->
      let trail = trailOf [ "g"; "h" ]
      FileTrail.undoFor "h" trail |> Expect.equal "newest" HistoryStep.StepAvailable
      FileTrail.undoFor "g" trail |> Expect.equal "older waits for h" (HistoryStep.OtherRowFirst "h")
      FileTrail.undoFor "g" FileTrail.empty |> Expect.equal "nothing" HistoryStep.NothingToStep

    testCase "an undo offers a redo to the same row, and a write after an undo ends the redo" <| fun _ ->
      let afterUndo = trailOf [ "g"; "h" ] |> FileTrail.undid
      FileTrail.redoFor "h" afterUndo |> Expect.equal "redo is h's" HistoryStep.StepAvailable
      FileTrail.redoFor "g" afterUndo |> Expect.equal "not g's turn" (HistoryStep.OtherRowFirst "h")
      FileTrail.redoFor "h" (FileTrail.wrote "g" afterUndo) |> Expect.equal "a new write ends redo" HistoryStep.NothingToStep

    testPropertyWithConfig propConfig "undo then redo restores the trail, and undoing everything empties it"
      (Prop.forAll (arbitrary (Gen.listOf (Gen.elements [ "a"; "b"; "c" ]))) (fun writes ->
        let trail = writes |> List.fold (fun t a -> FileTrail.wrote a t) FileTrail.empty
        let roundTrip = trail |> FileTrail.undid |> FileTrail.redid
        let emptied = writes |> List.fold (fun t _ -> FileTrail.undid t) trail
        roundTrip = trail && List.isEmpty emptied.Applied && emptied.Undone.Length = writes.Length))

    testPropertyWithConfig propConfig "agrees with the door's own history fold on any run of writes and undos"
      (Prop.forAll (arbitrary (Gen.listOf (Gen.elements [ Some "a"; Some "b"; None ]))) (fun steps ->
        // Some address = a write there, None = an undo. The door's `historyOf` over the same journal says how many writes are
        // applied; the trail must say the same.
        let address name : TweakAddress = { ModulePath = [ "M" ]; BindingName = name; Path = [] }
        let folded =
          steps
          |> List.fold
            (fun (log: EventLog, trail: FileTrail) step ->
              match step with
              | Some name ->
                let next, _ = EventLog.append log 0L (TweakLogEvent.TweakSaved(address name, "1", "2", "h", "f"))
                next, FileTrail.wrote name trail
              | None ->
                match (historyOf log).Applied |> List.tryLast with
                | None -> log, trail
                | Some target ->
                  let next, _ = EventLog.append log 0L (TweakLogEvent.RolledBack target)
                  next, FileTrail.undid trail)
            (EventLog.empty, FileTrail.empty)
        (historyOf (fst folded)).Applied.Length = (snd folded).Applied.Length
        && (historyOf (fst folded)).Undone.Length = (snd folded).Undone.Length))
  ]

[<Tests>]
let reloadWatchTests =
  testList "what the app did with the write: a verdict that is new, or the honest wait" [

    let facts case : ReloadFacts =
      { Case = case
        Patched = 1
        Considered = 1
        Message = "m"
        SuggestedAction = ""
        Mechanism = Features.ReloadOutcome.PatchMechanism.NoPatch
        Declarations = [ "Game.Tuning.gravity" ]
        Callers = Features.CallerState.CallersState.CallersNotReported }
    let patience = SageFs.Timeouts.compileQueueWait
    let soon = TimeSpan.FromTicks(patience.Ticks / 5L)

    testCase "a new verdict is reported as it is, with its declarations" <| fun _ ->
      let baseline = SessionReload.Finished(facts ReloadCase.Patched)
      let current = SessionReload.Finished { facts ReloadCase.PatchPending with Declarations = [ "Game.Tuning.gravity" ] }
      match ReloadWatch.ofSession baseline current TimeSpan.Zero patience with
      | ReloadWatch.Reported reported ->
        reported.Case |> Expect.equal "the case" ReloadCase.PatchPending
        reported.Declarations |> Expect.equal "the declaration named" [ "Game.Tuning.gravity" ]
      | other -> failtestf "expected the verdict, got %A" other

    testCase "a compile in progress is compiling, and the unchanged verdict waits, then says no new report arrived" <| fun _ ->
      let baseline = SessionReload.Finished(facts ReloadCase.Patched)
      ReloadWatch.ofSession baseline (SessionReload.Compiling None) TimeSpan.Zero patience |> Expect.equal "compiling" ReloadWatch.Compiling
      ReloadWatch.ofSession baseline baseline soon patience |> Expect.equal "waiting" ReloadWatch.AwaitingReload
      ReloadWatch.ofSession baseline baseline patience patience |> Expect.equal "silent" ReloadWatch.NoNewReport
      ReloadWatch.ofSession SessionReload.NoReloadYet SessionReload.NoReloadYet patience patience |> Expect.equal "no reload ever" ReloadWatch.NoNewReport
  ]
