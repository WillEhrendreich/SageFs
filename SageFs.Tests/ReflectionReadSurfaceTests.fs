/// Where a person or an agent meets rule 2's reflection reads: the session
/// config, the worker's endpoints, the MCP tool and the dashboard's Hot
/// Reload panel. The runtime tests (ReflectionReadTrackingTests) prove the
/// modes behave; these prove the mode and the hot-loop question reach the
/// surfaces people use, and that a choice there goes back to the worker.
module SageFs.Tests.ReflectionReadSurfaceTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.Middleware.ValueReads
open SageFs.McpTools
open SageFs.Server
open SageFs.Server.McpTools
open SageFs.WorkerProtocol

let private decoded (node: XmlNode) = Net.WebUtility.HtmlDecode(renderNode node)

let private notice : ReflectionNotice =
  { Value = "App.State.greeting"; Caller = "App.Log.tick"; ReadsPerSecond = 12000; Mode = ReflectionReadMode.MarkOnReflect }

let private report (mode: ReflectionReadMode) (notices: ValueNotice list) : ReflectionReadsReport =
  { Mode = mode; Watch = ReflectionWatchStatus.Watching; Notices = notices; Walks = 3L; SiteHits = 1200L }

let private asked = report ReflectionReadMode.MarkOnReflect [ { Value = notice.Value; State = NoticeState.Asked notice } ]

[<Tests>]
let configTests =
  testList "reflection reads: session config" [
    testCase "WHY — the mode is a setting whose values are exactly the modes' names, so config and MCP spell it one way" <| fun _ ->
      let d = SessionAgent.reflectionReadModeSetting
      for mode in ReflectionReadMode.all do
        match d.Parse(ReflectionReadMode.name mode) with
        | Result.Ok v -> d.Render v |> Expect.equal "renders back to the name" (ReflectionReadMode.name mode)
        | Result.Error e -> failtestf "%A should parse: %A" mode e
      match d.Parse "fastest" with
      | Result.Ok v -> failtestf "'fastest' isn't a mode, got %A" v
      | Result.Error _ -> ()

    testCase "WHY — with nothing configured a session starts in the default mode" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-reflect-%s" (Guid.NewGuid().ToString "N"))
      IO.Directory.CreateDirectory dir |> ignore
      let settings = SessionAgent.reflectionSettingsAt { GlobalDir = dir; Repo = NoRepoCheckout }
      settings.Mode |> Expect.equal "probe-callers" ReflectionReadMode.standard
      settings.HotLoop |> Expect.equal "the named hot-loop threshold" HotLoopThreshold.standard
      settings.Tiering |> Expect.equal "tiering off, so the watch can't lapse" TieringChoice.TieringOffWhileWatching

    testCase "WHY — a repo can pick its mode, and a new session starts in it" <| fun _ ->
      let root = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-reflect-%s" (Guid.NewGuid().ToString "N"))
      let globalDir = IO.Path.Combine(root, "global")
      IO.Directory.CreateDirectory globalDir |> ignore
      let paths = { GlobalDir = globalDir; Repo = RepoRootAt root }
      match SettingsCatalog.edit paths LRepo (ReflectionReadMode.name ReflectionReadMode.ExactEveryRead) SessionAgent.reflectionReadModeSetting with
      | Result.Ok _ -> ()
      | Result.Error e -> failtestf "the edit should persist: %A" e
      (SessionAgent.reflectionSettingsAt paths).Mode |> Expect.equal "the repo's mode" ReflectionReadMode.ExactEveryRead

    testCase "WHY — the tiering choice is a setting whose values are exactly the choices' names" <| fun _ ->
      let d = SessionAgent.tieredCompilationSetting
      for choice in TieringChoice.all do
        match d.Parse(TieringChoice.name choice) with
        | Result.Ok v -> d.Render v |> Expect.equal "renders back to the name" (TieringChoice.name choice)
        | Result.Error e -> failtestf "%A should parse: %A" choice e
      match d.Parse "sometimes" with
      | Result.Ok v -> failtestf "'sometimes' isn't a choice, got %A" v
      | Result.Error _ -> ()

    testCase "WHY — a bad persisted tiering value falls back to tiering-off-while-watching, not a session that won't start" <| fun _ ->
      let root = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-reflect-%s" (Guid.NewGuid().ToString "N"))
      let globalDir = IO.Path.Combine(root, "global")
      IO.Directory.CreateDirectory globalDir |> ignore
      let paths = { GlobalDir = globalDir; Repo = RepoRootAt root }
      SettingsStore.setKey (SettingsStore.repoPath root) SessionAgent.tieredCompilationSetting.Key "sometimes" |> ignore
      (SessionAgent.reflectionSettingsAt paths).Tiering |> Expect.equal "falls back to the safe default" TieringChoice.TieringOffWhileWatching

    testCase "WHY — a repo can choose to keep tiering on, and a new session starts with it kept on" <| fun _ ->
      let root = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-reflect-%s" (Guid.NewGuid().ToString "N"))
      let globalDir = IO.Path.Combine(root, "global")
      IO.Directory.CreateDirectory globalDir |> ignore
      let paths = { GlobalDir = globalDir; Repo = RepoRootAt root }
      match SettingsCatalog.edit paths LRepo (TieringChoice.name TieringChoice.KeepTiering) SessionAgent.tieredCompilationSetting with
      | Result.Ok _ -> ()
      | Result.Error e -> failtestf "the edit should persist: %A" e
      (SessionAgent.reflectionSettingsAt paths).Tiering |> Expect.equal "the repo's choice" TieringChoice.KeepTiering
  ]

