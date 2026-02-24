module SageFs.Tests.LiveTestingTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting

// --- Test Helpers ---

let mkTestId name fw = TestId.create name fw
let ts (ms: float) = TimeSpan.FromMilliseconds ms

let mkTestCase name fw category =
  { Id = TestId.create name fw
    FullName = name; DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = fw; Category = category }

let mkResult testId result =
  { TestId = testId; TestName = TestId.value testId
    Result = result; Timestamp = DateTimeOffset.UtcNow }

let mkAssemblyInfo name (refs: string list) =
  { Name = name; Location = sprintf "/%s.dll" name
    ReferencedAssemblies =
      refs |> List.map (fun r -> AssemblyName(r)) |> Array.ofList }

// --- TestId Tests (GREEN — already correct) ---

[<Tests>]
let testIdTests = testList "TestId" [
  test "create produces stable 16-char hex id" {
    let id = TestId.create "MyModule.Tests.should add" "xunit"
    TestId.value id
    |> Expect.hasLength "should be 16 chars" 16
  }

  test "different fullnames produce different ids" {
    let id1 = TestId.create "Test.A" "xunit"
    let id2 = TestId.create "Test.B" "xunit"
    TestId.value id1
    |> Expect.notEqual "different names should differ" (TestId.value id2)
  }

  test "different frameworks produce different ids" {
    let id1 = TestId.create "Test.A" "xunit"
    let id2 = TestId.create "Test.A" "nunit"
    TestId.value id1
    |> Expect.notEqual "different frameworks should differ" (TestId.value id2)
  }

  test "value extracts the string" {
    let id = TestId.create "test" "fw"
    TestId.value id
    |> Expect.isNotEmpty "should extract string"
  }
]

// --- filterByPolicy Tests (RED — stub returns all) ---

[<Tests>]
let filterByPolicyTests = testList "filterByPolicy" [
  test "keystroke trigger only runs OnEveryChange tests" {
    let unit = mkTestCase "Fast.test" "x" TestCategory.Unit
    let integ = mkTestCase "Slow.test" "x" TestCategory.Integration
    let policies = Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Integration, RunPolicy.OnDemand
    ]
    LiveTesting.filterByPolicy policies RunTrigger.Keystroke [| unit; integ |]
    |> Expect.hasLength "only unit test" 1
  }

  test "file save trigger runs OnEveryChange and OnSaveOnly tests" {
    let unit = mkTestCase "Fast.test" "x" TestCategory.Unit
    let arch = mkTestCase "Arch.test" "x" TestCategory.Architecture
    let integ = mkTestCase "Slow.test" "x" TestCategory.Integration
    let policies = Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Architecture, RunPolicy.OnSaveOnly
      TestCategory.Integration, RunPolicy.OnDemand
    ]
    LiveTesting.filterByPolicy policies RunTrigger.FileSave [| unit; arch; integ |]
    |> Expect.hasLength "unit + architecture" 2
  }

  test "explicit run triggers all non-disabled tests" {
    let unit = mkTestCase "Fast.test" "x" TestCategory.Unit
    let integ = mkTestCase "Slow.test" "x" TestCategory.Integration
    let disabled = mkTestCase "Off.test" "x" TestCategory.Benchmark
    let policies = Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Integration, RunPolicy.OnDemand
      TestCategory.Benchmark, RunPolicy.Disabled
    ]
    LiveTesting.filterByPolicy policies RunTrigger.ExplicitRun [| unit; integ; disabled |]
    |> Expect.hasLength "all except disabled" 2
  }

  test "disabled category blocks all triggers" {
    let tc = mkTestCase "Disabled.test" "x" TestCategory.Unit
    let policies = Map.ofList [ TestCategory.Unit, RunPolicy.Disabled ]
    for trigger in [ RunTrigger.Keystroke; RunTrigger.FileSave; RunTrigger.ExplicitRun ] do
      LiveTesting.filterByPolicy policies trigger [| tc |]
      |> Expect.hasLength "disabled blocks all" 0
  }

  test "property tests run on every change by default" {
    let tc = mkTestCase "Prop.test" "x" TestCategory.Property
    let policies = RunPolicyDefaults.defaults
    LiveTesting.filterByPolicy policies RunTrigger.Keystroke [| tc |]
    |> Expect.hasLength "property runs on keystroke" 1
  }

  test "empty test array returns empty" {
    let policies = RunPolicyDefaults.defaults
    LiveTesting.filterByPolicy policies RunTrigger.Keystroke Array.empty
    |> Expect.hasLength "empty in, empty out" 0
  }

  test "OnDemand category blocked on keystroke" {
    let tc = mkTestCase "Integ.test" "x" TestCategory.Integration
    let policies = Map.ofList [ TestCategory.Integration, RunPolicy.OnDemand ]
    LiveTesting.filterByPolicy policies RunTrigger.Keystroke [| tc |]
    |> Expect.hasLength "OnDemand blocked on keystroke" 0
  }

  test "OnDemand category blocked on file save" {
    let tc = mkTestCase "Integ.test" "x" TestCategory.Integration
    let policies = Map.ofList [ TestCategory.Integration, RunPolicy.OnDemand ]
    LiveTesting.filterByPolicy policies RunTrigger.FileSave [| tc |]
    |> Expect.hasLength "OnDemand blocked on save" 0
  }
]

