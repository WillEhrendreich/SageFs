module SageFs.Tests.LiveTestingCycleTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers
open SageFs.Measures

// --- TestCycleTiming Tests (RED — stub returns 0.0) ---

[<Tests>]
let TestCycleTimingTests = testList "TestCycleTiming" [
  test "treeSitterMs extracts from TreeSitterOnly" {
    let t = {
      Depth = TestCycleDepth.TreeSitterOnly (ts 0.8)
      TotalTests = 10; AffectedTests = 3
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    TestCycleTiming.treeSitterMs t
    |> Expect.floatClose "extracts 0.8ms" Accuracy.medium 0.8
  }

  test "treeSitterMs extracts from ThroughFcs" {
    let t = {
      Depth = TestCycleDepth.ThroughFcs (ts 1.2, ts 142.0)
      TotalTests = 10; AffectedTests = 3
      Trigger = RunTrigger.FileSave; Timestamp = DateTimeOffset.UtcNow
    }
    TestCycleTiming.treeSitterMs t
    |> Expect.floatClose "extracts 1.2ms" Accuracy.medium 1.2
  }

  test "treeSitterMs extracts from ThroughExecution" {
    let t = {
      Depth = TestCycleDepth.ThroughExecution (ts 0.5, ts 100.0, ts 87.0)
      TotalTests = 47; AffectedTests = 12
      Trigger = RunTrigger.ExplicitRun; Timestamp = DateTimeOffset.UtcNow
    }
    TestCycleTiming.treeSitterMs t
    |> Expect.floatClose "extracts 0.5ms" Accuracy.medium 0.5
  }
]

[<Tests>]
let TestCycleTimingExtendedTests = testList "TestCycleTiming extended" [
  test "fcsMs returns 0 for tree-sitter only" {
    let t = {
      Depth = TestCycleDepth.TreeSitterOnly (TimeSpan.FromMilliseconds 1.0)
      TotalTests = 0; AffectedTests = 0
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    TestCycleTiming.fcsMs t
    |> Expect.equal "no FCS for tree-sitter only" 0.0
  }

  test "fcsMs returns value for ThroughFcs" {
    let t = {
      Depth = TestCycleDepth.ThroughFcs (TimeSpan.FromMilliseconds 1.0, TimeSpan.FromMilliseconds 142.0)
      TotalTests = 10; AffectedTests = 5
      Trigger = RunTrigger.FileSave; Timestamp = DateTimeOffset.UtcNow
    }
    TestCycleTiming.fcsMs t
    |> Expect.equal "fcs ms" 142.0
  }

  test "totalMs sums all stages" {
    let t = {
      Depth = TestCycleDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 1.0, TimeSpan.FromMilliseconds 100.0, TimeSpan.FromMilliseconds 50.0)
      TotalTests = 10; AffectedTests = 3
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    TestCycleTiming.totalMs t
    |> Expect.equal "total" 151.0
  }

  test "toStatusBar tree-sitter only" {
    let t = {
      Depth = TestCycleDepth.TreeSitterOnly (TimeSpan.FromMilliseconds 0.8)
      TotalTests = 0; AffectedTests = 0
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    TestCycleTiming.toStatusBar t
    |> Expect.equal "format" "TS:0.8ms"
  }

  test "toStatusBar full cycle" {
    let t = {
      Depth = TestCycleDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 0.8, TimeSpan.FromMilliseconds 142.0, TimeSpan.FromMilliseconds 87.0)
      TotalTests = 47; AffectedTests = 12
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    TestCycleTiming.toStatusBar t
    |> Expect.equal "format" "TS:0.8ms | FCS:142ms | Run:87ms (12)"
  }
]


[<Tests>]
let orchestratorTests = testList "TestCycleOrchestrator" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" TestFramework.Expecto; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" TestFramework.Expecto; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let intTest =
    { Id = TestId.create "Module.Tests.intTest" TestFramework.Expecto; FullName = "Module.Tests.intTest"
      DisplayName = "intTest"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Integration }
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
    let d = TestCycleOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | TestCycleDecision.Skip _ -> ()
    | other -> failwithf "Expected Skip, got %A" other
  }

  test "decide skips when already running" {
    let gen = RunGeneration.next RunGeneration.zero
    let state = { baseState with RunPhases = Map.ofList ["s", Running gen]; LastGeneration = gen }
    let d = TestCycleOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | TestCycleDecision.Skip _ -> ()
    | other -> failwithf "Expected Skip, got %A" other
  }

  test "decide returns TreeSitterOnly when no tests discovered" {
    let state = { baseState with DiscoveredTests = [||] }
    let d = TestCycleOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    d |> Expect.equal "tree sitter only" TestCycleDecision.TreeSitterOnly
  }

  test "decide returns FullCycle with affected unit tests on Keystroke" {
    let d = TestCycleOrchestrator.decide baseState RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | TestCycleDecision.FullCycle ids ->
      ids.Length |> Expect.equal "1 affected" 1
      ids.[0] |> Expect.equal "test1" test1.Id
    | other -> failwithf "Expected FullCycle, got %A" other
  }

  test "decide filters integration tests on Keystroke" {
    let d = TestCycleOrchestrator.decide baseState RunTrigger.Keystroke [ "Module.dbCall" ] depGraph
    match d with
    | TestCycleDecision.TreeSitterOnly -> ()
    | other -> failwithf "Expected TreeSitterOnly (integration filtered), got %A" other
  }

  test "decide returns TreeSitterOnly when all affected tests filtered by policy" {
    let state = {
      baseState with
        RunPolicies = Map.ofList [ TestCategory.Unit, RunPolicy.OnDemand ]
    }
    let d = TestCycleOrchestrator.decide state RunTrigger.Keystroke [ "Module.add" ] depGraph
    match d with
    | TestCycleDecision.TreeSitterOnly -> ()
    | other -> failwithf "Expected TreeSitterOnly, got %A" other
  }

  test "decide includes integration tests on ExplicitRun" {
    let d = TestCycleOrchestrator.decide baseState RunTrigger.ExplicitRun [ "Module.dbCall" ] depGraph
    match d with
    | TestCycleDecision.FullCycle ids ->
      ids.Length |> Expect.equal "1 integration" 1
      ids.[0] |> Expect.equal "intTest" intTest.Id
    | other -> failwithf "Expected FullCycle, got %A" other
  }

  test "buildRunBatch returns matching test cases" {
    let batch = TestCycleOrchestrator.buildRunBatch baseState [| test1.Id |]
    batch.Length |> Expect.equal "1 test" 1
    batch.[0].FullName |> Expect.equal "test1" "Module.Tests.test1"
  }

  test "buildRunBatch filters out unknown IDs" {
    let unknownId = TestId.create "Unknown.test" TestFramework.XUnit
    let batch = TestCycleOrchestrator.buildRunBatch baseState [| unknownId |]
    batch |> Expect.isEmpty "no matches"
  }
]

// ============================================================
// cycle Status Bar Tests
// ============================================================

[<Tests>]
let cycleStatusBarTests = testList "cycle Status Bar" [
  test "full cycle timing formats correctly" {
    let timing = {
      Depth = TestCycleDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 0.8,
        TimeSpan.FromMilliseconds 142.0,
        TimeSpan.FromMilliseconds 87.0)
      TotalTests = 100; AffectedTests = 12
      Trigger = RunTrigger.Keystroke
      Timestamp = DateTimeOffset.UtcNow
    }
    let bar = TestCycleTiming.toStatusBar timing
    bar |> Expect.stringContains "TS" "TS:"
    bar |> Expect.stringContains "FCS" "FCS:"
    bar |> Expect.stringContains "Run" "Run:"
    bar |> Expect.stringContains "tests" "12"
  }

  test "tree-sitter only timing shows partial" {
    let timing = {
      Depth = TestCycleDepth.TreeSitterOnly (TimeSpan.FromMilliseconds 0.5)
      TotalTests = 100; AffectedTests = 0
      Trigger = RunTrigger.Keystroke; Timestamp = DateTimeOffset.UtcNow
    }
    let bar = TestCycleTiming.toStatusBar timing
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
    { Id = TestId.create "Module.Tests.test1" TestFramework.Expecto; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" TestFramework.Expecto; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let mkResult tid res =
    { TestId = tid; TestName = ""; Result = res; Timestamp = DateTimeOffset.UtcNow; Output = None }

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

  test "app-boundary finalization transitions merged results from Running to Passed" {
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
    let finalized =
      SageFsUpdate.recomputeStatuses
        { (SageFsModel.initial()).LiveTesting with TestState = state }
        (fun _ -> LiveTesting.mergeResults state newResults)
    let entry = finalized.TestState.StatusEntries |> Array.find (fun e -> e.TestId = test1.Id)
    match entry.Status with
    | TestRunStatus.Passed _ -> ()
    | other -> failwithf "Expected Passed, got %A" other
  }

  test "app-boundary finalization preserves previousStatus across merged results" {
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
    let finalized =
      SageFsUpdate.recomputeStatuses
        { (SageFsModel.initial()).LiveTesting with TestState = state }
        (fun _ -> LiveTesting.mergeResults state newResults)
    let entry = finalized.TestState.StatusEntries |> Array.find (fun e -> e.TestId = test1.Id)
    match entry.PreviousStatus with
    | TestRunStatus.Passed _ -> ()
    | other -> failwithf "Expected previous Passed, got %A" other
  }
]

// ============================================================
// Gutter Annotation Tests
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
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit "hello" 50<ms> t0
    ch.Pending |> Expect.isSome "has pending"
    ch.CurrentGeneration |> Expect.equal "gen 1" 1L
    ch.Pending.Value.Payload |> Expect.equal "payload" "hello"
    ch.Pending.Value.DelayMs |> Expect.equal "delay" 50<ms>
  }

  test "tryFire before delay returns None" {
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit "hello" 50<ms> t0
    let result, ch' = DebounceChannel.tryFire (t0.AddMilliseconds 30.0) ch
    result |> Expect.isNone "not ready yet"
    ch'.Pending |> Expect.isSome "still pending"
  }

  test "tryFire after delay returns payload" {
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit "hello" 50<ms> t0
    let result, ch' = DebounceChannel.tryFire (t0.AddMilliseconds 51.0) ch
    result |> Expect.isSome "ready"
    result.Value |> Expect.equal "payload" "hello"
    ch'.Pending |> Expect.isNone "cleared"
    ch'.LastCompleted |> Expect.isSome "completed set"
  }

  test "newer submit supersedes older pending" {
    let ch =
      DebounceChannel.empty<string>
      |> DebounceChannel.submit "first" 50<ms> t0
      |> DebounceChannel.submit "second" 50<ms> (t0.AddMilliseconds 20.0)
    ch.CurrentGeneration |> Expect.equal "gen 2" 2L
    ch.Pending.Value.Payload |> Expect.equal "latest payload" "second"
  }

  test "stale op is discarded on tryFire" {
    let ch =
      DebounceChannel.empty<string>
      |> DebounceChannel.submit "first" 50<ms> t0
    let ch2 = ch |> DebounceChannel.submit "second" 50<ms> (t0.AddMilliseconds 20.0)
    let staleOp = { Payload = "first"; RequestedAt = t0; DelayMs = 50<ms>; Generation = 1L }
    let ch3 = { ch2 with Pending = Some staleOp }
    let result, ch4 = DebounceChannel.tryFire (t0.AddMilliseconds 60.0) ch3
    result |> Expect.isNone "stale, discarded"
    ch4.Pending |> Expect.isNone "cleared"
  }

  test "isStale detects superseded pending" {
    let ch =
      DebounceChannel.empty<string>
      |> DebounceChannel.submit "first" 50<ms> t0
    let staleOp = { Payload = "first"; RequestedAt = t0; DelayMs = 50<ms>; Generation = 0L }
    let ch2 = { ch with Pending = Some staleOp }
    DebounceChannel.isStale ch2 |> Expect.isTrue "should be stale"
  }

  test "tryFire on empty channel returns None" {
    let result, _ = DebounceChannel.tryFire t0 DebounceChannel.empty<string>
    result |> Expect.isNone "nothing to fire"
  }

  test "exact delay boundary fires" {
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit "hello" 50<ms> t0
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
    let delay = LanguagePrimitives.Int32WithMeasure<ms> (delayMs.Get % 10000 + 1)
    let t0 = DateTimeOffset.UtcNow
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit payload delay t0
    let tFire = t0.AddMilliseconds(float (rawMs delay) + 1.0)
    let result, _ = DebounceChannel.tryFire tFire ch
    result = Some payload
  )

  testProperty "tryFire never fires before delay elapses" (fun (payload: string) (delayMs: FsCheck.PositiveInt) ->
    let delay = LanguagePrimitives.Int32WithMeasure<ms> ((delayMs.Get % 10000) + 2)
    let t0 = DateTimeOffset.UtcNow
    let ch = DebounceChannel.empty<string> |> DebounceChannel.submit payload delay t0
    let tEarly = t0.AddMilliseconds(float (rawMs delay - 2))
    let result, _ = DebounceChannel.tryFire tEarly ch
    result = None
  )

  testProperty "newer submission supersedes older" (fun (p1: string) (p2: string) (delayMs: FsCheck.PositiveInt) ->
    let delay = LanguagePrimitives.Int32WithMeasure<ms> (delayMs.Get % 10000 + 1)
    let t0 = DateTimeOffset.UtcNow
    let ch =
      DebounceChannel.empty<string>
      |> DebounceChannel.submit p1 delay t0
      |> DebounceChannel.submit p2 delay (t0.AddMilliseconds 10.0)
    let tFire = t0.AddMilliseconds(float (rawMs delay) + 100.0)
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
// cycle Debounce Tests
// ============================================================

[<Tests>]
let TestCycleDebounceTests = testList "TestCycleDebounce" [
  let t0 = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)

  test "empty has no pending on either channel" {
    let db = TestCycleDebounce.empty
    db.TreeSitter.Pending |> Expect.isNone "no tree-sitter"
    db.Fcs.Pending |> Expect.isNone "no fcs"
  }

  test "onKeystroke submits to both channels" {
    let db = TestCycleDebounce.empty |> TestCycleDebounce.onKeystroke "code" "file.fs" 300<ms> t0
    db.TreeSitter.Pending |> Expect.isSome "tree-sitter pending"
    db.Fcs.Pending |> Expect.isSome "fcs pending"
    db.TreeSitter.Pending.Value.DelayMs |> Expect.equal "ts delay" 50<ms>
    db.Fcs.Pending.Value.DelayMs |> Expect.equal "fcs delay" 300<ms>
  }

  test "tree-sitter fires before FCS" {
    let db = TestCycleDebounce.empty |> TestCycleDebounce.onKeystroke "code" "file.fs" 300<ms> t0
    let (tsResult, fcsResult), _ = TestCycleDebounce.tick (t0.AddMilliseconds 51.0) db
    tsResult |> Expect.isSome "tree-sitter fires"
    fcsResult |> Expect.isNone "fcs not yet"
  }

  test "both fire after 300ms" {
    let db = TestCycleDebounce.empty |> TestCycleDebounce.onKeystroke "code" "file.fs" 300<ms> t0
    let (tsResult, fcsResult), _ = TestCycleDebounce.tick (t0.AddMilliseconds 301.0) db
    tsResult |> Expect.isSome "tree-sitter fires"
    fcsResult |> Expect.isSome "fcs fires"
  }

  test "rapid keystrokes cancel previous debounce" {
    let db =
      TestCycleDebounce.empty
      |> TestCycleDebounce.onKeystroke "v1" "file.fs" 300<ms> t0
      |> TestCycleDebounce.onKeystroke "v2" "file.fs" 300<ms> (t0.AddMilliseconds 30.0)
      |> TestCycleDebounce.onKeystroke "v3" "file.fs" 300<ms> (t0.AddMilliseconds 60.0)
    let (tsResult, _), _ = TestCycleDebounce.tick (t0.AddMilliseconds 111.0) db
    tsResult |> Expect.isSome "fires for latest"
    tsResult.Value |> Expect.equal "latest content" "v3"
  }

  test "onFileSave uses short FCS delay" {
    let db =
      TestCycleDebounce.empty
      |> TestCycleDebounce.onKeystroke "code" "file.fs" 300<ms> t0
      |> TestCycleDebounce.onFileSave "file.fs" (t0.AddMilliseconds 100.0)
    db.Fcs.Pending.Value.DelayMs |> Expect.equal "save uses short delay" 50<ms>
    let (_, fcsResult), _ = TestCycleDebounce.tick (t0.AddMilliseconds 151.0) db
    fcsResult |> Expect.isSome "fcs fires soon after save"
  }

  test "tick clears fired ops" {
    let db = TestCycleDebounce.empty |> TestCycleDebounce.onKeystroke "code" "file.fs" 300<ms> t0
    let _, db' = TestCycleDebounce.tick (t0.AddMilliseconds 301.0) db
    db'.TreeSitter.Pending |> Expect.isNone "ts cleared"
    db'.Fcs.Pending |> Expect.isNone "fcs cleared"
  }
]

// ============================================================
// cycle Effects Tests
// ============================================================

[<Tests>]
let TestCycleEffectsTests = testList "TestCycleEffects" [
  test "fromTick with both payloads produces two effects" {
    let effects = TestCycleEffects.fromTick (Some "code") (Some "file.fs") "file.fs" None
    effects.Length |> Expect.equal "two effects" 2
    match effects.[0] with
    | TestCycleEffect.ParseTreeSitter (content, _) ->
      content |> Expect.equal "ts content" "code"
    | _ -> failtest "expected ParseTreeSitter"
    match effects.[1] with
    | TestCycleEffect.RequestFcsTypeCheck (fp, _tsElapsed) ->
      fp |> Expect.equal "fcs path" "file.fs"
    | _ -> failtest "expected RequestFcsTypeCheck"
  }

  test "fromTick with no payloads produces empty" {
    let effects = TestCycleEffects.fromTick None None "file.fs" None
    effects.Length |> Expect.equal "no effects" 0
  }

  test "fromTick with only tree-sitter produces one effect" {
    let effects = TestCycleEffects.fromTick (Some "code") None "file.fs" None
    effects.Length |> Expect.equal "one effect" 1
    match effects.[0] with
    | TestCycleEffect.ParseTreeSitter _ -> ()
    | _ -> failtest "expected ParseTreeSitter"
  }

  test "afterTypeCheck with affected tests returns RunAffectedTests" {
    let tc1 = { Id = TestId.create "test1" TestFramework.XUnit; FullName = "test1"; DisplayName = "test1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.XUnit
                Category = TestCategory.Unit }
    let state = { LiveTestState.empty with DiscoveredTests = [| tc1 |]; Activation = LiveTestingActivation.Active }
    let graph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList [ "Module.add", [| tc1.Id |] ]
        TransitiveCoverage = Map.ofList [ "Module.add", [| tc1.Id |] ]
    }
    match TestCycleEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty with
    | [ TestCycleEffect.RequestRebuild (tests, trigger, _tsElapsed, _fcsElapsed, _sessionId, _maps) ] ->
      tests.Length |> Expect.equal "one test" 1
      trigger |> Expect.equal "keystroke trigger" RunTrigger.Keystroke
    | other -> failtestf "expected single RequestRebuild for .fs file, got %A" other
  }

  test "afterTypeCheck with no affected tests returns None" {
    let state = { LiveTestState.empty with DiscoveredTests = [||]; Activation = LiveTestingActivation.Active }
    let graph = TestDependencyGraph.empty
    TestCycleEffects.afterTypeCheck ["unknown.symbol"] "test.fs" RunTrigger.Keystroke graph state None Map.empty
    |> Expect.isEmpty "no affected tests"
  }

  test "afterTypeCheck when disabled returns None" {
    let state = { LiveTestState.empty with Activation = LiveTestingActivation.Inactive }
    let graph = TestDependencyGraph.empty
    TestCycleEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty
    |> Expect.isEmpty "disabled"
  }

  test "afterTypeCheck when Running returns None (prevents concurrent test runs)" {
    let tc1 = { Id = TestId.create "test1" TestFramework.XUnit; FullName = "test1"; DisplayName = "test1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.XUnit
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
    TestCycleEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty
    |> Expect.isEmpty "should not produce effect when tests are running"
  }

  test "afterTypeCheck when RunningButEdited returns None (re-trigger handled by completion)" {
    let tc1 = { Id = TestId.create "test1" TestFramework.XUnit; FullName = "test1"; DisplayName = "test1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.XUnit
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
    TestCycleEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty
    |> Expect.isEmpty "should not produce effect when running-but-edited"
  }

  test "afterTypeCheck filters integration tests on keystroke" {
    let tc1 = { Id = TestId.create "unit1" TestFramework.XUnit; FullName = "unit1"; DisplayName = "unit1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.XUnit
                Category = TestCategory.Unit }
    let tc2 = { Id = TestId.create "integ1" TestFramework.XUnit; FullName = "integ1"; DisplayName = "integ1"
                Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.XUnit
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
    match TestCycleEffects.afterTypeCheck ["Module.add"] "test.fs" RunTrigger.Keystroke graph state None Map.empty with
    | [ TestCycleEffect.RequestRebuild (tests, _, _, _, _, _) ] ->
      tests.Length |> Expect.equal "only unit test" 1
      tests.[0].Id |> Expect.equal "unit test id" tc1.Id
    | other -> failtestf "expected single RequestRebuild for .fs file, got %A" other
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

// --- Phase 4: cycle Integration Tests ---

/// Test-only effect logger for asserting cycle behavior.
type EffectLog = { mutable Effects: TestCycleEffect list }

module EffectDispatcher =
  let create () = { Effects = [] }

  let dispatch (log: EffectLog) (effect: TestCycleEffect) =
    log.Effects <- log.Effects @ [effect]

  let dispatchAll (log: EffectLog) (effects: TestCycleEffect list) =
    effects |> List.iter (dispatch log)

  let reset (log: EffectLog) =
    log.Effects <- []

[<Tests>]
let cycleStateTests = testList "LiveTestCycleState" [
  test "onKeystroke updates debounce and active file" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    s.ActiveFile |> Expect.equal "active file set" (Some "File.fs")
    s.Debounce.TreeSitter.Pending |> Expect.isSome "ts channel has pending"
  }

  test "onFileSave updates fcs debounce with short delay" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onFileSave "File.fs" t0
    s.Debounce.Fcs.Pending |> Expect.isSome "fcs channel has pending"
  }

  test "tick with no pending produces no effects" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let effects, _ = LiveTestCycleState.empty |> LiveTestCycleState.tick t0
    effects |> Expect.isEmpty "no effects from empty state"
  }

  test "tick with no active file produces no effects even with pending" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let state = {
      LiveTestCycleState.empty with
        Debounce = {
          TreeSitter = {
            CurrentGeneration = 1L
            Pending = Some { Payload = "let x = 1"; RequestedAt = t0; DelayMs = 50<ms>; Generation = 1L }
            LastCompleted = None
          }
          Fcs = {
            CurrentGeneration = 1L
            Pending = Some { Payload = "File.fs"; RequestedAt = t0; DelayMs = 300<ms>; Generation = 1L }
            LastCompleted = None
          }
        }
    }
    let effects, s2 = state |> LiveTestCycleState.tick (t0.AddMilliseconds(301.0))
    effects |> Expect.isEmpty "no effects when no active file"
    s2.ActiveFile |> Expect.isNone "active file still None"
  }

  test "tick after keystroke delay fires tree-sitter parse" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let t50 = t0.AddMilliseconds(51.0)
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestCycleState.tick t50
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.ParseTreeSitter _ -> true
      | _ -> false)
    |> Expect.isTrue "should have tree-sitter parse"
  }

  test "tick after full delay fires both tree-sitter and fcs" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let t301 = t0.AddMilliseconds(301.0)
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestCycleState.tick t301
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.ParseTreeSitter _ -> true
      | _ -> false)
    |> Expect.isTrue "should have tree-sitter"
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.RequestFcsTypeCheck _ -> true
      | _ -> false)
    |> Expect.isTrue "should have fcs request"
  }

  test "tick with fcs debounce does NOT produce RunAffectedTests (deferred to afterTypeCheck)" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let t301 = t0.AddMilliseconds(301.0)
    let tc = mkTestCase "MyModule.myTest" TestFramework.Expecto TestCategory.Unit
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["mySymbol", [|tc.Id|]]
        TransitiveCoverage = Map.ofList ["mySymbol", [|tc.Id|]]
    }
    let state = {
      LiveTestCycleState.empty with
        DepGraph = depGraph
        ChangedSymbols = ["mySymbol"]
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let s = state |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestCycleState.tick t301
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.RunAffectedTests _ -> true
      | _ -> false)
    |> Expect.isFalse "tick should not produce RunAffectedTests (stale symbols fix)"
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.RequestFcsTypeCheck _ -> true
      | _ -> false)
    |> Expect.isTrue "tick should produce RequestFcsTypeCheck"
  }

  test "file save shortens fcs debounce to 50ms" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let t51 = t0.AddMilliseconds(51.0)
    let s = LiveTestCycleState.empty |> LiveTestCycleState.onFileSave "File.fs" t0
    let effects, _ = s |> LiveTestCycleState.tick t51
    effects
    |> List.exists (fun e ->
      match e with
      | TestCycleEffect.RequestFcsTypeCheck _ -> true
      | _ -> false)
    |> Expect.isTrue "fcs fires at 50ms on save"
  }
]

