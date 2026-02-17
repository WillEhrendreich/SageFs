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

/// sseHtmlElements uses renderHtml which prepends <!DOCTYPE html> to every
/// fragment, causing Datastar to choke. Use renderNode + sseStringElements instead.
let ssePatchNode (ctx: HttpContext) (node: XmlNode) =
  Response.sseStringElements ctx (renderNode node)

/// Discriminated union for output line kinds — replaces stringly-typed matching.
type OutputLineKind =
  | ResultLine
  | ErrorLine
  | InfoLine
  | SystemLine

module OutputLineKind =
  let fromString (s: string) =
    match s.ToLowerInvariant() with
    | "result" -> ResultLine
    | "error" -> ErrorLine
    | "info" -> InfoLine
    | _ -> SystemLine

  let toCssClass = function
    | ResultLine -> "output-result"
    | ErrorLine -> "output-error"
    | InfoLine -> "output-info"
    | SystemLine -> "output-system"

/// Parsed output line with typed kind.
type OutputLine = {
  Timestamp: string option
  Kind: OutputLineKind
  Text: string
}

/// Discriminated union for diagnostic severity.
type DiagSeverity =
  | DiagError
  | DiagWarning

module DiagSeverity =
  let fromString (s: string) =
    match s.ToLowerInvariant() with
    | "error" -> DiagError
    | _ -> DiagWarning

  let toCssClass = function
    | DiagError -> "diag-error"
    | DiagWarning -> "diag-warning"

  let toIcon = function
    | DiagError -> "✗"
    | DiagWarning -> "⚠"

/// Parsed diagnostic with typed severity.
type Diagnostic = {
  Severity: DiagSeverity
  Message: string
  Line: int
  Col: int
}

