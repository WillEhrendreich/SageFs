module SageFs.Server.DashboardFragments

open System
open System.IO
open Falco
open Falco.Markup
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.WarmUp
open SageFs.Affordances
open SageFs.Server.DashboardTypes

/// Use renderNode + sseStringElements instead of sseHtmlElements
/// (which prepends DOCTYPE to every fragment, causing Datastar to choke).
let ssePatchNode (ctx: HttpContext) (node: XmlNode) =
  Falco.Datastar.Response.sseStringElements ctx (renderNode node)

let renderKeyboardHelp () =
  let shortcut key desc =
    Elem.tr [] [
      Elem.td [ Attr.style "padding: 2px 8px; font-family: monospace; color: var(--fg-blue);" ] [ Text.raw key ]
      Elem.td [ Attr.style "padding: 2px 8px;" ] [ Text.raw desc ]
    ]
  Elem.div [ Attr.id DomIds.KeyboardHelp; Attr.style "margin-top: 0.5rem;" ] [
    Elem.table [ Attr.style "font-size: 0.85rem; border-collapse: collapse;" ] [
      shortcut "Alt+Enter" "Evaluate code"
      shortcut "Tab" "Insert 2 spaces (in editor)"
      shortcut "Ctrl+L" "Clear output"
    ]
  ]

/// Generate a JS object literal mapping theme names → CSS variable strings.
/// Render the completion dropdown as server-side HTML for Datastar morph.
/// Each item has data-on-click that calls the client-side insertion utility.
let renderCompletionDropdown (items: Features.AutoCompletion.CompletionItem list) (cursorPos: int) =
  let escJs (s: string) = s.Replace("\\", "\\\\").Replace("'", "\\'")
  match items with
  | [] ->
    Elem.div
      [ Attr.id DomIds.CompletionDropdown
        Attr.style "display:none; position:absolute; bottom:100%; left:0; max-height:200px; overflow-y:auto; background:var(--bg-default); border:1px solid var(--bg-selection); border-radius:4px; z-index:100; min-width:200px; font-size:0.85em; box-shadow:0 -2px 8px rgba(0,0,0,0.3);" ]
      []
  | items ->
    Elem.div
      [ Attr.id DomIds.CompletionDropdown
        Attr.style "display:block; position:absolute; bottom:100%; left:0; max-height:200px; overflow-y:auto; background:var(--bg-default); border:1px solid var(--bg-selection); border-radius:4px; z-index:100; min-width:200px; font-size:0.85em; box-shadow:0 -2px 8px rgba(0,0,0,0.3);" ]
      (items |> List.mapi (fun i item ->
        Elem.div
          [ Attr.class' "comp-item"
            Attr.style (sprintf "padding:2px 6px;cursor:pointer;%s" (match i with | 0 -> "background:var(--bg-selection)" | _ -> ""))
            Ds.onEvent ("click", sprintf "window._insertComp('%s',%d)" (escJs item.ReplacementText) cursorPos) ]
          [ Text.raw item.DisplayText
            Elem.span [ Attr.style "opacity:0.5;font-size:0.8em;margin-left:4px;" ] [
              Text.raw (sprintf "(%s)" (Features.AutoCompletion.CompletionKind.label item.Kind))
            ]
          ]))

/// Render a <style id="theme-vars"> element with CSS variables for the given theme.
/// Pushed via SSE on session switch — Datastar morphs the existing style element.
let renderThemeVars (themeName: string) =
  let config =
    ThemePresets.all
    |> List.tryFind (fun (n, _) -> n = themeName)
    |> Option.map snd
    |> Option.defaultValue Theme.defaults
  Elem.style [ Attr.id DomIds.ThemeVars ] [
    Text.raw (sprintf ":root { %s }" (Theme.toCssVariables config))
  ]

/// Render a <select id="theme-picker"> with the correct option selected.
/// Pushed via SSE on session switch — Datastar morphs the existing picker.
/// Uses Ds.bind for two-way signal sync and Ds.onEvent to POST theme change.
let renderThemePicker (selectedTheme: string) =
  Elem.select
    [ Attr.id DomIds.ThemePicker
      Attr.class' "theme-select"
      Ds.bind Signals.Theme
      Ds.onEvent ("change", "@post('/dashboard/set-theme')") ]
    (ThemePresets.all |> List.map (fun (name, _) ->
      Elem.option
        ([ Attr.value name ] @ (match name = selectedTheme with | true -> [ Attr.create "selected" "selected" ] | false -> []))
        [ Text.raw name ]))


