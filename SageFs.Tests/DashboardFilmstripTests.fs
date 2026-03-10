module SageFs.Tests.DashboardFilmstripTests

open System
open Expecto
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
      Expect.stringContains html DomIds.FilmstripPanel "should have filmstrip-panel id"
    }

    test "empty list shows No history message" {
      let html = renderSessionFilmstrip [] |> render
      Expect.stringContains html "No history" "should show No history message"
    }

    test "success entry renders success icon" {
      let html = renderSessionFilmstrip [ entry 0 "let x = 1" 42L EvalSuccess ] |> render
      Expect.stringContains html "✓" "should render success icon"
    }

    test "error entry renders error icon" {
      let html = renderSessionFilmstrip [ entry 0 "bad code" 10L EvalError ] |> render
      Expect.stringContains html "✗" "should render error icon"
    }

    test "cancelled entry renders cancelled icon" {
      let html = renderSessionFilmstrip [ entry 0 "long op" 5000L EvalCancelled ] |> render
      Expect.stringContains html "⊘" "should render cancelled icon"
    }

    test "entry label appears in output" {
      let html = renderSessionFilmstrip [ entry 0 "let answer = 42" 12L EvalSuccess ] |> render
      Expect.stringContains html "let answer = 42" "should contain entry label"
    }

    test "entry duration appears in output" {
      let html = renderSessionFilmstrip [ entry 0 "x" 123L EvalSuccess ] |> render
      Expect.stringContains html "123" "should show duration"
    }

    test "fast eval (under 100ms) gets fast CSS class" {
      let html = renderSessionFilmstrip [ entry 0 "x" 50L EvalSuccess ] |> render
      Expect.stringContains html "eval-fast" "should apply eval-fast class for < 100ms"
    }

    test "medium eval (100-500ms) gets medium CSS class" {
      let html = renderSessionFilmstrip [ entry 0 "x" 250L EvalSuccess ] |> render
      Expect.stringContains html "eval-medium" "should apply eval-medium class for 100-500ms"
    }

    test "slow eval (over 500ms) gets slow CSS class" {
      let html = renderSessionFilmstrip [ entry 0 "x" 800L EvalSuccess ] |> render
      Expect.stringContains html "eval-slow" "should apply eval-slow class for > 500ms"
    }

    test "multiple entries all appear in output" {
      let entries =
        [ entry 0 "let a = 1" 10L EvalSuccess
          entry 1 "let b = 2" 20L EvalSuccess
          entry 2 "let c = 3" 30L EvalError ]
      let html = renderSessionFilmstrip entries |> render
      Expect.stringContains html "let a = 1" "first entry label"
      Expect.stringContains html "let b = 2" "second entry label"
      Expect.stringContains html "let c = 3" "third entry label"
    }

    test "entry index appears in output" {
      let html = renderSessionFilmstrip [ entry 7 "do stuff" 15L EvalSuccess ] |> render
      Expect.stringContains html "#7" "should show frame index with # prefix"
    }

  ]

[<Tests>]
let filmstripSnapshotTests =
  testList "FilmstripPanel in DashboardSnapshot" [

    test "DashboardSnapshot has FilmstripPanel field" {
      let snap : DashboardSnapshot = {
        Version = "0.6.50"; SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
        WarmupProgress = ""; ThemeName = "default"; ConnectionLabel = None
        EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
        DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []
        DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []
        HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
        OutputPanel = Elem.div [] []; SessionsPanel = Elem.div [] []
        SessionPicker = Elem.div [] []; ThemePicker = Elem.div [] []
        ThemeVars = Elem.div [] []; BindingsPanel = Elem.div [] []
      }
      let html = snap.FilmstripPanel |> render
      Expect.isTrue (html.Length > 0) "FilmstripPanel should render non-empty HTML"
    }

    test "renderMainContent includes filmstrip-panel id" {
      let snap : DashboardSnapshot = {
        Version = "0.6.50"; SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
        WarmupProgress = ""; ThemeName = "default"; ConnectionLabel = None
        EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
        DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []
        DiagnosticsPanel = Elem.div [] []
        FilmstripPanel = renderSessionFilmstrip []
        HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
        OutputPanel = Elem.div [] []; SessionsPanel = Elem.div [] []
        SessionPicker = Elem.div [] []; ThemePicker = Elem.div [] []
        ThemeVars = Elem.div [] []; BindingsPanel = Elem.div [] []
      }
      let html = renderMainContent snap |> render
      Expect.stringContains html DomIds.FilmstripPanel "main content should include filmstrip panel"
    }

  ]
