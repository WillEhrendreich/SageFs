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

let pipelineTimingExtendedTests = testList "PipelineTiming extended" [
  test "fcsMs returns 0 for tree-sitter only" {
    let t = {
      Depth = PipelineDepth.TreeSitterOnly (TimeSpan.FromMilliseconds 1.0)
      TotalTests = 0; AffectedTests = 0
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    PipelineTiming.fcsMs t
    |> Expect.equal "no FCS for tree-sitter only" 0.0
  }

  test "fcsMs returns value for ThroughFcs" {
    let t = {
      Depth = PipelineDepth.ThroughFcs (TimeSpan.FromMilliseconds 1.0, TimeSpan.FromMilliseconds 142.0)
      TotalTests = 10; AffectedTests = 5
      Trigger = RunTrigger.FileSave; Timestamp = DateTimeOffset.UtcNow
    }
    PipelineTiming.fcsMs t
    |> Expect.equal "fcs ms" 142.0
  }

  test "totalMs sums all stages" {
    let t = {
      Depth = PipelineDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 1.0, TimeSpan.FromMilliseconds 100.0, TimeSpan.FromMilliseconds 50.0)
      TotalTests = 10; AffectedTests = 3
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    PipelineTiming.totalMs t
    |> Expect.equal "total" 151.0
  }

  test "toStatusBar tree-sitter only" {
    let t = {
      Depth = PipelineDepth.TreeSitterOnly (TimeSpan.FromMilliseconds 0.8)
      TotalTests = 0; AffectedTests = 0
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    PipelineTiming.toStatusBar t
    |> Expect.equal "format" "TS:0.8ms"
  }

  test "toStatusBar full pipeline" {
    let t = {
      Depth = PipelineDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 0.8, TimeSpan.FromMilliseconds 142.0, TimeSpan.FromMilliseconds 87.0)
      TotalTests = 47; AffectedTests = 12
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    PipelineTiming.toStatusBar t
    |> Expect.equal "format" "TS:0.8ms | FCS:142ms | Run:87ms (12)"
  }
]

let tracedTests = testList "Traced" [
  test "execute returns result and timing" {
    let (result, timing) = Traced.execute PipelineStage.TreeSitter (fun () -> 42)
    result |> Expect.equal "result" 42
    timing.Stage |> Expect.equal "stage" PipelineStage.TreeSitter
    Expect.isTrue "duration >= 0" (timing.Duration >= TimeSpan.Zero)
  }

  test "execute captures realistic duration" {
    let (_, timing) = Traced.execute PipelineStage.FcsTypeCheck (fun () ->
      Threading.Thread.Sleep 30
      "done")
    Expect.isTrue "duration > 25ms" (timing.Duration > TimeSpan.FromMilliseconds 25.0)
  }

  testAsync "executeAsync returns result and timing" {
    let! (result, timing) = Traced.executeAsync PipelineStage.TestExecution (fun () ->
      async {
        do! Async.Sleep 20
        return "async-done"
      })
    result |> Expect.equal "result" "async-done"
    timing.Stage |> Expect.equal "stage" PipelineStage.TestExecution
    Expect.isTrue "duration > 15ms" (timing.Duration > TimeSpan.FromMilliseconds 15.0)
  }
]

let pipelineResultTests = testList "PipelineResult" [
  test "map transforms completed result" {
    let pr = PipelineResult.Completed (42, [])
    let mapped = PipelineResult.map (fun x -> x * 2) pr
    match mapped with
    | PipelineResult.Completed (r, _) -> r |> Expect.equal "doubled" 84
    | PipelineResult.Cancelled -> failtest "should not be cancelled"
  }

  test "map preserves cancelled" {
    let pr : PipelineResult<int> = PipelineResult.Cancelled
    let mapped = PipelineResult.map (fun x -> x * 2) pr
    match mapped with
    | PipelineResult.Cancelled -> ()
    | _ -> failtest "should be cancelled"
  }

  test "toPipelineTiming builds TreeSitterOnly" {
    let timings = [
      { Stage = PipelineStage.TreeSitter; Duration = TimeSpan.FromMilliseconds 1.5; Timestamp = DateTimeOffset.UtcNow }
    ]
    let pt = PipelineResult.toPipelineTiming RunTrigger.Keystroke 10 3 timings
    match pt.Depth with
    | PipelineDepth.TreeSitterOnly ts ->
      ts.TotalMilliseconds |> Expect.equal "ts" 1.5
    | _ -> failtest "expected TreeSitterOnly"
    pt.TotalTests |> Expect.equal "total" 10
    pt.AffectedTests |> Expect.equal "affected" 3
  }

  test "toPipelineTiming builds ThroughExecution" {
    let timings = [
      { Stage = PipelineStage.TreeSitter; Duration = TimeSpan.FromMilliseconds 1.0; Timestamp = DateTimeOffset.UtcNow }
      { Stage = PipelineStage.FcsTypeCheck; Duration = TimeSpan.FromMilliseconds 100.0; Timestamp = DateTimeOffset.UtcNow }
      { Stage = PipelineStage.TestExecution; Duration = TimeSpan.FromMilliseconds 50.0; Timestamp = DateTimeOffset.UtcNow }
    ]
    let pt = PipelineResult.toPipelineTiming RunTrigger.FileSave 47 12 timings
    match pt.Depth with
    | PipelineDepth.ThroughExecution (ts, fcs, exec) ->
      ts.TotalMilliseconds |> Expect.equal "ts" 1.0
      fcs.TotalMilliseconds |> Expect.equal "fcs" 100.0
      exec.TotalMilliseconds |> Expect.equal "exec" 50.0
    | _ -> failtest "expected ThroughExecution"
  }
]

let debouncedPipelineTests = testList "DebouncedPipeline" [
  testAsync "runStage completes when not cancelled" {
    let dc = new DebounceController()
    let token = dc.Acquire(PipelineStage.TreeSitter)
    let! result = DebouncedPipeline.runStage PipelineStage.TreeSitter 10 token (fun () -> "parsed")
    match result with
    | PipelineResult.Completed (r, timings) ->
      r |> Expect.equal "result" "parsed"
      timings |> List.length |> Expect.equal "one timing" 1
      timings.[0].Stage |> Expect.equal "stage" PipelineStage.TreeSitter
    | PipelineResult.Cancelled -> failtest "should not be cancelled"
    (dc :> IDisposable).Dispose()
  }

  testAsync "runStage cancels when token is cancelled before delay" {
    let dc = new DebounceController()
    let token = dc.Acquire(PipelineStage.FcsTypeCheck)
    let _ = dc.Acquire(PipelineStage.FcsTypeCheck)
    let! result = DebouncedPipeline.runStage PipelineStage.FcsTypeCheck 10 token (fun () -> "never")
    match result with
    | PipelineResult.Cancelled -> ()
    | _ -> failtest "should be cancelled"
    (dc :> IDisposable).Dispose()
  }

  testAsync "andThen chains stages" {
    let dc = new DebounceController()
    let token = dc.Acquire(PipelineStage.TreeSitter)
    let! stage1 = DebouncedPipeline.runStage PipelineStage.TreeSitter 5 token (fun () -> 10)
    let! stage2 = DebouncedPipeline.andThen PipelineStage.FcsTypeCheck 5 token (fun x -> x * 2) stage1
    match stage2 with
    | PipelineResult.Completed (r, timings) ->
      r |> Expect.equal "chained result" 20
      timings |> List.length |> Expect.equal "two timings" 2
    | PipelineResult.Cancelled -> failtest "should not be cancelled"
    (dc :> IDisposable).Dispose()
  }

  testAsync "andThen short-circuits on cancelled" {
    let mutable ran = false
    let dc = new DebounceController()
    let token = dc.Acquire(PipelineStage.TreeSitter)
    let cancelled : PipelineResult<int> = PipelineResult.Cancelled
    let! result = DebouncedPipeline.andThen PipelineStage.FcsTypeCheck 5 token (fun x -> ran <- true; x) cancelled
    match result with
    | PipelineResult.Cancelled -> ()
    | _ -> failtest "should be cancelled"
    Expect.isFalse "f should not run" ran
    (dc :> IDisposable).Dispose()
  }
]

let pipelineHistoryTests = testList "PipelineHistory" [
  test "empty history has no latest" {
    let h = PipelineHistory(5)
    h.Latest |> Expect.isNone "no latest"
    h.Count |> Expect.equal "count" 0
  }

  test "add and retrieve latest" {
    let h = PipelineHistory(5)
    let t = {
      Depth = PipelineDepth.TreeSitterOnly (TimeSpan.FromMilliseconds 1.0)
      TotalTests = 10; AffectedTests = 3
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    h.Add(t)
    h.Latest |> Expect.isSome "has latest"
    h.Count |> Expect.equal "count" 1
  }

  test "ring buffer wraps at capacity" {
    let h = PipelineHistory(3)
    for i in 1..5 do
      h.Add {
        Depth = PipelineDepth.TreeSitterOnly (TimeSpan.FromMilliseconds (float i))
        TotalTests = i; AffectedTests = 0
        Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
      }
    h.Count |> Expect.equal "capped at 3" 3
    let arr = h.ToArray()
    arr.Length |> Expect.equal "3 items" 3
    arr.[0].TotalTests |> Expect.equal "first" 3
    arr.[1].TotalTests |> Expect.equal "second" 4
    arr.[2].TotalTests |> Expect.equal "third" 5
  }

  test "latest returns most recent after wrap" {
    let h = PipelineHistory(2)
    for i in 1..4 do
      h.Add {
        Depth = PipelineDepth.TreeSitterOnly (TimeSpan.FromMilliseconds (float i))
        TotalTests = i; AffectedTests = 0
        Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
      }
    match h.Latest with
    | Some t -> t.TotalTests |> Expect.equal "latest is 4" 4
    | None -> failtest "should have latest"
  }
]

let policyFilterTests = testList "PolicyFilter" [
  test "OnEveryChange runs on all triggers" {
    PolicyFilter.shouldRun RunPolicy.OnEveryChange RunTrigger.Keystroke
    |> Expect.isTrue "keystroke"
    PolicyFilter.shouldRun RunPolicy.OnEveryChange RunTrigger.FileSave
    |> Expect.isTrue "save"
    PolicyFilter.shouldRun RunPolicy.OnEveryChange RunTrigger.ExplicitRun
    |> Expect.isTrue "explicit"
  }

  test "OnSaveOnly skips keystrokes" {
    PolicyFilter.shouldRun RunPolicy.OnSaveOnly RunTrigger.Keystroke
    |> Expect.isFalse "keystroke"
    PolicyFilter.shouldRun RunPolicy.OnSaveOnly RunTrigger.FileSave
    |> Expect.isTrue "save"
    PolicyFilter.shouldRun RunPolicy.OnSaveOnly RunTrigger.ExplicitRun
    |> Expect.isTrue "explicit"
  }

  test "OnDemand only on explicit" {
    PolicyFilter.shouldRun RunPolicy.OnDemand RunTrigger.Keystroke
    |> Expect.isFalse "keystroke"
    PolicyFilter.shouldRun RunPolicy.OnDemand RunTrigger.FileSave
    |> Expect.isFalse "save"
    PolicyFilter.shouldRun RunPolicy.OnDemand RunTrigger.ExplicitRun
    |> Expect.isTrue "explicit"
  }

  test "Disabled never runs" {
    PolicyFilter.shouldRun RunPolicy.Disabled RunTrigger.Keystroke
    |> Expect.isFalse "keystroke"
    PolicyFilter.shouldRun RunPolicy.Disabled RunTrigger.ExplicitRun
    |> Expect.isFalse "explicit"
  }

  test "filterTests respects category policies" {
    let policies = Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Integration, RunPolicy.OnDemand
      TestCategory.Browser, RunPolicy.Disabled
    ]
    let tests = [|
      { Id = TestId.create "unit1" "xunit"; FullName = "unit1"; DisplayName = "unit1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
        Category = TestCategory.Unit }
      { Id = TestId.create "int1" "xunit"; FullName = "int1"; DisplayName = "int1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
        Category = TestCategory.Integration }
      { Id = TestId.create "browser1" "xunit"; FullName = "browser1"; DisplayName = "browser1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
        Category = TestCategory.Browser }
    |]
    let filtered = PolicyFilter.filterTests policies RunTrigger.Keystroke tests
    filtered.Length |> Expect.equal "only unit on keystroke" 1
    filtered.[0].FullName |> Expect.equal "unit test" "unit1"
    let explicit = PolicyFilter.filterTests policies RunTrigger.ExplicitRun tests
    explicit.Length |> Expect.equal "unit + integration on explicit" 2
  }
]

// ============================================================
// Staleness Tests
// ============================================================

[<Tests>]
let stalenessTests = testList "Staleness" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" "expecto"; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" "expecto"; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let mkResult tid res =
    { TestId = tid; TestName = ""; Result = res; Timestamp = DateTimeOffset.UtcNow }
  let depGraph = {
    SymbolToTests = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
    ]
    TransitiveCoverage = Map.empty; SourceVersion = 1
  }
  let baseState = {
    LiveTestState.empty with
      DiscoveredTests = [| test1; test2 |]
      LastResults = Map.ofList [
        test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
        test2.Id, mkResult test2.Id (TestResult.Passed (TimeSpan.FromMilliseconds 3.0))
      ]
      Enabled = true
  }

  test "markStale marks affected tests as NotRun" {
    let result = Staleness.markStale depGraph [ "Module.add" ] baseState
    let r = Map.find test1.Id result.LastResults
    r.Result |> Expect.equal "not run" TestResult.NotRun
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
]

// ============================================================
// Pipeline Orchestrator Tests
// ============================================================

[<Tests>]
let orchestratorTests = testList "PipelineOrchestrator" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" "expecto"; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" "expecto"; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let intTest =
    { Id = TestId.create "Module.Tests.intTest" "expecto"; FullName = "Module.Tests.intTest"
      DisplayName = "intTest"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Integration }
  let depGraph = {
    SymbolToTests = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
      "Module.dbCall", [| intTest.Id |]
    ]
    TransitiveCoverage = Map.empty; SourceVersion = 1
  }
  let baseState = {
    LiveTestState.empty with
      DiscoveredTests = [| test1; test2; intTest |]
      Enabled = true
  }

  test "decide skips when disabled" {
    let state = { baseState with Enabled = false }
    let d = PipelineOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | PipelineDecision.Skip _ -> ()
    | other -> failwithf "Expected Skip, got %A" other
  }

  test "decide skips when already running" {
    let state = { baseState with IsRunning = true }
    let d = PipelineOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | PipelineDecision.Skip _ -> ()
    | other -> failwithf "Expected Skip, got %A" other
  }

  test "decide returns TreeSitterOnly when no tests discovered" {
    let state = { baseState with DiscoveredTests = [||] }
    let d = PipelineOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    d |> Expect.equal "tree sitter only" PipelineDecision.TreeSitterOnly
  }

  test "decide returns FullPipeline with affected unit tests on Keystroke" {
    let d = PipelineOrchestrator.decide baseState RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | PipelineDecision.FullPipeline ids ->
      ids.Length |> Expect.equal "1 affected" 1
      ids.[0] |> Expect.equal "test1" test1.Id
    | other -> failwithf "Expected FullPipeline, got %A" other
  }

  test "decide filters integration tests on Keystroke" {
    let d = PipelineOrchestrator.decide baseState RunTrigger.Keystroke [ "Module.dbCall" ] depGraph
    match d with
    | PipelineDecision.TreeSitterOnly -> ()
    | other -> failwithf "Expected TreeSitterOnly (integration filtered), got %A" other
  }

  test "decide returns TreeSitterOnly when all affected tests filtered by policy" {
    let state = {
      baseState with
        RunPolicies = Map.ofList [ TestCategory.Unit, RunPolicy.OnDemand ]
    }
    let d = PipelineOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | PipelineDecision.TreeSitterOnly -> ()
    | other -> failwithf "Expected TreeSitterOnly, got %A" other
  }

  test "decide includes integration tests on ExplicitRun" {
    let d = PipelineOrchestrator.decide baseState RunTrigger.ExplicitRun [ "Module.dbCall" ] depGraph
    match d with
    | PipelineDecision.FullPipeline ids ->
      ids.Length |> Expect.equal "1 integration" 1
      ids.[0] |> Expect.equal "intTest" intTest.Id
    | other -> failwithf "Expected FullPipeline, got %A" other
  }

  test "buildRunBatch returns matching test cases" {
    let batch = PipelineOrchestrator.buildRunBatch baseState [| test1.Id |]
    batch.Length |> Expect.equal "1 test" 1
    batch.[0].FullName |> Expect.equal "test1" "Module.Tests.test1"
  }

  test "buildRunBatch filters out unknown IDs" {
    let unknownId = TestId.create "Unknown.test" "xunit"
    let batch = PipelineOrchestrator.buildRunBatch baseState [| unknownId |]
    batch |> Expect.isEmpty "no matches"
  }
]

