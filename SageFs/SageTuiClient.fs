module SageFs.Server.SageTuiClient

open System
open System.Net.Http
open System.Threading
open SageFs
open SageFs.Measures
open SageTUI

// Disambiguate names that conflict between SageFs and SageTUI
type private SageFsDirection = SageFs.Direction
type private SageFsKeyMap = Map<KeyCombo, UiAction>

// ── Theme bridging ──

/// Convert a SageFs hex color (e.g. "#ff5f5f") to a SageTUI Color.Rgb.
let private hexToColor (hex: string) : Color =
  let rgb = Theme.hexToRgb hex
  Color.Rgb(Theme.rgbR rgb, Theme.rgbG rgb, Theme.rgbB rgb)

// ── Failing test navigation state ──

type FailingNav = {
  Index: int
  Hint: string
}

module FailingNav =
  let empty = { Index = -1; Hint = "" }

// ── TEA Model ──

type Model = {
  Regions: RenderRegion list
  SessionId: string
  SessionState: string
  EvalCount: int
  AvgMs: float
  WorkingDir: string
  StandbyLabel: string
  LiveTestingStatus: string
  WatchedCount: int
  Density: UiDensity
  FocusedPane: PaneId
  ScrollOffsets: Map<PaneId, int>
  Layout: LayoutConfig
  Theme: ThemeConfig
  ThemeName: string
  SessionThemes: Map<string, string>
  TimeTravel: TimeTravel.TimeTravelState<RenderRegion list>
  FailingNav: FailingNav
  EvalInterrupted: bool
  Connected: bool
  StatusMessage: string
  FrameMs: float
  BaseUrl: string
  KeyMap: SageFsKeyMap
  HttpClient: HttpClient
  TestSourceLocations: Map<string, string * int>
}

// ── TEA Messages ──

type Msg =
  // SSE
  | SseStateReceived of StateEvent * RenderRegion list
  | SseReconnecting of string
  | SseConnected
  // Keyboard commands (mapped from TerminalCommand)
  | Quit
  | Redraw
  | CycleFocus
  | FocusDir of SageFsDirection
  | ScrollUp
  | ScrollDown
  | TogglePane of PaneId
  | SetLayout of string
  | ResizeH of int
  | ResizeV of int
  | ResizeR of int
  | CycleTheme
  | CycleDensity
  | TimeTravelBack
  | TimeTravelForward
  | TimeTravelGoLive
  | NextFailingTest
  | PrevFailingTest
  | JumpToTest
  | MarkAllStale
  | HotReloadWatchAll
  | HotReloadUnwatchAll
  | EnableLiveTesting
  | DisableLiveTesting
  | CycleRunPolicy
  | ToggleCoverage
  // Editor actions dispatched to daemon
  | DispatchAction of EditorAction
  | InsertChar of char
  // Mouse
  | ClickPane of PaneId * row: int * col: int
  | MouseScroll of up: bool
  // Daemon HTTP command (fire-and-forget)
  | HttpPost of path: string * body: string
  // Frame timing
  | FrameTiming of SageTUI.FrameTimings

// ── UX Hint Helpers ──

/// Returns the recovery hint text for a faulted session, or None if not faulted.
let faultedRecoveryHint (sessionState: string) : string option =
  match sessionState with
  | s when s.StartsWith("Faulted") ->
    Some "⚠ Session faulted — press Ctrl+R to hard reset, or run sagefs check"
  | _ -> None

/// Returns true when the first-run evangelical hint should be displayed.
let shouldShowEvangelicalHint (sessionState: string) (evalCount: int) : bool =
  sessionState = "Ready" && evalCount = 0

// ── Init ──