[<Tests>]
let tieringEnvironmentTests =
  testList "reflection reads: the process environment" [
    testCase "WHY — no watch means no env at all" <| fun _ ->
      SageFs.Middleware.ValueReadTracking.processEnvironment SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads
      |> Expect.isEmpty "nothing to set when nothing is watched"

    testCase "WHY — every (mode, tiering choice) combination sets exactly what the choice says, uniformly across modes" <| fun _ ->
      for mode in ReflectionReadMode.all do
        let watchWith tiering =
          SageFs.Middleware.ValueReadTracking.ValueReadWatch.WatchValueReads { Mode = mode; HotLoop = HotLoopThreshold.standard; Tiering = tiering }
        SageFs.Middleware.ValueReadTracking.processEnvironment (watchWith TieringChoice.TieringOffWhileWatching)
        |> Expect.equal (sprintf "%A: tiering-off-while-watching turns tiering off" mode) [ "DOTNET_TieredCompilation", "0" ]
        SageFs.Middleware.ValueReadTracking.processEnvironment (watchWith TieringChoice.KeepTiering)
        |> Expect.isEmpty (sprintf "%A: keep-tiering sets nothing, so the runtime's default (on) stands" mode)
  ]

[<Tests>]
let wireTests =
  testList "reflection reads: the wire" [
    testCase "WHY — the report survives the worker's JSON, every notice state and watch status, so the dashboard and MCP see what the app saw" <| fun _ ->
      let reports =
        [ asked
          report ReflectionReadMode.ProbeCallers [ { Value = "A.b"; State = NoticeState.Chosen(notice, ReflectionReadMode.ProbeCallers) } ]
          { report ReflectionReadMode.ExactEveryRead [] with Watch = ReflectionWatchStatus.NotWatching "no canary" }
          { report ReflectionReadMode.MarkOnReflect [] with Watch = ReflectionWatchStatus.Lapsed "recompiled Invoke" } ]
      for r in reports do
        use doc = Text.Json.JsonDocument.Parse(Features.KeptState.ReflectionReadsJson.render r)
        Features.KeptState.ReflectionReadsJson.parse doc.RootElement |> Expect.equal "round-trips" (Result.Ok r)
  ]