[<Tests>]
let effectDispatchTests = testList "EffectDispatcher" [
  test "ParseTreeSitter logs content and file" {
    let log = EffectDispatcher.create()
    EffectDispatcher.dispatch log (TestCycleEffect.ParseTreeSitter("let x = 1", "File.fs"))
    log.Effects |> Expect.hasLength "one effect logged" 1
    match log.Effects.[0] with
    | TestCycleEffect.ParseTreeSitter(c, f) ->
      c |> Expect.equal "content" "let x = 1"
      f |> Expect.equal "file" "File.fs"
    | _ -> failtest "wrong effect type"
  }

  test "RequestFcsTypeCheck logs file path" {
    let log = EffectDispatcher.create()
    EffectDispatcher.dispatch log (TestCycleEffect.RequestFcsTypeCheck("File.fs", System.TimeSpan.Zero))
    log.Effects |> Expect.hasLength "one effect" 1
    match log.Effects.[0] with
    | TestCycleEffect.RequestFcsTypeCheck (f, _) -> f |> Expect.equal "file" "File.fs"
    | _ -> failtest "wrong effect type"
  }

  test "RunAffectedTests logs tests and trigger" {
    let log = EffectDispatcher.create()
    let tests = [| { Id = TestId.create "t1" TestFramework.Expecto; FullName = "t1"; DisplayName = "t1"
                     Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
                     Category = TestCategory.Unit } |]
    EffectDispatcher.dispatch log (TestCycleEffect.RunAffectedTests(tests, RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||]))
    log.Effects |> Expect.hasLength "one effect" 1
    match log.Effects.[0] with
    | TestCycleEffect.RunAffectedTests(tcs, trigger, _, _, _, _) ->
      tcs |> Expect.hasLength "one test" 1
      trigger |> Expect.equal "trigger" RunTrigger.Keystroke
    | _ -> failtest "wrong effect type"
  }

  test "dispatchAll processes multiple effects" {
    let log = EffectDispatcher.create()
    let effects = [
      TestCycleEffect.ParseTreeSitter("x", "f")
      TestCycleEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)
    ]
    EffectDispatcher.dispatchAll log effects
    log.Effects |> Expect.hasLength "two effects" 2
  }
]

