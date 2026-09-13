/// ## PendingRunSummary Tests
///
/// PendingRunSummary is the O(1) running aggregate that replaced the unbounded
/// `TestRunResult array list` live-test accumulator (the one that grew until the
/// daemon reached ~16 GB when a run's completion event never fired). These tests
/// prove it produces the exact same run-complete summary the old array-based
/// `summaryLine` did — computed from constant-size counts, retaining zero result
/// payloads — and that its only variable-length field (distinct never-reported
/// reasons) is capped so memory stays bounded no matter what streams in.
module PendingRunSummaryTests

open Expecto
open Expecto.Flip
open System
open SageFs
open SageFs.Features.LiveTesting

let private ts (ms: float) = TimeSpan.FromMilliseconds ms

let private mkResult (name: string) (result: TestResult) : TestRunResult =
  { TestId = TestId.create name TestFramework.Expecto
    TestName = name
    Result = result
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

let private passed name ms = mkResult name (TestResult.Passed(ts ms))
let private failed name ms = mkResult name (TestResult.Failed(TestFailure.AssertionFailed "boom", ts ms))
let private skipped name = mkResult name (TestResult.Skipped "quarantined")
let private noResult name reason = mkResult name (TestResult.NoResult reason)

[<Tests>]
let tests =
  testList "PendingRunSummary" [
    testCase "empty has zero counts and a clean summary line" <| fun _ ->
      let agg = PendingRunSummary.empty
      agg |> PendingRunSummary.count |> Expect.equal "empty count is zero" 0
      (PendingRunSummary.toOutputLine agg).Text.Contains "0 passed, 0 failed, 0 skipped"
      |> Expect.isTrue "empty summary shows all-zero counts"

    testCase "addBatch accumulates passed/failed/skipped counts and duration" <| fun _ ->
      let agg =
        PendingRunSummary.empty
        |> PendingRunSummary.addBatch [| passed "a" 10.0; failed "b" 5.0; skipped "c" |]
      agg.Passed |> Expect.equal "one passed" 1
      agg.Failed |> Expect.equal "one failed" 1
      agg.Skipped |> Expect.equal "one skipped" 1
      agg.Total |> Expect.equal "three total" 3
      agg.TotalDurationMs |> Expect.equal "duration sums passed + failed" 15.0

    testCase "a failed run renders an Error line with exact counts across batches" <| fun _ ->
      let line =
        PendingRunSummary.empty
        |> PendingRunSummary.addBatches [ [| passed "a" 10.0; failed "b" 5.0 |]; [| skipped "c" |] ]
        |> PendingRunSummary.toOutputLine
      line.Kind |> Expect.equal "a failed run is surfaced as an error" OutputKind.Error
      line.Text
      |> Expect.equal "summary reflects every batch"
           "🧪 Test run complete: 1 passed, 1 failed, 1 skipped (15ms)"

    testCase "an all-pass run renders an Info line" <| fun _ ->
      let line =
        PendingRunSummary.empty
        |> PendingRunSummary.addBatch [| passed "a" 1.0; passed "b" 2.0 |]
        |> PendingRunSummary.toOutputLine
      line.Kind |> Expect.equal "an all-pass run is informational" OutputKind.Info

    testCase "never-reported results add the incomplete suffix and flip to Error" <| fun _ ->
      let line =
        PendingRunSummary.empty
        |> PendingRunSummary.addBatch [| passed "a" 1.0; noResult "b" NoResultReason.StreamEnded |]
        |> PendingRunSummary.toOutputLine
      line.Kind |> Expect.equal "a never-reported result flips the run to error" OutputKind.Error
      line.Text.Contains "1 of 2 never reported"
      |> Expect.isTrue "summary names how many of how many never reported"

    testCase "empty batches are skipped, matching the old buffer's filter" <| fun _ ->
      let agg =
        PendingRunSummary.empty
        |> PendingRunSummary.addBatches [ [||]; [| passed "a" 1.0 |]; [||] ]
      agg.Total |> Expect.equal "empty batches contribute nothing" 1

    testProperty "count equals the number of results folded, independent of how they were batched"
    <| fun (raw: int) ->
      let n = (abs raw) % 5000
      let all = Array.init n (fun i -> passed (sprintf "t%d" i) 1.0)
      // Splitting the same results into many small batches must not change the aggregate.
      let batches = all |> Array.chunkBySize 7 |> Array.toList
      let agg = PendingRunSummary.empty |> PendingRunSummary.addBatches batches
      agg.Passed = n && agg.Total = n && PendingRunSummary.count agg = n

    testCase "distinct never-reported reasons are capped so they cannot grow unbounded" <| fun _ ->
      // 1000 DISTINCT TransportFailed messages: the COUNT must be exact, but the
      // retained distinct-reason list must stay bounded (this is the memory guard).
      let batch =
        Array.init 1000 (fun i ->
          noResult (sprintf "t%d" i) (NoResultReason.TransportFailed(sprintf "err-%d" i)))
      let agg = PendingRunSummary.empty |> PendingRunSummary.addBatch batch
      agg.NeverReportedCount |> Expect.equal "every never-reported result is counted" 1000
      (List.length agg.NeverReportedReasons <= 16)
      |> Expect.isTrue "retained distinct reasons are capped (bounded memory)"
  ]
