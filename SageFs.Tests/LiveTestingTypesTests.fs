module SageFs.Tests.LiveTestingTypesTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting
open SageFs.Tests.SharedGenerators

[<Tests>]
let liveTestingTypesTests = testList "LiveTestingTypes" [

  testList "TestId" [
    test "create is deterministic" {
      let id1 = TestId.create "My.Test.name" TestFramework.XUnit
      let id2 = TestId.create "My.Test.name" TestFramework.XUnit
      id1 |> Expect.equal "same inputs same id" id2
    }
    test "create produces 16-char hex" {
      let (TestId.TestId id) = TestId.create "hello" TestFramework.Expecto
      id.Length |> Expect.equal "should be 16 chars" 16
      id |> Seq.forall (fun c -> Char.IsAsciiHexDigit c)
      |> Expect.isTrue "should be hex chars"
    }
    test "different fullName produces different id" {
      let id1 = TestId.create "test.A" TestFramework.XUnit
      let id2 = TestId.create "test.B" TestFramework.XUnit
      id1 = id2 |> Expect.isFalse "should differ"
    }
    test "different framework produces different id" {
      let id1 = TestId.create "test.A" TestFramework.XUnit
      let id2 = TestId.create "test.A" TestFramework.NUnit
      id1 = id2 |> Expect.isFalse "should differ"
    }
    test "empty input still produces valid id" {
      let (TestId.TestId id) = TestId.create "" (TestFramework.Unknown "")
      id.Length |> Expect.equal "should be 16 chars" 16
    }
  ]

  testList "TestRunPhase" [
    test "startRun produces Running" {
      let phase, gen = TestRunPhase.startRun RunGeneration.zero
      match phase with
      | TestRunPhase.Running _ -> ()
      | _ -> failtest "should be Running"
      gen |> Expect.equal "gen should be 1" (RunGeneration.next RunGeneration.zero)
    }
    test "onEdit from Idle stays Idle" {
      TestRunPhase.onEdit TestRunPhase.Idle
      |> Expect.equal "should stay Idle" TestRunPhase.Idle
    }
    test "onEdit from Running becomes RunningButEdited" {
      let gen = RunGeneration.next RunGeneration.zero
      let running = TestRunPhase.Running gen
      match TestRunPhase.onEdit running with
      | TestRunPhase.RunningButEdited g ->
        g |> Expect.equal "should preserve gen" gen
      | _ -> failtest "should be RunningButEdited"
    }
    test "onEdit from RunningButEdited stays RunningButEdited" {
      let gen = RunGeneration.next RunGeneration.zero
      let rbe = TestRunPhase.RunningButEdited gen
      match TestRunPhase.onEdit rbe with
      | TestRunPhase.RunningButEdited g ->
        g |> Expect.equal "should preserve gen" gen
      | _ -> failtest "should stay RunningButEdited"
    }
    test "onResultsArrived from Running with correct gen is Fresh" {
      let gen = RunGeneration.next RunGeneration.zero
      let running = TestRunPhase.Running gen
      let phase, freshness = TestRunPhase.onResultsArrived gen running
      phase |> Expect.equal "should return to Idle" TestRunPhase.Idle
      freshness |> Expect.equal "should be Fresh" ResultFreshness.Fresh
    }
    test "onResultsArrived from Running with wrong gen is StaleWrongGeneration" {
      let gen1 = RunGeneration.next RunGeneration.zero
      let gen2 = RunGeneration.next gen1
      let running = TestRunPhase.Running gen2
      let _, freshness = TestRunPhase.onResultsArrived gen1 running
      freshness |> Expect.equal "should be StaleWrongGeneration" ResultFreshness.StaleWrongGeneration
    }
    test "onResultsArrived from RunningButEdited is StaleCodeEdited" {
      let gen = RunGeneration.next RunGeneration.zero
      let rbe = TestRunPhase.RunningButEdited gen
      let phase, freshness = TestRunPhase.onResultsArrived gen rbe
      phase |> Expect.equal "should return to Idle" TestRunPhase.Idle
      freshness |> Expect.equal "should be StaleCodeEdited" ResultFreshness.StaleCodeEdited
    }
    test "isRunning true when Running" {
      TestRunPhase.Running(RunGeneration.next RunGeneration.zero)
      |> TestRunPhase.isRunning
      |> Expect.isTrue "should be running"
    }
    test "isRunning false when Idle" {
      TestRunPhase.Idle
      |> TestRunPhase.isRunning
      |> Expect.isFalse "should not be running"
    }
    test "currentGeneration returns gen from Running" {
      let gen = RunGeneration.next RunGeneration.zero
      TestRunPhase.Running gen
      |> TestRunPhase.currentGeneration RunGeneration.zero
      |> Expect.equal "should extract gen" gen
    }
    test "currentGeneration returns lastGen when Idle" {
      let lastGen = RunGeneration.next RunGeneration.zero
      TestRunPhase.Idle
      |> TestRunPhase.currentGeneration lastGen
      |> Expect.equal "should return lastGen" lastGen
    }
  ]

  testList "ResultWindow" [
    test "create has specified capacity" {
      let w = ResultWindow.create 5
      ResultWindow.toList w |> Expect.isEmpty "should be empty"
    }
    test "add single result" {
      let w = ResultWindow.create 3 |> ResultWindow.add TestOutcome.Pass
      ResultWindow.toList w
      |> Expect.hasLength "should have 1" 1
    }
    test "add respects capacity" {
      let w =
        ResultWindow.create 2
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Fail
        |> ResultWindow.add TestOutcome.Pass
      ResultWindow.toList w
      |> Expect.hasLength "should wrap at 2" 2
    }
    test "toList returns in insertion order" {
      let w =
        ResultWindow.create 3
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Fail
      let items = ResultWindow.toList w
      items.[0] |> Expect.equal "first added is Pass" TestOutcome.Pass
      items.[1] |> Expect.equal "second added is Fail" TestOutcome.Fail
    }
    test "countFlips counts transitions" {
      let w =
        ResultWindow.create 10
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Fail
        |> ResultWindow.add TestOutcome.Pass
      ResultWindow.countFlips w
      |> Expect.equal "should have 2 flips" 2
    }
    test "countFlips no flips in uniform window" {
      let w =
        ResultWindow.create 5
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Pass
      ResultWindow.countFlips w
      |> Expect.equal "should have 0 flips" 0
    }
    test "countFlips empty window returns 0" {
      ResultWindow.create 5
      |> ResultWindow.countFlips
      |> Expect.equal "should be 0" 0
    }
    test "wrap preserves order correctly" {
      let w =
        ResultWindow.create 3
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Fail
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Fail
      let items = ResultWindow.toList w
      items.[0] |> Expect.equal "newest is Fail" TestOutcome.Fail
      items.[1] |> Expect.equal "middle is Pass" TestOutcome.Pass
      items.[2] |> Expect.equal "oldest is Fail" TestOutcome.Fail
    }
    test "create with capacity 1" {
      let w =
        ResultWindow.create 1
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Fail
      ResultWindow.toList w
      |> Expect.hasLength "capacity 1 keeps 1" 1
    }
    test "add returns fresh window" {
      let w1 = ResultWindow.create 3
      let w2 = w1 |> ResultWindow.add TestOutcome.Pass
      ResultWindow.toList w1 |> Expect.isEmpty "original unchanged"
      ResultWindow.toList w2 |> Expect.hasLength "new has 1" 1
    }
  ]

  testList "TestStability" [
    test "insufficient data" {
      let w = ResultWindow.create 10 |> ResultWindow.add TestOutcome.Pass
      TestStability.assess 3 2 w
      |> Expect.equal "not enough data" TestStability.Insufficient
    }
    test "stable when all same" {
      let w =
        ResultWindow.create 10
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Pass
      TestStability.assess 3 2 w
      |> Expect.equal "all pass = stable" TestStability.Stable
    }
    test "flaky when flips detected" {
      let w =
        ResultWindow.create 10
        |> ResultWindow.add TestOutcome.Pass
        |> ResultWindow.add TestOutcome.Fail
        |> ResultWindow.add TestOutcome.Pass
      match TestStability.assess 3 2 w with
      | TestStability.Flaky _ -> ()
      | other -> failtestf "expected Flaky, got %A" other
    }
  ]

  testList "FlakyDetection" [
    test "outcomeOf Pass is Pass" {
      FlakyDetection.outcomeOf (TestResult.Passed(TimeSpan.FromMilliseconds 5.0))
      |> Expect.equal "should be Pass" TestOutcome.Pass
    }
    test "outcomeOf Failed is Fail" {
      FlakyDetection.outcomeOf (TestResult.Failed(TestFailure.AssertionFailed "x", TimeSpan.Zero))
      |> Expect.equal "should be Fail" TestOutcome.Fail
    }
    test "outcomeOf Skipped maps to Pass" {
      FlakyDetection.outcomeOf (TestResult.Skipped "reason")
      |> Expect.equal "skipped maps to Pass" TestOutcome.Pass
    }
    test "outcomeOf NotRun maps to Pass" {
      FlakyDetection.outcomeOf TestResult.NotRun
      |> Expect.equal "not run maps to Pass" TestOutcome.Pass
    }
    test "recordResult adds to empty history" {
      let tid = TestId.create "test" TestFramework.XUnit
      let history = FlakyDetection.recordResult tid (TestResult.Passed TimeSpan.Zero) Map.empty
      history |> Map.containsKey tid |> Expect.isTrue "should have entry"
    }
    test "assessTest on recorded history" {
      let tid = TestId.create "test" TestFramework.XUnit
      let history =
        Map.empty
        |> FlakyDetection.recordResult tid (TestResult.Passed TimeSpan.Zero)
        |> FlakyDetection.recordResult tid (TestResult.Failed(TestFailure.AssertionFailed "x", TimeSpan.Zero))
        |> FlakyDetection.recordResult tid (TestResult.Passed TimeSpan.Zero)
      FlakyDetection.assessTest tid history
      |> fun s -> match s with TestStability.Flaky _ -> true | _ -> false
      |> Expect.isTrue "should be Flaky"
    }
  ]

  testList "GutterIcon" [
    test "toChar produces expected chars" {
      GutterIcon.toChar GutterIcon.TestPassed |> Expect.equal "pass char" '\u2713'
      GutterIcon.toChar GutterIcon.TestFailed |> Expect.equal "fail char" '\u2717'
      GutterIcon.toChar GutterIcon.TestDiscovered |> Expect.equal "discovered char" '\u25C6'
    }
    test "toColorIndex maps to distinct indices for test icons" {
      let indices =
        [ GutterIcon.TestDiscovered; GutterIcon.TestPassed; GutterIcon.TestFailed; GutterIcon.TestRunning ]
        |> List.map GutterIcon.toColorIndex
      indices |> List.distinct |> List.length
      |> Expect.equal "test icons have distinct indices" 4
    }
    test "toLabel and parseLabel round-trip" {
      let icons = [
        GutterIcon.TestPassed; GutterIcon.TestFailed; GutterIcon.TestDiscovered
        GutterIcon.TestRunning; GutterIcon.TestSkipped; GutterIcon.TestFlaky
        GutterIcon.Covered; GutterIcon.NotCovered
      ]
      for icon in icons do
        let label = GutterIcon.toLabel icon
        let parsed = GutterIcon.parseLabel label
        parsed |> Expect.equal (sprintf "round-trip %A" icon) (Some icon)
    }
    test "parseLabel unknown returns None" {
      GutterIcon.parseLabel "nonexistent"
      |> Expect.isNone "should be None"
    }
  ]

  testList "StatusToGutter" [
    test "Passed maps to TestPassed" {
      StatusToGutter.fromTestStatus (TestRunStatus.Passed(TimeSpan.FromMilliseconds 5.0))
      |> Expect.equal "should be TestPassed" GutterIcon.TestPassed
    }
    test "Failed maps to TestFailed" {
      StatusToGutter.fromTestStatus (TestRunStatus.Failed(TestFailure.AssertionFailed "x", TimeSpan.Zero))
      |> Expect.equal "should be TestFailed" GutterIcon.TestFailed
    }
    test "Stale maps to TestDiscovered" {
      StatusToGutter.fromTestStatus TestRunStatus.Stale
      |> Expect.equal "should be TestDiscovered" GutterIcon.TestDiscovered
    }
    test "Detected maps to TestDiscovered" {
      StatusToGutter.fromTestStatus TestRunStatus.Detected
      |> Expect.equal "should be TestDiscovered" GutterIcon.TestDiscovered
    }
    test "Running maps to TestRunning" {
      StatusToGutter.fromTestStatus TestRunStatus.Running
      |> Expect.equal "should be TestRunning" GutterIcon.TestRunning
    }
    test "Skipped maps to TestSkipped" {
      StatusToGutter.fromTestStatus (TestRunStatus.Skipped "reason")
      |> Expect.equal "should be TestSkipped" GutterIcon.TestSkipped
    }
    test "Queued maps to TestDiscovered" {
      StatusToGutter.fromTestStatus TestRunStatus.Queued
      |> Expect.equal "should be TestDiscovered" GutterIcon.TestDiscovered
    }
    test "fromCoverageStatus Covered all passing" {
      StatusToGutter.fromCoverageStatus (CoverageStatus.Covered(3, CoverageHealth.AllPassing))
      |> Expect.equal "should be Covered" GutterIcon.Covered
    }
    test "fromCoverageStatus Covered some failing" {
      StatusToGutter.fromCoverageStatus (CoverageStatus.Covered(3, CoverageHealth.SomeFailing))
      |> Expect.equal "should be NotCovered" GutterIcon.NotCovered
    }
    test "fromCoverageStatus NotCovered" {
      StatusToGutter.fromCoverageStatus CoverageStatus.NotCovered
      |> Expect.equal "should be NotCovered" GutterIcon.NotCovered
    }
  ]

  testList "TestSummary" [
    test "empty summary" {
      let s = TestSummary.empty
      s.Total |> Expect.equal "total 0" 0
      s.Passed |> Expect.equal "passed 0" 0
      s.Failed |> Expect.equal "failed 0" 0
    }
    test "fromStatuses counts correctly" {
      let statuses = [|
        TestRunStatus.Passed(TimeSpan.Zero)
        TestRunStatus.Passed(TimeSpan.Zero)
        TestRunStatus.Failed(TestFailure.AssertionFailed "x", TimeSpan.Zero)
        TestRunStatus.Stale
        TestRunStatus.Running
      |]
      let s = TestSummary.fromStatuses LiveTestingActivation.Active statuses
      s.Total |> Expect.equal "total 5" 5
      s.Passed |> Expect.equal "passed 2" 2
      s.Failed |> Expect.equal "failed 1" 1
      s.Stale |> Expect.equal "stale 1" 1
      s.Running |> Expect.equal "running 1" 1
    }
    test "toStatusBar formats correctly" {
      let s = { TestSummary.empty with Total = 10; Passed = 8; Failed = 2 }
      let bar = TestSummary.toStatusBar s
      bar.Contains("8/10") |> Expect.isTrue "should contain passed/total"
      bar.Contains("2") |> Expect.isTrue "should contain failed count"
    }
    test "toStatusBar empty" {
      let bar = TestSummary.toStatusBar TestSummary.empty
      bar |> Expect.equal "none when 0" "Tests: none"
    }
    test "fromStatuses empty array" {
      TestSummary.fromStatuses LiveTestingActivation.Active [||]
      |> fun s -> s.Total
      |> Expect.equal "should be 0" 0
    }
    test "toInlineBadge with passed and failed" {
      let s = { TestSummary.empty with Total = 44; Passed = 42; Failed = 2 }
      TestSummary.toInlineBadge s
      |> Expect.equal "should show pass+fail" "✓42 ✗2"
    }
    test "toInlineBadge all passed" {
      let s = { TestSummary.empty with Total = 10; Passed = 10 }
      TestSummary.toInlineBadge s
      |> Expect.equal "should show pass only" "✓10"
    }
    test "toInlineBadge with running" {
      let s = { TestSummary.empty with Total = 5; Passed = 2; Running = 3 }
      TestSummary.toInlineBadge s
      |> Expect.equal "should show pass+running" "✓2 ⟳3"
    }
    test "toInlineBadge with stale" {
      let s = { TestSummary.empty with Total = 8; Passed = 6; Stale = 2 }
      TestSummary.toInlineBadge s
      |> Expect.equal "should show pass+stale" "✓6 ●2"
    }
    test "toInlineBadge empty" {
      TestSummary.toInlineBadge TestSummary.empty
      |> Expect.equal "empty for 0 tests" ""
    }
    test "toInlineBadge all disabled" {
      let s = { TestSummary.empty with Total = 3; Disabled = 3 }
      TestSummary.toInlineBadge s
      |> Expect.equal "fallback for no status parts" "3 tests"
    }
  ]

  testList "CategoryDetection" [
    test "integration label maps to Integration" {
      CategoryDetection.categorize ["Integration"] "My.Test" TestFramework.XUnit [||]
      |> Expect.equal "should be Integration" TestCategory.Integration
    }
    test "browser label maps to Browser" {
      CategoryDetection.categorize ["Browser"] "My.Test" TestFramework.XUnit [||]
      |> Expect.equal "should be Browser" TestCategory.Browser
    }
    test "e2e label maps to Browser" {
      CategoryDetection.categorize ["E2E"] "My.Test" TestFramework.XUnit [||]
      |> Expect.equal "should be Browser" TestCategory.Browser
    }
    test "benchmark label maps to Benchmark" {
      CategoryDetection.categorize ["Benchmark"] "My.Test" TestFramework.XUnit [||]
      |> Expect.equal "should be Benchmark" TestCategory.Benchmark
    }
    test "property label maps to Property" {
      CategoryDetection.categorize ["Property"] "My.Test" TestFramework.Expecto [||]
      |> Expect.equal "should be Property" TestCategory.Property
    }
    test "architecture label maps to Architecture" {
      CategoryDetection.categorize ["Architecture"] "My.Test" TestFramework.NUnit [||]
      |> Expect.equal "should be Architecture" TestCategory.Architecture
    }
    test "arch label maps to Architecture" {
      CategoryDetection.categorize ["arch"] "My.Test" TestFramework.NUnit [||]
      |> Expect.equal "should be Architecture" TestCategory.Architecture
    }
    test "Playwright assembly maps to Browser" {
      CategoryDetection.categorize [] "My.Test" TestFramework.XUnit [| "Microsoft.Playwright" |]
      |> Expect.equal "should be Browser" TestCategory.Browser
    }
    test "BenchmarkDotNet assembly maps to Benchmark" {
      CategoryDetection.categorize [] "My.Test" TestFramework.XUnit [| "BenchmarkDotNet" |]
      |> Expect.equal "should be Benchmark" TestCategory.Benchmark
    }
    test "integration in fullName maps to Integration" {
      CategoryDetection.categorize [] "My.IntegrationTests.test1" TestFramework.XUnit [||]
      |> Expect.equal "should be Integration" TestCategory.Integration
    }
    test "e2e in fullName maps to Browser" {
      CategoryDetection.categorize [] "My.E2ETests.test1" TestFramework.XUnit [||]
      |> Expect.equal "should be Browser" TestCategory.Browser
    }
    test "no signals defaults to Unit" {
      CategoryDetection.categorize [] "My.Tests.simpleTest" TestFramework.Expecto [||]
      |> Expect.equal "should be Unit" TestCategory.Unit
    }
    test "label detection is case-insensitive" {
      CategoryDetection.categorize ["INTEGRATION"] "My.Test" TestFramework.XUnit [||]
      |> Expect.equal "should be Integration" TestCategory.Integration
    }
  ]

  testList "PolicyFilter" [
    let mkTest cat = {
      Id = TestId.create "t" (TestFramework.Unknown "f")
      FullName = "t"
      DisplayName = "t"
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = TestFramework.XUnit
      Category = cat
    }
    let defaults = RunPolicyDefaults.defaults
    test "Unit tests pass on Keystroke" {
      [| mkTest TestCategory.Unit |]
      |> LiveTesting.filterByPolicy defaults RunTrigger.Keystroke
      |> Array.length
      |> Expect.equal "should include unit" 1
    }
    test "Integration tests filtered out on Keystroke" {
      [| mkTest TestCategory.Integration |]
      |> LiveTesting.filterByPolicy defaults RunTrigger.Keystroke
      |> Array.length
      |> Expect.equal "should exclude integration" 0
    }
    test "Integration tests pass on ExplicitRun" {
      [| mkTest TestCategory.Integration |]
      |> LiveTesting.filterByPolicy defaults RunTrigger.ExplicitRun
      |> Array.length
      |> Expect.equal "should include on explicit" 1
    }
    test "Architecture tests pass on FileSave" {
      [| mkTest TestCategory.Architecture |]
      |> LiveTesting.filterByPolicy defaults RunTrigger.FileSave
      |> Array.length
      |> Expect.equal "should include arch on save" 1
    }
    test "Architecture tests filtered out on Keystroke" {
      [| mkTest TestCategory.Architecture |]
      |> LiveTesting.filterByPolicy defaults RunTrigger.Keystroke
      |> Array.length
      |> Expect.equal "should exclude arch on keystroke" 0
    }
    test "Disabled tests always filtered" {
      let policies = Map.add TestCategory.Unit RunPolicy.Disabled defaults
      [| mkTest TestCategory.Unit |]
      |> LiveTesting.filterByPolicy policies RunTrigger.ExplicitRun
      |> Array.length
      |> Expect.equal "disabled tests excluded even on explicit" 0
    }
  ]

  testList "TestDependencyGraph" [
    test "empty has empty maps" {
      let g = TestDependencyGraph.empty
      g.SymbolToTests |> Map.isEmpty |> Expect.isTrue "should be empty"
      g.TransitiveCoverage |> Map.isEmpty |> Expect.isTrue "should be empty"
    }
    test "findAffected returns tests for changed symbol" {
      let tid1 = TestId.create "test1" TestFramework.XUnit
      let tid2 = TestId.create "test2" TestFramework.XUnit
      let g = TestDependencyGraph.fromDirect (Map.ofList ["MyModule.add", [| tid1; tid2 |]])
      TestDependencyGraph.findAffected ["MyModule.add"] g
      |> Array.length
      |> Expect.equal "should find 2 tests" 2
    }
    test "findAffected returns empty for unknown symbol" {
      let g = TestDependencyGraph.fromDirect (Map.ofList ["MyModule.add", [| TestId.create "t" (TestFramework.Unknown "x") |]])
      TestDependencyGraph.findAffected ["Unknown.func"] g
      |> Array.length
      |> Expect.equal "should find 0 tests" 0
    }
    test "findAffected deduplicates across symbols" {
      let tid = TestId.create "test1" TestFramework.XUnit
      let g = TestDependencyGraph.fromDirect (Map.ofList [
        "A.f1", [| tid |]
        "A.f2", [| tid |]
      ])
      TestDependencyGraph.findAffected ["A.f1"; "A.f2"] g
      |> Array.length
      |> Expect.equal "should deduplicate" 1
    }
    test "reachableFrom with simple graph" {
      let callGraph = Map.ofList [
        "A", [| "B"; "C" |]
        "B", [| "D" |]
      ]
      let reachable = TestDependencyGraph.reachableFrom callGraph "A"
      reachable |> List.length |> Expect.equal "should reach 4 nodes" 4
      reachable |> List.contains "A" |> Expect.isTrue "should include start"
      reachable |> List.contains "D" |> Expect.isTrue "should include transitive"
    }
    test "reachableFrom handles cycles" {
      let callGraph = Map.ofList [
        "A", [| "B" |]
        "B", [| "A" |]
      ]
      let reachable = TestDependencyGraph.reachableFrom callGraph "A"
      reachable |> List.length |> Expect.equal "should not infinite loop" 2
    }
    test "computeTransitiveCoverage propagates through call graph" {
      let tid = TestId.create "test1" TestFramework.XUnit
      let callGraph = Map.ofList [ "testFunc", [| "helperA"; "helperB" |] ]
      let direct = Map.ofList [ "testFunc", [| tid |] ]
      let transitive = TestDependencyGraph.computeTransitiveCoverage callGraph direct
      transitive |> Map.containsKey "helperA" |> Expect.isTrue "helper should be covered"
      transitive |> Map.containsKey "helperB" |> Expect.isTrue "helper should be covered"
    }
    test "mergePerFileIndexes combines across files" {
      let tid1 = TestId.create "t1" (TestFramework.Unknown "x")
      let tid2 = TestId.create "t2" (TestFramework.Unknown "x")
      let perFile = Map.ofList [
        "file1.fs", Map.ofList [ "sym", [| tid1 |] ]
        "file2.fs", Map.ofList [ "sym", [| tid2 |] ]
      ]
      let merged = TestDependencyGraph.mergePerFileIndexes perFile
      merged |> Map.find "sym" |> Array.length
      |> Expect.equal "should combine both tests" 2
    }
  ]

  testList "SourceMapping" [
    test "extractMethodName simple dotted" {
      SourceMapping.extractMethodName "Namespace.Module.Tests.shouldAdd"
      |> Expect.equal "should extract last part" "shouldAdd"
    }
    test "extractMethodName with slash (Expecto)" {
      SourceMapping.extractMethodName "Namespace.Module.myTests/should add numbers"
      |> Expect.equal "should extract module before slash" "myTests"
    }
    test "extractMethodName single name" {
      SourceMapping.extractMethodName "simpleTest"
      |> Expect.equal "should return as-is" "simpleTest"
    }
    test "attributeMatchesFramework xunit Fact" {
      SourceMapping.attributeMatchesFramework TestFramework.XUnit "Fact"
      |> Expect.isTrue "Fact matches xunit"
    }
    test "attributeMatchesFramework xunit FactAttribute" {
      SourceMapping.attributeMatchesFramework TestFramework.XUnit "FactAttribute"
      |> Expect.isTrue "FactAttribute matches xunit"
    }
    test "attributeMatchesFramework expecto Tests" {
      SourceMapping.attributeMatchesFramework TestFramework.Expecto "Tests"
      |> Expect.isTrue "Tests matches expecto"
    }
    test "attributeMatchesFramework nunit Test" {
      SourceMapping.attributeMatchesFramework TestFramework.NUnit "Test"
      |> Expect.isTrue "Test matches nunit"
    }
    test "attributeMatchesFramework mstest TestMethod" {
      SourceMapping.attributeMatchesFramework TestFramework.MSTest "TestMethod"
      |> Expect.isTrue "TestMethod matches mstest"
    }
    test "attributeMatchesFramework unknown returns false" {
      SourceMapping.attributeMatchesFramework (TestFramework.Unknown "unknown") "Fact"
      |> Expect.isFalse "unknown framework shouldn't match"
    }
  ]

  testList "AssemblyLoadError" [
    test "path extracts path from FileNotFound" {
      AssemblyLoadError.path (AssemblyLoadError.FileNotFound ("/a/b.dll", "msg"))
      |> Expect.equal "should extract path" "/a/b.dll"
    }
    test "message extracts message from LoadFailed" {
      AssemblyLoadError.message (AssemblyLoadError.LoadFailed ("/a.dll", "bad"))
      |> Expect.equal "should extract message" "bad"
    }
    test "describe for FileNotFound includes path" {
      AssemblyLoadError.describe (AssemblyLoadError.FileNotFound ("/x.dll", "not found"))
      |> fun s -> s.Contains("/x.dll")
      |> Expect.isTrue "should contain path"
    }
    test "describe for BadImage includes bad image" {
      AssemblyLoadError.describe (AssemblyLoadError.BadImage ("/x.dll", "bad format"))
      |> fun s -> s.Contains("Bad image")
      |> Expect.isTrue "should mention bad image"
    }
  ]

  testList "TestResultsBatchPayload" [
    test "deriveCompletion Fresh complete" {
      TestResultsBatchPayload.deriveCompletion Fresh 5 5
      |> Expect.equal "should be Complete" (BatchCompletion.Complete(5, 5))
    }
    test "deriveCompletion Fresh partial" {
      TestResultsBatchPayload.deriveCompletion Fresh 5 3
      |> Expect.equal "should be Partial" (BatchCompletion.Partial(5, 3))
    }
    test "deriveCompletion StaleCodeEdited is Superseded" {
      TestResultsBatchPayload.deriveCompletion StaleCodeEdited 5 5
      |> Expect.equal "should be Superseded" BatchCompletion.Superseded
    }
    test "isEmpty on empty payload" {
      let p = TestResultsBatchPayload.create RunGeneration.zero Fresh (BatchCompletion.Complete(0,0)) LiveTestingActivation.Active [||]
      TestResultsBatchPayload.isEmpty p
      |> Expect.isTrue "should be empty"
    }
  ]

  testList "CoverageBitmap roundtrip" [
    test "empty array" {
      let input = [||]
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result |> Expect.equal "empty roundtrip" input
    }
    test "single true" {
      let input = [| true |]
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result |> Expect.equal "single true" input
    }
    test "single false" {
      let input = [| false |]
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result |> Expect.equal "single false" input
    }
    test "exactly 64 elements (one word boundary)" {
      let input = Array.init 64 (fun i -> i % 2 = 0)
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result |> Expect.equal "64 elements" input
    }
    test "65 elements (crosses word boundary)" {
      let input = Array.init 65 (fun i -> i % 3 = 0)
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result |> Expect.equal "65 elements" input
    }
    test "128 elements (two full words)" {
      let input = Array.init 128 (fun i -> i % 5 = 0)
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result |> Expect.equal "128 elements" input
    }
    test "all true" {
      let input = Array.create 200 true
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result |> Expect.equal "all true" input
    }
    test "all false" {
      let input = Array.create 200 false
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result |> Expect.equal "all false" input
    }
  ]

  testList "CoverageBitmap properties" [
    testProperty "roundtrip preserves all elements" <| fun (bools: bool list) ->
      let input = bools |> Array.ofList
      let result = input |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      result = input

    testProperty "popCount equals number of true values" <| fun (bools: bool list) ->
      let input = bools |> Array.ofList
      let bm = CoverageBitmap.ofBoolArray input
      let expected = input |> Array.filter id |> Array.length
      CoverageBitmap.popCount bm = expected

    testProperty "equivalent is reflexive" <| fun (bools: bool list) ->
      let bm = bools |> Array.ofList |> CoverageBitmap.ofBoolArray
      CoverageBitmap.equivalent bm bm

    testProperty "equivalent detects differences" <| fun (bools: bool list) ->
      let arr = bools |> Array.ofList
      match arr.Length with
      | 0 -> true
      | _ ->
        let bm1 = CoverageBitmap.ofBoolArray arr
        let flipped = Array.copy arr
        flipped.[0] <- not flipped.[0]
        let bm2 = CoverageBitmap.ofBoolArray flipped
        not (CoverageBitmap.equivalent bm1 bm2)

    testProperty "intersect AND identity: a AND a = a" <| fun (bools: bool list) ->
      let bm = bools |> Array.ofList |> CoverageBitmap.ofBoolArray
      match bm.Count with
      | 0 -> true
      | _ ->
        let result = CoverageBitmap.intersect bm bm
        CoverageBitmap.equivalent result bm

    testProperty "xorDiff self XOR: a XOR a = all zeros" <| fun (bools: bool list) ->
      let bm = bools |> Array.ofList |> CoverageBitmap.ofBoolArray
      match bm.Count with
      | 0 -> true
      | _ ->
        let result = CoverageBitmap.xorDiff bm bm
        CoverageBitmap.popCount result = 0
  ]

  testList "ILCoverage.computeLineCoverage" [
    test "empty slots returns empty map" {
      let state = { Slots = [||]; Hits = [||] }
      ILCoverage.computeLineCoverage state
      |> Expect.isEmpty "empty slots"
    }
    test "mismatched lengths returns empty map" {
      let sp = { File = "a.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 }
      let state = { Slots = [| sp |]; Hits = [||] }
      ILCoverage.computeLineCoverage state
      |> Expect.isEmpty "mismatched"
    }
    test "single fully covered line" {
      let sp = { File = "a.fs"; Line = 5; Column = 0; EndLine = 5; EndColumn = 10; BranchId = 0 }
      let state = { Slots = [| sp |]; Hits = [| true |] }
      let result = ILCoverage.computeLineCoverage state
      let line = result |> Map.find "a.fs" |> Map.find 5
      line |> Expect.equal "fully covered" LineCoverage.FullyCovered
    }
    test "single not covered line" {
      let sp = { File = "a.fs"; Line = 5; Column = 0; EndLine = 5; EndColumn = 10; BranchId = 0 }
      let state = { Slots = [| sp |]; Hits = [| false |] }
      let result = ILCoverage.computeLineCoverage state
      let line = result |> Map.find "a.fs" |> Map.find 5
      line |> Expect.equal "not covered" LineCoverage.NotCovered
    }
    test "partially covered line (branch coverage)" {
      let sp1 = { File = "a.fs"; Line = 10; Column = 0; EndLine = 10; EndColumn = 20; BranchId = 0 }
      let sp2 = { File = "a.fs"; Line = 10; Column = 0; EndLine = 10; EndColumn = 20; BranchId = 1 }
      let state = { Slots = [| sp1; sp2 |]; Hits = [| true; false |] }
      let result = ILCoverage.computeLineCoverage state
      let line = result |> Map.find "a.fs" |> Map.find 10
      line |> Expect.equal "partial" (LineCoverage.PartiallyCovered(1, 2))
    }
    test "multiple files grouped correctly" {
      let sp1 = { File = "a.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 }
      let sp2 = { File = "b.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 }
      let state = { Slots = [| sp1; sp2 |]; Hits = [| true; false |] }
      let result = ILCoverage.computeLineCoverage state
      result |> Map.count |> Expect.equal "two files" 2
      result |> Map.find "a.fs" |> Map.find 1 |> Expect.equal "a covered" LineCoverage.FullyCovered
      result |> Map.find "b.fs" |> Map.find 1 |> Expect.equal "b not covered" LineCoverage.NotCovered
    }
    test "multiple probes on same line all covered" {
      let sp1 = { File = "a.fs"; Line = 7; Column = 0; EndLine = 7; EndColumn = 10; BranchId = 0 }
      let sp2 = { File = "a.fs"; Line = 7; Column = 11; EndLine = 7; EndColumn = 20; BranchId = 1 }
      let sp3 = { File = "a.fs"; Line = 7; Column = 21; EndLine = 7; EndColumn = 30; BranchId = 2 }
      let state = { Slots = [| sp1; sp2; sp3 |]; Hits = [| true; true; true |] }
      let result = ILCoverage.computeLineCoverage state
      result |> Map.find "a.fs" |> Map.find 7 |> Expect.equal "all branches covered" LineCoverage.FullyCovered
    }
  ]

  testList "InstrumentationMap" [
    test "merge empty array returns empty" {
      let result = InstrumentationMap.merge [||]
      result.TotalProbes |> Expect.equal "zero probes" 0
      result.Slots |> Expect.isEmpty "no slots"
    }
    test "merge single map returns identity" {
      let sp = { File = "a.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 }
      let map = { Slots = [| sp |]; TotalProbes = 1; TrackerTypeName = "T"; HitsFieldName = "H" }
      let result = InstrumentationMap.merge [| map |]
      result.TotalProbes |> Expect.equal "one probe" 1
      result.Slots |> Expect.hasLength "one slot" 1
    }
    test "merge concatenates slots in order" {
      let sp1 = { File = "a.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 }
      let sp2 = { File = "b.fs"; Line = 2; Column = 0; EndLine = 2; EndColumn = 10; BranchId = 0 }
      let m1 = { Slots = [| sp1 |]; TotalProbes = 1; TrackerTypeName = "T"; HitsFieldName = "H" }
      let m2 = { Slots = [| sp2 |]; TotalProbes = 1; TrackerTypeName = "T"; HitsFieldName = "H" }
      let result = InstrumentationMap.merge [| m1; m2 |]
      result.TotalProbes |> Expect.equal "two probes" 2
      result.Slots.[0].File |> Expect.equal "first slot file" "a.fs"
      result.Slots.[1].File |> Expect.equal "second slot file" "b.fs"
    }
    test "toCoverageState with matching lengths" {
      let sp = { File = "a.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 }
      let map = { Slots = [| sp |]; TotalProbes = 1; TrackerTypeName = "T"; HitsFieldName = "H" }
      let state = InstrumentationMap.toCoverageState [| true |] map
      state.Slots |> Expect.hasLength "one slot" 1
      state.Hits.[0] |> Expect.isTrue "hit"
    }
    test "toCoverageState with mismatched lengths returns empty" {
      let sp = { File = "a.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 }
      let map = { Slots = [| sp |]; TotalProbes = 1; TrackerTypeName = "T"; HitsFieldName = "H" }
      let state = InstrumentationMap.toCoverageState [| true; false |] map
      state.Slots |> Expect.isEmpty "empty on mismatch"
      state.Hits |> Expect.isEmpty "empty hits on mismatch"
    }
  ]

  testList "SequencePoint.hasRange" [
    test "valid range on same line" {
      { File = "a.fs"; Line = 1; Column = 0; EndLine = 1; EndColumn = 10; BranchId = 0 }
      |> SequencePoint.hasRange
      |> Expect.isTrue "same line, endCol > col"
    }
    test "multi-line range" {
      { File = "a.fs"; Line = 1; Column = 0; EndLine = 5; EndColumn = 0; BranchId = 0 }
      |> SequencePoint.hasRange
      |> Expect.isTrue "endLine > line"
    }
    test "degenerate: endLine=0" {
      { File = "a.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 10; BranchId = 0 }
      |> SequencePoint.hasRange
      |> Expect.isFalse "endLine=0 is degenerate"
    }
    test "degenerate: same line same column" {
      { File = "a.fs"; Line = 3; Column = 5; EndLine = 3; EndColumn = 5; BranchId = 0 }
      |> SequencePoint.hasRange
      |> Expect.isFalse "zero-width range"
    }
    test "degenerate: endCol < col on same line" {
      { File = "a.fs"; Line = 3; Column = 10; EndLine = 3; EndColumn = 5; BranchId = 0 }
      |> SequencePoint.hasRange
      |> Expect.isFalse "endCol < col on same line"
    }
  ]

  testList "properties" [
    let genOutcome =
      Gen.elements [TestOutcome.Pass; TestOutcome.Fail]

    let genOutcomeList =
      Gen.choose (1, 30)
      |> Gen.bind (fun n -> Gen.listOfLength n genOutcome)

    testPropertyWithConfig propConfig "ResultWindow.toList never exceeds capacity" <|
      Prop.forAll (Arb.fromGen genOutcomeList) (fun outcomes ->
        let capacity = 10
        let w = outcomes |> List.fold (fun w o -> ResultWindow.add o w) (ResultWindow.create capacity)
        let items = ResultWindow.toList w
        (List.length items, capacity + 1) |> Expect.isLessThan "within capacity")

    testPropertyWithConfig propConfig "ResultWindow.toList length = min(added, capacity)" <|
      Prop.forAll (Arb.fromGen genOutcomeList) (fun outcomes ->
        let capacity = 10
        let w = outcomes |> List.fold (fun w o -> ResultWindow.add o w) (ResultWindow.create capacity)
        let expected = min (List.length outcomes) capacity
        ResultWindow.toList w
        |> List.length
        |> Expect.equal "length matches" expected)

    testPropertyWithConfig propConfig "TestId.create is deterministic" <|
      fun (NonEmptyString name) ->
        let genFramework = Gen.elements [TestFramework.Expecto; TestFramework.XUnit; TestFramework.NUnit]
        Prop.forAll (Arb.fromGen genFramework) (fun fw ->
          let id1 = TestId.create name fw
          let id2 = TestId.create name fw
          id1 |> Expect.equal "deterministic" id2)

    testPropertyWithConfig propConfig "TestId.create produces 16-char hex string" <|
      fun (NonEmptyString name) ->
        let (TestId.TestId id) = TestId.create name TestFramework.Expecto
        id.Length |> Expect.equal "16 chars" 16
        id |> Seq.forall Char.IsAsciiHexDigit |> Expect.isTrue "hex chars"

    testPropertyWithConfig propConfig "GutterIcon.toChar is total — never throws" <|
      Prop.forAll
        (Arb.fromGen (Gen.elements [
          GutterIcon.TestDiscovered; GutterIcon.TestPassed; GutterIcon.TestFailed
          GutterIcon.TestRunning; GutterIcon.TestSkipped; GutterIcon.TestFlaky
          GutterIcon.Covered; GutterIcon.NotCovered
        ]))
        (fun icon ->
          let _ = GutterIcon.toChar icon
          true |> Expect.isTrue "no exception")
  ]
]

[<Tests>]
let coverageSummaryTests = testList "CoverageSummary" [
  testList "fromBitmaps" [
    test "empty sequence returns empty summary" {
      let result = CoverageSummary.fromBitmaps 16 Seq.empty
      result.TotalProbes |> Expect.equal "zero probes" 0
      result.CoveredProbes |> Expect.equal "zero covered" 0
      result.DensityStrip |> Expect.isEmpty "empty strip"
    }

    test "single bitmap all covered" {
      let bm = CoverageBitmap.ofBoolArray (Array.create 64 true)
      let result = CoverageSummary.fromBitmaps 4 (Seq.singleton bm)
      result.TotalProbes |> Expect.equal "64 probes" 64
      result.CoveredProbes |> Expect.equal "all covered" 64
      (result.CoveragePercent > 99.9)
      |> Expect.isTrue "100% coverage"
      result.DensityStrip.Length |> Expect.equal "4 chunks" 4
      result.DensityStrip |> Array.forall (fun d -> d >= 1.0)
      |> Expect.isTrue "all chunks fully covered"
    }

    test "single bitmap none covered" {
      let bm = CoverageBitmap.ofBoolArray (Array.create 64 false)
      let result = CoverageSummary.fromBitmaps 4 (Seq.singleton bm)
      result.TotalProbes |> Expect.equal "64 probes" 64
      result.CoveredProbes |> Expect.equal "none covered" 0
      (result.CoveragePercent < 0.1)
      |> Expect.isTrue "0% coverage"
      result.DensityStrip |> Array.forall (fun d -> d = 0.0)
      |> Expect.isTrue "all chunks zero"
    }

    test "OR merge of two bitmaps" {
      let half1 = Array.init 64 (fun i -> i < 32)
      let half2 = Array.init 64 (fun i -> i >= 32)
      let bm1 = CoverageBitmap.ofBoolArray half1
      let bm2 = CoverageBitmap.ofBoolArray half2
      let result = CoverageSummary.fromBitmaps 4 [ bm1; bm2 ]
      result.CoveredProbes |> Expect.equal "all 64 covered via merge" 64
    }

    test "incompatible bitmap counts excluded" {
      let bm64 = CoverageBitmap.ofBoolArray (Array.create 64 true)
      let bm128 = CoverageBitmap.ofBoolArray (Array.create 128 false)
      let result = CoverageSummary.fromBitmaps 4 [ bm64; bm128 ]
      result.TotalProbes |> Expect.equal "uses first bitmap count" 64
    }

    test "zero-count bitmaps filtered" {
      let bm0 = CoverageBitmap.empty
      let bm64 = CoverageBitmap.ofBoolArray (Array.init 64 (fun i -> i < 8))
      let result = CoverageSummary.fromBitmaps 4 [ bm0; bm64 ]
      result.TotalProbes |> Expect.equal "only valid bitmap" 64
    }
  ]

  testList "toTextStrip" [
    test "empty summary returns empty string" {
      CoverageSummary.toTextStrip CoverageSummary.empty
      |> Expect.equal "empty" ""
    }

    test "full coverage gives block chars" {
      let s = { TotalProbes = 4; CoveredProbes = 4; CoveragePercent = 100.0; DensityStrip = [| 1.0; 1.0; 1.0; 1.0 |] }
      let result = CoverageSummary.toTextStrip s
      result |> Expect.stringContains "has full blocks" "\u2588"
      result |> Expect.stringContains "has pct" "100"
    }

    test "zero coverage gives dots" {
      let s = { TotalProbes = 4; CoveredProbes = 0; CoveragePercent = 0.0; DensityStrip = [| 0.0; 0.0; 0.0; 0.0 |] }
      let result = CoverageSummary.toTextStrip s
      result |> Expect.stringContains "has dot" "\u00B7"
    }

    test "mixed density uses various block chars" {
      let s = { TotalProbes = 64; CoveredProbes = 28; CoveragePercent = 43.75
                DensityStrip = [| 1.0; 0.6; 0.3; 0.0 |] }
      let result = CoverageSummary.toTextStrip s
      result |> Expect.stringContains "has full block" "\u2588"
      result |> Expect.stringContains "has medium block" "\u2593"
      result |> Expect.stringContains "has light block" "\u2592"
      result |> Expect.stringContains "has dot" "\u00B7"
    }

    test "percentage is formatted" {
      let s = { TotalProbes = 100; CoveredProbes = 44; CoveragePercent = 44.0; DensityStrip = [| 0.44 |] }
      let result = CoverageSummary.toTextStrip s
      result |> Expect.stringContains "pct formatted" "44"
    }
  ]
]
