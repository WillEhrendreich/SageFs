namespace SageFs

/// A draw target — bundles a grid with a clipping rect.
type DrawTarget = {
  Grid: CellGrid
  Clip: Rect
}

module DrawTarget =
  let create grid rect = { Grid = grid; Clip = rect }

  let sub (target: DrawTarget) (rect: Rect) =
    let row = max target.Clip.Row rect.Row
    let col = max target.Clip.Col rect.Col
    let r2 = min (Rect.right target.Clip) (Rect.right rect)
    let b2 = min (Rect.bottom target.Clip) (Rect.bottom rect)
    { Grid = target.Grid
      Clip = Rect.create row col (max 0 (r2 - col)) (max 0 (b2 - row)) }

/// Immediate-mode draw primitives. All operate on DrawTarget (grid + clip rect).
/// Color parameters are packed RGB uint32 (0x00RRGGBB).
[<RequireQualifiedAccess>]
module rec Draw =
  let text (dt: DrawTarget) (row: int) (col: int) (fg: uint32) (bg: uint32) (attrs: CellAttrs) (s: string) =
    let absRow = dt.Clip.Row + row
    let absCol = dt.Clip.Col + col
    let maxCol = Rect.right dt.Clip
    let mutable c = absCol
    for i in 0 .. s.Length - 1 do
      match c < maxCol && absRow >= dt.Clip.Row && absRow < Rect.bottom dt.Clip with
      | true -> CellGrid.set dt.Grid absRow c { Char = s.[i]; Fg = fg; Bg = bg; Attrs = attrs }
      | false -> ()
      c <- c + 1

  /// Write text with per-character syntax highlighting from ColorSpan overlays.
  /// Characters not covered by any span use the default fg color.
  /// Spans are assumed sorted by Start and non-overlapping.
  let textHighlighted (dt: DrawTarget) (row: int) (col: int) (defaultFg: uint32) (bg: uint32) (attrs: CellAttrs) (spans: ColorSpan array) (s: string) =
    let absRow = dt.Clip.Row + row
    let absCol = dt.Clip.Col + col
    let maxCol = Rect.right dt.Clip
    match absRow < dt.Clip.Row || absRow >= Rect.bottom dt.Clip with
    | true -> ()
    | false ->
    let mutable c = absCol
    let mutable spanIdx = 0
    let mutable spanEnd = match spans.Length > 0 with | true -> spans.[0].Start + spans.[0].Length | false -> 0
    for i in 0 .. s.Length - 1 do
      match c < maxCol with
      | true ->
        while spanIdx < spans.Length && i >= spans.[spanIdx].Start + spans.[spanIdx].Length do
          spanIdx <- spanIdx + 1
          match spanIdx < spans.Length with
          | true -> spanEnd <- spans.[spanIdx].Start + spans.[spanIdx].Length
          | false -> ()
        let fg =
          match spanIdx < spans.Length && i >= spans.[spanIdx].Start && i < spanEnd with
          | true -> spans.[spanIdx].Fg
          | false -> defaultFg
        CellGrid.set dt.Grid absRow c { Char = s.[i]; Fg = fg; Bg = bg; Attrs = attrs }
      | false -> ()
      c <- c + 1

  let fill (dt: DrawTarget) (bg: uint32) =
    let cell = { Cell.empty with Bg = bg }
    CellGrid.fillRect dt.Grid dt.Clip cell

  let hline (dt: DrawTarget) (row: int) (fg: uint32) (bg: uint32) (ch: char) =
    let absRow = dt.Clip.Row + row
    for col in dt.Clip.Col .. Rect.right dt.Clip - 1 do
      CellGrid.set dt.Grid absRow col { Char = ch; Fg = fg; Bg = bg; Attrs = CellAttrs.None }

  let vline (dt: DrawTarget) (col: int) (fg: uint32) (bg: uint32) (ch: char) =
    let absCol = dt.Clip.Col + col
    for row in dt.Clip.Row .. Rect.bottom dt.Clip - 1 do
      CellGrid.set dt.Grid row absCol { Char = ch; Fg = fg; Bg = bg; Attrs = CellAttrs.None }

  let box (dt: DrawTarget) (title: string) (borderFg: uint32) (borderBg: uint32) : DrawTarget =
    let r = dt.Clip
    match r.Width < 2 || r.Height < 2 with
    | true -> dt
    | false ->
      CellGrid.set dt.Grid r.Row r.Col { Char = '\u250C'; Fg = borderFg; Bg = borderBg; Attrs = CellAttrs.None }
      CellGrid.set dt.Grid r.Row (Rect.right r - 1) { Char = '\u2510'; Fg = borderFg; Bg = borderBg; Attrs = CellAttrs.None }
      CellGrid.set dt.Grid (Rect.bottom r - 1) r.Col { Char = '\u2514'; Fg = borderFg; Bg = borderBg; Attrs = CellAttrs.None }
      CellGrid.set dt.Grid (Rect.bottom r - 1) (Rect.right r - 1) { Char = '\u2518'; Fg = borderFg; Bg = borderBg; Attrs = CellAttrs.None }
      for col in r.Col + 1 .. Rect.right r - 2 do
        CellGrid.set dt.Grid r.Row col { Char = '\u2500'; Fg = borderFg; Bg = borderBg; Attrs = CellAttrs.None }
        CellGrid.set dt.Grid (Rect.bottom r - 1) col { Char = '\u2500'; Fg = borderFg; Bg = borderBg; Attrs = CellAttrs.None }
      for row in r.Row + 1 .. Rect.bottom r - 2 do
        CellGrid.set dt.Grid row r.Col { Char = '\u2502'; Fg = borderFg; Bg = borderBg; Attrs = CellAttrs.None }
        CellGrid.set dt.Grid row (Rect.right r - 1) { Char = '\u2502'; Fg = borderFg; Bg = borderBg; Attrs = CellAttrs.None }
      match title.Length > 0 && r.Width > 4 with
      | true ->
        let maxTitleLen = r.Width - 4
        let t = match title.Length > maxTitleLen with | true -> title.Substring(0, maxTitleLen) | false -> title
        text dt 0 2 (Theme.hexToRgb Theme.fgDefault) borderBg CellAttrs.None (sprintf " %s " t)
      | false -> ()
      DrawTarget.sub dt (Rect.create (r.Row + 1) (r.Col + 1) (r.Width - 2) (r.Height - 2))

  /// Merge stacked box corners into proper T-junctions.
  /// Only adds MISSING vertical connections to corner characters
  /// when another box-drawing character is directly above/below.
  let resolveJunctions (dt: DrawTarget) =
    let r = dt.Clip
    let isBoxChar ch =
      ch = '\u2500' || ch = '\u2502' ||
      ch = '\u250C' || ch = '\u2510' ||
      ch = '\u2514' || ch = '\u2518' ||
      ch = '\u251C' || ch = '\u2524' ||
      ch = '\u252C' || ch = '\u2534' ||
      ch = '\u253C'
    for row in r.Row .. Rect.bottom r - 1 do
      for col in r.Col .. Rect.right r - 1 do
        let cell = CellGrid.get dt.Grid row col
        let above = match row > r.Row with | true -> (CellGrid.get dt.Grid (row - 1) col).Char | false -> ' '
        let below = match row < Rect.bottom r - 1 with | true -> (CellGrid.get dt.Grid (row + 1) col).Char | false -> ' '
        let newChar =
          match cell.Char with
          | '\u2518' when isBoxChar below -> '\u2524' // ┘ + box below → ┤
          | '\u2510' when isBoxChar above -> '\u2524' // ┐ + box above → ┤
          | '\u2514' when isBoxChar below -> '\u251C' // └ + box below → ├
          | '\u250C' when isBoxChar above -> '\u251C' // ┌ + box above → ├
          | _ -> cell.Char
        match newChar <> cell.Char with
        | true -> CellGrid.set dt.Grid row col { cell with Char = newChar }
        | false -> ()

  let scrolledLines (dt: DrawTarget) (lines: string list) (scrollOffset: int) (fg: uint32) (bg: uint32) =
    let visibleRows = dt.Clip.Height
    let startLine = max 0 scrollOffset
    for i in 0 .. visibleRows - 1 do
      let lineIdx = startLine + i
      match lineIdx < lines.Length with
      | true ->
        let line = lines.[lineIdx]
        let maxLen = min line.Length dt.Clip.Width
        text dt i 0 fg bg CellAttrs.None (match maxLen < line.Length with | true -> line.Substring(0, maxLen) | false -> line)
      | false -> ()

  let statusBar (dt: DrawTarget) (left: string) (right: string) (fg: uint32) (bg: uint32) =
    let row = dt.Clip.Height - 1
    hline dt row fg bg ' '
    text dt row 0 fg bg CellAttrs.None left
    let rightCol = dt.Clip.Width - right.Length
    match rightCol > left.Length with
    | true -> text dt row rightCol fg bg CellAttrs.None right
    | false -> ()
