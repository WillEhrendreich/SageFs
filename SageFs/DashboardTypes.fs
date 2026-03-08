/// Dashboard view models, parse functions, and action dispatch types.
/// Pure domain — no Falco, no HTML, no HTTP.
module SageFs.Server.DashboardTypes

open System
open System.IO
open System.Text.RegularExpressions
open SageFs
open SageFs.Utils
open SageFs.Affordances
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
  let [<Literal>] DiscoveredProjects = "discovered-projects"
  let [<Literal>] HotReloadPanel = "hot-reload-panel"
  let [<Literal>] TestTrace = "test-trace"
  let [<Literal>] ThemeVars = "theme-vars"
  let [<Literal>] ThemePicker = "theme-picker"
  let [<Literal>] ServerStatus = "server-status"
  let [<Literal>] CompletionDropdown = "completion-dropdown"
  let [<Literal>] KeyboardHelp = "keyboard-help"
  let [<Literal>] KeyboardHelpWrapper = "keyboard-help-wrapper"
  let [<Literal>] ConnectionCounts = "connection-counts"
  let [<Literal>] EditorArea = "editor-area"
  let [<Literal>] OutputSection = "output-section"
  let [<Literal>] Sidebar = "sidebar"
  let [<Literal>] SidebarResize = "sidebar-resize"
  let [<Literal>] BindingsPanel = "bindings-panel"

/// Datastar signal names — shared between Ds.signal init and Ds.bind/Ds.show refs.
[<RequireQualifiedAccess>]
module Signals =
  let [<Literal>] SessionId = "sessionId"
  let [<Literal>] Code = "code"
  let [<Literal>] HelpVisible = "helpVisible"
  let [<Literal>] SidebarOpen = "sidebarOpen"
  let [<Literal>] NewSessionDir = "newSessionDir"
  let [<Literal>] ManualProjects = "manualProjects"
  let [<Literal>] EvalLoading = "evalLoading"
  let [<Literal>] DiscoverLoading = "discoverLoading"
  let [<Literal>] CreateLoading = "createLoading"
  let [<Literal>] ConfigLoading = "configLoading"
  let [<Literal>] TempLoading = "tempLoading"
  let [<Literal>] Theme = "theme"
  let [<Literal>] CursorPos = "cursorPos"

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

