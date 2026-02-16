namespace SageFs

open System

/// Generate context-sensitive status bar hints from the active KeyMap.
module StatusHints =
  /// Short label for a key combo (compact for status bar)
  let private shortFormat (kc: KeyCombo) : string =
    let parts = ResizeArray<string>()
    if kc.Modifiers.HasFlag(ConsoleModifiers.Control) then parts.Add("^")
    if kc.Modifiers.HasFlag(ConsoleModifiers.Alt) then parts.Add("A-")
    if kc.Modifiers.HasFlag(ConsoleModifiers.Shift) then parts.Add("S-")
    let keyName =
      match kc.Key with
      | ConsoleKey.Enter -> "Enter"
      | ConsoleKey.Tab -> "Tab"
      | ConsoleKey.Escape -> "Esc"
      | ConsoleKey.PageUp -> "PgUp"
      | ConsoleKey.PageDown -> "PgDn"
      | ConsoleKey.UpArrow -> "Up"
      | ConsoleKey.DownArrow -> "Down"
      | ConsoleKey.LeftArrow -> "Left"
      | ConsoleKey.RightArrow -> "Right"
      | ConsoleKey.OemPlus -> "="
      | ConsoleKey.OemMinus -> "-"
      | ConsoleKey.Backspace -> "Bksp"
      | ConsoleKey.Spacebar -> "Space"
      | k -> sprintf "%A" k
    parts.Add(keyName)
    String.Concat(parts)

  let private findShort (keyMap: KeyMap) (action: UiAction) : string option =
    keyMap
    |> Map.tryFindKey (fun _ a -> a = action)
    |> Option.map shortFormat

  /// Build the right-side status bar hints string.
  /// Shows common actions with their configured keybindings.
  let build (keyMap: KeyMap) (focusedPane: PaneId) : string =
    let hint action label =
      findShort keyMap action
      |> Option.map (fun k -> sprintf "%s:%s" k label)
    let editorHint action label =
      hint (UiAction.Editor action) label
    let common =
      [ hint UiAction.Quit "quit"
        hint UiAction.CycleFocus "focus"
        hint UiAction.ScrollUp "scroll" ]
      |> List.choose id
    let paneHints =
      match focusedPane with
      | PaneId.Editor ->
        [ editorHint EditorAction.Submit "eval"
          editorHint EditorAction.TriggerCompletion "complete"
          editorHint EditorAction.Cancel "cancel" ]
        |> List.choose id
      | PaneId.Output | PaneId.Diagnostics ->
        [ hint UiAction.ScrollDown "scroll↓" ]
        |> List.choose id
      | PaneId.Sessions ->
        [ editorHint (EditorAction.CreateSession []) "new-session" ]
        |> List.choose id
    let all = paneHints @ common
    if all.IsEmpty then ""
    else sprintf " %s " (String.concat " | " all)

