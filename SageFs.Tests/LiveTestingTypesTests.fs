module SageFs.Tests.LiveTestingTypesTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

[<Tests>]
let liveTestingTypesTests = testList "LiveTestingTypes" [

  testList "TestId" [
    test "create is deterministic" {
      let id1 = TestId.create "My.Test.name" "xunit"
      let id2 = TestId.create "My.Test.name" "xunit"
      id1 |> Expect.equal "same inputs same id" id2
    }
    test "create produces 16-char hex" {
      let (TestId.TestId id) = TestId.create "hello" "expecto"
      id.Length |> Expect.equal "should be 16 chars" 16
      id |> Seq.forall (fun c -> Char.IsAsciiHexDigit c)
      |> Expect.isTrue "should be hex chars"
    }
    test "different fullName produces different id" {
      let id1 = TestId.create "test.A" "xunit"
      let id2 = TestId.create "test.B" "xunit"
      id1 = id2 |> Expect.isFalse "should differ"
    }
    test "different framework produces different id" {
      let id1 = TestId.create "test.A" "xunit"
      let id2 = TestId.create "test.A" "nunit"
      id1 = id2 |> Expect.isFalse "should differ"
    }
    test "empty input still produces valid id" {
      let (TestId.TestId id) = TestId.create "" ""
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
      let tid = TestId.create "test" "xunit"
      let history = FlakyDetection.recordResult tid (TestResult.Passed TimeSpan.Zero) Map.empty
      history |> Map.containsKey tid |> Expect.isTrue "should have entry"
    }
    test "assessTest on recorded history" {
      let tid = TestId.create "test" "xunit"
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
      let s = TestSummary.fromStatuses statuses
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
      TestSummary.fromStatuses [||]
      |> fun s -> s.Total
      |> Expect.equal "should be 0" 0
    }
  ]

  testList "CategoryDetection" [
    test "integration label maps to Integration" {
      CategoryDetection.categorize ["Integration"] "My.Test" "xunit" [||]
      |> Expect.equal "should be Integration" TestCategory.Integration
    }
    test "browser label maps to Browser" {
      CategoryDetection.categorize ["Browser"] "My.Test" "xunit" [||]
      |> Expect.equal "should be Browser" TestCategory.Browser
    }
    test "e2e label maps to Browser" {
      CategoryDetection.categorize ["E2E"] "My.Test" "xunit" [||]
      |> Expect.equal "should be Browser" TestCategory.Browser
    }
    test "benchmark label maps to Benchmark" {
      CategoryDetection.categorize ["Benchmark"] "My.Test" "xunit" [||]
      |> Expect.equal "should be Benchmark" TestCategory.Benchmark
    }
    test "property label maps to Property" {
      CategoryDetection.categorize ["Property"] "My.Test" "expecto" [||]
      |> Expect.equal "should be Property" TestCategory.Property
    }
    test "architecture label maps to Architecture" {
      CategoryDetection.categorize ["Architecture"] "My.Test" "nunit" [||]
      |> Expect.equal "should be Architecture" TestCategory.Architecture
    }
    test "arch label maps to Architecture" {
      CategoryDetection.categorize ["arch"] "My.Test" "nunit" [||]
      |> Expect.equal "should be Architecture" TestCategory.Architecture
    }
    test "Playwright assembly maps to Browser" {
      CategoryDetection.categorize [] "My.Test" "xunit" [| "Microsoft.Playwright" |]
      |> Expect.equal "should be Browser" TestCategory.Browser
    }
    test "BenchmarkDotNet assembly maps to Benchmark" {
      CategoryDetection.categorize [] "My.Test" "xunit" [| "BenchmarkDotNet" |]
      |> Expect.equal "should be Benchmark" TestCategory.Benchmark
    }
    test "integration in fullName maps to Integration" {
      CategoryDetection.categorize [] "My.IntegrationTests.test1" "xunit" [||]
      |> Expect.equal "should be Integration" TestCategory.Integration
    }
    test "e2e in fullName maps to Browser" {
      CategoryDetection.categorize [] "My.E2ETests.test1" "xunit" [||]
      |> Expect.equal "should be Browser" TestCategory.Browser
    }
    test "no signals defaults to Unit" {
      CategoryDetection.categorize [] "My.Tests.simpleTest" "expecto" [||]
      |> Expect.equal "should be Unit" TestCategory.Unit
    }
    test "label detection is case-insensitive" {
      CategoryDetection.categorize ["INTEGRATION"] "My.Test" "xunit" [||]
      |> Expect.equal "should be Integration" TestCategory.Integration
    }
  ]

  testList "PolicyFilter" [
    let mkTest cat = {
      Id = TestId.create "t" "f"
      FullName = "t"
      DisplayName = "t"
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = "xunit"
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
      let tid1 = TestId.create "test1" "xunit"
      let tid2 = TestId.create "test2" "xunit"
      let g = TestDependencyGraph.fromDirect (Map.ofList ["MyModule.add", [| tid1; tid2 |]])
      TestDependencyGraph.findAffected ["MyModule.add"] g
      |> Array.length
      |> Expect.equal "should find 2 tests" 2
    }
    test "findAffected returns empty for unknown symbol" {
      let g = TestDependencyGraph.fromDirect (Map.ofList ["MyModule.add", [| TestId.create "t" "x" |]])
      TestDependencyGraph.findAffected ["Unknown.func"] g
      |> Array.length
      |> Expect.equal "should find 0 tests" 0
    }
    test "findAffected deduplicates across symbols" {
      let tid = TestId.create "test1" "xunit"
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
      let tid = TestId.create "test1" "xunit"
      let callGraph = Map.ofList [ "testFunc", [| "helperA"; "helperB" |] ]
      let direct = Map.ofList [ "testFunc", [| tid |] ]
      let transitive = TestDependencyGraph.computeTransitiveCoverage callGraph direct
      transitive |> Map.containsKey "helperA" |> Expect.isTrue "helper should be covered"
      transitive |> Map.containsKey "helperB" |> Expect.isTrue "helper should be covered"
    }
    test "mergePerFileIndexes combines across files" {
      let tid1 = TestId.create "t1" "x"
      let tid2 = TestId.create "t2" "x"
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
      SourceMapping.attributeMatchesFramework "xunit" "Fact"
      |> Expect.isTrue "Fact matches xunit"
    }
    test "attributeMatchesFramework xunit FactAttribute" {
      SourceMapping.attributeMatchesFramework "xunit" "FactAttribute"
      |> Expect.isTrue "FactAttribute matches xunit"
    }
    test "attributeMatchesFramework expecto Tests" {
      SourceMapping.attributeMatchesFramework "expecto" "Tests"
      |> Expect.isTrue "Tests matches expecto"
    }
    test "attributeMatchesFramework nunit Test" {
      SourceMapping.attributeMatchesFramework "nunit" "Test"
      |> Expect.isTrue "Test matches nunit"
    }
    test "attributeMatchesFramework mstest TestMethod" {
      SourceMapping.attributeMatchesFramework "mstest" "TestMethod"
      |> Expect.isTrue "TestMethod matches mstest"
    }
    test "attributeMatchesFramework unknown returns false" {
      SourceMapping.attributeMatchesFramework "unknown" "Fact"
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
      let p = TestResultsBatchPayload.create RunGeneration.zero Fresh (BatchCompletion.Complete(0,0)) [||]
      TestResultsBatchPayload.isEmpty p
      |> Expect.isTrue "should be empty"
    }
  ]
]
