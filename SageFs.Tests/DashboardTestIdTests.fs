module SageFs.Tests.DashboardTestIdTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server.Dashboard
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

// Demo automation (demo-gif-plan.md §7 G4, §4.3 Targets) needs to click
// dashboard controls without depending on button text, icons, or CSS classes
// that are free to change. These tests pin the fixed, contract-tested
// `data-testid` vocabulary a demos-side DU mirrors — so a renamed/restructured
// control breaks a test here, not a demo recording.

let private runningProject : SageFs.ProjectLoading.ClassifiedProject =
  { Path = "MyApp.fsproj"; Role = SageFs.ProjectLoading.ProjectRole.Executable; PackageRefs = [] }

let private mkSession (id: string) (app: SageFs.AppRun.AppRunState) (projectRoles: SageFs.ProjectLoading.ClassifiedProject list) : ParsedSession =
  { Id = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    Status = SessionDisplayStatus.Running
    StatusMessage = None
    ProjectsText = "(MyApp.fsproj)"
    EvalCount = 1
    Uptime = "1m"
    WorkingDir = "/work"
    LastActivity = "eval"
    TestSummary = None
    CoverageSummary = None
    TestTreemapEntries = [||]; CoverageTreemap = None; BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
    ActiveProject = None; ProjectRoles = projectRoles; App = app; WorkerRssBytes = None }

let private mkSnap () : DashboardSnapshot =
  { DashboardSnapshot.Version = "0.0.0"
    SessionState = "ready"; SessionId = "0a2b3c4d"; WorkingDir = "/work"
    WarmupProgress = ""; WorkflowLabel = "REPL"
    EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
    ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
    HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
    OutputPanel = renderOutputForSession "0a2b3c4d" [] "No output yet"
    SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
    ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
    BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []
    FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []
    FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []
    LiveTestingPanel = renderLiveTestingPanel Features.LiveTestActivity.LiveTestActivity.Off
    FrictionPanel = Elem.div [] []
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    EvalToPixelP50Ms = None
    EvalToPixelP99Ms = None }

[<Tests>]
let dashboardTestIdTests = testList "Dashboard data-testid hooks" [

  testCase "WHY — a runnable session's Run App button carries data-testid=\"run-app\" so a demo can start the app without matching the ▶ glyph" <| fun _ ->
    let sessions = [ mkSession "0a2b3c4d" AppRun.AppRunState.NotRunning [ runningProject ] ]
    let html = renderSessionsForSession "0a2b3c4d" sessions false |> renderNode
    html |> Expect.stringContains "run-app testid present" "data-testid=\"run-app\""

  testCase "WHY — a running app's Stop App button carries data-testid=\"stop-app\" so a demo can stop the app without matching the ■ glyph" <| fun _ ->
    let running =
      AppRun.AppRunState.Running
        { RunId = "r1"; Project = "MyApp.fsproj"; EntryPoint = "Program"
          Endpoint = AppRun.AppEndpoint.Http ("http://localhost:5000", [])
          StartedAt = DateTime.UtcNow }
    let sessions = [ mkSession "0a2b3c4d" running [ runningProject ] ]
    let html = renderSessionsForSession "0a2b3c4d" sessions false |> renderNode
    html |> Expect.stringContains "stop-app testid present" "data-testid=\"stop-app\""

  testCase "WHY — every session row carries data-testid=\"session-card\" so a demo can select a specific session by id, not by row position" <| fun _ ->
    let sessions = [ mkSession "0a2b3c4d" AppRun.AppRunState.NotRunning [] ]
    let html = renderSessionsForSession "0a2b3c4d" sessions false |> renderNode
    html |> Expect.stringContains "session-card testid present" "data-testid=\"session-card\""

  testCase "WHY — the stopping-card placeholder also carries data-testid=\"session-card\" because it morphs into the same DOM id a demo may still be targeting mid-teardown" <| fun _ ->
    let sid = WorkerProtocol.SessionId.validate "0a2b3c4d" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let html = renderStoppingCard sid |> renderNode
    html |> Expect.stringContains "session-card testid present on stopping card" "data-testid=\"session-card\""

  testCase "WHY — the no-session picker's fastest path carries data-testid=\"quick-start\" so a demo can start from a clean slate reliably" <| fun _ ->
    let html = renderSessionPicker [] |> renderNode
    html |> Expect.stringContains "quick-start testid present" "data-testid=\"quick-start\""

  testCase "WHY — the session output panel carries data-testid=\"session-output\" so a demo can observe a session's evaluated output" <| fun _ ->
    let html = renderOutputForSession "0a2b3c4d" [] "No output yet" |> renderNode
    html |> Expect.stringContains "session-output testid present" "data-testid=\"session-output\""

  testCase "WHY — the live testing enable/disable button carries data-testid=\"live-testing-toggle\" regardless of its current on/off label" <| fun _ ->
    let htmlOff = renderLiveTestingPanel Features.LiveTestActivity.LiveTestActivity.Off |> renderNode
    let htmlOn = renderLiveTestingPanel (Features.LiveTestActivity.LiveTestActivity.Running Features.LiveTestActivity.TestTally.empty) |> renderNode
    htmlOff |> Expect.stringContains "live-testing-toggle testid present when off" "data-testid=\"live-testing-toggle\""
    htmlOn |> Expect.stringContains "live-testing-toggle testid present when on" "data-testid=\"live-testing-toggle\""

  testCase "WHY — the composed page carries eval/reset/hard-reset/new-session data-testids in the one place they are rendered" <| fun _ ->
    let html = renderMainContent (mkSnap ()) |> renderNode
    html |> Expect.stringContains "eval testid present" "data-testid=\"eval\""
    html |> Expect.stringContains "reset testid present" "data-testid=\"reset\""
    html |> Expect.stringContains "hard-reset testid present" "data-testid=\"hard-reset\""
    html |> Expect.stringContains "new-session testid present" "data-testid=\"new-session\""
    html |> Expect.stringContains "session-output testid present in composed page" "data-testid=\"session-output\""
    html |> Expect.stringContains "live-testing-toggle testid present in composed page" "data-testid=\"live-testing-toggle\""
]
