module SageFs.Server.TerminalMode

open System
open System.Threading
open SageFs

/// Mutable focus state for terminal panes
type TerminalState = {
  mutable Layout: TerminalLayout
  mutable FocusIndex: int
  mutable ScrollOffsets: Map<string, int>
}

module TerminalState =
  let paneIds = [| "output"; "sessions"; "diagnostics"; "editor" |]

  let create (rows: int) (cols: int) : TerminalState =
    { Layout = TerminalLayout.compute rows cols
      FocusIndex = 3 // editor focused by default
      ScrollOffsets = Map.empty }

  let focusedId (state: TerminalState) =
    paneIds.[state.FocusIndex % paneIds.Length]

  let cycleFocus (state: TerminalState) =
    state.FocusIndex <- (state.FocusIndex + 1) % paneIds.Length
    let focusId = focusedId state
    state.Layout <-
      { state.Layout with
          Panes =
            state.Layout.Panes
            |> List.map (fun p ->
              { p with Focused = p.RegionId = focusId }) }

  let scroll (state: TerminalState) (delta: int) =
    let id = focusedId state
    let current = state.ScrollOffsets |> Map.tryFind id |> Option.defaultValue 0
    let next = max 0 (current + delta)
    state.ScrollOffsets <- state.ScrollOffsets |> Map.add id next
    state.Layout <-
      { state.Layout with
          Panes =
            state.Layout.Panes
            |> List.map (fun p ->
              if p.RegionId = id then
                { p with ScrollOffset = next }
              else p) }

  let resize (state: TerminalState) (rows: int) (cols: int) =
    let focusId = focusedId state
    state.Layout <- TerminalLayout.compute rows cols
    state.Layout <-
      { state.Layout with
          Panes =
            state.Layout.Panes
            |> List.map (fun p ->
              let scroll = state.ScrollOffsets |> Map.tryFind p.RegionId |> Option.defaultValue 0
              { p with
                  ScrollOffset = scroll
                  Focused = p.RegionId = focusId }) }


/// Set up the console for raw terminal input
let setupRawMode () =
  Console.TreatControlCAsInput <- true
  Console.CursorVisible <- false
  // Enable VT100 processing (Windows 10+)
  try
    Console.OutputEncoding <- Text.Encoding.UTF8
  with _ -> ()

/// Restore console to normal mode
let restoreConsole () =
  Console.TreatControlCAsInput <- false
  Console.CursorVisible <- true
  Console.Write(AnsiCodes.reset)
  Console.Write(AnsiCodes.showCursor)
  Console.Write(AnsiCodes.clearScreen)
  Console.Write(AnsiCodes.home)

/// Run the interactive terminal UI.
/// Renders RenderRegions from the Elm runtime and dispatches user input as EditorActions.
let run
  (elmRuntime: ElmRuntime<SageFsModel, SageFsMsg, RenderRegion>)
  (stateChanged: IEvent<string>)
  (ct: CancellationToken)
  = task {

  let rows = Console.WindowHeight
  let cols = Console.WindowWidth
  let state = TerminalState.create rows cols

  setupRawMode ()

  let render () =
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
      Console.Write(frame)
    with ex ->
      eprintfn "[terminal] render error: %s" ex.Message

  // Initial render
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
        TerminalState.resize state newRows newCols
        Console.Write(AnsiCodes.clearScreen)
        render ()

      if Console.KeyAvailable then
        let keyInfo = Console.ReadKey(true)
        match TerminalInput.mapKey keyInfo with
        | Some TerminalCommand.Quit ->
          return ()
        | Some TerminalCommand.Redraw ->
          Console.Write(AnsiCodes.clearScreen)
          render ()
        | Some TerminalCommand.CycleFocus ->
          TerminalState.cycleFocus state
          render ()
        | Some TerminalCommand.ScrollUp ->
          TerminalState.scroll state -3
          render ()
        | Some TerminalCommand.ScrollDown ->
          TerminalState.scroll state 3
          render ()
        | Some (TerminalCommand.Action action) ->
          elmRuntime.Dispatch (SageFsMsg.Editor action)
        | None -> ()
      else
        // Small sleep to avoid busy-waiting
        do! Threading.Tasks.Task.Delay(16, ct) // ~60fps check rate
  finally
    stateChanged.RemoveHandler renderHandler
    restoreConsole ()
}
