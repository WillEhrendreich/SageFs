module SageFs.Server.Dashboard

open System
open System.IO
open Falco
open Falco.Markup
open Falco.Routing
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open System.Text.RegularExpressions
open SageFs
open SageFs.Affordances

module FalcoResponse = Falco.Response

/// Discover .fsproj and .sln/.slnx files in a directory.
type DiscoveredProjects = {
  WorkingDir: string
  Solutions: string list
  Projects: string list
}

let discoverProjects (workingDir: string) : DiscoveredProjects =
  let projects =
    try
      Directory.EnumerateFiles(workingDir, "*.fsproj", SearchOption.AllDirectories)
      |> Seq.map (fun p -> Path.GetRelativePath(workingDir, p))
      |> Seq.toList
    with _ -> []
  let solutions =
    try
      Directory.EnumerateFiles(workingDir)
      |> Seq.filter (fun f ->
        let ext = Path.GetExtension(f).ToLowerInvariant()
        ext = ".sln" || ext = ".slnx")
      |> Seq.map Path.GetFileName
      |> Seq.toList
    with _ -> []
  { WorkingDir = workingDir; Solutions = solutions; Projects = projects }

/// Render keyboard shortcut help as an HTML fragment.
let renderKeyboardHelp () =
  let shortcut key desc =
    Elem.tr [] [
      Elem.td [ Attr.style "padding: 2px 8px; font-family: monospace; color: var(--accent);" ] [ Text.raw key ]
      Elem.td [ Attr.style "padding: 2px 8px;" ] [ Text.raw desc ]
    ]
  Elem.div [ Attr.id "keyboard-help"; Attr.style "margin-top: 0.5rem;" ] [
    Elem.table [ Attr.style "font-size: 0.85rem; border-collapse: collapse;" ] [
      shortcut "Ctrl+Enter" "Evaluate code"
      shortcut "Tab" "Insert 2 spaces (in editor)"
      shortcut "Ctrl+L" "Clear output"
    ]
  ]

