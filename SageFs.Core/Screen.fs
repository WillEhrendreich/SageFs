namespace SageFs

open System
open SageFs.Features.LiveTesting

/// Helper functions for rendering gutter annotations in panes.
module GutterRender =
  /// Gutter width: 0 if no annotations, 2 if any exist.
  let gutterWidth (annotations: LineAnnotation array) : int =
    match annotations.Length = 0 with | true -> 0 | false -> 2

  /// Build a lookup from line number to annotation for O(1) access.
  let buildLookup (annotations: LineAnnotation array) : Map<int, LineAnnotation> =
    annotations |> Array.fold (fun m a -> Map.add a.Line a m) Map.empty

  /// Map GutterIcon to a theme foreground color.
  let iconFgColor (theme: ThemeConfig) (icon: GutterIcon) : uint32 =
    match icon with
    | GutterIcon.TestPassed -> Theme.hexToRgb theme.ColorPass
    | GutterIcon.TestFailed -> Theme.hexToRgb theme.ColorFail
    | GutterIcon.TestDiscovered -> Theme.hexToRgb theme.ColorWarn
    | GutterIcon.TestRunning -> Theme.hexToRgb theme.ColorInfo
    | GutterIcon.TestSkipped -> Theme.hexToRgb theme.FgDim
    | GutterIcon.TestFlaky -> Theme.hexToRgb theme.ColorWarn
    | GutterIcon.Covered -> Theme.hexToRgb theme.ColorPass
    | GutterIcon.NotCovered -> Theme.hexToRgb theme.FgDim

