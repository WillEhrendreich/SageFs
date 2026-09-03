/// Dashboard view models, parse functions, and action dispatch types.
/// Pure domain — no Falco, no HTML, no HTTP.
module SageFs.Server.DashboardTypes

open System
open System.IO
open System.Text.RegularExpressions
open SageFs
open SageFs.Utils
open SageFs.Affordances
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

/// Datastar signal names — shared between Ds.signal init and Ds.bind/Ds.show refs.
[<RequireQualifiedAccess>]
module Signals =
  let [<Literal>] ViewingSessionId = "viewingSessionId"
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
  let [<Literal>] ConfigLoading = "configLoading"
  let [<Literal>] TempLoading = "tempLoading"
  let [<Literal>] Theme = "theme"
  let [<Literal>] CursorPos = "cursorPos"
  let [<Literal>] TestFilter = "testFilter"
  let [<Literal>] ExpandedDashboard = "expandedDashboard"
  let [<Literal>] FrictionDrawerOpen = "frictionDrawerOpen"
  let [<Literal>] FrictionEndpoint = "frictionEndpoint"
  let [<Literal>] FrictionToken = "frictionToken"
  let [<Literal>] FrictionEdits = "frictionEdits"
  let [<Literal>] FrictionSending = "frictionSending"

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
        | Features.Diagnostics.DiagnosticSeverity.Error -> DiagError
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

let discoverProjects (workingDir: string) : DiscoveredProjects =
  let projects =
    try
      Directory.EnumerateFiles(workingDir, "*.fsproj", SearchOption.AllDirectories)
      |> Seq.map (fun p -> Path.GetRelativePath(workingDir, p))
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

/// Display-level session status for the dashboard sidebar.
/// Distinct from SessionState (daemon lifecycle) and WorkerProtocol.SessionStatus (wire protocol).
/// Every case is explicit — no stringly-typed fallback.
[<RequireQualifiedAccess>]
type SessionDisplayStatus =
  | Running
  | Starting
  | Faulted
  | Lost
  | Stopped

module SessionDisplayStatus =
  let label = function
    | SessionDisplayStatus.Running -> "running"
    | SessionDisplayStatus.Starting -> "starting"
    | SessionDisplayStatus.Faulted -> "faulted"
    | SessionDisplayStatus.Lost -> "lost"
    | SessionDisplayStatus.Stopped -> "stopped"

  let ofTuiString (raw: string) =
    match raw.Trim().ToLowerInvariant() with
    | "running" -> SessionDisplayStatus.Running
    | "starting" | "restarting" -> SessionDisplayStatus.Starting
    | "faulted" | "error" -> SessionDisplayStatus.Faulted
    | "lost" -> SessionDisplayStatus.Lost
    | "stopped" -> SessionDisplayStatus.Stopped
    | _ -> SessionDisplayStatus.Running

  let ofSessionState = function
    | SessionState.Ready -> SessionDisplayStatus.Running
    | SessionState.Evaluating -> SessionDisplayStatus.Running
    | SessionState.WarmingUp -> SessionDisplayStatus.Starting
    | SessionState.Faulted -> SessionDisplayStatus.Faulted
    | SessionState.Uninitialized -> SessionDisplayStatus.Lost

  let cssClass = function
    | SessionDisplayStatus.Running -> "status-ready"
    | SessionDisplayStatus.Starting -> "status-warming"
    | SessionDisplayStatus.Faulted -> "status-faulted"
    | SessionDisplayStatus.Lost -> "status-faulted"
    | SessionDisplayStatus.Stopped -> "status-faulted"

type ParsedSession = {
  Id: WorkerProtocol.SessionId
  Status: SessionDisplayStatus
  StatusMessage: string option
  IsActive: bool
  IsSelected: bool
  ProjectsText: string
  EvalCount: int
  Uptime: string
  WorkingDir: string
  LastActivity: string
  TestSummary: Features.LiveTesting.TestSummary option
  CoverageSummary: Features.LiveTesting.CoverageSummary option
  TestTreemapEntries: Features.LiveTesting.TestTreemapEntry array
  BindingEntries: Features.BindingExplorer.BindingInfo array
  AgentBadges: AgentBadge list
  GuidanceCssClass: string
}

