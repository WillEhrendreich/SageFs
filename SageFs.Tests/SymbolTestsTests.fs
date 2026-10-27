module SageFs.Tests.SymbolTestsTests

/// "Tests covering a symbol" for one session's cycle (`SymbolTests.ofCycle`).
///
/// The defect these pin: the symbol-to-tests map came from a name graph that decides what a test is by
/// looking for `.Tests.` in a definition's full name and gives the test the id of that definition. An
/// Expecto test is a `testCase "name" <| fun _` lambda inside one `[<Tests>]` value, and a module
/// that is not called `*.Tests.*` yields no test at all. Against a real two-project Expecto sample the
/// route answered `{"Symbols":[],"TotalSymbols":0}` after an edit, a run and a buffer change.

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting

let framework = LiveTestingDefaults.Framework

let mkTest (name: string) : TestCase =
  { Id = TestId.create name framework
    FullName = name
    DisplayName = name
    NamePath = []
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = framework
    Category = TestCategory.Unit }

let testFile = "/t/CalcTests.fs"

let sequencePoint (line: int) : SequencePoint =
  { File = testFile; Line = line; Column = 1; EndLine = line; EndColumn = 40; BranchId = 0 }

let mapOfLines (startup: int array) (lines: int array) : InstrumentationMap =
  { InstrumentationMap.empty with
      Slots = lines |> Array.map sequencePoint
      TotalProbes = lines.Length
      Source = { StartupSlots = startup; Baselines = [||] } }

let referenceAt (symbol: string) (line: int) : SymbolReference =
  { SymbolFullName = symbol
    UseKind = SymbolUseKind.Reference
    UsedInTestId = None
    FilePath = testFile
    Line = line }

let definitionAt (symbol: string) (line: int) : SymbolReference =
  { referenceAt symbol line with UseKind = SymbolUseKind.Definition }

/// A cycle holding the given tests, coverage and references of `testFile`.
let cycleOf
  (tests: TestCase array)
  (maps: InstrumentationMap array)
  (bitmaps: (TestCase * bool array) list)
  (refs: SymbolReference list)
  : LiveTestCycleState =
  let empty = LiveTestCycleState.empty
  { empty with
      TestState =
        { empty.TestState with
            DiscoveredTests = tests
            TestCoverageBitmaps =
              bitmaps |> List.map (fun (t, hits) -> t.Id, CoverageBitmap.ofBoolArray hits) |> Map.ofList }
      InstrumentationMaps = Map.ofList [ "s1", maps ]
      AnalysisCache = { FileSymbols = Map.ofList [ testFile, refs ] } }

let t1 = mkTest "Calc/add adds two numbers"
let t2 = mkTest "Calc/double doubles"

/// Slot 0 (line 5) runs at module initialization; slot 1 is test one's body (line 7); slot 2 is test two's (line 9).
let reachCycle =
  cycleOf
    [| t1; t2 |]
    [| mapOfLines [| 0 |] [| 5; 7; 9 |] |]
    [ t1, [| true; true; false |]; t2, [| true; false; true |] ]
    [ referenceAt "Calc.add" 7; referenceAt "Calc.double" 9; referenceAt "Calc.startup" 5 ]

