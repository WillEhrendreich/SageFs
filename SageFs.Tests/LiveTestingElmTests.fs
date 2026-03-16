module SageFs.Tests.LiveTestingElmTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers
open SageFs.Measures

// ── Elm Integration Tests ──

[<Tests>]
let elmIntegrationTests = testList "LiveTesting Elm Integration" [

  testList "Model structure" [
    test "SageFsModel has LiveTesting field" {
      typeof<SageFsModel>.GetProperties()
      |> Array.exists (fun p -> p.Name = "LiveTesting")
      |> Expect.isTrue "SageFsModel should have LiveTesting field"
    }

    test "(SageFsModel.initial()) has empty LiveTestState" {
      let model = (SageFsModel.initial())
      model.LiveTesting.TestState.DiscoveredTests
      |> Expect.equal "no discovered tests" Array.empty
      TestRunPhase.isAnyRunning model.LiveTesting.TestState.RunPhases
      |> Expect.isFalse "not running"
      model.LiveTesting.TestState.Activation
      |> Expect.equal "inactive by default" LiveTestingActivation.Inactive
    }
  ]

  testList "Event cases" [
    let hasCase name =
      Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<SageFsEvent>)
      |> Array.exists (fun uc -> uc.Name = name)
      |> Expect.isTrue (sprintf "SageFsEvent should have %s case" name)
    test "TestsDiscovered" { hasCase "TestsDiscovered" }
    test "TestResultsBatch" { hasCase "TestResultsBatch" }
    test "LiveTestingEnabled" { hasCase "LiveTestingEnabled" }
    test "LiveTestingDisabled" { hasCase "LiveTestingDisabled" }
    test "AffectedTestsComputed" { hasCase "AffectedTestsComputed" }
    test "CoverageUpdated" { hasCase "CoverageUpdated" }
    test "RunPolicyChanged" { hasCase "RunPolicyChanged" }
    test "ProvidersDetected" { hasCase "ProvidersDetected" }
    test "TestRunStarted" { hasCase "TestRunStarted" }
  ]

  testList "Update behavior" [
    test "TestsDiscovered updates DiscoveredTests" {
      let tc : TestCase =
        { Id = mkTestId "myTest" TestFramework.Expecto; DisplayName = "myTest"
          FullName = "MyModule.myTest"; Framework = TestFramework.Expecto
          Origin = TestOrigin.ReflectionOnly; Labels = []
          Category = TestCategory.Unit }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", [| tc |])))
          (SageFsModel.initial())
      model'.LiveTesting.TestState.DiscoveredTests.Length
      |> Expect.equal "should have 1 test" 1
    }
    test "TestRunStarted sets RunPhases to Running and AffectedTests" {
      let tid = mkTestId "t1" (TestFramework.Unknown "x")
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "s")))
          (SageFsModel.initial())
      TestRunPhase.isAnyRunning model'.LiveTesting.TestState.RunPhases
      |> Expect.isTrue "should be running"
      Set.contains tid model'.LiveTesting.TestState.AffectedTests
      |> Expect.isTrue "should contain test id"
    }
    test "TestResultsBatch merges results but keeps RunPhase (cleared by TestRunCompleted)" {
      let gen = RunGeneration.next RunGeneration.zero
      let m =
        { (SageFsModel.initial()) with
            LiveTesting =
              { (SageFsModel.initial()).LiveTesting with TestState = { (SageFsModel.initial()).LiveTesting.TestState with RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen } } }
      let tid = mkTestId "t1" (TestFramework.Unknown "x")
      let r : TestRunResult =
        { TestId = tid; TestName = "t1"
          Result = LTTestResult.Passed (ts 5.0)
          Timestamp = DateTimeOffset.UtcNow
          Output = None }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestResultsBatch [| r |])) m
      TestRunPhase.isAnyRunning model'.LiveTesting.TestState.RunPhases
      |> Expect.isTrue "should still be running (streaming — TestRunCompleted clears phase)"
      Map.containsKey tid model'.LiveTesting.TestState.LastResults
      |> Expect.isTrue "should have result"
    }
    test "LiveTestingEnabled activates" {
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event SageFsEvent.LiveTestingEnabled)
          (SageFsModel.initial())
      model'.LiveTesting.TestState.Activation
      |> Expect.equal "should be active" LiveTestingActivation.Active
    }
    test "LiveTestingDisabled deactivates" {
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event SageFsEvent.LiveTestingDisabled)
          (SageFsModel.initial())
      model'.LiveTesting.TestState.Activation
      |> Expect.equal "should be inactive" LiveTestingActivation.Inactive
    }
    test "AffectedTestsComputed sets AffectedTests" {
      let t1 = mkTestId "t1" (TestFramework.Unknown "x")
      let t2 = mkTestId "t2" (TestFramework.Unknown "x")
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.AffectedTestsComputed [| t1; t2 |]))
          (SageFsModel.initial())
      Set.count model'.LiveTesting.TestState.AffectedTests
      |> Expect.equal "should have 2 affected" 2
    }
    test "RunPolicyChanged updates policy" {
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.RunPolicyChanged (TestCategory.Integration, RunPolicy.OnSaveOnly)))
          (SageFsModel.initial())
      Map.find TestCategory.Integration model'.LiveTesting.TestState.RunPolicies
      |> Expect.equal "should be OnSaveOnly" RunPolicy.OnSaveOnly
    }
    test "ProvidersDetected updates providers" {
      let p = ProviderDescription.Custom { Name = TestFramework.Expecto; AssemblyMarker = "Expecto" }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.ProvidersDetected [p]))
          (SageFsModel.initial())
      model'.LiveTesting.TestState.DetectedProviders.Length
      |> Expect.equal "should have 1 provider" 1
    }
    test "CoverageUpdated produces annotations" {
      let cs : CoverageState =
        { Slots =
            [| { SequencePoint.File = "a.fs"; Line = 10; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
               { SequencePoint.File = "a.fs"; Line = 20; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 } |]
          Hits = [| true; false |] }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.CoverageUpdated cs))
          (SageFsModel.initial())
      model'.LiveTesting.TestState.CoverageAnnotations.Length
      |> Expect.equal "should have 2 annotations" 2
    }
    test "no effects for live testing events" {
      let _, effects =
        SageFsUpdate.update
          (SageFsMsg.Event SageFsEvent.LiveTestingEnabled)
          (SageFsModel.initial())
      effects |> Expect.isEmpty "should produce no effects"
    }
  ]
]

