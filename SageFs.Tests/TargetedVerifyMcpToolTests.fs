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
  async { return WorkerResponse.StatusResult("reply", { Status = SessionStatus.Ready; StatusMessage = None; EvalCount = 0; AvgDurationMs = FixtureDurations.unmeasuredMs; MinDurationMs = FixtureDurations.unmeasuredMs; MaxDurationMs = FixtureDurations.unmeasuredMs; Projects = []; CoreVersion = "0.0.0-test" }) }

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
    App = SageFs.AppRun.AppRunState.NotRunning; Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }

/// The session has the project on disk loaded, and its worker says when it loaded the build: whether the loaded definitions
/// are behind the files is read off the disk (SourceStateFixtures), not off a file list the Elm model carries.
let private mkCtx (session: SessionInfo) (project: SourceStateFixtures.Project) : McpContext =
  let sessionInfo = { session with ProjectRoles = [ SourceStateFixtures.classified project ] }
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
    GetElmModel = Some (fun () -> SageFsModel.initial())
    GetElmRegions = None
    GetWarmupContext = Some (fun _ -> Task.FromResult (Some (SourceStateFixtures.warmup project)))
    GetFeatureState = None; RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

[<Tests>]
let tests =
  testList "targeted_verify MCP tool" [
    testCaseTask "targeted_verify prefers snippet-first when the session is trustworthy" <| fun () ->
      SourceStateFixtures.using (fun project -> task {
        let ctx = mkCtx (mkSessionInfo SessionStatus.Ready) project
        let! output = targetedVerify ctx "mcp" (Some @"C:\Code\Repos\SageFs") "UserPreferences.loadFromFile" None
        output |> Expect.stringContains "should recommend snippet-first local proof" "snippet" })

    testCaseTask "targeted_verify refuses green when a file was written after the build the session loaded" <| fun () ->
      SourceStateFixtures.using (fun project -> task {
        SourceStateFixtures.editSource project
        let ctx = mkCtx (mkSessionInfo SessionStatus.Ready) project
        let! output = targetedVerify ctx "mcp" (Some @"C:\Code\Repos\SageFs") "UserPreferences.loadFromFile" None
        output |> Expect.stringContains "should explain stale session state" "stale definitions" })

    testCaseTask "targeted_verify refuses green while a rebuild is in progress, because the session still runs the build from before it" <| fun () ->
      SourceStateFixtures.using (fun project -> task {
        let rebuilding = { mkSessionInfo SessionStatus.Ready with Rebuild = LastRebuild.Latest (RebuildOutcome.InProgress (SourceStateFixtures.at -1)) }
        let ctx = mkCtx rebuilding project
        let! output = targetedVerify ctx "mcp" (Some @"C:\Code\Repos\SageFs") "UserPreferences.loadFromFile" None
        (output.Contains "snippet-first") |> Expect.isFalse "must not recommend a plan over a build that is being replaced"
        output |> Expect.stringContains "says why" "rebuild" })

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
    testCaseTask "targeted_verify reports missing evidence honestly even when an exact guard is named" <| fun () ->
      SourceStateFixtures.using (fun project -> task {
        let ctx = mkCtx (mkSessionInfo SessionStatus.Ready) project
        let! output =
          targetedVerify
            ctx
            "mcp"
            (Some @"C:\Code\Repos\SageFs")
            "UserPreferences.loadFromFile"
            (Some "Tests.UserPreferences.guard")
        output |> Expect.stringContains "should say no evidence was collected, not a plan sentence" "No snippet or exact-test evidence" })
  ]