let init (daemonInfo: DaemonInfo) (keyMap: SageFsKeyMap) () : Model * Cmd<Msg> =
  let dashboardPort = daemonInfo.DashboardPort
  let baseUrl = sprintf "http://localhost:%d" dashboardPort
  let handler = new HttpClientHandler(AutomaticDecompression = Net.DecompressionMethods.All)
  let client = new HttpClient(handler)
  client.Timeout <- Timeouts.sseKeepAlive
  let theme =
    match ThemePresets.tryFind "Kanagawa" with
    | Some t -> t
    | None -> Theme.defaults
  { Regions = []
    SessionId = ""
    SessionState = "Connecting..."
    EvalCount = 0
    AvgMs = 0.0
    WorkingDir = ""
    StandbyLabel = ""
    LiveTestingStatus = ""
    WatchedCount = 0
    Density = UiDensity.Normal
    FocusedPane = PaneId.Output
    ScrollOffsets = Map.empty
    Layout = LayoutConfig.defaults
    Theme = theme
    ThemeName = "Kanagawa"
    SessionThemes = Map.empty
    TimeTravel = TimeTravel.create { ModelSnapshot.Capacity = 200; ModelSnapshot.Enabled = true }
    FailingNav = FailingNav.empty
    EvalInterrupted = false
    Connected = false
    StatusMessage = "Connecting to SageFs daemon..."
    FrameMs = 0.0
    BaseUrl = baseUrl
    KeyMap = keyMap
    HttpClient = client
    TestSourceLocations = Map.empty },
  Cmd.none

// ── Helpers ──

let private navigateFailingTests (delta: int) (model: Model) (gridRows: int) : Model =
  let failing =
    model.Regions
    |> List.toArray
    |> Array.collect (fun r ->
      r.LineAnnotations
      |> Array.filter (fun a -> a.Icon = Features.LiveTesting.GutterIcon.TestFailed)
      |> Array.map (fun a -> a.Line))
    |> Array.sort
    |> Array.distinct
  match failing.Length with
  | 0 -> { model with FailingNav = { model.FailingNav with Hint = "" } }
  | total ->
    let newIdx =
      match model.FailingNav.Index < 0 with
      | true -> match delta > 0 with | true -> 0 | false -> total - 1
      | false -> (model.FailingNav.Index + delta + total) % total
    let targetLine = failing.[newIdx]
    let hint = sprintf "↯%d/%d" (newIdx + 1) total
    let totalLines =
      model.Regions
      |> List.tryFind (fun r -> r.Id = "editor")
      |> Option.map (fun r -> r.Content.Split('\n').Length)
      |> Option.defaultValue 100
    let approxHeight = max 1 (gridRows - 4)
    let scrollOff = max 0 (totalLines - approxHeight - targetLine + 3)
    { model with
        FailingNav = { Index = newIdx; Hint = hint }
        ScrollOffsets = model.ScrollOffsets |> Map.add PaneId.Editor scrollOff }

/// Fire-and-forget HTTP POST to daemon
let private httpPostCmd (model: Model) (path: string) (body: string) : Cmd<Msg> =
  Cmd.ofAsync (fun _dispatch -> async {
    try
      use content = new StringContent(body, Text.Encoding.UTF8, "application/json")
      let! _ = model.HttpClient.PostAsync(sprintf "%s%s" model.BaseUrl path, content) |> Async.AwaitTask
      ()
    with _ -> ()
  })

