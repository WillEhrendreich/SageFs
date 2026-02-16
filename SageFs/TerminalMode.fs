module SageFs.Server.TerminalMode

open System
open System.Threading
open SageFs

/// Immutable focus state for terminal panes
type TerminalState = {
  Layout: TerminalLayout
  Focus: PaneId
  ScrollOffsets: Map<PaneId, int>
}

module TerminalState =
  let create (rows: int) (cols: int) : TerminalState =
    { Layout = TerminalLayout.compute rows cols
      Focus = PaneId.Editor
      ScrollOffsets = Map.empty }

  let applyFocus (state: TerminalState) : TerminalState =
    let focusId = state.Focus
    { state with
        Layout =
          { state.Layout with
              Panes =
                state.Layout.Panes
                |> List.map (fun p ->
                  { p with Focused = p.PaneId = focusId }) } }

  let cycleFocus (state: TerminalState) : TerminalState =
    { state with Focus = PaneId.next state.Focus }
    |> applyFocus

  let focusDirection (dir: Direction) (state: TerminalState) : TerminalState =
    let paneRects =
      state.Layout.Panes
      |> List.map (fun p ->
        p.PaneId, Rect.create p.Row p.Col p.Width p.Height)
    let target = PaneId.navigate dir state.Focus paneRects
    { state with Focus = target }
    |> applyFocus

  let scroll (state: TerminalState) (delta: int) : TerminalState =
    let id = state.Focus
    let current = state.ScrollOffsets |> Map.tryFind id |> Option.defaultValue 0
    let next = max 0 (current + delta)
    let scrolls = state.ScrollOffsets |> Map.add id next
    { state with
        ScrollOffsets = scrolls
        Layout =
          { state.Layout with
              Panes =
                state.Layout.Panes
                |> List.map (fun p ->
                  if p.PaneId = id then
                    { p with ScrollOffset = next }
                  else p) } }

  let resize (state: TerminalState) (rows: int) (cols: int) : TerminalState =
    let fresh = TerminalLayout.compute rows cols
    { state with
        Layout =
          { fresh with
              Panes =
                fresh.Panes
                |> List.map (fun p ->
                  let scroll = state.ScrollOffsets |> Map.tryFind p.PaneId |> Option.defaultValue 0
                  { p with
                      ScrollOffset = scroll
                      Focused = p.PaneId = state.Focus }) } }


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
  (ct: CancellationToken)
  = task {

  let rows = Console.WindowHeight
  let cols = Console.WindowWidth
  let mutable state = TerminalState.create rows cols

  setupRawMode ()

  // Register terminal as connected UI
  let clientId = sprintf "terminal-%s" (Guid.NewGuid().ToString("N").[..7])
  connectionTracker |> Option.iter (fun t ->
    match sessionId with
    | Some sid -> t.Register(clientId, Terminal, sid)
    | None -> t.Register(clientId, Terminal))

  // Previous frame for diff optimization
  let mutable prevFrame = ""

  let render () =
    lock TerminalUIState.consoleLock (fun () ->
      try
        let regions = elmRuntime.GetRegions()
        let model = elmRuntime.GetModel()
        let sessionState =
          match model.Sessions.ActiveSessionId with
          | Some _ -> "Running"
          | None -> "No session"
        let evalCount =
          model.RecentOutput |> List.length
        let frame =
          TerminalRender.renderFrame state.Layout regions sessionState evalCount
        // Use diff rendering — only emit changed rows
        let output =
          if prevFrame.Length = 0 then frame
          else
            let d = FrameDiff.diff prevFrame frame
            if d.Length = 0 then "" // nothing changed
            else d
        if output.Length > 0 then
          Console.Write(output)
        prevFrame <- frame
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
      if newRows <> state.Layout.Rows || newCols <> state.Layout.Cols then
        state <- TerminalState.resize state newRows newCols
        prevFrame <- "" // force full redraw
        lock TerminalUIState.consoleLock (fun () ->
          Console.Write(AnsiCodes.clearScreen))
        render ()

      if Console.KeyAvailable then
        let keyInfo = Console.ReadKey(true)
        match TerminalInput.mapKey keyInfo with
        | Some TerminalCommand.Quit ->
          return ()
        | Some TerminalCommand.Redraw ->
          prevFrame <- "" // force full redraw
          lock TerminalUIState.consoleLock (fun () ->
            Console.Write(AnsiCodes.clearScreen))
          render ()
        | Some TerminalCommand.CycleFocus ->
          state <- TerminalState.cycleFocus state
          render ()
        | Some (TerminalCommand.FocusDirection dir) ->
          state <- TerminalState.focusDirection dir state
          render ()
        | Some TerminalCommand.ScrollUp ->
          state <- TerminalState.scroll state -3
          render ()
        | Some TerminalCommand.ScrollDown ->
          state <- TerminalState.scroll state 3
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