/// Render the dashboard HTML shell.
/// Datastar initializes and connects to the /dashboard/stream SSE endpoint.
let private renderShell (version: string) =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ Text.raw "SageFs Dashboard" ]
      Ds.cdnScript
      Elem.style [] [ Text.raw """
        :root { --bg: #0d1117; --fg: #c9d1d9; --fg-dim: #8b949e; --accent: #58a6ff; --green: #3fb950; --red: #f85149; --border: #30363d; --surface: #161b22; --bg-highlight: #21262d; --yellow: #d29922; }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: 'Cascadia Code', 'Fira Code', monospace; background: var(--bg); color: var(--fg); padding: 1rem; }
        h1 { color: var(--accent); margin-bottom: 1rem; font-size: 1.4rem; }
        .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
        .panel { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 1rem; }
        .panel h2 { color: var(--accent); font-size: 1rem; margin-bottom: 0.5rem; border-bottom: 1px solid var(--border); padding-bottom: 0.5rem; display: flex; justify-content: space-between; align-items: center; }
        .status { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; font-weight: bold; }
        .status-ready { background: var(--green); color: var(--bg); }
        .status-warming { background: var(--yellow); color: var(--bg); }
        .status-faulted { background: var(--red); color: white; }
        .output-line { font-size: 0.85rem; padding: 2px 0; border-bottom: 1px solid var(--border); white-space: pre-wrap; word-break: break-all; }
        .output-result { color: var(--green); }
        .output-error { color: var(--red); }
        .output-info { color: var(--accent); }
        .output-system { color: #8b949e; }
        .diag { font-size: 0.85rem; padding: 4px 0; }
        .diag-error { color: var(--red); }
        .diag-warning { color: var(--yellow); }
        .meta { color: #8b949e; font-size: 0.8rem; }
        .eval-input { width: 100%; background: var(--bg); color: var(--fg); border: 1px solid var(--border); border-radius: 4px; padding: 0.5rem; font-family: inherit; font-size: 0.9rem; resize: vertical; min-height: 80px; tab-size: 2; }
        .eval-input:focus { outline: 1px solid var(--accent); border-color: var(--accent); }
        .eval-btn { background: var(--accent); color: var(--bg); border: none; border-radius: 4px; padding: 0.5rem 1rem; cursor: pointer; font-family: inherit; font-weight: bold; margin-top: 0.5rem; transition: opacity 0.15s; }
        .eval-btn:hover { opacity: 0.85; }
        .eval-btn:active { opacity: 0.7; }
        .session-btn { background: var(--border); color: var(--fg); border: none; border-radius: 4px; padding: 2px 8px; cursor: pointer; font-size: 0.8rem; transition: background 0.15s; }
        .session-btn:hover { background: var(--accent); color: var(--bg); }
        .session-btn-danger:hover { background: var(--red); color: white; }
        .full-width { grid-column: 1 / -1; }
        .panel-header-btn { background: none; border: 1px solid var(--border); color: var(--fg); border-radius: 4px; padding: 1px 8px; cursor: pointer; font-size: 0.75rem; font-family: inherit; }
        .panel-header-btn:hover { background: var(--border); }
        .output-icon { font-weight: bold; margin-right: 0.25rem; }
        .auto-scroll { scroll-behavior: smooth; }
        .conn-banner { padding: 6px 1rem; text-align: center; font-size: 0.85rem; font-weight: bold; border-radius: 4px; margin-bottom: 1rem; transition: all 0.3s; }
        .conn-connected { background: var(--green); color: var(--bg); }
        .conn-disconnected { background: var(--red); color: white; animation: pulse 1.5s infinite; }
        .conn-reconnecting { background: var(--yellow); color: var(--bg); }
        @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.6; } }
        .eval-btn:disabled { opacity: 0.5; cursor: not-allowed; }
        @media (max-width: 768px) {
          .grid { grid-template-columns: 1fr; }
          body { padding: 0.5rem; }
          h1 { font-size: 1.1rem; }
        }
      """ ]
    ]
    Elem.body [ Ds.safariStreamingFix ] [
      // Dedicated init element that connects to SSE stream (per Falco.Datastar pattern)
      Elem.div [ Ds.onInit (Ds.get "/dashboard/stream") ] []
      // Connection status banner — pushed by SSE as "Connected", falls back to "Disconnected" via MutationObserver timeout
      Elem.div [ Attr.id "server-status"; Attr.class' "conn-banner conn-disconnected" ] [
        Text.raw "⏳ Connecting to server..."
      ]
      Elem.h1 [] [ Text.raw (sprintf "🧙 SageFs Dashboard v%s" version) ]
      Elem.div [ Attr.class' "grid" ] [
        // Row 1: Session status + Eval stats
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Session" ]
          Elem.div [ Attr.id "session-status" ] [
            Text.raw "Connecting..."
          ]
          Elem.div [ Attr.id "connection-counts"; Attr.class' "meta"; Attr.style "font-size: 0.75rem; margin-top: 4px;" ] []
        ]
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Eval Stats" ]
          Elem.div [ Attr.id "eval-stats" ] [
            Text.raw "Waiting for data..."
          ]
        ]
        // Row 2: Evaluate (full width)
        Elem.div [ Attr.class' "panel full-width" ] [
          Elem.h2 [] [
            Text.raw "Evaluate"
            Elem.button
              [ Attr.class' "panel-header-btn"
                Attr.create "data-on-click" "var h=document.getElementById('keyboard-help-wrapper'); h.style.display = h.style.display === 'none' ? 'block' : 'none'" ]
              [ Text.raw "⌨ Help" ]
          ]
          Elem.div [ Attr.id "keyboard-help-wrapper"; Attr.style "display: none;" ] [
            renderKeyboardHelp ()
          ]
          Elem.textarea
            [ Attr.class' "eval-input"
              Ds.bind "code"
              Attr.create "placeholder" "Enter F# code... (Ctrl+Enter to eval)"
              Attr.create "data-on-keydown" """
                if(event.ctrlKey && event.key === 'Enter') { event.preventDefault(); $$post('/dashboard/eval') }
                if(event.ctrlKey && event.key === 'l') { event.preventDefault(); $$post('/dashboard/clear-output') }
                if(event.key === 'Tab') { event.preventDefault(); var s=this.selectionStart; var e=this.selectionEnd; this.value=this.value.substring(0,s)+'  '+this.value.substring(e); this.selectionStart=this.selectionEnd=s+2; this.dispatchEvent(new Event('input')) }
              """
              Attr.create "spellcheck" "false" ]
            []
          Elem.div [ Attr.style "display: flex; gap: 0.5rem; margin-top: 0.5rem; align-items: center;" ] [
            Elem.button
              [ Attr.class' "eval-btn"
                Ds.indicator "evalLoading"
                Ds.attr' ("disabled", "$evalLoading")
                Ds.onClick (Ds.post "/dashboard/eval") ]
              [ Elem.span [ Ds.show "$evalLoading" ] [ Text.raw "⏳ " ]
                Elem.span [ Ds.show "!$evalLoading" ] [ Text.raw "▶ " ]
                Text.raw "Eval" ]
            Elem.button
              [ Attr.class' "eval-btn"
                Attr.style "background: var(--green);"
                Ds.onClick (Ds.post "/dashboard/reset") ]
              [ Text.raw "↻ Reset" ]
            Elem.button
              [ Attr.class' "eval-btn"
                Attr.style "background: var(--red);"
                Ds.onClick (Ds.post "/dashboard/hard-reset") ]
              [ Text.raw "⟳ Hard Reset" ]
          ]
          Elem.div [ Attr.class' "meta"; Attr.style "margin-top: 0.25rem; font-size: 0.75rem;" ] [
            Elem.span [ Ds.text """$code ? ($code.split('\\n').length + ' lines, ' + $code.length + ' chars') : '0 lines'""" ] []
          ]
          Elem.div [ Attr.id "eval-result" ] []
        ]
        // Row 3: Output (full width)
        Elem.div [ Attr.class' "panel full-width" ] [
          Elem.h2 [] [
            Text.raw "Output"
            Elem.button
              [ Attr.class' "panel-header-btn"
                Ds.onClick (Ds.post "/dashboard/clear-output") ]
              [ Text.raw "Clear" ]
          ]
          Elem.div
            [ Attr.id "output-panel"
              Attr.class' "auto-scroll"
              Attr.style "max-height: 400px; overflow-y: auto;" ]
            [ Text.raw "No output yet" ]
        ]
        // Row 4: Sessions + Diagnostics
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Sessions" ]
          Elem.div [ Attr.id "sessions-panel"; Attr.style "max-height: 300px; overflow-y: auto;" ] [
            Text.raw "No sessions"
          ]
        ]
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Diagnostics" ]
          Elem.div [ Attr.id "diagnostics-panel"; Attr.style "max-height: 300px; overflow-y: auto;" ] [
            Text.raw "No diagnostics"
          ]
        ]
        // Row 5: Create Session (full width)
        Elem.div [ Attr.class' "panel full-width" ] [
          Elem.h2 [] [ Text.raw "Create Session" ]
          Elem.div [ Attr.style "display: flex; gap: 0.5rem; align-items: flex-end;" ] [
            Elem.div [ Attr.style "flex: 1;" ] [
              Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px;" ] [
                Text.raw "Working Directory"
              ]
              Elem.input
                [ Attr.class' "eval-input"
                  Attr.style "min-height: auto; height: 2rem;"
                  Ds.bind "newSessionDir"
                  Attr.create "placeholder" @"C:\path\to\project" ]
            ]
            Elem.button
              [ Attr.class' "eval-btn"
                Attr.style "height: 2rem; padding: 0 1rem;"
                Ds.onClick (Ds.post "/dashboard/discover-projects") ]
              [ Text.raw "🔍 Discover" ]
          ]
          Elem.div [ Attr.id "discovered-projects" ] []
          Elem.div [ Attr.style "margin-top: 0.5rem;" ] [
            Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px;" ] [
              Text.raw "Or enter project paths (comma-separated)"
            ]
            Elem.input
              [ Attr.class' "eval-input"
                Attr.style "min-height: auto; height: 2rem;"
                Ds.bind "manualProjects"
                Attr.create "placeholder" "MyProject.fsproj, OtherProject.fsproj" ]
          ]
          Elem.button
            [ Attr.class' "eval-btn"
              Attr.style "margin-top: 0.5rem;"
              Ds.onClick (Ds.post "/dashboard/session/create") ]
            [ Text.raw "➕ Create Session" ]
        ]
      ]
      // Auto-scroll output panel when new content arrives
      Elem.script [] [ Text.raw """
        new MutationObserver(function(mutations) {
          var panel = document.getElementById('output-panel');
          if (panel) panel.scrollTop = panel.scrollHeight;
        }).observe(document.getElementById('output-panel') || document.body, { childList: true, subtree: true });
      """ ]
      // Server connection monitoring: SSE pushes server-status as "Connected" on every cycle.
      // MutationObserver detects when SSE stops updating, indicating disconnection.
      Elem.script [] [ Text.raw """
        (function() {
          var banner = document.getElementById('server-status');
          var timeout = null;
          var STALE_MS = 12000;

          function markDisconnected() {
            banner.className = 'conn-banner conn-disconnected';
            banner.textContent = '\u274c Server disconnected \u2014 waiting for reconnect...';
          }

          function markConnected() {
            banner.className = 'conn-banner conn-connected';
            banner.textContent = '\u2705 Connected';
          }

          function resetTimeout() {
            if (timeout) clearTimeout(timeout);
            timeout = setTimeout(markDisconnected, STALE_MS);
          }

          // Watch for any mutation to server-status (SSE pushes morphs here)
          var observer = new MutationObserver(function() {
            markConnected();
            resetTimeout();
          });
          observer.observe(banner, { childList: true, characterData: true, subtree: true, attributes: true });

          // Initial timeout — if no SSE arrives within STALE_MS, show disconnected
          resetTimeout();
        })();
      """ ]
    ]
  ]