// ── Update ──

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
  match msg with
  | SseConnected ->
    { model with Connected = true; StatusMessage = "Connected"; EvalInterrupted = false }, Cmd.none

  | SseReconnecting _ ->
    let wasEvaluating =
      model.SessionState.Contains("Evaluating", StringComparison.OrdinalIgnoreCase)
    let newState =
      match wasEvaluating with
      | true -> "⚠ Eval interrupted — daemon disconnected (reconnecting...)"
      | false -> sprintf "%s (reconnecting...)" model.SessionState
    { model with
        Connected = false
        EvalInterrupted = wasEvaluating
        SessionState = newState
        StatusMessage = "Reconnecting..." }, Cmd.none

  | SseStateReceived (event, regions) ->
    // Theme per-workspace switching
    let sessionThemes, theme, themeName =
      match event.ActiveWorkingDir.Length > 0 && event.ActiveWorkingDir <> model.WorkingDir && model.WorkingDir.Length > 0 with
      | true ->
        let st = model.SessionThemes |> Map.add model.WorkingDir model.ThemeName
        match st |> Map.tryFind event.ActiveWorkingDir with
        | Some name ->
          match ThemePresets.tryFind name with
          | Some t -> st, t, name
          | None -> st, model.Theme, model.ThemeName
        | None -> st, model.Theme, model.ThemeName
      | false -> model.SessionThemes, model.Theme, model.ThemeName
    let workingDir = match event.ActiveWorkingDir.Length > 0 with | true -> event.ActiveWorkingDir | false -> model.WorkingDir
    let tt = TimeTravel.record event.SessionState 0.0<SageFs.Measures.ms> regions model.TimeTravel
    { model with
        Regions = regions
        SessionId = event.SessionId
        SessionState = event.SessionState
        EvalCount = event.EvalCount
        AvgMs = event.AvgMs
        WorkingDir = workingDir
        StandbyLabel = event.StandbyLabel
        LiveTestingStatus = event.LiveTestingStatus
        WatchedCount = event.WatchedCount
        EvalInterrupted = false
        Connected = true
        SessionThemes = sessionThemes
        Theme = theme
        ThemeName = themeName
        TestSourceLocations =
          match event.TestSourceLocations.IsEmpty with
          | true -> model.TestSourceLocations
          | false -> event.TestSourceLocations
        TimeTravel = tt }, Cmd.none

  | Quit -> model, Cmd.quit
  | Redraw -> model, Cmd.none // SageTUI always redraws on model change

  | CycleFocus ->
    { model with FocusedPane = PaneId.nextVisible model.Layout.VisiblePanes model.FocusedPane }, Cmd.none

  | FocusDir _dir ->
    // Simplified: cycle for now; full spatial nav needs pane rects from layout
    { model with FocusedPane = PaneId.nextVisible model.Layout.VisiblePanes model.FocusedPane }, Cmd.none

  | ScrollUp ->
    let cur = model.ScrollOffsets |> Map.tryFind model.FocusedPane |> Option.defaultValue 0
    { model with ScrollOffsets = model.ScrollOffsets |> Map.add model.FocusedPane (cur + 3) }, Cmd.none

  | ScrollDown ->
    let cur = model.ScrollOffsets |> Map.tryFind model.FocusedPane |> Option.defaultValue 0
    { model with ScrollOffsets = model.ScrollOffsets |> Map.add model.FocusedPane (max 0 (cur - 3)) }, Cmd.none

  | TogglePane pid ->
    let layout = LayoutConfig.togglePane pid model.Layout
    let focused =
      match layout.VisiblePanes.Contains model.FocusedPane with
      | true -> model.FocusedPane
      | false -> PaneId.firstVisible layout.VisiblePanes
    { model with Layout = layout; FocusedPane = focused }, Cmd.none

  | SetLayout presetName ->
    let layout =
      match presetName with
      | "focus" -> LayoutConfig.focus
      | "minimal" -> LayoutConfig.minimal
      | _ -> LayoutConfig.defaults
    let focused =
      match layout.VisiblePanes.Contains model.FocusedPane with
      | true -> model.FocusedPane
      | false -> PaneId.firstVisible layout.VisiblePanes
    { model with Layout = layout; FocusedPane = focused }, Cmd.none

  | ResizeH d -> { model with Layout = LayoutConfig.resizeH d model.Layout }, Cmd.none
  | ResizeV d -> { model with Layout = LayoutConfig.resizeV d model.Layout }, Cmd.none
  | ResizeR d -> { model with Layout = LayoutConfig.resizeR d model.Layout }, Cmd.none

  | CycleTheme ->
    let name, theme = ThemePresets.cycleNext model.Theme
    let st =
      match model.WorkingDir.Length > 0 with
      | true -> model.SessionThemes |> Map.add model.WorkingDir name
      | false -> model.SessionThemes
    { model with Theme = theme; ThemeName = name; SessionThemes = st }, Cmd.none

  | CycleDensity ->
    { model with Density = UiDensity.cycle model.Density }, Cmd.none

  | TimeTravelBack ->
    { model with TimeTravel = TimeTravel.stepBack model.TimeTravel }, Cmd.none
  | TimeTravelForward ->
    { model with TimeTravel = TimeTravel.stepForward model.TimeTravel }, Cmd.none
  | TimeTravelGoLive ->
    { model with TimeTravel = TimeTravel.goLive model.TimeTravel }, Cmd.none

  | NextFailingTest ->
    // Use 30 as approximate; SageTUI handles actual terminal height
    navigateFailingTests 1 model 30, Cmd.none
  | PrevFailingTest ->
    navigateFailingTests (-1) model 30, Cmd.none

  | JumpToTest ->
    let scrollOff = model.ScrollOffsets |> Map.tryFind model.FocusedPane |> Option.defaultValue 0
    match JumpToTest.getSelectedTestLocation model.Regions model.FocusedPane scrollOff model.TestSourceLocations with
    | Some (file, line) -> JumpToTest.openInEditor file line; model, Cmd.none
    | None -> model, Cmd.none

  | MarkAllStale ->
    model, httpPostCmd model "/api/live-testing/mark-all-stale" "{}"

  | HotReloadWatchAll ->
    match model.SessionId.Length > 0 with
    | true -> model, httpPostCmd model (sprintf "/api/sessions/%s/hotreload/watch-all" model.SessionId) "{}"
    | false -> model, Cmd.none

  | HotReloadUnwatchAll ->
    match model.SessionId.Length > 0 with
    | true -> model, httpPostCmd model (sprintf "/api/sessions/%s/hotreload/unwatch-all" model.SessionId) "{}"
    | false -> model, Cmd.none

  | EnableLiveTesting ->
    model, httpPostCmd model "/api/dispatch" """{"action":"enableLiveTesting"}"""
  | DisableLiveTesting ->
    model, httpPostCmd model "/api/dispatch" """{"action":"disableLiveTesting"}"""
  | CycleRunPolicy ->
    model, httpPostCmd model "/api/dispatch" """{"action":"cycleRunPolicy"}"""
  | ToggleCoverage ->
    model, httpPostCmd model "/api/dispatch" """{"action":"toggleCoverage"}"""

  | DispatchAction action ->
    let remapped =
      match model.FocusedPane = PaneId.Sessions with
      | true ->
        match action with
        | EditorAction.MoveCursor SageFsDirection.Up -> EditorAction.SessionNavUp
        | EditorAction.MoveCursor SageFsDirection.Down -> EditorAction.SessionNavDown
        | EditorAction.NewLine -> EditorAction.SessionSelect
        | EditorAction.DeleteBackward -> EditorAction.SessionDelete
        | EditorAction.DeleteForward -> EditorAction.SessionDelete
        | other -> other
      | false -> action
    model, Cmd.ofAsync (fun _dispatch -> async {
      try
        do! DaemonClient.dispatch model.HttpClient model.BaseUrl remapped |> Async.AwaitTask
      with _ -> ()
    })

  | InsertChar ch ->
    model, Cmd.ofAsync (fun _dispatch -> async {
      try
        do! DaemonClient.dispatch model.HttpClient model.BaseUrl (EditorAction.InsertChar ch) |> Async.AwaitTask
      with _ -> ()
    })

  | ClickPane (paneId, row, col) ->
    let m = { model with FocusedPane = paneId }
    match paneId with
    | PaneId.Editor ->
      let scrollOff = model.ScrollOffsets |> Map.tryFind PaneId.Editor |> Option.defaultValue 0
      let line = row + scrollOff
      m, Cmd.ofAsync (fun _dispatch -> async {
        try
          do! DaemonClient.dispatch model.HttpClient model.BaseUrl (EditorAction.SetCursorPosition (line, col)) |> Async.AwaitTask
        with _ -> ()
      })
    | PaneId.Sessions ->
      let scrollOff = model.ScrollOffsets |> Map.tryFind PaneId.Sessions |> Option.defaultValue 0
      let sessionIdx = row + scrollOff
      m, Cmd.ofAsync (fun _dispatch -> async {
        try
          do! DaemonClient.dispatch model.HttpClient model.BaseUrl (EditorAction.SessionSetIndex sessionIdx) |> Async.AwaitTask
        with _ -> ()
      })
    | _ -> m, Cmd.none

  | MouseScroll up ->
    let cur = model.ScrollOffsets |> Map.tryFind model.FocusedPane |> Option.defaultValue 0
    let newOff = match up with | true -> max 0 (cur - 3) | false -> cur + 3
    { model with ScrollOffsets = model.ScrollOffsets |> Map.add model.FocusedPane newOff }, Cmd.none

  | HttpPost (path, body) ->
    model, httpPostCmd model path body

  | FrameTiming ft ->
    { model with FrameMs = ft.TotalMs }, Cmd.none