// ============================================================
// Pipeline Status Bar Tests
// ============================================================

[<Tests>]
let pipelineStatusBarTests = testList "Pipeline Status Bar" [
  test "full pipeline timing formats correctly" {
    let timing = {
      Depth = PipelineDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 0.8,
        TimeSpan.FromMilliseconds 142.0,
        TimeSpan.FromMilliseconds 87.0)
      TotalTests = 100; AffectedTests = 12
      Trigger = RunTrigger.Keystroke
      Timestamp = DateTimeOffset.UtcNow
    }
    let bar = PipelineTiming.toStatusBar timing
    bar |> Expect.stringContains "TS" "TS:"
    bar |> Expect.stringContains "FCS" "FCS:"
    bar |> Expect.stringContains "Run" "Run:"
    bar |> Expect.stringContains "tests" "12"
  }

  test "tree-sitter only timing shows partial" {
    let timing = {
      Depth = PipelineDepth.TreeSitterOnly (TimeSpan.FromMilliseconds 0.5)
      TotalTests = 100; AffectedTests = 0
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    let bar = PipelineTiming.toStatusBar timing
    bar |> Expect.stringContains "TS" "TS:"
    Expect.isFalse "no FCS" (bar.Contains "FCS:")
  }

  test "pipeline history ring buffer tracks last N runs" {
    let history = PipelineHistory(3)
    for i in 1..5 do
      let t = {
        Depth = PipelineDepth.TreeSitterOnly (TimeSpan.FromMilliseconds (float i))
        TotalTests = i; AffectedTests = 0
        Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
      }
      history.Add(t)
    history.Count |> Expect.equal "capped at 3" 3
    let latest = history.Latest
    match latest with
    | Some t -> t.TotalTests |> Expect.equal "latest is 5" 5
    | None -> failwith "Expected Some"
  }

  test "pipeline history latest returns most recent" {
    let history = PipelineHistory(10)
    let t = {
      Depth = PipelineDepth.TreeSitterOnly (TimeSpan.FromMilliseconds 1.0)
      TotalTests = 42; AffectedTests = 0
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    history.Add(t)
    let latest = history.Latest
    match latest with
    | Some l -> l.TotalTests |> Expect.equal "42" 42
    | None -> failwith "Expected Some"
  }
]

// ============================================================
// StatusEntry Computation Integration Tests
// ============================================================

[<Tests>]
let statusEntryTests = testList "StatusEntry Computation" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" "expecto"; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" "expecto"; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let mkResult tid res =
    { TestId = tid; TestName = ""; Result = res; Timestamp = DateTimeOffset.UtcNow }

  test "computeStatusEntries maps Passed results correctly" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]
        LastResults = Map.ofList [
          test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
        ]
        Enabled = true
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.Length |> Expect.equal "1 entry" 1
    match entries.[0].Status with
    | TestRunStatus.Passed dur -> dur.TotalMilliseconds |> Expect.floatClose "5ms" Accuracy.medium 5.0
    | other -> failwithf "Expected Passed, got %A" other
  }

  test "computeStatusEntries shows Detected for tests with no results" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Enabled = true
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].Status |> Expect.equal "detected" TestRunStatus.Detected
  }

  test "computeStatusEntries shows PolicyDisabled for disabled categories" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Enabled = true
        RunPolicies = Map.ofList [ TestCategory.Unit, RunPolicy.Disabled ]
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].Status |> Expect.equal "disabled" TestRunStatus.PolicyDisabled
  }

  test "computeStatusEntries shows Running for affected+running tests" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Enabled = true
        AffectedTests = Set.ofList [ test1.Id ]
        IsRunning = true
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].Status |> Expect.equal "running" TestRunStatus.Running
  }

  test "computeStatusEntries shows Queued for affected but not running" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Enabled = true
        AffectedTests = Set.ofList [ test1.Id ]
        IsRunning = false
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].Status |> Expect.equal "queued" TestRunStatus.Queued
  }

  test "mergeResults transitions from Running to Passed" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Enabled = true
        AffectedTests = Set.ofList [ test1.Id ]
        IsRunning = true
    }
    let newResults = [|
      mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    |]
    let merged = LiveTesting.mergeResults state newResults
    let entry = merged.StatusEntries |> Array.find (fun e -> e.TestId = test1.Id)
    match entry.Status with
    | TestRunStatus.Passed _ -> ()
    | other -> failwithf "Expected Passed, got %A" other
  }

  test "mergeResults preserves previousStatus" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Enabled = true
        LastResults = Map.ofList [
          test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
        ]
    }
    let state = { state with StatusEntries = LiveTesting.computeStatusEntries state }
    let newResults = [|
      mkResult test1.Id (TestResult.Failed (TestFailure.AssertionFailed "oops", TimeSpan.FromMilliseconds 1.0))
    |]
    let merged = LiveTesting.mergeResults state newResults
    let entry = merged.StatusEntries |> Array.find (fun e -> e.TestId = test1.Id)
    match entry.PreviousStatus with
    | TestRunStatus.Passed _ -> ()
    | other -> failwithf "Expected previous Passed, got %A" other
  }
]