/// Eval statistics view model — pre-computed for rendering.
type EvalStatsView = {
  Count: int
  AvgMs: float
  MinMs: float
  MaxMs: float
}

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
let renderShell (version: string) =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ Text.raw "SageFs Dashboard" ]
      // Connection monitor: intercept fetch to detect SSE stream lifecycle.
      // Tracks open/close of the /dashboard/stream SSE connection.
      Elem.script [] [ Text.raw """
        (function() {
          var origFetch = window.fetch, streamCount = 0;
          function setBanner(cls, text) {
            var b = document.getElementById('server-status');
            if (b) { b.className = 'conn-banner ' + cls; b.textContent = text; }
          }
          window.fetch = function(url) {
            var isStream = typeof url === 'string' && url.indexOf('/dashboard/stream') !== -1;
            if (isStream) streamCount++;
            var p = origFetch.apply(this, arguments);
            if (isStream) {
              p.then(function(resp) {
                if (resp.ok) setBanner('conn-connected', '\u2705 Connected');
                else setBanner('conn-disconnected', '\u274c Server error (' + resp.status + ')');
              }).catch(function() {
                setBanner('conn-disconnected', '\u274c Server disconnected \u2014 waiting for reconnect...');
              });
            }
            return p;
          };
        })();
      """ ]
      Ds.cdnScript
      Elem.style [] [ Text.raw (sprintf """
        :root { %s --font-size: 14px; --sidebar-width: 320px; }
        * { box-sizing: border-box; margin: 0; padding: 0; }""" (Theme.toCssVariables Theme.defaults)) ]
      Elem.style [] [ Text.raw """
        body { font-family: 'Cascadia Code', 'Fira Code', monospace; background: var(--bg-default); color: var(--fg-default); font-size: var(--font-size); height: 100vh; display: flex; flex-direction: column; overflow: hidden; }
        h1 { color: var(--fg-blue); font-size: 1.4rem; }
        .app-header { display: flex; align-items: center; justify-content: space-between; padding: 0.5rem 1rem; border-bottom: 1px solid var(--border-normal); flex-shrink: 0; }
        .app-layout { display: flex; flex: 1; overflow: hidden; }
        .main-area { flex: 1; display: flex; flex-direction: column; overflow: hidden; min-width: 0; }
        .sidebar { width: var(--sidebar-width); border-left: 1px solid var(--border-normal); background: var(--bg-panel); overflow-y: auto; flex-shrink: 0; transition: width 0.2s, padding 0.2s; }
        .sidebar.collapsed { width: 0; padding: 0; overflow: hidden; border-left: none; }
        .sidebar-inner { padding: 0.75rem; display: flex; flex-direction: column; gap: 0.75rem; min-width: var(--sidebar-width); }
        .panel { background: var(--bg-panel); border: 1px solid var(--border-normal); border-radius: 8px; padding: 0.75rem; }
        .panel h2 { color: var(--fg-blue); font-size: 0.9rem; margin-bottom: 0.5rem; border-bottom: 1px solid var(--border-normal); padding-bottom: 0.5rem; display: flex; justify-content: space-between; align-items: center; }
        .output-area { flex: 1; display: flex; flex-direction: column; overflow: hidden; border-bottom: 1px solid var(--border-normal); }
        .output-header { display: flex; align-items: center; justify-content: space-between; padding: 0.5rem 1rem; flex-shrink: 0; }
        .output-header h2 { color: var(--fg-blue); font-size: 1rem; margin: 0; }
        #output-panel { flex: 1; overflow-y: auto; scroll-behavior: smooth; background: var(--bg-default); font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 0.85rem; padding: 0.25rem 0; }
        .eval-area { flex-shrink: 0; padding: 0.75rem 1rem; }
        #editor-area { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
        #session-picker:empty { display: none; }
        #session-picker:not(:empty) ~ #editor-area { display: none; }
        .picker-container { display: flex; flex-direction: column; align-items: center; justify-content: center; flex: 1; padding: 2rem; gap: 1.5rem; }
        .picker-container h2 { color: var(--fg-blue); font-size: 1.4rem; margin-bottom: 0.5rem; }
        .picker-options { display: flex; gap: 1rem; flex-wrap: wrap; justify-content: center; width: 100%; max-width: 900px; }
        .picker-card { flex: 1; min-width: 240px; background: var(--bg-panel); border: 1px solid var(--border-normal); border-radius: 8px; padding: 1.25rem; cursor: pointer; transition: border-color 0.15s, background 0.15s; }
        .picker-card:hover { border-color: var(--fg-blue); background: var(--bg-highlight); }
        .picker-card h3 { color: var(--fg-blue); font-size: 1rem; margin-bottom: 0.5rem; }
        .picker-card p { color: var(--fg-dim); font-size: 0.85rem; line-height: 1.4; }
        .picker-form { width: 100%; max-width: 500px; }
        .picker-form .eval-input { min-height: auto; height: 2rem; margin-bottom: 0.5rem; }
        .picker-previous { width: 100%; max-width: 600px; }
        .picker-session-row { display: flex; align-items: center; justify-content: space-between; padding: 0.5rem 0.75rem; border-bottom: 1px solid var(--border-normal); cursor: pointer; border-radius: 4px; transition: background 0.1s; }
        .picker-session-row:hover { background: var(--bg-highlight); }
        .status { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; font-weight: bold; }
        .status-ready { background: var(--fg-green); color: var(--bg-default); }
        .status-warming { background: var(--fg-yellow); color: var(--bg-default); }
        .status-faulted { background: var(--fg-red); color: white; }
        .output-line { font-size: 0.85rem; padding: 1px 0.5rem; white-space: pre-wrap; word-break: break-all; line-height: 1.5; }
        .output-result { color: var(--fg-green); }
        .output-error { color: var(--fg-red); }
        .output-info { color: var(--fg-blue); }
        .output-system { color: var(--fg-dim); }
        .diag { font-size: 0.85rem; padding: 1px 0.5rem; line-height: 1.5; }
        .diag-error { color: var(--fg-red); }
        .diag-warning { color: var(--fg-yellow); }
        .diag-location { font-family: monospace; background: var(--bg-selection); padding: 1px 4px; border-radius: 3px; font-size: 0.8rem; margin-right: 0.25rem; }
        .meta { color: var(--fg-dim); font-size: 0.8rem; }
        .eval-input { width: 100%; background: var(--bg-editor); color: var(--fg-default); border: 1px solid var(--border-normal); border-radius: 4px; padding: 0.5rem; font-family: inherit; font-size: 0.9rem; resize: vertical; min-height: 80px; tab-size: 2; }
        .eval-input:focus { outline: 1px solid var(--border-focus); border-color: var(--border-focus); }
        .eval-btn { background: var(--fg-blue); color: var(--bg-default); border: none; border-radius: 4px; padding: 0.5rem 1rem; cursor: pointer; font-family: inherit; font-weight: bold; margin-top: 0.5rem; transition: opacity 0.15s; }
        .eval-btn:hover { opacity: 0.85; }
        .eval-btn:active { opacity: 0.7; }
        .session-btn { background: var(--border-normal); color: var(--fg-default); border: none; border-radius: 4px; padding: 2px 8px; cursor: pointer; font-size: 0.8rem; transition: background 0.15s; }
        .session-btn:hover { background: var(--fg-blue); color: var(--bg-default); }
        .session-btn-danger:hover { background: var(--fg-red); color: white; }
        .session-selected { background: var(--bg-selection); border-left: 3px solid var(--border-focus); }
        .session-row:hover { background: var(--bg-selection); }
        .panel-header-btn { background: none; border: 1px solid var(--border-normal); color: var(--fg-default); border-radius: 4px; padding: 1px 8px; cursor: pointer; font-size: 0.75rem; font-family: inherit; }
        .panel-header-btn:hover { background: var(--border-normal); }
        .log-box { background: var(--bg-default); border: 1px solid var(--border-normal); border-radius: 4px; padding: 0.5rem 0; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 0.85rem; }
        .conn-banner { padding: 4px 1rem; text-align: center; font-size: 0.8rem; font-weight: bold; border-radius: 0; transition: all 0.3s; flex-shrink: 0; }
        .conn-connected { background: var(--fg-green); color: var(--bg-default); }
        .conn-disconnected { background: var(--fg-red); color: white; animation: pulse 1.5s infinite; }
        .conn-reconnecting { background: var(--fg-yellow); color: var(--bg-default); }
        @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.6; } }
        .eval-btn:disabled { opacity: 0.5; cursor: not-allowed; }
        .sidebar-toggle { background: none; border: 1px solid var(--border-normal); color: var(--fg-default); border-radius: 4px; padding: 2px 8px; cursor: pointer; font-size: 0.85rem; font-family: inherit; }
        .sidebar-toggle:hover { background: var(--border-normal); }
        .resize-handle { width: 4px; background: var(--border-normal); cursor: col-resize; flex-shrink: 0; transition: background 0.15s; }
        .resize-handle:hover, .resize-handle.dragging { background: var(--border-focus); }
        .syn-keyword { color: var(--syn-keyword); }
        .syn-string { color: var(--syn-string); }
        .syn-comment { color: var(--syn-comment); font-style: italic; }
        .syn-number { color: var(--syn-number); }
        .syn-operator { color: var(--syn-operator); }
        .syn-type { color: var(--syn-type); }
        .syn-function { color: var(--syn-function); }
        .syn-variable { color: var(--syn-variable); }
        .syn-punctuation { color: var(--syn-punctuation); }
        .syn-constant { color: var(--syn-constant); }
        .syn-module { color: var(--syn-module); }
        .syn-attribute { color: var(--syn-attribute); }
        .syn-directive { color: var(--syn-directive); }
        .syn-property { color: var(--syn-property); }
        @media (max-width: 768px) {
          .sidebar { position: fixed; right: 0; top: 0; bottom: 0; z-index: 10; }
          .sidebar.collapsed { width: 0; }
        }
      """ ]
    ]
    Elem.body [ Ds.safariStreamingFix ] [
      // Dedicated init element that connects to SSE stream (per Falco.Datastar pattern)
      Elem.div [ Ds.onInit (Ds.get "/dashboard/stream"); Ds.signal ("helpVisible", "false"); Ds.signal ("sidebarOpen", "true"); Ds.signal ("serverConnected", "false") ] []
      // Connection status banner — hidden when connected
      Elem.div [ Attr.id "server-status"; Attr.class' "conn-banner conn-disconnected"; Ds.show "!$serverConnected" ] [
        Text.raw "⏳ Connecting to server..."
      ]
      // App header
      Elem.div [ Attr.class' "app-header" ] [
        Elem.h1 [] [ Text.raw (sprintf "🧙 SageFs v%s" version) ]
        Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.75rem;" ] [
          Elem.div [ Attr.id "session-status" ] []
          Elem.div [ Attr.id "eval-stats"; Attr.class' "meta" ] []
          Elem.button
            [ Attr.class' "sidebar-toggle"
              Ds.onEvent ("click", "$sidebarOpen = !$sidebarOpen") ]
            [ Text.raw "☰ Panel" ]
        ]
      ]
      // Main app layout: output+eval on left, sidebar on right
      Elem.div [ Attr.class' "app-layout" ] [
        // Main content area
        Elem.div [ Attr.class' "main-area" ] [
          // Session picker — shown when no sessions exist, hidden otherwise
          Elem.div [ Attr.id "session-picker" ] []
          // Output area — fills available space above eval
          Elem.div [ Attr.id "editor-area" ] [
            Elem.div [ Attr.id "output-section"; Attr.class' "output-area" ] [
              Elem.div [ Attr.class' "output-header" ] [
                Elem.h2 [] [ Text.raw "Output" ]
                Elem.button
                  [ Attr.class' "panel-header-btn"
                    Ds.onClick (Ds.post "/dashboard/clear-output") ]
                  [ Text.raw "Clear" ]
              ]
              Elem.div [ Attr.id "output-panel" ] [
                Elem.span [ Attr.class' "meta"; Attr.style "padding: 0.5rem;" ] [ Text.raw "No output yet" ]
              ]
            ]
            // Eval area — fixed at bottom
            Elem.div [ Attr.id "evaluate-section"; Attr.class' "eval-area" ] [
              Elem.div [ Attr.style "display: flex; align-items: center; justify-content: space-between; margin-bottom: 0.25rem;" ] [
                Elem.span [ Attr.style "color: var(--accent); font-weight: bold; font-size: 0.9rem;" ] [ Text.raw "Evaluate" ]
                Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5rem;" ] [
                  Elem.span [ Attr.class' "meta"; Attr.style "font-size: 0.75rem;" ] [
                    Elem.span [ Ds.text """$code ? ($code.split('\\n').length + 'L ' + $code.length + 'c') : ''""" ] []
                  ]
                  Elem.button
                    [ Attr.class' "panel-header-btn"
                      Ds.onEvent ("click", "$helpVisible = !$helpVisible") ]
                    [ Text.raw "⌨" ]
                ]
              ]
              Elem.div [ Attr.id "keyboard-help-wrapper"; Ds.show "$helpVisible" ] [
                renderKeyboardHelp ()
              ]
              Elem.textarea
                [ Attr.class' "eval-input"
                  Ds.bind "code"
                  Attr.create "placeholder" "Enter F# code... (Ctrl+Enter to eval)"
                  Ds.onEvent ("keydown", "if(event.ctrlKey && event.key === 'Enter') { event.preventDefault(); @post('/dashboard/eval') } if(event.ctrlKey && event.key === 'l') { event.preventDefault(); @post('/dashboard/clear-output') } if(event.key === 'Tab') { event.preventDefault(); var s=this.selectionStart; var e=this.selectionEnd; this.value=this.value.substring(0,s)+'  '+this.value.substring(e); this.selectionStart=this.selectionEnd=s+2; this.dispatchEvent(new Event('input')) }")
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
              Elem.div [ Attr.id "eval-result" ] []
            ]
          ] // close editor-area
        ]
        // Resize handle between main area and sidebar
        Elem.div [ Attr.class' "resize-handle"; Attr.id "sidebar-resize" ] []
        // Sidebar — sessions, diagnostics, create session
        Elem.div [ Attr.id "sidebar"; Attr.class' "sidebar"; Ds.class' ("collapsed", "!$sidebarOpen") ] [
          Elem.div [ Attr.class' "sidebar-inner" ] [
            // Sessions
            Elem.div [ Attr.class' "panel" ] [
              Elem.h2 [] [ Text.raw "Sessions" ]
              Elem.div [ Attr.id "connection-counts"; Attr.class' "meta"; Attr.style "font-size: 0.75rem; margin-bottom: 0.5rem;" ] []
              Elem.div [ Attr.id "sessions-panel"; Attr.style "max-height: 250px; overflow-y: auto;" ] []
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
                    Ds.bind "newSessionDir"
                    Attr.create "placeholder" @"C:\path\to\project" ]
              ]
              Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 0.5rem;" ] [
                Elem.button
                  [ Attr.class' "eval-btn"
                    Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                    Ds.indicator "discoverLoading"
                    Ds.attr' ("disabled", "$discoverLoading")
                    Ds.onClick (Ds.post "/dashboard/discover-projects") ]
                  [ Elem.span [ Ds.show "$discoverLoading" ] [ Text.raw "⏳ " ]
                    Elem.span [ Ds.show "!$discoverLoading" ] [ Text.raw "🔍 " ]
                    Text.raw "Discover" ]
              ]
              Elem.div [ Attr.id "discovered-projects" ] []
              Elem.div [ Attr.style "margin-top: 0.5rem;" ] [
                Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px;" ] [
                  Text.raw "Projects (comma-sep)"
                ]
                Elem.input
                  [ Attr.class' "eval-input"
                    Attr.style "min-height: auto; height: 2rem;"
                    Ds.bind "manualProjects"
                    Attr.create "placeholder" "MyProject.fsproj" ]
              ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "margin-top: 0.5rem; width: 100%; font-size: 0.8rem;"
                  Ds.indicator "createLoading"
                  Ds.attr' ("disabled", "$createLoading")
                  Ds.onClick (Ds.post "/dashboard/session/create") ]
                [ Elem.span [ Ds.show "$createLoading" ] [ Text.raw "⏳ Creating... " ]
                  Elem.span [ Ds.show "!$createLoading" ] [ Text.raw "➕ Create" ] ]
            ]
          ]
        ]
      ]
      // Auto-scroll output panel to bottom when new content arrives
      Elem.script [] [ Text.raw """
        new MutationObserver(function() {
          var panel = document.getElementById('output-panel');
          if (panel) panel.scrollTop = panel.scrollHeight;
        }).observe(document.getElementById('output-panel') || document.body, { childList: true, subtree: true });
      """ ]
      // Font size adjustment: Ctrl+= / Ctrl+- changes --font-size CSS variable
      Elem.script [] [ Text.raw """
        (function() {
          var sizes = [10, 12, 14, 16, 18, 20, 24];
          var idx = 2;
          document.addEventListener('keydown', function(e) {
            if (e.ctrlKey && (e.key === '=' || e.key === '+')) { e.preventDefault(); idx = Math.min(sizes.length - 1, idx + 1); document.documentElement.style.setProperty('--font-size', sizes[idx] + 'px'); }
            if (e.ctrlKey && e.key === '-') { e.preventDefault(); idx = Math.max(0, idx - 1); document.documentElement.style.setProperty('--font-size', sizes[idx] + 'px'); }
            if (e.ctrlKey && e.key === 'Tab') {
              e.preventDefault();
              var body = {action: e.shiftKey ? 'sessionCyclePrev' : 'sessionCycleNext'};
              fetch('/api/dispatch', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body) });
              return;
            }
            // Session navigation when not typing in an input/textarea
            var tag = (e.target.tagName || '').toLowerCase();
            if (tag !== 'input' && tag !== 'textarea') {
              var action = null;
              var value = null;
              if (e.key === 'j' || e.key === 'ArrowDown') { action = 'sessionNavDown'; }
              if (e.key === 'k' || e.key === 'ArrowUp') { action = 'sessionNavUp'; }
              if (e.key === 'Enter') { action = 'sessionSelect'; }
              if (e.key === 'x' || e.key === 'Delete') { action = 'sessionDelete'; }
              if (e.key === 'n') { e.preventDefault(); fetch('/dashboard/session/create', {method:'POST'}); return; }
              if (e.key >= '1' && e.key <= '9') { action = 'sessionSetIndex'; value = String(parseInt(e.key) - 1); }
              if (action) {
                e.preventDefault();
                var body = value ? {action: action, value: value} : {action: action};
                fetch('/api/dispatch', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body) });
              }
            }
          });
          // Sidebar resize drag
          var handle = document.getElementById('sidebar-resize');
          var sidebar = document.getElementById('sidebar');
          if (handle && sidebar) {
            var dragging = false;
            handle.addEventListener('mousedown', function(e) {
              dragging = true; handle.classList.add('dragging');
              e.preventDefault();
            });
            document.addEventListener('mousemove', function(e) {
              if (!dragging) return;
              var w = Math.max(200, Math.min(600, window.innerWidth - e.clientX));
              sidebar.style.width = w + 'px';
            });
            document.addEventListener('mouseup', function() {
              if (dragging) { dragging = false; handle.classList.remove('dragging'); }
            });
          }
        })();
      """ ]
    ]
  ]

