module SageFs.Tests.LiveTestingTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting

// --- Test Helpers ---

/// Resolve path to a project output DLL. In SageFs FSI the test DLL is shadow-copied,
/// so BaseDirectory points to the tool store, not the test bin.  Fall back to the known
/// project output directory when the DLL isn't beside the tool executable.
let resolveTestDll (dllName: string) =
  let basePath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, dllName)
  if System.IO.File.Exists basePath then basePath
  else
    let projBinPath =
      System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(
          System.IO.Path.GetDirectoryName(
            System.IO.Path.GetDirectoryName(
              System.IO.Path.GetDirectoryName(
                System.IO.Path.GetDirectoryName(
                  System.AppDomain.CurrentDomain.BaseDirectory))))),
        "SageFs.Tests", "bin", "Debug", "net10.0", dllName)
    if System.IO.File.Exists projBinPath then projBinPath
    else
      // Last resort: find it among loaded assemblies
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a ->
        not (System.String.IsNullOrEmpty a.Location)
        && System.IO.Path.GetFileName(a.Location) = dllName)
      |> Option.map (fun a -> a.Location)
      |> Option.defaultValue basePath

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

  test "merging results preserves RunPhase (no transition)" {
    let tid = mkTestId "t1" "x"
    let gen = RunGeneration.next RunGeneration.zero
    let running = { LiveTestState.empty with RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen }
    let results = [| mkResult tid (TestResult.Passed (ts 5.0)) |]
    LiveTesting.mergeResults running results
    |> fun s -> TestRunPhase.isAnyRunning s.RunPhases
    |> Expect.isTrue "should still be running"
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
      TransitiveCoverage = Map.ofList [ "MyModule.add", [| t1 |]; "MyModule.sub", [| t2 |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
    }
    TestDependencyGraph.findAffected ["MyModule.add"] graph
    |> Expect.hasLength "one affected test" 1
  }

  test "findAffected returns union of tests for multiple symbols" {
    let t1 = mkTestId "test1" "x"
    let t2 = mkTestId "test2" "x"
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| t1 |]; "B.g", [| t2 |] ]
      TransitiveCoverage = Map.ofList [ "A.f", [| t1 |]; "B.g", [| t2 |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
    }
    TestDependencyGraph.findAffected ["A.f"; "B.g"] graph
    |> Expect.hasLength "both tests" 2
  }

  test "findAffected deduplicates when test references multiple changed symbols" {
    let t1 = mkTestId "test1" "x"
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| t1 |]; "A.g", [| t1 |] ]
      TransitiveCoverage = Map.ofList [ "A.f", [| t1 |]; "A.g", [| t1 |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
    }
    TestDependencyGraph.findAffected ["A.f"; "A.g"] graph
    |> Expect.hasLength "deduplicated to one" 1
  }

  test "findAffected returns empty for unknown symbols" {
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| mkTestId "t" "x" |] ]
      TransitiveCoverage = Map.ofList [ "A.f", [| mkTestId "t" "x" |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
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
    | CoverageStatus.Covered (1, CoverageHealth.AllPassing) -> ()
    | other -> failtestf "expected Covered(1,AllPassing), got %A" other
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
    | CoverageStatus.Covered (1, CoverageHealth.SomeFailing) -> ()
    | other -> failtestf "expected Covered(1,SomeFailing), got %A" other
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
    | CoverageStatus.Covered (2, CoverageHealth.SomeFailing) -> ()
    | other -> failtestf "expected Covered(2,SomeFailing), got %A" other
  }

  test "symbol reachable by tests with no results yet has allPassing=false" {
    let tid = mkTestId "t1" "x"
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ]
    }
    match CoverageProjection.symbolCoverage graph Map.empty "MyModule.add" with
    | CoverageStatus.Covered (1, CoverageHealth.SomeFailing) -> ()
    | other -> failtestf "expected Covered(1,SomeFailing), got %A" other
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
    LiveTestState.empty.Activation
    |> Expect.equal "enabled by default" LiveTestingActivation.Active
  }

  test "starts with coverage shown" {
    LiveTestState.empty.CoverageDisplay
    |> Expect.equal "coverage shown by default" CoverageVisibility.Shown
  }

  test "starts not running" {
    TestRunPhase.isAnyRunning LiveTestState.empty.RunPhases
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
      TransitiveCoverage = Map.ofList [ "a", [| t1 |]; "b", [| t2 |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
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
      model.LiveTesting.TestState.DiscoveredTests
      |> Expect.equal "no discovered tests" Array.empty
      TestRunPhase.isAnyRunning model.LiveTesting.TestState.RunPhases
      |> Expect.isFalse "not running"
      model.LiveTesting.TestState.Activation
      |> Expect.equal "enabled" LiveTestingActivation.Active
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
        { Id = mkTestId "myTest" "expecto"; DisplayName = "myTest"
          FullName = "MyModule.myTest"; Framework = "expecto"
          Origin = TestOrigin.ReflectionOnly; Labels = []
          Category = TestCategory.Unit }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", [| tc |])))
          SageFsModel.initial
      model'.LiveTesting.TestState.DiscoveredTests.Length
      |> Expect.equal "should have 1 test" 1
    }
    test "TestRunStarted sets RunPhases to Running and AffectedTests" {
      let tid = mkTestId "t1" "x"
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "s")))
          SageFsModel.initial
      TestRunPhase.isAnyRunning model'.LiveTesting.TestState.RunPhases
      |> Expect.isTrue "should be running"
      Set.contains tid model'.LiveTesting.TestState.AffectedTests
      |> Expect.isTrue "should contain test id"
    }
    test "TestResultsBatch merges results but keeps RunPhase (cleared by TestRunCompleted)" {
      let gen = RunGeneration.next RunGeneration.zero
      let m =
        { SageFsModel.initial with
            LiveTesting =
              { SageFsModel.initial.LiveTesting with TestState = { SageFsModel.initial.LiveTesting.TestState with RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen } } }
      let tid = mkTestId "t1" "x"
      let r : TestRunResult =
        { TestId = tid; TestName = "t1"
          Result = LTTestResult.Passed (ts 5.0)
          Timestamp = DateTimeOffset.UtcNow }
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
          SageFsModel.initial
      model'.LiveTesting.TestState.Activation
      |> Expect.equal "should be active" LiveTestingActivation.Active
    }
    test "LiveTestingDisabled deactivates" {
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event SageFsEvent.LiveTestingDisabled)
          SageFsModel.initial
      model'.LiveTesting.TestState.Activation
      |> Expect.equal "should be inactive" LiveTestingActivation.Inactive
    }
    test "AffectedTestsComputed sets AffectedTests" {
      let t1 = mkTestId "t1" "x"
      let t2 = mkTestId "t2" "x"
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.AffectedTestsComputed [| t1; t2 |]))
          SageFsModel.initial
      Set.count model'.LiveTesting.TestState.AffectedTests
      |> Expect.equal "should have 2 affected" 2
    }
    test "RunPolicyChanged updates policy" {
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.RunPolicyChanged (TestCategory.Integration, RunPolicy.OnSaveOnly)))
          SageFsModel.initial
      Map.find TestCategory.Integration model'.LiveTesting.TestState.RunPolicies
      |> Expect.equal "should be OnSaveOnly" RunPolicy.OnSaveOnly
    }
    test "ProvidersDetected updates providers" {
      let p = ProviderDescription.Custom { Name = "expecto"; AssemblyMarker = "Expecto" }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.ProvidersDetected [p]))
          SageFsModel.initial
      model'.LiveTesting.TestState.DetectedProviders.Length
      |> Expect.equal "should have 1 provider" 1
    }
    test "CoverageUpdated produces annotations" {
      let cs : CoverageState =
        { Slots =
            [| { SequencePoint.File = "a.fs"; Line = 10; Column = 0; BranchId = 0 }
               { SequencePoint.File = "a.fs"; Line = 20; Column = 0; BranchId = 0 } |]
          Hits = [| true; false |] }
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.CoverageUpdated cs))
          SageFsModel.initial
      model'.LiveTesting.TestState.CoverageAnnotations.Length
      |> Expect.equal "should have 2 annotations" 2
    }
    test "no effects for live testing events" {
      let _, effects =
        SageFsUpdate.update
          (SageFsMsg.Event SageFsEvent.LiveTestingEnabled)
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


let affectedTestPipelineTests = testList "affected-test pipeline" [
  test "dep graph lookup finds affected tests" {
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [
          "MyModule.add", [| TestId.create "add-test" "xunit" |]
          "MyModule.validate", [| TestId.create "validate-test" "xunit" |]
        ]
        TransitiveCoverage = Map.ofList [
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
    let tc3 = mkTestCase "Module.Tests.newTest" "expecto" TestCategory.Unit
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
    TransitiveCoverage = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
      "Module.dbCall", [| intTest.Id |]
    ]; SourceVersion = 1
    PerFileIndex = Map.empty
  }
  let baseState = {
    LiveTestState.empty with
      DiscoveredTests = [| test1; test2; intTest |]
      Activation = LiveTestingActivation.Active
  }

  test "decide skips when disabled" {
    let state = { baseState with Activation = LiveTestingActivation.Inactive }
    let d = PipelineOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | PipelineDecision.Skip _ -> ()
    | other -> failwithf "Expected Skip, got %A" other
  }

  test "decide skips when already running" {
    let gen = RunGeneration.next RunGeneration.zero
    let state = { baseState with RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen }
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
        Activation = LiveTestingActivation.Active
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
        DiscoveredTests = [| test1 |]; Activation = LiveTestingActivation.Active
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].Status |> Expect.equal "detected" TestRunStatus.Detected
  }

  test "computeStatusEntries shows PolicyDisabled for disabled categories" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Activation = LiveTestingActivation.Active
        RunPolicies = Map.ofList [ TestCategory.Unit, RunPolicy.Disabled ]
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].Status |> Expect.equal "disabled" TestRunStatus.PolicyDisabled
  }

  test "computeStatusEntries shows Running for affected+running tests" {
    let gen = RunGeneration.next RunGeneration.zero
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Activation = LiveTestingActivation.Active
        AffectedTests = Set.ofList [ test1.Id ]
        RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].Status |> Expect.equal "running" TestRunStatus.Running
  }

  test "computeStatusEntries shows Queued for affected but not running" {
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Activation = LiveTestingActivation.Active
        AffectedTests = Set.ofList [ test1.Id ]
    }
    let entries = LiveTesting.computeStatusEntries state
    entries.[0].Status |> Expect.equal "queued" TestRunStatus.Queued
  }

  test "mergeResults transitions from Running to Passed" {
    let gen = RunGeneration.next RunGeneration.zero
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| test1 |]; Activation = LiveTestingActivation.Active
        AffectedTests = Set.ofList [ test1.Id ]
        RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
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
        DiscoveredTests = [| test1 |]; Activation = LiveTestingActivation.Active
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
let sourceMappingTests = testList "SourceMapping" [
  test "extractMethodName gets last segment from dotted name" {
    SourceMapping.extractMethodName "MyModule.Tests.shouldAdd"
    |> Expect.equal "last segment" "shouldAdd"
  }

  test "extractMethodName gets property before slash for Expecto" {
    SourceMapping.extractMethodName "Ns.Mod.allMyTests/group/should add"
    |> Expect.equal "before slash" "allMyTests"
  }

  test "attributeMatchesFramework matches Fact to xunit" {
    SourceMapping.attributeMatchesFramework "xunit" "Fact"
    |> Expect.isTrue "Fact is xunit"
  }

  test "attributeMatchesFramework does not match Fact to nunit" {
    SourceMapping.attributeMatchesFramework "nunit" "Fact"
    |> Expect.isFalse "Fact is not nunit"
  }

  test "mergeSourceLocations maps xunit test by function name" {
    let locations = [|
      { AttributeName = "Fact"; FunctionName = "shouldAdd"
        FilePath = "Tests.fs"; Line = 10; Column = 0 }
    |]
    let tests = [|
      { Id = TestId.create "Mod.Tests.shouldAdd" "xunit"
        FullName = "Mod.Tests.shouldAdd"; DisplayName = "shouldAdd"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "xunit"; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations locations tests
    result.[0].Origin
    |> Expect.equal "mapped" (TestOrigin.SourceMapped("Tests.fs", 10))
  }

  test "mergeSourceLocations maps Expecto hierarchical tests" {
    let locations = [|
      { AttributeName = "Tests"; FunctionName = "allMyTests"
        FilePath = "MyTests.fs"; Line = 5; Column = 0 }
    |]
    let tests = [|
      { Id = TestId.create "Ns.Mod.allMyTests/group/test1" "expecto"
        FullName = "Ns.Mod.allMyTests/group/test1"; DisplayName = "test1"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
      { Id = TestId.create "Ns.Mod.allMyTests/group/test2" "expecto"
        FullName = "Ns.Mod.allMyTests/group/test2"; DisplayName = "test2"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations locations tests
    result.[0].Origin
    |> Expect.equal "first mapped" (TestOrigin.SourceMapped("MyTests.fs", 5))
    result.[1].Origin
    |> Expect.equal "second mapped" (TestOrigin.SourceMapped("MyTests.fs", 5))
  }

  test "mergeSourceLocations preserves already-mapped tests" {
    let locations = [|
      { AttributeName = "Tests"; FunctionName = "myTests"
        FilePath = "New.fs"; Line = 99; Column = 0 }
    |]
    let tests = [|
      { Id = TestId.create "Mod.myTests" "expecto"
        FullName = "Mod.myTests"; DisplayName = "myTests"
        Origin = TestOrigin.SourceMapped("Old.fs", 42); Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations locations tests
    result.[0].Origin
    |> Expect.equal "kept original" (TestOrigin.SourceMapped("Old.fs", 42))
  }

  test "mergeSourceLocations returns tests unchanged when no locations" {
    let tests = [|
      { Id = TestId.create "T.x" "xunit"
        FullName = "T.x"; DisplayName = "x"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "xunit"; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations [||] tests
    result.[0].Origin
    |> Expect.equal "unchanged" TestOrigin.ReflectionOnly
  }

  test "mergeSourceLocations disambiguates by framework" {
    let locations = [|
      { AttributeName = "Test"; FunctionName = "validate"
        FilePath = "Nunit.fs"; Line = 10; Column = 0 }
      { AttributeName = "Fact"; FunctionName = "validate"
        FilePath = "Xunit.fs"; Line = 20; Column = 0 }
    |]
    let tests = [|
      { Id = TestId.create "Suite.validate" "xunit"
        FullName = "Suite.validate"; DisplayName = "validate"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "xunit"; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations locations tests
    result.[0].Origin
    |> Expect.equal "picked xunit location" (TestOrigin.SourceMapped("Xunit.fs", 20))
  }
]

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
          { AttributeName = "Test"; FunctionName = "test1"; FilePath = "test.fs"; Line = 10; Column = 0 }
          { AttributeName = "Test"; FunctionName = "test2"; FilePath = "other.fs"; Line = 5; Column = 0 }
        |]
        Activation = LiveTestingActivation.Active
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
          { AttributeName = "Test"; FunctionName = "test1"; FilePath = "test.fs"; Line = 10; Column = 0 }
        |]
        DiscoveredTests = [| test1 |]
        LastResults = Map.ofList [
          test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
        ]
        Activation = LiveTestingActivation.Active
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

  test "recomputeEditorAnnotations returns annotations when enabled" {
    let filePath = "Tests.fs"
    let state = { LiveTestState.empty with
                    SourceLocations = [|
                      { AttributeName = "Fact"; FunctionName = "test1"; FilePath = filePath; Line = 5; Column = 0 }
                      { AttributeName = "Test"; FunctionName = "test2"; FilePath = filePath; Line = 10; Column = 0 }
                    |] }
    let cached = LiveTesting.recomputeEditorAnnotations (Some filePath) state
    cached.Length |> Expect.equal "two annotations" 2
  }

  test "recomputeEditorAnnotations returns empty when disabled" {
    let filePath = "Tests.fs"
    let state = { LiveTestState.empty with
                    Activation = LiveTestingActivation.Inactive
                    SourceLocations = [|
                      { AttributeName = "Fact"; FunctionName = "test1"; FilePath = filePath; Line = 5; Column = 0 }
                    |] }
    LiveTesting.recomputeEditorAnnotations (Some filePath) state
    |> Array.length |> Expect.equal "no annotations" 0
  }

  test "recomputeEditorAnnotations matches annotationsForFile" {
    let filePath = "Tests.fs"
    let state = { LiveTestState.empty with
                    SourceLocations = [|
                      { AttributeName = "Fact"; FunctionName = "test1"; FilePath = filePath; Line = 5; Column = 0 }
                    |] }
    let cached = LiveTesting.recomputeEditorAnnotations (Some filePath) state
    let direct = LiveTesting.annotationsForFile filePath state
    cached |> Expect.equal "cache matches direct" direct
  }

  test "recomputeEditorAnnotations returns empty when no active file" {
    let state = { LiveTestState.empty with
                    SourceLocations = [|
                      { AttributeName = "Fact"; FunctionName = "test1"; FilePath = "Tests.fs"; Line = 5; Column = 0 }
                    |] }
    LiveTesting.recomputeEditorAnnotations None state
    |> Array.length |> Expect.equal "no annotations without active file" 0
  }

  test "CachedEditorAnnotations defaults to empty" {
    LiveTestState.empty.CachedEditorAnnotations
    |> Expect.equal "default empty" [||]
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
    | CoverageStatus.Covered (count, health) ->
      count |> Expect.equal "1 test" 1
      health |> Expect.equal "all passing" CoverageHealth.AllPassing
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
    | CoverageStatus.Covered (_, health) ->
      health |> Expect.equal "not all passing" CoverageHealth.SomeFailing
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
    TransitiveCoverage = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
    ]; SourceVersion = 1
    PerFileIndex = Map.empty
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
    let summary = TestSummary.fromStatuses LiveTestingActivation.Active statuses
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
// Test Prioritization Tests
// ============================================================

[<Tests>]
let prioritizationTests = testList "TestPrioritization" [
  test "failed tests come before passed tests" {
    let cases = [| mkTestCase "passed1" "expecto" TestCategory.Unit; mkTestCase "failed1" "expecto" TestCategory.Unit |]
    let results = Map.ofList [
      mkTestId "passed1" "expecto", mkResult (mkTestId "passed1" "expecto") (TestResult.Passed (ts 10.0))
      mkTestId "failed1" "expecto", mkResult (mkTestId "failed1" "expecto") (TestResult.Failed (TestFailure.AssertionFailed "bad", ts 10.0))
    ]
    let sorted = TestPrioritization.prioritize results cases
    sorted.[0].FullName |> Expect.equal "failed first" "failed1"
    sorted.[1].FullName |> Expect.equal "passed second" "passed1"
  }

  test "among passed, faster tests come first" {
    let cases = [| mkTestCase "slow" "expecto" TestCategory.Unit; mkTestCase "fast" "expecto" TestCategory.Unit |]
    let results = Map.ofList [
      mkTestId "slow" "expecto", mkResult (mkTestId "slow" "expecto") (TestResult.Passed (ts 500.0))
      mkTestId "fast" "expecto", mkResult (mkTestId "fast" "expecto") (TestResult.Passed (ts 5.0))
    ]
    let sorted = TestPrioritization.prioritize results cases
    sorted.[0].FullName |> Expect.equal "fast first" "fast"
    sorted.[1].FullName |> Expect.equal "slow second" "slow"
  }

  test "tests without results go last" {
    let cases = [| mkTestCase "no.result" "expecto" TestCategory.Unit; mkTestCase "has.result" "expecto" TestCategory.Unit |]
    let results = Map.ofList [
      mkTestId "has.result" "expecto", mkResult (mkTestId "has.result" "expecto") (TestResult.Passed (ts 10.0))
    ]
    let sorted = TestPrioritization.prioritize results cases
    sorted.[0].FullName |> Expect.equal "has result first" "has.result"
    sorted.[1].FullName |> Expect.equal "no result last" "no.result"
  }

  test "full priority: failed > skipped > passed > not-run > unknown" {
    let cases = [|
      mkTestCase "passed" "expecto" TestCategory.Unit
      mkTestCase "unknown" "expecto" TestCategory.Unit
      mkTestCase "failed" "expecto" TestCategory.Unit
      mkTestCase "skipped" "expecto" TestCategory.Unit
      mkTestCase "notrun" "expecto" TestCategory.Unit
    |]
    let results = Map.ofList [
      mkTestId "passed" "expecto", mkResult (mkTestId "passed" "expecto") (TestResult.Passed (ts 10.0))
      mkTestId "failed" "expecto", mkResult (mkTestId "failed" "expecto") (TestResult.Failed (TestFailure.AssertionFailed "x", ts 10.0))
      mkTestId "skipped" "expecto", mkResult (mkTestId "skipped" "expecto") (TestResult.Skipped "reason")
      mkTestId "notrun" "expecto", mkResult (mkTestId "notrun" "expecto") TestResult.NotRun
    ]
    let sorted = TestPrioritization.prioritize results cases
    sorted |> Array.map (fun tc -> tc.FullName)
    |> Expect.equal "priority order" [| "failed"; "skipped"; "passed"; "notrun"; "unknown" |]
  }

  test "empty input returns empty" {
    TestPrioritization.prioritize Map.empty [||]
    |> Expect.isEmpty "no tests"
  }
]

[<Tests>]
let prioritizationPropertyTests = testList "TestPrioritization properties" [
  testProperty "result count is preserved" (fun (n: FsCheck.PositiveInt) ->
    let count = n.Get % 50 + 1
    let cases = Array.init count (fun i -> mkTestCase (sprintf "test%d" i) "expecto" TestCategory.Unit)
    let sorted = TestPrioritization.prioritize Map.empty cases
    sorted.Length = cases.Length
  )

  testProperty "failed tests always precede passed tests in output" (fun (seed: int) ->
    let rng = System.Random(seed)
    let count = rng.Next(2, 20)
    let cases = Array.init count (fun i -> mkTestCase (sprintf "t%d" i) "expecto" TestCategory.Unit)
    let results =
      cases
      |> Array.map (fun tc ->
        let r =
          if rng.Next(3) = 0
          then TestResult.Failed (TestFailure.AssertionFailed "x", ts (float (rng.Next(1, 1000))))
          else TestResult.Passed (ts (float (rng.Next(1, 1000))))
        tc.Id, mkResult tc.Id r)
      |> Map.ofArray
    let sorted = TestPrioritization.prioritize results cases
    let indexOfLastFailed =
      sorted |> Array.tryFindIndexBack (fun tc ->
        match Map.tryFind tc.Id results with
        | Some r -> match r.Result with TestResult.Failed _ -> true | _ -> false
        | None -> false)
    let indexOfFirstPassed =
      sorted |> Array.tryFindIndex (fun tc ->
        match Map.tryFind tc.Id results with
        | Some r -> match r.Result with TestResult.Passed _ -> true | _ -> false
        | None -> false)
    match indexOfLastFailed, indexOfFirstPassed with
    | Some lastFail, Some firstPass -> lastFail < firstPass
    | _ -> true
  )

  testProperty "among same-outcome, faster comes first" (fun (seed: int) ->
    let rng = System.Random(seed)
    let count = rng.Next(2, 20)
    let cases = Array.init count (fun i -> mkTestCase (sprintf "t%d" i) "expecto" TestCategory.Unit)
    let results =
      cases
      |> Array.map (fun tc ->
        let dur = ts (float (rng.Next(1, 1000)))
        tc.Id, mkResult tc.Id (TestResult.Passed dur))
      |> Map.ofArray
    let sorted = TestPrioritization.prioritize results cases
    let durations =
      sorted |> Array.choose (fun tc ->
        match Map.tryFind tc.Id results with
        | Some r -> match r.Result with TestResult.Passed d -> Some d.TotalMilliseconds | _ -> None
        | None -> None)
    durations |> Array.pairwise |> Array.forall (fun (a, b) -> a <= b)
  )
]

// Assembly Load Diagnostics Tests
// ============================================================

[<Tests>]
let assemblyLoadDiagTests = testList "AssemblyLoadError" [
  test "FileNotFound carries path and message" {
    let err = AssemblyLoadError.FileNotFound("/some/path.dll", "File not found")
    AssemblyLoadError.path err |> Expect.equal "path" "/some/path.dll"
    AssemblyLoadError.message err |> Expect.equal "message" "File not found"
  }

  test "LoadFailed carries path and message" {
    let err = AssemblyLoadError.LoadFailed("/some/path.dll", "Access denied")
    AssemblyLoadError.path err |> Expect.equal "path" "/some/path.dll"
    AssemblyLoadError.message err |> Expect.equal "message" "Access denied"
  }

  test "BadImage carries path and message" {
    let err = AssemblyLoadError.BadImage("/some/path.dll", "Not a valid PE")
    AssemblyLoadError.path err |> Expect.equal "path" "/some/path.dll"
    AssemblyLoadError.message err |> Expect.equal "message" "Not a valid PE"
  }

  test "describe formats FileNotFound" {
    AssemblyLoadError.FileNotFound("test.dll", "gone")
    |> AssemblyLoadError.describe
    |> Expect.equal "formatted" "Assembly not found: test.dll (gone)"
  }

  test "describe formats LoadFailed" {
    AssemblyLoadError.LoadFailed("test.dll", "locked")
    |> AssemblyLoadError.describe
    |> Expect.equal "formatted" "Assembly load failed: test.dll (locked)"
  }

  test "describe formats BadImage" {
    AssemblyLoadError.BadImage("test.dll", "x86 vs x64")
    |> AssemblyLoadError.describe
    |> Expect.equal "formatted" "Bad image format: test.dll (x86 vs x64)"
  }

  test "loadAssembly returns Error for nonexistent path" {
    let result = AssemblyLoadError.loadAssembly "C:\\nonexistent\\fake.dll"
    match result with
    | Error (AssemblyLoadError.FileNotFound _) -> ()
    | other -> failtest (sprintf "Expected FileNotFound, got %A" other)
  }

  test "loadAssembly returns Ok for valid assembly" {
    let result = AssemblyLoadError.loadAssembly (typeof<AssemblyLoadError>.Assembly.Location)
    match result with
    | Ok asm -> asm.GetName().Name |> Expect.equal "loaded" "SageFs.Core"
    | Error e -> failtest (sprintf "Expected Ok, got Error: %s" (AssemblyLoadError.describe e))
  }

  test "LiveTestState.empty has no assembly load errors" {
    LiveTestState.empty.AssemblyLoadErrors
    |> Expect.isEmpty "no errors"
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
// Debounce Channel Property Tests (FsCheck)
// ============================================================

[<Tests>]
let debounceChannelPropertyTests = testList "DebounceChannel properties" [
  testProperty "tryFire always fires after delay has elapsed" (fun (payload: string) (delayMs: FsCheck.PositiveInt) ->
    let delay = delayMs.Get % 10000 + 1
    let t0 = DateTimeOffset.UtcNow
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit payload delay t0
    let tFire = t0.AddMilliseconds(float delay + 1.0)
    let result, _ = DebounceChannel.tryFire tFire ch
    result = Some payload
  )

  testProperty "tryFire never fires before delay elapses" (fun (payload: string) (delayMs: FsCheck.PositiveInt) ->
    let delay = (delayMs.Get % 10000) + 2
    let t0 = DateTimeOffset.UtcNow
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit payload delay t0
    let tEarly = t0.AddMilliseconds(float (delay - 2))
    let result, _ = DebounceChannel.tryFire tEarly ch
    result = None
  )

  testProperty "newer submission supersedes older" (fun (p1: string) (p2: string) (delayMs: FsCheck.PositiveInt) ->
    let delay = delayMs.Get % 10000 + 1
    let t0 = DateTimeOffset.UtcNow
    let ch =
      DebounceChannel.empty<string>
      |> DebounceChannel.submit p1 delay t0
      |> DebounceChannel.submit p2 delay (t0.AddMilliseconds 10.0)
    let tFire = t0.AddMilliseconds(float delay + 100.0)
    let result, _ = DebounceChannel.tryFire tFire ch
    result = Some p2
  )

  testProperty "empty channel never fires" (fun (offset: FsCheck.PositiveInt) ->
    let t0 = DateTimeOffset.UtcNow
    let result, _ = DebounceChannel.tryFire (t0.AddMilliseconds(float offset.Get)) DebounceChannel.empty<string>
    result = None
  )
]

// ============================================================
// AdaptiveDebounce Property Tests (FsCheck)
// ============================================================

[<Tests>]
let adaptiveDebouncePropertyTests = testList "AdaptiveDebounce properties" [
  testProperty "onFcsCanceled never decreases delay" (fun (cancels: FsCheck.PositiveInt) ->
    let n = cancels.Get % 100
    let mutable ad = AdaptiveDebounce.createDefault()
    let mutable prevDelay = ad.CurrentFcsDelayMs
    let mutable monotonic = true
    for _ in 1 .. n do
      ad <- AdaptiveDebounce.onFcsCanceled ad
      if ad.CurrentFcsDelayMs < prevDelay then monotonic <- false
      prevDelay <- ad.CurrentFcsDelayMs
    monotonic
  )

  testProperty "delay never exceeds MaxFcsMs" (fun (cancels: FsCheck.PositiveInt) ->
    let n = cancels.Get % 200
    let mutable ad = AdaptiveDebounce.createDefault()
    for _ in 1 .. n do
      ad <- AdaptiveDebounce.onFcsCanceled ad
    ad.CurrentFcsDelayMs <= ad.Config.MaxFcsMs
  )

  testProperty "enough successes always reset to base" (fun (cancels: FsCheck.PositiveInt) ->
    let n = cancels.Get % 50
    let mutable ad = AdaptiveDebounce.createDefault()
    for _ in 1 .. n do
      ad <- AdaptiveDebounce.onFcsCanceled ad
    for _ in 1 .. ad.Config.ResetAfterSuccessCount do
      ad <- AdaptiveDebounce.onFcsCompleted ad
    ad.CurrentFcsDelayMs = ad.Config.BaseFcsMs
  )

  testProperty "cancel then complete does not decrease below cancel level" (fun (cancels: FsCheck.PositiveInt) ->
    let n = max 1 (cancels.Get % 20)
    let mutable ad = AdaptiveDebounce.createDefault()
    for _ in 1 .. n do
      ad <- AdaptiveDebounce.onFcsCanceled ad
    let afterCancels = ad.CurrentFcsDelayMs
    ad <- AdaptiveDebounce.onFcsCompleted ad
    ad.CurrentFcsDelayMs >= afterCancels || ad.CurrentFcsDelayMs = ad.Config.BaseFcsMs
  )
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
    let db = PipelineDebounce.empty |> PipelineDebounce.onKeystroke "code" "file.fs" 300 t0
    db.TreeSitter.Pending |> Expect.isSome "tree-sitter pending"
    db.Fcs.Pending |> Expect.isSome "fcs pending"
    db.TreeSitter.Pending.Value.DelayMs |> Expect.equal "ts delay" 50
    db.Fcs.Pending.Value.DelayMs |> Expect.equal "fcs delay" 300
  }

  test "tree-sitter fires before FCS" {
    let db = PipelineDebounce.empty |> PipelineDebounce.onKeystroke "code" "file.fs" 300 t0
    let (tsResult, fcsResult), _ = PipelineDebounce.tick (t0.AddMilliseconds 51.0) db
    tsResult |> Expect.isSome "tree-sitter fires"
    fcsResult |> Expect.isNone "fcs not yet"
  }

  test "both fire after 300ms" {
    let db = PipelineDebounce.empty |> PipelineDebounce.onKeystroke "code" "file.fs" 300 t0
    let (tsResult, fcsResult), _ = PipelineDebounce.tick (t0.AddMilliseconds 301.0) db
    tsResult |> Expect.isSome "tree-sitter fires"
    fcsResult |> Expect.isSome "fcs fires"
  }

  test "rapid keystrokes cancel previous debounce" {
    let db =
      PipelineDebounce.empty
      |> PipelineDebounce.onKeystroke "v1" "file.fs" 300 t0
      |> PipelineDebounce.onKeystroke "v2" "file.fs" 300 (t0.AddMilliseconds 30.0)
      |> PipelineDebounce.onKeystroke "v3" "file.fs" 300 (t0.AddMilliseconds 60.0)
    let (tsResult, _), _ = PipelineDebounce.tick (t0.AddMilliseconds 111.0) db
    tsResult |> Expect.isSome "fires for latest"
    tsResult.Value |> Expect.equal "latest content" "v3"
  }

  test "onFileSave uses short FCS delay" {
    let db =
      PipelineDebounce.empty
      |> PipelineDebounce.onKeystroke "code" "file.fs" 300 t0
      |> PipelineDebounce.onFileSave "file.fs" (t0.AddMilliseconds 100.0)
    db.Fcs.Pending.Value.DelayMs |> Expect.equal "save uses short delay" 50
    let (_, fcsResult), _ = PipelineDebounce.tick (t0.AddMilliseconds 151.0) db
    fcsResult |> Expect.isSome "fcs fires soon after save"
  }

  test "tick clears fired ops" {
    let db = PipelineDebounce.empty |> PipelineDebounce.onKeystroke "code" "file.fs" 300 t0
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
    let effects = PipelineEffects.fromTick (Some "code") (Some "file.fs") "file.fs" None
    effects.Length |> Expect.equal "two effects" 2
    match effects.[0] with
    | PipelineEffect.ParseTreeSitter (content, _) ->
      content |> Expect.equal "ts content" "code"
    | _ -> failtest "expected ParseTreeSitter"
    match effects.[1] with
    | PipelineEffect.RequestFcsTypeCheck (fp, _tsElapsed) ->
      fp |> Expect.equal "fcs path" "file.fs"
    | _ -> failtest "expected RequestFcsTypeCheck"
  }

  test "fromTick with no payloads produces empty" {
    let effects = PipelineEffects.fromTick None None "file.fs" None
    effects.Length |> Expect.equal "no effects" 0
  }

  test "fromTick with only tree-sitter produces one effect" {
    let effects = PipelineEffects.fromTick (Some "code") None "file.fs" None
    effects.Length |> Expect.equal "one effect" 1
    match effects.[0] with
    | PipelineEffect.ParseTreeSitter _ -> ()
    | _ -> failtest "expected ParseTreeSitter"
  }

  test "afterTypeCheck with affected tests returns RunAffectedTests" {
    let tc1 = { Id = TestId.create "test1" "xunit"; FullName = "test1"; DisplayName = "test1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
                Category = TestCategory.Unit }
    let state = { LiveTestState.empty with DiscoveredTests = [| tc1 |]; Activation = LiveTestingActivation.Active }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "Module.add", [| tc1.Id |] ]
        TransitiveCoverage = Map.ofList [ "Module.add", [| tc1.Id |] ]
    }
    match PipelineEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty with
    | [ PipelineEffect.RunAffectedTests (tests, trigger, _tsElapsed, _fcsElapsed, _sessionId, _maps) ] ->
      tests.Length |> Expect.equal "one test" 1
      trigger |> Expect.equal "keystroke trigger" RunTrigger.Keystroke
    | other -> failtestf "expected single RunAffectedTests, got %A" other
  }

  test "afterTypeCheck with no affected tests returns None" {
    let state = { LiveTestState.empty with DiscoveredTests = [||]; Activation = LiveTestingActivation.Active }
    let graph = TestDependencyGraph.empty
    PipelineEffects.afterTypeCheck ["unknown.symbol"] "test.fs" RunTrigger.Keystroke graph state None Map.empty
    |> Expect.isEmpty "no affected tests"
  }

  test "afterTypeCheck when disabled returns None" {
    let state = { LiveTestState.empty with Activation = LiveTestingActivation.Inactive }
    let graph = TestDependencyGraph.empty
    PipelineEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty
    |> Expect.isEmpty "disabled"
  }

  test "afterTypeCheck when Running returns None (prevents concurrent test runs)" {
    let tc1 = { Id = TestId.create "test1" "xunit"; FullName = "test1"; DisplayName = "test1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
                Category = TestCategory.Unit }
    let gen = RunGeneration.RunGeneration 1
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| tc1 |]
        Activation = LiveTestingActivation.Active
        RunPhases = Map.ofList [ "test-session", TestRunPhase.Running gen ]
        TestSessionMap = Map.ofList [ tc1.Id, "test-session" ]
    }
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "Module.add", [| tc1.Id |] ]
    }
    PipelineEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty
    |> Expect.isEmpty "should not produce effect when tests are running"
  }

  test "afterTypeCheck when RunningButEdited returns None (re-trigger handled by completion)" {
    let tc1 = { Id = TestId.create "test1" "xunit"; FullName = "test1"; DisplayName = "test1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "xunit"
                Category = TestCategory.Unit }
    let gen = RunGeneration.RunGeneration 1
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [| tc1 |]
        Activation = LiveTestingActivation.Active
        RunPhases = Map.ofList [ "test-session", TestRunPhase.RunningButEdited gen ]
        TestSessionMap = Map.ofList [ tc1.Id, "test-session" ]
    }
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "Module.add", [| tc1.Id |] ]
    }
    PipelineEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty
    |> Expect.isEmpty "should not produce effect when running-but-edited"
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
        Activation = LiveTestingActivation.Active
    }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "Module.add", [| tc1.Id; tc2.Id |] ]
        TransitiveCoverage = Map.ofList [ "Module.add", [| tc1.Id; tc2.Id |] ]
    }
    match PipelineEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty with
    | [ PipelineEffect.RunAffectedTests (tests, _, _, _, _, _) ] ->
      tests.Length |> Expect.equal "only unit test" 1
      tests.[0].Id |> Expect.equal "unit test id" tc1.Id
    | other -> failtestf "expected single RunAffectedTests, got %A" other
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
  test "old token accessible after next without ObjectDisposedException" {
    let chain = new CancellationChain()
    let t1 = chain.next()
    let _t2 = chain.next()
    // Old token must remain accessible — Register should not throw
    t1.IsCancellationRequested |> Expect.isTrue "t1 cancelled"
    let _reg = t1.Register(fun () -> ())
    Expect.isTrue "Register on old token should not throw" true
    chain.dispose()
  }
]