// ============================================================
// Gutter Annotation Tests
// ============================================================

[<Tests>]
let annotationTests = testList "Gutter Annotations" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" "expecto"; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.SourceMapped ("test.fs", 10)
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let mkResult tid res =
    { TestId = tid; TestName = ""; Result = res; Timestamp = DateTimeOffset.UtcNow }

  test "annotationsForFile returns tree-sitter locations when no results" {
    let state = {
      LiveTestState.empty with
        SourceLocations = [|
          { AttributeName = "Test"; FilePath = "test.fs"; Line = 10; Column = 0 }
          { AttributeName = "Test"; FilePath = "other.fs"; Line = 5; Column = 0 }
        |]
        Enabled = true
    }
    let annotations = LiveTesting.annotationsForFile "test.fs" state
    annotations.Length |> Expect.equal "1 annotation" 1
    annotations.[0].Line |> Expect.equal "line 10" 10
    annotations.[0].Icon |> Expect.equal "detected glyph" GutterIcon.TestDiscovered
  }

  test "annotationsForFile prefers result annotations over tree-sitter" {
    let baseState = {
      LiveTestState.empty with
        SourceLocations = [|
          { AttributeName = "Test"; FilePath = "test.fs"; Line = 10; Column = 0 }
        |]
        DiscoveredTests = [| test1 |]
        LastResults = Map.ofList [
          test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
        ]
        Enabled = true
    }
    let state = { baseState with StatusEntries = LiveTesting.computeStatusEntries baseState }
    let annotations = LiveTesting.annotationsForFile "test.fs" state
    annotations.Length |> Expect.equal "1 annotation" 1
    annotations.[0].Icon |> Expect.equal "passed glyph" GutterIcon.TestPassed
  }

  test "GutterIcon chars are correct" {
    GutterIcon.toChar GutterIcon.TestPassed |> Expect.equal "check" '\u2713'
    GutterIcon.toChar GutterIcon.TestFailed |> Expect.equal "cross" '\u2717'
    GutterIcon.toChar GutterIcon.TestDiscovered |> Expect.equal "diamond" '\u25C6'
  }

  test "tooltip includes duration for passed tests" {
    let status = TestRunStatus.Passed (TimeSpan.FromMilliseconds 12.5)
    let tip = StatusToGutter.tooltip "test1" status
    tip |> Expect.stringContains "check mark" "\u2713"
    tip |> Expect.stringContains "duration" "12"
  }

  test "tooltip shows failure message" {
    let status = TestRunStatus.Failed (TestFailure.AssertionFailed "expected 42 got 0", TimeSpan.FromMilliseconds 1.0)
    let tip = StatusToGutter.tooltip "test1" status
    tip |> Expect.stringContains "cross mark" "\u2717"
    tip |> Expect.stringContains "message" "expected 42 got 0"
  }
]

