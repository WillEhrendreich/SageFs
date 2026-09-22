/// Dashboard view models, parse functions, and action dispatch types.
/// Pure domain — no Falco, no HTML, no HTTP.
module SageFs.Server.DashboardTypes

open System
open System.IO
open System.Text.RegularExpressions
open SageFs
open SageFs.Measures
open SageFs.Utils
open SageFs.Affordances
open SageFs.ProjectLoading
open SageFs.Features.LiveTesting
open Falco.Markup
open Falco.Datastar
open StarFederation.Datastar.FSharp

/// Shared DOM element IDs — single source of truth for strings that cross
/// the F#/JS boundary (used in both Attr.id and getElementById calls).
[<RequireQualifiedAccess>]
module DomIds =
  let [<Literal>] Main = "main"
  let [<Literal>] OutputPanel = "output-panel"
  /// The "N new evals ↓" pill over the bottom of the output panel.
  let [<Literal>] OutputNewEvals = "output-new-evals"
  let [<Literal>] SessionsPanel = "sessions-panel"
  let [<Literal>] EvalResult = "eval-result"
  let [<Literal>] EvalTextarea = "eval-textarea"
  let [<Literal>] EvalStats = "eval-stats"
  let [<Literal>] EvaluateSection = "evaluate-section"
  let [<Literal>] SessionStatus = "session-status"
  let [<Literal>] SessionPicker = "session-picker"
  let [<Literal>] SessionContext = "session-context"
  let [<Literal>] DiagnosticsPanel = "diagnostics-panel"
  let [<Literal>] FilmstripPanel = "filmstrip-panel"
  let [<Literal>] DiscoveredProjects = "discovered-projects"
  let [<Literal>] DirSuggestions = "dir-suggestions"
  let [<Literal>] HotReloadPanel = "hot-reload-panel"
  let [<Literal>] LiveTestingPanel = "live-testing-panel"
  let [<Literal>] TestTrace = "test-trace"
  let [<Literal>] ThemeVars = "theme-vars"
  let [<Literal>] ThemePicker = "theme-picker"
  let [<Literal>] ServerStatus = "server-status"
  let [<Literal>] CompletionDropdown = "completion-dropdown"
  let [<Literal>] KeyboardHelp = "keyboard-help"
  let [<Literal>] KeyboardHelpWrapper = "keyboard-help-wrapper"
  let [<Literal>] ConnectionCounts = "connection-counts"
  let [<Literal>] EditorArea = "editor-area"
  let [<Literal>] FrictionSendStatus = "friction-send-status"
  let [<Literal>] FrictionPanel = "friction-panel"
  let [<Literal>] FrictionDrawer = "friction-drawer"
  let [<Literal>] FrictionDrawerOverlay = "friction-drawer-overlay"
  let [<Literal>] OutputSection = "output-section"
  let [<Literal>] Sidebar = "sidebar"
  let [<Literal>] SidebarResize = "sidebar-resize"
  let [<Literal>] BindingsPanel = "bindings-panel"
  let [<Literal>] DaemonHealth = "daemon-health"
  let [<Literal>] FailureNarratives = "failure-narratives"
  let [<Literal>] AlarmBanner = "alarm-banner"
  let [<Literal>] CohortPanel = "cohort-panel"
  let [<Literal>] CohortMatrix = "cohort-matrix"
  let [<Literal>] CohortTerritory = "cohort-territory"
  let [<Literal>] CohortLanes = "cohort-lanes"
  let [<Literal>] CohortScrubber = "cohort-scrubber"
  /// The workflow picker in the tabline — replaces the old read-only badge
  /// (roast §4.1/§4.2/§11 Island B item 4). One id, morphed in place both
  /// for the optimistic "switching…" state and the final result.
  let [<Literal>] WorkflowSwitcher = "workflow-switcher"
  /// The viewed session's true `SessionHealth` verdict, rendered under the
  /// process-liveness daemon health bar — quiet for Starting/Healthy, the
  /// full reason inline for Degraded/Failed. Never hidden behind a disclosure.
  let [<Literal>] SessionHealthLine = "session-health-line"

/// Datastar signal names — shared between Ds.signal init and Ds.bind/Ds.show refs.
[<RequireQualifiedAccess>]
module Signals =
  let [<Literal>] ViewingSessionId = "viewingSessionId"
  /// Per-page connection id — generated at initial render and echoed by every
  /// @post so the server can retarget THIS tab's SSE stream when the viewing
  /// session signal changes (the dashboard is signal-driven; there is no
  /// session query parameter anymore).
  let [<Literal>] ClientId = "clientId"
  let [<Literal>] Code = "code"
  let [<Literal>] HelpVisible = "helpVisible"
  let [<Literal>] SidebarOpen = "sidebarOpen"
  let [<Literal>] NewSessionDir = "newSessionDir"
  let [<Literal>] ManualProjects = "manualProjects"
  let [<Literal>] EvalLoading = "evalLoading"
  let [<Literal>] DiscoverLoading = "discoverLoading"
  let [<Literal>] CreateLoading = "createLoading"
  /// Single in-flight signal shared by the eval-actions row (EVAL / RESET /
  /// HARD_RESET) so every control is disabled while ANY action is running.
  let [<Literal>] ActionLoading = "actionLoading"
  /// The Cancel button's OWN in-flight indicator — deliberately separate from
  /// ActionLoading. Cancel must stay clickable (and visible) for exactly the
  /// duration ActionLoading is true; binding its own `disabled` to
  /// ActionLoading would make it dead on arrival. This signal only guards
  /// against double-submitting the cancel request itself.
  let [<Literal>] CancelLoading = "cancelLoading"
  let [<Literal>] ConfigLoading = "configLoading"
  /// In-flight indicator for a Settings panel save/reset (Phase B2).
  let [<Literal>] SettingsSaving = "settingsSaving"
  /// Which layer a Settings edit targets: "global" or "repo" (Phase C1).
  let [<Literal>] SettingsScope = "settingsScope"
  let [<Literal>] LiveTestingLoading = "liveTestingLoading"
  let [<Literal>] TempLoading = "tempLoading"
  let [<Literal>] Theme = "theme"
  /// The "Resume Previous" list's chosen sort order (`PreviousSessionSort`,
  /// `DashboardFragments.fs`) — re-seeded on every picker render so
  /// Datastar's data-bind can never leave the `<select>` showing an order
  /// the server didn't choose, mirroring `Signals.Theme`.
  let [<Literal>] PreviousSort = "previousSort"
  let [<Literal>] CursorPos = "cursorPos"
  let [<Literal>] TestFilter = "testFilter"
  let [<Literal>] ExpandedDashboard = "expandedDashboard"
  let [<Literal>] BindingsPanelOpen = "bindingsPanelOpen"
  let [<Literal>] FrictionDrawerOpen = "frictionDrawerOpen"
  // Accordion open/closed state — one signal per static <details> panel, so
  // the panel survives the ~1s SSE-fallback morph (see signalDetails).
  let [<Literal>] AlarmBannerOpen = "alarmBannerOpen"
  let [<Literal>] FailureNarrativesOpen = "failureNarrativesOpen"
  let [<Literal>] FilmstripOpen = "filmstripOpen"
  let [<Literal>] DiagnosticsOpen = "diagnosticsOpen"
  let [<Literal>] EvaluateSectionOpen = "evaluateSectionOpen"
  /// Reveals the eval performance detail (avg/min/max, percentiles, eval-to-pixel
  /// latency) — hidden by default; perf is noise unless you're chasing a slowdown.
  let [<Literal>] PerfStatsOpen = "perfStatsOpen"
  let [<Literal>] NewSessionOpen = "newSessionOpen"
  let [<Literal>] HotReloadFilesOpen = "hotReloadFilesOpen"
  let [<Literal>] FrictionPanelOpen = "frictionPanelOpen"
  let [<Literal>] FrictionHistoryOpen = "frictionHistoryOpen"
  let [<Literal>] SessionContextOpen = "sessionContextOpen"
  let [<Literal>] SessionContextAssembliesOpen = "sessionContextAssembliesOpen"
  let [<Literal>] SessionContextNamespacesOpen = "sessionContextNamespacesOpen"
  /// Failed-opens is expanded by default — a warning the user should see, not
  /// hide behind a click.
  let [<Literal>] SessionContextFailedOpensOpen = "sessionContextFailedOpensOpen"
  let [<Literal>] SessionContextTimingOpen = "sessionContextTimingOpen"
  let [<Literal>] SessionContextFilesOpen = "sessionContextFilesOpen"
  let [<Literal>] ShadowedBindingsOpen = "shadowedBindingsOpen"
  let [<Literal>] CohortPanelOpen = "cohortPanelOpen"
  /// The cohort matrix's character-grid fallback (§6.5 "an image is not a
  /// document") — collapsed by default; the PNG picture is the primary view.
  let [<Literal>] CohortMatrixTextOpen = "cohortMatrixTextOpen"
  /// The territory map's text-legend fallback (§6.5 "a picture is not a
  /// document", applied to the territory SVG the same way the matrix's
  /// character grid applies to its PNG) — collapsed by default.
  let [<Literal>] CohortTerritoryTextOpen = "cohortTerritoryTextOpen"
  let [<Literal>] CohortLanesPanelOpen = "cohortLanesPanelOpen"
  /// The lane view's text-legend fallback (§6.5 "a picture is not a
  /// document"), same convention as `CohortTerritoryTextOpen` — collapsed by
  /// default.
  let [<Literal>] CohortLanesTextOpen = "cohortLanesTextOpen"
  /// The time-scrubber's viewed ledger seq (§6.5, Phase 2 item 16) — "" (the
  /// default) means this tab is live; a numeric string means this tab has
  /// scrubbed to that past seq. Per-tab, like `ViewingSessionId`: two tabs
  /// scrubbing to different seqs (or one live, one scrubbed) never interfere
  /// with each other — see `DashboardStreamCommand.SetCohortViewingSeq`.
  let [<Literal>] CohortViewingSeq = "cohortViewingSeq"
  let [<Literal>] FrictionEndpoint = "frictionEndpoint"
  let [<Literal>] FrictionToken = "frictionToken"
  let [<Literal>] FrictionEdits = "frictionEdits"
  let [<Literal>] FrictionSending = "frictionSending"
  /// Dashboard connection state — true when SSE stream is alive, false when
  /// disconnected. Set by connectionMonitorScript on fetch failure. The
  /// browser-side type system: when connected=false, NO UI element may show
  /// "Ready" or "No session" — only the disconnected overlay is legal.
  let [<Literal>] Connected = "connected"
  /// In-flight indicator for the workflow switcher (`WorkflowSwitch`) — its
  /// own signal, not shared with `ActionLoading`, because a workflow switch
  /// restarts the session (seconds, sometimes a rebuild) and must not grey
  /// out EVAL/RESET while it runs, nor be greyed out BY them.
  let [<Literal>] WorkflowSwitchLoading = "workflowSwitchLoading"
  // Chat-style scrolling of the output panel (see `OutputFollow`). The three
  // `OutputFeed*` signals are the server's: `renderOutputForSession` renders
  // them onto #output-panel, so every morph carries the current values. The
  // others are the browser's own and live in the shell outside #main, so no
  // morph can touch them. All of them start with `_`, which tells Datastar to
  // keep them out of the signals it sends with every @post: they're display
  // state, and the backend has no use for them.
  /// Server: the session the output panel is showing.
  let [<Literal>] OutputFeedSession = "_outputFeedSession"
  /// Server: how many evals have finished in that session.
  let [<Literal>] OutputFeedEvals = "_outputFeedEvals"
  /// Server: changes whenever the rendered output changes, so following
  /// works for output that isn't an eval result (app logs, printfn).
  let [<Literal>] OutputFeedRev = "_outputFeedRev"
  /// Browser: true while the panel sits at the bottom and follows new output.
  let [<Literal>] OutputPinned = "_outputPinned"
  /// Browser: `OutputFeedEvals` as it was when you scrolled away.
  let [<Literal>] OutputSeenEvals = "_outputSeenEvals"
  /// Browser: the session the pinned state belongs to. When the panel
  /// switches to another session, following starts over.
  let [<Literal>] OutputFollowSession = "_outputFollowSession"
  /// Browser: the panel's scrollTop at the last scroll event, so the scroll
  /// handler can tell you moving up from the layout moving under you.
  let [<Literal>] OutputScrollTop = "_outputScrollTop"