[<Tests>]
let tests =
  testList "symbol-to-tests of one session's cycle" [

    test "WHY — a symbol on a line only one test ran is covered by that test, under the id discovery gave it" {
      let map = SymbolTests.ofCycle reachCycle
      map.SymbolToTests
      |> Map.map (fun _ ids -> Set.ofArray ids)
      |> Expect.equal
           "add is test one's, double is test two's"
           (Map.ofList [ "Calc.add", Set.singleton t1.Id; "Calc.double", Set.singleton t2.Id ])
      map.State |> Expect.equal "the map is filled" SymbolTestsState.Mapped
    }

    test "WHY — a symbol on a line that runs when the module initializes belongs to no test" {
      (SymbolTests.ofCycle reachCycle).SymbolToTests
      |> Map.containsKey "Calc.startup"
      |> Expect.isFalse "the startup slot ran once, in whichever test got there first, so no test vouches for it"
    }

    test "WHY — a definition the name graph tied to an id no discovered test carries is dropped" {
      let invented = TestId.create "Calc.Tests.CalcTests.tests" framework
      let cycle =
        { reachCycle with
            DepGraph =
              { TestDependencyGraph.empty with
                  SymbolToTests = Map.ofList [ "Calc.add", [| invented |]; "Calc.only", [| invented |] ] } }
      let map = SymbolTests.ofCycle cycle
      map.SymbolToTests
      |> Map.containsKey "Calc.only"
      |> Expect.isFalse "a symbol whose only test is an invented id has no test"
      map.SymbolToTests
      |> Map.find "Calc.add"
      |> Expect.equal "the invented id is gone and the covering test is kept" [| t1.Id |]
    }

    test "WHY — a name graph entry for a discovered test is kept and merged with what coverage says" {
      let cycle =
        { reachCycle with
            DepGraph =
              { TestDependencyGraph.empty with
                  SymbolToTests = Map.ofList [ "Calc.add", [| t2.Id |] ] } }
      (SymbolTests.ofCycle cycle).SymbolToTests
      |> Map.find "Calc.add"
      |> Expect.equal "both tests, in discovery order" [| t1.Id; t2.Id |]
    }

    test "WHY — nothing type-checked answers NothingAnalysed, which the next type-check fills" {
      (SymbolTests.ofCycle LiveTestCycleState.empty).State
      |> Expect.equal "nothing analysed" SymbolTestsState.NothingAnalysed
    }

    test "WHY — files type-checked but no coverage recorded answers NoCoverageRecorded, which a run fills" {
      let cycle = { reachCycle with TestState = { reachCycle.TestState with TestCoverageBitmaps = Map.empty } }
      let map = SymbolTests.ofCycle cycle
      map.State |> Expect.equal "no coverage recorded" SymbolTestsState.NoCoverageRecorded
      map.SymbolToTests |> Expect.isEmpty "and the map is empty"
    }

    test "WHY — a session that holds no instrumentation cannot read its recorded coverage, and the answer says so" {
      let cycle = { reachCycle with InstrumentationMaps = Map.empty }
      let map = SymbolTests.ofCycle cycle
      map.State |> Expect.equal "recorded coverage with nothing to read it against" SymbolTestsState.CoverageOfAnotherBuild
      map.SymbolToTests |> Expect.isEmpty "and the map is empty"
    }

    test "WHY — coverage of another build (a different probe count) is not evidence about this one" {
      let cycle =
        { reachCycle with
            TestState =
              { reachCycle.TestState with
                  TestCoverageBitmaps =
                    Map.ofList [ t1.Id, CoverageBitmap.ofBoolArray [| true; true |] ] } }
      (SymbolTests.ofCycle cycle).State
      |> Expect.equal "the bitmap is of another build, and the answer says so" SymbolTestsState.CoverageOfAnotherBuild
    }

    test "WHY — coverage recorded and no symbol reached answers NothingReached, which will not fill by itself" {
      let cycle =
        cycleOf
          [| t1 |]
          [| mapOfLines [||] [| 7 |] |]
          [ t1, [| true |] ]
          [ referenceAt "Calc.add" 30 ]
      let map = SymbolTests.ofCycle cycle
      map.State |> Expect.equal "nothing reached" SymbolTestsState.NothingReached
    }

    test "WHY — a buffer whose line count differs from the compiled text is not read against recorded coverage" {
      let baseline : SourceLineHashes = { File = testFile; Lines = LineHash.ofText "a\nb\nc" }
      let map = { mapOfLines [||] [| 1; 2 |] with Source = { StartupSlots = [||]; Baselines = [| baseline |] } }
      let cycle =
        { cycleOf [| t1 |] [| map |] [ t1, [| true; true |] ] [ referenceAt "Calc.add" 2 ] with
            ActiveFile = Some testFile
            LatestContent = Some "a\nb\nc\nd" }
      (SymbolTests.ofCycle cycle).SymbolToTests
      |> Expect.isEmpty "an inserted line moved the buffer's lines off the compiled ones, so nothing is claimed"
    }

    test "WHY — a buffer edited in place keeps its shape and is read against recorded coverage" {
      let baseline : SourceLineHashes = { File = testFile; Lines = LineHash.ofText "a\nb\nc" }
      let map = { mapOfLines [||] [| 1; 2 |] with Source = { StartupSlots = [||]; Baselines = [| baseline |] } }
      let cycle =
        { cycleOf [| t1 |] [| map |] [ t1, [| true; true |] ] [ referenceAt "Calc.add" 2 ] with
            ActiveFile = Some testFile
            LatestContent = Some "a\nB\nc" }
      (SymbolTests.ofCycle cycle).SymbolToTests
      |> Map.containsKey "Calc.add"
      |> Expect.isTrue "line 2 is still line 2"
    }

    test "WHY — each test of a symbol carries its name and what the last run said about it" {
      let result =
        { TestId = t1.Id
          TestName = t1.FullName
          Result = TestResult.Passed FixtureDurations.usualResult
          Timestamp = System.DateTimeOffset.UnixEpoch
          Output = None }
      let cycle = { reachCycle with TestState = { reachCycle.TestState with LastResults = Map.ofList [ t1.Id, result ] } }
      let rows = SymbolTests.testsFor cycle (SymbolTests.ofCycle cycle) "Calc.add"
      rows
      |> Array.map (fun r -> r.TestName, SymbolTestStatus.toWire r.Status)
      |> Expect.equal "named, and passed" [| t1.FullName, "passed" |]
      SymbolTests.testsFor cycle (SymbolTests.ofCycle cycle) "Calc.double"
      |> Array.map (fun r -> r.TestName, SymbolTestStatus.toWire r.Status)
      |> Expect.equal "a test with no result is unknown, under its discovered name" [| t2.FullName, "unknown" |]
    }

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 300 }
      "WHY — over any hit matrix, a symbol maps to exactly the tests whose coverage reached its line"
      (Prop.forAll
        (Arb.fromGen (gen {
          let! testCount = Gen.choose (1, 5)
          let! lineCount = Gen.choose (1, 6)
          let! matrix = Gen.listOfLength testCount (Gen.listOfLength lineCount (Gen.elements [ true; false ]))
          return matrix
        }))
        (fun (matrix: bool list list) ->
          let tests = matrix |> List.mapi (fun i _ -> mkTest (sprintf "T%d" i)) |> Array.ofList
          let lineCount = matrix.Head.Length
          let lines = Array.init lineCount (fun k -> 10 + k)
          let refs = lines |> Array.map (fun line -> referenceAt (sprintf "Lib.s%d" (line - 10)) line) |> List.ofArray
          let cycle =
            cycleOf
              tests
              [| mapOfLines [||] lines |]
              (List.zip (List.ofArray tests) (matrix |> List.map Array.ofList))
              refs
          let got = (SymbolTests.ofCycle cycle).SymbolToTests |> Map.map (fun _ ids -> Set.ofArray ids)
          let expected =
            [ for k in 0 .. lineCount - 1 do
                let reached =
                  List.zip (List.ofArray tests) matrix
                  |> List.filter (fun (_, hits) -> hits.[k])
                  |> List.map (fun (t, _) -> t.Id)
                  |> Set.ofList
                match Set.isEmpty reached with
                | true -> ()
                | false -> yield sprintf "Lib.s%d" k, reached ]
            |> Map.ofList
          got = expected))
  ]
