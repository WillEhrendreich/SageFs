namespace SageFs.Features.LiveTesting
open System
open System.IO
open System.Numerics
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open SageFs.Measures

// Split from LiveTestingTypes.fs (file-size ratchet): the dashboard treemap projection is its own slice.
module TestTreemap =
  /// Extract treemap entries from test status entries.
  /// Only includes tests with known durations (Passed/Failed).
  let fromStatusEntries (entries: TestStatusEntry array) : TestTreemapEntry array =
    entries
    |> Array.choose (fun e ->
      match e.Status with
      | TestRunStatus.Passed duration ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = duration.TotalMilliseconds; Status = TreemapStatus.Passed }
      | TestRunStatus.Failed (_, duration) ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = duration.TotalMilliseconds; Status = TreemapStatus.Failed }
      | TestRunStatus.Running ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = SageFs.Timeouts.notRun.TotalMilliseconds; Status = TreemapStatus.Running }
      | TestRunStatus.Skipped _ ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = SageFs.Timeouts.notRun.TotalMilliseconds; Status = TreemapStatus.Skipped }
      | _ -> None)
    |> Array.sortByDescending (fun e -> e.DurationMs)

  /// Squarified treemap layout algorithm (Bruls, Huizing, van Wijk).
  /// Packs rectangles into a bounding box with near-square aspect ratios.
  /// Area of each rect is proportional to DurationMs.
  ///
  /// NOTE: this is area-proportional, so it degrades badly past a few hundred
  /// tests — at 14k it renders ~2px cells (see `TestStatusGrid` below, which is
  /// what the dashboard actually uses now). Kept because it is the right layout
  /// for the small runs it is still asked for and it has its own tests.
  let layout (width: float) (height: float) (entries: TestTreemapEntry array) : TreemapRect array =
    match entries.Length with
    | 0 -> [||]
    | _ ->
      let totalMs = entries |> Array.sumBy (fun e -> max e.DurationMs 0.1)
      let totalArea = width * height
      // Normalize: each entry's area = (durationMs / totalMs) * totalArea
      let areas = entries |> Array.map (fun e -> max e.DurationMs 0.1 / totalMs * totalArea)
      let result = ResizeArray<TreemapRect>()
      let mutable x0 = 0.0
      let mutable y0 = 0.0
      let mutable w0 = width
      let mutable h0 = height
      let mutable idx = 0
      while idx < entries.Length do
        // Lay out a strip along the shorter side
        let isVertical = w0 >= h0
        let side = match isVertical with | true -> h0 | false -> w0
        // Greedily add entries to the current strip while aspect ratio improves
        let mutable stripArea = 0.0
        let mutable bestWorst = System.Double.MaxValue
        let mutable stripEnd = idx
        let mutable improving = true
        while stripEnd < entries.Length && improving do
          stripArea <- stripArea + areas.[stripEnd]
          let stripLen = stripArea / side
          // Worst aspect ratio in this strip
          let worstAspect =
            let mutable worst = 0.0
            for j in idx .. stripEnd do
              let cellLen = areas.[j] / stripLen
              let aspect = max (cellLen / stripLen) (stripLen / cellLen)
              worst <- max worst aspect
            worst
          match worstAspect <= bestWorst with
          | true ->
            bestWorst <- worstAspect
            stripEnd <- stripEnd + 1
          | false ->
            // Adding this entry made it worse — back off
            stripArea <- stripArea - areas.[stripEnd]
            improving <- false
        // If we added nothing (shouldn't happen, but safety), take one
        let stripEnd = match stripEnd = idx with | true -> idx + 1 | false -> stripEnd
        let stripLen = stripArea / side
        // Place entries in the strip
        let mutable offset = 0.0
        for j in idx .. stripEnd - 1 do
          let cellLen = areas.[j] / stripLen
          match isVertical with
          | true ->
            result.Add { Entry = entries.[j]; X = x0; Y = y0 + offset; W = stripLen; H = cellLen }
          | false ->
            result.Add { Entry = entries.[j]; X = x0 + offset; Y = y0; W = cellLen; H = stripLen }
          offset <- offset + cellLen
        // Shrink remaining area
        match isVertical with
        | true ->
          x0 <- x0 + stripLen
          w0 <- w0 - stripLen
        | false ->
          y0 <- y0 + stripLen
          h0 <- h0 - stripLen
        idx <- stripEnd
      result.ToArray()