/// Pure logic for the dashboard's workflow switcher (sagefs-ux-roast.md
/// Island A item 3 / this session's Island B item 2 — "the dashboard cannot
/// see the workflow at all" badge upgraded to a real control). Deliberately
/// has no dependency on `DashboardSnapshot`'s `WorkflowLabel: string` field:
/// the picker only needs to compare each option's OWN label against the
/// current session's label to decide which `<option>` is selected, so no
/// new required field had to be threaded through every existing
/// `DashboardSnapshot` literal across the test suite.
[<RequireQualifiedAccess>]
module WorkflowSwitch =
  /// The three workflows the dashboard picker offers, in display order.
  let options : WorkflowTypes.SessionWorkflow list =
    [ WorkflowTypes.SessionWorkflow.Interactive
      WorkflowTypes.SessionWorkflow.LiveTesting
      WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults ]

  /// The exact string `POST /api/sessions/{sid}/workflow` accepts (one of
  /// `WorkflowTypes.SessionWorkflow.tryOfString`'s canonical aliases) — NOT
  /// the display label, which contains spaces `tryOfString` does not accept.
  let requestValue = function
    | WorkflowTypes.SessionWorkflow.Interactive -> "interactive"
    | WorkflowTypes.SessionWorkflow.LiveTesting -> "livetesting"
    | WorkflowTypes.SessionWorkflow.HotReload _ -> "hotreload"

  /// Parse the response body `POST /api/sessions/{sid}/workflow` returns
  /// (`McpServer.fs`'s `mapSessionRoutes`) into the plain `Result<string,
  /// string>` shape every other `DashboardActions` member already uses. Two
  /// distinct failure shapes exist on that route and both must parse: a
  /// direct `{success:false; error}` (unrecognized workflow / malformed
  /// session id) and a bare `SageFsError.toJson` object `{case; fields;
  /// message; suggestedAction}` with no `success` key at all (e.g.
  /// session-not-found). Never throws: an unreadable body degrades to a
  /// generic message naming the HTTP status rather than crashing the
  /// handler — the switch may already have happened or not, but the
  /// dashboard must always be able to show SOMETHING.
  let parseResponse (statusCode: int) (body: string) : Result<string, string> =
    try
      use doc = System.Text.Json.JsonDocument.Parse(body)
      let root = doc.RootElement
      let tryStr (name: string) =
        match root.TryGetProperty(name) with
        | true, p when p.ValueKind = System.Text.Json.JsonValueKind.String -> Some (p.GetString())
        | _ -> None
      let succeeded =
        match root.TryGetProperty("success") with
        | true, p -> p.ValueKind = System.Text.Json.JsonValueKind.True
        | false, _ -> false
      match succeeded with
      | true ->
        let workflow = tryStr "workflow" |> Option.defaultValue ""
        tryStr "message"
        |> Option.defaultValue (sprintf "Switched to %s" workflow)
        |> Ok
      | false ->
        tryStr "error"
        |> Option.orElse (tryStr "message")
        |> Option.defaultValue (sprintf "Workflow switch failed (HTTP %d)" statusCode)
        |> Error
    with _ ->
      Error (sprintf "Workflow switch returned an unreadable response (HTTP %d)" statusCode)

