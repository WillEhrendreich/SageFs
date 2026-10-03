/// `run_tests` through the tool layer, against a fake engine that runs the REAL
/// `SageFsUpdate.update` (so the run lifecycle, request records and result stamping
/// are the daemon's own) and scripts only the worker's answers. No daemon, no
/// sleeps: the worker's answers are released by the test, and every wait is bounded.
module SageFs.Tests.McpRunTestsTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.RunReceipts
open SageFs.McpRunTests
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.WorkerProtocol
open SageFs.Tests.LiveTestingTestHelpers


let private framework = TestFramework.Expecto
let private caseOf name = mkTestCase name framework TestCategory.Unit
let private idOf name = TestId.create name framework

/// What the scripted worker does for a test: pass it, fail it, or say nothing.
type private Answer =
  | Pass
  | Fail
  | Silence

/// The daemon's Elm loop, minus the daemon: the real update, a scripted worker.
type private Engine(sid: string, cases: TestCase list, answers: Map<TestId, Answer>) =
  let gate = obj ()
  let changed = Event<string>()
  let pending = Queue<SageFsMsg>()
  let runDispatched = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  let dispatched = List<SageFsMsg>()
  let mutable model =
    let initial = SageFsModel.initial ()
    fst (SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sid, Array.ofList cases))) initial)

  let apply (m: SageFsMsg) =
    lock gate (fun () ->
      let model', effects = SageFsUpdate.update m model
      model <- model'
      for effect in effects do
        match effect with
        | SageFsEffect.TestCycle (TestCycleEffect.RunRequestedTests (req, generation)) when not (Array.isEmpty req.Tests) ->
          let ids = req.Tests |> Array.map (fun tc -> tc.Id)
          // The performer dispatches the start before the run executes.
          let started = SageFsMsg.Event (TuiEvent.TestRunStartedAt (ids, sid, generation))
          let m2, _ = SageFsUpdate.update started model
          model <- m2
          let results =
            req.Tests
            |> Array.choose (fun tc ->
              match Map.tryFind tc.Id answers |> Option.defaultValue Pass with
              | Pass -> Some (mkResult tc.Id (TestResult.Passed (ts 3.0)))
              | Fail -> Some (mkResult tc.Id (TestResult.Failed (TestFailure.AssertionFailed "boom", ts 3.0)))
              | Silence -> None)
          pending.Enqueue (SageFsMsg.Event (TuiEvent.TestResultsBatch (Some sid, results)))
          pending.Enqueue (SageFsMsg.Event (TuiEvent.TestRunCompleted (Some sid)))
          runDispatched.TrySetResult () |> ignore
        | _ -> ())
    changed.Trigger ""

  member _.Model () = lock gate (fun () -> model)
  member _.Changed = changed.Publish
  /// Completes once the engine has been asked to run tests.
  member _.RunDispatched : Task = runDispatched.Task
  member _.Dispatched = lock gate (fun () -> List.ofSeq dispatched)
  member _.Dispatch (m: SageFsMsg) =
    lock gate (fun () -> dispatched.Add m)
    apply m
  /// The worker answers everything it has been asked so far.
  member this.ReleaseWorker () =
    let rec drain () =
      let next = lock gate (fun () -> match pending.Count with 0 -> None | _ -> Some (pending.Dequeue()))
      match next with
      | Some m ->
        apply m
        drain ()
      | None -> ()
    drain ()

let private runRequests (engine: Engine) =
  engine.Dispatched
  |> List.choose (function
    | SageFsMsg.Event (TuiEvent.RunTestsRequested (session, tests, Some rid)) -> Some (session, tests, rid)
    | _ -> None)

let private proxy : SessionProxy = fun _ -> async { return failwith "the run_tests path never calls the worker proxy" }

let rec private ctxFor (engine: Engine) (sid: string) (status: SessionLifecycleStatus) : McpContext =
  ctxForFreshness engine sid status ReplFreshness.InSync

