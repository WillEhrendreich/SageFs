module SageFs.Tests.CellGridTests

open Expecto
open SageFs

let cellGridTests = testList "CellGrid" [
  test "create makes grid of empty cells" {
    let grid = CellGrid.create 3 4
    Expect.equal (CellGrid.rows grid) 3 "rows"
    Expect.equal (CellGrid.cols grid) 4 "cols"
    Expect.equal (CellGrid.get grid 0 0) Cell.empty "cell is empty"
  }

  test "set and get round-trip" {
    let grid = CellGrid.create 2 2
    let cell = Cell.create 'X' 10uy 20uy CellAttrs.Bold
    CellGrid.set grid 1 0 cell
    Expect.equal (CellGrid.get grid 1 0) cell "should get what was set"
  }

  test "set out of bounds is no-op" {
    let grid = CellGrid.create 2 2
    CellGrid.set grid 5 5 (Cell.create '!' 0uy 0uy CellAttrs.None)
    Expect.equal (CellGrid.get grid 0 0) Cell.empty "grid unchanged"
  }

  test "get out of bounds returns empty" {
    let grid = CellGrid.create 2 2
    Expect.equal (CellGrid.get grid -1 0) Cell.empty "negative row"
    Expect.equal (CellGrid.get grid 0 99) Cell.empty "col overflow"
  }

  test "writeString writes chars with attributes" {
    let grid = CellGrid.create 1 5
    CellGrid.writeString grid 0 1 10uy 20uy CellAttrs.None "Hi"
    Expect.equal (CellGrid.get grid 0 0) Cell.empty "before string"
    Expect.equal (CellGrid.get grid 0 1).Char 'H' "first char"
    Expect.equal (CellGrid.get grid 0 2).Char 'i' "second char"
    Expect.equal (CellGrid.get grid 0 1).Fg 10uy "fg color"
    Expect.equal (CellGrid.get grid 0 3) Cell.empty "after string"
  }

  test "writeString clips at grid edge" {
    let grid = CellGrid.create 1 3
    CellGrid.writeString grid 0 1 0uy 0uy CellAttrs.None "Hello"
    Expect.equal (CellGrid.get grid 0 1).Char 'H' "first in bounds"
    Expect.equal (CellGrid.get grid 0 2).Char 'e' "second in bounds"
  }

  test "fillRect fills rectangular area" {
    let grid = CellGrid.create 3 4
    let cell = Cell.create '#' 1uy 2uy CellAttrs.None
    CellGrid.fillRect grid (Rect.create 0 1 2 2) cell
    Expect.equal (CellGrid.get grid 0 1).Char '#' "top-left of fill"
    Expect.equal (CellGrid.get grid 1 2).Char '#' "bottom-right of fill"
    Expect.equal (CellGrid.get grid 0 0) Cell.empty "outside fill"
    Expect.equal (CellGrid.get grid 0 3) Cell.empty "outside fill right"
  }

  test "toText produces correct string" {
    let grid = CellGrid.create 2 3
    CellGrid.writeString grid 0 0 0uy 0uy CellAttrs.None "abc"
    CellGrid.writeString grid 1 0 0uy 0uy CellAttrs.None "def"
    let txt = CellGrid.toText grid
    Expect.equal txt "abc\r\ndef" "full grid text"
  }

  test "toTextTrimmed trims trailing spaces" {
    let grid = CellGrid.create 2 5
    CellGrid.writeString grid 0 0 0uy 0uy CellAttrs.None "Hi"
    CellGrid.writeString grid 1 0 0uy 0uy CellAttrs.None "X"
    let txt = CellGrid.toTextTrimmed grid
    Expect.equal txt "Hi\r\nX" "trimmed trailing spaces"
  }

  test "clear resets all cells" {
    let grid = CellGrid.create 2 2
    CellGrid.writeString grid 0 0 10uy 20uy CellAttrs.Bold "AB"
    CellGrid.clear grid
    Expect.equal (CellGrid.get grid 0 0) Cell.empty "cleared"
    Expect.equal (CellGrid.get grid 0 1) Cell.empty "cleared"
  }
]

