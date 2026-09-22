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
  async { return WorkerResponse.StatusResult("reply", { Status = SessionStatus.Ready; StatusMessage = None; EvalCount = 0; AvgDurationMs = 0L; MinDurationMs = 0L; MaxDurationMs = 0L; Projects = []; CoreVersion = "0.0.0-test" }) }

let private mkSessionInfo status =
  { Id = SessionId.newId ()
    Name = Some "tests"
    Projects = [ "SageFs.Tests.fsproj" ]
    WorkingDirectory = @"C:\Code\Repos\SageFs"
    SolutionRoot = Some @"C:\Code\Repos\SageFs"
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    Status = SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.Ready { Pid = 1; Port = None }) status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning }

let private mkSessionContext sid files =
  { SessionId = sid
    ProjectNames = [ "SageFs.Tests" ]
    WorkingDir = @"C:\Code\Repos\SageFs"
    Status = "Ready"
    Warmup = WarmupContext.empty
    FileStatuses = files
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    AutoOpenNamespaces = true }

let private mkFile path readiness =
  { Path = path
    Readiness = readiness
    LastLoadedAt = Some DateTimeOffset.UtcNow
    IsWatched = true }

let private mkCtx (sessionInfo: SessionInfo) (sessionContext: SessionContext option) : McpContext =
  let diagEvent = Event<Features.DiagnosticsStore.T>()
  { FrictionStore = None
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
    GetFeatureState = None; RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None
    CohortOwner = None }

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

    // Was "targeted_verify plans exact guard when one is named," asserting the
    // guard name appeared in a "Plan: ..." sentence. That passed only because
    // `TargetedVerification.summarize` swallowed a Blocked-evidence outcome
    // behind the Perform-plan arms (sagefs-roast.md §10 / gap 2) — this call
    // site (`targetedVerify` in Mcp.fs) always passes `createReport` no
    // snippet/exact-test evidence, so the honest report is "no evidence was
    // collected," not a plan that names the guard. Naming the guard in the
    // response is real follow-on work (wiring actual evidence collection into
    // the Mcp.fs call site) — out of scope here; this test now asserts the
    // corrected, honest behavior instead of the bug it used to ride on.
    testCaseTask "targeted_verify reports missing evidence honestly even when an exact guard is named" <| fun () -> task {
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
      output |> Expect.stringContains "should say no evidence was collected, not a plan sentence" "No snippet or exact-test evidence"
    }
  ]