// ── View ──

/// Render a content pane — lines of text with scroll support and optional line annotations.
let private renderContentPane
  (theme: ThemeConfig)
  (title: string)
  (region: RenderRegion option)
  (scrollOffset: int)
  (isFocused: bool)
  : Element =
  let borderColor = match isFocused with | true -> hexToColor theme.BorderFocus | false -> hexToColor theme.BorderNormal
  let content =
    match region with
    | None -> El.text "  (empty)" |> El.dim
    | Some r ->
      let lines = r.Content.Split('\n')
      let gutterWidth = GutterRender.gutterWidth r.LineAnnotations
      let annotationLookup = GutterRender.buildLookup r.LineAnnotations
      let visible =
        lines
        |> Array.skip (min scrollOffset lines.Length)
        |> Array.toList
      El.column [
        for i, line in visible |> List.mapi (fun i l -> i, l) do
          let lineIdx = i + scrollOffset
          El.row [
            // Gutter annotation
            match gutterWidth > 0 with
            | true ->
              match annotationLookup |> Map.tryFind lineIdx with
              | Some ann ->
                let icon =
                  match ann.Icon with
                  | Features.LiveTesting.GutterIcon.TestPassed -> "✓"
                  | Features.LiveTesting.GutterIcon.TestFailed -> "✗"
                  | Features.LiveTesting.GutterIcon.TestRunning -> "◌"
                  | Features.LiveTesting.GutterIcon.TestDiscovered -> "○"
                  | Features.LiveTesting.GutterIcon.TestSkipped -> "⊘"
                  | Features.LiveTesting.GutterIcon.TestFlaky -> "~"
                  | Features.LiveTesting.GutterIcon.Covered -> "│"
                  | Features.LiveTesting.GutterIcon.NotCovered -> "·"
                  | Features.LiveTesting.GutterIcon.CellStale -> "?"
                El.text (sprintf "%s " icon) |> El.fg (hexToColor (
                  match ann.Icon with
                  | Features.LiveTesting.GutterIcon.TestPassed -> theme.ColorPass
                  | Features.LiveTesting.GutterIcon.TestFailed -> theme.ColorFail
                  | Features.LiveTesting.GutterIcon.TestRunning -> theme.ColorInfo
                  | _ -> theme.FgDim))
              | None ->
                El.text "  "
            | false -> ()
            El.text line |> El.fg (hexToColor theme.FgDefault)
          ]
      ]
  let titleSuffix = match isFocused with | true -> " ●" | false -> ""
  content
  |> El.fill
  |> El.borderedWithTitle (sprintf "%s%s" title titleSuffix) Light
  |> El.fg borderColor