let rectTests = testList "Rect" [
  test "create clamps negative values" {
    let r = Rect.create -5 -3 -1 -2
    Expect.equal r.Row 0 "row clamped"
    Expect.equal r.Col 0 "col clamped"
    Expect.equal r.Width 0 "width clamped"
    Expect.equal r.Height 0 "height clamped"
  }

  test "isEmpty for zero-size rects" {
    Expect.isTrue (Rect.isEmpty (Rect.create 0 0 0 5)) "zero width"
    Expect.isTrue (Rect.isEmpty (Rect.create 0 0 5 0)) "zero height"
    Expect.isFalse (Rect.isEmpty (Rect.create 0 0 5 5)) "non-empty"
  }

  test "splitH preserves total height" {
    let r = Rect.create 0 0 80 24
    let top, bot = Rect.splitH 10 r
    Expect.equal top.Height 10 "top height"
    Expect.equal bot.Height 14 "bot height"
    Expect.equal top.Row 0 "top starts at 0"
    Expect.equal bot.Row 10 "bot starts at 10"
    Expect.equal top.Width 80 "top width preserved"
    Expect.equal bot.Width 80 "bot width preserved"
  }

  test "splitV preserves total width" {
    let r = Rect.create 0 0 80 24
    let left, right = Rect.splitV 30 r
    Expect.equal left.Width 30 "left width"
    Expect.equal right.Width 50 "right width"
    Expect.equal left.Col 0 "left col"
    Expect.equal right.Col 30 "right col"
  }

  test "splitH clamps to bounds" {
    let r = Rect.create 0 0 80 24
    let top, bot = Rect.splitH 30 r
    Expect.equal top.Height 24 "top clamped to full"
    Expect.equal bot.Height 0 "bot empty"
  }

  test "splitHProp at 0.5 divides evenly" {
    let r = Rect.create 0 0 80 24
    let top, bot = Rect.splitHProp 0.5 r
    Expect.equal top.Height 12 "top half"
    Expect.equal bot.Height 12 "bot half"
  }

  test "splitVProp at 0.65 gives 65% to left" {
    let r = Rect.create 0 0 100 24
    let left, right = Rect.splitVProp 0.65 r
    Expect.equal left.Width 65 "65% left"
    Expect.equal right.Width 35 "35% right"
  }

  test "inset shrinks by margin on all sides" {
    let r = Rect.create 0 0 80 24
    let inner = Rect.inset 1 r
    Expect.equal inner.Row 1 "row shifted"
    Expect.equal inner.Col 1 "col shifted"
    Expect.equal inner.Width 78 "width shrunk"
    Expect.equal inner.Height 22 "height shrunk"
  }

  test "inset too large makes empty rect" {
    let r = Rect.create 0 0 4 4
    let inner = Rect.inset 3 r
    Expect.isTrue (Rect.isEmpty inner) "over-inset is empty"
  }

  test "right and bottom edges" {
    let r = Rect.create 5 10 20 15
    Expect.equal (Rect.right r) 30 "right edge"
    Expect.equal (Rect.bottom r) 20 "bottom edge"
  }
]

let drawTests = testList "Draw" [
  test "text writes at correct position" {
    let grid = CellGrid.create 3 10
    let dt = DrawTarget.create grid (Rect.create 0 0 10 3)
    Draw.text dt 1 2 Theme.fgDefault Theme.bgDefault CellAttrs.None "Hi"
    Expect.equal (CellGrid.get grid 1 2).Char 'H' "H at row=1,col=2"
    Expect.equal (CellGrid.get grid 1 3).Char 'i' "i at row=1,col=3"
    Expect.equal (CellGrid.get grid 0 0) Cell.empty "other cells empty"
  }

  test "text clips at clip boundary" {
    let grid = CellGrid.create 3 10
    let dt = DrawTarget.create grid (Rect.create 0 0 5 3)
    Draw.text dt 0 3 Theme.fgDefault Theme.bgDefault CellAttrs.None "Hello"
    Expect.equal (CellGrid.get grid 0 3).Char 'H' "H in bounds"
    Expect.equal (CellGrid.get grid 0 4).Char 'e' "e in bounds"
    Expect.equal (CellGrid.get grid 0 5) Cell.empty "clipped at 5"
  }

  test "fill sets background on all cells" {
    let grid = CellGrid.create 2 3
    let dt = DrawTarget.create grid (Rect.create 0 0 3 2)
    Draw.fill dt Theme.bgPanel
    Expect.equal (CellGrid.get grid 0 0).Bg Theme.bgPanel "bg set"
    Expect.equal (CellGrid.get grid 1 2).Bg Theme.bgPanel "bg set corner"
  }

  test "box draws border and returns inner target" {
    let grid = CellGrid.create 5 10
    let dt = DrawTarget.create grid (Rect.create 0 0 10 5)
    let inner = Draw.box dt "Test" Theme.borderNormal Theme.bgPanel
    Expect.equal (CellGrid.get grid 0 0).Char '\u250C' "top-left corner"
    Expect.equal (CellGrid.get grid 0 9).Char '\u2510' "top-right corner"
    Expect.equal (CellGrid.get grid 4 0).Char '\u2514' "bottom-left corner"
    Expect.equal (CellGrid.get grid 4 9).Char '\u2518' "bottom-right corner"
    Expect.equal inner.Clip.Row 1 "inner row"
    Expect.equal inner.Clip.Col 1 "inner col"
    Expect.equal inner.Clip.Width 8 "inner width"
    Expect.equal inner.Clip.Height 3 "inner height"
  }

  test "box title appears in top border" {
    let grid = CellGrid.create 5 20
    let dt = DrawTarget.create grid (Rect.create 0 0 20 5)
    let _ = Draw.box dt "Output" Theme.borderNormal Theme.bgPanel
    let txt = CellGrid.toText grid
    Expect.stringContains txt "Output" "title in border"
  }

  test "scrolledLines renders visible lines" {
    let grid = CellGrid.create 3 10
    let dt = DrawTarget.create grid (Rect.create 0 0 10 3)
    let lines = ["line 0"; "line 1"; "line 2"; "line 3"; "line 4"]
    Draw.scrolledLines dt lines 1 Theme.fgDefault Theme.bgDefault
    let txt = CellGrid.toTextTrimmed grid
    Expect.stringContains txt "line 1" "first visible"
    Expect.stringContains txt "line 2" "second visible"
    Expect.stringContains txt "line 3" "third visible"
  }

  test "statusBar shows left and right text" {
    let grid = CellGrid.create 1 20
    let dt = DrawTarget.create grid (Rect.create 0 0 20 1)
    Draw.statusBar dt "Ready" "0.5ms" Theme.fgDefault Theme.bgStatus
    let txt = CellGrid.toText grid
    Expect.stringContains txt "Ready" "left text"
    Expect.stringContains txt "0.5ms" "right text"
  }
]