/// Render session status as an HTML fragment for Datastar morphing.
let renderSessionStatus (sessionState: string) (sessionId: string) (projectCount: int) =
  let statusClass =
    match sessionState with
    | "Ready" -> "status-ready"
    | "WarmingUp" -> "status-warming"
    | _ -> "status-faulted"
  Elem.div [ Attr.id "session-status" ] [
    Elem.span [ Attr.class' (sprintf "status %s" statusClass) ] [ Text.raw sessionState ]
    Elem.br []
    Elem.span [ Attr.class' "meta" ] [
      Text.raw (sprintf "Session: %s | Projects: %d" sessionId projectCount)
    ]
  ]

/// Render eval stats as an HTML fragment.
let renderEvalStats (evalCount: int) (avgMs: float) (minMs: float) (maxMs: float) =
  Elem.div [ Attr.id "eval-stats" ] [
    Elem.div [] [ Text.raw (sprintf "Evals: %d" evalCount) ]
    Elem.div [ Attr.class' "meta" ] [
      Text.raw (sprintf "Avg: %.0fms | Min: %.0fms | Max: %.0fms" avgMs minMs maxMs)
    ]
  ]

/// Render output lines as an HTML fragment.
let renderOutput (lines: (string option * string * string) list) =
  let lineClass kind =
    match kind with
    | "Result" -> "output-result"
    | "Error" -> "output-error"
    | "Info" -> "output-info"
    | _ -> "output-system"
  let lineIcon kind =
    match kind with
    | "Result" -> "✓ "
    | "Error" -> "✗ "
    | "Info" -> "ℹ "
    | _ -> "· "
  Elem.div [ Attr.id "output-panel" ] [
    if lines.IsEmpty then
      Text.raw "No output yet"
    else
      yield! lines |> List.map (fun (ts, kind, text) ->
        Elem.div [ Attr.class' (sprintf "output-line %s" (lineClass kind)) ] [
          Elem.span [ Attr.class' (sprintf "output-icon %s" (lineClass kind)) ] [
            Text.raw (lineIcon kind)
          ]
          match ts with
          | Some t ->
            Elem.span [ Attr.class' "meta"; Attr.style "margin-right: 0.5rem;" ] [
              Text.raw t
            ]
          | None -> ()
          Text.raw text
        ])
  ]

/// Render diagnostics as an HTML fragment.
let renderDiagnostics (diags: (string * string * int * int) list) =
  Elem.div [ Attr.id "diagnostics-panel" ] [
    if diags.IsEmpty then
      Text.raw "No diagnostics"
    else
      yield! diags |> List.map (fun (severity, message, line, col) ->
        let cls = if severity = "Error" then "diag-error" else "diag-warning"
        Elem.div [ Attr.class' (sprintf "diag %s" cls) ] [
          Text.raw (sprintf "[%s] L%d:%d %s" severity line col message)
        ])
  ]

type ParsedSession = {
  Id: string
  Status: string
  IsActive: bool
  ProjectsText: string
  EvalCount: int
  Uptime: string
  WorkingDir: string
  LastActivity: string
}

let private parseSessionLines (content: string) =
  let sessionRegex = Regex(@"^(\S+)\s*\[([^\]]+)\](\s*\*)?(\s*\([^)]*\))?(\s*evals:\d+)?(\s*up:(?:just now|\S+))?(\s*dir:\S.*?)?(\s*last:.+)?$")
  let extractTag (prefix: string) (value: string) =
    let v = value.Trim()
    if v.StartsWith(prefix) then v.Substring(prefix.Length).Trim()
    else ""
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = sessionRegex.Match(l)
    if m.Success then
      let evalsMatch = Regex.Match(m.Groups.[5].Value, @"evals:(\d+)")
      { Id = m.Groups.[1].Value
        Status = m.Groups.[2].Value
        IsActive = m.Groups.[3].Value.Contains("*")
        ProjectsText = m.Groups.[4].Value.Trim()
        EvalCount = if evalsMatch.Success then int evalsMatch.Groups.[1].Value else 0
        Uptime = extractTag "up:" m.Groups.[6].Value
        WorkingDir = extractTag "dir:" m.Groups.[7].Value
        LastActivity = extractTag "last:" m.Groups.[8].Value }
    else
      { Id = l.Trim()
        Status = "unknown"
        IsActive = false
        ProjectsText = ""
        EvalCount = 0
        Uptime = ""
        WorkingDir = ""
        LastActivity = "" })
  |> Array.toList