// ── Instrumentation Tests ──

[<Tests>]
let stalenessTests = testList "Staleness" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" TestFramework.Expecto; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" TestFramework.Expecto; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let mkResult tid res =
    { TestId = tid; TestName = ""; Result = res; Timestamp = DateTimeOffset.UtcNow; Output = None }
  let depGraph = {
    SymbolToTests = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
    ]
    TransitiveCoverage = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
    ]; SourceVersion = 1
    PerFileIndex = Map.empty
  }
  let baseState = {
    LiveTestState.empty with
      DiscoveredTests = [| test1; test2 |]
      LastResults = Map.ofList [
        test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
        test2.Id, mkResult test2.Id (TestResult.Passed (TimeSpan.FromMilliseconds 3.0))
      ]
      Activation = LiveTestingActivation.Active
  }

  test "markStale preserves original result for affected tests" {
    let result = Staleness.markStale depGraph [ "Module.add" ] baseState
    let r = Map.find test1.Id result.LastResults
    match r.Result with
    | TestResult.Passed _ -> ()
    | other -> failwithf "Expected original Passed preserved, got %A" other
    let r2 = Map.find test2.Id result.LastResults
    match r2.Result with
    | TestResult.Passed _ -> ()
    | other -> failwithf "Expected Passed, got %A" other
  }

  test "markStale sets affected tests in state" {
    let result = Staleness.markStale depGraph [ "Module.add" ] baseState
    result.AffectedTests |> Expect.contains "test1 affected" test1.Id
  }

  test "markStale with shared symbol affects multiple tests" {
    let result = Staleness.markStale depGraph [ "Module.validate" ] baseState
    result.AffectedTests.Count |> Expect.equal "2 affected" 2
  }

  test "markStale with unknown symbol changes nothing" {
    let result = Staleness.markStale depGraph [ "Unknown.func" ] baseState
    result.AffectedTests |> Expect.isEmpty "no affected"
  }

  test "affected test with prior Passed shows Stale status" {
    let result = Staleness.markStale depGraph [ "Module.add" ] baseState
    let entry = result.StatusEntries |> Array.find (fun e -> e.TestId = test1.Id)
    match entry.Status with
    | TestRunStatus.Stale -> ()
    | other -> failwithf "expected Stale but got %A" other
  }

  test "affected test with no prior result shows Queued status" {
    let tc3 = mkTestCase "Module.Tests.newTest" TestFramework.Expecto TestCategory.Unit
    let graph2 = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "Module.add", [| tc3.Id |] ]
        TransitiveCoverage = Map.ofList [ "Module.add", [| tc3.Id |] ]
    }
    let stateNoPrior = {
      LiveTestState.empty with
        DiscoveredTests = [| tc3 |]
        Activation = LiveTestingActivation.Active
    }
    let result = Staleness.markStale graph2 [ "Module.add" ] stateNoPrior
    let entry = result.StatusEntries |> Array.find (fun e -> e.TestId = tc3.Id)
    match entry.Status with
    | TestRunStatus.Queued -> ()
    | other -> failwithf "expected Queued but got %A" other
  }

  test "unaffected test with Passed result stays Passed" {
    let result = Staleness.markStale depGraph [ "Module.add" ] baseState
    let entry = result.StatusEntries |> Array.find (fun e -> e.TestId = test2.Id)
    match entry.Status with
    | TestRunStatus.Passed _ -> ()
    | other -> failwithf "unaffected expected Passed but got %A" other
  }
]

// ============================================================
// cycle Orchestrator Tests
// ============================================================

// --- Elm Wiring Behavioral Scenario Tests ---

let hasPendingWork (s: LiveTestCycleState) =
  s.Debounce.TreeSitter.Pending.IsSome || s.Debounce.Fcs.Pending.IsSome

