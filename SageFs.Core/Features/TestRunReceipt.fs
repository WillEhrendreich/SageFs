/// The receipt `run_tests` returns. Its types live in this module, not at namespace level,
/// because `Pending`, `Failed` and `Skipped` are also cases of other unions in SageFs.Features
/// and a namespace-level `RunReceipt.Pending` makes every unqualified use of those ambiguous.
module SageFs.Features.RunReceipts

open System
open SageFs
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

/// What `run_tests` decided before dispatching anything.
[<RequireQualifiedAccess>]
type RunPlan =
  | Refuse of RunRefusal
  | Run of TestCase array

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
  /// Every requested test passed, but files the build was made from changed on disk after the build the tests ran
  /// against. What passed is not what the files say. Never to be read as green.
  | PassedOnStaleSource
  /// Every requested test passed while a rebuild was in progress, so the run says nothing about the edits the rebuild
  /// is picking up. Never to be read as green.
  | PassedWhileRebuilding
  /// Every requested test passed, but nothing could say whether the build is current (see `source` for why).
  /// Never to be read as green.
  | PassedOnUnknownSource

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
    Lines: ReceiptLine list
    /// Whether the build these tests ran against is behind the files on disk, as of this run. `observe` starts it at
    /// `Unknown NotAssessed`; `withSource` puts the real reading in and the verdict follows it.
    Source: SourceState }

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
    | RunVerdict.PassedOnStaleSource -> "PassedOnStaleSource"
    | RunVerdict.PassedWhileRebuilding -> "PassedWhileRebuilding"
    | RunVerdict.PassedOnUnknownSource -> "PassedOnUnknownSource"

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
            Lines = lines
            Source = SourceState.Unknown UnknownReason.NotAssessed }

  /// The receipt with the source reading a run was made against. Skeleton: it changes nothing yet.
  let withSource (source: SourceState) (receipt: RunReceipt) : RunReceipt = receipt

  /// Every requested test that did not pass in this run. Fail-closed, and the
  /// same set `RequestedRuns.failingIn` reports for the cohort landing gate.
  let notPassed (receipt: RanReceipt) : TestId list =
    receipt.Lines
    |> List.filter (fun line ->
      match line.Outcome with
      | LineOutcome.Passed _ -> false
      | _ -> true)
    |> List.map (fun line -> line.Id)

  /// The decision made before anything is dispatched. Trust first (a session that
  /// cannot be believed never runs anything), then whether there is anything to run.
  /// `discovered` is the session's own test list, `matched` what the caller's filters
  /// left of it, `filters` how to name those filters in a refusal.
  let plan (trust: SessionTrust) (discovered: TestCase array) (matched: TestCase array) (filters: string) : RunPlan =
    match SessionTrust.isTrusted trust, discovered.Length, matched.Length with
    | false, _, _ -> RunPlan.Refuse (RunRefusal.SessionNotTrusted trust)
    | true, 0, _ -> RunPlan.Refuse RunRefusal.NothingDiscovered
    | true, _, 0 -> RunPlan.Refuse (RunRefusal.NoTestMatched filters)
    | true, _, _ -> RunPlan.Run matched

  let private statusToken (receipt: RunReceipt) : string =
    match receipt with
    | RunReceipt.Refused _ -> "Refused"
    | RunReceipt.Pending _ -> "Pending"
    | RunReceipt.Started _ -> "Started"
    | RunReceipt.Ran _ -> "Ran"
    | RunReceipt.Unattributable _ -> "Unattributable"

  let private refusalToken (refusal: RunRefusal) : string =
    match refusal with
    | RunRefusal.SessionNotTrusted _ -> "SessionNotTrusted"
    | RunRefusal.NothingDiscovered -> "NothingDiscovered"
    | RunRefusal.NoTestMatched _ -> "NoTestMatched"
    | RunRefusal.NotAttributed _ -> "NotAttributed"
    | RunRefusal.NotDiscovered -> "NotDiscovered"

  let private lineDetail (outcome: LineOutcome) : string =
    match outcome with
    | LineOutcome.Passed duration -> sprintf "passed in %.0fms" duration.TotalMilliseconds
    | LineOutcome.Failed reason -> sprintf "failed: %s" reason
    | LineOutcome.Skipped reason -> sprintf "skipped: %s" reason
    | LineOutcome.DidNotReport reason -> sprintf "did not report: %s" reason

  let private waitAdvice (requestId: RunRequestId) : string =
    sprintf "Ask again with run_tests receipt_id=%s (and wait_seconds) to see the result." ((RunRequestId.value requestId).ToString())

  /// Plain-language receipt for the text block of the tool result.
  let summarize (receipt: RunReceipt) : string =
    match receipt with
    | RunReceipt.Refused refusal -> RunRefusal.describe refusal
    | RunReceipt.Pending (requestId, requested) ->
      sprintf "Queued %d test(s); the worker has not started them. %s" requested (waitAdvice requestId)
    | RunReceipt.Started (requestId, requested) ->
      sprintf "Running %d test(s). %s" requested (waitAdvice requestId)
    | RunReceipt.Unattributable requestId ->
      sprintf "Request %s cannot be attributed: it was never made, or it aged out of the engine's bounded record. Run run_tests again." ((RunRequestId.value requestId).ToString())
    | RunReceipt.Ran ran ->
      let c = ran.Counts
      let head =
        sprintf "%d passed, %d failed, %d skipped, %d did not report (session %s, run %d)."
          c.Passing c.Failing c.Skipping c.Unreported ran.Session (RunGeneration.value ran.Generation)
      let verdict =
        match ran.Verdict with
        | RunVerdict.AllPassed -> "Every requested test passed in this run."
        | RunVerdict.SomeFailed -> "Some tests failed:"
        | RunVerdict.Incomplete -> "This is not green: not every test passed in this run."
        | RunVerdict.PassedOnStaleSource
        | RunVerdict.PassedWhileRebuilding
        | RunVerdict.PassedOnUnknownSource -> ""
      let problems =
        ran.Lines
        |> List.filter (fun line -> match line.Outcome with LineOutcome.Passed _ -> false | _ -> true)
        |> List.map (fun line -> sprintf "  %s: %s" line.Name (lineDetail line.Outcome))
      String.concat "\n" (head :: verdict :: problems)

  /// The receipt as data, with a stable token for every case so an agent branches on
  /// the token and never parses the text.
  let toJson (receipt: RunReceipt) : System.Text.Json.Nodes.JsonObject =
    let node = System.Text.Json.Nodes.JsonObject()
    node["status"] <- System.Text.Json.Nodes.JsonValue.Create(statusToken receipt)
    node["message"] <- System.Text.Json.Nodes.JsonValue.Create(summarize receipt)
    match receipt with
    | RunReceipt.Refused refusal ->
      node["reason"] <- System.Text.Json.Nodes.JsonValue.Create(refusalToken refusal)
    | RunReceipt.Pending (requestId, requested)
    | RunReceipt.Started (requestId, requested) ->
      node["receiptId"] <- System.Text.Json.Nodes.JsonValue.Create((RunRequestId.value requestId).ToString())
      node["requested"] <- System.Text.Json.Nodes.JsonValue.Create(requested)
    | RunReceipt.Unattributable requestId ->
      node["receiptId"] <- System.Text.Json.Nodes.JsonValue.Create((RunRequestId.value requestId).ToString())
    | RunReceipt.Ran ran ->
      node["receiptId"] <- System.Text.Json.Nodes.JsonValue.Create((RunRequestId.value ran.RequestId).ToString())
      node["session"] <- System.Text.Json.Nodes.JsonValue.Create(ran.Session)
      node["generation"] <- System.Text.Json.Nodes.JsonValue.Create(RunGeneration.value ran.Generation)
      node["verdict"] <- System.Text.Json.Nodes.JsonValue.Create(RunVerdict.token ran.Verdict)
      let counts = System.Text.Json.Nodes.JsonObject()
      counts["passing"] <- System.Text.Json.Nodes.JsonValue.Create(ran.Counts.Passing)
      counts["failing"] <- System.Text.Json.Nodes.JsonValue.Create(ran.Counts.Failing)
      counts["skipping"] <- System.Text.Json.Nodes.JsonValue.Create(ran.Counts.Skipping)
      counts["unreported"] <- System.Text.Json.Nodes.JsonValue.Create(ran.Counts.Unreported)
      node["counts"] <- counts
      let lines = System.Text.Json.Nodes.JsonArray()
      for line in ran.Lines do
        let item = System.Text.Json.Nodes.JsonObject()
        item["id"] <- System.Text.Json.Nodes.JsonValue.Create(TestId.value line.Id)
        item["name"] <- System.Text.Json.Nodes.JsonValue.Create(line.Name)
        item["outcome"] <- System.Text.Json.Nodes.JsonValue.Create(LineOutcome.token line.Outcome)
        item["detail"] <- System.Text.Json.Nodes.JsonValue.Create(lineDetail line.Outcome)
        lines.Add item
      node["lines"] <- lines
    node
