/// ═══════════════════════════════════════════════════════════════════════════════
/// IMMEDIATE-MODE HTML — THE TAO OF DATASTAR
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// This dashboard follows the "Tao of Datastar" philosophy:
///   https://data-star.dev/essays/tao_of_datastar
///
/// CORE PRINCIPLE: The server renders the ENTIRE page from state on every push.
/// One morph. One <div id="main">. Datastar diffs the DOM.
///
/// Think of it as "immediate mode" rendering for HTML — just like a game engine
/// redraws every frame from state, we re-render every dashboard element from the
/// current Elm model on every state change. The server is the source of truth.
/// The client is a thin display layer.
///
/// WHY THIS MATTERS:
/// - No stale fragments: every push is the complete, consistent view
/// - No element-targeting bugs: we don't guess which elements changed
/// - No Datastar PatchElementsNoTargetsFound errors (we bypass element patches)
/// - Trivially correct: if the render function is right, the UI is right
/// - Version, theme, session status, output — ALL update in one atomic morph
///
/// WHAT THIS MEANS IN PRACTICE:
/// - renderMainContent: composes ALL dynamic content into <div id="main">
/// - pushState: calls renderMainContent once, sends one SSE morph
/// - renderShell: provides only the static HTML skeleton (head, scripts, CSS)
///   plus an empty <div id="main"></div> placeholder and the SSE data-init
/// - ALL state flows through the single SSE morph — never add per-element patches
///
/// DO NOT DIVERGE FROM THIS PATTERN. There is no reason to. If you think you
/// need per-element patches, you are wrong. Re-read the Tao of Datastar essay.
/// ═══════════════════════════════════════════════════════════════════════════════
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
open SageFs.Utils
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

module FalcoResponse = Falco.Response

/// Dashboard CSS — loaded from embedded resource at startup.
/// Served via GET /dashboard/dashboard.css with proper caching.
let dashboardCss =
  let asm = System.Reflection.Assembly.GetExecutingAssembly()
  use stream = asm.GetManifestResourceStream("SageFs.dashboard.css")
  use reader = new StreamReader(stream)
  reader.ReadToEnd()


/// Render keyboard shortcut help as an HTML fragment.
// ---------------------------------------------------------------------------
// Inline script blocks — named functions for testability and readability.
// Each returns an XmlNode (Elem.script) for embedding in the shell <head>/<body>.
// ---------------------------------------------------------------------------

/// SSE connection monitor — intercepts fetch to detect stream lifecycle.
/// Shows a banner on failure, polls for recovery.
let connectionMonitorScript () =
  Elem.script [] [ Text.raw (sprintf """
    (function() {
      var origFetch = window.fetch;
      window.fetch = function(url) {
        var p = origFetch.apply(this, arguments);
        if (typeof url === 'string' && url.indexOf('/dashboard/stream') !== -1) {
          var b = document.getElementById('%s');
          p.then(function(resp) {
            if (b) { b.style.display = resp.ok ? 'none' : ''; b.textContent = resp.ok ? '' : '\u274c Server error (' + resp.status + ')'; if (!resp.ok) b.className = 'conn-banner conn-disconnected'; }
          }).catch(function() {
            if (b) { b.className = 'conn-banner conn-disconnected'; b.textContent = '\u274c Server disconnected \u2014 reconnecting...'; b.style.display = ''; }
          });
        }
        return p;
      };
    })();
  """ DomIds.ServerStatus) ]

/// Completion insertion utility — called from server-rendered dropdown items.
/// Inserts text at cursor position, replacing the partial word being typed.
let completionInsertScript () =
  Elem.script [] [ Text.raw (sprintf """
    window._insertComp = function(text, reqPos) {
      var ta = document.getElementById('%s');
      if (!ta) return;
      var pos = ta.selectionStart;
      var before = ta.value.substring(0, pos);
      var wordStart = before.search(/[a-zA-Z0-9_]*$/);
      ta.value = ta.value.substring(0, wordStart) + text + ta.value.substring(pos);
      ta.selectionStart = ta.selectionEnd = wordStart + text.length;
      ta.dispatchEvent(new Event('input'));
      document.getElementById('%s').style.display = 'none';
      ta.focus();
    };
  """ DomIds.EvalTextarea DomIds.CompletionDropdown) ]

/// Auto-scroll output panel to bottom when new content arrives via SSE morph.
let autoScrollScript () =
  Elem.script [] [ Text.raw (sprintf """
    new MutationObserver(function() {
      var panel = document.getElementById('%s');
      if (panel) panel.scrollTop = panel.scrollHeight;
    }).observe(document.getElementById('%s') || document.body, { childList: true, subtree: true });
  """ DomIds.OutputPanel DomIds.Main) ]

