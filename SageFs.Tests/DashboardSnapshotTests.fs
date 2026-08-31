module SageFs.Tests.DashboardSnapshotTests

open System
open Expecto
open VerifyExpecto
open VerifyTests
open Falco.Markup
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Server.Dashboard
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

do try VerifyTests.VerifierSettings.DisableRequireUniquePrefix() with _ -> ()

let snapshotsDir =
  System.IO.Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

let verifyDashboard (name: string) (html: string) =
  let settings = VerifySettings()
  settings.UseDirectory(snapshotsDir)
  settings.DisableDiff()
  Verifier.Verify(name, html, "html", settings).ToTask()


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
        IsActive = true
        IsSelected = true
        ProjectsText = "(MyProj.fsproj, Tests.fsproj)"
        EvalCount = 15
        Uptime = "3m"
        WorkingDir = @"C:\Code\MyProj"
        LastActivity = "eval"
        StandbyLabel = ""
        TestSummary = None
        CoverageSummary = None
        TestTreemapEntries = [||]; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = "" }
      { Id = WorkerProtocol.SessionId.validate "0a2b3c4e" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
        Status = SessionDisplayStatus.Stopped
        StatusMessage = None
        IsActive = false
        IsSelected = false
        ProjectsText = ""
        EvalCount = 0
        Uptime = ""
        WorkingDir = ""
        LastActivity = ""
        StandbyLabel = ""
        TestSummary = None
        CoverageSummary = None
        TestTreemapEntries = [||]; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = "" }
    ]
    let html = renderSessions sessions false |> renderNode
    do! verifyDashboard "dashboard_sessions" html
  }

  testTask "renderSessions empty" {
    let html = renderSessions [] false |> renderNode
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
    Expect.isTrue (html.Contains ".SageFs/config.fsx") "should mention the config path"
    Expect.isTrue (html.Contains "warmup auto-open disabled") "should mention the warmup auto-open opt-out"
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

  let mkQueries (isActive: bool) (statusLabel: string) : DashboardQueries =
    {
      GetSessionState = fun _ -> SessionState.Ready
      GetStatusMsg = fun _ -> None
      GetEvalStats = fun _ -> System.Threading.Tasks.Task.FromResult(SageFs.Affordances.EvalStats.empty)
      GetFrictionStore = fun () -> System.Threading.Tasks.Task.FromResult None
      GetSessionWorkingDir = fun _ -> @"C:\Code\Repos\SageFs"
      GetElmRegionsForSession = fun _ -> None
      GetPreviousSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
      GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
      GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult StandbyInfo.NoPool
      GetSessionStandbyInfo = fun _ -> StandbyInfo.NoPool
      GetHotReloadState = fun _ -> System.Threading.Tasks.Task.FromResult None
      GetWarmupContext = fun _ -> System.Threading.Tasks.Task.FromResult None
      GetWarmupProgress = fun _ -> ""
      GetSessionTestSummary = fun _ -> None
      GetSessionCoverageSummary = fun _ -> None
      GetSessionTestTreemap = fun _ -> [||]
      GetSessionBindings = fun _ -> [||]
      GetLiveBindings = fun _ -> None
      GetBindingScopeSnapshot = fun () -> None
      GetLiveTestingStatus = fun () -> statusLabel
      GetLiveTestingActive = fun () -> isActive
      GetEvalTimeline =
        fun () -> SageFs.Features.EvalTimeline.TimelineState.empty |> SageFs.Features.EvalTimeline.timelineStats 20
      GetDaemonHealth = fun () -> None
      GetFailureNarratives = fun () -> []
      GetCurrentDiagnostics = fun () -> []
      GetFilmstripEntries = fun () -> []
      GetTestSourceLocations = fun () -> []
      GetSessionAgentBadges = fun _ -> []
      GetSessionGuidanceCss = fun _ -> ""
      GetSessionWorkflow = fun _ -> WorkflowTypes.SessionWorkflow.Interactive
    }

  let mkInfra () : DashboardInfra =
    {
      Version = "0.0.0"
      McpPort = 37749
      StateChanged = None
      ConnectionTracker = None
      SessionThemes = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
      GetCompletions = fun _ _ _ -> System.Threading.Tasks.Task.FromResult []
      GetSessionCount = fun () -> System.Threading.Tasks.Task.FromResult 0
      SystemAlarmBuffer = ref []
      TriggerStateChange = fun () -> ()
      ActivityTracker = None
      LiveBindingsAdaptive = None
    }

  testTask "buildDashboardSnapshot carries rebuilding status into the live testing panel" {
    let! snap, _, _ =
      buildDashboardSnapshot (mkQueries true "🔨 Rebuilding 2 tests") (mkInfra ()) (WorkerProtocol.SessionId.validate "session-1" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())) (WorkerProtocol.SessionId.newId ()) "" "default"
    let html = snap.LiveTestingPanel |> renderNode
    Expect.stringContains html "Live Testing: ON" "dashboard should show live testing as active"
    Expect.stringContains html "🔨 Rebuilding 2 tests" "dashboard should tell users that tests are waiting on compilation"
  }

  // ─── TDD improvement: OFF state must communicate the cost ───────────────────
  // WHY — when live testing is off, the dashboard currently says only
  // "Enable to start discovering and running tests automatically" which
  // describes the benefit. A user who enables without knowing the cost
  // (tests re-run on every keystroke and file save) will be surprised by
  // unexpected CPU usage or flapping test state. The hint must mention
  // "keystroke" so the cost is explicit.
  testTask "OFF state hint warns that tests run on every keystroke" {
    let! snap, _, _ =
      buildDashboardSnapshot (mkQueries false "Test cycle idle") (mkInfra ()) (WorkerProtocol.SessionId.validate "session-1" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())) (WorkerProtocol.SessionId.newId ()) "" "default"
    let html = snap.LiveTestingPanel |> renderNode
    // The "off" hint must mention the cost: keystrokes drive test re-runs.
    Expect.stringContains html "keystroke" "OFF hint must mention that tests run on every keystroke"
  }

  // ─── TDD improvement: passed/failed counts must be color-coded ──────────────
  // WHY — when the live testing header shows "5 passed / 2 failed", a user
  // scanning the dashboard cannot tell the colors apart because the count
  // is in a single inline string. A single failed test buried in green
  // can be missed. Passed must be green (--fg-green), failed must be red
  // (--fg-red) so the eyes latch onto the failing count.
  testTask "ON state shows passed count in green and failed count in red" {
    let ltSummary : SageFs.Features.LiveTestHealthSummary = {
      TotalTests = 7; Passed = 5; Failed = 2; Running = 0
    }
    let healthSnap : SageFs.Features.HealthSnapshot = {
      DaemonPid = 1; DaemonPort = 37749
      Uptime = TimeSpan.FromSeconds 1.0; Version = "0.0.0"
      SessionSummaries = []; LiveTestingSummary = Some ltSummary
      MemoryMB = 0
    }
    let queries = { (mkQueries true "Tests: 5 passed / 2 failed") with
                      GetDaemonHealth = fun () -> Some healthSnap }
    let! snap, _, _ =
      buildDashboardSnapshot queries (mkInfra ()) (WorkerProtocol.SessionId.validate "session-1" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())) (WorkerProtocol.SessionId.newId ()) "" "default"
    let html = snap.LiveTestingPanel |> renderNode
    Expect.stringContains html "--fg-green" "passed count must use green color"
    Expect.stringContains html "--fg-red" "failed count must use red color"
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
        IsActive = true
        IsSelected = true
        ProjectsText = "(MyProj.fsproj)"
        EvalCount = 42
        Uptime = "15m"
        WorkingDir = @"C:\Code\MyProj"
        LastActivity = "eval"
        StandbyLabel = ""
        TestSummary = None
        CoverageSummary = None
        TestTreemapEntries = [||]; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = "" }
    ]
    let html = renderSessions sessions false |> renderNode
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
    Expect.isTrue m.Success "should match timestamp+kind format"
    Expect.equal m.Groups.[1].Value "12:30:45" "timestamp"
    Expect.equal m.Groups.[2].Value "result" "kind"
    Expect.equal m.Groups.[3].Value "val x: int = 42" "content"
  }

  test "output parser handles kind without timestamp" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^\[(\w+)\]\s*(.*)",
      System.Text.RegularExpressions.RegexOptions.Singleline)
    let m = regex.Match("[error] Something went wrong")
    Expect.isTrue m.Success "should match kind-only format"
    Expect.equal m.Groups.[1].Value "error" "kind"
    Expect.equal m.Groups.[2].Value "Something went wrong" "content"
  }

  test "diag parser extracts severity line col" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
    let m = regex.Match("[error] (5,10) Type mismatch")
    Expect.isTrue m.Success "should match diag format"
    Expect.equal (int m.Groups.[2].Value) 5 "line"
    Expect.equal (int m.Groups.[3].Value) 10 "col"
    Expect.equal m.Groups.[4].Value "Type mismatch" "message"
  }

  test "diag parser fallback for non-standard format" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
    let m = regex.Match("Some general error")
    Expect.isFalse m.Success "should not match non-standard format"
  }

  test "session parser extracts id status active" {
    let regex = System.Text.RegularExpressions.Regex(
      @"^(\S+)\s+\[(\w+)\]\s*(\*?)\s*(\([^)]*\))?\s*(evals:\d+)?\s*(.*)")
    let m = regex.Match("session-abc [running] * (Proj.fsproj) evals:5 up:3m")
    Expect.isTrue m.Success "should match session format"
    Expect.equal m.Groups.[1].Value "session-abc" "session id"
    Expect.equal m.Groups.[2].Value "running" "status"
    Expect.stringContains m.Groups.[3].Value "*" "active marker"
  }
]

