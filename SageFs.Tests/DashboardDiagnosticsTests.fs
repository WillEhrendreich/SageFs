module SageFs.Tests.DashboardDiagnosticsTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private makeFeatureDiag
  (sev: Features.Diagnostics.DiagnosticSeverity)
  (msg: string)
  (startLine: int)
  (startCol: int)
  : Features.Diagnostics.Diagnostic =
  { Message = msg
    Subcategory = "typecheck"
    Range =
      { StartLine = startLine
        StartColumn = startCol
        EndLine = startLine
        EndColumn = startCol + msg.Length }
    Severity = sev }

let private renderToString (node: XmlNode) = renderNode node

[<Tests>]
let fromFeatureDiagTests =
  testList "Diagnostic.fromFeatureDiag" [

    test "Error severity maps to DiagError" {
      let d = makeFeatureDiag Features.Diagnostics.DiagnosticSeverity.Error "type mismatch" 5 3
      let result = Diagnostic.fromFeatureDiag d
      result.Severity |> Expect.equal "error severity should map to DiagError" DiagError
    }

    test "Warning severity maps to DiagWarning" {
      let d = makeFeatureDiag Features.Diagnostics.DiagnosticSeverity.Warning "unused value" 2 1
      let result = Diagnostic.fromFeatureDiag d
      result.Severity |> Expect.equal "warning severity should map to DiagWarning" DiagWarning
    }

    test "Hidden severity maps to DiagWarning" {
      let d = makeFeatureDiag Features.Diagnostics.DiagnosticSeverity.Hidden "hidden" 1 0
      let result = Diagnostic.fromFeatureDiag d
      result.Severity |> Expect.equal "hidden severity should fall back to DiagWarning" DiagWarning
    }

    test "Info severity maps to DiagWarning" {
      let d = makeFeatureDiag Features.Diagnostics.DiagnosticSeverity.Info "note" 1 0
      let result = Diagnostic.fromFeatureDiag d
      result.Severity |> Expect.equal "info severity should fall back to DiagWarning" DiagWarning
    }

    test "Message is preserved" {
      let msg = "The value 'foo' is not defined"
      let d = makeFeatureDiag Features.Diagnostics.DiagnosticSeverity.Error msg 1 0
      let result = Diagnostic.fromFeatureDiag d
      result.Message |> Expect.equal "message should round-trip" msg
    }

    test "StartLine maps to Line" {
      let d = makeFeatureDiag Features.Diagnostics.DiagnosticSeverity.Error "err" 42 0
      let result = Diagnostic.fromFeatureDiag d
      result.Line |> Expect.equal "StartLine should map to Line" 42
    }

    test "StartColumn maps to Col" {
      let d = makeFeatureDiag Features.Diagnostics.DiagnosticSeverity.Error "err" 1 17
      let result = Diagnostic.fromFeatureDiag d
      result.Col |> Expect.equal "StartColumn should map to Col" 17
    }

  ]

