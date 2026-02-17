module SageFs.Server.TerminalMode

open System
open System.Threading
open SageFs


/// Set up the console for raw terminal input (alternate screen buffer)
let setupRawMode () =
  Console.TreatControlCAsInput <- true
  AnsiCodes.enableVT100 () |> ignore
  Console.Write(AnsiCodes.enterAltScreen)
  Console.Write(AnsiCodes.hideCursor)
  Console.Write(AnsiCodes.clearScreen)
  Console.Write(AnsiCodes.home)
  TerminalUIState.IsActive <- true

/// Restore console to normal mode (return to main screen buffer)
let restoreConsole () =
  TerminalUIState.IsActive <- false
  Console.TreatControlCAsInput <- false
  Console.Write(AnsiCodes.showCursor)
  Console.Write(AnsiCodes.reset)
  Console.Write(AnsiCodes.leaveAltScreen)

/// Run the interactive terminal UI.
/// Renders RenderRegions from the Elm runtime and dispatches user input as EditorActions.
let run
  (elmRuntime: ElmRuntime<SageFsModel, SageFsMsg, RenderRegion>)
  (stateChanged: IEvent<string>)
  (connectionTracker: ConnectionTracker option)
  (sessionId: string option)
  (keyMap: KeyMap)
  (ct: CancellationToken)
  = task {

  let mutable gridRows = Console.WindowHeight
  let mutable gridCols = Console.WindowWidth
  let mutable grid = CellGrid.create gridRows gridCols
  let mutable focusedPane = PaneId.Editor
  let mutable scrollOffsets = Map.empty<PaneId, int>
  let mutable layoutConfig = LayoutConfig.defaults

  setupRawMode ()

  // Register terminal as connected UI
  let clientId = sprintf "terminal-%s" (Guid.NewGuid().ToString("N").[..7])
  connectionTracker |> Option.iter (fun t ->
    match sessionId with
    | Some sid -> t.Register(clientId, Terminal, sid)
    | None -> t.Register(clientId, Terminal))

  // Previous frame for diff optimization
  let mutable prevFrame = ""
  let mutable lastFrameMs = 0.0

  let render () =
    lock TerminalUIState.consoleLock (fun () ->
      try
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let regions = elmRuntime.GetRegions()
        let model = elmRuntime.GetModel()
        let sessionState =
          match model.Sessions.ActiveSessionId with
          | Some _ -> "Running"
          | None -> "No session"
        let evalCount = model.RecentOutput |> List.length
        let statusLeft = sprintf " %s | evals: %d | %s" sessionState evalCount (PaneId.displayName focusedPane)
        let statusRight = sprintf " %.1fms |%s" lastFrameMs (StatusHints.build keyMap focusedPane)
        let cursorPos = Screen.drawWith layoutConfig grid regions focusedPane scrollOffsets statusLeft statusRight
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

  // Initial render (alt screen already cleared by setupRawMode)
  render ()

  // Subscribe to Elm state changes for re-rendering
  let renderHandler = Handler<string>(fun _ _ -> render ())
  stateChanged.AddHandler renderHandler

  try
    // Key reading loop
    while not ct.IsCancellationRequested do
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
          return ()
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
        | Some (TerminalCommand.Action action) ->
          elmRuntime.Dispatch (SageFsMsg.Editor action)
        | None -> ()
      else
        // Small sleep to avoid busy-waiting
        try
          do! Threading.Tasks.Task.Delay(16, ct) // ~60fps check rate
        with :? OperationCanceledException -> ()
  finally
    connectionTracker |> Option.iter (fun t -> t.Unregister(clientId))
    stateChanged.RemoveHandler renderHandler
    restoreConsole ()
}