[<Tests>]
let elmWiringBehavioralTests = testList "Elm Wiring Behavioral Scenarios" [
  test "cold start: tick on empty cycle produces no effects" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let effects, s' = LiveTestCycleState.empty |> LiveTestCycleState.tick t0
    effects |> Expect.isEmpty "no effects on empty cycle"
    s' |> hasPendingWork |> Expect.isFalse "no pending work"
  }

  test "keystroke then tick past debounce fires TreeSitter parse" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestCycleState.tick (t0.AddMilliseconds(51.0))
    effects
    |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "TreeSitter parse fires after debounce"
  }

  test "full cycle: keystroke through FCS debounce" {
    let tc =
      { Id = TestId.create "T.t1" TestFramework.Expecto
        FullName = "T.t1"
        DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = TestFramework.Expecto
        Category = TestCategory.Unit }
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let depGraph =
      { TestDependencyGraph.empty with
          SymbolToTests = Map.ofList ["Lib.add", [|tc.Id|]]
          TransitiveCoverage = Map.ofList ["Lib.add", [|tc.Id|]] }
    let state =
      { LiveTestCycleState.empty with
          DepGraph = depGraph
          ChangedSymbols = ["Lib.add"]
          TestState =
            { LiveTestState.empty with
                DiscoveredTests = [|tc|]
                Activation = LiveTestingActivation.Active } }
    let s1 = state |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects301, _ = s1 |> LiveTestCycleState.tick (t0.AddMilliseconds(301.0))
    effects301
    |> List.exists (fun e -> match e with TestCycleEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    |> Expect.isTrue "FCS fires after 300ms debounce"
  }

  test "cycle goes idle after both debounces fire" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let _, s51 = s |> LiveTestCycleState.tick (t0.AddMilliseconds(51.0))
    let _, s301 = s51 |> LiveTestCycleState.tick (t0.AddMilliseconds(301.0))
    s301 |> hasPendingWork |> Expect.isFalse "cycle idle after both debounces"
    let effects500, _ = s301 |> LiveTestCycleState.tick (t0.AddMilliseconds(500.0))
    effects500 |> Expect.isEmpty "no further effects after idle"
  }

  test "rapid keystrokes: only latest content fires debounce" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestCycleState.empty
    let s1 = s0 |> LiveTestCycleState.onKeystroke "l" "F.fs" t0
    let s2 = s1 |> LiveTestCycleState.onKeystroke "le" "F.fs" (t0.AddMilliseconds(20.0))
    let s3 = s2 |> LiveTestCycleState.onKeystroke "let" "F.fs" (t0.AddMilliseconds(40.0))
    let effects, _ = s3 |> LiveTestCycleState.tick (t0.AddMilliseconds(91.0))
    let tsEffects =
      effects |> List.choose (fun e -> match e with TestCycleEffect.ParseTreeSitter (c, _) -> Some c | _ -> None)
    tsEffects |> Expect.hasLength "exactly one parse" 1
  }

  test "test result merge updates status entries" {
    let tc =
      { Id = TestId.create "T.t1" TestFramework.Expecto
        FullName = "T.t1"
        DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = TestFramework.Expecto
        Category = TestCategory.Unit }
    let now = DateTimeOffset.UtcNow
    let state =
      { LiveTestState.empty with
          DiscoveredTests = [|tc|]
          Activation = LiveTestingActivation.Active }
    let result : TestRunResult =
      { TestId = tc.Id
        TestName = tc.FullName
        Result = TestResult.Passed(TimeSpan.FromMilliseconds(5.0))
        Timestamp = now
        Output = None }
    let merged = LiveTesting.mergeResultsWithUpdatedStatusEntries state [|result|]
    LiveTestState.orderedStatusEntries merged |> Array.tryFind (fun e -> e.TestId = tc.Id)
    |> Option.map (fun e -> match e.Status with TestRunStatus.Passed _ -> true | _ -> false)
    |> Option.defaultValue false
    |> Expect.isTrue "status entry shows Passed after merge"
  }

  test "staleness then re-run clears stale" {
    let tc =
      { Id = TestId.create "T.t1" TestFramework.Expecto
        FullName = "T.t1"
        DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = TestFramework.Expecto
        Category = TestCategory.Unit }
    let now = DateTimeOffset.UtcNow
    let result : TestRunResult =
      { TestId = tc.Id
        TestName = tc.FullName
        Result = TestResult.Passed(TimeSpan.FromMilliseconds(5.0))
        Timestamp = now
        Output = None }
    let state =
      { LiveTestState.empty with
          DiscoveredTests = [|tc|]
          Activation = LiveTestingActivation.Active
          LastResults = Map.ofList [tc.Id, result] }
    let depGraph =
      { TestDependencyGraph.empty with
          SymbolToTests = Map.ofList ["Lib.add", [|tc.Id|]]
          TransitiveCoverage = Map.ofList ["Lib.add", [|tc.Id|]] }
    let gen = RunGeneration.next RunGeneration.zero
    let stale =
      { Staleness.markStale depGraph ["Lib.add"] state with RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen }
    LiveTestState.orderedStatusEntries stale |> Array.exists (fun e -> match e.Status with TestRunStatus.Stale -> true | _ -> false)
    |> Expect.isTrue "entry is Stale after symbol change"
    let newResult : TestRunResult =
      { TestId = tc.Id
        TestName = tc.FullName
        Result = TestResult.Passed(TimeSpan.FromMilliseconds(3.0))
        Timestamp = now.AddSeconds(1.0)
        Output = None }
    let cleared = LiveTesting.mergeResultsWithUpdatedStatusEntries stale [|newResult|]
    LiveTestState.orderedStatusEntries cleared |> Array.exists (fun e -> match e.Status with TestRunStatus.Passed _ -> true | _ -> false)
    |> Expect.isTrue "entry is Passed after re-run"
  }

  test "disabled policy shows PolicyDisabled in status entries" {
    let tc =
      { Id = TestId.create "T.t1" TestFramework.Expecto
        FullName = "T.t1"
        DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = TestFramework.Expecto
        Category = TestCategory.Unit }
    let state =
      { LiveTestState.empty with
          DiscoveredTests = [|tc|]
          Activation = LiveTestingActivation.Active
          RunPolicies = Map.ofList [TestCategory.Unit, RunPolicy.Disabled] }
    let entries = LiveTesting.computeStatusEntries state
    entries |> Array.exists (fun e -> match e.Status with TestRunStatus.PolicyDisabled -> true | _ -> false)
    |> Expect.isTrue "disabled policy shows PolicyDisabled"
  }
]

// --- FileContentChanged Integration Tests ---

[<Tests>]
let fileContentChangedTests = testList "FileContentChanged" [
  test "feeds content to cycle debounce when enabled" {
    let model = { (SageFsModel.initial()) with LiveTesting = { LiveTestCycleState.empty with TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } } }
    let newModel, _effects = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/MyModule.fs", "let x = 1")) model
    newModel.LiveTesting.ActiveFile
    |> Expect.equal "active file set" (Some "src/MyModule.fs")
    newModel.LiveTesting.Debounce.TreeSitter.Pending.IsSome
    |> Expect.isTrue "tree-sitter debounce pending"
    newModel.LiveTesting.Debounce.Fcs.Pending.IsSome
    |> Expect.isTrue "fcs debounce pending"
  }

  test "is no-op when live testing is disabled" {
    let model = { (SageFsModel.initial()) with LiveTesting = { LiveTestCycleState.empty with TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Inactive } } }
    let newModel, _effects = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/MyModule.fs", "let x = 1")) model
    newModel.LiveTesting.ActiveFile
    |> Expect.equal "active file unchanged" model.LiveTesting.ActiveFile
  }

  test "cycle tick after debounce fires tree-sitter effect" {
    let model = { (SageFsModel.initial()) with LiveTesting = { LiveTestCycleState.empty with TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } } }
    let afterKeystroke, _ = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/MyModule.fs", "let x = 1")) model
    let cycle = afterKeystroke.LiveTesting
    let t51 = DateTimeOffset.UtcNow.AddMilliseconds(51.0)
    let effects, _ = LiveTestCycleState.tick t51 cycle
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.ParseTreeSitter _ -> true
      | _ -> false)
    |> Expect.isTrue "tree-sitter parse fires after debounce"
  }

  test "multiple file changes supersede earlier ones" {
    let model = { (SageFsModel.initial()) with LiveTesting = { LiveTestCycleState.empty with TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } } }
    let after1, _ = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/First.fs", "let a = 1")) model
    let after2, _ = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/Second.fs", "let b = 2")) after1
    after2.LiveTesting.ActiveFile
    |> Expect.equal "latest file wins" (Some "src/Second.fs")
  }
]

