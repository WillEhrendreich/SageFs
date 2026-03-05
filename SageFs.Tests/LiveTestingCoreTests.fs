module SageFs.Tests.LiveTestingCoreTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

// --- TestId Tests (GREEN — already correct) ---

[<Tests>]
let testIdTests = testList "TestId" [
  test "create produces stable 16-char hex id" {
    let id = TestId.create "MyModule.Tests.should add" TestFramework.XUnit
    TestId.value id
    |> Expect.hasLength "should be 16 chars" 16
  }

  test "different fullnames produce different ids" {
    let id1 = TestId.create "Test.A" TestFramework.XUnit
    let id2 = TestId.create "Test.B" TestFramework.XUnit
    TestId.value id1
    |> Expect.notEqual "different names should differ" (TestId.value id2)
  }

  test "different frameworks produce different ids" {
    let id1 = TestId.create "Test.A" TestFramework.XUnit
    let id2 = TestId.create "Test.A" TestFramework.NUnit
    TestId.value id1
    |> Expect.notEqual "different frameworks should differ" (TestId.value id2)
  }

  test "value extracts the string" {
    let id = TestId.create "test" (TestFramework.Unknown "fw")
    TestId.value id
    |> Expect.isNotEmpty "should extract string"
  }
]

// --- filterByPolicy Tests (RED — stub returns all) ---

[<Tests>]
let filterByPolicyTests = testList "filterByPolicy" [
  test "keystroke trigger only runs OnEveryChange tests" {
    let unit = mkTestCase "Fast.test" (TestFramework.Unknown "x") TestCategory.Unit
    let integ = mkTestCase "Slow.test" (TestFramework.Unknown "x") TestCategory.Integration
    let policies = Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Integration, RunPolicy.OnDemand
    ]
    LiveTesting.filterByPolicy policies RunTrigger.Keystroke [| unit; integ |]
    |> Expect.hasLength "only unit test" 1
  }

  test "file save trigger runs OnEveryChange and OnSaveOnly tests" {
    let unit = mkTestCase "Fast.test" (TestFramework.Unknown "x") TestCategory.Unit
    let arch = mkTestCase "Arch.test" (TestFramework.Unknown "x") TestCategory.Architecture
    let integ = mkTestCase "Slow.test" (TestFramework.Unknown "x") TestCategory.Integration
    let policies = Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Architecture, RunPolicy.OnSaveOnly
      TestCategory.Integration, RunPolicy.OnDemand
    ]
    LiveTesting.filterByPolicy policies RunTrigger.FileSave [| unit; arch; integ |]
    |> Expect.hasLength "unit + architecture" 2
  }

  test "explicit run triggers all non-disabled tests" {
    let unit = mkTestCase "Fast.test" (TestFramework.Unknown "x") TestCategory.Unit
    let integ = mkTestCase "Slow.test" (TestFramework.Unknown "x") TestCategory.Integration
    let disabled = mkTestCase "Off.test" (TestFramework.Unknown "x") TestCategory.Benchmark
    let policies = Map.ofList [
      TestCategory.Unit, RunPolicy.OnEveryChange
      TestCategory.Integration, RunPolicy.OnDemand
      TestCategory.Benchmark, RunPolicy.Disabled
    ]
    LiveTesting.filterByPolicy policies RunTrigger.ExplicitRun [| unit; integ; disabled |]
    |> Expect.hasLength "all except disabled" 2
  }

  test "disabled category blocks all triggers" {
    let tc = mkTestCase "Disabled.test" (TestFramework.Unknown "x") TestCategory.Unit
    let policies = Map.ofList [ TestCategory.Unit, RunPolicy.Disabled ]
    for trigger in [ RunTrigger.Keystroke; RunTrigger.FileSave; RunTrigger.ExplicitRun ] do
      LiveTesting.filterByPolicy policies trigger [| tc |]
      |> Expect.hasLength "disabled blocks all" 0
  }

  test "property tests run on every change by default" {
    let tc = mkTestCase "Prop.test" (TestFramework.Unknown "x") TestCategory.Property
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
    let tc = mkTestCase "Integ.test" (TestFramework.Unknown "x") TestCategory.Integration
    let policies = Map.ofList [ TestCategory.Integration, RunPolicy.OnDemand ]
    LiveTesting.filterByPolicy policies RunTrigger.Keystroke [| tc |]
    |> Expect.hasLength "OnDemand blocked on keystroke" 0
  }

  test "OnDemand category blocked on file save" {
    let tc = mkTestCase "Integ.test" (TestFramework.Unknown "x") TestCategory.Integration
    let policies = Map.ofList [ TestCategory.Integration, RunPolicy.OnDemand ]
    LiveTesting.filterByPolicy policies RunTrigger.FileSave [| tc |]
    |> Expect.hasLength "OnDemand blocked on save" 0
  }
]