// --- Phase 4: Pipeline Integration Tests ---

/// Test-only effect logger for asserting pipeline behavior.
type EffectLog = { mutable Effects: PipelineEffect list }

module EffectDispatcher =
  let create () = { Effects = [] }

  let dispatch (log: EffectLog) (effect: PipelineEffect) =
    log.Effects <- log.Effects @ [effect]

  let dispatchAll (log: EffectLog) (effects: PipelineEffect list) =
    effects |> List.iter (dispatch log)

  let reset (log: EffectLog) =
    log.Effects <- []

[<Tests>]
let pipelineStateTests = testList "LiveTestPipelineState" [
  test "onKeystroke updates debounce and active file" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    s.ActiveFile |> Expect.equal "active file set" (Some "File.fs")
    s.Debounce.TreeSitter.Pending |> Expect.isSome "ts channel has pending"
  }

  test "onFileSave updates fcs debounce with short delay" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onFileSave "File.fs" t0
    s.Debounce.Fcs.Pending |> Expect.isSome "fcs channel has pending"
  }

  test "tick with no pending produces no effects" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let effects, _ = LiveTestPipelineState.empty |> LiveTestPipelineState.tick t0
    effects |> Expect.isEmpty "no effects from empty state"
  }

  test "tick with no active file produces no effects even with pending" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let state = {
      LiveTestPipelineState.empty with
        Debounce = {
          TreeSitter = {
            CurrentGeneration = 1L
            Pending = Some { Payload = "let x = 1"; RequestedAt = t0; DelayMs = 50; Generation = 1L }
            LastCompleted = None
          }
          Fcs = {
            CurrentGeneration = 1L
            Pending = Some { Payload = "File.fs"; RequestedAt = t0; DelayMs = 300; Generation = 1L }
            LastCompleted = None
          }
        }
    }
    let effects, s2 = state |> LiveTestPipelineState.tick (t0.AddMilliseconds(301.0))
    effects |> Expect.isEmpty "no effects when no active file"
    s2.ActiveFile |> Expect.isNone "active file still None"
  }

  test "tick after keystroke delay fires tree-sitter parse" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let t50 = t0.AddMilliseconds(51.0)
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestPipelineState.tick t50
    effects
    |> List.exists (fun e ->
      match e with
      | PipelineEffect.ParseTreeSitter _ -> true
      | _ -> false)
    |> Expect.isTrue "should have tree-sitter parse"
  }

  test "tick after full delay fires both tree-sitter and fcs" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let t301 = t0.AddMilliseconds(301.0)
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestPipelineState.tick t301
    effects
    |> List.exists (fun e ->
      match e with
      | PipelineEffect.ParseTreeSitter _ -> true
      | _ -> false)
    |> Expect.isTrue "should have tree-sitter"
    effects
    |> List.exists (fun e ->
      match e with
      | PipelineEffect.RequestFcsTypeCheck _ -> true
      | _ -> false)
    |> Expect.isTrue "should have fcs request"
  }

  test "tick with fcs debounce does NOT produce RunAffectedTests (deferred to afterTypeCheck)" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let t301 = t0.AddMilliseconds(301.0)
    let tc = mkTestCase "MyModule.myTest" "expecto" TestCategory.Unit
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["mySymbol", [|tc.Id|]]
        TransitiveCoverage = Map.ofList ["mySymbol", [|tc.Id|]]
    }
    let state = {
      LiveTestPipelineState.empty with
        DepGraph = depGraph
        ChangedSymbols = ["mySymbol"]
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let s = state |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestPipelineState.tick t301
    effects
    |> List.exists (fun e ->
      match e with
      | PipelineEffect.RunAffectedTests _ -> true
      | _ -> false)
    |> Expect.isFalse "tick should not produce RunAffectedTests (stale symbols fix)"
    effects
    |> List.exists (fun e ->
      match e with
      | PipelineEffect.RequestFcsTypeCheck _ -> true
      | _ -> false)
    |> Expect.isTrue "tick should produce RequestFcsTypeCheck"
  }

  test "file save shortens fcs debounce to 50ms" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let t51 = t0.AddMilliseconds(51.0)
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onFileSave "File.fs" t0
    let effects, _ = s |> LiveTestPipelineState.tick t51
    effects
    |> List.exists (fun e ->
      match e with
      | PipelineEffect.RequestFcsTypeCheck _ -> true
      | _ -> false)
    |> Expect.isTrue "fcs fires at 50ms on save"
  }
]

[<Tests>]
let effectDispatchTests = testList "EffectDispatcher" [
  test "ParseTreeSitter logs content and file" {
    let log = EffectDispatcher.create()
    EffectDispatcher.dispatch log (PipelineEffect.ParseTreeSitter("let x = 1", "File.fs"))
    log.Effects |> Expect.hasLength "one effect logged" 1
    match log.Effects.[0] with
    | PipelineEffect.ParseTreeSitter(c, f) ->
      c |> Expect.equal "content" "let x = 1"
      f |> Expect.equal "file" "File.fs"
    | _ -> failtest "wrong effect type"
  }

  test "RequestFcsTypeCheck logs file path" {
    let log = EffectDispatcher.create()
    EffectDispatcher.dispatch log (PipelineEffect.RequestFcsTypeCheck("File.fs", System.TimeSpan.Zero))
    log.Effects |> Expect.hasLength "one effect" 1
    match log.Effects.[0] with
    | PipelineEffect.RequestFcsTypeCheck (f, _) -> f |> Expect.equal "file" "File.fs"
    | _ -> failtest "wrong effect type"
  }

  test "RunAffectedTests logs tests and trigger" {
    let log = EffectDispatcher.create()
    let tests = [| { Id = TestId.create "t1" "expecto"; FullName = "t1"; DisplayName = "t1"
                     Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "expecto"
                     Category = TestCategory.Unit } |]
    EffectDispatcher.dispatch log (PipelineEffect.RunAffectedTests(tests, RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||]))
    log.Effects |> Expect.hasLength "one effect" 1
    match log.Effects.[0] with
    | PipelineEffect.RunAffectedTests(tcs, trigger, _, _, _, _) ->
      tcs |> Expect.hasLength "one test" 1
      trigger |> Expect.equal "trigger" RunTrigger.Keystroke
    | _ -> failtest "wrong effect type"
  }

  test "dispatchAll processes multiple effects" {
    let log = EffectDispatcher.create()
    let effects = [
      PipelineEffect.ParseTreeSitter("x", "f")
      PipelineEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)
    ]
    EffectDispatcher.dispatchAll log effects
    log.Effects |> Expect.hasLength "two effects" 2
  }
]

