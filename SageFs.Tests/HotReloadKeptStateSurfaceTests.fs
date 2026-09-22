/// Where a person or an agent actually meets kept live state (rule 3): the
/// dashboard's Hot Reload panel and the MCP tool. The outcome tests in
/// HotReloadStateOutcomeTests prove the worker keeps and resets real state in
/// a real app; these prove the notice and the reset reach the two surfaces
/// people use, and go to the worker endpoint that does it.
module SageFs.Tests.HotReloadKeptStateSurfaceTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.McpTools
open SageFs.Server
open SageFs.Server.McpTools
open SageFs.WorkerProtocol

let private kept : Features.ReloadOutcome.KeptValue =
  { Binding = "StateFixture.State.tuned"; KeptValue = "13"; NewInitializer = "25" }

let private decoded (node: XmlNode) = Net.WebUtility.HtmlDecode(renderNode node)

/// A real worker HTTP server whose kept state is a fake: it lists `kept` and
/// records what it was asked to reset.
let private startWorker (resets: Collections.Concurrent.ConcurrentBag<string>) =
  let access : Features.KeptState.Access =
    { Features.KeptState.Access.none with
        Pending = fun () -> [ kept ]
        Reset =
          fun binding -> async {
            resets.Add binding
            return
              match binding = kept.Binding with
              | true -> Features.KeptState.ResetOutcome.Reset(binding, "25")
              | false -> Features.KeptState.ResetOutcome.NothingPending binding } }
  WorkerHttpTransport.startServer
    (fun _ -> async { return WorkerResponse.WorkerShuttingDown })
    (ref HotReloadState.empty)
    access
    []
    (fun () -> WarmupContext.empty)
    (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun })
    (fun () -> HostAgent.AgentAnswered HostAgent.NoCoverage)
    0

/// The MCP tools, pointed at a HotReload session whose worker listens on `port`.
let private toolsFor (port: int) : SageFsTools =
  let sid = SessionId.newId ()
  let sessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- SessionId.value sid
  let info : SessionInfo =
    { Id = sid
      Name = None
      Projects = [ "StateFixture.fsproj" ]
      WorkingDirectory = "/tmp/state"
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = SessionLifecycleStatus.Ready { Pid = 42; Port = Some port }
      Workflow = WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning }
  let ops =
    { SessionManagementOps.stub with
        GetSessionInfo = fun _ -> Task.FromResult(Some info)
        // A routable worker: tool routing refuses a session it can't reach.
        GetProxy = fun _ -> Task.FromResult(Some(fun _ -> async { return WorkerResponse.WorkerShuttingDown })) }
  let ctx : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = Event<Features.DiagnosticsStore.T>().Publish
      StateChanged = None
      SessionOps = ops
      SessionMap = sessionMap
      McpPort = 0
      Dispatch = None
      GetElmModel = None
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None
      RecordEval = None
      ActivityTracker = AgentActivityTracker.create ()
      LiveSnapshotSink = None
      CohortOwner = None }
  SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

[<Tests>]
let keptStateSurfaceTests =
  testList "kept live state surfaces" [
    testCase "WHY — DashboardFragments.renderHotReloadPanelWithKept — the Hot Reload panel says what the save kept and what's waiting, because keeping state quietly is the thing people hate about Flutter" <| fun _ ->
      let html = DashboardFragments.renderHotReloadPanelWithKept "abcd1234" [] 0 [ kept ] |> decoded
      html |> Expect.stringContains "names the binding" "StateFixture.State.tuned"
      html |> Expect.stringContains "shows the value it kept" "13"
      html |> Expect.stringContains "shows the initializer waiting for a reset" "25"

    testCase "WHY — DashboardFragments.renderHotReloadPanelWithKept — the reset button posts the binding to the reset-state endpoint through Datastar, never a hand-rolled fetch" <| fun _ ->
      let html = DashboardFragments.renderHotReloadPanelWithKept "abcd1234" [] 0 [ kept ] |> decoded
      html |> Expect.stringContains "posts to the proxied worker route" "@post('/api/sessions/abcd1234/hotreload/reset-state')"
      html |> Expect.stringContains "stages the field the worker reads" "$binding = 'StateFixture.State.tuned';"
      html.Contains(" onclick=", StringComparison.Ordinal) |> Expect.isFalse "no raw onclick"

    testCase "WHY — DashboardFragments.renderHotReloadPanel — nothing kept means no notice and no reset button" <| fun _ ->
      let html = DashboardFragments.renderHotReloadPanel "abcd1234" [] 0 |> decoded
      html.Contains "reset-state" |> Expect.isFalse "no reset without something to reset"

    testTask "WHY — reset_hot_reload_state — with no binding it lists what the worker kept, so an agent sees the same notice the dashboard shows" {
      let resets = Collections.Concurrent.ConcurrentBag<string>()
      use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker resets
      let tools = toolsFor (Uri(server.BaseUrl).Port)
      let! reply = tools.reset_hot_reload_state("", "")
      reply |> Expect.stringContains "names the binding" "StateFixture.State.tuned"
      reply |> Expect.stringContains "and the value it kept" "13"
      reply |> Expect.stringContains "and the initializer waiting" "25"
      resets |> Expect.isEmpty "listing never resets anything"
    }

    testTask "WHY — reset_hot_reload_state — with a binding it asks the worker to reset exactly that one, because the reset re-runs only that initializer" {
      let resets = Collections.Concurrent.ConcurrentBag<string>()
      use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker resets
      let tools = toolsFor (Uri(server.BaseUrl).Port)
      let! reply = tools.reset_hot_reload_state(kept.Binding, "")
      reply |> Expect.stringContains "says what it's worth now" "Reset 'StateFixture.State.tuned': it's 25 now."
      resets |> Seq.toList |> Expect.equal "exactly that binding went to the worker" [ kept.Binding ]
    }
  ]
