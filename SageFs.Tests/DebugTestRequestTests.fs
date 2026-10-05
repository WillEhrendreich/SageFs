module SageFs.Tests.DebugTestRequestTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.DebugTestRequest
open SageFs.Features.LiveTesting
open SageFs.HostAgent.TestDebug
open SageFs.WorkerProtocol

/// The daemon's side of debugging one test: choosing the test, saying what the editor is told for every outcome, and asking
/// the worker.

let private makeTest (name: string) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private sessionId =
  match SessionId.validate "0a0b0c0d" with
  | Ok id -> id
  | Error e -> failwithf "bad fixture session id: %s" e

let private target : DebugTarget =
  { Pid = 4242
    Ticket = DebugTicket "debug-4242-1"
    TestId = (makeTest "suite/adds").Id
    TestName = "suite/adds"
    Symbols = SymbolSupport.CompiledWithSymbols
    Access = AttachAccess.Open
    HoldFor = TestTimeouts.patienceInProcess }

let private passed = TestResult.Passed FixtureDurations.usualResult

/// A session whose worker is the given function.
let private opsWith (proxy: SessionProxy) : SessionManagementOps =
  { SessionManagementOps.stub with GetProxy = fun _ -> Task.FromResult(Some proxy) }

/// A worker that answers every debug message with this payload.
let private workerAnswering (payload: string) : SessionProxy =
  fun _ -> async { return WorkerResponse.DebugTestAnswer("r", payload) }

/// Every case of a fieldless-or-not union, with fields filled by the given sample, so a new case joins the check by itself.
let private allDebugStatuses : DebugStatus list =
  FSharpType.GetUnionCases typeof<DebugStatus>
  |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> DebugStatus)
  |> Array.toList

/// One answer of every shape the endpoint can give.
let private everyAnswer : DebugAnswer list =
  [ DebugAnswer.Begun(DebugBegin.Held target)
    DebugAnswer.Begun(DebugBegin.Held { target with Symbols = SymbolSupport.DefinedByEval; Access = AttachAccess.Blocked "ptrace_scope is 2" })
    DebugAnswer.Begun(DebugBegin.Unsupported(UnsupportedReason.HoldAlreadyOpen target.Ticket))
    DebugAnswer.Begun(DebugBegin.Unsupported(UnsupportedReason.HostUnavailable "the host ended"))
    DebugAnswer.Progress DebugProgress.StillRunning
    DebugAnswer.Progress(DebugProgress.Ended(DebugEnd.Attached passed))
    DebugAnswer.Progress(DebugProgress.Ended(DebugEnd.NoDebuggerWithin TestTimeouts.patienceInProcess))
    DebugAnswer.Progress(DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger)
    DebugAnswer.Progress(DebugProgress.Ended DebugEnd.NoSuchHold)
    DebugAnswer.Progress(DebugProgress.Ended(DebugEnd.HostLost "the host ended"))
    DebugAnswer.NotDiscovered
    DebugAnswer.NoTestMatched "adds"
    DebugAnswer.AmbiguousTest [ "suite/adds"; "suite/adds more" ]
    DebugAnswer.NoWorker "0a0b0c0d"
    DebugAnswer.NoSession "no session"
    DebugAnswer.BadRequest "say which test"
    DebugAnswer.WorkerFailed "refused" ]

let private config = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private genName : Gen<string> =
  Gen.elements [ "a"; "ab"; "abc"; "suite/a"; "suite/ab"; "other/abc"; "x" ]

let private genTests : Gen<TestCase array> =
  gen {
    let! names = Gen.listOf genName
    return names |> List.distinct |> List.map makeTest |> List.toArray
  }