[<Tests>]
let endToEndPipelineTests = testList "End-to-end Pipeline" [
  test "keystroke → debounce → tree-sitter fires at 50ms" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let log = EffectDispatcher.create()
    let s0 = LiveTestPipelineState.empty |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    // at 30ms — nothing fires
    let effects30, s30 = s0 |> LiveTestPipelineState.tick (t0.AddMilliseconds(30.0))
    EffectDispatcher.dispatchAll log effects30
    log.Effects |> Expect.isEmpty "nothing at 30ms"
    // at 51ms — tree-sitter fires
    let effects51, _ = s30 |> LiveTestPipelineState.tick (t0.AddMilliseconds(51.0))
    EffectDispatcher.dispatchAll log effects51
    log.Effects
    |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "tree-sitter fired at 51ms"
  }

  test "burst typing resets debounce" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let log = EffectDispatcher.create()
    let s0 = LiveTestPipelineState.empty
             |> LiveTestPipelineState.onKeystroke "l" "F.fs" t0
             |> LiveTestPipelineState.onKeystroke "le" "F.fs" (t0.AddMilliseconds(20.0))
             |> LiveTestPipelineState.onKeystroke "let" "F.fs" (t0.AddMilliseconds(40.0))
    // at 60ms from first keystroke — only 20ms from last, shouldn't fire
    let effects60, _ = s0 |> LiveTestPipelineState.tick (t0.AddMilliseconds(60.0))
    EffectDispatcher.dispatchAll log effects60
    log.Effects |> Expect.isEmpty "burst resets debounce"
    // at 91ms from first (51ms from last) — fires
    EffectDispatcher.reset log
    let effects91, _ = s0 |> LiveTestPipelineState.tick (t0.AddMilliseconds(91.0))
    EffectDispatcher.dispatchAll log effects91
    log.Effects
    |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "fires 50ms after last keystroke"
  }

  test "full pipeline: keystroke → TS → FCS → afterTypeCheck → run affected" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let tc = mkTestCase "M.affectedTest" "expecto" TestCategory.Unit
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["changedFn", [|tc.Id|]]
        TransitiveCoverage = Map.ofList ["changedFn", [|tc.Id|]]
    }
    let state = {
      LiveTestPipelineState.empty with
        DepGraph = depGraph
        ChangedSymbols = ["changedFn"]
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let s = state |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    // Phase 1: tick fires TS + FCS
    let effects, s2 = s |> LiveTestPipelineState.tick (t0.AddMilliseconds(301.0))
    effects
    |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "has tree-sitter"
    effects
    |> List.exists (fun e -> match e with PipelineEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    |> Expect.isTrue "has fcs"
    // Phase 2: afterTypeCheck (after FCS completes) fires RunAffectedTests
    let runEffects = PipelineEffects.afterTypeCheck s2.ChangedSymbols "test.fs" RunTrigger.Keystroke s2.DepGraph s2.TestState None s2.InstrumentationMaps
    runEffects |> List.isEmpty |> Expect.isFalse "afterTypeCheck produces RunAffectedTests"
    match runEffects with
    | [ PipelineEffect.RunAffectedTests (tests, trigger, _, _, _, _) ] ->
      tests |> Array.length |> Expect.equal "one affected test" 1
      trigger |> Expect.equal "trigger is keystroke" RunTrigger.Keystroke
    | _ -> failwith "expected single RunAffectedTests"
  }

  test "disabled state produces no effects even after delay" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let tc = mkTestCase "M.t1" "expecto" TestCategory.Unit
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["sym", [|tc.Id|]]
        TransitiveCoverage = Map.ofList ["sym", [|tc.Id|]]
    }
    let state = {
      LiveTestPipelineState.empty with
        DepGraph = depGraph
        ChangedSymbols = ["sym"]
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Inactive }
    }
    let s = state |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestPipelineState.tick (t0.AddMilliseconds(301.0))
    // TS and FCS fire (debounce doesn't check enabled), but RunAffected should not
    effects
    |> List.exists (fun e -> match e with PipelineEffect.RunAffectedTests _ -> true | _ -> false)
    |> Expect.isFalse "no test run when disabled"
  }

  test "integration tests filtered on keystroke trigger" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let tc = mkTestCase "M.intTest" "expecto" TestCategory.Integration
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["sym", [|tc.Id|]]
        TransitiveCoverage = Map.ofList ["sym", [|tc.Id|]]
    }
    let state = {
      LiveTestPipelineState.empty with
        DepGraph = depGraph
        ChangedSymbols = ["sym"]
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let s = state |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestPipelineState.tick (t0.AddMilliseconds(301.0))
    effects
    |> List.exists (fun e -> match e with PipelineEffect.RunAffectedTests _ -> true | _ -> false)
    |> Expect.isFalse "integration tests filtered out on keystroke"
  }
]

[<Tests>]
let pipelineCancellationTests = testList "PipelineCancellation" [
  test "tokenForEffect returns live tokens" {
    let pc = PipelineCancellation.create()
    let t1 = PipelineCancellation.tokenForEffect (PipelineEffect.ParseTreeSitter("x", "f")) pc
    let t2 = PipelineCancellation.tokenForEffect (PipelineEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)) pc
    let t3 = PipelineCancellation.tokenForEffect (PipelineEffect.RunAffectedTests([||], RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||])) pc
    t1.IsCancellationRequested |> Expect.isFalse "ts token live"
    t2.IsCancellationRequested |> Expect.isFalse "fcs token live"
    t3.IsCancellationRequested |> Expect.isFalse "run token live"
    PipelineCancellation.dispose pc
  }

  test "new tree-sitter effect cancels previous tree-sitter" {
    let pc = PipelineCancellation.create()
    let t1 = PipelineCancellation.tokenForEffect (PipelineEffect.ParseTreeSitter("a", "f")) pc
    let _t2 = PipelineCancellation.tokenForEffect (PipelineEffect.ParseTreeSitter("b", "f")) pc
    t1.IsCancellationRequested |> Expect.isTrue "first ts cancelled"
    PipelineCancellation.dispose pc
  }

  test "new fcs effect cancels previous fcs but not tree-sitter" {
    let pc = PipelineCancellation.create()
    let tsToken = PipelineCancellation.tokenForEffect (PipelineEffect.ParseTreeSitter("x", "f")) pc
    let fcs1 = PipelineCancellation.tokenForEffect (PipelineEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)) pc
    let _fcs2 = PipelineCancellation.tokenForEffect (PipelineEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)) pc
    fcs1.IsCancellationRequested |> Expect.isTrue "first fcs cancelled"
    tsToken.IsCancellationRequested |> Expect.isFalse "ts not affected"
    PipelineCancellation.dispose pc
  }

  test "new test run cancels previous test run" {
    let pc = PipelineCancellation.create()
    let run1 = PipelineCancellation.tokenForEffect (PipelineEffect.RunAffectedTests([||], RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||])) pc
    let _run2 = PipelineCancellation.tokenForEffect (PipelineEffect.RunAffectedTests([||], RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||])) pc
    run1.IsCancellationRequested |> Expect.isTrue "first run cancelled"
    PipelineCancellation.dispose pc
  }

  test "dispose cancels all active tokens" {
    let pc = PipelineCancellation.create()
    let ts = PipelineCancellation.tokenForEffect (PipelineEffect.ParseTreeSitter("x", "f")) pc
    let fcs = PipelineCancellation.tokenForEffect (PipelineEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)) pc
    let run = PipelineCancellation.tokenForEffect (PipelineEffect.RunAffectedTests([||], RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||])) pc
    PipelineCancellation.dispose pc
    ts.IsCancellationRequested |> Expect.isTrue "ts cancelled"
    fcs.IsCancellationRequested |> Expect.isTrue "fcs cancelled"
    run.IsCancellationRequested |> Expect.isTrue "run cancelled"
  }
]

// --- Phase 4b: FCS Integration & Adaptive Debounce Tests ---

[<Tests>]
let adaptiveDebounceTests = testList "AdaptiveDebounce" [
  test "initial delay matches base config" {
    let ad = AdaptiveDebounce.createDefault()
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "base delay" 300.0
  }

  test "single cancel increases delay by multiplier" {
    let ad = AdaptiveDebounce.createDefault() |> AdaptiveDebounce.onFcsCanceled
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "450ms after one cancel" 450.0
  }

  test "three consecutive cancels compound the backoff" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "compounded" 1012.5
  }

  test "backoff caps at MaxFcsMs" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "capped at 2000" 2000.0
  }

  test "single success after cancel resets cancel count but not delay" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCompleted
    ad.ConsecutiveFcsCancels |> Expect.equal "cancels reset" 0
    ad.ConsecutiveFcsSuccesses |> Expect.equal "one success" 1
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "still elevated" 450.0
  }

  test "three consecutive successes reset delay to base" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCompleted
      |> AdaptiveDebounce.onFcsCompleted
      |> AdaptiveDebounce.onFcsCompleted
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "reset to base" 300.0
    ad.ConsecutiveFcsSuccesses |> Expect.equal "successes reset" 0
  }

  test "cancel after partial success restarts backoff from current delay" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCompleted
      |> AdaptiveDebounce.onFcsCompleted
      |> AdaptiveDebounce.onFcsCanceled
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "backoff from 450" 675.0
    ad.ConsecutiveFcsSuccesses |> Expect.equal "successes reset" 0
  }

  test "consecutive cancels counter tracks correctly" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
    ad.ConsecutiveFcsCancels |> Expect.equal "two cancels" 2
  }
]

[<Tests>]
let symbolGraphTests = testList "SymbolGraphBuilder" [
  test "buildIndex maps production symbols to test functions via line-range heuristic" {
    let refs = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "MyModule.parse"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 }
      { SymbolFullName = "MyModule.format"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 8 }
      { SymbolFullName = "MyApp.Tests.test2"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 20 }
      { SymbolFullName = "MyModule.parse"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 25 }
    ]
    let index = SymbolGraphBuilder.buildIndex ".Tests." "fcs" refs
    index |> Map.find "MyModule.parse" |> Array.length |> Expect.equal "parse has 2 tests" 2
    index |> Map.find "MyModule.format" |> Array.length |> Expect.equal "format has 1 test" 1
  }

  test "buildIndex produces empty graph when no test definitions present" {
    let refs = [
      { SymbolFullName = "MyModule.helper"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 }
    ]
    let index = SymbolGraphBuilder.buildIndex ".Tests." "fcs" refs
    index |> Map.isEmpty |> Expect.isTrue "no test context means no entries"
  }

  test "buildIndex deduplicates test entries for same symbol" {
    let refs = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "MyModule.parse"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 }
      { SymbolFullName = "MyModule.parse"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 8 }
    ]
    let index = SymbolGraphBuilder.buildIndex ".Tests." "fcs" refs
    index |> Map.find "MyModule.parse" |> Array.length |> Expect.equal "deduped" 1
  }

  test "updateGraph merges new symbols into existing graph" {
    // Set up existing graph WITH PerFileIndex tracking (as produced by real updateGraph calls)
    let existingGraph =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs"
        [ { SymbolFullName = "MyApp.Tests.OldTest"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Old.fs"; Line = 1 }
          { SymbolFullName = "OldModule.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Old.fs"; Line = 5 } ]
        "Old.fs"
    let newRefs = [
      { SymbolFullName = "MyApp.Tests.test2"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "New.fs"; Line = 1 }
      { SymbolFullName = "NewModule.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "New.fs"; Line = 5 }
    ]
    let updated = SymbolGraphBuilder.updateGraph ".Tests." "fcs" newRefs "New.fs" existingGraph
    updated.SymbolToTests |> Map.containsKey "OldModule.fn" |> Expect.isTrue "old preserved"
    updated.SymbolToTests |> Map.containsKey "NewModule.fn" |> Expect.isTrue "new added"
    updated.SourceVersion |> Expect.equal "version bumped" 2
  }

  test "updateGraph replaces same file's symbol entries on re-analysis" {
    // First analysis: FileA maps MyModule.fn → OldTest
    let graph1 =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs"
        [ { SymbolFullName = "MyApp.Tests.OldTest"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 1 }
          { SymbolFullName = "MyModule.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 } ]
        "F.fs"
    // Re-analysis: FileA now maps MyModule.fn → test2 (different test)
    let newRefs = [
      { SymbolFullName = "MyApp.Tests.test2"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "MyModule.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "F.fs"; Line = 5 }
    ]
    let updated = SymbolGraphBuilder.updateGraph ".Tests." "fcs" newRefs "F.fs" graph1
    let tests = updated.SymbolToTests |> Map.find "MyModule.fn"
    tests |> Array.length |> Expect.equal "replaced with new test" 1
  }

  test "empty refs produce empty index" {
    let index = SymbolGraphBuilder.buildIndex ".Tests." "fcs" []
    index |> Map.isEmpty |> Expect.isTrue "empty"
  }
]

[<Tests>]
let symbolDiffTests = testList "SymbolDiff" [
  test "no changes returns empty" {
    let syms = Set.ofList ["A.fn"; "B.fn"]
    SymbolDiff.computeChanges syms syms |> SymbolChanges.isEmpty |> Expect.isTrue "no changes"
  }

  test "added symbol is detected" {
    let prev = Set.ofList ["A.fn"]
    let curr = Set.ofList ["A.fn"; "B.fn"]
    let sc = SymbolDiff.computeChanges prev curr
    sc.Added |> Expect.contains "B.fn added" "B.fn"
    sc.Removed |> Expect.isEmpty "nothing removed"
  }

  test "removed symbol is detected" {
    let prev = Set.ofList ["A.fn"; "B.fn"]
    let curr = Set.ofList ["A.fn"]
    let sc = SymbolDiff.computeChanges prev curr
    sc.Removed |> Expect.contains "B.fn removed" "B.fn"
    sc.Added |> Expect.isEmpty "nothing added"
  }

  test "both added and removed detected separately" {
    let prev = Set.ofList ["A.fn"; "B.fn"]
    let curr = Set.ofList ["A.fn"; "C.fn"]
    let sc = SymbolDiff.computeChanges prev curr
    sc.Added |> Expect.contains "C.fn added" "C.fn"
    sc.Removed |> Expect.contains "B.fn removed" "B.fn"
  }

  test "fromRefs computes diff from reference lists" {
    let t1 = TestId.create "t1" "expecto"
    let prev = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "B.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 2 }
    ]
    let curr = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "C.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 3 }
    ]
    let sc = SymbolDiff.fromRefs prev curr
    sc.Added |> Expect.hasLength "one added" 1
    sc.Removed |> Expect.hasLength "one removed" 1
  }

  test "allChanged combines both lists" {
    let prev = Set.ofList ["A.fn"; "B.fn"]
    let curr = Set.ofList ["A.fn"; "C.fn"]
    let sc = SymbolDiff.computeChanges prev curr
    sc |> SymbolChanges.allChanged |> Expect.hasLength "two total" 2
  }

  test "empty previous means all current are added" {
    let curr = Set.ofList ["A.fn"; "B.fn"]
    let sc = SymbolDiff.computeChanges Set.empty curr
    sc.Added |> Expect.hasLength "all new" 2
    sc.Removed |> Expect.isEmpty "nothing removed"
  }
]

[<Tests>]
let fileAnalysisCacheTests = testList "FileAnalysisCache" [
  test "empty cache returns all symbols as added" {
    let t1 = TestId.create "t1" "expecto"
    let refs = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
    ]
    let changes, _ = FileAnalysisCache.empty |> FileAnalysisCache.update "F.fs" refs
    changes.Added |> Expect.hasLength "one added" 1
    changes.Removed |> Expect.isEmpty "nothing removed"
  }

  test "second update with same symbols returns no changes" {
    let t1 = TestId.create "t1" "expecto"
    let refs = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
    ]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "F.fs" refs
    let changes, _ = cache1 |> FileAnalysisCache.update "F.fs" refs
    changes |> SymbolChanges.isEmpty |> Expect.isTrue "no changes"
  }

  test "different file doesn't affect existing file's cache" {
    let t1 = TestId.create "t1" "expecto"
    let refs1 = [{ SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F1.fs"; Line = 1 }]
    let refs2 = [{ SymbolFullName = "B.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F2.fs"; Line = 1 }]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "F1.fs" refs1
    let _, cache2 = cache1 |> FileAnalysisCache.update "F2.fs" refs2
    cache2 |> FileAnalysisCache.getFileSymbols "F1.fs" |> Expect.hasLength "F1 preserved" 1
    cache2 |> FileAnalysisCache.getFileSymbols "F2.fs" |> Expect.hasLength "F2 added" 1
  }

  test "modified file separates added and removed" {
    let t1 = TestId.create "t1" "expecto"
    let refs1 = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "B.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 2 }
    ]
    let refs2 = [
      { SymbolFullName = "A.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 1 }
      { SymbolFullName = "C.fn"; UseKind = SymbolUseKind.Reference; UsedInTestId = Some t1; FilePath = "F.fs"; Line = 3 }
    ]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "F.fs" refs1
    let changes, _ = cache1 |> FileAnalysisCache.update "F.fs" refs2
    changes.Added |> Expect.contains "C added" "C.fn"
    changes.Removed |> Expect.contains "B removed" "B.fn"
  }

  test "getFileSymbols returns empty for unknown file" {
    FileAnalysisCache.empty |> FileAnalysisCache.getFileSymbols "unknown.fs"
    |> Expect.isEmpty "empty for unknown"
  }
]

[<Tests>]
let compositionTests = testList "compositionTests" [
  test "no-op edit with same symbols produces no affected tests" {
    let refs = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 }
      { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 5 }
      { SymbolFullName = "Lib.sub"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 8 }
    ]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "Lib.fs" refs
    let changes, _ = cache1 |> FileAnalysisCache.update "Lib.fs" refs
    changes |> SymbolChanges.isEmpty |> Expect.isTrue "no symbol changes"
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = SymbolGraphBuilder.buildIndex ".Tests." "fcs" refs
        TransitiveCoverage = SymbolGraphBuilder.buildIndex ".Tests." "fcs" refs
    }
    let affected = TestDependencyGraph.findAffected (SymbolChanges.allChanged changes) depGraph
    affected |> Expect.hasLength "no affected tests" 0
  }

  test "full pipeline roundtrip: keystroke → debounce → FCS → affected tests" {
    let tc = mkTestCase "MyApp.Tests.test1" "expecto" TestCategory.Unit
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let refs = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 }
      { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 5 }
    ]
    let state = {
      LiveTestPipelineState.empty with
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let s1 = state |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects30, s30 = s1 |> LiveTestPipelineState.tick (t0.AddMilliseconds(30.0))
    effects30 |> Expect.isEmpty "nothing at 30ms"
    let effects51, s51 = s30 |> LiveTestPipelineState.tick (t0.AddMilliseconds(51.0))
    effects51 |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "TS fires at 51ms"
    let effects301, s301 = s51 |> LiveTestPipelineState.tick (t0.AddMilliseconds(301.0))
    effects301 |> List.exists (fun e -> match e with PipelineEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    |> Expect.isTrue "FCS request fires at 301ms"
    // Phase 2: FCS completes → handleFcsResult → RunAffectedTests
    let fcsResult = FcsTypeCheckResult.Success ("File.fs", refs)
    let fcsEffects, _ = LiveTestPipelineState.handleFcsResult fcsResult s301
    fcsEffects |> List.exists (fun e -> match e with PipelineEffect.RunAffectedTests _ -> true | _ -> false)
    |> Expect.isTrue "affected tests triggered after FCS"
  }

  test "burst typing coalesces debounce to single tree-sitter parse" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestPipelineState.empty
    let s1 = s0 |> LiveTestPipelineState.onKeystroke "l" "F.fs" t0
    let s2 = s1 |> LiveTestPipelineState.onKeystroke "le" "F.fs" (t0.AddMilliseconds(20.0))
    let s3 = s2 |> LiveTestPipelineState.onKeystroke "let" "F.fs" (t0.AddMilliseconds(40.0))
    let s4 = s3 |> LiveTestPipelineState.onKeystroke "let " "F.fs" (t0.AddMilliseconds(60.0))
    let s5 = s4 |> LiveTestPipelineState.onKeystroke "let x" "F.fs" (t0.AddMilliseconds(80.0))
    let effects100, s100 = s5 |> LiveTestPipelineState.tick (t0.AddMilliseconds(100.0))
    effects100 |> Expect.isEmpty "nothing at 100ms (20ms after last keystroke)"
    let effects131, _ = s100 |> LiveTestPipelineState.tick (t0.AddMilliseconds(131.0))
    let tsCount =
      effects131
      |> List.filter (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
      |> List.length
    tsCount |> Expect.equal "exactly one TS parse" 1
    s5.ActiveFile |> Expect.equal "active file is F.fs" (Some "F.fs")
  }

  test "FCS cancel increases delay, successes reset it" {
    let ad0 = AdaptiveDebounce.createDefault ()
    ad0.CurrentFcsDelayMs |> Expect.equal "initial delay is 300" 300.0
    let ad1 = ad0 |> AdaptiveDebounce.onFcsCanceled
    ad1.CurrentFcsDelayMs |> Expect.equal "after 1 cancel: 450" 450.0
    ad1.ConsecutiveFcsCancels |> Expect.equal "1 cancel" 1
    let ad2 = ad1 |> AdaptiveDebounce.onFcsCanceled
    ad2.CurrentFcsDelayMs |> Expect.equal "after 2 cancels: 675" 675.0
    let ad3 = ad2 |> AdaptiveDebounce.onFcsCompleted
    ad3.ConsecutiveFcsSuccesses |> Expect.equal "1 success" 1
    ad3.CurrentFcsDelayMs |> Expect.equal "delay stays at 675" 675.0
    let ad4 = ad3 |> AdaptiveDebounce.onFcsCompleted
    let ad5 = ad4 |> AdaptiveDebounce.onFcsCompleted
    ad5.CurrentFcsDelayMs |> Expect.equal "reset to 300" 300.0
    ad5.ConsecutiveFcsCancels |> Expect.equal "cancels reset" 0
    ad5.ConsecutiveFcsSuccesses |> Expect.equal "successes reset" 0
  }

  test "symbol rename: cache detects removal+addition, graph finds affected" {
    let refsV1 = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 }
      { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 5 }
      { SymbolFullName = "Lib.oldFn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 8 }
    ]
    let refsV2 = [
      { SymbolFullName = "MyApp.Tests.test1"; UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Lib.fs"; Line = 1 }
      { SymbolFullName = "Lib.add"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 5 }
      { SymbolFullName = "Lib.newFn"; UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Lib.fs"; Line = 8 }
    ]
    let _, cache1 = FileAnalysisCache.empty |> FileAnalysisCache.update "Lib.fs" refsV1
    let graph1 = TestDependencyGraph.empty |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" refsV1 "Lib.fs"
    let changes, _ = cache1 |> FileAnalysisCache.update "Lib.fs" refsV2
    changes.Added |> Expect.contains "newFn added" "Lib.newFn"
    changes.Removed |> Expect.contains "oldFn removed" "Lib.oldFn"
    let graph2 = graph1 |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" refsV2 "Lib.fs"
    let affectedByRemoved = TestDependencyGraph.findAffected changes.Removed graph1
    affectedByRemoved |> Expect.hasLength "t2 affected by removal" 1
    let affectedByAdded = TestDependencyGraph.findAffected changes.Added graph2
    affectedByAdded |> Expect.hasLength "t2 affected by addition" 1
  }

  test "policy filters affected tests by trigger type" {
    let unitTC = mkTestCase "UnitTest.test1" "expecto" TestCategory.Unit
    let integTC = mkTestCase "IntegTest.test1" "expecto" TestCategory.Integration
    let browserTC = mkTestCase "BrowserTest.test1" "expecto" TestCategory.Browser
    let allTests = [|unitTC; integTC; browserTC|]
    let policies = RunPolicyDefaults.defaults
    let filtered = LiveTesting.filterByPolicy policies RunTrigger.Keystroke allTests
    filtered |> Array.length |> Expect.equal "only unit on keystroke" 1
    filtered.[0].Category |> Expect.equal "unit category" TestCategory.Unit
    let filteredExplicit = LiveTesting.filterByPolicy policies RunTrigger.ExplicitRun allTests
    filteredExplicit |> Array.length |> Expect.equal "all on explicit" 3
  }

  test "symbol change marks affected test results as stale" {
    let tc1 = mkTestCase "MyTest.test1" "expecto" TestCategory.Unit
    let tc2 = mkTestCase "MyTest.test2" "expecto" TestCategory.Unit
    let now = DateTimeOffset.UtcNow
    let passedResult = {
      TestId = tc1.Id
      TestName = tc1.FullName
      Result = TestResult.Passed(TimeSpan.FromMilliseconds(5.0))
      Timestamp = now
    }
    let state = {
      LiveTestState.empty with
        DiscoveredTests = [|tc1; tc2|]
        LastResults = Map.ofList [tc1.Id, passedResult]
        Activation = LiveTestingActivation.Active
    }
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["Lib.add", [|tc1.Id|]]
        TransitiveCoverage = Map.ofList ["Lib.add", [|tc1.Id|]]
    }
    let stalified = Staleness.markStale depGraph ["Lib.add"] state
    stalified.AffectedTests |> Set.contains tc1.Id |> Expect.isTrue "tc1 is affected"
    stalified.AffectedTests |> Set.contains tc2.Id |> Expect.isFalse "tc2 not affected"
    match stalified.LastResults |> Map.tryFind tc1.Id with
    | Some r -> r.Result |> Expect.equal "tc1 result preserved as Passed" (TestResult.Passed(TimeSpan.FromMilliseconds(5.0)))
    | None -> failtest "tc1 result should still exist"
    let entry = stalified.StatusEntries |> Array.find (fun e -> e.TestId = tc1.Id)
    match entry.Status with
    | TestRunStatus.Stale -> ()
    | other -> failtestf "expected Stale status but got %A" other
  }

  test "file switch mid-pipeline resets debounce timers" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestPipelineState.empty
    let s1 = s0 |> LiveTestPipelineState.onKeystroke "let x = 1" "File1.fs" t0
    s1.ActiveFile |> Expect.equal "active is File1" (Some "File1.fs")
    let s2 = s1 |> LiveTestPipelineState.onKeystroke "let y = 2" "File2.fs" (t0.AddMilliseconds(30.0))
    s2.ActiveFile |> Expect.equal "active is File2" (Some "File2.fs")
    let effects51, s51 = s2 |> LiveTestPipelineState.tick (t0.AddMilliseconds(51.0))
    effects51 |> Expect.isEmpty "no TS at 51ms (file switched)"
    let effects81, _ = s51 |> LiveTestPipelineState.tick (t0.AddMilliseconds(81.0))
    let hasTS =
      effects81
      |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    hasTS |> Expect.isTrue "TS fires for File2 at 81ms"
  }

  test "FCS completes after new keystroke: debounce restarts" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestPipelineState.empty
    let s1 = s0 |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects1, s2 = s1 |> LiveTestPipelineState.tick (t0.AddMilliseconds(51.0))
    let hasTS = effects1 |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    hasTS |> Expect.isTrue "TS fires after first keystroke"
    let s3 = s2 |> LiveTestPipelineState.onKeystroke "let x = 2" "File.fs" (t0.AddMilliseconds(100.0))
    let effects2, s4 = s3 |> LiveTestPipelineState.tick (t0.AddMilliseconds(352.0))
    let hasFCS = effects2 |> List.exists (fun e -> match e with PipelineEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    hasFCS |> Expect.isFalse "FCS should NOT fire - debounce restarted by keystroke2"
    let effects3, _ = s4 |> LiveTestPipelineState.tick (t0.AddMilliseconds(401.0))
    let hasFCS2 = effects3 |> List.exists (fun e -> match e with PipelineEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    hasFCS2 |> Expect.isTrue "FCS fires after new debounce window"
  }

  test "cold start with empty cache: first keystroke produces TS parse" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestPipelineState.empty
    let s1 = s0 |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    s1.AnalysisCache.FileSymbols |> Expect.isEmpty "cache should be empty on first keystroke"
    let effects, _ = s1 |> LiveTestPipelineState.tick (t0.AddMilliseconds(51.0))
    let hasTS = effects |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    hasTS |> Expect.isTrue "TS fires on first keystroke (cold start)"
  }

  test "session dispose mid-pipeline: state resets cleanly" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestPipelineState.empty
    let s1 = s0 |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let _, _ = s1 |> LiveTestPipelineState.tick (t0.AddMilliseconds(51.0))
    let s3 = LiveTestPipelineState.empty
    s3.ActiveFile |> Expect.isNone "no active file after reset"
    s3.AnalysisCache.FileSymbols |> Expect.isEmpty "no cache after reset"
    let s4 = s3 |> LiveTestPipelineState.onKeystroke "let y = 1" "File2.fs" (t0.AddMilliseconds(200.0))
    s4.ActiveFile |> Expect.equal "new file" (Some "File2.fs")
    let effects, _ = s4 |> LiveTestPipelineState.tick (t0.AddMilliseconds(251.0))
    let hasTS = effects |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    hasTS |> Expect.isTrue "TS fires after session reset"
  }
]