/// A real worker HTTP server whose agent is a fake: it reports `current` and
/// records every mode it's asked to switch to.
let private startWorker (current: ReflectionReadsReport) (switched: Collections.Concurrent.ConcurrentBag<ReflectionReadMode>) =
  let access : Features.KeptState.Access =
    { Features.KeptState.Access.none with
        ReflectionReads = fun () -> Result.Ok current
        SetReflectionMode =
          fun mode ->
            switched.Add mode
            Result.Ok { current with Mode = mode } }
  WorkerHttpTransport.startServer
    (fun _ -> async { return WorkerResponse.WorkerShuttingDown })
    (ref HotReloadState.empty)
    access
    []
    (fun () -> WarmupContext.empty)
    (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun })
    (fun () -> HostAgent.AgentAnswered HostAgent.NoCoverage)
    0

let private toolsFor (port: int) : SageFsTools =
  let sid = SessionId.newId ()
  let sessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- SessionId.value sid
  let info : SessionInfo =
    { Id = sid
      Name = None
      Projects = [ "App.fsproj" ]
      WorkingDirectory = "/tmp/reflect"
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

let private http = new Net.Http.HttpClient()

[<Tests>]
let endpointAndToolTests =
  testList "reflection reads: worker endpoint and MCP tool" [
    testTask "WHY — GET /hotreload carries the reflection report, so the dashboard shows the question without a second call" {
      let switched = Collections.Concurrent.ConcurrentBag()
      use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker asked switched
      let! (json: string) = http.GetStringAsync(server.BaseUrl + "/hotreload")
      let doc = Text.Json.JsonDocument.Parse(json)
      match doc.RootElement.TryGetProperty "reflectionReads" with
      | true, el -> Features.KeptState.ReflectionReadsJson.parse el |> Expect.equal "the report" (Result.Ok asked)
      | false, _ -> failtestf "no reflectionReads in %s" json
    }

    testTask "WHY — POST /hotreload/reflection-mode switches the running app's mode, and a name that isn't a mode is refused with the ones that are" {
      let switched = Collections.Concurrent.ConcurrentBag()
      use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker asked switched
      let body = new Net.Http.StringContent("""{"mode":"probe-callers"}""", Text.Encoding.UTF8, "application/json")
      let! (accepted: Net.Http.HttpResponseMessage) = http.PostAsync(server.BaseUrl + "/hotreload/reflection-mode", body)
      int accepted.StatusCode |> Expect.equal "switched" 200
      switched |> Seq.toList |> Expect.equal "the worker was asked for exactly that mode" [ ReflectionReadMode.ProbeCallers ]
      let bad = new Net.Http.StringContent("""{"mode":"fastest"}""", Text.Encoding.UTF8, "application/json")
      let! (rejected: Net.Http.HttpResponseMessage) = http.PostAsync(server.BaseUrl + "/hotreload/reflection-mode", bad)
      int rejected.StatusCode |> Expect.equal "refused" 400
      let! (why: string) = rejected.Content.ReadAsStringAsync()
      why |> Expect.stringContains "names the real modes" "mark-on-reflect"
    }

    testTask "WHY — set_reflection_read_mode with no mode says the current mode and every question the app has asked, with the choices" {
      let switched = Collections.Concurrent.ConcurrentBag()
      use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker asked switched
      let tools = toolsFor (Uri(server.BaseUrl).Port)
      let! reply = tools.set_reflection_read_mode("", "")
      reply |> Expect.stringContains "the current mode" "mark-on-reflect"
      reply |> Expect.stringContains "the value" "App.State.greeting"
      reply |> Expect.stringContains "the caller" "App.Log.tick"
      reply |> Expect.stringContains "the rate" "12000"
      for mode in ReflectionReadMode.all do
        reply |> Expect.stringContains "every choice, with what it does" (ReflectionReadMode.consequence mode)
      switched |> Expect.isEmpty "listing never switches"
    }

    testTask "WHY — set_reflection_read_mode with a mode switches the running app to it" {
      let switched = Collections.Concurrent.ConcurrentBag()
      use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker asked switched
      let tools = toolsFor (Uri(server.BaseUrl).Port)
      let! reply = tools.set_reflection_read_mode("exact-every-read", "")
      switched |> Seq.toList |> Expect.equal "switched" [ ReflectionReadMode.ExactEveryRead ]
      reply |> Expect.stringContains "says what it's in now" "exact-every-read"
    }
  ]

[<Tests>]
let panelTests =
  testList "reflection reads: the Hot Reload panel" [
    testCase "WHY — the panel shows the mode with the current one marked, and every choice posts through Datastar, never a hand-rolled fetch" <| fun _ ->
      let html =
        DashboardFragments.renderHotReloadPanelFull "abcd1234" [] 0 [] (Features.KeptState.ReflectionReadsView.Reported(report ReflectionReadMode.ProbeCallers []))
        |> decoded
      for mode in ReflectionReadMode.all do
        html |> Expect.stringContains "stages the mode the worker reads" (sprintf "$mode = '%s';" (ReflectionReadMode.name mode))
      html |> Expect.stringContains "posts to the proxied worker route" "@post('/api/sessions/abcd1234/hotreload/reflection-mode')"
      html |> Expect.stringContains "marks the current mode" "aria-pressed=\"true\""
      html.Contains(" onclick=", StringComparison.Ordinal) |> Expect.isFalse "no raw onclick"

    testCase "WHY — a hot loop's question is on the panel: value, caller, rate, what the current mode costs, and each choice with what it does" <| fun _ ->
      let html = DashboardFragments.renderHotReloadPanelFull "abcd1234" [] 0 [] (Features.KeptState.ReflectionReadsView.Reported asked) |> decoded
      html |> Expect.stringContains "the value" "App.State.greeting"
      html |> Expect.stringContains "the caller" "App.Log.tick"
      html |> Expect.stringContains "the rate" "12000"
      html |> Expect.stringContains "what mark-on-reflect costs" (ReflectionReadMode.cost ReflectionReadMode.MarkOnReflect)
      for mode in ReflectionReadMode.all do
        html |> Expect.stringContains "each choice's consequence" (ReflectionReadMode.consequence mode)

    testCase "WHY — an answered question stops asking" <| fun _ ->
      let chosen = report ReflectionReadMode.ProbeCallers [ { Value = notice.Value; State = NoticeState.Chosen(notice, ReflectionReadMode.ProbeCallers) } ]
      let html = DashboardFragments.renderHotReloadPanelFull "abcd1234" [] 0 [] (Features.KeptState.ReflectionReadsView.Reported chosen) |> decoded
      html.Contains(ReflectionReadMode.cost ReflectionReadMode.MarkOnReflect) |> Expect.isFalse "the question isn't asked again"

    testCase "WHY — a lapsed watch is said out loud on the panel, because every value edit restarts until the app does" <| fun _ ->
      let lapsed = { report ReflectionReadMode.ProbeCallers [] with Watch = ReflectionWatchStatus.Lapsed "the runtime recompiled MethodBase.Invoke" }
      let html = DashboardFragments.renderHotReloadPanelFull "abcd1234" [] 0 [] (Features.KeptState.ReflectionReadsView.Reported lapsed) |> decoded
      html |> Expect.stringContains "says why" "recompiled MethodBase.Invoke"

    testCase "WHY — a worker that doesn't report reflection reads gets no mode control, not a guessed one" <| fun _ ->
      let html = DashboardFragments.renderHotReloadPanelFull "abcd1234" [] 0 [] (Features.KeptState.ReflectionReadsView.NotReported "old worker") |> decoded
      html.Contains "reflection-mode" |> Expect.isFalse "no control for a mode nobody reported"
  ]
