module SageFs.Tests.LiveTestingUnresolvedSelectionTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting

// The dependency graph names the tests an edit reaches by an id it makes up
// from the symbols of the file it type-checked (`buildFromSymbolUses`: every
// definition under a `.Tests.` namespace is read as a test). Those ids are not
// the ids discovery gave the real tests, so a hit in the graph can name a test
// that is not among the discovered ones.
//
// An edit whose only graph hits are such names selected nothing, and was
// reported as "every affected test is outside the live-testing scope": a green
// status that was never re-judged. It showed up as an intermittent miss in
// McpToolOutcomeTests, because recorded coverage (which names the real tests)
// lands some time after the baseline run, and an edit made before it hit only
// the invented names.

let private mkTest fullName =
  { Id = TestId.create fullName TestFramework.Expecto
    FullName = fullName
    DisplayName = fullName
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private exactGraph entries =
  { TestDependencyGraph.empty with
      SymbolToTests = Map.ofList entries
      TransitiveCoverage = Map.ofList entries }

let private activeState discovered =
  { LiveTestState.empty with
      Activation = LiveTestingActivation.Active
      DiscoveredTests = discovered }

let private reference path line name =
  { SymbolFullName = name
    UseKind = SymbolUseKind.Reference
    UsedInTestId = None
    FilePath = path
    Line = line }

let private definition path line name =
  { SymbolFullName = name
    UseKind = SymbolUseKind.Definition
    UsedInTestId = None
    FilePath = path
    Line = line }

/// What a type-check of the McpToolOutcome fixture's Sample.fs resolves: a module
/// under `.Tests.`, three values, and the Expecto calls inside `tests`.
let private sampleFile = "/work/fixtures/McpToolOutcomeFixture/Sample.fs"
let private sampleModule = "SageFs.Tests.Fixtures.McpToolOutcome.Sample"

let private sampleRefs =
  [ definition sampleFile 4 sampleModule
    definition sampleFile 8 (sampleModule + ".add")
    reference sampleFile 8 "Microsoft.FSharp.Core.Operators.(+)"
    definition sampleFile 15 (sampleModule + ".subtract")
    reference sampleFile 15 "Microsoft.FSharp.Core.Operators.(-)"
    definition sampleFile 18 (sampleModule + ".tests")
    reference sampleFile 18 "Expecto.Tests.testList"
    reference sampleFile 21 "Expecto.Tests.test"
    reference sampleFile 22 (sampleModule + ".add")
    reference sampleFile 22 "Expecto.Flip.Expect.equal"
    reference sampleFile 25 "Expecto.Tests.test"
    reference sampleFile 26 (sampleModule + ".subtract")
    reference sampleFile 26 "Expecto.Flip.Expect.equal" ]

let private sampleTests =
  [| mkTest (sampleModule + ".tests/arithmetic/add computes the sum")
     mkTest (sampleModule + ".tests/arithmetic/subtract computes the difference")
     mkTest (sampleModule + ".tests/identity/add zero is identity") |]

let private selectedByEval (effects: TestCycleEffect list) : TestId list =
  effects
  |> List.collect (fun effect ->
    match effect with
    | TestCycleEffect.EvalBufferThenRunAffected request ->
      request.Run.Tests |> Array.map (fun test -> test.Id) |> Array.toList
    | TestCycleEffect.RunAffectedTests request
    | TestCycleEffect.RequestRebuild (_, request) ->
      request.Tests |> Array.map (fun test -> test.Id) |> Array.toList
    | _ -> [])

[<Tests>]
let tests =
  testList "Live testing: a selection that names no discovered test" [
    testCase "the first save of a test file, before any coverage is recorded, still runs the discovered tests" <| fun _ ->
      let cycle =
        { LiveTestCycleState.empty with TestState = activeState sampleTests }
        |> LiveTestCycleState.onFileSaveWithContent "module edited" sampleFile (DateTimeOffset.UtcNow)
      let effects, after =
        LiveTestCycleState.handleFcsResult (FcsTypeCheckResult.Success (sampleFile, sampleRefs)) cycle
      selectedByEval effects
      |> List.sort
      |> Expect.equal
           "every discovered test is queued: the graph's only hit named a test discovery never reported"
           (sampleTests |> Array.map (fun test -> test.Id) |> Array.toList |> List.sort)
      match after.TestState.LastDecision with
      | Some decision ->
        decision.Explanation.Precision
        |> Expect.notEqual
             "the decision must not read as policy suppression when policy held nothing back"
             SelectionPrecision.SuppressedByPolicy
      | None -> failtest "expected a recorded decision"

    testCase "a graph hit that resolves to a discovered test still narrows the run to it" <| fun _ ->
      let impacted = mkTest "Module.Tests.should_add"
      let unrelated = mkTest "Other.Tests.should_greet"
      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          { Changed = [ "Module.add" ]; InFile = []; Lines = ChangedLines.NoBaseline }
          "Module.fs"
          RunTrigger.FileSave
          (exactGraph [ "Module.add", [| impacted.Id |]; "Other.greet", [| unrelated.Id |] ])
          (activeState [| impacted; unrelated |])
          None
          Map.empty
      selectedByEval outcome.Effects
      |> Expect.equal "narrowing is untouched when the graph names a real test" [ impacted.Id ]

    testProperty "an edit to a compiled file under live testing never selects nothing while tests are discovered" <|
      Prop.forAll
        (Arb.fromGen (
          gen {
            let! discoveredCount = Gen.choose (1, 4)
            let! phantomCount = Gen.choose (0, 3)
            let symbols = [ "A.f"; "A.g"; "B.h"; "B.i" ]
            let pick = Gen.choose (0, 1)
            let! rows = Gen.listOfLength symbols.Length (Gen.listOfLength (discoveredCount + phantomCount) pick)
            let hits = List.zip symbols rows
            let! changedPicks = Gen.listOfLength symbols.Length pick
            let changed =
              List.zip symbols changedPicks
              |> List.filter (fun (_, pick) -> pick = 1)
              |> List.map fst
            let! inFile = Gen.elements [ []; [ "A.f" ]; symbols ]
            return discoveredCount, phantomCount, hits, (match changed with [] -> [ "A.f" ] | some -> some), inFile
          }))
        (fun (discoveredCount, phantomCount, hits, changed, inFile) ->
          let discovered = [| for i in 1 .. discoveredCount -> mkTest (sprintf "Suite.Tests.case%d" i) |]
          let phantoms = [| for i in 1 .. phantomCount -> TestId.create (sprintf "Phantom.Tests.value%d" i) TestFramework.Expecto |]
          let pool = Array.append (discovered |> Array.map (fun test -> test.Id)) phantoms
          let entries =
            hits
            |> List.map (fun (symbol, picks) ->
              symbol, Array.zip pool (List.toArray picks) |> Array.filter (fun (_, pick) -> pick = 1) |> Array.map fst)
            |> List.filter (fun (_, ids) -> not (Array.isEmpty ids))
          let outcome =
            TestCycleEffects.decideAfterTypeCheck
              { Changed = changed; InFile = inFile; Lines = ChangedLines.NoBaseline }
              "Suite.fs"
              RunTrigger.FileSave
              (exactGraph entries)
              (activeState discovered)
              None
              Map.empty
          not (List.isEmpty outcome.Effects))
  ]