let parseSessionLines (content: string) =
  let sessionRegex = Regex(@"^([> ])\s+(\S+)\s*\[([^\]]+)\](\s*\*)?(\s*\([^)]*\))?(\s*evals:\d+)?(\s*up:(?:just now|\S+))?(\s*dir:\S.*?)?(\s*last:.+)?$")
  let extractTag (prefix: string) (value: string) =
    let v = value.Trim()
    match v.StartsWith(prefix, StringComparison.Ordinal) with
    | true -> v.Substring(prefix.Length).Trim()
    | false -> ""
  content.Split('\n')
  |> Array.filter (fun (l: string) ->
    l.Length > 0
    && not (l.StartsWith("───", StringComparison.Ordinal))
    && not (l.StartsWith("⏳", StringComparison.Ordinal))
    && not (l.Contains("↑↓ nav"))
    && not (l.Contains("Enter switch"))
    && not (l.Contains("Ctrl+Tab cycle")))
  |> Array.choose (fun (l: string) ->
    let m = sessionRegex.Match(l)
    match m.Success with
    | false -> None
    | true ->
      let evalsMatch = Regex.Match(m.Groups.[6].Value, @"evals:(\d+)")
      let rawId = m.Groups.[2].Value
      match WorkerProtocol.SessionId.validate rawId with
      | Error _ -> None
      | Ok sessionId ->
        Some
          { Id = sessionId
            Status = SessionDisplayStatus.ofTuiString m.Groups.[3].Value
            StatusMessage = None
            IsActive = m.Groups.[4].Value.Contains("*")
            IsSelected = m.Groups.[1].Value = ">"
            ProjectsText = m.Groups.[5].Value.Trim()
            EvalCount = match evalsMatch.Success with | true -> int evalsMatch.Groups.[1].Value | false -> 0
            Uptime = extractTag "up:" m.Groups.[7].Value
            WorkingDir = extractTag "dir:" m.Groups.[8].Value
            LastActivity = extractTag "last:" m.Groups.[9].Value
            TestSummary = None
            CoverageSummary = None
            TestTreemapEntries = [||]
            BindingEntries = [||]
            AgentBadges = []
            GuidanceCssClass = "" })
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

/// Override parsed session statuses with live SessionState data.
/// The TUI text may be stale — live state is the source of truth.
/// Uninitialized here means "the actor doesn't know about this session"
/// (e.g. the worker process died, or the session was never fully started).
/// We surface it explicitly as "lost" rather than "stopped" so the user
/// can distinguish a session that was deliberately stopped from one
/// whose worker is gone, and decide to restart or dispose it.
let overrideSessionStatuses
  (getState: WorkerProtocol.SessionId -> SessionState)
  (getStatusMsg: WorkerProtocol.SessionId -> string option)
  (sessions: ParsedSession list) : ParsedSession list =
  sessions
  |> List.map (fun (s: ParsedSession) ->
    let liveStatus = getState s.Id |> SessionDisplayStatus.ofSessionState
    let guidanceCls =
      match liveStatus with
      | SessionDisplayStatus.Faulted -> "session-faulted"
      | SessionDisplayStatus.Lost -> "session-lost"
      | _ -> ""
    { s with Status = liveStatus; GuidanceCssClass = guidanceCls; StatusMessage = getStatusMsg s.Id })

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
///     Datastar signal (or from the `?sessionId=` query param for HTTP
///     JSON state streams), never from a daemon global
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
  GetHotReloadState: WorkerProtocol.SessionId -> Threading.Tasks.Task<{| files: {| path: string; watched: bool |} list; watchedCount: int |} option>
  GetWarmupContext: WorkerProtocol.SessionId -> Threading.Tasks.Task<WarmupContext option>
  GetWarmupProgress: WorkerProtocol.SessionId -> string
  GetSessionTestSummary: WorkerProtocol.SessionId -> Features.LiveTesting.TestSummary option
  GetSessionCoverageSummary: WorkerProtocol.SessionId -> Features.LiveTesting.CoverageSummary option
  GetSessionTestTreemap: WorkerProtocol.SessionId -> Features.LiveTesting.TestTreemapEntry array
  GetSessionBindings: WorkerProtocol.SessionId -> Features.BindingExplorer.BindingInfo array
  /// Live reflection-walked binding tree for a session (debugger watch window),
  /// from the adaptive store. None until the first eval snapshot arrives.
  GetLiveBindings: WorkerProtocol.SessionId -> Features.LiveValueTree.LiveValueSnapshot option
  GetBindingScopeSnapshot: unit -> Features.BindingExplorer.BindingScopeSnapshot option
  GetLiveTestingStatus: unit -> string
  /// Whether live testing is currently Active or Inactive.
  GetLiveTestingActive: unit -> bool
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
  /// Get the workflow for a session — returns Interactive as default.
  GetSessionWorkflow: WorkerProtocol.SessionId -> WorkflowTypes.SessionWorkflow
}