// ============================================================
// Coverage Projection Tests
// ============================================================

[<Tests>]
let coverageProjectionExtendedTests = testList "Coverage Projection Extended" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" "expecto"; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" "expecto"; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let mkResult tid res =
    { TestId = tid; TestName = ""; Result = res; Timestamp = DateTimeOffset.UtcNow }
  let results = Map.ofList [
    test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
    test2.Id, mkResult test2.Id (TestResult.Passed (TimeSpan.FromMilliseconds 3.0))
  ]

  test "symbolCoverage returns NotCovered for unknown symbol" {
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.empty }
    let cov = CoverageProjection.symbolCoverage graph results "Unknown.symbol"
    cov |> Expect.equal "not covered" CoverageStatus.NotCovered
  }

  test "symbolCoverage returns Covered with all passing" {
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "Module.add", [| test1.Id |] ]
    }
    let cov = CoverageProjection.symbolCoverage graph results "Module.add"
    match cov with
    | CoverageStatus.Covered (count, allPassing) ->
      count |> Expect.equal "1 test" 1
      allPassing |> Expect.isTrue "all passing"
    | other -> failwithf "Expected Covered, got %A" other
  }

  test "symbolCoverage returns Covered with not all passing when test fails" {
    let failedResults =
      Map.add test1.Id (mkResult test1.Id (TestResult.Failed (TestFailure.AssertionFailed "bad", TimeSpan.FromMilliseconds 1.0))) results
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "Module.add", [| test1.Id |] ]
    }
    let cov = CoverageProjection.symbolCoverage graph failedResults "Module.add"
    match cov with
    | CoverageStatus.Covered (_, allPassing) ->
      allPassing |> Expect.isFalse "not all passing"
    | other -> failwithf "Expected Covered, got %A" other
  }

  test "computeAll returns coverage for all symbols" {
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [
          "Module.add", [| test1.Id |]
          "Module.validate", [| test1.Id; test2.Id |]
          "Module.unused", [||]
        ]
    }
    let all = CoverageProjection.computeAll graph results
    all.Count |> Expect.equal "3 symbols" 3
    match Map.find "Module.unused" all with
    | CoverageStatus.NotCovered -> ()
    | other -> failwithf "Expected NotCovered for unused, got %A" other
    match Map.find "Module.validate" all with
    | CoverageStatus.Covered (count, _) -> count |> Expect.equal "2 tests" 2
    | other -> failwithf "Expected Covered for validate, got %A" other
  }

  test "IL line coverage computes correctly" {
    let covState = {
      Slots = [|
        { File = "test.fs"; Line = 10; Column = 0; BranchId = 0 }
        { File = "test.fs"; Line = 10; Column = 0; BranchId = 1 }
        { File = "test.fs"; Line = 10; Column = 0; BranchId = 2 }
        { File = "test.fs"; Line = 20; Column = 0; BranchId = 0 }
      |]
      Hits = [| true; true; false; false |]
    }
    let line10 = CoverageComputation.computeLineCoverage covState "test.fs" 10
    match line10 with
    | LineCoverage.PartiallyCovered (covered, total) ->
      covered |> Expect.equal "2 covered" 2
      total |> Expect.equal "3 total" 3
    | other -> failwithf "Expected PartiallyCovered, got %A" other

    let line20 = CoverageComputation.computeLineCoverage covState "test.fs" 20
    line20 |> Expect.equal "line 20 not covered" LineCoverage.NotCovered

    let line30 = CoverageComputation.computeLineCoverage covState "test.fs" 30
    line30 |> Expect.equal "line 30 not covered (no slots)" LineCoverage.NotCovered
  }
]

