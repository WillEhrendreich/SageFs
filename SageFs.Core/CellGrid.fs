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

/// The cell grid — a 2D array of cells with bounds-checked operations.
module CellGrid =
  let create (rows: int) (cols: int) : Cell[,] =
    Array2D.create rows cols Cell.empty

  let rows (grid: Cell[,]) = Array2D.length1 grid
  let cols (grid: Cell[,]) = Array2D.length2 grid

  let inBounds (grid: Cell[,]) row col =
    row >= 0 && row < rows grid && col >= 0 && col < cols grid

  let set (grid: Cell[,]) row col (cell: Cell) =
    if inBounds grid row col then
      grid.[row, col] <- cell

  let get (grid: Cell[,]) row col =
    if inBounds grid row col then grid.[row, col]
    else Cell.empty

  let clear (grid: Cell[,]) =
    let r = rows grid
    let c = cols grid
    for row in 0 .. r - 1 do
      for col in 0 .. c - 1 do
        grid.[row, col] <- Cell.empty

  let writeString (grid: Cell[,]) row col fg bg attrs (s: string) =
    let mutable c = col
    for i in 0 .. s.Length - 1 do
      if inBounds grid row c then
        grid.[row, c] <- { Char = s.[i]; Fg = fg; Bg = bg; Attrs = attrs }
      c <- c + 1

  let fillRect (grid: Cell[,]) (r: Rect) (cell: Cell) =
    for row in r.Row .. r.Row + r.Height - 1 do
      for col in r.Col .. r.Col + r.Width - 1 do
        if inBounds grid row col then
          grid.[row, col] <- cell

  let toText (grid: Cell[,]) : string =
    let sb = System.Text.StringBuilder(rows grid * (cols grid + 1))
    for row in 0 .. rows grid - 1 do
      for col in 0 .. cols grid - 1 do
        sb.Append(grid.[row, col].Char) |> ignore
      if row < rows grid - 1 then
        sb.AppendLine() |> ignore
    sb.ToString()

  let toTextTrimmed (grid: Cell[,]) : string =
    let sb = System.Text.StringBuilder(rows grid * (cols grid + 1))
    for row in 0 .. rows grid - 1 do
      let mutable lastNonSpace = -1
      for col in 0 .. cols grid - 1 do
        if grid.[row, col].Char <> ' ' then
          lastNonSpace <- col
      for col in 0 .. lastNonSpace do
        sb.Append(grid.[row, col].Char) |> ignore
      if row < rows grid - 1 then
        sb.AppendLine() |> ignore
    sb.ToString()

  /// Extract text from a rectangular selection range (inclusive).
  /// Coordinates are clamped to grid bounds. Lines are right-trimmed.
  let toTextRange (grid: Cell[,]) (startRow: int) (startCol: int) (endRow: int) (endCol: int) : string =
    let r = rows grid
    let c = cols grid
    let sr = max 0 (min startRow endRow)
    let er = min (r - 1) (max startRow endRow)
    let sc = max 0 (min startCol endCol)
    let ec = min (c - 1) (max startCol endCol)
    let sb = System.Text.StringBuilder()
    for row in sr .. er do
      let mutable lastNonSpace = sc - 1
      for col in sc .. ec do
        if grid.[row, col].Char <> ' ' then
          lastNonSpace <- col
      for col in sc .. lastNonSpace do
        sb.Append(grid.[row, col].Char) |> ignore
      if row < er then
        sb.AppendLine() |> ignore
    sb.ToString()