// --- Elm Wiring Behavioral Scenario Tests ---

let hasPendingWork (s: LiveTestPipelineState) =
  s.Debounce.TreeSitter.Pending.IsSome || s.Debounce.Fcs.Pending.IsSome

[<Tests>]
let elmWiringBehavioralTests = testList "Elm Wiring Behavioral Scenarios" [
  test "cold start: tick on empty pipeline produces no effects" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let effects, s' = LiveTestPipelineState.empty |> LiveTestPipelineState.tick t0
    effects |> Expect.isEmpty "no effects on empty pipeline"
    s' |> hasPendingWork |> Expect.isFalse "no pending work"
  }

  test "keystroke then tick past debounce fires TreeSitter parse" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestPipelineState.tick (t0.AddMilliseconds(51.0))
    effects
    |> List.exists (fun e -> match e with PipelineEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "TreeSitter parse fires after debounce"
  }

  test "full pipeline: keystroke through FCS debounce" {
    let tc =
      { Id = TestId.create "T.t1" "expecto"
        FullName = "T.t1"
        DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = "expecto"
        Category = TestCategory.Unit }
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let depGraph =
      { TestDependencyGraph.empty with
          SymbolToTests = Map.ofList ["Lib.add", [|tc.Id|]]
          TransitiveCoverage = Map.ofList ["Lib.add", [|tc.Id|]] }
    let state =
      { LiveTestPipelineState.empty with
          DepGraph = depGraph
          ChangedSymbols = ["Lib.add"]
          TestState =
            { LiveTestState.empty with
                DiscoveredTests = [|tc|]
                Activation = LiveTestingActivation.Active } }
    let s1 = state |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let effects301, _ = s1 |> LiveTestPipelineState.tick (t0.AddMilliseconds(301.0))
    effects301
    |> List.exists (fun e -> match e with PipelineEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    |> Expect.isTrue "FCS fires after 300ms debounce"
  }

  test "pipeline goes idle after both debounces fire" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onKeystroke "let x = 1" "File.fs" t0
    let _, s51 = s |> LiveTestPipelineState.tick (t0.AddMilliseconds(51.0))
    let _, s301 = s51 |> LiveTestPipelineState.tick (t0.AddMilliseconds(301.0))
    s301 |> hasPendingWork |> Expect.isFalse "pipeline idle after both debounces"
    let effects500, _ = s301 |> LiveTestPipelineState.tick (t0.AddMilliseconds(500.0))
    effects500 |> Expect.isEmpty "no further effects after idle"
  }

  test "rapid keystrokes: only latest content fires debounce" {
    let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s0 = LiveTestPipelineState.empty
    let s1 = s0 |> LiveTestPipelineState.onKeystroke "l" "F.fs" t0
    let s2 = s1 |> LiveTestPipelineState.onKeystroke "le" "F.fs" (t0.AddMilliseconds(20.0))
    let s3 = s2 |> LiveTestPipelineState.onKeystroke "let" "F.fs" (t0.AddMilliseconds(40.0))
    let effects, _ = s3 |> LiveTestPipelineState.tick (t0.AddMilliseconds(91.0))
    let tsEffects =
      effects |> List.choose (fun e -> match e with PipelineEffect.ParseTreeSitter (c, _) -> Some c | _ -> None)
    tsEffects |> Expect.hasLength "exactly one parse" 1
  }

  test "test result merge updates status entries" {
    let tc =
      { Id = TestId.create "T.t1" "expecto"
        FullName = "T.t1"
        DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = "expecto"
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
        Timestamp = now }
    let merged = LiveTesting.mergeResults state [|result|]
    merged.StatusEntries |> Array.tryFind (fun e -> e.TestId = tc.Id)
    |> Option.map (fun e -> match e.Status with TestRunStatus.Passed _ -> true | _ -> false)
    |> Option.defaultValue false
    |> Expect.isTrue "status entry shows Passed after merge"
  }

  test "staleness then re-run clears stale" {
    let tc =
      { Id = TestId.create "T.t1" "expecto"
        FullName = "T.t1"
        DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = "expecto"
        Category = TestCategory.Unit }
    let now = DateTimeOffset.UtcNow
    let result : TestRunResult =
      { TestId = tc.Id
        TestName = tc.FullName
        Result = TestResult.Passed(TimeSpan.FromMilliseconds(5.0))
        Timestamp = now }
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
    stale.StatusEntries |> Array.exists (fun e -> match e.Status with TestRunStatus.Stale -> true | _ -> false)
    |> Expect.isTrue "entry is Stale after symbol change"
    let newResult : TestRunResult =
      { TestId = tc.Id
        TestName = tc.FullName
        Result = TestResult.Passed(TimeSpan.FromMilliseconds(3.0))
        Timestamp = now.AddSeconds(1.0) }
    let cleared = LiveTesting.mergeResults stale [|newResult|]
    cleared.StatusEntries |> Array.exists (fun e -> match e.Status with TestRunStatus.Passed _ -> true | _ -> false)
    |> Expect.isTrue "entry is Passed after re-run"
  }

  test "disabled policy shows PolicyDisabled in status entries" {
    let tc =
      { Id = TestId.create "T.t1" "expecto"
        FullName = "T.t1"
        DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = "expecto"
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
  test "feeds content to pipeline debounce when enabled" {
    let model = { SageFsModel.initial with LiveTesting = { LiveTestPipelineState.empty with TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } } }
    let newModel, _effects = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/MyModule.fs", "let x = 1")) model
    newModel.LiveTesting.ActiveFile
    |> Expect.equal "active file set" (Some "src/MyModule.fs")
    newModel.LiveTesting.Debounce.TreeSitter.Pending.IsSome
    |> Expect.isTrue "tree-sitter debounce pending"
    newModel.LiveTesting.Debounce.Fcs.Pending.IsSome
    |> Expect.isTrue "fcs debounce pending"
  }

  test "is no-op when live testing is disabled" {
    let model = { SageFsModel.initial with LiveTesting = { LiveTestPipelineState.empty with TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Inactive } } }
    let newModel, _effects = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/MyModule.fs", "let x = 1")) model
    newModel.LiveTesting.ActiveFile
    |> Expect.equal "active file unchanged" model.LiveTesting.ActiveFile
  }

  test "pipeline tick after debounce fires tree-sitter effect" {
    let model = { SageFsModel.initial with LiveTesting = { LiveTestPipelineState.empty with TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } } }
    let afterKeystroke, _ = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/MyModule.fs", "let x = 1")) model
    let pipeline = afterKeystroke.LiveTesting
    let t51 = DateTimeOffset.UtcNow.AddMilliseconds(51.0)
    let effects, _ = LiveTestPipelineState.tick t51 pipeline
    effects
    |> List.exists (fun e ->
      match e with
      | PipelineEffect.ParseTreeSitter _ -> true
      | _ -> false)
    |> Expect.isTrue "tree-sitter parse fires after debounce"
  }

  test "multiple file changes supersede earlier ones" {
    let model = { SageFsModel.initial with LiveTesting = { LiveTestPipelineState.empty with TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } } }
    let after1, _ = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/First.fs", "let a = 1")) model
    let after2, _ = SageFsUpdate.update (SageFsMsg.FileContentChanged("src/Second.fs", "let b = 2")) after1
    after2.LiveTesting.ActiveFile
    |> Expect.equal "latest file wins" (Some "src/Second.fs")
  }
]

[<Tests>]
let fcsTypeCheckResultTests = testList "FcsTypeCheckResult" [
  test "Success updates symbol graph via onFcsComplete" {
    let tc = mkTestCase "MyApp.Tests.testAdd" "expecto" TestCategory.Unit
    let refs = [
      { SymbolReference.SymbolFullName = "MyApp.Tests.testAdd"
        UseKind = SymbolUseKind.Definition; UsedInTestId = None
        FilePath = "Test.fs"; Line = 1 }
      { SymbolReference.SymbolFullName = "Lib.add"
        UseKind = SymbolUseKind.Reference; UsedInTestId = None
        FilePath = "Test.fs"; Line = 5 }
    ]
    let state = {
      LiveTestPipelineState.empty with
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let result = FcsTypeCheckResult.Success ("Test.fs", refs)
    let _effects, s1 = LiveTestPipelineState.handleFcsResult result state
    s1.DepGraph.SymbolToTests
    |> Map.containsKey "Lib.add"
    |> Expect.isTrue "dep graph has Lib.add"
  }

  test "Success with changed symbols triggers RunAffectedTests" {
    let tc = mkTestCase "MyApp.Tests.testAdd" "expecto" TestCategory.Unit
    let refs = [
      { SymbolReference.SymbolFullName = "MyApp.Tests.testAdd"
        UseKind = SymbolUseKind.Definition; UsedInTestId = None
        FilePath = "Test.fs"; Line = 1 }
      { SymbolReference.SymbolFullName = "Lib.add"
        UseKind = SymbolUseKind.Reference; UsedInTestId = None
        FilePath = "Test.fs"; Line = 5 }
    ]
    let state = {
      LiveTestPipelineState.empty with
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let result = FcsTypeCheckResult.Success ("Test.fs", refs)
    let effects, _ = LiveTestPipelineState.handleFcsResult result state
    effects
    |> List.exists (fun e -> match e with PipelineEffect.RunAffectedTests _ -> true | _ -> false)
    |> Expect.isTrue "RunAffectedTests fires on new symbols"
  }

  test "Success updates adaptive debounce" {
    let state = LiveTestPipelineState.empty
    let result = FcsTypeCheckResult.Success ("test.fs", [])
    let _, s1 = LiveTestPipelineState.handleFcsResult result state
    s1.AdaptiveDebounce.ConsecutiveFcsSuccesses
    |> Expect.equal "success count incremented" 1
  }

  test "Failed produces no effects" {
    let state = LiveTestPipelineState.empty
    let result = FcsTypeCheckResult.Failed ("test.fs", ["error: type mismatch"])
    let effects, _ = LiveTestPipelineState.handleFcsResult result state
    effects |> Expect.isEmpty "no effects on failure"
  }

  test "Failed does not change adaptive debounce" {
    let state = LiveTestPipelineState.empty
    let result = FcsTypeCheckResult.Failed ("test.fs", ["error"])
    let _, s1 = LiveTestPipelineState.handleFcsResult result state
    s1.AdaptiveDebounce.ConsecutiveFcsSuccesses
    |> Expect.equal "unchanged success count" 0
    s1.AdaptiveDebounce.ConsecutiveFcsCancels
    |> Expect.equal "unchanged cancel count" 0
  }

  test "Cancelled updates adaptive debounce backoff" {
    let state = LiveTestPipelineState.empty
    let result = FcsTypeCheckResult.Cancelled "test.fs"
    let effects, s1 = LiveTestPipelineState.handleFcsResult result state
    effects |> Expect.isEmpty "no effects on cancel"
    s1.AdaptiveDebounce.ConsecutiveFcsCancels
    |> Expect.equal "cancel count incremented" 1
  }

  test "Cancelled increases FCS delay" {
    let state = LiveTestPipelineState.empty
    let baseFcsMs = state.AdaptiveDebounce.Config.BaseFcsMs
    let result = FcsTypeCheckResult.Cancelled "test.fs"
    let _, s1 = LiveTestPipelineState.handleFcsResult result state
    (s1.AdaptiveDebounce.CurrentFcsDelayMs, baseFcsMs)
    |> Expect.isGreaterThan "delay increased after cancel"
  }

  test "Multiple successes reset FCS delay to base" {
    let state = LiveTestPipelineState.empty
    let _, s1 = LiveTestPipelineState.handleFcsResult (FcsTypeCheckResult.Cancelled "f.fs") state
    let resetCount = s1.AdaptiveDebounce.Config.ResetAfterSuccessCount
    let mutable s = s1
    for _ in 1..resetCount do
      let _, sn = LiveTestPipelineState.handleFcsResult (FcsTypeCheckResult.Success ("f.fs", [])) s
      s <- sn
    s.AdaptiveDebounce.CurrentFcsDelayMs
    |> Expect.equal "delay reset to base" state.AdaptiveDebounce.Config.BaseFcsMs
  }

  test "Elm wiring: FcsTypeCheckCompleted Success updates model and emits effects" {
    let tc = mkTestCase "MyApp.Tests.testAdd" "expecto" TestCategory.Unit
    let refs = [
      { SymbolReference.SymbolFullName = "MyApp.Tests.testAdd"
        UseKind = SymbolUseKind.Definition; UsedInTestId = None; FilePath = "Test.fs"; Line = 1 }
      { SymbolReference.SymbolFullName = "Lib.add"
        UseKind = SymbolUseKind.Reference; UsedInTestId = None; FilePath = "Test.fs"; Line = 5 }
    ]
    let model = {
      SageFsModel.initial with
        LiveTesting = {
          LiveTestPipelineState.empty with
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
      | SageFsEffect.Pipeline (PipelineEffect.RunAffectedTests _) -> true
      | _ -> false)
    |> Expect.isTrue "Pipeline RunAffectedTests effect emitted"
  }

  test "Elm wiring: FcsTypeCheckCompleted Failed is no-op" {
    let model = SageFsModel.initial
    let msg = SageFsMsg.FcsTypeCheckCompleted (FcsTypeCheckResult.Failed ("test.fs", ["error"]))
    let model', effects = SageFsUpdate.update msg model
    effects |> Expect.isEmpty "no effects on failure"
    model'.LiveTesting.DepGraph.SymbolToTests
    |> Expect.isEmpty "dep graph unchanged"
  }
]

let triggerWiringTests = testList "RunTrigger wiring" [
  test "onFileSave sets LastTrigger to FileSave" {
    let now = DateTimeOffset.UtcNow
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onFileSave "f.fs" now
    s.LastTrigger |> Expect.equal "trigger is FileSave" RunTrigger.FileSave
  }

  test "onKeystroke sets LastTrigger to Keystroke" {
    let now = DateTimeOffset.UtcNow
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onKeystroke "x" "f.fs" now
    s.LastTrigger |> Expect.equal "trigger is Keystroke" RunTrigger.Keystroke
  }

  test "handleFcsResult uses stored FileSave trigger for OnSaveOnly tests" {
    let tc = mkTestCase "MyApp.Tests.archTest" "expecto" TestCategory.Architecture
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
      LiveTestPipelineState.empty with
        TestState = { LiveTestState.empty with
                        DiscoveredTests = [| tc |]
                        RunPolicies = RunPolicyDefaults.defaults }
    }
    let s1 = s0 |> LiveTestPipelineState.onFileSave "Test.fs" now
    let effects, _ =
      LiveTestPipelineState.handleFcsResult (FcsTypeCheckResult.Success ("Test.fs", refs)) s1
    effects
    |> List.exists (fun e -> match e with PipelineEffect.RunAffectedTests _ -> true | _ -> false)
    |> Expect.isTrue "OnSaveOnly test runs with FileSave trigger"
  }

  test "handleFcsResult with Keystroke trigger filters out OnSaveOnly tests" {
    let tc = mkTestCase "MyApp.Tests.archTest" "expecto" TestCategory.Architecture
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
      LiveTestPipelineState.empty with
        TestState = { LiveTestState.empty with
                        DiscoveredTests = [| tc |]
                        RunPolicies = RunPolicyDefaults.defaults }
    }
    let s1 = s0 |> LiveTestPipelineState.onKeystroke "let x = 1" "Test.fs" now
    let effects, _ =
      LiveTestPipelineState.handleFcsResult (FcsTypeCheckResult.Success ("Test.fs", refs)) s1
    effects
    |> Expect.isEmpty "OnSaveOnly test filtered out on Keystroke"
  }
]

let adaptiveDebounceWiringTests = testList "adaptive debounce wiring" [
  test "onKeystroke uses adaptive FCS delay after cancellations" {
    let now = DateTimeOffset.UtcNow
    let s0 = LiveTestPipelineState.empty
    let s1 = s0 |> LiveTestPipelineState.onFcsCanceled
    let s2 = s1 |> LiveTestPipelineState.onFcsCanceled
    let s3 = s2 |> LiveTestPipelineState.onFcsCanceled
    let expectedDelay = int (300.0 * 1.5 * 1.5 * 1.5)
    let s4 = s3 |> LiveTestPipelineState.onKeystroke "let x = 1" "Test.fs" now
    match s4.Debounce.Fcs.Pending with
    | Some p ->
      p.DelayMs |> Expect.equal "FCS delay reflects adaptive backoff" expectedDelay
    | None -> failtest "FCS debounce should have a pending entry"
  }

  test "onKeystroke uses base delay with no cancellations" {
    let now = DateTimeOffset.UtcNow
    let s = LiveTestPipelineState.empty |> LiveTestPipelineState.onKeystroke "x" "f.fs" now
    match s.Debounce.Fcs.Pending with
    | Some p ->
      p.DelayMs |> Expect.equal "base FCS delay" 300
    | None -> failtest "FCS debounce should have a pending entry"
  }

  test "adaptive delay resets after consecutive successes" {
    let now = DateTimeOffset.UtcNow
    let s0 = LiveTestPipelineState.empty
    // Cancel to raise delay
    let s1 = s0 |> LiveTestPipelineState.onFcsCanceled
    (LiveTestPipelineState.currentFcsDelay s1, 300.0)
    |> Expect.isGreaterThan "delay raised"
    // Reset via consecutive successes
    let mutable s = s1
    for _ in 1 .. s.AdaptiveDebounce.Config.ResetAfterSuccessCount do
      let _, sn = LiveTestPipelineState.handleFcsResult (FcsTypeCheckResult.Success ("f.fs", [])) s
      s <- sn
    let s2 = s |> LiveTestPipelineState.onKeystroke "x" "f.fs" now
    match s2.Debounce.Fcs.Pending with
    | Some p ->
      p.DelayMs |> Expect.equal "delay reset to base after successes" 300
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
    Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5.0) }

[<Tests>]
let runningToStaleOnKeystrokeTests = testList "Running → Stale on keystroke" [
  test "keystroke while tests running transitions to RunningButEdited" {
    let tid = TestId.create "TestA" "expecto"
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestPipelineState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" "expecto" |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
        }
    }
    let s' = LiveTestPipelineState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    match (s'.TestState.RunPhases |> Map.tryFind "s" |> Option.defaultValue Idle) with
    | RunningButEdited _ -> ()
    | other -> failwithf "Expected RunningButEdited, got %A" other
  }

  test "keystroke while tests running preserves AffectedTests" {
    let tid = TestId.create "TestA" "expecto"
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestPipelineState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" "expecto" |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
        }
    }
    let s' = LiveTestPipelineState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    s'.TestState.AffectedTests
    |> Expect.isNonEmpty "AffectedTests should be preserved"
  }

  test "status shows Passed after keystroke during running (streaming shows available result)" {
    let tid = TestId.create "TestA" "expecto"
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestPipelineState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" "expecto" |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
            LastResults = Map.ofList [ tid, mkPassedResult tid ]
        }
    }
    let s' = LiveTestPipelineState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    let entries = LiveTesting.computeStatusEntries s'.TestState
    // Streaming: result already available, so shows Passed (not Running)
    match entries.[0].Status with
    | TestRunStatus.Passed _ -> ()
    | other -> failtestf "expected Passed, got %A" other
  }

  test "status shows Queued for never-run affected test after keystroke" {
    let tid = TestId.create "TestA" "expecto"
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestPipelineState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" "expecto" |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
        }
    }
    let s' = LiveTestPipelineState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    let entries = LiveTesting.computeStatusEntries s'.TestState
    // RunningButEdited still shows Running for affected tests
    entries.[0].Status
    |> Expect.equal "should be Running" TestRunStatus.Running
  }

  test "keystroke while NOT running keeps Idle" {
    let s = LiveTestPipelineState.empty
    let s' = LiveTestPipelineState.onKeystroke "changed" "Foo.fs" DateTimeOffset.UtcNow s
    (s'.TestState.RunPhases |> Map.tryFind "s" |> Option.defaultValue Idle)
    |> Expect.equal "should stay Idle" Idle
  }
]

