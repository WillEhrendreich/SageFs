module SageFs.Tests.LiveTestingPerTestCoverageTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.LiveTesting

/// Per-test coverage, from the line an edit changed back to the tests that run it.
///
/// Before this, the host took ONE coverage reading per run and the daemon stored that one bitmap
/// against every test in the batch, so "which tests cover this line" had the same answer for every
/// test and an edit to a line only one test runs selected them all. These tests pin the chain end
/// to end: the line hash and the line edit, the recorded coverage that speaks for a line, the
/// selection that uses it, and what the selection refuses to claim.

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 200 }

let private mkTest (name: string) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private slot (file: string) (line: int) (endLine: int) : SequencePoint =
  { File = file; Line = line; Column = 0; EndLine = endLine; EndColumn = 80; BranchId = 0 }

let private mapOf (slots: SequencePoint array) (startup: int array) (baselines: SourceLineHashes array) : InstrumentationMap =
  { Slots = slots
    TotalProbes = slots.Length
    TrackerTypeName = "T"
    HitsFieldName = "H"
    Source = { StartupSlots = startup; Baselines = baselines } }

let private bitmapOf (count: int) (hit: int list) : CoverageBitmap =
  CoverageBitmap.ofBoolArray (Array.init count (fun i -> List.contains i hit))

let private moduleFs = "Module.fs"

/// Four probes in Module.fs. Slot 0 is line 10 (only A runs it), slot 1 is line 20 (only B),
/// slot 2 is a three-line expression both run, slot 3 is line 5, which runs at module startup.
let private fixtureMap =
  mapOf
    [| slot moduleFs 10 10; slot moduleFs 20 20; slot moduleFs 30 32; slot moduleFs 5 5 |]
    [| 3 |]
    [||]

let private testA = mkTest "Module.Tests.A runs the first branch"
let private testB = mkTest "Module.Tests.B runs the second branch"
let private testC = mkTest "Module.Tests.C was never run"
let private discoveredIds = [| testA.Id; testB.Id; testC.Id |]

/// A hits slots 0, 2 and the startup slot 3. B hits 1, 2 and 3. C has no coverage at all.
let private bitmaps =
  Map.ofList [ testA.Id, bitmapOf 4 [ 0; 2; 3 ]; testB.Id, bitmapOf 4 [ 1; 2; 3 ] ]

let private narrow (changed: int list) =
  CoverageBitmap.narrowByLines moduleFs (ChangedLines.InPlace (Set.ofList changed)) [| fixtureMap |] bitmaps discoveredIds

let private narrowedIds (result: LineNarrowing) =
  match result with
  | LineNarrowing.NarrowedTo ids -> ids
  | LineNarrowing.Refused reason -> failtestf "expected a narrowing, was refused: %A" reason

let private refusal (result: LineNarrowing) =
  match result with
  | LineNarrowing.Refused reason -> reason
  | LineNarrowing.NarrowedTo ids -> failtestf "expected a refusal, was narrowed to %A" ids