/// Render the status bar.
let private renderStatusBar (model: Model) : Element =
  let sid = match model.SessionId.Length > 8 with | true -> model.SessionId.[..7] | false -> model.SessionId
  let standby = match model.StandbyLabel.Length > 0 with | true -> sprintf " │ %s" model.StandbyLabel | false -> ""
  let liveTesting = match model.LiveTestingStatus.Length > 0 with | true -> sprintf " │ %s" model.LiveTestingStatus | false -> ""
  let ttStatus = TimeTravel.formatStatus model.TimeTravel
  let ttPart = match ttStatus with | Some s -> sprintf " │ %s" s | None -> ""
  let failNav = match model.FailingNav.Hint.Length > 0 with | true -> sprintf " │ %s" model.FailingNav.Hint | false -> ""
  let faultHint =
    match faultedRecoveryHint model.SessionState with
    | Some hint -> sprintf " │ %s" hint
    | None -> ""
  let leftStatus =
    match model.EvalCount > 0 with
    | true ->
      sprintf " %s %s │ evals: %d (avg %.0fms)%s%s%s%s%s │ %s"
        sid model.SessionState model.EvalCount model.AvgMs standby liveTesting ttPart failNav faultHint (PaneId.displayName model.FocusedPane)
    | false ->
      sprintf " %s %s │ evals: %d%s%s%s%s%s │ %s"
        sid model.SessionState model.EvalCount standby liveTesting ttPart failNav faultHint (PaneId.displayName model.FocusedPane)
  let rightStatus =
    sprintf " %s │ %.1fms │%s"
      model.ThemeName model.FrameMs (StatusHints.build model.KeyMap model.FocusedPane model.Layout.VisiblePanes model.WatchedCount model.Density)
  let statusBg = hexToColor model.Theme.BgStatus
  El.row [
    El.text leftStatus |> El.fg (hexToColor model.Theme.FgDefault)
    El.text "" |> El.fill
    El.text rightStatus |> El.fg (hexToColor model.Theme.FgDim)
  ] |> El.bg statusBg

/// Find a region by its ID.
let private findRegion (id: string) (regions: RenderRegion list) : RenderRegion option =
  regions |> List.tryFind (fun r -> r.Id = id)