[<Tests>]
let fcsTypeCheckResultTests = testList "FcsTypeCheckResult" [
  test "Success updates symbol graph via onFcsComplete" {
    let tc = mkTestCase "MyApp.Tests.testAdd" TestFramework.Expecto TestCategory.Unit
    let refs = [
      { SymbolReference.SymbolFullName = "MyApp.Tests.testAdd"
        UseKind = SymbolUseKind.Definition; UsedInTestId = None
        FilePath = "Test.fs"; Line = 1 }
      { SymbolReference.SymbolFullName = "Lib.add"
        UseKind = SymbolUseKind.Reference; UsedInTestId = None
        FilePath = "Test.fs"; Line = 5 }
    ]
    let state = {
      LiveTestCycleState.empty with
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let result = FcsTypeCheckResult.Success ("Test.fs", refs)
    let _effects, s1 = LiveTestCycleState.handleFcsResult result state
    s1.DepGraph.SymbolToTests
    |> Map.containsKey "Lib.add"
    |> Expect.isTrue "dep graph has Lib.add"
  }

  test "Success with changed symbols triggers RunAffectedTests" {
    let tc = mkTestCase "MyApp.Tests.testAdd" TestFramework.Expecto TestCategory.Unit
    let refs = [
      { SymbolReference.SymbolFullName = "MyApp.Tests.testAdd"
        UseKind = SymbolUseKind.Definition; UsedInTestId = None
        FilePath = "Test.fs"; Line = 1 }
      { SymbolReference.SymbolFullName = "Lib.add"
        UseKind = SymbolUseKind.Reference; UsedInTestId = None
        FilePath = "Test.fs"; Line = 5 }
    ]
    let state = {
      LiveTestCycleState.empty with
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let result = FcsTypeCheckResult.Success ("Test.fs", refs)
    let effects, _ = LiveTestCycleState.handleFcsResult result state
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.RequestRebuild _ -> true
      | TestCycleEffect.RunAffectedTests _ -> true
      | _ -> false)
    |> Expect.isTrue "RequestRebuild or RunAffectedTests fires on new symbols"
  }

  test "Success updates adaptive debounce" {
    let state = LiveTestCycleState.empty
    let result = FcsTypeCheckResult.Success ("test.fs", [])
    let _, s1 = LiveTestCycleState.handleFcsResult result state
    s1.AdaptiveDebounce.ConsecutiveFcsSuccesses
    |> Expect.equal "success count incremented" 1
  }

  test "Failed produces no effects" {
    let state = LiveTestCycleState.empty
    let result = FcsTypeCheckResult.Failed ("test.fs", ["error: type mismatch"])
    let effects, _ = LiveTestCycleState.handleFcsResult result state
    effects |> Expect.isEmpty "no effects on failure"
  }

  test "Failed does not change adaptive debounce" {
    let state = LiveTestCycleState.empty
    let result = FcsTypeCheckResult.Failed ("test.fs", ["error"])
    let _, s1 = LiveTestCycleState.handleFcsResult result state
    s1.AdaptiveDebounce.ConsecutiveFcsSuccesses
    |> Expect.equal "unchanged success count" 0
    s1.AdaptiveDebounce.ConsecutiveFcsCancels
    |> Expect.equal "unchanged cancel count" 0
  }

  test "Cancelled updates adaptive debounce backoff" {
    let state = LiveTestCycleState.empty
    let result = FcsTypeCheckResult.Cancelled "test.fs"
    let effects, s1 = LiveTestCycleState.handleFcsResult result state
    effects |> Expect.isEmpty "no effects on cancel"
    s1.AdaptiveDebounce.ConsecutiveFcsCancels
    |> Expect.equal "cancel count incremented" 1
  }

  test "Cancelled increases FCS delay" {
    let state = LiveTestCycleState.empty
    let baseFcsMs = state.AdaptiveDebounce.Config.BaseFcsMs
    let result = FcsTypeCheckResult.Cancelled "test.fs"
    let _, s1 = LiveTestCycleState.handleFcsResult result state
    (s1.AdaptiveDebounce.CurrentFcsDelayMs, baseFcsMs)
    |> Expect.isGreaterThan "delay increased after cancel"
  }

  test "Multiple successes reset FCS delay to base" {
    let state = LiveTestCycleState.empty
    let _, s1 = LiveTestCycleState.handleFcsResult (FcsTypeCheckResult.Cancelled "f.fs") state
    let resetCount = s1.AdaptiveDebounce.Config.ResetAfterSuccessCount
    let mutable s = s1
    for _ in 1..resetCount do
      let _, sn = LiveTestCycleState.handleFcsResult (FcsTypeCheckResult.Success ("f.fs", [])) s
      s <- sn
    s.AdaptiveDebounce.CurrentFcsDelayMs
    |> Expect.equal "delay reset to base" state.AdaptiveDebounce.Config.BaseFcsMs
  }

  test "Elm wiring: FcsTypeCheckCompleted Success updates model and emits effects" {
    let tc = mkTestCase "MyApp.Tests.testAdd" TestFramework.Expecto TestCategory.Unit
    let refs = [
      { SymbolReference.SymbolFullName = "MyApp.Tests.testAdd"
        UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Test.fs"; Line = 1 }
      { SymbolReference.SymbolFullName = "Lib.add"
        UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Test.fs"; Line = 5 }
    ]
    let model = {
      (SageFsModel.initial()) with
        LiveTesting = {
          LiveTestCycleState.empty with
            TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
        }
    }
    let msg = SageFsMsg.FcsTypeCheckCompleted (FcsTypeCheckResult.Success ("Test.fs", refs))
    let model', effects = SageFsUpdate.update msg model
    model'.LiveTesting.DepGraph.SymbolToTests
    |> Map.containsKey "Lib.add"
    |> Expect.isTrue "model dep graph updated"
    effects
    |> List.exists (fun e ->
      match e with
      | SageFsEffect.TestCycle (TestCycleEffect.RequestRebuild _) -> true
      | SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests _) -> true
      | _ -> false)
    |> Expect.isTrue "cycle RequestRebuild or RunAffectedTests effect emitted"
  }

  test "Elm wiring: FcsTypeCheckCompleted Failed is no-op" {
    let model = (SageFsModel.initial())
    let msg = SageFsMsg.FcsTypeCheckCompleted (FcsTypeCheckResult.Failed ("test.fs", ["error"]))
    let model', effects = SageFsUpdate.update msg model
    effects |> Expect.isEmpty "no effects on failure"
    model'.LiveTesting.DepGraph.SymbolToTests
    |> Expect.isEmpty "dep graph unchanged"
  }
]

[<Tests>]
let triggerWiringTests = testList "RunTrigger wiring" [
  test "onFileSave sets LastTrigger to FileSave" {
    let now = DateTimeOffset.UtcNow
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onFileSave "f.fs" now
    s.LastTrigger |> Expect.equal "trigger is FileSave" RunTrigger.FileSave
  }

  test "onKeystroke sets LastTrigger to Keystroke" {
    let now = DateTimeOffset.UtcNow
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "x" "f.fs" now
    s.LastTrigger |> Expect.equal "trigger is Keystroke" RunTrigger.Keystroke
  }

  test "handleFcsResult uses stored FileSave trigger for OnSaveOnly tests" {
    let tc = mkTestCase "MyApp.Tests.archTest" TestFramework.Expecto TestCategory.Architecture
    let refs = [
      { SymbolReference.SymbolFullName = "MyApp.Tests.archTest"
        UseKind = SymbolUseKind.Definition; UsedInTestId = None
        FilePath = "Test.fs"; Line = 1 }
      { SymbolReference.SymbolFullName = "Lib.check"
        UseKind = SymbolUseKind.Reference; UsedInTestId = None
        FilePath = "Test.fs"; Line = 5 }
    ]
    let now = DateTimeOffset.UtcNow
    let s0 = {
      LiveTestCycleState.empty with
        TestState = { LiveTestState.empty with
                        DiscoveredTests = [| tc |]
                        RunPolicies = RunPolicyDefaults.defaults
                        Activation = LiveTestingActivation.Active }
    }
    let s1 = s0 |> LiveTestCycleState.onFileSave "Test.fs" now
    let effects, _ =
      LiveTestCycleState.handleFcsResult (FcsTypeCheckResult.Success ("Test.fs", refs)) s1
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.RequestRebuild _ -> true
      | TestCycleEffect.RunAffectedTests _ -> true
      | _ -> false)
    |> Expect.isTrue "OnSaveOnly test runs with FileSave trigger"
  }

  test "handleFcsResult with Keystroke trigger filters out OnSaveOnly tests" {
    let tc = mkTestCase "MyApp.Tests.archTest" TestFramework.Expecto TestCategory.Architecture
    let refs = [
      { SymbolReference.SymbolFullName = "MyApp.Tests.archTest"
        UseKind = SymbolUseKind.Definition; UsedInTestId = None
        FilePath = "Test.fs"; Line = 1 }
      { SymbolReference.SymbolFullName = "Lib.check"
        UseKind = SymbolUseKind.Reference; UsedInTestId = None
        FilePath = "Test.fs"; Line = 5 }
    ]
    let now = DateTimeOffset.UtcNow
    let s0 = {
      LiveTestCycleState.empty with
        TestState = { LiveTestState.empty with
                        DiscoveredTests = [| tc |]
                        RunPolicies = RunPolicyDefaults.defaults
                        Activation = LiveTestingActivation.Active }
    }
    let s1 = s0 |> LiveTestCycleState.onKeystroke "let x = 1" "Test.fs" now
    let effects, _ =
      LiveTestCycleState.handleFcsResult (FcsTypeCheckResult.Success ("Test.fs", refs)) s1
    effects
    |> Expect.isEmpty "OnSaveOnly test filtered out on Keystroke"
  }
]

[<Tests>]
let adaptiveDebounceWiringTests = testList "adaptive debounce wiring" [
  test "onKeystroke uses adaptive FCS delay after cancellations" {
    let now = DateTimeOffset.UtcNow
    let s0 = LiveTestCycleState.empty
    let s1 = s0 |> LiveTestCycleState.onFcsCanceled
    let s2 = s1 |> LiveTestCycleState.onFcsCanceled
    let s3 = s2 |> LiveTestCycleState.onFcsCanceled
    let expectedDelay = LanguagePrimitives.Int32WithMeasure<ms> (int (300.0 * 1.5 * 1.5 * 1.5))
    let s4 = s3 |> LiveTestCycleState.onKeystroke "let x = 1" "Test.fs" now
    match s4.Debounce.Fcs.Pending with
    | Some p ->
      p.DelayMs |> Expect.equal "FCS delay reflects adaptive backoff" expectedDelay
    | None -> failtest "FCS debounce should have a pending entry"
  }

  test "onKeystroke uses base delay with no cancellations" {
    let now = DateTimeOffset.UtcNow
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "x" "f.fs" now
    match s.Debounce.Fcs.Pending with
    | Some p ->
      p.DelayMs |> Expect.equal "base FCS delay" 300<ms>
    | None -> failtest "FCS debounce should have a pending entry"
  }

  test "adaptive delay resets after consecutive successes" {
    let now = DateTimeOffset.UtcNow
    let s0 = LiveTestCycleState.empty
    // Cancel to raise delay
    let s1 = s0 |> LiveTestCycleState.onFcsCanceled
    (LiveTestCycleState.currentFcsDelay s1, 300.0<ms>)
    |> Expect.isGreaterThan "delay raised"
    // Reset via consecutive successes
    let mutable s = s1
    for _ in 1 .. s.AdaptiveDebounce.Config.ResetAfterSuccessCount do
      let _, sn = LiveTestCycleState.handleFcsResult (FcsTypeCheckResult.Success ("f.fs", [])) s
      s <- sn
    let s2 = s |> LiveTestCycleState.onKeystroke "x" "f.fs" now
    match s2.Debounce.Fcs.Pending with
    | Some p ->
      p.DelayMs |> Expect.equal "delay reset to base after successes" 300<ms>
    | None -> failtest "FCS debounce should have pending"
  }
]

// --- Running → Stale regression tests (Gap 1 fix) ---

let mkSourceMappedTestCase name fw =
  { Id = TestId.create name fw
    FullName = name; DisplayName = name
    Origin = TestOrigin.SourceMapped ("Foo.fs", 10)
    Labels = []; Framework = fw; Category = TestCategory.Unit }

let mkPassedResult tid =
  { TestId = tid; TestName = TestId.value tid
    Result = TestResult.Passed (TimeSpan.FromMilliseconds 10.0)
    Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5.0); Output = None }