/// Details toggle — update arrow indicator when eval section opens/closes.
let detailsToggleScript () =
  Elem.script [] [ Text.raw (sprintf """
    document.addEventListener('toggle', function(e) {
      if (e.target.id !== '%s') return;
      var label = e.target.querySelector('summary span:first-child');
      if (label) label.textContent = e.target.open ? '\u25be Evaluate' : '\u25b8 Evaluate';
    }, true);
  """ DomIds.EvaluateSection) ]

/// Keyboard shortcuts, font-size adjustment, session navigation, sidebar resize.
let keyboardHandlerScript () =
  Elem.script [] [ Text.raw (sprintf """
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
        var tag = (e.target.tagName || '').toLowerCase();
        if (tag !== 'input' && tag !== 'textarea') {
          var action = null;
          var value = null;
          if (e.key === 'j' || e.key === 'ArrowDown') { action = 'sessionNavDown'; }
          if (e.key === 'k' || e.key === 'ArrowUp') { action = 'sessionNavUp'; }
          if (e.key === 'Enter') { action = 'sessionSelect'; }
          if (e.key === 'x' || e.key === 'Delete') { action = 'sessionDelete'; }
          if (e.key === 'X') { action = 'sessionStopOthers'; }
          if (e.key === 'n') { e.preventDefault(); fetch('/dashboard/session/create', {method:'POST'}); return; }
          if (e.key >= '1' && e.key <= '9') { action = 'sessionSetIndex'; value = String(parseInt(e.key) - 1); }
          if (action) {
            e.preventDefault();
            var body = value ? {action: action, value: value} : {action: action};
            fetch('/api/dispatch', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body) });
          }
        }
      });
      var handle = document.getElementById('%s');
      var sidebar = document.getElementById('%s');
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
  """ DomIds.SidebarResize DomIds.Sidebar) ]

/// Render the dashboard HTML shell.
/// Datastar initializes and connects to the /dashboard/stream SSE endpoint.
let renderShell (version: string) =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ Text.raw "SageFs Dashboard" ]
      connectionMonitorScript ()
      Ds.cdnScript
      Elem.link [ Attr.rel "stylesheet"; Attr.href "/dashboard/dashboard.css" ]
    ]
    Elem.body [ Ds.safariStreamingFix ] [
      Elem.div [ Ds.onInit (Ds.get "/dashboard/stream"); Ds.signal (Signals.HelpVisible, "false"); Ds.signal (Signals.SidebarOpen, "true"); Ds.signal (Signals.SessionId, ""); Ds.signal (Signals.Code, ""); Ds.signal (Signals.NewSessionDir, ""); Ds.signal (Signals.ManualProjects, ""); Ds.signal (Signals.Theme, ""); Ds.signal (Signals.CursorPos, "0") ] []
      Elem.div [ Attr.id DomIds.ServerStatus; Attr.class' "conn-banner conn-disconnected"; Attr.style "display:none" ] [
        Text.raw "⏳ Connecting to server..."
      ]
      Elem.div [ Attr.id DomIds.Main ] [
        Elem.div [ Attr.style "display: flex; align-items: center; justify-content: center; height: 100vh; color: var(--fg-dim);" ] [
          Text.raw "⏳ Loading dashboard..."
        ]
      ]
      completionInsertScript ()
      autoScrollScript ()
      detailsToggleScript ()
      keyboardHandlerScript ()
    ]
  ]