/// Render session status as an HTML fragment for Datastar morphing.
let renderSessionStatus (sessionState: string) (sessionId: string) (projectCount: int) (workingDir: string) =
  match sessionState with
  | "Ready" ->
    Elem.div [ Attr.id "session-status" ] [
      Elem.span [ Attr.class' "status status-ready" ] [ Text.raw sessionState ]
      Elem.br []
      Elem.span [ Attr.class' "meta" ] [
        Text.raw (sprintf "Session: %s | CWD: %s | Projects: %d" sessionId workingDir projectCount)
      ]
    ]
  | _ ->
    let statusClass =
      match sessionState with
      | "WarmingUp" -> "status-warming"
      | _ -> "status-faulted"
    Elem.div [ Attr.id "session-status" ] [
      Elem.span [ Attr.class' (sprintf "status %s" statusClass) ] [ Text.raw sessionState ]
      Elem.br []
      Elem.span [ Attr.class' "meta" ] [
        Text.raw (sprintf "Session: %s | CWD: %s | Projects: %d" sessionId workingDir projectCount)
      ]
    ]

/// Render eval stats as an HTML fragment.
let renderEvalStats (stats: EvalStatsView) =
  Elem.div [ Attr.id "eval-stats"; Attr.class' "meta" ] [
    Text.raw (sprintf "%d evals · avg %.0fms · min %.0fms · max %.0fms" stats.Count stats.AvgMs stats.MinMs stats.MaxMs)
  ]

/// Map a tree-sitter capture name to the CSS class suffix.
let private captureToCssClass (capture: string) =
  match capture with
  | s when s.StartsWith "keyword" -> "syn-keyword"
  | s when s.StartsWith "string" -> "syn-string"
  | s when s.StartsWith "comment" -> "syn-comment"
  | s when s.StartsWith "number" -> "syn-number"
  | s when s.StartsWith "operator" -> "syn-operator"
  | s when s.StartsWith "type" -> "syn-type"
  | s when s.StartsWith "function" -> "syn-function"
  | s when s.StartsWith "variable" -> "syn-variable"
  | s when s.StartsWith "punctuation" -> "syn-punctuation"
  | s when s.StartsWith "constant" -> "syn-constant"
  | s when s.StartsWith "module" -> "syn-module"
  | s when s.StartsWith "attribute" -> "syn-attribute"
  | s when s.StartsWith "property" -> "syn-property"
  | s when s.StartsWith "boolean" -> "syn-constant"
  | _ -> ""

/// Render a single line of code with syntax highlighting as HTML spans.
let private renderHighlightedLine (spans: ColorSpan array) (line: string) : XmlNode list =
  if spans.Length = 0 || line.Length = 0 then [ Text.raw (System.Net.WebUtility.HtmlEncode line) ]
  else
    let nodes = ResizeArray<XmlNode>()
    let mutable pos = 0
    for span in spans do
      // Skip overlapping spans (tree-sitter may produce multiple captures for same position)
      if span.Start < pos then () else
      if span.Start > pos && pos < line.Length then
        // Unhighlighted gap
        let gapEnd = min span.Start line.Length
        nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode(line.Substring(pos, gapEnd - pos))))
        pos <- gapEnd
      if span.Start >= 0 && span.Start < line.Length then
        let end' = min (span.Start + span.Length) line.Length
        let text = line.Substring(span.Start, end' - span.Start)
        // Map fg packed RGB to a CSS class using theme defaults
        let cssClass =
          let theme = Theme.defaults
          if span.Fg = Theme.hexToRgb theme.SynKeyword then "syn-keyword"
          elif span.Fg = Theme.hexToRgb theme.SynString then "syn-string"
          elif span.Fg = Theme.hexToRgb theme.SynComment then "syn-comment"
          elif span.Fg = Theme.hexToRgb theme.SynNumber then "syn-number"
          elif span.Fg = Theme.hexToRgb theme.SynOperator then "syn-operator"
          elif span.Fg = Theme.hexToRgb theme.SynType then "syn-type"
          elif span.Fg = Theme.hexToRgb theme.SynFunction then "syn-function"
          elif span.Fg = Theme.hexToRgb theme.SynModule then "syn-module"
          elif span.Fg = Theme.hexToRgb theme.SynAttribute then "syn-attribute"
          elif span.Fg = Theme.hexToRgb theme.SynPunctuation then "syn-punctuation"
          elif span.Fg = Theme.hexToRgb theme.SynConstant then "syn-constant"
          elif span.Fg = Theme.hexToRgb theme.SynProperty then "syn-property"
          else ""
        if cssClass <> "" then
          nodes.Add(Elem.span [ Attr.class' cssClass ] [ Text.raw (System.Net.WebUtility.HtmlEncode text) ])
        else
          nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode text))
        pos <- end'
    // Remaining unhighlighted text
    if pos < line.Length then
      nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode(line.Substring(pos))))
    nodes |> Seq.toList