// --- mergeResults Tests (RED — stub returns state unchanged) ---

[<Tests>]
let mergeResultsTests = testList "mergeResults" [
  test "merging results updates LastResults map" {
    let tid = mkTestId "t1" (TestFramework.Unknown "x")
    let results = [| mkResult tid (TestResult.Passed (ts 5.0)) |]
    let state = LiveTesting.mergeResults LiveTestState.empty results
    state.LastResults
    |> Map.containsKey tid
    |> Expect.isTrue "should contain result"
  }

  test "merging results preserves RunPhase (no transition)" {
    let tid = mkTestId "t1" (TestFramework.Unknown "x")
    let gen = RunGeneration.next RunGeneration.zero
    let running = { LiveTestState.empty with RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen }
    let results = [| mkResult tid (TestResult.Passed (ts 5.0)) |]
    LiveTesting.mergeResults running results
    |> fun s -> TestRunPhase.isAnyRunning s.RunPhases
    |> Expect.isTrue "should still be running"
  }

  test "merging results updates History to PreviousRun" {
    let tid = mkTestId "t1" (TestFramework.Unknown "x")
    let results = [| mkResult tid (TestResult.Passed (ts 10.0)) |]
    let state = LiveTesting.mergeResults LiveTestState.empty results
    match state.History with
    | RunHistory.PreviousRun _ -> ()
    | RunHistory.NeverRun -> failtest "should be PreviousRun"
  }

  test "newer result overwrites older for same TestId" {
    let tid = mkTestId "t1" (TestFramework.Unknown "x")
    let old = [| mkResult tid (TestResult.Passed (ts 5.0)) |]
    let state1 = LiveTesting.mergeResults LiveTestState.empty old
    let newer = [| mkResult tid (TestResult.Failed (TestFailure.AssertionFailed "oops", ts 3.0)) |]
    let state2 = LiveTesting.mergeResults state1 newer
    match state2.LastResults |> Map.find tid |> fun r -> r.Result with
    | TestResult.Failed _ -> ()
    | _ -> failtest "should be Failed after overwrite"
  }

  test "merging empty results preserves state" {
    let tid = mkTestId "t1" (TestFramework.Unknown "x")
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
      mkTestCase "t1" (TestFramework.Unknown "x") TestCategory.Unit
      mkTestCase "t2" (TestFramework.Unknown "x") TestCategory.Unit
    |]
    let state = { LiveTestState.empty with DiscoveredTests = tests }
    LiveTesting.computeStatusEntries state
    |> Expect.hasLength "one per test" 2
  }

  test "test with no result shows Detected" {
    let tc = mkTestCase "t1" (TestFramework.Unknown "x") TestCategory.Unit
    let state = { LiveTestState.empty with DiscoveredTests = [| tc |] }
    let entries = LiveTesting.computeStatusEntries state
    match entries.[0].Status with
    | TestRunStatus.Detected -> ()
    | other -> failtestf "expected Detected, got %A" other
  }

  test "passed test shows Passed status" {
    let tc = mkTestCase "t1" (TestFramework.Unknown "x") TestCategory.Unit
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
    let tc = mkTestCase "t1" (TestFramework.Unknown "x") TestCategory.Unit
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
    let tc = mkTestCase "t1" (TestFramework.Unknown "x") TestCategory.Unit
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
    let tc = mkTestCase "t1" (TestFramework.Unknown "x") TestCategory.Unit
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
    let tc = mkTestCase "MyModule.Tests.add" TestFramework.XUnit TestCategory.Unit
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

// --- TestProviderDescriptions Tests (RED — stub returns empty) ---

[<Tests>]
let providerDetectionTests = testList "TestProviderDescriptions" [
  test "detects xunit from referenced assemblies" {
    let asm = mkAssemblyInfo "MyTests" ["xunit.core"; "mscorlib"]
    let providers = TestProviderDescriptions.detectProviders [asm]
    providers
    |> List.exists (fun p ->
      match p with
      | ProviderDescription.AttributeBased d -> d.Name = TestFramework.XUnit
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
      | ProviderDescription.Custom d -> d.Name = TestFramework.Expecto
      | _ -> false)
    |> Expect.isTrue "should detect expecto as custom"
  }

  test "empty assemblies returns empty" {
    TestProviderDescriptions.detectProviders []
    |> Expect.hasLength "no assemblies, no providers" 0
  }

  test "builtIn has 6 providers" {
    TestProviderDescriptions.builtInDescriptions
    |> List.length
    |> Expect.equal "6 built-in" 6
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

  test "starts inactive (demand-only)" {
    LiveTestState.empty.Activation
    |> Expect.equal "inactive by default" LiveTestingActivation.Inactive
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

[<Tests>]
let propertyTests = testList "Property-based" [
  testProperty "TestId.create is deterministic" (fun (name: string) (fw: string) ->
    let name = if isNull name then "" else name
    let fw = TestFramework.parse (if isNull fw then "" else fw)
    let id1 = TestId.create name fw
    let id2 = TestId.create name fw
    TestId.value id1 = TestId.value id2
  )

  testProperty "TestId.value always returns 16 chars" (fun (name: string) (fw: string) ->
    let name = if isNull name then "" else name
    let fw = TestFramework.parse (if isNull fw then "" else fw)
    let id = TestId.create name fw
    (TestId.value id).Length = 16
  )

  testProperty "filterByPolicy with Disabled always returns empty" (fun (cat: int) ->
    let category =
      match abs cat % 6 with
      | 0 -> TestCategory.Unit | 1 -> TestCategory.Integration
      | 2 -> TestCategory.Browser | 3 -> TestCategory.Benchmark
      | 4 -> TestCategory.Architecture | _ -> TestCategory.Property
    let tc = mkTestCase "Prop.test" (TestFramework.Unknown "x") category
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
    let tc = mkTestCase "Prop.test" (TestFramework.Unknown "x") category
    let policies = Map.ofList [ category, RunPolicy.OnEveryChange ]
    let result = LiveTesting.filterByPolicy policies RunTrigger.ExplicitRun [| tc |]
    result.Length = 1
  )

  testProperty "mergeResults never loses existing results" (fun (n: int) ->
    let count = (abs n % 10) + 1
    let results =
      [| for i in 1..count do
           let tid = mkTestId (sprintf "t%d" i) (TestFramework.Unknown "x")
           { TestId = tid; TestName = TestId.value tid
             Result = LTTestResult.Passed (ts 1.0)
             Timestamp = DateTimeOffset.UtcNow; Output = None } |]
    let state = LiveTesting.mergeResults LiveTestState.empty results
    state.LastResults |> Map.count >= count
  )

  testProperty "computeStatusEntries returns same count as DiscoveredTests" (fun (n: int) ->
    let count = abs n % 20
    let tests = [| for i in 1..count -> mkTestCase (sprintf "t%d" i) (TestFramework.Unknown "x") TestCategory.Unit |]
    let state = { LiveTestState.empty with DiscoveredTests = tests }
    let entries = LiveTesting.computeStatusEntries state
    entries.Length = count
  )

  testProperty "findAffected returns subset of all test ids in graph" (fun (sym: string) ->
    let sym = if isNull sym then "x" else sym
    let t1 = mkTestId "t1" (TestFramework.Unknown "x")
    let t2 = mkTestId "t2" (TestFramework.Unknown "x")
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

[<Tests>]
let categoryDetectionTests = testList "CategoryDetection" [
  test "categorizes integration by label" {
    let cat = CategoryDetection.categorize [ "Integration" ] "MyModule.test" TestFramework.Expecto [||]
    cat |> Expect.equal "integration" TestCategory.Integration
  }

  test "categorizes browser by Playwright assembly ref" {
    let cat = CategoryDetection.categorize [] "MyModule.test" TestFramework.Expecto [| "Microsoft.Playwright" |]
    cat |> Expect.equal "browser" TestCategory.Browser
  }

  test "categorizes benchmark by label" {
    let cat = CategoryDetection.categorize [ "Benchmark" ] "MyModule.test" TestFramework.Expecto [||]
    cat |> Expect.equal "benchmark" TestCategory.Benchmark
  }

  test "categorizes by namespace containing 'integration'" {
    let cat = CategoryDetection.categorize [] "MyApp.Integration.Tests.myTest" TestFramework.XUnit [||]
    cat |> Expect.equal "integration by name" TestCategory.Integration
  }

  test "defaults to Unit" {
    let cat = CategoryDetection.categorize [] "MyModule.test" TestFramework.Expecto [||]
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

// --- Serialization Roundtrip Integration Tests ---

[<Tests>]
let serializationRoundtripTests = testList "serialization roundtrip integration" [
  test "LiveTestHookResultDto survives JSON roundtrip" {
    let original : LiveTestHookResultDto = {
      DetectedProviders = [
        ProviderDescription.Custom { Name = TestFramework.Expecto; AssemblyMarker = "Expecto" }
        ProviderDescription.AttributeBased { Name = TestFramework.XUnit; TestAttributes = ["Fact"; "Theory"]; AssemblyMarker = "xunit.core" }
      ]
      DiscoveredTests = [|
        { Id = TestId.create "Test.add" TestFramework.Expecto
          FullName = "Test.add"; DisplayName = "add"
          Origin = TestOrigin.SourceMapped ("test.fs", 10)
          Labels = ["fast"]; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
        { Id = TestId.create "Test.validate" TestFramework.XUnit
          FullName = "Test.validate"; DisplayName = "validate"
          Origin = TestOrigin.ReflectionOnly
          Labels = []; Framework = TestFramework.XUnit; Category = TestCategory.Integration }
      |]
      AffectedTestIds = [| TestId.create "Test.add" TestFramework.Expecto |]
    }

    let json = SageFs.WorkerProtocol.Serialization.serialize original
    let deserialized = SageFs.WorkerProtocol.Serialization.deserialize<LiveTestHookResultDto> json

    deserialized
    |> Expect.equal "roundtrip preserves data" original
  }

  test "full cycle: serialize → deserialize → dispatch → annotations" {
    let hookResult : LiveTestHookResultDto = {
      DetectedProviders = [
        ProviderDescription.Custom { Name = TestFramework.Expecto; AssemblyMarker = "Expecto" }
      ]
      DiscoveredTests = [|
        { Id = TestId.create "Mod.test1" TestFramework.Expecto
          FullName = "Mod.test1"; DisplayName = "test1"
          Origin = TestOrigin.SourceMapped ("Mod.fs", 5)
          Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      |]
      AffectedTestIds = [| TestId.create "Mod.test1" TestFramework.Expecto |]
    }

    let json = SageFs.WorkerProtocol.Serialization.serialize hookResult
    let deserialized = SageFs.WorkerProtocol.Serialization.deserialize<LiveTestHookResultDto> json

    let m0 = (SageFsModel.initial())
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
