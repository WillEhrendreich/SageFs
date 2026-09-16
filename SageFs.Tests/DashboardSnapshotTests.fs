module SageFs.Tests.DashboardSnapshotTests

open System
open Expecto
open Expecto.Flip
open VerifyExpecto
open VerifyTests
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Features.LiveTesting
open SageFs.Server.Dashboard
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let verifyDashboard (name: string) (html: string) =
  SageFs.Tests.TestInfrastructure.Snapshots.verify "DashboardSnapshotTests" name "html" html


let dashboardRenderSnapshotTests = testList "Dashboard render snapshots" [
  testTask "renderSessionStatus ready" {
    let html = renderSessionStatus "Ready" "session-abc" "/home/user/project" "" "REPL" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_ready" html
  }

  testTask "renderSessionStatus warming" {
    let html = renderSessionStatus "WarmingUp" "session-def" "/home/user/project" "" "REPL" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_warming" html
  }

  testTask "renderEvalStats" {
    let html = renderEvalStats { Count = 42; AvgMs = 123.4; MinMs = 5.0; MaxMs = 1045.0; Sparkline = ""; P50Ms = None; P95Ms = None } |> renderNode
    do! verifyDashboard "dashboard_evalStats" html
  }

  testTask "renderOutput with mixed lines" {
    if not (SyntaxHighlight.isAvailable()) then
      Tests.skiptest "tree-sitter not available; snapshot was generated with syntax highlighting"
    let lines = [
      { Timestamp = Some "12:30:45"; Kind = ResultLine; Text = "val x: int = 42" }
      { Timestamp = Some "12:30:46"; Kind = ErrorLine; Text = "Type mismatch" }
      { Timestamp = None; Kind = InfoLine; Text = "Loading..." }
      { Timestamp = Some "12:30:47"; Kind = SystemLine; Text = "Hot reload" }
    ]
    let html = renderOutput lines "No output yet" |> renderNode
    do! verifyDashboard "dashboard_output_mixed" html
  }

  testTask "renderOutput empty" {
    let html = renderOutput [] "No output yet" |> renderNode
    do! verifyDashboard "dashboard_output_empty" html
  }

  // roast UX-5: a realistic Expecto failure — the assertion line, two
  // contiguous framework frames, the user's own Stats.fs frame, then one
  // more framework frame. Expect the two framework runs each folded into
  // their own collapsed `<details>` group, and the assertion + Stats.fs
  // frame promoted with `output-line-promoted`. All lines are ErrorLine so
  // this snapshot is stable regardless of tree-sitter availability.
  testTask "renderOutput folds framework frames and promotes the assertion + user frame (roast UX-5)" {
    let lines = [
      { Timestamp = Some "12:30:45"; Kind = ErrorLine; Text = "Actual value was 6.0 but had expected it to be 4.0." }
      { Timestamp = None; Kind = ErrorLine; Text = "   at Expecto.Expect.equal[T](String message, T expected, T actual) in /_/src/Expecto/Expect.fs:line 205" }
      { Timestamp = None; Kind = ErrorLine; Text = "   at Microsoft.FSharp.Control.AsyncPrimitives.CallThenInvoke[T](...) in /_/src/FSharp.Core/async.fs:line 509" }
      { Timestamp = None; Kind = ErrorLine; Text = "   at RuntimeBugs.StatsTests.testMean() in /home/will/proj/Stats.fs:line 12" }
      { Timestamp = None; Kind = ErrorLine; Text = "   at <StartupCode$FSI_0007>.$FSI_0007.main@() in FSI_0007.fsx:line 3" }
    ]
    let html = renderOutput lines "No output yet" |> renderNode
    do! verifyDashboard "dashboard_output_foldedFrames" html
  }

  testTask "renderDiagnostics with errors and warnings" {
    let diags = [
      { Severity = DiagError; Message = "Type mismatch"; Line = 5; Col = 10 }
      { Severity = DiagWarning; Message = "Unused binding"; Line = 1; Col = 1 }
    ]
    let html = renderDiagnostics diags |> renderNode
    do! verifyDashboard "dashboard_diagnostics" html
  }

  testTask "renderDiagnostics empty" {
    let html = renderDiagnostics [] |> renderNode
    do! verifyDashboard "dashboard_diagnostics_empty" html
  }

  testTask "renderSessions with active and inactive" {
    let sessions : ParsedSession list = [
      { Id = WorkerProtocol.SessionId.validate "0a2b3c4d" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
        Status = SessionDisplayStatus.Running
        StatusMessage = None
        ProjectsText = "(MyProj.fsproj, Tests.fsproj)"
        EvalCount = 15
        Uptime = "3m"
        WorkingDir = @"C:\Code\MyProj"
        LastActivity = "eval"
        TestSummary = None
        CoverageSummary = None
        TestTreemapEntries = [||]; CoverageTreemap = None; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
        ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning; WorkerRssBytes = None; SelfHostStaleness = None }
      { Id = WorkerProtocol.SessionId.validate "0a2b3c4e" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
        Status = SessionDisplayStatus.Stopped
        StatusMessage = None
        ProjectsText = ""
        EvalCount = 0
        Uptime = ""
        WorkingDir = ""
        LastActivity = ""
        TestSummary = None
        CoverageSummary = None
        TestTreemapEntries = [||]; CoverageTreemap = None; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
        ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning; WorkerRssBytes = None; SelfHostStaleness = None }
    ]
    let html = renderSessionsForSession "0a2b3c4d" sessions false |> renderNode
    do! verifyDashboard "dashboard_sessions" html
  }

  testTask "renderSessions empty" {
    let html = renderSessionsForSession "" [] false |> renderNode
    do! verifyDashboard "dashboard_sessions_empty" html
  }

  testTask "renderDiscoveredProjects with results" {
    let discovered : DiscoveredProjects = {
      WorkingDir = @"C:\Code\MyProj"
      Solutions = [ "MyProj.sln" ]
      Projects = [ "MyProj.fsproj"; "Tests.fsproj" ]
    }
    let html = renderDiscoveredProjects discovered |> renderNode
    do! verifyDashboard "dashboard_discoveredProjects" html
  }

  testTask "renderDiscoveredProjectsWithConfig shows auto-open opt-out note" {
    let discovered : DiscoveredProjects = {
      WorkingDir = @"C:\Code\MyProj"
      Solutions = [ "MyProj.sln" ]
      Projects = [ "MyProj.fsproj"; "Tests.fsproj" ]
    }

    let dirConfig = {
      DirectoryConfig.empty with
        AutoOpenNamespaces = false
    }

    let html = renderDiscoveredProjectsWithConfig (Some dirConfig) discovered |> renderNode
    (html.Contains ".SageFs/config.fsx") |> Expect.isTrue "should mention the config path"
    (html.Contains "warmup auto-open disabled") |> Expect.isTrue "should mention the warmup auto-open opt-out"
  }

  testTask "renderDiscoveredProjects empty" {
    let discovered : DiscoveredProjects = {
      WorkingDir = @"C:\Code\Empty"
      Solutions = []
      Projects = []
    }
    let html = renderDiscoveredProjects discovered |> renderNode
    do! verifyDashboard "dashboard_discoveredProjects_empty" html
  }
]

