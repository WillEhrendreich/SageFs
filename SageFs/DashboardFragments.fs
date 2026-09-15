module SageFs.Server.DashboardFragments

open System
open System.IO
open Falco
open Falco.Markup
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Measures
open SageFs.WarmUp
open SageFs.Affordances
open SageFs.Server.DashboardTypes

/// Use renderNode + sseStringElements instead of sseHtmlElements
/// (which prepends DOCTYPE to every fragment, causing Datastar to choke).
let ssePatchNode (ctx: HttpContext) (node: XmlNode) =
  Falco.Datastar.Response.sseStringElements ctx (renderNode node)

/// Escape the five HTML-significant characters — & < > " ' — and nothing else.
///
/// That set is exactly what can open a tag, close a quoted attribute, or start
/// an entity, so the result is inert in text content and in quoted attribute
/// values. Everything else (·, é, emoji) passes through unchanged. (Falco's
/// `Text.enc`/WebUtility.HtmlEncode also entity-encodes every char >= U+00A0,
/// which rewrites benign user-visible text into `&#183;`/`&#128994;` for no
/// security gain.)
let htmlEscape (s: string) : string =
  match String.IsNullOrEmpty s || s.IndexOfAny([| '&'; '<'; '>'; '"'; '\'' |]) < 0 with
  | true -> (match isNull s with | true -> "" | false -> s)
  | false ->
    let sb = Text.StringBuilder(s.Length + 16)
    for c in s do
      match c with
      | '&' -> sb.Append("&amp;") |> ignore
      | '<' -> sb.Append("&lt;") |> ignore
      | '>' -> sb.Append("&gt;") |> ignore
      | '"' -> sb.Append("&quot;") |> ignore
      | '\'' -> sb.Append("&#39;") |> ignore
      | c -> sb.Append(c) |> ignore
    sb.ToString()

/// Text node for any runtime string (session ids, dirs, project names,
/// messages, eval output). The ONE place a computed string reaches
/// `Text.raw` — every other `Text.raw` in the dashboard takes a string
/// literal, which DashboardEscapingTests enforces structurally.
let textEnc (s: string) : XmlNode = Text.raw (htmlEscape s)

/// Encode a runtime string for use as an attribute VALUE. Falco.Markup emits
/// attribute values verbatim inside double quotes, so a `"` in a working
/// directory, title, or path would otherwise break out of the attribute.
let attrEnc (s: string) : string = htmlEscape s

/// Stable automation hook for demo/E2E tooling. `name` is always a literal
/// from the fixed kebab-case vocabulary the demos tooling mirrors — never a
/// computed string — so a renamed control breaks a contract test, not a demo.
let testid (name: string) = Attr.create "data-testid" name