/// The main view function — renders the entire TUI.
let view (model: Model) : Element =
  // When viewing history, use historical regions; otherwise live
  let displayRegions =
    match TimeTravel.isLive model.TimeTravel with
    | true -> model.Regions
    | false ->
      match TimeTravel.currentModel model.TimeTravel with
      | Some regions -> regions
      | None -> model.Regions

  let scrollFor pane = model.ScrollOffsets |> Map.tryFind pane |> Option.defaultValue 0
  let isFocused pane = pane = model.FocusedPane

  // Title bar
  let titleBar =
    let connIcon = match model.Connected with | true -> "●" | false -> "○"
    let connColor = match model.Connected with | true -> hexToColor model.Theme.ColorPass | false -> hexToColor model.Theme.ColorFail
    El.row [
      El.text " >> SageFs " |> El.fg (hexToColor model.Theme.FgYellow) |> El.bold
      El.text connIcon |> El.fg connColor
      El.text "" |> El.fill
      El.text " q:quit Tab:focus ^↑↓:scroll " |> El.fg (hexToColor model.Theme.FgDim)
    ] |> El.bg (hexToColor model.Theme.BgStatus)

  // Build pane elements
  let outputPane =
    match model.Layout.VisiblePanes.Contains PaneId.Output with
    | true ->
      let contentPane = renderContentPane model.Theme "Output" (findRegion "output" displayRegions) (scrollFor PaneId.Output) (isFocused PaneId.Output)
      match shouldShowEvangelicalHint model.SessionState model.EvalCount with
      | true ->
        El.column [
          contentPane |> El.fill
          El.row [
            El.text " ** Ready! Try: " |> El.fg (hexToColor model.Theme.FgYellow)
            El.text "[1..10] |> List.sum;;" |> El.fg (hexToColor model.Theme.FgDefault) |> El.bold
            El.text "  (submit code to dismiss)" |> El.fg (hexToColor model.Theme.FgDim)
          ] |> El.bg (hexToColor model.Theme.BgStatus)
        ]
      | false -> contentPane
    | false -> El.empty

  let editorPane =
    match model.Layout.VisiblePanes.Contains PaneId.Editor with
    | true -> renderContentPane model.Theme "Editor" (findRegion "editor" displayRegions) (scrollFor PaneId.Editor) (isFocused PaneId.Editor)
    | false -> El.empty

  let sessionsPane =
    match model.Layout.VisiblePanes.Contains PaneId.Sessions with
    | true -> renderContentPane model.Theme "Sessions" (findRegion "sessions" displayRegions) (scrollFor PaneId.Sessions) (isFocused PaneId.Sessions)
    | false -> El.empty

  let diagnosticsPane =
    match model.Layout.VisiblePanes.Contains PaneId.Diagnostics with
    | true -> renderContentPane model.Theme "Diagnostics" (findRegion "diagnostics" displayRegions) (scrollFor PaneId.Diagnostics) (isFocused PaneId.Diagnostics)
    | false -> El.empty

  let testsPane =
    match model.Layout.VisiblePanes.Contains PaneId.Tests with
    | true -> renderContentPane model.Theme "Tests" (findRegion "tests" displayRegions) (scrollFor PaneId.Tests) (isFocused PaneId.Tests)
    | false -> El.empty

  let contextPane =
    match model.Layout.VisiblePanes.Contains PaneId.Context with
    | true -> renderContentPane model.Theme "Context" (findRegion "context" displayRegions) (scrollFor PaneId.Context) (isFocused PaneId.Context)
    | false -> El.empty

  // Layout: left column (output + editor) | right column (sessions + diagnostics + tests + context)
  let leftCol =
    El.column [
      outputPane |> El.fill
      editorPane |> El.fill
    ] |> El.percentage (int (model.Layout.LeftRightSplit * 100.0))

  let rightCol =
    El.column [
      sessionsPane |> El.fill
      diagnosticsPane |> El.fill
      testsPane |> El.fill
      contextPane |> El.fill
    ] |> El.fill

  El.column [
    titleBar |> El.height 1
    El.row [
      leftCol
      rightCol
    ] |> El.fill
    renderStatusBar model |> El.height 1
  ]

// ── Subscriptions ──