let renderSessionStatus (sessionState: string) (sessionId: string) (workingDir: string) (warmupProgress: string) (workflowLabel: string) =
  let warmupNode =
    match warmupProgress.Length > 0 with
    | true ->
      [ Elem.br []
        Elem.span [ Attr.class' "meta warmup-progress" ] [
          Text.raw (sprintf "⏳ %s" warmupProgress)
        ] ]
    | false -> []
  let workflowBadgeClass =
    match workflowLabel with
    | "Live" -> "badge badge-live"
    | _ -> "badge badge-workflow"
  let workflowNode =
    [ Elem.span [ Attr.class' workflowBadgeClass ] [
        Text.raw workflowLabel
      ] ]
  match sessionState with
  | "Ready" ->
    Elem.div [ Attr.id DomIds.SessionStatus; Attr.create "data-working-dir" workingDir ] [
      yield Elem.span [ Attr.class' "status status-ready" ] [ Text.raw sessionState ]
      yield! workflowNode
      yield Elem.br []
      yield Elem.span [ Attr.class' "meta" ] [
        Text.raw (sprintf "Session: %s | CWD: %s" sessionId workingDir)
      ]
      yield! warmupNode
    ]
  | _ ->
    let statusClass =
      match sessionState with
      | "WarmingUp" -> "status-warming"
      | _ -> "status-faulted"
    Elem.div [ Attr.id DomIds.SessionStatus; Attr.create "data-working-dir" workingDir ] [
      yield Elem.span [ Attr.class' (sprintf "status %s" statusClass) ] [ Text.raw sessionState ]
      yield! workflowNode
      yield Elem.br []
      yield Elem.span [ Attr.class' "meta" ] [
        Text.raw (sprintf "Session: %s | CWD: %s" sessionId workingDir)
      ]
      yield! warmupNode
    ]

/// Render system alarm banner — visible when ElmLoop throws at any catch site.
/// Empty list renders a hidden placeholder so Datastar can morph it away.
let private disclosureSummaryStyle =
  "cursor: pointer; font-weight: bold; font-size: 0.9rem; user-select: none;"

let renderAlarmBanner (alarms: SystemAlarmEntry list) =
  match alarms with
  | [] ->
    Elem.div [ Attr.id DomIds.AlarmBanner; Attr.style "display:none;" ] []
  | _ ->
    let alarmCount = alarms.Length
    let alarmCountLabel =
      sprintf "%d active alarm%s" alarmCount (if alarmCount = 1 then "" else "s")
    let alarmEntries =
      alarms |> List.map (fun alarm ->
        Elem.div [ Attr.class' "alarm-entry" ] [
          Elem.span [ Attr.class' "alarm-phase meta" ] [
            Text.raw (sprintf "[%s]" alarm.Phase)
          ]
          Elem.span [ Attr.class' "alarm-message" ] [
            Text.raw (sprintf " %s" alarm.Message)
          ]
          Elem.span [ Attr.class' "alarm-ts meta" ] [
            Text.raw (sprintf " @ %s" (alarm.Timestamp.ToLocalTime().ToString("HH:mm:ss")))
          ]
        ])
    Elem.div [ Attr.id DomIds.AlarmBanner; Attr.class' "alarm-banner" ] [
      Elem.details [] [
        Elem.summary [ Attr.style disclosureSummaryStyle ] [
          Elem.span [ Attr.class' "alarm-icon" ] [ Text.raw "🚨" ]
          Elem.span [ Attr.class' "alarm-title" ] [
            Text.raw (sprintf " System Alarm (%d)" alarmCount)
          ]
        ]
        Elem.div [ Attr.style "margin-top: 0.5rem;" ] [
          Elem.div [ Attr.class' "alarm-banner-header" ] [
            Elem.span [ Attr.class' "meta" ] [ Text.raw alarmCountLabel ]
            Elem.button
              [ Attr.class' "alarm-dismiss"
                Attr.title "Dismiss all alarms"
                Ds.onClick (Ds.post "/dashboard/dismiss-alarm") ]
              [ Text.raw "✕ dismiss" ]
          ]
          yield! alarmEntries
        ]
      ]
    ]

let private renderDisableWarmupAutoOpenButton (style: string) =
  Elem.button
    [ Attr.class' "eval-btn"
      Attr.style style
      Ds.indicator Signals.ConfigLoading
      Ds.attr' ("disabled", "$configLoading")
      Ds.onClick (Ds.post "/dashboard/config/disable-auto-open") ]
    [ Elem.span [ Ds.show "$configLoading" ] [ Text.raw "⏳ " ]
      Elem.span [ Ds.show "!$configLoading" ] [ Text.raw "⚙ " ]
      Text.raw "Disable Warmup Auto-Open" ]

/// Render daemon health as an HTML panel — shows status, uptime, memory, sessions, and tests.
let renderDaemonHealth (view: DaemonHealthView) =
  let emoji = Features.DaemonHealth.healthEmoji view.OverallHealth
  let label = Features.DaemonHealth.healthLabel view.OverallHealth
  let isNominal (s: Features.SessionHealthSummary) =
    match s.Status with
    | Features.SessionHealthStatus.Ready
    | Features.SessionHealthStatus.Evaluating
    | Features.SessionHealthStatus.WarmingUp -> true
    | Features.SessionHealthStatus.Faulted
    | Features.SessionHealthStatus.Stopped -> false
  let sessionSummaryText (summaries: Features.SessionHealthSummary list) =
    match summaries with
    | [] -> None
    | [s] ->
      Some (sprintf "%s %s [%s]"
        (Features.DaemonHealth.sessionStatusEmoji s.Status)
        s.ProjectName
        (Features.DaemonHealth.sessionStatusLabel s.Status))
    | _ when summaries.Length <= 3 ->
      Some (summaries
        |> List.map (fun s -> sprintf "%s %s" (Features.DaemonHealth.sessionStatusEmoji s.Status) s.ProjectName)
        |> String.concat " · ")
    | _ ->
      let degraded = summaries |> List.filter (not << isNominal)
      match degraded with
      | [] -> Some (sprintf "📦 %d sessions · all ready" summaries.Length)
      | problems ->
        let names = problems |> List.map (fun s -> s.ProjectName) |> String.concat ", "
        Some (sprintf "⚠️ %d sessions · %d degraded: %s" summaries.Length problems.Length names)
  Elem.div [ Attr.id DomIds.DaemonHealth; Attr.class' "meta" ] [
    Elem.span [ Attr.style "font-weight: bold;" ] [
      let statusText =
        match view.SessionSummaries with
        | [] -> sprintf "%s Ready · No active sessions" emoji
        | _ -> sprintf "%s %s" emoji label
      Text.raw (sprintf "%s · SageFs %s · up %s · %dMB"
        statusText view.Version view.UptimeLabel view.MemoryMB)
    ]
    match sessionSummaryText view.SessionSummaries with
    | None -> ()
    | Some txt ->
      Elem.span [ Attr.class' "session-health-list"; Attr.style "margin-left: 0.5rem;" ] [
        Text.raw txt
      ]
    // Test counts removed from health row — they live in the Live Testing panel.
    // When live testing is inactive, no stale counts bleed into the health bar.
  ]

/// Render failure narratives as a dashboard panel — shows recent test failures with context.
/// Silent when there are no failures — absence of red is the signal.
let renderFailureNarratives (view: FailureNarrativesPanelView) =
  Elem.div [ Attr.id DomIds.FailureNarratives; Attr.class' "failure-narratives-panel" ] [
    match view.TotalFailureCount with
    | 0 -> ()
    | total ->
      let badgeText =
        match view.SuppressedCount with
        | 0 when view.Entries.Length < total ->
          sprintf "🔴 %d failure%s · showing top %d" total (if total = 1 then "" else "s") view.Entries.Length
        | 0 ->
          sprintf "🔴 %d failure%s" total (if total = 1 then "" else "s")
        | suppressed when view.Entries.IsEmpty ->
          sprintf "🔴 %d failure%s · %d have no baseline yet" total (if total = 1 then "" else "s") suppressed
        | suppressed ->
          sprintf "🔴 %d failure%s · %d with context · %d no baseline" total (if total = 1 then "" else "s") view.Entries.Length suppressed
      Elem.details [] [
        Elem.summary [ Attr.style disclosureSummaryStyle ] [
          Elem.span [ Attr.class' "failure-count-badge"; Attr.style "font-weight: bold; margin-right: 0.5rem;" ] [
            Text.raw badgeText
          ]
        ]
        Elem.div [ Attr.style "margin-top: 0.25rem;" ] [
          for entry in view.Entries do
            Elem.div [ Attr.class' "narrative-entry"; Attr.style "margin-top: 0.25rem;" ] [
              Elem.span [ Attr.class' "narrative-test-name"; Attr.style "font-weight: bold;" ] [
                Text.raw (
                  let shortName =
                    let parts = entry.TestName.Split('.')
                    if parts.Length > 1 then parts.[parts.Length - 1] else entry.TestName
                  sprintf "🔴 %s" shortName)
              ]
              match entry.TimeSinceLabel with
              | Some label ->
                Elem.span [ Attr.class' "meta narrative-timing"; Attr.style "margin-left: 0.5rem;" ] [
                  Text.raw (sprintf "was passing %s" label)
                ]
              | None -> ()
              Elem.span [ Attr.class' "narrative-summary"; Attr.style "margin-left: 0.5rem;" ] [
                Text.raw entry.Summary
              ]
              match entry.CausalLabels with
              | [] -> ()
              | labels ->
                Elem.span [ Attr.class' "meta narrative-causal"; Attr.style "margin-left: 0.5rem;" ] [
                  Text.raw (sprintf "→ %s" (labels |> String.concat ", "))
                ]
              if entry.HasPropertyViolation then
                Elem.span [ Attr.class' "meta narrative-property"; Attr.style "margin-left: 0.5rem;" ] [
                  Text.raw "⚡ property violation"
                ]
            ]
        ]
      ]
  ]

/// Render eval stats as an HTML fragment — includes sparkline and P50/P95 latency.
let renderEvalStats (stats: EvalStatsView) =
  Elem.div [ Attr.id DomIds.EvalStats; Attr.class' "meta" ] [
    Text.raw (sprintf "%d evals · avg %.0fms · min %.0fms · max %.0fms" stats.Count stats.AvgMs stats.MinMs stats.MaxMs)
    match stats.Sparkline with
    | "" -> ()
    | sparkline ->
      Elem.span [ Attr.class' "eval-sparkline"; Attr.title "Recent eval latency (oldest → newest)" ] [
        Text.raw (sprintf " %s" sparkline)
      ]
      Elem.span [ Attr.class' "eval-percentiles meta" ] [
        Text.raw (sprintf " · P50 %s · P95 %s"
          (stats.P50Ms |> Option.map (sprintf "%.0fms") |> Option.defaultValue "—")
          (stats.P95Ms |> Option.map (sprintf "%.0fms") |> Option.defaultValue "—"))
      ]
  ]

/// Render a pipeline stage badge for the railway visualization.
let private renderStage (stage: PipelineStageView) =
  let (icon, cssClass) =
    match stage.Outcome with
    | StageSuccess -> ("✓", "stage-success")
    | StageFailure _ -> ("✗", "stage-failure")
  Elem.span [ Attr.class' (sprintf "pipeline-stage %s" cssClass) ] [
    Text.raw (sprintf "%s %s" stage.Name icon)
    Elem.span [ Attr.class' "stage-duration" ] [
      Text.raw (sprintf " [%.0fms]" stage.DurationMs)
    ]
  ]

/// Render the arrow connector between pipeline stages.
let private renderArrow () =
  Elem.span [ Attr.class' "pipeline-arrow" ] [ Text.raw " → " ]

/// Render the eval pipeline as a railway visualization.
/// Shows: Parse ✓ [12ms] → TypeCheck ✓ [45ms] → Execute ✓ [363ms] [420ms total]
let renderRailway (railway: PipelineRailwayView) =
  Elem.div [ Attr.class' "pipeline-railway" ] [
    match railway.Stages with
    | [] ->
      yield Elem.span [ Attr.class' "meta" ] [ Text.raw "No pipeline stages" ]
    | stages ->
      yield! stages
        |> List.mapi (fun i stage ->
          match i = 0 with
          | true -> [ renderStage stage ]
          | false -> [ renderArrow (); renderStage stage ])
        |> List.concat
      yield Elem.span [ Attr.class' "pipeline-total" ] [
        Text.raw (sprintf " [%.0fms total]" railway.TotalMs)
      ]
  ]

/// Map a tree-sitter capture name to the CSS class suffix.
let captureToCssClass (capture: string) =
  match capture with
  | s when s.StartsWith("keyword", System.StringComparison.Ordinal) -> "syn-keyword"
  | s when s.StartsWith("string", System.StringComparison.Ordinal) -> "syn-string"
  | s when s.StartsWith("comment", System.StringComparison.Ordinal) -> "syn-comment"
  | s when s.StartsWith("number", System.StringComparison.Ordinal) -> "syn-number"
  | s when s.StartsWith("operator", System.StringComparison.Ordinal) -> "syn-operator"
  | s when s.StartsWith("type", System.StringComparison.Ordinal) -> "syn-type"
  | s when s.StartsWith("function", System.StringComparison.Ordinal) -> "syn-function"
  | s when s.StartsWith("variable", System.StringComparison.Ordinal) -> "syn-variable"
  | s when s.StartsWith("punctuation", System.StringComparison.Ordinal) -> "syn-punctuation"
  | s when s.StartsWith("constant", System.StringComparison.Ordinal) -> "syn-constant"
  | s when s.StartsWith("module", System.StringComparison.Ordinal) -> "syn-module"
  | s when s.StartsWith("attribute", System.StringComparison.Ordinal) -> "syn-attribute"
  | s when s.StartsWith("property", System.StringComparison.Ordinal) -> "syn-property"
  | s when s.StartsWith("boolean", System.StringComparison.Ordinal) -> "syn-constant"
  | _ -> ""

/// Render a single line of code with syntax highlighting as HTML spans.
let renderHighlightedLine (spans: ColorSpan array) (line: string) : XmlNode list =
  match spans.Length = 0 || line.Length = 0 with
  | true -> [ Text.raw (System.Net.WebUtility.HtmlEncode line) ]
  | false ->
    let nodes = ResizeArray<XmlNode>()
    let mutable pos = 0
    for span in spans do
      match span.Start < pos with
      | true -> ()
      | false ->
      match span.Start > pos && pos < line.Length with
      | true ->
        let gapEnd = min span.Start line.Length
        nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode(line.Substring(pos, gapEnd - pos))))
        pos <- gapEnd
      | false -> ()
      match span.Start >= 0 && span.Start < line.Length with
      | true ->
        let end' = min (span.Start + span.Length) line.Length
        let text = line.Substring(span.Start, end' - span.Start)
        // Map fg packed RGB to a CSS class using precomputed lookup table
        let cssClass =
          match syntaxColorLookup.TryGetValue(span.Fg) with
          | true, cls -> cls
          | false, _ -> ""
        match cssClass <> "" with
        | true ->
          nodes.Add(Elem.span [ Attr.class' cssClass ] [ Text.raw (System.Net.WebUtility.HtmlEncode text) ])
        | false ->
          nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode text))
        pos <- end'
      | false -> ()
    match pos < line.Length with
    | true ->
      nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode(line.Substring(pos))))
    | false -> ()
    nodes |> Seq.toList

/// Render output lines as an HTML fragment.
let renderOutput (lines: OutputLine list) =
  Elem.div [ Attr.id DomIds.OutputPanel ] [
    match lines.IsEmpty with
    | true ->
      Elem.span [ Attr.class' "meta" ] [ Text.raw "No output yet" ]
    | false ->
      yield! lines |> List.map (fun line ->
        let css = OutputLineKind.toCssClass line.Kind
        Elem.div [ Attr.class' (sprintf "output-line %s" css) ] [
          match line.Timestamp with
          | Some t ->
            Elem.span [ Attr.class' "meta"; Attr.style "margin-right: 0.5rem;" ] [
              Text.raw t
            ]
          | None -> ()
          match (line.Kind = ResultLine || line.Kind = InfoLine) && SyntaxHighlight.isAvailable () with
          | true ->
            let allSpans = SyntaxHighlight.tokenize Theme.defaults line.Text
            match allSpans.Length > 0 with
            | true -> yield! renderHighlightedLine allSpans.[0] line.Text
            | false -> Text.raw (System.Net.WebUtility.HtmlEncode line.Text)
          | false ->
            Text.raw (System.Net.WebUtility.HtmlEncode line.Text)
        ])
  ]

/// Render diagnostics as an HTML fragment.
let renderDiagnostics (diags: Diagnostic list) =
  Elem.div [ Attr.id DomIds.DiagnosticsPanel; Attr.class' "log-box" ] [
    match diags.IsEmpty with
    | true ->
      Elem.span [ Attr.class' "meta" ] [ Text.raw "No diagnostics" ]
    | false ->
      yield! diags |> List.map (fun diag ->
        let cls = DiagSeverity.toCssClass diag.Severity
        Elem.div [ Attr.class' (sprintf "diag %s" cls) ] [
          Elem.span [ Attr.style "margin-right: 0.25rem;" ] [
            Text.raw (DiagSeverity.toIcon diag.Severity)
          ]
          match diag.Line > 0 || diag.Col > 0 with
          | true ->
            Elem.span [ Attr.class' "diag-location" ] [
              Text.raw (sprintf "L%d:%d" diag.Line diag.Col)
            ]
          | false -> ()
          Elem.span [] [
            Text.raw (sprintf " %s" diag.Message)
          ]
        ])
  ]


/// Render the session picker — shown in the main area when no sessions exist.
let renderSessionPicker (previous: PreviousSession list) =
  Elem.div [ Attr.id DomIds.SessionPicker ] [
    Elem.div [ Attr.class' "picker-container" ] [
      Elem.h2 [] [ Text.raw "Start a Session" ]
      Elem.p [ Attr.class' "meta"; Attr.style "text-align: center; max-width: 500px;" ] [
        Text.raw "Choose how to get started. You can create a new session or resume a previous one."
      ]
      Elem.div [ Attr.class' "picker-options" ] [
        // Option 1: Create in temp directory
        Elem.div
          [ Attr.class' "picker-card"
            Ds.indicator Signals.TempLoading
            Ds.onClick (Ds.post "/dashboard/session/create-temp") ]
          [ Elem.h3 [] [
              Elem.span [ Ds.show "$tempLoading" ] [ Text.raw "⏳ " ]
              Elem.span [ Ds.show "!$tempLoading" ] [ Text.raw "⚡ " ]
              Text.raw "Quick Start" ]
            Elem.p [] [ Text.raw "Create a new session in a temporary directory. Good for quick experiments and throwaway work." ] ]
        // Option 2: Create in custom directory
        Elem.div [ Attr.class' "picker-card"; Attr.style "cursor: default;" ] [
          Elem.h3 [] [ Text.raw "📁 Open Directory" ]
          Elem.p [] [ Text.raw "Create a session in a specific directory with your projects." ]
          Elem.div [ Attr.class' "picker-form"; Attr.style "margin-top: 0.75rem;" ] [
            Elem.input
              [ Attr.class' "eval-input"
                Attr.style "min-height: auto; height: 2rem;"
                Ds.bind Signals.NewSessionDir
                Attr.create "placeholder" @"C:\path\to\project" ]
            Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 0.5rem;" ] [
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                  Ds.indicator Signals.DiscoverLoading
                  Ds.attr' ("disabled", "$discoverLoading")
                  Ds.onClick (Ds.post "/dashboard/discover-projects") ]
                [ Elem.span [ Ds.show "$discoverLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$discoverLoading" ] [ Text.raw "🔍 " ]
                  Text.raw "Discover" ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                  Ds.indicator Signals.CreateLoading
                  Ds.attr' ("disabled", "$createLoading")
                  Ds.onClick (Ds.post "/dashboard/session/create") ]
                [ Elem.span [ Ds.show "$createLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$createLoading" ] [ Text.raw "➕ " ]
                  Text.raw "Create" ]
            ]
            renderDisableWarmupAutoOpenButton "margin-top: 0.5rem; width: 100%; font-size: 0.8rem;"
            Elem.div [ Attr.id DomIds.DiscoveredProjects ] []
          ]
        ]
      ]
      match previous.IsEmpty with
      | false ->
        Elem.div [ Attr.class' "picker-previous" ] [
          Elem.h3 [ Attr.style "color: var(--fg-blue); margin-bottom: 0.5rem;" ] [
            Text.raw "📋 Resume Previous"
          ]
          Elem.p [ Attr.class' "meta"; Attr.style "margin-bottom: 0.5rem;" ] [
            Text.raw "Sessions from the last 90 days. Retention is configurable."
          ]
          yield! previous |> List.map (fun s ->
            let age =
              let span = DateTime.UtcNow - s.LastSeen
              match span.TotalDays >= 1.0 with
              | true -> sprintf "%.0fd ago" span.TotalDays
              | false ->
                match span.TotalHours >= 1.0 with
                | true -> sprintf "%.0fh ago" span.TotalHours
                | false -> sprintf "%.0fm ago" span.TotalMinutes
            Elem.div
              [ Attr.class' "picker-session-row"
                Ds.onClick (Ds.post (sprintf "/dashboard/session/resume/%s" s.Id)) ]
              [ Elem.div [ Attr.style "flex: 1; min-width: 0;" ] [
                  Elem.div [ Attr.class' "flex-row"; Attr.style "gap: 0.5rem;" ] [
                    Elem.span [ Attr.style "font-weight: bold;" ] [ Text.raw s.Id ]
                    Elem.span [ Attr.class' "meta" ] [ Text.raw age ]
                  ]
                  match s.WorkingDir.Length > 0 with
                  | true ->
                    Elem.div
                      [ Attr.style "font-size: 0.75rem; color: var(--fg-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                        Attr.title s.WorkingDir ]
                      [ Text.raw (sprintf "📁 %s" s.WorkingDir) ]
                  | false -> ()
                  match s.Projects.IsEmpty with
                  | false ->
                    Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 2px; flex-wrap: wrap;" ] [
                      yield! s.Projects |> List.map (fun p ->
                        Elem.span
                          [ Attr.class' "badge"; Attr.style "background: var(--bg-focus); color: var(--fg-dim);" ]
                          [ Text.raw (Path.GetFileName p) ])
                    ]
                  | true -> ()
                ]
                Elem.span [ Attr.style "color: var(--fg-blue); font-size: 0.85rem;" ] [ Text.raw "▶" ]
              ])
        ]
      | true -> ()
    ]
  ]

/// Render an empty session picker (hidden — sessions exist).
let renderSessionPickerEmpty =
  Elem.div [ Attr.id DomIds.SessionPicker ] []

// ── Test Filter Bar (signal-driven, zero-JS filtering) ──────────────

/// Map TreemapStatus to the signal value used in Ds.show expressions.
let treemapStatusToFilterValue (status: Features.LiveTesting.TreemapStatus) =
  match status with
  | Features.LiveTesting.TreemapStatus.Passed -> "passed"
  | Features.LiveTesting.TreemapStatus.Failed -> "failed"
  | Features.LiveTesting.TreemapStatus.Running -> "running"
  | Features.LiveTesting.TreemapStatus.Skipped -> "skipped"
  | Features.LiveTesting.TreemapStatus.Other -> "other"

/// Render a filter toggle bar for test status filtering.
/// Uses Datastar signals: clicking a button sets $testFilter, Ds.show on entries filters display.
let renderTestFilterBar (entries: Features.LiveTesting.TestTreemapEntry array) : XmlNode =
  let countByStatus status =
    entries |> Array.filter (fun e -> e.Status = status) |> Array.length
  let passedCount = countByStatus Features.LiveTesting.TreemapStatus.Passed
  let failedCount = countByStatus Features.LiveTesting.TreemapStatus.Failed
  let runningCount = countByStatus Features.LiveTesting.TreemapStatus.Running
  let skippedCount = countByStatus Features.LiveTesting.TreemapStatus.Skipped
  let filterBtn (label: string) (value: string) (count: int) (color: string) =
    Elem.button
      [ Attr.class' "test-filter-btn"
        Ds.onEvent ("click", sprintf "$testFilter = '%s'" value)
        Ds.show (sprintf "$testFilter !== '%s'" value)
        Attr.style (sprintf "background:transparent;border:1px solid %s;color:%s;padding:1px 5px;font-size:0.6rem;border-radius:3px;cursor:pointer;margin-right:2px;" color color) ]
      [ Text.raw (sprintf "%s %d" label count) ]
  let activeBtn (label: string) (value: string) (count: int) (color: string) =
    Elem.button
      [ Attr.class' "test-filter-btn test-filter-active"
        Ds.onEvent ("click", "$testFilter = 'all'")
        Ds.show (sprintf "$testFilter === '%s'" value)
        Attr.style (sprintf "background:%s;color:#fff;padding:1px 5px;font-size:0.6rem;border-radius:3px;cursor:pointer;margin-right:2px;border:1px solid %s;" color color) ]
      [ Text.raw (sprintf "%s %d ✕" label count) ]
  Elem.div
    [ Attr.class' "test-filter-bar"
      Attr.style "display:flex;align-items:center;gap:2px;margin-bottom:4px;flex-wrap:wrap;" ]
    [ yield Elem.span
        [ Attr.style "font-size:0.6rem;color:var(--fg-dim);margin-right:4px;" ]
        [ Text.raw "Filter:" ]
      yield filterBtn "✓" "passed" passedCount "var(--fg-green,#27ae60)"
      yield activeBtn "✓" "passed" passedCount "var(--fg-green,#27ae60)"
      yield filterBtn "✗" "failed" failedCount "var(--fg-red,#e74c3c)"
      yield activeBtn "✗" "failed" failedCount "var(--fg-red,#e74c3c)"
      match runningCount > 0 with
      | true ->
        yield filterBtn "⟳" "running" runningCount "var(--fg-blue,#3498db)"
        yield activeBtn "⟳" "running" runningCount "var(--fg-blue,#3498db)"
      | false -> ()
      match skippedCount > 0 with
      | true ->
        yield filterBtn "⊘" "skipped" skippedCount "var(--fg-yellow,#f39c12)"
        yield activeBtn "⊘" "skipped" skippedCount "var(--fg-yellow,#f39c12)"
      | false -> () ]

// ── Test Treemap (WizTree-style: area = duration) ─────────────────

/// Render a squarified treemap of test results where area = duration.
/// Failed tests are red, passed are green — instantly see slow + broken.
let renderTestTreemap (entries: Features.LiveTesting.TestTreemapEntry array) : XmlNode =
  match entries.Length with
  | 0 -> Elem.div [] []
  | _ ->
    let rects = Features.LiveTesting.TestTreemap.layout 320.0 180.0 entries
    Elem.div
      [ Attr.style "position:relative;width:320px;height:180px;border-radius:4px;overflow:hidden;background:var(--bg-focus,#1a1a1a);margin-top:4px;" ]
      [ yield! rects |> Array.map (fun r ->
          let bgColor =
            match r.Entry.Status with
            | Features.LiveTesting.TreemapStatus.Passed -> "var(--fg-green,#27ae60)"
            | Features.LiveTesting.TreemapStatus.Failed -> "var(--fg-red,#e74c3c)"
            | Features.LiveTesting.TreemapStatus.Running -> "var(--fg-blue,#3498db)"
            | Features.LiveTesting.TreemapStatus.Skipped -> "var(--fg-yellow,#f39c12)"
            | Features.LiveTesting.TreemapStatus.Other -> "var(--bg-focus,#2a2a2a)"
          let durationLabel =
            match r.Entry.DurationMs with
            | ms when ms >= 1000.0 -> sprintf "%.1fs" (ms / 1000.0)
            | ms when ms >= 1.0 -> sprintf "%.0fms" ms
            | ms -> sprintf "%.2fms" ms
          let title = sprintf "%s — %s" r.Entry.DisplayName durationLabel
          let showLabel = r.W >= 28.0 && r.H >= 14.0
          let showDuration = r.W >= 40.0 && r.H >= 22.0
          let statusFilter = treemapStatusToFilterValue r.Entry.Status
          Elem.div
            [ Attr.style (sprintf "position:absolute;left:%.1fpx;top:%.1fpx;width:%.1fpx;height:%.1fpx;background:%s;opacity:0.85;border:0.5px solid rgba(0,0,0,0.3);overflow:hidden;box-sizing:border-box;"
                r.X r.Y r.W r.H bgColor)
              Attr.title title
              Ds.show (sprintf "$testFilter === 'all' || $testFilter === '%s'" statusFilter) ]
            [ match showLabel with
              | true ->
                Elem.div [ Attr.style "font-size:0.5rem;color:#fff;padding:1px 2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;line-height:1.1;" ] [
                  Text.raw r.Entry.DisplayName
                ]
              | false -> ()
              match showDuration with
              | true ->
                Elem.div [ Attr.style "font-size:0.45rem;color:rgba(255,255,255,0.7);padding:0 2px;line-height:1;" ] [
                  Text.raw durationLabel
                ]
              | false -> () ]) ]

/// Render a hierarchical tree view of a binding value.
/// Handles simple values, records, lists, and nested structures.
let rec renderValueTree (value: string) (depth: int) : XmlNode list =
  let indent = String.replicate (depth * 2) " "
  let isComplex = value.Contains("{") || value.Contains("[") || value.Contains("(") && value.Contains(")")
  
  match isComplex with
  | false ->
    // Simple scalar value — render on one line with full text (no truncation)
    [ Elem.span
        [ Attr.style "color:var(--fg-green,#98c379);font-size:0.65rem;word-break:break-word;" ]
        [ Text.raw (sprintf "%s" value) ] ]
  | true ->
    // Complex value — try to parse structure for better display
    let lines = value.Split([|'\n'|]) |> Array.filter (fun s -> s.Trim().Length > 0)
    match lines.Length with
    | 1 ->
      // Single line but complex — show as-is with word wrapping
      [ Elem.span
          [ Attr.style "color:var(--fg-green,#98c379);font-size:0.65rem;word-break:break-word;white-space:pre-wrap;" ]
          [ Text.raw value ] ]
    | _ ->
      // Multi-line — render with proper formatting
      lines 
      |> Array.map (fun line ->
        Elem.div
          [ Attr.style "color:var(--fg-green,#98c379);font-size:0.65rem;font-family:monospace;white-space:pre-wrap;word-break:break-word;" ]
          [ Text.raw line ])
      |> Array.toList

/// Render per-session bound values explorer as a hierarchical tree view.
let renderBindingExplorer (bindings: Features.BindingExplorer.BindingInfo array) : XmlNode =
  match bindings.Length with
  | 0 -> Elem.div [] []
  | _ ->
    Elem.div [ Attr.style "font-size: 0.72rem; max-height: 300px; overflow-y: auto; font-family: monospace;" ] [
      for i, b in bindings |> Array.indexed do
        let bindingId = sprintf "binding-%d" i
        Elem.details
          [ Attr.id bindingId
            Attr.style "margin: 4px 0; padding: 4px; border: 1px solid var(--border-normal,#333); border-radius: 2px; background: var(--bg-focus,#1a1a1a);" ]
          [ Elem.summary
              [ Attr.style "cursor: pointer; user-select: none; padding: 2px 4px; margin: -2px -4px;" ]
              [ Elem.code
                  [ Attr.style "color:var(--fg-cyan,#56b6c2);font-weight:bold;font-size:0.7rem;" ]
                  [ Text.raw b.Name ]
                Elem.span
                  [ Attr.style "color:var(--fg-dim,#666);font-size:0.65rem;margin-left:0.5em;" ]
                  [ Text.raw (sprintf ": %s" b.TypeSig) ]
                match b.Value with
                | Some v when v.Length < 50 ->
                  Elem.span
                    [ Attr.style "color:var(--fg-green,#98c379);font-size:0.65rem;margin-left:0.5em;" ]
                    [ Text.raw (sprintf "= %s" v) ]
                | Some _ ->
                  Elem.span
                    [ Attr.style "color:var(--fg-green,#98c379);font-size:0.65rem;margin-left:0.5em;" ]
                    [ Text.raw "= ..." ]
                | None -> () ]
            match b.Value with
            | Some v ->
              Elem.div
                [ Attr.style "margin-top: 6px; padding: 6px; background: var(--bg-default,#000); border-radius: 2px; border-left: 2px solid var(--fg-green,#98c379);" ]
                (renderValueTree v 1)
            | None ->
              Elem.div
                [ Attr.style "margin-top: 6px; color: var(--fg-dim,#666); font-style: italic;" ]
                [ Text.raw "(no value)" ]
            match b.ReferencedIn with
            | [] -> ()
            | refs ->
              Elem.div
                [ Attr.style "margin-top: 4px; padding-top: 4px; border-top: 1px solid var(--border-normal,#333); color: var(--fg-yellow,#e5c07b); font-size: 0.6rem;" ]
                [ Text.raw (sprintf "Referenced in %d cells" refs.Length) ] ]
    ]

/// Render sessions as an HTML fragment with action buttons.
let renderSessions (sessions: ParsedSession list) (creating: bool) =
  Elem.div [ Attr.id DomIds.SessionsPanel ] [
    match creating with
    | true ->
      Elem.div
        [ Attr.style "padding: 8px; text-align: center; color: var(--fg-blue); font-size: 0.85rem;" ]
        [ Text.raw "⏳ Creating session..." ]
    | false -> ()
    match sessions.IsEmpty && not creating with
    | true ->
      Text.raw "No sessions"
    | false ->
      yield! sessions |> List.mapi (fun i (s: ParsedSession) ->
        let statusClass =
          match s.Status with
          | "running" -> "status-ready"
          | "starting" | "restarting" -> "status-warming"
          | _ -> "status-faulted"
        let cls =
          match s.IsActive with
          | true -> "output-result"
          | false -> ""
        let guidanceCls =
          match s.GuidanceCssClass.Length > 0 with
          | true -> sprintf " %s" s.GuidanceCssClass
          | false -> ""
        Elem.div
          [ Attr.class' (sprintf "session-row flex-between %s%s" cls guidanceCls)
            Attr.style "padding: 8px 0; border-bottom: 1px solid var(--border-normal); cursor: pointer;"
            Ds.class' ("session-selected", sprintf "$%s === '%s'" Signals.ViewingSessionId s.Id)
            Ds.onEvent ("click", sprintf "$%s = '%s'; @post('/dashboard/session/switch/%s')" Signals.ViewingSessionId s.Id s.Id) ]
          [
            Elem.div [ Attr.style "flex: 1; min-width: 0;" ] [
              // Row 1: session ID + status + active indicator
              Elem.div [ Attr.class' "flex-row"; Attr.style "gap: 0.5rem;" ] [
                Elem.span [ Attr.style "font-weight: bold;" ] [ Text.raw s.Id ]
                Elem.span
                  [ Attr.class' (sprintf "status badge %s" statusClass) ]
                  [ Text.raw s.Status ]
                match s.StatusMessage with
                | Some msg ->
                  Elem.span
                    [ Attr.style "font-size: 0.65rem; color: var(--fg-yellow); font-style: italic;" ]
                    [ Text.raw (sprintf "⏳ %s" msg) ]
                | None -> ()
                match s.IsActive with
                | true ->
                  Elem.span [ Attr.style "color: var(--fg-green);" ] [ Text.raw "● active" ]
                | false -> ()
                // Per-session standby indicator
                match s.StandbyLabel.Length > 0 with
                | true ->
                  let color =
                    match s.StandbyLabel with
                    | l when l.Contains "✓" -> "var(--fg-green)"
                    | l when l.Contains "⏳" -> "var(--fg-yellow)"
                    | l when l.Contains "⚠" -> "var(--fg-red)"
                    | _ -> "var(--fg-dim)"
                  Elem.span
                    [ Attr.class' "badge"; Attr.style (sprintf "color: %s;" color) ]
                    [ Text.raw s.StandbyLabel ]
                | false -> ()
                // Agent presence badges (multi-agent coordination)
                yield! s.AgentBadges |> List.map (fun badge ->
                  Elem.span
                    [ Attr.class' badge.CssClass
                      Attr.title (
                        match badge.DetailLabel.Length > 0 with
                        | true -> sprintf "%s — files: %s" badge.Name badge.DetailLabel
                        | false -> badge.Name) ]
                    [ Text.raw (
                        match badge.IntentLabel.Length > 0 with
                        | true -> sprintf "🤖 %s (%s)" badge.Name badge.IntentLabel
                        | false -> sprintf "🤖 %s" badge.Name) ])
                match s.Uptime.Length > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta"; Attr.style "margin-left: auto;" ] [
                    Text.raw (sprintf "⏱ %s" s.Uptime)
                  ]
                | false -> ()
              ]
              // Row 2: working directory
              match s.WorkingDir.Length > 0 with
              | true ->
                Elem.div
                  [ Attr.style "font-size: 0.75rem; color: var(--fg-dim); margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                    Attr.title s.WorkingDir ]
                  [ Text.raw (sprintf "📁 %s" s.WorkingDir) ]
              | false -> ()
              // Row 3: projects as tags + evals + last activity
              Elem.div [ Attr.class' "flex-row"; Attr.style "gap: 0.5rem; margin-top: 2px; flex-wrap: wrap;" ] [
                match s.ProjectsText.Length > 0 with
                | true ->
                  let projNames =
                    s.ProjectsText.Trim('(', ')')
                      .Split(',')
                    |> Array.map (fun p -> p.Trim())
                    |> Array.filter (fun p -> p.Length > 0)
                  yield! projNames |> Array.map (fun pName ->
                    Elem.span
                      [ Attr.class' "badge"; Attr.style "background: var(--bg-focus); color: var(--fg-dim);" ]
                      [ Text.raw pName ])
                | false -> ()
                match s.EvalCount > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta" ] [
                    Text.raw (sprintf "evals: %d" s.EvalCount)
                  ]
                | false -> ()
                match s.TestSummary with
                | Some ts when ts.Total > 0 ->
                  let badge = Features.LiveTesting.TestSummary.toInlineBadge ts
                  let badgeColor =
                    match ts.Failed > 0 with
                    | true -> "var(--fg-red)"
                    | false ->
                      match ts.Running > 0 with
                      | true -> "var(--fg-blue)"
                      | false -> "var(--fg-green)"
                  Elem.span
                    [ Attr.class' "badge"
                      Attr.style (sprintf "color: %s; font-size: 0.7rem;" badgeColor) ]
                    [ Text.raw badge ]
                | _ -> ()
                match s.CoverageSummary with
                | Some cs when cs.TotalProbes > 0 ->
                  let gradientStops =
                    cs.DensityStrip
                    |> Array.mapi (fun i d ->
                      let color =
                        match d with
                        | x when x >= 0.8 -> "var(--fg-green)"
                        | x when x >= 0.4 -> "var(--fg-yellow)"
                        | x when x > 0.0 -> "var(--fg-red)"
                        | _ -> "var(--bg-focus)"
                      let startPct = float i / float cs.DensityStrip.Length * 100.0
                      let endPct = float (i + 1) / float cs.DensityStrip.Length * 100.0
                      sprintf "%s %.0f%%,%s %.0f%%" color startPct color endPct)
                    |> String.concat ","
                  let pct = sprintf "%.0f%%" cs.CoveragePercent
                  let title = sprintf "Coverage: %s (%d/%d probes)" pct cs.CoveredProbes cs.TotalProbes
                  Elem.span [ Attr.style "display:inline-flex;align-items:center;gap:2px;" ] [
                    Elem.span
                      [ Attr.style (sprintf "display:inline-block;width:48px;height:8px;border-radius:2px;background:linear-gradient(to right,%s);" gradientStops)
                        Attr.title title ]
                      []
                    Elem.span
                      [ Attr.style "font-size:0.6rem;color:var(--fg-dim);" ]
                      [ Text.raw pct ]
                  ]
                | _ -> ()
                match s.LastActivity.Length > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta"; Attr.style "margin-left: auto;" ] [
                    Text.raw (sprintf "last: %s" s.LastActivity)
                  ]
                | false -> ()
              ]
            ]
            Elem.div [ Attr.style "display: flex; gap: 4px; margin-left: 8px;" ] [
              match s.IsActive with
              | false ->
                Elem.button
                  [ Attr.class' "session-btn"
                    Ds.onEvent ("click", sprintf "$%s = '%s'; @post('/dashboard/session/switch/%s')" Signals.ViewingSessionId s.Id s.Id) ]
                  [ Text.raw "⇄" ]
              | true -> ()
              Elem.button
                [ Attr.class' "session-btn session-btn-danger"
                  Ds.onClick (Ds.post (sprintf "/dashboard/session/stop/%s" s.Id)) ]
                [ Text.raw "■" ]
            ]
            // Collapsible test treemap (WizTree-style: area = test duration)
            match s.TestTreemapEntries.Length with
            | 0 -> ()
            | _ ->
              let totalMs = s.TestTreemapEntries |> Array.sumBy (fun e -> e.DurationMs)
              let durationLabel =
                match totalMs with
                | ms when ms >= 1000.0 -> sprintf "%.1fs" (ms / 1000.0)
                | ms -> sprintf "%.0fms" ms
              Elem.details
                [ Attr.style "margin-top: 4px; font-size: 0.75rem;" ]
                [ Elem.summary
                    [ Attr.style "cursor:pointer;color:var(--fg-dim);user-select:none;" ]
                    [ Text.raw (sprintf "🧪 %d tests · %s" s.TestTreemapEntries.Length durationLabel) ]
                  renderTestFilterBar s.TestTreemapEntries
                  renderTestTreemap s.TestTreemapEntries ]
            // Collapsible bound values explorer
            match s.BindingEntries.Length with
            | 0 -> ()
            | _ ->
              Elem.details
                [ Attr.style "margin-top: 4px; font-size: 0.75rem;" ]
                [ Elem.summary
                    [ Attr.style "cursor:pointer;color:var(--fg-dim);user-select:none;" ]
                    [ Text.raw (sprintf "📦 %d bindings" s.BindingEntries.Length) ]
                  renderBindingExplorer s.BindingEntries ]
          ])
    Elem.div
      [ Attr.style "display: flex; justify-content: space-between; align-items: center; font-size: 0.7rem; color: var(--fg-dim); padding: 4px 0; margin-top: 4px;" ]
      [
        Elem.span [] [
          Text.raw "⇄ switch · ■ stop · X stop others"
        ]
        match sessions.Length > 1 with
        | true ->
          Elem.button
            [ Attr.class' "session-btn session-btn-danger"
              Attr.style "font-size: 0.65rem; padding: 1px 6px;"
              Ds.onClick (Ds.post "/dashboard/session/stop-others") ]
            [ Text.raw "■ stop others" ]
        | false -> ()
      ]
  ]





/// Render session eval history as a visual filmstrip — one card per recent eval.
/// Collapsed by default; the summary line shows count + recent outcome icons at a glance.
let renderSessionFilmstrip (entries: FilmstripEntry list) =
  Elem.div [ Attr.id DomIds.FilmstripPanel; Attr.class' "filmstrip-panel" ] [
    match entries with
    | [] -> ()
    | _ ->
      let recentIcons =
        entries
        |> List.rev
        |> List.truncate 5
        |> List.rev
        |> List.map (fun e ->
          match e.Outcome with EvalSuccess -> "✓" | EvalError -> "✗" | EvalCancelled -> "⊘")
        |> String.concat ""
      Elem.details [] [
        Elem.summary [ Attr.style "cursor: pointer; font-size: 0.75rem; color: var(--fg-dim); user-select: none;" ] [
          Text.raw (sprintf "⏱ %d evals  %s" entries.Length recentIcons)
        ]
        Elem.div [ Attr.class' "filmstrip-frames" ] [
          yield! entries |> List.map (fun e ->
            let icon = match e.Outcome with EvalSuccess -> "✓" | EvalError -> "✗" | EvalCancelled -> "⊘"
            let speedCls =
              match e.DurationMs with
              | ms when ms < 100L -> "eval-fast"
              | ms when ms <= 500L -> "eval-medium"
              | _ -> "eval-slow"
            Elem.div [ Attr.class' (sprintf "filmstrip-frame %s" speedCls) ] [
              Elem.span [ Attr.class' "frame-index" ] [ Text.raw (sprintf "#%d" e.Index) ]
              Elem.span [ Attr.class' "frame-icon" ] [ Text.raw icon ]
              Elem.span [ Attr.class' "frame-label" ] [ Text.raw e.Label ]
              Elem.span [ Attr.class' "frame-duration" ] [ Text.raw (sprintf " %dms" e.DurationMs) ]
            ])
        ]
      ]
  ]

/// Render current FSI diagnostics as a collapsible panel with emoji severity icons and a count badge.
/// Silent when there are no diagnostics — clean state needs no confirmation.
/// Uses <details>/<summary> so the user can collapse the error wall — consistent with
/// failure narratives and filmstrip panels. The count badge stays visible when collapsed.
let renderCurrentDiagnostics (diags: Diagnostic list) =
  let errorCount = diags |> List.filter (fun d -> d.Severity = DiagError) |> List.length
  let warnCount = diags |> List.filter (fun d -> d.Severity = DiagWarning) |> List.length
  Elem.div [ Attr.id DomIds.DiagnosticsPanel; Attr.class' "diagnostics-panel" ] [
    match diags.IsEmpty with
    | true -> ()
    | false ->
      let badgeText =
        match errorCount, warnCount with
        | e, 0 -> sprintf "🔴 %d error%s" e (if e = 1 then "" else "s")
        | 0, w -> sprintf "⚠️ %d warning%s" w (if w = 1 then "" else "s")
        | e, w -> sprintf "🔴 %d error%s · ⚠️ %d warning%s" e (if e = 1 then "" else "s") w (if w = 1 then "" else "s")
      Elem.details [] [
        Elem.summary [ Attr.style disclosureSummaryStyle ] [
          Elem.span [ Attr.class' "diag-count-badge"; Attr.style "font-weight: bold; margin-right: 0.5rem;" ] [
            Text.raw badgeText
          ]
        ]
        Elem.div [ Attr.style "margin-top: 0.25rem;" ] [
          yield! diags |> List.map (fun diag ->
            let icon = match diag.Severity with DiagError -> "🔴" | DiagWarning -> "⚠️"
            Elem.div [ Attr.class' (sprintf "diag %s" (DiagSeverity.toCssClass diag.Severity)) ] [
              Elem.span [ Attr.class' "diag-icon" ] [ Text.raw icon ]
              if diag.Line > 0 || diag.Col > 0 then
                Elem.span [ Attr.class' "diag-loc" ] [ Text.raw (sprintf " L%d:%d " diag.Line diag.Col) ]
              Elem.span [ Attr.class' "diag-msg" ] [ Text.raw diag.Message ]
            ])
        ]
      ]
  ]

/// Render the full dynamic content of the dashboard as a single <div id="main">.
/// This is the ONLY thing pushed via SSE on every state change.
/// Implements "immediate mode HTML" — the server renders the complete page from
/// state, sends one morph, and Datastar diffs the DOM.
/// See: "The Tao of Datastar" — https://data-star.dev/essays/tao_of_datastar
let renderMainContent (snap: DashboardSnapshot) : XmlNode =
  let connectionNode =
    match snap.ConnectionLabel with
    | Some label ->
      Elem.div [ Attr.id DomIds.ConnectionCounts; Attr.class' "meta"; Attr.style "font-size: 0.75rem; margin-top: 4px;" ] [
        Text.raw label
      ]
    | None ->
      Elem.div [ Attr.id DomIds.ConnectionCounts; Attr.class' "meta"; Attr.style "font-size: 0.75rem; margin-top: 4px;" ] []
  Elem.div [ Attr.id DomIds.Main; Ds.class' ("expanded", sprintf "$%s" Signals.ExpandedDashboard) ] [
    // Theme CSS variables — morphed with every push so theme changes propagate
    snap.ThemeVars
    // App header — version, status, stats, theme, sidebar toggle
    Elem.div [ Attr.class' "app-header" ] [
      Elem.h1 [] [ Text.raw (sprintf "🧙 SageFs v%s" snap.Version) ]
      Elem.div [ Attr.class' "flex-row"; Attr.style "gap: 0.75rem; align-items: center;" ] [
        renderSessionStatus snap.SessionState snap.SessionId snap.WorkingDir snap.WarmupProgress snap.WorkflowLabel
        renderEvalStats snap.EvalStats
        snap.ThemePicker
        Elem.button
          [ Attr.class' "sidebar-toggle"
            Ds.onEvent ("click", "$sidebarOpen = !$sidebarOpen")
            Ds.text "$sidebarOpen ? '✕ Panel' : '☰ Panel'" ]
          []
      ]
    ]
    // Daemon health bar — version, uptime, memory, session health
    snap.DaemonHealth
    // Expanded-only panels: alarm, failure narratives, diagnostics, filmstrip
    Elem.div [ Attr.class' "expanded-only" ] [
      snap.AlarmPanel
      snap.FailureNarrativesPanel
      snap.DiagnosticsPanel
      snap.FilmstripPanel
    ]
    // Main app layout: output+eval on left, sidebar on right
    Elem.div [ Attr.class' "app-layout" ] [
      Elem.div [ Attr.class' "main-area" ] [
        // Session picker — shown when no sessions exist, hidden otherwise
        snap.SessionPicker
        Elem.div [ Attr.id DomIds.EditorArea ] [
          Elem.div [ Attr.id DomIds.OutputSection; Attr.class' "output-area" ] [
            Elem.div [ Attr.class' "output-header" ] [
              Elem.h2 [] [ Text.raw "Output" ]
              Elem.button
                [ Attr.class' "panel-header-btn"
                  Ds.onClick (Ds.post "/dashboard/clear-output") ]
                [ Text.raw "Clear" ]
            ]
            snap.OutputPanel
          ]
          // Eval area — collapsed by default via <details>
          Elem.create "details" [ Attr.id DomIds.EvaluateSection; Attr.class' "eval-area" ] [
            Elem.create "summary" [ Attr.class' "flex-between"; Attr.style "cursor: pointer;" ] [
              Elem.span [ Attr.style "color: var(--fg-blue); font-weight: bold; font-size: 0.9rem;" ] [ Text.raw "▸ Evaluate" ]
              Elem.span [ Attr.class' "meta"; Attr.style "font-size: 0.75rem;" ] [
                Elem.span [ Ds.text """$code ? ($code.split('\\n').length + 'L ' + $code.length + 'c') : ''""" ] []
              ]
            ]
            // Keyboard help toggle — outside <summary> to avoid a11y issues (interactive inside summary)
            Elem.div [ Attr.style "display: flex; justify-content: flex-end; padding: 2px 0;" ] [
              Elem.button
                [ Attr.class' "panel-header-btn"
                  Ds.onEvent ("click", "$helpVisible = !$helpVisible") ]
                [ Text.raw "⌨" ]
            ]
            Elem.div [ Attr.id DomIds.KeyboardHelpWrapper; Ds.show "$helpVisible" ] [
              renderKeyboardHelp ()
            ]
            Elem.input [ Attr.type' "hidden"; Ds.bind Signals.SessionId ]
            Elem.div [ Attr.style "position: relative;" ] [
              Elem.textarea
                [ Attr.class' "eval-input"
                  Attr.id DomIds.EvalTextarea
                  Ds.bind Signals.Code
                  Attr.create "placeholder" "Enter F# code... (Alt+Enter to eval, ;; auto-appended)"
                  Ds.onEvent ("keydown", "if(event.altKey && event.key === 'Enter') { event.preventDefault(); @post('/dashboard/eval') } if(event.ctrlKey && event.key === 'l') { event.preventDefault(); @post('/dashboard/clear-output') } if(event.key === 'Tab') { event.preventDefault(); var s=this.selectionStart; var e=this.selectionEnd; this.value=this.value.substring(0,s)+'  '+this.value.substring(e); this.selectionStart=this.selectionEnd=s+2; this.dispatchEvent(new Event('input')) } if(event.key === 'Escape') { document.getElementById('completion-dropdown').style.display='none' }")
                  Ds.onEvent ("input.debounce_300ms", sprintf "var c=this.value[this.selectionStart-1]; $%s = this.selectionStart; if(c==='.'||(c>='a'&&c<='z')||(c>='A'&&c<='Z')){@post('/dashboard/completions')}" Signals.CursorPos)
                  Attr.create "spellcheck" "false" ]
                []
              Elem.div
                [ Attr.id DomIds.CompletionDropdown
                  Attr.style "display:none; position:absolute; bottom:100%; left:0; max-height:200px; overflow-y:auto; background:var(--bg-default); border:1px solid var(--bg-selection); border-radius:4px; z-index:100; min-width:200px; font-size:0.85em; box-shadow:0 -2px 8px rgba(0,0,0,0.3);" ]
                []
            ]
            Elem.div [ Attr.style "display: flex; gap: 0.5rem; margin-top: 0.5rem; align-items: center;" ] [
              Elem.button
                [ Attr.class' "eval-btn"
                  Ds.indicator Signals.EvalLoading
                  Ds.attr' ("disabled", "$evalLoading")
                  Ds.onClick (Ds.post "/dashboard/eval") ]
                [ Elem.span [ Ds.show "$evalLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$evalLoading" ] [ Text.raw "▶ " ]
                  Text.raw "Eval" ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "background: var(--fg-green);"
                  Ds.onClick (Ds.post "/dashboard/reset") ]
                [ Text.raw "↻ Reset" ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "background: var(--fg-red);"
                  Ds.onClick (Ds.post "/dashboard/hard-reset") ]
                [ Text.raw "⟳ Hard Reset" ]
              Elem.label
                [ Attr.class' "eval-btn"
                  Attr.style "background: var(--fg-blue); cursor: pointer; display: inline-flex; align-items: center;" ]
                [ Elem.input
                    [ Attr.type' "file"
                      Attr.accept ".fs,.fsx,.fsi"
                      Attr.style "display: none;"
                      Attr.create "onchange" "if(this.files[0]){var f=this.files[0];var r=new FileReader();r.onload=function(){var ta=document.getElementById('eval-textarea');ta.value=r.result;ta.dispatchEvent(new Event('input'))};r.readAsText(f);this.value=''}" ]
                  Text.raw "📂 Load File" ]
            ]
            Elem.div [ Attr.id DomIds.EvalResult ] []
          ]
        ]
      ]
      // Resize handle between main area and sidebar
      Elem.div [ Attr.class' "resize-handle"; Attr.id DomIds.SidebarResize ] []
      // Sidebar — sessions, panels, new session at bottom
      Elem.div [ Attr.id DomIds.Sidebar; Attr.class' "sidebar"; Ds.class' ("collapsed", "!$sidebarOpen") ] [
        Elem.div [ Attr.class' "sidebar-inner" ] [
          // Sessions panel (with context + bindings inline per row)
          Elem.div [ Attr.class' "panel" ] [
            Elem.h2 [] [ Text.raw "Sessions" ]
            connectionNode
            snap.SessionsPanel
          ]
          // Dynamic sidebar panels — expanded-only (hot reload, live testing, bindings, session context)
          Elem.div [ Attr.class' "expanded-only" ] [
            snap.HotReloadPanel
            snap.LiveTestingPanel
            snap.BindingsPanel
            snap.SessionContextPanel
          ]
          Elem.div [ Attr.class' "panel" ] [
            Elem.h2 [] [ Text.raw "New Session" ]
            Elem.div [] [
              Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px;" ] [
                Text.raw "Working Directory"
              ]
              Elem.input
                [ Attr.class' "eval-input"
                  Attr.style "min-height: auto; height: 2rem;"
                  Ds.bind Signals.NewSessionDir
                  Attr.create "placeholder" @"C:\path\to\project" ]
            ]
            Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 0.5rem;" ] [
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                  Ds.indicator Signals.DiscoverLoading
                  Ds.attr' ("disabled", "$discoverLoading")
                  Ds.onClick (Ds.post "/dashboard/discover-projects") ]
                [ Elem.span [ Ds.show "$discoverLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$discoverLoading" ] [ Text.raw "🔍 " ]
                  Text.raw "Discover" ]
            ]
            renderDisableWarmupAutoOpenButton "margin-top: 0.5rem; width: 100%; font-size: 0.8rem;"
            Elem.div [ Attr.id DomIds.DiscoveredProjects ] []
            Elem.div [ Attr.style "margin-top: 0.5rem;" ] [
              Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px;" ] [
                Text.raw "Projects (comma-sep)"
              ]
              Elem.input
                [ Attr.class' "eval-input"
                  Attr.style "min-height: auto; height: 2rem;"
                  Ds.bind Signals.ManualProjects
                  Attr.create "placeholder" "MyProject.fsproj" ]
            ]
            Elem.button
              [ Attr.class' "eval-btn"
                Attr.style "margin-top: 0.5rem; width: 100%; font-size: 0.8rem;"
                Ds.indicator Signals.CreateLoading
                Ds.attr' ("disabled", "$createLoading")
                Ds.onClick (Ds.post "/dashboard/session/create") ]
              [ Elem.span [ Ds.show "$createLoading" ] [ Text.raw "⏳ Creating... " ]
                Elem.span [ Ds.show "!$createLoading" ] [ Text.raw "➕ Create" ] ]
          ]
        ]
      ]
    ]
    // Floating expand/collapse toggle — always visible, bottom-right corner
    Elem.button
      [ Attr.class' "expand-toggle-btn"
        Ds.onEvent ("click", sprintf "$%s = !$%s" Signals.ExpandedDashboard Signals.ExpandedDashboard)
        Ds.text (sprintf "$%s ? '✕ collapse' : '⋯ expand'" Signals.ExpandedDashboard) ]
      []
  ]

let renderRegionForSse (getSessionState: string -> SessionState) (getStatusMsg: string -> string option) (getSessionStandbyInfo: string -> StandbyInfo) (region: RenderRegion) =
  match region.Id with
  | "output" -> Some (renderOutput (parseOutputLines region.Content))
  | "sessions" ->
    let parsed = parseSessionLines region.Content
    let corrected = overrideSessionStatuses getSessionState getStatusMsg parsed
    let visible =
      corrected
      |> List.filter (fun s -> s.Status <> "stopped")
      |> List.map (fun s ->
        let info = getSessionStandbyInfo s.Id
        { s with StandbyLabel = StandbyInfo.label info })
    Some (renderSessions visible (isCreatingSession region.Content))
  | _ -> None

let pushRegions
  (ctx: HttpContext)
  (regions: RenderRegion list)
  (getPreviousSessions: unit -> Threading.Tasks.Task<PreviousSession list>)
  (getSessionState: string -> SessionState)
  (getStatusMsg: string -> string option)
  (getSessionStandbyInfo: string -> StandbyInfo)
  = task {
    for region in regions do
      match renderRegionForSse getSessionState getStatusMsg getSessionStandbyInfo region with
      | Some html -> do! ssePatchNode ctx html
      | None -> ()
      // When sessions region is pushed, also push picker visibility
      match region.Id = "sessions" with
      | true ->
        let sessions = parseSessionLines region.Content
        let creating = isCreatingSession region.Content
        match sessions.IsEmpty && not creating with
        | true ->
          let! previous = getPreviousSessions ()
          do! ssePatchNode ctx (renderSessionPicker previous)
        | false ->
          do! ssePatchNode ctx renderSessionPickerEmpty
      | false -> ()
  }

/// Decides whether a theme push is needed after a state change.
/// Returns Some themeName if push needed, None otherwise.
/// Pure function — no side effects — for testability.
let resolveThemePush
  (themes: System.Collections.Generic.IDictionary<string, string>)
  (currentSessionId: string)
  (currentWorkingDir: string)
  (previousSessionId: string)
  (previousWorkingDir: string)
  : string option =
  let sessionChanged =
    currentSessionId.Length > 0 && currentSessionId <> previousSessionId
  let workingDirChanged =
    currentWorkingDir.Length > 0 && currentWorkingDir <> previousWorkingDir
  match sessionChanged || workingDirChanged with
  | true ->
    match currentWorkingDir.Length > 0 with
    | true ->
      match themes.TryGetValue(currentWorkingDir) with
      | true, n -> Some n
      | false, _ -> Some defaultThemeName
    | false ->
      Some defaultThemeName
  | false ->
    None

/// Render the hot-reload panel with a file list grouped by directory.
let renderHotReloadPanel (sessionId: string) (files: {| path: string; watched: bool |} list) (watchedCount: int) =
  let total = List.length files
  let grouped =
    files
    |> List.groupBy (fun f ->
      let normalized = f.path.Replace('\\', '/')
      match normalized.LastIndexOf('/') with
      | -1 -> ""
      | idx -> normalized.[..idx])
    |> List.sortBy fst
  Elem.div [ Attr.id DomIds.HotReloadPanel; Attr.class' "panel" ] [
    Elem.h2 [] [
      match watchedCount with
      | 0 -> Text.raw "Hot Reload: OFF"
      | n -> Text.raw (sprintf "Hot Reload: ON — %d of %d files" n total)
    ]
    Elem.div [ Attr.class' "meta"; Attr.style "margin-bottom: 0.5rem; font-size: 0.8rem;" ] [
      Text.raw (sprintf "%d of %d files watched" watchedCount total)
    ]
    Elem.div [ Attr.style "display: flex; gap: 4px; margin-bottom: 0.5rem;" ] [
      Elem.button
        [ Attr.class' "eval-btn"
          Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;"
          Attr.create "onclick" (sprintf "fetch('/api/sessions/%s/hotreload/watch-all',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})" sessionId) ]
        [ Text.raw "Watch All" ]
      Elem.button
        [ Attr.class' "eval-btn"
          Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;"
          Attr.create "onclick" (sprintf "fetch('/api/sessions/%s/hotreload/unwatch-all',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})" sessionId) ]
        [ Text.raw "Unwatch All" ]
    ]
    Elem.details [] [
      Elem.summary [ Attr.style "cursor: pointer; font-size: 0.75rem; color: var(--fg-dim); user-select: none;" ] [
        Text.raw (sprintf "📁 %d files" total)
      ]
      Elem.div [ Attr.style "max-height: 200px; overflow-y: auto; font-size: 0.75rem;" ] [
        yield! grouped |> List.collect (fun (dir, dirFiles) ->
          let dirLabel =
            match dir.Length > 40 with
            | true -> "..." + dir.[dir.Length - 37..]
            | false -> dir
          let dirWatchedCount = dirFiles |> List.filter (fun f -> f.watched) |> List.length
          let allWatched = dirWatchedCount = List.length dirFiles
          let dirIcon = match allWatched, dirWatchedCount > 0 with | true, _ -> "●" | false, true -> "◐" | false, false -> "○"
          let dirColor = match allWatched || dirWatchedCount > 0 with | true -> "var(--fg-blue, #7aa2f7)" | false -> "var(--fg-dim, #565f89)"
          let dirAction = match allWatched with | true -> "unwatch-directory" | false -> "watch-directory"
          let dirKey = "directory"
          [
            Elem.div
              [ Attr.style "font-weight: 600; margin-top: 4px; opacity: 0.8; font-size: 0.7rem; cursor: pointer; display: flex; align-items: center; gap: 4px;"
                Attr.create "onclick" (sprintf "fetch('/api/sessions/%s/hotreload/%s',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({%s:'%s'})})" sessionId dirAction dirKey (dir.Replace("\\", "\\\\"))) ]
              [ Elem.span [ Attr.style (sprintf "color: %s;" dirColor) ] [ Text.raw dirIcon ]
                Text.raw (sprintf "📁 %s (%d/%d)" dirLabel dirWatchedCount (List.length dirFiles)) ]
            yield! dirFiles |> List.map (fun f ->
              let fileName =
                let n = f.path.Replace('\\', '/')
                match n.LastIndexOf('/') with
                | -1 -> n
                | idx -> n.[idx + 1..]
              let icon = match f.watched with | true -> "●" | false -> "○"
              let color = match f.watched with | true -> "var(--fg-blue, #7aa2f7)" | false -> "var(--fg-dim, #565f89)"
              Elem.div
                [ Attr.style "cursor: pointer; padding: 1px 4px; display: flex; align-items: center; gap: 4px;"
                  Attr.create "onclick" (sprintf "fetch('/api/sessions/%s/hotreload/toggle',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({path:'%s'})})" sessionId (f.path.Replace("\\", "\\\\"))) ]
                [ Elem.span [ Attr.style (sprintf "color: %s; font-size: 0.8rem;" color) ] [ Text.raw icon ]
                  Elem.span [ Attr.style (match f.watched with | true -> "opacity: 1" | false -> "opacity: 0.6") ] [ Text.raw fileName ] ]
            )
          ])
      ]
    ]
  ]

/// Render empty hot-reload panel when no session is active.
let renderHotReloadEmpty =
  Elem.div [ Attr.id DomIds.HotReloadPanel; Attr.class' "panel" ] [
    Elem.h2 [] [ Text.raw "Hot Reload: OFF" ]
    Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.8rem;" ] [
      Text.raw "No active session"
    ]
  ]

/// Render the live testing panel with ON/OFF toggle and test summary when active.
let renderLiveTestingPanel (isActive: bool) (statusLabel: string) (testsPassed: int option) (testsFailed: int option) =
  Elem.div [ Attr.id DomIds.LiveTestingPanel; Attr.class' "panel" ] [
    Elem.h2 [] [
      match isActive, testsPassed, testsFailed with
      | false, _, _ -> Text.raw "Live Testing: OFF"
      | true, Some p, Some f -> Text.raw (sprintf "Live Testing: ON — %d✓ %d✗" p f)
      | true, _, _ -> Text.raw "Live Testing: ON"
    ]
    Elem.div [ Attr.style "display: flex; gap: 4px; margin-bottom: 0.5rem;" ] [
      match isActive with
      | false ->
        Elem.button
          [ Attr.class' "eval-btn"
            Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;"
            Attr.create "onclick" "fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'enableLiveTesting'})})" ]
          [ Text.raw "Enable" ]
      | true ->
        Elem.button
          [ Attr.class' "eval-btn"
            Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;"
            Attr.create "onclick" "fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'disableLiveTesting'})})" ]
          [ Text.raw "Disable" ]
    ]
    match isActive with
    | false ->
      Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.8rem;" ] [
        Text.raw "Enable to start discovering and running tests automatically."
      ]
    | true ->
      match statusLabel.Length > 0 with
      | true ->
        Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.8rem;" ] [
          Text.raw statusLabel
        ]
      | false ->
        Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.8rem;" ] [
          Text.raw "Discovering tests…"
        ]
  ]

/// Render session context panel with warmup details (assemblies, namespaces, files).
/// Uses HTML <details>/<summary> so it's collapsed by default.
let renderSessionContextPanel (ctx: SessionContext) =
  let summaryText = SessionContext.summary ctx

  let assembliesSection =
    Elem.details [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        Text.raw (sprintf "📦 Assemblies (%d)" (ctx.Warmup.AssembliesLoaded |> List.length))
      ]
      Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.2em; font-size: 0.7rem;" ] [
        for asm in ctx.Warmup.AssembliesLoaded do
          Elem.li [] [ Text.raw (SessionContext.assemblyLine asm) ]
      ]
    ]

  let namespacesSection =
    let opened = ctx.Warmup.NamespacesOpened
    let failed = ctx.Warmup.FailedOpens
    Elem.details [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        Text.raw (sprintf "📂 Namespaces (%d opened, %d failed)"
          (opened |> List.length) (failed |> List.length))
      ]
      Elem.div [ Attr.style "font-size: 0.7rem;" ] [
        Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.2em;" ] [
          for b in opened do
            Elem.li [] [
              Elem.code [] [ Text.raw (SessionContext.openLine b) ]
              match b.DurationMs > 0.0 with
              | true ->
                Elem.span [ Attr.style "color: var(--fg-dim); margin-left: 0.5em;" ] [
                  Text.raw (sprintf "(%.1fms)" b.DurationMs)
                ]
              | false -> ()
            ]
        ]
        match List.isEmpty failed with
        | false ->
          Elem.details [ Attr.create "open" ""; Attr.style "margin-top: 0.5em;" ] [
            Elem.summary [ Attr.style "color: var(--fg-red); cursor: pointer; font-weight: bold;" ] [
              Text.raw (sprintf "⚠️ %d Failed Opens (expanded)" failed.Length)
            ]
            Elem.div [ Attr.style "padding-left: 0.5em;" ] [
              for f in failed do
                Elem.div [ Attr.class' "diag-error-block" ] [
                  Elem.div [ Attr.style "font-weight: bold; color: var(--fg-red);" ] [
                    let kind = OpenableKind.label f.Kind
                    Text.raw (sprintf "✖ %s (%s)" f.Name kind)
                    match f.RetryCount > 1 with
                    | true ->
                      Elem.span [ Attr.style "color: var(--fg-dim); font-weight: normal; margin-left: 0.5em;" ] [
                        Text.raw (sprintf "(%d retries)" f.RetryCount)
                      ]
                    | false -> ()
                  ]
                  Elem.div [ Attr.style "color: var(--fg-red); margin-top: 0.2em;" ] [
                    Text.raw f.ErrorMessage
                  ]
                  match List.isEmpty f.Diagnostics with
                  | false ->
                    Elem.ul [ Attr.style "margin: 0.2em 0; padding-left: 1.2em; list-style: none;" ] [
                      for d in f.Diagnostics do
                        let sevClass =
                          match d.Severity with
                          | "error" -> "diag-error"
                          | "warning" -> "diag-warning"
                          | _ -> "diag"
                        Elem.li [ Attr.class' sevClass; Attr.style "margin: 0.15em 0;" ] [
                          Elem.code [ Attr.class' "diag-code" ] [
                            Text.raw (sprintf "FS%04d" d.ErrorNumber)
                          ]
                          match d.FileName with
                          | Some fn ->
                            Elem.span [ Attr.style "margin-left: 0.4em; color: var(--fg-dim);" ] [
                              Text.raw (sprintf "%s:%d:%d" fn d.StartLine d.StartColumn)
                            ]
                          | None -> ()
                          Elem.span [ Attr.style "margin-left: 0.4em;" ] [
                            Text.raw d.Message
                          ]
                        ]
                    ]
                  | true -> ()
                ]
            ]
          ]
        | true -> ()
      ]
    ]

  let timingSection =
    let t = ctx.Warmup.PhaseTiming
    Elem.details [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        Text.raw (sprintf "⏱️ Warmup Timing (%dms total)" t.TotalMs)
      ]
      Elem.div [ Attr.style "font-size: 0.7rem; padding-left: 0.5em;" ] [
        let phases = [
          "Scan source files", t.ScanSourceFilesMs
          "Scan assemblies", t.ScanAssembliesMs
          "Open namespaces", t.OpenNamespacesMs
        ]
        let maxMs = match t.TotalMs with | 0L -> 1L | v -> v
        for (label, ms) in phases do
          let pct = float ms / float maxMs * 100.0
          Elem.div [ Attr.style "margin: 0.2em 0;" ] [
            Elem.div [ Attr.class' "flex-row"; Attr.style "gap: 0.5em;" ] [
              Elem.span [ Attr.style "min-width: 120px;" ] [ Text.raw label ]
              Elem.div [ Attr.class' "progress-track" ] [
                Elem.div [ Attr.style (sprintf "width: %.1f%%; height: 100%%; background: var(--fg-blue); border-radius: 4px;" pct) ] []
              ]
              Elem.span [ Attr.style "min-width: 50px; text-align: right; color: var(--fg-dim);" ] [
                Text.raw (sprintf "%dms" ms)
              ]
            ]
          ]
      ]
    ]

  let filesSection =
    Elem.details [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        let loadedCount =
          ctx.FileStatuses
          |> List.filter (fun f -> f.Readiness = Loaded)
          |> List.length
        Text.raw (sprintf "📄 Files (%d/%d loaded)" loadedCount (ctx.FileStatuses |> List.length))
      ]
      Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.2em; font-size: 0.7rem;" ] [
        for f in ctx.FileStatuses do
          let color =
            match f.Readiness with
            | Loaded -> "var(--fg-green)"
            | Stale -> "var(--fg-yellow)"
            | LoadFailed -> "var(--fg-red)"
            | NotLoaded -> "var(--fg-dim)"
          Elem.li [ Attr.style (sprintf "color: %s" color) ] [
            Text.raw (SessionContext.fileLine f)
          ]
      ]
    ]

  Elem.div [ Attr.id DomIds.SessionContext; Attr.class' "panel" ] [
    Elem.details [] [
      Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.8rem;" ] [
        Text.raw (sprintf "🔍 Session Context: %s" summaryText)
      ]
      Elem.div [ Attr.style "padding-left: 0.5em; margin-top: 0.3em;" ] [
        timingSection
        assembliesSection
        namespacesSection
        filesSection
      ]
    ]
  ]

