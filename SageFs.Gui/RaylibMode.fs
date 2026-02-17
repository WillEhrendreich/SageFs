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
    | TogglePane of string
    | LayoutPreset of string
    | ResizeH of int
    | ResizeV of int
    | ResizeR of int
    | CycleTheme
    | CopySelection

  /// Convert Raylib KeyboardKey to System.ConsoleKey for KeyMap lookup
  let private raylibToConsoleKey (key: KeyboardKey) : System.ConsoleKey option =
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
    | KeyboardKey.Equal -> Some System.ConsoleKey.OemPlus
    | KeyboardKey.Minus -> Some System.ConsoleKey.OemMinus
    | k when k >= KeyboardKey.A && k <= KeyboardKey.Z ->
      Some (enum<System.ConsoleKey> (int System.ConsoleKey.A + int k - int KeyboardKey.A))
    | k when k >= KeyboardKey.Zero && k <= KeyboardKey.Nine ->
      Some (enum<System.ConsoleKey> (int System.ConsoleKey.D0 + int k - int KeyboardKey.Zero))
    | _ -> None

  let private mapKeyWith (keyMap: KeyMap) () : GuiCommand option =
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
        | Some (UiAction.Editor action) -> Some (Action action)
        | None ->
          // Ctrl+C not in keymap → copy selection
          if ck = System.ConsoleKey.C && c && not a && not s then Some CopySelection
          else None

  /// Get typed characters (for InsertChar) — separate from key presses
  let private getCharInput () : EditorAction option =
    let ch = charPressed ()
    if ch > 0 then Some (EditorAction.InsertChar (char ch))
    else None

  /// Compute pane layout rects for the given grid dimensions.
  /// Render regions into the CellGrid using shared Screen module
  let private renderRegions
    (grid: Cell[,])
    (regions: RenderRegion list)
    (sessionId: string)
    (sessionState: string)
    (evalCount: int)
    (focusedPane: PaneId)
    (scrollOffsets: Map<PaneId, int>)
    (fontSize: int)
    (currentFps: int)
    (keyMap: KeyMap)
    (layoutConfig: LayoutConfig)
    (theme: ThemeConfig)
    (themeName: string) =

    let statusLeft =
      let sid = if sessionId.Length > 8 then sessionId.[..7] else sessionId
      sprintf " %s %s | evals: %d | %s" sid sessionState evalCount (PaneId.displayName focusedPane)
    let statusRight = sprintf " %s | %dpt | %d fps |%s" themeName fontSize currentFps (StatusHints.build keyMap focusedPane)
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
    let mutable lastSessionId = ""
    let mutable lastEvalCount = 0
    let mutable lastFps = 0
    let mutable focusedPane = PaneId.Editor
    let mutable scrollOffsets = Map.empty<PaneId, int>
    let mutable layoutConfig = LayoutConfig.defaults
    let mutable currentTheme = Theme.defaults
    let mutable currentThemeName = "One Dark"
    let statelock = obj ()
    let mutable running = true
    // Text selection state (grid coordinates)
    let mutable selStart : (int * int) option = None
    let mutable selEnd : (int * int) option = None
    let mutable selecting = false

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
          (fun sessionId sessionState evalCount _avgMs regions ->
            lock statelock (fun () ->
              lastSessionId <- sessionId
              lastSessionState <- sessionState
              lastEvalCount <- evalCount
              lastRegions <- regions))
          (fun _ ->
            lock statelock (fun () ->
              lastSessionState <- sprintf "%s (reconnecting...)" lastSessionState))
          cts.Token
        |> fun t -> t.Wait())

    while running && not (windowShouldClose ()) do

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
          let paneRects = Screen.computeLayoutWith layoutConfig gridRows gridCols |> fst
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
        | TogglePane paneName ->
          match PaneId.tryParse paneName with
          | Some pid ->
            layoutConfig <- LayoutConfig.togglePane pid layoutConfig
            if not (layoutConfig.VisiblePanes.Contains focusedPane) then
              focusedPane <- PaneId.Editor
          | None -> ()
        | LayoutPreset presetName ->
          layoutConfig <-
            match presetName with
            | "focus" -> LayoutConfig.focus
            | "minimal" -> LayoutConfig.minimal
            | _ -> LayoutConfig.defaults
          if not (layoutConfig.VisiblePanes.Contains focusedPane) then
            focusedPane <- PaneId.Editor
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
          DaemonClient.dispatch client baseUrl remappedAction |> fun t -> t.Wait()
        keyCmd <- mapKey ()

      // Handle char input (typed text)
      let mutable charAction = getCharInput ()
      while running && charAction.IsSome do
        match charAction.Value with
        | action ->
          DaemonClient.dispatch client baseUrl action |> fun t -> t.Wait()
        charAction <- getCharInput ()

      // Handle mouse → text selection (drag) + focus pane + cursor/session click
      if mousePressed MouseButton.Left then
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
              |> fun t -> t.Wait()
          elif id = PaneId.Sessions then
            let contentRow = clickRow - r.Row - 1
            let scrollOff = scrollOffsets |> Map.tryFind PaneId.Sessions |> Option.defaultValue 0
            let sessionIdx = contentRow + scrollOff
            if contentRow >= 0 then
              DaemonClient.dispatch client baseUrl (EditorAction.SessionSetIndex sessionIdx)
              |> fun t -> t.Wait()
        | None -> ()
      elif selecting && mouseDown MouseButton.Left then
        // Extend selection while dragging
        let mp = mousePos ()
        let dragCol = max 0 (min (gridCols - 1) (int mp.X / cellW))
        let dragRow = max 0 (min (gridRows - 1) (int mp.Y / cellH))
        selEnd <- Some (dragRow, dragCol)
      elif selecting && mouseReleased MouseButton.Left then
        selecting <- false
        // If start == end, it was a click not a drag — clear selection
        match selStart, selEnd with
        | Some s, Some e when s = e ->
          selStart <- None
          selEnd <- None
        | _ -> ()

      if running then
        // Render
        let regions, sessionId, sessionState, evalCount =
          lock statelock (fun () -> lastRegions, lastSessionId, lastSessionState, lastEvalCount)

        renderRegions grid regions sessionId sessionState evalCount focusedPane scrollOffsets fontSize lastFps keyMap layoutConfig currentTheme currentThemeName
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