/// Directory autocomplete for the New Session working-directory input. `split`
/// is pure and unit-tested; `suggest` adds the one filesystem read.
[<RequireQualifiedAccess>]
module DirSuggest =
  /// Split a partial path into (directory-to-list, name-prefix). A path ending
  /// in a separator lists that directory's children; otherwise the last segment
  /// is a case-insensitive prefix filter within its parent. Pure.
  let split (partial: string) : string * string =
    let p = (partial |> Option.ofObj |> Option.defaultValue "").Trim()
    if p = "" then "", ""
    elif p.EndsWith "/" || p.EndsWith(string System.IO.Path.DirectorySeparatorChar) then p, ""
    else
      let dir = System.IO.Path.GetDirectoryName p
      let name = System.IO.Path.GetFileName p
      (if isNull dir then "" else dir), name

  /// Matching subdirectories for a partial path. Fail-safe (any error → []),
  /// directories only, capped at 20, sorted by name. Empty input → no
  /// suggestions (don't spam the picker before the user has typed anything).
  let suggest (partial: string) : string list =
    let p = (partial |> Option.ofObj |> Option.defaultValue "").Trim()
    if p = "" then [] else
    let dir, prefix = split p
    let dirToList =
      if dir = "" then (if p.StartsWith "/" then "/" else System.Environment.CurrentDirectory)
      else dir
    try
      System.IO.Directory.GetDirectories dirToList
      |> Array.filter (fun d -> (System.IO.Path.GetFileName d).StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
      |> Array.sortBy System.IO.Path.GetFileName
      |> Array.truncate 20
      |> Array.toList
    with _ -> []

/// Precomputed syntax-color RGB → CSS class lookup (eliminates 12-branch if/elif chain)
let syntaxColorLookup =
  let t = Theme.defaults
  dict [
    Theme.hexToRgb t.SynKeyword, "syn-keyword"
    Theme.hexToRgb t.SynString, "syn-string"
    Theme.hexToRgb t.SynComment, "syn-comment"
    Theme.hexToRgb t.SynNumber, "syn-number"
    Theme.hexToRgb t.SynOperator, "syn-operator"
    Theme.hexToRgb t.SynType, "syn-type"
    Theme.hexToRgb t.SynFunction, "syn-function"
    Theme.hexToRgb t.SynModule, "syn-module"
    Theme.hexToRgb t.SynAttribute, "syn-attribute"
    Theme.hexToRgb t.SynPunctuation, "syn-punctuation"
    Theme.hexToRgb t.SynConstant, "syn-constant"
    Theme.hexToRgb t.SynProperty, "syn-property"
  ]

let defaultThemeName = "Kanagawa"

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

/// Chat-style scrolling for the output panel, the way a chat app does it.
/// Sitting at the bottom, the panel follows new output. Scrolled up, it holds
/// still, and a pill over the bottom counts the evals that landed since you
/// scrolled away ("3 new evals ↓"). Click it, or scroll back down yourself,
/// and it follows again.
///
/// The split of who owns what is the whole trick. The server owns the facts:
/// which session the panel shows, how many evals have finished there, and a
/// revision of the rendered output. They ride on #output-panel as Datastar
/// signals, so every fat morph of #main re-renders them. The browser owns only
/// what the server can't know: whether you're pinned to the bottom, and the
/// eval count at the moment you scrolled away. Those are signals declared in
/// the shell, outside #main, so a morph never resets them. The pill is the
/// difference between the two counts.
///
/// Everything here is either pure F# or a Datastar expression built from the
/// same constants, so the browser's pill text and `pillLabel` can't drift.
[<RequireQualifiedAccess>]
module OutputFollow =
  /// How close to the bottom still counts as "at the bottom". A few px of
  /// slack, so sub-pixel rounding at odd zoom levels never unpins you.
  let atBottomTolerancePx = 24

  let [<Literal>] private OneEval = "new eval"
  let [<Literal>] private ManyEvals = "new evals"
  let [<Literal>] private DownArrow = "↓"

  /// The pill's accessible name. It says what clicking does; the count is in
  /// the visible text and in the polite live region.
  let [<Literal>] JumpLabel = "Jump to the newest output"

  /// Evals that finished since you scrolled away. Never negative: a smaller
  /// count than the one you left (a restarted daemon) just means none.
  let unseenEvals (finished: int) (seenWhenScrolledAway: int) =
    max 0 (finished - seenWhenScrolledAway)

  /// "1 new eval ↓", "3 new evals ↓".
  let pillLabel (unseen: int) =
    match unseen with
    | 1 -> sprintf "%d %s %s" unseen OneEval DownArrow
    | n -> sprintf "%d %s %s" n ManyEvals DownArrow

  /// `unseenEvals`, in the browser.
  let unseenExpr =
    sprintf "Math.max(0, $%s - $%s)" Signals.OutputFeedEvals Signals.OutputSeenEvals

  /// `pillLabel`, in the browser.
  let pillLabelExpr =
    sprintf "(n => n + (n === 1 ? ' %s' : ' %s') + ' %s')(%s)" OneEval ManyEvals DownArrow unseenExpr

  /// The pill shows only while you're scrolled up and something new landed.
  let pillShowExpr =
    sprintf "!$%s && %s > 0" Signals.OutputPinned unseenExpr

  /// Text for the polite live region: the pill label while it shows, empty
  /// otherwise. It only changes when the count does, so a screen reader hears
  /// one short update per new eval and nothing while you're following.
  let announceExpr =
    sprintf "(%s) ? %s : ''" pillShowExpr pillLabelExpr

  /// Clicking the pill re-pins. The follow effect does the actual jump.
  let jumpExpr = sprintf "$%s = true" Signals.OutputPinned

  /// On every scroll of the panel. Reaching the bottom pins. Only moving UP
  /// unpins, never just "not at the bottom": opening the Evaluate section
  /// shrinks the panel, and Chrome's scroll anchoring fires a scroll event for
  /// that with you suddenly 180px off the bottom. Found dogfooding at phone
  /// width, where the first eval after opening Evaluate didn't follow. Wheel,
  /// keys, touch and scrollbar drags all move scrollTop up; a layout change
  /// under you doesn't. Our own jump to the bottom lands here and confirms the
  /// pin.
  let scrollExpr =
    sprintf
      "var t = el.scrollTop; el.scrollHeight - t - el.clientHeight <= %d ? ($%s = true) : (t < $%s - 1 && ($%s = false)); $%s = t"
      atBottomTolerancePx Signals.OutputPinned Signals.OutputScrollTop Signals.OutputPinned Signals.OutputScrollTop

  /// Runs on #output-panel whenever the server's feed signals or the pin
  /// change. A different session starts over pinned. Pinned, it records the
  /// current count as seen and jumps to the bottom. `behavior: 'instant'`
  /// matters: a smooth scroll fires scroll events on the way down, and those
  /// would read "not at the bottom yet" and unpin you mid-jump.
  let followEffectExpr =
    sprintf
      "$%s !== $%s && ($%s = $%s, $%s = true); $%s; $%s && ($%s = $%s, el.scrollTo({ top: el.scrollHeight, behavior: 'instant' }))"
      Signals.OutputFeedSession Signals.OutputFollowSession
      Signals.OutputFollowSession Signals.OutputFeedSession Signals.OutputPinned
      Signals.OutputFeedRev
      Signals.OutputPinned Signals.OutputSeenEvals Signals.OutputFeedEvals

  /// A revision of the rendered output: changes when any line does. Following
  /// keys off it, so output that isn't an eval result still follows.
  /// Deterministic (FNV-1a) rather than `String.GetHashCode`, which is
  /// randomized per process and would make render snapshots unstable.
  let contentRev (lines: OutputLine list) : int =
    let mutable h = 2166136261u
    let mix (s: string) =
      for c in s do
        h <- (h ^^^ uint32 c) * 16777619u
      h <- (h ^^^ 10u) * 16777619u
    for line in lines do
      line.Timestamp |> Option.iter mix
      mix line.Text
    int (h >>> 1)

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

module Diagnostic =
  /// Convert a core Features.Diagnostics.Diagnostic to a dashboard Diagnostic.
  let fromFeatureDiag (d: Features.Diagnostics.Diagnostic) : Diagnostic =
    { Severity =
        match d.Severity with
        | Features.Diagnostics.DiagnosticSeverity.Blocking -> DiagError
        | _ -> DiagWarning
      Message = d.Message
      Line = d.Range.StartLine
      Col = d.Range.StartColumn }

/// Outcome of a single FSI evaluation.
type EvalOutcome = EvalSuccess | EvalError | EvalCancelled

/// View model for a single filmstrip frame — one eval in the session history.
type FilmstripEntry = {
  Index: int
  Label: string
  DurationMs: int64
  Outcome: EvalOutcome
  Timestamp: System.DateTimeOffset
}

/// Eval statistics view model — pre-computed for rendering.
type EvalStatsView = {
  Count: int
  AvgMs: float
  MinMs: float
  MaxMs: float
  /// Unicode sparkline of recent eval durations (e.g. "▁▂▃▄█"). Empty when no evals.
  Sparkline: string
  /// P50 latency in ms (median). None when no evals.
  P50Ms: float option
  /// P95 latency in ms. None when fewer than 2 evals.
  P95Ms: float option
}

module EvalStatsView =
  /// Combine raw EvalStats with EvalTimeline stats into a unified view model.
  let fromStats (evalStats: SageFs.Affordances.EvalStats) (timelineStats: SageFs.Features.EvalTimeline.TimelineStats) : EvalStatsView =
    let avg =
      match evalStats.EvalCount > 0 with
      | true -> evalStats.TotalDuration.TotalMilliseconds / float evalStats.EvalCount
      | false -> 0.0
    { Count = evalStats.EvalCount
      AvgMs = avg
      MinMs = evalStats.MinDuration.TotalMilliseconds
      MaxMs = evalStats.MaxDuration.TotalMilliseconds
      Sparkline = timelineStats.Sparkline
      P50Ms = timelineStats.P50Ms
      P95Ms = timelineStats.P95Ms }

/// Daemon health view model — pre-computed for rendering.
type DaemonHealthView = {
  Version: string
  MemoryMB: int
  UptimeLabel: string
  OverallHealth: Features.OverallHealth
  SessionCount: int
  /// Per-session summaries for the health panel rows.
  SessionSummaries: Features.SessionHealthSummary list
  /// Total tests passing, if live testing is active.
  TestsPassed: int option
  /// Total tests failing, if live testing is active.
  TestsFailed: int option
}

module DaemonHealthView =
  /// Build a DaemonHealthView from a raw HealthSnapshot.
  let fromSnapshot (snap: Features.HealthSnapshot) : DaemonHealthView =
    let overallHealth = Features.DaemonHealth.overallStatus snap
    let uptimeLabel = Features.DaemonHealth.formatUptime snap.Uptime
    let (testsPassed, testsFailed) =
      match snap.LiveTestingSummary with
      | Some lt -> (Some lt.Passed, Some lt.Failed)
      | None -> (None, None)
    { Version = snap.Version
      MemoryMB = snap.MemoryMB
      UptimeLabel = uptimeLabel
      OverallHealth = overallHealth
      SessionCount = snap.SessionSummaries.Length
      SessionSummaries = snap.SessionSummaries
      TestsPassed = testsPassed
      TestsFailed = testsFailed }

/// A single failure narrative entry for the dashboard panel.
type FailureNarrativeEntry = {
  TestName: string
  Summary: string
  /// Formatted time since the test last passed (e.g. "5 minutes ago"), if known.
  TimeSinceLabel: string option
  /// Human-readable causal change labels (e.g. "symbol: Foo.bar", "file: Baz.fs").
  CausalLabels: string list
  HasPropertyViolation: bool
}

module FailureNarrativeEntry =
  /// Format a TimeSpan into a human-readable "ago" label.
  let formatTimeSince (ts: TimeSpan option) : string option =
    match ts with
    | None -> None
    | Some ts when ts.TotalSeconds < 60.0 -> Some "just now"
    | Some ts when ts.TotalMinutes < 60.0 -> Some (sprintf "%d minutes ago" (int ts.TotalMinutes))
    | Some ts -> Some (sprintf "%d hours ago" (int ts.TotalHours))

  // Unknown produces no label — keeps CausalLabels structurally empty for no-data entries
  let private formatCausalChange (cc: Features.LiveTesting.CausalChange) : string option =
    match cc with
    | Features.LiveTesting.CausalChange.SymbolChanged sym -> Some (sprintf "symbol: %s" sym)
    | Features.LiveTesting.CausalChange.FileChanged (file: string) -> Some (sprintf "file: %s" (System.IO.Path.GetFileName file))
    | Features.LiveTesting.CausalChange.Unknown -> None

  /// Build a FailureNarrativeEntry from a test name and its FailureNarrative.
  let fromNarrative (testName: string) (narrative: Features.LiveTesting.FailureNarrative) : FailureNarrativeEntry =
    { TestName = testName
      Summary = narrative.Summary
      TimeSinceLabel = formatTimeSince narrative.TimeSinceLastPass
      CausalLabels = narrative.CausalChanges |> List.choose formatCausalChange
      HasPropertyViolation = narrative.PropertyViolation.IsSome }

  /// True when this entry has actual diagnostic context worth showing.
  /// Entries with no time-since, no causal context, and no property violation are noise.
  let isMeaningful (entry: FailureNarrativeEntry) =
    entry.TimeSinceLabel.IsSome || not entry.CausalLabels.IsEmpty || entry.HasPropertyViolation

/// View model for the failure narratives dashboard panel.
type FailureNarrativesPanelView = {
  /// Entries with actual diagnostic context — capped at 10 for display.
  Entries: FailureNarrativeEntry list
  /// Total failures across all tests (including suppressed no-baseline entries).
  TotalFailureCount: int
  /// Number of failures suppressed due to having no diagnostic context.
  SuppressedCount: int
}

module FailureNarrativesPanelView =
  /// Build from a list of (testName, FailureNarrative) pairs.
  /// Caps display at 10; all failures are shown (diagnostic context is optional).
  let fromNarratives (pairs: (string * Features.LiveTesting.FailureNarrative) list) : FailureNarrativesPanelView =
    let all = pairs |> List.map (fun (name, narr) -> FailureNarrativeEntry.fromNarrative name narr)
    { Entries = all |> List.truncate 10
      TotalFailureCount = all.Length
      SuppressedCount = 0 }

/// Pipeline stage outcome — success or failure with an error message.
[<Struct>]
type StageOutcome =
  | StageSuccess
  | StageFailure of error: string

/// A single eval pipeline stage for railway visualization.
[<Struct>]
type PipelineStageView = {
  Name: string
  DurationMs: float
  Outcome: StageOutcome
}

/// Railway visualization view model — the complete eval pipeline trace.
type PipelineRailwayView = {
  Stages: PipelineStageView list
  TotalMs: float
}

module PipelineRailwayView =
  /// Build from the raw (name * durationMs) list emitted by EvalTraced events.
  let fromStages (stages: (string * float) list) (totalMs: float) : PipelineRailwayView =
    { Stages =
        stages
        |> List.map (fun (name, ms) ->
          { Name = name; DurationMs = ms; Outcome = StageSuccess })
      TotalMs = totalMs }

  /// Build from stages where the last stage failed.
  let fromStagesWithFailure (stages: (string * float) list) (totalMs: float) (failedStage: string) (error: string) : PipelineRailwayView =
    { Stages =
        stages
        |> List.map (fun (name, ms) ->
          match name = failedStage with
          | true -> { Name = name; DurationMs = ms; Outcome = StageFailure error }
          | false -> { Name = name; DurationMs = ms; Outcome = StageSuccess })
      TotalMs = totalMs }

  let empty : PipelineRailwayView = { Stages = []; TotalMs = 0.0 }

/// Discover .fsproj and .sln/.slnx files in a directory.
type DiscoveredProjects = {
  WorkingDir: string
  Solutions: string list
  Projects: string list
}

/// Path segments that are never a project a user wants to load: build output,
/// VCS/tooling state, package caches, and sibling git worktrees. THE canonical
/// list — `McpAdapter.projectNoiseSegments` delegates here so discovery cannot
/// mean two different things on two surfaces (it used to: the dashboard offered
/// `.worktrees/*` copies of the repo's own projects, the MCP walk did not).
let projectNoiseSegments : Set<string> =
  Set.ofList [ "bin"; "obj"; ".git"; ".claude"; ".vs"; ".idea"; "node_modules"; "packages"; ".fable"; ".fake"; ".worktrees" ]

/// True if any path segment is build/worktree/tooling noise.
let isNoiseProjectPath (path: string) : bool =
  path.Split([| '/'; '\\' |]) |> Array.exists projectNoiseSegments.Contains

let discoverProjects (workingDir: string) : DiscoveredProjects =
  let projects =
    try
      Directory.EnumerateFiles(workingDir, "*.fsproj", SearchOption.AllDirectories)
      |> Seq.map (fun p -> Path.GetRelativePath(workingDir, p))
      |> Seq.filter (isNoiseProjectPath >> not)
      |> Seq.sortBy (fun p -> (p.Split([| '/'; '\\' |]).Length, p.ToLowerInvariant()))
      |> Seq.toList
    with ex ->
      Log.warn "[Discovery] Project enumeration failed in %s: %s (%s)\n%s" workingDir ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      []
  let solutions =
    try
      Directory.EnumerateFiles(workingDir)
      |> Seq.filter (fun f ->
        let ext = Path.GetExtension(f).ToLowerInvariant()
        ext = ".sln" || ext = ".slnx")
      |> Seq.map Path.GetFileName
      |> Seq.toList
    with ex ->
      Log.warn "[Discovery] Project enumeration failed in %s: %s (%s)\n%s" workingDir ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      []
  { WorkingDir = workingDir; Solutions = solutions; Projects = projects }

/// Pre-formatted agent badge for dashboard rendering.
/// Populated at snapshot time from AgentPresence + AgentFreshness.
type AgentBadge = {
  Name: string
  IntentLabel: string
  CssClass: string
  DetailLabel: string
}

/// Display-level session status for the dashboard sidebar. This is the one
/// SessionDisplayStatus type (SessionDisplay.fs, namespace SageFs), brought
/// into scope by `open SageFs` above, along with its companion module
/// (label/cssClass/ofSessionState) declared next to it. It used to be
/// redeclared here with its own reason-less Faulted/Lost/Stopped cases, so
/// the sidebar and the MCP/TUI event stream could disagree about the same
/// session (roast-4 #6).
/// Distinct from SessionState (daemon lifecycle) and WorkerProtocol.SessionStatus (wire protocol).

/// Type-safe daemon connection state for the dashboard.
/// Illegal states unrepresentable: the browser CANNOT show "Ready" or "No session"
/// when the daemon is disconnected — the DU cases enforce this at the type level.
[<RequireQualifiedAccess>]
type DashboardConnectionState =
  /// SSE stream is alive and the daemon is reachable.
  | Connected
  /// SSE stream failed or was never established — the daemon may not be running.
  | Disconnected

module DashboardConnectionState =
  /// Convert to the Datastar signal string value.
  let signalValue = function
    | DashboardConnectionState.Connected -> "true"
    | DashboardConnectionState.Disconnected -> "false"

  /// The CSS class for the status badge based on connection state.
  let statusBadgeCssClass = function
    | DashboardConnectionState.Connected -> "status-ready"
    | DashboardConnectionState.Disconnected -> "status-disconnected"

  /// The text shown in the status badge.
  let statusBadgeLabel (sessionState: string) = function
    | DashboardConnectionState.Connected -> sessionState
    | DashboardConnectionState.Disconnected -> "Disconnected"

  /// The text shown in the statusline info area.
  let statuslineLabel (workingDir: string) (version: string) = function
    | DashboardConnectionState.Connected ->
      sprintf "\"%s\" — SageFs v%s — ready" workingDir version
    | DashboardConnectionState.Disconnected ->
      sprintf "\"%s\" — SageFs v%s — daemon not running" workingDir version

  /// The text shown in the command line area.
  let cmdlineLabel = function
    | DashboardConnectionState.Connected -> "SageFs -- ready"
    | DashboardConnectionState.Disconnected -> "SageFs -- daemon not running"

/// One sidebar card. Which card is being viewed is NOT a property of the card:
/// renderers are always told the viewing session explicitly (the page's
/// viewingSessionId signal), so no card can claim to be viewed on its own.
type ParsedSession = {
  Id: WorkerProtocol.SessionId
  Status: SessionDisplayStatus
  StatusMessage: string option
  ProjectsText: string
  EvalCount: int
  Uptime: string
  WorkingDir: string
  LastActivity: string
  TestSummary: Features.LiveTesting.TestSummary option
  CoverageSummary: Features.LiveTesting.CoverageSummary option
  TestTreemapEntries: Features.LiveTesting.TestTreemapEntry array
  /// Solution -> project -> file -> symbol coverage hierarchy for the
  /// drill-down treemap panel. None until coverage data exists.
  CoverageTreemap: Features.Treemap.CoverageTreemapNode option
  BindingEntries: Features.BindingExplorer.BindingInfo array
  AgentBadges: AgentBadge list
  GuidanceCssClass: string
  ActiveProject: string option
  ProjectRoles: SageFs.ProjectLoading.ClassifiedProject list
  App: AppRun.AppRunState
  /// The worker process's resident set size (`Process.WorkingSet64`), read
  /// live from its pid (vision §3.4 "worker RSS" — the first of the four
  /// numbers that decide cohort member count). None when the session has no
  /// live worker pid (starting, faulted, stopped) or the pid's process has
  /// already exited.
  WorkerRssBytes: int64 option
  /// The self-host staleness affordance line (F5b) for a session that adopted
  /// its own SageFs.Core build, when a newer build has since landed on disk;
  /// None for a non-self-hosting or up-to-date session.
  SelfHostStaleness: string option
  /// The one true, user-meaningful usability verdict for this session
  /// (`SessionHealth.classify`) — distinct from `Status` above, which only
  /// reflects worker-process liveness ("Ready" means "the worker is alive,"
  /// not "my project loaded and I can evaluate"). Computed from the same
  /// facts get_fsi_status and /api/sessions use, so the card can never
  /// disagree with the MCP/HTTP surfaces about the same session (roast:
  /// sagefs-ux-roast.md §1 — "the fix that reached no human"). Best-effort
  /// for cards other than the one being viewed: computing it accurately
  /// requires the session's warmup context, which costs a worker HTTP round
  /// trip, so only the viewed session's real context is threaded through —
  /// other cards classify with `warmup = None`, which `SessionHealth.classify`
  /// itself defines as "nothing to be suspicious about" (quiet, not a lie).
  Health: SessionHealth
}

/// Best-effort live RSS of a worker process, by pid. Never throws: a pid
/// that has already exited, or that this user cannot query cross-user, is
/// `None` rather than a fault — the card must never break on a process that
/// died between "the daemon recorded this pid" and "the sidebar rendered."
let tryGetWorkerRssBytes (pid: int) : int64 option =
  match pid with
  | 0 -> None
  | pid ->
    try Some (Diagnostics.Process.GetProcessById(pid).WorkingSet64)
    with _ -> None

/// A span of time in the sidebar's words: "just now", "5m", "2h", "3d".
let spanLabel (span: TimeSpan) =
  match span with
  | s when s.TotalMinutes < 1.0 -> "just now"
  | s when s.TotalHours < 1.0 -> sprintf "%dm" (int s.TotalMinutes)
  | s when s.TotalDays < 1.0 -> sprintf "%dh" (int s.TotalHours)
  | s -> sprintf "%dd" (int s.TotalDays)

/// One session's sidebar card, built from typed state only: the registry's
/// SessionInfo (status, projects, directory, fault reason, app), the session's
/// warmup progress, and its eval count. The sidebar used to regex-parse these
/// back out of the TUI's text rendering, which silently dropped any session
/// whose project list contained ")" and showed errored or stale sessions as
/// running. The status message follows the status: warmup progress while
/// starting, the fault reason only while faulted or lost — never a stale
/// reason on a running card.
let sessionCardOf
  (now: DateTime)
  (warmupProgress: string option)
  (evalCount: int)
  (health: SessionHealth)
  (info: WorkerProtocol.SessionInfo)
  : ParsedSession =
  // info carries the real SessionLifecycleStatus, so the fault reason (if
  // any) is derived from it directly rather than through the reason-less
  // SessionState — the same source SessionDisplay.displayStatus uses.
  let status = SessionDisplay.displayStatus now info
  { Id = info.Id
    Status = status
    StatusMessage =
      match status with
      | SessionDisplayStatus.Starting -> warmupProgress
      | SessionDisplayStatus.Faulted reason -> Some reason
      | SessionDisplayStatus.Lost -> WorkerProtocol.SessionLifecycleStatus.faultReason info.Status
      | SessionDisplayStatus.Running
      | SessionDisplayStatus.Restarting
      | SessionDisplayStatus.Idle
      | SessionDisplayStatus.Stopped -> None
    ProjectsText =
      match info.Projects with
      | [] -> ""
      | projects ->
        sprintf "(%s)" (projects |> List.map System.IO.Path.GetFileNameWithoutExtension |> String.concat ", ")
    EvalCount = evalCount
    Uptime = spanLabel (now - info.CreatedAt)
    WorkingDir = info.WorkingDirectory
    LastActivity =
      match spanLabel (now - info.LastActivity) with
      | "just now" -> "just now"
      | label -> label + " ago"
    TestSummary = None
    CoverageSummary = None
    TestTreemapEntries = [||]
    CoverageTreemap = None
    BindingEntries = [||]
    AgentBadges = []
    GuidanceCssClass = ""
    ActiveProject = info.ActiveProject
    ProjectRoles = info.ProjectRoles
    App = info.App
    WorkerRssBytes =
      WorkerProtocol.SessionLifecycleStatus.workerPid info.Status
      |> Option.bind tryGetWorkerRssBytes
    // Enriched (like TestSummary etc.) by buildSessionCardsFrom via a
    // DashboardQueries lookup; the base card carries no staleness.
    SelfHostStaleness = None
    Health = health }

/// Every session the sidebar lists — all but Stopped — in registry order (the
/// same order the initial page and viewing reconciliation use).
///
/// `warmupContextFor` is best-effort: it need only answer for sessions whose
/// warmup context is already in hand for free (the currently-viewed session
/// reuses the context `buildDashboardSnapshotWithSessions` already fetched
/// for its own panels) and should return `None` for every other session
/// rather than trigger a worker HTTP round trip per card per push — the same
/// "cheap local reads only" discipline every other per-card enrichment
/// follows. `SessionHealth.classify` treats `None` as "nothing to be
/// suspicious about," so an unfetched card renders quietly rather than lying.
let liveSessionCards
  (now: DateTime)
  (warmupProgress: WorkerProtocol.SessionId -> string option)
  (evalCounts: Map<WorkerProtocol.SessionId, int>)
  (warmupContextFor: WorkerProtocol.SessionId -> WarmupContext option)
  (sessions: WorkerProtocol.SessionInfo list)
  : ParsedSession list =
  sessions
  |> List.filter (fun s -> s.Status <> WorkerProtocol.SessionLifecycleStatus.Stopped)
  |> List.map (fun s ->
    let evals = evalCounts |> Map.tryFind s.Id |> Option.defaultValue 0
    let health = SessionHealth.classify s.Status s.ProjectRoles (warmupContextFor s.Id)
    sessionCardOf now (warmupProgress s.Id) evals health s)

/// A previously-known session that can be resumed.
type PreviousSession = {
  Id: string
  WorkingDir: string
  Projects: string list
  LastSeen: DateTime
}

let parseOutputLines (content: string) : OutputLine list =
  let tsKindRegex = Regex(@"^\[(\d{2}:\d{2}:\d{2})\]\s*\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  let kindOnlyRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = tsKindRegex.Match(l)
    match m.Success with
    | true ->
      { Timestamp = Some m.Groups.[1].Value
        Kind = OutputLineKind.fromString m.Groups.[2].Value
        Text = m.Groups.[3].Value }
    | false ->
      let m2 = kindOnlyRegex.Match(l)
      match m2.Success with
      | true ->
        { Timestamp = None
          Kind = OutputLineKind.fromString m2.Groups.[1].Value
          Text = m2.Groups.[2].Value }
      | false ->
        { Timestamp = None; Kind = ResultLine; Text = l })
  |> Array.toList

let parseDiagLines (content: string) : Diagnostic list =
  let diagRegex = Regex(@"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = diagRegex.Match(l)
    match m.Success with
    | true ->
      { Severity = DiagSeverity.fromString m.Groups.[1].Value
        Message = m.Groups.[4].Value
        Line = int m.Groups.[2].Value
        Col = int m.Groups.[3].Value }
    | false ->
      { Severity = match l.Contains("[error]") with | true -> DiagError | false -> DiagWarning
        Message = l
        Line = 0
        Col = 0 })
  |> Array.toList

/// A single system alarm entry — phase name, exception message, and when it fired.
type SystemAlarmEntry = {
  Phase: string
  Message: string
  Timestamp: DateTimeOffset
}

/// State queries — always-present read accessors for dashboard rendering.
///
/// IMPORTANT: there is intentionally NO `GetActiveSessionId` and NO
/// global `GetElmRegions` on this type. "Active session" is a per-client
/// concept (which session is THIS browser tab or MCP connection
/// currently viewing), and the output region is per-session in the
/// underlying `SessionOutputStore`. Dashboard code MUST:
///   - read the viewing session from the per-connection `viewingSessionId`
///     Datastar signal — never from a daemon global. The HTML dashboard never
///     reads a session from its URL: the browser's viewing-session signal is the
///     source of truth, synced with the backend via per-connection stream
///     retargets. (The legacy TUI JSON stream still uses `?sessionId=`.)
///   - call `GetElmRegionsForSession sessionId` with THAT session id,
///     never a global accessor
/// The Elm runtime's `ActiveSessionId` is not exposed to the dashboard
/// layer. If you find yourself wanting to add "just a quick global" to
/// this type, don't — route it through the per-client path.
type DashboardQueries = {
  GetSessionState: WorkerProtocol.SessionId -> SessionState
  GetStatusMsg: WorkerProtocol.SessionId -> string option
  GetEvalStats: WorkerProtocol.SessionId -> Threading.Tasks.Task<SageFs.Affordances.EvalStats>
  GetFrictionStore: unit -> Threading.Tasks.Task<SageFs.Features.FrictionSqlite.FrictionStore option>
  GetSessionWorkingDir: WorkerProtocol.SessionId -> string
  /// Per-session render regions. The dashboard MUST call this with the
  /// per-client viewing session id. The output region's content is
  /// sourced from the requested session's `OutputRingBuffer`, not from
  /// the Elm runtime's global active session.
  GetElmRegionsForSession: WorkerProtocol.SessionId -> RenderRegion list option
  GetPreviousSessions: unit -> Threading.Tasks.Task<PreviousSession list>
  GetAllSessions: unit -> Threading.Tasks.Task<WorkerProtocol.SessionInfo list>
  GetHotReloadState: WorkerProtocol.SessionId -> Threading.Tasks.Task<{| files: {| path: string; watched: bool |} list; watchedCount: int; kept: SageFs.Features.ReloadOutcome.KeptValue list; reflection: SageFs.Features.KeptState.ReflectionReadsView |} option>
  GetWarmupContext: WorkerProtocol.SessionId -> Threading.Tasks.Task<WarmupContext option>
  GetWarmupProgress: WorkerProtocol.SessionId -> string
  GetSessionTestSummary: WorkerProtocol.SessionId -> Features.LiveTesting.TestSummary option
  GetSessionCoverageSummary: WorkerProtocol.SessionId -> Features.LiveTesting.CoverageSummary option
  GetSessionTestTreemap: WorkerProtocol.SessionId -> Features.LiveTesting.TestTreemapEntry array
  /// The solution -> project -> file -> symbol coverage hierarchy for a
  /// session's WizTree-style treemap panel. None until the session has both
  /// instrumentation maps and at least one collected coverage bitmap.
  GetSessionCoverageTreemap: WorkerProtocol.SessionId -> Features.Treemap.CoverageTreemapNode option
  GetSessionBindings: WorkerProtocol.SessionId -> Features.BindingExplorer.BindingInfo array
  /// Live reflection-walked binding tree for a session (debugger watch window),
  /// from the adaptive store. None until the first eval snapshot arrives.
  GetLiveBindings: WorkerProtocol.SessionId -> Features.LiveValueTree.LiveValueSnapshot option
  GetBindingScopeSnapshot: unit -> Features.BindingExplorer.BindingScopeSnapshot option
  GetLiveTestingStatus: unit -> string
  /// Whether live testing is currently Active or Inactive.
  GetLiveTestingActive: unit -> bool
  /// The one live-testing state for a session ("" when no session is viewed).
  GetLiveTestActivity: string -> Features.LiveTestActivity.LiveTestActivity
  /// Read current EvalTimeline stats from the shared feature push state.
  GetEvalTimeline: unit -> Features.EvalTimeline.TimelineStats
  /// Read current daemon health snapshot from the shared feature push state.
  GetDaemonHealth: unit -> Features.HealthSnapshot option
  /// Read current failure narratives from live test state — (testName, narrative) pairs.
  GetFailureNarratives: unit -> (string * Features.LiveTesting.FailureNarrative) list
  /// Read current FSI diagnostics (errors/warnings) from the diagnostics store.
  GetCurrentDiagnostics: unit -> Diagnostic list
  /// Read recent eval filmstrip entries from the eval history — newest-last, capped at 20.
  GetFilmstripEntries: unit -> FilmstripEntry list
  /// Read resolved test source locations from the Elm model.
  GetTestSourceLocations: unit -> Features.LiveTesting.TestSourceLocation list
  /// Get pre-formatted agent badges for a session from the activity tracker.
  GetSessionAgentBadges: WorkerProtocol.SessionId -> AgentBadge list
  /// Get the CSS class for session guidance (ambient row styling).
  GetSessionGuidanceCss: WorkerProtocol.SessionId -> string
  /// Self-host staleness affordance (F5b) for a session that adopted its own
  /// SageFs.Core build and a newer build has since landed on disk; None for a
  /// non-self-hosting or up-to-date session.
  GetSessionSelfHostStaleness: WorkerProtocol.SessionId -> string option
  /// Get the workflow for a session — returns Interactive as default.
  GetSessionWorkflow: WorkerProtocol.SessionId -> WorkflowTypes.SessionWorkflow
  /// Get the active project name for a session (for Run App feature).
  GetSessionActiveProject: WorkerProtocol.SessionId -> string option
  /// Get the classified projects for a session (for Run App feature).
  GetSessionProjectRoles: WorkerProtocol.SessionId -> ClassifiedProject list
  /// The app the session runs, as the user should see it.
  GetSessionApp: WorkerProtocol.SessionId -> AppRun.AppRunState
  /// Each session's eval count, from the Elm model's typed session registry.
  GetSessionEvalCounts: unit -> Map<WorkerProtocol.SessionId, int>
  /// Whether a session is being created right now (the sidebar's placeholder).
  IsCreatingSession: unit -> bool
}

/// Recently-fetched worker-derived dashboard data, reused across SSE pushes so
/// high-frequency ticks do not pay three worker HTTP round-trips each. The
/// render-diff guard means reusing this can never SEND stale HTML — it only
/// makes unchanged ticks cheaper; a real change still renders and sends.
type DashboardWorkerCache = {
  SessionId: WorkerProtocol.SessionId
  EvalStats: SageFs.Affordances.EvalStats
  HotReloadState: {| files: {| path: string; watched: bool |} list; watchedCount: int; kept: SageFs.Features.ReloadOutcome.KeptValue list; reflection: SageFs.Features.KeptState.ReflectionReadsView |} option
  WarmupContext: WarmupContext option
  /// Server-built friction review panel — reusing it avoids the synchronous
  /// SQLite read (GetFrictionStore + reportDirect + ListSentReports) on every
  /// push. The view changes only when friction tools record events, which do
  /// not flow through the SSE state-change stream; the render-diff guard keeps
  /// a reused panel from ever being SENT stale — it only skips re-reading the
  /// DB when nothing else on the page changed either.
  FrictionPanel: Falco.Markup.XmlNode option
}

/// Commands that mutate session state.
type DashboardActions = {
  EvalCode: WorkerProtocol.SessionId -> string -> Threading.Tasks.Task<Result<string, string>>
  /// Cooperative cancel of the session's in-flight eval (CTS cancel + thread
  /// interrupt on the worker). Genuinely stops an eval blocked on I/O or one
  /// that checks a cancellation token; it CANNOT preempt a tight synchronous
  /// CPU loop with no yield point (e.g. `while true do ()`) — .NET has no
  /// safe way to abort a running thread. `Ok` here means "cancel requested",
  /// not "the eval definitely stopped" — hard-reset remains the escape hatch.
  CancelEval: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  ResetSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  HardResetSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  Dispatch: SageFsMsg -> unit
  SwitchSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  StopSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  /// Purge — stop the session + remove its .sagefm manifest entry (gone from resume picker).
  PurgeSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  CreateSession: string list -> string -> Threading.Tasks.Task<Result<WorkerProtocol.SessionId, string>>
  ShutdownCallback: (unit -> unit) option
  /// Run the session's executable project (see AppRunOrchestration).
  RunApp: WorkerProtocol.SessionId -> AppRun.RunRequest -> Threading.Tasks.Task<Result<string, string>>
  /// Stop the app the session runs.
  StopApp: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
}

/// Per-connection SSE stream command — daemon state pushes plus viewing-session
/// retargets issued by dashboard POST handlers. The stream channel is keyed by
/// the page's client id so a signal-driven session selection re-targets the
/// right tab's stream (and never a daemon-global session).
[<RequireQualifiedAccess>]
type DashboardStreamCommand =
  | StateChange of SseEvent
  | RetargetView of WorkerProtocol.SessionId option
  /// The time-scrubber's per-tab retarget (§6.5, Phase 2 item 16), the
  /// `RetargetView` pattern applied to the cohort panel: `None` means this
  /// tab is live again, `Some seq` means it has scrubbed to that past
  /// ledger seq. The connection that owns this channel is the ONLY one
  /// affected — this is what makes scrubbing one tab never touch another
  /// tab's live push (`Dashboard.fs`'s stream loop stores it in a
  /// per-connection mutable, exactly like `currentSessionOpt`).
  | SetCohortViewingSeq of int64<ledgerSeq> option

/// Infrastructure dependencies — event sources, tracking, themes.
type DashboardInfra = {
  Version: string
  McpPort: int
  /// The daemon's single push-notification source. Non-optional: roast-6
  /// Phase 0 item 1 deletes the dashboard's 1s polling fallback (there was
  /// no scenario where a live daemon lacked this event; only a `None` value
  /// let dead poll-loop code exist at all) — one push system, not two.
  /// Tests that don't exercise the SSE stream pass a never-firing event.
  StateChanged: IEvent<SseEvent>
  ConnectionTracker: ConnectionTracker option
  SessionThemes: Collections.Concurrent.ConcurrentDictionary<string, string>
  GetCompletions: WorkerProtocol.SessionId -> string -> int -> Threading.Tasks.Task<Features.AutoCompletion.CompletionItem list>
  GetSessionCount: unit -> Threading.Tasks.Task<int>
  /// Shared alarm buffer — populated when ElmLoop fires OnSystemAlarm.
  /// Shared across all SSE connections; first-dismiss clears for all.
  SystemAlarmBuffer: SystemAlarmEntry list ref
  /// Triggers a state-change push on all connected SSE streams (used by dismiss route).
  TriggerStateChange: unit -> unit
  /// Agent activity tracker for multi-agent coordination.
  ActivityTracker: AgentActivityTracker.Tracker option
  /// Adaptive live-bindings store — the dashboard stream subscribes to a
  /// session's cell and patches only the bindings panel on change.
  LiveBindingsAdaptive: Features.LiveBindingsAdaptive.State option
  /// Live SSE stream channels keyed by page client id — dashboard POST
  /// handlers retarget the owning stream when the viewing-session signal
  /// changes (signal-driven session selection; no URL query parameter).
  ConnectionChannels: Collections.Concurrent.ConcurrentDictionary<string, MailboxProcessor<DashboardStreamCommand>>
  /// Wait-free read of the daemon's single implicit cohort frame
  /// (cohort-integration-plan.md Slice 4, D4) — dereferences
  /// `CohortOwner.Handle.ReadFrame`'s published pointer directly, no
  /// mailbox round-trip, no IO. The cohort is daemon-scoped, not
  /// session-scoped, so this is called on every render regardless of
  /// which (or whether any) session is being viewed.
  ReadCohortFrame: unit -> Cohort.CohortFrame<MemberTable.MemberId>
  /// Every recorded cohort ledger entry, in `Seq` order (§6.5's lane view —
  /// `CohortLanes.project`'s input). `CohortFrame` carries no timing data at
  /// all (see `CohortLanes.fs`'s module doc), so the lane view reads the
  /// ledger directly rather than the frame. Not wait-free like
  /// `ReadCohortFrame` — it is a full ledger read (SQLite in production,
  /// `Features.CohortLedgerSqlite.Sqlite.create`'s `ReadAll`) — but the
  /// ledger for one daemon-scoped cohort is small (§5.1's v1 scope), so a
  /// full read per dashboard push is cheap the same way the friction
  /// panel's per-push SQLite read already is.
  ReadCohortLedger: unit -> Cohort.LedgerEntry<MemberTable.MemberId> list
}

/// Complete snapshot of all dashboard state needed for a single full-page render.
/// Constructed once per push, then passed to renderMainContent for atomic morph.
type DashboardSnapshot = {
  Version: string
  /// Type-safe daemon connection state — renderer derives badge class, statusline,
  /// and cmdline text from this DU case. When Disconnected, the UI MUST NOT show
  /// "Ready" or "No session" — enforced by DashboardConnectionState module functions.
  ConnectionState: DashboardConnectionState
  SessionState: string
  SessionId: string
  WorkingDir: string
  WarmupProgress: string
  WorkflowLabel: string
  EvalStats: EvalStatsView
  AlarmPanel: XmlNode
  DaemonHealth: XmlNode
  FailureNarrativesPanel: XmlNode
  DiagnosticsPanel: XmlNode
  FilmstripPanel: XmlNode
  ThemeName: string
  ConnectionLabel: string option
  HotReloadPanel: XmlNode
  LiveTestingPanel: XmlNode
  SessionContextPanel: XmlNode
  OutputPanel: XmlNode
  SessionsPanel: XmlNode
  SessionPicker: XmlNode
  ThemePicker: XmlNode
  ThemeVars: XmlNode
  BindingsPanel: XmlNode
  FrictionPanel: XmlNode
  /// The daemon's implicit cohort — members, claims, ledger version
  /// (cohort-integration-plan.md Slice 4). Daemon-scoped: rendered
  /// identically regardless of which session (if any) is being viewed.
  CohortPanel: XmlNode
  /// Active project selection for "Run App" feature.
  ActiveProject: string option
  /// Classification of all projects in the session.
  ProjectRoles: SageFs.ProjectLoading.ClassifiedProject list
  /// The app the session runs, as the user should see it.
  App: AppRun.AppRunState
  /// Eval-to-pixel latency chain (vision §3.4, §7.4) — p50/p99 in ms over
  /// the last 256 completed chains, rendered in the statusline. None until
  /// at least one eval has completed its full request-to-morph journey.
  EvalToPixelP50Ms: float option
  EvalToPixelP99Ms: float option
}

/// The sidebar panels that only show up when they're relevant. Everything
/// else in the dashboard always renders; these four used to render all the
/// time too, which put hot reload on a REPL session, a live-testing toggle on
/// a session that never asked for it, and cohort lanes built from stale
/// ledger rows in front of everyone.
[<RequireQualifiedAccess>]
type OptionalPanel =
  | HotReload
  | LiveTesting
  /// The cohort panel and its lanes, together.
  | Cohort
  | Friction

[<RequireQualifiedAccess>]
type PanelVisibility =
  | Shown
  /// Carries why, for the View menu the redesign adds later.
  | Hidden of reason: string

/// What the viewed session is doing, as far as panel visibility cares.
[<RequireQualifiedAccess>]
type ViewedSession =
  | NoSession
  | Viewing of workflow: WorkflowTypes.SessionWorkflow * liveTesting: Features.LiveTestActivity.LiveTestActivity

/// Whether a cohort is running right now: members actually present, never
/// just rows sitting in the ledger.
[<RequireQualifiedAccess>]
type CohortPresence =
  | NoActiveMembers
  | ActiveMembers of count: int

/// The friction panel is off by default until reporting has a real endpoint.
/// A tab can still ask for it, deliberately, with `/dashboard?panels=friction`.
[<RequireQualifiedAccess>]
type FrictionPanelOptIn =
  | NotOptedIn
  | OptedIn

type PanelFacts = {
  Viewed: ViewedSession
  Cohort: CohortPresence
  Friction: FrictionPanelOptIn
}

module PanelFacts =
  /// The query key and value that opt a tab into the friction panel.
  let panelsQueryKey = "panels"
  let frictionPanelValue = "friction"

  /// `?panels=friction` (comma-separated, case-insensitive) opts in.
  let frictionOptInOfQuery (panels: string) : FrictionPanelOptIn =
    let requested =
      (if isNull panels then "" else panels).Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.exists (fun p -> String.Equals(p, frictionPanelValue, StringComparison.OrdinalIgnoreCase))
    match requested with
    | true -> FrictionPanelOptIn.OptedIn
    | false -> FrictionPanelOptIn.NotOptedIn

  let cohortPresence (frame: SageFs.Cohort.CohortFrame<'m>) : CohortPresence =
    let present =
      frame.MemberSeat
      |> Array.filter (fun seat ->
        match seat with
        | SageFs.Cohort.SeatState.Present -> true
        | SageFs.Cohort.SeatState.Departed _ -> false)
      |> Array.length
    match present with
    | 0 -> CohortPresence.NoActiveMembers
    | n -> CohortPresence.ActiveMembers n

module PanelVisibility =
  let decide (facts: PanelFacts) (panel: OptionalPanel) : PanelVisibility =
    match panel with
    | OptionalPanel.HotReload ->
      match facts.Viewed with
      | ViewedSession.NoSession -> PanelVisibility.Hidden "Hot reload: no session is open"
      | ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.HotReload _, _) -> PanelVisibility.Shown
      | ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.Interactive, _) ->
        PanelVisibility.Hidden "Hot reload: this session is in REPL mode"
      | ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.LiveTesting, _) ->
        PanelVisibility.Hidden "Hot reload: this session is in Live Testing mode"
    | OptionalPanel.LiveTesting ->
      match facts.Viewed with
      | ViewedSession.NoSession -> PanelVisibility.Hidden "Live testing: no session is open"
      | ViewedSession.Viewing(_, Features.LiveTestActivity.LiveTestActivity.Off) ->
        PanelVisibility.Hidden "Live testing: it's off for this session"
      | ViewedSession.Viewing _ -> PanelVisibility.Shown
    | OptionalPanel.Cohort ->
      match facts.Cohort with
      | CohortPresence.ActiveMembers _ -> PanelVisibility.Shown
      | CohortPresence.NoActiveMembers -> PanelVisibility.Hidden "Cohort: no cohort is running"
    | OptionalPanel.Friction ->
      match facts.Friction with
      | FrictionPanelOptIn.OptedIn -> PanelVisibility.Shown
      | FrictionPanelOptIn.NotOptedIn ->
        PanelVisibility.Hidden "Friction: hidden until reporting has a real endpoint (open /dashboard?panels=friction to see it)"

  /// A hidden panel renders nothing at all.
  let private keep (facts: PanelFacts) (panel: OptionalPanel) (node: Falco.Markup.XmlNode) =
    match decide facts panel with
    | PanelVisibility.Shown -> node
    | PanelVisibility.Hidden _ -> Text.raw ""

  /// Drop the panels that aren't relevant from a built snapshot. Still one
  /// snapshot and one fat morph; hidden panels just aren't in it.
  let apply (facts: PanelFacts) (snap: DashboardSnapshot) : DashboardSnapshot =
    { snap with
        HotReloadPanel = keep facts OptionalPanel.HotReload snap.HotReloadPanel
        LiveTestingPanel = keep facts OptionalPanel.LiveTesting snap.LiveTestingPanel
        CohortPanel = keep facts OptionalPanel.Cohort snap.CohortPanel
        FrictionPanel = keep facts OptionalPanel.Friction snap.FrictionPanel }