and private ctxForFreshness (engine: Engine) (sid: string) (status: SessionLifecycleStatus) (freshness: ReplFreshness) : McpContext =
  let info : SessionInfo =
    { Id = SageFs.McpSessionRouting.toSessionId sid
      Name = None
      Projects = []
      WorkingDirectory = "/run-tests"
      SolutionRoot = None
      Status = status
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet
      Freshness = freshness }
  let ops : SessionManagementOps =
    { SessionManagementOps.stub with
        GetProxy = fun _ -> Task.FromResult (Some proxy)
        GetSessionInfo = fun _ -> Task.FromResult (Some info)
        GetAllSessions = fun () -> Task.FromResult [ info ] }
  { FrictionStore = None
    DiagnosticsChanged = (Event<SageFs.Features.DiagnosticsStore.T>()).Publish
    StateChanged = Some engine.Changed
    SessionOps = ops
    SessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = Some engine.Dispatch
    GetElmModel = Some engine.Model
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

let private ready = SessionLifecycleStatus.Ready { Pid = 42; Port = None }
let private sid = "a1b2c3d4"
let private everything : RunTestsRequest =
  { SessionId = Some sid
    WorkingDirectory = None
    Pattern = None
    File = None
    Category = None
    Continue = None
    Wait = TestTimeouts.runTestsWait }

let private receiptOf (outcome: RunTestsOutcome) : RunReceipt =
  match outcome with
  | RunTestsOutcome.Receipt r -> r
  | other -> failtestf "expected a receipt, got %A" other

let private cases = [ caseOf "Suite.alpha"; caseOf "Suite.beta" ]

// A project on disk the session has loaded, so a receipt can be read against real files (SourceStateFixtures).

/// The session's context with the project on disk loaded, a worker that says when it loaded, the given rebuild record and
/// the given REPL freshness.
let private ctxOverProjectFresh (engine: Engine) (f: SourceStateFixtures.Project) (rebuild: LastRebuild) (freshness: ReplFreshness) : McpContext =
  let baseCtx = ctxForFreshness engine sid ready freshness
  let info : SessionInfo =
    { Id = SageFs.McpSessionRouting.toSessionId sid
      Name = None
      Projects = [ f.ProjectFile ]
      WorkingDirectory = f.Dir
      SolutionRoot = None
      Status = ready
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = [ SourceStateFixtures.classified f ]
      App = AppRun.AppRunState.NotRunning
      Rebuild = rebuild
      Reload = SessionReload.NoReloadYet
      Freshness = freshness }
  { baseCtx with
      SessionOps =
        { baseCtx.SessionOps with
            GetSessionInfo = fun _ -> Task.FromResult (Some info)
            GetAllSessions = fun () -> Task.FromResult [ info ] }
      GetWarmupContext = Some (fun _ -> Task.FromResult (Some (SourceStateFixtures.warmup f))) }

let private ctxOverProject (engine: Engine) (f: SourceStateFixtures.Project) (rebuild: LastRebuild) : McpContext =
  ctxOverProjectFresh engine f rebuild ReplFreshness.InSync

let private withFixture = SourceStateFixtures.using

/// Ceiling on any single wait. A test that reaches it has failed.
let private patience = TestTimeouts.patience

[<Tests>]
let tests =
  testList "run_tests through the tool layer" [

    testTask "WHY — a session that is still warming up refuses without dispatching anything, and says to wait" {
      let engine = Engine(sid, cases, Map.empty)
      let ctx = ctxFor engine sid (SessionLifecycleStatus.Starting { Pid = 42; Port = None })
      let! outcome = runTests ctx "agent" everything
      match receiptOf outcome with
      | RunReceipt.Refused (RunRefusal.SessionNotTrusted (Verification.SessionTrust.WarmingUp _)) -> ()
      | other -> failtestf "expected a trust refusal, got %A" other
      runRequests engine |> List.length |> Expect.equal "nothing was dispatched" 0
    }

    testTask "WHY — a session with no discovered tests says so instead of running nothing" {
      let engine = Engine(sid, [], Map.empty)
      let! outcome = runTests (ctxFor engine sid ready) "agent" everything
      match receiptOf outcome with
      | RunReceipt.Refused RunRefusal.NothingDiscovered -> ()
      | other -> failtestf "expected NothingDiscovered, got %A" other
    }

    testTask "WHY — a filter that matches nothing is a refusal that names the filter, not a green zero-test run" {
      let engine = Engine(sid, cases, Map.empty)
      let! outcome = runTests (ctxFor engine sid ready) "agent" { everything with Pattern = Some "zzz-no-such-test" }
      match receiptOf outcome with
      | RunReceipt.Refused (RunRefusal.NoTestMatched filters) -> filters |> Expect.stringContains "names the pattern" "zzz-no-such-test"
      | other -> failtestf "expected NoTestMatched, got %A" other
      runRequests engine |> List.length |> Expect.equal "nothing was dispatched" 0
    }

    testTask "WHY — a run the worker answers comes back as a receipt the engine's own record backs: every test passed in THIS run" {
      do! withFixture (fun f -> task {
        let engine = Engine(sid, cases, Map.empty)
        let running = runTests (ctxOverProject engine f LastRebuild.NeverRebuilt) "agent" everything
        do! engine.RunDispatched.WaitAsync patience
        engine.ReleaseWorker ()
        let! outcome = running
        match receiptOf outcome with
        | RunReceipt.Ran ran ->
          ran.Verdict |> Expect.equal "all passed" RunVerdict.AllPassed
          ran.Lines |> List.length |> Expect.equal "a line per test" 2
        | other -> failtestf "expected Ran, got %A" other
        runRequests engine |> List.length |> Expect.equal "exactly one run was requested" 1 })
    }

    testTask "WHY — a failing test makes the run SomeFailed" {
      let engine = Engine(sid, cases, Map.ofList [ idOf "Suite.beta", Fail ])
      let running = runTests (ctxFor engine sid ready) "agent" everything
      do! engine.RunDispatched.WaitAsync patience
      engine.ReleaseWorker ()
      let! outcome = running
      match receiptOf outcome with
      | RunReceipt.Ran ran -> ran.Verdict |> Expect.equal "some failed" RunVerdict.SomeFailed
      | other -> failtestf "expected Ran, got %A" other
    }

    testTask "WHY — a test the worker never reports makes the run Incomplete, never AllPassed" {
      let engine = Engine(sid, cases, Map.ofList [ idOf "Suite.beta", Silence ])
      let running = runTests (ctxFor engine sid ready) "agent" everything
      do! engine.RunDispatched.WaitAsync patience
      engine.ReleaseWorker ()
      let! outcome = running
      match receiptOf outcome with
      | RunReceipt.Ran ran -> ran.Verdict |> Expect.equal "incomplete" RunVerdict.Incomplete
      | other -> failtestf "expected Ran, got %A" other
    }

    testTask "WHY — a wait that runs out hands back the request id, and asking again with it returns the finished receipt from the same record" {
      do! withFixture (fun f -> task {
        let engine = Engine(sid, cases, Map.empty)
        let ctx = ctxOverProject engine f LastRebuild.NeverRebuilt
        // The worker is silent for the whole (short) wait.
        let! first = runTests ctx "agent" { everything with Wait = TestTimeouts.runTestsSilentWait }
        let rid =
          match receiptOf first with
          | RunReceipt.Pending (rid, _) | RunReceipt.Started (rid, _) -> rid
          | other -> failtestf "expected an in-flight receipt, got %A" other
        // The worker answers, and the same request id now reads as finished.
        engine.ReleaseWorker ()
        let! second = runTests ctx "agent" { everything with Continue = Some rid }
        match receiptOf second with
        | RunReceipt.Ran ran -> ran.Verdict |> Expect.equal "all passed" RunVerdict.AllPassed
        | other -> failtestf "expected Ran, got %A" other
        runRequests engine |> List.length |> Expect.equal "asking again dispatched nothing new" 1 })
    }

    testTask "WHY — a run against a REPL that is behind its app says so on the result an agent reads, because those tests ran the build from before the patch" {
      do! withFixture (fun f -> task {
        let behind = ReplFreshness.BehindApp (1, [ "Handlers.describe" ])
        let engine = Engine(sid, cases, Map.empty)
        let tools = SageFsTools(ctxOverProjectFresh engine f LastRebuild.NeverRebuilt behind, Microsoft.Extensions.Logging.Abstractions.NullLogger<SageFsTools>.Instance)
        let running = tools.run_tests("", "", "", 30, "", sid, "")
        do! engine.RunDispatched.WaitAsync patience
        engine.ReleaseWorker ()
        let! (result: ModelContextProtocol.Protocol.CallToolResult) = running
        let text = result.Content |> Seq.pick (fun c -> match c with :? ModelContextProtocol.Protocol.TextContentBlock as t -> Some t.Text | _ -> None)
        text |> Expect.stringContains "the run is still reported" "Every requested test passed in this run"
        text |> Expect.stringContains "and the REPL is said to be behind" "BEHIND"
        text |> Expect.stringContains "with what to do" "hard_reset_fsi_session"
        result.StructuredContent.Value.GetProperty("replFreshness").GetProperty("state").GetString()
        |> Expect.equal "as a field too" "BehindApp" })
    }

    testTask "WHY — a run against a level REPL carries no warning" {
      let engine = Engine(sid, cases, Map.empty)
      let tools = SageFsTools(ctxFor engine sid ready, Microsoft.Extensions.Logging.Abstractions.NullLogger<SageFsTools>.Instance)
      let running = tools.run_tests("", "", "", 30, "", sid, "")
      do! engine.RunDispatched.WaitAsync patience
      engine.ReleaseWorker ()
      let! (result: ModelContextProtocol.Protocol.CallToolResult) = running
      let text = result.Content |> Seq.pick (fun c -> match c with :? ModelContextProtocol.Protocol.TextContentBlock as t -> Some t.Text | _ -> None)
      text.Contains "BEHIND" |> Expect.isFalse "nothing to warn about"
    }

    testTask "WHY — a request id the engine has no record of claims nothing" {
      let engine = Engine(sid, cases, Map.empty)
      let! outcome = runTests (ctxFor engine sid ready) "agent" { everything with Continue = Some (RunRequestId.fresh ()) }
      match receiptOf outcome with
      | RunReceipt.Unattributable _ -> ()
      | other -> failtestf "expected Unattributable, got %A" other
    }
  ]

[<Tests>]
let categoryTests =
  testList "McpRunTests.parseCategory" [

    testCase "WHY — blank means no category filter, and a known name maps to its case regardless of case" <| fun _ ->
      parseCategory "" |> Expect.isNone "blank is no filter"
      parseCategory "  " |> Expect.isNone "whitespace is no filter"
      parseCategory "UNIT" |> Expect.equal "case-insensitive" (Some TestCategory.Unit)
      parseCategory "integration" |> Expect.equal "integration" (Some TestCategory.Integration)

    testCase "WHY — an unknown name is a custom category, never silently dropped (a dropped filter would run everything)" <| fun _ ->
      parseCategory "smoke" |> Expect.equal "custom" (Some (TestCategory.Custom "smoke"))
  ]

// ── what the receipt says about its source ───────────────────────────────────────────────

let private ranOf (outcome: RunTestsOutcome) : RanReceipt =
  match receiptOf outcome with
  | RunReceipt.Ran ran -> ran
  | other -> failtestf "expected Ran, got %A" other

[<Tests>]
let sourceTests =
  testList "run_tests says what source its receipt reflects" [

    testTask "WHY — a run over a project whose files are all older than its build is AllPassed, and the receipt says the source was in sync" {
      do! withFixture (fun f -> task {
        let engine = Engine(sid, cases, Map.empty)
        let running = runTests (ctxOverProject engine f LastRebuild.NeverRebuilt) "agent" everything
        do! engine.RunDispatched.WaitAsync patience
        engine.ReleaseWorker ()
        let! outcome = running
        let ran = ranOf outcome
        ran.Verdict |> Expect.equal "plain AllPassed" RunVerdict.AllPassed
        match ran.Source with
        | SourceState.InSync _ -> ()
        | other -> failtestf "expected InSync, got %A" other })
    }

    testTask "WHY — the Nehemiah case: a source edited after the build, then run_tests, is passed-on-stale-source and names the file" {
      do! withFixture (fun f -> task {
        SourceStateFixtures.editSource f
        let engine = Engine(sid, cases, Map.empty)
        let running = runTests (ctxOverProject engine f LastRebuild.NeverRebuilt) "agent" everything
        do! engine.RunDispatched.WaitAsync patience
        engine.ReleaseWorker ()
        let! outcome = running
        let ran = ranOf outcome
        ran.Verdict |> Expect.equal "not AllPassed" RunVerdict.PassedOnStaleSource
        match ran.Source with
        | SourceState.Stale [ file ] -> file.Path |> Expect.equal "the edited file" f.Source
        | other -> failtestf "expected Stale naming the source, got %A" other
        TestRunReceipt.summarize (RunReceipt.Ran ran) |> Expect.stringContains "the text says STALE" "STALE" })
    }

    testTask "WHY — a session with no project loaded cannot be called in sync: the run is passed-on-unknown-source, and the reason is that no project is loaded" {
      let engine = Engine(sid, cases, Map.empty)
      let running = runTests (ctxFor engine sid ready) "agent" everything
      do! engine.RunDispatched.WaitAsync patience
      engine.ReleaseWorker ()
      let! outcome = running
      let ran = ranOf outcome
      ran.Verdict |> Expect.equal "not AllPassed" RunVerdict.PassedOnUnknownSource
      ran.Source |> Expect.equal "no project" (SourceState.Unknown UnknownReason.NoProjectLoaded)
    }

    testTask "WHY — a run dispatched while a rebuild is in progress says so: passed-while-rebuilding" {
      do! withFixture (fun f -> task {
        let engine = Engine(sid, cases, Map.empty)
        let rebuilding = LastRebuild.Latest (RebuildOutcome.InProgress (SourceStateFixtures.at -1))
        let running = runTests (ctxOverProject engine f rebuilding) "agent" everything
        do! engine.RunDispatched.WaitAsync patience
        engine.ReleaseWorker ()
        let! outcome = running
        (ranOf outcome).Verdict |> Expect.equal "passed while rebuilding" RunVerdict.PassedWhileRebuilding })
    }

    testTask "WHY — a file edited WHILE the run is in flight counts: dispatched in sync, finished stale is stale" {
      do! withFixture (fun f -> task {
        let engine = Engine(sid, cases, Map.empty)
        let running = runTests (ctxOverProject engine f LastRebuild.NeverRebuilt) "agent" everything
        do! engine.RunDispatched.WaitAsync patience
        SourceStateFixtures.editSource f
        engine.ReleaseWorker ()
        let! outcome = running
        (ranOf outcome).Verdict |> Expect.equal "stale, because of the edit during the run" RunVerdict.PassedOnStaleSource })
    }

    testTask "WHY — the receipt is frozen when the run settles: an edit AFTER it finished does not change what the same receipt_id says" {
      do! withFixture (fun f -> task {
        let engine = Engine(sid, cases, Map.empty)
        let ctx = ctxOverProject engine f LastRebuild.NeverRebuilt
        let running = runTests ctx "agent" everything
        do! engine.RunDispatched.WaitAsync patience
        engine.ReleaseWorker ()
        let! first = running
        let firstRan = ranOf first
        SourceStateFixtures.editSource f
        let! again = runTests ctx "agent" { everything with Continue = Some firstRan.RequestId }
        (ranOf again).Verdict |> Expect.equal "still what the run was" RunVerdict.AllPassed
        (ranOf again).Source |> Expect.equal "the same reading" firstRan.Source })
    }

    testTask "WHY — the tool's structured result carries the source and the verdict token, so an agent branches on data" {
      do! withFixture (fun f -> task {
        SourceStateFixtures.editSource f
        let engine = Engine(sid, cases, Map.empty)
        let tools = SageFsTools(ctxOverProject engine f LastRebuild.NeverRebuilt, Microsoft.Extensions.Logging.Abstractions.NullLogger<SageFsTools>.Instance)
        let running = tools.run_tests("", "", "", 30, "", sid, "")
        do! engine.RunDispatched.WaitAsync patience
        engine.ReleaseWorker ()
        let! (result: ModelContextProtocol.Protocol.CallToolResult) = running
        let data = result.StructuredContent.Value
        data.GetProperty("verdict").GetString() |> Expect.equal "the verdict token" "PassedOnStaleSource"
        data.GetProperty("source").GetProperty("state").GetString() |> Expect.equal "the source state" "Stale"
        data.GetProperty("replFreshness").GetProperty("state").GetString() |> Expect.equal "the REPL's own freshness stays its own field" "InSync" })
    }
  ]
