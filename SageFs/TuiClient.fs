module SageFs.Server.TuiClient

open System
open System.Net.Http
open System.Threading
open SageFs

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
  let mutable focusedPane = PaneId.Editor
  let mutable scrollOffsets = Map.empty<PaneId, int>
  let mutable prevFrame = ""
  let mutable lastRegions : RenderRegion list = []
  let mutable lastSessionState = "Connecting..."
  let mutable lastSessionId = ""
  let mutable lastEvalCount = 0
  let mutable lastAvgMs = 0.0
  let mutable layoutConfig = LayoutConfig.defaults
  let mutable currentTheme = Theme.defaults
  let mutable currentThemeName = "One Dark"

  // Load keybindings from config, merge with defaults
  let keyMap =
    let cwd = IO.Directory.GetCurrentDirectory()
    match DirectoryConfig.load cwd with
    | Some cfg when not cfg.Keybindings.IsEmpty ->
      KeyMap.merge cfg.Keybindings KeyMap.defaults
    | _ -> KeyMap.defaults

  // Set up raw terminal mode
  TerminalMode.setupRawMode ()

  let mutable lastFrameMs = 0.0

  let render () =
    lock TerminalUIState.consoleLock (fun () ->
      try
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let statusLeft =
          let sid = if lastSessionId.Length > 8 then lastSessionId.[..7] else lastSessionId
          if lastEvalCount > 0 then
            sprintf " %s %s | evals: %d (avg %.0fms) | %s" sid lastSessionState lastEvalCount lastAvgMs (PaneId.displayName focusedPane)
          else
            sprintf " %s %s | evals: %d | %s" sid lastSessionState lastEvalCount (PaneId.displayName focusedPane)
        let statusRight = sprintf " %s | %.1fms |%s" currentThemeName lastFrameMs (StatusHints.build keyMap focusedPane)
        let cursorPos = Screen.drawWith layoutConfig currentTheme grid lastRegions focusedPane scrollOffsets statusLeft statusRight
        let cursorRow, cursorCol =
          match cursorPos with
          | Some (r, c) -> r, c
          | None -> 0, 0
        let frame = AnsiEmitter.emit grid cursorRow cursorCol
        let output =
          if prevFrame.Length = 0 then frame
          else
            let d = FrameDiff.diff prevFrame frame
            if d.Length = 0 then ""
            else d
        if output.Length > 0 then
          Console.Write(output)
        prevFrame <- frame
        sw.Stop()
        lastFrameMs <- sw.Elapsed.TotalMilliseconds
      with _ -> ())

  // Initial render
  render ()

  // Start SSE listener in background using shared DaemonClient
  let sseTask =
    DaemonClient.runSseListener
      baseUrl
      (fun sessionId sessionState evalCount avgMs regions ->
        lastSessionId <- sessionId
        lastSessionState <- sessionState
        lastEvalCount <- evalCount
        lastAvgMs <- avgMs
        lastRegions <- regions
        render ())
      (fun _ ->
        lastSessionState <- sprintf "%s (reconnecting...)" lastSessionState
        render ())
      cts.Token

  let mutable exitCode = 0
  try
    // Key reading loop
    while not cts.Token.IsCancellationRequested do
      // Check for terminal resize
      let newRows = Console.WindowHeight
      let newCols = Console.WindowWidth
      if newRows <> gridRows || newCols <> gridCols then
        gridRows <- newRows
        gridCols <- newCols
        grid <- CellGrid.create gridRows gridCols
        prevFrame <- ""
        lock TerminalUIState.consoleLock (fun () ->
          Console.Write(AnsiCodes.clearScreen))
        render ()

      if Console.KeyAvailable then
        let keyInfo = Console.ReadKey(true)
        match TerminalInput.mapKeyWith keyMap keyInfo with
        | Some TerminalCommand.Quit ->
          cts.Cancel()
        | Some TerminalCommand.Redraw ->
          prevFrame <- ""
          lock TerminalUIState.consoleLock (fun () ->
            Console.Write(AnsiCodes.clearScreen))
          render ()
        | Some TerminalCommand.CycleFocus ->
          focusedPane <- PaneId.next focusedPane
          render ()
        | Some (TerminalCommand.FocusDirection dir) ->
          let paneRects = Screen.computeLayoutWith layoutConfig gridRows gridCols |> fst
          focusedPane <- PaneId.navigate dir focusedPane paneRects
          render ()
        | Some TerminalCommand.ScrollUp ->
          let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
          scrollOffsets <- scrollOffsets |> Map.add focusedPane (max 0 (cur - 3))
          render ()
        | Some TerminalCommand.ScrollDown ->
          let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
          scrollOffsets <- scrollOffsets |> Map.add focusedPane (cur + 3)
          render ()
        | Some (TerminalCommand.TogglePane paneName) ->
          match PaneId.tryParse paneName with
          | Some pid ->
            layoutConfig <- LayoutConfig.togglePane pid layoutConfig
            if not (layoutConfig.VisiblePanes.Contains focusedPane) then
              focusedPane <- PaneId.Editor
            render ()
          | None -> ()
        | Some (TerminalCommand.LayoutPreset presetName) ->
          layoutConfig <-
            match presetName with
            | "focus" -> LayoutConfig.focus
            | "minimal" -> LayoutConfig.minimal
            | _ -> LayoutConfig.defaults
          if not (layoutConfig.VisiblePanes.Contains focusedPane) then
            focusedPane <- PaneId.Editor
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
          render ()
        | Some (TerminalCommand.Action action) ->
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
          do! DaemonClient.dispatch client baseUrl remappedAction
        | None -> ()
      else
        try
          do! Threading.Tasks.Task.Delay(16, cts.Token)
        with :? OperationCanceledException -> ()
  finally
    TerminalMode.restoreConsole ()
    try cts.Cancel() with _ -> ()

  return exitCode
}