let liveTestingVisibilityTests = testList "live testing visibility" [

  let tally (passed: int) (failed: int) =
    { SageFs.Features.LiveTestActivity.TestTally.empty with Passed = passed; Failed = failed }

  let mkQueries (activity: SageFs.Features.LiveTestActivity.LiveTestActivity) : DashboardQueries =
    {
      GetSessionState = fun _ -> SessionState.Ready
      GetStatusMsg = fun _ -> None
      GetEvalStats = fun _ -> System.Threading.Tasks.Task.FromResult(SageFs.Affordances.EvalStats.empty)
      GetFrictionStore = fun () -> System.Threading.Tasks.Task.FromResult None
      GetSessionWorkingDir = fun _ -> @"C:\Code\Repos\SageFs"
      GetElmRegionsForSession = fun _ -> None
      GetPreviousSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
      GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
      GetHotReloadState = fun _ -> System.Threading.Tasks.Task.FromResult None
      GetWarmupContext = fun _ -> System.Threading.Tasks.Task.FromResult None
      GetWarmupProgress = fun _ -> ""
      GetSessionTestSummary = fun _ -> None
      GetSessionCoverageSummary = fun _ -> None
      GetSessionTestTreemap = fun _ -> [||]
      GetSessionCoverageTreemap = fun _ -> None
      GetSessionBindings = fun _ -> [||]
      GetLiveBindings = fun _ -> None
      GetBindingScopeSnapshot = fun () -> None
      GetLiveTestingStatus = fun () -> ""
      GetLiveTestingActive = fun () -> activity <> SageFs.Features.LiveTestActivity.LiveTestActivity.Off
      GetLiveTestActivity = fun _ -> activity
      GetEvalTimeline =
        fun () -> SageFs.Features.EvalTimeline.TimelineState.empty |> SageFs.Features.EvalTimeline.timelineStats 20
      GetDaemonHealth = fun () -> None
      GetFailureNarratives = fun () -> []
      GetCurrentDiagnostics = fun () -> []
      GetFilmstripEntries = fun () -> []
      GetTestSourceLocations = fun () -> []
      GetSessionAgentBadges = fun _ -> []
      GetSessionGuidanceCss = fun _ -> ""
      GetSessionSelfHostStaleness = fun _ -> None
      GetSessionWorkflow = fun _ -> WorkflowTypes.SessionWorkflow.Interactive
      GetSessionActiveProject = fun _ -> None
      GetSessionProjectRoles = fun _ -> []
      GetSessionApp = fun _ -> SageFs.AppRun.AppRunState.NotRunning
      GetSessionEvalCounts = fun () -> Map.empty
      IsCreatingSession = fun () -> false
    }

  let mkInfra () : DashboardInfra =
    {
      Version = "0.0.0"
      McpPort = 37749
      // Non-optional (roast-6 Phase 0 item 1): these tests never exercise the
      // SSE stream, so a never-firing event is a faithful "no push happens" stand-in.
      StateChanged = (Event<SseEvent>()).Publish
      ConnectionTracker = None
      SessionThemes = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
      GetCompletions = fun _ _ _ -> System.Threading.Tasks.Task.FromResult []
      GetSessionCount = fun () -> System.Threading.Tasks.Task.FromResult 0
      SystemAlarmBuffer = ref []
      TriggerStateChange = fun () -> ()
      ConnectionChannels = System.Collections.Concurrent.ConcurrentDictionary<string, MailboxProcessor<DashboardStreamCommand>>()
      ActivityTracker = None
      LiveBindingsAdaptive = None
      ReadCohortFrame = fun () -> SageFs.Cohort.project (SageFs.Cohort.replayHead []) [||]
      ReadCohortLedger = fun () -> []
    }

  let panelFor (activity: SageFs.Features.LiveTestActivity.LiveTestActivity) = task {
    let! snap, _, _, _ =
      buildDashboardSnapshot (mkQueries activity) (mkInfra ()) (WorkerProtocol.SessionId.validate "session-1" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())) (WorkerProtocol.SessionId.newId ()) "" "default" None
    return snap.LiveTestingPanel |> renderNode
  }

  testTask "buildDashboardSnapshot carries rebuilding status into the live testing panel" {
    let! html = panelFor (SageFs.Features.LiveTestActivity.LiveTestActivity.Rebuilding (2, tally 3 0))
    html |> Expect.stringContains "dashboard should show live testing as active" "Live Testing: ON"
    html |> Expect.stringContains "dashboard should tell users that tests are waiting on the build" "Rebuilding to re-run 2 tests"
  }

  testTask "WHY — a discovery that found nothing says so instead of Discovering forever" {
    let! html = panelFor (SageFs.Features.LiveTestActivity.LiveTestActivity.NoTestsFound [ "Expecto" ])
    html |> Expect.stringContains "the panel must say discovery finished empty" "No tests found (Expecto detected)"
    (html.Contains "Discovering") |> Expect.isFalse "a finished discovery must not read as still discovering"
  }

  testTask "WHY — a failed discovery shows its reason because a spinner that never ends explains nothing" {
    let! html = panelFor (SageFs.Features.LiveTestActivity.LiveTestActivity.DiscoveryFailed "could not load Tests.dll")
    html |> Expect.stringContains "the panel must give the reason" "Could not discover tests: could not load Tests.dll"
  }

  testTask "WHY — the enable toggle posts through Datastar and shows it is turning on because a silent click reads as broken" {
    let! html = panelFor SageFs.Features.LiveTestActivity.LiveTestActivity.Off
    html |> Expect.stringContains "enable must post to the dashboard route" "/dashboard/live-testing/enable"
    html |> Expect.stringContains "enable must bind the in-flight indicator" "liveTestingLoading"
    html |> Expect.stringContains "enable must say it is turning on while in flight" "Turning on"
    (html.Contains "/api/dispatch") |> Expect.isFalse "the toggle must not be a fire-and-forget fetch"
  }

  testTask "WHY — the live-testing toggle route dispatches the change and pushes because the panel only shows what the model holds" {
    let dispatched = ResizeArray<SageFsMsg>()
    let mutable pushes = 0
    let ctx = Microsoft.AspNetCore.Http.DefaultHttpContext()
    ctx.Response.Body <- new IO.MemoryStream()
    do! createLiveTestingToggleHandler dispatched.Add (fun () -> pushes <- pushes + 1) SageFsMsg.EnableLiveTesting ctx
    (dispatched |> Seq.exists (function SageFsMsg.EnableLiveTesting -> true | _ -> false)) |> Expect.isTrue "the route dispatches EnableLiveTesting"
    pushes |> Expect.equal "the route triggers a dashboard push" 1
  }

  testTask "WHY — the disable toggle posts through Datastar and shows it is turning off" {
    let! html = panelFor (SageFs.Features.LiveTestActivity.LiveTestActivity.Settled (tally 1 0))
    html |> Expect.stringContains "disable must post to the dashboard route" "/dashboard/live-testing/disable"
    html |> Expect.stringContains "disable must say it is turning off while in flight" "Turning off"
  }

  // ─── TDD improvement: OFF state must communicate the cost ───────────────────
  // WHY — when live testing is off, the dashboard currently says only
  // "Enable to start discovering and running tests automatically" which
  // describes the benefit. A user who enables without knowing the cost
  // (tests re-run on every keystroke and file save) will be surprised by
  // unexpected CPU usage or flapping test state. The hint must mention
  // "keystroke" so the cost is explicit.
  testTask "OFF state hint warns that tests run on every keystroke" {
    let! html = panelFor SageFs.Features.LiveTestActivity.LiveTestActivity.Off
    // The "off" hint must mention the cost: keystrokes drive test re-runs.
    html |> Expect.stringContains "OFF hint must mention that tests run on every keystroke" "keystroke"
  }

  // ─── TDD improvement: passed/failed counts must be color-coded ──────────────
  // WHY — when the live testing header shows "5 passed / 2 failed", a user
  // scanning the dashboard cannot tell the colors apart because the count
  // is in a single inline string. A single failed test buried in green
  // can be missed. Passed must be green (--fg-green), failed must be red
  // (--fg-red) so the eyes latch onto the failing count.
  testTask "ON state shows passed count in green and failed count in red" {
    let! html = panelFor (SageFs.Features.LiveTestActivity.LiveTestActivity.Settled (tally 5 2))
    html |> Expect.stringContains "passed count must use green color" "--fg-green"
    html |> Expect.stringContains "failed count must use red color" "--fg-red"
  }

  // ─── Item 4: the worker-data cache must skip the three expensive fetches ──
  testTask "cached worker data skips eval-stats/hot-reload/warmup fetches" {
    let sid = WorkerProtocol.SessionId.validate "session-1" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let mutable evalFetches = 0
    let mutable hrFetches = 0
    let mutable wCtxFetches = 0
    let queries =
      { mkQueries (SageFs.Features.LiveTestActivity.LiveTestActivity.Settled (tally 0 0)) with
          GetEvalStats = fun _ ->
            evalFetches <- evalFetches + 1
            System.Threading.Tasks.Task.FromResult SageFs.Affordances.EvalStats.empty
          GetHotReloadState = fun _ ->
            hrFetches <- hrFetches + 1
            System.Threading.Tasks.Task.FromResult None
          GetWarmupContext = fun _ ->
            wCtxFetches <- wCtxFetches + 1
            System.Threading.Tasks.Task.FromResult None }
    let cache : DashboardWorkerCache = {
      SessionId = sid
      EvalStats = SageFs.Affordances.EvalStats.empty
      HotReloadState = None
      WarmupContext = None
      FrictionPanel = None
    }
    // Uncached: all three fetches run.
    let! _, _, _, _ =
      buildDashboardSnapshot queries (mkInfra ()) sid (WorkerProtocol.SessionId.newId ()) "" "default" None
    evalFetches |> Expect.equal "uncached push fetches eval stats" 1
    hrFetches |> Expect.equal "uncached push fetches hot-reload state" 1
    wCtxFetches |> Expect.equal "uncached push fetches warmup context" 1
    // Cached (same session): all three fetches are skipped.
    let! _, _, _, _ =
      buildDashboardSnapshot queries (mkInfra ()) sid (WorkerProtocol.SessionId.newId ()) "" "default" (Some cache)
    evalFetches |> Expect.equal "cached push must not re-fetch eval stats" 1
    hrFetches |> Expect.equal "cached push must not re-fetch hot-reload state" 1
    wCtxFetches |> Expect.equal "cached push must not re-fetch warmup context" 1
  }
]