/// Create the SSE stream handler that pushes Elm state to the browser.
let createStreamHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  : HttpHandler =
  fun ctx -> task {
    SageFs.Instrumentation.sseConnectionsActive.Add(1L)
    Response.sseStartResponse ctx |> ignore

    let clientId = Guid.NewGuid().ToString("N").[..7]
    // Resolve initial session: first available session (observer behavior — don't create)
    let! sessions = q.GetAllSessions ()
    let mutable currentSessionId =
      sessions |> List.tryHead |> Option.map (fun s -> s.Id) |> Option.defaultValue ""
    infra.ConnectionTracker |> Option.iter (fun t -> t.Register(clientId, Browser, currentSessionId))
    let mutable lastSessionId = ""
    let mutable lastWorkingDir = ""
    let mutable lastOutputHash = 0
    let mutable lastThemeName = defaultThemeName

    let pushState () = task {
      // === FULL-PAGE MORPH: "The Tao of Datastar" ===
      // Build a complete DashboardSnapshot from current state, render it all
      // into one <div id="main">, and push a single SSE morph. Datastar diffs.
      // See module-level doc comment for philosophy.

      // Track daemon's active session for theme switching
      let activeId = q.GetActiveSessionId ()
      match activeId.Length > 0 with
      | true -> currentSessionId <- activeId
      | false -> ()
      let state = q.GetSessionState currentSessionId
      let stateStr = SessionState.label state
      let workingDir = q.GetSessionWorkingDir currentSessionId
      // Parallelize all worker HTTP fetches — prevents sequential 2s+ timeouts
      let statsTask = q.GetEvalStats currentSessionId
      let hrTask = q.GetHotReloadState currentSessionId
      let wCtxTask = q.GetWarmupContext currentSessionId
      do! System.Threading.Tasks.Task.WhenAll(statsTask, hrTask, wCtxTask)
      let stats : SageFs.Affordances.EvalStats = statsTask.Result
      let hrState = hrTask.Result
      let wCtx = wCtxTask.Result
      // Push sessionId signal so eval form can include it
      do! Response.ssePatchSignal ctx (SignalPath.sp "sessionId") currentSessionId
      let avgMs =
        match stats.EvalCount > 0 with
        | true -> stats.TotalDuration.TotalMilliseconds / float stats.EvalCount
        | false -> 0.0
      // Resolve theme
      let themeName =
        match resolveThemePush infra.SessionThemes currentSessionId workingDir lastSessionId lastWorkingDir with
        | Some name -> lastThemeName <- name; name
        | None -> lastThemeName
      lastSessionId <- currentSessionId
      lastWorkingDir <- workingDir
      // Build connection label
      let connectionLabel =
        match infra.ConnectionTracker with
        | Some tracker ->
          let counts = tracker.GetAllCounts()
          let parts =
            [ match counts.Browsers > 0 with | true -> sprintf "🌐 %d" counts.Browsers | false -> ()
              match counts.McpAgents > 0 with | true -> sprintf "🤖 %d" counts.McpAgents | false -> ()
              match counts.Terminals > 0 with | true -> sprintf "💻 %d" counts.Terminals | false -> () ]
          match parts.IsEmpty with
          | true -> Some (sprintf "%d connected" tracker.TotalCount)
          | false -> Some (String.Join(" ", parts))
        | None -> None
      // Build hot-reload panel
      let hrPanel =
        match currentSessionId.Length > 0 with
        | true ->
          match hrState with
          | Some hr -> renderHotReloadPanel currentSessionId hr.files hr.watchedCount
          | None -> renderHotReloadEmpty
        | false -> renderHotReloadEmpty
      // Build session context panel
      let scPanel =
        match currentSessionId.Length > 0 with
        | true ->
          match wCtx with
          | Some ctx' ->
            let fileStatuses =
              match hrState with
              | Some hr ->
                hr.files |> List.map (fun f ->
                  let readiness =
                    ctx'.NamespacesOpened
                    |> List.exists (fun b -> f.path.EndsWith(b.Name, StringComparison.OrdinalIgnoreCase))
                    |> fun loaded -> match loaded with | true -> FileReadiness.Loaded | false -> FileReadiness.NotLoaded
                  { Path = f.path; Readiness = readiness; LastLoadedAt = None; IsWatched = f.watched })
              | None -> []
            renderSessionContextPanel
              { SessionId = currentSessionId
                ProjectNames = []
                WorkingDir = q.GetSessionWorkingDir currentSessionId
                Status = SessionState.label (q.GetSessionState currentSessionId)
                Warmup = ctx'
                FileStatuses = fileStatuses }
          | None -> renderSessionContextEmpty
        | false -> renderSessionContextEmpty
      // Build bindings explorer panel
      let bindingsPanel = renderBindingsPanel (q.GetBindingScopeSnapshot ())
      // Build output + sessions from Elm regions
      let! outputPanel, sessionsPanel, sessionPicker = task {
        match q.GetElmRegions () with
        | Some regions ->
          let outputRegion = regions |> List.tryFind (fun r -> r.Id = "output")
          let outputHash = outputRegion |> Option.map (fun r -> r.Content.GetHashCode()) |> Option.defaultValue 0
          let outNode =
            match outputRegion with
            | Some r -> renderOutput (parseOutputLines r.Content)
            | None -> renderOutput []
          lastOutputHash <- outputHash
          let sessRegion = regions |> List.tryFind (fun r -> r.Id = "sessions")
          match sessRegion with
          | Some r ->
            let parsed = parseSessionLines r.Content
            let corrected = overrideSessionStatuses q.GetSessionState q.GetStatusMsg parsed
            let visible =
              corrected
              |> List.filter (fun s -> s.Status <> "stopped")
              |> List.map (fun s ->
                let info = q.GetSessionStandbyInfo s.Id
                let testSummary = q.GetSessionTestSummary s.Id
                let coverageSummary = q.GetSessionCoverageSummary s.Id
                let treemapEntries = q.GetSessionTestTreemap s.Id
                let bindingEntries = q.GetSessionBindings s.Id
                { s with
                    StandbyLabel = StandbyInfo.label info
                    TestSummary = testSummary
                    CoverageSummary = coverageSummary
                    TestTreemapEntries = treemapEntries
                    BindingEntries = bindingEntries })
            let creating = isCreatingSession r.Content
            let sess = renderSessions visible creating
            let! pick =
              match visible.IsEmpty && not creating with
              | true ->
                task {
                  let! previous = q.GetPreviousSessions ()
                  return renderSessionPicker previous
                }
              | false -> task { return renderSessionPickerEmpty }
            return (outNode, sess, pick)
          | None ->
            return (outNode, renderSessions [] false, renderSessionPickerEmpty)
        | None ->
          return (renderOutput [], renderSessions [] false, renderSessionPickerEmpty)
      }
      // Compose everything into one snapshot and push ONE morph
      let snap : DashboardSnapshot = {
        Version = infra.Version
        SessionState = stateStr
        SessionId = currentSessionId
        WorkingDir = workingDir
        WarmupProgress = q.GetWarmupProgress currentSessionId
        EvalStats =
          { Count = stats.EvalCount
            AvgMs = avgMs
            MinMs = stats.MinDuration.TotalMilliseconds
            MaxMs = stats.MaxDuration.TotalMilliseconds }
        ThemeName = themeName
        ConnectionLabel = connectionLabel
        HotReloadPanel = hrPanel
        SessionContextPanel = scPanel
        OutputPanel = outputPanel
        SessionsPanel = sessionsPanel
        SessionPicker = sessionPicker
        ThemePicker = renderThemePicker themeName
        ThemeVars = renderThemeVars themeName
        BindingsPanel = bindingsPanel
      }
      do! ssePatchNode ctx (renderMainContent snap)
    }

    try
      // Push initial state (catch all exceptions — don't let a transient failure kill the stream)
      try
        do! pushState ()
      with ex ->
        Log.error "[Dashboard SSE] Initial pushState failed: %s" ex.Message

      match infra.StateChanged with
      | Some evt ->
        let tcs = Threading.Tasks.TaskCompletionSource()
        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
        // Serialize SSE writes via MailboxProcessor — no locks, no mutable state.
        // Coalesces rapid state changes: drain queued, throttle 100ms, drain again, push once.
        // Heartbeat: when idle >15s, sends `: keepalive\n\n` SSE comment to prevent
        // proxy/browser timeouts. Integrated into the actor loop to avoid concurrent writes.
        let pushAgent = MailboxProcessor.Start((fun inbox ->
          let rec loop () = async {
            let! msg = inbox.TryReceive(15_000)
            match msg with
            | None ->
              // Idle timeout — send SSE keepalive comment
              try
                let bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n\n")
                do! ctx.Response.Body.AsyncWrite(bytes, 0, bytes.Length)
                do! ctx.Response.Body.FlushAsync() |> Async.AwaitTask
              with
              | :? System.IO.IOException -> ()
              | :? ObjectDisposedException -> ()
              | :? OperationCanceledException -> ()
              | :? System.ArgumentOutOfRangeException -> ()
              | :? System.InvalidOperationException -> ()
              return! loop ()
            | Some () ->
              // Got a state change — drain + coalesce + push
              while inbox.CurrentQueueLength > 0 do
                do! inbox.Receive()
              do! Async.Sleep 100
              while inbox.CurrentQueueLength > 0 do
                do! inbox.Receive()
              try
                do! pushState () |> Async.AwaitTask
              with
              | :? System.IO.IOException -> ()
              | :? ObjectDisposedException -> ()
              | :? OperationCanceledException -> ()
              | :? System.ArgumentOutOfRangeException -> ()
              | :? System.InvalidOperationException -> ()
              | ex -> Log.debug "[Dashboard SSE] pushState failed: %s" ex.Message
              return! loop ()
          }
          loop ()), ctx.RequestAborted)
        use _sub = evt.Subscribe(fun _ ->
          try pushAgent.Post(())
          with :? ObjectDisposedException -> ())
        do! tcs.Task
      | None ->
        // Fallback: poll every second
        while not ctx.RequestAborted.IsCancellationRequested do
          try
            do! Threading.Tasks.Task.Delay(Timeouts.sseEventInterval, ctx.RequestAborted)
            do! pushState ()
          with
          | :? OperationCanceledException -> ()
    finally
      SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
      infra.ConnectionTracker |> Option.iter (fun t -> t.Unregister(clientId))
  }

