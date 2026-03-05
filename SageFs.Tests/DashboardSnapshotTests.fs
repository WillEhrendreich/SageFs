module SageFs.Tests.DashboardSnapshotTests

open Expecto
open VerifyExpecto
open VerifyTests
open Falco.Markup
open SageFs
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
    let html = renderSessionStatus "Ready" "session-abc" "/home/user/project" "" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_ready" html
  }

  testTask "renderSessionStatus warming" {
    let html = renderSessionStatus "WarmingUp" "session-def" "/home/user/project" "" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_warming" html
  }

  testTask "renderEvalStats" {
    let html = renderEvalStats { Count = 42; AvgMs = 123.4; MinMs = 5.0; MaxMs = 1045.0 } |> renderNode
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
    let html = renderOutput lines |> renderNode
    do! verifyDashboard "dashboard_output_mixed" html
  }

  testTask "renderOutput empty" {
    let html = renderOutput [] |> renderNode
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
      { Id = "session-abc"
        Status = "running"
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
        TestTreemapEntries = [||] }
      { Id = "session-def"
        Status = "stopped"
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
        TestTreemapEntries = [||] }
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

let keyboardHelpSnapshotTests = testList "keyboard help snapshots" [
  testTask "renderKeyboardHelp" {
    let html = renderKeyboardHelp () |> renderNode
    do! verifyDashboard "dashboard_keyboardHelp" html
  }
]

let edgeCaseSnapshotTests = testList "edge case snapshots" [
  testTask "renderSessions single active session" {
    let sessions : ParsedSession list = [
      { Id = "session-xyz"
        Status = "running"
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
        TestTreemapEntries = [||] }
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
    let html = renderEvalStats { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0 } |> renderNode
    do! verifyDashboard "dashboard_evalStats_zero" html
  }

  testTask "renderSessionStatus faulted" {
    let html = renderSessionStatus "Faulted" "session-err" @"C:\broken" "" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_faulted" html
  }

  testTask "renderSessionStatus warming with progress" {
    let html = renderSessionStatus "WarmingUp" "session-warm" "/home/user/project" "2/4 Scanned 12 source files" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_warmingWithProgress" html
  }

  testTask "renderOutput single result line" {
    if not (SyntaxHighlight.isAvailable()) then
      Tests.skiptest "tree-sitter not available; snapshot was generated with syntax highlighting"
    let lines = [ { Timestamp = Some "14:00:00"; Kind = ResultLine; Text = "val it: int = 0" } ]
    let html = renderOutput lines |> renderNode
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
    let html = renderShell "0.0.0-test" |> renderNode
    do! verifyDashboard "dashboard_shell" html
  }

  test "shell has SageFs title" {
    let html = renderShell "1.2.3" |> renderNode
    Expect.stringContains html "SageFs" "shell has SageFs title"
  }

  // Full-page morph: dynamic elements live in renderMainContent, not renderShell.
  // These tests verify the morphed content includes key interactive elements.
  let mkSnap version = {
    DashboardSnapshot.Version = version
    SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
    WarmupProgress = ""; EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0 }
    ThemeName = "default"; ConnectionLabel = None
    HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
    OutputPanel = Elem.div [] []
    SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
    ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
    BindingsPanel = Elem.div [] []
  }

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
    Expect.stringContains html "Reset" "has Reset button"
    Expect.stringContains html "Hard Reset" "has Hard Reset button"
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
  }

  test "server-status banner has no data-show attribute" {
    let html = renderShell "0.0.0" |> renderNode
    let bannerStart = html.IndexOf("id=\"server-status\"")
    Expect.isTrue (bannerStart > -1) "server-status exists"
    let tagEnd = html.IndexOf(">", bannerStart)
    let tag = html.Substring(bannerStart, tagEnd - bannerStart)
    Expect.isFalse (tag.Contains("data-show")) "banner must not use data-show"
  }
]

let standbyBadgeSseTests = testList "SSE standby badge" [

  test "ready standby shows green badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Ready
    let r = mkRegion "sessions" "active s1 SageFs.Tests.fsproj C:\\Code\\Repos\\SageFs 42 1m Ready 0"
    let html = renderRegionForSse getState getMsg getStandby r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--fg-green)") "ready standby should use green"
  }

  test "warming standby shows yellow badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Warming ""
    let r = mkRegion "sessions" "active s1 SageFs.Tests.fsproj C:\\Code\\Repos\\SageFs 42 1m Ready 0"
    let html = renderRegionForSse getState getMsg getStandby r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--fg-yellow)") "warming standby should use yellow"
  }

  test "invalidated standby shows red badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.Invalidated
    let r = mkRegion "sessions" "active s1 SageFs.Tests.fsproj C:\\Code\\Repos\\SageFs 42 1m Ready 0"
    let html = renderRegionForSse getState getMsg getStandby r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--fg-red)") "invalidated standby should use red"
  }

  test "no pool shows no badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let getStandby _ = StandbyInfo.NoPool
    let r = mkRegion "sessions" "active s1 SageFs.Tests.fsproj C:\\Code\\Repos\\SageFs 42 1m Ready 0"
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
    let r = mkRegion "sessions" "No sessions"
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
    let r = mkRegion "sessions" "No sessions"
    let result = renderRegionForSse getState getMsg getStandby r
    match result with
    | Some node ->
      let html = renderNode node
      Expect.stringContains html "⏳ standby" "should show default label"
    | None -> failtest "should render"
  }
]


[<Tests>]
let allDashboardSnapshotTests = testList "Dashboard Snapshots" [
  dashboardRenderSnapshotTests
  keyboardHelpSnapshotTests
  edgeCaseSnapshotTests
  parserTests
  shellStructureTests
  standbyBadgeSseTests
  warmupProgressSseTests
]