[<Tests>]
let endToEndCycleTests = testList "End-to-end cycle" [
  test "keystroke → debounce → tree-sitter fires at 50ms" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let log = EffectDispatcher.create()
    let s0 = LiveTestCycleState.empty |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    // at 30ms — nothing fires
    let effects30, s30 = s0 |> LiveTestCycleState.tick (t0.AddMilliseconds(30.0))
    EffectDispatcher.dispatchAll log effects30
    log.Effects |> Expect.isEmpty "nothing at 30ms"
    // at 51ms — tree-sitter fires
    let effects51, _ = s30 |> LiveTestCycleState.tick (t0.AddMilliseconds(51.0))
    EffectDispatcher.dispatchAll log effects51
    log.Effects
    |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "tree-sitter fired at 51ms"
  }

  test "burst typing resets debounce" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let log = EffectDispatcher.create()
    let s0 = LiveTestCycleState.empty
             |> LiveTestCycleState.onKeystroke "l" "F.fs" t0
             |> LiveTestCycleState.onKeystroke "le" "F.fs" (t0.AddMilliseconds(20.0))
             |> LiveTestCycleState.onKeystroke "let" "F.fs" (t0.AddMilliseconds(40.0))
    // at 60ms from first keystroke — only 20ms from last, shouldn't fire
    let effects60, _ = s0 |> LiveTestCycleState.tick (t0.AddMilliseconds(60.0))
    EffectDispatcher.dispatchAll log effects60
    log.Effects |> Expect.isEmpty "burst resets debounce"
    // at 91ms from first (51ms from last) — fires
    EffectDispatcher.reset log
    let effects91, _ = s0 |> LiveTestCycleState.tick (t0.AddMilliseconds(91.0))
    EffectDispatcher.dispatchAll log effects91
    log.Effects
    |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "fires 50ms after last keystroke"
  }

  test "full cycle: keystroke → TS → FCS → afterTypeCheck → run affected" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let tc = mkTestCase "M.affectedTest" TestFramework.Expecto TestCategory.Unit
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["changedFn", [|tc.Id|]]
        TransitiveCoverage = Map.ofList ["changedFn", [|tc.Id|]]
    }
    let state = {
      LiveTestCycleState.empty with
        DepGraph = depGraph
        ChangedSymbols = ["changedFn"]
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let s = state |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    // Phase 1: tick fires TS + FCS
    let effects, s2 = s |> LiveTestCycleState.tick (t0.AddMilliseconds(301.0))
    effects
    |> List.exists (fun e -> match e with TestCycleEffect.ParseTreeSitter _ -> true | _ -> false)
    |> Expect.isTrue "has tree-sitter"
    effects
    |> List.exists (fun e -> match e with TestCycleEffect.RequestFcsTypeCheck _ -> true | _ -> false)
    |> Expect.isTrue "has fcs"
    // Phase 2: afterTypeCheck (after FCS completes) fires RequestRebuild for .fs files
    let runEffects = TestCycleEffects.afterTypeCheck s2.ChangedSymbols "test.fs" RunTrigger.Keystroke s2.DepGraph s2.TestState None s2.InstrumentationMaps
    runEffects |> List.isEmpty |> Expect.isFalse "afterTypeCheck produces effect"
    match runEffects with
    | [ TestCycleEffect.RequestRebuild (tests, trigger, _, _, _, _) ] ->
      tests |> Array.length |> Expect.equal "one affected test" 1
      trigger |> Expect.equal "trigger is keystroke" RunTrigger.Keystroke
    | [ TestCycleEffect.RunAffectedTests (tests, trigger, _, _, _, _) ] ->
      tests |> Array.length |> Expect.equal "one affected test" 1
      trigger |> Expect.equal "trigger is keystroke" RunTrigger.Keystroke
    | _ -> failwith "expected single RequestRebuild or RunAffectedTests"
  }

  test "disabled state produces no effects even after delay" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let tc = mkTestCase "M.t1" TestFramework.Expecto TestCategory.Unit
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["sym", [|tc.Id|]]
        TransitiveCoverage = Map.ofList ["sym", [|tc.Id|]]
    }
    let state = {
      LiveTestCycleState.empty with
        DepGraph = depGraph
        ChangedSymbols = ["sym"]
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Inactive }
    }
    let s = state |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestCycleState.tick (t0.AddMilliseconds(301.0))
    // TS and FCS fire (debounce doesn't check enabled), but RunAffected should not
    effects
    |> List.exists (fun e -> match e with TestCycleEffect.RunAffectedTests _ -> true | _ -> false)
    |> Expect.isFalse "no test run when disabled"
  }

  test "integration tests filtered on keystroke trigger" {
    let t0 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let tc = mkTestCase "M.intTest" TestFramework.Expecto TestCategory.Integration
    let depGraph = {
      TestDependencyGraph.empty with
        SymbolToTests = Map.ofList ["sym", [|tc.Id|]]
        TransitiveCoverage = Map.ofList ["sym", [|tc.Id|]]
    }
    let state = {
      LiveTestCycleState.empty with
        DepGraph = depGraph
        ChangedSymbols = ["sym"]
        TestState = { LiveTestState.empty with DiscoveredTests = [|tc|]; Activation = LiveTestingActivation.Active }
    }
    let s = state |> LiveTestCycleState.onKeystroke "let x = 1" "File.fs" t0
    let effects, _ = s |> LiveTestCycleState.tick (t0.AddMilliseconds(301.0))
    effects
    |> List.exists (fun e -> match e with TestCycleEffect.RunAffectedTests _ -> true | _ -> false)
    |> Expect.isFalse "integration tests filtered out on keystroke"
  }
]

