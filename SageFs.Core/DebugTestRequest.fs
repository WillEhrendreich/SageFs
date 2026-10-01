/// "Debug this test", from the editor's side of the daemon.
///
/// The test runs in the process that loaded the user's code, so a debugger has to attach to that process. The daemon's
/// part is small: pick the test, ask the session's worker to hold it (the worker asks the host that runs it), and tell
/// the editor which process to attach to and what it will and will not be able to stop in. The editor attaches, then
/// asks to continue, which releases the test and waits for it to finish.
///
/// Everything here is pure except the two calls to the worker, and those go through the session's `SessionProxy`, so a
/// fake worker is a function.
module SageFs.DebugTestRequest

open System
open System.Threading.Tasks
open SageFs.Features.LiveTesting
open SageFs.HostAgent.TestDebug
open SageFs.WorkerProtocol

// ---- choosing the test ------------------------------------------------------------------------------

/// How the editor names the test it wants to debug.
[<RequireQualifiedAccess>]
type TestSelector =
  /// The test's id, as the daemon reports it. Exact.
  | ById of string
  /// A name the person sees: the full name or display name, whole or in part.
  | ByName of string

[<RequireQualifiedAccess>]
type Resolution =
  | Resolved of TestCase
  | NoMatch
  | Ambiguous of names: string list

/// Pick one test out of what the session has discovered. An id matches at most one test. A name prefers a whole match
/// over a partial one, and refuses to guess when more than one test fits.
let resolve (discovered: TestCase array) (selector: TestSelector) : Resolution =
  let one (tests: TestCase list) =
    match tests with
    | [ test ] -> Some(Resolution.Resolved test)
    | [] -> None
    | many -> Some(Resolution.Ambiguous(many |> List.map (fun t -> t.FullName)))
  match selector with
  | TestSelector.ById id ->
    discovered
    |> Array.filter (fun t -> TestId.value t.Id = id)
    |> Array.toList
    |> one
    |> Option.defaultValue Resolution.NoMatch
  | TestSelector.ByName name ->
    let whole = discovered |> Array.filter (fun t -> t.FullName = name || t.DisplayName = name) |> Array.toList
    match one whole with
    | Some answer -> answer
    | None ->
      discovered
      |> Array.filter (fun t -> t.FullName.Contains name || t.DisplayName.Contains name)
      |> Array.toList
      |> one
      |> Option.defaultValue Resolution.NoMatch

// ---- what the editor is told ------------------------------------------------------------------------

/// Every status the endpoint can answer with. A closed set, with one function that spells it.
[<RequireQualifiedAccess>]
type DebugStatus =
  | Held
  | StillRunning
  | Attached
  | NoDebuggerWithin
  | ReleasedWithoutDebugger
  | NoSuchHold
  | HostLost
  | HoldAlreadyOpen
  | HostUnavailable
  | NotDiscovered
  | NoTestMatched
  | AmbiguousTest
  | NoWorker
  | NoSession
  | WorkerFailed
  | BadRequest

module DebugStatus =
  let wire (status: DebugStatus) : string =
    match status with
    | DebugStatus.Held -> "held"
    | DebugStatus.StillRunning -> "still_running"
    | DebugStatus.Attached -> "attached"
    | DebugStatus.NoDebuggerWithin -> "no_debugger_within"
    | DebugStatus.ReleasedWithoutDebugger -> "released_without_debugger"
    | DebugStatus.NoSuchHold -> "no_such_hold"
    | DebugStatus.HostLost -> "host_lost"
    | DebugStatus.HoldAlreadyOpen -> "hold_already_open"
    | DebugStatus.HostUnavailable -> "host_unavailable"
    | DebugStatus.NotDiscovered -> "not_discovered"
    | DebugStatus.NoTestMatched -> "no_test_matched"
    | DebugStatus.AmbiguousTest -> "ambiguous_test"
    | DebugStatus.NoWorker -> "no_worker"
    | DebugStatus.NoSession -> "no_session"
    | DebugStatus.BadRequest -> "bad_request"
    | DebugStatus.WorkerFailed -> "worker_failed"

/// What a finished test run came to, for the editor.
[<RequireQualifiedAccess>]
type TestOutcome =
  | Passed
  | Failed
  | Skipped
  | NotRun
  | NoResult

module TestOutcome =
  let wire (outcome: TestOutcome) : string =
    match outcome with
    | TestOutcome.Passed -> "passed"
    | TestOutcome.Failed -> "failed"
    | TestOutcome.Skipped -> "skipped"
    | TestOutcome.NotRun -> "not_run"
    | TestOutcome.NoResult -> "no_result"

  /// The outcome, the person-readable detail (the failure message, the skip reason) and the duration in milliseconds.
  let describe (result: TestResult) : TestOutcome * string * float =
    match result with
    | TestResult.Passed duration -> TestOutcome.Passed, "", duration.TotalMilliseconds
    | TestResult.Failed(failure, duration) ->
      let message =
        match failure with
        | TestFailure.AssertionFailed message -> message
        | TestFailure.ExceptionThrown(message, _) -> message
        | TestFailure.TimedOut after -> sprintf "timed out after %.0f seconds" after.TotalSeconds
      TestOutcome.Failed, message, duration.TotalMilliseconds
    | TestResult.Skipped reason -> TestOutcome.Skipped, reason, 0.0
    | TestResult.NotRun -> TestOutcome.NotRun, "", 0.0
    | TestResult.NoResult reason -> TestOutcome.NoResult, NoResultReason.describe reason, 0.0