/// Generate context-sensitive status bar hints from the active KeyMap.
module StatusHints =
  /// Short label for a key combo (compact for status bar)
  let shortFormat (kc: KeyCombo) : string =
    let parts = ResizeArray<string>()
    match kc.Modifiers.HasFlag(ConsoleModifiers.Control) with | true -> parts.Add("^") | false -> ()
    match kc.Modifiers.HasFlag(ConsoleModifiers.Alt) with | true -> parts.Add("A-") | false -> ()
    match kc.Modifiers.HasFlag(ConsoleModifiers.Shift) with | true -> parts.Add("S-") | false -> ()
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
      match layout.Contains PaneId.Editor with
      | true -> hint (UiAction.TogglePane "Editor") "hide-editor"
      | false -> hint (UiAction.TogglePane "Editor") "show-editor"
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
      | PaneId.Output | PaneId.Diagnostics | PaneId.Context ->
        [ hint UiAction.ScrollDown "scroll↓" ]
        |> List.choose id
      | PaneId.Sessions ->
        [ editorHint (EditorAction.CreateSession []) "new-session" ]
        |> List.choose id
    let all = paneHints @ common
    match all.IsEmpty with
    | true -> ""
    | false -> sprintf " %s " (String.concat " | " all)

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
    match cfg.VisiblePanes.Contains paneId with
    | true -> { cfg with VisiblePanes = Set.remove paneId cfg.VisiblePanes }
    | false -> { cfg with VisiblePanes = Set.add paneId cfg.VisiblePanes }

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
      cfg.VisiblePanes.Contains PaneId.Sessions || cfg.VisiblePanes.Contains PaneId.Diagnostics || cfg.VisiblePanes.Contains PaneId.Context
    let panes = ResizeArray<PaneId * Rect>()
    match hasLeft, hasRight with
    | true, true ->
      let left, right = Rect.splitVProp cfg.LeftRightSplit contentArea
      // Left column
      match cfg.VisiblePanes.Contains PaneId.Output, cfg.VisiblePanes.Contains PaneId.Editor with
      | true, true ->
        let outputRect, editorRect = Rect.splitH (left.Height - cfg.OutputEditorSplit) left
        panes.Add(PaneId.Output, outputRect)
        panes.Add(PaneId.Editor, editorRect)
      | true, false ->
        panes.Add(PaneId.Output, left)
      | false, true ->
        panes.Add(PaneId.Editor, left)
      | false, false -> ()
      // Right column — split evenly among visible right-column panes
      let rightPanes =
        [PaneId.Sessions; PaneId.Context; PaneId.Diagnostics]
        |> List.filter cfg.VisiblePanes.Contains
      match rightPanes with
      | [single] -> panes.Add(single, right)
      | [top; bottom] ->
        let topRect, bottomRect = Rect.splitHProp cfg.SessionsDiagSplit right
        panes.Add(top, topRect)
        panes.Add(bottom, bottomRect)
      | many ->
        let count = many.Length
        let h = right.Height / count
        many |> List.iteri (fun i pid ->
          let isLast = i = count - 1
          let rowOff = right.Row + i * h
          let height = match isLast with | true -> right.Height - i * h | false -> h
          panes.Add(pid, Rect.create rowOff right.Col right.Width height))
    | true, false ->
      match cfg.VisiblePanes.Contains PaneId.Output, cfg.VisiblePanes.Contains PaneId.Editor with
      | true, true ->
        let outputRect, editorRect = Rect.splitH (contentArea.Height - cfg.OutputEditorSplit) contentArea
        panes.Add(PaneId.Output, outputRect)
        panes.Add(PaneId.Editor, editorRect)
      | true, false ->
        panes.Add(PaneId.Output, contentArea)
      | false, true ->
        panes.Add(PaneId.Editor, contentArea)
      | false, false -> ()
    | false, true ->
      let rightPanes =
        [PaneId.Sessions; PaneId.Context; PaneId.Diagnostics]
        |> List.filter cfg.VisiblePanes.Contains
      match rightPanes with
      | [single] -> panes.Add(single, contentArea)
      | [top; bottom] ->
        let topRect, bottomRect = Rect.splitHProp cfg.SessionsDiagSplit contentArea
        panes.Add(top, topRect)
        panes.Add(bottom, bottomRect)
      | many ->
        let count = many.Length
        let h = contentArea.Height / count
        many |> List.iteri (fun i pid ->
          let isLast = i = count - 1
          let rowOff = contentArea.Row + i * h
          let height = match isLast with | true -> contentArea.Height - i * h | false -> h
          panes.Add(pid, Rect.create rowOff contentArea.Col contentArea.Width height))
    | false, false -> ()
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
        match paneId = focusedPane with | true -> Theme.hexToRgb theme.BorderFocus | false -> Theme.hexToRgb theme.BorderNormal
      let bg =
        match paneId = PaneId.Editor with | true -> Theme.hexToRgb theme.BgEditor | false -> Theme.hexToRgb theme.BgPanel
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

        // Gutter annotations (test status / coverage icons)
        let gw = GutterRender.gutterWidth region.LineAnnotations
        let annotationLookup =
          match gw > 0 with
          | true -> GutterRender.buildLookup region.LineAnnotations
          | false -> Map.empty

        // Apply syntax highlighting for editor and output panes
        let shouldHighlight =
          paneId = PaneId.Editor || paneId = PaneId.Output
        let allSpans =
          match shouldHighlight && SyntaxHighlight.isAvailable () with
          | true -> SyntaxHighlight.tokenize theme region.Content
          | false -> [||]
        let spanOffset = skip

        visibleLines |> Array.iteri (fun row line ->
          let lineIdx = spanOffset + row
          // Draw gutter icon if annotations exist
          match gw > 0 with
          | true ->
            match Map.tryFind lineIdx annotationLookup with
            | Some ann ->
              let iconFg = GutterRender.iconFgColor theme ann.Icon
              Draw.text inner row 0 iconFg bg CellAttrs.None (sprintf "%c " (GutterIcon.toChar ann.Icon))
            | None ->
              Draw.text inner row 0 (Theme.hexToRgb theme.FgDim) bg CellAttrs.None "  "
          | false -> ()
          // Draw text content offset by gutter width
          match shouldHighlight && lineIdx < allSpans.Length && allSpans.[lineIdx].Length > 0 with
          | true -> Draw.textHighlighted inner row gw fg bg CellAttrs.None allSpans.[lineIdx] line
          | false -> Draw.text inner row gw fg bg CellAttrs.None line)

        // Scroll indicators
        match skip > 0 with
        | true -> Draw.text inner 0 (inner.Clip.Width - 1) (Theme.hexToRgb theme.FgDim) bg CellAttrs.None "▲"
        | false -> ()
        match lines.Length > skip + inner.Clip.Height with
        | true -> Draw.text inner (inner.Clip.Height - 1) (inner.Clip.Width - 1) (Theme.hexToRgb theme.FgDim) bg CellAttrs.None "▼"
        | false -> ()

        // Track cursor for focused pane (offset by gutter width)
        match paneId = focusedPane with
        | true ->
          match region.Cursor with
          | Some c ->
            let screenRow = rect.Row + 1 + c.Line
            let screenCol = rect.Col + 1 + c.Col + gw
            cursorPos <- Some (screenRow, screenCol)
          | None ->
            cursorPos <- Some (rect.Row + 1, rect.Col + 1 + gw)
        | false -> ()

        // Completion popup overlay (offset by gutter width)
        match region.Completions with
        | Some compl when compl.Items.Length > 0 ->
          let cursorScreenRow =
            match region.Cursor with
            | Some c -> rect.Row + 1 + c.Line
            | None -> rect.Row + 1
          let cursorScreenCol =
            match region.Cursor with
            | Some c -> rect.Col + 1 + c.Col + gw
            | None -> rect.Col + 1 + gw
          let popupRow = cursorScreenRow + 1
          let popupCol = cursorScreenCol
          let maxVisible = min 8 compl.Items.Length
          let menuWidth = (compl.Items |> List.map (fun s -> s.Length) |> List.max) + 2
          for i in 0 .. maxVisible - 1 do
            let r = popupRow + i
            let c = popupCol
            match r < rows - 1 && c + menuWidth < cols with
            | true ->
              let isSelected = i = compl.SelectedIndex
              let itemFg = match isSelected with | true -> Theme.hexToRgb theme.BgEditor | false -> Theme.hexToRgb theme.FgDefault
              let itemBg = match isSelected with | true -> Theme.hexToRgb theme.BorderFocus | false -> Theme.hexToRgb theme.BgStatus
              let label = compl.Items.[i].PadRight(menuWidth)
              let itemDt = DrawTarget.create grid (Rect.create r c menuWidth 1)
              Draw.text itemDt 0 0 itemFg itemBg CellAttrs.None (sprintf " %s" label)
            | false -> ()
        | _ -> ()
      | None ->
        // Default cursor at content start for focused pane
        match paneId = focusedPane with
        | true -> cursorPos <- Some (rect.Row + 1, rect.Col + 1)
        | false -> ()

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