[<Tests>]
let tests =
  testList "DebugTestRequest" [

    testList "resolve (choosing the test)" [

      testPropertyWithConfig config "WHY: an id picks exactly the test that has it, whatever else is discovered"
      <| Prop.forAll (Arb.fromGen (Gen.zip genTests genName)) (fun (tests, name) ->
        let wanted = makeTest name
        match resolve tests (TestSelector.ById(TestId.value wanted.Id)), tests |> Array.exists (fun t -> t.Id = wanted.Id) with
        | Resolution.Resolved found, true -> found.Id = wanted.Id
        | Resolution.NoMatch, false -> true
        | _ -> false)

      testPropertyWithConfig config "WHY: a name never guesses: it resolves only when one test fits, and says every fit when several do"
      <| Prop.forAll (Arb.fromGen (Gen.zip genTests genName)) (fun (tests, name) ->
        let exact = tests |> Array.filter (fun t -> t.FullName = name)
        let partial = tests |> Array.filter (fun t -> t.FullName.Contains name)
        let expectedFits = match exact.Length with 0 -> partial | _ -> exact
        match resolve tests (TestSelector.ByName name), expectedFits.Length with
        | Resolution.NoMatch, 0 -> true
        | Resolution.Resolved found, 1 -> found.Id = expectedFits.[0].Id
        | Resolution.Ambiguous names, n when n > 1 -> List.sort names = (expectedFits |> Array.map (fun t -> t.FullName) |> Array.toList |> List.sort)
        | _ -> false)

      testCase "WHY: a whole name wins over a partial one, so debugging 'suite/a' does not complain that 'suite/ab' also matches" <| fun _ ->
        let tests = [| makeTest "suite/a"; makeTest "suite/ab" |]
        match resolve tests (TestSelector.ByName "suite/a") with
        | Resolution.Resolved found -> found.FullName |> Expect.equal "the whole match" "suite/a"
        | other -> failtestf "expected Resolved, got %A" other

      testCase "WHY: nothing discovered means nothing matches" <| fun _ ->
        resolve [||] (TestSelector.ByName "anything") |> Expect.equal "no match" Resolution.NoMatch
    ]

    testList "toWire (what the editor is told)" [

      testCase "WHY: every status has one spelling, snake case, and no two statuses share it" <| fun _ ->
        let spellings = allDebugStatuses |> List.map DebugStatus.wire
        spellings |> List.distinct |> List.length |> Expect.equal "all distinct" spellings.Length
        for spelling in spellings do
          spelling |> Expect.isMatch "snake case" "^[a-z]+(_[a-z]+)*$"

      testCase "WHY: every answer yields a status the registry knows, a message that says something, and no em dash" <| fun _ ->
        let known = allDebugStatuses |> List.map DebugStatus.wire |> Set.ofList
        for answer in everyAnswer do
          let code, wire = toWire answer
          known |> Expect.contains (sprintf "%A has a known status" answer) wire.Status
          wire.Message |> Expect.isNotEmpty (sprintf "%A says something" answer)
          wire.Message.Contains "—" |> Expect.isFalse "no em dash"
          (code >= 200 && code < 600) |> Expect.isTrue "an HTTP status"

      testCase "WHY: every status is produced by some answer, so none is dead" <| fun _ ->
        let produced = everyAnswer |> List.map (toWire >> snd >> fun wire -> wire.Status) |> Set.ofList
        let all = allDebugStatuses |> List.map DebugStatus.wire |> Set.ofList
        Set.difference all produced |> Set.toList |> Expect.isEmpty "every status is reachable"

      testCase "WHY: a held test tells the editor the pid, the ticket, the test, and how long it has" <| fun _ ->
        let code, wire = toWire (DebugAnswer.Begun(DebugBegin.Held target))
        code |> Expect.equal "ok" 200
        wire.Status |> Expect.equal "status" "held"
        wire.Pid |> Expect.equal "pid" 4242
        wire.Ticket |> Expect.equal "ticket" "debug-4242-1"
        wire.TestId |> Expect.equal "test id" (TestId.value target.TestId)
        wire.TestName |> Expect.equal "test name" "suite/adds"
        wire.Symbols |> Expect.equal "compiled symbols" "compiled"
        wire.SymbolsNote |> Expect.equal "nothing to warn about" ""
        wire.Access |> Expect.equal "open" "open"
        wire.HoldMs |> Expect.equal "hold" (int target.HoldFor.TotalMilliseconds)

      testCase "WHY: a test an eval defined says plainly that breakpoints will not bind, and what to do" <| fun _ ->
        let _, wire = toWire (DebugAnswer.Begun(DebugBegin.Held { target with Symbols = SymbolSupport.DefinedByEval }))
        wire.Symbols |> Expect.equal "eval" "eval"
        wire.SymbolsNote |> Expect.stringContains "says no symbols" "no debug symbols"
        wire.SymbolsNote |> Expect.stringContains "says what to do" "rebuild"

      testCase "WHY: a blocked attach carries the reason the host gave" <| fun _ ->
        let _, wire = toWire (DebugAnswer.Begun(DebugBegin.Held { target with Access = AttachAccess.Blocked "ptrace_scope is 2" }))
        wire.Access |> Expect.equal "blocked" "blocked"
        wire.AccessNote |> Expect.equal "the reason" "ptrace_scope is 2"

      testCase "WHY: a finished run carries how it ended, and a failure carries its message" <| fun _ ->
        let failed = TestResult.Failed(TestFailure.AssertionFailed "expected 3 but got 4", FixtureDurations.usualResult)
        let _, wire = toWire (DebugAnswer.Progress(DebugProgress.Ended(DebugEnd.Attached failed)))
        wire.Status |> Expect.equal "attached" "attached"
        wire.Outcome |> Expect.equal "failed" "failed"
        wire.Detail |> Expect.equal "the assertion" "expected 3 but got 4"

      testCase "WHY: the outcome spellings are one set, and every TestResult maps to one of them" <| fun _ ->
        let results =
          [ passed
            TestResult.Failed(TestFailure.TimedOut TestTimeouts.patienceBrief, FixtureDurations.usualResult)
            TestResult.Skipped "why"
            TestResult.NotRun
            TestResult.NoResult NoResultReason.RunCancelled ]
        let spellings = results |> List.map (TestOutcome.describe >> (fun (outcome, _, _) -> TestOutcome.wire outcome))
        spellings |> List.distinct |> List.length |> Expect.equal "five results, five spellings" 5

      testCase "WHY: a host lost with a reason that already ends in a full stop is told in sentences with no doubled full stop" <| fun _ ->
        let crashed = HostCrash.describe { Exit = ExitedWith 134; Output = "" }
        for reason in [ crashed; "the host ended"; "the host ended."; "Last output from the host:\nboom." ] do
          let _, wire = toWire (DebugAnswer.Progress(DebugProgress.Ended(DebugEnd.HostLost reason)))
          wire.Message.Contains ".." |> Expect.isFalse (sprintf "no doubled full stop for '%s': %s" reason wire.Message)
          wire.Message |> Expect.stringContains "still says what to do" "Start debugging again"

      testCase "WHY: the HTTP status separates what the caller can fix from what is down" <| fun _ ->
        let code (answer: DebugAnswer) = toWire answer |> fst
        code (DebugAnswer.BadRequest "x") |> Expect.equal "bad request" 400
        code (DebugAnswer.NoTestMatched "x") |> Expect.equal "not found" 404
        code (DebugAnswer.AmbiguousTest [ "a"; "b" ]) |> Expect.equal "conflict" 409
        code (DebugAnswer.Progress(DebugProgress.Ended(DebugEnd.HostLost "x"))) |> Expect.equal "unavailable" 503
        code (DebugAnswer.Progress DebugProgress.StillRunning) |> Expect.equal "still going is fine" 200

      testPropertyWithConfig config "WHY: a duration reads in words, seconds under two minutes and minutes after"
      <| fun (seconds: byte) ->
        let span = TimeSpan.FromSeconds(float seconds)
        let text = describeSpan span
        match seconds < 120uy with
        | true -> text.EndsWith "seconds"
        | false -> text.EndsWith "minutes"
    ]

    testList "asking the worker" [

      testTask "WHY: begin hands the worker the test and reads its answer back as the same value" {
        let seen = ref None
        let proxy : SessionProxy =
          fun message ->
            async {
              seen.Value <- Some message
              return WorkerResponse.DebugTestAnswer("r", WorkerProtocol.Serialization.serialize (DebugBegin.Held target))
            }
        let test = makeTest "suite/adds"
        let! answer = beginDebug (opsWith proxy) sessionId test
        answer |> Expect.equal "the worker's answer" (DebugAnswer.Begun(DebugBegin.Held target))
        match seen.Value with
        | Some(WorkerMessage.DebugTestBegin(sent, _)) -> sent |> Expect.equal "the test it was asked to hold" test
        | other -> failtestf "expected DebugTestBegin, got %A" other
      }

      testTask "WHY: continue carries the ticket and the park bound, and reads the progress back" {
        let seen = ref None
        let proxy : SessionProxy =
          fun message ->
            async {
              seen.Value <- Some message
              return WorkerResponse.DebugTestAnswer("r", WorkerProtocol.Serialization.serialize (DebugProgress.Ended(DebugEnd.Attached passed)))
            }
        let! answer = continueDebug (opsWith proxy) sessionId "debug-4242-1" TestTimeouts.patienceBrief
        answer |> Expect.equal "the progress" (DebugAnswer.Progress(DebugProgress.Ended(DebugEnd.Attached passed)))
        match seen.Value with
        | Some(WorkerMessage.DebugTestContinue(ticket, park, _)) ->
          ticket |> Expect.equal "the ticket" "debug-4242-1"
          park |> Expect.equal "the park bound" TestTimeouts.patienceBrief
        | other -> failtestf "expected DebugTestContinue, got %A" other
      }

      testTask "WHY: a session with no worker says so, rather than failing mysteriously" {
        let! answer = beginDebug SessionManagementOps.stub sessionId (makeTest "t")
        answer |> Expect.equal "no worker" (DebugAnswer.NoWorker(SessionId.value sessionId))
      }

      testTask "WHY: a worker that cannot be reached is a WorkerFailed with the reason" {
        let proxy : SessionProxy = fun _ -> async { return failwith "connection refused" }
        let! answer = beginDebug (opsWith proxy) sessionId (makeTest "t")
        match answer with
        | DebugAnswer.WorkerFailed message -> message |> Expect.stringContains "carries the reason" "connection refused"
        | other -> failtestf "expected WorkerFailed, got %A" other
      }

      testTask "WHY: a payload that is not the answer asked for is a WorkerFailed, never a guess" {
        let! answer = beginDebug (opsWith (workerAnswering "not json")) sessionId (makeTest "t")
        match answer with
        | DebugAnswer.WorkerFailed _ -> ()
        | other -> failtestf "expected WorkerFailed, got %A" other
      }

      testTask "WHY: a reply of the wrong kind is a WorkerFailed" {
        let proxy : SessionProxy = fun _ -> async { return WorkerResponse.WorkerShuttingDown }
        let! answer = continueDebug (opsWith proxy) sessionId "t" TestTimeouts.patienceBrief
        match answer with
        | DebugAnswer.WorkerFailed _ -> ()
        | other -> failtestf "expected WorkerFailed, got %A" other
      }
    ]

    testList "the worker wire" [

      testCase "WHY: begin and continue go to their own routes with the fields the worker reads" <| fun _ ->
        let test = makeTest "t"
        let method, path, body = HttpWorkerClient.toRoute (WorkerMessage.DebugTestBegin(test, "r1"))
        method |> Expect.equal "post" "POST"
        path |> Expect.equal "route" "/debug-test"
        body |> Option.defaultValue "" |> Expect.stringContains "carries the test and the reply id" "\"replyId\""
        let _, continuePath, continueBody = HttpWorkerClient.toRoute (WorkerMessage.DebugTestContinue("tk", TestTimeouts.patienceBrief, "r2"))
        continuePath |> Expect.equal "route" "/debug-test-continue"
        continueBody |> Option.defaultValue "" |> Expect.stringContains "carries the ticket" "\"ticket\""

      testCase "WHY: starting a debug hold counts as using the session, and continuing one does not" <| fun _ ->
        WorkerMessage.isActivity (WorkerMessage.DebugTestBegin(makeTest "t", "r")) |> Expect.isTrue "begin is activity"
        WorkerMessage.isActivity (WorkerMessage.DebugTestContinue("tk", TestTimeouts.patienceBrief, "r")) |> Expect.isFalse "a poll is not"

      testCase "WHY: the worker's answer survives the wire as the host's own types" <| fun _ ->
        let response = WorkerResponse.DebugTestAnswer("r", WorkerProtocol.Serialization.serialize (DebugBegin.Held target))
        match WorkerProtocol.Serialization.tryDeserialize<WorkerResponse> (WorkerProtocol.Serialization.serialize response) with
        | Result.Ok(WorkerResponse.DebugTestAnswer(_, payload)) ->
          WorkerProtocol.Serialization.tryDeserialize<DebugBegin> payload
          |> Expect.equal "the host's answer" (Result.Ok(DebugBegin.Held target))
        | other -> failtestf "expected DebugTestAnswer, got %A" other
    ]
  ]