let mkRegion id content = {
  Id = id; Flags = RegionFlags.None; Content = content
  Affordances = []; Cursor = None; Completions = None
  LineAnnotations = [||]
}

let shellStructureTests = testList "shell structure (replaces browser existence checks)" [
  testTask "renderShell snapshot" {
    let html = renderShell "0.0.0-test" "" (Elem.div [] []) |> renderNode
    do! verifyDashboard "dashboard_shell" html
  }

  test "shell has SageFs title" {
    let html = renderShell "1.2.3" "" (Elem.div [] []) |> renderNode
    Expect.stringContains html "SageFs" "shell has SageFs title"
  }

  // Full-page morph: dynamic elements live in renderMainContent, not renderShell.
  // These tests verify the morphed content includes key interactive elements.
  let mkSnap version = {
    DashboardSnapshot.Version = version
    SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
    WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
    ThemeName = "default"; ConnectionLabel = None
    HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
    OutputPanel = Elem.div [] []
    SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
    ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
    BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] [] }

  test "renderMainContent shows version" {
    let html = renderMainContent (mkSnap "1.2.3") |> renderNode
    Expect.stringContains html "v1.2.3" "main content has version"
  }

  test "evaluate section has textarea with placeholder" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    Expect.stringContains html "eval-input" "has eval-input class"
    Expect.stringContains html "F# code" "placeholder mentions F#"
  }

  test "eval button is present" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    Expect.stringContains html "Eval" "has Eval button"
  }

  test "reset and hard reset buttons are present" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    Expect.stringContains html "[RESET]" "has [RESET] button"
    Expect.stringContains html "[HARD_RESET]" "has [HARD_RESET] button"
  }

  test "clear output button in panel header" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    Expect.stringContains html "Clear" "has Clear button"
  }

  test "create session section has all inputs" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    Expect.stringContains html "Discover" "has Discover button"
    Expect.stringContains html "fsproj" "has fsproj placeholder"
    Expect.stringContains html "Create" "has Create Session button"
    Expect.stringContains html "New Session" "new session section is a collapsible details"
  }

  test "server-status banner has no data-show attribute" {
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    let bannerStart = html.IndexOf("id=\"server-status\"")
    Expect.isTrue (bannerStart > -1) "server-status exists"
    let tagEnd = html.IndexOf(">", bannerStart)
    let tag = html.Substring(bannerStart, tagEnd - bannerStart)
    Expect.isFalse (tag.Contains("data-show")) "banner must not use data-show"
  }

  // ── Minimal mode (Task 1) ──────────────────────────────────────
  test "renderShell has expandedDashboard signal" {
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    // Datastar renders signal names as kebab-case in attributes (expandedDashboard → expanded-dashboard)
    Expect.stringContains html "expanded-dashboard" "shell has expanded-dashboard signal attribute"
  }

  test "renderMainContent has expanded-only sections" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    Expect.stringContains html "expanded-only" "main content has expanded-only class"
  }

  test "renderMainContent has expand toggle button" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    Expect.stringContains html "expandedDashboard = !$expandedDashboard" "has expand toggle onclick"
  }

  test "renderMainContent expand toggle button has class expand-toggle-btn" {
    let html = renderMainContent (mkSnap "0.0.0") |> renderNode
    Expect.stringContains html "expand-toggle-btn" "has expand-toggle-btn CSS class"
  }

  // ── SSE full-state push on connect (Task 2) ───────────────────
  test "SSE full-state push: shell connects to stream endpoint" {
    // createStreamHandler calls pushState() immediately on connect (initial pushState in try/catch).
    // This test verifies the shell wires up the SSE stream that triggers the initial state push.
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    Expect.stringContains html "/dashboard/stream" "shell connects to SSE stream endpoint for initial push"
  }
]