/// What the daemon answers the editor with. Flat on purpose: an editor reads a field or leaves it, and a field that does
/// not apply to this status is empty or zero. `Message` always says what happened and what to do about it.
type DebugWire =
  { Status: string
    Message: string
    /// The process to attach the debugger to. Zero unless the test is held.
    Pid: int
    /// Hand this back to continue. Empty unless the test is held.
    Ticket: string
    TestId: string
    TestName: string
    /// "compiled" when breakpoints can bind, "eval" when the test's code has no debug symbols. Empty unless held.
    Symbols: string
    SymbolsNote: string
    /// "open" or "blocked": whether the operating system lets a debugger attach. Empty unless held.
    Access: string
    AccessNote: string
    /// How long the host keeps the test held, in milliseconds. Zero unless held.
    HoldMs: int
    /// How the test ended, once it ran under the debugger.
    Outcome: string
    Detail: string
    DurationMs: float }

/// What a wire field that does not apply to this status holds.
let private notApplicable = 0
let private notMeasured = 0.0

let private emptyWire (status: DebugStatus) (message: string) : DebugWire =
  { Status = DebugStatus.wire status
    Message = message
    Pid = notApplicable
    Ticket = ""
    TestId = ""
    TestName = ""
    Symbols = ""
    SymbolsNote = ""
    Access = ""
    AccessNote = ""
    HoldMs = notApplicable
    Outcome = ""
    Detail = ""
    DurationMs = notMeasured }

/// How long, in words.
let describeSpan (span: TimeSpan) : string =
  match span.TotalSeconds < 120.0 with
  | true -> sprintf "%.0f seconds" span.TotalSeconds
  | false -> sprintf "%.0f minutes" span.TotalMinutes

/// What an answer from the worker, or the reason there was none, comes to.
[<RequireQualifiedAccess>]
type DebugAnswer =
  | Begun of DebugBegin
  | Progress of DebugProgress
  | NotDiscovered
  | NoTestMatched of selector: string
  | AmbiguousTest of names: string list
  | NoWorker of sessionId: string
  /// No session could be chosen (none exist, or the choice is ambiguous); `message` says which and what to do.
  | NoSession of message: string
  /// The request did not say what it needs to; `message` says what to add.
  | BadRequest of message: string
  | WorkerFailed of message: string

let private symbolsText (support: SymbolSupport) : string * string =
  match support with
  | SymbolSupport.CompiledWithSymbols -> "compiled", ""
  | SymbolSupport.DefinedByEval ->
    "eval",
    "You defined this test by evaluating code in the session. Evaluated code has no debug symbols, so a breakpoint inside it will not bind. Hard reset the session with a rebuild to debug the compiled copy."

let private accessText (access: AttachAccess) : string * string =
  match access with
  | AttachAccess.Open -> "open", ""
  | AttachAccess.Blocked reason -> "blocked", reason

let private endWire (ended: DebugEnd) : DebugWire =
  match ended with
  | DebugEnd.Attached result ->
    let outcome, detail, durationMs = TestOutcome.describe result
    { emptyWire DebugStatus.Attached "The test ran under the debugger and finished." with
        Outcome = TestOutcome.wire outcome
        Detail = detail
        DurationMs = durationMs }
  | DebugEnd.NoDebuggerWithin bound ->
    emptyWire
      DebugStatus.NoDebuggerWithin
      (sprintf "No debugger released the test within %s, so it did not run. Start debugging again." (describeSpan bound))
  | DebugEnd.ReleasedWithoutDebugger ->
    emptyWire
      DebugStatus.ReleasedWithoutDebugger
      "The test was released but no debugger was attached to the test host, so it did not run. Check the debug console for why the attach failed, then start debugging again."
  | DebugEnd.NoSuchHold ->
    emptyWire DebugStatus.NoSuchHold "The test host holds no test under that ticket. It restarted, or the hold ran out. Start debugging again."
  | DebugEnd.HostLost reason ->
    emptyWire DebugStatus.HostLost (sprintf "The test host ended while it held the test: %s. Start debugging again once the session is Ready." reason)

