module SageFs.Tests.Tier0CorrectnessTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let private mkEntry (name: string) (status: TestRunStatus) : TestStatusEntry =
  let tid = TestId.create name TestFramework.Expecto
  { TestId = tid
    DisplayName = name
    FullName = name
    Origin = TestOrigin.ReflectionOnly
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
    CurrentPolicy = RunPolicy.OnEveryChange
    Status = status
    PreviousStatus = TestRunStatus.Detected }

let collectResultsStaleTests =
  testList "collectResults Stale handling" [

    test "stale tests are NOT counted as passed+skipped" {
      let entries = [|
        mkEntry "test.pass" (TestRunStatus.Passed (System.TimeSpan.FromMilliseconds 100.))
        mkEntry "test.stale" TestRunStatus.Stale
        mkEntry "test.skip" (TestRunStatus.Skipped "reason")
      |]
      let triggered = entries |> Array.map (fun e -> e.TestId) |> Set.ofArray
      let flakyHistory = Map.empty
      let (passedPlusSkipped, _failed, _running, stale, _failures, _runningNames) =
        SageFs.McpTools.collectResults entries triggered flakyHistory
      passedPlusSkipped
      |> Expect.equal "passed+skipped should NOT include stale" 2
      stale |> Expect.equal "stale count should be 1" 1
    }

    test "multiple stale tests counted separately" {
      let entries = [|
        mkEntry "test.stale1" TestRunStatus.Stale
        mkEntry "test.stale2" TestRunStatus.Stale
        mkEntry "test.stale3" TestRunStatus.Stale
      |]
      let triggered = entries |> Array.map (fun e -> e.TestId) |> Set.ofArray
      let flakyHistory = Map.empty
      let (passedPlusSkipped, _failed, _running, stale, _failures, _runningNames) =
        SageFs.McpTools.collectResults entries triggered flakyHistory
      passedPlusSkipped |> Expect.equal "no passed or skipped" 0
      stale |> Expect.equal "3 stale tests" 3
    }

    test "mixed statuses all counted correctly" {
      let entries = [|
        mkEntry "p1" (TestRunStatus.Passed (System.TimeSpan.FromMilliseconds 10.))
        mkEntry "p2" (TestRunStatus.Passed (System.TimeSpan.FromMilliseconds 20.))
        mkEntry "f1" (TestRunStatus.Failed (TestFailure.AssertionFailed "oops", System.TimeSpan.FromMilliseconds 30.))
        mkEntry "s1" TestRunStatus.Stale
        mkEntry "s2" TestRunStatus.Stale
        mkEntry "r1" TestRunStatus.Running
        mkEntry "sk1" (TestRunStatus.Skipped "disabled")
      |]
      let triggered = entries |> Array.map (fun e -> e.TestId) |> Set.ofArray
      let flakyHistory = Map.empty
      let (passedPlusSkipped, failed, running, stale, _failures, _runningNames) =
        SageFs.McpTools.collectResults entries triggered flakyHistory
      passedPlusSkipped |> Expect.equal "2 passed + 1 skipped" 3
      failed |> Expect.equal "1 failed" 1
      running |> Expect.equal "1 running" 1
      stale |> Expect.equal "2 stale" 2
    }
  ]

let alreadyRunningTests =
  testList "RunTestsResult AlreadyRunning" [
    test "AlreadyRunning case exists and formats" {
      let formatted =
        SageFs.McpTools.RunTestsResult.format SageFs.McpTools.RunTestsResult.AlreadyRunning
      formatted |> Expect.stringContains "mentions already running" "already in progress"
    }
  ]

[<Tests>]
let tests =
  testList "Tier 0 Correctness" [
    collectResultsStaleTests
    alreadyRunningTests
  ]
