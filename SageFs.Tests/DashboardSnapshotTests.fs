module SageFs.Tests.DashboardSnapshotTests

open Expecto
open VerifyExpecto
open VerifyTests
open Falco.Markup
open SageFs
open SageFs.Server.Dashboard

let snapshotsDir =
  System.IO.Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

let verifyDashboard (name: string) (html: string) =
  let settings = VerifySettings()
  settings.UseDirectory(snapshotsDir)
  settings.DisableDiff()
  Verifier.Verify(name, html, "html", settings).ToTask()


let dashboardRenderSnapshotTests = testList "Dashboard render snapshots" [
  testTask "renderSessionStatus ready" {
    let html = renderSessionStatus "Ready" "session-abc" "/home/user/project" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_ready" html
  }

  testTask "renderSessionStatus warming" {
    let html = renderSessionStatus "WarmingUp" "session-def" "/home/user/project" |> renderNode
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
        LastActivity = "eval" }
      { Id = "session-def"
        Status = "stopped"
        StatusMessage = None
        IsActive = false
        IsSelected = false
        ProjectsText = ""
        EvalCount = 0
        Uptime = ""
        WorkingDir = ""
        LastActivity = "" }
    ]
    let html = renderSessions sessions false "" |> renderNode
    do! verifyDashboard "dashboard_sessions" html
  }

  testTask "renderSessions empty" {
    let html = renderSessions [] false "" |> renderNode
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
        LastActivity = "eval" }
    ]
    let html = renderSessions sessions false "" |> renderNode
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
    let html = renderSessionStatus "Faulted" "session-err" @"C:\broken" |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_faulted" html
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
}

let standbyBadgeSseTests = testList "SSE standby badge" [

  test "ready standby shows green badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let r = mkRegion "sessions" "active s1 SageFs.Tests.fsproj C:\\Code\\Repos\\SageFs 42 1m Ready 0"
    let html = renderRegionForSse getState getMsg "✓ standby" r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--green)") "ready standby should use green"
  }

  test "warming standby shows yellow badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let r = mkRegion "sessions" "active s1 SageFs.Tests.fsproj C:\\Code\\Repos\\SageFs 42 1m Ready 0"
    let html = renderRegionForSse getState getMsg "⏳ standby" r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--fg-yellow)") "warming standby should use yellow"
  }

  test "invalidated standby shows red badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let r = mkRegion "sessions" "active s1 SageFs.Tests.fsproj C:\\Code\\Repos\\SageFs 42 1m Ready 0"
    let html = renderRegionForSse getState getMsg "⚠ standby" r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isTrue (html.Contains "standby") "should contain standby"
    Expect.isTrue (html.Contains "var(--red)") "invalidated standby should use red"
  }

  test "empty label shows no badge" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let r = mkRegion "sessions" "active s1 SageFs.Tests.fsproj C:\\Code\\Repos\\SageFs 42 1m Ready 0"
    let html = renderRegionForSse getState getMsg "" r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isFalse (html.Contains "standby") "empty label should not show standby badge"
  }

  test "StandbyInfo.label maps correctly" {
    Expect.equal (StandbyInfo.label StandbyInfo.NoPool) "" "NoPool -> empty"
    Expect.equal (StandbyInfo.label (StandbyInfo.Warming "")) "⏳ standby" "Warming empty"
    Expect.equal (StandbyInfo.label (StandbyInfo.Warming "2/4 Scanned 12 files")) "⏳ 2/4 Scanned 12 files" "Warming with progress"
    Expect.equal (StandbyInfo.label StandbyInfo.Ready) "✓ standby" "Ready"
    Expect.equal (StandbyInfo.label StandbyInfo.Invalidated) "⚠ standby" "Invalidated"
  }

  test "output region unaffected by standby label" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let r = mkRegion "output" "[12:00:00] [info] hello world"
    let html = renderRegionForSse getState getMsg "✓ standby" r |> Option.map renderNode |> Option.defaultValue ""
    Expect.isFalse (html.Contains "standby") "output region should not contain standby"
  }

  test "unknown region returns None" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let r = mkRegion "unknown" "whatever"
    Expect.isNone (renderRegionForSse getState getMsg "✓ standby" r) "unknown region -> None"
  }
]

let warmupProgressSseTests = testList "Standby warmup progress SSE" [
  test "warming badge with progress shows phase text" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let standbyLabel = StandbyInfo.label (StandbyInfo.Warming "2/4 Scanned 12 files")
    let r = mkRegion "sessions" "No sessions"
    let result = renderRegionForSse getState getMsg standbyLabel r
    match result with
    | Some node ->
      let html = renderNode node
      Expect.stringContains html "⏳ 2/4 Scanned 12 files" "should show progress"
    | None -> failtest "should render sessions region"
  }
  test "warming badge with empty progress shows default" {
    let getState _ = SessionState.Ready
    let getMsg _ = None
    let standbyLabel = StandbyInfo.label (StandbyInfo.Warming "")
    let r = mkRegion "sessions" "No sessions"
    let result = renderRegionForSse getState getMsg standbyLabel r
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
  standbyBadgeSseTests
  warmupProgressSseTests
]