[<Tests>]
let runningToStaleOnKeystrokeTests = testList "Running → Stale on keystroke" [
  test "keystroke while tests running transitions to RunningButEdited" {
    let tid = TestId.create "TestA" TestFramework.Expecto
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestCycleState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" TestFramework.Expecto |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
        }
    }
    let s' = LiveTestCycleState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    match (s'.TestState.RunPhases |> Map.tryFind "s" |> Option.defaultValue Idle) with
    | RunningButEdited _ -> ()
    | other -> failwithf "Expected RunningButEdited, got %A" other
  }

  test "keystroke while tests running preserves AffectedTests" {
    let tid = TestId.create "TestA" TestFramework.Expecto
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestCycleState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" TestFramework.Expecto |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
        }
    }
    let s' = LiveTestCycleState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    s'.TestState.AffectedTests
    |> Expect.isNonEmpty "AffectedTests should be preserved"
  }

  test "status shows Passed after keystroke during running (streaming shows available result)" {
    let tid = TestId.create "TestA" TestFramework.Expecto
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestCycleState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" TestFramework.Expecto |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
            LastResults = Map.ofList [ tid, mkPassedResult tid ]
        }
    }
    let s' = LiveTestCycleState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    let entries = LiveTesting.computeStatusEntries s'.TestState
    // Streaming: result already available, so shows Passed (not Running)
    match entries.[0].Status with
    | TestRunStatus.Passed _ -> ()
    | other -> failtestf "expected Passed, got %A" other
  }

  test "status shows Queued for never-run affected test after keystroke" {
    let tid = TestId.create "TestA" TestFramework.Expecto
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestCycleState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" TestFramework.Expecto |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
        }
    }
    let s' = LiveTestCycleState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    let entries = LiveTesting.computeStatusEntries s'.TestState
    // RunningButEdited still shows Running for affected tests
    entries.[0].Status
    |> Expect.equal "should be Running" TestRunStatus.Running
  }

  test "keystroke while NOT running keeps Idle" {
    let s = LiveTestCycleState.empty
    let s' = LiveTestCycleState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    (s'.TestState.RunPhases |> Map.tryFind "s" |> Option.defaultValue Idle)
    |> Expect.equal "should stay Idle" Idle
  }
]

