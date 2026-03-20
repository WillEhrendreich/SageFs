module SageFs.Tests.TargetedVerifyMcpToolTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.WorkerProtocol

let private dummyProxy (_: WorkerMessage) =
  async { return WorkerResponse.StatusResult("reply", { Status = SessionStatus.Ready; StatusMessage = None; EvalCount = 0; AvgDurationMs = 0L; MinDurationMs = 0L; MaxDurationMs = 0L }) }

let private mkSessionInfo status =
  { Id = SessionId.newId ()
    Name = Some "tests"
    Projects = [ "SageFs.Tests.fsproj" ]
    WorkingDirectory = @"C:\Code\Repos\SageFs"
    SolutionRoot = Some @"C:\Code\Repos\SageFs"
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    Status = status
    WorkerPid = None
    Workflow = WorkflowTypes.SessionWorkflow.Interactive }

let private mkSessionContext sid files =
  { SessionId = sid
    ProjectNames = [ "SageFs.Tests" ]
    WorkingDir = @"C:\Code\Repos\SageFs"
    Status = "Ready"
    Warmup = WarmupContext.empty
    FileStatuses = files
    Workflow = WorkflowTypes.SessionWorkflow.Interactive }

let private mkFile path readiness =
  { Path = path
    Readiness = readiness
    LastLoadedAt = Some DateTimeOffset.UtcNow
    IsWatched = true }

let private mkCtx (sessionInfo: SessionInfo) (sessionContext: SessionContext option) : McpContext =
  let diagEvent = Event<Features.DiagnosticsStore.T>()
  { Persistence = EventStore.EventPersistence.noop
    DiagnosticsChanged = diagEvent.Publish
    StateChanged = None
    SessionOps =
      { SessionManagementOps.stub with
          GetAllSessions = fun () -> Task.FromResult([ sessionInfo ])
          GetProxy = fun _ -> Task.FromResult(Some dummyProxy)
          GetSessionInfo = fun _ -> Task.FromResult(Some sessionInfo) }
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel =
      Some (fun () ->
        { SageFsModel.initial() with
            SessionContext = sessionContext })
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    ActivityTracker = SageFs.AgentActivityTracker.create() }

[<Tests>]
let tests =
  testList "targeted_verify MCP tool" [
    testCaseTask "targeted_verify prefers snippet-first when the session is trustworthy" <| fun () -> task {
      let sessionInfo = mkSessionInfo SessionStatus.Ready
      let sid = SessionId.value sessionInfo.Id
      let ctx =
        mkCtx sessionInfo (Some (mkSessionContext sid [ mkFile "UserPreferences.fs" Loaded ]))

      let! output = targetedVerify ctx "mcp" (Some @"C:\Code\Repos\SageFs") "UserPreferences.loadFromFile" None
      output |> Expect.stringContains "should recommend snippet-first local proof" "snippet"
    }

    testCaseTask "targeted_verify refuses green when loaded files are stale" <| fun () -> task {
      let sessionInfo = mkSessionInfo SessionStatus.Ready
      let sid = SessionId.value sessionInfo.Id
      let ctx =
        mkCtx sessionInfo (Some (mkSessionContext sid [ mkFile "UserPreferences.fs" Stale ]))

      let! output = targetedVerify ctx "mcp" (Some @"C:\Code\Repos\SageFs") "UserPreferences.loadFromFile" None
      output |> Expect.stringContains "should explain stale session state" "stale definitions"
    }

    testCaseTask "targeted_verify plans exact guard when one is named" <| fun () -> task {
      let sessionInfo = mkSessionInfo SessionStatus.Ready
      let sid = SessionId.value sessionInfo.Id
      let ctx =
        mkCtx sessionInfo (Some (mkSessionContext sid [ mkFile "UserPreferences.fs" Loaded ]))

      let! output =
        targetedVerify
          ctx
          "mcp"
          (Some @"C:\Code\Repos\SageFs")
          "UserPreferences.loadFromFile"
          (Some "Tests.UserPreferences.guard")
      output |> Expect.stringContains "should mention exact guard" "Tests.UserPreferences.guard"
    }
  ]