let standbyBadgeSseTests = testList "SSE standby badge" [

  test "ready standby shows green badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Ready
    let r = mkRegion "sessions" "  0a2b3c4d [ready] * (SageFs.Tests.fsproj) evals:42 up:1m dir:C:\\Code\\Repos\\SageFs last:now"
    let html = renderRegionForSse getState getMsg getStandby r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--fg-green)") "ready standby should use green"
  }

  test "warming standby shows yellow badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Warming ""
    let r = mkRegion "sessions" "  0a2b3c4d [ready] * (SageFs.Tests.fsproj) evals:42 up:1m dir:C:\\Code\\Repos\\SageFs last:now"
    let html = renderRegionForSse getState getMsg getStandby r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--fg-yellow)") "warming standby should use yellow"
  }

  test "invalidated standby shows red badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Invalidated
    let r = mkRegion "sessions" "  0a2b3c4d [ready] * (SageFs.Tests.fsproj) evals:42 up:1m dir:C:\\Code\\Repos\\SageFs last:now"
    let html = renderRegionForSse getState getMsg getStandby r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--fg-red)") "invalidated standby should use red"
  }

  test "no pool shows no badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.NoPool
    let r = mkRegion "sessions" "  0a2b3c4d [ready] * (SageFs.Tests.fsproj) evals:42 up:1m dir:C:\\Code\\Repos\\SageFs last:now"
    let html = renderRegionForSse getState getMsg getStandby r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isFalse (html.Contains "standby") "NoPool should not show standby badge"
  }

  test "StandbyInfo.label maps correctly" {
    Expect.equal (StandbyInfo.label StandbyInfo.NoPool) "" "NoPool -> empty"
    Expect.equal (StandbyInfo.label (StandbyInfo.Warming "")) "⏳ standby" "Warming empty"
    Expect.equal (StandbyInfo.label (StandbyInfo.Warming "2/4 Scanned 12 files")) "⏳ 2/4 Scanned 12 files" "Warming with progress"
    Expect.equal (StandbyInfo.label StandbyInfo.Ready) "✓ standby" "Ready"
    Expect.equal (StandbyInfo.label StandbyInfo.Invalidated) "⚠ standby" "Invalidated"
  }

  test "output region unaffected by standby" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Ready
    let r = mkRegion "output" "[12:00:00] [info] hello world"
    let html = renderRegionForSse getState getMsg getStandby r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isFalse (html.Contains "standby") "output region should not contain standby"
  }

  test "unknown region returns None" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Ready
    let r = mkRegion "unknown" "whatever"
    Expect.isNone (renderRegionForSse getState getMsg getStandby r) "unknown region -> None"
  }
]