/// One cell of the dashboard's per-test status grid. A cell is a fixed-size tile
/// carrying one test's status — deliberately NOT area-weighted, because the
/// status panel's job is "which tests passed/failed/never ran", not "which are
/// slow". Duration is still carried on `Entry` for the cell's tooltip.
type StatusGridCell = {
  /// Position in worst-first order, which equals `Row * Columns + Column`.
  Index: int
  Column: int
  Row: int
  Entry: TestTreemapEntry
}

/// A laid-out status grid. `Cells.Length` always equals the input length: a
/// session with 14,360 tests gets 14,360 cells, each at least
/// `TestStatusGrid.MinCellPx` on a side.
type StatusGridLayout = {
  Columns: int
  Rows: int
  /// The uniform size of every cell, in px. Never below the floor, never above the cap.
  CellPx: float
  Width: float
  Height: float
  Cells: StatusGridCell array
}

/// The dashboard's per-test status grid: the legible replacement for the
/// duration-proportional treemap at scale.
///
/// `layout` above makes every rectangle's AREA proportional to its duration. That
/// is a good layout for a handful of slow tests and useless for a whole suite:
/// the panel's box is fixed (320x180), so area, and therefore cell size, shrinks
/// as 1/sqrt(n). A 14,360-test session got ~2x2px cells — a moire of specks
/// where no individual test is distinguishable — and, because `fromStatusEntries`
/// records Running/Skipped as `Timeouts.notRun` (TimeSpan.Zero), the untimed
/// majority all carried identical area, so 99.5% of the picture encoded nothing
/// at all.
///
/// This grid gives every test the SAME square cell, sized between a legibility
/// floor and a cap, packed left-to-right into as many columns as the floor
/// allows. A cell is never a speck, and status is per-cell rather than
/// per-area, so a test that never ran is as visible as one that passed.
module TestStatusGrid =

  /// The smallest a cell may render, in px. Below roughly this a cell stops
  /// reading as a cell and becomes noise, which is the defect being fixed.
  [<Literal>]
  let MinCellPx = 3.0

  /// The largest a cell may render, in px, so a small run is a tidy grid of
  /// chips rather than one enormous block swallowing the panel.
  [<Literal>]
  let MaxCellPx = 12.0

  /// Worst first, so a failure lands at the very first cell and is never buried
  /// under thousands of not-run cells. Order among equals is stable.
  let private rank (status: TreemapStatus) =
    match status with
    | TreemapStatus.Failed -> 0
    | TreemapStatus.Running -> 1
    | TreemapStatus.Other -> 2
    | TreemapStatus.Skipped -> 3
    | TreemapStatus.Passed -> 4

  /// Lay `entries` out as a uniform status grid no wider than `maxWidthPx`.
  /// Returns `None` for an empty run (there is nothing to draw). Total height
  /// grows with the count; callers scroll or cap the panel.
  let layout (maxWidthPx: float) (entries: TestTreemapEntry array) : StatusGridLayout option =
    match entries.Length with
    | 0 -> None
    | n ->
      let usableWidth = max MinCellPx maxWidthPx
      // Aim for a near-square grid. When that would push cells under the floor,
      // fall back to as many columns as the floor allows and let the grid grow
      // taller — legibility outranks squareness.
      let idealColumns = max 1 (int (ceil (sqrt (float n))))
      let columns, cellPx =
        if usableWidth / float idealColumns >= MinCellPx then
          let capped = max 1 (int (floor (usableWidth / MaxCellPx)))
          let c = max 1 (min capped idealColumns)
          c, max MinCellPx (min MaxCellPx (usableWidth / float c))
        else
          // Floor-bound: spend the whole width on as many floor-sized cells as
          // fit. `floor` here is deliberate — it leaves the leftover sub-floor
          // pixel as slack rather than stretching the cells above the floor.
          let c = max 1 (int (floor (usableWidth / MinCellPx)))
          c, max MinCellPx (usableWidth / float c)
      let rows = int (ceil (float n / float columns))
      let cells =
        entries
        |> Array.sortBy (fun e -> rank e.Status)
        |> Array.mapi (fun i e ->
          { Index = i; Column = i % columns; Row = i / columns; Entry = e })
      Some { Columns = columns
             Rows = rows
             CellPx = cellPx
             Width = float columns * cellPx
             Height = float rows * cellPx
             Cells = cells }

  /// The total duration of the timed tests, in ms — what the panel's summary
  /// header shows. Untimed entries (Running/Skipped, recorded as
  /// `Timeouts.notRun`) contribute nothing.
  let totalDurationMs (entries: TestTreemapEntry array) =
    entries |> Array.sumBy (fun e -> max 0.0 e.DurationMs)

