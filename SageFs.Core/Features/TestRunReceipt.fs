/// The receipt `run_tests` returns. Its types live in this module, not at namespace level,
/// because `Pending`, `Failed` and `Skipped` are also cases of other unions in SageFs.Features
/// and a namespace-level `RunReceipt.Pending` makes every unqualified use of those ambiguous.
module SageFs.Features.RunReceipts

open System
open SageFs.Features.LiveTesting
open SageFs.Features.Verification

/// Why `run_tests` did not start a run. Every case says why and what to do, so
/// an agent never has to guess what a refusal means.
[<RequireQualifiedAccess>]
type RunRefusal =
  /// The session cannot be believed right now (warming up, stale, ambiguous...).
  | SessionNotTrusted of SessionTrust
  /// The session has no discovered tests yet.
  | NothingDiscovered
  /// Tests exist, but none matched the filters the caller gave.
  | NoTestMatched of filters: string
  /// Named tests that were not discovered into this session's own cycle.
  | NotAttributed of TestId list
  /// Named tests that are not among the session's discovered tests.
  | NotDiscovered

/// What one requested test did in THIS run, and only this run.
[<RequireQualifiedAccess>]
type LineOutcome =
  | Passed of duration: TimeSpan
  | Failed of reason: string
  | Skipped of reason: string
  /// No result from this run: never reported, cut off, or only an earlier run's
  /// result exists. Not a failure (nothing is known about the code) and never a pass.
  | DidNotReport of reason: string

type ReceiptLine =
  { Id: TestId
    Name: string
    Outcome: LineOutcome }

[<RequireQualifiedAccess>]
type RunVerdict =
  /// Tests were named and every one passed in this run.
  | AllPassed
  | SomeFailed
  /// Nothing failed, but not every test passed here (skipped, cut off, never reported,
  /// or no tests named). Never to be read as green.
  | Incomplete

type RunCounts =
  { Passing: int
    Failing: int
    Skipping: int
    Unreported: int }

type RanReceipt =
  { RequestId: RunRequestId
    Session: string
    Generation: RunGeneration
    Verdict: RunVerdict
    Counts: RunCounts
    Lines: ReceiptLine list }

/// What the engine can say about a `run_tests` request right now.
[<RequireQualifiedAccess>]
type RunReceipt =
  | Refused of RunRefusal
  /// Generation allocated; the worker has not started the run.
  | Pending of RunRequestId * requested: int
  /// The worker is running it.
  | Started of RunRequestId * requested: int
  | Ran of RanReceipt
  /// No record of this request (never made, or evicted from the bounded map), so
  /// nothing can be claimed about it.
  | Unattributable of RunRequestId

module RunVerdict =
  /// Stable wire token, one per case.
  let token = function
    | RunVerdict.AllPassed -> "AllPassed"
    | RunVerdict.SomeFailed -> "SomeFailed"
    | RunVerdict.Incomplete -> "Incomplete"

module LineOutcome =
  let token = function
    | LineOutcome.Passed _ -> "Passed"
    | LineOutcome.Failed _ -> "Failed"
    | LineOutcome.Skipped _ -> "Skipped"
    | LineOutcome.DidNotReport _ -> "DidNotReport"

module RunRefusal =
  /// What went wrong and what to do about it.
  let describe (refusal: RunRefusal) : string =
    match refusal with
    | RunRefusal.SessionNotTrusted trust ->
      sprintf "Not running: %s. Wait for the session to be Ready (get_session_status with wait_seconds), or fix what it names." (SessionTrust.describe trust)
    | RunRefusal.NothingDiscovered ->
      "Not running: this session has no discovered tests yet. Enable live testing and wait for discovery (list_tests shows them when it is done)."
    | RunRefusal.NoTestMatched filters ->
      sprintf "Not running: no discovered test matched (%s). list_tests shows what exists." filters
    | RunRefusal.NotAttributed ids ->
      sprintf "Not running: %d named test(s) were not discovered into this session's own test list, so running them would verify some other session's code." ids.Length
    | RunRefusal.NotDiscovered ->
      "Not running: some named test ids are not among this session's discovered tests."

