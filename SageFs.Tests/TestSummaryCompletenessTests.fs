module SageFs.Tests.TestSummaryCompletenessTests

/// WHY — `TestSummary` is what every agent and every test reads to decide
/// whether tests passed. `fromStatuses` and `applyStatusCountDelta` both ended
/// in a catch-all `| _ -> ()`, so `Detected`, `Queued` and `Skipped` fell into
/// NO bucket. A suite of three detected tests therefore reported
///
///   Total=3 Passed=0 Failed=0 Stale=0 Running=0
///
/// which is indistinguishable from "nothing happened" — and is exactly what the
/// live-testing integration tests observed when a run was accepted, reported
/// "Queued 3 test(s)", and every counter stayed at zero.
///
/// The invariant: the buckets must ACCOUNT FOR the total. A summary that loses
/// statuses is a false green, and this is the same claim-without-outcome
/// defect as the queued-run bug, one layer down.
///
/// The RED state was confirmed in the live REPL before the fix: three cases,
/// 3 failed / 0 passed.

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let private activation = LiveTestingActivation.Active

let private summaryOf (statuses: TestRunStatus array) =
  TestSummary.fromStatuses activation statuses

/// The property the whole file is about: nothing falls between the buckets.
let private sumOf (s: TestSummary) =
  s.Passed + s.Failed + s.Stale + s.Running + s.Disabled

[<Tests>]
let testSummaryCompletenessTests =
  testList "TestSummary accounts for every status" [

    testCase "WHY — a detected suite is not silently all-zero: Detected has no result yet, so it is stale" <| fun _ ->
      let s = summaryOf [| TestRunStatus.Detected; TestRunStatus.Detected; TestRunStatus.Detected |]
      s.Total |> Expect.equal "total counts them" 3
      s.Stale |> Expect.equal "a detected test has no result, which is exactly 'stale'" 3
      sumOf s |> Expect.equal "the buckets must account for the total" s.Total

    testCase "WHY — a queued suite is accounted for, so 'accepted but not finished' is visible" <| fun _ ->
      let s = summaryOf [| TestRunStatus.Queued; TestRunStatus.Queued |]
      s.Total |> Expect.equal "total counts them" 2
      s.Running |> Expect.equal "a queued test is in flight" 2
      sumOf s |> Expect.equal "the buckets must account for the total" s.Total

    testCase "WHY — a skipped suite is accounted for, so 'did not run' is reported rather than dropped" <| fun _ ->
      let s = summaryOf [| TestRunStatus.Skipped "excluded"; TestRunStatus.Skipped "excluded" |]
      s.Total |> Expect.equal "total counts them" 2
      s.Stale |> Expect.equal "a skipped test has no passing result" 2
      sumOf s |> Expect.equal "the buckets must account for the total" s.Total

    testCase "WHY — a mixed suite still accounts for every test" <| fun _ ->
      let s =
        summaryOf
          [| TestRunStatus.Passed (TimeSpan.FromMilliseconds 1.0)
             TestRunStatus.Failed (TestFailure.AssertionFailed "boom", TimeSpan.Zero)
             TestRunStatus.Detected
             TestRunStatus.Queued
             TestRunStatus.Skipped "x"
             TestRunStatus.Running
             TestRunStatus.PolicyDisabled
             TestRunStatus.Stale |]

      s.Total |> Expect.equal "total counts them" 8
      sumOf s |> Expect.equal "every status lands in a bucket" s.Total

    testCase "WHY — the empty summary is still consistent" <| fun _ ->
      let s = summaryOf [||]
      s.Total |> Expect.equal "nothing" 0
      sumOf s |> Expect.equal "and nothing unaccounted for" 0

    testCase "WHY — a NotRun result is VISIBLE, not a silent zero" <| fun _ ->
      // A dispatched-but-unrun run records a NotRun result. The end-to-end
      // story must be "these tests have no result", never "zero of zero".
      let s = summaryOf [| TestRunStatus.Skipped "never ran" |]
      s.Total |> Expect.equal "total counts it" 1
      sumOf s |> Expect.equal "and it is accounted for" s.Total
      s.Stale |> Expect.equal "a never-ran test is not passing" 1
  ]