/// Create the eval POST handler.
let createEvalHandler
  (evalCode: string -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = Request.getSignalsJson ctx
      let code =
        match doc.RootElement.TryGetProperty("code") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let sessionId =
        match doc.RootElement.TryGetProperty("sessionId") with
        | true, prop -> prop.GetString()
        | _ -> ""
      match String.IsNullOrWhiteSpace code with
      | true ->
        Response.sseStartResponse ctx |> ignore
        do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
      | false ->
        let codeWithTerminator =
          let trimmed = code.TrimEnd()
          match trimmed.EndsWith(";;") with
          | true -> code
          | false -> sprintf "%s;;" trimmed
        let! result = evalCode sessionId codeWithTerminator
        Response.sseStartResponse ctx |> ignore
        do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
        let displayResult, cssClass =
          match result with
          | Ok msg -> msg, "output-line output-result"
          | Error err ->
            err
              .Replace("FSharp.Compiler.Interactive.Shell+FsiCompilationException: ", "")
              .Replace("Evaluation failed: ", "⚠ "),
            "output-line output-error"
        let resultHtml =
          Elem.div [ Attr.id DomIds.EvalResult ] [
            Elem.pre [ Attr.class' cssClass; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
              Text.raw displayResult
            ]
          ]
        do! ssePatchNode ctx resultHtml
    with
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the eval-file POST handler (reads file, evals its content).
let createEvalFileHandler
  (evalCode: string -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      use doc = System.Text.Json.JsonDocument.Parse(body)
      let filePath =
        match doc.RootElement.TryGetProperty("path") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let sessionId =
        match doc.RootElement.TryGetProperty("sessionId") with
        | true, prop -> prop.GetString()
        | _ -> ""
      match String.IsNullOrWhiteSpace filePath || not (File.Exists filePath) with
      | true ->
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = "File not found or path missing" |})
      | false ->
        let! code = File.ReadAllTextAsync(filePath)
        let codeWithTerminator =
          let trimmed = code.TrimEnd()
          match trimmed.EndsWith(";;") with
          | true -> code
          | false -> sprintf "%s;;" trimmed
        let! result = evalCode sessionId codeWithTerminator
        match result with
        | Ok msg -> do! ctx.Response.WriteAsJsonAsync({| success = true; result = msg |})
        | Error err ->
          ctx.Response.StatusCode <- 422
          do! ctx.Response.WriteAsJsonAsync({| success = false; error = err |})
    with ex ->
      ctx.Response.StatusCode <- 500
      do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
  }