[<Tests>]
let lineEditTests =
  testList "per-test coverage: line hashes and line edits" [

    testCase "the same line hashes the same, in any process" <| fun _ ->
      LineHash.ofLine "let add a b = a + b"
      |> Expect.equal "stable for equal text" (LineHash.ofLine "let add a b = a + b")

    testCase "different lines hash differently" <| fun _ ->
      LineHash.ofLine "let add a b = a + b"
      |> Expect.notEqual "a one-character edit moves the hash" (LineHash.ofLine "let add a b = a - b")

    testCase "the hash is a fixed value, so a worker and a daemon in different processes agree" <| fun _ ->
      // FNV-1a 64 of "a": 0xaf63dc4c8601ec8c. A randomized hash (String.GetHashCode) would differ per process.
      LineHash.ofLine "a"
      |> Expect.equal "FNV-1a 64 of one character" -5808556873153909620L

    testCase "ofText splits on newlines and ignores a trailing carriage return" <| fun _ ->
      LineHash.ofText "one\r\ntwo\nthree"
      |> Expect.equal "three lines, line endings do not matter" (Array.map LineHash.ofLine [| "one"; "two"; "three" |])

    testPropertyWithConfig cfg "an editor that sends CRLF and a file with LF hash to the same lines" <|
      fun (lines: NonEmptyArray<NonNull<string>>) ->
        let clean = lines.Get |> Array.map (fun l -> l.Get.Replace("\r", "").Replace("\n", ""))
        LineHash.ofText (String.Join("\r\n", clean))
        |> Expect.equal "CRLF and LF agree" (LineHash.ofText (String.Join("\n", clean)))

    testCase "an edit that changes one line says which line" <| fun _ ->
      let compiled = "let a = 1\nlet add x y = x + y\nlet b = 2"
      let edited = "let a = 1\nlet add x y = x - y\nlet b = 2"
      LineEdit.between (LineHash.ofText compiled) edited
      |> Expect.equal "line 2, 1-based" (ChangedLines.InPlace (Set.ofList [ 2 ]))

    testCase "text identical to the compiled text changes no line" <| fun _ ->
      let compiled = "let a = 1\nlet b = 2"
      LineEdit.between (LineHash.ofText compiled) compiled
      |> Expect.equal "nothing differs" (ChangedLines.InPlace Set.empty)

    testCase "an inserted line shifts every line after it, so line numbers cannot be trusted" <| fun _ ->
      let compiled = "let a = 1\nlet b = 2"
      LineEdit.between (LineHash.ofText compiled) "let a = 1\nlet extra = 0\nlet b = 2"
      |> Expect.equal "a different line count" ChangedLines.Shifted

    testCase "a removed line shifts too" <| fun _ ->
      LineEdit.between (LineHash.ofText "let a = 1\nlet b = 2") "let a = 1"
      |> Expect.equal "a different line count" ChangedLines.Shifted

    testCase "no compiled text means no baseline, never an empty edit" <| fun _ ->
      LineEdit.between [||] "let a = 1"
      |> Expect.equal "no baseline" ChangedLines.NoBaseline

    testPropertyWithConfig cfg "changing one line of n lines reports exactly that line" <|
      fun (n: PositiveInt) (pick: PositiveInt) ->
        let count = 1 + (n.Get % 40)
        let target = pick.Get % count
        let compiled = [| for i in 0 .. count - 1 -> sprintf "let v%d = %d" i i |]
        let edited = compiled |> Array.mapi (fun i l -> match i = target with | true -> l + " + 1" | false -> l)
        LineEdit.between (LineHash.ofText (String.Join("\n", compiled))) (String.Join("\n", edited))
        |> Expect.equal "one line, 1-based" (ChangedLines.InPlace (Set.ofList [ target + 1 ]))
  ]

[<Tests>]
let mergeTests =
  testList "per-test coverage: merging maps keeps what each knew" [

    testCase "merged startup slots are offset by the slots of the maps before them" <| fun _ ->
      let first = mapOf [| slot "A.fs" 1 1; slot "A.fs" 2 2 |] [| 1 |] [||]
      let second = mapOf [| slot "B.fs" 1 1; slot "B.fs" 2 2; slot "B.fs" 3 3 |] [| 0; 2 |] [||]
      (InstrumentationMap.merge [| first; second |]).Source.StartupSlots
      |> Array.sort
      |> Expect.equal "slot 1 of the first, slots 0 and 2 of the second sit after two slots" [| 1; 2; 4 |]

    testCase "merged baselines keep every file's compiled lines" <| fun _ ->
      let a = { File = "A.fs"; Lines = [| 1L; 2L |] }
      let b = { File = "B.fs"; Lines = [| 3L |] }
      let first = mapOf [| slot "A.fs" 1 1 |] [||] [| a |]
      let second = mapOf [| slot "B.fs" 1 1 |] [||] [| b |]
      (InstrumentationMap.merge [| first; second |]).Source.Baselines
      |> Array.sortBy (fun x -> x.File)
      |> Expect.equal "both files, unchanged" [| a; b |]
  ]