/// Render sessions as an HTML fragment with action buttons.
let renderSessions (sessions: ParsedSession list) =
  Elem.div [ Attr.id "sessions-panel" ] [
    if sessions.IsEmpty then
      Text.raw "No sessions"
    else
      yield! sessions |> List.map (fun (s: ParsedSession) ->
        let statusClass =
          match s.Status with
          | "running" -> "status-ready"
          | "starting" | "restarting" -> "status-warming"
          | _ -> "status-faulted"
        let cls = if s.IsActive then "output-result" else ""
        Elem.div
          [ Attr.class' (sprintf "session-row %s" cls)
            Attr.style "display: flex; align-items: center; justify-content: space-between; padding: 8px 0; border-bottom: 1px solid var(--border);" ]
          [
            Elem.div [ Attr.style "flex: 1; min-width: 0;" ] [
              // Row 1: session ID + status + active indicator
              Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5rem;" ] [
                Elem.span [ Attr.style "font-weight: bold;" ] [ Text.raw s.Id ]
                Elem.span
                  [ Attr.class' (sprintf "status %s" statusClass)
                    Attr.style "font-size: 0.7rem; padding: 1px 6px; border-radius: 3px;" ]
                  [ Text.raw s.Status ]
                if s.IsActive then
                  Elem.span [ Attr.style "color: var(--green);" ] [ Text.raw "● active" ]
                if s.Uptime.Length > 0 then
                  Elem.span [ Attr.class' "meta"; Attr.style "margin-left: auto;" ] [
                    Text.raw (sprintf "⏱ %s" s.Uptime)
                  ]
              ]
              // Row 2: working directory
              if s.WorkingDir.Length > 0 then
                Elem.div
                  [ Attr.style "font-size: 0.75rem; color: var(--fg-dim); margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                    Attr.title s.WorkingDir ]
                  [ Text.raw (sprintf "📁 %s" s.WorkingDir) ]
              // Row 3: projects as tags + evals + last activity
              Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5rem; margin-top: 2px; flex-wrap: wrap;" ] [
                if s.ProjectsText.Length > 0 then
                  // Parse project names from "(Proj1, Proj2)" format and render as tags
                  let projNames =
                    s.ProjectsText.Trim('(', ')')
                      .Split(',')
                    |> Array.map (fun p -> p.Trim())
                    |> Array.filter (fun p -> p.Length > 0)
                  yield! projNames |> Array.map (fun pName ->
                    Elem.span
                      [ Attr.style "font-size: 0.65rem; padding: 1px 5px; border-radius: 3px; background: var(--bg-highlight); color: var(--fg-dim);" ]
                      [ Text.raw pName ])
                if s.EvalCount > 0 then
                  Elem.span [ Attr.class' "meta" ] [
                    Text.raw (sprintf "evals: %d" s.EvalCount)
                  ]
                if s.LastActivity.Length > 0 then
                  Elem.span [ Attr.class' "meta"; Attr.style "margin-left: auto;" ] [
                    Text.raw (sprintf "last: %s" s.LastActivity)
                  ]
              ]
            ]
            Elem.div [ Attr.style "display: flex; gap: 4px; margin-left: 8px;" ] [
              if not s.IsActive then
                Elem.button
                  [ Attr.class' "session-btn"
                    Ds.onClick (Ds.post (sprintf "/dashboard/session/switch/%s" s.Id)) ]
                  [ Text.raw "⇄" ]
              Elem.button
                [ Attr.class' "session-btn session-btn-danger"
                  Ds.onClick (Ds.post (sprintf "/dashboard/session/stop/%s" s.Id)) ]
                [ Text.raw "■" ]
            ]
          ])
  ]

