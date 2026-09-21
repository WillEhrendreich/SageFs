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
open SageFs.Server.CohortInspector
open SageFs.Features.FrictionSqlite

module FalcoResponse = Falco.Response

/// p50/p99 of the eval-to-pixel latency chain (vision §3.4, §7.4), read from
/// the shared tracker for every snapshot build — cheap (a lock + a sort of
/// at most 256 floats) and rendered in the statusline.
let evalToPixelPercentiles () : float option * float option =
  EvalLatencyTrace.percentiles (EvalLatencyTrace.shared.Snapshot())

/// Dashboard CSS — loaded from embedded resource at startup.
/// Served via GET /dashboard/dashboard.css with proper caching.
let dashboardCss =
  let asm = System.Reflection.Assembly.GetExecutingAssembly()
  use stream = asm.GetManifestResourceStream("SageFs.dashboard.css")
  use reader = new StreamReader(stream)
  reader.ReadToEnd()

/// The SHA-256 (uppercase hex) of the pinned Datastar bundle bytes below. This
/// is the ENFORCED pin: DatastarBundleIntegrityTests hashes the embedded
/// resource and fails the build if it drifts from this value, so the served
/// bytes can never silently diverge from the reviewed bundle. Upgrading
/// Datastar means replacing datastar.js AND updating this constant in one
/// reviewed change — exactly "a separate, testable change."
let datastarBundleSha256 =
  "5D6B7794A50A83D82DA962AEC5E382F5AE83AC7AFBC751F903F7A9C6BD433C65"

/// Pinned Datastar client bundle — loaded from the embedded resource and
/// served by the daemon at /dashboard/datastar.js. WHY pinned + self-hosted:
/// the dashboard previously fetched starfederation/datastar@develop from a
/// CDN at runtime — a moving, unversioned branch (supply-chain + XSS
/// surface, and a version skew breaks every open dashboard tab). The bytes
/// below are the exact @develop bundle the dashboard was built against,
/// frozen in-repo; its integrity is pinned by `datastarBundleSha256` above.
let datastarBundle =
  let asm = System.Reflection.Assembly.GetExecutingAssembly()
  use stream = asm.GetManifestResourceStream("SageFs.datastar.js")
  use reader = new StreamReader(stream)
  reader.ReadToEnd()

/// JetBrains Mono (SIL OFL 1.1 — the license text ships alongside and is
/// served at /dashboard/fonts/JetBrainsMono-OFL.txt), self-hosted from
/// embedded resources and served by the daemon at /dashboard/fonts/<file>.
/// WHY: the shell loaded the face from fonts.googleapis.com — the last external
/// request a localhost tool made (offline it degraded silently, and every
/// dashboard open leaked a request to a third party). Only the weights the
/// dashboard CSS uses (400/500/600/700) ship, taken from the official
/// JetBrains/JetBrainsMono v2.304 release.
let dashboardFonts : Map<string, byte array> =
  let asm = System.Reflection.Assembly.GetExecutingAssembly()
  [ "JetBrainsMono-Regular.woff2"
    "JetBrainsMono-Medium.woff2"
    "JetBrainsMono-SemiBold.woff2"
    "JetBrainsMono-Bold.woff2"
    "JetBrainsMono-OFL.txt" ]
  |> List.map (fun file ->
    match asm.GetManifestResourceStream("SageFs.fonts." + file) with
    | null -> failwithf "Embedded dashboard font resource missing: %s" file
    | stream ->
      use stream = stream
      use bytes = new MemoryStream()
      stream.CopyTo bytes
      file, bytes.ToArray())
  |> Map.ofList

/// @font-face for every embedded weight — font-display: swap, so text renders
/// immediately in a fallback face and never blocks on the font — plus the full
/// system monospace fallback stack.
let fontFaceCss =
  let faces =
    [ 400, "Regular"; 500, "Medium"; 600, "SemiBold"; 700, "Bold" ]
    |> List.map (fun (weight, style) ->
      sprintf "@font-face{font-family:'JetBrains Mono';font-style:normal;font-weight:%d;font-display:swap;src:url('/dashboard/fonts/JetBrainsMono-%s.woff2') format('woff2')}" weight style)
    |> String.concat ""
  faces + "body{font-family:'JetBrains Mono','Fira Code','Cascadia Code','Cascadia Mono','SF Mono',Menlo,Monaco,Consolas,'Liberation Mono','DejaVu Sans Mono',ui-monospace,monospace}"

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

/// SSE connection monitor — a server-emitted heartbeat signal plus a
/// client-side staleness timer, both driven through Datastar's own
/// structure-typed API (Ds.signal / Ds.effect / Ds.onInterval), never a
/// hand-patched `window.fetch` or a MutationObserver on `#main`.
///
/// WHY this replaced the old fetch-monkeypatch: the Datastar SSE client
/// resolves the fetch's `r.ok` as soon as response HEADERS arrive — a daemon
/// that dies MID-STREAM errors at the body level, which `.then(r)` never
/// sees, so the old probe never fired on the real failure mode. And the
/// MutationObserver-on-`#main` half only ever HID the banner: it had no path
/// that showed it, and the no-change-SSE-suppression means `#main`
/// legitimately stops mutating while still perfectly connected, so a
/// mutation-based heartbeat would have false-positived anyway.
///
/// This mechanism is transport-independent (works whichever transport
/// Datastar's SSE plugin uses) and immune to the no-change-render guard: the
/// stream loop patches `ConnMonitor.HeartbeatSignal` on a fixed cadence
/// (`Timeouts.dashboardHeartbeat`) REGARDLESS of whether the rendered
/// snapshot changed. The browser just watches the clock against the last
/// value it received.
///
/// WHY there is a SECOND signal (`LastSeenSignal`), and not a direct compare
/// against `HeartbeatSignal` (GLM roast #5, sagefs-roast-2day-cmd.md): the
/// server patches `HeartbeatSignal` with ITS OWN absolute `UtcNow` in
/// milliseconds. Comparing that against the browser's own `Date.now()`
/// (as the original E9 fix did) is a CROSS-CLOCK comparison — if the
/// client's clock is even a little behind the server's, the difference stays
/// small (or goes negative) forever, so the staleness check never trips and
/// the banner fails OPEN on a dead daemon; if the client's clock is ahead,
/// the same expression can exceed the staleness budget immediately on a
/// perfectly healthy daemon. Either direction of skew breaks the guarantee
/// this indicator exists to provide. The fix: never compare the two clocks.
/// `LastSeenSignal` is written ONLY from the browser's own `Date.now()`,
/// every time `HeartbeatSignal` changes (via `Ds.effect`, which re-runs
/// whenever a signal it reads is patched) — so every value on both sides of
/// the later staleness comparison (`connectionStaleCheckExpr`) comes from the
/// SAME clock, and no skew between browser and server can move the result.
module private ConnMonitor =
  /// Server-patched, absolute-clock heartbeat — a monotonic CHANGE TOKEN,
  /// never compared directly against the browser's own clock. Not in the
  /// shared `Signals` module (DashboardTypes.fs) because it is a private
  /// implementation detail of this connection-monitor mechanism; no other
  /// code reads or writes it.
  [<Literal>]
  let HeartbeatSignal = "dsHeartbeatAt"

  /// Client-local arrival timestamp: the browser's own `Date.now()` at the
  /// moment it last observed `HeartbeatSignal` change. Written only by
  /// `heartbeatArrivalEffectExpr`'s `Ds.effect`; read only by
  /// `connectionStaleCheckExpr`'s `Ds.onInterval`. Both reads and writes use
  /// the browser's clock exclusively — this is what makes the staleness
  /// check immune to server/client clock skew.
  [<Literal>]
  let LastSeenSignal = "dsLastSeenAt"

  /// The last `HeartbeatSignal` VALUE this page actually observed. It exists so
  /// that stamping `LastSeenSignal` is idempotent — see
  /// `heartbeatArrivalEffectExpr` for the failure it closes.
  [<Literal>]
  let LastBeatSignal = "dsLastBeatAt"

/// The reactive `data-effect` expression: stamps `LastSeenSignal` with the
/// BROWSER's own `Date.now()` — but ONLY when `HeartbeatSignal` holds a value
/// this page has not seen before.
///
/// It used to stamp unconditionally, `$dsLastSeenAt = ($dsHeartbeatAt,
/// Date.now())`, trusting the effect to run only when the heartbeat changed.
/// It does not. Observed in a real browser with the daemon killed: the
/// heartbeat froze, correctly, but `dsLastSeenAt` kept advancing (Datastar
/// serialises every signal into each SSE reconnect URL, which is how it was
/// seen: 590270 → 592270 → 596270 → 604270 with `dsHeartbeatAt` fixed at
/// 588344). A reactive framework is free to re-evaluate an effect for reasons
/// other than its dependency changing, and every such re-run counted as a
/// sighting, so the page was refreshed by the very outage it existed to
/// detect: `data-connected` stayed "true" and the banner never appeared.
///
/// Comparing against the last value SEEN makes the stamp a function of what
/// arrived rather than of when the effect happened to run, so a spurious re-run
/// does nothing. It keeps the clock-skew fix intact: both sides of the
/// staleness comparison are still the browser's own `Date.now()`.
let private heartbeatArrivalEffectExpr () =
  sprintf
    "$%s !== $%s && ($%s = $%s, $%s = Date.now())"
    ConnMonitor.LastBeatSignal ConnMonitor.HeartbeatSignal
    ConnMonitor.LastBeatSignal ConnMonitor.HeartbeatSignal
    ConnMonitor.LastSeenSignal