type DaemonInfoContract = {
  Pid: int
  Version: string
  StartedAt: string
  WorkingDirectory: string
  McpPort: int
  DashboardPort: int
  ApiVersion: int
  SessionCount: int
}

[<RequireQualifiedAccess>]
module DaemonInfoContract =
  let create pid version startedAt workingDirectory mcpPort sessionCount : DaemonInfoContract =
    { Pid = pid
      Version = version
      StartedAt = startedAt
      WorkingDirectory = workingDirectory
      McpPort = mcpPort
      DashboardPort = mcpPort + 1
      ApiVersion = EndpointContracts.apiVersion
      SessionCount = sessionCount }

/// Parse an editor action string + optional value into an EditorAction DU case.
let parseEditorAction (actionName: string) (value: string option) : EditorAction option =
  match actionName with
  | "insertChar" ->
    value |> Option.bind (fun s -> if s.Length > 0 then Some (EditorAction.InsertChar s.[0]) else None)
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
    value |> Option.bind (fun v ->
      let parts = (v : string).Split(',')
      match parts.Length = 2 with
      | false -> None
      | true ->
        match Int32.TryParse(parts.[0] : string), Int32.TryParse(parts.[1] : string) with
        | (true, line), (true, col) -> Some (EditorAction.SetCursorPosition (line, col))
        | _ -> None)
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
  | "switchSession" -> value |> Option.map EditorAction.SwitchSession
  | "createSession" -> value |> Option.map (fun v -> EditorAction.CreateSession [v])
  | "configureWarmupAutoOpen" -> Some EditorAction.ConfigureWarmupAutoOpen
  | "stopSession" -> value |> Option.map EditorAction.StopSession
  | "historySearch" -> value |> Option.map EditorAction.HistorySearch
  | "resetSession" -> Some EditorAction.ResetSession
  | "hardResetSession" -> Some EditorAction.HardResetSession
  | "smartReset" -> Some EditorAction.SmartReset
  | "sessionNavUp" -> Some EditorAction.SessionNavUp
  | "sessionNavDown" -> Some EditorAction.SessionNavDown
  | "sessionSelect" -> Some EditorAction.SessionSelect
  | "sessionDelete" -> Some EditorAction.SessionDelete
  | "sessionStopOthers" -> Some EditorAction.SessionStopOthers
  | "clearOutput" -> Some EditorAction.ClearOutput
  | "sessionSetIndex" ->
    value |> Option.bind (fun s -> match Int32.TryParse(s) with true, i -> Some (EditorAction.SessionSetIndex i) | _ -> None)
  | "sessionCycleNext" -> Some EditorAction.SessionCycleNext
  | "sessionCyclePrev" -> Some EditorAction.SessionCyclePrev
  | "promptChar" ->
    value |> Option.bind (fun s -> if s.Length > 0 then Some (EditorAction.PromptChar s.[0]) else None)
  | "promptBackspace" -> Some EditorAction.PromptBackspace
  | "promptConfirm" -> Some EditorAction.PromptConfirm
  | "promptCancel" -> Some EditorAction.PromptCancel
  | _ -> None