// Hostile-string escaping: FSI output is agent-controlled text. Every FSI-
// derived render sink must escape HTML so a hostile eval result can never
// inject markup or script into the dashboard. (Roast-2 item 1.)
let hostileEscapingTests = testList "FSI-derived hostile-string escaping" [
  let hostile = "x\"><script>alert(1)</script>"

  testTask "renderBindingsPanel escapes hostile binding name, type sig, and value" {
    let b : SageFs.Features.BindingExplorer.BindingInfo = {
      Name = hostile
      TypeSig = hostile
      Value = Some hostile
      CellIndex = 1
      ShadowedBy = []
      ReferencedIn = []
    }
    let snapshot : SageFs.Features.BindingExplorer.BindingScopeSnapshot = {
      Bindings = [ b ]
      ActiveBindings = Map.ofList [ hostile, b ]
      ShadowedBindings = []
    }
    let html = renderBindingsPanel (Some snapshot) |> renderNode
    (html.Contains("<script>alert(1)</script>")) |> Expect.isFalse "active-binding fields must not contain raw script"
    (html.Contains("<script>alert(1)</script>")) |> Expect.isFalse "hostile payload must be escaped everywhere"
    (html.Contains("&lt;script&gt;")) |> Expect.isTrue "encoded payload must appear in the HTML"
  }

  testTask "renderSessionContextPanel escapes hostile failed-open error message" {
    let ctx : SageFs.SessionContext = {
      SessionId = "session-1"
      ProjectNames = [ "P.fsproj" ]
      WorkingDir = @"C:\Code\Proj"
      Status = "Ready"
      Warmup = {
        SourceFilesScanned = 1
        AssembliesLoaded = []
        NamespacesOpened = []
        FailedOpens = [
          { Name = hostile
            Kind = SageFs.WarmUp.OpenableKind.Namespace
            ErrorMessage = hostile
            Diagnostics = []
            RetryCount = 1
            DurationMs = 1.0 }
        ]
        PhaseTiming = {
          ScanSourceFilesMs = 0L
          ScanAssembliesMs = 0L
          OpenNamespacesMs = 0L
          TotalMs = 0L
        }
        StartedAt = DateTimeOffset.MinValue
      }
      FileStatuses = []
      Workflow = SageFs.WorkflowTypes.SessionWorkflow.Interactive
      AutoOpenNamespaces = false
    }
    let html = renderSessionContextPanel ctx |> renderNode
    (html.Contains("<script>alert(1)</script>")) |> Expect.isFalse "failed-open message must not contain raw script"
    (html.Contains("&lt;script&gt;")) |> Expect.isTrue "encoded payload must appear in the HTML"
  }
]

let keyboardHelpSnapshotTests = testList "keyboard help snapshots" [
  testTask "renderKeyboardHelp" {
    let html = renderKeyboardHelp () |> renderNode
    do! verifyDashboard "dashboard_keyboardHelp" html
  }
]

let edgeCaseSnapshotTests = testList "edge case snapshots" [
  testTask "renderSessions single active session" {
    let sessions : ParsedSession list = [
      { Id = WorkerProtocol.SessionId.validate "0a2b3c4d" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
        Status = SessionDisplayStatus.Running
        StatusMessage = None
        ProjectsText = "(MyProj.fsproj)"
        EvalCount = 42
        Uptime = "15m"
        WorkingDir = @"C:\Code\MyProj"
        LastActivity = "eval"
        TestSummary = None
        CoverageSummary = None
        TestTreemapEntries = [||]; CoverageTreemap = None; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
        ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning; WorkerRssBytes = None; SelfHostStaleness = None }
    ]
    let html = renderSessionsForSession "0a2b3c4d" sessions false |> renderNode
    do! verifyDashboard "dashboard_sessions_singleActive" html
  }
  testTask "renderDiagnostics with zero line col" {
    let diags = [
      { Severity = DiagError; Message = "General compilation error"; Line = 0; Col = 0 }
    ]
    let html = renderDiagnostics diags |> renderNode
    do! verifyDashboard "dashboard_diagnostics_zeroLineCol" html
  }

  testTask "renderEvalStats zero evals" {
    let html = renderEvalStats { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None } |> renderNode
    do! verifyDashboard "dashboard_evalStats_zero" html
  }

  testTask "renderSessionStatus faulted" {
    let html = renderSessionStatus "Faulted" "session-err" @"C:\broken" "" "REPL" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_faulted" html
  }

  testTask "renderSessionStatus warming with progress" {
    let html = renderSessionStatus "WarmingUp" "session-warm" "/home/user/project" "2/4 Scanned 12 source files" "REPL" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_warmingWithProgress" html
  }

  testTask "renderOutput single result line" {
    if not (SyntaxHighlight.isAvailable()) then
      Tests.skiptest "tree-sitter not available; snapshot was generated with syntax highlighting"
    let lines = [ { Timestamp = Some "14:00:00"; Kind = ResultLine; Text = "val it: int = 0" } ]
    let html = renderOutput lines "No output yet" |> renderNode
    do! verifyDashboard "dashboard_output_singleResult" html
  }
]