// ============================================================
// Dependency Graph Tests
// ============================================================

[<Tests>]
let depGraphBfsTests = testList "TestDependencyGraph BFS" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" "expecto"; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" "expecto"; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
  let depGraph = {
    SymbolToTests = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
    ]
    TransitiveCoverage = Map.empty; SourceVersion = 1
  }

  test "findAffected returns empty for unknown symbols" {
    let result = TestDependencyGraph.findAffected [ "Unknown" ] depGraph
    result |> Expect.isEmpty "no affected tests"
  }

  test "findAffected returns union of affected tests for multiple symbols" {
    let result = TestDependencyGraph.findAffected [ "Module.add"; "Module.validate" ] depGraph
    result.Length |> Expect.equal "2 unique affected" 2
  }

  test "computeTransitiveCoverage propagates through call chain" {
    let callGraph = Map.ofList [
      "testFunc", [| "helperA" |]
      "helperA", [| "helperB" |]
    ]
    let directSymbolToTests = Map.ofList [
      "testFunc", [| test1.Id |]
    ]
    let transitive = TestDependencyGraph.computeTransitiveCoverage callGraph directSymbolToTests
    transitive.ContainsKey "helperA" |> Expect.isTrue "helperA reachable"
    transitive.ContainsKey "helperB" |> Expect.isTrue "helperB reachable"
    let helperBTests = Map.find "helperB" transitive
    helperBTests |> Expect.contains "helperB covered by test1" test1.Id
  }

  test "computeTransitiveCoverage handles diamond dependency" {
    let callGraph = Map.ofList [
      "test1Func", [| "A" |]
      "test2Func", [| "B" |]
      "A", [| "C" |]
      "B", [| "C" |]
    ]
    let direct = Map.ofList [
      "test1Func", [| test1.Id |]
      "test2Func", [| test2.Id |]
    ]
    let transitive = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    let cTests = Map.find "C" transitive
    cTests.Length |> Expect.equal "C covered by 2 tests" 2
  }

  test "computeTransitiveCoverage handles cycles without infinite loop" {
    let callGraph = Map.ofList [
      "A", [| "B" |]
      "B", [| "A" |]
    ]
    let direct = Map.ofList [
      "A", [| test1.Id |]
    ]
    let transitive = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    transitive.ContainsKey "B" |> Expect.isTrue "B reachable from A"
    transitive.ContainsKey "A" |> Expect.isTrue "A reachable from itself"
  }
]