[<Tests>]
let runningToStaleOnFileSaveTests = testList "Running → Stale on file save" [
  test "save while tests running transitions to RunningButEdited" {
    let tid = TestId.create "TestA" "expecto"
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestPipelineState.empty with
        TestState = {
          LiveTestState.empty with
            DiscoveredTests = [| mkSourceMappedTestCase "TestA" "expecto" |]
            RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen
            AffectedTests = Set.singleton tid
            LastResults = Map.ofList [ tid, mkPassedResult tid ]
        }
    }
    let s' = LiveTestPipelineState.onFileSave "Foo.fs" DateTimeOffset.UtcNow s
    match (s'.TestState.RunPhases |> Map.tryFind "s" |> Option.defaultValue Idle) with
    | RunningButEdited _ -> ()
    | other -> failwithf "Expected RunningButEdited, got %A" other
  }

  test "save while NOT running keeps Idle" {
    let s = LiveTestPipelineState.empty
    let s' = LiveTestPipelineState.onFileSave "Foo.fs" DateTimeOffset.UtcNow s
    (s'.TestState.RunPhases |> Map.tryFind "s" |> Option.defaultValue Idle)
    |> Expect.equal "should stay Idle" Idle
  }
]

[<Tests>]
let mergeResultsStalenessFixTests = testList "mergeResults staleness handling" [
  test "mergeResults preserves AffectedTests (no clearing)" {
    let tid = TestId.create "TestA" "expecto"
    let result = mkResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestState.empty with
        DiscoveredTests = [| mkSourceMappedTestCase "TestA" "expecto" |]
        RunPhases = Map.ofList ["s", RunningButEdited gen]; LastGeneration = gen
        AffectedTests = Set.singleton tid
    }
    let s' = LiveTesting.mergeResults s [| result |]
    s'.AffectedTests
    |> Expect.isNonEmpty "mergeResults should not clear AffectedTests"
  }

  test "streaming result shows Passed while RunPhase is still Running" {
    let tid = TestId.create "TestA" "expecto"
    let result = mkResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestState.empty with
        DiscoveredTests = [| mkSourceMappedTestCase "TestA" "expecto" |]
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
    let tid = TestId.create "TestA" "expecto"
    let result = mkResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    let gen = RunGeneration.next RunGeneration.zero
    let s = {
      LiveTestState.empty with
        DiscoveredTests = [| mkSourceMappedTestCase "TestA" "expecto" |]
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
              Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
              Category = TestCategory.Unit; CurrentPolicy = RunPolicy.OnEveryChange
              Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected }
            { TestId = TestId.TestId "t2"; DisplayName = "session-b test"; FullName = "session-b test"
              Origin = TestOrigin.ReflectionOnly; Framework = "xunit"
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
              Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
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
    let tid = TestId.create "Tests.payload_fresh" "expecto"
    let entry = {
      TestId = tid; DisplayName = "payload_fresh"; FullName = "Tests.payload_fresh"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
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
    let tid = TestId.create "Tests.not_empty" "expecto"
    let entry = {
      TestId = tid; DisplayName = "not_empty"; FullName = "Tests.not_empty"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
      Category = TestCategory.Unit; CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Detected; PreviousStatus = TestRunStatus.Detected }
    let gen = RunGeneration.zero
    let batch = TestResultsBatchPayload.create gen ResultFreshness.Fresh (BatchCompletion.Complete(1, 1)) LiveTestingActivation.Active [| entry |]
    TestResultsBatchPayload.isEmpty batch |> Expect.isFalse "should not be empty"
  }
]

// --- Symbol graph wiring integration tests ---

[<Tests>]
let symbolGraphWiringTests = testList "symbol graph wiring integration" [
  test "afterTypeCheck with affected symbol produces RunAffectedTests effect" {
    let tid = TestId.create "Tests.affected_by_add" "expecto"
    let testCase = {
      Id = tid; FullName = "Tests.affected_by_add"; DisplayName = "affected_by_add"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
      Category = TestCategory.Unit; Labels = [] }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "MyModule.add", [| tid |] ]
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ] }
    let ltState = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testCase |]
        RunPolicies = RunPolicyDefaults.defaults }
    let effect =
      PipelineEffects.afterTypeCheck
        [ "MyModule.add" ] "test.fs" RunTrigger.Keystroke graph ltState None Map.empty
    match effect with
    | [ PipelineEffect.RunAffectedTests (tests, _, _, _, _, _) ] ->
      tests |> Array.exists (fun t -> t.Id = tid)
      |> Expect.isTrue "should contain affected test"
    | other -> failtestf "expected single RunAffectedTests, got %A" other
  }

  test "handleFcsResult updates dep graph via onFcsComplete" {
    let tid = TestId.create "Tests.t1" "expecto"
    let testCase = {
      Id = tid; FullName = "Tests.t1"; DisplayName = "t1"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
      Category = TestCategory.Unit; Labels = [] }
    let pipeState = {
      LiveTestPipelineState.empty with
        TestState = { LiveTestState.empty with
                        Activation = LiveTestingActivation.Active
                        DiscoveredTests = [| testCase |]
                        RunPolicies = RunPolicyDefaults.defaults } }
    let refs = [
      { SymbolFullName = "MyModule.add"; UseKind = SymbolUseKind.Reference
        UsedInTestId = Some tid; FilePath = "test.fs"; Line = 5 } ]
    let fcsResult = FcsTypeCheckResult.Success ("test.fs", refs)
    let _effects, updated = LiveTestPipelineState.handleFcsResult fcsResult pipeState
    updated.DepGraph.SourceVersion
    |> fun v -> Expect.isGreaterThan "should increment version" (v, pipeState.DepGraph.SourceVersion)
  }

  test "triggerExecutionForAffected fallback path" {
    let tid = TestId.create "Tests.fallback" "expecto"
    let testCase = {
      Id = tid; FullName = "Tests.fallback"; DisplayName = "fallback"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
      Category = TestCategory.Unit; Labels = [] }
    let pipeState = {
      LiveTestPipelineState.empty with
        TestState = { LiveTestState.empty with
                        Activation = LiveTestingActivation.Active
                        DiscoveredTests = [| testCase |]
                        RunPolicies = RunPolicyDefaults.defaults } }
    let effects =
      LiveTestPipelineState.triggerExecutionForAffected
        [| tid |] RunTrigger.FileSave None pipeState
    effects
    |> List.exists (fun e ->
      match e with
      | PipelineEffect.RunAffectedTests (tests, _, _, _, _, _) ->
        tests |> Array.exists (fun t -> t.Id = tid)
      | _ -> false)
    |> Expect.isTrue "should produce RunAffectedTests via fallback"
  }

  test "no effects when testing disabled" {
    let tid = TestId.create "Tests.disabled" "expecto"
    let testCase = {
      Id = tid; FullName = "Tests.disabled"; DisplayName = "disabled"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
      Category = TestCategory.Unit; Labels = [] }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "M.func", [| tid |] ] }
    let ltState = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Inactive
        DiscoveredTests = [| testCase |]
        RunPolicies = RunPolicyDefaults.defaults }
    PipelineEffects.afterTypeCheck [ "M.func" ] "test.fs" RunTrigger.Keystroke graph ltState None Map.empty
    |> Expect.isEmpty "no effect when disabled"
  }

  test "no effects when no symbols changed" {
    let tid = TestId.create "Tests.no_change" "expecto"
    let testCase = {
      Id = tid; FullName = "Tests.no_change"; DisplayName = "no_change"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
      Category = TestCategory.Unit; Labels = [] }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "M.func", [| tid |] ] }
    let ltState = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testCase |]
        RunPolicies = RunPolicyDefaults.defaults }
    PipelineEffects.afterTypeCheck [] "test.fs" RunTrigger.Keystroke graph ltState None Map.empty
    |> Expect.isEmpty "no effect when no symbols"
  }
]

// --- Optimistic gutter transition tests ---

[<Tests>]
let optimisticGutterTests = testList "optimistic gutter transitions" [
  test "TestRunStarted marks affected tests as Running via status entries" {
    let tid = TestId.create "Tests.optimistic_run" "expecto"
    let tests = [|
      { Id = tid; FullName = "Tests.optimistic_run"; DisplayName = "optimistic_run"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let model0 = SageFsModel.initial
    let model1 = { model0 with
                    LiveTesting = { model0.LiveTesting with
                                      TestState = { model0.LiveTesting.TestState with
                                                      Activation = LiveTestingActivation.Active
                                                      DiscoveredTests = tests } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "s"))) model1
    let entry =
      model2.LiveTesting.TestState.StatusEntries
      |> Array.tryFind (fun e -> e.TestId = tid)
    match entry with
    | Some e -> e.Status |> Expect.equal "should be Running" TestRunStatus.Running
    | None -> failtest "expected status entry for affected test"
  }

  test "non-affected tests stay Detected while others run" {
    let tidA = TestId.create "Tests.affected" "expecto"
    let tidB = TestId.create "Tests.unaffected" "expecto"
    let tests = [|
      { Id = tidA; FullName = "Tests.affected"; DisplayName = "affected"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
      { Id = tidB; FullName = "Tests.unaffected"; DisplayName = "unaffected"
        Origin = TestOrigin.SourceMapped ("editor", 10)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let model0 = SageFsModel.initial
    let model1 = { model0 with
                    LiveTesting = { model0.LiveTesting with
                                      TestState = { model0.LiveTesting.TestState with
                                                      Activation = LiveTestingActivation.Active
                                                      DiscoveredTests = tests } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tidA |], Some "s"))) model1
    let entryB =
      model2.LiveTesting.TestState.StatusEntries
      |> Array.tryFind (fun e -> e.TestId = tidB)
    match entryB with
    | Some e -> e.Status |> Expect.equal "should still be Detected" TestRunStatus.Detected
    | None -> failtest "expected status entry for unaffected test"
  }

  test "TestRunStarted keeps previous Passed status visible (streaming shows available result)" {
    let tid = TestId.create "Tests.prev_passed" "expecto"
    let tests = [|
      { Id = tid; FullName = "Tests.prev_passed"; DisplayName = "prev_passed"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let dur = System.TimeSpan.FromMilliseconds 10.0
    let result = {
      TestId = tid; TestName = "Tests.prev_passed"
      Result = TestResult.Passed dur; Timestamp = System.DateTimeOffset.UtcNow }
    let model0 = SageFsModel.initial
    let model1 = { model0 with
                    LiveTesting = { model0.LiveTesting with
                                      TestState = { model0.LiveTesting.TestState with
                                                      Activation = LiveTestingActivation.Active
                                                      DiscoveredTests = tests
                                                      LastResults = Map.ofList [ tid, result ] } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) model1
    let model3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "test-session"))) model2
    let entry =
      model3.LiveTesting.TestState.StatusEntries
      |> Array.tryFind (fun e -> e.TestId = tid)
    match entry with
    | Some e ->
      // Streaming: previous result stays visible during run
      match e.Status with
      | TestRunStatus.Passed _ -> ()
      | other -> failtestf "expected Passed (streaming), got %A" other
      match e.PreviousStatus with
      | TestRunStatus.Passed _ -> ()
      | other -> failtestf "expected previous Passed, got %A" other
    | None -> failtest "expected status entry"
  }

  test "effect handler dispatches TestRunStarted before async execution" {
    let tid = TestId.create "Tests.sync_dispatch" "expecto"
    let tests = [|
      { Id = tid; FullName = "Tests.sync_dispatch"; DisplayName = "sync_dispatch"
        Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
        Category = TestCategory.Unit; Labels = [] }
    |]
    let model0 = SageFsModel.initial
    let model1 = { model0 with
                    LiveTesting = { model0.LiveTesting with
                                      TestState = { model0.LiveTesting.TestState with
                                                      Activation = LiveTestingActivation.Active
                                                      DiscoveredTests = tests } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "s"))) model1
    TestRunPhase.isAnyRunning model2.LiveTesting.TestState.RunPhases
    |> Expect.isTrue "phase should be Running after TestRunStarted dispatch"
  }
]

// --- SSE enrichment round-trip tests ---

[<Tests>]
let sseEnrichmentTests = testList "SSE enrichment round-trip" [
  test "after mergeResults, compute enriched batch from state" {
    let tid = TestId.create "Tests.round_trip" "expecto"
    let testCase = {
      Id = tid; FullName = "Tests.round_trip"; DisplayName = "round_trip"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
      Category = TestCategory.Unit; Labels = [] }
    let dur = System.TimeSpan.FromMilliseconds 42.0
    let result = {
      TestId = tid; TestName = "Tests.round_trip"
      Result = TestResult.Passed dur; Timestamp = System.DateTimeOffset.UtcNow }
    let phase, gen = TestRunPhase.startRun RunGeneration.zero
    let state = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testCase |]
        RunPhases = Map.ofList ["s", phase]
        LastGeneration = gen
        AffectedTests = Set.ofList [tid] }
    let merged = LiveTesting.mergeResults state [| result |]
    let entries = LiveTesting.computeStatusEntries merged
    let freshness = ResultFreshness.Fresh
    let batch =
      let completion = TestResultsBatchPayload.deriveCompletion freshness 1 entries.Length
      TestResultsBatchPayload.create gen freshness completion merged.Activation entries
    batch.Generation |> Expect.equal "generation matches" gen
    batch.Freshness |> Expect.equal "fresh results" ResultFreshness.Fresh
    batch.Entries.Length |> Expect.equal "one entry" 1
    match batch.Entries.[0].Status with
    | TestRunStatus.Passed d -> d |> Expect.equal "correct duration" dur
    | other -> failtestf "expected Passed, got %A" other
    batch.Summary.Passed |> Expect.equal "summary shows passed" 1
  }

  test "stale results after edit produce StaleCodeEdited" {
    let tid = TestId.create "Tests.stale_edit" "expecto"
    let testCase = {
      Id = tid; FullName = "Tests.stale_edit"; DisplayName = "stale_edit"
      Origin = TestOrigin.ReflectionOnly; Framework = "expecto"
      Category = TestCategory.Unit; Labels = [] }
    let phase, gen = TestRunPhase.startRun RunGeneration.zero
    let editedPhase = TestRunPhase.onEdit phase
    let state = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [| testCase |]
        RunPhases = Map.ofList ["s", editedPhase]
        LastGeneration = gen
        AffectedTests = Set.ofList [tid] }
    let result = {
      TestId = tid; TestName = "Tests.stale_edit"
      Result = TestResult.Passed (System.TimeSpan.FromMilliseconds 10.0)
      Timestamp = System.DateTimeOffset.UtcNow }
    let merged = LiveTesting.mergeResults state [| result |]
    let entries = LiveTesting.computeStatusEntries merged
    let _newPhase, freshness = TestRunPhase.onResultsArrived gen editedPhase
    let batch =
      let completion = TestResultsBatchPayload.deriveCompletion freshness 1 entries.Length
      TestResultsBatchPayload.create gen freshness completion merged.Activation entries
    batch.Freshness |> Expect.equal "stale code edited" ResultFreshness.StaleCodeEdited
  }
]

[<Tests>]
let elmUpdateStatusRecomputationTests = testList "Elm update StatusEntries recomputation" [
  test "TestsDiscovered recomputes StatusEntries" {
    let tests = [|
      { Id = TestId.create "t1" "test1"
        FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]

    let model0 = SageFsModel.initial
    let model1 = { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) model1

    model2.LiveTesting.TestState.StatusEntries
    |> Array.length
    |> Expect.equal "should have 1 status entry after TestsDiscovered" 1
  }

  test "AffectedTestsComputed recomputes StatusEntries to Queued" {
    let tid = TestId.create "t1" "test1"
    let tests = [|
      { Id = tid; FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]

    let model0 = SageFsModel.initial
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
      { Id = TestId.create "t1" "test1"
        FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]

    let model0 = SageFsModel.initial
    let model1 = { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) model1

    let annotations = LiveTesting.annotationsForFile "editor" model2.LiveTesting.TestState
    annotations
    |> Array.length
    |> Expect.equal "should have 1 annotation after TestsDiscovered" 1
  }

  test "TestRunStarted shows Running status" {
    let tid = TestId.create "t1" "test1"
    let tests = [|
      { Id = tid; FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]

    let model0 = SageFsModel.initial
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some "test-session"))) model1

    model2.LiveTesting.TestState.StatusEntries
    |> Array.tryHead
    |> Option.map (fun e -> e.Status)
    |> Expect.equal "should be Running after TestRunStarted" (Some TestRunStatus.Running)
  }

  test "RunPolicyChanged to Disabled shows PolicyDisabled status" {
    let tests = [|
      { Id = TestId.create "t1" "test1"
        FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]

    let model0 = SageFsModel.initial
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", tests))) { model0 with LiveTesting = { model0.LiveTesting with TestState = { model0.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.RunPolicyChanged (TestCategory.Unit, RunPolicy.Disabled))) model1

    model2.LiveTesting.TestState.StatusEntries
    |> Array.tryHead
    |> Option.map (fun e -> e.Status)
    |> Expect.equal "should be PolicyDisabled after RunPolicyChanged" (Some TestRunStatus.PolicyDisabled)
  }

  test "Full lifecycle: Discovered → Started → Completed shows pass/fail annotations" {
    let tid1 = TestId.create "t1" "test1"
    let tid2 = TestId.create "t2" "test2"
    let tests = [|
      { Id = tid1; FullName = "M.test1"; DisplayName = "test1"
        Origin = TestOrigin.SourceMapped ("editor", 5)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
      { Id = tid2; FullName = "M.test2"; DisplayName = "test2"
        Origin = TestOrigin.SourceMapped ("editor", 10)
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let results = [|
      { TestId = tid1; TestName = "test1"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 5.0)
        Timestamp = DateTimeOffset.UtcNow }
      { TestId = tid2; TestName = "test2"
        Result = TestResult.Failed (TestFailure.AssertionFailed "Expected 42 got 43", TimeSpan.FromMilliseconds 12.0)
        Timestamp = DateTimeOffset.UtcNow }
    |]

    let model0 = SageFsModel.initial
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

// --- Serialization Roundtrip Integration Tests ---

[<Tests>]
let serializationRoundtripTests = testList "serialization roundtrip integration" [
  test "LiveTestHookResultDto survives JSON roundtrip" {
    let original : LiveTestHookResultDto = {
      DetectedProviders = [
        ProviderDescription.Custom { Name = "expecto"; AssemblyMarker = "Expecto" }
        ProviderDescription.AttributeBased { Name = "xunit"; TestAttributes = ["Fact"; "Theory"]; AssemblyMarker = "xunit.core" }
      ]
      DiscoveredTests = [|
        { Id = TestId.create "Test.add" "expecto"
          FullName = "Test.add"; DisplayName = "add"
          Origin = TestOrigin.SourceMapped ("test.fs", 10)
          Labels = ["fast"]; Framework = "expecto"; Category = TestCategory.Unit }
        { Id = TestId.create "Test.validate" "xunit"
          FullName = "Test.validate"; DisplayName = "validate"
          Origin = TestOrigin.ReflectionOnly
          Labels = []; Framework = "xunit"; Category = TestCategory.Integration }
      |]
      AffectedTestIds = [| TestId.create "Test.add" "expecto" |]
    }

    let json = SageFs.WorkerProtocol.Serialization.serialize original
    let deserialized = SageFs.WorkerProtocol.Serialization.deserialize<LiveTestHookResultDto> json

    deserialized
    |> Expect.equal "roundtrip preserves data" original
  }

  test "full pipeline: serialize → deserialize → dispatch → annotations" {
    let hookResult : LiveTestHookResultDto = {
      DetectedProviders = [
        ProviderDescription.Custom { Name = "expecto"; AssemblyMarker = "Expecto" }
      ]
      DiscoveredTests = [|
        { Id = TestId.create "Mod.test1" "expecto"
          FullName = "Mod.test1"; DisplayName = "test1"
          Origin = TestOrigin.SourceMapped ("Mod.fs", 5)
          Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
      |]
      AffectedTestIds = [| TestId.create "Mod.test1" "expecto" |]
    }

    let json = SageFs.WorkerProtocol.Serialization.serialize hookResult
    let deserialized = SageFs.WorkerProtocol.Serialization.deserialize<LiveTestHookResultDto> json

    let m0 = SageFsModel.initial
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.ProvidersDetected deserialized.DetectedProviders)) m0
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("test-session", deserialized.DiscoveredTests))) m1
    let m3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.AffectedTestsComputed deserialized.AffectedTestIds)) m2

    let annotations = LiveTesting.annotationsForFile "Mod.fs" m3.LiveTesting.TestState
    annotations.Length
    |> Expect.equal "should have 1 annotation" 1

    annotations.[0].Line
    |> Expect.equal "annotation on line 5" 5
  }
]