let parserTests = testList "parser integration" [
  test "output parser extracts timestamp and kind" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^\[(\d{2}:\d{2}:\d{2})\]\s*\[(\w+)\]\s*(.*)",
      System.Text.RegularExpressions.RegexOptions.Singleline)
    let m = regex.Match("[12:30:45] [result] val x: int = 42")
    m.Success |> Expect.isTrue "should match timestamp+kind format"
    m.Groups.[1].Value |> Expect.equal "timestamp" "12:30:45"
    m.Groups.[2].Value |> Expect.equal "kind" "result"
    m.Groups.[3].Value |> Expect.equal "content" "val x: int = 42"
  }

  test "output parser handles kind without timestamp" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^\[(\w+)\]\s*(.*)",
      System.Text.RegularExpressions.RegexOptions.Singleline)
    let m = regex.Match("[error] Something went wrong")
    m.Success |> Expect.isTrue "should match kind-only format"
    m.Groups.[1].Value |> Expect.equal "kind" "error"
    m.Groups.[2].Value |> Expect.equal "content" "Something went wrong"
  }

  test "diag parser extracts severity line col" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
    let m = regex.Match("[error] (5,10) Type mismatch")
    m.Success |> Expect.isTrue "should match diag format"
    (int m.Groups.[2].Value) |> Expect.equal "line" 5
    (int m.Groups.[3].Value) |> Expect.equal "col" 10
    m.Groups.[4].Value |> Expect.equal "message" "Type mismatch"
  }

  test "diag parser fallback for non-standard format" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
    let m = regex.Match("Some general error")
    m.Success |> Expect.isFalse "should not match non-standard format"
  }

  test "session parser extracts id status active" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^(\S+)\s+\[(\w+)\]\s*(\*?)\s*(\([^)]*\))?\s*(evals:\d+)?\s*(.*)")
    let m = regex.Match("session-abc [running] * (Proj.fsproj) evals:5 up:3m")
    m.Success |> Expect.isTrue "should match session format"
    m.Groups.[1].Value |> Expect.equal "session id" "session-abc"
    m.Groups.[2].Value |> Expect.equal "status" "running"
    m.Groups.[3].Value |> Expect.stringContains "active marker" "*"
  }
]

let mkRegion id content = {
  Id = id; Flags = RegionFlags.None; Content = content
  Affordances = []; Cursor = None; Completions = None
  LineAnnotations = [||]
}