let warmupProgressSseTests = testList "Standby warmup progress SSE" [
  test "warming badge with progress shows phase text" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Warming "2/4 Scanned 12 files"
    let r = mkRegion "sessions" "  0a2b3c4d [ready] * (SageFs.Tests.fsproj) evals:42 up:1m dir:C:\\Code\\Repos\\SageFs last:now"
    let result = renderRegionForSse getState getMsg getStandby r
    match result with
    | Some node ->
      let html = renderNode node
      Expect.stringContains html "⏳ 2/4 Scanned 12 files" "should show progress"
    | None -> failtest "should render sessions region"
  }
  test "warming badge with empty progress shows default" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Warming ""
    let r = mkRegion "sessions" "  0a2b3c4d [ready] * (SageFs.Tests.fsproj) evals:42 up:1m dir:C:\\Code\\Repos\\SageFs last:now"
    let result = renderRegionForSse getState getMsg getStandby r
    match result with
    | Some node ->
      let html = renderNode node
      Expect.stringContains html "⏳ standby" "should show default label"
    | None -> failtest "should render"
  }
]

// ── Zero-JS badge tests ──

let zeroJsBadgeTests = testList "Zero-JS badge" [
  test "shell contains no framework JS (React/Vue/Angular/Svelte)" {
    let html = renderShell "1.0.0" "" (Elem.div [] []) |> renderNode
    let frameworks = [ "react"; "vue"; "angular"; "svelte"; "jquery"; "alpine" ]
    for fw in frameworks do
      Expect.isFalse
        (html.ToLowerInvariant().Contains fw)
        (sprintf "should not contain %s framework reference" fw)
  }

  test "shell contains only Datastar CDN script as external JS" {
    let html = renderShell "1.0.0" "" (Elem.div [] []) |> renderNode
    let srcPattern = System.Text.RegularExpressions.Regex("src=\"([^\"]+)\"")
    let scriptSrcs = srcPattern.Matches(html)
    let jsSources =
      [ for m in scriptSrcs -> m.Groups.[1].Value ]
      |> List.filter (fun s -> s.EndsWith(".js") || s.Contains("datastar"))
    match jsSources.Length with
    | 0 -> () // CDN might be inline
    | _ ->
      Expect.isTrue
        (jsSources |> List.forall (fun s -> s.Contains "datastar"))
        (sprintf "all external JS should be Datastar only, found: %A" jsSources)
  }

  test "inline scripts are utility-only, not application logic" {
    let html = renderShell "1.0.0" "" (Elem.div [] []) |> renderNode
    let scriptPattern = System.Text.RegularExpressions.Regex("<script[^>]*>([\\s\\S]*?)</script>")
    let scriptBlocks = scriptPattern.Matches(html)
    let inlineScripts = [ for m in scriptBlocks -> m.Groups.[1].Value ]
    for script in inlineScripts do
      // No state management patterns
      Expect.isFalse (script.Contains "useState") "no React-style state"
      Expect.isFalse (script.Contains "createStore") "no Redux-style store"
      Expect.isFalse (script.Contains "createSignal") "no SolidJS-style signals"
      // No fetch for data retrieval (fetch for POST commands is OK)
      let fetchCount = script.Split("fetch(").Length - 1
      let postCount = script.Split("'POST'").Length + script.Split("\"POST\"").Length - 2
      Expect.isTrue
        (fetchCount <= postCount + 1)
        "fetch calls should be POST-only (command dispatch), not GET (data retrieval)"
  }

  test "total inline JS payload is under 5KB" {
    let html = renderShell "1.0.0" "" (Elem.div [] []) |> renderNode
    let scriptPattern = System.Text.RegularExpressions.Regex("<script[^>]*>([\\s\\S]*?)</script>")
    let scriptBlocks = scriptPattern.Matches(html)
    let totalBytes =
      [ for m in scriptBlocks -> m.Groups.[1].Value ]
      |> List.sumBy (fun s -> System.Text.Encoding.UTF8.GetByteCount(s))
    Expect.isLessThan totalBytes 5120
      (sprintf "inline JS should be <5KB, was %d bytes" totalBytes)
  }

  test "no application-level JS event handlers in HTML attributes" {
    let html = renderShell "1.0.0" "" (Elem.div [] []) |> renderNode
    // onclick/onchange etc. should use Datastar data-on-* attributes, not raw HTML
    Expect.isFalse (html.Contains " onclick=") "should not use raw onclick (use Ds.onClick)"
    Expect.isFalse (html.Contains " onchange=") "should not use raw onchange (use Ds.onEvent)"
    Expect.isFalse (html.Contains " onsubmit=") "should not use raw onsubmit"
  }
]