// --- mergeResults Tests (RED — stub returns state unchanged) ---

[<Tests>]
let mergeResultsTests = testList "mergeResults" [
  test "merging results updates LastResults map" {
    let tid = mkTestId "t1" "x"
    let results = [| mkResult tid (TestResult.Passed (ts 5.0)) |]
    let state = LiveTesting.mergeResults LiveTestState.empty results
    state.LastResults
    |> Map.containsKey tid
    |> Expect.isTrue "should contain result"
  }

  test "merging results sets IsRunning to false" {
    let tid = mkTestId "t1" "x"
    let running = { LiveTestState.empty with IsRunning = true }
    let results = [| mkResult tid (TestResult.Passed (ts 5.0)) |]
    LiveTesting.mergeResults running results
    |> fun s -> s.IsRunning
    |> Expect.isFalse "should stop running"
  }

  test "merging results updates History to PreviousRun" {
    let tid = mkTestId "t1" "x"
    let results = [| mkResult tid (TestResult.Passed (ts 10.0)) |]
    let state = LiveTesting.mergeResults LiveTestState.empty results
    match state.History with
    | RunHistory.PreviousRun _ -> ()
    | RunHistory.NeverRun -> failtest "should be PreviousRun"
  }

  test "newer result overwrites older for same TestId" {
    let tid = mkTestId "t1" "x"
    let old = [| mkResult tid (TestResult.Passed (ts 5.0)) |]
    let state1 = LiveTesting.mergeResults LiveTestState.empty old
    let newer = [| mkResult tid (TestResult.Failed (TestFailure.AssertionFailed "oops", ts 3.0)) |]
    let state2 = LiveTesting.mergeResults state1 newer
    match state2.LastResults |> Map.find tid |> fun r -> r.Result with
    | TestResult.Failed _ -> ()
    | _ -> failtest "should be Failed after overwrite"
  }

  test "merging empty results preserves state" {
    let tid = mkTestId "t1" "x"
    let withResult =
      { LiveTestState.empty with
          LastResults = Map.ofList [ tid, mkResult tid (TestResult.Passed (ts 1.0)) ] }
    let after = LiveTesting.mergeResults withResult Array.empty
    after.LastResults
    |> Map.count
    |> Expect.equal "preserve existing" 1
  }
]

// --- computeStatusEntries Tests (RED — stub returns empty) ---

[<Tests>]
let computeStatusEntriesTests = testList "computeStatusEntries" [
  test "returns one entry per discovered test" {
    let tests = [|
      mkTestCase "t1" "x" TestCategory.Unit
      mkTestCase "t2" "x" TestCategory.Unit
    |]
    let state = { LiveTestState.empty with DiscoveredTests = tests }
    LiveTesting.computeStatusEntries state
    |> Expect.hasLength "one per test" 2
  }

  test "test with no result shows Detected" {
    let tc = mkTestCase "t1" "x" TestCategory.Unit
    let state = { LiveTestState.empty with DiscoveredTests = [| tc |] }
    let entries = LiveTesting.computeStatusEntries state
    match entries.[0].Status with
    | TestRunStatus.Detected -> ()
    | other -> failtestf "expected Detected, got %A" other
  }

  test "passed test shows Passed status" {
    let tc = mkTestCase "t1" "x" TestCategory.Unit
    let result = mkResult tc.Id (TestResult.Passed (ts 5.0))
    let state =
      { LiveTestState.empty with
          DiscoveredTests = [| tc |]
          LastResults = Map.ofList [ tc.Id, result ] }
    let entries = LiveTesting.computeStatusEntries state
    match entries.[0].Status with
    | TestRunStatus.Passed _ -> ()
    | other -> failtestf "expected Passed, got %A" other
  }

  test "failed test shows Failed status" {
    let tc = mkTestCase "t1" "x" TestCategory.Unit
    let failure = TestFailure.AssertionFailed "expected 1 got 2"
    let result = mkResult tc.Id (TestResult.Failed (failure, ts 3.0))
    let state =
      { LiveTestState.empty with
          DiscoveredTests = [| tc |]
          LastResults = Map.ofList [ tc.Id, result ] }
    let entries = LiveTesting.computeStatusEntries state
    match entries.[0].Status with
    | TestRunStatus.Failed _ -> ()
    | other -> failtestf "expected Failed, got %A" other
  }

  test "disabled policy shows PolicyDisabled" {
    let tc = mkTestCase "t1" "x" TestCategory.Unit
    let state =
      { LiveTestState.empty with
          DiscoveredTests = [| tc |]
          RunPolicies = Map.ofList [ TestCategory.Unit, RunPolicy.Disabled ] }
    let entries = LiveTesting.computeStatusEntries state
    match entries.[0].Status with
    | TestRunStatus.PolicyDisabled -> ()
    | other -> failtestf "expected PolicyDisabled, got %A" other
  }

  test "affected test shows Queued" {
    let tc = mkTestCase "t1" "x" TestCategory.Unit
    let state =
      { LiveTestState.empty with
          DiscoveredTests = [| tc |]
          AffectedTests = Set.singleton tc.Id }
    let entries = LiveTesting.computeStatusEntries state
    match entries.[0].Status with
    | TestRunStatus.Queued -> ()
    | other -> failtestf "expected Queued, got %A" other
  }

  test "entry preserves test metadata" {
    let tc = mkTestCase "MyModule.Tests.add" "xunit" TestCategory.Unit
    let state = { LiveTestState.empty with DiscoveredTests = [| tc |] }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].FullName
    |> Expect.equal "preserves fullname" "MyModule.Tests.add"
  }

  test "empty discovered tests returns empty entries" {
    LiveTesting.computeStatusEntries LiveTestState.empty
    |> Expect.hasLength "empty in, empty out" 0
  }
]

