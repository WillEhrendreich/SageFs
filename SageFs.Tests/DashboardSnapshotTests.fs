module SageFs.Tests.DashboardSnapshotTests

open Expecto
open VerifyExpecto
open VerifyTests
open Falco.Markup
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
    let html = renderSessionStatus "Ready" "session-abc" 3 |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_ready" html
  }

  testTask "renderSessionStatus warming" {
    let html = renderSessionStatus "WarmingUp" "session-def" 5 |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_warming" html
  }

  testTask "renderEvalStats" {
    let html = renderEvalStats 42 123.4 5.0 1045.0 |> renderNode
    do! verifyDashboard "dashboard_evalStats" html
  }

  testTask "renderOutput with mixed lines" {
    let lines = [
      Some "12:30:45", "Result", "val x: int = 42"
      Some "12:30:46", "Error", "Type mismatch"
      None, "Info", "Loading..."
      Some "12:30:47", "System", "Hot reload"
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
      "Error", "Type mismatch", 5, 10
      "Warning", "Unused binding", 1, 1
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
        IsActive = true
        ProjectsText = "(MyProj.fsproj, Tests.fsproj)"
        EvalCount = 15
        Uptime = "3m"
        WorkingDir = @"C:\Code\MyProj"
        LastActivity = "eval" }
      { Id = "session-def"
        Status = "stopped"
        IsActive = false
        ProjectsText = ""
        EvalCount = 0
        Uptime = ""
        WorkingDir = ""
        LastActivity = "" }
    ]
    let html = renderSessions sessions |> renderNode
    do! verifyDashboard "dashboard_sessions" html
  }

  testTask "renderSessions empty" {
    let html = renderSessions [] |> renderNode
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
        IsActive = true
        ProjectsText = "(MyProj.fsproj)"
        EvalCount = 42
        Uptime = "15m"
        WorkingDir = @"C:\Code\MyProj"
        LastActivity = "eval" }
    ]
    let html = renderSessions sessions |> renderNode
    do! verifyDashboard "dashboard_sessions_singleActive" html
  }

  testTask "renderDiagnostics with zero line col" {
    let diags = [
      "Error", "General compilation error", 0, 0
    ]
    let html = renderDiagnostics diags |> renderNode
    do! verifyDashboard "dashboard_diagnostics_zeroLineCol" html
  }

  testTask "renderEvalStats zero evals" {
    let html = renderEvalStats 0 0.0 0.0 0.0 |> renderNode
    do! verifyDashboard "dashboard_evalStats_zero" html
  }

  testTask "renderSessionStatus faulted" {
    let html = renderSessionStatus "Faulted" "session-err" 0 |> renderNode
    do! verifyDashboard "dashboard_sessionStatus_faulted" html
  }

  testTask "renderOutput single result line" {
    let lines = [ Some "14:00:00", "Result", "val it: int = 0" ]
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


[<Tests>]
let allDashboardSnapshotTests = testList "Dashboard Snapshots" [
  dashboardRenderSnapshotTests
  keyboardHelpSnapshotTests
  edgeCaseSnapshotTests
  parserTests
]