let ansiEmitterTests = testList "AnsiEmitter" [
  test "uniform color grid emits minimal escape codes" {
    let grid = CellGrid.create 2 3
    let dt = DrawTarget.create grid (Rect.create 0 0 3 2)
    Draw.fill dt Theme.bgPanel
    Draw.text dt 0 0 Theme.fgDefault Theme.bgPanel CellAttrs.None "abc"
    Draw.text dt 1 0 Theme.fgDefault Theme.bgPanel CellAttrs.None "def"
    let output = AnsiEmitter.emitGridOnly grid
    let fgCode = sprintf "\x1b[38;5;%dm" (int Theme.fgDefault)
    let parts = output.Split(fgCode)
    Expect.isLessThanOrEqual parts.Length 2 "fg code emitted at most once"
  }

  test "alternating colors emit codes at transitions" {
    let grid = CellGrid.create 1 4
    CellGrid.set grid 0 0 (Cell.create 'A' 10uy 0uy CellAttrs.None)
    CellGrid.set grid 0 1 (Cell.create 'B' 20uy 0uy CellAttrs.None)
    CellGrid.set grid 0 2 (Cell.create 'C' 10uy 0uy CellAttrs.None)
    CellGrid.set grid 0 3 (Cell.create 'D' 20uy 0uy CellAttrs.None)
    let output = AnsiEmitter.emitGridOnly grid
    Expect.stringContains output "38;5;10" "fg=10 present"
    Expect.stringContains output "38;5;20" "fg=20 present"
  }

  test "emit includes cursor positioning and show/hide" {
    let grid = CellGrid.create 2 2
    let output = AnsiEmitter.emit grid 0 0
    Expect.stringContains output "\x1b[?25l" "hides cursor"
    Expect.stringContains output "\x1b[?25h" "shows cursor at end"
    Expect.stringContains output "\x1b[1;1H" "cursor at 1,1"
  }

  test "bold attribute emitted" {
    let grid = CellGrid.create 1 1
    CellGrid.set grid 0 0 (Cell.create 'X' 255uy 0uy CellAttrs.Bold)
    let output = AnsiEmitter.emitGridOnly grid
    Expect.stringContains output "\x1b[1m" "bold code"
  }

  test "same color adjacent cells emit no extra codes" {
    let grid = CellGrid.create 1 3
    for i in 0 .. 2 do
      CellGrid.set grid 0 i (Cell.create (char (65 + i)) 42uy 0uy CellAttrs.None)
    let output = AnsiEmitter.emitGridOnly grid
    let parts = output.Split("\x1b[38;5;42m")
    Expect.equal parts.Length 2 "exactly one fg=42 code"
  }
]