// ---------------------------------------------------------------------------
// Theme persistence helpers
// ---------------------------------------------------------------------------

/// Canonicalize a working-directory for use as a theme key.
/// Resolves . and .., normalizes separators, lowercases on Windows,
/// and strips trailing separators. Two different string forms of the
/// same directory (e.g. "C:\Foo", "c:\foo\", "C:/Foo") collapse to one key.
let canonicalizeThemeKey (workingDir: string) : string =
  match String.IsNullOrWhiteSpace workingDir with
  | true -> ""
  | false ->
    try
      let full = Path.GetFullPath workingDir
      let trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
      // On Windows, paths are case-insensitive — normalize to lower.
      if OperatingSystem.IsWindows() then trimmed.ToLowerInvariant()
      else trimmed
    with _ -> workingDir

/// Save theme preferences to ~/.SageFs/themes.json.
/// Existing entries with non-canonicalized keys are preserved as-is for
/// backward compatibility, but new writes go through canonicalizeThemeKey
/// (handled at the call site).
let saveThemes (sageFsDir: string) (themes: Collections.Concurrent.ConcurrentDictionary<string, string>) =
  try
    match Directory.Exists sageFsDir with
    | false -> Directory.CreateDirectory sageFsDir |> ignore
    | true -> ()
    let path = Path.Combine(sageFsDir, "themes.json")
    let dict = themes |> Seq.map (fun kv -> kv.Key, kv.Value) |> dict
    let json = Text.Json.JsonSerializer.Serialize(dict, Text.Json.JsonSerializerOptions(WriteIndented = true))
    File.WriteAllText(path, json)
  with ex -> Log.warn "Failed to save themes to %s: %s\n%s" sageFsDir ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")