let shellStructureTests = testList "shell structure (replaces browser existence checks)" [
  testTask "renderShell snapshot" {
    let html = renderShell "0.0.0-test" "test-id" "" "" (Elem.div [] []) |> renderNode
    do! verifyDashboard "dashboard_shell" html
  }

  test "shell has SageFs title" {
    let html = renderShell "1.2.3" "test-id" "" "" (Elem.div [] []) |> renderNode
    html |> Expect.stringContains "shell has SageFs title" "SageFs"
  }

  // A localhost tool must not reach out to third parties: no script, style,
  // font or preconnect from an external origin (fonts used to come from
  // fonts.googleapis.com).
  test "shell loads nothing from an external origin" {
    let html = renderShell "1.2.3" "test-id" "" "" (Elem.div [] []) |> renderNode
    let externalLoads =
      System.Text.RegularExpressions.Regex.Matches(
        html, @"(?:src|href)\s*=\s*""https?://|url\(\s*['""]?https?://|@import")
    externalLoads.Count |> Expect.equal "shell must not load anything from an external origin" 0
  }

  test "every @font-face the shell declares is an embedded woff2 served by the daemon" {
    let html = renderShell "1.2.3" "test-id" "" "" (Elem.div [] []) |> renderNode
    let declared =
      System.Text.RegularExpressions.Regex.Matches(html, @"url\('/dashboard/fonts/([^']+)'\)")
      |> Seq.map (fun m -> m.Groups.[1].Value)
      |> List.ofSeq
    declared.Length |> Expect.equal "the four weights the CSS uses (400/500/600/700) are declared" 4
    for file in declared do
      match Map.tryFind file dashboardFonts with
      | Some bytes ->
        (System.Text.Encoding.ASCII.GetString(bytes, 0, 4)) |> Expect.equal (sprintf "%s is a woff2 font" file) "wOF2"
      | None -> failtestf "%s is declared by the shell but not embedded" file
    html |> Expect.stringContains "faces use font-display: swap so text never blocks on the font" "font-display:swap"
    html |> Expect.stringContains "a full system monospace fallback stack follows the face" "ui-monospace,monospace"
    (dashboardFonts.ContainsKey "JetBrainsMono-OFL.txt") |> Expect.isTrue "the SIL OFL license text ships with the font"
  }

  // Full-page morph: dynamic elements live in renderMainContent, not renderShell.
  // These tests verify the morphed content includes key interactive elements.
  let mkSnap version = {
    DashboardSnapshot.Version = version
    SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
    WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
    ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
    HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
    OutputPanel = Elem.div [] []
    SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
    ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
    BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] []; CohortPanel = Elem.div [] []
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning
    EvalToPixelP50Ms = None
    EvalToPixelP99Ms = None }

  test "renderMainContent shows version" {
    // Version lives in the daemon health bar ("SageFs <version>"), not in a
    // separate statusline/cmdline band (those are gone). A realistic snapshot
    // carries a health node, so mkSnap's empty stub is replaced here.
    let health =
      renderDaemonHealth
        { Version = "1.2.3"; MemoryMB = 0; UptimeLabel = "0s"
          OverallHealth = Features.OverallHealth.Healthy
          SessionCount = 0; SessionSummaries = []; TestsPassed = None; TestsFailed = None }
    let html = renderMainContent { mkSnap "1.2.3" with DaemonHealth = health } |> renderNode
    html |> Expect.stringContains "main content shows the version in the health bar" "1.2.3"
  }

  test "WHY — connection monitor script is valid JavaScript because a syntax error can disable dashboard stream diagnostics" {
    let html = connectionMonitorScript () |> renderNode
    (html.Contains(";\n        .catch")) |> Expect.isFalse "promise catch must remain chained to then"
    html |> Expect.stringContains "monitor must handle stream failures" ".catch(function()"
  }

  test "WHY — selected-session projection — main, selected card, and output share one session identity because separate identities can display another session's output" {
    let sessionA = WorkerProtocol.SessionId.validate "0a2b3c4d" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let sessionB = WorkerProtocol.SessionId.validate "0a2b3c4e" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let sessions = [
      { Id = sessionA; Status = SessionDisplayStatus.Running; StatusMessage = None
        ProjectsText = "(A.fsproj)"; EvalCount = 1
        Uptime = "1m"; WorkingDir = "/a"; LastActivity = "A"
        TestSummary = None; CoverageSummary = None; TestTreemapEntries = [||]; CoverageTreemap = None
        BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
        ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning; WorkerRssBytes = None; SelfHostStaleness = None }
      { Id = sessionB; Status = SessionDisplayStatus.Running; StatusMessage = None
        ProjectsText = "(B.fsproj)"; EvalCount = 1
        Uptime = "1m"; WorkingDir = "/b"; LastActivity = "B"
        TestSummary = None; CoverageSummary = None; TestTreemapEntries = [||]; CoverageTreemap = None
        BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
        ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning; WorkerRssBytes = None; SelfHostStaleness = None }
    ]
    let snap =
      { mkSnap "0.0.0" with
          SessionId = "0a2b3c4e"
          OutputPanel = renderOutputForSession "0a2b3c4e" [ { Timestamp = None; Kind = ResultLine; Text = "SESSIONB" } ] "No output yet"
          SessionsPanel = renderSessionsForSession "0a2b3c4e" sessions false }
    let html = renderMainContent snap |> renderNode
    html |> Expect.stringContains "main must declare the one viewing identity" "data-viewing-session-id=\"0a2b3c4e\""
    html |> Expect.stringContains "output must carry the same identity" "data-session-id=\"0a2b3c4e\""
    html |> Expect.stringContains "selected session card must be explicit in the same morph" "aria-current=\"true\""
    html |> Expect.stringContains "the selected session's output panel must be in the same morph" "id=\"output-panel\""
    (html.Contains "data-session-id=\"0a2b3c4d\" aria-current=\"true\"") |> Expect.isFalse "another session must not be selected"
  }

  test "WHY — stream reconnect identity — requested session wins over list order because reconnecting to the first session would desynchronize highlight and output" {
    let sessionA = WorkerProtocol.SessionId.validate "0a2b3c4d" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let sessionB = WorkerProtocol.SessionId.validate "0a2b3c4e" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let info sessionId project : WorkerProtocol.SessionInfo =
      { Id = sessionId
        Name = None
        Projects = [ project ]
        WorkingDirectory = "/tmp"
        SolutionRoot = None
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        ActiveProject = None
        ProjectRoles = []
        App = SageFs.AppRun.AppRunState.NotRunning }
    let resolved = resolveViewingSession (Some "0a2b3c4e") [ info sessionA "A.fsproj"; info sessionB "B.fsproj" ]
    resolved |> Expect.equal "stream must retain the browser-requested session" (Some sessionB)
  }

  test "evaluate section has textarea with placeholder" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    html |> Expect.stringContains "has eval-input class" "eval-input"
    html |> Expect.stringContains "placeholder mentions F#" "F# code"
  }

  test "WHY — evaluator has no session-bound hidden input because an empty input overwrites the selected-session signal" {
    let mainHtml = renderMainContent (mkSnap "0.0.0") |> renderNode
    let shellHtml = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    shellHtml |> Expect.stringContains "shell must own the selected-session signal" "viewing-session-id"
    (mainHtml.Contains "data-bind:viewing-session-id") |> Expect.isFalse "hidden input must not overwrite the selected-session signal"
    (mainHtml.Contains "data-bind:session-id") |> Expect.isFalse "no second session identity may exist"
  }

  test "WHY — main snapshot embeds selected output because an action response must atomically deliver its committed evaluation" {
    let snap =
      { mkSnap "0.0.0" with
          SessionId = "0a2b3c4e"
          OutputPanel = renderOutputForSession "0a2b3c4e" [ { Timestamp = None; Kind = ResultLine; Text = "val dashboardProbe: int = 8967" } ] "No output" }
    let html = renderMainContent snap |> renderNode
    html |> Expect.stringContains "action morph must retain selected identity" "data-viewing-session-id=\"0a2b3c4e\""
    html |> Expect.stringContains "action morph output must use selected identity" "data-session-id=\"0a2b3c4e\""
    html |> Expect.stringContains "action morph must contain the committed result" "8967"
  }

  test "eval button is present" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    html |> Expect.stringContains "has Eval button" "Eval"
  }

  test "reset and hard reset buttons are present" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    html |> Expect.stringContains "has [RESET] button" "[RESET]"
    html |> Expect.stringContains "has [HARD_RESET] button" "[HARD_RESET]"
  }

  test "clear output button in panel header" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    html |> Expect.stringContains "has Clear button" "Clear"
  }

  test "create session section has all inputs" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    html |> Expect.stringContains "has Discover button" "Discover"
    html |> Expect.stringContains "has fsproj placeholder" "fsproj"
    html |> Expect.stringContains "has Create Session button" "Create"
    html |> Expect.stringContains "new session section is a collapsible details" "New Session"
  }

  test "server-status banner has no data-show attribute" {
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    let bannerStart = html.IndexOf("id=\"server-status\"")
    (bannerStart > -1) |> Expect.isTrue "server-status exists"
    let tagEnd = html.IndexOf(">", bannerStart)
    let tag = html.Substring(bannerStart, tagEnd - bannerStart)
    (tag.Contains("data-show")) |> Expect.isFalse "banner must not use data-show"
  }

  // ── Minimal mode (Task 1) ──────────────────────────────────────
  test "renderShell has expandedDashboard signal" {
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    // Datastar renders signal names as kebab-case in attributes (expandedDashboard → expanded-dashboard)
    html |> Expect.stringContains "shell has expanded-dashboard signal attribute" "expanded-dashboard"
  }

  test "renderMainContent has expanded-only sections" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    html |> Expect.stringContains "main content has expanded-only class" "expanded-only"
  }

  test "renderMainContent has expand toggle button" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    html |> Expect.stringContains "has expand toggle onclick" "expandedDashboard = !$expandedDashboard"
  }

  test "renderMainContent has the expand toggle on the Sessions panel (not by app settings)" {
    // The 'more session info' toggle moved from the app-header (by settings) to
    // the Sessions panel header; it keeps the stable id so it stays targetable.
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    html |> Expect.stringContains "expand toggle present by id" "id=\"expand-toggle-btn\""
    html |> Expect.stringContains "expand toggle toggles the expandedDashboard signal" "expandedDashboard"
  }

  // ── SSE full-state push on connect (Task 2) ───────────────────
  test "SSE full-state push: shell connects to stream endpoint" {
    // createStreamHandler calls pushState() immediately on connect (initial pushState in try/catch).
    // This test verifies the shell wires up the SSE stream that triggers the initial state push.
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    html |> Expect.stringContains "shell connects to SSE stream endpoint for initial push" "/dashboard/stream"
  }
]

// ── Zero-JS badge tests ──