let renderKeyboardHelp () =
  let shortcut key desc =
    Elem.tr [] [
      Elem.td [ Attr.style "padding: 2px 8px; font-family: monospace; color: var(--fg-blue);" ] [ textEnc key ]
      Elem.td [ Attr.style "padding: 2px 8px;" ] [ textEnc desc ]
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
        Attr.style "display:none; position:absolute; bottom:100%; left:0; max-height:200px; overflow-y:auto; background:var(--bg-default); border:1px solid var(--bg-selection); border-radius:0; z-index:100; min-width:200px; font-size:0.85em; box-shadow:0 -2px 8px rgba(0,0,0,0.3);" ]
      []
  | items ->
    Elem.div
      [ Attr.id DomIds.CompletionDropdown
        Attr.style "display:block; position:absolute; bottom:100%; left:0; max-height:200px; overflow-y:auto; background:var(--bg-default); border:1px solid var(--bg-selection); border-radius:0; z-index:100; min-width:200px; font-size:0.85em; box-shadow:0 -2px 8px rgba(0,0,0,0.3);" ]
      (items |> List.mapi (fun i item ->
        Elem.div
          [ Attr.class' "comp-item"
            Attr.style (sprintf "padding:2px 6px;cursor:pointer;%s" (match i with | 0 -> "background:var(--bg-selection)" | _ -> ""))
            Ds.onEvent ("click", sprintf "window._insertComp('%s',%d)" (escJs item.ReplacementText) cursorPos) ]
          [ textEnc item.DisplayText
            Elem.span [ Attr.style "opacity:0.5;font-size:0.8em;margin-left:4px;" ] [
              textEnc (sprintf "(%s)" (Features.AutoCompletion.CompletionKind.label item.Kind))
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
      // Send the freshly-selected value (not the signal, which is updated
      // async by data-bind) by reading the select element at event time.
      // (event.target, not `this` — Datastar leaves `this` unbound.)
      Ds.onEvent ("change", "var t=event.target.value; @post('/dashboard/set-theme', {theme: t})") ]
    (ThemePresets.all |> List.map (fun (name, _) ->
      Elem.option
        ([ Attr.value name ] @ (match name = selectedTheme with | true -> [ Attr.create "selected" "selected" ] | false -> []))
        [ textEnc name ]))


let renderSessionStatus (sessionState: string) (sessionId: string) (workingDir: string) (warmupProgress: string) (workflowLabel: string) =
  let warmupNode =
    match warmupProgress.Length > 0 with
    | true ->
      [ Elem.br []
        Elem.span [ Attr.class' "meta warmup-progress" ] [
          Text.raw "⏳ "
          textEnc warmupProgress
        ] ]
    | false -> []
  let workflowBadgeClass =
    match workflowLabel with
    | "Live" -> "badge badge-live"
    | _ -> "badge badge-workflow"
  let workflowNode =
    [ Elem.span [ Attr.class' workflowBadgeClass ] [
        textEnc workflowLabel
      ] ]
  let statusClass =
    match sessionState with
    | "Ready" -> "status-ready"
    | "WarmingUp" -> "status-warming"
    | _ -> "status-faulted"
  Elem.div [ Attr.id DomIds.SessionStatus; Attr.create "data-working-dir" (attrEnc workingDir) ] [
    yield Elem.span [ Attr.class' (sprintf "status %s" statusClass) ] [ textEnc sessionState ]
    yield! workflowNode
    yield Elem.br []
    yield Elem.span [ Attr.class' "meta" ] [
      textEnc (sprintf "Session: %s | CWD: %s" sessionId workingDir)
    ]
    yield! warmupNode
  ]

/// Render system alarm banner — visible when ElmLoop throws at any catch site.
/// Empty list renders a hidden placeholder so Datastar can morph it away.
let private disclosureSummaryStyle =
  "cursor: pointer; font-weight: bold; font-size: 0.9rem; user-select: none;"

/// A `<details>` accordion whose open/closed state lives in a Datastar signal
/// instead of the DOM `open` attribute.
///
/// The SSE fallback re-renders `#main` on every tick (a ticking label defeats
/// the no-change dedupe), and a morph replaces the element WITHOUT `open` —
/// so a raw `<details>` snaps every open accordion shut about once a second.
/// Signals live in Datastar's client-side store, not the DOM: binding `open`
/// to `$signalName` means the surviving signal re-supplies `open` after every
/// morph, and the `toggle` event keeps the signal in sync with what the user
/// actually clicked (and, since Datastar sends signals with every `@post`,
/// "synced with the server" — the server sees the current open state on the
/// next action).
///
/// For a signal with a fixed, known name, declare its initial state once via
/// `Ds.signal (name, initialOpen)` in the page's signal list (`Dashboard.fs`)
/// so the GET render, the SSE stream, and every morph agree on the same
/// starting value. A signal name computed per-item (one `<details>` per row
/// in a dynamically-sized list, e.g. per session or per tree node) needs no
/// separate declaration: until the user toggles it, `$name` reads as
/// undefined (falsy) everywhere — GET, stream, and morph alike — which
/// renders closed, and the first `toggle` creates the signal in the store.
let signalDetails (signalName: string) (attrs: XmlAttribute list) (children: XmlNode list) : XmlNode =
  // Every collapsible panel gets a stable DOM id (the Datastar way — things are
  // targetable). Callers may pass their own id; otherwise derive one from the
  // signal name so no panel renders id-less.
  let hasId = attrs |> List.exists (function KeyValueAttr("id", _) -> true | _ -> false)
  let idAttr = if hasId then [] else [ Attr.id signalName ]
  Elem.details
    (idAttr @ attrs
     @ [ Ds.attr' ("open", sprintf "$%s" signalName)
         Ds.onEvent ("toggle", sprintf "$%s = event.target.open" signalName) ])
    children

/// Reduce arbitrary (possibly FSI-derived) text to identifier characters, for
/// splicing into a Datastar signal name inside an attribute expression.
let private signalIdent (s: string) : string =
  s |> String.map (fun c -> match Char.IsAsciiLetterOrDigit c || c = '_' with | true -> c | false -> '_')

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
            textEnc (sprintf "[%s]" alarm.Phase)
          ]
          Elem.span [ Attr.class' "alarm-message" ] [
            textEnc (sprintf " %s" alarm.Message)
          ]
          Elem.span [ Attr.class' "alarm-ts meta" ] [
            textEnc (sprintf " @ %s" (alarm.Timestamp.ToLocalTime().ToString("HH:mm:ss")))
          ]
        ])
    Elem.div [ Attr.id DomIds.AlarmBanner; Attr.class' "alarm-banner" ] [
      signalDetails Signals.AlarmBannerOpen [] [
        Elem.summary [ Attr.style disclosureSummaryStyle ] [
          Elem.span [ Attr.class' "alarm-icon" ] [ Text.raw "🚨" ]
          Elem.span [ Attr.class' "alarm-title" ] [
            textEnc (sprintf " System Alarm (%d)" alarmCount)
          ]
        ]
        Elem.div [ Attr.style "margin-top: 0.5rem;" ] [
          Elem.div [ Attr.class' "alarm-banner-header" ] [
            Elem.span [ Attr.class' "meta" ] [ textEnc alarmCountLabel ]
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

/// Small color-coded auto-open state icon for a session card.
/// Green = auto-open ON (namespaces/modules will be opened during warmup),
/// dim/red = OFF (skipped). The tooltip spells out the state and action;
/// clicking toggles the per-directory config.
let renderAutoOpenToggleIcon (enabled: bool) =
  let endpoint, glyph, color, tooltip =
    match enabled with
    | true ->
      "/dashboard/config/disable-auto-open",
      "◎",
      "var(--fg-green)",
      "Warmup auto-open is ON — namespaces/modules in this project are opened automatically during warmup. Click to disable (writes .SageFs/config.fsx with AutoOpenNamespaces = false)."
    | false ->
      "/dashboard/config/enable-auto-open",
      "◌",
      "var(--fg-red)",
      "Warmup auto-open is OFF — namespaces/modules are NOT opened automatically during warmup. Click to enable (rewrites .SageFs/config.fsx back to AutoOpenNamespaces = true)."
  Elem.button
    [ Attr.class' "session-btn session-btn-autoopen"
      Attr.title tooltip
      Attr.style (sprintf "color: %s;" color)
      Ds.onClick (Ds.post endpoint) ]
    [ textEnc glyph ]

/// Context-aware warmup auto-open toggle (full-width variant).
/// Kept for potential use in panels where a labeled button fits better than
/// the compact card icon (renderAutoOpenToggleIcon).
let renderAutoOpenToggleButton (enabled: bool) (style: string) =
  let endpoint, label =
    match enabled with
    | true ->
      "/dashboard/config/disable-auto-open",
      Elem.span [] [ Text.raw "✓ Auto-Open On — click to disable" ]
    | false ->
      "/dashboard/config/enable-auto-open",
      Elem.span [] [ Text.raw "✗ Auto-Open Off — click to enable" ]
  Elem.button
    [ Attr.class' "eval-btn"
      Attr.style style
      Ds.indicator Signals.ConfigLoading
      Ds.attr' ("disabled", "$configLoading")
      Ds.onClick (Ds.post endpoint) ]
    [ Elem.span [ Ds.show "$configLoading" ] [ Text.raw "⏳ " ]
      Elem.span [ Ds.show "!$configLoading" ] [ Text.raw "⚙ " ]
      label ]

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
      // Daemon facts only (version · uptime · memory). Readiness lives in the
      // top status tab + the green dot; the no-session state is shown by the
      // Sessions panel empty-state — neither is repeated here.
      let statusText =
        match view.SessionSummaries with
        | [] -> emoji
        | _ -> sprintf "%s %s" emoji label
      textEnc (sprintf "%s · SageFs %s · up %s · %dMB"
        statusText view.Version view.UptimeLabel view.MemoryMB)
    ]
    match sessionSummaryText view.SessionSummaries with
    | None -> ()
    | Some txt ->
      Elem.span [ Attr.class' "session-health-list"; Attr.style "margin-left: 0.5rem;" ] [
        textEnc txt
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
          sprintf "%d failure%s · showing top %d" total (if total = 1 then "" else "s") view.Entries.Length
        | 0 ->
          sprintf "%d failure%s" total (if total = 1 then "" else "s")
        | suppressed when view.Entries.IsEmpty ->
          sprintf "%d failure%s · %d have no baseline yet" total (if total = 1 then "" else "s") suppressed
        | suppressed ->
          sprintf "%d failure%s · %d with context · %d no baseline" total (if total = 1 then "" else "s") view.Entries.Length suppressed
      signalDetails Signals.FailureNarrativesOpen [] [
        Elem.summary [ Attr.style disclosureSummaryStyle ] [
          Elem.span [ Attr.class' "failure-count-badge"; Attr.style "font-weight: bold; margin-right: 0.5rem;" ] [
            Text.raw "🔴 "
            textEnc badgeText
          ]
        ]
        Elem.div [ Attr.style "margin-top: 0.25rem;" ] [
          for entry in view.Entries do
            let shortName =
              let parts = entry.TestName.Split('.')
              if parts.Length > 1 then parts.[parts.Length - 1] else entry.TestName
            Elem.div [ Attr.class' "narrative-entry"; Attr.style "margin-top: 0.25rem;" ] [
              Elem.span [ Attr.class' "narrative-test-name"; Attr.style "font-weight: bold;" ] [
                Text.raw "🔴 "
                textEnc shortName
              ]
              match entry.TimeSinceLabel with
              | Some label ->
                Elem.span [ Attr.class' "meta narrative-timing"; Attr.style "margin-left: 0.5rem;" ] [
                  textEnc (sprintf "was passing %s" label)
                ]
              | None -> ()
              Elem.span [ Attr.class' "narrative-summary"; Attr.style "margin-left: 0.5rem;" ] [
                textEnc entry.Summary
              ]
              match entry.CausalLabels with
              | [] -> ()
              | labels ->
                Elem.span [ Attr.class' "meta narrative-causal"; Attr.style "margin-left: 0.5rem;" ] [
                  textEnc (sprintf "→ %s" (labels |> String.concat ", "))
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
    textEnc (sprintf "%d evals · avg %.0fms · min %.0fms · max %.0fms" stats.Count stats.AvgMs stats.MinMs stats.MaxMs)
    match stats.Sparkline with
    | "" -> ()
    | sparkline ->
      Elem.span [ Attr.class' "eval-sparkline"; Attr.title "Recent eval latency (oldest → newest)" ] [
        textEnc (sprintf " %s" sparkline)
      ]
      Elem.span [ Attr.class' "eval-percentiles meta" ] [
        textEnc (sprintf " · P50 %s · P95 %s"
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
    textEnc (sprintf "%s %s" stage.Name icon)
    Elem.span [ Attr.class' "stage-duration" ] [
      textEnc (sprintf " [%.0fms]" stage.DurationMs)
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
        textEnc (sprintf " [%.0fms total]" railway.TotalMs)
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
  | true -> [ textEnc line ]
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
        nodes.Add(textEnc (line.Substring(pos, gapEnd - pos)))
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
          nodes.Add(Elem.span [ Attr.class' cssClass ] [ textEnc text ])
        | false ->
          nodes.Add(textEnc text)
        pos <- end'
      | false -> ()
    match pos < line.Length with
    | true ->
      nodes.Add(textEnc (line.Substring(pos)))
    | false -> ()
    nodes |> Seq.toList

/// Render output lines as an HTML fragment.
let renderOutputForSession (sessionId: string) (lines: OutputLine list) (placeholder: string) =
  Elem.div [ Attr.id DomIds.OutputPanel; testid "session-output"; Attr.create "data-session-id" (attrEnc sessionId) ] [
    match lines.IsEmpty with
    | true ->
      Elem.span [ Attr.class' "meta" ] [ textEnc placeholder ]
    | false ->
      yield! lines |> List.map (fun line ->
        let css = OutputLineKind.toCssClass line.Kind
        Elem.div [ Attr.class' (sprintf "output-line %s" css) ] [
          match line.Timestamp with
          | Some t ->
            Elem.span [ Attr.class' "meta"; Attr.style "margin-right: 0.5rem;" ] [
              textEnc t
            ]
          | None -> ()
          match (line.Kind = ResultLine || line.Kind = InfoLine) && SyntaxHighlight.isAvailable () with
          | true ->
            let allSpans = SyntaxHighlight.tokenize Theme.defaults line.Text
            match allSpans.Length > 0 with
            | true -> yield! renderHighlightedLine allSpans.[0] line.Text
            | false -> textEnc line.Text
          | false ->
            textEnc line.Text
        ])
  ]

let renderOutput (lines: OutputLine list) (placeholder: string) =
  renderOutputForSession "" lines placeholder

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
            textEnc (DiagSeverity.toIcon diag.Severity)
          ]
          match diag.Line > 0 || diag.Col > 0 with
          | true ->
            Elem.span [ Attr.class' "diag-location" ] [
              textEnc (sprintf "L%d:%d" diag.Line diag.Col)
            ]
          | false -> ()
          Elem.span [] [
            textEnc (sprintf " %s" diag.Message)
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
            testid "quick-start"
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
                  Attr.style "flex: 1; font-size: 0.8rem; display: inline-flex; align-items: center; justify-content: center; gap: 2px; height: 2rem;"
                  Ds.indicator Signals.DiscoverLoading
                  Ds.attr' ("disabled", "$discoverLoading")
                  Ds.onClick (Ds.post "/dashboard/discover-projects") ]
                [ Elem.span [ Ds.show "$discoverLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$discoverLoading" ] [ Text.raw "🔍 " ]
                  Text.raw "Discover" ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; font-size: 0.8rem; display: inline-flex; align-items: center; justify-content: center; gap: 2px; height: 2rem;"
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
                    Elem.span [ Attr.style "font-weight: bold;" ] [ textEnc s.Id ]
                    Elem.span [ Attr.class' "meta" ] [ textEnc age ]
                  ]
                  match s.WorkingDir.Length > 0 with
                  | true ->
                    Elem.div
                      [ Attr.style "font-size: 0.75rem; color: var(--fg-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                        Attr.title (attrEnc s.WorkingDir) ]
                      [ Text.raw "📁 "; textEnc s.WorkingDir ]
                  | false -> ()
                  match s.Projects.IsEmpty with
                  | false ->
                    Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 2px; flex-wrap: wrap;" ] [
                      yield! s.Projects |> List.map (fun p ->
                        Elem.span
                          [ Attr.class' "badge"; Attr.style "background: var(--bg-focus); color: var(--fg-dim);" ]
                          [ textEnc (Path.GetFileName p) ])
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
        Attr.style (sprintf "background:transparent;border:1px solid %s;color:%s;padding:1px 5px;font-size:0.6rem;border-radius:0;cursor:pointer;margin-right:2px;" color color) ]
      [ textEnc (sprintf "%s %d" label count) ]
  let activeBtn (label: string) (value: string) (count: int) (color: string) =
    Elem.button
      [ Attr.class' "test-filter-btn test-filter-active"
        Ds.onEvent ("click", "$testFilter = 'all'")
        Ds.show (sprintf "$testFilter === '%s'" value)
        Attr.style (sprintf "background:%s;color:#fff;padding:1px 5px;font-size:0.6rem;border-radius:0;cursor:pointer;margin-right:2px;border:1px solid %s;" color color) ]
      [ textEnc (sprintf "%s %d ✕" label count) ]
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
      [ Attr.style "position:relative;width:320px;height:180px;border-radius:0;overflow:hidden;background:var(--bg-focus,#1a1a1a);margin-top:4px;" ]
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
              Attr.title (attrEnc title)
              Ds.show (sprintf "$testFilter === 'all' || $testFilter === '%s'" statusFilter) ]
            [ match showLabel with
              | true ->
                Elem.div [ Attr.style "font-size:0.5rem;color:#fff;padding:1px 2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;line-height:1.1;" ] [
                  textEnc r.Entry.DisplayName
                ]
              | false -> ()
              match showDuration with
              | true ->
                Elem.div [ Attr.style "font-size:0.45rem;color:rgba(255,255,255,0.7);padding:0 2px;line-height:1;" ] [
                  textEnc durationLabel
                ]
              | false -> () ]) ]

// ── Coverage Treemap (WinDirStat/WizTree-style: solution → project → file → symbol) ──

/// Locked color legend: green = pass (a passing test covers it), red = fail
/// (a failing test touches it), grey = none (the gap — nothing exercises
/// it), amber = running (in flight, no verdict yet).
let private coverageStatusColor (status: Features.Treemap.CoverageStatus) =
  match status with
  | Features.Treemap.CoverageStatus.Covered -> "var(--fg-green,#27ae60)"
  | Features.Treemap.CoverageStatus.Failed -> "var(--fg-red,#e74c3c)"
  | Features.Treemap.CoverageStatus.Running -> "var(--fg-yellow,#f39c12)"
  | Features.Treemap.CoverageStatus.Uncovered -> "var(--fg-dim,#666)"

let private coverageStatusLabel (status: Features.Treemap.CoverageStatus) =
  match status with
  | Features.Treemap.CoverageStatus.Covered -> "pass"
  | Features.Treemap.CoverageStatus.Failed -> "fail"
  | Features.Treemap.CoverageStatus.Running -> "running"
  | Features.Treemap.CoverageStatus.Uncovered -> "none"

/// A single-quoted JS string literal for splicing a node id into a Datastar
/// expression (`Ds.onEvent`/`Ds.show` bodies are raw JS, not attribute text).
let private jsStringLiteral (s: string) =
  "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'"

let private coveragePercent (probeCount: int) (coveredCount: int) =
  match probeCount with
  | 0 -> 0.0
  | n -> float coveredCount / float n * 100.0

let private coverageLegendSwatch (label: string) (color: string) =
  Elem.span [ Attr.style "display:inline-flex;align-items:center;gap:2px;" ] [
    Elem.span [ Attr.style (sprintf "display:inline-block;width:8px;height:8px;background:%s;" color) ] []
    textEnc label
  ]

/// Render one drill level (root, a project, a file, or a symbol) plus every
/// descendant level, as sibling `<div>`s gated by the per-session drill
/// signal — clicking a packed region sets the signal to that region's `Id`,
/// so switching the visible level is a zero-round-trip client update (the
/// same "signal-driven, zero-JS filtering" pattern as `renderTestFilterBar`
/// above). The root level shows by default (`!$signal`) since no drill has
/// happened yet; every other level shows only on an exact id match — this
/// is what lets the signal morph-survive with no `Ds.attr'` rebind needed:
/// `Ds.show` is re-evaluated live from the client-side signal store on every
/// render, so a server morph can never reset "where am I" client state.
let rec private renderCoverageLevels
  (drillSignal: string)
  (width: float)
  (height: float)
  (parentId: string option)
  (node: Features.Treemap.CoverageTreemapNode)
  : XmlNode list =
  let idJs = jsStringLiteral node.Id
  let showExpr =
    match parentId with
    | None -> sprintf "!$%s || $%s === %s" drillSignal drillSignal idJs
    | Some _ -> sprintf "$%s === %s" drillSignal idJs
  let backLink =
    match parentId with
    | None -> Elem.div [] []
    | Some pid ->
      Elem.div
        [ Attr.style "cursor:pointer;font-size:0.65rem;color:var(--fg-blue);margin-bottom:2px;user-select:none;"
          Ds.onEvent ("click", sprintf "$%s = %s" drillSignal (jsStringLiteral pid)) ]
        [ Text.raw "← back" ]
  let body =
    match node.Children with
    | [] ->
      // Leaf — nothing further to drill into; show its own detail.
      let pct = coveragePercent node.ProbeCount node.CoveredCount
      Elem.div [ Attr.style "padding:4px 2px;font-size:0.7rem;" ] [
        Elem.div
          [ Attr.style (sprintf "font-weight:bold;color:%s;" (coverageStatusColor node.Status)) ]
          [ textEnc node.Name ]
        Elem.div [ Attr.class' "meta" ] [
          textEnc (sprintf "%s — %d/%d probes (%.0f%%)" (coverageStatusLabel node.Status) node.CoveredCount node.ProbeCount pct)
        ]
      ]
    | children ->
      let rects =
        Features.Treemap.CoverageTreemapNode.layoutChildren
          { Features.Treemap.Rect.X = 0.0; Y = 0.0; W = width; H = height } node
      Elem.div
        [ Attr.style (sprintf "position:relative;width:%.0fpx;height:%.0fpx;background:var(--bg-focus,#1a1a1a);overflow:hidden;" width height) ]
        [ yield! rects |> List.map (fun (child, r) ->
            let color = coverageStatusColor child.Status
            let pct = coveragePercent child.ProbeCount child.CoveredCount
            let title =
              sprintf "%s — %s (%d/%d probes, %.0f%%)"
                child.Name (coverageStatusLabel child.Status) child.CoveredCount child.ProbeCount pct
            let showLabel = r.W >= 26.0 && r.H >= 14.0
            Elem.div
              [ Attr.style
                  (sprintf
                    "position:absolute;left:%.1fpx;top:%.1fpx;width:%.1fpx;height:%.1fpx;background:%s;opacity:0.85;border:0.5px solid rgba(0,0,0,0.3);overflow:hidden;box-sizing:border-box;cursor:pointer;"
                    r.X r.Y r.W r.H color)
                Attr.title (attrEnc title)
                Ds.onEvent ("click", sprintf "$%s = %s" drillSignal (jsStringLiteral child.Id)) ]
              [ match showLabel with
                | true ->
                  Elem.div
                    [ Attr.style "font-size:0.5rem;color:#fff;padding:1px 2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;line-height:1.1;" ]
                    [ textEnc child.Name ]
                | false -> () ]) ]
  let levelDiv = Elem.div [ Ds.show showExpr ] [ backLink; body ]
  let childLevels =
    node.Children |> List.collect (renderCoverageLevels drillSignal width height (Some node.Id))
  levelDiv :: childLevels

/// Render the coverage drill-down treemap panel for one session: a locked
/// color legend plus every drill level (only one visible at a time, per
/// `renderCoverageLevels`). `sid` scopes the drill signal per session card
/// so multiple open panels never fight over one signal.
let renderCoverageTreemap (sid: string) (root: Features.Treemap.CoverageTreemapNode option) : XmlNode =
  match root with
  | None -> Elem.div [] []
  | Some root when root.ProbeCount = 0 -> Elem.div [] []
  | Some root ->
    let drillSignal = signalIdent (sprintf "coverageDrill_%s" sid)
    let legend =
      Elem.div
        [ Attr.style "display:flex;gap:6px;align-items:center;font-size:0.6rem;color:var(--fg-dim);margin-bottom:4px;flex-wrap:wrap;" ]
        [ Elem.span [] [ Text.raw "Legend:" ]
          coverageLegendSwatch "pass" (coverageStatusColor Features.Treemap.CoverageStatus.Covered)
          coverageLegendSwatch "fail" (coverageStatusColor Features.Treemap.CoverageStatus.Failed)
          coverageLegendSwatch "running" (coverageStatusColor Features.Treemap.CoverageStatus.Running)
          coverageLegendSwatch "none" (coverageStatusColor Features.Treemap.CoverageStatus.Uncovered) ]
    Elem.div [ Attr.style "overflow-x:auto;" ] [
      legend
      Elem.div [] (renderCoverageLevels drillSignal 300.0 160.0 None root)
    ]

/// Render per-session bound values explorer (collapsible, with values)
let renderBindingExplorer (bindings: Features.BindingExplorer.BindingInfo array) : XmlNode =
  match bindings.Length with
  | 0 -> Elem.div [] []
  | _ ->
    Elem.div [ Attr.style "font-size: 0.72rem; max-height: 200px; overflow-y: auto;" ] [
      for b in bindings do
        Elem.div
          [ Attr.style "display:flex;align-items:baseline;gap:0.4em;padding:2px 0;border-bottom:1px solid var(--border-normal,#333);" ]
          [ Elem.code
              [ Attr.style "color:var(--fg-cyan,#56b6c2);font-weight:bold;white-space:nowrap;font-size:0.7rem;" ]
              [ textEnc b.Name ]
            Elem.span
              [ Attr.style "color:var(--fg-dim,#666);font-size:0.65rem;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;" ]
              [ textEnc (sprintf ": %s" b.TypeSig) ]
            match b.Value with
            | Some v ->
              Elem.span
                [ Attr.style "color:var(--fg-green,#98c379);font-size:0.65rem;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:120px;"
                  Attr.title (attrEnc v) ]
                [ textEnc (sprintf "= %s" v) ]
            | None -> ()
            match b.ReferencedIn.Length with
            | 0 -> ()
            | n ->
              Elem.span
                [ Attr.style "color:var(--fg-yellow,#e5c07b);font-size:0.6rem;white-space:nowrap;" ]
                [ textEnc (sprintf "→%d" n) ] ]
    ]

/// Render the "⏳ Stopping session id:[id]..." card that replaces a session's
/// display card while it unloads/disposes itself.
/// Shares the session-card-<id> DOM id with the card in renderSessions so
/// Datastar morphs the card in place.
let renderStoppingCard (sessionId: WorkerProtocol.SessionId) =
  let sid = WorkerProtocol.SessionId.value sessionId
  Elem.div
    [ Attr.id (sprintf "session-card-%s" sid)
      Attr.class' "session-row session-stopping"
      testid "session-card"
      Attr.style "padding: 8px 0; border-bottom: 1px solid var(--border-normal);" ]
    [ Elem.span [ Attr.style "font-weight: bold;" ] [ textEnc sid ]
      Elem.span [ Attr.class' "meta"; Attr.style "margin-left: 0.5rem;" ] [
        textEnc (sprintf "⏳ Stopping session id:%s..." sid)
      ] ]

/// Render sessions as an HTML fragment with action buttons.
let renderSessionsForSession (viewingSessionId: string) (sessions: ParsedSession list) (creating: bool) =
  Elem.div [ Attr.id DomIds.SessionsPanel ] [
    match creating with
    | true ->
      Elem.div
        [ Attr.style "padding: 8px; text-align: center; color: var(--fg-blue); font-size: 0.85rem;" ]
        [ Text.raw "⏳ Creating session..." ]
    | false -> ()
    match sessions.IsEmpty && not creating with
    | true ->
      Elem.div [ Attr.class' "sessions-empty" ] [
        Text.raw "No active sessions"
        Elem.div [ Attr.class' "sessions-empty-hint" ] [ Text.raw "Start one with Quick Start, or open a directory." ]
      ]
    | false ->
      yield! sessions |> List.mapi (fun i (s: ParsedSession) ->
        let statusClass = SessionDisplayStatus.cssClass s.Status
        let sid = WorkerProtocol.SessionId.value s.Id
        let isViewing = sid = viewingSessionId
        let cls =
          match isViewing with
          | true -> "output-result session-selected"
          | false -> ""
        // A faulted or lost session is marked from its own status; agent
        // guidance (a contested session) is its own, separate class.
        let statusCls =
          match s.Status with
          | SessionDisplayStatus.Faulted _ -> " session-faulted"
          | SessionDisplayStatus.Lost -> " session-lost"
          | SessionDisplayStatus.Running
          | SessionDisplayStatus.Starting
          | SessionDisplayStatus.Restarting
          | SessionDisplayStatus.Stale
          | SessionDisplayStatus.Stopped -> ""
        let guidanceCls =
          match s.GuidanceCssClass.Length > 0 with
          | true -> sprintf "%s %s" statusCls s.GuidanceCssClass
          | false -> statusCls
        Elem.div
          [ Attr.id (sprintf "session-card-%s" sid)
            Attr.class' (sprintf "session-row %s%s" cls guidanceCls)
            Attr.style "padding: 10px 0; border-bottom: 1px solid var(--border-normal); cursor: pointer;"
            testid "session-card"
            Attr.create "data-session-id" sid
            Attr.create "aria-current" (match isViewing with | true -> "true" | false -> "false")
            // Switching is signal-driven: the POST carries this page's clientId
            // signal, so the backend retargets THIS tab's stream and patches
            // $viewingSessionId (GET /dashboard has no session parameter). One
            // guard here keeps clicks on the card's own buttons and links from
            // also switching — no child has to remember to stop propagation.
            match isViewing with
            | true -> ()
            | false ->
              Ds.onClick (sprintf "evt.target.closest('button, a') || %s" (Ds.post (sprintf "/dashboard/session/switch/%s" sid))) ]
          [
            Elem.div [ Attr.class' "session-card-body" ] [
              // Row 1: session ID + status + selected indicator
              Elem.div [ Attr.class' "session-card-status-row" ] [
                Elem.span [ Attr.style "font-weight: bold;" ] [ textEnc sid ]
                Elem.span
                  [ Attr.class' (sprintf "status badge %s" statusClass) ]
                  [ textEnc (SessionDisplayStatus.label s.Status) ]
                match isViewing with
                | true ->
                  Elem.span [ Attr.style "color: var(--fg-green);" ] [ Text.raw "● selected" ]
                | false -> ()
                // Agent presence badges (multi-agent coordination)
                yield! s.AgentBadges |> List.map (fun badge ->
                  Elem.span
                    [ Attr.class' badge.CssClass
                      Attr.title (attrEnc (
                        match badge.DetailLabel.Length > 0 with
                        | true -> sprintf "%s — files: %s" badge.Name badge.DetailLabel
                        | false -> badge.Name)) ]
                    [ Text.raw "🤖 "
                      textEnc (
                        match badge.IntentLabel.Length > 0 with
                        | true -> sprintf "%s (%s)" badge.Name badge.IntentLabel
                        | false -> badge.Name) ])
                match s.Uptime.Length > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta"; Attr.style "margin-left: auto;" ] [
                    textEnc (sprintf "⏱ %s" s.Uptime)
                  ]
                | false -> ()
              ]
              // Transient status message (warmup, discovery, etc.) on its own
              // line so it never cramps the badges row
              match s.StatusMessage with
              | Some msg ->
                Elem.div
                  [ Attr.class' "status-msg"
                    Attr.style "font-size: 0.7rem; color: var(--fg-yellow); font-style: italic;" ]
                  [ textEnc (sprintf "⏳ %s" msg) ]
              | None -> ()
              // Self-host staleness (F5b): this session adopted its own
              // SageFs.Core build and a newer one has since landed on disk —
              // its own line so it never cramps the badges, wrapping at any
              // width. The text already carries the ⚠ prefix + remediation.
              match s.SelfHostStaleness with
              | Some line ->
                Elem.div
                  [ Attr.class' "self-host-stale"
                    Attr.style "font-size: 0.7rem; color: var(--fg-yellow); font-weight: bold; overflow-wrap: anywhere; margin-top: 2px;" ]
                  [ textEnc line ]
              | None -> ()
              // Row 2: working directory
              match s.WorkingDir.Length > 0 with
              | true ->
                Elem.div
                  [ Attr.class' "session-dir"
                    Attr.style "font-size: 0.75rem; color: var(--fg-dim);"
                    Attr.title (attrEnc s.WorkingDir) ]
                  [ Text.raw "📁 "; textEnc s.WorkingDir ]
              | false -> ()
              // Row 3: projects as tags + evals + last activity
              Elem.div [ Attr.class' "flex-row"; Attr.style "gap: 0.5rem; flex-wrap: wrap;" ] [
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
                      [ textEnc pName ])
                | false -> ()
                match s.EvalCount > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta" ] [
                    textEnc (sprintf "evals: %d" s.EvalCount)
                  ]
                | false -> ()
                // Worker RSS (vision §3.4 "worker RSS" — the first of the
                // four numbers that decide cohort member count). Live from
                // the worker's own pid; absent while starting/faulted/stopped
                // or if the pid has already exited (never a fault).
                match s.WorkerRssBytes with
                | Some bytes ->
                  Elem.span [ Attr.class' "meta"; testid "session-rss" ] [
                    textEnc (sprintf "RSS: %dMB" (bytes / 1024L / 1024L))
                  ]
                | None -> ()
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
                    [ textEnc badge ]
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
                      [ Attr.style (sprintf "display:inline-block;width:48px;height:8px;border-radius:0;background:linear-gradient(to right,%s);" gradientStops)
                        Attr.title title ]
                      []
                    Elem.span
                      [ Attr.style "font-size:0.6rem;color:var(--fg-dim);" ]
                      [ textEnc pct ]
                  ]
                | _ -> ()
                match s.LastActivity.Length > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta"; Attr.style "margin-left: auto;" ] [
                    textEnc (sprintf "last: %s" s.LastActivity)
                  ]
                | false -> ()
                match s.App with
                | AppRun.AppRunState.NotRunning -> ()
                | app ->
                  let color =
                    match app with
                    | AppRun.AppRunState.Running _ -> "var(--fg-green)"
                    | AppRun.AppRunState.Crashed _ | AppRun.AppRunState.CouldNotStart _ | AppRun.AppRunState.BuildFailed _ | AppRun.AppRunState.LostTrack _ -> "var(--fg-red)"
                    | _ -> "var(--fg-dim)"
                  Elem.div
                    [ Attr.class' "session-card-app"
                      Attr.style (sprintf "flex-basis: 100%%; font-size: 0.7rem; color: %s; overflow-wrap: anywhere;" color) ]
                    [ textEnc (AppRun.describeState app) ]
              ]
            ]
            Elem.div [ Attr.class' "session-card-actions" ] [
              // Warmup auto-open state icon (color-coded on/off, tooltip explains, click toggles)
              (match s.WorkingDir.Length with
               | 0 -> renderAutoOpenToggleIcon true
               | _ -> renderAutoOpenToggleIcon (DirectoryConfig.autoOpenNamespacesForDirectory s.WorkingDir))
              match isViewing with
              | false ->
                Elem.button
                  [ Attr.class' "session-btn"
                    Attr.title "Switch — show this session's output here"
                    Ds.onClick (Ds.post (sprintf "/dashboard/session/switch/%s" sid)) ]
                  [ Text.raw "⇄" ]
              | true -> ()
              // Run App — for sessions that own an executable project. Every app
              // state has its own control: starting, running (with a link), and
              // how the last run ended in the run button's tooltip.
              let runnable =
                s.ProjectRoles |> List.filter (fun p -> p.Role = SageFs.ProjectLoading.ProjectRole.Executable)
              let runTitle (name: string) =
                match s.App with
                | AppRun.AppRunState.Exited _ | AppRun.AppRunState.Crashed _ | AppRun.AppRunState.CouldNotStart _ | AppRun.AppRunState.BuildFailed _ | AppRun.AppRunState.LostTrack _ ->
                  sprintf "Run %s with hot reload — last run: %s" name (AppRun.describeState s.App)
                | _ ->
                  sprintf "Run %s with hot reload (an Interactive session restarts into WebLive first, losing its REPL bindings)" name
              match runnable, s.App with
              | [], _ -> ()
              | _, AppRun.AppRunState.Running app ->
                match app.Endpoint with
                | AppRun.AppEndpoint.Http (url, _) ->
                  Elem.a
                    [ Attr.class' "session-btn session-btn-link"
                      Attr.href (attrEnc url)
                      Attr.target "_blank"
                      Attr.rel "noopener"
                      Attr.title (attrEnc (sprintf "Open %s — save a source file to hot reload it" url)) ]
                    [ Text.raw "🌐" ]
                | AppRun.AppEndpoint.NoServer -> ()
                Elem.button
                  [ Attr.class' "session-btn session-btn-success"
                    testid "stop-app"
                    Attr.title (attrEnc (sprintf "Stop App — %s" (AppRun.describeState s.App)))
                    Ds.onClick (Ds.post (sprintf "/dashboard/stop-app/%s" sid)) ]
                  [ Text.raw "■" ]
              | _, AppRun.AppRunState.Starting _ ->
                Elem.button
                  [ Attr.class' "session-btn"
                    Attr.disabled
                    Attr.title (attrEnc (AppRun.describeState s.App)) ]
                  [ Text.raw "⏳" ]
              | [ project ], _ ->
                Elem.button
                  [ Attr.class' "session-btn session-btn-primary"
                    testid "run-app"
                    Attr.title (attrEnc (runTitle (AppRun.projectName project.Path)))
                    Ds.onClick (Ds.post (sprintf "/dashboard/run-app/%s" sid)) ]
                  [ Text.raw "▶" ]
              | projects, _ ->
                // Several executables: each button must say which project it
                // runs, so it is a labeled pill (auto width, label truncates
                // with an ellipsis) — text never goes in the 28px icon box.
                for project in projects do
                  let name = AppRun.projectName project.Path
                  Elem.button
                    [ Attr.class' "session-btn session-btn-primary session-btn-labeled"
                      testid "run-app"
                      Attr.title (attrEnc (runTitle name))
                      Ds.onClick (Ds.post (sprintf "/dashboard/run-app/%s/%s" sid (Uri.EscapeDataString name))) ]
                    [ Elem.span [ Attr.create "aria-hidden" "true" ] [ Text.raw "▶" ]
                      Elem.span [ Attr.class' "session-btn-label" ] [ textEnc name ] ]
              Elem.button
                [ Attr.class' "session-btn session-btn-danger"
                  Attr.title "Stop — unload the session (saved memory kept)"
                  Ds.onClick (Ds.post (sprintf "/dashboard/session/stop/%s" sid)) ]
                [ Text.raw "■" ]
              Elem.button
                [ Attr.class' "session-btn session-btn-warn"
                  Attr.title "Dispose — stop the session (no separate saved-memory file remains; purge removes the manifest entry)"
                  Ds.onClick (Ds.post (sprintf "/dashboard/session/dispose/%s" sid)) ]
                [ Text.raw "⌫" ]
              Elem.button
                [ Attr.class' "session-btn session-btn-danger"
                  Attr.title "Purge — dispose and delete binaries + manifest entry (corrupt state)"
                  Ds.onClick (Ds.post (sprintf "/dashboard/session/purge/%s" sid)) ]
                [ Text.raw "✖" ]
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
              signalDetails
                (sprintf "testTreemapOpen_%s" sid)
                [ Attr.style "margin-top: 4px; font-size: 0.75rem;" ]
                [ Elem.summary
                    [ Attr.style "cursor:pointer;color:var(--fg-dim);user-select:none;" ]
                    [ Text.raw "🧪 "
                      textEnc (sprintf "%d tests · %s" s.TestTreemapEntries.Length durationLabel) ]
                  renderTestFilterBar s.TestTreemapEntries
                  renderTestTreemap s.TestTreemapEntries ]
            // Collapsible coverage treemap (WinDirStat/WizTree-style drill-down:
            // solution -> project -> file -> symbol, area = probe count)
            match s.CoverageTreemap with
            | None -> ()
            | Some root when root.ProbeCount = 0 -> ()
            | Some root ->
              let pct = coveragePercent root.ProbeCount root.CoveredCount
              signalDetails
                (sprintf "coverageTreemapOpen_%s" sid)
                [ Attr.style "margin-top: 4px; font-size: 0.75rem;" ]
                [ Elem.summary
                    [ Attr.style "cursor:pointer;color:var(--fg-dim);user-select:none;" ]
                    [ Text.raw "\U0001F5FA "
                      textEnc (sprintf "coverage map · %.0f%% (%d/%d probes)" pct root.CoveredCount root.ProbeCount) ]
                  renderCoverageTreemap sid s.CoverageTreemap ]
            // (Bound-values explorer removed from the session card — bindings
            // are not a per-card concern; they live in the Bindings panel.)
          ])
    // The action legend only makes sense when there are sessions to act on.
    match sessions.IsEmpty with
    | true -> ()
    | false ->
      Elem.div
        [ Attr.style "display: flex; justify-content: space-between; align-items: center; font-size: 0.7rem; color: var(--fg-dim); padding: 4px 0; margin-top: 4px;" ]
        [
          Elem.span [] [
            Text.raw "⇄ switch · ■ stop · ⌫ dispose · ✖ purge"
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
      signalDetails Signals.FilmstripOpen [] [
        Elem.summary [ Attr.style "cursor: pointer; font-size: 0.75rem; color: var(--fg-dim); user-select: none;" ] [
          textEnc (sprintf "⏱ %d evals  %s" entries.Length recentIcons)
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
              Elem.span [ Attr.class' "frame-index" ] [ textEnc (sprintf "#%d" e.Index) ]
              Elem.span [ Attr.class' "frame-icon" ] [ textEnc icon ]
              Elem.span [ Attr.class' "frame-label" ] [ textEnc e.Label ]
              Elem.span [ Attr.class' "frame-duration" ] [ textEnc (sprintf " %dms" e.DurationMs) ]
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
      let plural n = if n = 1 then "" else "s"
      let badgeNodes =
        match errorCount, warnCount with
        | e, 0 -> [ Text.raw "🔴 "; textEnc (sprintf "%d error%s" e (plural e)) ]
        | 0, w -> [ Text.raw "⚠️ "; textEnc (sprintf "%d warning%s" w (plural w)) ]
        | e, w ->
          [ Text.raw "🔴 "
            textEnc (sprintf "%d error%s · " e (plural e))
            Text.raw "⚠️ "
            textEnc (sprintf "%d warning%s" w (plural w)) ]
      signalDetails Signals.DiagnosticsOpen [] [
        Elem.summary [ Attr.style disclosureSummaryStyle ] [
          Elem.span [ Attr.class' "diag-count-badge"; Attr.style "font-weight: bold; margin-right: 0.5rem;" ] badgeNodes
        ]
        Elem.div [ Attr.style "margin-top: 0.25rem;" ] [
          yield! diags |> List.map (fun diag ->
            let icon = match diag.Severity with DiagError -> Text.raw "🔴" | DiagWarning -> Text.raw "⚠️"
            Elem.div [ Attr.class' (sprintf "diag %s" (DiagSeverity.toCssClass diag.Severity)) ] [
              Elem.span [ Attr.class' "diag-icon" ] [ icon ]
              if diag.Line > 0 || diag.Col > 0 then
                Elem.span [ Attr.class' "diag-loc" ] [ textEnc (sprintf " L%d:%d " diag.Line diag.Col) ]
              Elem.span [ Attr.class' "diag-msg" ] [ textEnc diag.Message ]
            ])
        ]
      ]
  ]

/// Statusline left block: session state + working directory.
/// Shared by the full-shell render and the switch/teardown SSE patches so the
/// two can never diverge (the patch once dropped the classes and the encoding).
let renderStatuslineLeft (_stateLabel: string) (workingDir: string) =
  // The status/readiness label lives in the top status tab — not repeated here.
  Elem.div [ Attr.id "statusline-left"; Attr.class' "statusline-left" ] [
    Elem.div [ Attr.id "statusline-file"; Attr.class' "statusline-file" ] [ textEnc workingDir ]
  ]

/// Render the full dynamic content of the dashboard as a single <div id="main">.
/// This is the ONLY thing pushed via SSE on every state change.
/// Implements "immediate mode HTML" — the server renders the complete page from
/// state, sends one morph, and Datastar diffs the DOM.
/// See: "The Tao of Datastar" — https://data-star.dev/essays/tao_of_datastar
let renderMainContent (snap: DashboardSnapshot) : XmlNode =
  let connectionNode =
    // Connected-clients count (browsers 🌐 · agents 🤖 · terminals 💻). Lives in
    // the Sessions panel header (not floating above the panel body).
    match snap.ConnectionLabel with
    | Some label ->
      Elem.div
        [ Attr.id DomIds.ConnectionCounts; Attr.class' "meta"
          Attr.style "font-size: 0.72rem;"
          Attr.title "Connected clients — 🌐 browsers · 🤖 agents · 💻 terminals" ]
        [ textEnc label ]
    | None ->
      Elem.div [ Attr.id DomIds.ConnectionCounts; Attr.class' "meta"; Attr.style "font-size: 0.72rem;" ] []
  Elem.div [ Attr.id DomIds.Main; Attr.create "data-viewing-session-id" (attrEnc snap.SessionId); Ds.class' ("expanded", sprintf "$%s" Signals.ExpandedDashboard) ] [
    // Theme CSS variables — morphed with every push so theme changes propagate
    snap.ThemeVars
    // App header — tabline style like sagetech.dev
    Elem.div [ Attr.class' "app-header" ] [
      // Brand tab — left side
      Elem.div [ Attr.class' "tabline-brand"; Attr.style "display:flex;align-items:center;gap:8px;padding:0 16px;height:100%;border-right:1px solid var(--border-normal);" ] [
        Elem.span [ Attr.style "font-weight:700;color:var(--fg-blue);font-size:14px;" ] [ Text.raw "🧙 SageFs" ]
      ]
      // Status tabs — center
      Elem.div [ Attr.class' "tabline-menu"; Attr.style "display:flex;align-items:center;height:100%;flex:1;min-width:0;" ] [
        Elem.div [ Attr.id DomIds.SessionStatus; Attr.class' "tabline-status"; Attr.style "display:flex;align-items:center;height:100%;padding:0 12px;border-right:1px solid var(--border-normal);font-size:12px;white-space:nowrap;flex-shrink:0;" ] [
          Elem.span [ Attr.class' (sprintf "status %s" (DashboardConnectionState.statusBadgeCssClass snap.ConnectionState)); Attr.style "border-radius:0;" ] [
            textEnc (DashboardConnectionState.statusBadgeLabel snap.SessionState snap.ConnectionState) ]
        ]
        Elem.div [ Attr.class' "tabline-info"; Attr.style "display:flex;align-items:center;height:100%;padding:0 12px;border-right:1px solid var(--border-normal);color:var(--fg-dim);font-size:12px;white-space:nowrap;" ] [
          textEnc (sprintf "Session: %s" snap.SessionId)
        ]
        Elem.div [ Attr.id DomIds.EvalStats; Attr.class' "tabline-info"; Attr.style "display:flex;align-items:center;height:100%;padding:0 12px;color:var(--fg-dim);font-size:12px;" ] [ renderEvalStats snap.EvalStats ]
      ]
      // Right side — expand toggle, theme picker
      Elem.div [ Attr.class' "tabline-right"; Attr.style "display:flex;align-items:center;height:100%;margin-left:auto;" ] [
        Elem.button
          [ Attr.class' "expand-toggle-btn"
            Ds.onEvent ("click", sprintf "$%s = !$%s" Signals.ExpandedDashboard Signals.ExpandedDashboard)
            Ds.text (sprintf "$%s ? '✕' : '⋯'" Signals.ExpandedDashboard)
            Attr.title "Toggle extra panels (Hot Reload, Live Testing, Bindings)" ]
          []
        Elem.a
          [ Attr.class' "expand-toggle-btn"
            Attr.href "/dashboard/settings"
            Attr.title "Runtime settings"
            Attr.style "text-decoration:none;display:inline-flex;align-items:center;justify-content:center;" ]
          [ Text.raw "⚙" ]
        snap.ThemePicker
      ]
    ]
    // Daemon health bar — version, uptime, memory, session health
    snap.DaemonHealth
    // Eval-to-pixel latency — the one unique perf stat — as a slim, dim line
    // under the health bar, shown ONLY once the first eval has completed the
    // chain, so there is never an empty band when idle. The working dir is not
    // repeated here; it lives on the session's sidebar card.
    match snap.EvalToPixelP50Ms, snap.EvalToPixelP99Ms with
    | Some p50, Some p99 ->
      Elem.div [ Attr.class' "session-context" ] [
        Elem.span [ Attr.class' "session-context-latency"; testid "eval-to-pixel-latency" ] [
          textEnc (sprintf "px p50 %.1fms p99 %.1fms" p50 p99)
        ]
      ]
    | _ -> ()
    // Expanded-only panels: alarm, failure narratives, diagnostics.
    // The eval filmstrip ("N evals" per-cell history) was removed — it pushed
    // the whole UI down to show raw eval code+timing that the top eval-stats
    // and the Output panel already cover, and cost an O(n) rev per SSE push.
    Elem.div [ Attr.class' "expanded-only" ] [
      snap.AlarmPanel
      snap.FailureNarrativesPanel
      snap.DiagnosticsPanel
    ]
    // Main app layout: output+eval on left, sidebar on right
    Elem.div [ Attr.class' "app-layout" ] [
      Elem.div [ Attr.class' "main-area" ] [
        // Session picker — shown when no sessions exist, hidden otherwise
        snap.SessionPicker
        Elem.div [ Attr.id DomIds.EditorArea ] [
          Elem.div [ Attr.id DomIds.OutputSection; Attr.class' "output-area" ] [
            Elem.div [ Attr.class' "output-header" ] [
              Elem.div [ Attr.style "display:flex;align-items:center;gap:16px;" ] [
                Elem.h2 [] [ Text.raw "Output" ]
              ]
              Elem.button
                [ Attr.class' "panel-header-btn"
                  Ds.onClick (Ds.post "/dashboard/clear-output") ]
                [ Text.raw "[CLEAR]" ]
            ]
            snap.OutputPanel
          ]
          // Eval area — collapsed by default via <details>
          signalDetails Signals.EvaluateSectionOpen [ Attr.id DomIds.EvaluateSection; Attr.class' "eval-area" ] [
            Elem.create "summary" [ Attr.class' "flex-between"; Attr.style "cursor: pointer;" ] [
              Elem.span [ Attr.style "color: var(--fg-blue); font-weight: bold; font-size: 0.85rem;" ] [ Text.raw "▸ Evaluate" ]
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
            Elem.div [ Attr.style "position: relative;" ] [
              Elem.textarea
                [ Attr.class' "eval-input"
                  Attr.id DomIds.EvalTextarea
                  Ds.bind Signals.Code
                  Attr.create "placeholder" "Enter F# code... (Alt+Enter to eval, ;; auto-appended)"
                  // NOTE: Datastar evaluates data-on expressions with `this`
                  // unbound — the element must be reached via event.target,
                  // never `this`. (A `this.` reference throws "Cannot read
                  // properties of undefined (reading 'substring')" at runtime.)
                  Ds.onEvent ("keydown", "var t=event.target; if(event.altKey && event.key === 'Enter') { event.preventDefault(); @post('/dashboard/eval') } if(event.ctrlKey && event.key === 'l') { event.preventDefault(); @post('/dashboard/clear-output') } if(event.key === 'Tab') { event.preventDefault(); var s=t.selectionStart; var e=t.selectionEnd; t.value=t.value.substring(0,s)+'  '+t.value.substring(e); t.selectionStart=t.selectionEnd=s+2; t.dispatchEvent(new Event('input')) } if(event.key === 'Escape') { document.getElementById('completion-dropdown').style.display='none' }")
                  Ds.onEvent ("input.debounce_300ms", sprintf "var t=event.target; var c=t.value[t.selectionStart-1]; $%s = t.selectionStart; if(c==='.'||(c>='a'&&c<='z')||(c>='A'&&c<='Z')){@post('/dashboard/completions')}" Signals.CursorPos)
                  Attr.create "spellcheck" "false" ]
                []
              Elem.div
                [ Attr.id DomIds.CompletionDropdown
                  Attr.style "display:none; position:absolute; bottom:100%; left:0; max-height:200px; overflow-y:auto; background:var(--bg-default); border:1px solid var(--bg-selection); border-radius:0; z-index:100; min-width:200px; font-size:0.85em; box-shadow:0 -2px 8px rgba(0,0,0,0.3);" ]
                []
            ]
            Elem.div
              [ Attr.class' "eval-controls" ]
              [
                // Eval / reset / hard-reset share one in-flight signal so every
                // action button is disabled while ANY of them is running — a
                // click can never double-fire a destructive reset behind an eval.
                Elem.button
                  [ Attr.class' "eval-btn"
                    testid "eval"
                    Ds.indicator Signals.ActionLoading
                    Ds.attr' ("disabled", "$actionLoading")
                    Ds.onClick (Ds.post "/dashboard/eval") ]
                  [ Elem.span [ Ds.show "$actionLoading" ] [ Text.raw "⏳ " ]
                    Elem.span [ Ds.show "!$actionLoading" ] [ Text.raw "▶ " ]
                    Text.raw "[EVAL]" ]
                Elem.button
                  [ Attr.class' "eval-btn eval-btn-reset"
                    testid "reset"
                    Ds.indicator Signals.ActionLoading
                    Ds.attr' ("disabled", "$actionLoading")
                    Ds.onClick (Ds.post "/dashboard/reset") ]
                  [ Elem.span [ Ds.show "$actionLoading" ] [ Text.raw "⏳ " ]
                    Elem.span [ Ds.show "!$actionLoading" ] [ Text.raw "↻ " ]
                    Text.raw "[RESET]" ]
                Elem.button
                  [ Attr.class' "eval-btn eval-btn-reset eval-btn-hard"
                    testid "hard-reset"
                    Ds.indicator Signals.ActionLoading
                    Ds.attr' ("disabled", "$actionLoading")
                    Ds.onClick (Ds.post "/dashboard/hard-reset") ]
                  [ Elem.span [ Ds.show "$actionLoading" ] [ Text.raw "⏳ " ]
                    Elem.span [ Ds.show "!$actionLoading" ] [ Text.raw "✖ " ]
                    Text.raw "[HARD_RESET]" ]
                Elem.label
                  [ Attr.class' "eval-btn"
                    Attr.style "background: var(--fg-blue); cursor: pointer; display: inline-flex; align-items: center; gap: 2px; height: 2rem; padding: 0 0.75rem;" ]
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
        // Sidebar header — title + dynamic collapse/expand toggle (always visible)
        Elem.div [ Attr.class' "sidebar-header" ] [
          Elem.h2 [] [ Text.raw "Sessions" ]
          Elem.span [ Attr.style "margin-left:auto;margin-right:8px;" ] [ connectionNode ]
          Elem.button
            [ Attr.class' "sidebar-header-btn"
              Attr.id "sidebar-toggle-btn"
              Ds.onEvent ("click", "$sidebarOpen = !$sidebarOpen")
              Ds.text "$sidebarOpen ? '✕' : '☰'"
              Attr.title "$sidebarOpen ? 'Collapse panel' : 'Expand panel'" ]
            []
        ]
        Elem.div [ Attr.class' "sidebar-inner" ] [
          // Sessions panel (with context + bindings inline per row)
          Elem.div [ Attr.class' "panel" ] [
            snap.SessionsPanel
          ]
          // Dynamic sidebar panels — expanded-only (hot reload, live testing, bindings, session context)
          Elem.div [ Attr.class' "expanded-only" ] [
            snap.HotReloadPanel
            snap.LiveTestingPanel
            snap.BindingsPanel
            snap.SessionContextPanel
            snap.FrictionPanel
            snap.CohortPanel
          ]
          signalDetails
            Signals.NewSessionOpen
            [ Attr.class' "panel new-session-panel" ]
            [
              Elem.summary
                [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.9rem; user-select: none; color: var(--fg-blue);" ]
                [ Text.raw "➕ New Session" ]
              Elem.div [ Attr.style "margin-top: 0.5rem;" ] [
                Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px;" ] [
                  Text.raw "Working Directory"
                ]
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
                ]
                Elem.div [ Attr.id DomIds.DiscoveredProjects ] []
                Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px; margin-top: 0.5rem;" ] [
                  Text.raw "Projects (comma-sep)"
                ]
                Elem.input
                  [ Attr.class' "eval-input"
                    Attr.style "min-height: auto; height: 2rem;"
                    Ds.bind Signals.ManualProjects
                    Attr.create "placeholder" "MyProject.fsproj" ]
                Elem.button
                  [ Attr.class' "eval-btn"
                    Attr.style "margin-top: 0.5rem; width: 100%; font-size: 0.8rem; display: inline-flex; align-items: center; justify-content: center; gap: 2px; height: 2rem;"
                    testid "new-session"
                    Ds.indicator Signals.CreateLoading
                    Ds.attr' ("disabled", "$createLoading")
                    Ds.onClick (Ds.post "/dashboard/session/create") ]
                  [ Elem.span [ Ds.show "$createLoading" ] [ Text.raw "⏳ Creating... " ]
                    Elem.span [ Ds.show "!$createLoading" ] [ Text.raw "➕ Create" ] ]
              ]
            ]
        ]
      ]
    ]
  ]


/// Decides whether a theme push is needed after a state change.
/// Returns Some themeName if push needed, None otherwise.
/// Pure function — no side effects — for testability.
/// The themes dictionary is expected to use canonical keys (see
/// `canonicalizeThemeKey` in DashboardTypes.fs); the lookup key here
/// is canonicalized before the dictionary read so different string
/// forms of the same directory collapse to one entry.
let resolveThemePush
  (themes: System.Collections.Generic.IDictionary<string, string>)
  (currentSessionId: string)
  (currentWorkingDir: string)
  (previousSessionId: string)
  (previousWorkingDir: string)
  (lastThemeName: string)
  : string option =
  let sessionChanged =
    currentSessionId.Length > 0 && currentSessionId <> previousSessionId
  let workingDirChanged =
    currentWorkingDir.Length > 0 && currentWorkingDir <> previousWorkingDir
  // Look up the server-side theme for the current working dir
  let serverTheme =
    match currentWorkingDir.Length > 0 with
    | true ->
      let key = canonicalizeThemeKey currentWorkingDir
      match key.Length > 0 with
      | true ->
        match themes.TryGetValue(key) with
        | true, n -> Some n
        | false, _ -> None
      | false -> None
    | false -> None
  match sessionChanged || workingDirChanged with
  | true ->
    // Session changed — use the server-side theme or fall back to default
    match serverTheme with
    | Some n -> Some n
    | None -> Some defaultThemeName
  | false ->
    // Session didn't change — only push if the server-side theme
    // differs from what was last pushed (handles set-theme without
    // a session switch)
    match serverTheme with
    | Some n when n <> lastThemeName -> Some n
    | _ -> None

/// Render the hot-reload panel with a file list grouped by directory.
///
/// Every control here is wired through the Datastar `Ds.post`/`Ds.onClick`
/// action builders — never a hand-rolled `onclick` + `fetch()` string (the
/// "some random xml nonsense" the maintainer explicitly banned). A shared
/// per-control-class `Ds.indicator` signal drives immediate ⏳ feedback,
/// mirroring `Signals.ActionLoading` (the eval-actions row's single
/// in-flight signal). The `/api/sessions/{sid}/hotreload/*` endpoints and
/// their JSON body shapes (`{}`, `{"path":...}`, `{"directory":...}`) are
/// unchanged: the per-item value is staged into a plain signal named after
/// the exact field the worker reads (`path`/`directory`) immediately
/// before `@post` fires, so `@post` still ships it — Datastar's actions
/// always send the current signals store as the request body.
let renderHotReloadPanel (sessionId: string) (files: {| path: string; watched: bool |} list) (watchedCount: int) =
  let total = List.length files
  let hotReloadWatchAllLoading = "hotReloadWatchAllLoading"
  let hotReloadUnwatchAllLoading = "hotReloadUnwatchAllLoading"
  let hotReloadDirLoading = "hotReloadDirLoading"
  let hotReloadFileLoading = "hotReloadFileLoading"
  let hotReloadEndpoint (action: string) =
    sprintf "/api/sessions/%s/hotreload/%s" (Uri.EscapeDataString sessionId) action
  // `assign` stages a value into the signal the worker's JSON body reader
  // expects (e.g. `$path = '...'; `), or "" for the no-body bulk actions.
  // The whole expression is attribute-encoded: a `"` in a path must break
  // out of neither the JS string (jsStringLiteral) nor the HTML attribute.
  let hotReloadClick (assign: string) (action: string) =
    attrEnc (assign + Ds.post (hotReloadEndpoint action))
  let indicatorAttrs (signal: string) =
    [ Ds.indicator signal; Ds.attr' ("disabled", sprintf "$%s" signal) ]
  let loadingSpan (signal: string) =
    Elem.span [ Ds.show (sprintf "$%s" signal) ] [ Text.raw "⏳ " ]
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
      | n -> textEnc (sprintf "Hot Reload: ON — %d of %d files" n total)
    ]
    Elem.div [ Attr.class' "meta"; Attr.style "margin-bottom: 0.5rem; font-size: 0.8rem;" ] [
      textEnc (sprintf "%d of %d files watched" watchedCount total)
    ]
    Elem.div [ Attr.style "display: flex; gap: 4px; margin-bottom: 0.5rem;" ] [
      Elem.button
        ([ Attr.class' "eval-btn"
           Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;" ]
         @ indicatorAttrs hotReloadWatchAllLoading
         @ [ Ds.onClick (hotReloadClick "" "watch-all") ])
        [ loadingSpan hotReloadWatchAllLoading; Text.raw "Watch All" ]
      Elem.button
        ([ Attr.class' "eval-btn"
           Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;" ]
         @ indicatorAttrs hotReloadUnwatchAllLoading
         @ [ Ds.onClick (hotReloadClick "" "unwatch-all") ])
        [ loadingSpan hotReloadUnwatchAllLoading; Text.raw "Unwatch All" ]
    ]
    signalDetails Signals.HotReloadFilesOpen [] [
      Elem.summary [ Attr.style "cursor: pointer; font-size: 0.75rem; color: var(--fg-dim); user-select: none;" ] [
        Text.raw "📁 "
        textEnc (sprintf "%d files" total)
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
          let dirClick = hotReloadClick (sprintf "$directory = %s; " (jsStringLiteral dir)) dirAction
          [
            Elem.div
              [ Attr.style "font-weight: 600; margin-top: 4px; opacity: 0.8; font-size: 0.7rem; cursor: pointer; display: flex; align-items: center; gap: 4px;"
                Ds.indicator hotReloadDirLoading
                Ds.onClick dirClick ]
              [ loadingSpan hotReloadDirLoading
                Elem.span [ Attr.style (sprintf "color: %s;" dirColor); Ds.show (sprintf "!$%s" hotReloadDirLoading) ] [ textEnc dirIcon ]
                Text.raw "📁 "
                textEnc (sprintf "%s (%d/%d)" dirLabel dirWatchedCount (List.length dirFiles)) ]
            yield! dirFiles |> List.map (fun f ->
              let fileName =
                let n = f.path.Replace('\\', '/')
                match n.LastIndexOf('/') with
                | -1 -> n
                | idx -> n.[idx + 1..]
              let icon = match f.watched with | true -> "●" | false -> "○"
              let color = match f.watched with | true -> "var(--fg-blue, #7aa2f7)" | false -> "var(--fg-dim, #565f89)"
              let fileClick = hotReloadClick (sprintf "$path = %s; " (jsStringLiteral f.path)) "toggle"
              Elem.div
                [ Attr.style "cursor: pointer; padding: 1px 4px; display: flex; align-items: center; gap: 4px;"
                  Ds.indicator hotReloadFileLoading
                  Ds.onClick fileClick ]
                [ loadingSpan hotReloadFileLoading
                  Elem.span [ Attr.style (sprintf "color: %s; font-size: 0.8rem;" color); Ds.show (sprintf "!$%s" hotReloadFileLoading) ] [ textEnc icon ]
                  Elem.span [ Attr.style (match f.watched with | true -> "opacity: 1" | false -> "opacity: 0.6") ] [ textEnc fileName ] ]
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

/// Render the friction review panel (Phase 5 dashboard journey).
///
/// Privacy model: this panel shows ONLY the user's LOCAL friction telemetry
/// (the SQLite store). It renders a sanitized preview of what a send would
/// contain, editable reason fields bound to Datastar signals, an endpoint +
/// optional token, and the local send history. The send handler is
/// server-authoritative — the client never assembles the payload.
let renderFrictionPanel (snap: SageFs.Features.FrictionReviewView.FrictionReviewSnapshot) =
  signalDetails Signals.FrictionPanelOpen [ Attr.id DomIds.FrictionPanel; Attr.class' "panel"; Attr.style "margin-top: 0.5rem;" ] [
    Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.85rem; user-select: none; color: var(--fg-blue);" ] [
      Elem.span [] [
        Text.raw "🧾 "
        textEnc (sprintf "Friction (%d events, %d feedback)" snap.EventCount snap.FeedbackCount) ]
    ]
    match snap.IsEmpty with
    | true ->
      Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.8rem; margin-top: 0.4rem;" ] [
        Text.raw "No local friction recorded yet. It appears here as SageFs tools are used."
      ]
    | false ->
      Elem.div [ Attr.style "margin-top: 0.5rem; display: flex; flex-direction: column; gap: 0.4rem;" ] [
        Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.75rem;" ] [
          Text.raw "Report is sanitized locally before send. You can edit the reasons below."
        ]
        // Top tools with friction — a compact preview of the outbound summary
        (match snap.Outgoing.ToolsWithFriction with
         | [] -> Elem.div [] []
         | tools ->
           Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.1em; font-size: 0.72rem; color: var(--fg-dim);" ] [
             for t in tools |> List.truncate 5 do
               Elem.li [] [
                 textEnc (sprintf "%s — %d calls, %d blocked, %d abandoned, %d feedback"
                   t.Tool t.Invocations t.Blocked t.Abandoned t.ExplicitFeedback)
               ]
           ])
        // Editable feedback reasons — one textarea per (tool, kind). Each is
        // a .friction-edit field carrying its data-tool/data-kind; the send
        // button assembles them into the frictionEdits signal JSON.
        (match snap.Outgoing.RecentFeedback with
         | [] -> Elem.div [] []
         | feedback ->
           Elem.div [ Attr.style "display: flex; flex-direction: column; gap: 0.3rem;" ] [
             for f in feedback do
               Elem.label [ Attr.class' "meta"; Attr.style "font-size: 0.7rem; display: block;" ] [
                 textEnc (sprintf "%s (%s)" f.Tool f.Kind)
               ]
               Elem.textarea
                 [ Attr.class' "eval-input friction-edit"
                   Attr.create "data-tool" (attrEnc f.Tool)
                   Attr.create "data-kind" (attrEnc f.Kind)
                   Attr.style "min-height: 3rem; height: auto; font-size: 0.75rem;" ]
                  [ textEnc f.Reason ]
           ])
        // Endpoint + optional token, bound to signals.
        Elem.label [ Attr.class' "meta"; Attr.style "font-size: 0.7rem; display: block;" ] [
          Text.raw "Report endpoint (https, or http to localhost)"
        ]
        Elem.input
          [ Attr.class' "eval-input"
            Attr.style "min-height: auto; height: 2rem; font-size: 0.75rem;"
            Ds.bind Signals.FrictionEndpoint
            Attr.create "placeholder" "https://your-worker.example.workers.dev/" ]
        Elem.label [ Attr.class' "meta"; Attr.style "font-size: 0.7rem; display: block; margin-top: 0.3rem;" ] [
          Text.raw "Ingest token (optional)"
        ]
        Elem.input
          [ Attr.class' "eval-input"
            Attr.style "min-height: auto; height: 2rem; font-size: 0.75rem;"
            Ds.bind Signals.FrictionToken
            Attr.create "placeholder" "token if your receiver requires one" ]
        // Send button + result status. The click handler assembles the
        // per-reason edits into frictionEdits before POSTing.
        Elem.div [ Attr.style "display: flex; gap: 0.4rem; align-items: center; margin-top: 0.4rem;" ] [
          Elem.button
            [ Attr.class' "eval-btn"
              Attr.style "font-size: 0.75rem; height: 2rem; padding: 0 0.6rem;"
              Ds.indicator Signals.FrictionSending
              Ds.attr' ("disabled", "$frictionSending")
              Ds.onEvent ("click", "var edits={};document.querySelectorAll('.friction-edit').forEach(function(ta){if(ta.dataset.tool&&ta.dataset.kind)edits[ta.dataset.tool+'|'+ta.dataset.kind]=ta.value});var h=document.getElementById('friction-edits-json');h.value=JSON.stringify(edits);h.dispatchEvent(new Event('input',{bubbles:true}));@post('/dashboard/friction/send')") ]
            [ Elem.span [ Ds.show "$frictionSending" ] [ Text.raw "⏳ " ]
              Text.raw "Send Report" ]
        ]
        Elem.input
          [ Attr.id "friction-edits-json"
            Attr.type' "hidden"
            Ds.bind Signals.FrictionEdits ]
        Elem.div [ Attr.id DomIds.FrictionSendStatus ] []
        // Local send history.
        (match snap.SentReports with
         | [] -> Elem.div [] []
         | history ->
           signalDetails Signals.FrictionHistoryOpen [ Attr.style "margin-top: 0.4rem;" ] [
             Elem.summary [ Attr.class' "meta"; Attr.style "font-size: 0.72rem; cursor: pointer;" ] [
               textEnc (sprintf "Sent reports (%d)" history.Length)
             ]
             Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.1em; font-size: 0.7rem; color: var(--fg-dim);" ] [
               for s in history |> List.truncate 10 do
                 Elem.li [] [
                   textEnc (sprintf "%s — %s (%d events)" s.ReportId (s.SentAtUtc.ToLocalTime().ToString("g")) s.TotalEvents)
                 ]
             ]
           ])
      ]
  ]


// ── Cohort panel (cohort-integration-plan.md Slice 4) ──────────────────────
// Renders `Cohort.CohortFrame<MemberId>` — the daemon's single implicit
// cohort, read wait-free via `DashboardInfra.ReadCohortFrame` (D4, no new
// SSE channel, no mailbox round-trip). Daemon-scoped, so this panel is built
// identically whether or not a session is currently being viewed.

let private cohortRoleLabel (role: SageFs.Cohort.JoinableRole) : string =
  match role with
  | SageFs.Cohort.JoinableRole.Implementer -> "Implementer"
  | SageFs.Cohort.JoinableRole.Verifier -> "Verifier"
  | SageFs.Cohort.JoinableRole.Observer -> "Observer"

let private cohortSeatLabel (seat: SageFs.Cohort.SeatState) : string =
  match seat with
  | SageFs.Cohort.SeatState.Present -> "present"
  | SageFs.Cohort.SeatState.Departed since ->
    sprintf "departed %s" (since.ToLocalTime().ToString("HH:mm:ss"))

let private cohortScopeLabel (scope: SageFs.Cohort.ClaimScope) : string =
  match scope with
  | SageFs.Cohort.ClaimScope.File path -> sprintf "file:%s" path
  | SageFs.Cohort.ClaimScope.Project path -> sprintf "project:%s" path

/// Describes a claim NOT currently `Held` (`ClaimHolderIndex.[i] < 0`) — the
/// held case is rendered separately from `MemberIds.[ClaimHolderIndex.[i]]`
/// (§ CohortFrame doc: the holder lives in the index array, never re-derived
/// from this DU while held).
let private cohortClaimStateLabel (state: SageFs.Cohort.ClaimState<MemberTable.MemberId>) : string =
  match state with
  | SageFs.Cohort.ClaimState.Held holder ->
    sprintf "held by %s" (MemberTable.MemberId.display holder)
  | SageFs.Cohort.ClaimState.Orphaned(previousHolder, since) ->
    sprintf "orphaned — was %s, since %s" (MemberTable.MemberId.display previousHolder) (since.ToLocalTime().ToString("HH:mm:ss"))
  | SageFs.Cohort.ClaimState.Released(by, at) ->
    sprintf "released by %s at %s" (MemberTable.MemberId.display by) (at.ToLocalTime().ToString("HH:mm:ss"))

/// Pure render of one cohort frame. `frame`'s arrays are index-aligned
/// (`CohortFrame` doc, Cohort.fs) — every lookup here is a plain array index,
/// never a `Map` walk.
let rec renderCohortPanel (frame: SageFs.Cohort.CohortFrame<MemberTable.MemberId>) : XmlNode =
  let memberCount = frame.MemberIds.Length
  let claimCount = frame.ClaimIds.Length
  signalDetails Signals.CohortPanelOpen [ Attr.id DomIds.CohortPanel; Attr.class' "panel"; Attr.style "margin-top: 0.5rem;" ] [
    Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.85rem; user-select: none; color: var(--fg-blue);" ] [
      Text.raw "👥 "
      textEnc (sprintf "Cohort — %d member%s" memberCount (if memberCount = 1 then "" else "s"))
    ]
    match memberCount with
    | 0 ->
      Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.8rem; margin-top: 0.4rem;" ] [
        Text.raw "No cohort members — agents join via join_cohort."
      ]
    | _ ->
      Elem.div [ Attr.style "margin-top: 0.5rem; display: flex; flex-direction: column; gap: 0.5rem;" ] [
        Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.72rem;" ] [
          textEnc (sprintf "Ledger v%d" (int64 frame.Version))
        ]
        Elem.div [] [
          Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.72rem; margin-bottom: 0.2rem;" ] [
            Text.raw "Members"
          ]
          Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.1em; font-size: 0.75rem; display: flex; flex-direction: column; gap: 0.25rem;" ] [
            for i in 0 .. memberCount - 1 do
              Elem.li [ Attr.style "display: flex; align-items: baseline; gap: 0.4rem; flex-wrap: wrap; overflow-wrap: anywhere;" ] [
                textEnc (MemberTable.MemberId.display frame.MemberIds.[i])
                Elem.span [ Attr.class' "badge"; Attr.style "background: var(--bg-focus); color: var(--fg-dim);" ] [
                  textEnc (cohortRoleLabel frame.MemberRole.[i])
                ]
                Elem.span [ Attr.class' "meta"; Attr.style "font-size: 0.7rem;" ] [
                  textEnc (cohortSeatLabel frame.MemberSeat.[i])
                ]
              ]
          ]
        ]
        match claimCount with
        | 0 ->
          Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.75rem;" ] [
            Text.raw "No claims held."
          ]
        | _ ->
          Elem.div [] [
            Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.72rem; margin-bottom: 0.2rem;" ] [
              textEnc (sprintf "Claims (%d)" claimCount)
            ]
            Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.1em; font-size: 0.75rem; display: flex; flex-direction: column; gap: 0.3rem;" ] [
              for i in 0 .. claimCount - 1 do
                let (SageFs.Cohort.ClaimId claimIdStr) = frame.ClaimIds.[i]
                let holderIdx = frame.ClaimHolderIndex.[i]
                let holderText =
                  match holderIdx >= 0 && holderIdx < memberCount with
                  | true -> sprintf "held by %s" (MemberTable.MemberId.display frame.MemberIds.[holderIdx])
                  | false -> cohortClaimStateLabel frame.ClaimState.[i]
                Elem.li [ Attr.style "overflow-wrap: anywhere;" ] [
                  Elem.div [] [
                    textEnc claimIdStr
                    Text.raw " — "
                    textEnc (cohortScopeLabel frame.ClaimScope.[i])
                  ]
                  Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.7rem;" ] [
                    textEnc (sprintf "%s · fence %d" holderText (int64 frame.ClaimFence.[i]))
                  ]
                ]
            ]
          ]
        renderCohortMatrix frame
        renderCohortTerritory frame
      ]
  ]

/// §6.5 "Matrix view — a picture, with a text fallback": the cohort test
/// matrix rendered from the frame's own bitplanes
/// (`CohortMatrixRender.toPng`/`toCharGrid`), inlined as a `data:image/png`
/// `<img>` per §6.5 (no served route, no extra HTTP round trip) with the
/// fixed-pitch character grid behind a toggle as the accessible fallback.
/// `frame.TestIds.Length = 0` (no test outcomes projected onto any session
/// yet) renders nothing, matching the empty-state convention the rest of
/// this panel already uses for zero members/zero claims.
and private renderCohortMatrix (frame: SageFs.Cohort.CohortFrame<MemberTable.MemberId>) : XmlNode =
  match frame.TestIds.Length with
  | 0 -> Elem.div [] []
  | testCount ->
    let rowCount = frame.Pass.Length
    let dataUri = Features.CohortMatrixRender.toPngDataUri frame.Pass frame.Fail frame.Stale
    let charGrid = Features.CohortMatrixRender.toCharGrid frame.Pass frame.Fail frame.Stale
    Elem.div [ Attr.id DomIds.CohortMatrix; Attr.style "margin-top: 0.2rem;" ] [
      Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.72rem; margin-bottom: 0.2rem;" ] [
        textEnc (sprintf "Test matrix (%d tests x %d row%s)" testCount rowCount (if rowCount = 1 then "" else "s"))
      ]
      Elem.create "img" [
        Attr.create "src" dataUri
        Attr.create "alt" "Cohort test matrix — pass green, fail red, stale amber, not-run grey"
        Attr.style "image-rendering: pixelated; width: 100%; max-width: 420px; display: block; border: 1px solid var(--border, #444);"
      ] []
      signalDetails Signals.CohortMatrixTextOpen [ Attr.style "margin-top: 0.3rem;" ] [
        Elem.summary [ Attr.style "cursor: pointer; font-size: 0.7rem; color: var(--fg-dim); user-select: none;" ] [
          Text.raw "Text fallback"
        ]
        Elem.pre [ Attr.style "font-size: 0.62rem; line-height: 1.15; overflow-x: auto; margin: 0.3rem 0 0 0; white-space: pre;" ] [
          textEnc charGrid
        ]
      ]
    ]

/// §6.5 "Territory map" (Phase 2 item 16), scoped to what `CohortFrame`
/// actually carries — see `CohortTerritory`'s module doc (`Features/CohortTerritory.fs`)
/// for why this is claims-as-territory rather than the vision's full
/// compile-list-with-coverage-health map (that data isn't on `CohortFrame`
/// yet). Rendered as an inline SVG via `Text.raw`, the same no-served-route,
/// no-new-stream pattern `renderCohortMatrix` uses for its PNG data URI —
/// deterministic (`CohortTerritory.toSvg`) so it composes with the same
/// `SnapshotRenderGuard` the matrix relies on. A frame with no Held/Orphaned
/// claims anywhere renders nothing, matching this panel's other empty-state
/// conventions (zero members, zero claims, zero test outcomes).
and private renderCohortTerritory (frame: SageFs.Cohort.CohortFrame<MemberTable.MemberId>) : XmlNode =
  let tiles = Features.CohortTerritory.ofFrame frame
  match tiles with
  | [] -> Elem.div [] []
  | _ ->
    let svg = Features.CohortTerritory.toSvg 320.0 200.0 tiles
    let legend =
      tiles
      |> List.map (fun t ->
        let holderText =
          match t.HolderIndex >= 0 && t.HolderIndex < frame.MemberIds.Length with
          | true -> MemberTable.MemberId.display frame.MemberIds.[t.HolderIndex]
          | false -> "unclaimed"
        sprintf "%s — %s" t.Label holderText)
      |> String.concat "\n"
    Elem.div [ Attr.id DomIds.CohortTerritory; Attr.style "margin-top: 0.4rem;" ] [
      Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.72rem; margin-bottom: 0.2rem;" ] [
        textEnc (sprintf "Territory map (%d claimed path%s)" tiles.Length (if tiles.Length = 1 then "" else "s"))
      ]
      Elem.div [ Attr.style "max-width: 420px;" ] [ Text.raw svg ]
      signalDetails Signals.CohortTerritoryTextOpen [ Attr.style "margin-top: 0.3rem;" ] [
        Elem.summary [ Attr.style "cursor: pointer; font-size: 0.7rem; color: var(--fg-dim); user-select: none;" ] [
          Text.raw "Text fallback"
        ]
        Elem.pre [ Attr.style "font-size: 0.68rem; line-height: 1.3; overflow-x: auto; margin: 0.3rem 0 0 0; white-space: pre-wrap;" ] [
          textEnc legend
        ]
      ]
    ]

let private cohortSpanOutcomeLabel (outcome: Features.CohortLanes.SpanOutcome) : string =
  match outcome with
  | Features.CohortLanes.SpanOutcome.Open -> "open"
  | Features.CohortLanes.SpanOutcome.Succeeded -> "succeeded"
  | Features.CohortLanes.SpanOutcome.Failed -> "failed"

let private cohortLaneLabel (member_: MemberTable.MemberId option) : string =
  match member_ with
  | Some m -> MemberTable.MemberId.display m
  | None -> "Integration"

/// §6.5 "the lane view from `LaneEvents` with span flames from
/// `CohortFrame.Spans`" (Phase 2 item 16), scoped to what the shipped core
/// actually carries — see `CohortLanes.fs`'s module doc for why this reads
/// the ledger (`DashboardInfra.ReadCohortLedger`) rather than a
/// `CohortFrame.Spans` field that does not exist yet.
///
/// Deliberately a STANDALONE panel, not folded into `renderCohortPanel`'s
/// own `<details>`: `renderCohortPanel : CohortFrame<MemberId> -> XmlNode`
/// is called directly (one argument, a bare frame) by
/// `CohortPanelTests.fs`/`CohortTerritoryPanelTests.fs` — two files that
/// explicitly stay out of each other's way (see `CohortTerritoryPanelTests.fs`'s
/// own module doc) because another agent may be mid-edit on one of them.
/// Widening `renderCohortPanel`'s signature to also take the ledger would
/// force a mechanical edit into both, on every call site, for no reason a
/// merge should have to resolve. `Dashboard.fs` instead composes this
/// panel's output as a sibling of `renderCohortPanel`'s, so neither that
/// function's signature nor its existing test call sites change at all.
///
/// Deterministic (`CohortLanes.toSvg`), same `SnapshotRenderGuard`
/// composition as the matrix/territory islands. A ledger with no claim or
/// landing activity on any lane renders nothing, matching this panel
/// family's other empty-state conventions.
let renderCohortLanesPanel (ledgerEntries: SageFs.Cohort.LedgerEntry<MemberTable.MemberId> list) : XmlNode =
  let model = Features.CohortLanes.project ledgerEntries
  let hasActivity = model.Lanes |> List.exists (fun lane -> not lane.Spans.IsEmpty)
  match hasActivity with
  | false -> Elem.div [] []
  | true ->
    let laneCount = model.Lanes.Length
    let rowHeight = 16.0
    let lanesSvg = Features.CohortLanes.toSvg cohortLaneLabel 320.0 rowHeight model
    let legend =
      model.Lanes
      |> List.collect (fun lane ->
        lane.Spans
        |> List.map (fun s -> sprintf "%s — %s (%s)" (cohortLaneLabel lane.Member) s.Label (cohortSpanOutcomeLabel s.Outcome)))
      |> String.concat "\n"
    signalDetails Signals.CohortLanesPanelOpen [ Attr.id DomIds.CohortLanes; Attr.class' "panel"; Attr.style "margin-top: 0.4rem;" ] [
      Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.85rem; user-select: none; color: var(--fg-blue);" ] [
        Text.raw "🔥 "
        textEnc (sprintf "Lanes — %d lane%s" laneCount (if laneCount = 1 then "" else "s"))
      ]
      Elem.div [ Attr.style "max-width: 420px; margin-top: 0.4rem;" ] [ Text.raw lanesSvg ]
      signalDetails Signals.CohortLanesTextOpen [ Attr.style "margin-top: 0.3rem;" ] [
        Elem.summary [ Attr.style "cursor: pointer; font-size: 0.7rem; color: var(--fg-dim); user-select: none;" ] [
          Text.raw "Text fallback"
        ]
        Elem.pre [ Attr.style "font-size: 0.68rem; line-height: 1.3; overflow-x: auto; margin: 0.3rem 0 0 0; white-space: pre-wrap;" ] [
          textEnc legend
        ]
      ]
    ]

/// §6.5's time-scrubber (Phase 2 item 16): "the scrubber over a CohortFrame
/// SnapshotRing with per-tab Viewing and `f` = `fork_cohort` at the viewed
/// seq". See `CohortScrubber.fs`'s module doc for why no `SnapshotRing`/
/// `fork_cohort` needed building — scrubbing replays a shorter ledger
/// prefix through the SAME `Cohort.project` the live view uses, and
/// forking is honestly left unbuilt (`Cohort.fs` has no fork
/// command/event). `viewingSeq = None` renders the LIVE affordance (drag
/// the slider back from `latest` to start scrubbing); `Some seq` renders
/// the VIEWING affordance plus a "back to live" button. `latestSeq = None`
/// (an empty ledger — nothing has happened yet) renders nothing, matching
/// this panel family's other empty-state conventions.
///
/// The range input's `change` event reads the slider's value directly off
/// `event.target` (the same `var t=event.target.value; @post(...)` idiom
/// `renderThemePicker`'s onchange handler uses) and posts it as
/// `Signals.CohortViewingSeq` — Datastar's `@post` sends the WHOLE current
/// signal store alongside it, so `Dashboard.fs`'s handler still reads
/// `Signals.ClientId` off the same request body without this control
/// needing to know about client ids at all.
let renderCohortScrubControl (viewingSeq: int64<ledgerSeq> option) (latestSeq: int64<ledgerSeq> option) : XmlNode =
  match latestSeq with
  | None -> Elem.div [] []
  | Some latest ->
    let latestInt = int64 latest
    let currentInt = viewingSeq |> Option.map int64 |> Option.defaultValue latestInt
    let scrubEndpoint = "/dashboard/cohort/scrub"
    Elem.div [ Attr.id DomIds.CohortScrubber; Attr.style "margin-top: 0.3rem; padding-bottom: 0.3rem; border-bottom: 1px solid var(--border, #444); display: flex; flex-direction: column; gap: 0.3rem;" ] [
      Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.72rem; display: flex; align-items: center; gap: 0.4rem; flex-wrap: wrap;" ] [
        Text.raw "🕐 "
        match viewingSeq with
        | None -> Elem.span [ Attr.style "color: var(--fg-green, #27ae60);" ] [ textEnc (sprintf "LIVE · v%d" latestInt) ]
        | Some v -> Elem.span [ Attr.style "color: var(--fg-amber, orange);" ] [ textEnc (sprintf "Viewing v%d of v%d" (int64 v) latestInt) ]
        Elem.create "input" [
          Attr.create "type" "range"
          Attr.create "min" "0"
          Attr.create "max" (string latestInt)
          Attr.create "value" (string currentInt)
          Attr.style "flex: 1 1 100px; min-width: 60px;"
          Ds.onEvent ("change", sprintf "var v=event.target.value; @post('%s', {%s: v})" scrubEndpoint Signals.CohortViewingSeq)
        ] []
        match viewingSeq with
        | Some _ ->
          Elem.button [
            Attr.class' "session-btn"
            Attr.create "title" "Back to live"
            Ds.onEvent ("click", sprintf "@post('%s', {%s: ''})" scrubEndpoint Signals.CohortViewingSeq)
          ] [ Text.raw "⏭" ]
        | None -> Elem.div [] []
        Elem.button [
          Attr.create "disabled" "disabled"
          Attr.create "title" "Forking a cohort from a scrubbed seq is not supported yet — Cohort.fs has no fork command/event to send (see CohortScrubber.fs's module doc)."
          Attr.style "font-size: 0.68rem; opacity: 0.5; cursor: not-allowed;"
        ] [ Text.raw "⑂ fork (not yet supported)" ]
      ]
    ]


module private LiveTestActivityView =
  type Activity = Features.LiveTestActivity.LiveTestActivity

  /// Passed in green, failed in red, so one failure is not lost in a green count.
  let counts (tally: Features.LiveTestActivity.TestTally) : XmlNode list =
    [ tally.Passed, "var(--fg-green, #27ae60)", "✓"
      tally.Failed, "var(--fg-red, #e74c3c)", "✗" ]
    |> List.filter (fun (n, _, _) -> n > 0)
    |> List.map (fun (n, color, glyph) ->
      Elem.span [ Attr.style (sprintf "color: %s; margin-right: 0.25rem;" color) ] [ textEnc (sprintf "%d%s" n glyph) ])

  let header (activity: Activity) : XmlNode list =
    match activity with
    | Activity.Off -> [ Text.raw "Live Testing: OFF" ]
    | Activity.Running tally
    | Activity.Settled tally
    | Activity.Rebuilding (_, tally)
    | Activity.BlockedByCompileErrors (_, _, tally)
    | Activity.BlockedByFailedRebuild (_, tally) ->
      // One span for the counts: the header spreads its children apart, so
      // separate spans would scatter the passed and failed counts.
      match counts tally with
      | [] -> [ Text.raw "Live Testing: ON" ]
      | shown -> [ Text.raw "Live Testing: ON"; Elem.span [] shown ]
    | Activity.Discovering
    | Activity.DiscoveryFailed _
    | Activity.NoTestsFound _ -> [ Text.raw "Live Testing: ON" ]

  /// Problems read red, work in progress blue, so the line says at a glance whether to act.
  let tone (activity: Activity) =
    match activity with
    | Activity.DiscoveryFailed _
    | Activity.BlockedByCompileErrors _
    | Activity.BlockedByFailedRebuild _ -> "color: var(--fg-red, #e74c3c);"
    | Activity.Settled tally when tally.Failed > 0 -> "color: var(--fg-red, #e74c3c);"
    | Activity.Discovering
    | Activity.Rebuilding _
    | Activity.Running _ -> "color: var(--fg-blue);"
    | Activity.Off
    | Activity.NoTestsFound _
    | Activity.Settled _ -> ""

  let words (activity: Activity) =
    match activity with
    | Activity.Off -> "Enable to run tests on every keystroke and file save."
    | other -> Features.LiveTestActivity.LiveTestActivity.describe other

/// Render the live testing panel from the session's one activity state: the ON/OFF
/// header with counts, a toggle that shows it is in flight, and the activity in words.
let renderLiveTestingPanel (activity: Features.LiveTestActivity.LiveTestActivity) =
  let endpoint, label, pending =
    match activity with
    | Features.LiveTestActivity.LiveTestActivity.Off -> "/dashboard/live-testing/enable", "Enable", "Turning on…"
    | _ -> "/dashboard/live-testing/disable", "Disable", "Turning off…"
  Elem.div [ Attr.id DomIds.LiveTestingPanel; Attr.class' "panel" ] [
    Elem.h2 [] (LiveTestActivityView.header activity)
    Elem.div [ Attr.style "display: flex; gap: 4px; margin-bottom: 0.5rem;" ] [
      Elem.button
        [ Attr.class' "eval-btn"
          Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;"
          testid "live-testing-toggle"
          Ds.indicator Signals.LiveTestingLoading
          Ds.attr' ("disabled", "$liveTestingLoading")
          Ds.onClick (Ds.post endpoint) ]
        [ Elem.span [ Ds.show "$liveTestingLoading" ] [ textEnc ("⏳ " + pending) ]
          Elem.span [ Ds.show "!$liveTestingLoading" ] [ textEnc label ] ]
    ]
    // Reasons carry compiler output, which contains markup characters.
    Elem.div [ Attr.class' "meta"; Attr.style ("font-size: 0.8rem; " + LiveTestActivityView.tone activity) ] [
      textEnc (LiveTestActivityView.words activity)
    ]
  ]

/// Render session context panel with warmup details (assemblies, namespaces, files).
/// Uses HTML <details>/<summary> so it's collapsed by default.
let renderSessionContextPanel (ctx: SessionContext) =
  let summaryText = SessionContext.summary ctx

  let assembliesSection =
    signalDetails Signals.SessionContextAssembliesOpen [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        Text.raw "📦 "
        textEnc (sprintf "Assemblies (%d)" (ctx.Warmup.AssembliesLoaded |> List.length))
      ]
      Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.2em; font-size: 0.7rem;" ] [
        for asm in ctx.Warmup.AssembliesLoaded do
          Elem.li [] [ textEnc (SessionContext.assemblyLine asm) ]
      ]
    ]

  let namespacesSection =
    let opened = ctx.Warmup.NamespacesOpened
    let failed = ctx.Warmup.FailedOpens
    signalDetails Signals.SessionContextNamespacesOpen [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        Text.raw "📂 "
        textEnc (sprintf "Namespaces (%d opened, %d failed)"
          (opened |> List.length) (failed |> List.length))
      ]
      Elem.div [ Attr.style "font-size: 0.7rem;" ] [
        Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.2em;" ] [
          for b in opened do
            Elem.li [] [
              Elem.code [] [ textEnc (SessionContext.openLine b) ]
              match b.DurationMs > 0.0 with
              | true ->
                Elem.span [ Attr.style "color: var(--fg-dim); margin-left: 0.5em;" ] [
                  textEnc (sprintf "(%.1fms)" b.DurationMs)
                ]
              | false -> ()
            ]
        ]
        match List.isEmpty failed with
        | false ->
          signalDetails Signals.SessionContextFailedOpensOpen [ Attr.style "margin-top: 0.5em;" ] [
            Elem.summary [ Attr.style "color: var(--fg-red); cursor: pointer; font-weight: bold;" ] [
              textEnc (sprintf "⚠️ %d Failed Opens (expanded)" failed.Length)
            ]
            Elem.div [ Attr.style "padding-left: 0.5em;" ] [
              for f in failed do
                Elem.div [ Attr.class' "diag-error-block" ] [
                  Elem.div [ Attr.style "font-weight: bold; color: var(--fg-red);" ] [
                    let kind = OpenableKind.label f.Kind
                    textEnc (sprintf "✖ %s (%s)" f.Name kind)
                    match f.RetryCount > 1 with
                    | true ->
                      Elem.span [ Attr.style "color: var(--fg-dim); font-weight: normal; margin-left: 0.5em;" ] [
                        textEnc (sprintf "(%d retries)" f.RetryCount)
                      ]
                    | false -> ()
                  ]
                  Elem.div [ Attr.style "color: var(--fg-red); margin-top: 0.2em;" ] [
                    textEnc f.ErrorMessage
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
                            textEnc (sprintf "FS%04d" d.ErrorNumber)
                          ]
                          match d.FileName with
                          | Some fn ->
                            Elem.span [ Attr.style "margin-left: 0.4em; color: var(--fg-dim);" ] [
                              textEnc (sprintf "%s:%d:%d" fn d.StartLine d.StartColumn)
                            ]
                          | None -> ()
                          Elem.span [ Attr.style "margin-left: 0.4em;" ] [
                            textEnc d.Message
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
    signalDetails Signals.SessionContextTimingOpen [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        textEnc (sprintf "⏱️ Warmup Timing (%dms total)" t.TotalMs)
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
              Elem.span [ Attr.style "min-width: 120px;" ] [ textEnc label ]
              Elem.div [ Attr.class' "progress-track" ] [
                Elem.div [ Attr.style (sprintf "width: %.1f%%; height: 100%%; background: var(--fg-blue); border-radius: 0;" pct) ] []
              ]
              Elem.span [ Attr.style "min-width: 50px; text-align: right; color: var(--fg-dim);" ] [
                textEnc (sprintf "%dms" ms)
              ]
            ]
          ]
      ]
    ]

  let filesSection =
    signalDetails Signals.SessionContextFilesOpen [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        let loadedCount =
          ctx.FileStatuses
          |> List.filter (fun f -> f.Readiness = Loaded)
          |> List.length
        Text.raw "📄 "
        textEnc (sprintf "Files (%d/%d loaded)" loadedCount (ctx.FileStatuses |> List.length))
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
            textEnc (SessionContext.fileLine f)
          ]
      ]
    ]

  Elem.div [ Attr.id DomIds.SessionContext; Attr.class' "panel" ] [
    signalDetails Signals.SessionContextOpen [] [
      Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.8rem;" ] [
        Text.raw "🔍 "
        textEnc (sprintf "Session Context: %s" summaryText)
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
    // Shares Signals.BindingsPanelOpen with renderLiveBindingsPanel — the two
    // are mutually exclusive renders of the same DOM id, so one open state.
    signalDetails Signals.BindingsPanelOpen [] [
      Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.9rem; user-select: none;" ] [
        Text.raw "📦 "
        textEnc (sprintf "Bindings (%d)" activeCount)
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
                  textEnc b.Name
                ]
                Elem.span [ Attr.style "color: var(--fg-dim, #666); font-size: 0.7rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;" ] [
                  textEnc b.TypeSig
                ]
                Elem.span [ Attr.style "color: var(--fg-dim, #555); font-size: 0.65rem; white-space: nowrap;" ] [
                  textEnc (sprintf "cell %d" b.CellIndex)
                ]
                match b.ReferencedIn.Length with
                | 0 -> ()
                | n ->
                  Elem.span [ Attr.style "color: var(--fg-yellow, #e5c07b); font-size: 0.65rem; white-space: nowrap;" ] [
                    textEnc (sprintf "→%d" n)
                  ]
                match b.Value with
                | None -> ()
                | Some v ->
                  Elem.span [ Attr.class' "value-display"; Attr.style "color: var(--fg-green, #98c379); font-size: 0.7rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 20em;" ] [
                    textEnc (sprintf "= %s" v)
                  ]
              ]
          ]
          match shadowedCount with
          | 0 -> ()
          | _ ->
            signalDetails Signals.ShadowedBindingsOpen [ Attr.style "margin-top: 0.5em;" ] [
              Elem.summary [ Attr.style "font-size: 0.7rem; cursor: pointer; color: var(--fg-dim, #666);" ] [
                Text.raw "👻 "
                textEnc (sprintf "%d shadowed" shadowedCount)
              ]
              Elem.div [ Attr.style "font-size: 0.7rem; opacity: 0.6;" ] [
                for b in scope.ShadowedBindings do
                  Elem.div [ Attr.style "padding: 1px 0;" ] [
                    Elem.code [] [ textEnc b.Name ]
                    Elem.span [ Attr.style "color: var(--fg-dim, #555); margin-left: 0.3em;" ] [
                      textEnc (sprintf ": %s (cell %d)" b.TypeSig b.CellIndex)
                    ]
                  ]
              ]
            ]
    ]
  ]

/// Render the live bindings watch window — a recursive, fully-delineated tree
/// of the actual bound values in the FSI session (debugger-style).
/// Each binding shows name : type + compact preview, with expandable children.
let renderLiveBindingsPanel (snapshot: SageFs.Features.LiveValueTree.LiveValueSnapshot option) =
  let kindBadge (node: SageFs.Features.LiveValueTree.LiveValueNode) =
    match node.Kind with
    | SageFs.Features.LiveValueTree.NodeKind.Closure ->
      [ Elem.span [ Attr.class' "live-closure-badge"; Attr.title "Best-effort expansion of captured values" ] [ Text.raw "~(best-effort)" ] ]
    | SageFs.Features.LiveValueTree.NodeKind.Cycle ->
      [ Elem.span [ Attr.class' "live-cycle" ] [ Text.raw "↩ (cycle)" ] ]
    | SageFs.Features.LiveValueTree.NodeKind.Truncated ->
      [ Elem.span [ Attr.class' "live-truncated" ] [ Text.raw "… (truncated)" ] ]
    | _ -> []
  let rec renderNode (node: SageFs.Features.LiveValueTree.LiveValueNode) =
    let hasChildren = not (List.isEmpty node.Children)
    // Text.create escapes — labels, type names, and previews are FSI-derived
    // DATA, not trusted markup. A string preview containing <script> (e.g. a
    // bound value holding HTML) must never inject into the dashboard DOM.
    let row =
      Elem.div [ Attr.class' "live-binding-node"; Attr.style (sprintf "padding-left: %dem;" (node.Depth)) ] [
        Elem.code [ Attr.style "color: var(--fg-cyan, #56b6c2); font-weight: bold; white-space: nowrap;" ] [ textEnc node.Label ]
        Elem.span [ Attr.style "color: var(--fg-dim, #666); font-size: 0.7rem; margin-left: 0.4em;" ] [ textEnc node.TypeName ]
        Elem.span [ Attr.class' "live-preview"; Attr.style "color: var(--fg-green, #98c379); font-size: 0.7rem; margin-left: 0.4em; overflow-wrap: anywhere;" ] [
          textEnc (sprintf "= %s" node.Preview)
        ]
        yield! kindBadge node
      ]
    match hasChildren with
    | false -> row
    | true ->
      // Per-node signal for open/closed state — survives Datastar morphs.
      // The label is FSI-derived data spliced into a Datastar expression inside
      // an attribute, so reduce it to identifier characters only.
      let nodeSignal = sprintf "open_%s_%d" (signalIdent (node.Label.Replace("(", "").Replace(")", ""))) node.Depth
      signalDetails nodeSignal [ Attr.style "font-size: 0.75rem;" ] [
        Elem.summary [ Attr.style "cursor: pointer; user-select: none; list-style: none;" ] [ row ]
        Elem.div [ Attr.style "margin-top: 2px;" ] [
          yield! node.Children |> List.map renderNode
        ]
      ]
  let count = snapshot |> Option.map (fun s -> s.Bindings.Length) |> Option.defaultValue 0
  let gen = snapshot |> Option.map (fun s -> s.Generation) |> Option.defaultValue 0L
  let captured =
    snapshot
    |> Option.map (fun s -> s.CapturedAt.ToLocalTime().ToString("HH:mm:ss"))
    |> Option.defaultValue ""
  Elem.div [ Attr.id DomIds.BindingsPanel; Attr.class' "panel" ] [
    signalDetails Signals.BindingsPanelOpen [] [
      Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.9rem; user-select: none;" ] [
        Text.raw "🔴 "
        textEnc (sprintf "Live Bindings (%d)" count)
        match snapshot with
        | Some _ ->
          Elem.span [ Attr.style "color: var(--fg-dim, #666); font-weight: normal; font-size: 0.65rem; margin-left: 0.5em;" ] [
            textEnc (sprintf "gen %d · %s" gen captured)
          ]
        | None -> ()
      ]
      match snapshot with
      | None ->
        Elem.div [ Attr.class' "meta" ] [ Text.raw "No live bindings yet — evaluate some code" ]
      | Some s ->
        match s.Bindings with
        | [] ->
          Elem.div [ Attr.class' "meta" ] [ Text.raw "No active bindings" ]
        | bindings ->
          Elem.div [ Attr.style "font-size: 0.75rem; max-height: 24em; overflow-y: auto;" ] [
            yield! bindings |> List.map (fun b ->
              Elem.div [ Attr.style "border-bottom: 1px solid var(--border, #333); padding: 2px 0;" ] [
                renderNode b.Root
              ])
          ]
          match s.Truncated with
          | true ->
            Elem.div [ Attr.class' "live-truncated"; Attr.style "font-size: 0.65rem; margin-top: 4px;" ] [
              Text.raw "Some values truncated (depth/children limits)"
            ]
          | false -> ()
    ]
  ]

/// Create the SSE stream handler that pushes Elm state to the browser.

let private renderDiscoveredProjectsBody (discovered: DiscoveredProjects) = [
  match discovered.Solutions.IsEmpty && discovered.Projects.IsEmpty with
  | true ->
    Elem.div [ Attr.class' "output-line output-error" ] [
      textEnc (sprintf "No .sln/.fsproj found in %s" discovered.WorkingDir)
    ]
  | false ->
    Elem.div [ Attr.class' "output-line output-result" ] [
      textEnc (sprintf "Found in %s:" discovered.WorkingDir)
    ]
    match discovered.Solutions.IsEmpty with
    | false ->
      yield! discovered.Solutions |> List.map (fun s ->
        Elem.div [ Attr.class' "output-line output-info"; Attr.style "padding-left: 1rem;" ] [
          Text.raw "📁 "
          textEnc (sprintf "%s (solution)" s)
        ])
    | true -> ()
    yield! discovered.Projects |> List.map (fun p ->
      Elem.div [ Attr.class' "output-line"; Attr.style "padding-left: 1rem;" ] [
        Text.raw "📄 "
        textEnc p
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
            textEnc (sprintf "⚙️ .SageFs/config.fsx: solution %s" path)
          ]
        | Projects paths ->
          Elem.div [ Attr.class' "output-line output-info"; Attr.style "margin-bottom: 4px;" ] [
            textEnc (sprintf "⚙️ .SageFs/config.fsx: %s" (String.Join(", ", paths)))
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
      // msg is user/agent-derived (directory names, eval output); encoded
      // here exactly once — callers pass the raw string.
      textEnc msg
    ]
  ]