[<Tests>]
let narrowingTests =
  testList "per-test coverage: narrowing an edit to the tests that run the changed lines" [

    testCase "a line only A runs selects A, and a test with no coverage, and not B" <| fun _ ->
      narrow [ 10 ]
      |> narrowedIds
      |> Expect.equal "A (covers it) and C (not known to be unaffected), in discovery order" [| testA.Id; testC.Id |]

    testCase "a line only B runs selects B and the test with no coverage" <| fun _ ->
      narrow [ 20 ]
      |> narrowedIds
      |> Expect.equal "B and C" [| testB.Id; testC.Id |]

    testCase "a line inside a multi-line expression is run by whoever ran the expression" <| fun _ ->
      narrow [ 31 ]
      |> narrowedIds
      |> Expect.equal "A, B and C" [| testA.Id; testB.Id; testC.Id |]

    testCase "two changed lines select the union of their tests" <| fun _ ->
      narrow [ 10; 20 ]
      |> narrowedIds
      |> Expect.equal "A, B and C" [| testA.Id; testB.Id; testC.Id |]

    testCase "a changed line with no sequence point is refused, not read as unaffected" <| fun _ ->
      narrow [ 10; 99 ]
      |> refusal
      |> Expect.equal "line 99 has no probe" (LineNarrowingRefusal.ChangedLineHasNoProbe 99)

    testCase "a changed line that runs at startup is refused: only the first test to touch the module hit it" <| fun _ ->
      narrow [ 5 ]
      |> refusal
      |> Expect.equal "line 5 is a startup probe" (LineNarrowingRefusal.ChangedLineRunsAtStartup 5)

    testCase "an edit that shifted lines is refused" <| fun _ ->
      CoverageBitmap.narrowByLines moduleFs ChangedLines.Shifted [| fixtureMap |] bitmaps discoveredIds
      |> refusal
      |> Expect.equal "shifted" LineNarrowingRefusal.EditShifted

    testCase "an edit with no compiled text to measure against is refused" <| fun _ ->
      CoverageBitmap.narrowByLines moduleFs ChangedLines.NoBaseline [| fixtureMap |] bitmaps discoveredIds
      |> refusal
      |> Expect.equal "no baseline" LineNarrowingRefusal.NoBaseline

    testCase "an edit that changed no line relative to the compiled text is refused, never read as no impact" <| fun _ ->
      CoverageBitmap.narrowByLines moduleFs (ChangedLines.InPlace Set.empty) [| fixtureMap |] bitmaps discoveredIds
      |> refusal
      |> Expect.equal "nothing changed" LineNarrowingRefusal.NothingChanged

    testCase "with no test coverage recorded at all there is nothing to narrow with" <| fun _ ->
      CoverageBitmap.narrowByLines moduleFs (ChangedLines.InPlace (Set.ofList [ 10 ])) [| fixtureMap |] Map.empty discoveredIds
      |> refusal
      |> Expect.equal "no bitmaps" LineNarrowingRefusal.NoBitmaps

    testCase "a bitmap from a different instrumentation (wrong size) is not evidence, so its test is kept" <| fun _ ->
      let stale = Map.add testB.Id (bitmapOf 9 [ 1 ]) bitmaps
      CoverageBitmap.narrowByLines moduleFs (ChangedLines.InPlace (Set.ofList [ 10 ])) [| fixtureMap |] stale discoveredIds
      |> narrowedIds
      |> Expect.equal "A covers it; B and C have no usable coverage" [| testA.Id; testB.Id; testC.Id |]

    testCase "a changed line in a file the maps do not know is refused" <| fun _ ->
      CoverageBitmap.narrowByLines "Other.fs" (ChangedLines.InPlace (Set.ofList [ 10 ])) [| fixtureMap |] bitmaps discoveredIds
      |> refusal
      |> Expect.equal "no probe in that file" (LineNarrowingRefusal.ChangedLineHasNoProbe 10)

    testCase "startup slots of a later map are found after merging, at the offset the merged bitmap uses" <| fun _ ->
      let first = mapOf [| slot "A.fs" 1 1 |] [||] [||]
      let second = mapOf [| slot "B.fs" 7 7; slot "B.fs" 8 8 |] [| 0 |] [||]
      let two = Map.ofList [ testA.Id, bitmapOf 3 [ 0; 1 ]; testB.Id, bitmapOf 3 [ 2 ] ]
      CoverageBitmap.narrowByLines "B.fs" (ChangedLines.InPlace (Set.ofList [ 7 ])) [| first; second |] two [| testA.Id; testB.Id |]
      |> refusal
      |> Expect.equal "B.fs line 7 is the second map's startup slot" (LineNarrowingRefusal.ChangedLineRunsAtStartup 7)
  ]

