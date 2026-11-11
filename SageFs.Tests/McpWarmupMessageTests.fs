module SageFs.Tests.McpWarmupMessageTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.McpSessionRouting
open SageFs.McpRouteError

// A tool call that reaches a session still warming up is refused with a sentence that says how long to wait. The sentence is
// `formatSessionResolution`'s, and `routeToSessionWithin` used to carry its own copy that said "typically takes 15-30s for test
// projects": the figure the first large-repo trial ran into (a session on a big repo takes minutes), kept in one of the two
// places after the other was corrected.

let warmingInfo (id: WorkerProtocol.SessionId) : WorkerProtocol.SessionInfo =
  { Id = id
    Name = None
    Projects = []
    WorkingDirectory = ""
    SolutionRoot = None
    Status = WorkerProtocol.SessionLifecycleStatus.Starting { Pid = 4242; Port = None }
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = SageFs.ReplFreshness.InSync }

/// A context whose one session is still starting and has no worker transport yet.
let warmingContext (sessionId: string) : McpContext =
  let result = SageFs.Tests.TestInfrastructure.globalActorResult.Value
  let ops : SessionManagementOps =
    { CreateSession = fun _ _ _ -> Task.FromResult(Ok "test-session")
      ListSessions = fun () -> Task.FromResult("No sessions")
      StopSession = fun _ -> Task.FromResult(Ok "stopped")
      PurgeSession = fun _ -> Task.FromResult(Ok "purged")
      RestartSession = fun _ _ -> Task.FromResult(Ok "restarted")
      GetProxy = fun _ -> Task.FromResult None
      GetSessionInfo = fun id -> Task.FromResult(Some (warmingInfo id))
      GetAllSessions = fun () -> Task.FromResult([])
      UpdateSessionStatus = fun _ _ -> Task.FromResult(())
      NotifyWorkerDied = fun _ -> ()
      ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
      ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
      AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
      EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
      AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
      SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
      GetAdoptedCore = fun _ -> Task.FromResult(None)
      RefetchLiveTestingState = fun _ -> Task.FromResult(())
      GetWarmupProgress = fun _ -> Task.FromResult(None) }
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- sessionId
  { FrictionStore = None
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = ops
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None; RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None } : McpContext

[<Tests>]
let mcpWarmupMessageTests =
  testList "warming session refusal" [
    testTask "WHY — a call to a session that is still starting is told what the routing module says, so the wait it names is one sentence in one place" {
      let sessionId = "aaa00001"
      let ctx = warmingContext sessionId
      let status = WorkerProtocol.SessionLifecycleStatus.Starting { Pid = 4242; Port = None }
      let! routed =
        routeToSessionWithin ctx CancellationToken.None sessionId (fun _ -> WorkerProtocol.WorkerMessage.GetStatus "rid")
      match routed with
      | Result.Ok _ -> failtest "a session with no worker transport cannot be routed to"
      | Result.Error (Message text) ->
        text |> Expect.equal "the routing module's sentence" (formatSessionResolution (WarmingUp (sessionId, status)))
      | Result.Error other -> failtestf "expected the warmup message, got %A" other
    }

    testCase "the sentence names the wait the way a large repo needs it, and carries no fixed figure for a test project" <| fun _ ->
      let status = WorkerProtocol.SessionLifecycleStatus.Starting { Pid = 4242; Port = None }
      let text = formatSessionResolution (WarmingUp ("aaa00001", status))
      text |> Expect.stringContains "minutes on a large repo" "minutes on a large repo"
      text |> Expect.stringContains "progress is in get_session_status" "get_session_status"
      text |> Expect.stringContains "waits on the server" "wait_seconds=60"
      text.Contains "15-30s" |> Expect.isFalse "no fixed figure"
  ]