[<Tests>]
let TestCycleCancellationTests = testList "TestCycleCancellation" [
  test "tokenForEffect returns live tokens" {
    let pc = TestCycleCancellation.create()
    let t0 = TestCycleCancellation.tokenForEffect TestCycleEffect.RequestInitialDiscovery pc
    let t1 = TestCycleCancellation.tokenForEffect (TestCycleEffect.ParseTreeSitter("x", "f")) pc
    let t2 = TestCycleCancellation.tokenForEffect (TestCycleEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)) pc
    let t3 = TestCycleCancellation.tokenForEffect (TestCycleEffect.RunAffectedTests([||], RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||])) pc
    t0.IsCancellationRequested |> Expect.isFalse "discovery token live"
    t1.IsCancellationRequested |> Expect.isFalse "ts token live"
    t2.IsCancellationRequested |> Expect.isFalse "fcs token live"
    t3.IsCancellationRequested |> Expect.isFalse "run token live"
    TestCycleCancellation.dispose pc
  }

  test "new tree-sitter effect cancels previous tree-sitter" {
    let pc = TestCycleCancellation.create()
    let t1 = TestCycleCancellation.tokenForEffect (TestCycleEffect.ParseTreeSitter("a", "f")) pc
    let _t2 = TestCycleCancellation.tokenForEffect (TestCycleEffect.ParseTreeSitter("b", "f")) pc
    t1.IsCancellationRequested |> Expect.isTrue "first ts cancelled"
    TestCycleCancellation.dispose pc
  }

  test "new fcs effect cancels previous fcs but not tree-sitter" {
    let pc = TestCycleCancellation.create()
    let tsToken = TestCycleCancellation.tokenForEffect (TestCycleEffect.ParseTreeSitter("x", "f")) pc
    let fcs1 = TestCycleCancellation.tokenForEffect (TestCycleEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)) pc
    let _fcs2 = TestCycleCancellation.tokenForEffect (TestCycleEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)) pc
    fcs1.IsCancellationRequested |> Expect.isTrue "first fcs cancelled"
    tsToken.IsCancellationRequested |> Expect.isFalse "ts not affected"
    TestCycleCancellation.dispose pc
  }

  test "new test run cancels previous test run" {
    let pc = TestCycleCancellation.create()
    let run1 = TestCycleCancellation.tokenForEffect (TestCycleEffect.RunAffectedTests([||], RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||])) pc
    let _run2 = TestCycleCancellation.tokenForEffect (TestCycleEffect.RunAffectedTests([||], RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||])) pc
    run1.IsCancellationRequested |> Expect.isTrue "first run cancelled"
    TestCycleCancellation.dispose pc
  }

  test "dispose cancels all active tokens" {
    let pc = TestCycleCancellation.create()
    let ts = TestCycleCancellation.tokenForEffect (TestCycleEffect.ParseTreeSitter("x", "f")) pc
    let fcs = TestCycleCancellation.tokenForEffect (TestCycleEffect.RequestFcsTypeCheck("f", System.TimeSpan.Zero)) pc
    let run = TestCycleCancellation.tokenForEffect (TestCycleEffect.RunAffectedTests([||], RunTrigger.Keystroke, System.TimeSpan.Zero, System.TimeSpan.Zero, None, [||])) pc
    TestCycleCancellation.dispose pc
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
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "base delay" 300.0<ms>
  }

  test "single cancel increases delay by multiplier" {
    let ad = AdaptiveDebounce.createDefault() |> AdaptiveDebounce.onFcsCanceled
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "450ms after one cancel" 450.0<ms>
  }

  test "three consecutive cancels compound the backoff" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "compounded" 1012.5<ms>
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
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "capped at 2000" 2000.0<ms>
  }

  test "single success after cancel resets cancel count but not delay" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCompleted
    ad.ConsecutiveFcsCancels |> Expect.equal "cancels reset" 0
    ad.ConsecutiveFcsSuccesses |> Expect.equal "one success" 1
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "still elevated" 450.0<ms>
  }

  test "three consecutive successes reset delay to base" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCompleted
      |> AdaptiveDebounce.onFcsCompleted
      |> AdaptiveDebounce.onFcsCompleted
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "reset to base" 300.0<ms>
    ad.ConsecutiveFcsSuccesses |> Expect.equal "successes reset" 0
  }

  test "cancel after partial success restarts backoff from current delay" {
    let ad =
      AdaptiveDebounce.createDefault()
      |> AdaptiveDebounce.onFcsCanceled
      |> AdaptiveDebounce.onFcsCompleted
      |> AdaptiveDebounce.onFcsCompleted
      |> AdaptiveDebounce.onFcsCanceled
    AdaptiveDebounce.currentFcsDelay ad |> Expect.equal "backoff from 450" 675.0<ms>
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
let TestCycleTimingDispatchTests = testList "cycle timing dispatch" [
  test "TestCycleTimingRecorded stores LastTiming in model" {
    let model0 = (SageFsModel.initial())
    model0.LiveTesting.LastTiming
    |> Expect.isNone "initial model should have no timing"

    let timing = {
      Depth = TestCycleDepth.ThroughExecution (
        System.TimeSpan.FromMilliseconds 1.2,
        System.TimeSpan.FromMilliseconds 85.0,
        System.TimeSpan.FromMilliseconds 42.0)
      TotalTests = 10
      AffectedTests = 3
      Trigger = RunTrigger.Keystroke
      Timestamp = System.DateTimeOffset.UtcNow
    }

    let msg = SageFsMsg.Event (SageFsEvent.TestCycleTimingRecorded timing)
    let model1, effects = SageFsUpdate.update msg model0

    model1.LiveTesting.LastTiming
    |> Expect.isSome "after dispatch, model should have timing"

    effects
    |> Expect.isEmpty "TestCycleTimingRecorded should produce no effects"
  }

  test "TestCycleTiming.toStatusBar formats correctly for ThroughExecution" {
    let timing = {
      Depth = TestCycleDepth.ThroughExecution (
        System.TimeSpan.FromMilliseconds 1.2,
        System.TimeSpan.FromMilliseconds 85.0,
        System.TimeSpan.FromMilliseconds 42.0)
      TotalTests = 10
      AffectedTests = 3
      Trigger = RunTrigger.Keystroke
      Timestamp = System.DateTimeOffset.UtcNow
    }
    TestCycleTiming.toStatusBar timing
    |> Expect.equal "should format all three stages" "TS:1.2ms | FCS:85ms | Run:42ms (3)"
  }

  test "TestCycleTiming.toStatusBar formats TreeSitterOnly" {
    let timing = {
      Depth = TestCycleDepth.TreeSitterOnly (System.TimeSpan.FromMilliseconds 0.8)
      TotalTests = 5
      AffectedTests = 0
      Trigger = RunTrigger.Keystroke
      Timestamp = System.DateTimeOffset.UtcNow
    }
    TestCycleTiming.toStatusBar timing
    |> Expect.equal "should only show tree-sitter" "TS:0.8ms"
  }

  test "TestCycleTiming.toStatusBar formats ThroughFcs" {
    let timing = {
      Depth = TestCycleDepth.ThroughFcs (
        System.TimeSpan.FromMilliseconds 1.5,
        System.TimeSpan.FromMilliseconds 142.0)
      TotalTests = 20
      AffectedTests = 5
      Trigger = RunTrigger.FileSave
      Timestamp = System.DateTimeOffset.UtcNow
    }
    TestCycleTiming.toStatusBar timing
    |> Expect.equal "should show tree-sitter and FCS" "TS:1.5ms | FCS:142ms"
  }

  test "new timing replaces old timing" {
    let timing1 = {
      Depth = TestCycleDepth.TreeSitterOnly (System.TimeSpan.FromMilliseconds 0.5)
      TotalTests = 5
      AffectedTests = 0
      Trigger = RunTrigger.Keystroke
      Timestamp = System.DateTimeOffset.UtcNow
    }
    let timing2 = {
      Depth = TestCycleDepth.ThroughExecution (
        System.TimeSpan.FromMilliseconds 1.0,
        System.TimeSpan.FromMilliseconds 100.0,
        System.TimeSpan.FromMilliseconds 50.0)
      TotalTests = 10
      AffectedTests = 3
      Trigger = RunTrigger.FileSave
      Timestamp = System.DateTimeOffset.UtcNow
    }

    let model0 = (SageFsModel.initial())
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestCycleTimingRecorded timing1)) model0
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestCycleTimingRecorded timing2)) model1

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
    let state = LiveTestCycleState.empty
    LiveTestCycleState.liveTestingStatusBar state
    |> Expect.equal "should be empty" ""
  }

  test "returns rebuilding marker when rebuild is pending" {
    let pending =
      { Tests = [| mkTestCase "MyTest.test1" TestFramework.Expecto TestCategory.Unit |]
        Trigger = RunTrigger.FileSave
        TreeSitterElapsed = TimeSpan.FromMilliseconds 5.0
        FcsElapsed = TimeSpan.FromMilliseconds 10.0
        SessionId = Some "session-1"
        InstrumentationMaps = [||] }
    let state = { LiveTestCycleState.empty with PendingRebuild = Some pending }
    let result = LiveTestCycleState.liveTestingStatusBar state
    result
    |> Expect.stringContains "should show rebuilding marker" "🔨 Rebuilding"
  }

  test "returns timing only when tests are empty" {
    let timing = {
      Depth = TestCycleDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 1.0,
        TimeSpan.FromMilliseconds 50.0,
        TimeSpan.FromMilliseconds 30.0)
      TotalTests = 5
      AffectedTests = 2
      Trigger = RunTrigger.FileSave
      Timestamp = DateTimeOffset.UtcNow
    }
    let state = { LiveTestCycleState.empty with LastTiming = Some timing }
    let result = LiveTestCycleState.liveTestingStatusBar state
    result |> Expect.isNotEmpty "should have timing text"
    result |> Expect.stringContains "should contain TS" "TS:"
  }

  test "returns tests only when timing is None" {
    let testId = TestId.create "MyTest.test1" TestFramework.Expecto
    let entry = {
      TestId = testId
      DisplayName = "test1"
      FullName = "MyTest.test1"
      Origin = TestOrigin.ReflectionOnly
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0)
      PreviousStatus = TestRunStatus.Detected
    }
    let testState = { LiveTestCycleState.empty.TestState with StatusEntries = [| entry |] }
    let state = { LiveTestCycleState.empty with TestState = testState }
    let result = LiveTestCycleState.liveTestingStatusBar state
    result |> Expect.isNotEmpty "should have tests text"
    result |> Expect.stringContains "should contain pass count" "1"
  }

  test "returns combined timing and tests" {
    let timing = {
      Depth = TestCycleDepth.ThroughExecution (
        TimeSpan.FromMilliseconds 1.0,
        TimeSpan.FromMilliseconds 50.0,
        TimeSpan.FromMilliseconds 30.0)
      TotalTests = 5
      AffectedTests = 2
      Trigger = RunTrigger.FileSave
      Timestamp = DateTimeOffset.UtcNow
    }
    let testId = TestId.create "MyTest.test1" TestFramework.Expecto
    let entry = {
      TestId = testId
      DisplayName = "test1"
      FullName = "MyTest.test1"
      Origin = TestOrigin.ReflectionOnly
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0)
      PreviousStatus = TestRunStatus.Detected
    }
    let testState = { LiveTestCycleState.empty.TestState with StatusEntries = [| entry |] }
    let state = { LiveTestCycleState.empty with LastTiming = Some timing; TestState = testState }
    let result = LiveTestCycleState.liveTestingStatusBar state
    result |> Expect.stringContains "should contain TS" "TS:"
    result |> Expect.stringContains "should contain pipe separator" " | "
  }
]