let zeroJsBadgeTests = testList "Zero-JS badge" [
  test "shell contains no framework JS (React/Vue/Angular/Svelte)" {
    let html = renderShell "1.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    let frameworks = [ "react"; "vue"; "angular"; "svelte"; "jquery"; "alpine" ]
    for fw in frameworks do
      (html.ToLowerInvariant().Contains fw) |> Expect.isFalse (sprintf "should not contain %s framework reference" fw)
  }

  test "shell contains only Datastar CDN script as external JS" {
    let html = renderShell "1.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    let srcPattern = System.Text.RegularExpressions.Regex("src=\"([^\"]+)\"")
    let scriptSrcs = srcPattern.Matches(html)
    let jsSources =
      [ for m in scriptSrcs -> m.Groups.[1].Value ]
      |> List.filter (fun s -> s.EndsWith(".js") || s.Contains("datastar"))
    match jsSources.Length with
    | 0 -> () // CDN might be inline
    | _ ->
      (jsSources |> List.forall (fun s -> s.Contains "datastar")) |> Expect.isTrue (sprintf "all external JS should be Datastar only, found: %A" jsSources)
  }

  test "inline scripts are utility-only, not application logic" {
    let html = renderShell "1.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    let scriptPattern = System.Text.RegularExpressions.Regex("<script[^>]*>([\\s\\S]*?)</script>")
    let scriptBlocks = scriptPattern.Matches(html)
    let inlineScripts = [ for m in scriptBlocks -> m.Groups.[1].Value ]
    for script in inlineScripts do
      // No state management patterns
      (script.Contains "useState") |> Expect.isFalse "no React-style state"
      (script.Contains "createStore") |> Expect.isFalse "no Redux-style store"
      (script.Contains "createSignal") |> Expect.isFalse "no SolidJS-style signals"
      // No fetch for data retrieval (fetch for POST commands is OK)
      let fetchCount = script.Split("fetch(").Length - 1
      let postCount = script.Split("'POST'").Length + script.Split("\"POST\"").Length - 2
      (fetchCount <= postCount + 1) |> Expect.isTrue "fetch calls should be POST-only (command dispatch), not GET (data retrieval)"
  }

  test "total inline JS payload is under 5KB" {
    let html = renderShell "1.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    let scriptPattern = System.Text.RegularExpressions.Regex("<script[^>]*>([\\s\\S]*?)</script>")
    let scriptBlocks = scriptPattern.Matches(html)
    let totalBytes =
      [ for m in scriptBlocks -> m.Groups.[1].Value ]
      |> List.sumBy (fun s -> System.Text.Encoding.UTF8.GetByteCount(s))
    (totalBytes, 5120) |> Expect.isLessThan (sprintf "inline JS should be <5KB, was %d bytes" totalBytes)
  }

  test "no application-level JS event handlers in HTML attributes" {
    let html = renderShell "1.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    // onclick/onchange etc. should use Datastar data-on-* attributes, not raw HTML
    (html.Contains " onclick=") |> Expect.isFalse "should not use raw onclick (use Ds.onClick)"
    (html.Contains " onchange=") |> Expect.isFalse "should not use raw onchange (use Ds.onEvent)"
    (html.Contains " onsubmit=") |> Expect.isFalse "should not use raw onsubmit"
  }
]

// ── Railway visualization tests ──

let railwayVisualizationTests = testList "Railway visualization" [

  testList "PipelineRailwayView.fromStages" [
    test "builds stages with StageSuccess outcomes" {
      let stages = [ ("Parse", 12.0); ("TypeCheck", 45.0); ("Execute", 363.0) ]
      let view = PipelineRailwayView.fromStages stages 420.0
      view.Stages.Length |> Expect.equal "should have 3 stages" 3
      view.TotalMs |> Expect.equal "total" 420.0
      (view.Stages |> List.forall (fun s ->
          match s.Outcome with StageSuccess -> true | _ -> false)) |> Expect.isTrue "all stages should be success"
    }

    test "empty stages list produces empty railway" {
      let view = PipelineRailwayView.fromStages [] 0.0
      view.Stages |> Expect.isEmpty "should be empty"
      view.TotalMs |> Expect.equal "total" 0.0
    }

    test "stage names are preserved" {
      let stages = [ ("Parse", 10.0); ("Execute", 20.0) ]
      let view = PipelineRailwayView.fromStages stages 30.0
      (view.Stages |> List.map (fun s -> s.Name)) |> Expect.equal "names" [ "Parse"; "Execute" ]
    }

    test "stage durations are preserved" {
      let stages = [ ("Parse", 12.5); ("TypeCheck", 45.7) ]
      let view = PipelineRailwayView.fromStages stages 58.2
      (view.Stages |> List.map (fun s -> s.DurationMs)) |> Expect.equal "durations" [ 12.5; 45.7 ]
    }
  ]

  testList "PipelineRailwayView.fromStagesWithFailure" [
    test "marks the failed stage with StageFailure" {
      let stages = [ ("Parse", 12.0); ("TypeCheck", 45.0); ("Execute", 0.0) ]
      let view = PipelineRailwayView.fromStagesWithFailure stages 57.0 "Execute" "Runtime error"
      match view.Stages |> List.tryFind (fun s -> s.Name = "Execute") with
      | Some s ->
        match s.Outcome with
        | StageFailure err -> err |> Expect.equal "error msg" "Runtime error"
        | StageSuccess -> failtest "Execute should be StageFailure"
      | None -> failtest "Execute stage missing"
    }

    test "non-failed stages remain StageSuccess" {
      let stages = [ ("Parse", 12.0); ("TypeCheck", 45.0); ("Execute", 0.0) ]
      let view = PipelineRailwayView.fromStagesWithFailure stages 57.0 "Execute" "err"
      match view.Stages |> List.tryFind (fun s -> s.Name = "Parse") with
      | Some s ->
        match s.Outcome with
        | StageSuccess -> ()
        | StageFailure _ -> failtest "Parse should be StageSuccess"
      | None -> failtest "Parse stage missing"
    }
  ]

  testList "renderRailway" [
    test "renders success stages with checkmarks" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0); ("TypeCheck", 45.0); ("Execute", 363.0) ] 420.0
      let html = renderRailway railway |> renderNode
      html |> Expect.stringContains "should show Parse checkmark" "Parse ✓"
      html |> Expect.stringContains "should show TypeCheck checkmark" "TypeCheck ✓"
      html |> Expect.stringContains "should show Execute checkmark" "Execute ✓"
    }

    test "renders stage durations in brackets" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0); ("Execute", 363.0) ] 375.0
      let html = renderRailway railway |> renderNode
      html |> Expect.stringContains "should show Parse duration" "[12ms]"
      html |> Expect.stringContains "should show Execute duration" "[363ms]"
    }

    test "renders arrows between stages" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0); ("Execute", 363.0) ] 375.0
      let html = renderRailway railway |> renderNode
      html |> Expect.stringContains "should have arrow separator" "→"
    }

    test "renders total duration" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0) ] 420.0
      let html = renderRailway railway |> renderNode
      html |> Expect.stringContains "should show total" "[420ms total]"
    }

    test "renders empty pipeline as 'No pipeline stages'" {
      let html = renderRailway PipelineRailwayView.empty |> renderNode
      html |> Expect.stringContains "should show empty message" "No pipeline stages"
    }

    test "renders failure stage with cross mark" {
      let railway = PipelineRailwayView.fromStagesWithFailure [ ("Parse", 12.0); ("Execute", 0.0) ] 12.0 "Execute" "boom"
      let html = renderRailway railway |> renderNode
      html |> Expect.stringContains "should show failure cross" "Execute ✗"
    }

    test "success stage has stage-success CSS class" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0) ] 12.0
      let html = renderRailway railway |> renderNode
      html |> Expect.stringContains "should have success class" "stage-success"
    }

    test "failure stage has stage-failure CSS class" {
      let railway = PipelineRailwayView.fromStagesWithFailure [ ("Parse", 0.0) ] 0.0 "Parse" "err"
      let html = renderRailway railway |> renderNode
      html |> Expect.stringContains "should have failure class" "stage-failure"
    }

    test "pipeline-railway CSS class on container" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 1.0) ] 1.0
      let html = renderRailway railway |> renderNode
      html |> Expect.stringContains "should have container class" "pipeline-railway"
    }

    test "single stage has no arrows" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 1.0) ] 1.0
      let html = renderRailway railway |> renderNode
      let arrowCount = html.Split("→").Length - 1
      arrowCount |> Expect.equal "should have no arrows for single stage" 0
    }

    test "N stages produce N-1 arrows" {
      let stages = [ ("A", 1.0); ("B", 2.0); ("C", 3.0); ("D", 4.0) ]
      let railway = PipelineRailwayView.fromStages stages 10.0
      let html = renderRailway railway |> renderNode
      let arrowCount = html.Split("→").Length - 1
      arrowCount |> Expect.equal "should have 3 arrows for 4 stages" 3
    }
  ]
]

