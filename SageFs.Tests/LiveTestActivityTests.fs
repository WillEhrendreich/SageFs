module SageFs.Tests.LiveTestActivityTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTestActivity

let private passed = TestRunStatus.Passed (TimeSpan.FromMilliseconds 5.)
let private failed = TestRunStatus.Failed (TestFailure.AssertionFailed "boom", TimeSpan.FromMilliseconds 5.)

/// One status per case index, so FsCheck can build arbitrary mixes.
let private statusOf (i: int) : TestRunStatus =
  match abs i % 8 with
  | 0 -> TestRunStatus.Detected
  | 1 -> TestRunStatus.Queued
  | 2 -> TestRunStatus.Running
  | 3 -> passed
  | 4 -> failed
  | 5 -> TestRunStatus.Skipped "ignored"
  | 6 -> TestRunStatus.Stale
  | _ -> TestRunStatus.PolicyDisabled

let private input (activation: LiveTestingActivation) (discovery: DiscoveryProgress) (statuses: TestRunStatus list) : ActivityInput =
  { Activation = activation
    Discovery = discovery
    Frameworks = [ "Expecto" ]
    Compile = CompileBlock.NoCompileErrors
    Statuses = List.toArray statuses }

let private active = LiveTestingActivation.Active

[<Tests>]
let tallyTests =
  testList "LiveTestActivity tally" [
    testProperty "WHY — TestTally.ofStatuses — every test lands in exactly one bucket because counts that do not add up to the total mislead" <| fun (cases: int list) ->
      let statuses = cases |> List.map statusOf |> List.toArray
      TestTally.total (TestTally.ofStatuses statuses) = statuses.Length

    testCase "WHY — TestTally.ofStatuses — detected and queued tests are not yet run because they must never read as passed" <| fun _ ->
      TestTally.ofStatuses [| TestRunStatus.Detected; TestRunStatus.Queued; passed |]
      |> Expect.equal "two not yet run, one passed" { TestTally.empty with NotYetRun = 2; Passed = 1 }
  ]

[<Tests>]
let decideTests =
  testList "LiveTestActivity decide" [
    testCase "WHY — LiveTestActivity.decide — disabled is Off whatever else is known because off must read as off" <| fun _ ->
      LiveTestActivity.decide (input LiveTestingActivation.Inactive DiscoveryProgress.InProgress [ failed ])
      |> Expect.equal "off" LiveTestActivity.Off

    testCase "WHY — LiveTestActivity.decide — discovery with no tests known yet is Discovering because the user is waiting on it" <| fun _ ->
      LiveTestActivity.decide (input active DiscoveryProgress.InProgress [])
      |> Expect.equal "discovering" LiveTestActivity.Discovering

    testCase "WHY — LiveTestActivity.decide — a rediscovery keeps showing the results because they are still the latest" <| fun _ ->
      LiveTestActivity.decide (input active DiscoveryProgress.InProgress [ passed ])
      |> Expect.equal "settled" (LiveTestActivity.Settled { TestTally.empty with Passed = 1 })

    testCase "WHY — LiveTestActivity.decide — a failed discovery says so because a spinner that never ends explains nothing" <| fun _ ->
      LiveTestActivity.decide (input active (DiscoveryProgress.Failed "could not load Tests.dll") [])
      |> Expect.equal "failed" (LiveTestActivity.DiscoveryFailed "could not load Tests.dll")

    testCase "WHY — LiveTestActivity.decide — a finished discovery with no tests is NoTestsFound, naming the frameworks, because it is not still discovering" <| fun _ ->
      LiveTestActivity.decide (input active DiscoveryProgress.Completed [])
      |> Expect.equal "no tests" (LiveTestActivity.NoTestsFound [ "Expecto" ])

    testCase "WHY — LiveTestActivity.decide — a compile error blocks the run and keeps the last results because the user must see why nothing re-ran" <| fun _ ->
      let blocked = { input active DiscoveryProgress.Completed [ passed; TestRunStatus.Stale ] with Compile = CompileBlock.CompileErrors ("/src/Math.fs", 2) }
      LiveTestActivity.decide blocked
      |> Expect.equal "blocked" (LiveTestActivity.BlockedByCompileErrors ("/src/Math.fs", 2, { TestTally.empty with Passed = 1; Stale = 1 }))

    testCase "WHY — LiveTestActivity.decide — any running test makes it Running because old failures must not hide a run in progress" <| fun _ ->
      LiveTestActivity.decide (input active DiscoveryProgress.Completed [ failed; TestRunStatus.Running ])
      |> Expect.equal "running" (LiveTestActivity.Running { TestTally.empty with Failed = 1; Running = 1 })
  ]

[<Tests>]
let describeTests =
  let describe = LiveTestActivity.describe
  testList "LiveTestActivity describe" [
    testCase "WHY — LiveTestActivity.describe — off" <| fun _ ->
      describe LiveTestActivity.Off |> Expect.equal "wording" "Live testing is off"

    testCase "WHY — LiveTestActivity.describe — discovering" <| fun _ ->
      describe LiveTestActivity.Discovering |> Expect.equal "wording" "Looking for tests…"

    testCase "WHY — LiveTestActivity.describe — a failed discovery gives the reason" <| fun _ ->
      describe (LiveTestActivity.DiscoveryFailed "could not load Tests.dll")
      |> Expect.equal "wording" "Could not discover tests: could not load Tests.dll"

    testCase "WHY — LiveTestActivity.describe — no tests and no framework says no framework was detected because that is what the user must fix" <| fun _ ->
      describe (LiveTestActivity.NoTestsFound [])
      |> Expect.equal "wording" "No tests found — no test framework detected in this session"

    testCase "WHY — LiveTestActivity.describe — no tests with a framework names it" <| fun _ ->
      describe (LiveTestActivity.NoTestsFound [ "Expecto"; "xUnit" ])
      |> Expect.equal "wording" "No tests found (Expecto, xUnit detected)"

    testCase "WHY — LiveTestActivity.describe — a compile block names the file and says the results are from the last good build" <| fun _ ->
      describe (LiveTestActivity.BlockedByCompileErrors ("/src/Math.fs", 2, { TestTally.empty with Passed = 3 }))
      |> Expect.equal "wording" "Waiting for Math.fs to compile (2 errors) — showing the last good results: 3 passed"

    testCase "WHY — LiveTestActivity.describe — running counts the tests in flight" <| fun _ ->
      describe (LiveTestActivity.Running { TestTally.empty with Running = 2; Passed = 8 })
      |> Expect.equal "wording" "Running 2 of 10 tests…"

    testCase "WHY — LiveTestActivity.describe — never-run tests are not reported as passed because 0/12 ✓ reads as success" <| fun _ ->
      describe (LiveTestActivity.Settled { TestTally.empty with NotYetRun = 12 })
      |> Expect.equal "wording" "12 not yet run"

    testCase "WHY — LiveTestActivity.describe — a mixed result lists every non-empty bucket in a fixed order" <| fun _ ->
      describe (LiveTestActivity.Settled { TestTally.empty with Passed = 12; Failed = 1; Stale = 2; NotYetRun = 3 })
      |> Expect.equal "wording" "1 failed · 12 passed · 2 stale · 3 not yet run"

    testCase "WHY — LiveTestActivity.describe — a clean run says so plainly" <| fun _ ->
      describe (LiveTestActivity.Settled { TestTally.empty with Passed = 12 })
      |> Expect.equal "wording" "All 12 tests passed"
  ]