[<Tests>]
let runningToStaleOnFileSaveTests = testList "Running → Stale on file save" [
  test "save while tests running transitions to RunningButEdited" {
    let tid = TestId.create "TestA" TestFramework.Expecto
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestCycleState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" TestFramework.Expecto |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
            LastResults = Map.ofList [ tid, mkPassedResult tid ]
        }
    }
    let s' = LiveTestCycleState.onFileSave "Foo.fs" DateTimeOffset.UtcNow s
    match (s'.TestState.RunPhases |> Map.tryFind "s" |> Option.defaultValue Idle) with
    | RunningButEdited _ -> ()
    | other -> failwithf "Expected RunningButEdited, got %A" other
  }

  test "save while NOT running keeps Idle" {
    let s = LiveTestCycleState.empty
    let s' = LiveTestCycleState.onFileSave "Foo.fs" DateTimeOffset.UtcNow s
    (s'.TestState.RunPhases |> Map.tryFind "s" |> Option.defaultValue Idle)
    |> Expect.equal "should stay Idle" Idle
  }
]

[<Tests>]
let mergeResultsStalenessFixTests = testList "mergeResults staleness handling" [
  test "mergeResults preserves AffectedTests (no clearing)" {
    let tid = TestId.create "TestA" TestFramework.Expecto
    let result = mkResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestState.empty with
        DiscoveredTests = [| mkSourceMappedTestCase "TestA" TestFramework.Expecto |]
        RunPhases = Map.ofList ["s", RunningButEdited gen]; LastGeneration = gen
        AffectedTests = Set.singleton tid
    }
    let s' = LiveTesting.mergeResults s [| result |]
    s'.AffectedTests
    |> Expect.isNonEmpty "mergeResults should not clear AffectedTests"
  }

  test "streaming result shows Passed while RunPhase is still Running" {
    let tid = TestId.create "TestA" TestFramework.Expecto
    let result = mkResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestState.empty with
        DiscoveredTests = [| mkSourceMappedTestCase "TestA" TestFramework.Expecto |]
        RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
        AffectedTests = Set.singleton tid
    }
    let s' = LiveTesting.mergeResults s [| result |]
    let entries = LiveTesting.computeStatusEntries s'
    match entries.[0].Status with
    | TestRunStatus.Passed _ -> ()
    | other -> failtestf "expected Passed, got %A" other
  }

  test "mergeResults preserves RunPhase as Running" {
    let tid = TestId.create "TestA" TestFramework.Expecto
    let result = mkResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestState.empty with
        DiscoveredTests = [| mkSourceMappedTestCase "TestA" TestFramework.Expecto |]
        RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
        AffectedTests = Set.singleton tid
    }
    let s' = LiveTesting.mergeResults s [| result |]
    TestRunPhase.isAnyRunning s'.RunPhases
    |> Expect.isTrue "RunPhase should still be Running after mergeResults"
  }
]

[<Tests>]
let sessionScopedIsolationTests = testList "session-scoped isolation" [
  test "NotRun does not overwrite Passed result" {
    let tid = TestId.TestId "t1"
    let passed = mkResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    let notRun = mkResult tid TestResult.NotRun
    let state1 = LiveTesting.mergeResults LiveTestState.empty [| passed |]
    let state2 = LiveTesting.mergeResults state1 [| notRun |]
    state2.LastResults
    |> Map.find tid
    |> fun r ->
      match r.Result with
      | TestResult.Passed _ -> ()
      | other -> failwithf "Expected Passed preserved, got %A" other
  }

  test "NotRun does not overwrite Failed result" {
    let tid = TestId.TestId "t1"
    let failed = mkResult tid (TestResult.Failed (TestFailure.AssertionFailed "nope", TimeSpan.FromMilliseconds 5.0))
    let notRun = mkResult tid TestResult.NotRun
    let state1 = LiveTesting.mergeResults LiveTestState.empty [| failed |]
    let state2 = LiveTesting.mergeResults state1 [| notRun |]
    state2.LastResults
    |> Map.find tid
    |> fun r ->
      match r.Result with
      | TestResult.Failed _ -> ()
      | other -> failwithf "Expected Failed preserved, got %A" other
  }

  test "NotRun IS added when no prior result exists" {
    let tid = TestId.TestId "t1"
    let notRun = mkResult tid TestResult.NotRun
    let state = LiveTesting.mergeResults LiveTestState.empty [| notRun |]
    state.LastResults
    |> Map.find tid
    |> fun r ->
      match r.Result with
      | TestResult.NotRun -> ()
      | other -> failwithf "Expected NotRun, got %A" other
  }

  test "statusEntriesForSession filters by session" {
    let state =
      { LiveTestState.empty with
          StatusEntries = [|
            { TestId = TestId.TestId "t1"; DisplayName = "session-a test"; FullName = "session-a test"
              Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
              Category = TestCategory.Unit; CurrentPolicy = RunPolicy.OnEveryChange
              Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected }
            { TestId = TestId.TestId "t2"; DisplayName = "session-b test"; FullName = "session-b test"
              Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.XUnit
              Category = TestCategory.Unit; CurrentPolicy = RunPolicy.OnEveryChange
              Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected }
          |]
          TestSessionMap = Map.ofList [ TestId.TestId "t1", "session-a"; TestId.TestId "t2", "session-b" ] }
    let filtered = LiveTestState.statusEntriesForSession "session-a" state
    filtered.Length |> Expect.equal "should have 1 entry for session-a" 1
    filtered.[0].DisplayName |> Expect.equal "should be session-a test" "session-a test"
  }

  test "statusEntriesForSession returns all when empty session id" {
    let state =
      { LiveTestState.empty with
          StatusEntries = [|
            { TestId = TestId.TestId "t1"; DisplayName = "test1"; FullName = "test1"
              Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
              Category = TestCategory.Unit; CurrentPolicy = RunPolicy.OnEveryChange
              Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected }
          |] }
    let filtered = LiveTestState.statusEntriesForSession "" state
    filtered.Length |> Expect.equal "should return all entries" 1
  }
]

