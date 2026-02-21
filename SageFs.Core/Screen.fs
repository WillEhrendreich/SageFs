namespace SageFs

open System

/// Generate context-sensitive status bar hints from the active KeyMap.
module StatusHints =
  /// Short label for a key combo (compact for status bar)
  let shortFormat (kc: KeyCombo) : string =
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

  let findShort (keyMap: KeyMap) (action: UiAction) : string option =
    keyMap
    |> Map.tryFindKey (fun _ a -> a = action)
    |> Option.map shortFormat

  /// Build the right-side status bar hints string.
  /// Shows common actions with their configured keybindings.
  let build (keyMap: KeyMap) (focusedPane: PaneId) (layout: Set<PaneId>) : string =
    let hint action label =
      findShort keyMap action
      |> Option.map (fun k -> sprintf "%s:%s" k label)
    let editorHint action label =
      hint (UiAction.Editor action) label
    let editorToggle =
      if layout.Contains PaneId.Editor then
        hint (UiAction.TogglePane "Editor") "hide-editor"
      else
        hint (UiAction.TogglePane "Editor") "show-editor"
    let common =
      [ hint UiAction.Quit "quit"
        hint UiAction.CycleFocus "focus"
        hint UiAction.ScrollUp "scroll"
        editorToggle ]
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

/// Layout configuration — which panes are visible and how space is divided.
type LayoutConfig = {
  VisiblePanes: Set<PaneId>
  LeftRightSplit: float  // proportion for left column (0.0-1.0)
  OutputEditorSplit: int // rows reserved for editor in left column
  SessionsDiagSplit: float // proportion for sessions in right column
}

module LayoutConfig =
  let defaults = {
    VisiblePanes = Set.ofList [ PaneId.Output; PaneId.Sessions ]
    LeftRightSplit = 0.65
    OutputEditorSplit = 6
    SessionsDiagSplit = 0.5
  }

  /// Focus mode: editor + output only
  let focus = {
    VisiblePanes = Set.ofList [ PaneId.Output; PaneId.Editor ]
    LeftRightSplit = 1.0
    OutputEditorSplit = 6
    SessionsDiagSplit = 0.5
  }

  /// Minimal mode: editor only
  let minimal = {
    VisiblePanes = Set.singleton PaneId.Editor
    LeftRightSplit = 1.0
    OutputEditorSplit = 0
    SessionsDiagSplit = 0.5
  }

  /// Toggle a pane's visibility.
  let togglePane (paneId: PaneId) (cfg: LayoutConfig) : LayoutConfig =
    if cfg.VisiblePanes.Contains paneId then
      { cfg with VisiblePanes = Set.remove paneId cfg.VisiblePanes }
    else
      { cfg with VisiblePanes = Set.add paneId cfg.VisiblePanes }

  let clampF lo hi v = max lo (min hi v)

  /// Adjust LeftRightSplit by delta (±1 maps to ±0.05)
  let resizeH (delta: int) (cfg: LayoutConfig) : LayoutConfig =
    { cfg with LeftRightSplit = clampF 0.2 0.9 (System.Math.Round(cfg.LeftRightSplit + float delta * 0.05, 2)) }

  /// Adjust OutputEditorSplit by delta (±1 row)
  let resizeV (delta: int) (cfg: LayoutConfig) : LayoutConfig =
    { cfg with OutputEditorSplit = max 2 (cfg.OutputEditorSplit + delta) }

  /// Adjust SessionsDiagSplit by delta (±1 maps to ±0.05)
  let resizeR (delta: int) (cfg: LayoutConfig) : LayoutConfig =
    { cfg with SessionsDiagSplit = clampF 0.1 0.9 (System.Math.Round(cfg.SessionsDiagSplit + float delta * 0.05, 2)) }