/// Map a SageFs UiAction to our Msg type.
let private uiActionToMsg (action: UiAction) : Msg option =
  match action with
  | UiAction.Quit -> Some Quit
  | UiAction.Redraw -> Some Redraw
  | UiAction.CycleFocus -> Some CycleFocus
  | UiAction.FocusDir dir -> Some (FocusDir dir)
  | UiAction.ScrollUp -> Some ScrollUp
  | UiAction.ScrollDown -> Some ScrollDown
  | UiAction.TogglePane pid -> Some (TogglePane pid)
  | UiAction.LayoutPreset name -> Some (SetLayout name)
  | UiAction.ResizeH d -> Some (ResizeH d)
  | UiAction.ResizeV d -> Some (ResizeV d)
  | UiAction.ResizeR d -> Some (ResizeR d)
  | UiAction.CycleTheme -> Some CycleTheme
  | UiAction.CycleDensity -> Some CycleDensity
  | UiAction.TimeTravelBack -> Some TimeTravelBack
  | UiAction.TimeTravelForward -> Some TimeTravelForward
  | UiAction.TimeTravelGoLive -> Some TimeTravelGoLive
  | UiAction.NextFailingTest -> Some NextFailingTest
  | UiAction.PrevFailingTest -> Some PrevFailingTest
  | UiAction.JumpToTest -> Some JumpToTest
  | UiAction.MarkAllStale -> Some MarkAllStale
  | UiAction.HotReloadWatchAll -> Some HotReloadWatchAll
  | UiAction.HotReloadUnwatchAll -> Some HotReloadUnwatchAll
  | UiAction.EnableLiveTesting -> Some EnableLiveTesting
  | UiAction.DisableLiveTesting -> Some DisableLiveTesting
  | UiAction.CycleRunPolicy -> Some CycleRunPolicy
  | UiAction.ToggleCoverage -> Some ToggleCoverage
  | UiAction.Editor action -> Some (DispatchAction action)
  | UiAction.FontSizeUp -> None // TUI can't change font size
  | UiAction.FontSizeDown -> None

/// Convert a ConsoleKey to a SageTUI Key.
let private consoleKeyToSageTuiKey (ck: ConsoleKey) : Key option =
  match ck with
  | ConsoleKey.Enter -> Some Key.Enter
  | ConsoleKey.Escape -> Some Key.Escape
  | ConsoleKey.Backspace -> Some Key.Backspace
  | ConsoleKey.Tab -> Some Key.Tab
  | ConsoleKey.UpArrow -> Some Key.Up
  | ConsoleKey.DownArrow -> Some Key.Down
  | ConsoleKey.LeftArrow -> Some Key.Left
  | ConsoleKey.RightArrow -> Some Key.Right
  | ConsoleKey.Home -> Some Key.Home
  | ConsoleKey.End -> Some Key.End
  | ConsoleKey.PageUp -> Some Key.PageUp
  | ConsoleKey.PageDown -> Some Key.PageDown
  | ConsoleKey.Insert -> Some Key.Insert
  | ConsoleKey.Delete -> Some Key.Delete
  | ConsoleKey.F1 -> Some (Key.F 1)
  | ConsoleKey.F2 -> Some (Key.F 2)
  | ConsoleKey.F3 -> Some (Key.F 3)
  | ConsoleKey.F4 -> Some (Key.F 4)
  | ConsoleKey.F5 -> Some (Key.F 5)
  | ConsoleKey.F6 -> Some (Key.F 6)
  | ConsoleKey.F7 -> Some (Key.F 7)
  | ConsoleKey.F8 -> Some (Key.F 8)
  | ConsoleKey.F9 -> Some (Key.F 9)
  | ConsoleKey.F10 -> Some (Key.F 10)
  | ConsoleKey.F11 -> Some (Key.F 11)
  | ConsoleKey.F12 -> Some (Key.F 12)
  | _ -> None

/// Convert ConsoleModifiers to SageTUI Modifiers.
let private consoleModsToSageTuiMods (cm: ConsoleModifiers) : Modifiers =
  let mutable m = Modifiers.None
  match cm.HasFlag(ConsoleModifiers.Shift) with | true -> m <- m ||| Modifiers.Shift | false -> ()
  match cm.HasFlag(ConsoleModifiers.Alt) with | true -> m <- m ||| Modifiers.Alt | false -> ()
  match cm.HasFlag(ConsoleModifiers.Control) with | true -> m <- m ||| Modifiers.Ctrl | false -> ()
  m