/// Shared screen composition — computes layout and renders panes into a CellGrid.
/// Used by both TUI (via AnsiEmitter) and GUI (via RaylibEmitter).
module Screen =

  /// Compute the standard 4-pane layout for the given grid dimensions.
  /// Returns (PaneId * Rect) list and a status bar rect.
  let computeLayout (rows: int) (cols: int) : (PaneId * Rect) list * Rect =
    let contentArea = Rect.create 0 0 cols (rows - 1)
    let left, right = Rect.splitVProp 0.65 contentArea
    let outputRect, editorRect = Rect.splitH (left.Height - 6) left
    let sessRect, diagRect = Rect.splitHProp 0.5 right
    let panes =
      [ PaneId.Output, outputRect
        PaneId.Editor, editorRect
        PaneId.Sessions, sessRect
        PaneId.Diagnostics, diagRect ]
    let statusRect = Rect.create (rows - 1) 0 cols 1
    panes, statusRect

  /// Draw all panes and status bar into the given CellGrid.
  /// Returns the cursor position (screen row, col) if the focused pane has one.
  let draw
    (grid: Cell[,])
    (regions: RenderRegion list)
    (focusedPane: PaneId)
    (scrollOffsets: Map<PaneId, int>)
    (statusLeft: string)
    (statusRight: string) : (int * int) option =

    let rows = CellGrid.rows grid
    let cols = CellGrid.cols grid

    CellGrid.clear grid
    let dt = DrawTarget.create grid (Rect.create 0 0 cols rows)
    Draw.fill dt Theme.bgPanel

    let paneRects, _statusRect = computeLayout rows cols

    let mutable cursorPos = None

    for (paneId, rect) in paneRects do
      let borderColor =
        if paneId = focusedPane then Theme.borderFocus else Theme.borderNormal
      let bg =
        if paneId = PaneId.Editor then Theme.bgEditor else Theme.bgPanel
      let inner =
        Draw.box (DrawTarget.create grid rect) (PaneId.displayName paneId) borderColor bg

      let regionId = PaneId.toRegionId paneId
      match regions |> List.tryFind (fun r -> r.Id = regionId) with
      | Some region ->
        let lines = region.Content.Split('\n')
        let offset = scrollOffsets |> Map.tryFind paneId |> Option.defaultValue 0
        let skip = min offset (max 0 (lines.Length - 1))
        let visibleLines = lines |> Array.skip skip |> Array.truncate inner.Clip.Height
        let fg = Theme.fgDefault
        visibleLines |> Array.iteri (fun row line ->
          Draw.text inner row 0 fg bg CellAttrs.None line)

        // Scroll indicators
        if skip > 0 then
          Draw.text inner 0 (inner.Clip.Width - 1) Theme.fgDim bg CellAttrs.None "▲"
        if lines.Length > skip + inner.Clip.Height then
          Draw.text inner (inner.Clip.Height - 1) (inner.Clip.Width - 1) Theme.fgDim bg CellAttrs.None "▼"

        // Track cursor for focused pane
        if paneId = focusedPane then
          match region.Cursor with
          | Some c ->
            let screenRow = rect.Row + 1 + c.Line
            let screenCol = rect.Col + 1 + c.Col
            cursorPos <- Some (screenRow, screenCol)
          | None ->
            cursorPos <- Some (rect.Row + 1, rect.Col + 1)

        // Completion popup overlay
        match region.Completions with
        | Some compl when compl.Items.Length > 0 ->
          let cursorScreenRow =
            match region.Cursor with
            | Some c -> rect.Row + 1 + c.Line
            | None -> rect.Row + 1
          let cursorScreenCol =
            match region.Cursor with
            | Some c -> rect.Col + 1 + c.Col
            | None -> rect.Col + 1
          let popupRow = cursorScreenRow + 1
          let popupCol = cursorScreenCol
          let maxVisible = min 8 compl.Items.Length
          let menuWidth = (compl.Items |> List.map (fun s -> s.Length) |> List.max) + 2
          for i in 0 .. maxVisible - 1 do
            let r = popupRow + i
            let c = popupCol
            if r < rows - 1 && c + menuWidth < cols then
              let isSelected = i = compl.SelectedIndex
              let itemFg = if isSelected then Theme.bgEditor else Theme.fgDefault
              let itemBg = if isSelected then Theme.borderFocus else Theme.bgStatus
              let label = compl.Items.[i].PadRight(menuWidth)
              let itemDt = DrawTarget.create grid (Rect.create r c menuWidth 1)
              Draw.text itemDt 0 0 itemFg itemBg CellAttrs.None (sprintf " %s" label)
        | _ -> ()
      | None ->
        // Default cursor at content start for focused pane
        if paneId = focusedPane then
          cursorPos <- Some (rect.Row + 1, rect.Col + 1)

    // Status bar
    Draw.statusBar dt statusLeft statusRight Theme.fgDefault Theme.bgStatus

    cursorPos