[<Tests>]
let cycleBenchmarkTests = testList "[Benchmark] cycle Core Benchmark" [
  test "200-test cycle core completes under 5ms p95" {
    let sw = System.Diagnostics.Stopwatch()
    let makeTestCase i =
      { Id = TestId.create (sprintf "Module.Tests.test%d" i) TestFramework.Expecto
        FullName = sprintf "Module.Tests.test%d" i; DisplayName = sprintf "test%d" i
        Origin = TestOrigin.SourceMapped ("editor", i + 1); Labels = []; Framework = TestFramework.Expecto
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
                Timestamp = DateTimeOffset.UtcNow; Output = None }) |> Map.ofArray
    let locs = tests |> Array.mapi (fun i t ->
      { AttributeName = "Test"; FunctionName = sprintf "t%d" i; FilePath = "editor"
        Line = (match t.Origin with TestOrigin.SourceMapped (_, l) -> l | _ -> 0); Column = 0 })
    let state = { LiveTestState.empty with
                    DiscoveredTests = tests; LastResults = results; SourceLocations = locs
                    AffectedTests = tests.[..19] |> Array.map (fun t -> t.Id) |> Set.ofArray }
    let stateWithEntries = { state with StatusEntries = LiveTesting.computeStatusEntries state }

    let timings = Array.init 100 (fun _ ->
      sw.Restart()
      let _ = TestCycleOrchestrator.decide stateWithEntries RunTrigger.Keystroke ["Module.func1"] graph
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

  test "1000-test cycle core completes under 20ms p95" {
    let sw = System.Diagnostics.Stopwatch()
    let makeTestCase i =
      { Id = TestId.create (sprintf "M.T.t%d" i) TestFramework.Expecto
        FullName = sprintf "M.T.t%d" i; DisplayName = sprintf "t%d" i
        Origin = TestOrigin.SourceMapped ("editor", i + 1); Labels = []; Framework = TestFramework.Expecto
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
                Timestamp = DateTimeOffset.UtcNow; Output = None }) |> Map.ofArray
    let locs = tests |> Array.mapi (fun i t ->
      { AttributeName = "Test"; FunctionName = sprintf "t%d" i; FilePath = "editor"
        Line = (match t.Origin with TestOrigin.SourceMapped (_, l) -> l | _ -> 0); Column = 0 })
    let state = { LiveTestState.empty with
                    DiscoveredTests = tests; LastResults = results; SourceLocations = locs
                    AffectedTests = tests.[..49] |> Array.map (fun t -> t.Id) |> Set.ofArray }
    let stateWithEntries = { state with StatusEntries = LiveTesting.computeStatusEntries state }

    let timings = Array.init 50 (fun _ ->
      sw.Restart()
      let _ = TestCycleOrchestrator.decide stateWithEntries RunTrigger.Keystroke ["func1"] graph
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

// --- E2E cycle Flow Tests ---

[<Tests>]
let e2eCycleFlowTests = testList "E2E cycle Flow" [
  test "file change through to test status update" {
    let sessionId = SageFs.WorkerProtocol.SessionId.newId()
    let sessionIdStr = SageFs.WorkerProtocol.SessionId.value sessionId
    let tid = TestId.TestId "MyModule.myTest should work"
    let testCase = {
      TestCase.Id = tid; DisplayName = "myTest should work"; FullName = "MyModule.myTest should work"
      Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit
    }
    let model0 = (SageFsModel.initial())
    let snap = {
      SessionSnapshot.Id = sessionId; Name = Some "TestSession"; Projects = ["MyProject.fsproj"]
      Status = SessionDisplayStatus.Running; LastActivity = System.DateTime.UtcNow
      EvalCount = 0; UpSince = System.DateTime.UtcNow; IsActive = true; WorkingDirectory = "C:\\Test"
    }
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap)) model0
    let model2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered (sessionIdStr, [| testCase |]))) model1
    model2.LiveTesting.TestState.DiscoveredTests |> Array.length |> Expect.equal "should have 1 discovered test" 1

    let model3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid |], Some sessionIdStr))) model2
    let result = {
      TestRunResult.TestId = tid; TestName = "myTest should work"
      Result = TestResult.Passed (System.TimeSpan.FromMilliseconds 42.0)
      Timestamp = System.DateTimeOffset.UtcNow
      Output = None
    }
    let model4, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestResultsBatch [| result |])) model3
    model4.LiveTesting.TestState.LastResults |> Map.tryFind tid |> Expect.isSome "should have result for test"

    let model5, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunCompleted (Some sessionIdStr))) model4
    model5.LiveTesting.TestState.RunPhases |> Map.tryFind sessionIdStr
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
    let tc1 = { TestCase.Id = tid1; FullName = "test.in.session1"; DisplayName = "t1"; Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    let tc2 = { TestCase.Id = tid2; FullName = "test.in.session2"; DisplayName = "t2"; Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    let s1 = SageFs.WorkerProtocol.SessionId.newId()
    let s2 = SageFs.WorkerProtocol.SessionId.newId()
    let s1Str = SageFs.WorkerProtocol.SessionId.value s1
    let s2Str = SageFs.WorkerProtocol.SessionId.value s2
    let m0 = (SageFsModel.initial())
    let snap1 = { SessionSnapshot.Id = s1; Name = Some "S1"; Projects = ["A.fsproj"]; Status = SessionDisplayStatus.Running; LastActivity = System.DateTime.UtcNow; EvalCount = 0; UpSince = System.DateTime.UtcNow; IsActive = true; WorkingDirectory = "C:\\A" }
    let snap2 = { SessionSnapshot.Id = s2; Name = Some "S2"; Projects = ["B.fsproj"]; Status = SessionDisplayStatus.Running; LastActivity = System.DateTime.UtcNow; EvalCount = 0; UpSince = System.DateTime.UtcNow; IsActive = false; WorkingDirectory = "C:\\B" }
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap1)) m0
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap2)) m1
    let m3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered (s1Str, [| tc1 |]))) m2
    let m4, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestsDiscovered (s2Str, [| tc2 |]))) m3
    m4.LiveTesting.TestState.TestSessionMap |> Map.find tid1 |> Expect.equal "t1 in s1" s1Str
    m4.LiveTesting.TestState.TestSessionMap |> Map.find tid2 |> Expect.equal "t2 in s2" s2Str

    let m5, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tid1 |], Some s1Str))) m4
    let r1 = { TestRunResult.TestId = tid1; TestName = "t1"; Result = TestResult.Passed (System.TimeSpan.FromMilliseconds 10.0); Timestamp = System.DateTimeOffset.UtcNow; Output = None }
    let m6, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestResultsBatch [| r1 |])) m5
    let s2Status = m6.LiveTesting.TestState.StatusEntries |> Array.tryFind (fun e -> e.TestId = tid2)
    match s2Status with
    | Some entry -> match entry.Status with | TestRunStatus.Passed _ -> failwith "s2's test should NOT be Passed" | _ -> ()
    | None -> ()
  }
]