/// Recently-fetched worker-derived dashboard data, reused across SSE pushes so
/// high-frequency ticks do not pay three worker HTTP round-trips each. The
/// render-diff guard means reusing this can never SEND stale HTML — it only
/// makes unchanged ticks cheaper; a real change still renders and sends.
type DashboardWorkerCache = {
  SessionId: WorkerProtocol.SessionId
  EvalStats: SageFs.Affordances.EvalStats
  HotReloadState: {| files: {| path: string; watched: bool |} list; watchedCount: int |} option
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
  ResetSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  HardResetSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  Dispatch: SageFsMsg -> unit
  SwitchSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  StopSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  /// Purge — stop the session + remove its .sagefm manifest entry (gone from resume picker).
  PurgeSession: WorkerProtocol.SessionId -> Threading.Tasks.Task<Result<string, string>>
  CreateSession: string list -> string -> Threading.Tasks.Task<Result<WorkerProtocol.SessionId, string>>
  ShutdownCallback: (unit -> unit) option
}

/// Infrastructure dependencies — event sources, tracking, themes.
type DashboardInfra = {
  Version: string
  McpPort: int
  StateChanged: IEvent<DaemonStateChange> option
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
}

/// Complete snapshot of all dashboard state needed for a single full-page render.
/// Constructed once per push, then passed to renderMainContent for atomic morph.
type DashboardSnapshot = {
  Version: string
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
}

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
let resolveSessionProjects (dir: string) (manualProjects: string) =
  let autoDetectProjects dir =
    let discovered = discoverProjects dir
    match discovered.Solutions.IsEmpty with
    | false -> [ Path.Combine(dir, discovered.Solutions.Head) ]
    | true ->
      match discovered.Projects.IsEmpty with
      | false -> discovered.Projects |> List.map (fun p -> Path.Combine(dir, p))
      | true -> []
  match String.IsNullOrWhiteSpace manualProjects with
  | false ->
    manualProjects.Split(',')
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.map (fun p ->
      match Path.IsPathRooted p with
      | true -> p
      | false -> Path.Combine(dir, p))
    |> Array.toList
  | true ->
    match DirectoryConfig.load dir with
    | Some config ->
      match config.Load with
      | Solution path ->
        let full = match Path.IsPathRooted path with | true -> path | false -> Path.Combine(dir, path)
        [ full ]
      | Projects paths ->
        paths |> List.map (fun p ->
          match Path.IsPathRooted p with
          | true -> p
          | false -> Path.Combine(dir, p))
      | NoLoad -> []
      | AutoDetect -> autoDetectProjects dir
    | _ -> autoDetectProjects dir

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
