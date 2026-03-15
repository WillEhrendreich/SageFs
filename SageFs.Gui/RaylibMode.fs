namespace SageFs.Gui

#nowarn "3391"

open Raylib_cs
open SageFs
open System
open System.Net.Http
open System.Threading

/// Raylib window loop — immediate-mode GUI rendering of CellGrid.
/// Connects to running SageFs daemon via same protocol as TUI client.
module RaylibMode =
  let defaultFontSize = 16
  let minFontSize = 8
  let maxFontSize = 48

  /// Try loading a font from well-known paths, fallback to default
  let loadFont (size: int) =
    let candidates = [
      @"C:\Windows\Fonts\JetBrainsMonoNerdFontMono-Regular.ttf"
      @"C:\Windows\Fonts\JetBrainsMonoNerdFont-Regular.ttf"
      @"C:\Windows\Fonts\JetBrainsMono-Regular.ttf"
      @"C:\Windows\Fonts\CascadiaCode.ttf"
      @"C:\Windows\Fonts\consola.ttf"
    ]
    let path =
      candidates |> List.tryFind System.IO.File.Exists
    match path with
    | Some p ->
      // Load ASCII + Latin-1 Supplement + Box Drawing + Block Elements + Arrows + Misc Symbols
      let codepoints = ResizeArray<int>()
      for cp in 0x0020 .. 0x00FF do codepoints.Add(cp) // Basic Latin + Latin-1 Supplement
      for cp in 0x2500 .. 0x257F do codepoints.Add(cp) // Box Drawing
      for cp in 0x2580 .. 0x259F do codepoints.Add(cp) // Block Elements (▀▄█░▒▓)
      for cp in 0x25A0 .. 0x25FF do codepoints.Add(cp) // Geometric Shapes (▲▼◆●)
      for cp in 0x2190 .. 0x21FF do codepoints.Add(cp) // Arrows (←→↑↓)
      for cp in 0x2700 .. 0x27BF do codepoints.Add(cp) // Dingbats
      let arr = codepoints.ToArray()
      let f = Raylib.LoadFontEx(p, size, arr, arr.Length)
      if CBool.op_Implicit(Raylib.IsFontValid(f)) then f
      else Raylib.GetFontDefault()
    | None -> Raylib.GetFontDefault()

  /// Map Raylib key input to EditorAction (mirrors TerminalInput.mapKey)
  type GuiCommand =
    | Quit
    | CycleFocus
    | FocusDir of Direction
    | ScrollUp
    | ScrollDown
    | Redraw
    | FontSizeUp
    | FontSizeDown
    | Action of EditorAction
    | TogglePane of PaneId
    | LayoutPreset of string
    | ResizeH of int
    | ResizeV of int
    | ResizeR of int
    | CycleTheme
    | CopySelection
    | HotReloadWatchAll
    | HotReloadUnwatchAll
    | EnableLiveTesting
    | DisableLiveTesting
    | CycleRunPolicy
    | ToggleCoverage
    | TimeTravelBack
    | TimeTravelForward
    | TimeTravelGoLive
    | CycleDensity
    | NextFailingTest
    | PrevFailingTest
    | JumpToTest
    | MarkAllStale

  /// Convert Raylib KeyboardKey to System.ConsoleKey for KeyMap lookup
  let raylibToConsoleKey (key: KeyboardKey) : System.ConsoleKey option =
    match key with
    | KeyboardKey.Enter -> Some System.ConsoleKey.Enter
    | KeyboardKey.Tab -> Some System.ConsoleKey.Tab
    | KeyboardKey.Escape -> Some System.ConsoleKey.Escape
    | KeyboardKey.Space -> Some System.ConsoleKey.Spacebar
    | KeyboardKey.Backspace -> Some System.ConsoleKey.Backspace
    | KeyboardKey.Delete -> Some System.ConsoleKey.Delete
    | KeyboardKey.Up -> Some System.ConsoleKey.UpArrow
    | KeyboardKey.Down -> Some System.ConsoleKey.DownArrow
    | KeyboardKey.Left -> Some System.ConsoleKey.LeftArrow
    | KeyboardKey.Right -> Some System.ConsoleKey.RightArrow
    | KeyboardKey.Home -> Some System.ConsoleKey.Home
    | KeyboardKey.End -> Some System.ConsoleKey.End
    | KeyboardKey.PageUp -> Some System.ConsoleKey.PageUp
    | KeyboardKey.PageDown -> Some System.ConsoleKey.PageDown
    | KeyboardKey.F12 -> Some System.ConsoleKey.F12
    | KeyboardKey.Equal -> Some System.ConsoleKey.OemPlus
    | KeyboardKey.Minus -> Some System.ConsoleKey.OemMinus
    | k when k >= KeyboardKey.A && k <= KeyboardKey.Z ->
      Some (enum<System.ConsoleKey> (int System.ConsoleKey.A + int k - int KeyboardKey.A))
    | k when k >= KeyboardKey.Zero && k <= KeyboardKey.Nine ->
      Some (enum<System.ConsoleKey> (int System.ConsoleKey.D0 + int k - int KeyboardKey.Zero))
    | _ -> None

  let mapKeyWith (keyMap: KeyMap) () : GuiCommand option =
    let c = ctrl ()
    let a = alt ()
    let s = shift ()

    let key = keyPressed ()
    if key = KeyboardKey.Null then None
    else
      match raylibToConsoleKey key with
      | None -> None
      | Some ck ->
        let mods =
          (if c then System.ConsoleModifiers.Control else enum 0) |||
          (if a then System.ConsoleModifiers.Alt else enum 0) |||
          (if s then System.ConsoleModifiers.Shift else enum 0)
        let combo : KeyCombo = { Key = ck; Modifiers = mods; Char = None }
        match keyMap |> Map.tryFind combo with
        | Some (UiAction.Quit) -> Some Quit
        | Some (UiAction.CycleFocus) -> Some CycleFocus
        | Some (UiAction.FocusDir d) -> Some (FocusDir d)
        | Some (UiAction.ScrollUp) -> Some ScrollUp
        | Some (UiAction.ScrollDown) -> Some ScrollDown
        | Some (UiAction.Redraw) -> Some Redraw
        | Some (UiAction.FontSizeUp) -> Some FontSizeUp
        | Some (UiAction.FontSizeDown) -> Some FontSizeDown
        | Some (UiAction.TogglePane p) -> Some (TogglePane p)
        | Some (UiAction.LayoutPreset p) -> Some (LayoutPreset p)
        | Some (UiAction.ResizeH d) -> Some (ResizeH d)
        | Some (UiAction.ResizeV d) -> Some (ResizeV d)
        | Some (UiAction.ResizeR d) -> Some (ResizeR d)
        | Some (UiAction.CycleTheme) -> Some CycleTheme
        | Some (UiAction.HotReloadWatchAll) -> Some HotReloadWatchAll
        | Some (UiAction.HotReloadUnwatchAll) -> Some HotReloadUnwatchAll
        | Some (UiAction.EnableLiveTesting) -> Some EnableLiveTesting
        | Some (UiAction.DisableLiveTesting) -> Some DisableLiveTesting
        | Some (UiAction.CycleRunPolicy) -> Some CycleRunPolicy
        | Some (UiAction.ToggleCoverage) -> Some ToggleCoverage
        | Some (UiAction.TimeTravelBack) -> Some TimeTravelBack
        | Some (UiAction.TimeTravelForward) -> Some TimeTravelForward
        | Some (UiAction.TimeTravelGoLive) -> Some TimeTravelGoLive
        | Some (UiAction.CycleDensity) -> Some CycleDensity
        | Some (UiAction.NextFailingTest) -> Some NextFailingTest
        | Some (UiAction.PrevFailingTest) -> Some PrevFailingTest
        | Some (UiAction.JumpToTest) -> Some JumpToTest
        | Some (UiAction.MarkAllStale) -> Some MarkAllStale
        | Some (UiAction.Editor action) -> Some (Action action)
        | None ->
          // Ctrl+C not in keymap → copy selection
          if ck = System.ConsoleKey.C && c && not a && not s then Some CopySelection
          else None

  /// Get typed characters (for InsertChar) — separate from key presses
  let getCharInput () : EditorAction option =
    let ch = charPressed ()
    if ch > 0 then Some (EditorAction.InsertChar (char ch))
    else None

  /// Compute pane layout rects for the given grid dimensions.
  /// Render regions into the CellGrid using shared Screen module
  let renderRegions
    (grid: CellGrid)
    (regions: RenderRegion list)
    (sessionId: string)
    (sessionState: string)
    (evalCount: int)
    (standbyLabel: string)
    (liveTestingStatus: string)
    (workflowLabel: string)
    (watchedCount: int)
    (timeTravelStatus: string option)
    (focusedPane: PaneId)
    (scrollOffsets: Map<PaneId, int>)
    (fontSize: int)
    (currentFps: int)
    (keyMap: KeyMap)
    (layoutConfig: LayoutConfig)
    (theme: ThemeConfig)
    (themeName: string)
    (density: UiDensity) =

    let statusLeft =
      let sid = match sessionId.Length > 8 with | true -> sessionId.[..7] | false -> sessionId
      let workflow = sprintf "[%s]" workflowLabel
      let standby = match standbyLabel.Length > 0 with | true -> sprintf " | %s" standbyLabel | false -> ""
      let liveTesting = match liveTestingStatus.Length > 0 with | true -> sprintf " | %s" liveTestingStatus | false -> ""
      let ttPart = match timeTravelStatus with | Some s -> sprintf " | %s" s | None -> ""
      sprintf " %s %s %s | evals: %d%s%s%s | %s" sid sessionState workflow evalCount standby liveTesting ttPart (PaneId.displayName focusedPane)
    let statusRight = sprintf " %s | %dpt | %d fps |%s" themeName fontSize currentFps (StatusHints.build keyMap focusedPane layoutConfig.VisiblePanes watchedCount density)
    Screen.drawWith layoutConfig theme grid regions focusedPane scrollOffsets statusLeft statusRight |> ignore

  /// Run the Raylib GUI window connected to daemon.
  let run () =
    // Load keybindings from config, merge with defaults
    let keyMap =
      let cwd = System.IO.Directory.GetCurrentDirectory()
      match DirectoryConfig.load cwd with
      | Some cfg when not cfg.Keybindings.IsEmpty ->
        KeyMap.merge cfg.Keybindings KeyMap.defaults
      | _ -> KeyMap.defaults
    let mapKey = mapKeyWith keyMap

    // Discover daemon
    let daemonInfo =
      match DaemonState.read () with
      | None ->
        eprintfn "No SageFs daemon running. Start one with: sagefs"
        None
      | Some info -> Some info

    match daemonInfo with
    | None -> ()
    | Some daemonInfo ->

    let dashboardPort = daemonInfo.DashboardPort
    let baseUrl = sprintf "http://localhost:%d" dashboardPort

    // Verify connection before opening window
    use handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
    use client = new HttpClient(handler)
    client.Timeout <- TimeSpan.FromHours(24.0)
    let connected =
      try
        let resp = client.GetAsync(sprintf "%s/dashboard" baseUrl).Result
        resp.EnsureSuccessStatusCode() |> ignore
        true
      with ex ->
        eprintfn "Cannot connect to SageFs daemon at %s: %s" baseUrl ex.Message
        false

    if not connected then () else

    // Mutable state (updated from SSE thread)
    let mutable lastRegions : RenderRegion list = []
    let mutable lastSessionState = "Connecting..."
    let mutable lastSessionId = ""
    let mutable lastWorkingDir = ""
    let mutable lastEvalCount = 0
    let mutable lastStandbyLabel = ""
    let mutable lastLiveTestingStatus = ""
    let mutable lastWorkflowLabel = "REPL"
    let mutable lastWatchedCount = 0
    let mutable lastDensity = UiDensity.Normal
    let mutable lastFps = 0
    // Eval watchdog: tracks whether the daemon was evaluating when SSE disconnected.
    let mutable evalInterruptedOnDisconnect = false
    // Time-travel: buffer last N region snapshots for keyboard navigation
    let mutable timeTravelState =
      TimeTravel.create { ModelSnapshot.Capacity = 200; ModelSnapshot.Enabled = true }
    let mutable focusedPane = PaneId.Output
    let mutable scrollOffsets = Map.empty<PaneId, int>
    let mutable layoutConfig = LayoutConfig.defaults
    let mutable currentTheme =
      match ThemePresets.tryFind "Kanagawa" with
      | Some t -> t
      | None -> Theme.defaults
    let mutable currentThemeName = "Kanagawa"
    let sessionThemes = System.Collections.Generic.Dictionary<string, string>()
    let statelock = obj ()
    let mutable running = true
    // Text selection state (grid coordinates)
    let mutable selStart : (int * int) option = None
    let mutable selEnd : (int * int) option = None
    let mutable selecting = false
    // Failing test navigation
    let mutable failingTestIdx = -1
    let mutable failingNavHint = ""
    // Test source locations for jump-to-source (F12)
    let mutable testSourceLocations = Map.empty<string, string * int>

    // Init window
    let mutable gridCols = 120
    let mutable gridRows = 40
    Raylib.SetConfigFlags(ConfigFlags.ResizableWindow)
    Raylib.InitWindow(gridCols * 10, gridRows * 20, "SageFs GUI")
    Raylib.SetTargetFPS(144)

    let mutable fontSize = defaultFontSize
    let mutable font = loadFont fontSize
    let mutable charSize = Raylib.MeasureTextEx(font, "M", float32 fontSize, 0.0f)
    let mutable cellW = max 1 (int (System.MathF.Ceiling(charSize.X)))
    let mutable cellH = max 1 (int (System.MathF.Ceiling(charSize.Y)) + 2)
    let mutable grid = CellGrid.rent gridRows gridCols

    let reloadFont () =
      Raylib.UnloadFont(font)
      font <- loadFont fontSize
      charSize <- Raylib.MeasureTextEx(font, "M", float32 fontSize, 0.0f)
      cellW <- max 1 (int (System.MathF.Ceiling(charSize.X)))
      cellH <- max 1 (int (System.MathF.Ceiling(charSize.Y)) + 2)

    // Start SSE listener
    use cts = new CancellationTokenSource()
    let _sseTask =
      DaemonClient.runSseListener
        baseUrl
        (fun event regions ->
          lock statelock (fun () ->
            evalInterruptedOnDisconnect <- false
            // Detect session switch by working directory change
            if event.ActiveWorkingDir.Length > 0 && event.ActiveWorkingDir <> lastWorkingDir && lastWorkingDir.Length > 0 then
              sessionThemes.[lastWorkingDir] <- currentThemeName
              match sessionThemes.TryGetValue(event.ActiveWorkingDir) with
              | true, themeName ->
                match ThemePresets.tryFind themeName with
                | Some theme ->
                  currentTheme <- theme
                  currentThemeName <- themeName
                | None -> ()
              | false, _ -> ()
            if event.ActiveWorkingDir.Length > 0 then
              lastWorkingDir <- event.ActiveWorkingDir
            lastSessionId <- event.SessionId
            lastSessionState <- event.SessionState
            lastEvalCount <- event.EvalCount
            lastStandbyLabel <- event.StandbyLabel
            lastLiveTestingStatus <- event.LiveTestingStatus
            lastWorkflowLabel <- event.WorkflowLabel
            lastWatchedCount <- event.WatchedCount
            lastRegions <- regions
            match event.TestSourceLocations.IsEmpty with
            | true -> ()
            | false -> testSourceLocations <- event.TestSourceLocations
            // Record region snapshot for time-travel (only in live mode)
            timeTravelState <-
              TimeTravel.record event.SessionState 0.0<SageFs.Measures.ms> regions timeTravelState))
        (fun _ ->
          lock statelock (fun () ->
            let wasEvaluating =
              lastSessionState.Contains("Evaluating", StringComparison.OrdinalIgnoreCase)
              || lastSessionState.Contains("evaluating", StringComparison.OrdinalIgnoreCase)
            match wasEvaluating with
            | true ->
              evalInterruptedOnDisconnect <- true
              lastSessionState <- "⚠ Eval lost — daemon disconnected (reconnecting...)"
            | false ->
              lastSessionState <- sprintf "%s (reconnecting...)" lastSessionState))
        cts.Token

    let navigateFailingTests (delta: int) =
      let failing =
        lock statelock (fun () ->
          lastRegions
          |> List.toArray
          |> Array.collect (fun r ->
            r.LineAnnotations
            |> Array.filter (fun a -> a.Icon = Features.LiveTesting.GutterIcon.TestFailed)
            |> Array.map (fun a -> a.Line))
          |> Array.sort
          |> Array.distinct)
      match failing.Length with
      | 0 -> failingNavHint <- ""
      | total ->
        let newIdx =
          match failingTestIdx < 0 with
          | true -> match delta > 0 with | true -> 0 | false -> total - 1
          | false -> (failingTestIdx + delta + total) % total
        failingTestIdx <- newIdx
        let targetLine = failing.[newIdx]
        failingNavHint <- sprintf "↯%d/%d" (newIdx + 1) total
        let totalLines =
          lock statelock (fun () ->
            lastRegions
            |> List.tryFind (fun r -> r.Id = "editor")
            |> Option.map (fun r -> r.Content.Split('\n').Length)
            |> Option.defaultValue 100)
        let approxHeight = max 1 (gridRows - 4)
        scrollOffsets <- scrollOffsets |> Map.add PaneId.Editor (max 0 (totalLines - approxHeight - targetLine + 3))

    while running && not (windowShouldClose ()) do

      // Handle window resize
      let winW = screenW ()
      let winH = screenH ()
      let newCols = max 40 (winW / cellW)
      let newRows = max 10 (winH / cellH)
      if newCols <> gridCols || newRows <> gridRows then
        gridCols <- newCols
        gridRows <- newRows
        CellGrid.release grid
        grid <- CellGrid.rent gridRows gridCols

      // Handle input — process all pending keys
      let mutable keyCmd = mapKey ()
      while running && keyCmd.IsSome do
        match keyCmd.Value with
        | Quit -> running <- false
        | CycleFocus ->
          focusedPane <- PaneId.nextVisible layoutConfig.VisiblePanes focusedPane
        | FocusDir dir ->
          let paneRects = Screen.computeLayoutWith layoutConfig gridRows gridCols |> fst
          focusedPane <- PaneId.navigate dir focusedPane paneRects
        | ScrollUp ->
          let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
          scrollOffsets <- scrollOffsets |> Map.add focusedPane (cur + 3)
        | ScrollDown ->
          let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
          scrollOffsets <- scrollOffsets |> Map.add focusedPane (max 0 (cur - 3))
        | Redraw -> ()
        | FontSizeUp ->
          fontSize <- min maxFontSize (fontSize + 2)
          reloadFont ()
        | FontSizeDown ->
          fontSize <- max minFontSize (fontSize - 2)
          reloadFont ()
        | TogglePane pid ->
          layoutConfig <- LayoutConfig.togglePane pid layoutConfig
          if not (layoutConfig.VisiblePanes.Contains focusedPane) then
            focusedPane <- PaneId.firstVisible layoutConfig.VisiblePanes
        | LayoutPreset presetName ->
          layoutConfig <-
            match presetName with
            | "focus" -> LayoutConfig.focus
            | "minimal" -> LayoutConfig.minimal
            | _ -> LayoutConfig.defaults
          if not (layoutConfig.VisiblePanes.Contains focusedPane) then
            focusedPane <- PaneId.firstVisible layoutConfig.VisiblePanes
        | ResizeH d ->
          layoutConfig <- LayoutConfig.resizeH d layoutConfig
        | ResizeV d ->
          layoutConfig <- LayoutConfig.resizeV d layoutConfig
        | ResizeR d ->
          layoutConfig <- LayoutConfig.resizeR d layoutConfig
        | CycleTheme ->
          let name, theme = ThemePresets.cycleNext currentTheme
          currentTheme <- theme
          currentThemeName <- name
          if lastWorkingDir.Length > 0 then
            sessionThemes.[lastWorkingDir] <- name
        | CopySelection ->
          match selStart, selEnd with
          | Some (r1, c1), Some (r2, c2) ->
            let text = CellGrid.toTextRange grid r1 c1 r2 c2
            if text.Length > 0 then
              Raylib.SetClipboardText(text)
            selStart <- None
            selEnd <- None
            selecting <- false
          | _ -> ()
        | HotReloadWatchAll ->
          if lastSessionId.Length > 0 then
            client.PostAsync(sprintf "%s/api/sessions/%s/hotreload/watch-all" baseUrl lastSessionId, new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json")) |> ignore
        | HotReloadUnwatchAll ->
          if lastSessionId.Length > 0 then
            client.PostAsync(sprintf "%s/api/sessions/%s/hotreload/unwatch-all" baseUrl lastSessionId, new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json")) |> ignore
        | EnableLiveTesting ->
          DaemonClient.dispatchAction client baseUrl "enableLiveTesting" None |> ignore
        | DisableLiveTesting ->
          DaemonClient.dispatchAction client baseUrl "disableLiveTesting" None |> ignore
        | CycleRunPolicy ->
          DaemonClient.dispatchAction client baseUrl "cycleRunPolicy" None |> ignore
        | ToggleCoverage ->
          DaemonClient.dispatchAction client baseUrl "toggleCoverage" None |> ignore
        | TimeTravelBack ->
          timeTravelState <- TimeTravel.stepBack timeTravelState
        | TimeTravelForward ->
          timeTravelState <- TimeTravel.stepForward timeTravelState
        | TimeTravelGoLive ->
          timeTravelState <- TimeTravel.goLive timeTravelState
        | CycleDensity ->
          lastDensity <- UiDensity.cycle lastDensity
        | NextFailingTest -> navigateFailingTests 1
        | PrevFailingTest -> navigateFailingTests (-1)
        | JumpToTest ->
          let scrollOff = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
          match JumpToTest.getSelectedTestLocation lastRegions focusedPane scrollOff testSourceLocations with
          | Some (file, line) -> JumpToTest.openInEditor file line
          | None -> ()
        | MarkAllStale ->
          client.PostAsync(sprintf "%s/api/live-testing/mark-all-stale" baseUrl, new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json")) |> ignore
        | Action action ->
          // When Sessions pane is focused, remap movement keys to session navigation
          let remappedAction =
            if focusedPane = PaneId.Sessions then
              match action with
              | EditorAction.MoveCursor Direction.Up -> EditorAction.SessionNavUp
              | EditorAction.MoveCursor Direction.Down -> EditorAction.SessionNavDown
              | EditorAction.NewLine -> EditorAction.SessionSelect
              | EditorAction.DeleteBackward -> EditorAction.SessionDelete
              | EditorAction.DeleteForward -> EditorAction.SessionDelete
              | other -> other
            else action
          DaemonClient.dispatch client baseUrl remappedAction |> ignore
        keyCmd <- mapKey ()

      // Handle char input (typed text)
      let mutable charAction = getCharInput ()
      while running && charAction.IsSome do
        match charAction.Value with
        | action ->
          DaemonClient.dispatch client baseUrl action |> ignore
        charAction <- getCharInput ()

      // Handle mouse → text selection (drag) + focus pane + cursor/session click
      if mousePressed Raylib_cs.MouseButton.Left then
        let mp = mousePos ()
        let clickCol = int mp.X / cellW
        let clickRow = int mp.Y / cellH
        // Start text selection
        selStart <- Some (clickRow, clickCol)
        selEnd <- Some (clickRow, clickCol)
        selecting <- true
        // Focus pane + editor cursor / session click
        let paneRects = Screen.computeLayoutWith layoutConfig gridRows gridCols |> fst
        let clicked =
          paneRects
          |> List.tryFind (fun (_, r) ->
            clickRow >= r.Row && clickRow < r.Row + r.Height &&
            clickCol >= r.Col && clickCol < r.Col + r.Width)
        match clicked with
        | Some (id, r) ->
          focusedPane <- id
          if id = PaneId.Editor then
            let contentRow = clickRow - r.Row - 1
            let contentCol = clickCol - r.Col - 1
            if contentRow >= 0 && contentCol >= 0 then
              let scrollOff = scrollOffsets |> Map.tryFind PaneId.Editor |> Option.defaultValue 0
              let line = contentRow + scrollOff
              DaemonClient.dispatch client baseUrl (EditorAction.SetCursorPosition (line, contentCol))
              |> ignore
          elif id = PaneId.Sessions then
            let contentRow = clickRow - r.Row - 1
            let scrollOff = scrollOffsets |> Map.tryFind PaneId.Sessions |> Option.defaultValue 0
            let sessionIdx = contentRow + scrollOff
            if contentRow >= 0 then
              DaemonClient.dispatch client baseUrl (EditorAction.SessionSetIndex sessionIdx)
              |> ignore
        | None -> ()
      elif selecting && mouseDown Raylib_cs.MouseButton.Left then
        // Extend selection while dragging
        let mp = mousePos ()
        let dragCol = max 0 (min (gridCols - 1) (int mp.X / cellW))
        let dragRow = max 0 (min (gridRows - 1) (int mp.Y / cellH))
        selEnd <- Some (dragRow, dragCol)
      elif selecting && mouseReleased Raylib_cs.MouseButton.Left then
        selecting <- false
        // If start == end, it was a click not a drag — clear selection
        match selStart, selEnd with
        | Some s, Some e when s = e ->
          selStart <- None
          selEnd <- None
        | _ -> ()

      if running then
        // Render
        let regions, sessionId, sessionState, evalCount, standbyLabel, liveTestingStatus, workflowLabel, watchedCount =
          lock statelock (fun () -> lastRegions, lastSessionId, lastSessionState, lastEvalCount, lastStandbyLabel, lastLiveTestingStatus, lastWorkflowLabel, lastWatchedCount)

        // When viewing history, use historical regions; otherwise use live
        let displayRegions =
          match TimeTravel.isLive timeTravelState with
          | true -> regions
          | false ->
            match TimeTravel.currentModel timeTravelState with
            | Some r -> r
            | None -> regions
        let ttStatus = TimeTravel.formatStatus timeTravelState
        let liveTestingWithNav =
          match failingNavHint.Length > 0 with
          | true -> sprintf "%s %s" liveTestingStatus failingNavHint
          | false -> liveTestingStatus

        renderRegions grid displayRegions sessionId sessionState evalCount standbyLabel liveTestingWithNav workflowLabel watchedCount ttStatus focusedPane scrollOffsets fontSize lastFps keyMap layoutConfig currentTheme currentThemeName lastDensity
        lastFps <- fps ()

        Raylib.BeginDrawing()
        Raylib.ClearBackground(RaylibPalette.hexToColor currentTheme.BgDefault)
        let sel =
          match selStart, selEnd with
          | Some (r1, c1), Some (r2, c2) -> Some (r1, c1, r2, c2)
          | _ -> None
        RaylibEmitter.emitWithSelection grid font cellW cellH fontSize sel
        Raylib.EndDrawing()

    cts.Cancel()
    if windowReady () then Raylib.CloseWindow()