// ── Test filter bar tests ──

let sampleTestEntries = [|
  { DisplayName = "test A"; FullName = "Ns.test A"; DurationMs = 100.0
    Status = Features.LiveTesting.TreemapStatus.Passed }
  { DisplayName = "test B"; FullName = "Ns.test B"; DurationMs = 200.0
    Status = Features.LiveTesting.TreemapStatus.Failed }
  { DisplayName = "test C"; FullName = "Ns.test C"; DurationMs = 50.0
    Status = Features.LiveTesting.TreemapStatus.Passed }
  { DisplayName = "test D"; FullName = "Ns.test D"; DurationMs = 10.0
    Status = Features.LiveTesting.TreemapStatus.Running }
  { DisplayName = "test E"; FullName = "Ns.test E"; DurationMs = 5.0
    Status = Features.LiveTesting.TreemapStatus.Skipped }
|]

let testFilterTests = testList "Test filter bar" [

  testList "treemapStatusToFilterValue" [
    test "Passed maps to 'passed'" {
      (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Passed) |> Expect.equal "Passed" "passed"
    }
    test "Failed maps to 'failed'" {
      (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Failed) |> Expect.equal "Failed" "failed"
    }
    test "Running maps to 'running'" {
      (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Running) |> Expect.equal "Running" "running"
    }
    test "Skipped maps to 'skipped'" {
      (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Skipped) |> Expect.equal "Skipped" "skipped"
    }
    test "Other maps to 'other'" {
      (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Other) |> Expect.equal "Other" "other"
    }
  ]

  testList "renderTestFilterBar" [
    test "renders filter bar container with test-filter-bar class" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should have container class" "test-filter-bar"
    }

    test "shows passed count" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should show 2 passed" "✓ 2"
    }

    test "shows failed count" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should show 1 failed" "✗ 1"
    }

    test "shows running button when running tests exist" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should show 1 running" "⟳ 1"
    }

    test "shows skipped button when skipped tests exist" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should show 1 skipped" "⊘ 1"
    }

    test "hides running button when no running tests" {
      let entries = sampleTestEntries |> Array.filter (fun e ->
        e.Status <> Features.LiveTesting.TreemapStatus.Running)
      let html = renderTestFilterBar entries |> renderNode
      (html.Contains "⟳") |> Expect.isFalse "should not show running button"
    }

    test "hides skipped button when no skipped tests" {
      let entries = sampleTestEntries |> Array.filter (fun e ->
        e.Status <> Features.LiveTesting.TreemapStatus.Skipped)
      let html = renderTestFilterBar entries |> renderNode
      (html.Contains "⊘") |> Expect.isFalse "should not show skipped button"
    }

    test "filter buttons use Datastar show expression" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should reference testFilter signal" "$testFilter"
    }

    test "click sets testFilter signal to status value" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should set filter to passed" "$testFilter = 'passed'"
      html |> Expect.stringContains "should set filter to failed" "$testFilter = 'failed'"
    }

    test "active button resets filter to all on click" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "active button should reset to all" "$testFilter = 'all'"
    }

    test "active button has test-filter-active class" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should have active class" "test-filter-active"
    }

    test "renders Filter label" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      html |> Expect.stringContains "should have Filter label" "Filter:"
    }
  ]

  testList "renderTestTreemap with signal filtering" [
    test "treemap entries have data-show attribute" {
      let html = renderTestTreemap sampleTestEntries |> renderNode
      html |> Expect.stringContains "should have data-show for filtering" "data-show"
    }

    test "passed entries show when filter is all or passed" {
      let html = renderTestTreemap sampleTestEntries |> renderNode
      html |> Expect.stringContains "should show for all or passed" "$testFilter === 'all' || $testFilter === 'passed'"
    }

    test "failed entries show when filter is all or failed" {
      let html = renderTestTreemap sampleTestEntries |> renderNode
      html |> Expect.stringContains "should show for all or failed" "$testFilter === 'all' || $testFilter === 'failed'"
    }
  ]

  testList "Signals module" [
    test "TestFilter signal name is defined" {
      Signals.TestFilter |> Expect.equal "should be testFilter" "testFilter"
    }
  ]
]

