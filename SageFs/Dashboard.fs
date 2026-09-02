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
open System.Net.Http
open System.Security.Cryptography
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
open SageFs.Features.FrictionSqlite

module FalcoResponse = Falco.Response

/// Dashboard CSS — loaded from embedded resource at startup.
/// Served via GET /dashboard/dashboard.css with proper caching.
let dashboardCss =
  let asm = System.Reflection.Assembly.GetExecutingAssembly()
  use stream = asm.GetManifestResourceStream("SageFs.dashboard.css")
  use reader = new StreamReader(stream)
  reader.ReadToEnd()

/// Pinned Datastar client bundle — loaded from the embedded resource and
/// served by the daemon at /dashboard/datastar.js. WHY pinned + self-hosted:
/// the dashboard previously fetched starfederation/datastar@develop from a
/// CDN at runtime — a moving, unversioned branch (supply-chain + XSS
/// surface, and a version skew breaks every open dashboard tab). The bytes
/// below are the exact @develop bundle the dashboard was built against,
/// frozen in-repo (SHA-256 5D6B7794A50A83D82DA962AEC5E382F5AE83AC7AFBC751F
/// 903F7A9C6BD433C65). Upgrading Datastar is a separate, testable change.
let datastarBundle =
  let asm = System.Reflection.Assembly.GetExecutingAssembly()
  use stream = asm.GetManifestResourceStream("SageFs.datastar.js")
  use reader = new StreamReader(stream)
  reader.ReadToEnd()

let private bindingSnapshotFromEntries
  (bindings: SageFs.Features.BindingExplorer.BindingInfo array)
  : SageFs.Features.BindingExplorer.BindingScopeSnapshot option =
  match bindings.Length with
  | 0 -> None
  | _ ->
    let bindingList = bindings |> Array.toList
    Some
      { Bindings = bindingList
        ActiveBindings = bindingList |> List.map (fun binding -> binding.Name, binding) |> Map.ofList
        ShadowedBindings = [] }

let resolveBindingsPanelSnapshot
  (sharedSnapshot: SageFs.Features.BindingExplorer.BindingScopeSnapshot option)
  (sessionBindings: SageFs.Features.BindingExplorer.BindingInfo array)
  : SageFs.Features.BindingExplorer.BindingScopeSnapshot option =
  let sessionSnapshot = bindingSnapshotFromEntries sessionBindings
  match sharedSnapshot, sessionSnapshot with
  | Some scope, Some fallback when scope.ActiveBindings.Count = 0 -> Some fallback
  | Some scope, _ -> Some scope
  | None, fallback -> fallback


/// Render keyboard shortcut help as an HTML fragment.
// ---------------------------------------------------------------------------
// Inline script blocks — named functions for testability and readability.
// Each returns an XmlNode (Elem.script) for embedding in the shell <head>/<body>.
// ---------------------------------------------------------------------------

/// SSE connection monitor — intercepts fetch to detect stream lifecycle.
/// Shows a banner on SSE fetch failure. The Datastar SSE client uses
/// EventSource (not fetch), so this probe is only a secondary signal.
/// The primary connection-health signal is the MutationObserver on #main:
/// every successful SSE push replaces the main content, which clears the
/// banner.
let connectionMonitorScript () =
  Elem.script [] [ Text.raw (sprintf """
    (function(){
      var b=document.getElementById('%s');
      var m=document.getElementById('main');
      if(b&&m){new MutationObserver(function(){b.style.display='none';}).observe(m,{childList:true});}
      var f=window.fetch;window.fetch=function(u){var p=f.apply(this,arguments);
      if(typeof u==='string'&&u.indexOf('/dashboard/stream')!==-1){
        p.then(function(r){if(b){if(r.ok){b.style.display='none';}else{b.textContent='\u274c Server error ('+r.status+')';b.style.display='';}}})
         .catch(function(){if(b){b.textContent='\u274c Server disconnected \u2014 reconnecting...';b.style.display='';}});}
      return p;};
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
    new MutationObserver(function(){var p=document.getElementById('%s');if(p)p.scrollTop=p.scrollHeight;})
      .observe(document.getElementById('%s')||document.body,{childList:true,subtree:true});
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
      var h = document.getElementById('%s');
      var s = document.getElementById('%s');
      if (h && s) {
        // Persist via CSS var on <html> — Datastar morphs #main (incl. sidebar) on every change, wiping inline styles.
        var K = 'sagefs.sidebarWidth';
        function setW(w) { document.documentElement.style.setProperty('--sidebar-width', w + 'px'); }
        try { var v = localStorage.getItem(K); if (v) { var n = parseInt(v, 10); if (n >= 200 && n <= 600) setW(n); } } catch (e) {}
        var d = false, raf = null, pending = 0;
        h.addEventListener('mousedown', function(e) { d = true; e.preventDefault(); });
        document.addEventListener('mousemove', function(e) {
          if (!d) return;
          pending = Math.max(200, Math.min(600, window.innerWidth - e.clientX));
          if (!raf) raf = requestAnimationFrame(function() { setW(pending); raf = null; });
        });
        document.addEventListener('mouseup', function() {
          if (d) { d = false;
            try { localStorage.setItem(K, document.documentElement.style.getPropertyValue('--sidebar-width').replace('px','').trim()); } catch (e) {}
          }
        });
      }
    })();
  """ DomIds.SidebarResize DomIds.Sidebar) ]

/// Render the dashboard HTML shell with pre-rendered initial content.
/// Providing initialContent eliminates the loading-screen flash — the browser
/// receives a complete first paint without needing an SSE round-trip first.
let renderShell (version: string) (initialSessionId: string) (initialContent: XmlNode) =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ Text.raw "SageFs Dashboard" ]
      connectionMonitorScript ()
      // Self-hosted pinned Datastar bundle (see `datastarBundle`) — never
      // fetch a moving CDN branch at runtime.
      Elem.script [ Attr.type' "module"; Attr.src "/dashboard/datastar.js" ] []
      Elem.link [ Attr.rel "preconnect"; Attr.href "https://fonts.googleapis.com" ]
      Elem.link [ Attr.rel "preconnect"; Attr.href "https://fonts.gstatic.com"; Attr.create "crossorigin" "" ]
      Elem.link [ Attr.rel "stylesheet"; Attr.href "https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;500;700&display=swap" ]
      Elem.link [ Attr.rel "stylesheet"; Attr.href "/dashboard/dashboard.css" ]
    ]
    Elem.body [ Ds.safariStreamingFix ] [
      Elem.div [ Ds.onInit (Ds.get (sprintf "/dashboard/stream?session=%s" (Uri.EscapeDataString initialSessionId))); Ds.signal (Signals.HelpVisible, "false"); Ds.signal (Signals.SidebarOpen, "true"); Ds.signal (Signals.ViewingSessionId, initialSessionId); Ds.signal (Signals.Code, ""); Ds.signal (Signals.NewSessionDir, ""); Ds.signal (Signals.ManualProjects, ""); Ds.signal (Signals.Theme, ""); Ds.signal (Signals.CursorPos, "0"); Ds.signal (Signals.TestFilter, "all"); Ds.signal (Signals.ExpandedDashboard, "false"); Ds.signal (Signals.FrictionEndpoint, ""); Ds.signal (Signals.FrictionToken, ""); Ds.signal (Signals.FrictionEdits, "{}"); Ds.signal (Signals.FrictionSending, "false") ] []
      Elem.div [ Attr.id DomIds.ServerStatus; Attr.class' "conn-banner conn-disconnected"; Attr.style "display:none" ] [
        Text.raw "⏳ Connecting to server..."
      ]
      Elem.div [ Attr.id DomIds.Main ] [ initialContent ]
      completionInsertScript ()
      autoScrollScript ()
      detailsToggleScript ()
      keyboardHandlerScript ()
    ]
  ]