/// The reactive `data-on-interval` expression: every `dashboardHeartbeat`
/// tick, compare "now" against `LastSeenSignal` — the browser's OWN
/// timestamp of when it last saw the heartbeat change, not the server's
/// absolute clock value. Both sides of this comparison are the browser's
/// `Date.now()`, so no server/client clock skew can move the result (see the
/// `ConnMonitor` module doc for the regression this replaced). Stale beyond
/// `dashboardStaleAfter` flips `Signals.Connected` false (which `Ds.show` on
/// the banner reacts to) and mirrors the literal string onto
/// `body[data-connected]` for anything else that reads it (tests, other
/// scripts) — matching the pre-existing static-attribute convention rather
/// than relying on Datastar's own attribute-binding boolean stringification.
let private connectionStaleCheckExpr () =
  let staleMs = int64 Timeouts.dashboardStaleAfter.TotalMilliseconds
  sprintf
    "$%s = (Date.now() - $%s) < %d; document.body.setAttribute('data-connected', $%s ? 'true' : 'false')"
    Signals.Connected ConnMonitor.LastSeenSignal staleMs Signals.Connected

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
      function d(a,v){var b=v?{action:a,value:v}:{action:a};fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(b)});}
      var sizes=[10,12,14,16,18,20,24],idx=2;
      document.addEventListener('keydown',function(e){
        if(e.ctrlKey&&(e.key==='='||e.key==='+')){e.preventDefault();idx=Math.min(sizes.length-1,idx+1);document.documentElement.style.setProperty('--font-size',sizes[idx]+'px');}
        if(e.ctrlKey&&e.key==='-'){e.preventDefault();idx=Math.max(0,idx-1);document.documentElement.style.setProperty('--font-size',sizes[idx]+'px');}
        if(e.ctrlKey&&e.key==='Tab'){e.preventDefault();d(e.shiftKey?'sessionCyclePrev':'sessionCycleNext');return;}
        var tag=(e.target.tagName||'').toLowerCase();
        if(tag!=='input'&&tag!=='textarea'){
          var a=null,v=null;
          if(e.key==='j'||e.key==='ArrowDown'){a='sessionNavDown';}
          if(e.key==='k'||e.key==='ArrowUp'){a='sessionNavUp';}
          if(e.key==='Enter'){a='sessionSelect';}
          if(e.key==='x'||e.key==='Delete'){a='sessionDelete';}
          if(e.key==='X'){a='sessionStopOthers';}
          if(e.key==='n'){e.preventDefault();fetch('/dashboard/session/create',{method:'POST'});return;}
          if(e.key>='1'&&e.key<='9'){a='sessionSetIndex';v=String(parseInt(e.key)-1);}
          if(a){e.preventDefault();d(a,v);}
        }
      });
      var h=document.getElementById('%s'),s=document.getElementById('%s');
      if(h&&s){
        var K='sagefs.sidebarWidth';
        function setW(w){document.documentElement.style.setProperty('--sidebar-width',w+'px');}
        try{var v=localStorage.getItem(K);if(v){var n=parseInt(v,10);if(n>=200&&n<=600)setW(n);}}catch(e){}
        var dragging=false,raf=null,pending=0;
        h.addEventListener('mousedown',function(e){dragging=true;e.preventDefault();});
        document.addEventListener('mousemove',function(e){
          if(!dragging)return;
          pending=Math.max(200,Math.min(600,window.innerWidth-e.clientX));
          if(!raf)raf=requestAnimationFrame(function(){setW(pending);raf=null;});
        });
        document.addEventListener('mouseup',function(){
          if(dragging){dragging=false;
            try{localStorage.setItem(K,document.documentElement.style.getPropertyValue('--sidebar-width').replace('px','').trim());}catch(e){}
          }
        });
      }
    })();
  """ DomIds.SidebarResize DomIds.Sidebar) ]

/// Render the dashboard HTML shell with pre-rendered initial content.
/// Providing initialContent eliminates the loading-screen flash — the browser
/// receives a complete first paint without needing an SSE round-trip first.
/// `clientId` identifies THIS page's SSE stream so the backend can retarget
/// it when the viewing-session signal changes; `initialSessionId` is the
/// server-side default for the signal (first available session, or empty for
/// the picker). There is NO session query parameter anywhere — the dashboard
/// is driven entirely by the `viewingSessionId` signal synced with the backend.
let renderShell (version: string) (clientId: string) (initialSessionId: string) (defaultWorkingDir: string) (initialContent: XmlNode) =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ Text.raw "SageFs Dashboard" ]
      // Inline 🧙 favicon (base64 SVG data URI) — matches the header glyph and
      // makes no external request, so the browser tab stops 404-ing on
      // /favicon.ico (roast UX-8). base64 (not raw SVG) so attribute encoding
      // can't mangle the data URI.
      Elem.link [ Attr.rel "icon"; Attr.type' "image/svg+xml"; Attr.href "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMDAgMTAwIj48dGV4dCB5PSIuOWVtIiBmb250LXNpemU9IjkwIj7wn6eZPC90ZXh0Pjwvc3ZnPg==" ]
      // Self-hosted pinned Datastar bundle (see `datastarBundle`) — never
      // fetch a moving CDN branch at runtime.
      Elem.script [ Attr.type' "module"; Attr.src "/dashboard/datastar.js" ] []
      Elem.link [ Attr.rel "stylesheet"; Attr.href "/dashboard/dashboard.css" ]
      // Self-hosted JetBrains Mono (see `dashboardFonts`) — after the
      // stylesheet so its full fallback stack wins; the dashboard makes no
      // external network requests.
      Elem.style [] [ Text.raw fontFaceCss ]
    ]
    Elem.body [ Ds.safariStreamingFix; Attr.create "data-connected" "true" ] [
      Elem.div [ Ds.onInit (Ds.get (sprintf "/dashboard/stream/%s" clientId)); Ds.signal (Signals.HelpVisible, false); Ds.signal (Signals.SidebarOpen, true); Ds.signal (Signals.Connected, true); Ds.signal (Signals.ViewingSessionId, initialSessionId); Ds.signal (Signals.ClientId, clientId); Ds.signal (Signals.Code, ""); Ds.signal (Signals.NewSessionDir, defaultWorkingDir); Ds.signal (Signals.ManualProjects, ""); Ds.signal (Signals.Theme, ""); Ds.signal (Signals.CursorPos, "0"); Ds.signal (Signals.TestFilter, "all"); Ds.signal (Signals.ExpandedDashboard, false); Ds.signal (Signals.BindingsPanelOpen, true); Ds.signal (Signals.FrictionEndpoint, ""); Ds.signal (Signals.FrictionToken, ""); Ds.signal (Signals.FrictionEdits, "{}"); Ds.signal (Signals.FrictionSending, false); Ds.signal (Signals.AlarmBannerOpen, false); Ds.signal (Signals.FailureNarrativesOpen, false); Ds.signal (Signals.FilmstripOpen, false); Ds.signal (Signals.DiagnosticsOpen, false); Ds.signal (Signals.EvaluateSectionOpen, false); Ds.signal (Signals.PerfStatsOpen, false); Ds.signal (Signals.NewSessionOpen, false); Ds.signal (Signals.HotReloadFilesOpen, false); Ds.signal (Signals.FrictionPanelOpen, false); Ds.signal (Signals.FrictionHistoryOpen, false); Ds.signal (Signals.SessionContextOpen, false); Ds.signal (Signals.SessionContextAssembliesOpen, false); Ds.signal (Signals.SessionContextNamespacesOpen, false); Ds.signal (Signals.SessionContextFailedOpensOpen, true); Ds.signal (Signals.SessionContextTimingOpen, false); Ds.signal (Signals.SessionContextFilesOpen, false); Ds.signal (Signals.ShadowedBindingsOpen, false); Ds.signal (Signals.CohortPanelOpen, false); Ds.signal (Signals.CohortMatrixTextOpen, false); Ds.signal (Signals.CohortTerritoryTextOpen, false); Ds.signal (Signals.CohortViewingSeq, "");
                // Disconnect-indicator heartbeat (todo-dashboard-disconnect-indicator.md):
                // seeded to "now" so the very first client-side staleness check
                // (before the stream's first heartbeat patch lands) never
                // false-positives; the stream loop keeps it fresh thereafter.
                // This is a server-clock value used ONLY as a change token —
                // never compared directly against the browser's clock (see
                // `ConnMonitor` module doc, GLM roast #5).
                Ds.signal (ConnMonitor.HeartbeatSignal, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                // Client-local arrival stamp, seeded with a placeholder — it is
                // immediately overwritten by `heartbeatArrivalEffectExpr`'s
                // `Ds.effect`, which fires on page load before the first
                // `Ds.onInterval` tick can ever run, so this seed value is never
                // actually read for a staleness decision.
                Ds.signal (ConnMonitor.LastSeenSignal, 0L);
                // No heartbeat value observed yet, so the first effect run on
                // page load always stamps `LastSeenSignal` fresh.
                Ds.signal (ConnMonitor.LastBeatSignal, 0L);
                // Stamps `LastSeenSignal` with the BROWSER's own `Date.now()`
                // every time `HeartbeatSignal` changes — the client-local-arrival
                // half of the clock-skew fix (see `ConnMonitor` module doc).
                Ds.effect (heartbeatArrivalEffectExpr ());
                // Re-evaluated every `dashboardHeartbeat` tick, client-side —
                // a bounded, named-constant interval, not a hand-rolled
                // setInterval/poll-sleep.
                Ds.onInterval (connectionStaleCheckExpr (), int Timeouts.dashboardHeartbeat.TotalMilliseconds) ] []
      Elem.div [ Attr.id DomIds.ServerStatus; Attr.class' "conn-banner conn-disconnected"; Attr.style "display:none"; Ds.show (sprintf "!$%s" Signals.Connected) ] [
        Text.raw "❌ Daemon not running — start SageFs to continue"
      ]
      Elem.div [ Attr.id DomIds.Main ] [ initialContent ]
      completionInsertScript ()
      autoScrollScript ()
      detailsToggleScript ()
      keyboardHandlerScript ()
    ]
  ]

/// Every session the sidebar lists, in registry order, as typed cards (never
/// re-parsed from the TUI's text) with the per-session enrichments each card
/// renders: tests, coverage, bindings, agent presence.
///
/// Takes the session list as a parameter (rather than fetching it) so a
/// caller that already has a fresh list — the SSE push loop fetches one
/// every tick to reconcile the viewed session — never pays for a second,
/// redundant `GetAllSessions` read building the sidebar cards.
///
/// `viewedContext` carries the currently-viewed session's id and its
/// already-fetched `WarmupContext` (when the caller has one in hand), reused
/// to compute that ONE card's real `SessionHealth` verdict for free. Every
/// other card classifies with `warmup = None` rather than pay a worker HTTP
/// round trip per session per push — see `liveSessionCards`'s doc comment.
let buildSessionCardsFrom
  (q: DashboardQueries)
  (viewedContext: (WorkerProtocol.SessionId * WarmupContext option) option)
  (sessions: WorkerProtocol.SessionInfo list)
  : ParsedSession list =
  let warmupContextFor sid =
    match viewedContext with
    | Some (viewedId, ctx) when viewedId = sid -> ctx
    | _ -> None
  liveSessionCards DateTime.UtcNow q.GetStatusMsg (q.GetSessionEvalCounts ()) warmupContextFor sessions
  |> List.map (fun card ->
    { card with
        TestSummary = q.GetSessionTestSummary card.Id
        CoverageSummary = q.GetSessionCoverageSummary card.Id
        TestTreemapEntries = q.GetSessionTestTreemap card.Id
        CoverageTreemap = q.GetSessionCoverageTreemap card.Id
        BindingEntries = q.GetSessionBindings card.Id
        AgentBadges = q.GetSessionAgentBadges card.Id
        GuidanceCssClass = q.GetSessionGuidanceCss card.Id
        SelfHostStaleness = q.GetSessionSelfHostStaleness card.Id })

/// Standalone entry point for callers that do not already have a fresh
/// session list in hand — fetches once, then delegates to `buildSessionCardsFrom`.
/// No viewed session is known here, so every card classifies conservatively
/// (`warmup = None`).
let buildSessionCards (q: DashboardQueries) : System.Threading.Tasks.Task<ParsedSession list> =
  task {
    let! sessions = q.GetAllSessions ()
    return buildSessionCardsFrom q None sessions
  }

let private buildOutputPanelsFrom
  (q: DashboardQueries)
  (sessions: WorkerProtocol.SessionInfo list)
  (sessionId: WorkerProtocol.SessionId)
  (sessionState: string)
  (warmupProgress: string)
  (wCtx: WarmupContext option)
  : System.Threading.Tasks.Task<XmlNode * XmlNode * XmlNode> =
  task {
    let! previous = q.GetPreviousSessions ()
    let cards = buildSessionCardsFrom q (Some (sessionId, wCtx)) sessions
    let creating = q.IsCreatingSession ()
    let sessionsPanel = renderSessionsForSession (WorkerProtocol.SessionId.value sessionId) cards creating
    let sessionPicker =
      match cards.IsEmpty && not creating with
      // This is a rare defensive fallback (a specific session was requested
      // but zero live sessions exist), not the primary "no session" landing
      // — that's buildNoSessionSnapshotWithSessions below, which threads the
      // real per-connection sort choice. No clientId is available here, so
      // this path always renders the default order.
      | true -> renderSessionPicker previous
      | false -> renderSessionPickerEmpty
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
        // DIAGNOSTIC: log output region content length
        outputRegion |> Option.iter (fun r ->
          Log.info "[buildOutputPanels] outputRegion.Content.Length=%d sessionId=%s" r.Content.Length (WorkerProtocol.SessionId.value sessionId))
        let outNode =
          match outputRegion with
          | Some r ->
            let lines = parseOutputLines r.Content
            match lines.IsEmpty with
            | true -> renderOutputForSession (WorkerProtocol.SessionId.value sessionId) lines emptyPlaceholder
            | false -> renderOutputForSession (WorkerProtocol.SessionId.value sessionId) lines "No output yet"
          | None -> renderOutputForSession (WorkerProtocol.SessionId.value sessionId) [] emptyPlaceholder
        (outNode, sessionsPanel, sessionPicker)
      | None ->
        (renderOutputForSession (WorkerProtocol.SessionId.value sessionId) [] emptyPlaceholder, sessionsPanel, sessionPicker)
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

/// What a dashboard stream should view on its next push. A session can die
/// outside this page's own teardown path — an MCP stop, a worker crash,
/// another client — so every push reconciles against the sessions that exist.
[<RequireQualifiedAccess>]
type ViewingDecision =
  /// The viewed session is still live.
  | Keep of WorkerProtocol.SessionId
  /// The viewed session is gone; view the first live session instead.
  | SwitchTo of WorkerProtocol.SessionId
  /// Nothing is selected, or nothing is left to view.
  | ShowPicker

/// A live session is any that is not Stopped (a Stopped worker is gone). The
/// first live session is the default the dashboard shows when nothing specific
/// is selected — the picker is for "no sessions to show", never a landing
/// state to sit on while a live session exists.
let firstLiveSession (sessions: WorkerProtocol.SessionInfo list) : WorkerProtocol.SessionId option =
  sessions
  |> List.filter (fun s -> s.Status <> WorkerProtocol.SessionLifecycleStatus.Stopped)
  |> List.tryHead
  |> Option.map (fun s -> s.Id)

/// Pure viewing reconciliation: keep a live viewed session; otherwise — whether
/// nothing is selected or the selected session has died — default to the first
/// live session, and show the picker ONLY when there are zero live sessions.
/// A newly-created session must therefore appear in the output panel on the
/// next push, not leave the tab sitting on the picker (the picker exists for
/// the empty-daemon case alone).
let reconcileViewing
  (current: WorkerProtocol.SessionId option)
  (sessions: WorkerProtocol.SessionInfo list)
  : ViewingDecision =
  let live = sessions |> List.filter (fun s -> s.Status <> WorkerProtocol.SessionLifecycleStatus.Stopped)
  match current with
  | Some sid when live |> List.exists (fun s -> s.Id = sid) -> ViewingDecision.Keep sid
  | _ ->
    match live with
    | first :: _ -> ViewingDecision.SwitchTo first.Id
    | [] -> ViewingDecision.ShowPicker

/// Whether a daemon state change can alter the worker-fetched panels (eval
/// stats, hot-reload state, warmup context), so the push must re-fetch them
/// instead of reusing the TTL cache — reusing it across a real change would
/// render identical HTML and wrongly suppress the morph.
let invalidatesWorkerData (change: SseEvent) =
  match change with
  | SseEvent.ModelChanged _
  | SseEvent.HotReloadChanged _
  | SseEvent.FileReloaded _
  | SseEvent.WarmupProgress _
  | SseEvent.SessionReady _
  | SseEvent.SessionSwitched _
  | SseEvent.SessionFaulted _ -> true
  | SseEvent.SessionProgress
  | SseEvent.SystemAlarm _ -> false
  // The dashboard's own stateChangedEvent stream only ever carries "state"
  // channel cases (DaemonMode.fs triggers exactly the nine cases above) —
  // the "session" channel cases below are pushed on a separate broadcast
  // (McpServer's SessionEventBroadcast) and never reach this match today.
  // Kept exhaustive rather than a wildcard so a future rewire that DID
  // route one of these through this stream is forced to decide, here,
  // whether it invalidates the worker-fetched panels.
  | SseEvent.WarmupContextSnapshot _
  | SseEvent.HotReloadSnapshot _
  | SseEvent.HotReloadFileToggled _
  | SseEvent.SessionActivated _
  | SseEvent.SessionCreated _
  | SseEvent.SessionStopped _
  | SseEvent.WorkflowSwitching _
  | SseEvent.WorkflowSwitched _
  | SseEvent.SessionHealthChanged _ -> false

/// Whether a coalesced burst of stream commands asked this connection to view
/// another session.
[<RequireQualifiedAccess>]
type BurstRetarget =
  | NoRetarget
  /// View this session (None = the picker) — the last retarget in the burst.
  | RetargetTo of WorkerProtocol.SessionId option

/// Whether a coalesced burst of stream commands asked this connection to
/// change its cohort time-scrubber target (§6.5, Phase 2 item 16) — the
/// `BurstRetarget` pattern applied to `SetCohortViewingSeq`.
[<RequireQualifiedAccess>]
type BurstCohortViewingSeq =
  | NoChange
  /// Set the viewed seq to this (the last such command in the burst) —
  /// `None` moves this tab back to live.
  | SetTo of int64<SageFs.Measures.ledgerSeq> option

/// One push's worth of coalesced stream commands. A burst of state changes is
/// rendered once, but a retarget arriving inside the burst is never dropped:
/// the browser has already moved its viewing-session signal, and a lost
/// retarget leaves the stream re-rendering the old session over the switch.
/// Same reasoning applies to a cohort-scrub retarget arriving mid-burst.
type StreamBurst = { Retarget: BurstRetarget; WorkerInvalidated: bool; CohortViewingSeq: BurstCohortViewingSeq }

module StreamBurst =
  let empty = { Retarget = BurstRetarget.NoRetarget; WorkerInvalidated = false; CohortViewingSeq = BurstCohortViewingSeq.NoChange }

  let add (burst: StreamBurst) (command: DashboardStreamCommand) =
    match command with
    | DashboardStreamCommand.RetargetView target ->
      { burst with Retarget = BurstRetarget.RetargetTo target }
    | DashboardStreamCommand.StateChange change ->
      { burst with WorkerInvalidated = burst.WorkerInvalidated || invalidatesWorkerData change }
    | DashboardStreamCommand.SetCohortViewingSeq seqOpt ->
      { burst with CohortViewingSeq = BurstCohortViewingSeq.SetTo seqOpt }

  let ofCommands (commands: DashboardStreamCommand list) =
    commands |> List.fold add empty

/// Per-connection render-skip guard (roast-6 Finding #10). The no-change
/// suppression above (`lastPushedMain`) already stops an unchanged tick from
/// SENDING a morph, but it decides that only AFTER paying for the expensive
/// part: `renderMainContent snap |> renderNode` composes the whole page's
/// XmlNode tree and serializes it to an HTML string on every single tick,
/// even when nothing this connection would show actually changed. Building
/// the DashboardSnapshot itself is comparatively cheap (the worker-fetched
/// panels are already TTL-cached above) — the render+string-materialization
/// step is the cost this guard exists to skip.
///
/// A monotonic version tracks the content this connection last rendered.
/// Equal `'Snapshot` values — literally the same data `renderMainContent`
/// would otherwise re-walk — are Skip: `renderMainContent`/`renderNode`
/// never run for that tick, and no HTML string is ever materialized. Any
/// genuine difference (including the very first tick, which has nothing to
/// compare against) is Render, with the version bumped by exactly one.
[<RequireQualifiedAccess>]
module SnapshotRenderGuard =
  /// Monotonic version tag for the content a connection last rendered.
  /// Starts at 0 and only ever increases — bumped exactly once per tick
  /// whose snapshot content actually differs from what was last rendered.
  /// The case is private (mirroring WorkerProtocol.SessionId): a case named
  /// the same as its type is ambiguous to qualify from outside this module
  /// (`X.SnapshotVersion.next` can resolve to the constructor, not the
  /// companion module), so callers go through `initial`/`next` and compare
  /// versions with plain `=`/`>=` — never by unwrapping the int64.
  type SnapshotVersion = private SnapshotVersion of int64

  module SnapshotVersion =
    let initial = SnapshotVersion 0L
    let next (SnapshotVersion v) = SnapshotVersion (v + 1L)

  /// One connection's render memory: the last snapshot it actually rendered,
  /// and the version that content was assigned. `LastRendered = None` means
  /// this connection has never rendered — so the first tick always renders.
  type RenderMemory<'Snapshot> = {
    LastRendered: 'Snapshot option
    Version: SnapshotVersion
  }

  module RenderMemory =
    let initial<'Snapshot> : RenderMemory<'Snapshot> =
      { LastRendered = None; Version = SnapshotVersion.initial }

  [<RequireQualifiedAccess>]
  type Decision<'Snapshot> =
    /// Content changed (or this is the first tick) — render, send, and
    /// remember this as the connection's new RenderMemory.
    | Render of RenderMemory<'Snapshot>
    /// Content is identical to what this connection already rendered —
    /// skip renderMainContent/renderNode/send entirely for this tick.
    | Skip

  /// Pure decision — never touches renderMainContent/renderNode/IO. Equal
  /// `'Snapshot` values (F#'s structural equality, over the exact data the
  /// renderer would otherwise walk) are Skip; anything else is Render with
  /// the version bumped by exactly one past what this connection last saw.
  let decide (memory: RenderMemory<'Snapshot>) (candidate: 'Snapshot) : Decision<'Snapshot> =
    match memory.LastRendered with
    | Some last when last = candidate -> Decision.Skip
    | _ ->
      Decision.Render { LastRendered = Some candidate; Version = SnapshotVersion.next memory.Version }

/// Read the page client id from a signals JSON body; empty when absent.
let private clientIdFromSignals (doc: System.Text.Json.JsonDocument) =
  match doc.RootElement.TryGetProperty(Signals.ClientId) with
  | true, p -> p.GetString()
  | _ -> ""

/// Retarget a specific browser's SSE stream to a viewing session — the
/// signal-driven counterpart of session selection (there is no URL query
/// parameter). The stream re-renders the appropriate output pane and confirms
/// the signal; if the stream has not connected yet the POST's own patch still
/// applies and the stream picks the target up on connect via its default.
let private retargetStream
  (infra: DashboardInfra)
  (clientId: string)
  (sessionId: WorkerProtocol.SessionId option)
  : unit =
  match clientId with
  | "" -> ()
  | id ->
    match infra.ConnectionChannels.TryGetValue id with
    | true, ch ->
      try ch.Post(DashboardStreamCommand.RetargetView sessionId)
      with :? ObjectDisposedException -> ()
    | _ -> ()

/// Adaptive live-bindings subscription for one browser connection. The
/// callback never writes to the SSE response directly — it posts a
/// ModelChanged state-change through the connection's channel so a full
/// #main morph is serialized and cannot interleave with other writes.
/// Module-scope (not nested in the stream handler) so the mailbox loop and
/// the initial/RetargetView paths share the same logic without forward-
/// reference ordering problems.
let private subscribeLiveBindings
  (infra: DashboardInfra)
  (clientId: string)
  (liveBindingsSub: (IDisposable option) ref)
  (sessionId: WorkerProtocol.SessionId)
  : unit =
  liveBindingsSub.Value |> Option.iter (fun d -> d.Dispose())
  let sidStr = WorkerProtocol.SessionId.value sessionId
  liveBindingsSub.Value <-
    infra.LiveBindingsAdaptive
    |> Option.bind (fun store ->
        Some (SageFs.Features.LiveBindingsAdaptive.subscribe store sidStr (fun _ ->
          match infra.ConnectionChannels.TryGetValue clientId with
          | true, ch ->
            try ch.Post(DashboardStreamCommand.StateChange (ModelChanged (0, 0)))
            with :? ObjectDisposedException -> ()
          | _ -> ())))

/// The time-scrubber's push-gate wiring (§6.5, Phase 2 item 16): overrides
/// an already-built `DashboardSnapshot`'s `CohortPanel` with the scrubbed
/// view when THIS connection is not live, using
/// `Features.CohortScrubber.resolveViewedFrame` (the pure gate — see its
/// own doc comment for why a set viewing seq can never resolve back to the
/// live frame). Applied post-hoc to the snapshot rather than by widening
/// `buildDashboardSnapshotWithSessions`/`buildNoSessionSnapshotWithSessions`'s
/// signatures: those functions build the daemon's SHARED state and have no
/// notion of "this one connection" — only the SSE stream loop does.
///
/// Composing this BEFORE `SnapshotRenderGuard.decide` (both call sites, in
/// `pushState`) is what makes scrubbing pin the view: `frameAtSeq` replays
/// a FIXED prefix of an append-only ledger, so its output — and therefore
/// this override's output — never changes from tick to tick while the
/// viewed seq stays the same, even though `pushState` reruns every push.
/// The rest of the page (output, sessions, friction, …) still updates live
/// for this tab; only the cohort section is pinned, in keeping with the
/// single-fat-morph doctrine (`Dashboard.fs`'s header comment) — no new SSE
/// channel, no per-panel fragment renderer.
///
/// The lanes panel is truncated with the SAME `ledgerThroughSeq` clamp
/// `frameAtSeq` uses internally, so the lane view (which reads the ledger
/// directly, not the frame — see `renderCohortLanesPanel`'s doc comment)
/// agrees with the matrix/territory view about what "as of this seq" means.
let private applyCohortViewing
  (infra: DashboardInfra)
  (viewingSeq: int64<SageFs.Measures.ledgerSeq> option)
  (snap: DashboardSnapshot)
  : DashboardSnapshot =
  let ledger = infra.ReadCohortLedger ()
  let latest = Features.CohortScrubber.latestSeq ledger
  let scrubControl = renderCohortScrubControl viewingSeq latest
  match Features.CohortScrubber.resolveViewedFrame ledger [||] (infra.ReadCohortFrame ()) viewingSeq with
  | Features.CohortScrubber.ViewedFrame.Live _ ->
    if snap.SessionId = "" then snap
    else { snap with CohortPanel = Elem.div [] [ scrubControl; snap.CohortPanel ] }
  | Features.CohortScrubber.ViewedFrame.Scrubbed(frame, seq) ->
    let prefixLedger = Features.CohortScrubber.ledgerThroughSeq ledger seq
    { snap with
        CohortPanel =
          Elem.div [] [
            scrubControl
            renderCohortPanel frame
            renderCohortLanesPanel prefixLedger
          ] }

/// This CONNECTION's "Resume Previous" sort choice, keyed by page client id
/// — the same per-tab convention as `currentSessionOpt`/`ConnectionChannels`
/// (a sort choice belongs to one browser tab, never a daemon global). Unlike
/// the cohort scrubber's per-tab state, this needs no push-loop command: the
/// stream loop already knows its own `clientId` and can read this shared
/// dictionary directly on its next tick, so a change made by the POST
/// handler below is picked up naturally without a forced re-render — the
/// POST's own response morph already shows it immediately. Removed when the
/// connection closes (`createStreamHandler`'s `finally`), same as
/// `ConnectionChannels`.
let private previousSessionSortByClient =
  Collections.Concurrent.ConcurrentDictionary<string, PreviousSessionSort>()

/// Build a complete DashboardSnapshot from the current daemon state.
/// Independent of any HTTP/SSE context — called from both the initial GET render
/// and each SSE push. Returns the snapshot, the resolved session ID, and the
/// resolved theme name so the caller can update its tracking state.
///
/// `cachedWorkerData` lets an SSE stream reuse recent worker-fetched values
/// (eval stats / hot-reload state / warmup context) across high-frequency
/// ticks: the three fetches are the expensive per-push cost (worker HTTP
/// round-trips), and the render-diff guard means reusing a cache can never
/// SEND stale HTML — it only makes unchanged ticks cheaper. When None, all
/// three are fetched fresh (the initial GET render and cold pushes).
///
/// `sessions` is likewise a parameter rather than an internal fetch: the SSE
/// push loop already fetches the session list once per tick (to reconcile
/// the viewed session before rendering) and must not pay for a second
/// `GetAllSessions` read just to build the sidebar cards.
let buildDashboardSnapshotWithSessions
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (currentSessionId: WorkerProtocol.SessionId)
  (lastSessionId: WorkerProtocol.SessionId)
  (lastWorkingDir: string)
  (lastThemeName: string)
  (cachedWorkerData: DashboardWorkerCache option)
  (sessions: WorkerProtocol.SessionInfo list)
  : System.Threading.Tasks.Task<DashboardSnapshot * WorkerProtocol.SessionId * string * {| EvalStats: SageFs.Affordances.EvalStats; HotReloadState: {| files: {| path: string; watched: bool |} list; watchedCount: int |} option; WarmupContext: WarmupContext option; FrictionPanel: XmlNode |}> =
  task {
    let sessionId = currentSessionId
    let sid = WorkerProtocol.SessionId.value sessionId
    let lastSid = WorkerProtocol.SessionId.value lastSessionId
    let state = q.GetSessionState sessionId
    let stateStr = SessionState.label state
    let workingDir = q.GetSessionWorkingDir sessionId
    let statsTask =
      match cachedWorkerData with
      | Some cache when cache.SessionId = sessionId -> System.Threading.Tasks.Task.FromResult cache.EvalStats
      | _ -> q.GetEvalStats sessionId
    let hrTask =
      match cachedWorkerData with
      | Some cache when cache.SessionId = sessionId -> System.Threading.Tasks.Task.FromResult cache.HotReloadState
      | _ -> q.GetHotReloadState sessionId
    let wCtxTask =
      match cachedWorkerData with
      | Some cache when cache.SessionId = sessionId -> System.Threading.Tasks.Task.FromResult cache.WarmupContext
      | _ -> q.GetWarmupContext sessionId
    let! stats = statsTask
    let! hrState = hrTask
    let! wCtx = wCtxTask
    let timelineStats = q.GetEvalTimeline()
    let evalStatsView = EvalStatsView.fromStats stats timelineStats
    let daemonHealth = q.GetDaemonHealth()
    // The one true, user-meaningful usability verdict for the VIEWED session
    // (SessionHealth.classify) — computed from the same facts get_fsi_status
    // and /api/sessions use, reusing the wCtx already fetched above for the
    // session-context panel so this costs no extra worker round trip. Never
    // hidden behind the ⊕ disclosure: Degraded/Failed render inline, right
    // under the process-liveness health bar (sagefs-ux-roast.md §1, §1.4 —
    // "the fix that reached no human").
    let viewedHealth =
      match sessions |> List.tryFind (fun s -> s.Id = sessionId) with
      | Some info -> SessionHealth.classify info.Status info.ProjectRoles wCtx
      | None -> SessionHealth.Starting
    let daemonHealthPanel =
      let processLivenessPanel =
        match daemonHealth with
        | Some snap -> renderDaemonHealth (DaemonHealthView.fromSnapshot snap)
        | None -> Elem.div [ Attr.id DomIds.DaemonHealth; Attr.class' "meta" ] []
      Elem.div [] [ processLivenessPanel; renderSessionHealthLine viewedHealth ]
    let failureNarrativesPanel =
      let pairs = q.GetFailureNarratives()
      renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs)
    let diagnosticsPanel = renderCurrentDiagnostics (q.GetCurrentDiagnostics())
    // Filmstrip panel removed from the UI (pushed the UI down for low-value
    // per-eval history); don't spend the per-push GetFilmstripEntries work.
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
    let liveTestingPanel = renderLiveTestingPanel (q.GetLiveTestActivity (WorkerProtocol.SessionId.value sessionId))
    let alarmPanel = renderAlarmBanner (infra.SystemAlarmBuffer.Value)
    let warmupProgress = q.GetWarmupProgress sessionId
    let! outputPanel, sessionsPanel, sessionPicker = buildOutputPanelsFrom q sessions sessionId stateStr warmupProgress wCtx
    // Friction review panel — local store only. Built server-side so the
    // client never assembles raw telemetry. The read is synchronous SQLite;
    // an SSE stream reuses its last-built panel within the worker-data TTL
    // (the view only changes via friction-tool events that arrive through
    // their own push path, and the render-diff guard prevents stale sends).
    let frictionPanelTask =
      match cachedWorkerData with
      | Some cache ->
        match cache.FrictionPanel with
        | Some panel -> System.Threading.Tasks.Task.FromResult panel
        | None -> System.Threading.Tasks.Task.FromResult (Elem.div [ Attr.id DomIds.FrictionPanel ] [])
      | None ->
        task {
          let! frictionStore = q.GetFrictionStore () |> Async.AwaitTask
          match frictionStore with
          | None -> return Elem.div [ Attr.id DomIds.FrictionPanel ] []
          | Some store ->
            let! reportResult = SageFs.Features.McpFrictionRecorder.Recorder.reportDirect store None
            let historyResult = store.ListSentReports ()
            match reportResult, historyResult with
            | Ok bundle, Ok history ->
              // Brief B8: thread the ObservedSignals bundle B7 already
              // computed (reportDirect ran ObservedFriction.detectAll once)
              // straight into the view — this is the single required
              // one-argument addition at this call site; no other logic
              // here changes.
              let view = SageFs.Features.FrictionReviewView.build bundle.Report bundle.ObservedSignals history
              return renderFrictionPanel view
            | _ -> return Elem.div [ Attr.id DomIds.FrictionPanel ] []
        }
    let! frictionPanel = frictionPanelTask
    let evalToPixelP50Ms, evalToPixelP99Ms = evalToPixelPercentiles ()
    // Wait-free (D4): dereferences the published frame pointer, no IO, no
    // mailbox round-trip — cheap enough to call on every push. The lane
    // view (§6.5) is rendered as a sibling, not folded into
    // renderCohortPanel itself — see renderCohortLanesPanel's doc comment.
    let cohortPanel =
      Elem.div [] [
        renderCohortPanel (infra.ReadCohortFrame ())
        renderCohortLanesPanel (infra.ReadCohortLedger ())
      ]
    let snap : DashboardSnapshot = {
              Version = infra.Version
              ConnectionState = DashboardConnectionState.Connected
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
              FilmstripPanel = Elem.div [] []
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
              CohortPanel = cohortPanel
              ActiveProject = q.GetSessionActiveProject sessionId
              ProjectRoles = q.GetSessionProjectRoles sessionId
              App = q.GetSessionApp sessionId
              EvalToPixelP50Ms = evalToPixelP50Ms
              EvalToPixelP99Ms = evalToPixelP99Ms
            }
    return snap, sessionId, themeName, {| EvalStats = stats; HotReloadState = hrState; WarmupContext = wCtx; FrictionPanel = frictionPanel |}
  }

/// Standalone entry point for callers that do not already have a fresh
/// session list in hand (e.g. tests, one-off callers) — fetches once, then
/// delegates to `buildDashboardSnapshotWithSessions`.
let buildDashboardSnapshot
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (currentSessionId: WorkerProtocol.SessionId)
  (lastSessionId: WorkerProtocol.SessionId)
  (lastWorkingDir: string)
  (lastThemeName: string)
  (cachedWorkerData: DashboardWorkerCache option)
  : System.Threading.Tasks.Task<DashboardSnapshot * WorkerProtocol.SessionId * string * {| EvalStats: SageFs.Affordances.EvalStats; HotReloadState: {| files: {| path: string; watched: bool |} list; watchedCount: int |} option; WarmupContext: WarmupContext option; FrictionPanel: XmlNode |}> =
  task {
    let! sessions = q.GetAllSessions ()
    return! buildDashboardSnapshotWithSessions q infra currentSessionId lastSessionId lastWorkingDir lastThemeName cachedWorkerData sessions
  }

/// Build the full-shell dashboard snapshot for the "no session in play" state:
/// the complete dashboard chrome (header, daemon health, sidebar Sessions panel,
/// New Session panel, statusline) renders ALWAYS — only the main-area content
/// differs. With no session to view, the main area shows the session picker
/// (with Resume Previous) instead of a session's output.
///
/// This is the same full shell `buildDashboardSnapshot` produces for a session;
/// session-dependent panels use their empty variants. It must NEVER render a
/// bare picker fragment — an empty #main outside this shell is what caused the
/// 0.6.460 blank screen (PatchElementsNoTargetsFound), and a bare-picker page
/// is what hid the Sessions sidebar in 0.6.470.
///
/// `sessions` is a parameter rather than an internal fetch for the same
/// reason as `buildDashboardSnapshotWithSessions`: the SSE push loop already
/// fetched a fresh list this tick to reconcile the viewed session, and must
/// not pay for a second `GetAllSessions` read to build the sidebar cards.
///
/// `previousSort` is this CONNECTION's chosen "Resume Previous" order —
/// read by the caller from `previousSessionSortByClient` (keyed by page
/// client id, same convention as `currentSessionOpt`/`ConnectionChannels`),
/// never a daemon global, so one tab's sort choice can never leak into
/// another tab's picker. `buildNoSessionSnapshotWithSessions` below is the
/// Recent-default compat entry point for callers with no such choice.
let buildNoSessionSnapshotWithSessionsSorted
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (sessions: WorkerProtocol.SessionInfo list)
  (previousSort: PreviousSessionSort)
  : System.Threading.Tasks.Task<DashboardSnapshot> =
  task {
    let! previous = q.GetPreviousSessions ()
    // The sidebar MUST list the live sessions (so a session created while
    // viewing the picker is clickable without a page reload), mirroring the
    // enrichment buildOutputPanelsFrom applies to the session view's sidebar.
    // The same typed cards the session view lists (Stopped filtered out).
    // No session is in play here, so there is nothing to classify — every
    // card is best-effort (warmup = None), same as buildSessionCards.
    let liveRows = buildSessionCardsFrom q None sessions
    let daemonHealth = q.GetDaemonHealth()
    let daemonHealthPanel =
      match daemonHealth with
      | Some snap -> renderDaemonHealth (DaemonHealthView.fromSnapshot snap)
      | None -> Elem.div [ Attr.id DomIds.DaemonHealth; Attr.class' "meta" ] []
    let failureNarrativesPanel =
      let pairs = q.GetFailureNarratives()
      renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs)
    let diagnosticsPanel = renderCurrentDiagnostics (q.GetCurrentDiagnostics())
    // Filmstrip panel removed from the UI (pushed the UI down for low-value
    // per-eval history); don't spend the per-push GetFilmstripEntries work.
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
    // No session in view: the activity across every session's tests.
    let liveTestingPanel = renderLiveTestingPanel (q.GetLiveTestActivity "")
    let evalToPixelP50Ms, evalToPixelP99Ms = evalToPixelPercentiles ()
    let snap : DashboardSnapshot = {
      Version = infra.Version
      ConnectionState = DashboardConnectionState.Connected
      SessionState = "No session"
      SessionId = ""
      WorkingDir = ""
      WarmupProgress = ""
      WorkflowLabel = "Interactive"
      EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
      AlarmPanel = renderAlarmBanner (infra.SystemAlarmBuffer.Value)
      DaemonHealth = daemonHealthPanel
      FailureNarrativesPanel = failureNarrativesPanel
      DiagnosticsPanel = diagnosticsPanel
      FilmstripPanel = Elem.div [] []
      ThemeName = defaultThemeName
      ConnectionLabel = connectionLabel
      HotReloadPanel = renderHotReloadEmpty
      LiveTestingPanel = liveTestingPanel
      SessionContextPanel = renderSessionContextEmpty
      OutputPanel = renderOutputForSession "" [] "No session in play — create or resume one to start."
      SessionsPanel = renderSessionsForSession "" liveRows false
      // The picker always shows in the no-session state (it is the "no session
      // in play" landing — Quick Start / Open Directory / Resume Previous),
      // while the sidebar lists the live sessions so any of them is one click
      // away. It stays visible even when sessions exist but none is selected.
      SessionPicker = renderSessionPickerSorted previousSort previous
      ThemePicker = renderThemePicker defaultThemeName
      ThemeVars = renderThemeVars defaultThemeName
      BindingsPanel = renderBindingsPanel None
      FrictionPanel = Elem.div [ Attr.id DomIds.FrictionPanel ] []
      // Cohort panel: only rendered when a session is selected (cohort data is
      // daemon-scoped and irrelevant without a session to scope it against).
      CohortPanel = Elem.div [] []
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      EvalToPixelP50Ms = evalToPixelP50Ms
      EvalToPixelP99Ms = evalToPixelP99Ms
    }
    return snap
  }

/// Compat entry point for callers with no per-connection "Resume Previous"
/// sort choice to thread through — defaults to `Recent`. The live push loop
/// and teardown-to-picker path, which DO have a connection's remembered
/// choice, call `buildNoSessionSnapshotWithSessionsSorted` directly.
let buildNoSessionSnapshotWithSessions
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (sessions: WorkerProtocol.SessionInfo list)
  : System.Threading.Tasks.Task<DashboardSnapshot> =
  buildNoSessionSnapshotWithSessionsSorted q infra sessions PreviousSessionSort.Recent

/// Standalone entry point for callers that do not already have a fresh
/// session list in hand — fetches once, then delegates to
/// `buildNoSessionSnapshotWithSessions`. Only called from the initial GET
/// render, which always mints a brand-new client id (no prior sort choice
/// can exist for it yet), so it always renders the default order.
let buildNoSessionSnapshot
  (q: DashboardQueries)
  (infra: DashboardInfra)
  : System.Threading.Tasks.Task<DashboardSnapshot> =
  task {
    let! sessions = q.GetAllSessions ()
    return! buildNoSessionSnapshotWithSessions q infra sessions
  }

/// Create the SSE stream handler that pushes Elm state to the browser.
let createStreamHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (clientId: string)
  : HttpHandler =
  fun ctx -> task {
    SageFs.Instrumentation.sseConnectionsActive.Add(1L)
    Response.sseStartResponse ctx |> ignore

    // The viewing session for this connection is driven by the browser's
    // `viewingSessionId` signal, NOT a URL query parameter. It starts at the
    // same server-side default as the initial render (first available session,
    // or the picker when none exist) and is re-targeted whenever the signal
    // changes via dashboard POST handlers (RetargetView on this channel).
    let! sessions = q.GetAllSessions ()
    // Default to the first LIVE session (mirrors the initial GET); reconcileViewing
    // re-checks on every push, so a session created later still gets picked up.
    let defaultViewingSession = firstLiveSession sessions
    let mutable currentSessionOpt = defaultViewingSession
    // The time-scrubber's per-tab `Viewing` (§6.5, Phase 2 item 16): `None`
    // (the default) means this tab renders the live cohort frame; `Some seq`
    // means it has scrubbed to that past ledger seq. Independent of session
    // retargeting — the cohort is daemon-scoped, not session-scoped (D4), so
    // switching the viewed SESSION never resets a scrub, and scrubbing never
    // affects any other connection's `currentCohortViewingSeq`.
    let mutable currentCohortViewingSeq : int64<SageFs.Measures.ledgerSeq> option = None
    let currentSessionStr = currentSessionOpt |> Option.map WorkerProtocol.SessionId.value |> Option.defaultValue ""
    infra.ConnectionTracker |> Option.iter (fun t -> t.Register(clientId, Browser, currentSessionStr))
    // A dashboard tab is a member too (sagefs-multiagent-vision.md §4.1):
    // record it in the SAME ActivityTracker instance MCP occupancy reads
    // (DaemonMode.fs threads one shared Tracker into both McpContext and
    // DashboardInfra), keyed by MemberId.Browser so it can never collide
    // with — or be evicted by — an MCP connection's identity.
    infra.ActivityTracker
    |> Option.iter (fun tracker ->
      AgentActivityTracker.recordToolCall
        tracker (MemberTable.MemberId.display (MemberTable.MemberId.Browser clientId))
        currentSessionStr None None DateTime.UtcNow)
    let mutable lastSessionId = currentSessionOpt |> Option.defaultValue (WorkerProtocol.SessionId.newId ())
    let mutable lastWorkingDir = ""
    let mutable lastThemeName = defaultThemeName
    // No-change suppression: the last #main HTML actually sent on this stream.
    // pushState renders the full snapshot on every tick (state reads are cheap),
    // but the SSE morph only fires when the rendered HTML differs — a poll tick
    // with nothing changed sends zero payload bytes instead of a full fat morph.
    let mutable lastPushedMain = ""
    // Disconnect-indicator heartbeat (todo-dashboard-disconnect-indicator.md):
    // the last time this connection patched `ConnMonitor.HeartbeatSignal`.
    // Gates `sendHeartbeatIfDue` below to the named `Timeouts.dashboardHeartbeat`
    // cadence, independent of render/state-change traffic — the whole point is
    // that it fires even on a tick the no-change guard suppresses.
    let mutable lastHeartbeatAt = DateTime.MinValue
    // Render-skip guard (roast-6 Finding #10): renderMainContent/renderNode
    // are the expensive part of a tick — a snapshot structurally identical
    // to the one this connection last rendered never reaches them at all.
    // See SnapshotRenderGuard above for why this is safe alongside the
    // byte-compare (`lastPushedMain`) guard, which stays as a cheap,
    // independent safety net right before the send.
    let mutable renderMemory : SnapshotRenderGuard.RenderMemory<DashboardSnapshot> = SnapshotRenderGuard.RenderMemory.initial
    // Worker-data cache: the three worker HTTP fetches (eval stats, hot-reload
    // state, warmup context) are the dominant per-push cost. In poll mode
    // (StateChanged = None) pushState fires every second; reusing the last
    // fetch for up to `workerDataTtlMs` keeps unchanged ticks cheap. The
    // render-diff guard below means a reused cache can never SEND stale HTML —
    // a tick whose rendered output differs still morphs. Real worker changes
    // also arrive as state events in the wired (event) mode; there the push is
    // event-driven and the TTL is short enough that the change is reflected on
    // the next push after the event.
    let workerDataTtlMs = 2000
    let mutable lastWorkerFetch = DateTime.MinValue
    let mutable workerCache : DashboardWorkerCache option = None
    let tryGetFreshWorkerCache (sessionId: WorkerProtocol.SessionId) =
      match workerCache with
      | Some cache when cache.SessionId = sessionId ->
        match (DateTime.UtcNow - lastWorkerFetch).TotalMilliseconds < float workerDataTtlMs with
        | true -> Some cache
        | false -> None
      | _ -> None

    // Adaptive live-bindings subscription for the viewed session. Never write
    // directly to the SSE response from its callback: eval completion can fire
    // it concurrently with the Elm ModelChanged event, so all writes go
    // through pushAgent and a full #main morph is never interleaved.
    let liveBindingsSub : (IDisposable option) ref = ref None

    /// Point this connection at another session (or the picker when None):
    /// every cached artifact is stale and the live-bindings watch follows.
    let retargetTo (sidOpt: WorkerProtocol.SessionId option) =
      currentSessionOpt <- sidOpt
      workerCache <- None
      lastWorkerFetch <- DateTime.MinValue
      lastSessionId <- currentSessionOpt |> Option.defaultValue (WorkerProtocol.SessionId.newId ())
      lastWorkingDir <- ""
      liveBindingsSub.Value |> Option.iter (fun d -> d.Dispose())
      liveBindingsSub.Value <- None
      currentSessionOpt |> Option.iter (subscribeLiveBindings infra clientId liveBindingsSub)

    /// Patches `ConnMonitor.HeartbeatSignal` to "now" when at least
    /// `Timeouts.dashboardHeartbeat` has elapsed since the last patch — the
    /// server half of the disconnect-indicator mechanism
    /// (todo-dashboard-disconnect-indicator.md). Called from every mailbox
    /// loop iteration (busy or idle) so the cadence holds regardless of
    /// state-change traffic or the no-change render guard: a tick that
    /// renders nothing still proves the daemon is alive on the wire.
    let sendHeartbeatIfDue () = task {
      let now = DateTime.UtcNow
      match now - lastHeartbeatAt >= Timeouts.dashboardHeartbeat with
      | false -> ()
      | true ->
        lastHeartbeatAt <- now
        try
          do! Response.ssePatchSignal ctx (SignalPath.sp ConnMonitor.HeartbeatSignal) (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        with
        | :? System.IO.IOException -> ()
        | :? ObjectDisposedException -> ()
        | :? OperationCanceledException -> ()
        | :? System.ArgumentOutOfRangeException -> ()
        | :? System.InvalidOperationException -> ()
    }

    let pushState () = task {
      // A viewed session that died outside this page's teardown path must not
      // keep rendering as a dead "Uninitialized" header: reconcile first.
      let! liveSessions = q.GetAllSessions ()
      match reconcileViewing currentSessionOpt liveSessions with
      | ViewingDecision.Keep _ -> ()
      | ViewingDecision.SwitchTo sid ->
        retargetTo (Some sid)
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value sid)
      | ViewingDecision.ShowPicker ->
        match currentSessionOpt with
        | Some _ -> retargetTo None
        | None -> ()
      match currentSessionOpt with
      | None ->
        // No session in play — push the FULL shell with the picker in the
        // main area (only when it actually changed). This mirrors the initial
        // GET render: the sidebar Sessions panel and chrome stay visible, and
        // the no-change guard compares like-for-like full-shell HTML.
        // liveSessions was already fetched above for reconciliation — reuse
        // it here instead of paying for a second GetAllSessions read.
        // This connection's own "Resume Previous" sort choice, if it has
        // ever made one — defaults to Recent for a tab that hasn't.
        let previousSort = previousSessionSortByClient.GetOrAdd(clientId, PreviousSessionSort.Recent)
        let! snapRaw = buildNoSessionSnapshotWithSessionsSorted q infra liveSessions previousSort
        let snap = applyCohortViewing infra currentCohortViewingSeq snapRaw
        match SnapshotRenderGuard.decide renderMemory snap with
        | SnapshotRenderGuard.Decision.Skip -> () // unchanged tick — renderMainContent/renderNode never run
        | SnapshotRenderGuard.Decision.Render newMemory ->
          renderMemory <- newMemory
          // Render once: the node built here is reused for the patch below
          // instead of calling renderMainContent a second time.
          let mainNode = renderMainContent snap
          let mainHtml = renderNode mainNode
          match mainHtml = lastPushedMain with
          | true -> () // no-change tick — nothing to send
          | false ->
            lastPushedMain <- mainHtml
            do! ssePatchNode ctx mainNode
            // Eval-to-pixel latency chain, stage 5/5: the morph reached the wire.
            EvalLatencyTrace.shared.StampMorphWritten()
            do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) ""
      | Some sessionId ->
      let cached = tryGetFreshWorkerCache sessionId
      // liveSessions was already fetched above for reconciliation — reuse it
      // here instead of paying for a second GetAllSessions read.
      let! snapRaw, newSessionId, newThemeName, rawWorkerData =
        buildDashboardSnapshotWithSessions q infra sessionId lastSessionId lastWorkingDir lastThemeName cached liveSessions
      let snap = applyCohortViewing infra currentCohortViewingSeq snapRaw
      match cached with
      | None ->
        // This push performed the expensive fetches — record them so the next
        // ticks within the TTL window reuse them.
        workerCache <- Some {
          SessionId = sessionId
          EvalStats = rawWorkerData.EvalStats
          HotReloadState = rawWorkerData.HotReloadState
          WarmupContext = rawWorkerData.WarmupContext
          FrictionPanel = Some rawWorkerData.FrictionPanel
        }
        lastWorkerFetch <- DateTime.UtcNow
      | Some _ -> ()
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
      // Keep the browser's viewing-session signal synced with the backend:
      // if the server resolved this connection to a different session than the
      // one that was requested (e.g. the requested session vanished), echo the
      // canonical id back so the picker/sidebar highlight never disagrees.
      if sessionChanged then
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value newSessionId)
      // Render only when the snapshot actually changed for THIS connection
      // (SnapshotRenderGuard): renderMainContent/renderNode never run for a
      // tick whose content is identical to what was last rendered here.
      match SnapshotRenderGuard.decide renderMemory snap with
      | SnapshotRenderGuard.Decision.Skip -> () // unchanged tick — renderMainContent/renderNode never run
      | SnapshotRenderGuard.Decision.Render newMemory ->
        renderMemory <- newMemory
        // Render once, morph only on change: identical snapshots (timer poll
        // ticks with no state movement) send zero payload bytes. The node
        // built here is reused for the patch below instead of calling
        // renderMainContent a second time.
        let mainNode = renderMainContent snap
        let mainHtml = renderNode mainNode
        match mainHtml = lastPushedMain with
        | true -> () // no-change tick — nothing to send
        | false ->
          // DIAGNOSTIC: log when SSE stream sends a changed snapshot
          Log.info "[pushState] sending changed mainHtml.Length=%d sessionId=%s" mainHtml.Length (WorkerProtocol.SessionId.value sessionId)
          lastPushedMain <- mainHtml
          do! ssePatchNode ctx mainNode
          // Eval-to-pixel latency chain, stage 5/5: the morph reached the wire.
          EvalLatencyTrace.shared.StampMorphWritten()
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

      let tcs = Threading.Tasks.TaskCompletionSource()
      use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
      // Serialize SSE writes via MailboxProcessor — no locks, no mutable state.
      // Render-on-first-change with natural back-pressure (roast-6 Phase 0
      // item 1 / vision §3.4, §7.4): NO fixed coalesce delay. A lone state
      // change is rendered the moment it's dequeued; a burst that arrived
      // before dequeue is drained and folded into that one render. If MORE
      // changes queued up while the render was running, the queue is
      // non-empty the instant pushState returns — drain and render again
      // immediately instead of waiting for the next mailbox receive. This
      // still collapses a steady burst into one render per render-duration
      // (never one render per message), while a single isolated change pays
      // zero added latency (the old code always slept 100ms first).
      // Heartbeat: when idle >15s, sends `: keepalive\n\n` SSE comment to prevent
      // proxy/browser timeouts. Integrated into the actor loop to avoid concurrent writes.
      let pushAgent = MailboxProcessor<DashboardStreamCommand>.Start((fun inbox ->
        let renderBurst (seed: StreamBurst) = async {
          let mutable burst = seed
          while inbox.CurrentQueueLength > 0 do
            let! drained = inbox.Receive()
            burst <- StreamBurst.add burst drained
          match burst.Retarget with
          | BurstRetarget.RetargetTo target -> retargetTo target
          | BurstRetarget.NoRetarget -> ()
          match burst.CohortViewingSeq with
          | BurstCohortViewingSeq.SetTo seqOpt -> currentCohortViewingSeq <- seqOpt
          | BurstCohortViewingSeq.NoChange -> ()
          if burst.WorkerInvalidated then
            lastWorkerFetch <- DateTime.MinValue
          try
            do! pushState () |> Async.AwaitTask
          with
          | :? System.IO.IOException -> ()
          | :? ObjectDisposedException -> ()
          | :? OperationCanceledException -> ()
          | :? System.ArgumentOutOfRangeException -> ()
          | :? System.InvalidOperationException -> ()
          | ex -> Log.debug "[Dashboard SSE] pushState failed: %s" ex.Message
        }
        let rec loop () = async {
          // Disconnect-indicator heartbeat: checked on every iteration
          // (busy or idle) so it fires on the named `dashboardHeartbeat`
          // cadence regardless of how much other mailbox traffic there is —
          // a burst of state-change ticks that all render nothing (no-change
          // guard) must not starve it. The idle-timeout below is bounded by
          // the same constant, so a fully quiet connection re-enters this
          // loop often enough to keep the cadence during idle periods too.
          do! sendHeartbeatIfDue () |> Async.AwaitTask
          let! msg = inbox.TryReceive(int Timeouts.dashboardHeartbeat.TotalMilliseconds)
          match msg with
          | None ->
            // Idle timeout — send SSE keepalive comment. Bounded by
            // `Timeouts.dashboardHeartbeat` (stays well under Kestrel's
            // default keep-alive timeout regardless of its configured
            // value). The write is wrapped to swallow connection-closed
            // exceptions so the loop survives transient client disconnects
            // (Datastar will reconnect via its own retry).
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
          | Some (DashboardStreamCommand.RetargetView sidOpt) ->
            // Signal-driven session retarget: a dashboard POST changed the
            // browser's viewing-session signal, so this connection must now
            // push the newly-selected session (or the picker when None).
            retargetTo sidOpt
            try
              do! pushState () |> Async.AwaitTask
            with
            | :? System.IO.IOException -> ()
            | :? ObjectDisposedException -> ()
            | :? OperationCanceledException -> ()
            | :? System.ArgumentOutOfRangeException -> ()
            | :? System.InvalidOperationException -> ()
            | ex -> Log.debug "[Dashboard SSE] pushState after retarget failed: %s" ex.Message
            return! loop ()
          | Some (DashboardStreamCommand.SetCohortViewingSeq seqOpt) ->
            // Signal-driven cohort scrub retarget (§6.5, Phase 2 item 16):
            // this connection's OWN viewed seq changed. Only this
            // connection's next `pushState` is affected — every other tab's
            // mailbox never sees this message at all.
            currentCohortViewingSeq <- seqOpt
            try
              do! pushState () |> Async.AwaitTask
            with
            | :? System.IO.IOException -> ()
            | :? ObjectDisposedException -> ()
            | :? OperationCanceledException -> ()
            | :? System.ArgumentOutOfRangeException -> ()
            | :? System.InvalidOperationException -> ()
            | ex -> Log.debug "[Dashboard SSE] pushState after cohort scrub failed: %s" ex.Message
            return! loop ()
          | Some (DashboardStreamCommand.StateChange change) ->
            // Eval-to-pixel latency chain, stage 4/5 (vision §3.4, §7.4):
            // the push agent dequeued a state-change notification.
            EvalLatencyTrace.shared.StampPushReceived()
            do! renderBurst (StreamBurst.add StreamBurst.empty (DashboardStreamCommand.StateChange change))
            // Back-pressure: a burst that queued up WHILE the render above
            // was running must not wait for the next TryReceive — render it
            // now. Repeats until a render leaves the queue empty.
            while inbox.CurrentQueueLength > 0 do
              do! renderBurst StreamBurst.empty
            return! loop ()
        }
        loop ()), ctx.RequestAborted)
      infra.ConnectionChannels.[clientId] <- pushAgent
      use _sub = infra.StateChanged.Subscribe(fun change ->
        try pushAgent.Post(DashboardStreamCommand.StateChange change)
        with :? ObjectDisposedException -> ())
      // Initial live-bindings subscription — unless the first push already
      // retargeted (and so subscribed) while reconciling a dead session.
      match liveBindingsSub.Value with
      | None -> currentSessionOpt |> Option.iter (subscribeLiveBindings infra clientId liveBindingsSub)
      | Some _ -> ()
      do! tcs.Task
    finally
      // Whichever mode the stream ran in, its live-bindings watch ends with it.
      liveBindingsSub.Value |> Option.iter (fun d -> d.Dispose())
      SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
      infra.ConnectionTracker |> Option.iter (fun t -> t.Unregister(clientId))
      infra.ActivityTracker
      |> Option.iter (fun tracker ->
        AgentActivityTracker.forget tracker (MemberTable.MemberId.display (MemberTable.MemberId.Browser clientId)))
      infra.ConnectionChannels.TryRemove(clientId) |> ignore
      previousSessionSortByClient.TryRemove(clientId) |> ignore
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
          let! snap, _, _, _ =
            buildDashboardSnapshot q infra sessionId sessionId (q.GetSessionWorkingDir sessionId) defaultThemeName None
          // DIAGNOSTIC: log output panel HTML to verify content is present.
          // Render once — the node built here is reused for the patch below.
          let mainNode = renderMainContent snap
          let mainHtml = renderNode mainNode
          Log.info "[eval-POST] mainHtml.Length=%d sessionId=%s" mainHtml.Length (WorkerProtocol.SessionId.value sessionId)
          do! ssePatchNode ctx mainNode
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
                textEnc displayResult
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

/// Create the reset POST handler. `label` names the action in every message
/// ("Reset" / "Hard Reset") so one handler serves both routes.
let createResetHandler
  (label: string)
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
      Response.sseStartResponse ctx |> ignore
      match sessionIdResult with
      | Error errMsg ->
        do! ssePatchNode ctx (evalResultError (sprintf "%s: %s" label errMsg))
      | Ok sessionId ->
        // Immediate feedback: show the in-flight state before the reset
        // itself (which can take seconds — minutes for a hard-reset
        // rebuild) resolves. Mirrors the teardown path's immediate
        // "⏳ Stopping…" card swap (roast UX-8: RESET/HARD_RESET showed
        // nothing until the action itself finished).
        do! ssePatchNode ctx (evalResultInfo (sprintf "%s: in progress…" label))
        let! result = resetSession sessionId
        let msg =
          match result with
          | Ok m -> m
          | Error e -> sprintf "Failed: %s" e
        let resultHtml =
          Elem.div [ Attr.id DomIds.EvalResult ] [
            Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
              textEnc (sprintf "%s: %s" label msg)
            ]
          ]
        do! ssePatchNode ctx resultHtml
        // Clear stale output after reset (Bug #5)
        let clearedOutput =
          Elem.div [ Attr.id DomIds.OutputPanel ] [
            Elem.span [ Attr.class' "meta"; Attr.style "padding: 0.5rem;" ] [
              textEnc (sprintf "%s: %s" label msg)
            ]
          ]
        do! ssePatchNode ctx clearedOutput
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// The ONLY place `/dashboard/switch-workflow` makes a real network call:
/// the SAME REST route VS Code already uses (`POST
/// /api/sessions/{sid}/workflow`, `McpServer.fs`), reached over loopback on
/// the daemon's own MCP/API port (`infra.McpPort`) instead of a second,
/// independent switch implementation — one route, one implementation, for
/// every client that wants to switch a session's workflow.
let switchWorkflowViaApi
  (mcpPort: int)
  (sessionId: WorkerProtocol.SessionId)
  (target: WorkflowTypes.SessionWorkflow)
  : Threading.Tasks.Task<Result<string, string>> =
  task {
    try
      use http = new HttpClient()
      // A HotReload switch can rebuild the target project before the new
      // worker is ready — generous, matching Hard Reset's own expectations
      // (SessionBuild's kill timer) rather than a short eval-style timeout.
      http.Timeout <- TimeSpan.FromMinutes(10.0)
      let url = sprintf "http://127.0.0.1:%d/api/sessions/%s/workflow" mcpPort (WorkerProtocol.SessionId.value sessionId)
      let bodyJson = Text.Json.JsonSerializer.Serialize({| workflow = WorkflowSwitch.requestValue target |})
      use req = new HttpRequestMessage(HttpMethod.Post, url)
      req.Content <- new StringContent(bodyJson, Text.Encoding.UTF8, "application/json")
      let! resp = http.SendAsync(req)
      let! body = resp.Content.ReadAsStringAsync()
      return WorkflowSwitch.parseResponse (int resp.StatusCode) body
    with ex ->
      return Error (sprintf "Could not reach the session API: %s" ex.Message)
  }

/// Create the workflow-switch POST handler. Parametrized over `getCurrentLabel`
/// and `switchWorkflow` (mirroring `createResetHandler`'s injection of
/// `resetSession`) purely for testability — a fake of each lets tests drive
/// every branch (unknown session, bad target, API success, API failure)
/// with no network and no daemon. Production wiring (`createEndpoints`)
/// supplies `switchWorkflowViaApi infra.McpPort`, the only place a real HTTP
/// call happens.
let createWorkflowSwitchHandler
  (getCurrentLabel: WorkerProtocol.SessionId -> string)
  (switchWorkflow: WorkerProtocol.SessionId -> WorkflowTypes.SessionWorkflow -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      let! sessionIdResult, targetRaw = task {
        try
          use! doc = readSignalsJsonSized ctx
          let sidResult =
            match doc.RootElement.TryGetProperty(Signals.ViewingSessionId) with
            | true, prop -> WorkerProtocol.SessionId.validate (prop.GetString())
            | _ -> Error "Missing viewingSessionId"
          let target =
            match doc.RootElement.TryGetProperty("workflowTarget") with
            | true, prop when prop.ValueKind = Text.Json.JsonValueKind.String -> prop.GetString()
            | _ -> ""
          return sidResult, target
        with ex ->
          Log.warn "[Dashboard] workflow-switch request parse failed: %s" ex.Message
          return Error "Failed to parse request", ""
      }
      Response.sseStartResponse ctx |> ignore
      match sessionIdResult, WorkflowTypes.SessionWorkflow.tryOfString targetRaw with
      | Error errMsg, _ ->
        do! ssePatchNode ctx (evalResultError (sprintf "Workflow switch: %s" errMsg))
      | Ok _, None ->
        do! ssePatchNode ctx (evalResultError (sprintf "Workflow switch: unrecognized workflow '%s'" targetRaw))
      | Ok sessionId, Some target ->
        let currentLabel = getCurrentLabel sessionId
        let targetLabel = WorkflowTypes.SessionWorkflow.label target
        // Immediate feedback: swap the picker for a "switching…" status
        // BEFORE awaiting the restart (which can take seconds — a rebuild if
        // the worker needs one) — the teardown path's own best pattern
        // (renderStoppingCard, roast UX-8), applied here.
        do! ssePatchNode ctx (renderWorkflowSwitcherPending targetLabel)
        let! result = switchWorkflow sessionId target
        match result with
        | Ok message ->
          do! ssePatchNode ctx (renderWorkflowSwitcher targetLabel (WorkerProtocol.SessionId.value sessionId))
          do! ssePatchNode ctx (evalResultInfo message)
        | Error err ->
          // Revert to the workflow the session is ACTUALLY still running —
          // never leave the control stuck showing the failed target.
          do! ssePatchNode ctx (renderWorkflowSwitcher currentLabel (WorkerProtocol.SessionId.value sessionId))
          do! ssePatchNode ctx (evalResultError (sprintf "Workflow switch failed: %s" err))
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Cancel the session's in-flight eval. Cooperative on the worker side (CTS
/// cancel + thread interrupt — see `DashboardActions.CancelEval`'s doc): this
/// genuinely stops an eval blocked on I/O or one that checks a cancellation
/// token, but it CANNOT preempt a tight synchronous CPU loop with no yield
/// point (e.g. `while true do ()`) — .NET has no safe way to abort a running
/// thread, so that case is left running until a Hard Reset kills the worker.
/// Only the eval-result slot is patched here with an immediate acknowledgement;
/// the still-pending `/dashboard/eval` request (if cancellation actually takes)
/// resolves on its own and re-renders the full snapshot, clearing $actionLoading.
let createCancelEvalHandler
  (cancelEval: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>)
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
      Response.sseStartResponse ctx |> ignore
      match sessionIdResult with
      | Error errMsg ->
        do! ssePatchNode ctx (evalResultError (sprintf "Cancel: %s" errMsg))
      | Ok sessionId ->
        let! result = cancelEval sessionId
        match result with
        | Ok msg -> do! ssePatchNode ctx (evalResultInfo (sprintf "Cancel: %s" msg))
        | Error err -> do! ssePatchNode ctx (evalResultError (sprintf "Cancel failed: %s" err))
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
/// Falco's route data parses values that look numeric, so the session id
/// "8e940641" reads back as "Infinity"; session routes read the raw value.
let routeValue (key: string) (ctx: HttpContext) : string =
  match ctx.Request.RouteValues.TryGetValue key with
  | true, (:? string as value) -> value
  | _ -> ""

let mapPostRaw (route: string) (read: HttpContext -> 'T) (handler: 'T -> HttpHandler) : HttpEndpoint =
  post route (fun ctx -> handler (read ctx) ctx)

let mapGetRaw (route: string) (read: HttpContext -> 'T) (handler: 'T -> HttpHandler) : HttpEndpoint =
  get route (fun ctx -> handler (read ctx) ctx)

// ── Phase B2: Settings panel edit/clear (server-authoritative, Ds.post + morph) ──

/// Config paths for the settings page: the global store, plus the daemon's own
/// working directory as the repo layer (its `.SageFs/settings.json`). The
/// fail-safe read means a cwd with no repo settings simply contributes nothing,
/// so the panel shows global/default until a repo override is written.
let private settingsPaths () : SageFs.ConfigPaths =
  { GlobalDir = DaemonState.SageFsDir
    Repo = SageFs.RepoRootAt (System.Environment.CurrentDirectory) }

let private settingsRows (paths: SageFs.ConfigPaths) : SettingsPanel.SettingRow list =
  SageFs.SettingsCatalog.pilots
  |> List.map (fun d -> { Descriptor = d; Resolved = SageFs.SettingsCatalog.resolve paths d })

/// The persisted default working directory (session.defaultWorkingDirectory),
/// resolved across the config layers, used to pre-fill the New Session form.
/// Fail-safe: any resolution error yields "" (no default).
let private resolveDefaultWorkingDir () : string =
  let d = SageFs.SettingsCatalog.sessionDefaultWorkingDir
  match SageFs.SettingsCatalog.resolve (settingsPaths ()) d with
  | Ok p -> d.Render p.Effective
  | Error _ -> ""

let private descriptorForSignal (sigName: string) : SageFs.SettingDescriptor option =
  SageFs.SettingsCatalog.pilots |> List.tryFind (fun d -> SettingsPanel.signalName d.Key = sigName)

/// Morph the whole panel back with the given notice — the one authoritative
/// re-render after an edit (Tao of Datastar).
let private morphSettingsPanel (ctx: HttpContext) (notice: SettingsPanel.PanelNotice) (paths: SageFs.ConfigPaths) =
  task {
    Response.sseStartResponse ctx |> ignore
    do! ssePatchNode ctx (SettingsPanel.renderPanel notice (settingsRows paths))
  }

let createSettingsEditHandler : string -> HttpHandler =
  fun sigName ctx -> task {
    try
      let paths = settingsPaths ()
      match descriptorForSignal sigName with
      | None -> do! morphSettingsPanel ctx (SettingsPanel.Rejected("Unknown setting", sigName)) paths
      | Some d ->
        use! doc = readSignalsJsonSized ctx
        let readSignal (name: string) =
          match doc.RootElement.TryGetProperty(name) with
          | true, prop ->
            match prop.ValueKind with
            | System.Text.Json.JsonValueKind.String -> prop.GetString()
            | _ -> prop.GetRawText()
          | _ -> ""
        let rawValue = readSignal sigName
        let layer = SettingsPanel.layerForScope (readSignal Signals.SettingsScope) d
        let notice =
          match SageFs.SettingsCatalog.edit paths layer rawValue d with
          | Ok _ -> SettingsPanel.Applied d.Name
          | Error e -> SettingsPanel.Rejected(d.Name, SageFs.ConfigError.describe e)
        do! morphSettingsPanel ctx notice paths
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

let createSettingsClearHandler : string -> HttpHandler =
  fun sigName ctx -> task {
    try
      let paths = settingsPaths ()
      match descriptorForSignal sigName with
      | None -> do! morphSettingsPanel ctx (SettingsPanel.Rejected("Unknown setting", sigName)) paths
      | Some d ->
        use! doc = readSignalsJsonSized ctx
        let scope =
          match doc.RootElement.TryGetProperty(Signals.SettingsScope) with
          | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.String -> prop.GetString()
          | _ -> SettingsPanel.ScopeGlobal
        let layer = SettingsPanel.layerForScope scope d
        let notice =
          match SageFs.SettingsCatalog.clear paths layer d with
          | Ok _ -> SettingsPanel.Applied (sprintf "%s (reset)" d.Name)
          | Error e -> SettingsPanel.Rejected(d.Name, SageFs.ConfigError.describe e)
        do! morphSettingsPanel ctx notice paths
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

let createSessionActionHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (action: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>)
  (teardown: bool)
  : WorkerProtocol.SessionId -> HttpHandler =
  fun sessionId ctx -> task {
    try
      let sid = WorkerProtocol.SessionId.value sessionId
      // Read the signals so we know which session is currently being viewed
      // and which page (client id) owns this browser tab's SSE stream.
      let viewingId, channelClientId =
        try
          use doc = readSignalsJsonSized ctx |> Async.AwaitTask |> Async.RunSynchronously
          getSignalString doc Signals.ViewingSessionId "viewing-session-id",
          clientIdFromSignals doc
        with _ -> "", ""
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
            // The sidebar lists sessions in registry order — "next in list" is
            // the session after the torn-down one in that same order.
            let orderedIds = sessions |> List.map (fun s -> s.Id)
            let orderedRemaining = remainingIds
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
        retargetStream infra channelClientId (Some nextSession)
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value nextSession)
        // Sync the output, sessions panel, and statusline to the auto-selected next session.
        match q.GetElmRegionsForSession nextSession with
        | Some regions ->
          match regions |> List.tryFind (fun r -> r.Id = "output") with
          | Some outputRegion ->
            do! ssePatchNode ctx (renderOutput (parseOutputLines outputRegion.Content) "No output yet")
          | None -> ()
        | None -> ()
        let! cards = buildSessionCards q
        do! ssePatchNode ctx (renderSessionsForSession (WorkerProtocol.SessionId.value nextSession) cards (q.IsCreatingSession ()))
        let stateLabel = q.GetSessionState nextSession |> SessionState.label
        do! ssePatchNode ctx (
          Elem.div [ Attr.id DomIds.SessionStatus ] [
            Elem.span [ Attr.class' "status status-ready"; Attr.style "border-radius:0;" ] [ textEnc stateLabel ]
          ])
        let switchedDir = q.GetSessionWorkingDir nextSession
        do! ssePatchNode ctx (renderStatuslineLeft stateLabel switchedDir)
      | None when teardown && Result.isOk result ->
        // No sessions remain — show the session picker (with Resume Previous),
        // honoring this same connection's earlier sort choice if it made one.
        retargetStream infra channelClientId None
        let! previous = q.GetPreviousSessions ()
        let previousSort = previousSessionSortByClient.GetOrAdd(channelClientId, PreviousSessionSort.Recent)
        do! ssePatchNode ctx (renderSessionPickerSorted previousSort previous)
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) ""
      | None ->
        // Switch path (or failed teardown): eval form targets the requested session.
        retargetStream infra channelClientId (Some sessionId)
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value sessionId)
        // Immediately sync the output, sessions panel, and statusline to the
        // newly-selected session so the display matches the sidebar highlight.
        match q.GetElmRegionsForSession sessionId with
        | Some regions ->
          match regions |> List.tryFind (fun r -> r.Id = "output") with
          | Some outputRegion ->
            do! ssePatchNode ctx (renderOutput (parseOutputLines outputRegion.Content) "No output yet")
          | None -> ()
        | None -> ()
        let! cards = buildSessionCards q
        do! ssePatchNode ctx (renderSessionsForSession (WorkerProtocol.SessionId.value sessionId) cards (q.IsCreatingSession ()))
        // Patch the tabline status with the switched-to session's state.
        let stateLabel = q.GetSessionState sessionId |> SessionState.label
        do! ssePatchNode ctx (
          Elem.div [ Attr.id DomIds.SessionStatus ] [
            Elem.span [ Attr.class' "status status-ready"; Attr.style "border-radius:0;" ] [ textEnc stateLabel ]
          ])
        // Patch the statusline with the switched-to session's working dir + state.
        let switchedDir = q.GetSessionWorkingDir sessionId
        do! ssePatchNode ctx (renderStatuslineLeft stateLabel switchedDir)
      let msg, cssClass =
        match result with
        | Ok m -> m, "output-line output-info"
        | Error e -> e, "output-line output-error"
      let resultHtml =
        Elem.div [ Attr.id DomIds.EvalResult ] [
          Elem.pre [ Attr.class' cssClass; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            textEnc msg
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
    Elem.pre [ Attr.style "margin: 0; white-space: pre-wrap; font-size: 0.8rem;" ] [ textEnc statusText ]
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
            | Ok bundle ->
              let outgoing = SageFs.Features.FrictionReviewView.buildOutgoingForSend bundle.Report editsJson
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
      // The Projects field's current value — the preview must reflect what
      // Create will ACTUALLY do for what the user has typed right now, not
      // just the untouched auto-detect case (roast-9 #2).
      let manualProjects = getSignalString doc "manualProjects" "manual-projects"
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
              textEnc (sprintf "Directory not found: %s — check the path for typos, or create the directory first." dir)
            ]])
      | false, true ->
        do! pushDiscoverResults ctx dir manualProjects
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Toggle a discovered project/solution path into (or out of) the New
/// Session form's Projects field — clicking a Discover result selects it
/// instead of making the user read it off the screen and retype it
/// (roast-9 #4). The path rides the query string (not a route segment): a
/// relative project path contains '/', and an encoded %2F does not round-trip
/// through ASP.NET route matching (see the set-project handler below for the
/// same lesson).
let createToggleProjectHandler : HttpHandler =
  fun ctx -> task {
    try
      let path =
        match ctx.Request.Query.TryGetValue "path" with
        | true, v -> string v
        | _ -> ""
      use! doc = readSignalsJsonSized ctx
      let current = getSignalString doc "manualProjects" "manual-projects"
      let dir = getSignalString doc "newSessionDir" "new-session-dir"
      Response.sseStartResponse ctx |> ignore
      match String.IsNullOrWhiteSpace path with
      | true -> ()
      | false ->
        let updated = toggleManualProject current path
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ManualProjects) updated
        // Re-state the load plan for the NEW selection. Without this the preview
        // keeps describing the auto-detect result — measured: after clicking
        // SageFs.Demos the field said "SageFs.Demos/SageFs.Demos.fsproj" while the
        // preview still read "Will load SageFs.slnx (the whole solution)", i.e. the
        // preview contradicted what Create would do, which is the exact defect the
        // plan text was added to fix.
        match String.IsNullOrWhiteSpace dir with
        | true -> ()
        | false -> do! pushDiscoverResults ctx dir updated
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the directory-autocomplete POST handler — morphs the datalist with
/// subdirectories matching the partial path as the user types.
let createDirSuggestHandler : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      let dir = getSignalString doc "newSessionDir" "new-session-dir"
      Response.sseStartResponse ctx |> ignore
      do! ssePatchNode ctx (renderDirSuggestions (DirSuggest.suggest dir))
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the create-session POST handler.
let createCreateSessionHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (createSession: string list -> string -> Threading.Tasks.Task<Result<WorkerProtocol.SessionId, string>>)
  (switchSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      let dir = getSignalString doc "newSessionDir" "new-session-dir"
      let manualProjects = getSignalString doc "manualProjects" "manual-projects"
      let channelClientId = clientIdFromSignals doc
      Response.sseStartResponse ctx |> ignore
      // Feedback patches into #discovered-projects, NOT #eval-result: that
      // node lives inside the Evaluate accordion (closed by default), so a
      // validation error patched there is real DOM the user never sees — a
      // click on Create with a blank directory used to measure as a
      // byte-identical #main, i.e. a dead button (roast-9 #1). The reactive
      // `disabled` on the button (DashboardFragments) covers the common
      // case; this is the backstop for every other rejection.
      match String.IsNullOrWhiteSpace dir, Directory.Exists dir with
      | true, _ ->
        do! ssePatchNode ctx (sessionCreateResultError "Working directory is required")
      | false, false ->
        do! ssePatchNode ctx (sessionCreateResultError (sprintf "Directory not found: %s — check the path for typos, or create the directory first." dir))
      | false, true ->
        match resolveSessionProjects dir manualProjects with
        | Error err ->
          // A named project escaped the working directory — refuse loudly
          // instead of quietly creating a session missing a project.
          do! ssePatchNode ctx (sessionCreateResultError (SageFs.SageFsError.describe err))
        | Ok projects ->
          match projects.IsEmpty with
          | true ->
            do! ssePatchNode ctx (sessionCreateResultError "No projects found. Enter paths manually or check the directory.")
          | false ->
            // Immediate feedback: show the in-flight state before the
            // 15-30s warmup resolves — mirrors the teardown path's
            // immediate "⏳ Stopping…" card swap (roast UX-8: session
            // create showed nothing until warmup finished).
            let! preCards = buildSessionCards q
            do! ssePatchNode ctx (renderSessionsForSession "" preCards true)
            do! ssePatchNode ctx (sessionCreateResultInfo (sprintf "Creating session in %s… (warmup can take up to 30s)" dir))
            let! result = createSession projects dir
            match result with
            | Ok newSessionId ->
              // Switch to the new session so the SSE stream picks it up.
              let! _ = switchSession newSessionId
              // Push the new viewing identity so every dashboard action targets it,
              // and retarget this page's SSE stream to the new session (signal-driven
              // session selection — no URL query parameter).
              retargetStream infra channelClientId (Some newSessionId)
              do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value newSessionId)
              do! ssePatchNode ctx (
                Elem.div [ Attr.id DomIds.EvalResult ] [
                  Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem;" ] [
                    textEnc (sprintf "Session '%s' created. Switched to it." (WorkerProtocol.SessionId.value newSessionId))
                  ]
                ])
              // Clear the Discover slot only on SUCCESS — clearing it
              // unconditionally used to wipe out the Error branch's own
              // message the instant it was patched into the same slot.
              do! ssePatchNode ctx (Elem.div [ Attr.id DomIds.DiscoveredProjects ] [])
            | Error msg ->
              do! ssePatchNode ctx (sessionCreateResultError (sprintf "Failed: %s" msg))
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
            textEnc message
          ]
        ]
      Response.sseStartResponse ctx |> ignore
      match String.IsNullOrWhiteSpace dir, Directory.Exists dir with
      | true, _ ->
        do! ssePatchNode ctx (evalResultError "Working directory is required")
      | false, false ->
        do! ssePatchNode ctx (evalResultError (sprintf "Directory not found: %s — check the path for typos, or create the directory first." dir))
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
              // Empty manual input => auto-detect, which is always Ok; the
              // Error arm keeps the match total (it cannot fire here).
              match resolveSessionProjects dir "" with
              | Ok ps -> ps |> List.truncate 1
              | Error _ -> []
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
        do! pushDiscoverResults ctx dir ""
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
      // Legacy TUI JSON stream (deprecated client). Unlike the dashboard, this
      // endpoint keys off a `?sessionId=` query param — the dashboard itself is
      // signal-driven and carries no session in its URL.
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
      // Legacy TUI JSON stream (deprecated client, AGENTS.md) — not a Phase 0
      // target (roast-6 item 1 scopes the Datastar dashboard's push agent
      // and its fixed-delay/polling deletion). Kept minimally in sync with
      // `DashboardInfra.StateChanged` becoming non-optional.
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
      use _sub = infra.StateChanged.Subscribe(fun _ ->
        try pushAgent.Post(())
        with :? ObjectDisposedException -> ())
      do! tcs.Task
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
      Log.warn "[dashboard] /api/dispatch failed: %s" ex.Message
      ctx.Response.StatusCode <- 400
      do! ctx.Response.WriteAsJsonAsync({| error = "Request failed" |})
  }

/// Live-testing toggle route: dispatch the change, then push so the panel shows the
/// result. The button's in-flight indicator covers the round trip.
let createLiveTestingToggleHandler
  (dispatch: SageFsMsg -> unit)
  (triggerStateChange: unit -> unit)
  (msg: SageFsMsg)
  : HttpHandler =
  fun ctx -> task {
    dispatch msg
    triggerStateChange ()
    Response.sseStartResponse ctx |> ignore
  }

/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "the inspector") —
/// `GET /dashboard/inspect/<kind>/<id>`. A standalone request/response page
/// (never a fragment on the dashboard's SSE stream, see `CohortInspector`'s
/// module doc): fetches the same three read models `renderCohortLanesPanel`/
/// `renderCohortTerritory` already read (`infra.ReadCohortFrame`,
/// `infra.ReadCohortLedger`, `q.GetAllSessions`), then hands them to the
/// pure `CohortInspector.inspectRaw` projection. An unrecognized kind or a
/// missing id is a clean 404 page, never a 500.
let createInspectHandler (q: DashboardQueries) (infra: DashboardInfra) (kindRaw: string, idRaw: string) : HttpHandler =
  fun ctx -> task {
    let! sessions = q.GetAllSessions ()
    let frame = infra.ReadCohortFrame ()
    let ledger = infra.ReadCohortLedger ()
    let model = inspectRaw kindRaw idRaw frame ledger sessions
    match model with
    | InspectorModel.NotFound _ -> ctx.Response.StatusCode <- 404
    | InspectorModel.Found _ -> ()
    return! FalcoResponse.ofHtml (renderInspectorPage model) ctx
  }

/// `GET /dashboard/inspect` — the inspector's entity search (§6.5's "`/`
/// opens an entity search"). `?q=` is optional; an absent/blank query
/// renders the search box with no results rather than the whole cohort.
let createInspectSearchHandler (q: DashboardQueries) (infra: DashboardInfra) : HttpHandler =
  fun ctx -> task {
    let query =
      match ctx.Request.Query.TryGetValue "q" with
      | true, v -> v.ToString()
      | false, _ -> ""
    let! sessions = q.GetAllSessions ()
    let frame = infra.ReadCohortFrame ()
    let ledger = infra.ReadCohortLedger ()
    let results = search query frame ledger sessions
    return! FalcoResponse.ofHtml (renderSearchPage query results) ctx
  }

/// The time-scrubber's POST (§6.5, Phase 2 item 16): move THIS tab's cohort
/// view to a past ledger seq, or back to live. Per-connection, the same
/// signal-driven retarget `retargetStream`/`RetargetView` use for session
/// selection — posts `SetCohortViewingSeq` onto the client's OWN SSE
/// channel (`infra.ConnectionChannels`), so scrubbing one tab can only ever
/// reach that tab's `currentCohortViewingSeq` (see
/// `DashboardStreamCommand.SetCohortViewingSeq`'s doc comment). The actual
/// re-render happens on the persistent stream connection (the mailbox
/// loop's `SetCohortViewingSeq` arm), not on this POST's own response —
/// same division of labor as every other retarget handler in this file.
let createCohortScrubHandler (infra: DashboardInfra) : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      let channelClientId = clientIdFromSignals doc
      let raw = getSignalString doc Signals.CohortViewingSeq "cohort-viewing-seq"
      let seqOpt = Features.CohortScrubber.tryParseSeq raw
      match channelClientId with
      | "" -> ()
      | id ->
        match infra.ConnectionChannels.TryGetValue id with
        | true, ch ->
          try ch.Post(DashboardStreamCommand.SetCohortViewingSeq seqOpt)
          with :? ObjectDisposedException -> ()
        | _ -> ()
      Response.sseStartResponse ctx |> ignore
    with
    | :? RequestTooLargeException -> ()
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create all dashboard routes.
let createEndpoints
  (q: DashboardQueries)
  (a: DashboardActions)
  (infra: DashboardInfra)
  : HttpEndpoint list =
  [
    // The `<link rel="icon">` data URI in renderShell stops most browsers
    // from ever asking, but Chromium (and others) still probe /favicon.ico
    // unconditionally as a legacy fallback — the roast measured this exact
    // 404 console error on every page load in headless Chromium despite the
    // inline icon already being in place. Serve the SAME icon bytes here
    // so that fallback probe gets a real 200 instead of a console error.
    yield get "/favicon.ico" (fun ctx -> task {
      ctx.Response.ContentType <- "image/svg+xml"
      ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "public, max-age=86400"
      do! ctx.Response.WriteAsync("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><text y=".9em" font-size="90">🧙</text></svg>""")
    })
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
    // Self-hosted JetBrains Mono + its OFL license — see `dashboardFonts`.
    // The bytes are frozen in the assembly, so they are cacheable forever.
    yield!
      dashboardFonts
      |> Map.toList
      |> List.map (fun (file, bytes) ->
        get ("/dashboard/fonts/" + file) (fun ctx -> task {
          ctx.Response.ContentType <-
            match file.EndsWith(".woff2", StringComparison.Ordinal) with
            | true -> "font/woff2"
            | false -> "text/plain; charset=utf-8"
          ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "public, max-age=31536000, immutable"
          do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
        }))
    yield get "/dashboard" (fun ctx -> task {
      try
        let! sessions = q.GetAllSessions ()
        // The viewing session is chosen by the browser's `viewingSessionId`
        // signal, which starts at a server-side default: the first available
        // session when any exist ("if there is any session, one of those"),
        // otherwise empty so the picker shows. There is NO session query
        // parameter — deep links land on the picker and the signal drives
        // everything thereafter, synced with the backend.
        let clientId = Guid.NewGuid().ToString("N").[..7]
        // Default to the first LIVE session (never a Stopped/dead one); the
        // picker shows only when there are zero live sessions to display.
        match firstLiveSession sessions with
        | Some firstId ->
          let! snap, resolvedId, _, _ = buildDashboardSnapshot q infra firstId (WorkerProtocol.SessionId.newId ()) "" defaultThemeName None
          let html = renderShell infra.Version clientId (WorkerProtocol.SessionId.value resolvedId) (resolveDefaultWorkingDir ()) (renderMainContent snap)
          return! FalcoResponse.ofHtml html ctx
        | None ->
          // No session in play: render the FULL dashboard shell with the
          // session picker in the main area (never a bare picker page — the
          // sidebar Sessions panel and chrome must stay visible so the user
          // can resume/create a session). The stream's no-session push morphs
          // the same state, so the initial HTML must contain #session-picker
          // or Datastar fails the page with PatchElementsNoTargetsFound.
          let! snap = buildNoSessionSnapshot q infra
          let html = renderShell infra.Version clientId "" (resolveDefaultWorkingDir ()) (renderMainContent snap)
          return! FalcoResponse.ofHtml html ctx
      with _ ->
        let clientId = Guid.NewGuid().ToString("N").[..7]
        let! snap = buildNoSessionSnapshot q infra
        let html = renderShell infra.Version clientId "" (resolveDefaultWorkingDir ()) (renderMainContent snap)
        return! FalcoResponse.ofHtml html ctx
    })
    // Stream endpoint for a specific page — the client id is a PATH segment
    // (no session query parameter): this both keys the per-connection channel
    // registry for signal-driven retargets and keeps the URL free of ?session=.
    yield mapGet "/dashboard/stream/{clientId}"
      (fun (r: RequestData) -> r.GetString("clientId", ""))
      (fun clientId -> createStreamHandler q infra clientId)
    // The cockpit inspector (§6.5) — a standalone GET detail page + search,
    // not a fragment on the SSE stream. Two-segment route reads via
    // `routeValue` (not Falco's numeric-sniffing route-value parser — see
    // `routeValue`'s own doc comment) so an id that looks numeric (a test
    // named "1", say) round-trips as the literal string it is.
    yield mapGetRaw "/dashboard/inspect/{kind}/{id}"
      (fun ctx -> routeValue "kind" ctx, routeValue "id" ctx)
      (createInspectHandler q infra)
    yield get "/dashboard/inspect" (createInspectSearchHandler q infra)
    // Phase B: the runtime Settings panel, server-rendered from the resolved
    // config catalog. Global-scoped for this standalone page (the repo-override
    // layer is surfaced per-session; the resolver + store already support it).
    yield get "/dashboard/settings" (fun ctx -> task {
      let rows = settingsRows (settingsPaths ())
      return! FalcoResponse.ofHtml (SettingsPanel.renderPage rows) ctx
    })
    // Edit/clear a setting: Ds.post from the panel -> SettingsCatalog -> morph
    // the whole panel back with a notice. Route param is the sanitised signal
    // name (no dots), mapped back to the descriptor.
    yield mapPostRaw "/dashboard/settings/edit/{sig}" (routeValue "sig") createSettingsEditHandler
    yield mapPostRaw "/dashboard/settings/clear/{sig}" (routeValue "sig") createSettingsClearHandler
    yield post "/dashboard/eval" (createEvalHandler q infra a.EvalCode)
    yield post "/dashboard/eval-file" (createEvalFileHandler q.GetSessionWorkingDir a.EvalCode)
    yield post "/dashboard/completions" (createCompletionsHandler infra.GetCompletions)
    yield post "/dashboard/reset" (createResetHandler "Reset" a.ResetSession)
    yield post "/dashboard/hard-reset" (createResetHandler "Hard Reset" a.HardResetSession)
    yield post "/dashboard/cancel-eval" (createCancelEvalHandler a.CancelEval)
    // The real workflow switcher (sagefs-ux-roast.md §4.1/§4.2/§11 Island B
    // item 4): reuses the already-shipped `POST /api/sessions/{sid}/workflow`
    // over loopback rather than adding a second switch implementation.
    yield post "/dashboard/switch-workflow"
      (createWorkflowSwitchHandler
        (q.GetSessionWorkflow >> WorkflowTypes.SessionWorkflow.label)
        (switchWorkflowViaApi infra.McpPort))
    yield post "/dashboard/clear-output" createClearOutputHandler
    yield post "/dashboard/discover-projects" createDiscoverHandler
    yield post "/dashboard/toggle-project" createToggleProjectHandler
    yield post "/dashboard/dir-suggest" createDirSuggestHandler
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
        Log.warn "[dashboard] /dashboard/set-theme failed: %s" ex.Message
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = "Request failed" |})
    })
    // Change the "Resume Previous" list's sort order — server-authoritative,
    // same shape as /dashboard/set-theme: read the chosen key, remember it
    // for THIS connection (previousSessionSortByClient, keyed by client id —
    // never a daemon global), and morph the picker back with the new order
    // already applied and its <option selected> reflecting it. The
    // persistent SSE stream picks the same choice up on its own next tick
    // (it reads the same dictionary by its own clientId), so the order
    // survives subsequent pushes instead of resetting on the next unrelated
    // state change.
    yield post "/dashboard/session-picker/sort" (fun ctx -> task {
      try
        use! doc = readSignalsJsonSized ctx
        let sortKey =
          match ctx.Request.Query.ContainsKey "sort" with
          | true -> ctx.Request.Query.["sort"].ToString()
          | false ->
            match doc.RootElement.TryGetProperty("sort") with
            | true, prop -> prop.GetString()
            | _ -> ""
        let order = PreviousSessionSort.ofKey sortKey
        let clientId = clientIdFromSignals doc
        match clientId with
        | "" -> ()
        | id -> previousSessionSortByClient.[id] <- order
        let! previous = q.GetPreviousSessions ()
        Response.sseStartResponse ctx |> ignore
        do! ssePatchNode ctx (renderSessionPickerSorted order previous)
        // Patch the signal so data-bind matches the server-rendered
        // <option selected>, same reasoning as set-theme.
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.PreviousSort) (PreviousSessionSort.toKey order)
      with
      | :? RequestTooLargeException -> ()
      | ex ->
        Log.warn "[dashboard] /dashboard/session-picker/sort failed: %s" ex.Message
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = "Request failed" |})
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
              textEnc (sprintf "Session '%s' created." (WorkerProtocol.SessionId.value sessionId))
            ]
          ])
      | Error err ->
        do! ssePatchNode ctx (evalResultError err)
    })
    // Resume previous session (re-creates in same working dir)
    yield mapPostRaw "/dashboard/session/resume/{id}"
      (routeValue "id")
      (fun sessionId -> fun ctx -> task {
        let! previous = q.GetPreviousSessions ()
        let channelClientId =
          try
            use doc = readSignalsJsonSized ctx |> Async.AwaitTask |> Async.RunSynchronously
            clientIdFromSignals doc
          with _ -> ""
        match previous |> List.tryFind (fun s -> s.Id = sessionId) with
        | Some prev ->
          Response.sseStartResponse ctx |> ignore
          let! result = a.CreateSession prev.Projects prev.WorkingDir
          match result with
          | Ok newSessionId ->
            a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
            // Show and select the resumed session — patch the signal AND retarget
            // this page's SSE stream to it (signal-driven; no URL query param).
            retargetStream infra channelClientId (Some newSessionId)
            do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value newSessionId)
            do! ssePatchNode ctx (
              Elem.div [ Attr.id DomIds.EvalResult ] [
                Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
                  textEnc (sprintf "Session '%s' created." (WorkerProtocol.SessionId.value newSessionId))
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
    yield post "/dashboard/cohort/scrub" (createCohortScrubHandler infra)
    yield post "/dashboard/live-testing/enable" (createLiveTestingToggleHandler a.Dispatch infra.TriggerStateChange SageFsMsg.EnableLiveTesting)
    yield post "/dashboard/live-testing/disable" (createLiveTestingToggleHandler a.Dispatch infra.TriggerStateChange SageFsMsg.DisableLiveTesting)
    yield post "/dashboard/session/create" (createCreateSessionHandler q infra a.CreateSession a.SwitchSession)
    yield post "/dashboard/config/disable-auto-open" (createToggleWarmupAutoOpenHandler a false)
    yield post "/dashboard/config/enable-auto-open" (createToggleWarmupAutoOpenHandler a true)
    yield mapPostRaw "/dashboard/session/switch/{id}"
      (routeValue "id")
      (fun sid -> createSessionActionHandler q infra a.SwitchSession false (WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())))
    // Load a different project into a session. There is no in-place "swap the
    // project" in the worker — the project set is fixed when FSI starts — so this
    // is honestly a recreate: new session on the chosen project, then stop the old
    // one. The button that posts here says so (the REPL's bindings go with the old
    // worker). Project comes from the route, so it is never interpolated into markup.
    // The project rides the QUERY STRING, not a path segment: a project path
    // contains '/', and an encoded %2F inside a route parameter does not round-trip
    // through ASP.NET's route matching (observed: the request never reached the
    // handler and the SSE response died as ERR_INCOMPLETE_CHUNKED_ENCODING).
    yield mapPostRaw "/dashboard/session/set-project/{id}"
      (fun ctx ->
        let project =
          match ctx.Request.Query.TryGetValue "project" with
          | true, v -> string v
          | _ -> ""
        routeValue "id" ctx, project)
      (fun (sid, project) -> fun ctx -> task {
        Response.sseStartResponse ctx |> ignore
        let say (text: string) =
          ssePatchNode ctx (
            Elem.div [ Attr.id DomIds.EvalResult ] [
              Elem.pre
                [ Attr.class' "output-line output-result"
                  Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ]
                [ textEnc text ] ])
        match WorkerProtocol.SessionId.validate sid with
        | Error _ -> do! say (sprintf "Not a session id: %s" sid)
        | Ok sessionId ->
          let! sessions = q.GetAllSessions ()
          match sessions |> List.tryFind (fun s -> s.Id = sessionId) with
          | None -> do! say (sprintf "Session '%s' is no longer running." sid)
          | Some existing ->
            let dir = existing.WorkingDirectory
            // Same containment discipline as every other session-create path: a
            // project must resolve inside the session's own directory.
            match DashboardTypes.resolveSessionProjects dir project with
            | Error err -> do! say (SageFsError.describe err)
            | Ok resolved ->
              do! say (sprintf "Loading %s… (the previous session's REPL bindings are discarded)" project)
              let! created = a.CreateSession resolved dir
              match created with
              | Error err -> do! say (sprintf "Could not load %s: %s" project err)
              | Ok newId ->
                let! _ = a.StopSession sessionId
                let! _ = a.SwitchSession newId
                a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
                do! say (sprintf "Session '%s' loaded %s." (WorkerProtocol.SessionId.value newId) project)
      })
    yield mapPostRaw "/dashboard/session/stop/{id}"
      (routeValue "id")
      (fun sid -> createSessionActionHandler q infra a.StopSession true (WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())))
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
            textEnc (sprintf "Stopped %d other session(s)" others.Length)
          ]
        ]
      do! ssePatchNode ctx resultHtml
    })
    yield mapPostRaw "/dashboard/session/dispose/{id}"
      (routeValue "id")
      // Dispose == stop: the per-session .sagefs replay binary is gone, so
      // there is no separate "clear saved memory" step anymore (see the
      // event-sourcing story — the .sagefm manifest is the only durable state).
      (fun sid -> createSessionActionHandler q infra a.StopSession true (WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())))
    yield mapPostRaw "/dashboard/session/purge/{id}"
      (routeValue "id")
      (fun sid -> createSessionActionHandler q infra a.PurgeSession true (WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())))
    // Run App / Stop App: the session's executable project — the only one (or
    // the active one), or a named one when the session has several.
    yield mapPostRaw "/dashboard/run-app/{id}"
      (routeValue "id")
      (fun sid ->
        let sessionId = WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
        createSessionActionHandler q infra (fun s -> a.RunApp s AppRun.RunRequest.DefaultTarget) false sessionId)
    yield mapPostRaw "/dashboard/run-app/{id}/{project}"
      (fun ctx -> routeValue "id" ctx, routeValue "project" ctx)
      (fun (sid, project) ->
        let sessionId = WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
        createSessionActionHandler q infra (fun s -> a.RunApp s (AppRun.RunRequest.Named project)) false sessionId)
    yield mapPostRaw "/dashboard/stop-app/{id}"
      (routeValue "id")
      (fun sid ->
        let sessionId = WorkerProtocol.SessionId.validate sid |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
        createSessionActionHandler q infra a.StopApp false sessionId)
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