[<Tests>]
let coveringTests =
  testList "per-test coverage: which tests cover a line" [

    testCase "each line lists exactly the tests whose own coverage reached it" <| fun _ ->
      let byLine = CoverageBitmap.coveringTestsByLine moduleFs [| fixtureMap |] bitmaps discoveredIds
      byLine |> Map.tryFind 10 |> Expect.equal "line 10" (Some [| testA.Id |])
      byLine |> Map.tryFind 20 |> Expect.equal "line 20" (Some [| testB.Id |])
      byLine |> Map.tryFind 30 |> Expect.equal "the expression's first line" (Some [| testA.Id; testB.Id |])
      byLine |> Map.tryFind 32 |> Expect.equal "the expression's last line" (Some [| testA.Id; testB.Id |])

    testCase "a test with no recorded coverage covers no line" <| fun _ ->
      CoverageBitmap.coveringTestsByLine moduleFs [| fixtureMap |] bitmaps discoveredIds
      |> Map.values
      |> Seq.forall (fun ids -> not (Array.contains testC.Id ids))
      |> Expect.isTrue "C never appears"

    testCase "a line nobody hit is not listed, so the gutter shows it as uncovered" <| fun _ ->
      let onlyA = Map.ofList [ testA.Id, bitmapOf 4 [ 0 ] ]
      CoverageBitmap.coveringTestsByLine moduleFs [| fixtureMap |] onlyA discoveredIds
      |> Map.tryFind 20
      |> Expect.isNone "line 20 has a probe and no test hit it"

    testCase "the annotation for a line carries the tests that cover it, named" <| fun _ ->
      let status = CoverageStatus.Covered (1, CoverageHealth.AllPassing)
      let ann line : CoverageAnnotation =
        { Symbol = sprintf "Module.f%d" line; FilePath = moduleFs; DefinitionLine = line; Status = status; BranchCoverage = BranchCoverage.Unknown }
      let cycle =
        { LiveTestCycleState.empty with
            InstrumentationMaps = Map.ofList [ "s1", [| fixtureMap |] ]
            TestState =
              { LiveTestState.empty with
                  DiscoveredTests = [| testA; testB; testC |]
                  TestCoverageBitmaps = bitmaps
                  CoverageAnnotations = [| ann 10; ann 20 |] } }
      let annotations = FileAnnotations.projectWithCoverage moduleFs cycle
      let at line = annotations.CoverageAnnotations |> Array.find (fun a -> a.Line = line)
      (at 10).CoveringTests |> Array.map (fun t -> t.DisplayName) |> Expect.equal "line 10 is covered by A only" [| testA.DisplayName |]
      (at 20).CoveringTests |> Array.map (fun t -> t.DisplayName) |> Expect.equal "line 20 is covered by B only" [| testB.DisplayName |]
      (at 10).CoveringTestIds |> Expect.equal "the ids say the same" [| testA.Id |]
  ]

/// Drive the selection the way the keystroke path does: a body-only edit, the file's symbols
/// known, the compiled lines known, per-test coverage recorded.
let private decide (lines: ChangedLines) (changed: string list) =
  let graph =
    { TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "Module.add", [| testA.Id; testB.Id |] ]
        TransitiveCoverage = Map.ofList [ "Module.add", [| testA.Id; testB.Id |] ] }
  let state =
    { LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testA; testB; testC |]
        TestCoverageBitmaps = bitmaps }
  TestCycleEffects.decideAfterTypeCheck
    { Changed = changed; InFile = [ "Module.add" ]; Lines = lines }
    moduleFs
    RunTrigger.Keystroke
    graph
    state
    None
    (Map.ofList [ "s1", [| fixtureMap |] ])