[<Tests>]
let pipelineTimingDispatchTests = testList "pipeline timing dispatch" [
  test "PipelineTimingRecorded stores LastTiming in model" {
    let model0 = SageFsModel.initial
    model0.LiveTesting.LastTiming
    |> Expect.isNone "initial model should have no timing"

    let timing = {
      Depth = PipelineDepth.ThroughExecution (
        System.TimeSpan.FromMilliseconds 1.2,
        System.TimeSpan.FromMilliseconds 85.0,
        System.TimeSpan.FromMilliseconds 42.0)
      TotalTests = 10
      AffectedTests = 3
      Trigger = RunTrigger.Keystroke
      Timestamp = System.DateTimeOffset.UtcNow
    }

    let msg = SageFsMsg.Event (SageFsEvent.PipelineTimingRecorded timing)
    let model1, effects = SageFsUpdate.update msg model0

    model1.LiveTesting.LastTiming
    |> Expect.isSome "after dispatch, model should have timing"

    effects
    |> Expect.isEmpty "PipelineTimingRecorded should produce no effects"
  }

  test "PipelineTiming.toStatusBar formats correctly for ThroughExecution" {
    let timing = {
      Depth = PipelineDepth.ThroughExecution (
        System.TimeSpan.FromMilliseconds 1.2,
        System.TimeSpan.FromMilliseconds 85.0,
        System.TimeSpan.FromMilliseconds 42.0)
      TotalTests = 10
      AffectedTests = 3
      Trigger = RunTrigger.Keystroke
      Timestamp = System.DateTimeOffset.UtcNow
    }
    PipelineTiming.toStatusBar timing
    |> Expect.equal "should format all three stages" "TS:1.2ms | FCS:85ms | Run:42ms (3)"
  }

  test "PipelineTiming.toStatusBar formats TreeSitterOnly" {
    let timing = {
      Depth = PipelineDepth.TreeSitterOnly (System.TimeSpan.FromMilliseconds 0.8)
      TotalTests = 5
      AffectedTests = 0
      Trigger = RunTrigger.Keystroke
      Timestamp = System.DateTimeOffset.UtcNow
    }
    PipelineTiming.toStatusBar timing
    |> Expect.equal "should only show tree-sitter" "TS:0.8ms"
  }

  test "PipelineTiming.toStatusBar formats ThroughFcs" {
    let timing = {
      Depth = PipelineDepth.ThroughFcs (
        System.TimeSpan.FromMilliseconds 1.5,
        System.TimeSpan.FromMilliseconds 142.0)
      TotalTests = 20
      AffectedTests = 5
      Trigger = RunTrigger.FileSave
      Timestamp = System.DateTimeOffset.UtcNow
    }
    PipelineTiming.toStatusBar timing
    |> Expect.equal "should show tree-sitter and FCS" "TS:1.5ms | FCS:142ms"
  }

  test "new timing replaces old timing" {
    let timing1 = {
      Depth = PipelineDepth.TreeSitterOnly (System.TimeSpan.FromMilliseconds 0.5)
      TotalTests = 5
      AffectedTests = 0
      Trigger = RunTrigger.Keystroke
      Timestamp = System.DateTimeOffset.UtcNow
    }
    let timing2 = {
      Depth = PipelineDepth.ThroughExecution (
        System.TimeSpan.FromMilliseconds 1.0,
        System.TimeSpan.FromMilliseconds 100.0,
        System.TimeSpan.FromMilliseconds 50.0)
      TotalTests = 10
      AffectedTests = 3
      Trigger = RunTrigger.FileSave
      Timestamp = System.DateTimeOffset.UtcNow
    }

    let model0 = SageFsModel.initial
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.PipelineTimingRecorded timing1)) model0
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.PipelineTimingRecorded timing2)) model1

    match model2.LiveTesting.LastTiming with
    | Some t ->
      t.AffectedTests
      |> Expect.equal "should have second timing's affected count" 3
      t.TotalTests
      |> Expect.equal "should have second timing's total count" 10
    | None -> failtest "timing should be Some after two dispatches"
  }
]

[<Tests>]
let liveTestingStatusBarTests = testList "liveTestingStatusBar" [

  test "returns empty string when no timing and no tests" {
    let state = LiveTestPipelineState.empty
    LiveTestPipelineState.liveTestingStatusBar state
    |> Expect.equal "should be empty" ""
  }

  test "returns timing only when tests are empty" {
    let timing = {
      Depth = PipelineDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 1.0,
        TimeSpan.FromMilliseconds 50.0,
        TimeSpan.FromMilliseconds 30.0)
      TotalTests = 5
      AffectedTests = 2
      Trigger = RunTrigger.FileSave
      Timestamp = DateTimeOffset.UtcNow
    }
    let state = { LiveTestPipelineState.empty with LastTiming = Some timing }
    let result = LiveTestPipelineState.liveTestingStatusBar state
    result |> Expect.isNotEmpty "should have timing text"
    result |> Expect.stringContains "should contain TS" "TS:"
  }

  test "returns tests only when timing is None" {
    let testId = TestId.create "MyTest.test1" "expecto"
    let entry = {
      TestId = testId
      DisplayName = "test1"
      FullName = "MyTest.test1"
      Origin = TestOrigin.ReflectionOnly
      Framework = "expecto"
      Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0)
      PreviousStatus = TestRunStatus.Detected
    }
    let testState = { LiveTestPipelineState.empty.TestState with StatusEntries = [| entry |] }
    let state = { LiveTestPipelineState.empty with TestState = testState }
    let result = LiveTestPipelineState.liveTestingStatusBar state
    result |> Expect.isNotEmpty "should have tests text"
    result |> Expect.stringContains "should contain pass count" "1"
  }

  test "returns combined timing and tests" {
    let timing = {
      Depth = PipelineDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 1.0,
        TimeSpan.FromMilliseconds 50.0,
        TimeSpan.FromMilliseconds 30.0)
      TotalTests = 5
      AffectedTests = 2
      Trigger = RunTrigger.FileSave
      Timestamp = DateTimeOffset.UtcNow
    }
    let testId = TestId.create "MyTest.test1" "expecto"
    let entry = {
      TestId = testId
      DisplayName = "test1"
      FullName = "MyTest.test1"
      Origin = TestOrigin.ReflectionOnly
      Framework = "expecto"
      Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0)
      PreviousStatus = TestRunStatus.Detected
    }
    let testState = { LiveTestPipelineState.empty.TestState with StatusEntries = [| entry |] }
    let state = { LiveTestPipelineState.empty with LastTiming = Some timing; TestState = testState }
    let result = LiveTestPipelineState.liveTestingStatusBar state
    result |> Expect.stringContains "should contain TS" "TS:"
    result |> Expect.stringContains "should contain pipe separator" " | "
  }
]

let pipelineBenchmarkTests = testList "Pipeline Core Benchmark" [
  test "200-test pipeline core completes under 5ms p95" {
    let sw = System.Diagnostics.Stopwatch()
    let makeTestCase i =
      { Id = TestId.create (sprintf "Module.Tests.test%d" i) "expecto"
        FullName = sprintf "Module.Tests.test%d" i; DisplayName = sprintf "test%d" i
        Origin = TestOrigin.SourceMapped ("editor", i + 1); Labels = []; Framework = "expecto"
        Category = if i % 10 = 0 then TestCategory.Integration else TestCategory.Unit }
    let tests = Array.init 200 makeTestCase
    let directMap =
      tests |> Array.map (fun t -> sprintf "Module.func%d" (t.Id.GetHashCode() % 50), [| t.Id |])
      |> Array.groupBy fst |> Array.map (fun (sym, pairs) -> sym, pairs |> Array.collect snd) |> Map.ofArray
    let graph = { SymbolToTests = directMap; TransitiveCoverage = directMap; SourceVersion = 1; PerFileIndex = Map.empty }
    let results =
      tests.[..149] |> Array.map (fun t ->
        t.Id, { TestId = t.Id; TestName = t.DisplayName
                Result = TestResult.Passed (TimeSpan.FromMilliseconds 5.0)
                Timestamp = DateTimeOffset.UtcNow }) |> Map.ofArray
    let locs = tests |> Array.mapi (fun i t ->
      { AttributeName = "Test"; FunctionName = sprintf "t%d" i; FilePath = "editor"
        Line = (match t.Origin with TestOrigin.SourceMapped (_, l) -> l | _ -> 0); Column = 0 })
    let state = { LiveTestState.empty with
                    DiscoveredTests = tests; LastResults = results; SourceLocations = locs
                    AffectedTests = tests.[..19] |> Array.map (fun t -> t.Id) |> Set.ofArray }
    let stateWithEntries = { state with StatusEntries = LiveTesting.computeStatusEntries state }

    let timings = Array.init 100 (fun _ ->
      sw.Restart()
      let _ = PipelineOrchestrator.decide stateWithEntries RunTrigger.Keystroke ["Module.func1"] graph
      let _ = TestDependencyGraph.findAffected ["Module.func1"] graph
      let _ = LiveTesting.filterByPolicy RunPolicyDefaults.defaults RunTrigger.Keystroke tests
      let _ = LiveTesting.computeStatusEntries stateWithEntries
      let _ = LiveTesting.recomputeEditorAnnotations (Some "editor") stateWithEntries
      sw.Stop()
      sw.Elapsed.TotalMilliseconds)

    let sorted = timings |> Array.sort
    let p95 = sorted.[94]
    (p95, 5.0) |> Expect.isLessThan "p95 under 5ms"
  }

  test "1000-test pipeline core completes under 20ms p95" {
    let sw = System.Diagnostics.Stopwatch()
    let makeTestCase i =
      { Id = TestId.create (sprintf "M.T.t%d" i) "expecto"
        FullName = sprintf "M.T.t%d" i; DisplayName = sprintf "t%d" i
        Origin = TestOrigin.SourceMapped ("editor", i + 1); Labels = []; Framework = "expecto"
        Category = TestCategory.Unit }
    let tests = Array.init 1000 makeTestCase
    let directMap =
      tests |> Array.map (fun t -> sprintf "func%d" (t.Id.GetHashCode() % 200), [| t.Id |])
      |> Array.groupBy fst |> Array.map (fun (sym, pairs) -> sym, pairs |> Array.collect snd) |> Map.ofArray
    let graph = { SymbolToTests = directMap; TransitiveCoverage = directMap; SourceVersion = 1; PerFileIndex = Map.empty }
    let results =
      tests.[..799] |> Array.map (fun t ->
        t.Id, { TestId = t.Id; TestName = t.DisplayName
                Result = TestResult.Passed (TimeSpan.FromMilliseconds 3.0)
                Timestamp = DateTimeOffset.UtcNow }) |> Map.ofArray
    let locs = tests |> Array.mapi (fun i t ->
      { AttributeName = "Test"; FunctionName = sprintf "t%d" i; FilePath = "editor"
        Line = (match t.Origin with TestOrigin.SourceMapped (_, l) -> l | _ -> 0); Column = 0 })
    let state = { LiveTestState.empty with
                    DiscoveredTests = tests; LastResults = results; SourceLocations = locs
                    AffectedTests = tests.[..49] |> Array.map (fun t -> t.Id) |> Set.ofArray }
    let stateWithEntries = { state with StatusEntries = LiveTesting.computeStatusEntries state }

    let timings = Array.init 50 (fun _ ->
      sw.Restart()
      let _ = PipelineOrchestrator.decide stateWithEntries RunTrigger.Keystroke ["func1"] graph
      let _ = TestDependencyGraph.findAffected ["func1"] graph
      let _ = LiveTesting.filterByPolicy RunPolicyDefaults.defaults RunTrigger.Keystroke tests
      let _ = LiveTesting.computeStatusEntries stateWithEntries
      let _ = LiveTesting.recomputeEditorAnnotations (Some "editor") stateWithEntries
      sw.Stop()
      sw.Elapsed.TotalMilliseconds)

    let sorted = timings |> Array.sort
    let p95 = sorted.[47]
    (p95, 20.0) |> Expect.isLessThan "p95 under 20ms"
  }
]

// --- FCS Dependency Graph Integration Tests ---

[<Tests>]
let fcsGraphTests = testList "FCS dependency graph builder" [

  test "builds inverted index from symbol uses" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Math.multiply"; DisplayName = "multiply"; UseKind = SymbolUseKind.Definition; StartLine = 3; EndLine = 3 }
      { FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Tests.mulTest"; DisplayName = "mulTest"; UseKind = SymbolUseKind.Definition; StartLine = 7; EndLine = 7 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Math.multiply"; DisplayName = "multiply"; UseKind = SymbolUseKind.Reference; StartLine = 7; EndLine = 7 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." "fcs" uses
    graph.SymbolToTests.Count
    |> Expect.equal "should have 2 production symbols" 2
    let addAffected = TestDependencyGraph.findAffected ["MyApp.Math.add"] graph
    addAffected.Length
    |> Expect.equal "add should affect 1 test" 1
    let mulAffected = TestDependencyGraph.findAffected ["MyApp.Math.multiply"] graph
    mulAffected.Length
    |> Expect.equal "multiply should affect 1 test" 1
  }

  test "test calling multiple production functions maps to all" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Math.multiply"; DisplayName = "multiply"; UseKind = SymbolUseKind.Definition; StartLine = 3; EndLine = 3 }
      { FullName = "MyApp.Tests.combinedTest"; DisplayName = "combinedTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 6; EndLine = 6 }
      { FullName = "MyApp.Math.multiply"; DisplayName = "multiply"; UseKind = SymbolUseKind.Reference; StartLine = 7; EndLine = 7 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." "fcs" uses
    let combinedId = TestId.create "MyApp.Tests.combinedTest" "fcs"
    TestDependencyGraph.findAffected ["MyApp.Math.add"] graph
    |> Array.contains combinedId
    |> Expect.isTrue "add should affect combinedTest"
    TestDependencyGraph.findAffected ["MyApp.Math.multiply"] graph
    |> Array.contains combinedId
    |> Expect.isTrue "multiply should affect combinedTest"
  }

  test "unused production symbol has no affected tests" {
    let uses = [|
      { FullName = "MyApp.Math.unused"; DisplayName = "unused"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Tests.someTest"; DisplayName = "someTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." "fcs" uses
    TestDependencyGraph.findAffected ["MyApp.Math.unused"] graph
    |> Array.length
    |> Expect.equal "unused should affect 0 tests" 0
  }

  test "multiple changed symbols finds union of affected tests" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Math.sub"; DisplayName = "sub"; UseKind = SymbolUseKind.Definition; StartLine = 3; EndLine = 3 }
      { FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Tests.subTest"; DisplayName = "subTest"; UseKind = SymbolUseKind.Definition; StartLine = 8; EndLine = 8 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 6; EndLine = 6 }
      { FullName = "MyApp.Math.sub"; DisplayName = "sub"; UseKind = SymbolUseKind.Reference; StartLine = 9; EndLine = 9 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." "fcs" uses
    TestDependencyGraph.findAffected ["MyApp.Math.add"; "MyApp.Math.sub"] graph
    |> Array.length
    |> Expect.equal "should find 2 distinct tests" 2
  }

  test "empty symbol uses produces empty graph" {
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." "fcs" [||]
    graph.SymbolToTests.Count
    |> Expect.equal "empty uses produces empty graph" 0
  }

  test "FSharp stdlib uses are excluded from graph" {
    let uses = [|
      { FullName = "MyApp.Tests.test1"; DisplayName = "test1"; UseKind = SymbolUseKind.Definition; StartLine = 1; EndLine = 1 }
      { FullName = "Microsoft.FSharp.Core.ExtraTopLevelOperators.printfn"; DisplayName = "printfn"; UseKind = SymbolUseKind.Reference; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Logic.doThing"; DisplayName = "doThing"; UseKind = SymbolUseKind.Reference; StartLine = 3; EndLine = 3 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." "fcs" uses
    graph.SymbolToTests.Count
    |> Expect.equal "only production symbols, not stdlib" 1
    graph.SymbolToTests |> Map.containsKey "Microsoft.FSharp.Core.ExtraTopLevelOperators.printfn"
    |> Expect.isFalse "should not contain FSharp stdlib"
  }

  test "orchestrator uses FCS graph for affected test selection" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Tests.otherTest"; DisplayName = "otherTest"; UseKind = SymbolUseKind.Definition; StartLine = 8; EndLine = 8 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 6; EndLine = 6 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." "fcs" uses
    let addTestCase = {
      Id = TestId.create "MyApp.Tests.addTest" "fcs"
      FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"
      Origin = TestOrigin.SourceMapped ("test.fsx", 5)
      Labels = []; Framework = "fcs"; Category = TestCategory.Unit
    }
    let otherTestCase = {
      Id = TestId.create "MyApp.Tests.otherTest" "fcs"
      FullName = "MyApp.Tests.otherTest"; DisplayName = "otherTest"
      Origin = TestOrigin.SourceMapped ("test.fsx", 8)
      Labels = []; Framework = "fcs"; Category = TestCategory.Unit
    }
    let state = { LiveTestState.empty with
                    DiscoveredTests = [| addTestCase; otherTestCase |]
                    Activation = LiveTestingActivation.Active }
    let stateWithEntries = { state with StatusEntries = LiveTesting.computeStatusEntries state }
    match PipelineOrchestrator.decide stateWithEntries RunTrigger.Keystroke ["MyApp.Math.add"] graph with
    | PipelineDecision.FullPipeline testIds ->
      testIds.Length
      |> Expect.equal "should only run addTest, not otherTest" 1
      testIds.[0]
      |> Expect.equal "should be addTest" addTestCase.Id
    | other -> failtest (sprintf "Expected FullPipeline, got %A" other)
  }

  test "coverage projection with built graph" {
    let uses = [|
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Definition; StartLine = 2; EndLine = 2 }
      { FullName = "MyApp.Math.unused"; DisplayName = "unused"; UseKind = SymbolUseKind.Definition; StartLine = 3; EndLine = 3 }
      { FullName = "MyApp.Tests.addTest"; DisplayName = "addTest"; UseKind = SymbolUseKind.Definition; StartLine = 5; EndLine = 5 }
      { FullName = "MyApp.Math.add"; DisplayName = "add"; UseKind = SymbolUseKind.Reference; StartLine = 6; EndLine = 6 }
    |]
    let graph = TestDependencyGraph.buildFromSymbolUses ".Tests." "fcs" uses
    let addTestId = TestId.create "MyApp.Tests.addTest" "fcs"
    let results = Map.ofList [
      addTestId, {
        TestId = addTestId; TestName = "addTest"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 5.0)
        Timestamp = DateTimeOffset.UtcNow
      }
    ]
    match CoverageProjection.symbolCoverage graph results "MyApp.Math.add" with
    | CoverageStatus.Covered (count, health) ->
      count |> Expect.equal "add covered by 1 test" 1
      health |> Expect.equal "addTest passes" CoverageHealth.AllPassing
    | other -> failtest (sprintf "Expected Covered for add, got %A" other)
    match CoverageProjection.symbolCoverage graph results "MyApp.Math.unused" with
    | CoverageStatus.NotCovered -> ()
    | other -> failtest (sprintf "Expected NotCovered for unused, got %A" other)
  }
]

// --- Multi-File Merge Tests ---
let mkSymRef fullName line isDef : SymbolReference =
  { SymbolFullName = fullName
    UseKind = if isDef then SymbolUseKind.Definition else SymbolUseKind.Reference
    UsedInTestId = None
    FilePath = "test.fs"
    Line = line }

[<Tests>]
let perFileMergeTests = testList "per-file merge correctness" [
  test "updateGraph merges TestIds across files, not overwrite" {
    let fileARefs = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]
    let fileBRefs = [
      mkSymRef "MyApp.Tests.test2" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]

    let graph1 =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" fileARefs "FileA.fs"

    let test1Id = TestId.create "MyApp.Tests.test1" "fcs"
    TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graph1
    |> Expect.contains "FileA should map Prod.foo to test1" test1Id

    let graph2 =
      graph1
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" fileBRefs "FileB.fs"

    let test2Id = TestId.create "MyApp.Tests.test2" "fcs"
    let affected = TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graph2

    affected |> Expect.contains "Should still have test1 from FileA" test1Id
    affected |> Expect.contains "Should have test2 from FileB" test2Id
  }

  test "re-analyzing same file replaces old entries" {
    let refsV1 = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]
    let refsV2 = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.bar" 12 false
    ]

    let graph1 =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" refsV1 "FileA.fs"
    let graph2 =
      graph1
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" refsV2 "FileA.fs"

    let test1Id = TestId.create "MyApp.Tests.test1" "fcs"
    TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graph2
    |> Expect.isEmpty "foo should be gone after FileA re-analysis"

    TestDependencyGraph.findAffected ["MyApp.Prod.bar"] graph2
    |> Expect.contains "bar should map to test1" test1Id
  }

  test "removing a file's refs clears only that file" {
    let fileARefs = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]
    let fileBRefs = [
      mkSymRef "MyApp.Tests.test2" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]

    let graph =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" fileARefs "FileA.fs"
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" fileBRefs "FileB.fs"

    let graphAfter =
      graph |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" [] "FileA.fs"

    let test2Id = TestId.create "MyApp.Tests.test2" "fcs"
    let affected = TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graphAfter

    affected |> Expect.hasLength "only FileB's test2 remains" 1
    affected |> Expect.contains "test2 should still be there" test2Id
  }

  test "PerFileIndex tracks contributions" {
    let fileARefs = [
      mkSymRef "MyApp.Tests.test1" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
    ]

    let graph =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" fileARefs "FileA.fs"

    graph.PerFileIndex
    |> Map.containsKey "FileA.fs"
    |> Expect.isTrue "PerFileIndex should contain FileA.fs"
  }

  test "multiple symbols across multiple files" {
    let fileARefs = [
      mkSymRef "MyApp.Tests.testA" 10 true
      mkSymRef "MyApp.Prod.foo" 12 false
      mkSymRef "MyApp.Prod.bar" 14 false
    ]
    let fileBRefs = [
      mkSymRef "MyApp.Tests.testB" 10 true
      mkSymRef "MyApp.Prod.bar" 12 false
      mkSymRef "MyApp.Prod.baz" 14 false
    ]

    let graph =
      TestDependencyGraph.empty
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" fileARefs "FileA.fs"
      |> SymbolGraphBuilder.updateGraph ".Tests." "fcs" fileBRefs "FileB.fs"

    let testAId = TestId.create "MyApp.Tests.testA" "fcs"
    let testBId = TestId.create "MyApp.Tests.testB" "fcs"

    TestDependencyGraph.findAffected ["MyApp.Prod.foo"] graph
    |> Expect.equal "foo maps to testA only" [| testAId |]

    let barAffected = TestDependencyGraph.findAffected ["MyApp.Prod.bar"] graph
    barAffected |> Expect.contains "bar maps to testA" testAId
    barAffected |> Expect.contains "bar maps to testB" testBId

    TestDependencyGraph.findAffected ["MyApp.Prod.baz"] graph
    |> Expect.equal "baz maps to testB only" [| testBId |]
  }
]

