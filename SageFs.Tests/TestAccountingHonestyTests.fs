module SageFs.Tests.TestAccountingHonestyTests

/// WHY — a discovery-only batch once rendered as "12605 test result(s)
/// received (Fresh)" immediately beside "0 passed, 0 failed". An agent reads
/// that as a fully green suite that ran 12,605 passing tests. Nothing ran.
/// The Lemmings roast showed small models doing exactly that.
///
/// The contract: whenever the batch carries no executions, the line must say
/// so in those words. "Fresh" is a freshness label, never an execution result,
/// and it must not stand alone next to two zeroes.
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting

let private summary (total: int) (passed: int) (failed: int) (stale: int) (running: int) : TestSummary =
  { Total = total
    Passed = passed
    Failed = failed
    Stale = stale
    Running = running
    Disabled = 0
    Enabled = true }

[<Tests>]
let testAccountingHonestyTests = testList "test-result accounting is honest" [

  testCase "WHY — a discovery-only batch must say not run, never imply a green suite" <| fun _ ->
    let payload : TestResultsBatchPayload =
      { Generation = RunGeneration 7
        Freshness = ResultFreshness.Fresh
        Completion = BatchCompletion.Complete(0, 0)
        Entries = Array.empty
        Summary = summary 12605 0 0 0 0
        LastDecision = None }

    let line : string = SageFs.McpPushNotifications.PushEvent.formatForLlm (SageFs.McpPushNotifications.PushEvent.TestResultsBatch payload)

    line
    |> Expect.stringContains "the count of discovered tests must be visible" "12605"

    line
    |> Expect.stringContains "the un-run count must be stated in words" "not run"

    line
    |> Expect.stringContains "the passed count must stay explicit" "0 passed"

    // The exact misleading shape the roast captured.
    line.Contains "test result(s) received"
    |> Expect.isFalse "a bare 'N test result(s) received (Fresh)' must never ship again"

  testCase "WHY — a real run still reports its true executed counts" <| fun _ ->
    let payload : TestResultsBatchPayload =
      { Generation = RunGeneration 8
        Freshness = ResultFreshness.Fresh
        Completion = BatchCompletion.Complete(11, 11)
        Entries = Array.empty
        Summary = summary 11 9 2 0 0
        LastDecision = None }

    let line : string = SageFs.McpPushNotifications.PushEvent.formatForLlm (SageFs.McpPushNotifications.PushEvent.TestResultsBatch payload)

    line |> Expect.stringContains "passed count" "9 passed"
    line |> Expect.stringContains "failed count" "2 failed"
    // 11 total - 9 passed - 2 failed = 0 not run, and the arithmetic must not
    // go negative or invent an un-run bucket for a completed run.
    line |> Expect.stringContains "not-run count" "0 not run"

  testCase "WHY — never reports a negative not-run count when a run is in flight" <| fun _ ->
    // A concurrent summary can momentarily report more passed+failed than the
    // total it was captured with. The formatter must clamp, not print -3.
    let payload : TestResultsBatchPayload =
      { Generation = RunGeneration 9
        Freshness = ResultFreshness.Fresh
        Completion = BatchCompletion.Partial(5, 4)
        Entries = Array.empty
        Summary = summary 2 3 2 0 1
        LastDecision = None }

    let line : string = SageFs.McpPushNotifications.PushEvent.formatForLlm (SageFs.McpPushNotifications.PushEvent.TestResultsBatch payload)

    line.Contains "-"
    |> Expect.isFalse "a negative count would be nonsense to an agent"
]