/// Render empty session context panel when no session is active.
let renderSessionContextEmpty =
  Elem.div [ Attr.id DomIds.SessionContext; Attr.class' "panel" ] [
    Elem.div [ Attr.style "font-size: 0.8rem; opacity: 0.6;" ] [
      Text.raw "No session context"
    ]
  ]

let renderBindingsPanel (snapshot: Features.BindingExplorer.BindingScopeSnapshot option) =
  let activeCount =
    snapshot |> Option.map (fun s -> s.ActiveBindings.Count) |> Option.defaultValue 0
  let shadowedCount =
    snapshot |> Option.map (fun s -> s.ShadowedBindings.Length) |> Option.defaultValue 0
  Elem.div [ Attr.id DomIds.BindingsPanel; Attr.class' "panel" ] [
    Elem.details [] [
      Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.9rem; user-select: none;" ] [
        Text.raw (sprintf "📦 Bindings (%d)" activeCount)
      ]
      match snapshot with
      | None ->
        Elem.div [ Attr.class' "meta" ] [ Text.raw "No bindings yet — evaluate some code" ]
      | Some scope ->
        match scope.ActiveBindings.Count with
        | 0 ->
          Elem.div [ Attr.class' "meta" ] [ Text.raw "No active bindings" ]
        | _ ->
          Elem.div [ Attr.style "font-size: 0.75rem;" ] [
            for KeyValue(_, b) in scope.ActiveBindings do
              Elem.div [ Attr.style "display: flex; align-items: baseline; gap: 0.5em; padding: 2px 0; border-bottom: 1px solid var(--border, #333);" ] [
                Elem.code [ Attr.style "color: var(--fg-cyan, #56b6c2); font-weight: bold; white-space: nowrap;" ] [
                  Text.raw b.Name
                ]
                Elem.span [ Attr.style "color: var(--fg-dim, #666); font-size: 0.7rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;" ] [
                  Text.raw b.TypeSig
                ]
                Elem.span [ Attr.style "color: var(--fg-dim, #555); font-size: 0.65rem; white-space: nowrap;" ] [
                  Text.raw (sprintf "cell %d" b.CellIndex)
                ]
                match b.ReferencedIn.Length with
                | 0 -> ()
                | n ->
                  Elem.span [ Attr.style "color: var(--fg-yellow, #e5c07b); font-size: 0.65rem; white-space: nowrap;" ] [
                    Text.raw (sprintf "→%d" n)
                  ]
                match b.Value with
                | None -> ()
                | Some v ->
                  Elem.span [ Attr.class' "value-display"; Attr.style "color: var(--fg-green, #98c379); font-size: 0.7rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 20em;" ] [
                    Text.raw (sprintf "= %s" v)
                  ]
              ]
          ]
          match shadowedCount with
          | 0 -> ()
          | _ ->
            Elem.details [ Attr.style "margin-top: 0.5em;" ] [
              Elem.summary [ Attr.style "font-size: 0.7rem; cursor: pointer; color: var(--fg-dim, #666);" ] [
                Text.raw (sprintf "👻 %d shadowed" shadowedCount)
              ]
              Elem.div [ Attr.style "font-size: 0.7rem; opacity: 0.6;" ] [
                for b in scope.ShadowedBindings do
                  Elem.div [ Attr.style "padding: 1px 0;" ] [
                    Elem.code [] [ Text.raw b.Name ]
                    Elem.span [ Attr.style "color: var(--fg-dim, #555); margin-left: 0.3em;" ] [
                      Text.raw (sprintf ": %s (cell %d)" b.TypeSig b.CellIndex)
                    ]
                  ]
              ]
            ]
    ]
  ]

/// Create the SSE stream handler that pushes Elm state to the browser.

let private renderDiscoveredProjectsBody (discovered: DiscoveredProjects) = [
  match discovered.Solutions.IsEmpty && discovered.Projects.IsEmpty with
  | true ->
    Elem.div [ Attr.class' "output-line output-error" ] [
      Text.raw (sprintf "No .sln/.fsproj found in %s" discovered.WorkingDir)
    ]
  | false ->
    Elem.div [ Attr.class' "output-line output-result" ] [
      Text.raw (sprintf "Found in %s:" discovered.WorkingDir)
    ]
    match discovered.Solutions.IsEmpty with
    | false ->
      yield! discovered.Solutions |> List.map (fun s ->
        Elem.div [ Attr.class' "output-line output-info"; Attr.style "padding-left: 1rem;" ] [
          Text.raw (sprintf "📁 %s (solution)" s)
        ])
    | true -> ()
    yield! discovered.Projects |> List.map (fun p ->
      Elem.div [ Attr.class' "output-line"; Attr.style "padding-left: 1rem;" ] [
        Text.raw (sprintf "📄 %s" p)
      ])
    Elem.div [ Attr.class' "meta"; Attr.style "margin-top: 4px;" ] [
      match discovered.Solutions.IsEmpty with
      | false ->
        Text.raw "Will use solution file. Click 'Create Session' to proceed."
      | true ->
        Text.raw "Will load all projects. Click 'Create Session' to proceed."
    ]
]

let private renderDiscoverConfigNotes (dirConfig: DirectoryConfig option) =
  match dirConfig with
  | Some config ->
    [
      yield
        match config.Load with
        | Solution path ->
          Elem.div [ Attr.class' "output-line output-info"; Attr.style "margin-bottom: 4px;" ] [
            Text.raw (sprintf "⚙️ .SageFs/config.fsx: solution %s" path)
          ]
        | Projects paths ->
          Elem.div [ Attr.class' "output-line output-info"; Attr.style "margin-bottom: 4px;" ] [
            Text.raw (sprintf "⚙️ .SageFs/config.fsx: %s" (String.Join(", ", paths)))
          ]
        | NoLoad ->
          Elem.div [ Attr.class' "output-line meta"; Attr.style "margin-bottom: 4px;" ] [
            Text.raw "⚙️ .SageFs/config.fsx: no project loading (bare session)"
          ]
        | AutoDetect ->
          Elem.div [ Attr.class' "output-line meta"; Attr.style "margin-bottom: 4px;" ] [
            Text.raw "⚙️ .SageFs/config.fsx found (auto-detect projects)"
          ]

      if not config.AutoOpenNamespaces then
        yield Elem.div [ Attr.class' "output-line meta"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw "⚙️ .SageFs/config.fsx: warmup auto-open disabled (sessions won't auto-open namespaces/modules)"
        ]
    ]
  | None -> []

let renderDiscoveredProjects (discovered: DiscoveredProjects) =
  Elem.div [ Attr.id DomIds.DiscoveredProjects; Attr.style "margin-top: 0.5rem;" ] (
    renderDiscoveredProjectsBody discovered
  )

let renderDiscoveredProjectsWithConfig (dirConfig: DirectoryConfig option) (discovered: DiscoveredProjects) =
  Elem.div [ Attr.id DomIds.DiscoveredProjects; Attr.style "margin-top: 0.5rem;" ] [
    yield! renderDiscoverConfigNotes dirConfig
    yield! renderDiscoveredProjectsBody discovered
  ]

/// Push discover results for a directory via SSE.
let pushDiscoverResults (ctx: HttpContext) (dir: string) = task {
  let dirConfig = DirectoryConfig.load dir
  let discovered = discoverProjects dir
  do! ssePatchNode ctx (renderDiscoveredProjectsWithConfig dirConfig discovered)
}

/// Helper: render an eval-result error fragment.
let evalResultError (msg: string) =
  Elem.div [ Attr.id DomIds.EvalResult ] [
    Elem.pre [ Attr.class' "output-line output-error"; Attr.style "margin-top: 0.5rem;" ] [
      Text.raw msg
    ]
  ]


