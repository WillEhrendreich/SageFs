/// The tool-call gate and send_fsharp_code's body used to resolve the session
/// twice, and the gate's worker status probe had no short timeout (the worker
/// HTTP budget is ten minutes). These tests count the lookups and worker calls
/// around a gated eval, and check that a hung probe is refused quickly without
/// the session being marked Faulted.
module SageFs.Tests.GateAdmissionTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.WorkerProtocol


let private workingDirectory = "/work/gate-admission"

/// A session with a fake worker. Every registry lookup and worker message lands
/// in `Log`, in order, so a test can say exactly what happened.
type private Harness =
  { Ctx: McpContext
    Log: ConcurrentQueue<string> }

let private entries (h: Harness) (name: string) : int =
  h.Log |> Seq.filter (fun entry -> entry = name) |> Seq.length

let private readySnapshot : WorkerStatusSnapshot =
  { Status = SessionStatus.Ready
    StatusMessage = None
    EvalCount = 0
    AvgDurationMs = FixtureDurations.notRunMsInt64
    MinDurationMs = FixtureDurations.notRunMsInt64
    MaxDurationMs = FixtureDurations.notRunMsInt64
    Projects = []
    CoreVersion = "0.0.0-test" }

let private mkHarness (answerStatus: string -> Async<WorkerResponse>) : Harness =
  let log = ConcurrentQueue<string>()
  let sid = SessionId.newId ()
  let info : SessionInfo =
    { Id = sid
      Name = None
      Projects = []
      WorkingDirectory = workingDirectory
      SolutionRoot = None
      Status = SessionLifecycleStatus.Ready { Pid = 7; Port = Some 6000 }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }
  let proxy : SessionProxy =
    fun msg ->
      async {
        match msg with
        | WorkerMessage.GetStatus replyId ->
          log.Enqueue "worker:GetStatus"
          return! answerStatus replyId
        | WorkerMessage.EvalCode (_, replyId) ->
          log.Enqueue "worker:EvalCode"
          return WorkerResponse.EvalResult(replyId, Result.Ok "val it: int = 2", [], Map.empty)
        | other -> return failwithf "unexpected worker message in gate test: %A" other
      }
  let ops : SessionManagementOps =
    { SessionManagementOps.stub with
        GetProxy = fun _ ->
          log.Enqueue "GetProxy"
          Task.FromResult(Some proxy)
        GetSessionInfo = fun _ ->
          log.Enqueue "GetSessionInfo"
          Task.FromResult(Some info)
        GetAllSessions = fun () ->
          log.Enqueue "GetAllSessions"
          Task.FromResult [ info ]
        UpdateSessionStatus = fun _ _ ->
          log.Enqueue "UpdateSessionStatus"
          Task.FromResult ()
        NotifyWorkerDied = fun _ -> log.Enqueue "NotifyWorkerDied" }
  let ctx : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
      StateChanged = None
      SessionOps = ops
      SessionMap = ConcurrentDictionary<string, string>()
      McpPort = 0
      Dispatch = None
      GetElmModel = None
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None
      RecordEval = None
      ActivityTracker = AgentActivityTracker.create ()
      LiveBindings = None
      CohortOwner = None
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }
  { Ctx = ctx; Log = log }

let private answersReady (replyId: string) : Async<WorkerResponse> =
  async { return WorkerResponse.StatusResult(replyId, readySnapshot) }

/// Never answers, the way a wedged worker does.
let private neverAnswers (_: string) : Async<WorkerResponse> =
  let never = TaskCompletionSource<WorkerResponse>()
  Async.AwaitTask never.Task

let private tools (h: Harness) : SageFsTools = SageFsTools(h.Ctx, NullLogger<SageFsTools>.Instance)

let private sendCode (h: Harness) : Task<ModelContextProtocol.Protocol.CallToolResult> =
  (tools h).send_fsharp_code("mcp", "1 + 1;;", workingDirectory, "", "", 0, "", "")

let private admit (h: Harness) (bound: TimeSpan) : Task<Result<GateAdmission, string>> =
  admitToolCallWithin bound h.Ctx "mcp" None (Some workingDirectory) "send_fsharp_code"

let private admitted (verdict: Result<GateAdmission, string>) : GateAdmission =
  match verdict with
  | Result.Ok admission -> admission
  | Result.Error refusal -> failtestf "the gate refused a Ready session: %s" refusal

[<Tests>]
let tests =
  testList "tool gate admission" [

    testTask "a gated eval resolves the session once and asks the worker for status once before evaluating" {
      let h = mkHarness answersReady
      let! verdict = admit h Timeouts.gateStatusProbe
      let! _ = runAdmitted (admitted verdict) (fun () -> sendCode h)
      entries h "GetAllSessions"
      |> Expect.equal "the working directory was matched against the registry once, by the gate" 1
      entries h "GetProxy"
      |> Expect.equal "GetProxy ran for the gate's resolution, the status probe and the eval, not a fourth time" 3
      h.Log |> Seq.filter (fun entry -> entry.StartsWith "worker:") |> Seq.toList
      |> Expect.equal "one status probe, then the eval" [ "worker:GetStatus"; "worker:EvalCode" ]
    }

    testTask "a body run outside the gate's admission still resolves for itself" {
      let h = mkHarness answersReady
      let! _ = sendCode h
      entries h "GetAllSessions"
      |> Expect.equal "with no admission in scope the body resolves by working directory" 1
    }

    testTask "the gate's admission is cleared once the body has run" {
      let h = mkHarness answersReady
      let! verdict = admit h Timeouts.gateStatusProbe
      let! _ = runAdmitted (admitted verdict) (fun () -> sendCode h)
      let! _ = sendCode h
      entries h "GetAllSessions"
      |> Expect.equal "the gate resolved once and the later, ungated call resolved for itself" 2
    }

    testTask "an admission for a different call is not reused" {
      let h = mkHarness answersReady
      let! verdict = admitToolCallWithin Timeouts.gateStatusProbe h.Ctx "mcp" None None "send_fsharp_code"
      let! _ = runAdmitted (admitted verdict) (fun () -> sendCode h)
      (entries h "GetAllSessions", 2)
      |> Expect.isGreaterThanOrEqual "the gate looked at no working directory, the body looked at one, so the body resolved again"
    }

    testTask "a hung worker is refused within the probe bound and the session is not marked Faulted" {
      let h = mkHarness neverAnswers
      let probe = admit h TestTimeouts.hungProbeBound
      let! winner = Task.WhenAny(probe, Task.Delay(TestTimeouts.patienceInProcess))
      Object.ReferenceEquals(winner, probe)
      |> Expect.isTrue "the gate answered instead of waiting on the worker"
      match probe.Result with
      | Result.Error _ -> ()
      | Result.Ok _ -> failtest "a worker that never answers status must not be admitted"
      entries h "UpdateSessionStatus"
      |> Expect.equal "a probe that ran out of time is not a status change" 0
      entries h "NotifyWorkerDied"
      |> Expect.equal "and does not tell the SessionManager the worker died" 0
    }
  ]