/// Render output lines as an HTML fragment.
let renderOutput (lines: OutputLine list) =
  Elem.div [ Attr.id "output-panel" ] [
    if lines.IsEmpty then
      Elem.span [ Attr.class' "meta" ] [ Text.raw "No output yet" ]
    else
      yield! lines |> List.map (fun line ->
        let css = OutputLineKind.toCssClass line.Kind
        Elem.div [ Attr.class' (sprintf "output-line %s" css) ] [
          match line.Timestamp with
          | Some t ->
            Elem.span [ Attr.class' "meta"; Attr.style "margin-right: 0.5rem;" ] [
              Text.raw t
            ]
          | None -> ()
          // Apply syntax highlighting to result/info lines (F# code output)
          if (line.Kind = ResultLine || line.Kind = InfoLine) && SyntaxHighlight.isAvailable () then
            let allSpans = SyntaxHighlight.tokenize Theme.defaults line.Text
            if allSpans.Length > 0 then
              yield! renderHighlightedLine allSpans.[0] line.Text
            else
              Text.raw (System.Net.WebUtility.HtmlEncode line.Text)
          else
            Text.raw (System.Net.WebUtility.HtmlEncode line.Text)
        ])
  ]

/// Render diagnostics as an HTML fragment.
let renderDiagnostics (diags: Diagnostic list) =
  Elem.div [ Attr.id "diagnostics-panel"; Attr.class' "log-box" ] [
    if diags.IsEmpty then
      Elem.span [ Attr.class' "meta" ] [ Text.raw "No diagnostics" ]
    else
      yield! diags |> List.map (fun diag ->
        let cls = DiagSeverity.toCssClass diag.Severity
        Elem.div [ Attr.class' (sprintf "diag %s" cls) ] [
          Elem.span [ Attr.style "margin-right: 0.25rem;" ] [
            Text.raw (DiagSeverity.toIcon diag.Severity)
          ]
          if diag.Line > 0 || diag.Col > 0 then
            Elem.span [ Attr.class' "diag-location" ] [
              Text.raw (sprintf "L%d:%d" diag.Line diag.Col)
            ]
          Elem.span [] [
            Text.raw (sprintf " %s" diag.Message)
          ]
        ])
  ]

type ParsedSession = {
  Id: string
  Status: string
  IsActive: bool
  IsSelected: bool
  ProjectsText: string
  EvalCount: int
  Uptime: string
  WorkingDir: string
  LastActivity: string
}

let parseSessionLines (content: string) =
  let sessionRegex = Regex(@"^([> ])\s+(\S+)\s*\[([^\]]+)\](\s*\*)?(\s*\([^)]*\))?(\s*evals:\d+)?(\s*up:(?:just now|\S+))?(\s*dir:\S.*?)?(\s*last:.+)?$")
  let extractTag (prefix: string) (value: string) =
    let v = value.Trim()
    if v.StartsWith(prefix) then v.Substring(prefix.Length).Trim()
    else ""
  content.Split('\n')
  |> Array.filter (fun (l: string) ->
    l.Length > 0
    && not (l.StartsWith("───"))
    && not (l.StartsWith("⏳")))
  |> Array.map (fun (l: string) ->
    let m = sessionRegex.Match(l)
    if m.Success then
      let evalsMatch = Regex.Match(m.Groups.[6].Value, @"evals:(\d+)")
      { Id = m.Groups.[2].Value
        Status = m.Groups.[3].Value
        IsActive = m.Groups.[4].Value.Contains("*")
        IsSelected = m.Groups.[1].Value = ">"
        ProjectsText = m.Groups.[5].Value.Trim()
        EvalCount = if evalsMatch.Success then int evalsMatch.Groups.[1].Value else 0
        Uptime = extractTag "up:" m.Groups.[7].Value
        WorkingDir = extractTag "dir:" m.Groups.[8].Value
        LastActivity = extractTag "last:" m.Groups.[9].Value }
    else
      { Id = l.Trim()
        Status = "unknown"
        IsActive = false
        IsSelected = false
        ProjectsText = ""
        EvalCount = 0
        Uptime = ""
        WorkingDir = ""
        LastActivity = "" })
  |> Array.toList

let isCreatingSession (content: string) =
  content.Contains("⏳ Creating session...")

/// A previously-known session that can be resumed.
type PreviousSession = {
  Id: string
  WorkingDir: string
  Projects: string list
  LastSeen: DateTime
}

/// Render the session picker — shown in the main area when no sessions exist.
let renderSessionPicker (previous: PreviousSession list) =
  Elem.div [ Attr.id "session-picker" ] [
    Elem.div [ Attr.class' "picker-container" ] [
      Elem.h2 [] [ Text.raw "Start a Session" ]
      Elem.p [ Attr.class' "meta"; Attr.style "text-align: center; max-width: 500px;" ] [
        Text.raw "Choose how to get started. You can create a new session or resume a previous one."
      ]
      Elem.div [ Attr.class' "picker-options" ] [
        // Option 1: Create in temp directory
        Elem.div
          [ Attr.class' "picker-card"
            Ds.indicator "tempLoading"
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
                Ds.bind "newSessionDir"
                Attr.create "placeholder" @"C:\path\to\project" ]
            Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 0.5rem;" ] [
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                  Ds.indicator "discoverLoading"
                  Ds.attr' ("disabled", "$discoverLoading")
                  Ds.onClick (Ds.post "/dashboard/discover-projects") ]
                [ Elem.span [ Ds.show "$discoverLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$discoverLoading" ] [ Text.raw "🔍 " ]
                  Text.raw "Discover" ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                  Ds.indicator "createLoading"
                  Ds.attr' ("disabled", "$createLoading")
                  Ds.onClick (Ds.post "/dashboard/session/create") ]
                [ Elem.span [ Ds.show "$createLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$createLoading" ] [ Text.raw "➕ " ]
                  Text.raw "Create" ]
            ]
            Elem.div [ Attr.id "discovered-projects" ] []
          ]
        ]
      ]
      // Previous sessions list
      if not previous.IsEmpty then
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
              if span.TotalDays >= 1.0 then sprintf "%.0fd ago" span.TotalDays
              elif span.TotalHours >= 1.0 then sprintf "%.0fh ago" span.TotalHours
              else sprintf "%.0fm ago" span.TotalMinutes
            Elem.div
              [ Attr.class' "picker-session-row"
                Ds.onClick (Ds.post (sprintf "/dashboard/session/resume/%s" s.Id)) ]
              [ Elem.div [ Attr.style "flex: 1; min-width: 0;" ] [
                  Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5rem;" ] [
                    Elem.span [ Attr.style "font-weight: bold;" ] [ Text.raw s.Id ]
                    Elem.span [ Attr.class' "meta" ] [ Text.raw age ]
                  ]
                  if s.WorkingDir.Length > 0 then
                    Elem.div
                      [ Attr.style "font-size: 0.75rem; color: var(--fg-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                        Attr.title s.WorkingDir ]
                      [ Text.raw (sprintf "📁 %s" s.WorkingDir) ]
                  if not s.Projects.IsEmpty then
                    Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 2px; flex-wrap: wrap;" ] [
                      yield! s.Projects |> List.map (fun p ->
                        Elem.span
                          [ Attr.style "font-size: 0.65rem; padding: 1px 5px; border-radius: 3px; background: var(--bg-highlight); color: var(--fg-dim);" ]
                          [ Text.raw (Path.GetFileName p) ])
                    ]
                ]
                Elem.span [ Attr.style "color: var(--fg-blue); font-size: 0.85rem;" ] [ Text.raw "▶" ]
              ])
        ]
    ]
  ]