let private keyBindings (keyMap: SageFsKeyMap) : Sub<Msg> =
  // Build SageTUI key bindings from the SageFs KeyMap (KeyCombo -> UiAction)
  let bindings =
    keyMap
    |> Map.toList
    |> List.choose (fun (kc, uiAction) ->
      // Try to get a SageTUI Key from the ConsoleKey
      let sageTuiKey =
        match consoleKeyToSageTuiKey kc.Key with
        | Some k -> Some k
        | None ->
          // For letter keys, try the char
          match kc.Char with
          | Some ch when ch >= ' ' && ch <= '~' -> Some (Key.Char (Text.Rune ch))
          | _ -> None
      match sageTuiKey with
      | Some k ->
        match uiActionToMsg uiAction with
        | Some msg -> Some ((k, consoleModsToSageTuiMods kc.Modifiers), msg)
        | None -> None
      | None -> None)
  Keys.bindWithMods bindings

/// Fallback subscription for printable characters not captured by keymap bindings.
/// Also handles Ctrl+R (DC2 = '\x12') as a hard-reset shortcut.
let private charFallback : Sub<Msg> =
  KeySub (fun (key, _mods) ->
    match key with
    | Key.Char r ->
      match r.IsBmp with
      | true ->
        let ch = char (r.Value)
        match ch with
        | '\x12' -> Some (DispatchAction EditorAction.HardResetSession) // Ctrl+R
        | c when c >= ' ' -> Some (InsertChar c)
        | _ -> None
      | false -> None
    | _ -> None)

let private sseSub (baseUrl: string) : Sub<Msg> =
  Sub.CustomSub("sse-daemon", fun dispatch ct -> async {
    let onState (event: StateEvent) (regions: RenderRegion list) =
      dispatch SseConnected
      dispatch (SseStateReceived (event, regions))
    let onReconnecting (msg: string) =
      dispatch (SseReconnecting msg)
    do! DaemonClient.runSseListener baseUrl onState onReconnecting ct |> Async.AwaitTask
  })

let private mouseSub : Sub<Msg> =
  MouseSub (fun me ->
    match me.Phase with
    | Pressed ->
      match me.Button with
      | MouseButton.ScrollUp -> Some (MouseScroll true)
      | MouseButton.ScrollDown -> Some (MouseScroll false)
      | MouseButton.LeftButton -> Some (ClickPane (PaneId.Output, me.Y, me.X))
      | _ -> None
    | _ -> None)

let subscribe (keyMap: SageFsKeyMap) (baseUrl: string) (_model: Model) : Sub<Msg> list =
  [ keyBindings keyMap
    charFallback
    sseSub baseUrl
    mouseSub
    Sub.frameTimings FrameTiming ]

// ── Entry point ──

/// Run the SageTUI-based TUI client.
let run (daemonInfo: DaemonInfo) =
  // Load keybindings from config
  let keyMap : SageFsKeyMap =
    let cwd = IO.Directory.GetCurrentDirectory()
    match DirectoryConfig.load cwd with
    | Some cfg when not cfg.Keybindings.IsEmpty ->
      SageFs.KeyMap.merge cfg.Keybindings SageFs.KeyMap.defaults
    | _ -> SageFs.KeyMap.defaults

  let dashboardPort = daemonInfo.DashboardPort
  let baseUrl = sprintf "http://localhost:%d" dashboardPort

  // Verify daemon is reachable first
  let mutable connError = None
  try
    use handler = new HttpClientHandler(AutomaticDecompression = Net.DecompressionMethods.All)
    use client = new HttpClient(handler)
    let resp = client.GetAsync(sprintf "%s/dashboard" baseUrl).GetAwaiter().GetResult()
    resp.EnsureSuccessStatusCode() |> ignore
  with ex ->
    connError <- Some (sprintf "Cannot connect to SageFs daemon at %s\n  %s\n\nIs the daemon running? Start it with:\n  sagefs --proj <project.fsproj>" baseUrl ex.Message)

  match connError with
  | Some msg ->
    eprintfn "%s" msg
    1
  | None ->
    let program : Program<Model, Msg> = {
      Init = init daemonInfo keyMap
      Update = update
      View = view
      Subscribe = subscribe keyMap baseUrl
      OnError = CrashOnError
    }
    App.run (program |> Program.withDebugger DebuggerConfig.defaults)
    0