// --- TestDependencyGraph Tests (RED — stub returns empty) ---

[<Tests>]
let dependencyGraphTests = testList "TestDependencyGraph" [
  test "findAffected returns tests that reference changed symbol" {
    let t1 = mkTestId "test1" "x"
    let t2 = mkTestId "test2" "x"
    let graph = {
      SymbolToTests = Map.ofList [ "MyModule.add", [| t1 |]; "MyModule.sub", [| t2 |] ]
      TransitiveCoverage = Map.empty; SourceVersion = 1
    }
    TestDependencyGraph.findAffected ["MyModule.add"] graph
    |> Expect.hasLength "one affected test" 1
  }

  test "findAffected returns union of tests for multiple symbols" {
    let t1 = mkTestId "test1" "x"
    let t2 = mkTestId "test2" "x"
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| t1 |]; "B.g", [| t2 |] ]
      TransitiveCoverage = Map.empty; SourceVersion = 1
    }
    TestDependencyGraph.findAffected ["A.f"; "B.g"] graph
    |> Expect.hasLength "both tests" 2
  }

  test "findAffected deduplicates when test references multiple changed symbols" {
    let t1 = mkTestId "test1" "x"
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| t1 |]; "A.g", [| t1 |] ]
      TransitiveCoverage = Map.empty; SourceVersion = 1
    }
    TestDependencyGraph.findAffected ["A.f"; "A.g"] graph
    |> Expect.hasLength "deduplicated to one" 1
  }

  test "findAffected returns empty for unknown symbols" {
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| mkTestId "t" "x" |] ]
      TransitiveCoverage = Map.empty; SourceVersion = 1
    }
    TestDependencyGraph.findAffected ["Unknown.sym"] graph
    |> Expect.hasLength "no matches" 0
  }

  test "empty graph returns empty for any symbol" {
    TestDependencyGraph.findAffected ["anything"] TestDependencyGraph.empty
    |> Expect.hasLength "empty graph" 0
  }
]

// --- CoverageProjection Tests (RED — stub returns Pending) ---

[<Tests>]
let coverageProjectionTests = testList "CoverageProjection" [
  test "symbol with no tests is NotCovered" {
    let result = CoverageProjection.symbolCoverage TestDependencyGraph.empty Map.empty "MyModule.add"
    match result with
    | CoverageStatus.NotCovered -> ()
    | other -> failtestf "expected NotCovered, got %A" other
  }

  test "symbol with empty test array is NotCovered" {
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", Array.empty ]
    }
    let result = CoverageProjection.symbolCoverage graph Map.empty "MyModule.add"
    match result with
    | CoverageStatus.NotCovered -> ()
    | other -> failtestf "expected NotCovered, got %A" other
  }

  test "symbol reachable by passing tests is Covered with allPassing=true" {
    let tid = mkTestId "t1" "x"
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ]
    }
    let results = Map.ofList [ tid, mkResult tid (TestResult.Passed (ts 5.0)) ]
    match CoverageProjection.symbolCoverage graph results "MyModule.add" with
    | CoverageStatus.Covered (1, true) -> ()
    | other -> failtestf "expected Covered(1,true), got %A" other
  }

  test "symbol reachable by failing test is Covered with allPassing=false" {
    let tid = mkTestId "t1" "x"
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ]
    }
    let failure = TestFailure.AssertionFailed "nope"
    let results = Map.ofList [ tid, mkResult tid (TestResult.Failed (failure, ts 3.0)) ]
    match CoverageProjection.symbolCoverage graph results "MyModule.add" with
    | CoverageStatus.Covered (1, false) -> ()
    | other -> failtestf "expected Covered(1,false), got %A" other
  }

  test "symbol reachable by mixed results has allPassing=false" {
    let t1 = mkTestId "t1" "x"
    let t2 = mkTestId "t2" "x"
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| t1; t2 |] ]
    }
    let failure = TestFailure.AssertionFailed "nope"
    let results = Map.ofList [
      t1, mkResult t1 (TestResult.Passed (ts 5.0))
      t2, mkResult t2 (TestResult.Failed (failure, ts 3.0))
    ]
    match CoverageProjection.symbolCoverage graph results "MyModule.add" with
    | CoverageStatus.Covered (2, false) -> ()
    | other -> failtestf "expected Covered(2,false), got %A" other
  }

  test "symbol reachable by tests with no results yet has allPassing=false" {
    let tid = mkTestId "t1" "x"
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ]
    }
    match CoverageProjection.symbolCoverage graph Map.empty "MyModule.add" with
    | CoverageStatus.Covered (1, false) -> ()
    | other -> failtestf "expected Covered(1,false), got %A" other
  }

  test "computeAll produces coverage for every symbol in graph" {
    let t1 = mkTestId "t1" "x"
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "A.f", [| t1 |]; "B.g", Array.empty ]
    }
    let result = CoverageProjection.computeAll graph Map.empty
    result |> Map.count
    |> Expect.equal "covers all symbols" 2
  }
]