let createCompletionsHandler
  (getCompletions: string -> string -> int -> Threading.Tasks.Task<Features.AutoCompletion.CompletionItem list>)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = Request.getSignalsJson ctx
      let code =
        match doc.RootElement.TryGetProperty(Signals.Code) with
        | true, prop -> prop.GetString()
        | _ -> ""
      let cursorPos =
        match doc.RootElement.TryGetProperty(Signals.CursorPos) with
        | true, prop ->
          match prop.ValueKind with
          | System.Text.Json.JsonValueKind.Number -> prop.GetInt32()
          | System.Text.Json.JsonValueKind.String ->
            match System.Int32.TryParse(prop.GetString()) with
            | true, v -> v
            | false, _ -> -1
          | _ -> -1
        | _ -> -1
      let sessionId =
        match doc.RootElement.TryGetProperty(Signals.SessionId) with
        | true, prop -> prop.GetString()
        | _ -> ""
      Response.sseStartResponse ctx |> ignore
      match String.IsNullOrWhiteSpace code || cursorPos < 0 with
      | true ->
        do! ssePatchNode ctx (renderCompletionDropdown [] 0)
      | false ->
        let! items = getCompletions sessionId code cursorPos
        do! ssePatchNode ctx (renderCompletionDropdown items cursorPos)
    with ex ->
      ctx.Response.StatusCode <- 500
      do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
  }