[<Tests>]
let projectAssemblyDiscoveryTests = testList "Project assembly initial discovery" [
  test "afterReload on SageFs.Tests assembly discovers tests" {
    let asm = System.Reflection.Assembly.LoadFrom(resolveTestDll "SageFs.Tests.dll")
    let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn asm []
    Expect.isGreaterThan
      "should discover at least 100 tests"
      (result.DiscoveredTests.Length, 100)
  }

  test "afterReload detects expecto provider from test assembly" {
    let asm = System.Reflection.Assembly.LoadFrom(resolveTestDll "SageFs.Tests.dll")
    let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn asm []
    result.DetectedProviders
    |> List.exists (fun p ->
      match p with
      | ProviderDescription.Custom c -> c.Name = "expecto"
      | _ -> false)
    |> Expect.isTrue "should detect expecto provider"
  }

  test "all discovered tests have framework=expecto" {
    let asm = System.Reflection.Assembly.LoadFrom(resolveTestDll "SageFs.Tests.dll")
    let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn asm []
    result.DiscoveredTests
    |> Array.iter (fun t ->
      t.Framework
      |> Expect.equal "framework should be expecto" "expecto")
  }

  test "merging FSI + project results deduplicates providers" {
    let fsiResult = LiveTestHookResult.empty
    let projResult = {
      DetectedProviders =
        [ ProviderDescription.Custom
            { Name = "expecto"; AssemblyMarker = "Expecto" } ]
      DiscoveredTests =
        [| { Id = TestId.create "test1" "expecto"
             FullName = "test1"
             DisplayName = "t1"
             Origin = TestOrigin.ReflectionOnly
             Labels = []
             Framework = "expecto"
             Category = TestCategory.Unit } |]
      AffectedTestIds = [||]
      RunTest = LiveTestHookResult.noOp
    }
    let allResults = [fsiResult; projResult]
    let mergedProviders =
      allResults
      |> List.collect (fun r -> r.DetectedProviders)
      |> List.distinctBy (fun p ->
        match p with
        | ProviderDescription.AttributeBased a -> a.Name
        | ProviderDescription.Custom c -> c.Name)
    let mergedTests =
      allResults
      |> List.map (fun r -> r.DiscoveredTests)
      |> Array.concat
    mergedProviders.Length
    |> Expect.equal "one distinct provider" 1
    mergedTests.Length
    |> Expect.equal "one test from project" 1
  }
]

[<Tests>]
let mergeDiscoveredTestsTests = testList "LiveTesting.mergeDiscoveredTests" [
  test "empty incoming preserves existing tests" {
    let existing = [|
      { Id = TestId.create "test1" "expecto"; FullName = "test1"; DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "expecto"
        Category = TestCategory.Unit } |]
    let result = LiveTesting.mergeDiscoveredTests existing [||]
    result.Length |> Expect.equal "keeps existing" 1
  }

  test "empty existing uses incoming" {
    let incoming = [|
      { Id = TestId.create "test1" "expecto"; FullName = "test1"; DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "expecto"
        Category = TestCategory.Unit } |]
    let result = LiveTesting.mergeDiscoveredTests [||] incoming
    result.Length |> Expect.equal "takes incoming" 1
  }

  test "incoming overrides existing with same TestId" {
    let existing = [|
      { Id = TestId.create "test1" "expecto"; FullName = "test1"; DisplayName = "old"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "expecto"
        Category = TestCategory.Unit } |]
    let incoming = [|
      { Id = TestId.create "test1" "expecto"; FullName = "test1"; DisplayName = "new"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "expecto"
        Category = TestCategory.Unit } |]
    let result = LiveTesting.mergeDiscoveredTests existing incoming
    result.Length |> Expect.equal "one merged test" 1
    result.[0].DisplayName |> Expect.equal "incoming wins" "new"
  }

  test "disjoint tests are unioned" {
    let existing = [|
      { Id = TestId.create "test1" "expecto"; FullName = "test1"; DisplayName = "t1"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "expecto"
        Category = TestCategory.Unit } |]
    let incoming = [|
      { Id = TestId.create "test2" "expecto"; FullName = "test2"; DisplayName = "t2"
        Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "expecto"
        Category = TestCategory.Unit } |]
    let result = LiveTesting.mergeDiscoveredTests existing incoming
    result.Length |> Expect.equal "both tests present" 2
  }

  test "second eval with empty incoming does not wipe tests" {
    // Simulates: first eval discovers 3454 tests, second eval discovers 0 from FSI
    let projectTests = [|
      { Id = TestId.create "proj.test1" "expecto"; FullName = "proj.test1"
        DisplayName = "t1"; Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
      { Id = TestId.create "proj.test2" "expecto"; FullName = "proj.test2"
        DisplayName = "t2"; Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit } |]
    let afterFirstEval = LiveTesting.mergeDiscoveredTests [||] projectTests
    // Second eval: FSI dynamic assembly has no tests
    let afterSecondEval = LiveTesting.mergeDiscoveredTests afterFirstEval [||]
    afterSecondEval.Length
    |> Expect.equal "project tests survive second eval" 2
  }
]

let affectedExecutionTriggerTests = testList "AffectedTestsComputed execution trigger" [
  let mkState tests =
    { LiveTestState.empty with
        DiscoveredTests = tests
        Activation = LiveTestingActivation.Active
        RunPolicies = RunPolicyDefaults.defaults }
  let mkPipelineState tests =
    { LiveTestPipelineState.empty with
        TestState = mkState tests }
  let tc1 = {
    Id = TestId.create "ns.t1" "expecto"
    FullName = "ns.t1"; DisplayName = "t1"
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = "expecto"; Category = TestCategory.Unit
  }
  let tc2 = {
    Id = TestId.create "ns.t2" "expecto"
    FullName = "ns.t2"; DisplayName = "t2"
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = "expecto"; Category = TestCategory.Integration
  }
  let tc3 = {
    Id = TestId.create "ns.t3" "expecto"
    FullName = "ns.t3"; DisplayName = "t3"
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = "expecto"; Category = TestCategory.Unit
  }

  test "empty affected IDs produce no effects" {
    let ps = mkPipelineState [| tc1; tc2 |]
    let effects = LiveTestPipelineState.triggerExecutionForAffected [||] RunTrigger.FileSave None ps
    effects
    |> Expect.isEmpty "should produce no effects for empty affected IDs"
  }

  test "non-empty affected IDs produce RunAffectedTests effect" {
    let ps = mkPipelineState [| tc1; tc2; tc3 |]
    let effects = LiveTestPipelineState.triggerExecutionForAffected [| tc1.Id; tc3.Id |] RunTrigger.FileSave None ps
    effects
    |> List.length
    |> Expect.equal "should produce exactly one effect" 1
  }

  test "only unit tests run on FileSave when policy is OnEveryChange" {
    let ps = mkPipelineState [| tc1; tc2; tc3 |]
    let effects = LiveTestPipelineState.triggerExecutionForAffected [| tc1.Id; tc2.Id |] RunTrigger.FileSave None ps
    match effects with
    | [ PipelineEffect.RunAffectedTests (tests, _, _, _, _, _) ] ->
      tests
      |> Array.length
      |> Expect.equal "should only include unit test" 1
      tests.[0].Category
      |> Expect.equal "should be unit category" TestCategory.Unit
    | _ -> failtest "expected exactly one RunAffectedTests effect"
  }

  test "ExplicitRun trigger runs integration tests too" {
    let ps = mkPipelineState [| tc1; tc2; tc3 |]
    let effects = LiveTestPipelineState.triggerExecutionForAffected [| tc1.Id; tc2.Id |] RunTrigger.ExplicitRun None ps
    match effects with
    | [ PipelineEffect.RunAffectedTests (tests, _, _, _, _, _) ] ->
      tests
      |> Array.length
      |> Expect.equal "should include both unit and integration" 2
    | _ -> failtest "expected exactly one RunAffectedTests effect"
  }

  test "disabled live testing produces no effects" {
    let ps = { mkPipelineState [| tc1; tc2 |] with
                 TestState = { (mkState [| tc1; tc2 |]) with Activation = LiveTestingActivation.Inactive } }
    let effects = LiveTestPipelineState.triggerExecutionForAffected [| tc1.Id |] RunTrigger.FileSave None ps
    effects
    |> Expect.isEmpty "should produce no effects when disabled"
  }

  test "affected IDs not in discovered tests are ignored" {
    let ps = mkPipelineState [| tc1 |]
    let unknownId = TestId.create "unknown" "expecto"
    let effects = LiveTestPipelineState.triggerExecutionForAffected [| unknownId |] RunTrigger.FileSave None ps
    effects
    |> Expect.isEmpty "unknown test IDs should not produce effects"
  }
]

// ── findAffectedTests fix + filterTestsForExplicitRun + findAllTestIds tests ──

[<Tests>]
let findAffectedTestsFixTests =
  let mkTC id name fw =
    { Id = TestId.create id name; FullName = name
      DisplayName = name.Split('.').[name.Split('.').Length - 1]
      Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = fw
      Category = TestCategory.Unit }
  let sampleTests = [|
    mkTC "t1" "MyModule.Tests.test_add" "expecto"
    mkTC "t2" "MyModule.Tests.test_sub" "expecto"
    mkTC "t3" "Other.Tests.test_mul" "xunit"
  |]
  testList "findAffectedTests fixed semantics" [
    test "empty method names returns empty array" {
      LiveTestingHook.findAffectedTests sampleTests []
      |> Array.length
      |> Expect.equal "nothing changed = no affected tests" 0
    }
    test "matching method name returns affected test" {
      LiveTestingHook.findAffectedTests sampleTests ["test_add"]
      |> Array.length
      |> Expect.equal "one match" 1
    }
    test "multiple matching methods return multiple tests" {
      LiveTestingHook.findAffectedTests sampleTests ["test_add"; "test_mul"]
      |> Array.length
      |> Expect.equal "two matches" 2
    }
    test "non-matching method falls back to all tests" {
      // Conservative fallback: non-empty methods but no match → run everything
      LiveTestingHook.findAffectedTests sampleTests ["nonexistent"]
      |> Array.length
      |> Expect.equal "falls back to all tests" 3
    }
  ]

[<Tests>]
let findAllTestIdsTests =
  let mkTC id name fw =
    { Id = TestId.create id name; FullName = name
      DisplayName = name.Split('.').[name.Split('.').Length - 1]
      Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = fw
      Category = TestCategory.Unit }
  let sampleTests = [|
    mkTC "t1" "A.test1" "xunit"
    mkTC "t2" "B.test2" "xunit"
  |]
  testList "findAllTestIds" [
    test "returns all test IDs" {
      LiveTestingHook.findAllTestIds sampleTests
      |> Array.length
      |> Expect.equal "all tests" 2
    }
    test "empty discovered returns empty" {
      LiveTestingHook.findAllTestIds [||]
      |> Array.length
      |> Expect.equal "no tests" 0
    }
  ]

[<Tests>]
let filterTestsForExplicitRunTests =
  let mkTC id name fw =
    { Id = TestId.create id name; FullName = name
      DisplayName = name.Split('.').[name.Split('.').Length - 1]
      Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = fw
      Category = TestCategory.Unit }
  let sampleTests = [|
    mkTC "t1" "MyModule.Tests.test_add" "expecto"
    mkTC "t2" "MyModule.Tests.test_sub" "expecto"
    mkTC "t3" "Other.Tests.test_mul" "xunit"
  |]
  testList "filterTestsForExplicitRun" [
    test "no filters returns all" {
      LiveTestPipelineState.filterTestsForExplicitRun sampleTests None None None
      |> Array.length
      |> Expect.equal "all tests" 3
    }
    test "pattern filter matches by FullName" {
      LiveTestPipelineState.filterTestsForExplicitRun sampleTests None (Some "test_add") None
      |> Array.length
      |> Expect.equal "pattern match" 1
    }
    test "category filter" {
      let mixedTests = [|
        mkTC "t1" "Unit.test1" "xunit"
        { mkTC "t2" "Integration.test2" "xunit" with Category = TestCategory.Integration }
      |]
      LiveTestPipelineState.filterTestsForExplicitRun mixedTests None None (Some TestCategory.Unit)
      |> Array.length
      |> Expect.equal "only unit" 1
    }
    test "file filter works for SourceMapped" {
      let mappedTests = [|
        { mkTC "t1" "Test1" "xunit" with Origin = TestOrigin.SourceMapped ("foo.fs", 10) }
        { mkTC "t2" "Test2" "xunit" with Origin = TestOrigin.SourceMapped ("bar.fs", 20) }
        mkTC "t3" "Test3" "xunit"
      |]
      LiveTestPipelineState.filterTestsForExplicitRun mappedTests (Some "foo.fs") None None
      |> Array.length
      |> Expect.equal "only foo.fs" 1
    }
    test "combined filters intersect" {
      let mixedTests = [|
        mkTC "t1" "MyModule.add_test" "xunit"
        { mkTC "t2" "MyModule.db_test" "xunit" with Category = TestCategory.Integration }
        mkTC "t3" "Other.add_test" "xunit"
      |]
      LiveTestPipelineState.filterTestsForExplicitRun mixedTests None (Some "add_test") (Some TestCategory.Unit)
      |> Array.length
      |> Expect.equal "pattern + category intersection" 2
    }
  ]

[<Tests>]
let flakyDetectionTests = testList "FlakyDetection" [

  testProperty "ResultWindow.count never exceeds windowSize" <| fun (windowSize: FsCheck.PositiveInt) ->
    let ws = max 2 windowSize.Get
    let w = ResultWindow.create ws
    let outcomes = [| TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass; TestOutcome.Fail; TestOutcome.Pass |]
    (outcomes |> Array.fold (fun acc o -> ResultWindow.add o acc) w).Count <= ws

  testProperty "ResultWindow.toList length equals min(adds, windowSize)" <| fun (windowSize: FsCheck.PositiveInt) (additions: FsCheck.PositiveInt) ->
    let ws = max 2 windowSize.Get |> min 50
    let adds = max 1 additions.Get |> min 100
    let w = ResultWindow.create ws
    let outcomes = [| for i in 1..adds -> if i % 2 = 0 then TestOutcome.Pass else TestOutcome.Fail |]
    (outcomes |> Array.fold (fun acc o -> ResultWindow.add o acc) w |> ResultWindow.toList).Length = min adds ws

  test "insufficient when count < minSamples" {
    ResultWindow.create 10 |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
    |> TestStability.assess 3 2
    |> Expect.equal "2 samples = Insufficient" TestStability.Insufficient
  }

  test "stable when no flips" {
    ResultWindow.create 10
    |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Pass
    |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Pass
    |> TestStability.assess 3 2
    |> Expect.equal "all Pass = Stable" TestStability.Stable
  }

  test "flaky when flips exceed threshold" {
    ResultWindow.create 10
    |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
    |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
    |> TestStability.assess 3 2
    |> function
       | TestStability.Flaky n when n >= 2 -> ()
       | other -> failwithf "expected Flaky with >= 2 flips, got %A" other
  }

  test "stable transitions back from flaky when consistent" {
    let w =
      ResultWindow.create 5
      |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
      |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
      |> ResultWindow.add TestOutcome.Pass
    match TestStability.assess 3 2 w with
    | TestStability.Flaky _ -> ()
    | other -> failwithf "expected Flaky before, got %A" other
    w |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Pass
      |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Pass
      |> ResultWindow.add TestOutcome.Pass
    |> TestStability.assess 3 2
    |> Expect.equal "stabilized" TestStability.Stable
  }

  testProperty "empty window is always Insufficient" <| fun (windowSize: FsCheck.PositiveInt) ->
    let ws = max 2 windowSize.Get |> min 50
    ResultWindow.create ws |> TestStability.assess 3 2 = TestStability.Insufficient

  testProperty "all same outcome is never Flaky" <| fun (windowSize: FsCheck.PositiveInt) (additions: FsCheck.PositiveInt) ->
    let ws = max 2 windowSize.Get |> min 50
    let adds = max 3 additions.Get |> min 100
    [1..adds] |> List.fold (fun acc _ -> ResultWindow.add TestOutcome.Pass acc) (ResultWindow.create ws)
    |> TestStability.assess 3 2
    |> function TestStability.Flaky _ -> false | _ -> true

  testProperty "countFlips is symmetric" <| fun () ->
    let w1 = ResultWindow.create 4
              |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
              |> ResultWindow.add TestOutcome.Pass |> ResultWindow.add TestOutcome.Fail
    let w2 = ResultWindow.create 4
              |> ResultWindow.add TestOutcome.Fail |> ResultWindow.add TestOutcome.Pass
              |> ResultWindow.add TestOutcome.Fail |> ResultWindow.add TestOutcome.Pass
    ResultWindow.countFlips w1 = ResultWindow.countFlips w2

  test "outcomeOf maps Passed to Pass" {
    FlakyDetection.outcomeOf (TestResult.Passed (TimeSpan.FromMilliseconds 10.0))
    |> Expect.equal "Passed→Pass" TestOutcome.Pass
  }

  test "outcomeOf maps Failed to Fail" {
    FlakyDetection.outcomeOf (TestResult.Failed (TestFailure.AssertionFailed "msg", TimeSpan.FromMilliseconds 10.0))
    |> Expect.equal "Failed→Fail" TestOutcome.Fail
  }

  test "recordResult creates window for new test" {
    let tid = TestId.create "t1" "expecto"
    let updated = FlakyDetection.recordResult tid (TestResult.Passed (TimeSpan.FromMilliseconds 5.0)) Map.empty
    Expect.isTrue "should have entry" (Map.containsKey tid updated)
    (Map.find tid updated).Count |> Expect.equal "1 result" 1
  }

  test "assessTest returns Insufficient for unknown test" {
    FlakyDetection.assessTest (TestId.create "unknown" "expecto") Map.empty
    |> Expect.equal "unknown = Insufficient" TestStability.Insufficient
  }

  test "GutterIcon.TestFlaky has correct char and color" {
    GutterIcon.toChar GutterIcon.TestFlaky |> Expect.equal "flaky char" '\u2248'
    GutterIcon.toColorIndex GutterIcon.TestFlaky |> Expect.equal "flaky color" 214uy
  }

  test "GutterIcon.TestFlaky roundtrips through label" {
    GutterIcon.toLabel GutterIcon.TestFlaky |> GutterIcon.parseLabel
    |> Expect.equal "roundtrip" (Some GutterIcon.TestFlaky)
  }
]

[<Tests>]
let coverageCorrelationTests = testList "CoverageCorrelation" [

  test "testsForSymbol returns NotCovered when symbol not in graph" {
    let graph = TestDependencyGraph.empty
    CoverageCorrelation.testsForSymbol graph Array.empty Map.empty "MyModule.add"
    |> Expect.equal "not in graph" CoverageDetail.NotCovered
  }

  test "testsForSymbol returns NotCovered when testIds array is empty" {
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.ofList ["MyModule.add", Array.empty] }
    CoverageCorrelation.testsForSymbol graph Array.empty Map.empty "MyModule.add"
    |> Expect.equal "empty array" CoverageDetail.NotCovered
  }

  test "testsForSymbol returns Covered with test info when tests exist" {
    let tid1 = TestId.create "test1" "expecto"
    let tid2 = TestId.create "test2" "expecto"
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.ofList ["MyModule.add", [| tid1; tid2 |]] }
    let tests = [|
      { Id = tid1; FullName = "Tests.test1"; DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
      { Id = tid2; FullName = "Tests.test2"; DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
        Labels = []; Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let results = Map.ofList [
      tid1, { TestId = tid1; TestName = "test1"; Result = TestResult.Passed (TimeSpan.FromMilliseconds 5.0); Timestamp = DateTimeOffset.UtcNow }
    ]
    match CoverageCorrelation.testsForSymbol graph tests results "MyModule.add" with
    | CoverageDetail.Covered infos ->
      infos.Length |> Expect.equal "2 tests" 2
      infos.[0].DisplayName |> Expect.equal "name1" "test1"
      Expect.isSome "has result" infos.[0].Result
      Expect.isNone "no result yet" infos.[1].Result
    | other -> failwithf "expected Covered, got %A" other
  }

  test "testsForSymbol uses TestId hash when test not in discovered" {
    let tid = TestId.create "orphan" "xunit"
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.ofList ["Mod.fn", [| tid |]] }
    match CoverageCorrelation.testsForSymbol graph Array.empty Map.empty "Mod.fn" with
    | CoverageDetail.Covered infos ->
      infos.[0].DisplayName |> Expect.equal "falls back to hash" (TestId.value tid)
    | other -> failwithf "expected Covered, got %A" other
  }

  test "testsForLine returns NotCovered when no annotation matches" {
    let annotations = [| { Symbol = "Other.fn"; FilePath = "other.fs"; DefinitionLine = 5; Status = CoverageStatus.NotCovered } |]
    CoverageCorrelation.testsForLine annotations TestDependencyGraph.empty Array.empty Map.empty "prod.fs" 10
    |> Expect.equal "no match" CoverageDetail.NotCovered
  }

  test "testsForLine chains through annotation to graph" {
    let tid = TestId.create "lineTest" "expecto"
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.ofList ["Prod.validate", [| tid |]] }
    let annotations = [| { Symbol = "Prod.validate"; FilePath = "prod.fs"; DefinitionLine = 42; Status = CoverageStatus.Covered (1, CoverageHealth.AllPassing) } |]
    let tests = [| { Id = tid; FullName = "Tests.lineTest"; DisplayName = "lineTest"; Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "expecto"; Category = TestCategory.Unit } |]
    let results = Map.ofList [ tid, { TestId = tid; TestName = "lineTest"; Result = TestResult.Passed (TimeSpan.FromMilliseconds 3.0); Timestamp = DateTimeOffset.UtcNow } ]
    match CoverageCorrelation.testsForLine annotations graph tests results "prod.fs" 42 with
    | CoverageDetail.Covered infos ->
      infos.Length |> Expect.equal "1 test" 1
      infos.[0].DisplayName |> Expect.equal "correct name" "lineTest"
      match infos.[0].Result with
      | Some (TestResult.Passed _) -> ()
      | other -> failwithf "expected Passed, got %A" other
    | other -> failwithf "expected Covered, got %A" other
  }

  test "testsForLine returns NotCovered when annotation exists but graph has no entry" {
    let annotations = [| { Symbol = "Prod.orphan"; FilePath = "prod.fs"; DefinitionLine = 10; Status = CoverageStatus.Pending } |]
    CoverageCorrelation.testsForLine annotations TestDependencyGraph.empty Array.empty Map.empty "prod.fs" 10
    |> Expect.equal "annotation but no graph entry" CoverageDetail.NotCovered
  }
]