[<Tests>]
let renderCurrentDiagnosticsTests =
  testList "renderCurrentDiagnostics HTML" [

    test "empty list renders diagnostics-panel id" {
      let html = renderCurrentDiagnostics [] |> renderToString
      html |> Expect.stringContains "should have diagnostics-panel id" DomIds.DiagnosticsPanel
    }

    test "empty list renders no content (silent when empty)" {
      let html = renderCurrentDiagnostics [] |> renderToString
      (html.Contains("No diagnostics")) |> Expect.isFalse "should NOT show no-diagnostics message when empty (progressive disclosure)"
    }

    test "error diagnostic renders error CSS class" {
      let diags = [ { Severity = DiagError; Message = "type error"; Line = 3; Col = 5 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      html |> Expect.stringContains "should apply error CSS class" "diag-error"
    }

    test "warning diagnostic renders warning CSS class" {
      let diags = [ { Severity = DiagWarning; Message = "warning here"; Line = 1; Col = 1 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      html |> Expect.stringContains "should apply warning CSS class" "diag-warning"
    }

    test "error renders error icon" {
      let diags = [ { Severity = DiagError; Message = "err"; Line = 1; Col = 1 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      html |> Expect.stringContains "should show error icon" "🔴"
    }

    test "warning renders warning icon" {
      let diags = [ { Severity = DiagWarning; Message = "warn"; Line = 1; Col = 1 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      html |> Expect.stringContains "should show warning icon" "⚠️"
    }

    test "renders location for diagnostic with line and col" {
      let diags = [ { Severity = DiagError; Message = "err"; Line = 10; Col = 3 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      html |> Expect.stringContains "should render line:col location" "L10:3"
    }

    test "does not render location span when line and col are zero" {
      let diags = [ { Severity = DiagError; Message = "err"; Line = 0; Col = 0 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      (html.Contains "L0:0") |> Expect.isFalse "should not render zero location"
    }

    test "renders message text in output" {
      let diags = [ { Severity = DiagError; Message = "FS0001: type mismatch"; Line = 5; Col = 2 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      html |> Expect.stringContains "should contain message" "FS0001: type mismatch"
    }

    test "renders count badge for multiple diagnostics" {
      let diags =
        [ { Severity = DiagError; Message = "e1"; Line = 1; Col = 1 }
          { Severity = DiagError; Message = "e2"; Line = 2; Col = 1 }
          { Severity = DiagWarning; Message = "w1"; Line = 3; Col = 1 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      html |> Expect.stringContains "should show count badge" "3"
    }

    test "diagnostics wrapped in collapsible details element" {
      let diags = [ { Severity = DiagError; Message = "err"; Line = 1; Col = 1 } ]
      let html = renderCurrentDiagnostics diags |> renderToString
      // Not "<details>" — the accordion's open state is a Datastar signal
      // (signalDetails), so the tag carries "open"/"data-on-toggle" attributes.
      html |> Expect.stringContains "should wrap diagnostics in <details> for collapse" "<details"
      html |> Expect.stringContains "should have summary element with count badge" "<summary"
    }

    test "empty diagnostics have no details element" {
      let html = renderCurrentDiagnostics [] |> renderToString
      (html.Contains "<details") |> Expect.isFalse "empty diagnostics should not render collapsible wrapper"
    }

  ]

[<Tests>]
let diagnosticsPanelSnapshotTests =
  testList "DiagnosticsPanel in DashboardSnapshot" [

    test "DashboardSnapshot has DiagnosticsPanel field" {
      let snap : DashboardSnapshot = {
        Version = "0.6.48"; SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
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
        ActiveProject = None

        ProjectRoles = []

        App = SageFs.AppRun.AppRunState.NotRunning

      }
      let html = snap.DiagnosticsPanel |> renderToString
      (html.Length > 0) |> Expect.isTrue "DiagnosticsPanel should render non-empty HTML"
    }

    test "renderMainContent includes diagnostics-panel id" {
      let snap : DashboardSnapshot = {
        Version = "0.6.48"; SessionState = "ready"; SessionId = "test-id"; WorkingDir = @"C:\Code"
        WarmupProgress = ""; WorkflowLabel = "REPL"; ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
        EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
        DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []
        DiagnosticsPanel = renderCurrentDiagnostics []; FilmstripPanel = Elem.div [] []
        HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
        OutputPanel = Elem.div [] []; SessionsPanel = Elem.div [] []
        SessionPicker = Elem.div [] []; ThemePicker = Elem.div [] []
        ThemeVars = Elem.div [] []; BindingsPanel = Elem.div [] []
        AlarmPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []
        FrictionPanel = Elem.div [] []
        ActiveProject = None

        ProjectRoles = []

        App = SageFs.AppRun.AppRunState.NotRunning

      }
      let html = renderMainContent snap |> renderToString
      html |> Expect.stringContains "main content should include diagnostics panel" DomIds.DiagnosticsPanel
    }

  ]