// ── Railway visualization tests ──

let railwayVisualizationTests = testList "Railway visualization" [

  testList "PipelineRailwayView.fromStages" [
    test "builds stages with StageSuccess outcomes" {
      let stages = [ ("Parse", 12.0); ("TypeCheck", 45.0); ("Execute", 363.0) ]
      let view = PipelineRailwayView.fromStages stages 420.0
      Expect.equal view.Stages.Length 3 "should have 3 stages"
      Expect.equal view.TotalMs 420.0 "total"
      Expect.isTrue
        (view.Stages |> List.forall (fun s ->
          match s.Outcome with StageSuccess -> true | _ -> false))
        "all stages should be success"
    }

    test "empty stages list produces empty railway" {
      let view = PipelineRailwayView.fromStages [] 0.0
      Expect.isEmpty view.Stages "should be empty"
      Expect.equal view.TotalMs 0.0 "total"
    }

    test "stage names are preserved" {
      let stages = [ ("Parse", 10.0); ("Execute", 20.0) ]
      let view = PipelineRailwayView.fromStages stages 30.0
      Expect.equal
        (view.Stages |> List.map (fun s -> s.Name))
        [ "Parse"; "Execute" ]
        "names"
    }

    test "stage durations are preserved" {
      let stages = [ ("Parse", 12.5); ("TypeCheck", 45.7) ]
      let view = PipelineRailwayView.fromStages stages 58.2
      Expect.equal
        (view.Stages |> List.map (fun s -> s.DurationMs))
        [ 12.5; 45.7 ]
        "durations"
    }
  ]

  testList "PipelineRailwayView.fromStagesWithFailure" [
    test "marks the failed stage with StageFailure" {
      let stages = [ ("Parse", 12.0); ("TypeCheck", 45.0); ("Execute", 0.0) ]
      let view = PipelineRailwayView.fromStagesWithFailure stages 57.0 "Execute" "Runtime error"
      match view.Stages |> List.tryFind (fun s -> s.Name = "Execute") with
      | Some s ->
        match s.Outcome with
        | StageFailure err -> Expect.equal err "Runtime error" "error msg"
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
      Expect.stringContains html "Parse ✓" "should show Parse checkmark"
      Expect.stringContains html "TypeCheck ✓" "should show TypeCheck checkmark"
      Expect.stringContains html "Execute ✓" "should show Execute checkmark"
    }

    test "renders stage durations in brackets" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0); ("Execute", 363.0) ] 375.0
      let html = renderRailway railway |> renderNode
      Expect.stringContains html "[12ms]" "should show Parse duration"
      Expect.stringContains html "[363ms]" "should show Execute duration"
    }

    test "renders arrows between stages" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0); ("Execute", 363.0) ] 375.0
      let html = renderRailway railway |> renderNode
      Expect.stringContains html "→" "should have arrow separator"
    }

    test "renders total duration" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0) ] 420.0
      let html = renderRailway railway |> renderNode
      Expect.stringContains html "[420ms total]" "should show total"
    }

    test "renders empty pipeline as 'No pipeline stages'" {
      let html = renderRailway PipelineRailwayView.empty |> renderNode
      Expect.stringContains html "No pipeline stages" "should show empty message"
    }

    test "renders failure stage with cross mark" {
      let railway = PipelineRailwayView.fromStagesWithFailure [ ("Parse", 12.0); ("Execute", 0.0) ] 12.0 "Execute" "boom"
      let html = renderRailway railway |> renderNode
      Expect.stringContains html "Execute ✗" "should show failure cross"
    }

    test "success stage has stage-success CSS class" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 12.0) ] 12.0
      let html = renderRailway railway |> renderNode
      Expect.stringContains html "stage-success" "should have success class"
    }

    test "failure stage has stage-failure CSS class" {
      let railway = PipelineRailwayView.fromStagesWithFailure [ ("Parse", 0.0) ] 0.0 "Parse" "err"
      let html = renderRailway railway |> renderNode
      Expect.stringContains html "stage-failure" "should have failure class"
    }

    test "pipeline-railway CSS class on container" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 1.0) ] 1.0
      let html = renderRailway railway |> renderNode
      Expect.stringContains html "pipeline-railway" "should have container class"
    }

    test "single stage has no arrows" {
      let railway = PipelineRailwayView.fromStages [ ("Parse", 1.0) ] 1.0
      let html = renderRailway railway |> renderNode
      let arrowCount = html.Split("→").Length - 1
      Expect.equal arrowCount 0 "should have no arrows for single stage"
    }

    test "N stages produce N-1 arrows" {
      let stages = [ ("A", 1.0); ("B", 2.0); ("C", 3.0); ("D", 4.0) ]
      let railway = PipelineRailwayView.fromStages stages 10.0
      let html = renderRailway railway |> renderNode
      let arrowCount = html.Split("→").Length - 1
      Expect.equal arrowCount 3 "should have 3 arrows for 4 stages"
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
      Expect.equal
        (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Passed)
        "passed" "Passed"
    }
    test "Failed maps to 'failed'" {
      Expect.equal
        (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Failed)
        "failed" "Failed"
    }
    test "Running maps to 'running'" {
      Expect.equal
        (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Running)
        "running" "Running"
    }
    test "Skipped maps to 'skipped'" {
      Expect.equal
        (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Skipped)
        "skipped" "Skipped"
    }
    test "Other maps to 'other'" {
      Expect.equal
        (treemapStatusToFilterValue Features.LiveTesting.TreemapStatus.Other)
        "other" "Other"
    }
  ]

  testList "renderTestFilterBar" [
    test "renders filter bar container with test-filter-bar class" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "test-filter-bar" "should have container class"
    }

    test "shows passed count" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "✓ 2" "should show 2 passed"
    }

    test "shows failed count" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "✗ 1" "should show 1 failed"
    }

    test "shows running button when running tests exist" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "⟳ 1" "should show 1 running"
    }

    test "shows skipped button when skipped tests exist" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "⊘ 1" "should show 1 skipped"
    }

    test "hides running button when no running tests" {
      let entries = sampleTestEntries |> Array.filter (fun e ->
        e.Status <> Features.LiveTesting.TreemapStatus.Running)
      let html = renderTestFilterBar entries |> renderNode
      Expect.isFalse (html.Contains "⟳") "should not show running button"
    }

    test "hides skipped button when no skipped tests" {
      let entries = sampleTestEntries |> Array.filter (fun e ->
        e.Status <> Features.LiveTesting.TreemapStatus.Skipped)
      let html = renderTestFilterBar entries |> renderNode
      Expect.isFalse (html.Contains "⊘") "should not show skipped button"
    }

    test "filter buttons use Datastar show expression" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "$testFilter" "should reference testFilter signal"
    }

    test "click sets testFilter signal to status value" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "$testFilter = 'passed'" "should set filter to passed"
      Expect.stringContains html "$testFilter = 'failed'" "should set filter to failed"
    }

    test "active button resets filter to all on click" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "$testFilter = 'all'" "active button should reset to all"
    }

    test "active button has test-filter-active class" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "test-filter-active" "should have active class"
    }

    test "renders Filter label" {
      let html = renderTestFilterBar sampleTestEntries |> renderNode
      Expect.stringContains html "Filter:" "should have Filter label"
    }
  ]

  testList "renderTestTreemap with signal filtering" [
    test "treemap entries have data-show attribute" {
      let html = renderTestTreemap sampleTestEntries |> renderNode
      Expect.stringContains html "data-show" "should have data-show for filtering"
    }

    test "passed entries show when filter is all or passed" {
      let html = renderTestTreemap sampleTestEntries |> renderNode
      Expect.stringContains html "$testFilter === 'all' || $testFilter === 'passed'" "should show for all or passed"
    }

    test "failed entries show when filter is all or failed" {
      let html = renderTestTreemap sampleTestEntries |> renderNode
      Expect.stringContains html "$testFilter === 'all' || $testFilter === 'failed'" "should show for all or failed"
    }
  ]

  testList "Signals module" [
    test "TestFilter signal name is defined" {
      Expect.equal Signals.TestFilter "testFilter" "should be testFilter"
    }
  ]
]