/// Load theme preferences from ~/.SageFs/themes.json
let loadThemes (sageFsDir: string) : Collections.Concurrent.ConcurrentDictionary<string, string> =
  let result = Collections.Concurrent.ConcurrentDictionary<string, string>()
  try
    let path = Path.Combine(sageFsDir, "themes.json")
    match File.Exists(path) with
    | true ->
      let json = File.ReadAllText(path)
      let dict = Text.Json.JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, string>>(json)
      match isNull dict with
      | false ->
        for kv in dict do
          result.[kv.Key] <- kv.Value
      | true -> ()
    | false -> ()
  with ex -> Log.warn "Failed to load themes from %s: %s\n%s" sageFsDir ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
  result

// ---------------------------------------------------------------------------
// Project resolution helpers
// ---------------------------------------------------------------------------

/// Resolve session projects from manual input or auto-detection.
/// Manual project paths are CONTAINED to the chosen working directory: a
/// dashboard peer must not be able to point the daemon at an arbitrary
/// project elsewhere on disk (mirrors the eval-file containment discipline).
///
/// A manually-named project that escapes the working directory is REJECTED
/// loudly — `Error (SageFsError.UnsafeSessionPath …)` — rather than silently
/// filtered out of the list, so a caller who names N projects can never get a
/// session quietly created with fewer, missing one it asked for. This matches
/// `McpServer.validateSessionCreateRequest`, which already rejects the whole
/// request on the first unsafe path (roast-8 §4: the two validators disagreed).
/// Auto-detection (empty manual input) only ever produces paths under `dir`,
/// so it is always `Ok`.
let resolveSessionProjects (dir: string) (manualProjects: string) : Result<string list, SageFsError> =
  let autoDetectProjects dir =
    let discovered = discoverProjects dir
    match discovered.Solutions.IsEmpty with
    | false -> [ Path.Combine(dir, discovered.Solutions.Head) ]
    | true ->
      match discovered.Projects.IsEmpty with
      | false -> discovered.Projects |> List.map (fun p -> Path.Combine(dir, p))
      | true -> []
  let resolveRealPath (p: string) : string =
    let full = Path.GetFullPath p
    let fsi : System.IO.FileSystemInfo =
      match Directory.Exists(full) with
      | true -> DirectoryInfo(full) :> System.IO.FileSystemInfo
      | false -> FileInfo(full) :> System.IO.FileSystemInfo
    match fsi.ResolveLinkTarget(returnFinalTarget = true) with
    | null -> full
    | resolved -> resolved.FullName
  let canonicalDir = resolveRealPath dir
  let isContainedInDir (p: string) =
    let canonical = resolveRealPath p
    canonical.StartsWith(canonicalDir + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    || canonical.Equals(canonicalDir, StringComparison.OrdinalIgnoreCase)
  match String.IsNullOrWhiteSpace manualProjects with
  | false ->
    let resolved =
      manualProjects.Split(',')
      |> Array.map (fun s -> s.Trim())
      |> Array.filter (fun s -> s.Length > 0)
      |> Array.map (fun p ->
        match Path.IsPathRooted p with
        | true -> p
        | false -> Path.Combine(dir, p))
      |> Array.toList
    // Reject loudly on the first escaping project rather than dropping it.
    match resolved |> List.tryFind (isContainedInDir >> not) with
    | Some escaping ->
      Error (SageFsError.UnsafeSessionPath(escaping, "project path escapes the session working directory"))
    | None -> Ok resolved
  | true ->
    match DirectoryConfig.load dir with
    | Some config ->
      match config.Load with
      | Solution path ->
        let full = match Path.IsPathRooted path with | true -> path | false -> Path.Combine(dir, path)
        Ok [ full ]
      | Projects paths ->
        paths |> List.map (fun p ->
          match Path.IsPathRooted p with
          | true -> p
          | false -> Path.Combine(dir, p))
        |> Ok
      | NoLoad -> Ok []
      | AutoDetect -> Ok (autoDetectProjects dir)
    | _ -> Ok (autoDetectProjects dir)

/// Raised when a request body exceeds the configured size limit (results in 413 response).
/// Handlers that use readSignalsJsonSized or checkBodySize should catch this exception
/// and return without writing a second response — the 413 is already committed.
exception RequestTooLargeException

/// Write a 413 response body (internal helper).
let private write413Body (ctx: Microsoft.AspNetCore.Http.HttpContext) = task {
  ctx.Response.StatusCode <- 413
  do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes """{"error":"Request body too large"}""")
}