let performanceTests = testList "Performance" [
  test "CellGrid clear 200x60 under 100µs" {
    let grid = CellGrid.create 60 200
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let iterations = 10000
    for _ in 1 .. iterations do
      CellGrid.clear grid
    sw.Stop()
    let avgUs = sw.Elapsed.TotalMicroseconds / float iterations
    Expect.isLessThan avgUs 100.0 (sprintf "clear: %.1f µs" avgUs)
  }

  test "Draw pipeline 200x60 under 500µs" {
    let grid = CellGrid.create 60 200
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let iterations = 5000
    for _ in 1 .. iterations do
      let dt = DrawTarget.create grid (Rect.create 0 0 200 60)
      Draw.fill dt Theme.bgPanel
      let inner = Draw.box dt "Output" Theme.borderNormal Theme.bgPanel
      Draw.text inner 0 0 Theme.fgDefault Theme.bgPanel CellAttrs.None "Hello, this is a test line of reasonable length"
      Draw.text inner 1 0 Theme.fgGreen Theme.bgPanel CellAttrs.None "[15:30:02 INF] Test passed"
      Draw.text inner 2 0 Theme.fgRed Theme.bgPanel CellAttrs.None "[15:30:02 ERR] Test failed"
    sw.Stop()
    let avgUs = sw.Elapsed.TotalMicroseconds / float iterations
    Expect.isLessThan avgUs 500.0 (sprintf "draw: %.1f µs" avgUs)
  }

  test "AnsiEmitter.emit 200x60 under 2ms" {
    let grid = CellGrid.create 60 200
    let dt = DrawTarget.create grid (Rect.create 0 0 200 60)
    Draw.fill dt Theme.bgPanel
    for row in 0 .. 30 do
      Draw.text dt row 0 Theme.fgDefault Theme.bgPanel CellAttrs.None
        (sprintf "Line %d: some output text with varying content here" row)
    for row in 31 .. 40 do
      for col in 0 .. 99 do
        let fg = if col % 2 = 0 then Theme.fgGreen else Theme.fgRed
        CellGrid.set grid row col (Cell.create 'X' fg Theme.bgPanel CellAttrs.None)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let iterations = 1000
    for _ in 1 .. iterations do
      AnsiEmitter.emit grid 55 5 |> ignore
    sw.Stop()
    let avgUs = sw.Elapsed.TotalMicroseconds / float iterations
    Expect.isLessThan avgUs 2000.0 (sprintf "emit: %.1f µs" avgUs)
  }

  test "Full frame pipeline 200x60 under 6.9ms (144fps)" {
    let grid = CellGrid.create 60 200
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let iterations = 1000
    for _ in 1 .. iterations do
      CellGrid.clear grid
      let dt = DrawTarget.create grid (Rect.create 0 0 200 60)
      Draw.fill dt Theme.bgPanel
      let left, right = Rect.splitVProp 0.65 dt.Clip
      let outputRect, editorRect = Rect.splitH (left.Height - 6) left
      let sessRect, diagRect = Rect.splitHProp 0.5 right
      let oInner = Draw.box (DrawTarget.create grid outputRect) "Output" Theme.borderNormal Theme.bgPanel
      let eInner = Draw.box (DrawTarget.create grid editorRect) "Editor" Theme.borderFocus Theme.bgEditor
      let sInner = Draw.box (DrawTarget.create grid sessRect) "Sessions" Theme.borderNormal Theme.bgPanel
      let dInner = Draw.box (DrawTarget.create grid diagRect) "Diagnostics" Theme.borderNormal Theme.bgPanel
      for row in 0 .. min 20 (oInner.Clip.Height - 1) do
        Draw.text oInner row 0 Theme.fgDefault Theme.bgPanel CellAttrs.None (sprintf "[eval] line %d output" row)
      Draw.text eInner 0 0 Theme.fgDefault Theme.bgEditor CellAttrs.None "let x = 42"
      Draw.text sInner 0 0 Theme.fgCyan Theme.bgPanel CellAttrs.None "session-abc123 (Ready)"
      Draw.text dInner 0 0 Theme.fgYellow Theme.bgPanel CellAttrs.None "No diagnostics"
      Draw.statusBar dt "Ready | session-abc123" "0.5ms" Theme.fgDefault Theme.bgStatus
      AnsiEmitter.emit grid 55 5 |> ignore
    sw.Stop()
    let avgUs = sw.Elapsed.TotalMicroseconds / float iterations
    let avgMs = avgUs / 1000.0
    Expect.isLessThan avgMs 6.9 (sprintf "full frame: %.2f ms (%.0f fps)" avgMs (1000.0 / avgMs))
  }
]

[<Tests>]
let allCellGridTests = testList "CellGrid Rendering" [
  cellGridTests
  rectTests
  drawTests
  ansiEmitterTests
  performanceTests
]