let datastarComplianceTests = testList "Datastar compliance (synthesis 5.4)" [

  test "shell initializes SSE stream via data-init" {
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    Expect.stringContains html "data-init" "must have data-init for SSE"
    Expect.stringContains html "/dashboard/stream" "must target stream endpoint"
  }

  test "shell loads Datastar CDN script" {
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    Expect.stringContains html "datastar" "must include datastar CDN"
  }

  test "all Signals are initialized in shell via data-signals" {
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    // Datastar renders signal names kebab-case: helpVisible → help-visible
    let expectedSignalAttrs =
      [ "data-signals:help-visible"; "data-signals:sidebar-open"; "data-signals:session-id"
        "data-signals:code"; "data-signals:new-session-dir"; "data-signals:manual-projects"
        "data-signals:theme"; "data-signals:cursor-pos"; "data-signals:test-filter" ]
    for attr in expectedSignalAttrs do
      Expect.stringContains html attr (sprintf "signal attr '%s' must be initialized" attr)
  }

  test "main div has correct DOM ID" {
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    Expect.stringContains html (sprintf "id=\"%s\"" DomIds.Main) "must have main div"
  }

  test "server-status div has correct DOM ID" {
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    Expect.stringContains html (sprintf "id=\"%s\"" DomIds.ServerStatus) "must have server-status div"
  }

  test "renderMainContent includes key DOM IDs" {
    let snap = {
      DashboardSnapshot.Version = "0.0.0"
      SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
      WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
      ThemeName = "default"; ConnectionLabel = None
      HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
      OutputPanel = Elem.div [] []
      SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
      ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
      BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] [] }
    let html = renderMainContent snap |> renderNode
    let mustHaveIds =
      [ DomIds.Main; DomIds.SessionStatus; DomIds.EvalStats
        DomIds.EditorArea; DomIds.EvaluateSection; DomIds.EvalTextarea ]
    for domId in mustHaveIds do
      Expect.stringContains html (sprintf "id=\"%s\"" domId)
        (sprintf "main content must have id='%s'" domId)
  }

  test "SSE format: events end with double newline" {
    let evt = SageFs.SseWriter.formatSseEvent "test" "data"
    Expect.isTrue (evt.Length > 0) "non-empty"
    Expect.isTrue (evt.EndsWith("\n\n")) "must end with \\n\\n"
  }

  test "SSE format: retry hint is spec-compliant" {
    let retry = SageFs.SseWriter.formatRetryHint 3000
    Expect.equal retry "retry: 3000\n\n" "retry format"
  }

  test "shell has no React/Vue/Angular framework references" {
    let html = renderShell "0.0.0" "" (Elem.div [] []) |> renderNode
    let banned = [ "react"; "vue"; "angular"; "svelte"; "htmx"; "alpine" ]
    let lower = html.ToLowerInvariant()
    for framework in banned do
      Expect.isFalse (lower.Contains(framework)) (sprintf "must not reference %s" framework)
  }

  test "morph target: renderMainContent wraps in div#main" {
    let snap = {
      DashboardSnapshot.Version = "0.0.0"
      SessionState = "ready"; SessionId = "t"; WorkingDir = "C:\\"
      WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
      ThemeName = "default"; ConnectionLabel = None
      HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
      OutputPanel = Elem.div [] []
      SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
      ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
      BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] [] }
    let html = renderMainContent snap |> renderNode
    Expect.isTrue (html.StartsWith("<div id=\"main\""))"must start with div#main"
  }
]