/// Check body ContentLength and raise RequestTooLargeException (after writing 413) if > 1 MB.
/// Call before reading the body in any POST handler that doesn't already have a size cap.
let checkBodySize (ctx: Microsoft.AspNetCore.Http.HttpContext) = task {
  let maxBytes = 1_048_576L
  match ctx.Request.ContentLength with
  | cl when cl.HasValue && cl.Value > maxBytes ->
    do! write413Body ctx
    raise RequestTooLargeException
  | _ -> ()
}

/// Size-guarded wrapper for Request.getSignalsJson (Falco.Datastar).
/// Raises RequestTooLargeException (after writing 413) if ContentLength > 1 MB.
/// W2(R8): Sets IHttpMaxRequestBodySizeFeature.MaxRequestBodySize to cap chunked requests.
/// W2(R9): Fail-closed: if the feature is null (reverse proxy) or IsReadOnly (body already
///         started reading), raise 413 rather than proceed unguarded with no cap enforced.
let readSignalsJsonSized (ctx: Microsoft.AspNetCore.Http.HttpContext) : System.Threading.Tasks.Task<System.Text.Json.JsonDocument> = task {
  let maxBytes = 1_048_576L
  let maxBodyFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
  match maxBodyFeature with
  | null ->
    // Feature unavailable (reverse proxy stripped it). Fall through — checkBodySize provides
    // header-based gate. No Kestrel cap can be set; ContentLength header is our only defence.
    ()
  | f when f.IsReadOnly ->
    // Body already started reading (e.g., buffering middleware called EnableBuffering()).
    // Kestrel cap cannot be set at this point. Header-based check is our only gate.
    ()
  | f ->
    f.MaxRequestBodySize <- maxBytes
  do! checkBodySize ctx
  let! doc = Request.getSignalsJson ctx
  return doc
}

/// Helper: extract a signal by camelCase or kebab-case name from JSON signals.
let getSignalString (doc: System.Text.Json.JsonDocument) (camelCase: string) (kebab: string) =
  match doc.RootElement.TryGetProperty(camelCase) with
  | true, prop -> prop.GetString()
  | _ ->
    match doc.RootElement.TryGetProperty(kebab) with
    | true, prop -> prop.GetString()
    | _ -> ""

/// Parse an app-level message, falling back to EditorAction wrapped in SageFsMsg.Editor.
let parseAppMsg (actionName: string) (editorAction: EditorAction option) : SageFsMsg option =
  match actionName with
  | "enableLiveTesting" -> Some SageFsMsg.EnableLiveTesting
  | "disableLiveTesting" -> Some SageFsMsg.DisableLiveTesting
  | "cycleRunPolicy" -> Some SageFsMsg.CycleRunPolicy
  | "toggleCoverage" -> Some SageFsMsg.ToggleCoverage
  | _ -> editorAction |> Option.map SageFsMsg.Editor
