module SageFs.Tests.DashboardFilmstripTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private entry idx label durationMs outcome =
  { Index = idx
    Label = label
    DurationMs = durationMs
    Outcome = outcome
    Timestamp = DateTimeOffset.UtcNow }

let private render (node: XmlNode) = renderNode node

[<Tests>]
let filmstripRenderTests =
  testList "renderSessionFilmstrip HTML" [

    test "empty list renders filmstrip-panel id" {
      let html = renderSessionFilmstrip [] |> render
      html |> Expect.stringContains "should have filmstrip-panel id" DomIds.FilmstripPanel
    }

    test "empty list renders no content (silent when empty)" {
      let html = renderSessionFilmstrip [] |> render
      (html.Contains("No history")) |> Expect.isFalse "should NOT show No history message when empty (progressive disclosure)"
    }

    test "success entry renders success icon" {
      let html = renderSessionFilmstrip [ entry 0 "let x = 1" 42L EvalSuccess ] |> render
      html |> Expect.stringContains "should render success icon" "✓"
    }

    test "error entry renders error icon" {
      let html = renderSessionFilmstrip [ entry 0 "bad code" 10L EvalError ] |> render
      html |> Expect.stringContains "should render error icon" "✗"
    }

    test "cancelled entry renders cancelled icon" {
      let html = renderSessionFilmstrip [ entry 0 "long op" 5000L EvalCancelled ] |> render
      html |> Expect.stringContains "should render cancelled icon" "⊘"
    }

    test "entry label appears in output" {
      let html = renderSessionFilmstrip [ entry 0 "let answer = 42" 12L EvalSuccess ] |> render
      html |> Expect.stringContains "should contain entry label" "let answer = 42"
    }

    test "entry duration appears in output" {
      let html = renderSessionFilmstrip [ entry 0 "x" 123L EvalSuccess ] |> render
      html |> Expect.stringContains "should show duration" "123"
    }

    test "fast eval (under 100ms) gets fast CSS class" {
      let html = renderSessionFilmstrip [ entry 0 "x" 50L EvalSuccess ] |> render
      html |> Expect.stringContains "should apply eval-fast class for < 100ms" "eval-fast"
    }

    test "medium eval (100-500ms) gets medium CSS class" {
      let html = renderSessionFilmstrip [ entry 0 "x" 250L EvalSuccess ] |> render
      html |> Expect.stringContains "should apply eval-medium class for 100-500ms" "eval-medium"
    }

    test "slow eval (over 500ms) gets slow CSS class" {
      let html = renderSessionFilmstrip [ entry 0 "x" 800L EvalSuccess ] |> render
      html |> Expect.stringContains "should apply eval-slow class for > 500ms" "eval-slow"
    }

    test "multiple entries all appear in output" {
      let entries =
        [ entry 0 "let a = 1" 10L EvalSuccess
          entry 1 "let b = 2" 20L EvalSuccess
          entry 2 "let c = 3" 30L EvalError ]
      let html = renderSessionFilmstrip entries |> render
      html |> Expect.stringContains "first entry label" "let a = 1"
      html |> Expect.stringContains "second entry label" "let b = 2"
      html |> Expect.stringContains "third entry label" "let c = 3"
    }

    test "entry index appears in output" {
      let html = renderSessionFilmstrip [ entry 7 "do stuff" 15L EvalSuccess ] |> render
      html |> Expect.stringContains "should show frame index with # prefix" "#7"
    }

  ]

[<Tests>]
let filmstripSnapshotTests =
  testList "FilmstripPanel in DashboardSnapshot" [

    test "DashboardSnapshot has FilmstripPanel field" {
      let snap : DashboardSnapshot = {
        Version = "0.6.50"; SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
        WarmupProgress = ""; WorkflowLabel = "REPL"; ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
        EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
        DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []
        DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []
        HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
        OutputPanel = Elem.div [] []; SessionsPanel = Elem.div [] []
        SessionPicker = Elem.div [] []; ThemePicker = Elem.div [] []
        ThemeVars = Elem.div [] []; BindingsPanel = Elem.div [] []
        AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []
        FrictionPanel = Elem.div [] []
        CohortPanel = Elem.div [] []
        ActiveProject = None

        ProjectRoles = []

        App = SageFs.AppRun.AppRunState.NotRunning
        EvalToPixelP50Ms = None
        EvalToPixelP99Ms = None

      }
      let html = snap.FilmstripPanel |> render
      (html.Length > 0) |> Expect.isTrue "FilmstripPanel should render non-empty HTML"
    }

    test "renderMainContent includes filmstrip-panel id" {
      let snap : DashboardSnapshot = {
        Version = "0.6.50"; SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
        WarmupProgress = ""; WorkflowLabel = "REPL"; ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
        EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
        DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []
        DiagnosticsPanel = Elem.div [] []
        FilmstripPanel = renderSessionFilmstrip []
        HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
        OutputPanel = Elem.div [] []; SessionsPanel = Elem.div [] []
        SessionPicker = Elem.div [] []; ThemePicker = Elem.div [] []
        ThemeVars = Elem.div [] []; BindingsPanel = Elem.div [] []
        AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []
        FrictionPanel = Elem.div [] []
        CohortPanel = Elem.div [] []
        ActiveProject = None

        ProjectRoles = []

        App = SageFs.AppRun.AppRunState.NotRunning
        EvalToPixelP50Ms = None
        EvalToPixelP99Ms = None

      }
      let html = renderMainContent snap |> render
      html |> Expect.stringContains "main content should include filmstrip panel" DomIds.FilmstripPanel
    }

  ]