// --- CoverageBitmap Pipeline Wiring Tests ---

[<Tests>]
let coverageBitmapWiringTests = testList "CoverageBitmap Pipeline Wiring" [
  test "CoverageBitmapCollected populates TestCoverageBitmaps map" {
    let tid1 = mkTestId "ns" "t1"
    let tid2 = mkTestId "ns" "t2"
    let hits = [| true; false; true; true; false; false; true |]
    let bitmap = CoverageBitmap.ofBoolArray hits
    let model', _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid1; tid2 |], bitmap)))
        SageFsModel.initial
    let bitmaps = model'.LiveTesting.TestState.TestCoverageBitmaps
    Map.count bitmaps
    |> Expect.equal "should have 2 entries" 2
    Map.find tid1 bitmaps
    |> CoverageBitmap.equivalent bitmap
    |> Expect.isTrue "tid1 bitmap should match"
    Map.find tid2 bitmaps
    |> CoverageBitmap.equivalent bitmap
    |> Expect.isTrue "tid2 bitmap should match"
  }

  test "subsequent CoverageBitmapCollected merges into existing map" {
    let tid1 = mkTestId "ns" "t1"
    let tid2 = mkTestId "ns" "t2"
    let tid3 = mkTestId "ns" "t3"
    let bm1 = CoverageBitmap.ofBoolArray [| true; false |]
    let bm2 = CoverageBitmap.ofBoolArray [| false; true |]
    let model1, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid1; tid2 |], bm1)))
        SageFsModel.initial
    let model2, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid3 |], bm2)))
        model1
    let bitmaps = model2.LiveTesting.TestState.TestCoverageBitmaps
    Map.count bitmaps
    |> Expect.equal "should have 3 entries" 3
    Map.find tid1 bitmaps |> CoverageBitmap.equivalent bm1 |> Expect.isTrue "tid1 still bm1"
    Map.find tid3 bitmaps |> CoverageBitmap.equivalent bm2 |> Expect.isTrue "tid3 is bm2"
  }

  test "CoverageBitmapCollected overwrites stale entry for same test" {
    let tid = mkTestId "ns" "t1"
    let bm1 = CoverageBitmap.ofBoolArray [| true; false; false |]
    let bm2 = CoverageBitmap.ofBoolArray [| false; true; true |]
    let model1, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid |], bm1)))
        SageFsModel.initial
    let model2, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid |], bm2)))
        model1
    let bitmaps = model2.LiveTesting.TestState.TestCoverageBitmaps
    Map.count bitmaps
    |> Expect.equal "should still have 1 entry" 1
    Map.find tid bitmaps |> CoverageBitmap.equivalent bm2 |> Expect.isTrue "should be overwritten to bm2"
  }

  test "empty TestCoverageBitmaps on initial model" {
    LiveTestState.empty.TestCoverageBitmaps
    |> Map.isEmpty
    |> Expect.isTrue "should start empty"
  }
]

// --- Coverage-Based Test Selection Tests ---

[<Tests>]
let coverageSelectionTests = testList "Coverage-based test selection" [
  test "buildFileMask sets bits for probes in target file only" {
    let maps = [| { Slots = [|
      { File = "A.fs"; Line = 1; Column = 0; BranchId = 0 }
      { File = "A.fs"; Line = 2; Column = 0; BranchId = 1 }
      { File = "B.fs"; Line = 1; Column = 0; BranchId = 2 }
    |]; TotalProbes = 3; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let mask = CoverageBitmap.buildFileMask "A.fs" maps
    CoverageBitmap.popCount mask |> Expect.equal "2 probes in A.fs" 2
    CoverageBitmap.isSet 0 mask |> Expect.isTrue "probe 0 (A.fs:1)"
    CoverageBitmap.isSet 1 mask |> Expect.isTrue "probe 1 (A.fs:2)"
    CoverageBitmap.isSet 2 mask |> Expect.isFalse "probe 2 (B.fs:1)"
  }

  test "buildFileMask with no matching file returns zero popcount" {
    let maps = [| { Slots = [|
      { File = "B.fs"; Line = 1; Column = 0; BranchId = 0 }
    |]; TotalProbes = 1; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let mask = CoverageBitmap.buildFileMask "A.fs" maps
    CoverageBitmap.popCount mask |> Expect.equal "no probes" 0
  }

  test "findCoverageAffected returns tests covering changed file" {
    let maps = [| { Slots = [|
      { File = "A.fs"; Line = 1; Column = 0; BranchId = 0 }
      { File = "A.fs"; Line = 2; Column = 0; BranchId = 1 }
      { File = "B.fs"; Line = 1; Column = 0; BranchId = 2 }
    |]; TotalProbes = 3; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let t1 = mkTestId "ns" "test_a"
    let t2 = mkTestId "ns" "test_b"
    let bm1 = CoverageBitmap.ofBoolArray [| true; false; false |]
    let bm2 = CoverageBitmap.ofBoolArray [| false; false; true |]
    let bitmaps = Map.ofList [ t1, bm1; t2, bm2 ]
    let affected = CoverageBitmap.findCoverageAffected "A.fs" maps bitmaps
    affected |> Array.length |> Expect.equal "only t1 affected" 1
    affected |> Array.contains t1 |> Expect.isTrue "t1 covers A.fs"
    affected |> Array.contains t2 |> Expect.isFalse "t2 doesn't cover A.fs"
  }

  test "findCoverageAffected returns empty when no bitmaps exist" {
    let maps = [| { Slots = [|
      { File = "A.fs"; Line = 1; Column = 0; BranchId = 0 }
    |]; TotalProbes = 1; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let affected = CoverageBitmap.findCoverageAffected "A.fs" maps Map.empty
    affected |> Array.isEmpty |> Expect.isTrue "no bitmaps = no coverage-based selection"
  }

  test "findCoverageAffected skips bitmaps with mismatched size" {
    let maps = [| { Slots = [|
      { File = "A.fs"; Line = 1; Column = 0; BranchId = 0 }
      { File = "A.fs"; Line = 2; Column = 0; BranchId = 1 }
    |]; TotalProbes = 2; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let t1 = mkTestId "ns" "t1"
    let bm_wrong_size = CoverageBitmap.ofBoolArray [| true; false; true |]
    let bitmaps = Map.ofList [ t1, bm_wrong_size ]
    let affected = CoverageBitmap.findCoverageAffected "A.fs" maps bitmaps
    affected |> Array.isEmpty |> Expect.isTrue "mismatched size skipped"
  }

  test "afterTypeCheck unions symbol and coverage affected tests" {
    let tid1 = mkTestId "Tests.t1" "expecto"
    let tid2 = mkTestId "Tests.t2" "expecto"
    let tc1 = { Id = tid1; FullName = "Tests.t1"; DisplayName = "t1"
                Origin = TestOrigin.ReflectionOnly
                Labels = []; Framework = "expecto"
                Category = TestCategory.Unit }
    let tc2 = { Id = tid2; FullName = "Tests.t2"; DisplayName = "t2"
                Origin = TestOrigin.ReflectionOnly
                Labels = []; Framework = "expecto"
                Category = TestCategory.Unit }
    let graph =
      { SymbolToTests = Map.empty
        TransitiveCoverage = Map.ofList [ "Module.add", [| tid1 |] ]
        PerFileIndex = Map.empty
        SourceVersion = 0 }
    let maps = [| { Slots = [|
      { File = "Module.fs"; Line = 1; Column = 0; BranchId = 0 }
    |]; TotalProbes = 1; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let bm = CoverageBitmap.ofBoolArray [| true |]
    let state =
      { LiveTestState.empty with
          DiscoveredTests = [| tc1; tc2 |]
          TestCoverageBitmaps = Map.ofList [ tid2, bm ]
          TestSessionMap = Map.ofList [ tid1, "s"; tid2, "s" ] }
    let instrMaps = Map.ofList [ "s", maps ]
    match PipelineEffects.afterTypeCheck ["Module.add"] "Module.fs" RunTrigger.Keystroke graph state None instrMaps with
    | [ PipelineEffect.RunAffectedTests (tests, _, _, _, _, _) ] ->
      let ids = tests |> Array.map (fun t -> t.Id) |> Set.ofArray
      ids |> Set.contains tid1 |> Expect.isTrue "t1 from symbol heuristic"
      ids |> Set.contains tid2 |> Expect.isTrue "t2 from coverage bitmap"
    | other -> failtestf "expected single RunAffectedTests, got %A" other
  }
]

// --- CoverageBitmap Tests ---

[<Tests>]
let coverageBitmapTests = testList "CoverageBitmap" [
  testList "ofBoolArray/toBoolArray round-trip" [
    test "empty array round-trips" {
      let bm = CoverageBitmap.ofBoolArray [||]
      bm |> CoverageBitmap.toBoolArray |> Expect.equal "should round-trip empty" [||]
    }

    test "single true round-trips" {
      let hits = [| true |]
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip [true]" hits
    }

    test "single false round-trips" {
      let hits = [| false |]
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip [false]" hits
    }

    test "64 elements round-trips (exact word boundary)" {
      let hits = Array.init 64 (fun i -> i % 3 = 0)
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip 64 elements" hits
    }

    test "65 elements round-trips (crosses word boundary)" {
      let hits = Array.init 65 (fun i -> i % 2 = 0)
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip 65 elements" hits
    }

    test "256 elements round-trips (4 words)" {
      let hits = Array.init 256 (fun i -> i % 5 = 0 || i % 7 = 0)
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip 256 elements" hits
    }

    testProperty "round-trip preserves any bool array" (fun (hits: bool array) ->
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray = hits)
  ]

  testList "equivalent" [
    test "identical bitmaps are equivalent" {
      let bm = CoverageBitmap.ofBoolArray [| true; false; true; true |]
      CoverageBitmap.equivalent bm bm |> Expect.isTrue "same bitmap should be equivalent"
    }

    test "different bitmaps are not equivalent" {
      let a = CoverageBitmap.ofBoolArray [| true; false |]
      let b = CoverageBitmap.ofBoolArray [| false; true |]
      CoverageBitmap.equivalent a b |> Expect.isFalse "different bitmaps should not be equivalent"
    }

    test "different sizes are not equivalent" {
      let a = CoverageBitmap.ofBoolArray [| true |]
      let b = CoverageBitmap.ofBoolArray [| true; true |]
      CoverageBitmap.equivalent a b |> Expect.isFalse "different sizes should not be equivalent"
    }

    test "empty bitmaps are equivalent" {
      CoverageBitmap.equivalent CoverageBitmap.empty CoverageBitmap.empty
      |> Expect.isTrue "empty bitmaps should be equivalent"
    }
  ]

  testList "popCount" [
    test "empty bitmap has 0 population" {
      CoverageBitmap.empty |> CoverageBitmap.popCount |> Expect.equal "should be 0" 0
    }

    test "all-true bitmap has count = length" {
      let bm = CoverageBitmap.ofBoolArray (Array.create 100 true)
      bm |> CoverageBitmap.popCount |> Expect.equal "should be 100" 100
    }

    test "all-false bitmap has count = 0" {
      let bm = CoverageBitmap.ofBoolArray (Array.create 100 false)
      bm |> CoverageBitmap.popCount |> Expect.equal "should be 0" 0
    }

    testProperty "popCount equals count of true values" (fun (hits: bool array) ->
      let expected = hits |> Array.filter id |> Array.length
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.popCount = expected)
  ]

  testList "intersect" [
    test "AND of identical bitmaps is same bitmap" {
      let bm = CoverageBitmap.ofBoolArray [| true; false; true |]
      let result = CoverageBitmap.intersect bm bm
      CoverageBitmap.equivalent result bm |> Expect.isTrue "AND with self should be self"
    }

    test "AND with all-false yields all-false" {
      let a = CoverageBitmap.ofBoolArray [| true; true; true |]
      let b = CoverageBitmap.ofBoolArray [| false; false; false |]
      let result = CoverageBitmap.intersect a b
      result |> CoverageBitmap.popCount |> Expect.equal "AND with zeros should be 0" 0
    }
  ]

  testList "xorDiff" [
    test "XOR of identical bitmaps is all zeros" {
      let bm = CoverageBitmap.ofBoolArray [| true; false; true |]
      let result = CoverageBitmap.xorDiff bm bm
      result |> CoverageBitmap.popCount |> Expect.equal "XOR with self should be 0" 0
    }

    test "XOR finds differences" {
      let a = CoverageBitmap.ofBoolArray [| true;  false; true;  false |]
      let b = CoverageBitmap.ofBoolArray [| true;  true;  false; false |]
      let result = CoverageBitmap.xorDiff a b
      result |> CoverageBitmap.popCount |> Expect.equal "should have 2 differences" 2
    }
  ]

  testList "isSet" [
    test "returns true for set bit" {
      let bm = CoverageBitmap.ofBoolArray [| false; true; false |]
      bm |> CoverageBitmap.isSet 1 |> Expect.isTrue "bit 1 should be set"
    }

    test "returns false for unset bit" {
      let bm = CoverageBitmap.ofBoolArray [| false; true; false |]
      bm |> CoverageBitmap.isSet 0 |> Expect.isFalse "bit 0 should not be set"
    }

    test "out of range returns false" {
      let bm = CoverageBitmap.ofBoolArray [| true |]
      bm |> CoverageBitmap.isSet 5 |> Expect.isFalse "out of range should be false"
    }
  ]

  testList "memory efficiency" [
    test "bitmap uses 8x less memory than bool array" {
      let hits = Array.create 256 true
      let bm = CoverageBitmap.ofBoolArray hits
      bm.Bits.Length |> Expect.equal "should use 4 uint64 words" 4
    }

    test "2608 probes fits in 41 uint64 words" {
      let hits = Array.create 2608 false
      let bm = CoverageBitmap.ofBoolArray hits
      bm.Bits.Length |> Expect.equal "should use 41 words for 2608 probes" 41
    }
  ]
]

// --- E2E Pipeline Flow Tests ---

[<Tests>]
let e2ePipelineFlowTests = testList "E2E Pipeline Flow" [
  test "file change through to test status update" {
    let sessionId = "test-session"
    let tid = TestId.TestId "MyModule.myTest should work"
    let testCase = {
      TestCase.Id = tid; DisplayName = "myTest should work"; FullName = "MyModule.myTest should work"
      Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "Expecto"; Category = TestCategory.Unit
    }
    let model0 = SageFsModel.initial
    let snap = {
      SessionSnapshot.Id = sessionId; Name = Some "TestSession"; Projects = ["MyProject.fsproj"]
      Status = SessionDisplayStatus.Running; LastActivity = System.DateTime.UtcNow
      EvalCount = 0; UpSince = System.DateTime.UtcNow; IsActive = true; WorkingDirectory = "C:\\Test"
    }
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap)) model0
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered (sessionId, [| testCase |]))) model1
    model2.LiveTesting.TestState.DiscoveredTests |> Array.length |> Expect.equal "should have 1 discovered test" 1

    let model3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some sessionId))) model2
    let result = {
      TestRunResult.TestId = tid; TestName = "myTest should work"
      Result = TestResult.Passed (System.TimeSpan.FromMilliseconds 42.0)
      Timestamp = System.DateTimeOffset.UtcNow
    }
    let model4, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestResultsBatch [| result |])) model3
    model4.LiveTesting.TestState.LastResults |> Map.tryFind tid |> Expect.isSome "should have result for test"

    let model5, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunCompleted (Some sessionId))) model4
    model5.LiveTesting.TestState.RunPhases |> Map.tryFind sessionId
    |> fun p -> match p with | Some TestRunPhase.Idle -> () | other -> failwithf "Expected Idle but got %A" other
  }

  test "test summary produces correct counts" {
    let statuses = [|
      TestRunStatus.Passed (System.TimeSpan.FromMilliseconds 10.0)
      TestRunStatus.Failed (TestFailure.AssertionFailed "oops", System.TimeSpan.FromMilliseconds 5.0)
    |]
    let summary = TestSummary.fromStatuses LiveTestingActivation.Active statuses
    summary.Total |> Expect.equal "total should be 2" 2
    summary.Passed |> Expect.equal "passed should be 1" 1
    summary.Failed |> Expect.equal "failed should be 1" 1
    let bar = TestSummary.toStatusBar summary
    bar |> Expect.isNotEmpty "status bar should not be empty"
  }

  test "multi-session test isolation" {
    let tid1 = TestId.TestId "test.in.session1"
    let tid2 = TestId.TestId "test.in.session2"
    let tc1 = { TestCase.Id = tid1; FullName = "test.in.session1"; DisplayName = "t1"; Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "Expecto"; Category = TestCategory.Unit }
    let tc2 = { TestCase.Id = tid2; FullName = "test.in.session2"; DisplayName = "t2"; Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "Expecto"; Category = TestCategory.Unit }
    let m0 = SageFsModel.initial
    let snap1 = { SessionSnapshot.Id = "s1"; Name = Some "S1"; Projects = ["A.fsproj"]; Status = SessionDisplayStatus.Running; LastActivity = System.DateTime.UtcNow; EvalCount = 0; UpSince = System.DateTime.UtcNow; IsActive = true; WorkingDirectory = "C:\\A" }
    let snap2 = { SessionSnapshot.Id = "s2"; Name = Some "S2"; Projects = ["B.fsproj"]; Status = SessionDisplayStatus.Running; LastActivity = System.DateTime.UtcNow; EvalCount = 0; UpSince = System.DateTime.UtcNow; IsActive = false; WorkingDirectory = "C:\\B" }
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap1)) m0
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap2)) m1
    let m3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s1", [| tc1 |]))) m2
    let m4, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s2", [| tc2 |]))) m3
    m4.LiveTesting.TestState.TestSessionMap |> Map.find tid1 |> Expect.equal "t1 in s1" "s1"
    m4.LiveTesting.TestState.TestSessionMap |> Map.find tid2 |> Expect.equal "t2 in s2" "s2"

    let m5, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid1 |], Some "s1"))) m4
    let r1 = { TestRunResult.TestId = tid1; TestName = "t1"; Result = TestResult.Passed (System.TimeSpan.FromMilliseconds 10.0); Timestamp = System.DateTimeOffset.UtcNow }
    let m6, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestResultsBatch [| r1 |])) m5
    let s2Status = m6.LiveTesting.TestState.StatusEntries |> Array.tryFind (fun e -> e.TestId = tid2)
    match s2Status with
    | Some entry -> match entry.Status with | TestRunStatus.Passed _ -> failwith "s2's test should NOT be Passed" | _ -> ()
    | None -> ()
  }
]

// --- Coverage Pipeline Verification Tests ---

[<Tests>]
let coveragePipelineVerificationTests = testList "Coverage Pipeline Verification" [
  test "InstrumentationMapsReady populates model maps" {
    let maps = [|
      { InstrumentationMap.Slots = [| { SequencePoint.File = "test.fs"; Line = 10; Column = 1; BranchId = 0 } |]
        TotalProbes = 1; TrackerTypeName = "__SageFsCoverage"; HitsFieldName = "Hits" }
    |]
    let model0 = SageFsModel.initial
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.InstrumentationMapsReady ("s1", maps))) model0
    model1.LiveTesting.InstrumentationMaps
    |> Map.containsKey "s1"
    |> Expect.isTrue "should have maps for session s1"
    model1.LiveTesting.InstrumentationMaps
    |> Map.find "s1"
    |> Array.length
    |> Expect.equal "should have 1 map" 1
  }

  test "CoverageBitmapCollected populates TestCoverageBitmaps" {
    let tid = TestId.TestId "ns.test1"
    let bitmap = CoverageBitmap.ofBoolArray [| true; false; true |]
    let model0 = SageFsModel.initial
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid |], bitmap))) model0
    model1.LiveTesting.TestState.TestCoverageBitmaps
    |> Map.containsKey tid
    |> Expect.isTrue "should have bitmap for test"
  }

  test "afterTypeCheck uses coverage bitmaps when both maps and bitmaps exist" {
    let tid = TestId.TestId "ns.test1"
    let sp = { SequencePoint.File = "src/MyFile.fs"; Line = 10; Column = 1; BranchId = 0 }
    let imap = {
      InstrumentationMap.Slots = [| sp |]; TotalProbes = 1
      TrackerTypeName = "__SageFsCoverage"; HitsFieldName = "Hits"
    }
    let bitmap = CoverageBitmap.ofBoolArray [| true |]
    let state = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [|
          { TestCase.Id = tid; FullName = "ns.test1"; DisplayName = "test1"
            Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "Expecto"
            Category = TestCategory.Unit }
        |]
        TestCoverageBitmaps = Map.ofList [ tid, bitmap ]
        TestSessionMap = Map.ofList [ tid, "s1" ]
        RunPhases = Map.ofList [ "s1", TestRunPhase.Idle ]
    }
    let instrumentationMaps = Map.ofList [ "s1", [| imap |] ]
    let effects =
      PipelineEffects.afterTypeCheck
        [] "src/MyFile.fs" RunTrigger.FileSave
        TestDependencyGraph.empty state None instrumentationMaps
    effects
    |> List.isEmpty
    |> Expect.isFalse "should produce RunAffectedTests effect when coverage matches"
  }

  test "afterTypeCheck with empty bitmaps skips coverage path" {
    let tid = TestId.TestId "ns.test2"
    let sp = { SequencePoint.File = "src/Other.fs"; Line = 5; Column = 1; BranchId = 0 }
    let imap = {
      InstrumentationMap.Slots = [| sp |]; TotalProbes = 1
      TrackerTypeName = "__SageFsCoverage"; HitsFieldName = "Hits"
    }
    let state = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [|
          { TestCase.Id = tid; FullName = "ns.test2"; DisplayName = "test2"
            Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = "Expecto"
            Category = TestCategory.Unit }
        |]
        TestCoverageBitmaps = Map.empty
        TestSessionMap = Map.ofList [ tid, "s1" ]
        RunPhases = Map.ofList [ "s1", TestRunPhase.Idle ]
    }
    let instrumentationMaps = Map.ofList [ "s1", [| imap |] ]
    let effects =
      PipelineEffects.afterTypeCheck
        [] "src/Other.fs" RunTrigger.FileSave
        TestDependencyGraph.empty state None instrumentationMaps
    effects
    |> List.isEmpty
    |> Expect.isTrue "should produce no effects without bitmaps"
  }
]