[<Tests>]
let runGenerationTests = testList "RunGeneration" [
  test "zero starts at 0" {
    RunGeneration.value RunGeneration.zero
    |> Expect.equal "zero is 0" 0
  }
  test "next increments" {
    RunGeneration.zero |> RunGeneration.next |> RunGeneration.value
    |> Expect.equal "next of zero is 1" 1
  }
]

[<Tests>]
let testRunPhaseTests = testList "TestRunPhase state machine" [
  test "initial state is Idle" {
    let phase = Idle
    TestRunPhase.isRunning phase
    |> Expect.isFalse "Idle is not running"
  }
  test "startRun transitions to Running with incremented generation" {
    let phase, gen = TestRunPhase.startRun RunGeneration.zero
    TestRunPhase.isRunning phase |> Expect.isTrue "should be running"
    RunGeneration.value gen |> Expect.equal "gen is 1" 1
  }
  test "onEdit from Running transitions to RunningButEdited" {
    let phase, gen = TestRunPhase.startRun RunGeneration.zero
    let edited = TestRunPhase.onEdit phase
    match edited with
    | RunningButEdited g -> RunGeneration.value g |> Expect.equal "same gen" (RunGeneration.value gen)
    | other -> failwithf "Expected RunningButEdited, got %A" other
  }
  test "onEdit from Idle stays Idle" {
    TestRunPhase.onEdit Idle |> Expect.equal "stays Idle" Idle
  }
  test "onEdit from RunningButEdited stays RunningButEdited" {
    let gen = RunGeneration.next RunGeneration.zero
    let phase = RunningButEdited gen
    TestRunPhase.onEdit phase |> Expect.equal "stays RunningButEdited" (RunningButEdited gen)
  }
  test "onResultsArrived with matching gen from Running returns Fresh" {
    let gen = RunGeneration.next RunGeneration.zero
    let phase, freshness = TestRunPhase.onResultsArrived gen (Running gen)
    phase |> Expect.equal "back to Idle" Idle
    freshness |> Expect.equal "fresh" Fresh
  }
  test "onResultsArrived with matching gen from RunningButEdited returns StaleCodeEdited" {
    let gen = RunGeneration.next RunGeneration.zero
    let phase, freshness = TestRunPhase.onResultsArrived gen (RunningButEdited gen)
    phase |> Expect.equal "back to Idle" Idle
    freshness |> Expect.equal "stale code edited" StaleCodeEdited
  }
  test "onResultsArrived with old gen returns StaleWrongGeneration" {
    let oldGen = RunGeneration.next RunGeneration.zero
    let newGen = RunGeneration.next oldGen
    let phase, freshness = TestRunPhase.onResultsArrived oldGen (Running newGen)
    phase |> Expect.equal "back to Idle" Idle
    freshness |> Expect.equal "stale wrong gen" StaleWrongGeneration
  }
  test "multiple edits stay RunningButEdited with same gen" {
    let phase, gen = TestRunPhase.startRun RunGeneration.zero
    let e1 = TestRunPhase.onEdit phase
    let e2 = TestRunPhase.onEdit e1
    match e2 with
    | RunningButEdited g -> RunGeneration.value g |> Expect.equal "same gen" (RunGeneration.value gen)
    | other -> failwithf "Expected RunningButEdited, got %A" other
  }
  test "full lifecycle: run→edit→stale→new run→fresh" {
    let phase1, gen1 = TestRunPhase.startRun RunGeneration.zero
    let edited = TestRunPhase.onEdit phase1
    let phase2, freshness1 = TestRunPhase.onResultsArrived gen1 edited
    freshness1 |> Expect.equal "first run stale" StaleCodeEdited
    phase2 |> Expect.equal "back to Idle" Idle
    let phase3, gen2 = TestRunPhase.startRun gen1
    let phase4, freshness2 = TestRunPhase.onResultsArrived gen2 phase3
    freshness2 |> Expect.equal "second run fresh" Fresh
    phase4 |> Expect.equal "back to Idle" Idle
  }
]

// --- TestResultsBatchPayload tests ---