let private parseOutputLines (content: string) =
  let tsKindRegex = Regex(@"^\[(\d{2}:\d{2}:\d{2})\]\s*\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  let kindOnlyRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = tsKindRegex.Match(l)
    if m.Success then
      let kind =
        match m.Groups.[2].Value.ToLowerInvariant() with
        | "result" -> "Result"
        | "error" -> "Error"
        | "info" -> "Info"
        | _ -> "System"
      Some m.Groups.[1].Value, kind, m.Groups.[3].Value
    else
      let m2 = kindOnlyRegex.Match(l)
      if m2.Success then
        let kind =
          match m2.Groups.[1].Value.ToLowerInvariant() with
          | "result" -> "Result"
          | "error" -> "Error"
          | "info" -> "Info"
          | _ -> "System"
        None, kind, m2.Groups.[2].Value
      else
        None, "Result", l)
  |> Array.toList

let private parseDiagLines (content: string) =
  let diagRegex = Regex(@"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = diagRegex.Match(l)
    if m.Success then
      let severity = if m.Groups.[1].Value = "error" then "Error" else "Warning"
      let line = int m.Groups.[2].Value
      let col = int m.Groups.[3].Value
      let message = m.Groups.[4].Value
      severity, message, line, col
    else
      let severity = if l.Contains("[error]") then "Error" else "Warning"
      severity, l, 0, 0)
  |> Array.toList



let private renderRegionForSse (region: RenderRegion) =
  match region.Id with
  | "output" -> Some (renderOutput (parseOutputLines region.Content))
  | "diagnostics" -> Some (renderDiagnostics (parseDiagLines region.Content))
  | "sessions" -> Some (renderSessions (parseSessionLines region.Content))
  | _ -> None

let private pushRegions
  (ctx: HttpContext)
  (regions: RenderRegion list)
  = task {
    for region in regions do
      match renderRegionForSse region with
      | Some html -> do! Response.sseHtmlElements ctx html
      | None -> ()
  }

/// Create the SSE stream handler that pushes Elm state to the browser.
let createStreamHandler
  (getSessionState: unit -> SessionState)
  (getEvalStats: unit -> EvalStats)
  (sessionId: string)
  (projectCount: int)
  (getElmRegions: unit -> RenderRegion list option)
  (stateChanged: IEvent<string> option)
  (connectionTracker: ConnectionTracker option)
  : HttpHandler =
  fun ctx -> task {
    Response.sseStartResponse ctx |> ignore

    let clientId = Guid.NewGuid().ToString("N").[..7]
    connectionTracker |> Option.iter (fun t -> t.Register(clientId, Browser, sessionId))

    let pushState () = task {
      // Push server-status as "Connected" — proves SSE is alive
      do! Response.sseHtmlElements ctx (
        Elem.div [ Attr.id "server-status"; Attr.class' "conn-banner conn-connected" ] [
          Text.raw "✅ Connected"
        ])
      let state = getSessionState ()
      let stats = getEvalStats ()
      let stateStr = SessionState.label state
      let avgMs =
        if stats.EvalCount > 0
        then stats.TotalDuration.TotalMilliseconds / float stats.EvalCount
        else 0.0
      do! Response.sseHtmlElements ctx (
        renderSessionStatus stateStr sessionId projectCount)
      do! Response.sseHtmlElements ctx (
        renderEvalStats
          stats.EvalCount
          avgMs
          stats.MinDuration.TotalMilliseconds
          stats.MaxDuration.TotalMilliseconds)
      // Push connection counts
      match connectionTracker with
      | Some tracker ->
        let total = tracker.TotalCount
        let counts = tracker.GetCounts(sessionId)
        let parts =
          [ if counts.Browsers > 0 then sprintf "🌐 %d" counts.Browsers
            if counts.McpAgents > 0 then sprintf "🤖 %d" counts.McpAgents
            if counts.Terminals > 0 then sprintf "💻 %d" counts.Terminals ]
        let label =
          if parts.IsEmpty then sprintf "%d connected" total
          else sprintf "%s" (String.Join(" ", parts))
        do! Response.sseHtmlElements ctx (
          Elem.div [ Attr.id "connection-counts"; Attr.class' "meta"; Attr.style "font-size: 0.75rem; margin-top: 4px;" ] [
            Text.raw label
          ])
      | None -> ()
      match getElmRegions () with
      | Some regions -> do! pushRegions ctx regions
      | None -> ()
    }

    try
      // Push initial state
      do! pushState ()

      match stateChanged with
      | Some evt ->
        // Event-driven: push on every state change
        let tcs = Threading.Tasks.TaskCompletionSource()
        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
        use _sub = evt.Subscribe(fun _ ->
          try
            pushState()
            |> Async.AwaitTask
            |> Async.RunSynchronously
          with _ -> ())
        do! tcs.Task
      | None ->
        // Fallback: poll every second
        while not ctx.RequestAborted.IsCancellationRequested do
          try
            do! Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 1.0, ctx.RequestAborted)
            do! pushState ()
          with
          | :? OperationCanceledException -> ()
    finally
      connectionTracker |> Option.iter (fun t -> t.Unregister(clientId))
  }

