module SageFs.Server.TuiClient

open System
open System.Net.Http
open System.Threading
open SageFs

/// Convert an InputEvent (from VT parser) to a TerminalCommand via KeyMap lookup
let private mapInputEvent (keyMap: KeyMap) (ev: InputEvent) : TerminalCommand option =
  match ev with
  | KeyEvent (key, ch, mods) ->
    let ki = ConsoleKeyInfo(ch, key, mods.HasFlag(ConsoleModifiers.Shift), mods.HasFlag(ConsoleModifiers.Alt), mods.HasFlag(ConsoleModifiers.Control))
    TerminalInput.mapKeyWith keyMap ki
  | InputEvent.MouseEvent _ -> None // Mouse handled separately

/// Run the TUI client, connecting to a running daemon.
let run (daemonInfo: DaemonInfo) = task {
  let dashboardPort = daemonInfo.Port + 1
  let baseUrl = sprintf "http://localhost:%d" dashboardPort
  use client = new HttpClient()
  client.Timeout <- TimeSpan.FromHours(24.0)

  // Verify daemon is reachable
  let mutable connError = None
  try
    let! resp = client.GetAsync(sprintf "%s/dashboard" baseUrl)
    resp.EnsureSuccessStatusCode() |> ignore
  with ex ->
    connError <- Some (sprintf "Cannot connect to SageFs daemon at %s\n  %s\n\nIs the daemon running? Start it with:\n  sagefs --proj <project.fsproj>" baseUrl ex.Message)

  match connError with
  | Some msg ->
    eprintfn "%s" msg
    return 1
  | None ->

  use cts = new CancellationTokenSource()

  let rows = Console.WindowHeight
  let cols = Console.WindowWidth
  let mutable gridRows = rows
  let mutable gridCols = cols
  let mutable grid = CellGrid.create gridRows gridCols
  let mutable focusedPane = PaneId.Output // Fix: start on visible pane
  let mutable scrollOffsets = Map.empty<PaneId, int>
  let mutable prevGrid : CellGrid option = None
  let mutable lastRegions : RenderRegion list = []
  let mutable lastSessionState = "Connecting..."
  let mutable lastSessionId = ""
  let mutable lastWorkingDir = ""
  let mutable lastEvalCount = 0
  let mutable lastAvgMs = 0.0
  let mutable lastStandbyLabel = ""
  let mutable layoutConfig = LayoutConfig.defaults
  let mutable currentTheme =
    match ThemePresets.tryFind "Kanagawa" with
    | Some t -> t
    | None -> Theme.defaults
  let mutable currentThemeName = "Kanagawa"
  let sessionThemes = System.Collections.Generic.Dictionary<string, string>()

  // Load keybindings from config, merge with defaults
  let keyMap =
    let cwd = IO.Directory.GetCurrentDirectory()
    match DirectoryConfig.load cwd with
    | Some cfg when not cfg.Keybindings.IsEmpty ->
      KeyMap.merge cfg.Keybindings KeyMap.defaults
    | _ -> KeyMap.defaults

  // Set up raw terminal mode (includes mouse tracking + VT input)
  TerminalMode.setupRawMode ()
  ConsoleInput.RawInput.start ()

  let mutable lastFrameMs = 0.0

  let render () =
    lock TerminalUIState.consoleLock (fun () ->
      try
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let statusLeft =
          let sid = if lastSessionId.Length > 8 then lastSessionId.[..7] else lastSessionId
          let standby = if lastStandbyLabel.Length > 0 then sprintf " | %s" lastStandbyLabel else ""
          if lastEvalCount > 0 then
            sprintf " %s %s | evals: %d (avg %.0fms)%s | %s" sid lastSessionState lastEvalCount lastAvgMs standby (PaneId.displayName focusedPane)
          else
            sprintf " %s %s | evals: %d%s | %s" sid lastSessionState lastEvalCount standby (PaneId.displayName focusedPane)
        let statusRight = sprintf " %s | %.1fms |%s" currentThemeName lastFrameMs (StatusHints.build keyMap focusedPane layoutConfig.VisiblePanes)
        let cursorPos = Screen.drawWith layoutConfig currentTheme grid lastRegions focusedPane scrollOffsets statusLeft statusRight
        let cursorRow, cursorCol =
          match cursorPos with
          | Some (r, c) -> r, c
          | None -> 0, 0
        let output =
          match prevGrid with
          | None -> AnsiEmitter.emit grid cursorRow cursorCol
          | Some prev -> AnsiEmitter.emitDiff prev grid cursorRow cursorCol
        if output.Length > 0 then
          Console.Write(output)
        prevGrid <- Some (CellGrid.clone grid)
        sw.Stop()
        lastFrameMs <- sw.Elapsed.TotalMilliseconds
      with _ -> ())

  // Initial render
  render ()

  // Start SSE listener in background using shared DaemonClient
  let sseTask =
    DaemonClient.runSseListener
      baseUrl
      (fun sessionId sessionState evalCount avgMs activeWorkingDir standbyLabel regions ->
        // Detect session switch by working directory change
        if activeWorkingDir.Length > 0 && activeWorkingDir <> lastWorkingDir && lastWorkingDir.Length > 0 then
          sessionThemes.[lastWorkingDir] <- currentThemeName
          match sessionThemes.TryGetValue(activeWorkingDir) with
          | true, themeName ->
            match ThemePresets.tryFind themeName with
            | Some theme ->
              currentTheme <- theme
              currentThemeName <- themeName
            | None -> ()
          | false, _ -> ()
        if activeWorkingDir.Length > 0 then
          lastWorkingDir <- activeWorkingDir
        lastSessionId <- sessionId
        lastSessionState <- sessionState
        lastEvalCount <- evalCount
        lastAvgMs <- avgMs
        lastStandbyLabel <- standbyLabel
        lastRegions <- regions
        render ())
      (fun _ ->
        lastSessionState <- sprintf "%s (reconnecting...)" lastSessionState
        render ())
      cts.Token

  let mutable exitCode = 0
  try
    // Input event loop
    while not cts.Token.IsCancellationRequested do
      // Check for terminal resize
      let newRows = Console.WindowHeight
      let newCols = Console.WindowWidth
      if newRows <> gridRows || newCols <> gridCols then
        gridRows <- newRows
        gridCols <- newCols
        grid <- CellGrid.create gridRows gridCols
        prevGrid <- None
        lock TerminalUIState.consoleLock (fun () ->
          Console.Write(AnsiCodes.clearScreen))
        render ()

      // Process raw stdin chars through VT parser
      ConsoleInput.RawInput.processChars ()

      // Drain parsed input events
      let mutable hadInput = false
      let mutable ev = ConsoleInput.RawInput.tryRead ()
      while ev.IsSome && not cts.Token.IsCancellationRequested do
        hadInput <- true
        match ev.Value with
        | InputEvent.MouseEvent me ->
          match me.Action with
          | MouseAction.Press when me.Button = MouseButton.Left ->
            // Click-to-focus pane + editor cursor / session click
            let paneRects = Screen.computeLayoutWith layoutConfig gridRows gridCols |> fst
            let clicked =
              paneRects
              |> List.tryFind (fun (_, r) ->
                me.Row >= r.Row && me.Row < r.Row + r.Height &&
                me.Col >= r.Col && me.Col < r.Col + r.Width)
            match clicked with
            | Some (id, r) ->
              focusedPane <- id
              if id = PaneId.Editor then
                let contentRow = me.Row - r.Row - 1
                let contentCol = me.Col - r.Col - 1
                if contentRow >= 0 && contentCol >= 0 then
                  let scrollOff = scrollOffsets |> Map.tryFind PaneId.Editor |> Option.defaultValue 0
                  let line = contentRow + scrollOff
                  do! DaemonClient.dispatch client baseUrl (EditorAction.SetCursorPosition (line, contentCol))
              elif id = PaneId.Sessions then
                let contentRow = me.Row - r.Row - 1
                let scrollOff = scrollOffsets |> Map.tryFind PaneId.Sessions |> Option.defaultValue 0
                let sessionIdx = contentRow + scrollOff
                if contentRow >= 0 then
                  do! DaemonClient.dispatch client baseUrl (EditorAction.SessionSetIndex sessionIdx)
            | None -> ()
            render ()
          | MouseAction.WheelUp ->
            let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            scrollOffsets <- scrollOffsets |> Map.add focusedPane (cur + 3)
            render ()
          | MouseAction.WheelDown ->
            let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            scrollOffsets <- scrollOffsets |> Map.add focusedPane (max 0 (cur - 3))
            render ()
          | _ -> () // Ignore release/move for now

        | KeyEvent (key, ch, mods) ->
          let ki = ConsoleKeyInfo(ch, key, mods.HasFlag(ConsoleModifiers.Shift), mods.HasFlag(ConsoleModifiers.Alt), mods.HasFlag(ConsoleModifiers.Control))
          match TerminalInput.mapKeyWith keyMap ki with
          | Some TerminalCommand.Quit ->
            cts.Cancel()
          | Some TerminalCommand.Redraw ->
            prevGrid <- None
            lock TerminalUIState.consoleLock (fun () ->
              Console.Write(AnsiCodes.clearScreen))
            render ()
          | Some TerminalCommand.CycleFocus ->
            focusedPane <- PaneId.nextVisible layoutConfig.VisiblePanes focusedPane
            render ()
          | Some (TerminalCommand.FocusDirection dir) ->
            let paneRects = Screen.computeLayoutWith layoutConfig gridRows gridCols |> fst
            focusedPane <- PaneId.navigate dir focusedPane paneRects
            render ()
          | Some TerminalCommand.ScrollUp ->
            let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            scrollOffsets <- scrollOffsets |> Map.add focusedPane (cur + 3)
            render ()
          | Some TerminalCommand.ScrollDown ->
            let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            scrollOffsets <- scrollOffsets |> Map.add focusedPane (max 0 (cur - 3))
            render ()
          | Some (TerminalCommand.TogglePane paneName) ->
            match PaneId.tryParse paneName with
            | Some pid ->
              layoutConfig <- LayoutConfig.togglePane pid layoutConfig
              if not (layoutConfig.VisiblePanes.Contains focusedPane) then
                focusedPane <- PaneId.firstVisible layoutConfig.VisiblePanes
              render ()
            | None -> ()
          | Some (TerminalCommand.LayoutPreset presetName) ->
            layoutConfig <-
              match presetName with
              | "focus" -> LayoutConfig.focus
              | "minimal" -> LayoutConfig.minimal
              | _ -> LayoutConfig.defaults
            if not (layoutConfig.VisiblePanes.Contains focusedPane) then
              focusedPane <- PaneId.firstVisible layoutConfig.VisiblePanes
            render ()
          | Some (TerminalCommand.ResizeH d) ->
            layoutConfig <- LayoutConfig.resizeH d layoutConfig
            render ()
          | Some (TerminalCommand.ResizeV d) ->
            layoutConfig <- LayoutConfig.resizeV d layoutConfig
            render ()
          | Some (TerminalCommand.ResizeR d) ->
            layoutConfig <- LayoutConfig.resizeR d layoutConfig
            render ()
          | Some TerminalCommand.CycleTheme ->
            let name, theme = ThemePresets.cycleNext currentTheme
            currentTheme <- theme
            currentThemeName <- name
            if lastWorkingDir.Length > 0 then
              sessionThemes.[lastWorkingDir] <- name
            render ()
          | Some (TerminalCommand.Action action) ->
            // When Sessions pane is focused, remap movement keys
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
            do! DaemonClient.dispatch client baseUrl remappedAction
          | None ->
            // No keymap match — try printable char insertion
            if ch >= ' ' && ch <= '~' then
              do! DaemonClient.dispatch client baseUrl (EditorAction.InsertChar ch)
            elif ch > '\x7f' then
              do! DaemonClient.dispatch client baseUrl (EditorAction.InsertChar ch)

        ev <- ConsoleInput.RawInput.tryRead ()

      if not hadInput then
        try
          do! Threading.Tasks.Task.Delay(8, cts.Token)
        with :? OperationCanceledException -> ()
  finally
    ConsoleInput.RawInput.stop ()
    TerminalMode.restoreConsole ()
    try cts.Cancel() with _ -> ()

  return exitCode
}