let private buildOutputPanels
  (q: DashboardQueries)
  (sessionId: WorkerProtocol.SessionId)
  (sessionState: string)
  (warmupProgress: string)
  : System.Threading.Tasks.Task<XmlNode * XmlNode * XmlNode> =
  task {
    let! previous = q.GetPreviousSessions ()
    // Build a meaningful placeholder so the output panel always shows SOMETHING
    // when the session exists but hasn't produced eval output yet.
    let emptyPlaceholder =
      match warmupProgress.Length > 0 with
      | true -> sprintf "⏳ %s" warmupProgress
      | false ->
        match sessionState with
        | "Ready" -> "Ready — type code in the evaluator below, or use the MCP tools"
        | state -> sprintf "%s" state
    let computeResult () =
      // Per-session regions use the caller's immutable viewing session,
      // never the Elm runtime's global active session.
      match q.GetElmRegionsForSession sessionId with
      | Some regions ->
        let outputRegion = regions |> List.tryFind (fun r -> r.Id = "output")
        let outNode =
          match outputRegion with
          | Some r ->
            let lines = parseOutputLines r.Content
            match lines.IsEmpty with
            | true -> renderOutputForSession (WorkerProtocol.SessionId.value sessionId) lines emptyPlaceholder
            | false -> renderOutputForSession (WorkerProtocol.SessionId.value sessionId) lines "No output yet"
          | None -> renderOutputForSession (WorkerProtocol.SessionId.value sessionId) [] emptyPlaceholder
        let sessRegion = regions |> List.tryFind (fun r -> r.Id = "sessions")
        match sessRegion with
        | Some r ->
          let parsed = parseSessionLines r.Content
          let corrected = overrideSessionStatuses q.GetSessionState q.GetStatusMsg parsed
          let visible =
            corrected
            |> List.filter (fun s -> s.Status <> SessionDisplayStatus.Stopped)
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
                  BindingEntries = bindingEntries
                  AgentBadges = q.GetSessionAgentBadges s.Id
                  GuidanceCssClass = q.GetSessionGuidanceCss s.Id })
          let creating = isCreatingSession r.Content
          let sess = renderSessionsForSession (WorkerProtocol.SessionId.value sessionId) visible creating
          let sessionPicker =
            match visible.IsEmpty && not creating with
            | true -> renderSessionPicker previous
            | false -> renderSessionPickerEmpty
          (outNode, sess, sessionPicker)
        | None ->
          (outNode, renderSessionsForSession (WorkerProtocol.SessionId.value sessionId) [] false, renderSessionPickerEmpty)
      | None ->
        (renderOutputForSession (WorkerProtocol.SessionId.value sessionId) [] emptyPlaceholder, renderSessionsForSession (WorkerProtocol.SessionId.value sessionId) [] false, renderSessionPickerEmpty)
    return computeResult ()
  }

let resolveViewingSession
  (requestedSession: string option)
  (sessions: WorkerProtocol.SessionInfo list)
  : WorkerProtocol.SessionId option =
  let requested =
    requestedSession
    |> Option.bind (WorkerProtocol.SessionId.validate >> Result.toOption)
    |> Option.filter (fun sessionId -> sessions |> List.exists (fun session -> session.Id = sessionId))
  requested
  |> Option.orElseWith (fun () -> sessions |> List.tryHead |> Option.map (fun session -> session.Id))

/// Build a complete DashboardSnapshot from the current daemon state.
/// Independent of any HTTP/SSE context — called from both the initial GET render
/// and each SSE push. Returns the snapshot, the resolved session ID, and the
/// resolved theme name so the caller can update its tracking state.
let buildDashboardSnapshot
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (currentSessionId: WorkerProtocol.SessionId)
  (lastSessionId: WorkerProtocol.SessionId)
  (lastWorkingDir: string)
  (lastThemeName: string)
  : System.Threading.Tasks.Task<DashboardSnapshot * WorkerProtocol.SessionId * string> =
  task {
    let sessionId = currentSessionId
    let sid = WorkerProtocol.SessionId.value sessionId
    let lastSid = WorkerProtocol.SessionId.value lastSessionId
    let state = q.GetSessionState sessionId
    let stateStr = SessionState.label state
    let workingDir = q.GetSessionWorkingDir sessionId
    let statsTask = q.GetEvalStats sessionId
    let hrTask = q.GetHotReloadState sessionId
    let wCtxTask = q.GetWarmupContext sessionId
    let! stats = statsTask
    let! hrState = hrTask
    let! wCtx = wCtxTask
    let timelineStats = q.GetEvalTimeline()
    let evalStatsView = EvalStatsView.fromStats stats timelineStats
    let daemonHealth = q.GetDaemonHealth()
    let daemonHealthPanel =
      match daemonHealth with
      | Some snap -> renderDaemonHealth (DaemonHealthView.fromSnapshot snap)
      | None -> Elem.div [ Attr.id DomIds.DaemonHealth; Attr.class' "meta" ] []
    let failureNarrativesPanel =
      let pairs = q.GetFailureNarratives()
      renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs)
    let diagnosticsPanel = renderCurrentDiagnostics (q.GetCurrentDiagnostics())
    let filmstripPanel = renderSessionFilmstrip (q.GetFilmstripEntries())
    let themeName =
      match resolveThemePush infra.SessionThemes sid workingDir lastSid lastWorkingDir lastThemeName with
      | Some name -> name
      | None -> lastThemeName
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
    let hrPanel =
      match sid.Length > 0 with
      | true ->
        match hrState with
        | Some hr -> renderHotReloadPanel sid hr.files hr.watchedCount
        | None -> renderHotReloadEmpty
      | false -> renderHotReloadEmpty
    let scPanel =
      match sid.Length > 0 with
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
            { SessionId = sid
              ProjectNames = []
              WorkingDir = q.GetSessionWorkingDir sessionId
              Status = SessionState.label (q.GetSessionState sessionId)
              Warmup = ctx'
              FileStatuses = fileStatuses
              Workflow = WorkflowTypes.SessionWorkflow.Interactive
              AutoOpenNamespaces = DirectoryConfig.autoOpenNamespacesForDirectory (q.GetSessionWorkingDir sessionId) }
        | None -> renderSessionContextEmpty
      | false -> renderSessionContextEmpty
    let bindingsPanel =
      // Live watch window takes priority; fall back to the text-parsed panel
      // for sessions that haven't produced a live snapshot yet.
      match q.GetLiveBindings sessionId with
      | Some liveSnap -> renderLiveBindingsPanel (Some liveSnap)
      | None -> renderBindingsPanel (resolveBindingsPanelSnapshot (q.GetBindingScopeSnapshot ()) (q.GetSessionBindings sessionId))
    let liveTestingActive = q.GetLiveTestingActive ()
    let liveTestingStatus = q.GetLiveTestingStatus ()
    let (ltPassed, ltFailed) =
      match daemonHealth with
      | Some dh ->
        match dh.LiveTestingSummary with
        | Some lt -> (Some lt.Passed, Some lt.Failed)
        | None -> (None, None)
      | None -> (None, None)
    let liveTestingPanel = renderLiveTestingPanel liveTestingActive liveTestingStatus ltPassed ltFailed
    let alarmPanel = renderAlarmBanner (infra.SystemAlarmBuffer.Value)
    let warmupProgress = q.GetWarmupProgress sessionId
    let! outputPanel, sessionsPanel, sessionPicker = buildOutputPanels q sessionId stateStr warmupProgress
    // Friction review panel — local store only. Built server-side so the
    // client never assembles raw telemetry.
    let! frictionStore = q.GetFrictionStore () |> Async.AwaitTask
    let frictionPanel =
      match frictionStore with
      | None -> Elem.div [ Attr.id DomIds.FrictionPanel ] []
      | Some store ->
        let reportResult =
          SageFs.Features.McpFrictionRecorder.Recorder.reportDirect store None
          |> Async.AwaitTask
          |> Async.RunSynchronously
        let historyResult = store.ListSentReports ()
        match reportResult, historyResult with
        | Ok report, Ok history ->
          let view = SageFs.Features.FrictionReviewView.build report history
          renderFrictionPanel view
        | _ -> Elem.div [ Attr.id DomIds.FrictionPanel ] []
    let snap : DashboardSnapshot = {
      Version = infra.Version
      SessionState = stateStr
      SessionId = sid
      WorkingDir = workingDir
      WarmupProgress = warmupProgress
      WorkflowLabel = q.GetSessionWorkflow sessionId |> WorkflowTypes.SessionWorkflow.label
      EvalStats = evalStatsView
      AlarmPanel = alarmPanel
      DaemonHealth = daemonHealthPanel
      FailureNarrativesPanel = failureNarrativesPanel
      DiagnosticsPanel = diagnosticsPanel
      FilmstripPanel = filmstripPanel
      ThemeName = themeName
      ConnectionLabel = connectionLabel
      HotReloadPanel = hrPanel
      LiveTestingPanel = liveTestingPanel
      SessionContextPanel = scPanel
      OutputPanel = outputPanel
      SessionsPanel = sessionsPanel
      SessionPicker = sessionPicker
      ThemePicker = renderThemePicker themeName
      ThemeVars = renderThemeVars themeName
      BindingsPanel = bindingsPanel
      FrictionPanel = frictionPanel
    }
    return snap, sessionId, themeName
  }