/// Create the eval POST handler.
let createEvalHandler
  (evalCode: string -> Threading.Tasks.Task<string>)
  : HttpHandler =
  fun ctx -> task {
    let! doc = Request.getSignalsJson ctx
    let code =
      match doc.RootElement.TryGetProperty("code") with
      | true, prop -> prop.GetString()
      | _ -> ""
    if String.IsNullOrWhiteSpace code then
      Response.sseStartResponse ctx |> ignore
      do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
    else
      let! result = evalCode code
      Response.sseStartResponse ctx |> ignore
      do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
      // Show result inline below the eval button
      let resultHtml =
        Elem.div [ Attr.id "eval-result" ] [
          Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw result
          ]
        ]
      do! Response.sseHtmlElements ctx resultHtml
  }

/// Create the reset POST handler.
let createResetHandler
  (resetSession: unit -> Threading.Tasks.Task<string>)
  : HttpHandler =
  fun ctx -> task {
    let! result = resetSession ()
    Response.sseStartResponse ctx |> ignore
    let resultHtml =
      Elem.div [ Attr.id "eval-result" ] [
        Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
          Text.raw (sprintf "Reset: %s" result)
        ]
      ]
    do! Response.sseHtmlElements ctx resultHtml
  }

/// Create the session action handler (switch/stop).
let createSessionActionHandler
  (action: string -> Threading.Tasks.Task<string>)
  : string -> HttpHandler =
  fun sessionId ctx -> task {
    let! result = action sessionId
    Response.sseStartResponse ctx |> ignore
    let resultHtml =
      Elem.div [ Attr.id "eval-result" ] [
        Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
          Text.raw result
        ]
      ]
    do! Response.sseHtmlElements ctx resultHtml
  }

/// Create clear-output handler.
let createClearOutputHandler : HttpHandler =
  fun ctx -> task {
    Response.sseStartResponse ctx |> ignore
    let emptyOutput = Elem.div [ Attr.id "output-panel" ] [ Text.raw "No output yet" ]
    do! Response.sseHtmlElements ctx emptyOutput
  }

/// Render discovered projects as an SSE fragment.
let renderDiscoveredProjects (discovered: DiscoveredProjects) =
  Elem.div [ Attr.id "discovered-projects"; Attr.style "margin-top: 0.5rem;" ] [
    if discovered.Solutions.IsEmpty && discovered.Projects.IsEmpty then
      Elem.div [ Attr.class' "output-line output-error" ] [
        Text.raw (sprintf "No .sln/.fsproj found in %s" discovered.WorkingDir)
      ]
    else
      Elem.div [ Attr.class' "output-line output-result" ] [
        Text.raw (sprintf "Found in %s:" discovered.WorkingDir)
      ]
      if not discovered.Solutions.IsEmpty then
        yield! discovered.Solutions |> List.map (fun s ->
          Elem.div [ Attr.class' "output-line output-info"; Attr.style "padding-left: 1rem;" ] [
            Text.raw (sprintf "📁 %s (solution)" s)
          ])
      yield! discovered.Projects |> List.map (fun p ->
        Elem.div [ Attr.class' "output-line"; Attr.style "padding-left: 1rem;" ] [
          Text.raw (sprintf "📄 %s" p)
        ])
      Elem.div [ Attr.class' "meta"; Attr.style "margin-top: 4px;" ] [
        if not discovered.Solutions.IsEmpty then
          Text.raw "Will use solution file. Click 'Create Session' to proceed."
        else
          Text.raw "Will load all projects. Click 'Create Session' to proceed."
      ]
  ]

/// Helper: render an eval-result error fragment.
let private evalResultError (msg: string) =
  Elem.div [ Attr.id "eval-result" ] [
    Elem.pre [ Attr.class' "output-line output-error"; Attr.style "margin-top: 0.5rem;" ] [
      Text.raw msg
    ]
  ]

/// Helper: resolve which projects to use from signal data.
/// Priority: manual > .SageFs/config.fsx > auto-discovery
let private resolveSessionProjects (dir: string) (manualProjects: string) =
  if not (String.IsNullOrWhiteSpace manualProjects) then
    manualProjects.Split(',')
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.map (fun p ->
      if Path.IsPathRooted p then p
      else Path.Combine(dir, p))
    |> Array.toList
  else
    match DirectoryConfig.load dir with
    | Some config when not config.Projects.IsEmpty ->
      config.Projects |> List.map (fun p ->
        if Path.IsPathRooted p then p
        else Path.Combine(dir, p))
    | _ ->
      let discovered = discoverProjects dir
      if not discovered.Solutions.IsEmpty then
        [ Path.Combine(dir, discovered.Solutions.Head) ]
      elif not discovered.Projects.IsEmpty then
        discovered.Projects |> List.map (fun p -> Path.Combine(dir, p))
      else
        []

/// Helper: extract a signal by camelCase or kebab-case name.
let private getSignalString (doc: System.Text.Json.JsonDocument) (camelCase: string) (kebab: string) =
  match doc.RootElement.TryGetProperty(camelCase) with
  | true, prop -> prop.GetString()
  | _ ->
    match doc.RootElement.TryGetProperty(kebab) with
    | true, prop -> prop.GetString()
    | _ -> ""

