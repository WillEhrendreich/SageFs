namespace SageFs

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

        // Track cursor for focused pane
        if paneId = focusedPane then
          match region.Cursor with
          | Some c ->
            let screenRow = rect.Row + 1 + c.Line
            let screenCol = rect.Col + 1 + c.Col
            cursorPos <- Some (screenRow, screenCol)
          | None ->
            cursorPos <- Some (rect.Row + 1, rect.Col + 1)
      | None ->
        // Default cursor at content start for focused pane
        if paneId = focusedPane then
          cursorPos <- Some (rect.Row + 1, rect.Col + 1)

    // Status bar
    Draw.statusBar dt statusLeft statusRight Theme.fgDefault Theme.bgStatus

    cursorPos