/// Create the SSE stream handler that pushes Elm state to the browser.
let createStreamHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  : HttpHandler =
  fun ctx -> task {
    SageFs.Instrumentation.sseConnectionsActive.Add(1L)
    Response.sseStartResponse ctx |> ignore

    let clientId = Guid.NewGuid().ToString("N").[..7]
    // Resolve one immutable viewing session for this browser connection.
    // renderShell puts this same ID in the selected card, output projection,
    // eval target, and stream URL. Daemon-global switches never replace it.
    let! sessions = q.GetAllSessions ()
    let requestedSession =
      match ctx.Request.Query.TryGetValue("session") with
      | true, values when values.Count > 0 -> Some values.[0]
      | _ -> None
    let currentSessionOpt = resolveViewingSession requestedSession sessions
    let currentSessionStr = currentSessionOpt |> Option.map WorkerProtocol.SessionId.value |> Option.defaultValue ""
    infra.ConnectionTracker |> Option.iter (fun t -> t.Register(clientId, Browser, currentSessionStr))
    let mutable lastSessionId = currentSessionOpt |> Option.defaultValue (WorkerProtocol.SessionId.newId ())
    let mutable lastWorkingDir = ""
    let mutable lastThemeName = defaultThemeName

    let pushState () = task {
      match currentSessionOpt with
      | None ->
        // No sessions — push session picker
        let! previous = q.GetPreviousSessions ()
        do! ssePatchNode ctx (renderSessionPicker previous)
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) ""
      | Some sessionId ->
      let! snap, newSessionId, newThemeName =
        buildDashboardSnapshot q infra sessionId lastSessionId lastWorkingDir lastThemeName
      let sessionChanged = newSessionId <> lastSessionId
      // Patch the theme signal on session switch (always), or when the
      // resolved theme name changed. This keeps the picker in sync with
      // the server's source of truth.
      let themeChanged = newThemeName <> lastThemeName
      let shouldPatchTheme = sessionChanged || themeChanged
      // Only log when something actually changed. Without this guard the log
      // floods with one entry per SSE poll (each poll pushes a snapshot even
      // when nothing changed), drowning out the signal.
      match shouldPatchTheme with
      | true ->
        Log.info "[pushState] cur=%s last=%s newTheme=%s lastTheme=%s sessionChanged=%b themeChanged=%b"
          (WorkerProtocol.SessionId.value sessionId) (WorkerProtocol.SessionId.value lastSessionId) newThemeName lastThemeName sessionChanged themeChanged
      | false -> ()
      lastSessionId <- newSessionId
      lastWorkingDir <- q.GetSessionWorkingDir newSessionId
      lastThemeName <- newThemeName
      // When the active session changes OR the resolved theme name differs
      // from the last theme the server pushed, patch the theme signal so
      // Datastar's data-bind-theme on the <select> stays in sync with the
      // server-rendered <option selected>.
      match shouldPatchTheme with
      | true -> do! Response.ssePatchSignal ctx (SignalPath.sp Signals.Theme) newThemeName
      | false -> ()
      do! ssePatchNode ctx (renderMainContent snap)
    }

    try
      // Push initial state (catch all exceptions — don't let a transient failure kill the stream)
      try
        do! pushState ()
      with
      | :? OperationCanceledException -> ()
      | :? System.IO.IOException -> ()
      | :? ObjectDisposedException -> ()
      | ex ->
        Log.error "[Dashboard SSE] Initial pushState failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")

      match infra.StateChanged with
      | Some evt ->
        let tcs = Threading.Tasks.TaskCompletionSource()
        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
        // Serialize SSE writes via MailboxProcessor — no locks, no mutable state.
        // Coalesces rapid state changes: drain queued, throttle 100ms, drain again, push once.
        // Heartbeat: when idle >15s, sends `: keepalive\n\n` SSE comment to prevent
        // proxy/browser timeouts. Integrated into the actor loop to avoid concurrent writes.
        let pushAgent = MailboxProcessor<DaemonStateChange>.Start((fun inbox ->
          let rec loop () = async {
            let! msg = inbox.TryReceive(15_000)
            match msg with
            | None ->
              // Idle timeout — send SSE keepalive comment. The 15s interval
              // matches the Datastar client's expected heartbeat and stays
              // well under Kestrel's default keep-alive timeout. The write
              // is wrapped to swallow connection-closed exceptions so the
              // loop survives transient client disconnects (Datastar will
              // reconnect via a new EventSource).
              try
                if not ctx.RequestAborted.IsCancellationRequested then
                  let bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n\n")
                  do! ctx.Response.Body.AsyncWrite(bytes, 0, bytes.Length)
                  do! ctx.Response.Body.FlushAsync() |> Async.AwaitTask
              with
              | :? System.IO.IOException -> ()
              | :? ObjectDisposedException -> ()
              | :? OperationCanceledException -> ()
              | :? System.ArgumentOutOfRangeException -> ()
              | :? System.InvalidOperationException -> ()
              | _ -> ()
              // If the client cancelled, exit the loop. The MailboxProcessor
              // was started with ctx.RequestAborted so it's also being torn
              // down, but exiting explicitly lets the handler return cleanly.
              match ctx.RequestAborted.IsCancellationRequested with
              | true -> ()
              | false -> return! loop ()
            | Some _change ->
              // Other state changes — drain + coalesce + push
              while inbox.CurrentQueueLength > 0 do
                let! _ = inbox.Receive()
                ()
              do! Async.Sleep 100
              while inbox.CurrentQueueLength > 0 do
                let! _ = inbox.Receive()
                ()
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
        use _sub = evt.Subscribe(fun change ->
          try pushAgent.Post(change)
          with :? ObjectDisposedException -> ())
        // Adaptive live-bindings subscription. Never write directly to the SSE
        // response here: eval completion can fire this callback concurrently
        // with the Elm ModelChanged event. All writes must go through pushAgent
        // so a full #main morph is serialized and cannot be interleaved.
        let mutable liveBindingsSub : IDisposable option = None
        let subscribeLiveBindings (sessionId: WorkerProtocol.SessionId) =
          liveBindingsSub |> Option.iter (fun d -> d.Dispose())
          let sidStr = WorkerProtocol.SessionId.value sessionId
          liveBindingsSub <-
            infra.LiveBindingsAdaptive
            |> Option.bind (fun store ->
                Some (SageFs.Features.LiveBindingsAdaptive.subscribe store sidStr (fun _ ->
                  try pushAgent.Post(ModelChanged (0, 0))
                  with :? ObjectDisposedException -> ())))
        match currentSessionOpt with
        | Some sid -> subscribeLiveBindings sid
        | None -> ()
        do! tcs.Task
        liveBindingsSub |> Option.iter (fun d -> d.Dispose())
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
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (evalCode: WorkerProtocol.SessionId -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      let code =
        match doc.RootElement.TryGetProperty("code") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let sessionIdStr =
        match doc.RootElement.TryGetProperty(Signals.ViewingSessionId) with
        | true, prop -> prop.GetString()
        | _ -> ""
      match String.IsNullOrWhiteSpace code with
      | true ->
        Response.sseStartResponse ctx |> ignore
        do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
      | false ->
        match WorkerProtocol.SessionId.validate sessionIdStr with
        | Error _ ->
          Response.sseStartResponse ctx |> ignore
          do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
        | Ok sessionId ->
          let codeWithTerminator =
            let trimmed = code.TrimEnd()
            match trimmed.EndsWith(";;") with
            | true -> code
            | false -> sprintf "%s;;" trimmed
          let! result = evalCode sessionId codeWithTerminator
          Response.sseStartResponse ctx |> ignore
          do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
          // The action response owns this interaction. Render the freshly
          // committed snapshot here so Datastar cannot drop an overlapping
          // long-lived stream morph while this POST is still resolving.
          let! snap, _, _ =
            buildDashboardSnapshot q infra sessionId sessionId (q.GetSessionWorkingDir sessionId) defaultThemeName
          do! ssePatchNode ctx (renderMainContent snap)
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
    | :? RequestTooLargeException -> ()  // 413 already written by readSignalsJsonSized
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }
let createEvalFileHandler
  (getSessionWorkingDir: WorkerProtocol.SessionId -> string)
  (evalCode: WorkerProtocol.SessionId -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      // W5(R7): 1 MB size cap — eval-file was sending full file content without limit.
      do! checkBodySize ctx
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      use doc = System.Text.Json.JsonDocument.Parse(body)
      let filePath =
        match doc.RootElement.TryGetProperty("path") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let sessionIdStr =
        match doc.RootElement.TryGetProperty(Signals.ViewingSessionId) with
        | true, prop -> prop.GetString()
        | _ -> ""
      match WorkerProtocol.SessionId.validate sessionIdStr with
      | Error _ ->
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = "Invalid session ID" |})
      | Ok sessionId ->
        // W1: Canonicalize and confirm the requested file is inside the session's working directory.
        // This prevents path traversal attacks like {"path":"C:/Users/.ssh/id_rsa"}.
        // W1(R7): Use ResolveLinkTarget(returnFinalTarget=true) to handle directory symlinks too.
        // W1(R8): Hoist canonical before isContained check so the SAME value is used for both
        //         the containment check and the actual read (eliminates TOCTOU/invariant violation).
        let workingDir = getSessionWorkingDir sessionId
        let resolveRealPath (p: string) : string =
          let full = Path.GetFullPath p
          let fsi : System.IO.FileSystemInfo =
            match Directory.Exists(full) with
            | true -> DirectoryInfo(full) :> System.IO.FileSystemInfo
            | false -> FileInfo(full) :> System.IO.FileSystemInfo
          match fsi.ResolveLinkTarget(returnFinalTarget = true) with
          | null -> full
          | resolved -> resolved.FullName
        let canonical = resolveRealPath filePath
        let canonicalDir = resolveRealPath workingDir
        let isContained =
          not (String.IsNullOrWhiteSpace filePath || String.IsNullOrWhiteSpace workingDir)
          && (canonical.StartsWith(
                canonicalDir + string Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
              || canonical.Equals(canonicalDir, StringComparison.OrdinalIgnoreCase))
        match isContained && File.Exists canonical with
        | false ->
          ctx.Response.StatusCode <- 403
          do! ctx.Response.WriteAsJsonAsync({| success = false; error = "File not found or outside session working directory" |})
        | true ->
          let! code = File.ReadAllTextAsync(canonical)
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
    with
    | :? RequestTooLargeException -> ()  // 413 already written
    | ex ->
      ctx.Response.StatusCode <- 500
      let details = SageFs.SageFsError.toJson (SageFs.SageFsError.Unexpected ex)
      do! ctx.Response.WriteAsJsonAsync(
            {| success = false
               error = SageFs.SageFsError.describe (SageFs.SageFsError.Unexpected ex)
               errorDetails = details |})
  }
let createCompletionsHandler
  (getCompletions: WorkerProtocol.SessionId -> string -> int -> Threading.Tasks.Task<Features.AutoCompletion.CompletionItem list>)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
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
      let sessionIdStr =
        match doc.RootElement.TryGetProperty(Signals.ViewingSessionId) with
        | true, prop -> prop.GetString()
        | _ -> ""
      Response.sseStartResponse ctx |> ignore
      match String.IsNullOrWhiteSpace code || cursorPos < 0 with
      | true ->
        do! ssePatchNode ctx (renderCompletionDropdown [] 0)
      | false ->
        match WorkerProtocol.SessionId.validate sessionIdStr with
        | Error _ ->
          do! ssePatchNode ctx (renderCompletionDropdown [] 0)
        | Ok sessionId ->
          let! items = getCompletions sessionId code cursorPos
          do! ssePatchNode ctx (renderCompletionDropdown items cursorPos)
    with
    | :? RequestTooLargeException -> ()
    | ex ->
      ctx.Response.StatusCode <- 500
      let details = SageFs.SageFsError.toJson (SageFs.SageFsError.Unexpected ex)
      do! ctx.Response.WriteAsJsonAsync(
            {| success = false
               error = SageFs.SageFsError.describe (SageFs.SageFsError.Unexpected ex)
               errorDetails = details |})
  }

/// Create the reset POST handler.
let createResetHandler
  (resetSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      let! sessionIdResult = task {
        try
          use! doc = readSignalsJsonSized ctx
          match doc.RootElement.TryGetProperty(Signals.ViewingSessionId) with
          | true, prop -> return WorkerProtocol.SessionId.validate (prop.GetString())
          | _ -> return Error "Missing viewingSessionId"
        with ex ->
          Log.warn "[Dashboard] Session ID extraction from JSON failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          return Error "Failed to parse request"
      }
      match sessionIdResult with
      | Error errMsg ->
        Response.sseStartResponse ctx |> ignore
        let resultHtml =
          Elem.div [ Attr.id DomIds.EvalResult ] [
            Elem.pre [ Attr.class' "output-line output-error"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
              Text.raw (sprintf "Reset: %s" errMsg)
            ]
          ]
        do! ssePatchNode ctx resultHtml
      | Ok sessionId ->
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
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the session action handler (switch/stop).
/// If `teardown` is true (stop/dispose/purge), it:
///   - immediately replaces the session's card with "⏳ Stopping session id:[id]..."
///   - clears the output + eval-result if that session is the one being viewed
///   - after the action resolves, auto-switches to the next session in the list
///     (or shows the session picker if none remain)
let createSessionActionHandler
  (q: DashboardQueries)
  (action: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>)
  (teardown: bool)
  : WorkerProtocol.SessionId -> HttpHandler =
  fun sessionId ctx -> task {
    try
      let sid = WorkerProtocol.SessionId.value sessionId
      // Read the signals so we know which session is currently being viewed.
      let viewingId =
        try
          use doc = readSignalsJsonSized ctx |> Async.AwaitTask |> Async.RunSynchronously
          getSignalString doc Signals.ViewingSessionId "viewing-session-id"
        with _ -> ""
      Response.sseStartResponse ctx |> ignore
      let isViewingStopped = viewingId = sid
      // Immediate feedback: swap the card for the "Stopping…" message.
      if teardown then
        do! ssePatchNode ctx (renderStoppingCard sessionId)
        if isViewingStopped then
          // Clear the output display + eval result of the session being unloaded.
          let clearedOutput =
            Elem.div [ Attr.id DomIds.OutputPanel ] [
              Elem.span [ Attr.class' "meta"; Attr.style "padding: 0.5rem;" ] [ Text.raw "No output yet" ]
            ]
          do! ssePatchNode ctx clearedOutput
          do! ssePatchNode ctx (Elem.div [ Attr.id DomIds.EvalResult ] [])
      let! result = action sessionId
      // After a successful teardown, select the next session (or show the picker).
      let! nextId =
        match teardown && Result.isOk result with
        | false -> task { return None }
        | true ->
          task {
            let! sessions = q.GetAllSessions ()
            let remainingIds =
              sessions
              |> List.map (fun s -> s.Id)
              |> List.filter (fun id -> id <> sessionId)
            // Sidebar order comes from the Elm sessions region — use it for "next in list".
            let orderedIds =
              match q.GetElmRegionsForSession sessionId with
              | Some regions ->
                regions
                |> List.tryFind (fun r -> r.Id = "sessions")
                |> Option.map (fun r -> parseSessionLines r.Content |> List.map (fun s -> s.Id))
                |> Option.defaultValue remainingIds
              | None -> remainingIds
            let orderedRemaining = orderedIds |> List.filter (fun id -> List.contains id remainingIds)
            match orderedRemaining with
            | [] -> return None
            | ids ->
              match List.tryFindIndex ((=) sessionId) orderedIds with
              | Some idx when idx < List.length ids -> return Some ids.[idx]
              | _ -> return Some (List.head ids)
          }
      match nextId with
      | Some nextSession ->
        // Auto-switch: display + select the next session.
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value nextSession)
        // Sync the output, sessions panel, and statusline to the auto-selected next session.
        match q.GetElmRegionsForSession nextSession with
        | Some regions ->
          match regions |> List.tryFind (fun r -> r.Id = "output") with
          | Some outputRegion ->
            do! ssePatchNode ctx (renderOutput (parseOutputLines outputRegion.Content) "No output yet")
          | None -> ()
          match regions |> List.tryFind (fun r -> r.Id = "sessions") with
          | Some sessRegion ->
            let parsed = parseSessionLines sessRegion.Content
            let corrected = overrideSessionStatuses q.GetSessionState q.GetStatusMsg parsed
            let visible =
              corrected
              |> List.filter (fun s -> s.Status <> SessionDisplayStatus.Stopped)
              |> List.map (fun s ->
                let info = q.GetSessionStandbyInfo s.Id
                { s with StandbyLabel = StandbyInfo.label info })
            do! ssePatchNode ctx (renderSessions visible false)
          | None -> ()
        | None -> ()
        let stateLabel = q.GetSessionState nextSession |> SessionState.label
        do! ssePatchNode ctx (
          Elem.div [ Attr.id DomIds.SessionStatus ] [
            Elem.span [ Attr.class' "status status-ready"; Attr.style "border-radius:0;" ] [ Text.raw stateLabel ]
          ])
        let switchedDir = q.GetSessionWorkingDir nextSession
        do! ssePatchNode ctx (
          Elem.div [ Attr.id "statusline-left" ] [
            Elem.div [ Attr.id "statusline-branch" ] [ Text.raw stateLabel ]
            Elem.div [ Attr.id "statusline-file" ] [ Text.raw switchedDir ]
          ])
      | None when teardown && Result.isOk result ->
        // No sessions remain — show the session picker (with Resume Previous).
        let! previous = q.GetPreviousSessions ()
        do! ssePatchNode ctx (renderSessionPicker previous)
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) ""
      | None ->
        // Switch path (or failed teardown): eval form targets the requested session.
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value sessionId)
        // Immediately sync the output, sessions panel, and statusline to the
        // newly-selected session so the display matches the sidebar highlight.
        match q.GetElmRegionsForSession sessionId with
        | Some regions ->
          match regions |> List.tryFind (fun r -> r.Id = "output") with
          | Some outputRegion ->
            do! ssePatchNode ctx (renderOutput (parseOutputLines outputRegion.Content) "No output yet")
          | None -> ()
          match regions |> List.tryFind (fun r -> r.Id = "sessions") with
          | Some sessRegion ->
            let parsed = parseSessionLines sessRegion.Content
            let corrected = overrideSessionStatuses q.GetSessionState q.GetStatusMsg parsed
            let visible =
              corrected
              |> List.filter (fun s -> s.Status <> SessionDisplayStatus.Stopped)
              |> List.map (fun s ->
                let info = q.GetSessionStandbyInfo s.Id
                { s with StandbyLabel = StandbyInfo.label info })
            do! ssePatchNode ctx (renderSessions visible false)
          | None -> ()
        | None -> ()
        // Patch the tabline status with the switched-to session's state.
        let stateLabel = q.GetSessionState sessionId |> SessionState.label
        do! ssePatchNode ctx (
          Elem.div [ Attr.id DomIds.SessionStatus ] [
            Elem.span [ Attr.class' "status status-ready"; Attr.style "border-radius:0;" ] [ Text.raw stateLabel ]
          ])
        // Patch the statusline with the switched-to session's working dir + state.
        let switchedDir = q.GetSessionWorkingDir sessionId
        do! ssePatchNode ctx (
          Elem.div [ Attr.id "statusline-left" ] [
            Elem.div [ Attr.id "statusline-branch" ] [ Text.raw stateLabel ]
            Elem.div [ Attr.id "statusline-file" ] [ Text.raw switchedDir ]
          ])
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

/// Result of a friction send attempt. Rendered into the dashboard so
/// the user sees success/failure inline in the friction panel.
let private frictionSendResultDom (ok: bool) (error: string) (reportId: string) =
  let statusClass = if ok then "friction-send-ok" else "friction-send-err"
  let statusText =
    if ok
    then sprintf "Sent (report id: %s)" (if reportId.Length > 0 then reportId else "?")
    else sprintf "Failed: %s" (if error.Length > 0 then error else "unknown error")
  Elem.div [ Attr.class' (sprintf "friction-send-status %s" statusClass); Attr.id DomIds.FrictionSendStatus ] [
    Elem.pre [ Attr.style "margin: 0; white-space: pre-wrap; font-size: 0.8rem;" ] [ Text.raw statusText ]
  ]

/// SHA-256 of the endpoint URL, hex-encoded. We hash rather than store
/// the URL so the local SQLite friction store doesn't accumulate secrets
/// the user might rotate (e.g. rotate a Discord webhook URL).
let private frictionEndpointHash (url: string) : string =
  use sha = System.Security.Cryptography.SHA256.Create()
  let bytes = System.Text.Encoding.UTF8.GetBytes(url)
  let hash = sha.ComputeHash(bytes)
  System.Convert.ToHexString(hash).ToLowerInvariant()

/// P0 safety: validate a friction-report destination before any network I/O.
/// Absolute https only; http permitted solely to loopback (local receiver
/// development). Rejects file://, non-loopback plaintext http (the ingest
/// token would cross the wire unencrypted), and malformed URLs — so the
/// client-supplied endpoint cannot be abused as an SSRF/proxy primitive.
let isAllowedFrictionEndpoint (endpoint: string) : bool =
  match System.Uri.TryCreate(endpoint, System.UriKind.Absolute) with
  | true, uri ->
    match uri.Scheme.ToLowerInvariant() with
    | "https" -> true
    | "http" -> uri.IsLoopback
    | _ -> false
  | _ -> false

/// POST /dashboard/friction/send — server-authoritative friction send.
///
/// Privacy + integrity model:
/// - The client supplies ONLY the destination (frictionEndpoint + optional
///   frictionToken) and optional per-feedback reason edits (frictionEdits).
///   It NEVER supplies the report payload — the server builds the outgoing
///   report from the LOCAL SQLite store and sanitizes it (FrictionSanitize)
///   immediately before serialization. A buggy or malicious client cannot
///   push raw local data out; the server is the only assembly point.
/// - The destination is validated strictly (https, or http to loopback)
///   before any network I/O.
/// - The receipt is recorded only after remote acceptance AND local
///   receipt-write success.
let createFrictionSendHandler
  (q: DashboardQueries)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      let endpoint =
        match doc.RootElement.TryGetProperty("frictionEndpoint") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let token =
        match doc.RootElement.TryGetProperty("frictionToken") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let editsJson =
        match doc.RootElement.TryGetProperty("frictionEdits") with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.String -> prop.GetString()
        | true, prop -> prop.GetRawText()
        | _ -> ""
      Response.sseStartResponse ctx |> ignore
      match endpoint.Length with
      | 0 ->
        do! ssePatchNode ctx (frictionSendResultDom false "missing endpoint" "")
      | _ ->
        // P0 safety: validate the destination strictly before any network I/O.
        match isAllowedFrictionEndpoint endpoint with
        | false ->
          do! ssePatchNode ctx (frictionSendResultDom false "endpoint must be an absolute https URL (http allowed only to loopback)" "")
        | true ->
          // Server-authoritative: build the report from the local store.
          let! store = q.GetFrictionStore () |> Async.AwaitTask
          match store with
          | None ->
            do! ssePatchNode ctx (frictionSendResultDom false "no local friction store is available to build the report" "")
          | Some s ->
            let! reportResult = SageFs.Features.McpFrictionRecorder.Recorder.reportDirect s None |> Async.AwaitTask
            match reportResult with
            | Error err ->
              do! ssePatchNode ctx (frictionSendResultDom false err "")
            | Ok report ->
              let outgoing = SageFs.Features.FrictionReviewView.buildOutgoingForSend report editsJson
              let payloadJson = System.Text.Json.JsonSerializer.Serialize(outgoing)
              let urlHash = frictionEndpointHash endpoint
              let mutable attemptError : string option = None
              let mutable reportId = ""
              try
                use http = new HttpClient()
                http.Timeout <- System.TimeSpan.FromSeconds(15.0)
                try
                  let req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                  req.Content <- new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json")
                  if token.Length > 0 then
                    req.Headers.Add("X-SageFs-Token", token)
                  let! resp = http.SendAsync(req) |> Async.AwaitTask
                  let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                  if resp.IsSuccessStatusCode then
                    try
                      use respDoc = System.Text.Json.JsonDocument.Parse(body)
                      let root = respDoc.RootElement
                      reportId <-
                        match root.TryGetProperty("reportId") with
                        | true, p -> p.GetString()
                        | _ -> ""
                    with _ -> ()
                    let sentAt = System.DateTimeOffset.UtcNow
                    let sent =
                      { ReportId = if reportId.Length > 0 then reportId else System.Guid.NewGuid().ToString("N").[..12]
                        SentAtUtc = sentAt
                        SageFsVersion = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current ()
                        TotalEvents = outgoing.TotalEvents
                        TotalFeedbackItems = outgoing.TotalFeedbackItems
                        DestinationKind = "cloudflare-worker"
                        DestinationUrlHash = urlHash }
                    // P0 safety: the receipt is only "sent" after BOTH remote
                    // acceptance AND local receipt-write success.
                    match s.RecordSentReport sent with
                    | Ok () ->
                      do! ssePatchNode ctx (frictionSendResultDom true "" reportId)
                    | Error e ->
                      Log.warn "[friction] failed to record sent report locally: %s" e
                      attemptError <- Some (sprintf "worker accepted the report but recording the local receipt failed: %s" e)
                  else
                    attemptError <- Some (sprintf "worker returned %d: %s" (int resp.StatusCode) (if body.Length > 200 then body.[..200] + "..." else body))
                finally
                  http.Dispose()
              with ex ->
                attemptError <- Some ex.Message
              match attemptError with
              | Some err -> do! ssePatchNode ctx (frictionSendResultDom false err "")
              | None -> ()
    with
    | :? RequestTooLargeException -> ()
    | ex -> Log.warn "[friction] send failed: %s" ex.Message
  }

/// Create the discover-projects POST handler.
let createDiscoverHandler : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
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
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the create-session POST handler.
let createCreateSessionHandler
  (createSession: string list -> string -> Threading.Tasks.Task<Result<WorkerProtocol.SessionId, string>>)
  (switchSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
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
            let! _ = switchSession newSessionId
            // Push the new viewing identity so every dashboard action targets it.
            do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value newSessionId)
            do! ssePatchNode ctx (
              Elem.div [ Attr.id DomIds.EvalResult ] [
                Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem;" ] [
                  Text.raw (sprintf "Session '%s' created. Switched to it." (WorkerProtocol.SessionId.value newSessionId))
                ]
              ])
          | Error msg ->
            do! ssePatchNode ctx (evalResultError (sprintf "Failed: %s" msg))
          do! ssePatchNode ctx (Elem.div [ Attr.id DomIds.DiscoveredProjects ] [])
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Toggle warmup auto-open for the session's working directory and re-init the
/// session so the new setting takes effect immediately.
///
/// Disable: writes the .SageFs/config.fsx opt-out, stops the current session,
/// and re-opens a BARE session (no projects, nothing auto-loaded). No warmup
/// status is reported — the response is just "auto open disabled".
///
/// Enable: rewrites the config back to default, stops the current session, and
/// re-creates it (warmup runs with auto-open again).
let createToggleWarmupAutoOpenHandler
  (a: DashboardActions)
  (enable: bool)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      let dir = getSignalString doc "newSessionDir" "new-session-dir"
      let sessionId = getSignalString doc Signals.ViewingSessionId "viewing-session-id"
      let configResultNode message cssClass =
        Elem.div [ Attr.id DomIds.EvalResult ] [
          Elem.pre [ Attr.class' (sprintf "output-line %s" cssClass); Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw message
          ]
        ]
      Response.sseStartResponse ctx |> ignore
      match String.IsNullOrWhiteSpace dir, Directory.Exists dir with
      | true, _ ->
        do! ssePatchNode ctx (evalResultError "Working directory is required")
      | false, false ->
        do! ssePatchNode ctx (evalResultError (sprintf "Directory not found: %s" dir))
      | false, true ->
        // 1) Write the config so FUTURE sessions pick up the setting.
        let configWrite : Result<unit, string> =
          match enable with
          | false ->
            DirectoryConfig.ensureAutoOpenNamespacesOptOut dir
            |> Result.map (fun _ -> ())
          | true ->
            DirectoryConfig.ensureAutoOpenNamespacesOptIn dir
            |> Result.map (fun _ -> ())
        match configWrite with
        | Error msg ->
          do! ssePatchNode ctx (evalResultError msg)
        | Ok _ ->
          // 2) Stop the current session for this directory (if any) so the
          //    re-created session starts clean with the new setting.
          match WorkerProtocol.SessionId.validate sessionId with
          | Ok sid -> let! _ = a.StopSession sid in ()
          | Error _ -> ()
          // 3) Re-create: bare (no projects) when disabled — nothing loads and
          //    no warmup happens. With projects (auto-detected) when enabled.
          let projects =
            match enable with
            | false -> []
            | true ->
              resolveSessionProjects dir ""
              |> List.truncate 1
          let! result = a.CreateSession projects dir
          match result with
          | Ok newSessionId ->
            let! _ = a.SwitchSession newSessionId in ()
            do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value newSessionId)
            // 4) Concise confirmation — no warmup status spam.
            let message =
              match enable with
              | false -> "auto open disabled"
              | true -> "auto open enabled"
            do! ssePatchNode ctx (configResultNode message "output-result")
          | Error msg ->
            do! ssePatchNode ctx (evalResultError (sprintf "Failed to re-init session: %s" msg))
        do! pushDiscoverResults ctx dir
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
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
    let defaultSid = sessions |> List.tryHead |> Option.map (fun s -> WorkerProtocol.SessionId.value s.Id) |> Option.defaultValue ""
    let connSessionId =
      match ctx.Request.Query.TryGetValue("sessionId") with
      | true, v when v.Count > 0 && not (String.IsNullOrEmpty(v.[0])) -> v.[0]
      | _ -> defaultSid
    let clientId = sprintf "tui-%s" (Guid.NewGuid().ToString("N").[..7])
    infra.ConnectionTracker |> Option.iter (fun t -> t.Register(clientId, Terminal, connSessionId))

    let pushJson () = task {
      // Use THIS connection's session (set via ?sessionId= query param),
      // not a daemon global. Each TUI/dashboard SSE connection has its own
      // sessionId, so the push reflects what THAT client is viewing.
      let activeSidStr = connSessionId
      let activeSid = WorkerProtocol.SessionId.validate activeSidStr |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
      let activeDir = q.GetSessionWorkingDir activeSid
      let state = q.GetSessionState activeSid
      let! (stats : SageFs.Affordances.EvalStats) = q.GetEvalStats activeSid
      let regions =
        match q.GetElmRegionsForSession activeSid with
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
      let! hrState = q.GetHotReloadState activeSid
      let watchedCount = hrState |> Option.map (fun hr -> hr.watchedCount) |> Option.defaultValue 0
      let testSourceLocations =
        q.GetTestSourceLocations()
        |> List.map (fun l ->
          {| testName = l.TestName; filePath = l.FilePath; startLine = l.StartLine |})
      let workflow = q.GetSessionWorkflow activeSid
      let payload =
        System.Text.Json.JsonSerializer.Serialize(
          {| sessionId = activeSidStr
             sessionState = SessionState.label state
             evalCount = stats.EvalCount
             avgMs = if stats.EvalCount > 0 then stats.TotalDuration.TotalMilliseconds / float stats.EvalCount else 0.0
             activeWorkingDir = activeDir
             standbyLabel = StandbyInfo.label standby
             liveTestingStatus = liveTestingStatus
             watchedCount = watchedCount
             regions = regions
             testSourceLocations = testSourceLocations
             workflowLabel = WorkflowTypes.SessionWorkflow.label workflow
             replCapability = WorkflowTypes.ReplCapability.label (WorkflowTypes.SessionWorkflow.replCapability workflow)
             hotReloadActive = WorkflowTypes.SessionWorkflow.isHotReloadActive workflow |})
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
    // W5: 1 MB body cap — /api/dispatch was the last POST endpoint without a size limit.
    do! checkBodySize ctx
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
    with
    | :? RequestTooLargeException -> ()  // 413 already written
    | ex ->
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
    // Static CSS — served from embedded resource. No immutable caching so
    // dashboard.css changes propagate without requiring a browser hard-refresh.
    yield get "/dashboard/dashboard.css" (fun ctx -> task {
      ctx.Response.ContentType <- "text/css; charset=utf-8"
      ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache, must-revalidate"
      do! ctx.Response.WriteAsync(dashboardCss)
    })
    // Pinned self-hosted Datastar bundle — see `datastarBundle`.
    yield get "/dashboard/datastar.js" (fun ctx -> task {
      ctx.Response.ContentType <- "application/javascript; charset=utf-8"
      ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache, must-revalidate"
      do! ctx.Response.WriteAsync(datastarBundle)
    })
    yield get "/dashboard" (fun ctx -> task {
      try
        let! sessions = q.GetAllSessions ()
        // Initial session for a fresh page load: prefer a ?session= query
        // param so deep-links work, then fall back to the first available
        // session. There is intentionally NO global "active session" read
        // here — that concept does not exist at the dashboard layer.
        let requestedSession =
          match ctx.Request.Query.TryGetValue("session") with
          | true, v when v.Count > 0 && not (String.IsNullOrEmpty(v.[0])) ->
            Some v.[0]
          | _ -> None
        match resolveViewingSession requestedSession sessions with
        | Some initialSessionId ->
          let! snap, resolvedId, _ = buildDashboardSnapshot q infra initialSessionId (WorkerProtocol.SessionId.newId ()) "" defaultThemeName
          let html = renderShell infra.Version (WorkerProtocol.SessionId.value resolvedId) (renderMainContent snap)
          return! FalcoResponse.ofHtml html ctx
        | None ->
          let html = renderShell infra.Version "" (Elem.div [] [])
          return! FalcoResponse.ofHtml html ctx
      with _ ->
        let html = renderShell infra.Version "" (Elem.div [] [])
        return! FalcoResponse.ofHtml html ctx
    })
    yield get "/dashboard/stream" (createStreamHandler q infra)
    yield post "/dashboard/eval" (createEvalHandler q infra a.EvalCode)
    yield post "/dashboard/eval-file" (createEvalFileHandler q.GetSessionWorkingDir a.EvalCode)
    yield post "/dashboard/completions" (createCompletionsHandler infra.GetCompletions)
    yield post "/dashboard/reset" (createResetHandler a.ResetSession)
    yield post "/dashboard/hard-reset" (createResetHandler a.HardResetSession)
    yield post "/dashboard/clear-output" createClearOutputHandler
    yield post "/dashboard/discover-projects" createDiscoverHandler
    yield post "/dashboard/friction/send" (createFrictionSendHandler q)
    // Dismiss all system alarms — clears the shared buffer and re-triggers SSE push.
    yield post "/dashboard/dismiss-alarm" (fun ctx -> task {
      infra.SystemAlarmBuffer.Value <- []
      infra.TriggerStateChange ()
      Response.sseStartResponse ctx |> ignore
      do! ssePatchNode ctx (renderAlarmBanner [])
    })
    yield post "/dashboard/set-theme" (fun ctx -> task {
      try
        // Theme is passed in the POST body by the select's onchange handler
        // (see renderThemePicker). We also read it from the signals JSON as
        // a fallback for legacy clients that only had data-bind.
        use! doc = readSignalsJsonSized ctx
        let theme =
          match ctx.Request.Query.ContainsKey "theme" with
          | true -> ctx.Request.Query.["theme"].ToString()
          | false ->
            match doc.RootElement.TryGetProperty(Signals.Theme) with
            | true, prop -> prop.GetString()
            | _ -> ""
        // Per-client viewing session: the session the user is looking at in
        // THIS browser tab. There is NO global fallback — if the client
        // didn't send a viewing-session signal, we don't know which session
        // the picker belongs to, and we must NOT silently route to a
        // daemon global (that would let one client's choice overwrite
        // another client's project theme).
        let viewingId =
          match doc.RootElement.TryGetProperty(Signals.ViewingSessionId) with
          | true, prop -> prop.GetString()
          | _ -> ""
        let rawDir =
          match String.IsNullOrEmpty viewingId with
          | false ->
            match WorkerProtocol.SessionId.validate viewingId with
            | Ok sid -> q.GetSessionWorkingDir sid
            | Error _ -> ""
          | true -> ""
        let workingDir = canonicalizeThemeKey rawDir
        Log.info "[set-theme] theme=%s viewingId=%s rawDir=%s key=%s" theme viewingId rawDir workingDir
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
        // Patch the theme signal so Datastar's data-bind-theme matches the
        // server-rendered <option selected>, preventing the binding from
        // immediately re-overriding the picker with a stale client value.
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.Theme) theme
      with
      | :? RequestTooLargeException -> ()
      | ex ->
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
    })
    // Create session in temp directory
    yield post "/dashboard/session/create-temp" (fun ctx -> task {
      let tempDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-%s" (Guid.NewGuid().ToString("N").[..7]))
      Directory.CreateDirectory(tempDir) |> ignore
      Response.sseStartResponse ctx |> ignore
      let! result = a.CreateSession [] tempDir
      match result with
      | Ok sessionId ->
        a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
        do! ssePatchNode ctx (
          Elem.div [ Attr.id DomIds.EvalResult ] [
            Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
              Text.raw (sprintf "Session '%s' created." (WorkerProtocol.SessionId.value sessionId))
            ]
          ])
      | Error err ->
        do! ssePatchNode ctx (evalResultError err)
    })
    // Resume previous session (re-creates in same working dir)
    yield mapPost "/dashboard/session/resume/{id}"
      (fun (r: RequestData) -> r.GetString("id", ""))
      (fun sessionId -> fun ctx -> task {
        let! previous = q.GetPreviousSessions ()
        match previous |> List.tryFind (fun s -> s.Id = sessionId) with
        | Some prev ->
          Response.sseStartResponse ctx |> ignore
          let! result = a.CreateSession prev.Projects prev.WorkingDir
          match result with
          | Ok sessionId ->
            a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
            do! ssePatchNode ctx (
              Elem.div [ Attr.id DomIds.EvalResult ] [
                Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
                  Text.raw (sprintf "Session '%s' created." (WorkerProtocol.SessionId.value sessionId))
                ]
              ])
          | Error err ->
            do! ssePatchNode ctx (evalResultError err)
        | None ->
          Response.sseStartResponse ctx |> ignore
          do! ssePatchNode ctx (evalResultError (sprintf "Previous session '%s' not found" sessionId))
      })
    // TUI client API
    yield get "/api/state" (createApiStateHandler q infra)
    yield post "/api/dispatch" (createApiDispatchHandler a.Dispatch)
    yield post "/dashboard/session/create" (createCreateSessionHandler a.CreateSession a.SwitchSession)
    yield post "/dashboard/config/disable-auto-open" (createToggleWarmupAutoOpenHandler a false)
    yield post "/dashboard/config/enable-auto-open" (createToggleWarmupAutoOpenHandler a true)
    yield mapPost "/dashboard/session/switch/{id}"
      (fun (r: RequestData) -> r.GetString("id", ""))
      (fun sid -> createSessionActionHandler q a.SwitchSession false (WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())))
    yield mapPost "/dashboard/session/stop/{id}"
      (fun (r: RequestData) -> r.GetString("id", ""))
      (fun sid -> createSessionActionHandler q a.StopSession true (WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())))
    yield post "/dashboard/session/stop-others" (fun ctx -> task {
      let! sessions = q.GetAllSessions ()
      // "Others" = everyone except the session THIS client is viewing.
      // Read from the per-client viewing-session signal, not a global,
      // so two tabs with different viewing sessions can independently
      // click "stop others" without interfering.
      let! doc = readSignalsJsonSized ctx
      let viewingId =
        match doc.RootElement.TryGetProperty(Signals.ViewingSessionId) with
        | true, prop -> prop.GetString()
        | _ -> ""
      let keepId =
        match String.IsNullOrEmpty viewingId with
        | false ->
          match WorkerProtocol.SessionId.validate viewingId with
          | Ok sid -> sid
          | Error _ -> sessions |> List.tryHead |> Option.map (fun s -> s.Id) |> Option.defaultValue (WorkerProtocol.SessionId.newId ())
        | true -> sessions |> List.tryHead |> Option.map (fun s -> s.Id) |> Option.defaultValue (WorkerProtocol.SessionId.newId ())
      let others =
        sessions
        |> List.filter (fun (s: WorkerProtocol.SessionInfo) -> s.Id <> keepId)
      for s in others do
        let! _ = a.StopSession s.Id
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
    yield mapPost "/dashboard/session/dispose/{id}"
      (fun (r: RequestData) -> r.GetString("id", ""))
      (fun sid -> createSessionActionHandler q a.DisposeSession true (WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())))
    yield mapPost "/dashboard/session/purge/{id}"
      (fun (r: RequestData) -> r.GetString("id", ""))
      (fun sid -> createSessionActionHandler q a.PurgeSession true (WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())))
    // Daemon info endpoint for client discovery (replaces daemon.json)
    yield get "/api/daemon-info" (fun ctx -> task {
      let startedAt =
        let proc = System.Diagnostics.Process.GetCurrentProcess()
        proc.StartTime.ToUniversalTime()
      let! sessionCount = infra.GetSessionCount()
      let data =
        DaemonInfoContract.create
          Environment.ProcessId
          infra.Version
          (startedAt.ToString("o"))
          Environment.CurrentDirectory
          infra.McpPort
          sessionCount
      do! ctx.Response.WriteAsJsonAsync(data)
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