/// Render an empty session picker (hidden — sessions exist).
let renderSessionPickerEmpty =
  Elem.div [ Attr.id "session-picker" ] []

/// Render sessions as an HTML fragment with action buttons.
let renderSessions (sessions: ParsedSession list) (creating: bool) =
  Elem.div [ Attr.id "sessions-panel" ] [
    if creating then
      Elem.div
        [ Attr.style "padding: 8px; text-align: center; color: var(--accent); font-size: 0.85rem;" ]
        [ Text.raw "⏳ Creating session..." ]
    if sessions.IsEmpty && not creating then
      Text.raw "No sessions"
    else
      yield! sessions |> List.mapi (fun i (s: ParsedSession) ->
        let statusClass =
          match s.Status with
          | "running" -> "status-ready"
          | "starting" | "restarting" -> "status-warming"
          | _ -> "status-faulted"
        let cls =
          if s.IsSelected then "session-selected"
          elif s.IsActive then "output-result"
          else ""
        Elem.div
          [ Attr.class' (sprintf "session-row %s" cls)
            Attr.style "display: flex; align-items: center; justify-content: space-between; padding: 8px 0; border-bottom: 1px solid var(--border); cursor: pointer;"
            Ds.onEvent ("click", sprintf "fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'sessionSetIndex',value:'%d'})}).then(function(){fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'sessionSelect'})})})" i) ]
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
    Elem.div
      [ Attr.style "font-size: 0.7rem; color: var(--fg-dim); text-align: center; padding: 4px 0; margin-top: 4px;" ]
      [ Text.raw "click/Enter switch · x stop · 1-9 jump · n new · Ctrl+Tab cycle" ]
  ]

let parseOutputLines (content: string) : OutputLine list =
  let tsKindRegex = Regex(@"^\[(\d{2}:\d{2}:\d{2})\]\s*\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  let kindOnlyRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = tsKindRegex.Match(l)
    if m.Success then
      { Timestamp = Some m.Groups.[1].Value
        Kind = OutputLineKind.fromString m.Groups.[2].Value
        Text = m.Groups.[3].Value }
    else
      let m2 = kindOnlyRegex.Match(l)
      if m2.Success then
        { Timestamp = None
          Kind = OutputLineKind.fromString m2.Groups.[1].Value
          Text = m2.Groups.[2].Value }
      else
        { Timestamp = None; Kind = ResultLine; Text = l })
  |> Array.toList

let parseDiagLines (content: string) : Diagnostic list =
  let diagRegex = Regex(@"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = diagRegex.Match(l)
    if m.Success then
      { Severity = DiagSeverity.fromString m.Groups.[1].Value
        Message = m.Groups.[4].Value
        Line = int m.Groups.[2].Value
        Col = int m.Groups.[3].Value }
    else
      { Severity = if l.Contains("[error]") then DiagError else DiagWarning
        Message = l
        Line = 0
        Col = 0 })
  |> Array.toList



let renderRegionForSse (region: RenderRegion) =
  match region.Id with
  | "output" -> Some (renderOutput (parseOutputLines region.Content))
  | "sessions" -> Some (renderSessions (parseSessionLines region.Content) (isCreatingSession region.Content))
  | _ -> None

let private pushRegions
  (ctx: HttpContext)
  (regions: RenderRegion list)
  (getPreviousSessions: unit -> PreviousSession list)
  = task {
    for region in regions do
      match renderRegionForSse region with
      | Some html -> do! ssePatchNode ctx html
      | None -> ()
      // When sessions region is pushed, also push picker visibility
      if region.Id = "sessions" then
        let sessions = parseSessionLines region.Content
        let creating = isCreatingSession region.Content
        if sessions.IsEmpty && not creating then
          let previous = getPreviousSessions ()
          do! ssePatchNode ctx (renderSessionPicker previous)
        else
          do! ssePatchNode ctx renderSessionPickerEmpty
  }

/// Create the SSE stream handler that pushes Elm state to the browser.
let createStreamHandler
  (getSessionState: unit -> SessionState)
  (getEvalStats: unit -> SageFs.Affordances.EvalStats)
  (getSessionId: unit -> string)
  (getSessionWorkingDir: unit -> string)
  (getProjectCount: unit -> int)
  (getElmRegions: unit -> RenderRegion list option)
  (stateChanged: IEvent<string> option)
  (connectionTracker: ConnectionTracker option)
  (getPreviousSessions: unit -> PreviousSession list)
  : HttpHandler =
  fun ctx -> task {
    Response.sseStartResponse ctx |> ignore

    let clientId = Guid.NewGuid().ToString("N").[..7]
    let sessionId = getSessionId ()
    connectionTracker |> Option.iter (fun t -> t.Register(clientId, Browser, sessionId))

    let pushState () = task {
      let state = getSessionState ()
      let stats = getEvalStats ()
      let stateStr = SessionState.label state
      let currentSessionId = getSessionId ()
      let workingDir = getSessionWorkingDir ()
      let avgMs =
        if stats.EvalCount > 0
        then stats.TotalDuration.TotalMilliseconds / float stats.EvalCount
        else 0.0
      let isReady = stateStr = "Ready"
      do! Response.ssePatchSignal ctx (SignalPath.sp "serverConnected") true
      do! ssePatchNode ctx (
        renderSessionStatus stateStr currentSessionId (getProjectCount ()) workingDir)
      do! ssePatchNode ctx (
        renderEvalStats
          { Count = stats.EvalCount
            AvgMs = avgMs
            MinMs = stats.MinDuration.TotalMilliseconds
            MaxMs = stats.MaxDuration.TotalMilliseconds })
      // Push connection counts
      match connectionTracker with
      | Some tracker ->
        let total = tracker.TotalCount
        let counts = tracker.GetCounts(currentSessionId)
        let parts =
          [ if counts.Browsers > 0 then sprintf "🌐 %d" counts.Browsers
            if counts.McpAgents > 0 then sprintf "🤖 %d" counts.McpAgents
            if counts.Terminals > 0 then sprintf "💻 %d" counts.Terminals ]
        let label =
          if parts.IsEmpty then sprintf "%d connected" total
          else sprintf "%s" (String.Join(" ", parts))
        do! ssePatchNode ctx (
          Elem.div [ Attr.id "connection-counts"; Attr.class' "meta"; Attr.style "font-size: 0.75rem; margin-top: 4px;" ] [
            Text.raw label
          ])
      | None -> ()
      match getElmRegions () with
      | Some regions -> do! pushRegions ctx regions getPreviousSessions
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
        // Throttle: coalesce rapid state changes into at most one push per 100ms.
        // Without this, rapid evals flood the SSE stream and freeze the browser.
        let mutable dirty = 0
        let pushThrottled () = task {
          if Threading.Interlocked.Exchange(&dirty, 1) = 0 then
            do! Threading.Tasks.Task.Delay(100)
            Threading.Interlocked.Exchange(&dirty, 0) |> ignore
            try
              do! pushState ()
            with
            | :? System.IO.IOException -> ()
            | :? System.ObjectDisposedException -> ()
            | :? OperationCanceledException -> ()
        }
        use _sub = evt.Subscribe(fun _ ->
          Threading.Tasks.Task.Run(fun () -> pushThrottled () :> Threading.Tasks.Task)
          |> ignore)
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
    try
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
        do! ssePatchNode ctx resultHtml
    with
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the reset POST handler.
let createResetHandler
  (resetSession: unit -> Threading.Tasks.Task<string>)
  : HttpHandler =
  fun ctx -> task {
    try
      let! result = resetSession ()
      Response.sseStartResponse ctx |> ignore
      let resultHtml =
        Elem.div [ Attr.id "eval-result" ] [
          Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw (sprintf "Reset: %s" result)
          ]
        ]
      do! ssePatchNode ctx resultHtml
    with
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the session action handler (switch/stop).
let createSessionActionHandler
  (action: string -> Threading.Tasks.Task<string>)
  : string -> HttpHandler =
  fun sessionId ctx -> task {
    try
      let! result = action sessionId
      Response.sseStartResponse ctx |> ignore
      let resultHtml =
        Elem.div [ Attr.id "eval-result" ] [
          Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw result
          ]
        ]
      do! ssePatchNode ctx resultHtml
    with
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create clear-output handler.
let createClearOutputHandler : HttpHandler =
  fun ctx -> task {
    Response.sseStartResponse ctx |> ignore
    let emptyOutput = Elem.div [ Attr.id "output-panel" ] [
      Elem.span [ Attr.class' "meta"; Attr.style "padding: 0.5rem;" ] [ Text.raw "No output yet" ]
    ]
    do! ssePatchNode ctx emptyOutput
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
let evalResultError (msg: string) =
  Elem.div [ Attr.id "eval-result" ] [
    Elem.pre [ Attr.class' "output-line output-error"; Attr.style "margin-top: 0.5rem;" ] [
      Text.raw msg
    ]
  ]

/// Helper: resolve which projects to use from signal data.
/// Priority: manual > .SageFs/config.fsx > auto-discovery
let resolveSessionProjects (dir: string) (manualProjects: string) =
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
    | Some config ->
      match config.Load with
      | Solution path ->
        let full = if Path.IsPathRooted path then path else Path.Combine(dir, path)
        [ full ]
      | Projects paths ->
        paths |> List.map (fun p ->
          if Path.IsPathRooted p then p
          else Path.Combine(dir, p))
      | NoLoad -> []
      | AutoDetect ->
        let discovered = discoverProjects dir
        if not discovered.Solutions.IsEmpty then
          [ Path.Combine(dir, discovered.Solutions.Head) ]
        elif not discovered.Projects.IsEmpty then
          discovered.Projects |> List.map (fun p -> Path.Combine(dir, p))
        else
          []
    | _ ->
      let discovered = discoverProjects dir
      if not discovered.Solutions.IsEmpty then
        [ Path.Combine(dir, discovered.Solutions.Head) ]
      elif not discovered.Projects.IsEmpty then
        discovered.Projects |> List.map (fun p -> Path.Combine(dir, p))
      else
        []

/// Helper: extract a signal by camelCase or kebab-case name.
let getSignalString (doc: System.Text.Json.JsonDocument) (camelCase: string) (kebab: string) =
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
    let combined = Elem.div [ Attr.id "discovered-projects"; Attr.style "margin-top: 0.5rem;" ] [
      note; mainContent
    ]
    do! ssePatchNode ctx combined
  | None ->
    do! ssePatchNode ctx mainContent
}

/// Create the discover-projects POST handler.
let createDiscoverHandler : HttpHandler =
  fun ctx -> task {
    let! doc = Request.getSignalsJson ctx
    let dir = getSignalString doc "newSessionDir" "new-session-dir"
    Response.sseStartResponse ctx |> ignore
    if String.IsNullOrWhiteSpace dir then
      do! ssePatchNode ctx (
        Elem.div [ Attr.id "discovered-projects" ] [
          Elem.span [ Attr.class' "output-line output-error" ] [
            Text.raw "Enter a working directory first"
          ]])
    elif not (Directory.Exists dir) then
      do! ssePatchNode ctx (
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
      do! ssePatchNode ctx (evalResultError "Working directory is required")
    elif not (Directory.Exists dir) then
      do! ssePatchNode ctx (evalResultError (sprintf "Directory not found: %s" dir))
    else
      let projects = resolveSessionProjects dir manualProjects
      if projects.IsEmpty then
        do! ssePatchNode ctx (evalResultError "No projects found. Enter paths manually or check the directory.")
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
        do! ssePatchNode ctx resultHtml
        do! ssePatchNode ctx (Elem.div [ Attr.id "discovered-projects" ] [])
  }

/// JSON SSE stream for TUI clients — pushes regions + model summary as JSON.
let createApiStateHandler
  (getSessionState: unit -> SessionState)
  (getEvalStats: unit -> SageFs.Affordances.EvalStats)
  (getSessionId: unit -> string)
  (getElmRegions: unit -> RenderRegion list option)
  (stateChanged: IEvent<string> option)
  (connectionTracker: ConnectionTracker option)
  : HttpHandler =
  fun ctx -> task {
    ctx.Response.ContentType <- "text/event-stream"
    ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache"
    ctx.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues "keep-alive"

    let sessionId = getSessionId ()
    let clientId = sprintf "tui-%s" (Guid.NewGuid().ToString("N").[..7])
    connectionTracker |> Option.iter (fun t -> t.Register(clientId, Terminal, sessionId))

    let pushJson () = task {
      let state = getSessionState ()
      let stats = getEvalStats ()
      let currentSessionId = getSessionId ()
      let regions =
        match getElmRegions () with
        | Some r ->
          r |> List.map (fun region ->
            {| id = region.Id
               content = region.Content
               cursor = region.Cursor |> Option.map (fun c -> {| line = c.Line; col = c.Col |})
               completions = region.Completions |> Option.map (fun co ->
                 {| items = co.Items; selectedIndex = co.SelectedIndex |}) |})
        | None -> []
      let payload =
        System.Text.Json.JsonSerializer.Serialize(
          {| sessionId = currentSessionId
             sessionState = SessionState.label state
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
          Threading.Tasks.Task.Run(fun () ->
            task {
              try do! pushJson ()
              with _ -> () // Client disconnected or pipe broken — ignore
            } :> Threading.Tasks.Task)
          |> ignore)
        do! tcs.Task
      | None ->
        while not ctx.RequestAborted.IsCancellationRequested do
          try
            do! Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 1.0, ctx.RequestAborted)
            do! pushJson ()
          with
          | :? OperationCanceledException -> ()
          | _ -> () // Pipe broken or write error — ignore
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
        | "setCursorPosition" ->
          match action.value with
          | Some v ->
            let parts = (v : string).Split(',')
            if parts.Length = 2 then
              match System.Int32.TryParse(parts.[0] : string), System.Int32.TryParse(parts.[1] : string) with
              | (true, line), (true, col) -> Some (EditorAction.SetCursorPosition (line, col))
              | _ -> None
            else None
          | None -> None
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
        | "resetSession" -> Some EditorAction.ResetSession
        | "hardResetSession" -> Some EditorAction.HardResetSession
        | "sessionNavUp" -> Some EditorAction.SessionNavUp
        | "sessionNavDown" -> Some EditorAction.SessionNavDown
        | "sessionSelect" -> Some EditorAction.SessionSelect
        | "sessionDelete" -> Some EditorAction.SessionDelete
        | "clearOutput" -> Some EditorAction.ClearOutput
        | "sessionSetIndex" ->
          action.value |> Option.bind (fun s -> match Int32.TryParse(s) with true, i -> Some (EditorAction.SessionSetIndex i) | _ -> None)
        | "sessionCycleNext" -> Some EditorAction.SessionCycleNext
        | "sessionCyclePrev" -> Some EditorAction.SessionCyclePrev
        | "promptChar" ->
          action.value |> Option.bind (fun s -> if s.Length > 0 then Some (EditorAction.PromptChar s.[0]) else None)
        | "promptBackspace" -> Some EditorAction.PromptBackspace
        | "promptConfirm" -> Some EditorAction.PromptConfirm
        | "promptCancel" -> Some EditorAction.PromptCancel
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
  (getEvalStats: unit -> SageFs.Affordances.EvalStats)
  (getSessionId: unit -> string)
  (getSessionWorkingDir: unit -> string)
  (getProjectCount: unit -> int)
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
  (shutdownCallback: (unit -> unit) option)
  (getPreviousSessions: unit -> PreviousSession list)
  : HttpEndpoint list =
  [
    yield get "/dashboard" (FalcoResponse.ofHtml (renderShell version))
    yield get "/dashboard/stream" (createStreamHandler getSessionState getEvalStats getSessionId getSessionWorkingDir getProjectCount getElmRegions stateChanged connectionTracker getPreviousSessions)
    yield post "/dashboard/eval" (createEvalHandler evalCode)
    yield post "/dashboard/reset" (createResetHandler resetSession)
    yield post "/dashboard/hard-reset" (createResetHandler hardResetSession)
    yield post "/dashboard/clear-output" createClearOutputHandler
    yield post "/dashboard/discover-projects" createDiscoverHandler
    // Create session in temp directory
    match createSession with
    | Some handler ->
      yield post "/dashboard/session/create-temp" (fun ctx -> task {
        let tempDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-%s" (Guid.NewGuid().ToString("N").[..7]))
        Directory.CreateDirectory(tempDir) |> ignore
        Response.sseStartResponse ctx |> ignore
        let! result = handler [] tempDir
        match result with
        | Ok msg ->
          dispatch (SageFsMsg.Editor EditorAction.ListSessions)
          do! ssePatchNode ctx (
            Elem.div [ Attr.id "eval-result" ] [
              Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
                Text.raw msg
              ]
            ])
        | Error err ->
          do! ssePatchNode ctx (evalResultError err)
      })
    | None -> ()
    // Resume previous session (re-creates in same working dir)
    match createSession with
    | Some handler ->
      yield mapPost "/dashboard/session/resume/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sessionId -> fun ctx -> task {
          let previous = getPreviousSessions ()
          match previous |> List.tryFind (fun s -> s.Id = sessionId) with
          | Some prev ->
            Response.sseStartResponse ctx |> ignore
            let! result = handler prev.Projects prev.WorkingDir
            match result with
            | Ok msg ->
              dispatch (SageFsMsg.Editor EditorAction.ListSessions)
              do! ssePatchNode ctx (
                Elem.div [ Attr.id "eval-result" ] [
                  Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
                    Text.raw msg
                  ]
                ])
            | Error err ->
              do! ssePatchNode ctx (evalResultError err)
          | None ->
            Response.sseStartResponse ctx |> ignore
            do! ssePatchNode ctx (evalResultError (sprintf "Previous session '%s' not found" sessionId))
        })
    | None -> ()
    // TUI client API
    yield get "/api/state" (createApiStateHandler getSessionState getEvalStats getSessionId getElmRegions stateChanged connectionTracker)
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
    // Daemon info endpoint for client discovery (replaces daemon.json)
    yield get "/api/daemon-info" (fun ctx -> task {
      let startedAt =
        let proc = System.Diagnostics.Process.GetCurrentProcess()
        proc.StartTime.ToUniversalTime()
      do! ctx.Response.WriteAsJsonAsync({|
        pid = Environment.ProcessId
        version = version
        startedAt = startedAt.ToString("o")
        workingDirectory = Environment.CurrentDirectory
      |})
    })
    // Graceful shutdown endpoint
    match shutdownCallback with
    | Some shutdown ->
      yield post "/api/shutdown" (fun ctx -> task {
        do! ctx.Response.WriteAsJsonAsync({| status = "shutting_down" |})
        shutdown ()
      })
    | None -> ()
  ]