let datastarComplianceTests = testList "Datastar compliance (synthesis 5.4)" [

  test "shell initializes SSE stream via data-init" {
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    html |> Expect.stringContains "must have data-init for SSE" "data-init"
    html |> Expect.stringContains "must target stream endpoint" "/dashboard/stream"
  }

  test "shell loads Datastar CDN script" {
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    html |> Expect.stringContains "must include datastar CDN" "datastar"
  }

  test "all Signals are initialized in shell via data-signals" {
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    // Datastar renders signal names kebab-case: helpVisible → help-visible
    let expectedSignalAttrs =
      [ "data-signals:help-visible"; "data-signals:sidebar-open"; "data-signals:viewing-session-id"
        "data-signals:code"; "data-signals:new-session-dir"; "data-signals:manual-projects"
        "data-signals:theme"; "data-signals:cursor-pos"; "data-signals:test-filter" ]
    for attr in expectedSignalAttrs do
      html |> Expect.stringContains (sprintf "signal attr '%s' must be initialized" attr) attr
  }

  test "main div has correct DOM ID" {
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    html |> Expect.stringContains "must have main div" (sprintf "id=\"%s\"" DomIds.Main)
  }

  test "server-status div has correct DOM ID" {
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    html |> Expect.stringContains "must have server-status div" (sprintf "id=\"%s\"" DomIds.ServerStatus)
  }

  test "renderMainContent includes key DOM IDs" {
    let snap = {
      DashboardSnapshot.Version = "0.0.0"
      SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
      WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
      ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
      HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
      OutputPanel = Elem.div [] []
      SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
      ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
      BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] []; CohortPanel = Elem.div [] []
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning
      EvalToPixelP50Ms = None
      EvalToPixelP99Ms = None }
    let html = renderMainContent snap |> renderNode
    let mustHaveIds =
      [ DomIds.Main; DomIds.SessionStatus; DomIds.EvalStats
        DomIds.EditorArea; DomIds.EvaluateSection; DomIds.EvalTextarea ]
    for domId in mustHaveIds do
      html |> Expect.stringContains (sprintf "main content must have id='%s'" domId) (sprintf "id=\"%s\"" domId)
  }

  test "SSE format: events end with double newline" {
    let evt = SageFs.SseWriter.formatSseEvent "test" "data"
    (evt.Length > 0) |> Expect.isTrue "non-empty"
    (evt.EndsWith("\n\n")) |> Expect.isTrue "must end with \\n\\n"
  }

  test "SSE format: retry hint is spec-compliant" {
    let retry = SageFs.SseWriter.formatRetryHint 3000
    retry |> Expect.equal "retry format" "retry: 3000\n\n"
  }

  test "shell has no React/Vue/Angular framework references" {
    let html = renderShell "0.0.0" "test-id" "" "" (Elem.div [] []) |> renderNode
    let banned = [ "react"; "vue"; "angular"; "svelte"; "htmx"; "alpine" ]
    let lower = html.ToLowerInvariant()
    for framework in banned do
      (lower.Contains(framework)) |> Expect.isFalse (sprintf "must not reference %s" framework)
  }

  test "morph target: renderMainContent wraps in div#main" {
    let snap = {
      DashboardSnapshot.Version = "0.0.0"
      SessionState = "ready"; SessionId = "t"; WorkingDir = "C:\\"
      WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
      ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
      HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
      OutputPanel = Elem.div [] []
      SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
      ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
      BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] []; CohortPanel = Elem.div [] []
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning
      EvalToPixelP50Ms = None
      EvalToPixelP99Ms = None }
    let html = renderMainContent snap |> renderNode
    (html.StartsWith("<div id=\"main\"")) |> Expect.isTrue "must start with div#main"
  }
]

let snapshotCompletenessTests = testList "Snapshot field completeness (synthesis 3.1)" [

  let mkSnap version sessionId workingDir state =
    { DashboardSnapshot.Version = version
      SessionState = state; SessionId = sessionId; WorkingDir = workingDir
      WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = { Count = 7; AvgMs = 42.0; MinMs = 1.0; MaxMs = 100.0; Sparkline = ""; P50Ms = None; P95Ms = None }
      ThemeName = "monokai"; ConnectionLabel = Some "🌐 2 🤖 1"; ConnectionState = DashboardConnectionState.Connected
      HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
      OutputPanel = Elem.div [] []
      SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
      ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
      BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] []; CohortPanel = Elem.div [] []
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning
      EvalToPixelP50Ms = None
      EvalToPixelP99Ms = None }
  test "Version appears in rendered output" {
    // Version lives in the daemon health bar; a realistic snapshot carries one.
    let health =
      renderDaemonHealth
        { Version = "1.2.3"; MemoryMB = 0; UptimeLabel = "0s"
          OverallHealth = Features.OverallHealth.Healthy
          SessionCount = 0; SessionSummaries = []; TestsPassed = None; TestsFailed = None }
    let snap = { (mkSnap "1.2.3" "s1" "C:\\" "ready") with DaemonHealth = health }
    let html = renderMainContent snap |> renderNode
    html |> Expect.stringContains "version should appear" "1.2.3"
  }

  test "SessionId appears in rendered output" {
    let html = mkSnap "0.0.0" "my-session-42" "C:\\" "ready" |> renderMainContent |> renderNode
    html |> Expect.stringContains "sessionId should appear" "my-session-42"
  }

  test "WorkingDir appears on the session card" {
    // The working dir moved off the main-content chrome (it read as an obnoxious
    // boxed tab there) onto the session card — its one home. Field completeness
    // now means it surfaces on the card, not in renderMainContent's own markup.
    let sid = WorkerProtocol.SessionId.validate "0a2b3c4d" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let s : ParsedSession =
      { Id = sid; Status = SessionDisplayStatus.Running; StatusMessage = None
        ProjectsText = "(A.fsproj)"; EvalCount = 1
        Uptime = "1m"; WorkingDir = @"C:\MyProject\Src"; LastActivity = "A"
        TestSummary = None; CoverageSummary = None; TestTreemapEntries = [||]; CoverageTreemap = None
        BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
        ActiveProject = None; ProjectRoles = []
        App = AppRun.AppRunState.NotRunning
        WorkerRssBytes = None; SelfHostStaleness = None }
    let html = renderSessionsForSession "0a2b3c4e" [ s ] false |> renderNode
    html |> Expect.stringContains "working dir should appear on the card" @"C:\MyProject\Src"
  }

  test "SessionState appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" "C:\\" "faulted" |> renderMainContent |> renderNode
    html |> Expect.stringContains "session state should appear" "faulted"
  }

  test "EvalStats count appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" "C:\\" "ready" |> renderMainContent |> renderNode
    html |> Expect.stringContains "eval count should appear" "7 evals"
  }

  test "EvalStats avg appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" "C:\\" "ready" |> renderMainContent |> renderNode
    html |> Expect.stringContains "avg ms should appear" "42"
  }

  test "ConnectionLabel appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" "C:\\" "ready" |> renderMainContent |> renderNode
    html |> Expect.stringContains "connection label should appear" "🌐 2"
  }

  test "ThemeName appears in theme picker" {
    let snap =
      { (mkSnap "0.0.0" "s1" "C:\\" "ready") with
          ThemePicker = Elem.div [] [ Text.raw "Theme: monokai" ] }
    let html = renderMainContent snap |> renderNode
    html |> Expect.stringContains "theme name should appear" "monokai"
  }

  test "rendered output always wraps in div#main" {
    let snap = mkSnap "0.0.0" "s1" "C:\\" "ready"
    let html = renderMainContent snap |> renderNode
    (html.StartsWith "<div id=\"main\"") |> Expect.isTrue "must start with div#main"
    (html.EndsWith "</div>") |> Expect.isTrue "must end with closing div"
  }
]

let bindingsPanelSseTests = testList "SSE bindings panel" [

  test "initial push uses selected session bindings when global snapshot is missing" {
    let binding : SageFs.Features.BindingExplorer.BindingInfo =
      { Name = "answer"
        TypeSig = "int"
        Value = Some "42"
        CellIndex = 0
        ShadowedBy = []
        ReferencedIn = [] }
    let html =
      resolveBindingsPanelSnapshot None [| binding |]
      |> renderBindingsPanel
      |> renderNode

    html |> Expect.stringContains "selected session bindings should populate the bindings panel" "Bindings (1)"
    html |> Expect.stringContains "bindings panel should render the selected session binding" "answer"
  }
]


[<Tests>]
let allDashboardSnapshotTests = testList "Dashboard Snapshots" [
  dashboardRenderSnapshotTests
  hostileEscapingTests
  liveTestingVisibilityTests
  keyboardHelpSnapshotTests
  edgeCaseSnapshotTests
  parserTests
  shellStructureTests
  bindingsPanelSseTests
  zeroJsBadgeTests
  railwayVisualizationTests
  testFilterTests
  datastarComplianceTests
  snapshotCompletenessTests
]


