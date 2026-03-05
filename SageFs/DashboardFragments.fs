module SageFs.Server.DashboardFragments

open System
open System.IO
open Falco
open Falco.Markup
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open SageFs
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
let themePresetsJs () =
  let entries =
    ThemePresets.all
    |> List.map (fun (name, config) ->
      sprintf "  %s: `%s`" (System.Text.Json.JsonSerializer.Serialize(name)) (Theme.toCssVariables config))
  sprintf "{\n%s\n}" (String.concat ",\n" entries)

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
let renderThemePicker (selectedTheme: string) =
  Elem.select
    [ Attr.id DomIds.ThemePicker; Attr.class' "theme-select" ]
    (ThemePresets.all |> List.map (fun (name, _) ->
      Elem.option
        ([ Attr.value name ] @ (match name = selectedTheme with | true -> [ Attr.create "selected" "selected" ] | false -> []))
        [ Text.raw name ]))


let renderSessionStatus (sessionState: string) (sessionId: string) (workingDir: string) (warmupProgress: string) =
  let warmupNode =
    match warmupProgress.Length > 0 with
    | true ->
      [ Elem.br []
        Elem.span [ Attr.class' "meta warmup-progress" ] [
          Text.raw (sprintf "⏳ %s" warmupProgress)
        ] ]
    | false -> []
  match sessionState with
  | "Ready" ->
    Elem.div [ Attr.id DomIds.SessionStatus; Attr.create "data-working-dir" workingDir ] [
      yield Elem.span [ Attr.class' "status status-ready" ] [ Text.raw sessionState ]
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
      yield Elem.br []
      yield Elem.span [ Attr.class' "meta" ] [
        Text.raw (sprintf "Session: %s | CWD: %s" sessionId workingDir)
      ]
      yield! warmupNode
    ]

/// Render eval stats as an HTML fragment.
let renderEvalStats (stats: EvalStatsView) =
  Elem.div [ Attr.id DomIds.EvalStats; Attr.class' "meta" ] [
    Text.raw (sprintf "%d evals · avg %.0fms · min %.0fms · max %.0fms" stats.Count stats.AvgMs stats.MinMs stats.MaxMs)
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
          match s.IsSelected, s.IsActive with
          | true, _ -> "session-selected"
          | false, true -> "output-result"
          | false, false -> ""
        Elem.div
          [ Attr.class' (sprintf "session-row flex-between %s" cls)
            Attr.style "padding: 8px 0; border-bottom: 1px solid var(--border-normal); cursor: pointer;"
            Ds.onEvent ("click", sprintf "fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'sessionSetIndex',value:'%d'})}).then(function(){fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'sessionSelect'})})})" i) ]
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
                    Ds.onClick (Ds.post (sprintf "/dashboard/session/switch/%s" s.Id)) ]
                  [ Text.raw "⇄" ]
              | true -> ()
              Elem.button
                [ Attr.class' "session-btn session-btn-danger"
                  Ds.onClick (Ds.post (sprintf "/dashboard/session/stop/%s" s.Id)) ]
                [ Text.raw "■" ]
            ]
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
  Elem.div [ Attr.id DomIds.Main ] [
    // Theme CSS variables — morphed with every push so theme changes propagate
    snap.ThemeVars
    // App header — version, status, stats, sidebar toggle
    Elem.div [ Attr.class' "app-header" ] [
      Elem.h1 [] [ Text.raw (sprintf "🧙 SageFs v%s" snap.Version) ]
      Elem.div [ Attr.class' "flex-row"; Attr.style "gap: 0.75rem;" ] [
        renderSessionStatus snap.SessionState snap.SessionId snap.WorkingDir snap.WarmupProgress
        renderEvalStats snap.EvalStats
        Elem.button
          [ Attr.class' "sidebar-toggle"
            Ds.onEvent ("click", "$sidebarOpen = !$sidebarOpen")
            Ds.text "$sidebarOpen ? '✕ Panel' : '☰ Panel'" ]
          []
      ]
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
                  Ds.onEvent ("input", "clearTimeout(window._compTimer); var ta=this; window._compTimer=setTimeout(function(){ var code=ta.value; var pos=ta.selectionStart; if(pos>0 && (code[pos-1]==='.' || (code[pos-1]>='a' && code[pos-1]<='z') || (code[pos-1]>='A' && code[pos-1]<='Z'))) { var sid=document.querySelector('[data-signal-sessionId]'); var sidVal=sid?sid.value:''; fetch('/dashboard/completions',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code:code,cursorPos:pos,sessionId:sidVal})}).then(r=>r.json()).then(d=>{ var dd=document.getElementById('completion-dropdown'); if(d.completions && d.completions.length>0){ dd.innerHTML=d.completions.map(function(c,i){return '<div class=\"comp-item\" data-insert=\"'+c.insertText+'\" style=\"padding:2px 6px;cursor:pointer;'+(i===0?'background:var(--bg-selection)':'')+'\">'+c.label+' <span style=\"opacity:0.5;font-size:0.8em\">('+c.kind+')</span></div>'}).join(''); dd.style.display='block'; dd.querySelectorAll('.comp-item').forEach(function(el){ el.onclick=function(){ var ins=el.dataset.insert; var before=ta.value.substring(0,pos); var wordStart=before.search(/[a-zA-Z0-9_]*$/); ta.value=ta.value.substring(0,wordStart)+ins+ta.value.substring(pos); ta.selectionStart=ta.selectionEnd=wordStart+ins.length; ta.dispatchEvent(new Event('input')); dd.style.display='none'; ta.focus(); }}) } else { dd.style.display='none' } }).catch(function(){}) } }, 300)")
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
      // Sidebar — sessions, diagnostics, panels
      Elem.div [ Attr.id DomIds.Sidebar; Attr.class' "sidebar"; Ds.class' ("collapsed", "!$sidebarOpen") ] [
        Elem.div [ Attr.class' "sidebar-inner" ] [
          // Sessions panel
          Elem.div [ Attr.class' "panel" ] [
            Elem.h2 [] [ Text.raw "Sessions" ]
            connectionNode
            snap.SessionsPanel
          ]
          // Create Session
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
          // Dynamic sidebar panels — rendered from current state
          snap.TestTracePanel
          snap.HotReloadPanel
          snap.SessionContextPanel
          // Theme picker
          Elem.div [ Attr.class' "panel" ] [
            Elem.h2 [] [ Text.raw "Theme" ]
            snap.ThemePicker
          ]
        ]
      ]
    ]
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
    Elem.h2 [] [ Text.raw "Hot Reload" ]
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

/// Render empty hot-reload panel when no session is active.
let renderHotReloadEmpty =
  Elem.div [ Attr.id DomIds.HotReloadPanel; Attr.class' "panel" ] [
    Elem.h2 [] [ Text.raw "Hot Reload" ]
    Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.8rem;" ] [
      Text.raw "No active session"
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
                    let kind = match f.IsModule with | true -> "module" | false -> "namespace"
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

// ── Test Trace Panel ──────────────────────────────────────────────

let private renderTestPhase (label: string) (ms: float) (maxMs: float) (icon: string) =
  let pct = match maxMs > 0.0 with | true -> min 100.0 (ms / maxMs * 100.0) | false -> 0.0
  let color =
    match ms with
    | ms when ms < 50.0 -> "var(--fg-green, #27ae60)"
    | ms when ms < 500.0 -> "var(--fg-yellow, #f39c12)"
    | _ -> "var(--fg-red, #e74c3c)"
  Elem.div [ Attr.style "margin-bottom: 4px;" ] [
    Elem.div [ Attr.style "display: flex; justify-content: space-between; font-size: 0.75rem;" ] [
      Elem.span [] [ Text.raw (sprintf "%s %s" icon label) ]
      Elem.span [] [ Text.raw (sprintf "%.0fms" ms) ]
    ]
    Elem.div [ Attr.class' "progress-track-sm" ] [
      Elem.div [ Attr.style (sprintf "width: %.0f%%; height: 100%%; background: %s; border-radius: 2px;" pct color) ] []
    ]
  ]

let renderTestTracePanel
  (timing: Features.LiveTesting.TestCycleTiming option)
  (isRunning: bool)
  (summary: Features.LiveTesting.TestSummary)
  =
  let statusLabel =
    match summary.Enabled, isRunning with
    | false, _ -> "⏸ disabled"
    | true, true -> "⏳ running"
    | true, false -> "✅ idle"
  let summaryParts = [
    yield Elem.span [ Attr.style "color: var(--fg-green, #27ae60);" ] [
      Text.raw (sprintf "✓ %d" summary.Passed) ]
    yield Elem.span [ Attr.style "color: var(--fg-red, #e74c3c);" ] [
      Text.raw (sprintf "✗ %d" summary.Failed) ]
    yield Elem.span [ Attr.style "opacity: 0.6;" ] [
      Text.raw (sprintf "/ %d" summary.Total) ]
    match summary.Stale > 0 with
    | true ->
      yield Elem.span [ Attr.style "color: var(--fg-yellow, #f39c12);" ] [
        Text.raw (sprintf "⟳ %d stale" summary.Stale) ]
    | false -> ()
    match summary.Running > 0 with
    | true ->
      yield Elem.span [ Attr.style "color: var(--fg-cyan, #3498db);" ] [
        Text.raw (sprintf "⏳ %d" summary.Running) ]
    | false -> ()
  ]
  let timingSection =
    match timing with
    | None ->
      Elem.div [ Attr.style "font-size: 0.75rem; opacity: 0.5; padding: 4px 0;" ] [
        Text.raw "No timing data yet" ]
    | Some t ->
      let tsMs, fcsMs, execMs =
        match t.Depth with
        | Features.LiveTesting.TestCycleDepth.TreeSitterOnly ts -> ts.TotalMilliseconds, 0.0, 0.0
        | Features.LiveTesting.TestCycleDepth.ThroughFcs (ts, fcs) -> ts.TotalMilliseconds, fcs.TotalMilliseconds, 0.0
        | Features.LiveTesting.TestCycleDepth.ThroughExecution (ts, fcs, exec) -> ts.TotalMilliseconds, fcs.TotalMilliseconds, exec.TotalMilliseconds
      let totalMs = tsMs + fcsMs + execMs
      let maxMs = max totalMs 1.0
      Elem.div [] [
        yield renderTestPhase "Tree-sitter" tsMs maxMs "🌳"
        match t.Depth with
        | Features.LiveTesting.TestCycleDepth.TreeSitterOnly _ -> ()
        | _ -> yield renderTestPhase "FCS Check" fcsMs maxMs "🔍"
        match t.Depth with
        | Features.LiveTesting.TestCycleDepth.ThroughExecution _ -> yield renderTestPhase "Execution" execMs maxMs "🧪"
        | _ -> ()
        yield Elem.div [ Attr.style "font-size: 0.7rem; opacity: 0.5; margin-top: 4px;" ] [
          Text.raw (sprintf "%.0fms total • %d/%d tests • %s"
            totalMs t.AffectedTests t.TotalTests
            (match t.Trigger with
             | Features.LiveTesting.RunTrigger.Keystroke -> "keystroke"
             | Features.LiveTesting.RunTrigger.FileSave -> "save"
             | Features.LiveTesting.RunTrigger.ExplicitRun -> "manual"))
        ]
      ]
  Elem.div [ Attr.id DomIds.TestTrace; Attr.class' "panel" ] [
    Elem.div [ Attr.style "display: flex; justify-content: space-between; align-items: center;" ] [
      Elem.h2 [ Attr.style "margin: 0;" ] [ Text.raw "Tests" ]
      Elem.span [ Attr.style "font-size: 0.7rem; opacity: 0.7;" ] [ Text.raw statusLabel ]
    ]
    Elem.div [ Attr.style "display: flex; gap: 8px; font-size: 0.75rem; margin: 6px 0;" ] summaryParts
    timingSection
  ]

let renderTestTraceEmpty =
  Elem.div [ Attr.id DomIds.TestTrace; Attr.class' "panel" ] [
    Elem.h2 [] [ Text.raw "Tests" ]
    Elem.div [ Attr.style "font-size: 0.8rem; opacity: 0.6;" ] [
      Text.raw "No active session"
    ]
  ]

/// Create the SSE stream handler that pushes Elm state to the browser.

let renderDiscoveredProjects (discovered: DiscoveredProjects) =
  Elem.div [ Attr.id DomIds.DiscoveredProjects; Attr.style "margin-top: 0.5rem;" ] [
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

/// Push discover results for a directory via SSE.
let pushDiscoverResults (ctx: HttpContext) (dir: string) = task {
  let dirConfig = DirectoryConfig.load dir
  let discovered = discoverProjects dir
  let configNote =
    match dirConfig with
    | Some config ->
      match config.Load with
      | Solution path ->
        Some (Elem.div [ Attr.class' "output-line output-info"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw (sprintf "⚙️ .SageFs/config.fsx: solution %s" path)
        ])
      | Projects paths ->
        Some (Elem.div [ Attr.class' "output-line output-info"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw (sprintf "⚙️ .SageFs/config.fsx: %s" (String.Join(", ", paths)))
        ])
      | NoLoad ->
        Some (Elem.div [ Attr.class' "output-line meta"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw "⚙️ .SageFs/config.fsx: no project loading (bare session)"
        ])
      | AutoDetect ->
        Some (Elem.div [ Attr.class' "output-line meta"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw "⚙️ .SageFs/config.fsx found (auto-detect projects)"
        ])
    | None -> None
  let mainContent = renderDiscoveredProjects discovered
  match configNote with
  | Some note ->
    let combined = Elem.div [ Attr.id DomIds.DiscoveredProjects; Attr.style "margin-top: 0.5rem;" ] [
      note; mainContent
    ]
    do! ssePatchNode ctx combined
  | None ->
    do! ssePatchNode ctx mainContent
}

/// Helper: render an eval-result error fragment.
let evalResultError (msg: string) =
  Elem.div [ Attr.id DomIds.EvalResult ] [
    Elem.pre [ Attr.class' "output-line output-error"; Attr.style "margin-top: 0.5rem;" ] [
      Text.raw msg
    ]
  ]