// ============================================================
// Category Detection Tests
// ============================================================

[<Tests>]
let categoryDetectionTests = testList "CategoryDetection" [
  test "categorizes integration by label" {
    let cat = CategoryDetection.categorize [ "Integration" ] "MyModule.test" "expecto" [||]
    cat |> Expect.equal "integration" TestCategory.Integration
  }

  test "categorizes browser by Playwright assembly ref" {
    let cat = CategoryDetection.categorize [] "MyModule.test" "expecto" [| "Microsoft.Playwright" |]
    cat |> Expect.equal "browser" TestCategory.Browser
  }

  test "categorizes benchmark by label" {
    let cat = CategoryDetection.categorize [ "Benchmark" ] "MyModule.test" "expecto" [||]
    cat |> Expect.equal "benchmark" TestCategory.Benchmark
  }

  test "categorizes by namespace containing 'integration'" {
    let cat = CategoryDetection.categorize [] "MyApp.Integration.Tests.myTest" "xunit" [||]
    cat |> Expect.equal "integration by name" TestCategory.Integration
  }

  test "defaults to Unit" {
    let cat = CategoryDetection.categorize [] "MyModule.test" "expecto" [||]
    cat |> Expect.equal "unit by default" TestCategory.Unit
  }
]

// ============================================================
// Test Summary Tests
// ============================================================

[<Tests>]
let testSummaryDetailTests = testList "TestSummary" [
  test "fromStatuses counts correctly" {
    let statuses = [|
      TestRunStatus.Passed (TimeSpan.FromMilliseconds 5.0)
      TestRunStatus.Passed (TimeSpan.FromMilliseconds 3.0)
      TestRunStatus.Failed (TestFailure.AssertionFailed "x", TimeSpan.FromMilliseconds 1.0)
      TestRunStatus.Stale
      TestRunStatus.Running
      TestRunStatus.PolicyDisabled
      TestRunStatus.Detected
    |]
    let summary = TestSummary.fromStatuses statuses
    summary.Total |> Expect.equal "total" 7
    summary.Passed |> Expect.equal "passed" 2
    summary.Failed |> Expect.equal "failed" 1
    summary.Stale |> Expect.equal "stale" 1
    summary.Running |> Expect.equal "running" 1
    summary.Disabled |> Expect.equal "disabled" 1
  }

  test "toStatusBar shows all passing" {
    let s = { TestSummary.empty with Total = 10; Passed = 10 }
    let bar = TestSummary.toStatusBar s
    bar |> Expect.stringContains "check" "\u2713"
    bar |> Expect.stringContains "count" "10/10"
  }

  test "toStatusBar shows failures" {
    let s = { TestSummary.empty with Total = 10; Passed = 8; Failed = 2 }
    let bar = TestSummary.toStatusBar s
    bar |> Expect.stringContains "cross" "\u2717"
    bar |> Expect.stringContains "fail count" "2"
  }

  test "toStatusBar shows running" {
    let s = { TestSummary.empty with Total = 10; Passed = 5; Running = 3 }
    let bar = TestSummary.toStatusBar s
    bar |> Expect.stringContains "spinner" "\u27F3"
  }

  test "toStatusBar shows none when empty" {
    let bar = TestSummary.toStatusBar TestSummary.empty
    bar |> Expect.equal "none text" "Tests: none"
  }
]