// --- CoverageComputation Tests (RED — stub returns NotCovered) ---

[<Tests>]
let coverageComputationTests = testList "CoverageComputation" [
  test "line with all branches hit is FullyCovered" {
    let state = {
      Slots = [|
        { File = "a.fs"; Line = 10; Column = 0; BranchId = 0 }
        { File = "a.fs"; Line = 10; Column = 5; BranchId = 1 }
        { File = "a.fs"; Line = 10; Column = 10; BranchId = 2 }
      |]
      Hits = [| true; true; true |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 10 with
    | LineCoverage.FullyCovered -> ()
    | other -> failtestf "expected FullyCovered, got %A" other
  }

  test "line with some branches hit is PartiallyCovered" {
    let state = {
      Slots = [|
        { File = "a.fs"; Line = 10; Column = 0; BranchId = 0 }
        { File = "a.fs"; Line = 10; Column = 5; BranchId = 1 }
        { File = "a.fs"; Line = 10; Column = 10; BranchId = 2 }
      |]
      Hits = [| true; false; true |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 10 with
    | LineCoverage.PartiallyCovered (2, 3) -> ()
    | other -> failtestf "expected PartiallyCovered(2,3), got %A" other
  }

  test "line with no branches hit is NotCovered" {
    let state = {
      Slots = [|
        { File = "a.fs"; Line = 10; Column = 0; BranchId = 0 }
        { File = "a.fs"; Line = 10; Column = 5; BranchId = 1 }
      |]
      Hits = [| false; false |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 10 with
    | LineCoverage.NotCovered -> ()
    | other -> failtestf "expected NotCovered, got %A" other
  }

  test "line with no slots is NotCovered" {
    let state = {
      Slots = [|
        { File = "a.fs"; Line = 20; Column = 0; BranchId = 0 }
      |]
      Hits = [| true |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 10 with
    | LineCoverage.NotCovered -> ()
    | other -> failtestf "expected NotCovered for untracked line, got %A" other
  }

  test "single slot line fully covered returns FullyCovered" {
    let state = {
      Slots = [| { File = "a.fs"; Line = 5; Column = 0; BranchId = 0 } |]
      Hits = [| true |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 5 with
    | LineCoverage.FullyCovered -> ()
    | other -> failtestf "expected FullyCovered, got %A" other
  }

  test "empty state returns NotCovered" {
    let state = { Slots = Array.empty; Hits = Array.empty }
    match CoverageComputation.computeLineCoverage state "a.fs" 1 with
    | LineCoverage.NotCovered -> ()
    | other -> failtestf "expected NotCovered, got %A" other
  }
]

// --- PipelineTiming Tests (RED — stub returns 0.0) ---

[<Tests>]
let pipelineTimingTests = testList "PipelineTiming" [
  test "treeSitterMs extracts from TreeSitterOnly" {
    let t = {
      Depth = PipelineDepth.TreeSitterOnly (ts 0.8)
      TotalTests = 10; AffectedTests = 3
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    PipelineTiming.treeSitterMs t
    |> Expect.floatClose "extracts 0.8ms" Accuracy.medium 0.8
  }

  test "treeSitterMs extracts from ThroughFcs" {
    let t = {
      Depth = PipelineDepth.ThroughFcs (ts 1.2, ts 142.0)
      TotalTests = 10; AffectedTests = 3
      Trigger = RunTrigger.FileSave; Timestamp = DateTimeOffset.UtcNow
    }
    PipelineTiming.treeSitterMs t
    |> Expect.floatClose "extracts 1.2ms" Accuracy.medium 1.2
  }

  test "treeSitterMs extracts from ThroughExecution" {
    let t = {
      Depth = PipelineDepth.ThroughExecution (ts 0.5, ts 100.0, ts 87.0)
      TotalTests = 47; AffectedTests = 12
      Trigger = RunTrigger.ExplicitRun; Timestamp = DateTimeOffset.UtcNow
    }
    PipelineTiming.treeSitterMs t
    |> Expect.floatClose "extracts 0.5ms" Accuracy.medium 0.5
  }
]

// --- TestProviderDescriptions Tests (RED — stub returns empty) ---

[<Tests>]
let providerDetectionTests = testList "TestProviderDescriptions" [
  test "detects xunit from referenced assemblies" {
    let asm = mkAssemblyInfo "MyTests" ["xunit.core"; "mscorlib"]
    let providers = TestProviderDescriptions.detectProviders [asm]
    providers
    |> List.exists (fun p ->
      match p with
      | ProviderDescription.AttributeBased d -> d.Name = "xunit"
      | _ -> false)
    |> Expect.isTrue "should detect xunit"
  }

  test "detects multiple frameworks from single assembly" {
    let asm = mkAssemblyInfo "MultiTests" ["xunit.core"; "nunit.framework"]
    let providers = TestProviderDescriptions.detectProviders [asm]
    providers |> List.length >= 2
    |> Expect.isTrue "at least 2 providers"
  }

  test "detects nothing when no markers match" {
    let asm = mkAssemblyInfo "NoTests" ["mscorlib"; "FSharp.Core"]
    TestProviderDescriptions.detectProviders [asm]
    |> Expect.hasLength "no providers" 0
  }

  test "detects expecto as Custom provider" {
    let asm = mkAssemblyInfo "MyTests" ["Expecto"; "FSharp.Core"]
    let providers = TestProviderDescriptions.detectProviders [asm]
    providers
    |> List.exists (fun p ->
      match p with
      | ProviderDescription.Custom d -> d.Name = "expecto"
      | _ -> false)
    |> Expect.isTrue "should detect expecto as custom"
  }

  test "empty assemblies returns empty" {
    TestProviderDescriptions.detectProviders []
    |> Expect.hasLength "no assemblies, no providers" 0
  }

  test "builtIn has 5 providers" {
    TestProviderDescriptions.builtInDescriptions
    |> List.length
    |> Expect.equal "5 built-in" 5
  }
]

// --- RunPolicyDefaults Tests (GREEN — pure data) ---

[<Tests>]
let runPolicyDefaultsTests = testList "RunPolicyDefaults" [
  test "unit tests default to OnEveryChange" {
    RunPolicyDefaults.defaults
    |> Map.find TestCategory.Unit
    |> Expect.equal "unit -> OnEveryChange" RunPolicy.OnEveryChange
  }

  test "integration tests default to OnDemand" {
    RunPolicyDefaults.defaults
    |> Map.find TestCategory.Integration
    |> Expect.equal "integration -> OnDemand" RunPolicy.OnDemand
  }

  test "browser tests default to OnDemand" {
    RunPolicyDefaults.defaults
    |> Map.find TestCategory.Browser
    |> Expect.equal "browser -> OnDemand" RunPolicy.OnDemand
  }

  test "benchmark tests default to OnDemand" {
    RunPolicyDefaults.defaults
    |> Map.find TestCategory.Benchmark
    |> Expect.equal "benchmark -> OnDemand" RunPolicy.OnDemand
  }

  test "architecture tests default to OnSaveOnly" {
    RunPolicyDefaults.defaults
    |> Map.find TestCategory.Architecture
    |> Expect.equal "arch -> OnSaveOnly" RunPolicy.OnSaveOnly
  }

  test "property tests default to OnEveryChange" {
    RunPolicyDefaults.defaults
    |> Map.find TestCategory.Property
    |> Expect.equal "property -> OnEveryChange" RunPolicy.OnEveryChange
  }

  test "all 6 categories have defaults" {
    RunPolicyDefaults.defaults
    |> Map.count
    |> Expect.equal "6 categories" 6
  }
]

// --- LiveTestState.empty Tests (GREEN — pure data) ---

[<Tests>]
let liveTestStateEmptyTests = testList "LiveTestState.empty" [
  test "starts with empty arrays" {
    LiveTestState.empty.SourceLocations |> Expect.hasLength "no locations" 0
    LiveTestState.empty.DiscoveredTests |> Expect.hasLength "no tests" 0
    LiveTestState.empty.StatusEntries |> Expect.hasLength "no entries" 0
    LiveTestState.empty.CoverageAnnotations |> Expect.hasLength "no coverage" 0
  }

  test "starts enabled" {
    LiveTestState.empty.Enabled
    |> Expect.isTrue "enabled by default"
  }

  test "starts with coverage shown" {
    LiveTestState.empty.ShowCoverage
    |> Expect.isTrue "coverage shown by default"
  }

  test "starts not running" {
    LiveTestState.empty.IsRunning
    |> Expect.isFalse "not running initially"
  }

  test "starts with NeverRun history" {
    match LiveTestState.empty.History with
    | RunHistory.NeverRun -> ()
    | _ -> failtest "should be NeverRun"
  }

  test "starts with default run policies" {
    LiveTestState.empty.RunPolicies
    |> Map.count
    |> Expect.equal "6 default policies" 6
  }

  test "starts with no providers" {
    LiveTestState.empty.DetectedProviders
    |> Expect.hasLength "no providers" 0
  }
]

// --- Property-based Tests (RED — stubs fail properties) ---

// Alias to avoid FsCheck.TestResult collision
type LTTestResult = TestResult

[<Tests>]
let propertyTests = testList "Property-based" [
  testProperty "TestId.create is deterministic" (fun (name: string) (fw: string) ->
    let name = if isNull name then "" else name
    let fw = if isNull fw then "" else fw
    let id1 = TestId.create name fw
    let id2 = TestId.create name fw
    TestId.value id1 = TestId.value id2
  )

  testProperty "TestId.value always returns 16 chars" (fun (name: string) (fw: string) ->
    let name = if isNull name then "" else name
    let fw = if isNull fw then "" else fw
    let id = TestId.create name fw
    (TestId.value id).Length = 16
  )

  testProperty "filterByPolicy with Disabled always returns empty" (fun (cat: int) ->
    let category =
      match abs cat % 6 with
      | 0 -> TestCategory.Unit | 1 -> TestCategory.Integration
      | 2 -> TestCategory.Browser | 3 -> TestCategory.Benchmark
      | 4 -> TestCategory.Architecture | _ -> TestCategory.Property
    let tc = mkTestCase "Prop.test" "x" category
    let policies = Map.ofList [ category, RunPolicy.Disabled ]
    let result = LiveTesting.filterByPolicy policies RunTrigger.ExplicitRun [| tc |]
    result.Length = 0
  )

  testProperty "filterByPolicy ExplicitRun includes all non-disabled" (fun (cat: int) ->
    let category =
      match abs cat % 6 with
      | 0 -> TestCategory.Unit | 1 -> TestCategory.Integration
      | 2 -> TestCategory.Browser | 3 -> TestCategory.Benchmark
      | 4 -> TestCategory.Architecture | _ -> TestCategory.Property
    let tc = mkTestCase "Prop.test" "x" category
    let policies = Map.ofList [ category, RunPolicy.OnEveryChange ]
    let result = LiveTesting.filterByPolicy policies RunTrigger.ExplicitRun [| tc |]
    result.Length = 1
  )

  testProperty "mergeResults never loses existing results" (fun (n: int) ->
    let count = (abs n % 10) + 1
    let results =
      [| for i in 1..count do
           let tid = mkTestId (sprintf "t%d" i) "x"
           { TestId = tid; TestName = TestId.value tid
             Result = LTTestResult.Passed (ts 1.0)
             Timestamp = DateTimeOffset.UtcNow } |]
    let state = LiveTesting.mergeResults LiveTestState.empty results
    state.LastResults |> Map.count >= count
  )

  testProperty "computeStatusEntries returns same count as DiscoveredTests" (fun (n: int) ->
    let count = abs n % 20
    let tests = [| for i in 1..count -> mkTestCase (sprintf "t%d" i) "x" TestCategory.Unit |]
    let state = { LiveTestState.empty with DiscoveredTests = tests }
    let entries = LiveTesting.computeStatusEntries state
    entries.Length = count
  )

  testProperty "findAffected returns subset of all test ids in graph" (fun (sym: string) ->
    let sym = if isNull sym then "x" else sym
    let t1 = mkTestId "t1" "x"
    let t2 = mkTestId "t2" "x"
    let graph = {
      SymbolToTests = Map.ofList [ "a", [| t1 |]; "b", [| t2 |] ]
      TransitiveCoverage = Map.empty; SourceVersion = 1
    }
    let affected = TestDependencyGraph.findAffected [sym] graph
    let allIds = Set.ofList [ t1; t2 ]
    affected |> Array.forall (fun id -> Set.contains id allIds)
  )
]

// ── Elm Integration Tests ──

[<Tests>]
let elmIntegrationTests = testList "LiveTesting Elm Integration" [

  testList "Model structure" [
    test "SageFsModel has LiveTesting field" {
      typeof<SageFsModel>.GetProperties()
      |> Array.exists (fun p -> p.Name = "LiveTesting")
      |> Expect.isTrue "SageFsModel should have LiveTesting field"
    }

    test "SageFsModel.initial has empty LiveTestState" {
      let model = SageFsModel.initial
      model.LiveTesting.DiscoveredTests
      |> Expect.equal "no discovered tests" Array.empty
      model.LiveTesting.IsRunning
      |> Expect.isFalse "not running"
      model.LiveTesting.Enabled
      |> Expect.isTrue "enabled"
    }
  ]

  testList "Event cases" [
    let hasCase name =
      Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<SageFsEvent>)
      |> Array.exists (fun uc -> uc.Name = name)
      |> Expect.isTrue (sprintf "SageFsEvent should have %s case" name)
    test "TestsDiscovered" { hasCase "TestsDiscovered" }
    test "TestResultsBatch" { hasCase "TestResultsBatch" }
    test "LiveTestingToggled" { hasCase "LiveTestingToggled" }
    test "AffectedTestsComputed" { hasCase "AffectedTestsComputed" }
    test "CoverageUpdated" { hasCase "CoverageUpdated" }
    test "RunPolicyChanged" { hasCase "RunPolicyChanged" }
    test "ProvidersDetected" { hasCase "ProvidersDetected" }
    test "TestRunStarted" { hasCase "TestRunStarted" }
  ]

  testList "Update behavior" [
    test "TestsDiscovered updates DiscoveredTests" {
      let tc : TestCase =
        { Id = mkTestId "myTest" "expecto"; DisplayName = "myTest"
          FullName = "MyModule.myTest"; Framework = "expecto"
          Origin = TestOrigin.ReflectionOnly; Labels = []
          Category = TestCategory.Unit }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestsDiscovered [| tc |]))
          SageFsModel.initial
      model'.LiveTesting.DiscoveredTests.Length
      |> Expect.equal "should have 1 test" 1
    }
    test "TestRunStarted sets IsRunning and AffectedTests" {
      let tid = mkTestId "t1" "x"
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestRunStarted [| tid |]))
          SageFsModel.initial
      model'.LiveTesting.IsRunning
      |> Expect.isTrue "should be running"
      Set.contains tid model'.LiveTesting.AffectedTests
      |> Expect.isTrue "should contain test id"
    }
    test "TestResultsBatch merges results and clears IsRunning" {
      let m =
        { SageFsModel.initial with
            LiveTesting =
              { SageFsModel.initial.LiveTesting with IsRunning = true } }
      let tid = mkTestId "t1" "x"
      let r : TestRunResult =
        { TestId = tid; TestName = "t1"
          Result = LTTestResult.Passed (ts 5.0)
          Timestamp = DateTimeOffset.UtcNow }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestResultsBatch [| r |])) m
      model'.LiveTesting.IsRunning
      |> Expect.isFalse "should not be running"
      Map.containsKey tid model'.LiveTesting.LastResults
      |> Expect.isTrue "should have result"
    }
    test "LiveTestingToggled updates Enabled" {
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.LiveTestingToggled false))
          SageFsModel.initial
      model'.LiveTesting.Enabled
      |> Expect.isFalse "should be disabled"
    }
    test "AffectedTestsComputed sets AffectedTests" {
      let t1 = mkTestId "t1" "x"
      let t2 = mkTestId "t2" "x"
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.AffectedTestsComputed [| t1; t2 |]))
          SageFsModel.initial
      Set.count model'.LiveTesting.AffectedTests
      |> Expect.equal "should have 2 affected" 2
    }
    test "RunPolicyChanged updates policy" {
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.RunPolicyChanged (TestCategory.Integration, RunPolicy.OnSaveOnly)))
          SageFsModel.initial
      Map.find TestCategory.Integration model'.LiveTesting.RunPolicies
      |> Expect.equal "should be OnSaveOnly" RunPolicy.OnSaveOnly
    }
    test "ProvidersDetected updates providers" {
      let p = ProviderDescription.Custom { Name = "expecto"; AssemblyMarker = "Expecto" }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.ProvidersDetected [p]))
          SageFsModel.initial
      model'.LiveTesting.DetectedProviders.Length
      |> Expect.equal "should have 1 provider" 1
    }
    test "CoverageUpdated produces annotations" {
      let cs : CoverageState =
        { Slots =
            [| { SequencePoint.File = "a.fs"; Line = 10; Column = 0; BranchId = 0 }
               { SequencePoint.File = "a.fs"; Line = 10; Column = 5; BranchId = 1 } |]
          Hits = [| true; false |] }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.CoverageUpdated cs))
          SageFsModel.initial
      model'.LiveTesting.CoverageAnnotations.Length
      |> Expect.equal "should have 2 annotations" 2
    }
    test "no effects for live testing events" {
      let _, effects =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.LiveTestingToggled true))
          SageFsModel.initial
      effects |> Expect.isEmpty "should produce no effects"
    }
  ]
]

