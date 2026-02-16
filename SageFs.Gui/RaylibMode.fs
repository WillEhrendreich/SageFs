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
  let private defaultFontSize = 16
  let private minFontSize = 8
  let private maxFontSize = 48

  /// Try loading a font from well-known paths, fallback to default
  let private loadFont (size: int) =
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
      let f = Raylib.LoadFontEx(p, size, null, 0)
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

  let private mapKey () : GuiCommand option =
    // Modifier state
    let ctrl = ctrl ()
    let alt = alt ()
    let shift = shift ()

    let key = keyPressed ()
    if key = KeyboardKey.Null then None
    else
      match key with
      // Quit
      | KeyboardKey.Q when ctrl -> Some Quit
      | KeyboardKey.D when ctrl -> Some Quit
      // Focus
      | KeyboardKey.Tab when not ctrl -> Some CycleFocus
      // Spatial focus (Ctrl+H/J/K/L — vim-style)
      | KeyboardKey.H when ctrl -> Some (FocusDir Direction.Left)
      | KeyboardKey.J when ctrl -> Some (FocusDir Direction.Down)
      | KeyboardKey.K when ctrl -> Some (FocusDir Direction.Up)
      | KeyboardKey.L when ctrl -> Some (FocusDir Direction.Right)
      // Session management
      | KeyboardKey.N when ctrl -> Some (Action (EditorAction.CreateSession []))
      | KeyboardKey.S when ctrl && alt -> Some (Action EditorAction.ToggleSessionPanel)
      // Scroll
      | KeyboardKey.PageUp -> Some ScrollUp
      | KeyboardKey.PageDown -> Some ScrollDown
      | KeyboardKey.Up when alt -> Some ScrollUp
      | KeyboardKey.Down when alt -> Some ScrollDown
      // Font size
      | KeyboardKey.Equal when ctrl -> Some FontSizeUp
      | KeyboardKey.Minus when ctrl -> Some FontSizeDown
      // Navigation
      | KeyboardKey.Up when ctrl -> Some (Action EditorAction.HistoryPrevious)
      | KeyboardKey.Down when ctrl -> Some (Action EditorAction.HistoryNext)
      | KeyboardKey.Up -> Some (Action (EditorAction.MoveCursor Direction.Up))
      | KeyboardKey.Down -> Some (Action (EditorAction.MoveCursor Direction.Down))
      | KeyboardKey.Left when ctrl -> Some (Action EditorAction.MoveWordBackward)
      | KeyboardKey.Right when ctrl -> Some (Action EditorAction.MoveWordForward)
      | KeyboardKey.Left -> Some (Action (EditorAction.MoveCursor Direction.Left))
      | KeyboardKey.Right -> Some (Action (EditorAction.MoveCursor Direction.Right))
      | KeyboardKey.Home -> Some (Action EditorAction.MoveToLineStart)
      | KeyboardKey.End -> Some (Action EditorAction.MoveToLineEnd)
      // Editing
      | KeyboardKey.Enter when ctrl -> Some (Action EditorAction.Submit)
      | KeyboardKey.Enter -> Some (Action EditorAction.NewLine)
      | KeyboardKey.Backspace when ctrl -> Some (Action EditorAction.DeleteWord)
      | KeyboardKey.Backspace -> Some (Action EditorAction.DeleteBackward)
      | KeyboardKey.Delete -> Some (Action EditorAction.DeleteForward)
      // Selection & completion
      | KeyboardKey.A when ctrl -> Some (Action EditorAction.SelectAll)
      | KeyboardKey.Space when ctrl -> Some (Action EditorAction.TriggerCompletion)
      | KeyboardKey.Escape -> Some (Action EditorAction.DismissCompletion)
      // Undo/Redo
      | KeyboardKey.R when ctrl -> Some (Action EditorAction.Undo)
      | KeyboardKey.Z when ctrl && shift -> Some (Action EditorAction.Redo)
      | KeyboardKey.Z when ctrl -> Some (Action EditorAction.Undo)
      // Cancel
      | KeyboardKey.C when ctrl -> Some (Action EditorAction.Cancel)
      | _ -> None

  /// Get typed characters (for InsertChar) — separate from key presses
  let private getCharInput () : EditorAction option =
    let ch = charPressed ()
    if ch > 0 then Some (EditorAction.InsertChar (char ch))
    else None

  /// Compute pane layout rects for the given grid dimensions.
  let private computePaneRects (rows: int) (cols: int) : (PaneId * Rect) list =
    Screen.computeLayout rows cols |> fst

  /// Render regions into the CellGrid using shared Screen module
  let private renderRegions
    (grid: Cell[,])
    (regions: RenderRegion list)
    (sessionState: string)
    (evalCount: int)
    (focusedPane: PaneId)
    (scrollOffsets: Map<PaneId, int>)
    (fontSize: int) =

    let statusLeft = sprintf " %s | evals: %d | %s" sessionState evalCount (PaneId.displayName focusedPane)
    let statusRight = sprintf " %dpt | Ctrl+/- font | Ctrl+Q quit | Tab focus " fontSize
    Screen.draw grid regions focusedPane scrollOffsets statusLeft statusRight |> ignore

  /// Run the Raylib GUI window connected to daemon.
  let run () =
    // Discover daemon
    let daemonInfo =
      match DaemonState.read () with
      | None ->
        eprintfn "No SageFs daemon running. Start one with: sagefs --proj <project.fsproj>"
        None
      | Some info -> Some info

    match daemonInfo with
    | None -> ()
    | Some daemonInfo ->

    let dashboardPort = daemonInfo.Port + 1
    let baseUrl = sprintf "http://localhost:%d" dashboardPort

    // Verify connection before opening window
    use client = new HttpClient()
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
    let mutable lastEvalCount = 0
    let mutable focusedPane = PaneId.Editor
    let mutable scrollOffsets = Map.empty<PaneId, int>
    let statelock = obj ()
    let mutable running = true

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
    let mutable grid = CellGrid.create gridRows gridCols

    let reloadFont () =
      Raylib.UnloadFont(font)
      font <- loadFont fontSize
      charSize <- Raylib.MeasureTextEx(font, "M", float32 fontSize, 0.0f)
      cellW <- max 1 (int (System.MathF.Ceiling(charSize.X)))
      cellH <- max 1 (int (System.MathF.Ceiling(charSize.Y)) + 2)

    // Start SSE listener
    use cts = new CancellationTokenSource()
    let _sseTask =
      System.Threading.Tasks.Task.Run(fun () ->
        DaemonClient.runSseListener
          baseUrl
          (fun sessionState evalCount regions ->
            lock statelock (fun () ->
              lastSessionState <- sessionState
              lastEvalCount <- evalCount
              lastRegions <- regions))
          (fun _ ->
            lock statelock (fun () ->
              lastSessionState <- sprintf "%s (reconnecting...)" lastSessionState))
          cts.Token
        |> fun t -> t.Wait())

    while running && not (windowShouldClose ()) do
      let sw = System.Diagnostics.Stopwatch.StartNew()

      // Handle window resize
      let winW = screenW ()
      let winH = screenH ()
      let newCols = max 40 (winW / cellW)
      let newRows = max 10 (winH / cellH)
      if newCols <> gridCols || newRows <> gridRows then
        gridCols <- newCols
        gridRows <- newRows
        grid <- CellGrid.create gridRows gridCols

      // Handle input — process all pending keys
      let mutable keyCmd = mapKey ()
      while running && keyCmd.IsSome do
        match keyCmd.Value with
        | Quit -> running <- false
        | CycleFocus ->
          focusedPane <- PaneId.next focusedPane
        | FocusDir dir ->
          let paneRects = computePaneRects gridRows gridCols
          focusedPane <- PaneId.navigate dir focusedPane paneRects
        | ScrollUp ->
          let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
          scrollOffsets <- scrollOffsets |> Map.add focusedPane (max 0 (cur - 3))
        | ScrollDown ->
          let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
          scrollOffsets <- scrollOffsets |> Map.add focusedPane (cur + 3)
        | Redraw -> ()
        | FontSizeUp ->
          fontSize <- min maxFontSize (fontSize + 2)
          reloadFont ()
        | FontSizeDown ->
          fontSize <- max minFontSize (fontSize - 2)
          reloadFont ()
        | Action action ->
          DaemonClient.dispatch client baseUrl action |> fun t -> t.Wait()
        keyCmd <- mapKey ()

      // Handle char input (typed text)
      let mutable charAction = getCharInput ()
      while running && charAction.IsSome do
        match charAction.Value with
        | action ->
          DaemonClient.dispatch client baseUrl action |> fun t -> t.Wait()
        charAction <- getCharInput ()

      // Handle mouse click → focus pane
      if mousePressed MouseButton.Left then
        let mp = mousePos ()
        let clickCol = int mp.X / cellW
        let clickRow = int mp.Y / cellH
        let paneRects = computePaneRects gridRows gridCols
        let clicked =
          paneRects
          |> List.tryFind (fun (_, r) ->
            clickRow >= r.Row && clickRow < r.Row + r.Height &&
            clickCol >= r.Col && clickCol < r.Col + r.Width)
        match clicked with
        | Some (id, _) -> focusedPane <- id
        | None -> ()

      if running then
        // Render
        let regions, sessionState, evalCount =
          lock statelock (fun () -> lastRegions, lastSessionState, lastEvalCount)

        renderRegions grid regions sessionState evalCount focusedPane scrollOffsets fontSize

        sw.Stop()
        let frameMs = sw.Elapsed.TotalMilliseconds
        // Overlay frame timing on status bar area
        let fpsText = sprintf "%d fps | %.1f ms" (fps ()) frameMs
        Draw.text (DrawTarget.create grid (Rect.create 0 0 gridCols gridRows))
          (gridRows - 1) (gridCols - fpsText.Length - 1)
          Theme.fgDim Theme.bgStatus CellAttrs.None fpsText

        Raylib.BeginDrawing()
        Raylib.ClearBackground(RaylibPalette.toColor Theme.bgDefault)
        RaylibEmitter.emit grid font cellW cellH fontSize
        Raylib.EndDrawing()

    cts.Cancel()
    if windowReady () then Raylib.CloseWindow()