// ============================================================
// Debounce Channel Tests
// ============================================================

[<Tests>]
let debounceChannelTests = testList "DebounceChannel" [
  let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)

  test "empty channel has no pending" {
    let ch = DebounceChannel.empty<string>
    ch.Pending |> Expect.isNone "no pending"
    ch.CurrentGeneration |> Expect.equal "gen 0" 0L
  }

  test "submit creates pending op" {
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit "hello" 50 t0
    ch.Pending |> Expect.isSome "has pending"
    ch.CurrentGeneration |> Expect.equal "gen 1" 1L
    ch.Pending.Value.Payload |> Expect.equal "payload" "hello"
    ch.Pending.Value.DelayMs |> Expect.equal "delay" 50
  }

  test "tryFire before delay returns None" {
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit "hello" 50 t0
    let result, ch' = DebounceChannel.tryFire (t0.AddMilliseconds 30.0) ch
    result |> Expect.isNone "not ready yet"
    ch'.Pending |> Expect.isSome "still pending"
  }

  test "tryFire after delay returns payload" {
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit "hello" 50 t0
    let result, ch' = DebounceChannel.tryFire (t0.AddMilliseconds 51.0) ch
    result |> Expect.isSome "ready"
    result.Value |> Expect.equal "payload" "hello"
    ch'.Pending |> Expect.isNone "cleared"
    ch'.LastCompleted |> Expect.isSome "completed set"
  }

  test "newer submit supersedes older pending" {
    let ch =
      DebounceChannel.empty<string>
      |> DebounceChannel.submit "first" 50 t0
      |> DebounceChannel.submit "second" 50 (t0.AddMilliseconds 20.0)
    ch.CurrentGeneration |> Expect.equal "gen 2" 2L
    ch.Pending.Value.Payload |> Expect.equal "latest payload" "second"
  }

  test "stale op is discarded on tryFire" {
    let ch =
      DebounceChannel.empty<string>
      |> DebounceChannel.submit "first" 50 t0
    let ch2 = ch |> DebounceChannel.submit "second" 50 (t0.AddMilliseconds 20.0)
    let staleOp = { Payload = "first"; RequestedAt = t0; DelayMs = 50; Generation = 1L }
    let ch3 = { ch2 with Pending = Some staleOp }
    let result, ch4 = DebounceChannel.tryFire (t0.AddMilliseconds 60.0) ch3
    result |> Expect.isNone "stale, discarded"
    ch4.Pending |> Expect.isNone "cleared"
  }

  test "isStale detects superseded pending" {
    let ch =
      DebounceChannel.empty<string>
      |> DebounceChannel.submit "first" 50 t0
    let staleOp = { Payload = "first"; RequestedAt = t0; DelayMs = 50; Generation = 0L }
    let ch2 = { ch with Pending = Some staleOp }
    DebounceChannel.isStale ch2 |> Expect.isTrue "should be stale"
  }

  test "tryFire on empty channel returns None" {
    let result, _ = DebounceChannel.tryFire t0 DebounceChannel.empty<string>
    result |> Expect.isNone "nothing to fire"
  }

  test "exact delay boundary fires" {
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit "hello" 50 t0
    let result, _ = DebounceChannel.tryFire (t0.AddMilliseconds 50.0) ch
    result |> Expect.isSome "fires at exact boundary"
  }
]

// ============================================================
// Pipeline Debounce Tests
// ============================================================

[<Tests>]
let pipelineDebounceTests = testList "PipelineDebounce" [
  let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)

  test "empty has no pending on either channel" {
    let db = PipelineDebounce.empty
    db.TreeSitter.Pending |> Expect.isNone "no tree-sitter"
    db.Fcs.Pending |> Expect.isNone "no fcs"
  }

  test "onKeystroke submits to both channels" {
    let db = PipelineDebounce.empty |> PipelineDebounce.onKeystroke "code" "file.fs" t0
    db.TreeSitter.Pending |> Expect.isSome "tree-sitter pending"
    db.Fcs.Pending |> Expect.isSome "fcs pending"
    db.TreeSitter.Pending.Value.DelayMs |> Expect.equal "ts delay" 50
    db.Fcs.Pending.Value.DelayMs |> Expect.equal "fcs delay" 300
  }

  test "tree-sitter fires before FCS" {
    let db = PipelineDebounce.empty |> PipelineDebounce.onKeystroke "code" "file.fs" t0
    let (tsResult, fcsResult), _ = PipelineDebounce.tick (t0.AddMilliseconds 51.0) db
    tsResult |> Expect.isSome "tree-sitter fires"
    fcsResult |> Expect.isNone "fcs not yet"
  }

  test "both fire after 300ms" {
    let db = PipelineDebounce.empty |> PipelineDebounce.onKeystroke "code" "file.fs" t0
    let (tsResult, fcsResult), _ = PipelineDebounce.tick (t0.AddMilliseconds 301.0) db
    tsResult |> Expect.isSome "tree-sitter fires"
    fcsResult |> Expect.isSome "fcs fires"
  }

  test "rapid keystrokes cancel previous debounce" {
    let db =
      PipelineDebounce.empty
      |> PipelineDebounce.onKeystroke "v1" "file.fs" t0
      |> PipelineDebounce.onKeystroke "v2" "file.fs" (t0.AddMilliseconds 30.0)
      |> PipelineDebounce.onKeystroke "v3" "file.fs" (t0.AddMilliseconds 60.0)
    let (tsResult, _), _ = PipelineDebounce.tick (t0.AddMilliseconds 111.0) db
    tsResult |> Expect.isSome "fires for latest"
    tsResult.Value |> Expect.equal "latest content" "v3"
  }

  test "onFileSave uses short FCS delay" {
    let db =
      PipelineDebounce.empty
      |> PipelineDebounce.onKeystroke "code" "file.fs" t0
      |> PipelineDebounce.onFileSave "file.fs" (t0.AddMilliseconds 100.0)
    db.Fcs.Pending.Value.DelayMs |> Expect.equal "save uses short delay" 50
    let (_, fcsResult), _ = PipelineDebounce.tick (t0.AddMilliseconds 151.0) db
    fcsResult |> Expect.isSome "fcs fires soon after save"
  }

  test "tick clears fired ops" {
    let db = PipelineDebounce.empty |> PipelineDebounce.onKeystroke "code" "file.fs" t0
    let _, db' = PipelineDebounce.tick (t0.AddMilliseconds 301.0) db
    db'.TreeSitter.Pending |> Expect.isNone "ts cleared"
    db'.Fcs.Pending |> Expect.isNone "fcs cleared"
  }
]