// ── Instrumentation Tests ──

[<Tests>]
let instrumentationTests = testList "LiveTestingInstrumentation" [
  test "ActivitySource name is SageFs.LiveTesting" {
    LiveTestingInstrumentation.activitySource.Name
    |> Expect.equal "activity source name" "SageFs.LiveTesting"
  }

  test "Meter name is SageFs.LiveTesting" {
    LiveTestingInstrumentation.meter.Name
    |> Expect.equal "meter name" "SageFs.LiveTesting"
  }

  test "traced returns same value as wrapped function" {
    LiveTestingInstrumentation.traced "test.op" [] (fun () -> 42)
    |> Expect.equal "should return 42" 42
  }

  test "traced preserves exceptions" {
    Expect.throwsT<System.InvalidOperationException>
      "should rethrow"
      (fun () ->
        LiveTestingInstrumentation.traced
          "test.fail" [] (fun () ->
            raise (System.InvalidOperationException "boom")) |> ignore)
  }

  test "traced works with string return" {
    LiveTestingInstrumentation.traced
      "test.string" [("key", box "val")] (fun () -> "hello")
    |> Expect.equal "should return hello" "hello"
  }

  test "treeSitterHistogram is created" {
    LiveTestingInstrumentation.treeSitterHistogram
    |> Expect.isNotNull "should not be null"
  }

  test "fcsHistogram is created" {
    LiveTestingInstrumentation.fcsHistogram
    |> Expect.isNotNull "should not be null"
  }

  test "executionHistogram is created" {
    LiveTestingInstrumentation.executionHistogram
    |> Expect.isNotNull "should not be null"
  }
]