module TestRunReceipt =
  let private describeFailure (failure: TestFailure) : string =
    match failure with
    | TestFailure.AssertionFailed message -> message
    | TestFailure.ExceptionThrown (message, _) -> message
    | TestFailure.TimedOut after -> sprintf "timed out after %.0fs" after.TotalSeconds

  /// One test's outcome in the run that had `generation`. A result only counts if
  /// this very run stamped it: a pass from an earlier run is not this run's pass.
  let private lineOutcome (generation: RunGeneration) (state: LiveTestState) (id: TestId) : LineOutcome =
    match Map.tryFind id state.LastResults, Map.tryFind id state.ResultGenerations with
    | Some result, Some stamped when stamped = generation ->
      match result.Result with
      | TestResult.Passed duration -> LineOutcome.Passed duration
      | TestResult.Failed (failure, _) -> LineOutcome.Failed (describeFailure failure)
      | TestResult.Skipped reason -> LineOutcome.Skipped reason
      | TestResult.NotRun -> LineOutcome.DidNotReport "the engine marked it not run"
      | TestResult.NoResult reason -> LineOutcome.DidNotReport (NoResultReason.describe reason)
    | Some _, _ -> LineOutcome.DidNotReport "the only result is from an earlier run, not this one"
    | None, _ -> LineOutcome.DidNotReport "no result was reported for it"

  let private countsOf (lines: ReceiptLine list) : RunCounts =
    lines
    |> List.fold (fun counts line ->
      match line.Outcome with
      | LineOutcome.Passed _ -> { counts with Passing = counts.Passing + 1 }
      | LineOutcome.Failed _ -> { counts with Failing = counts.Failing + 1 }
      | LineOutcome.Skipped _ -> { counts with Skipping = counts.Skipping + 1 }
      | LineOutcome.DidNotReport _ -> { counts with Unreported = counts.Unreported + 1 }) { Passing = 0; Failing = 0; Skipping = 0; Unreported = 0 }

  /// AllPassed needs tests to have been named and every one to have passed here.
  let verdictOf (counts: RunCounts) : RunVerdict =
    let total = counts.Passing + counts.Failing + counts.Skipping + counts.Unreported
    match counts.Failing, counts.Passing = total && total > 0 with
    | 0, true -> RunVerdict.AllPassed
    | 0, false -> RunVerdict.Incomplete
    | _ -> RunVerdict.SomeFailed

  /// What the engine can say about `requestId`, read from its own durable record.
  let observe (requestId: RunRequestId) (state: LiveTestState) : RunReceipt =
    match Map.tryFind requestId state.RunRequests with
    | None -> RunReceipt.Unattributable requestId
    | Some request ->
      match request.RequestedStatus with
      | RequestedRunStatus.Pending -> RunReceipt.Pending (requestId, request.RequestedTests.Length)
      | RequestedRunStatus.Running -> RunReceipt.Started (requestId, request.RequestedTests.Length)
      | RequestedRunStatus.Completed ->
        let nameOf (id: TestId) =
          state.DiscoveredTests
          |> Array.tryFind (fun tc -> tc.Id = id)
          |> Option.map (fun tc -> tc.FullName)
          |> Option.defaultValue (TestId.value id)
        let lines =
          request.RequestedTests
          |> List.map (fun id ->
            { Id = id
              Name = nameOf id
              Outcome = lineOutcome request.RequestedGeneration state id })
        let counts = countsOf lines
        RunReceipt.Ran
          { RequestId = requestId
            Session = request.RequestedSession
            Generation = request.RequestedGeneration
            Verdict = verdictOf counts
            Counts = counts
            Lines = lines }

  /// Every requested test that did not pass in this run. Fail-closed, and the
  /// same set `RequestedRuns.failingIn` reports for the cohort landing gate.
  let notPassed (receipt: RanReceipt) : TestId list =
    receipt.Lines
    |> List.filter (fun line ->
      match line.Outcome with
      | LineOutcome.Passed _ -> false
      | _ -> true)
    |> List.map (fun line -> line.Id)