/// Push discover results for a directory.
let private pushDiscoverResults (ctx: HttpContext) (dir: string) = task {
  let dirConfig = DirectoryConfig.load dir
  let discovered = discoverProjects dir
  let configNote =
    match dirConfig with
    | Some config when not config.Projects.IsEmpty ->
      Some (Elem.div [ Attr.class' "output-line output-info"; Attr.style "margin-bottom: 4px;" ] [
        Text.raw (sprintf "⚙️ .SageFs/config.fsx: %s" (String.Join(", ", config.Projects)))
      ])
    | Some _ ->
      Some (Elem.div [ Attr.class' "output-line meta"; Attr.style "margin-bottom: 4px;" ] [
        Text.raw "⚙️ .SageFs/config.fsx found (no projects configured)"
      ])
    | None -> None
  let mainContent = renderDiscoveredProjects discovered
  match configNote with
  | Some note ->
    let combined = Elem.div [ Attr.id "discovered-projects"; Attr.style "margin-top: 0.5rem;" ] [
      note; mainContent
    ]
    do! Response.sseHtmlElements ctx combined
  | None ->
    do! Response.sseHtmlElements ctx mainContent
}

/// Create the discover-projects POST handler.
let createDiscoverHandler : HttpHandler =
  fun ctx -> task {
    let! doc = Request.getSignalsJson ctx
    let dir = getSignalString doc "newSessionDir" "new-session-dir"
    Response.sseStartResponse ctx |> ignore
    if String.IsNullOrWhiteSpace dir then
      do! Response.sseHtmlElements ctx (
        Elem.div [ Attr.id "discovered-projects" ] [
          Elem.span [ Attr.class' "output-line output-error" ] [
            Text.raw "Enter a working directory first"
          ]])
    elif not (Directory.Exists dir) then
      do! Response.sseHtmlElements ctx (
        Elem.div [ Attr.id "discovered-projects" ] [
          Elem.span [ Attr.class' "output-line output-error" ] [
            Text.raw (sprintf "Directory not found: %s" dir)
          ]])
    else
      do! pushDiscoverResults ctx dir
  }

/// Create the create-session POST handler.
let createCreateSessionHandler
  (createSession: string list -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    let! doc = Request.getSignalsJson ctx
    let dir = getSignalString doc "newSessionDir" "new-session-dir"
    let manualProjects = getSignalString doc "manualProjects" "manual-projects"
    Response.sseStartResponse ctx |> ignore
    if String.IsNullOrWhiteSpace dir then
      do! Response.sseHtmlElements ctx (evalResultError "Working directory is required")
    elif not (Directory.Exists dir) then
      do! Response.sseHtmlElements ctx (evalResultError (sprintf "Directory not found: %s" dir))
    else
      let projects = resolveSessionProjects dir manualProjects
      if projects.IsEmpty then
        do! Response.sseHtmlElements ctx (evalResultError "No projects found. Enter paths manually or check the directory.")
      else
        let! result = createSession projects dir
        let resultHtml =
          match result with
          | Ok msg ->
            Elem.div [ Attr.id "eval-result" ] [
              Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem;" ] [
                Text.raw msg
              ]
            ]
          | Error msg -> evalResultError (sprintf "Failed: %s" msg)
        do! Response.sseHtmlElements ctx resultHtml
        do! Response.sseHtmlElements ctx (Elem.div [ Attr.id "discovered-projects" ] [])
  }

/// JSON SSE stream for TUI clients — pushes regions + model summary as JSON.
let createApiStateHandler
  (getSessionState: unit -> SessionState)
  (getEvalStats: unit -> EvalStats)
  (sessionId: string)
  (getElmRegions: unit -> RenderRegion list option)
  (stateChanged: IEvent<string> option)
  (connectionTracker: ConnectionTracker option)
  : HttpHandler =
  fun ctx -> task {
    ctx.Response.ContentType <- "text/event-stream"
    ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache"
    ctx.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues "keep-alive"

    let clientId = sprintf "tui-%s" (Guid.NewGuid().ToString("N").[..7])
    connectionTracker |> Option.iter (fun t -> t.Register(clientId, Terminal, sessionId))

    let pushJson () = task {
      let state = getSessionState ()
      let stats = getEvalStats ()
      let regions =
        match getElmRegions () with
        | Some r ->
          r |> List.map (fun region ->
            {| id = region.Id
               content = region.Content
               cursor = region.Cursor |> Option.map (fun c -> {| line = c.Line; col = c.Col |}) |})
        | None -> []
      let payload =
        System.Text.Json.JsonSerializer.Serialize(
          {| sessionState = SessionState.label state
             evalCount = stats.EvalCount
             avgMs = if stats.EvalCount > 0 then stats.TotalDuration.TotalMilliseconds / float stats.EvalCount else 0.0
             regions = regions |})
      do! ctx.Response.WriteAsync(sprintf "data: %s\n\n" payload)
      do! ctx.Response.Body.FlushAsync()
    }

    try
      do! pushJson ()
      match stateChanged with
      | Some evt ->
        let tcs = Threading.Tasks.TaskCompletionSource()
        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
        use _sub = evt.Subscribe(fun _ ->
          try pushJson () |> Async.AwaitTask |> Async.RunSynchronously
          with _ -> ())
        do! tcs.Task
      | None ->
        while not ctx.RequestAborted.IsCancellationRequested do
          try
            do! Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 1.0, ctx.RequestAborted)
            do! pushJson ()
          with :? OperationCanceledException -> ()
    finally
      connectionTracker |> Option.iter (fun t -> t.Unregister(clientId))
  }

