namespace SageFs

/// A draw target — bundles a grid with a clipping rect.
type DrawTarget = {
  Grid: Cell[,]
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
      if c < maxCol && absRow >= dt.Clip.Row && absRow < Rect.bottom dt.Clip then
        CellGrid.set dt.Grid absRow c { Char = s.[i]; Fg = fg; Bg = bg; Attrs = attrs }
      c <- c + 1

  /// Write text with per-character syntax highlighting from ColorSpan overlays.
  /// Characters not covered by any span use the default fg color.
  let textHighlighted (dt: DrawTarget) (row: int) (col: int) (defaultFg: uint32) (bg: uint32) (attrs: CellAttrs) (spans: ColorSpan array) (s: string) =
    let absRow = dt.Clip.Row + row
    let absCol = dt.Clip.Col + col
    let maxCol = Rect.right dt.Clip
    if absRow < dt.Clip.Row || absRow >= Rect.bottom dt.Clip then () else
    // Build per-character fg array
    let len = s.Length
    let fgs = Array.create len defaultFg
    for span in spans do
      let stop = min len (span.Start + span.Length)
      for i in span.Start .. stop - 1 do
        fgs.[i] <- span.Fg
    let mutable c = absCol
    for i in 0 .. len - 1 do
      if c < maxCol then
        CellGrid.set dt.Grid absRow c { Char = s.[i]; Fg = fgs.[i]; Bg = bg; Attrs = attrs }
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
    if r.Width < 2 || r.Height < 2 then dt
    else
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
      if title.Length > 0 && r.Width > 4 then
        let maxTitleLen = r.Width - 4
        let t = if title.Length > maxTitleLen then title.Substring(0, maxTitleLen) else title
        text dt 0 2 (Theme.hexToRgb Theme.fgDefault) borderBg CellAttrs.None (sprintf " %s " t)
      DrawTarget.sub dt (Rect.create (r.Row + 1) (r.Col + 1) (r.Width - 2) (r.Height - 2))

  let scrolledLines (dt: DrawTarget) (lines: string list) (scrollOffset: int) (fg: uint32) (bg: uint32) =
    let visibleRows = dt.Clip.Height
    let startLine = max 0 scrollOffset
    for i in 0 .. visibleRows - 1 do
      let lineIdx = startLine + i
      if lineIdx < lines.Length then
        let line = lines.[lineIdx]
        let maxLen = min line.Length dt.Clip.Width
        text dt i 0 fg bg CellAttrs.None (if maxLen < line.Length then line.Substring(0, maxLen) else line)

  let statusBar (dt: DrawTarget) (left: string) (right: string) (fg: uint32) (bg: uint32) =
    let row = dt.Clip.Height - 1
    hline dt row fg bg ' '
    text dt row 0 fg bg CellAttrs.None left
    let rightCol = dt.Clip.Width - right.Length
    if rightCol > left.Length then
      text dt row rightCol fg bg CellAttrs.None right