let snapshotCompletenessTests = testList "Snapshot field completeness (synthesis 3.1)" [

  let mkSnap version sessionId workingDir state =
    { DashboardSnapshot.Version = version
      SessionState = state; SessionId = sessionId; WorkingDir = workingDir
      WarmupProgress = ""; WorkflowLabel = "REPL"; EvalStats = { Count = 7; AvgMs = 42.0; MinMs = 1.0; MaxMs = 100.0; Sparkline = ""; P50Ms = None; P95Ms = None }
      ThemeName = "monokai"; ConnectionLabel = Some "🌐 2 🤖 1"
      HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
      OutputPanel = Elem.div [] []
      SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
      ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
      BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] [] }
  test "Version appears in rendered output" {
    let html = mkSnap "1.2.3" "s1" "C:\\" "ready" |> renderMainContent |> renderNode
    Expect.stringContains html "1.2.3" "version should appear"
  }

  test "SessionId appears in rendered output" {
    let html = mkSnap "0.0.0" "my-session-42" "C:\\" "ready" |> renderMainContent |> renderNode
    Expect.stringContains html "my-session-42" "sessionId should appear"
  }

  test "WorkingDir appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" @"C:\MyProject\Src" "ready" |> renderMainContent |> renderNode
    Expect.stringContains html @"C:\MyProject\Src" "working dir should appear"
  }

  test "SessionState appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" "C:\\" "faulted" |> renderMainContent |> renderNode
    Expect.stringContains html "faulted" "session state should appear"
  }

  test "EvalStats count appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" "C:\\" "ready" |> renderMainContent |> renderNode
    Expect.stringContains html "7 evals" "eval count should appear"
  }

  test "EvalStats avg appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" "C:\\" "ready" |> renderMainContent |> renderNode
    Expect.stringContains html "42" "avg ms should appear"
  }

  test "ConnectionLabel appears in rendered output" {
    let html = mkSnap "0.0.0" "s1" "C:\\" "ready" |> renderMainContent |> renderNode
    Expect.stringContains html "🌐 2" "connection label should appear"
  }

  test "ThemeName appears in theme picker" {
    let snap =
      { (mkSnap "0.0.0" "s1" "C:\\" "ready") with
          ThemePicker = Elem.div [] [ Text.raw "Theme: monokai" ] }
    let html = renderMainContent snap |> renderNode
    Expect.stringContains html "monokai" "theme name should appear"
  }

  test "rendered output always wraps in div#main" {
    let snap = mkSnap "0.0.0" "s1" "C:\\" "ready"
    let html = renderMainContent snap |> renderNode
    Expect.isTrue (html.StartsWith "<div id=\"main\"") "must start with div#main"
    Expect.isTrue (html.EndsWith "</div>") "must end with closing div"
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

    Expect.stringContains html "Bindings (1)" "selected session bindings should populate the bindings panel"
    Expect.stringContains html "answer" "bindings panel should render the selected session binding"
  }
]

[<Tests>]
let allDashboardSnapshotTests = testList "Dashboard Snapshots" [
  dashboardRenderSnapshotTests
  liveTestingVisibilityTests
  keyboardHelpSnapshotTests
  edgeCaseSnapshotTests
  parserTests
  shellStructureTests
  standbyBadgeSseTests
  bindingsPanelSseTests
  warmupProgressSseTests
  zeroJsBadgeTests
  railwayVisualizationTests
  testFilterTests
  datastarComplianceTests
  snapshotCompletenessTests
]