/// POST /api/dispatch — accept EditorAction JSON and dispatch to Elm runtime.
let createApiDispatchHandler
  (dispatch: SageFsMsg -> unit)
  : HttpHandler =
  fun ctx -> task {
    use reader = new StreamReader(ctx.Request.Body)
    let! body = reader.ReadToEndAsync()
    try
      let action = System.Text.Json.JsonSerializer.Deserialize<{| action: string; value: string option |}>(body)
      let editorAction =
        match action.action with
        | "insertChar" ->
          action.value |> Option.bind (fun s -> if s.Length > 0 then Some (EditorAction.InsertChar s.[0]) else None)
        | "newLine" -> Some EditorAction.NewLine
        | "submit" -> Some EditorAction.Submit
        | "cancel" -> Some EditorAction.Cancel
        | "deleteBackward" -> Some EditorAction.DeleteBackward
        | "deleteForward" -> Some EditorAction.DeleteForward
        | "deleteWord" -> Some EditorAction.DeleteWord
        | "moveUp" -> Some (EditorAction.MoveCursor Direction.Up)
        | "moveDown" -> Some (EditorAction.MoveCursor Direction.Down)
        | "moveLeft" -> Some (EditorAction.MoveCursor Direction.Left)
        | "moveRight" -> Some (EditorAction.MoveCursor Direction.Right)
        | "moveWordForward" -> Some EditorAction.MoveWordForward
        | "moveWordBackward" -> Some EditorAction.MoveWordBackward
        | "moveToLineStart" -> Some EditorAction.MoveToLineStart
        | "moveToLineEnd" -> Some EditorAction.MoveToLineEnd
        | "undo" -> Some EditorAction.Undo
        | "selectAll" -> Some EditorAction.SelectAll
        | "triggerCompletion" -> Some EditorAction.TriggerCompletion
        | "dismissCompletion" -> Some EditorAction.DismissCompletion
        | "historyPrevious" -> Some EditorAction.HistoryPrevious
        | "historyNext" -> Some EditorAction.HistoryNext
        | "acceptCompletion" -> Some EditorAction.AcceptCompletion
        | "nextCompletion" -> Some EditorAction.NextCompletion
        | "previousCompletion" -> Some EditorAction.PreviousCompletion
        | "selectWord" -> Some EditorAction.SelectWord
        | "deleteToEndOfLine" -> Some EditorAction.DeleteToEndOfLine
        | "redo" -> Some EditorAction.Redo
        | "toggleSessionPanel" -> Some EditorAction.ToggleSessionPanel
        | "listSessions" -> Some EditorAction.ListSessions
        | "switchSession" ->
          action.value |> Option.map EditorAction.SwitchSession
        | "createSession" ->
          action.value |> Option.map (fun v -> EditorAction.CreateSession [v])
        | "stopSession" ->
          action.value |> Option.map EditorAction.StopSession
        | "historySearch" ->
          action.value |> Option.map EditorAction.HistorySearch
        | _ -> None
      match editorAction with
      | Some ea ->
        dispatch (SageFsMsg.Editor ea)
        ctx.Response.StatusCode <- 200
        do! ctx.Response.WriteAsJsonAsync({| ok = true |})
      | None ->
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = sprintf "Unknown action: %s" action.action |})
    with ex ->
      ctx.Response.StatusCode <- 400
      do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
  }

/// Create all dashboard routes.
let createEndpoints
  (version: string)
  (getSessionState: unit -> SessionState)
  (getEvalStats: unit -> EvalStats)
  (sessionId: string)
  (projectCount: int)
  (getElmRegions: unit -> RenderRegion list option)
  (stateChanged: IEvent<string> option)
  (evalCode: string -> Threading.Tasks.Task<string>)
  (resetSession: unit -> Threading.Tasks.Task<string>)
  (hardResetSession: unit -> Threading.Tasks.Task<string>)
  (switchSession: (string -> Threading.Tasks.Task<string>) option)
  (stopSession: (string -> Threading.Tasks.Task<string>) option)
  (createSession: (string list -> string -> Threading.Tasks.Task<Result<string, string>>) option)
  (connectionTracker: ConnectionTracker option)
  (dispatch: SageFsMsg -> unit)
  : HttpEndpoint list =
  [
    yield get "/dashboard" (FalcoResponse.ofHtml (renderShell version))
    yield get "/dashboard/stream" (createStreamHandler getSessionState getEvalStats sessionId projectCount getElmRegions stateChanged connectionTracker)
    yield post "/dashboard/eval" (createEvalHandler evalCode)
    yield post "/dashboard/reset" (createResetHandler resetSession)
    yield post "/dashboard/hard-reset" (createResetHandler hardResetSession)
    yield post "/dashboard/clear-output" createClearOutputHandler
    yield post "/dashboard/discover-projects" createDiscoverHandler
    // TUI client API
    yield get "/api/state" (createApiStateHandler getSessionState getEvalStats sessionId getElmRegions stateChanged connectionTracker)
    yield post "/api/dispatch" (createApiDispatchHandler dispatch)
    match createSession with
    | Some handler ->
      yield post "/dashboard/session/create" (createCreateSessionHandler handler)
    | None -> ()
    match switchSession with
    | Some handler ->
      yield mapPost "/dashboard/session/switch/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sid -> createSessionActionHandler handler sid)
    | None -> ()
    match stopSession with
    | Some handler ->
      yield mapPost "/dashboard/session/stop/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sid -> createSessionActionHandler handler sid)
    | None -> ()
  ]