/// Shared screen composition — computes layout and renders panes into a CellGrid.
/// Used by both TUI (via AnsiEmitter) and GUI (via RaylibEmitter).
module Screen =

  /// Compute layout using the given LayoutConfig.
  let computeLayoutWith (cfg: LayoutConfig) (rows: int) (cols: int) : (PaneId * Rect) list * Rect =
    let contentArea = Rect.create 0 0 cols (rows - 1)
    let statusRect = Rect.create (rows - 1) 0 cols 1
    let hasLeft =
      cfg.VisiblePanes.Contains PaneId.Output || cfg.VisiblePanes.Contains PaneId.Editor
    let hasRight =
      cfg.VisiblePanes.Contains PaneId.Sessions || cfg.VisiblePanes.Contains PaneId.Diagnostics
    let panes = ResizeArray<PaneId * Rect>()
    if hasLeft && hasRight then
      let left, right = Rect.splitVProp cfg.LeftRightSplit contentArea
      // Left column
      if cfg.VisiblePanes.Contains PaneId.Output && cfg.VisiblePanes.Contains PaneId.Editor then
        let outputRect, editorRect = Rect.splitH (left.Height - cfg.OutputEditorSplit) left
        panes.Add(PaneId.Output, outputRect)
        panes.Add(PaneId.Editor, editorRect)
      elif cfg.VisiblePanes.Contains PaneId.Output then
        panes.Add(PaneId.Output, left)
      elif cfg.VisiblePanes.Contains PaneId.Editor then
        panes.Add(PaneId.Editor, left)
      // Right column
      if cfg.VisiblePanes.Contains PaneId.Sessions && cfg.VisiblePanes.Contains PaneId.Diagnostics then
        let sessRect, diagRect = Rect.splitHProp cfg.SessionsDiagSplit right
        panes.Add(PaneId.Sessions, sessRect)
        panes.Add(PaneId.Diagnostics, diagRect)
      elif cfg.VisiblePanes.Contains PaneId.Sessions then
        panes.Add(PaneId.Sessions, right)
      elif cfg.VisiblePanes.Contains PaneId.Diagnostics then
        panes.Add(PaneId.Diagnostics, right)
    elif hasLeft then
      if cfg.VisiblePanes.Contains PaneId.Output && cfg.VisiblePanes.Contains PaneId.Editor then
        let outputRect, editorRect = Rect.splitH (contentArea.Height - cfg.OutputEditorSplit) contentArea
        panes.Add(PaneId.Output, outputRect)
        panes.Add(PaneId.Editor, editorRect)
      elif cfg.VisiblePanes.Contains PaneId.Output then
        panes.Add(PaneId.Output, contentArea)
      elif cfg.VisiblePanes.Contains PaneId.Editor then
        panes.Add(PaneId.Editor, contentArea)
    elif hasRight then
      if cfg.VisiblePanes.Contains PaneId.Sessions && cfg.VisiblePanes.Contains PaneId.Diagnostics then
        let sessRect, diagRect = Rect.splitHProp cfg.SessionsDiagSplit contentArea
        panes.Add(PaneId.Sessions, sessRect)
        panes.Add(PaneId.Diagnostics, diagRect)
      elif cfg.VisiblePanes.Contains PaneId.Sessions then
        panes.Add(PaneId.Sessions, contentArea)
      elif cfg.VisiblePanes.Contains PaneId.Diagnostics then
        panes.Add(PaneId.Diagnostics, contentArea)
    panes |> Seq.toList, statusRect

  /// Compute the standard 4-pane layout for the given grid dimensions.
  let computeLayout (rows: int) (cols: int) : (PaneId * Rect) list * Rect =
    computeLayoutWith LayoutConfig.defaults rows cols

  /// Draw all panes and status bar into the given CellGrid, using the given LayoutConfig.
  /// Returns the cursor position (screen row, col) if the focused pane has one.
  let drawWith
    (cfg: LayoutConfig)
    (theme: ThemeConfig)
    (grid: CellGrid)
    (regions: RenderRegion list)
    (focusedPane: PaneId)
    (scrollOffsets: Map<PaneId, int>)
    (statusLeft: string)
    (statusRight: string) : (int * int) option =

    let rows = CellGrid.rows grid
    let cols = CellGrid.cols grid

    CellGrid.clear grid
    let dt = DrawTarget.create grid (Rect.create 0 0 cols rows)
    Draw.fill dt (Theme.hexToRgb theme.BgPanel)

    let paneRects, _statusRect = computeLayoutWith cfg rows cols

    let mutable cursorPos = None

    for (paneId, rect) in paneRects do
      let borderColor =
        if paneId = focusedPane then Theme.hexToRgb theme.BorderFocus else Theme.hexToRgb theme.BorderNormal
      let bg =
        if paneId = PaneId.Editor then Theme.hexToRgb theme.BgEditor else Theme.hexToRgb theme.BgPanel
      let inner =
        Draw.box (DrawTarget.create grid rect) (PaneId.displayName paneId) borderColor bg

      let regionId = PaneId.toRegionId paneId
      match regions |> List.tryFind (fun r -> r.Id = regionId) with
      | Some region ->
        let lines = region.Content.Split('\n')
        let offsetFromBottom = scrollOffsets |> Map.tryFind paneId |> Option.defaultValue 0
        // offset 0 = show bottom (latest output), positive = scrolled up from bottom
        let skip = max 0 (lines.Length - inner.Clip.Height - offsetFromBottom)
        let visibleLines = lines |> Array.skip skip |> Array.truncate inner.Clip.Height
        let fg = Theme.hexToRgb theme.FgDefault

        // Apply syntax highlighting for editor and output panes
        let shouldHighlight =
          paneId = PaneId.Editor || paneId = PaneId.Output
        let allSpans =
          if shouldHighlight && SyntaxHighlight.isAvailable () then
            SyntaxHighlight.tokenize theme region.Content
          else
            [||]
        let spanOffset = skip

        visibleLines |> Array.iteri (fun row line ->
          let lineIdx = spanOffset + row
          if shouldHighlight && lineIdx < allSpans.Length && allSpans.[lineIdx].Length > 0 then
            Draw.textHighlighted inner row 0 fg bg CellAttrs.None allSpans.[lineIdx] line
          else
            Draw.text inner row 0 fg bg CellAttrs.None line)

        // Scroll indicators
        if skip > 0 then
          Draw.text inner 0 (inner.Clip.Width - 1) (Theme.hexToRgb theme.FgDim) bg CellAttrs.None "▲"
        if lines.Length > skip + inner.Clip.Height then
          Draw.text inner (inner.Clip.Height - 1) (inner.Clip.Width - 1) (Theme.hexToRgb theme.FgDim) bg CellAttrs.None "▼"

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
              let itemFg = if isSelected then Theme.hexToRgb theme.BgEditor else Theme.hexToRgb theme.FgDefault
              let itemBg = if isSelected then Theme.hexToRgb theme.BorderFocus else Theme.hexToRgb theme.BgStatus
              let label = compl.Items.[i].PadRight(menuWidth)
              let itemDt = DrawTarget.create grid (Rect.create r c menuWidth 1)
              Draw.text itemDt 0 0 itemFg itemBg CellAttrs.None (sprintf " %s" label)
        | _ -> ()
      | None ->
        // Default cursor at content start for focused pane
        if paneId = focusedPane then
          cursorPos <- Some (rect.Row + 1, rect.Col + 1)

    // Merge adjacent box borders into proper T-junctions
    Draw.resolveJunctions dt

    // Status bar
    Draw.statusBar dt statusLeft statusRight (Theme.hexToRgb theme.FgDefault) (Theme.hexToRgb theme.BgStatus)

    cursorPos

  /// Draw all panes and status bar using the default layout config and theme.
  let draw
    (grid: CellGrid)
    (regions: RenderRegion list)
    (focusedPane: PaneId)
    (scrollOffsets: Map<PaneId, int>)
    (statusLeft: string)
    (statusRight: string) : (int * int) option =
    drawWith LayoutConfig.defaults Theme.defaults grid regions focusedPane scrollOffsets statusLeft statusRight