type ParsedSession = {
  Id: string
  Status: string
  StatusMessage: string option
  IsActive: bool
  IsSelected: bool
  ProjectsText: string
  EvalCount: int
  Uptime: string
  WorkingDir: string
  LastActivity: string
  StandbyLabel: string
  TestSummary: Features.LiveTesting.TestSummary option
  CoverageSummary: Features.LiveTesting.CoverageSummary option
  TestTreemapEntries: Features.LiveTesting.TestTreemapEntry array
  BindingEntries: Features.BindingExplorer.BindingInfo array
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
  |> Array.map (fun (l: string) ->
    let m = sessionRegex.Match(l)
    match m.Success with
    | true ->
      let evalsMatch = Regex.Match(m.Groups.[6].Value, @"evals:(\d+)")
      { Id = m.Groups.[2].Value
        Status = m.Groups.[3].Value
        StatusMessage = None
        IsActive = m.Groups.[4].Value.Contains("*")
        IsSelected = m.Groups.[1].Value = ">"
        ProjectsText = m.Groups.[5].Value.Trim()
        EvalCount = match evalsMatch.Success with | true -> int evalsMatch.Groups.[1].Value | false -> 0
        Uptime = extractTag "up:" m.Groups.[7].Value
        WorkingDir = extractTag "dir:" m.Groups.[8].Value
        LastActivity = extractTag "last:" m.Groups.[9].Value
        StandbyLabel = ""
        TestSummary = None
        CoverageSummary = None
        TestTreemapEntries = [||]
        BindingEntries = [||] }
    | false ->
      { Id = l.Trim()
        Status = "unknown"
        StatusMessage = None
        IsActive = false
        IsSelected = false
        ProjectsText = ""
        EvalCount = 0
        Uptime = ""
        WorkingDir = ""
        LastActivity = ""
        StandbyLabel = ""
        TestSummary = None
        CoverageSummary = None
        TestTreemapEntries = [||]
        BindingEntries = [||] })
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
let overrideSessionStatuses
  (getState: string -> SessionState)
  (getStatusMsg: string -> string option)
  (sessions: ParsedSession list) : ParsedSession list =
  sessions
  |> List.map (fun (s: ParsedSession) ->
    let liveStatus =
      match getState s.Id with
      | SessionState.Ready -> "running"
      | SessionState.Evaluating -> "running"
      | SessionState.WarmingUp -> "starting"
      | SessionState.Faulted -> "faulted"
      | SessionState.Uninitialized -> "stopped"
    { s with Status = liveStatus; StatusMessage = getStatusMsg s.Id })

/// State queries — always-present read accessors for dashboard rendering.
type DashboardQueries = {
  GetSessionState: string -> SessionState
  GetStatusMsg: string -> string option
  GetEvalStats: string -> Threading.Tasks.Task<SageFs.Affordances.EvalStats>
  GetSessionWorkingDir: string -> string
  GetActiveSessionId: unit -> string
  GetElmRegions: unit -> RenderRegion list option
  GetPreviousSessions: unit -> Threading.Tasks.Task<PreviousSession list>
  GetAllSessions: unit -> Threading.Tasks.Task<WorkerProtocol.SessionInfo list>
  GetStandbyInfo: unit -> Threading.Tasks.Task<StandbyInfo>
  GetSessionStandbyInfo: string -> StandbyInfo
  GetHotReloadState: string -> Threading.Tasks.Task<{| files: {| path: string; watched: bool |} list; watchedCount: int |} option>
  GetWarmupContext: string -> Threading.Tasks.Task<WarmupContext option>
  GetWarmupProgress: string -> string
  GetSessionTestSummary: string -> Features.LiveTesting.TestSummary option
  GetSessionCoverageSummary: string -> Features.LiveTesting.CoverageSummary option
  GetSessionTestTreemap: string -> Features.LiveTesting.TestTreemapEntry array
  GetSessionBindings: string -> Features.BindingExplorer.BindingInfo array
  GetBindingScopeSnapshot: unit -> Features.BindingExplorer.BindingScopeSnapshot option
  GetLiveTestingStatus: unit -> string
}

/// Commands that mutate session state.
type DashboardActions = {
  EvalCode: string -> string -> Threading.Tasks.Task<Result<string, string>>
  ResetSession: string -> Threading.Tasks.Task<Result<string, string>>
  HardResetSession: string -> Threading.Tasks.Task<Result<string, string>>
  Dispatch: SageFsMsg -> unit
  SwitchSession: (string -> Threading.Tasks.Task<Result<string, string>>) option
  StopSession: (string -> Threading.Tasks.Task<Result<string, string>>) option
  CreateSession: (string list -> string -> Threading.Tasks.Task<Result<string, string>>) option
  ShutdownCallback: (unit -> unit) option
}

/// Infrastructure dependencies — event sources, tracking, themes.
type DashboardInfra = {
  Version: string
  StateChanged: IEvent<DaemonStateChange> option
  ConnectionTracker: ConnectionTracker option
  SessionThemes: Collections.Concurrent.ConcurrentDictionary<string, string>
  GetCompletions: (string -> string -> int -> Threading.Tasks.Task<Features.AutoCompletion.CompletionItem list>) option
}

/// Complete snapshot of all dashboard state needed for a single full-page render.
/// Constructed once per push, then passed to renderMainContent for atomic morph.
type DashboardSnapshot = {
  Version: string
  SessionState: string
  SessionId: string
  WorkingDir: string
  WarmupProgress: string
  EvalStats: EvalStatsView
  ThemeName: string
  ConnectionLabel: string option
  HotReloadPanel: XmlNode
  SessionContextPanel: XmlNode
  OutputPanel: XmlNode
  SessionsPanel: XmlNode
  SessionPicker: XmlNode
  ThemePicker: XmlNode
  ThemeVars: XmlNode
  BindingsPanel: XmlNode
}

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

/// Save theme preferences to ~/.SageFs/themes.json
let saveThemes (sageFsDir: string) (themes: Collections.Concurrent.ConcurrentDictionary<string, string>) =
  try
    match Directory.Exists sageFsDir with
    | false -> Directory.CreateDirectory sageFsDir |> ignore
    | true -> ()
    let path = Path.Combine(sageFsDir, "themes.json")
    let dict = themes |> Seq.map (fun kv -> kv.Key, kv.Value) |> dict
    let json = Text.Json.JsonSerializer.Serialize(dict, Text.Json.JsonSerializerOptions(WriteIndented = true))
    File.WriteAllText(path, json)
  with ex -> Log.warn "Failed to save themes to %s: %s" sageFsDir ex.Message

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
  with ex -> Log.warn "Failed to load themes from %s: %s" sageFsDir ex.Message
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
/// W2(R8): Also sets IHttpMaxRequestBodySizeFeature.MaxRequestBodySize to cap chunked
///         requests that arrive without a Content-Length header (bypassing checkBodySize).
let readSignalsJsonSized (ctx: Microsoft.AspNetCore.Http.HttpContext) : System.Threading.Tasks.Task<System.Text.Json.JsonDocument> = task {
  let maxBytes = 1_048_576L
  // Enforce Kestrel-level cap first so chunked bodies are bounded before we read any bytes.
  let maxBodyFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
  if not (isNull maxBodyFeature) && not maxBodyFeature.IsReadOnly then
    maxBodyFeature.MaxRequestBodySize <- maxBytes
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