/// Create the reset POST handler.
let createResetHandler
  (resetSession: string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      let! sessionId = task {
        try
          use! doc = Request.getSignalsJson ctx
          match doc.RootElement.TryGetProperty("sessionId") with
          | true, prop -> return prop.GetString()
          | _ -> return ""
        with _ -> return ""
      }
      let! result = resetSession sessionId
      Response.sseStartResponse ctx |> ignore
      let msg =
        match result with
        | Ok m -> m
        | Error e -> sprintf "Failed: %s" e
      let resultHtml =
        Elem.div [ Attr.id DomIds.EvalResult ] [
          Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw (sprintf "Reset: %s" msg)
          ]
        ]
      do! ssePatchNode ctx resultHtml
      // Clear stale output after reset (Bug #5)
      let clearedOutput =
        Elem.div [ Attr.id DomIds.OutputPanel ] [
          Elem.span [ Attr.class' "meta"; Attr.style "padding: 0.5rem;" ] [
            Text.raw (sprintf "Reset: %s" msg)
          ]
        ]
      do! ssePatchNode ctx clearedOutput
    with
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the session action handler (switch/stop).
let createSessionActionHandler
  (action: string -> Threading.Tasks.Task<Result<string, string>>)
  : string -> HttpHandler =
  fun sessionId ctx -> task {
    try
      let! result = action sessionId
      Response.sseStartResponse ctx |> ignore
      // Push sessionId so eval form targets the new session
      do! Response.ssePatchSignal ctx (SignalPath.sp "sessionId") sessionId
      let msg, cssClass =
        match result with
        | Ok m -> m, "output-line output-info"
        | Error e -> e, "output-line output-error"
      let resultHtml =
        Elem.div [ Attr.id DomIds.EvalResult ] [
          Elem.pre [ Attr.class' cssClass; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw msg
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
    let emptyOutput = Elem.div [ Attr.id DomIds.OutputPanel ] [
      Elem.span [ Attr.class' "meta"; Attr.style "padding: 0.5rem;" ] [ Text.raw "No output yet" ]
    ]
    do! ssePatchNode ctx emptyOutput
  }


/// Create the discover-projects POST handler.
let createDiscoverHandler : HttpHandler =
  fun ctx -> task {
    use! doc = Request.getSignalsJson ctx
    let dir = getSignalString doc "newSessionDir" "new-session-dir"
    Response.sseStartResponse ctx |> ignore
    match String.IsNullOrWhiteSpace dir, Directory.Exists dir with
    | true, _ ->
      do! ssePatchNode ctx (
        Elem.div [ Attr.id DomIds.DiscoveredProjects ] [
          Elem.span [ Attr.class' "output-line output-error" ] [
            Text.raw "Enter a working directory first"
          ]])
    | false, false ->
      do! ssePatchNode ctx (
        Elem.div [ Attr.id DomIds.DiscoveredProjects ] [
          Elem.span [ Attr.class' "output-line output-error" ] [
            Text.raw (sprintf "Directory not found: %s" dir)
          ]])
    | false, true ->
      do! pushDiscoverResults ctx dir
  }

/// Create the create-session POST handler.
let createCreateSessionHandler
  (createSession: string list -> string -> Threading.Tasks.Task<Result<string, string>>)
  (switchSession: (string -> Threading.Tasks.Task<Result<string, string>>) option)
  : HttpHandler =
  fun ctx -> task {
    use! doc = Request.getSignalsJson ctx
    let dir = getSignalString doc "newSessionDir" "new-session-dir"
    let manualProjects = getSignalString doc "manualProjects" "manual-projects"
    Response.sseStartResponse ctx |> ignore
    match String.IsNullOrWhiteSpace dir, Directory.Exists dir with
    | true, _ ->
      do! ssePatchNode ctx (evalResultError "Working directory is required")
    | false, false ->
      do! ssePatchNode ctx (evalResultError (sprintf "Directory not found: %s" dir))
    | false, true ->
      let projects = resolveSessionProjects dir manualProjects
      match projects.IsEmpty with
      | true ->
        do! ssePatchNode ctx (evalResultError "No projects found. Enter paths manually or check the directory.")
      | false ->
        let! result = createSession projects dir
        match result with
        | Ok newSessionId ->
          // Switch to the new session so the SSE stream picks it up
          match switchSession with
          | Some switch -> let! _ = switch newSessionId in ()
          | None -> ()
          // Push the new session's ID so the eval form targets it
          do! Response.ssePatchSignal ctx (SignalPath.sp "sessionId") newSessionId
          do! ssePatchNode ctx (
            Elem.div [ Attr.id DomIds.EvalResult ] [
              Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem;" ] [
                Text.raw (sprintf "Session '%s' created. Switched to it." newSessionId)
              ]
            ])
        | Error msg ->
          do! ssePatchNode ctx (evalResultError (sprintf "Failed: %s" msg))
        do! ssePatchNode ctx (Elem.div [ Attr.id DomIds.DiscoveredProjects ] [])
  }

/// JSON SSE stream for TUI clients — pushes regions + model summary as JSON.
let createApiStateHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  : HttpHandler =
  fun ctx -> task {
    SageFs.Instrumentation.sseConnectionsActive.Add(1L)
    ctx.Response.ContentType <- "text/event-stream"
    ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache"
    ctx.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues "keep-alive"

    // Each SSE connection tracks its own session via query param
    let! sessions = q.GetAllSessions ()
    let defaultSid = sessions |> List.tryHead |> Option.map (fun s -> s.Id) |> Option.defaultValue ""
    let connSessionId =
      match ctx.Request.Query.TryGetValue("sessionId") with
      | true, v when v.Count > 0 && not (String.IsNullOrEmpty(v.[0])) -> v.[0]
      | _ -> defaultSid
    let clientId = sprintf "tui-%s" (Guid.NewGuid().ToString("N").[..7])
    infra.ConnectionTracker |> Option.iter (fun t -> t.Register(clientId, Terminal, connSessionId))

    let pushJson () = task {
      let activeSid = q.GetActiveSessionId ()
      let activeDir = q.GetSessionWorkingDir activeSid
      let state = q.GetSessionState activeSid
      let! (stats : SageFs.Affordances.EvalStats) = q.GetEvalStats activeSid
      let regions =
        match q.GetElmRegions () with
        | Some r ->
          r |> List.map (fun region ->
            {| id = region.Id
               content = region.Content
               cursor = region.Cursor |> Option.map (fun c -> {| line = c.Line; col = c.Col |})
               completions = region.Completions |> Option.map (fun co ->
                 {| items = co.Items; selectedIndex = co.SelectedIndex |})
               lineAnnotations =
                 region.LineAnnotations |> Array.map (fun a ->
                   {| line = a.Line
                      icon = SageFs.Features.LiveTesting.GutterIcon.toLabel a.Icon
                      tooltip = a.Tooltip |}) |})
        | None -> []
      let! standby = q.GetStandbyInfo ()
      let liveTestingStatus = q.GetLiveTestingStatus ()
      let payload =
        System.Text.Json.JsonSerializer.Serialize(
          {| sessionId = activeSid
             sessionState = SessionState.label state
             evalCount = stats.EvalCount
             avgMs = if stats.EvalCount > 0 then stats.TotalDuration.TotalMilliseconds / float stats.EvalCount else 0.0
             activeWorkingDir = activeDir
             standbyLabel = StandbyInfo.label standby
             liveTestingStatus = liveTestingStatus
             regions = regions |})
      do! ctx.Response.WriteAsync(sprintf "data: %s\n\n" payload)
      do! ctx.Response.Body.FlushAsync()
    }

    try
      do! pushJson ()
      match infra.StateChanged with
      | Some evt ->
        let tcs = Threading.Tasks.TaskCompletionSource()
        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
        // Serialize SSE writes via MailboxProcessor — matches Datastar handler pattern.
        // Coalesces rapid state changes: drain queued, throttle 100ms, drain again, push once.
        // Heartbeat: when idle >15s, sends `: keepalive\n\n` SSE comment.
        let pushAgent = MailboxProcessor.Start((fun inbox ->
          let rec loop () = async {
            let! msg = inbox.TryReceive(15_000)
            match msg with
            | None ->
              try
                let bytes = Text.Encoding.UTF8.GetBytes(": keepalive\n\n")
                do! ctx.Response.Body.AsyncWrite(bytes, 0, bytes.Length)
                do! ctx.Response.Body.FlushAsync() |> Async.AwaitTask
              with
              | :? System.IO.IOException | :? ObjectDisposedException -> ()
              | :? OperationCanceledException -> ()
              | :? System.ArgumentOutOfRangeException | :? System.InvalidOperationException -> ()
              return! loop ()
            | Some () ->
              while inbox.CurrentQueueLength > 0 do
                do! inbox.Receive()
              do! Async.Sleep 100
              while inbox.CurrentQueueLength > 0 do
                do! inbox.Receive()
              try
                do! pushJson () |> Async.AwaitTask
              with
              | :? System.IO.IOException | :? ObjectDisposedException -> ()
              | :? OperationCanceledException -> ()
              | :? System.ArgumentOutOfRangeException | :? System.InvalidOperationException -> ()
              | ex -> Log.debug "[dashboard] Push error: %s" ex.Message
              return! loop ()
          }
          loop ()), ctx.RequestAborted)
        use _sub = evt.Subscribe(fun _ ->
          try pushAgent.Post(())
          with :? ObjectDisposedException -> ())
        do! tcs.Task
      | None ->
        while not ctx.RequestAborted.IsCancellationRequested do
          try
            do! Threading.Tasks.Task.Delay(Timeouts.sseEventInterval, ctx.RequestAborted)
            do! pushJson ()
          with
          | :? OperationCanceledException -> ()
          | _ -> () // Pipe broken or write error — ignore
    finally
      SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
      infra.ConnectionTracker |> Option.iter (fun t -> t.Unregister(clientId))
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
      let editorAction = parseEditorAction action.action action.value
      let appMsg = parseAppMsg action.action editorAction
      match appMsg with
      | Some msg ->
        dispatch msg
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
  (q: DashboardQueries)
  (a: DashboardActions)
  (infra: DashboardInfra)
  : HttpEndpoint list =
  [
    // Static CSS — served from embedded resource with aggressive caching
    yield get "/dashboard/dashboard.css" (fun ctx -> task {
      ctx.Response.ContentType <- "text/css; charset=utf-8"
      ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "public, max-age=31536000, immutable"
      do! ctx.Response.WriteAsync(dashboardCss)
    })
    yield get "/dashboard" (FalcoResponse.ofHtml (renderShell infra.Version))
    yield get "/dashboard/stream" (createStreamHandler q infra)
    yield post "/dashboard/eval" (createEvalHandler a.EvalCode)
    yield post "/dashboard/eval-file" (createEvalFileHandler a.EvalCode)
    match infra.GetCompletions with
    | Some gc -> yield post "/dashboard/completions" (createCompletionsHandler gc)
    | None -> ()
    yield post "/dashboard/reset" (createResetHandler a.ResetSession)
    yield post "/dashboard/hard-reset" (createResetHandler a.HardResetSession)
    yield post "/dashboard/clear-output" createClearOutputHandler
    yield post "/dashboard/discover-projects" createDiscoverHandler
    yield post "/dashboard/set-theme" (fun ctx -> task {
      try
        use! doc = Request.getSignalsJson ctx
        let theme =
          match doc.RootElement.TryGetProperty(Signals.Theme) with
          | true, prop -> prop.GetString()
          | _ -> ""
        let activeId = q.GetActiveSessionId ()
        let workingDir = q.GetSessionWorkingDir activeId
        match workingDir.Length > 0 && theme.Length > 0 with
        | true ->
          infra.SessionThemes.[workingDir] <- theme
          saveThemes DaemonState.SageFsDir infra.SessionThemes
        | false -> ()
        // Respond with SSE morph — Datastar @post expects SSE, not JSON.
        // Push updated theme vars so the page re-styles immediately.
        Response.sseStartResponse ctx |> ignore
        do! ssePatchNode ctx (renderThemeVars theme)
        do! ssePatchNode ctx (renderThemePicker theme)
      with ex ->
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
    })
    // Create session in temp directory
    match a.CreateSession with
    | Some handler ->
      yield post "/dashboard/session/create-temp" (fun ctx -> task {
        let tempDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-%s" (Guid.NewGuid().ToString("N").[..7]))
        Directory.CreateDirectory(tempDir) |> ignore
        Response.sseStartResponse ctx |> ignore
        let! result = handler [] tempDir
        match result with
        | Ok msg ->
          a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
          do! ssePatchNode ctx (
            Elem.div [ Attr.id DomIds.EvalResult ] [
              Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
                Text.raw msg
              ]
            ])
        | Error err ->
          do! ssePatchNode ctx (evalResultError err)
      })
    | None -> ()
    // Resume previous session (re-creates in same working dir)
    match a.CreateSession with
    | Some handler ->
      yield mapPost "/dashboard/session/resume/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sessionId -> fun ctx -> task {
          let! previous = q.GetPreviousSessions ()
          match previous |> List.tryFind (fun s -> s.Id = sessionId) with
          | Some prev ->
            Response.sseStartResponse ctx |> ignore
            let! result = handler prev.Projects prev.WorkingDir
            match result with
            | Ok msg ->
              a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
              do! ssePatchNode ctx (
                Elem.div [ Attr.id DomIds.EvalResult ] [
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
    yield get "/api/state" (createApiStateHandler q infra)
    yield post "/api/dispatch" (createApiDispatchHandler a.Dispatch)
    match a.CreateSession with
    | Some handler ->
      yield post "/dashboard/session/create" (createCreateSessionHandler handler a.SwitchSession)
    | None -> ()
    match a.SwitchSession with
    | Some handler ->
      yield mapPost "/dashboard/session/switch/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sid -> createSessionActionHandler handler sid)
    | None -> ()
    match a.StopSession with
    | Some handler ->
      yield mapPost "/dashboard/session/stop/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sid -> createSessionActionHandler handler sid)
      yield post "/dashboard/session/stop-others" (fun ctx -> task {
        let! sessions = q.GetAllSessions ()
        let activeId = q.GetActiveSessionId ()
        let others =
          sessions
          |> List.filter (fun (s: WorkerProtocol.SessionInfo) -> s.Id <> activeId)
        for s in others do
          let! _ = handler s.Id
          ()
        a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
        Response.sseStartResponse ctx |> ignore
        let resultHtml =
          Elem.div [ Attr.id DomIds.EvalResult ] [
            Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
              Text.raw (sprintf "Stopped %d other session(s)" others.Length)
            ]
          ]
        do! ssePatchNode ctx resultHtml
      })
    | None -> ()
    // Daemon info endpoint for client discovery (replaces daemon.json)
    yield get "/api/daemon-info" (fun ctx -> task {
      let startedAt =
        let proc = System.Diagnostics.Process.GetCurrentProcess()
        proc.StartTime.ToUniversalTime()
      do! ctx.Response.WriteAsJsonAsync({|
        pid = Environment.ProcessId
        version = infra.Version
        startedAt = startedAt.ToString("o")
        workingDirectory = Environment.CurrentDirectory
      |})
    })
    // Graceful shutdown endpoint
    match a.ShutdownCallback with
    | Some shutdown ->
      yield post "/api/shutdown" (fun ctx -> task {
        do! ctx.Response.WriteAsJsonAsync({| status = "shutting_down" |})
        shutdown ()
      })
    | None -> ()
  ]