/// The wire form of an answer, and the HTTP status to send it with.
let toWire (answer: DebugAnswer) : int * DebugWire =
  match answer with
  | DebugAnswer.Begun(DebugBegin.Held target) ->
    let symbols, symbolsNote = symbolsText target.Symbols
    let access, accessNote = accessText target.Access
    200,
    { emptyWire
        DebugStatus.Held
        (sprintf "Attach a .NET debugger to process %d, then continue to release the test." target.Pid) with
        Pid = target.Pid
        Ticket = (let (DebugTicket ticket) = target.Ticket in ticket)
        TestId = TestId.value target.TestId
        TestName = target.TestName
        Symbols = symbols
        SymbolsNote = symbolsNote
        Access = access
        AccessNote = accessNote
        HoldMs = int target.HoldFor.TotalMilliseconds }
  | DebugAnswer.Begun(DebugBegin.Unsupported(UnsupportedReason.HoldAlreadyOpen(DebugTicket ticket))) ->
    409,
    emptyWire
      DebugStatus.HoldAlreadyOpen
      (sprintf "Another test is already held for a debugger (ticket %s). The host holds one at a time, so finish that one or wait for it to time out." ticket)
  | DebugAnswer.Begun(DebugBegin.Unsupported(UnsupportedReason.HostUnavailable reason)) ->
    503, emptyWire DebugStatus.HostUnavailable (sprintf "The session's test host is not available: %s" reason)
  | DebugAnswer.Progress DebugProgress.StillRunning ->
    200, emptyWire DebugStatus.StillRunning "The test is still running under the debugger."
  | DebugAnswer.Progress(DebugProgress.Ended ended) ->
    let wire = endWire ended
    let code =
      match ended with
      | DebugEnd.Attached _ -> 200
      | DebugEnd.NoDebuggerWithin _
      | DebugEnd.ReleasedWithoutDebugger
      | DebugEnd.NoSuchHold -> 409
      | DebugEnd.HostLost _ -> 503
    code, wire
  | DebugAnswer.NotDiscovered ->
    409,
    emptyWire
      DebugStatus.NotDiscovered
      "No tests have been discovered in this session yet. Enable live testing and wait for discovery to finish."
  | DebugAnswer.NoTestMatched selector ->
    404, emptyWire DebugStatus.NoTestMatched (sprintf "No discovered test matches '%s'." selector)
  | DebugAnswer.AmbiguousTest names ->
    let shown = names |> List.truncate 5 |> String.concat ", "
    409,
    emptyWire
      DebugStatus.AmbiguousTest
      (sprintf "%d tests match. Pass the test id to pick one. Matches: %s" names.Length shown)
  | DebugAnswer.NoWorker sessionId ->
    404, emptyWire DebugStatus.NoWorker (sprintf "Session %s has no running worker, so there is no test host to debug in." sessionId)
  | DebugAnswer.NoSession message -> 404, emptyWire DebugStatus.NoSession message
  | DebugAnswer.BadRequest message -> 400, emptyWire DebugStatus.BadRequest message
  | DebugAnswer.WorkerFailed message ->
    502, emptyWire DebugStatus.WorkerFailed (sprintf "The worker could not be reached: %s" message)

// ---- asking the worker ------------------------------------------------------------------------------

let private newReplyId () = Guid.NewGuid().ToString("N").Substring(0, 8)

/// Send one message to the session's worker and read the JSON payload of its `DebugTestAnswer`.
let private askWorker
  (ops: SessionManagementOps)
  (sessionId: SessionId)
  (message: WorkerMessage)
  (read: string -> DebugAnswer)
  : Task<DebugAnswer> =
  task {
    let sid = SessionId.value sessionId
    match! ops.GetProxy sessionId with
    | None -> return DebugAnswer.NoWorker sid
    | Some proxy ->
      try
        match! proxy message |> Async.StartAsTask with
        | WorkerResponse.DebugTestAnswer(_, payload) -> return read payload
        | WorkerResponse.WorkerError error -> return DebugAnswer.WorkerFailed(SageFsError.describe error)
        | other -> return DebugAnswer.WorkerFailed(sprintf "unexpected reply %A" other)
      with ex ->
        return DebugAnswer.WorkerFailed ex.Message
  }

/// Ask the session's worker to hold `test` for a debugger.
let beginDebug (ops: SessionManagementOps) (sessionId: SessionId) (test: TestCase) : Task<DebugAnswer> =
  askWorker ops sessionId (WorkerMessage.DebugTestBegin(test, newReplyId ())) (fun payload ->
    match Serialization.tryDeserialize<DebugBegin> payload with
    | Result.Ok answer -> DebugAnswer.Begun answer
    | Result.Error error -> DebugAnswer.WorkerFailed(SageFsError.describe error))

/// Release the held test (the editor's debugger is attached) and wait for it to finish, up to `park`.
let continueDebug (ops: SessionManagementOps) (sessionId: SessionId) (ticket: string) (park: TimeSpan) : Task<DebugAnswer> =
  askWorker ops sessionId (WorkerMessage.DebugTestContinue(ticket, park, newReplyId ())) (fun payload ->
    match Serialization.tryDeserialize<DebugProgress> payload with
    | Result.Ok progress -> DebugAnswer.Progress progress
    | Result.Error error -> DebugAnswer.WorkerFailed(SageFsError.describe error))