// ============================================================
// Pipeline Effects Tests
// ============================================================

[<Tests>]
let pipelineEffectsTests = testList "PipelineEffects" [
  test "fromTick with both payloads produces two effects" {
    let effects = PipelineEffects.fromTick (Some "code") (Some "file.fs") "file.fs"
    effects.Length |> Expect.equal "two effects" 2
    match effects.[0] with
    | PipelineEffect.ParseTreeSitter (content, _) ->
      content |> Expect.equal "ts content" "code"
    | _ -> failtest "expected ParseTreeSitter"
    match effects.[1] with
    | PipelineEffect.RequestFcsTypeCheck fp ->
      fp |> Expect.equal "fcs path" "file.fs"
    | _ -> failtest "expected RequestFcsTypeCheck"
  }

  test "fromTick with no payloads produces empty" {
    let effects = PipelineEffects.fromTick None None "file.fs"
    effects.Length |> Expect.equal "no effects" 0
  }

  test "fromTick with only tree-sitter produces one effect" {
    let effects = PipelineEffects.fromTick (Some "code") None "file.fs"
    effects.Length |> Expect.equal "one effect" 1
    match effects.[0] with
    | PipelineEffect.ParseTreeSitter _ -> ()
    | _ -> failtest "expected ParseTreeSitter"
  }

  test "afterTypeCheck with affected tests returns RunAffectedTests" {
    let tc1 = { Id = TestId.create "test1" "xunit"; FullName = "test1"; DisplayName = "test1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
                Category = TestCategory.Unit }
    let state = { LiveTestState.empty with DiscoveredTests = [| tc1 |]; Enabled = true }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "Module.add", [| tc1.Id |] ]
    }
    match PipelineEffects.afterTypeCheck ["Module.add"] RunTrigger.Keystroke graph state with
    | PipelineEffect.RunAffectedTests (ids, trigger) ->
      ids.Length |> Expect.equal "one test" 1
      trigger |> Expect.equal "keystroke trigger" RunTrigger.Keystroke
    | other -> failtestf "expected RunAffectedTests, got %A" other
  }

  test "afterTypeCheck with no affected tests returns NoOp" {
    let state = { LiveTestState.empty with DiscoveredTests = [||]; Enabled = true }
    let graph = TestDependencyGraph.empty
    PipelineEffects.afterTypeCheck ["unknown.symbol"] RunTrigger.Keystroke graph state
    |> Expect.equal "no op" PipelineEffect.NoOp
  }

  test "afterTypeCheck when disabled returns NoOp" {
    let state = { LiveTestState.empty with Enabled = false }
    let graph = TestDependencyGraph.empty
    PipelineEffects.afterTypeCheck ["Module.add"] RunTrigger.Keystroke graph state
    |> Expect.equal "disabled" PipelineEffect.NoOp
  }

  test "afterTypeCheck filters integration tests on keystroke" {
    let tc1 = { Id = TestId.create "unit1" "xunit"; FullName = "unit1"; DisplayName = "unit1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
                Category = TestCategory.Unit }
    let tc2 = { Id = TestId.create "integ1" "xunit"; FullName = "integ1"; DisplayName = "integ1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
                Category = TestCategory.Integration }
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| tc1; tc2 |]
        Enabled = true
    }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "Module.add", [| tc1.Id; tc2.Id |] ]
    }
    match PipelineEffects.afterTypeCheck ["Module.add"] RunTrigger.Keystroke graph state with
    | PipelineEffect.RunAffectedTests (ids, _) ->
      ids.Length |> Expect.equal "only unit test" 1
      ids.[0] |> Expect.equal "unit test id" tc1.Id
    | other -> failtestf "expected RunAffectedTests, got %A" other
  }
]

[<Tests>]
let cancellationChainTests = testList "CancellationChain" [
  test "next returns a live token" {
    use chain = new CancellationChain()
    let token = chain.next()
    token.IsCancellationRequested |> Expect.isFalse "fresh token is live"
  }
  test "next cancels previous token" {
    use chain = new CancellationChain()
    let t1 = chain.next()
    let _t2 = chain.next()
    t1.IsCancellationRequested |> Expect.isTrue "first token cancelled"
  }
  test "currentToken reflects latest" {
    use chain = new CancellationChain()
    let t1 = chain.next()
    (chain.currentToken = t1) |> Expect.isTrue "matches t1"
    let t2 = chain.next()
    (chain.currentToken = t2) |> Expect.isTrue "matches t2"
  }
  test "dispose cancels current token" {
    let chain = new CancellationChain()
    let t = chain.next()
    chain.dispose()
    t.IsCancellationRequested |> Expect.isTrue "disposed token cancelled"
  }
  test "currentToken is None when no next called" {
    use chain = new CancellationChain()
    (chain.currentToken = System.Threading.CancellationToken.None) |> Expect.isTrue "none token"
  }
]