[<Tests>]
let batchPayloadTests = testList "TestResultsBatchPayload" [
  test "create with fresh results computes summary" {
    let tid = TestId.create "Tests.payload_fresh" TestFramework.Expecto
    let entry = {
      TestId = tid; DisplayName = "payload_fresh"; FullName = "Tests.payload_fresh"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Passed (System.TimeSpan.FromMilliseconds 5.0)
      PreviousStatus = TestRunStatus.Detected }
    let gen = RunGeneration.next RunGeneration.zero
    let batch = TestResultsBatchPayload.create gen ResultFreshness.Fresh (BatchCompletion.Complete(1, 1)) LiveTestingActivation.Active [| entry |]
    batch.Summary.Passed |> Expect.equal "one passed" 1
    batch.Summary.Total |> Expect.equal "one total" 1
    batch.Freshness |> Expect.equal "fresh" ResultFreshness.Fresh
    batch.Generation |> Expect.equal "gen matches" gen
  }

  test "create with stale results carries StaleCodeEdited" {
    let gen = RunGeneration.next RunGeneration.zero
    let batch = TestResultsBatchPayload.create gen ResultFreshness.StaleCodeEdited BatchCompletion.Superseded LiveTestingActivation.Active [||]
    batch.Freshness |> Expect.equal "stale edited" ResultFreshness.StaleCodeEdited
  }

  test "create with wrong generation carries StaleWrongGeneration" {
    let gen = RunGeneration.next RunGeneration.zero
    let batch = TestResultsBatchPayload.create gen ResultFreshness.StaleWrongGeneration BatchCompletion.Superseded LiveTestingActivation.Active [||]
    batch.Freshness |> Expect.equal "wrong gen" ResultFreshness.StaleWrongGeneration
  }

  test "isEmpty returns true for empty entries" {
    let gen = RunGeneration.zero
    let batch = TestResultsBatchPayload.create gen ResultFreshness.Fresh (BatchCompletion.Complete(0, 0)) LiveTestingActivation.Active [||]
    TestResultsBatchPayload.isEmpty batch |> Expect.isTrue "should be empty"
  }

  test "isEmpty returns false for non-empty entries" {
    let tid = TestId.create "Tests.not_empty" TestFramework.Expecto
    let entry = {
      TestId = tid; DisplayName = "not_empty"; FullName = "Tests.not_empty"
      Origin = TestOrigin.ReflectionOnly; Framework = TestFramework.Expecto
      Category = TestCategory.Unit; CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected }
    let gen = RunGeneration.zero
    let batch = TestResultsBatchPayload.create gen ResultFreshness.Fresh (BatchCompletion.Complete(1, 1)) LiveTestingActivation.Active [| entry |]
    TestResultsBatchPayload.isEmpty batch |> Expect.isFalse "should not be empty"
  }
]

[<Tests>]
let elmUpdateStatusRecomputationTests = testList "Elm update StatusEntries recomputation" [
  test "TestsDiscovered recomputes StatusEntries" {
    let tests = [|
      { Id = TestId.create "t1" (TestFramework.Unknown "test1")
        FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]

    let model0 = (SageFsModel.initial())
    let model1 = { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) model1

    model2.LiveTesting.TestState.StatusEntries
    |> Array.length
    |> Expect.equal "should have 1 status entry after TestsDiscovered" 1
  }

  test "AffectedTestsComputed recomputes StatusEntries to Queued" {
    let tid = TestId.create "t1" (TestFramework.Unknown "test1")
    let tests = [|
      { Id = tid; FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]

    let model0 = (SageFsModel.initial())
    let stateWithTests = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active; DiscoveredTests = tests }
    let stateRecomputed = { stateWithTests with StatusEntries = LiveTesting.computeStatusEntries stateWithTests }
    let model1 = { model0 with LiveTesting = { model0.LiveTesting with TestState = stateRecomputed } }

    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.AffectedTestsComputed [| tid |])) model1

    model2.LiveTesting.TestState.StatusEntries
    |> Array.tryHead
    |> Option.map (fun e -> e.Status)
    |> Expect.equal "should be Queued after AffectedTestsComputed" (Some TestRunStatus.Queued)
  }

  test "annotationsForFile works after TestsDiscovered event" {
    let tests = [|
      { Id = TestId.create "t1" (TestFramework.Unknown "test1")
        FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]

    let model0 = (SageFsModel.initial())
    let model1 = { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) model1

    let annotations = LiveTesting.annotationsForFile "editor" model2.LiveTesting.TestState
    annotations
    |> Array.length
    |> Expect.equal "should have 1 annotation after TestsDiscovered" 1
  }

  test "TestRunStarted shows Running status" {
    let tid = TestId.create "t1" (TestFramework.Unknown "test1")
    let tests = [|
      { Id = tid; FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]

    let model0 = (SageFsModel.initial())
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "test-session"))) model1

    model2.LiveTesting.TestState.StatusEntries
    |> Array.tryHead
    |> Option.map (fun e -> e.Status)
    |> Expect.equal "should be Running after TestRunStarted" (Some TestRunStatus.Running)
  }

  test "RunPolicyChanged to Disabled shows PolicyDisabled status" {
    let tests = [|
      { Id = TestId.create "t1" (TestFramework.Unknown "test1")
        FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]

    let model0 = (SageFsModel.initial())
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.RunPolicyChanged (TestCategory.Unit, RunPolicy.Disabled))) model1

    model2.LiveTesting.TestState.StatusEntries
    |> Array.tryHead
    |> Option.map (fun e -> e.Status)
    |> Expect.equal "should be PolicyDisabled after RunPolicyChanged" (Some TestRunStatus.PolicyDisabled)
  }

  test "Full lifecycle: Discovered → Started → Completed shows pass/fail annotations" {
    let tid1 = TestId.create "t1" (TestFramework.Unknown "test1")
    let tid2 = TestId.create "t2" (TestFramework.Unknown "test2")
    let tests = [|
      { Id = tid1; FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      { Id = tid2; FullName = "M.test2"; DisplayName = "test2"
        Origin = TestOrigin.SourceMapped ("editor", 10)
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let results = [|
      { TestId = tid1; TestName = "test1"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 5.0)
        Timestamp = DateTimeOffset.UtcNow; Output = None }
      { TestId = tid2; TestName = "test2"
        Result = TestResult.Failed (TestFailure.AssertionFailed "Expected 42 got 43", TimeSpan.FromMilliseconds 12.0)
        Timestamp = DateTimeOffset.UtcNow; Output = None }
    |]

    let model0 = (SageFsModel.initial())
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid1; tid2 |], Some "test-session"))) m1
    let m3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestResultsBatch results)) m2

    let annotations = LiveTesting.annotationsForFile "editor" m3.LiveTesting.TestState
    annotations |> Array.length |> Expect.equal "should have 2 annotations" 2

    let passAnns = annotations |> Array.filter (fun a -> a.Icon = GutterIcon.TestPassed)
    let failAnns = annotations |> Array.filter (fun a -> a.Icon = GutterIcon.TestFailed)
    passAnns |> Array.length |> Expect.equal "should have 1 pass annotation" 1
    failAnns |> Array.length |> Expect.equal "should have 1 fail annotation" 1
  }
]