let private selectedBy (outcome: AfterTypeCheckOutcome) =
  match outcome.Effects with
  | [ TestCycleEffect.RunAffectedTests req ] -> req.Tests |> Array.map (fun tc -> tc.Id)
  | other -> failtestf "expected one run, got %A" other

[<Tests>]
let selectionTests =
  testList "per-test coverage: the keystroke selection" [

    testCase "an edit to a line only A runs selects A, not every test that reaches the function" <| fun _ ->
      let outcome = decide (ChangedLines.InPlace (Set.ofList [ 10 ])) []
      outcome
      |> selectedBy
      |> Array.sort
      |> Expect.equal "A, and C because it has no coverage; B is left alone" (Array.sort [| testA.Id; testC.Id |])
      match outcome.Decision with
      | Some decision ->
        decision.Explanation.Precision
        |> Expect.equal "the label says why" SelectionPrecision.LineCoverageNarrowing
        decision.Explanation.SelectedTests
        |> Array.contains testB.FullName
        |> Expect.isFalse "B is not in the explanation's selected tests"
      | None -> failtest "expected a decision"

    testCase "the same edit with no compiled text to measure against keeps the wide selection" <| fun _ ->
      decide ChangedLines.NoBaseline []
      |> selectedBy
      |> Array.sort
      |> Expect.equal "everything that reaches the function, plus the untested one" (Array.sort [| testA.Id; testB.Id |])

    testCase "an edit that shifted lines keeps the wide selection" <| fun _ ->
      decide ChangedLines.Shifted []
      |> selectedBy
      |> Array.sort
      |> Expect.equal "every test reaching the function" (Array.sort [| testA.Id; testB.Id |])

    testCase "a changed line that runs at startup keeps the wide selection" <| fun _ ->
      decide (ChangedLines.InPlace (Set.ofList [ 5 ])) []
      |> selectedBy
      |> Array.sort
      |> Expect.equal "every test reaching the function" (Array.sort [| testA.Id; testB.Id |])

    testCase "an edit that moves symbol names is never narrowed by lines: other files may reach what moved" <| fun _ ->
      decide (ChangedLines.InPlace (Set.ofList [ 10 ])) [ "Module.add" ]
      |> selectedBy
      |> Array.sort
      |> Expect.equal "the dependency graph's answer stands" (Array.sort [| testA.Id; testB.Id |])
  ]

[<Tests>]
let recordingTests =
  testList "per-test coverage: what the model records" [

    testCase "a reading that hit nothing never replaces a recorded bitmap" <| fun _ ->
      // After a keystroke eval the tests run against evaluated (dynamic) code, which the host's coverage
      // read skips, so the reading is all false. That is "ran somewhere unrecorded", not "covers nothing".
      let tid = testA.Id
      let first, _ =
        SageFsUpdate.update (SageFsMsg.Event (TuiEvent.CoverageBitmapCollected (None, [| tid |], bitmapOf 4 [ 0; 2 ]))) (SageFsModel.initial ())
      let second, _ =
        SageFsUpdate.update (SageFsMsg.Event (TuiEvent.CoverageBitmapCollected (None, [| tid |], bitmapOf 4 []))) first
      second.LiveTesting.TestState.TestCoverageBitmaps
      |> Map.find tid
      |> CoverageBitmap.equivalent (bitmapOf 4 [ 0; 2 ])
      |> Expect.isTrue "the earlier evidence stands"

    testCase "a reading that hit nothing is not stored for a test with no bitmap: absent means unknown" <| fun _ ->
      let model, _ =
        SageFsUpdate.update (SageFsMsg.Event (TuiEvent.CoverageBitmapCollected (None, [| testA.Id |], bitmapOf 4 []))) (SageFsModel.initial ())
      model.LiveTesting.TestState.TestCoverageBitmaps
      |> Map.containsKey testA.Id
      |> Expect.isFalse "unknown stays unknown instead of becoming 'covers nothing'"
  ]