[<Tests>]
let transitiveClosureTests = testList "TransitiveCoverage" [
  test "single symbol with direct test stays in result" {
    let t1 = TestId.create "test1" "x"
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.empty<string, string array>
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "A" result
    |> Expect.equal "A should have t1" [| t1 |]
  }

  test "callee of tested symbol is transitively covered" {
    let t1 = TestId.create "test1" "x"
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "B" result
    |> Expect.equal "B transitively covered by T1" [| t1 |]
  }

  test "two-hop transitive coverage" {
    let t1 = TestId.create "test1" "x"
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |]; "B", [| "C" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "C" result
    |> Expect.equal "C transitively covered by T1" [| t1 |]
  }

  test "multiple tests merge at shared callee" {
    let t1 = TestId.create "test1" "x"
    let t2 = TestId.create "test2" "x"
    let direct = Map.ofList [ "A", [| t1 |]; "B", [| t2 |] ]
    let callGraph = Map.ofList [ "A", [| "C" |]; "B", [| "C" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    let coveringC = Map.find "C" result |> Array.sort
    let expected = [| t1; t2 |] |> Array.sort
    coveringC |> Expect.equal "C covered by both" expected
  }

  test "cycle in call graph terminates" {
    let t1 = TestId.create "test1" "x"
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |]; "B", [| "A" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "B" result
    |> Expect.equal "B covered despite cycle" [| t1 |]
  }

  test "empty call graph returns direct mapping" {
    let t1 = TestId.create "test1" "x"
    let direct = Map.ofList [ "A", [| t1 |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage Map.empty direct
    result |> Expect.equal "same as direct" direct
  }

  test "callee symbol with no direct tests gets attributed" {
    let t1 = TestId.create "test1" "x"
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.containsKey "B" result
    |> Expect.isTrue "B should appear in result"
  }

  test "diamond call graph merges correctly" {
    // A calls B and C, both B and C call D
    let t1 = TestId.create "test1" "x"
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B"; "C" |]; "B", [| "D" |]; "C", [| "D" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "D" result
    |> Expect.equal "D covered by T1 (once, not duplicated)" [| t1 |]
  }

  test "symbols only in call graph but not tested are included" {
    let t1 = TestId.create "test1" "x"
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |]; "B", [| "C" |]; "C", [| "D" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    [ "A"; "B"; "C"; "D" ]
    |> List.forall (fun s -> Map.containsKey s result)
    |> Expect.isTrue "all reachable symbols should be in result"
  }

  test "empty direct map produces empty result" {
    let callGraph = Map.ofList [ "A", [| "B" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph Map.empty
    result |> Expect.equal "empty result" Map.empty
  }
]

// --- Phase 4: As-You-Type Pipeline Tests ---

let debounceControllerTests = testList "DebounceController" [
  test "acquire cancels previous token for same stage" {
    let dc = new DebounceController()
    let t1 = dc.Acquire(PipelineStage.TreeSitter)
    t1.IsCancellationRequested
    |> Expect.isFalse "first token not cancelled"
    let _t2 = dc.Acquire(PipelineStage.TreeSitter)
    t1.IsCancellationRequested
    |> Expect.isTrue "first token cancelled after re-acquire"
    (dc :> IDisposable).Dispose()
  }

  test "different stages are independent" {
    let dc = new DebounceController()
    let ts = dc.Acquire(PipelineStage.TreeSitter)
    let fcs = dc.Acquire(PipelineStage.FcsTypeCheck)
    let _ts2 = dc.Acquire(PipelineStage.TreeSitter)
    ts.IsCancellationRequested
    |> Expect.isTrue "tree-sitter cancelled"
    fcs.IsCancellationRequested
    |> Expect.isFalse "fcs NOT cancelled"
    (dc :> IDisposable).Dispose()
  }

  test "cancelAll cancels all stages" {
    let dc = new DebounceController()
    let ts = dc.Acquire(PipelineStage.TreeSitter)
    let fcs = dc.Acquire(PipelineStage.FcsTypeCheck)
    dc.CancelAll()
    ts.IsCancellationRequested
    |> Expect.isTrue "tree-sitter cancelled"
    fcs.IsCancellationRequested
    |> Expect.isTrue "fcs cancelled"
  }
]

let debouncedExecutionTests = testList "DebouncedExecution" [
  testAsync "cancelled token returns None" {
    let cts = new Threading.CancellationTokenSource()
    cts.Cancel()
    let! result = DebouncedExecution.execute 1 cts.Token (fun () -> 42)
    result |> Expect.isNone "cancelled should return None"
  }

  testAsync "valid token returns Some" {
    let cts = new Threading.CancellationTokenSource()
    let! result = DebouncedExecution.execute 1 cts.Token (fun () -> 42)
    result |> Expect.equal "should return Some 42" (Some 42)
  }

  testAsync "rapid keystrokes: only last executes" {
    let dc = new DebounceController()
    let mutable lastResult = None
    let tokens = [| for i in 1..5 -> dc.Acquire(PipelineStage.TreeSitter), i |]
    for (token, i) in tokens do
      if not token.IsCancellationRequested then
        let! r = DebouncedExecution.execute 1 token (fun () -> i)
        match r with
        | Some v -> lastResult <- Some v
        | None -> ()
    lastResult
    |> Expect.equal "only last keystroke should execute" (Some 5)
    (dc :> IDisposable).Dispose()
  }
]

let timedExecutionTests = testList "timed" [
  test "measures duration" {
    let (result, elapsed) = DebouncedExecution.timed (fun () ->
      Threading.Thread.Sleep 50
      "done")
    result |> Expect.equal "result" "done"
    Expect.isTrue "elapsed > 40ms" (elapsed > TimeSpan.FromMilliseconds 40.0)
  }
]

let affectedTestPipelineTests = testList "affected-test pipeline" [
  test "dep graph lookup finds affected tests" {
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [
          "MyModule.add", [| TestId.create "add-test" "xunit" |]
          "MyModule.validate", [| TestId.create "validate-test" "xunit" |]
        ]
    }
    let affected = TestDependencyGraph.findAffected ["MyModule.add"] graph
    affected.Length |> Expect.equal "one affected test" 1
    affected.[0] |> Expect.equal "correct test" (TestId.create "add-test" "xunit")
  }
]
