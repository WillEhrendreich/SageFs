namespace SageFs

open System

/// Attribute flags for a cell (bitfield for compact struct)
[<Flags>]
type CellAttrs =
  | None    = 0uy
  | Bold    = 1uy
  | Dim     = 2uy
  | Inverse = 4uy

/// A single cell in the rendering grid. Struct for cache-friendly layout.
/// Fg/Bg are packed RGB (0x00RRGGBB).
[<Struct>]
type Cell = {
  Char: char
  Fg: uint32
  Bg: uint32
  Attrs: CellAttrs
}

module Cell =
  let empty = { Char = ' '; Fg = 0x00FFFFFFu; Bg = 0u; Attrs = CellAttrs.None }
  let create ch fg bg attrs = { Char = ch; Fg = fg; Bg = bg; Attrs = attrs }

/// A rectangle in the grid. Smart constructor clamps to non-negative.
[<Struct>]
type Rect = {
  Row: int
  Col: int
  Width: int
  Height: int
}

module Rect =
  let create row col width height =
    { Row = max 0 row
      Col = max 0 col
      Width = max 0 width
      Height = max 0 height }

  let isEmpty r = r.Width <= 0 || r.Height <= 0
  let right r = r.Col + r.Width
  let bottom r = r.Row + r.Height

  let inset margin r =
    create (r.Row + margin) (r.Col + margin) (r.Width - margin * 2) (r.Height - margin * 2)

  let splitH (topH: int) (r: Rect) =
    let topH = topH |> max 0 |> min r.Height
    let top = create r.Row r.Col r.Width topH
    let bot = create (r.Row + topH) r.Col r.Width (r.Height - topH)
    top, bot

  let splitV (leftW: int) (r: Rect) =
    let leftW = leftW |> max 0 |> min r.Width
    let left = create r.Row r.Col leftW r.Height
    let right = create r.Row (r.Col + leftW) (r.Width - leftW) r.Height
    left, right

  let splitHProp (frac: float) (r: Rect) =
    let topH = int (float r.Height * frac)
    splitH topH r

  let splitVProp (frac: float) (r: Rect) =
    let leftW = int (float r.Width * frac)
    splitV leftW r

/// The cell grid — wraps a 1D array with row/col dimensions for cache-friendly operations.
/// Uses Array.Fill for bulk clears/fills (7× faster than per-cell Array2D loops).
type CellGrid = {
  Cells: Cell[]
  Rows: int
  Cols: int
}

module CellGrid =
  let create (rows: int) (cols: int) : CellGrid =
    { Cells = Array.create (rows * cols) Cell.empty
      Rows = rows
      Cols = cols }

  let rows (grid: CellGrid) = grid.Rows
  let cols (grid: CellGrid) = grid.Cols

  let inline idx (grid: CellGrid) row col = row * grid.Cols + col

  let inBounds (grid: CellGrid) row col =
    row >= 0 && row < grid.Rows && col >= 0 && col < grid.Cols

  let set (grid: CellGrid) row col (cell: Cell) =
    if inBounds grid row col then
      grid.Cells.[idx grid row col] <- cell

  /// Unchecked set — caller must guarantee row/col are in bounds.
  let inline setUnsafe (grid: CellGrid) row col (cell: Cell) =
    grid.Cells.[row * grid.Cols + col] <- cell

  let get (grid: CellGrid) row col =
    if inBounds grid row col then grid.Cells.[idx grid row col]
    else Cell.empty

  let clear (grid: CellGrid) =
    Array.Fill(grid.Cells, Cell.empty)

  let clone (grid: CellGrid) : CellGrid =
    { Cells = Array.copy grid.Cells; Rows = grid.Rows; Cols = grid.Cols }

  let writeString (grid: CellGrid) row col fg bg attrs (s: string) =
    let maxCol = grid.Cols
    let mutable c = col
    for i in 0 .. s.Length - 1 do
      if c >= 0 && c < maxCol && row >= 0 && row < grid.Rows then
        grid.Cells.[row * maxCol + c] <- { Char = s.[i]; Fg = fg; Bg = bg; Attrs = attrs }
      c <- c + 1

  let fillRect (grid: CellGrid) (r: Rect) (cell: Cell) =
    let gridCols = grid.Cols
    let gridRows = grid.Rows
    let startRow = max 0 r.Row
    let endRow = min gridRows (r.Row + r.Height)
    let startCol = max 0 r.Col
    let endCol = min gridCols (r.Col + r.Width)
    let fillWidth = endCol - startCol
    if fillWidth > 0 then
      for row in startRow .. endRow - 1 do
        Array.Fill(grid.Cells, cell, row * gridCols + startCol, fillWidth)

  let toText (grid: CellGrid) : string =
    let sb = System.Text.StringBuilder(grid.Rows * (grid.Cols + 1))
    for row in 0 .. grid.Rows - 1 do
      let rowBase = row * grid.Cols
      for col in 0 .. grid.Cols - 1 do
        sb.Append(grid.Cells.[rowBase + col].Char) |> ignore
      if row < grid.Rows - 1 then
        sb.AppendLine() |> ignore
    sb.ToString()

  let toTextTrimmed (grid: CellGrid) : string =
    let sb = System.Text.StringBuilder(grid.Rows * (grid.Cols + 1))
    for row in 0 .. grid.Rows - 1 do
      let rowBase = row * grid.Cols
      let mutable lastNonSpace = -1
      for col in 0 .. grid.Cols - 1 do
        if grid.Cells.[rowBase + col].Char <> ' ' then
          lastNonSpace <- col
      for col in 0 .. lastNonSpace do
        sb.Append(grid.Cells.[rowBase + col].Char) |> ignore
      if row < grid.Rows - 1 then
        sb.AppendLine() |> ignore
    sb.ToString()

  /// Extract text from a rectangular selection range (inclusive).
  /// Coordinates are clamped to grid bounds. Lines are right-trimmed.
  let toTextRange (grid: CellGrid) (startRow: int) (startCol: int) (endRow: int) (endCol: int) : string =
    let sr = max 0 (min startRow endRow)
    let er = min (grid.Rows - 1) (max startRow endRow)
    let sc = max 0 (min startCol endCol)
    let ec = min (grid.Cols - 1) (max startCol endCol)
    let sb = System.Text.StringBuilder()
    for row in sr .. er do
      let rowBase = row * grid.Cols
      let mutable lastNonSpace = sc - 1
      for col in sc .. ec do
        if grid.Cells.[rowBase + col].Char <> ' ' then
          lastNonSpace <- col
      for col in sc .. lastNonSpace do
        sb.Append(grid.Cells.[rowBase + col].Char) |> ignore
      if row < er then
        sb.AppendLine() |> ignore
    sb.ToString()

/// Double-buffered grid pair — swap instead of clone each frame. Zero per-frame allocation.
type DoubleBuffer = {
  mutable Front: CellGrid
  mutable Back: CellGrid
}

module DoubleBuffer =
  let create rows cols =
    { Front = CellGrid.create rows cols
      Back = CellGrid.create rows cols }

  /// Swap front↔back. Returns the new render target (Back).
  let swap (db: DoubleBuffer) =
    let tmp = db.Front
    db.Front <- db.Back
    db.Back <- tmp
    db.Back

  let clearBack (db: DoubleBuffer) =
    CellGrid.clear db.Back
