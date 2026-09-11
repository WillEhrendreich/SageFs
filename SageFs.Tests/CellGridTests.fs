module SageFs.Tests.CellGridTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.SharedGenerators

let cellGridTests = testList "CellGrid bounds safety and value integrity" [
  test "create makes grid of empty cells" {
    let grid = CellGrid.create 3 4
    CellGrid.rows grid |> Expect.equal "rows" 3
    CellGrid.cols grid |> Expect.equal "cols" 4
    CellGrid.get grid 0 0 |> Expect.equal "cell is empty" Cell.empty
  }

  test "set and get round-trip" {
    let grid = CellGrid.create 2 2
    let cell = Cell.create 'X' 0x00FF0000u 0x00008000u CellAttrs.Bold
    CellGrid.set grid 1 0 cell
    CellGrid.get grid 1 0 |> Expect.equal "should get what was set" cell
  }

  test "set out of bounds is no-op" {
    let grid = CellGrid.create 2 2
    CellGrid.set grid 5 5 (Cell.create '!' 0u 0u CellAttrs.None)
    CellGrid.get grid 0 0 |> Expect.equal "grid unchanged" Cell.empty
  }

  test "get out of bounds returns empty" {
    let grid = CellGrid.create 2 2
    CellGrid.get grid -1 0 |> Expect.equal "negative row" Cell.empty
    CellGrid.get grid 0 99 |> Expect.equal "col overflow" Cell.empty
  }

  test "writeString writes chars with attributes" {
    let grid = CellGrid.create 1 5
    CellGrid.writeString grid 0 1 0x000A0A0Au 0x00141414u CellAttrs.None "Hi"
    CellGrid.get grid 0 0 |> Expect.equal "before string" Cell.empty
    (CellGrid.get grid 0 1).Char |> Expect.equal "first char" 'H'
    (CellGrid.get grid 0 2).Char |> Expect.equal "second char" 'i'
    (CellGrid.get grid 0 1).Fg |> Expect.equal "fg color" 0x000A0A0Au
    CellGrid.get grid 0 3 |> Expect.equal "after string" Cell.empty
  }

  test "writeString clips at grid edge" {
    let grid = CellGrid.create 1 3
    CellGrid.writeString grid 0 1 0u 0u CellAttrs.None "Hello"
    (CellGrid.get grid 0 1).Char |> Expect.equal "first in bounds" 'H'
    (CellGrid.get grid 0 2).Char |> Expect.equal "second in bounds" 'e'
  }

  test "fillRect fills rectangular area" {
    let grid = CellGrid.create 3 4
    let cell = Cell.create '#' 0x00010101u 0x00020202u CellAttrs.None
    CellGrid.fillRect grid (Rect.create 0 1 2 2) cell
    (CellGrid.get grid 0 1).Char |> Expect.equal "top-left of fill" '#'
    (CellGrid.get grid 1 2).Char |> Expect.equal "bottom-right of fill" '#'
    CellGrid.get grid 0 0 |> Expect.equal "outside fill" Cell.empty
    CellGrid.get grid 0 3 |> Expect.equal "outside fill right" Cell.empty
  }

  test "toText produces correct string" {
    let grid = CellGrid.create 2 3
    CellGrid.writeString grid 0 0 0u 0u CellAttrs.None "abc"
    CellGrid.writeString grid 1 0 0u 0u CellAttrs.None "def"
    let txt = CellGrid.toText grid
    txt |> Expect.equal "full grid text" "abc\r\ndef"
  }

  test "toTextTrimmed trims trailing spaces" {
    let grid = CellGrid.create 2 5
    CellGrid.writeString grid 0 0 0u 0u CellAttrs.None "Hi"
    CellGrid.writeString grid 1 0 0u 0u CellAttrs.None "X"
    let txt = CellGrid.toTextTrimmed grid
    txt |> Expect.equal "trimmed trailing spaces" "Hi\r\nX"
  }

  test "clear resets all cells" {
    let grid = CellGrid.create 2 2
    CellGrid.writeString grid 0 0 0x000A0000u 0x00140000u CellAttrs.Bold "AB"
    CellGrid.clear grid
    CellGrid.get grid 0 0 |> Expect.equal "cleared" Cell.empty
    CellGrid.get grid 0 1 |> Expect.equal "cleared" Cell.empty
  }

  test "writeString at origin" {
    let grid = CellGrid.create 2 5
    CellGrid.writeString grid 0 0 0u 0u CellAttrs.None "AB"
    (CellGrid.get grid 0 0).Char |> Expect.equal "A at (0,0)" 'A'
    (CellGrid.get grid 0 1).Char |> Expect.equal "B at (0,1)" 'B'
  }

  test "writeString empty is no-op" {
    let grid = CellGrid.create 2 5
    CellGrid.writeString grid 0 0 0u 0u CellAttrs.None ""
    CellGrid.get grid 0 0 |> Expect.equal "should remain empty" Cell.empty
  }

  test "fillRect with zero-area rect is no-op" {
    let grid = CellGrid.create 3 3
    let cell = Cell.create '#' 0u 0u CellAttrs.None
    CellGrid.fillRect grid (Rect.create 0 0 0 0) cell
    CellGrid.get grid 0 0 |> Expect.equal "zero area should not fill" Cell.empty
  }
]

let rectTests = testList "Rect geometry preserves invariants" [
  test "create clamps negative values" {
    let r = Rect.create -5 -3 -1 -2
    r.Row |> Expect.equal "row clamped" 0
    r.Col |> Expect.equal "col clamped" 0
    r.Width |> Expect.equal "width clamped" 0
    r.Height |> Expect.equal "height clamped" 0
  }

  test "isEmpty for zero-size rects" {
    Rect.isEmpty (Rect.create 0 0 0 5) |> Expect.isTrue "zero width"
    Rect.isEmpty (Rect.create 0 0 5 0) |> Expect.isTrue "zero height"
    Rect.isEmpty (Rect.create 0 0 5 5) |> Expect.isFalse "non-empty"
  }

  test "splitH preserves total height" {
    let r = Rect.create 0 0 80 24
    let top, bot = Rect.splitH 10 r
    top.Height |> Expect.equal "top height" 10
    bot.Height |> Expect.equal "bot height" 14
    top.Row |> Expect.equal "top starts at 0" 0
    bot.Row |> Expect.equal "bot starts at 10" 10
    top.Width |> Expect.equal "top width preserved" 80
    bot.Width |> Expect.equal "bot width preserved" 80
  }

  test "splitV preserves total width" {
    let r = Rect.create 0 0 80 24
    let left, right = Rect.splitV 30 r
    left.Width |> Expect.equal "left width" 30
    right.Width |> Expect.equal "right width" 50
    left.Col |> Expect.equal "left col" 0
    right.Col |> Expect.equal "right col" 30
  }

  test "splitH clamps to bounds" {
    let r = Rect.create 0 0 80 24
    let top, bot = Rect.splitH 30 r
    top.Height |> Expect.equal "top clamped to full" 24
    bot.Height |> Expect.equal "bot empty" 0
  }

  test "splitHProp at 0.5 divides evenly" {
    let r = Rect.create 0 0 80 24
    let top, bot = Rect.splitHProp 0.5 r
    top.Height |> Expect.equal "top half" 12
    bot.Height |> Expect.equal "bot half" 12
  }

  test "splitVProp at 0.65 gives 65% to left" {
    let r = Rect.create 0 0 100 24
    let left, right = Rect.splitVProp 0.65 r
    left.Width |> Expect.equal "65% left" 65
    right.Width |> Expect.equal "35% right" 35
  }

  test "inset shrinks by margin on all sides" {
    let r = Rect.create 0 0 80 24
    let inner = Rect.inset 1 r
    inner.Row |> Expect.equal "row shifted" 1
    inner.Col |> Expect.equal "col shifted" 1
    inner.Width |> Expect.equal "width shrunk" 78
    inner.Height |> Expect.equal "height shrunk" 22
  }

  test "inset too large makes empty rect" {
    let r = Rect.create 0 0 4 4
    let inner = Rect.inset 3 r
    Rect.isEmpty inner |> Expect.isTrue "over-inset is empty"
  }

  test "right and bottom edges" {
    let r = Rect.create 5 10 20 15
    Rect.right r |> Expect.equal "right edge" 30
    Rect.bottom r |> Expect.equal "bottom edge" 20
  }

  testPropertyWithConfig propConfig
    "contains(row,col) = true iff row ∈ [r.Row..r.Row+h) ∧ col ∈ [r.Col..r.Col+w)" <|
    Prop.forAll (Arb.fromGen genSmallRect) (fun r ->
      Prop.forAll
        (Arb.fromGen (gen {
          let! row = Gen.choose (-5, r.Row + r.Height + 5)
          let! col = Gen.choose (-5, r.Col + r.Width + 5)
          return (row, col)
        }))
        (fun (row, col) ->
          let expected =
            row >= r.Row && row < r.Row + r.Height
            && col >= r.Col && col < r.Col + r.Width
          Rect.contains row col r
          |> Expect.equal
               (sprintf "contains(%d,%d) in rect(%d,%d,%d,%d)"
                 row col r.Row r.Col r.Width r.Height)
               expected))
]

let drawTests = testList "Draw primitives render correctly" [
  test "text writes at correct position" {
    let grid = CellGrid.create 3 10
    let dt = DrawTarget.create grid (Rect.create 0 0 10 3)
    Draw.text dt 1 2 (Theme.hexToRgb Theme.fgDefault) (Theme.hexToRgb Theme.bgDefault) CellAttrs.None "Hi"
    (CellGrid.get grid 1 2).Char |> Expect.equal "H at row=1,col=2" 'H'
    (CellGrid.get grid 1 3).Char |> Expect.equal "i at row=1,col=3" 'i'
    CellGrid.get grid 0 0 |> Expect.equal "other cells empty" Cell.empty
  }

  test "text clips at clip boundary" {
    let grid = CellGrid.create 3 10
    let dt = DrawTarget.create grid (Rect.create 0 0 5 3)
    Draw.text dt 0 3 (Theme.hexToRgb Theme.fgDefault) (Theme.hexToRgb Theme.bgDefault) CellAttrs.None "Hello"
    (CellGrid.get grid 0 3).Char |> Expect.equal "H in bounds" 'H'
    (CellGrid.get grid 0 4).Char |> Expect.equal "e in bounds" 'e'
    CellGrid.get grid 0 5 |> Expect.equal "clipped at 5" Cell.empty
  }

  test "fill sets background on all cells" {
    let grid = CellGrid.create 2 3
    let dt = DrawTarget.create grid (Rect.create 0 0 3 2)
    Draw.fill dt (Theme.hexToRgb Theme.bgPanel)
    (CellGrid.get grid 0 0).Bg |> Expect.equal "bg set" (Theme.hexToRgb Theme.bgPanel)
    (CellGrid.get grid 1 2).Bg |> Expect.equal "bg set corner" (Theme.hexToRgb Theme.bgPanel)
  }

  test "box draws border and returns inner target" {
    let grid = CellGrid.create 5 10
    let dt = DrawTarget.create grid (Rect.create 0 0 10 5)
    let inner = Draw.box dt "Test" (Theme.hexToRgb Theme.borderNormal) (Theme.hexToRgb Theme.bgPanel)
    (CellGrid.get grid 0 0).Char |> Expect.equal "top-left corner" '┌'
    (CellGrid.get grid 0 9).Char |> Expect.equal "top-right corner" '┐'
    (CellGrid.get grid 4 0).Char |> Expect.equal "bottom-left corner" '└'
    (CellGrid.get grid 4 9).Char |> Expect.equal "bottom-right corner" '┘'
    inner.Clip.Row |> Expect.equal "inner row" 1
    inner.Clip.Col |> Expect.equal "inner col" 1
    inner.Clip.Width |> Expect.equal "inner width" 8
    inner.Clip.Height |> Expect.equal "inner height" 3
  }

  test "box title appears in top border" {
    let grid = CellGrid.create 5 20
    let dt = DrawTarget.create grid (Rect.create 0 0 20 5)
    let _ = Draw.box dt "Output" (Theme.hexToRgb Theme.borderNormal) (Theme.hexToRgb Theme.bgPanel)
    let txt = CellGrid.toText grid
    txt |> Expect.stringContains "title in border" "Output"
  }

  test "scrolledLines renders visible lines" {
    let grid = CellGrid.create 3 10
    let dt = DrawTarget.create grid (Rect.create 0 0 10 3)
    let lines = ["line 0"; "line 1"; "line 2"; "line 3"; "line 4"]
    Draw.scrolledLines dt lines 1 (Theme.hexToRgb Theme.fgDefault) (Theme.hexToRgb Theme.bgDefault)
    let txt = CellGrid.toTextTrimmed grid
    txt |> Expect.stringContains "first visible" "line 1"
    txt |> Expect.stringContains "second visible" "line 2"
    txt |> Expect.stringContains "third visible" "line 3"
  }

  test "statusBar shows left and right text" {
    let grid = CellGrid.create 1 20
    let dt = DrawTarget.create grid (Rect.create 0 0 20 1)
    Draw.statusBar dt "Ready" "0.5ms" (Theme.hexToRgb Theme.fgDefault) (Theme.hexToRgb Theme.bgStatus)
    let txt = CellGrid.toText grid
    txt |> Expect.stringContains "left text" "Ready"
    txt |> Expect.stringContains "right text" "0.5ms"
  }
]

let ansiEmitterTests = testList "AnsiEmitter produces correct ANSI sequences" [
  test "uniform color grid emits minimal escape codes" {
    let grid = CellGrid.create 2 3
    let dt = DrawTarget.create grid (Rect.create 0 0 3 2)
    Draw.fill dt (Theme.hexToRgb Theme.bgPanel)
    Draw.text dt 0 0 (Theme.hexToRgb Theme.fgDefault) (Theme.hexToRgb Theme.bgPanel) CellAttrs.None "abc"
    Draw.text dt 1 0 (Theme.hexToRgb Theme.fgDefault) (Theme.hexToRgb Theme.bgPanel) CellAttrs.None "def"
    let output = AnsiEmitter.emitGridOnly grid
    // Truecolor format: 38;2;R;G;B
    let fgRgb = Theme.hexToRgb Theme.fgDefault
    let fgCode = sprintf "\x1b[38;2;%d;%d;%dm" (int (Theme.rgbR fgRgb)) (int (Theme.rgbG fgRgb)) (int (Theme.rgbB fgRgb))
    let parts = output.Split(fgCode)
    (parts.Length, 2) |> Expect.isLessThanOrEqual "fg code emitted at most once"
  }

  test "alternating colors emit codes at transitions" {
    let grid = CellGrid.create 1 4
    CellGrid.set grid 0 0 (Cell.create 'A' 0x000A1420u 0u CellAttrs.None)
    CellGrid.set grid 0 1 (Cell.create 'B' 0x00283C50u 0u CellAttrs.None)
    CellGrid.set grid 0 2 (Cell.create 'C' 0x000A1420u 0u CellAttrs.None)
    CellGrid.set grid 0 3 (Cell.create 'D' 0x00283C50u 0u CellAttrs.None)
    let output = AnsiEmitter.emitGridOnly grid
    output |> Expect.stringContains "fg=0A1420 present" "38;2;10;20;32"
    output |> Expect.stringContains "fg=283C50 present" "38;2;40;60;80"
  }

  test "emit includes cursor positioning and show/hide" {
    let grid = CellGrid.create 2 2
    let output = AnsiEmitter.emit grid 0 0
    output |> Expect.stringContains "hides cursor" "\x1b[?25l"
    output |> Expect.stringContains "shows cursor at end" "\x1b[?25h"
    output |> Expect.stringContains "cursor at 1,1" "\x1b[1;1H"
  }

  test "bold attribute emitted" {
    let grid = CellGrid.create 1 1
    CellGrid.set grid 0 0 (Cell.create 'X' 0x00FFFFFFu 0u CellAttrs.Bold)
    let output = AnsiEmitter.emitGridOnly grid
    output |> Expect.stringContains "bold code" "\x1b[1m"
  }

  test "same color adjacent cells emit no extra codes" {
    let grid = CellGrid.create 1 3
    for i in 0 .. 2 do
      CellGrid.set grid 0 i (Cell.create (char (65 + i)) 0x002A2A2Au 0u CellAttrs.None)
    let output = AnsiEmitter.emitGridOnly grid
    let parts = output.Split("\x1b[38;2;42;42;42m")
    parts.Length |> Expect.equal "exactly one fg=2A2A2A code" 2
  }
]

let junctionTests = testList "resolveJunctions connects box-drawing characters" [
  test "stacked corners get T-junctions" {
    let brN = Theme.hexToRgb Theme.borderNormal
    let bgP = Theme.hexToRgb Theme.bgPanel
    let g = CellGrid.create 6 10
    let dt = DrawTarget.create g (Rect.create 0 0 10 6)
    Draw.box (DrawTarget.create g (Rect.create 0 0 10 3)) "" brN bgP |> ignore
    Draw.box (DrawTarget.create g (Rect.create 3 0 10 3)) "" brN bgP |> ignore
    Draw.resolveJunctions dt
    let after = CellGrid.toText g
    after |> Expect.stringContains "after should have ├" "├"
    after |> Expect.stringContains "after should have ┤" "┤"
    let lines = after.Split('\n')
    lines.[0] |> Expect.stringStarts "top-left stays ┌" "┌"
    lines.[0].TrimEnd() |> Expect.stringEnds "top-right stays ┐" "┐"
  }

  test "single box unchanged" {
    let brN = Theme.hexToRgb Theme.borderNormal
    let bgP = Theme.hexToRgb Theme.bgPanel
    let g = CellGrid.create 3 10
    let dt = DrawTarget.create g (Rect.create 0 0 10 3)
    Draw.box dt "" brN bgP |> ignore
    let before = CellGrid.toText g
    Draw.resolveJunctions dt
    let after = CellGrid.toText g
    after |> Expect.equal "single box should not change" before
  }

  test "side-by-side boxes unchanged" {
    let brN = Theme.hexToRgb Theme.borderNormal
    let bgP = Theme.hexToRgb Theme.bgPanel
    let g = CellGrid.create 3 20
    let dt = DrawTarget.create g (Rect.create 0 0 20 3)
    Draw.box (DrawTarget.create g (Rect.create 0 0 10 3)) "" brN bgP |> ignore
    Draw.box (DrawTarget.create g (Rect.create 0 10 10 3)) "" brN bgP |> ignore
    let before = CellGrid.toText g
    Draw.resolveJunctions dt
    let after = CellGrid.toText g
    after |> Expect.equal "side-by-side boxes should not change" before
  }

  test "full layout junctions" {
    let brN = Theme.hexToRgb Theme.borderNormal
    let brF = Theme.hexToRgb Theme.borderFocus
    let bgP = Theme.hexToRgb Theme.bgPanel
    let bgE = Theme.hexToRgb Theme.bgEditor
    let g = CellGrid.create 15 60
    let dt = DrawTarget.create g (Rect.create 0 0 60 15)
    Draw.fill dt bgP
    let allPanes = {
      LayoutConfig.defaults with
        VisiblePanes = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions ] }
    let panes, _ = Screen.computeLayoutWith allPanes 15 60
    for (pid, rect) in panes do
      let bC = if pid = PaneId.Editor then brF else brN
      let bg = if pid = PaneId.Editor then bgE else bgP
      Draw.box (DrawTarget.create g rect) (PaneId.displayName pid) bC bg |> ignore
    Draw.resolveJunctions dt
    let lines = (CellGrid.toText g).Split('\n')
    lines.[7] |> Expect.stringStarts "Output BL → ├" "├"
    lines.[7].Contains("┤") |> Expect.isTrue "Output BR → ┤"
    lines.[8] |> Expect.stringStarts "Editor TL → ├" "├"
    lines.[8].Contains("┤") |> Expect.isTrue "Editor TR → ┤"
    lines.[0] |> Expect.stringStarts "Output TL stays ┌" "┌"
    lines.[13] |> Expect.stringStarts "bottom stays └" "└"
  }
]

let performanceTests = testList "Performance stays within allocation budgets" [
  test "CellGrid clear 200x60 under 100µs" {
    let grid = CellGrid.create 60 200
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let iterations = 10000
    for _ in 1 .. iterations do
      CellGrid.clear grid
    sw.Stop()
    let avgUs = sw.Elapsed.TotalMicroseconds / float iterations
    (avgUs, 100.0) |> Expect.isLessThan (sprintf "clear: %.1f µs" avgUs)
  }

  test "Draw cycle 200x60 under 500µs" {
    let grid = CellGrid.create 60 200
    let fgDef = Theme.hexToRgb Theme.fgDefault
    let fgG = Theme.hexToRgb Theme.fgGreen
    let fgR = Theme.hexToRgb Theme.fgRed
    let bgP = Theme.hexToRgb Theme.bgPanel
    let bgE = Theme.hexToRgb Theme.bgEditor
    let brN = Theme.hexToRgb Theme.borderNormal
    let brF = Theme.hexToRgb Theme.borderFocus
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let iterations = 5000
    for _ in 1 .. iterations do
      let dt = DrawTarget.create grid (Rect.create 0 0 200 60)
      Draw.fill dt bgP
      let inner = Draw.box dt "Output" brN bgP
      Draw.text inner 0 0 fgDef bgP CellAttrs.None "Hello, this is a test line of reasonable length"
      Draw.text inner 1 0 fgG bgP CellAttrs.None "[15:30:02 INF] Test passed"
      Draw.text inner 2 0 fgR bgP CellAttrs.None "[15:30:02 ERR] Test failed"
    sw.Stop()
    let avgUs = sw.Elapsed.TotalMicroseconds / float iterations
    (avgUs, 500.0) |> Expect.isLessThan (sprintf "draw: %.1f µs" avgUs)
  }

  test "AnsiEmitter.emit 200x60 under 2ms" {
    let grid = CellGrid.create 60 200
    let fgDef = Theme.hexToRgb Theme.fgDefault
    let fgG = Theme.hexToRgb Theme.fgGreen
    let fgR = Theme.hexToRgb Theme.fgRed
    let bgP = Theme.hexToRgb Theme.bgPanel
    let brN = Theme.hexToRgb Theme.borderNormal
    let dt = DrawTarget.create grid (Rect.create 0 0 200 60)
    Draw.fill dt bgP
    for row in 0 .. 30 do
      Draw.text dt row 0 fgDef bgP CellAttrs.None
        (sprintf "Line %d: some output text with varying content here" row)
    for row in 31 .. 40 do
      for col in 0 .. 99 do
        let fg = if col % 2 = 0 then fgG else fgR
        CellGrid.set grid row col (Cell.create 'X' fg bgP CellAttrs.None)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let iterations = 1000
    for _ in 1 .. iterations do
      AnsiEmitter.emit grid 55 5 |> ignore
    sw.Stop()
    let avgUs = sw.Elapsed.TotalMicroseconds / float iterations
    (avgUs, 2000.0) |> Expect.isLessThan (sprintf "emit: %.1f µs" avgUs)
  }

  test "Full frame cycle 200x60 under 6.9ms (144fps)" {
    let grid = CellGrid.create 60 200
    let fgDef = Theme.hexToRgb Theme.fgDefault
    let fgG = Theme.hexToRgb Theme.fgGreen
    let fgR = Theme.hexToRgb Theme.fgRed
    let fgC = Theme.hexToRgb Theme.fgCyan
    let fgY = Theme.hexToRgb Theme.fgYellow
    let bgP = Theme.hexToRgb Theme.bgPanel
    let bgE = Theme.hexToRgb Theme.bgEditor
    let bgS = Theme.hexToRgb Theme.bgStatus
    let brN = Theme.hexToRgb Theme.borderNormal
    let brF = Theme.hexToRgb Theme.borderFocus
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let iterations = 1000
    for _ in 1 .. iterations do
      CellGrid.clear grid
      let dt = DrawTarget.create grid (Rect.create 0 0 200 60)
      Draw.fill dt bgP
      let left, right = Rect.splitVProp 0.65 dt.Clip
      let outputRect, editorRect = Rect.splitH (left.Height - 6) left
      let sessRect, diagRect = Rect.splitHProp 0.5 right
      let oInner = Draw.box (DrawTarget.create grid outputRect) "Output" brN bgP
      let eInner = Draw.box (DrawTarget.create grid editorRect) "Editor" brF bgE
      let sInner = Draw.box (DrawTarget.create grid sessRect) "Sessions" brN bgP
      let dInner = Draw.box (DrawTarget.create grid diagRect) "Diagnostics" brN bgP
      for row in 0 .. min 20 (oInner.Clip.Height - 1) do
        Draw.text oInner row 0 fgDef bgP CellAttrs.None (sprintf "[eval] line %d output" row)
      Draw.text eInner 0 0 fgDef bgE CellAttrs.None "let x = 42"
      Draw.text sInner 0 0 fgC bgP CellAttrs.None "session-abc123 (Ready)"
      Draw.text dInner 0 0 fgY bgP CellAttrs.None "No diagnostics"
      Draw.statusBar dt "Ready | session-abc123" "0.5ms" fgDef bgS
      AnsiEmitter.emit grid 55 5 |> ignore
    sw.Stop()
    let avgUs = sw.Elapsed.TotalMicroseconds / float iterations
    let avgMs = avgUs / 1000.0
    (avgMs, 6.9) |> Expect.isLessThan (sprintf "full frame: %.2f ms (%.0f fps)" avgMs (1000.0 / avgMs))
  }
]

let cellGridPropertyTests = testList "CellGrid properties" [
  testPropertyWithConfig propConfig "set/get roundtrip for in-bounds coords" <|
    Prop.forAll (Arb.fromGen genSmallRect) (fun r ->
      Prop.forAll (Arb.fromGen genCell) (fun cell ->
        let grid = CellGrid.create (r.Height + r.Row + 1) (r.Width + r.Col + 1)
        CellGrid.set grid r.Row r.Col cell
        CellGrid.get grid r.Row r.Col |> Expect.equal "roundtrip" cell))

  testPropertyWithConfig propConfig "Rect.splitH conserves total height" <|
    Prop.forAll (Arb.fromGen genSmallRect) (fun r ->
      let splitAt = r.Height / 2
      let top, bot = Rect.splitH splitAt r
      top.Height + bot.Height |> Expect.equal "height conserved" r.Height)

  testPropertyWithConfig propConfig "Rect.splitV conserves total width" <|
    Prop.forAll (Arb.fromGen genSmallRect) (fun r ->
      let splitAt = r.Width / 2
      let left, right = Rect.splitV splitAt r
      left.Width + right.Width |> Expect.equal "width conserved" r.Width)

  testPropertyWithConfig propConfig "Rect.splitH top/bottom are contiguous" <|
    Prop.forAll (Arb.fromGen genSmallRect) (fun r ->
      let splitAt = r.Height / 2
      let top, bot = Rect.splitH splitAt r
      top.Row |> Expect.equal "top starts at original row" r.Row
      bot.Row |> Expect.equal "bottom starts after top" (r.Row + top.Height))

  testPropertyWithConfig propConfig "get out-of-bounds returns Cell.empty" <|
    fun (PositiveInt rows) (PositiveInt cols) ->
      let rows = min rows 50
      let cols = min cols 50
      let grid = CellGrid.create rows cols
      CellGrid.get grid rows cols |> Expect.equal "out of bounds" Cell.empty
      CellGrid.get grid -1 0 |> Expect.equal "negative" Cell.empty

  testPropertyWithConfig propConfig "set out-of-bounds is always a silent no-op" <|
    Prop.forAll (Arb.fromGen genCell) (fun cell ->
      let grid = CellGrid.create 5 5
      let clone = CellGrid.clone grid
      // Set at various OOB positions — grid must remain unchanged
      CellGrid.set grid -1 0 cell
      CellGrid.set grid 0 -1 cell
      CellGrid.set grid 5 0 cell
      CellGrid.set grid 0 5 cell
      CellGrid.set grid 99 99 cell
      CellGrid.set grid -100 -100 cell
      for r in 0 .. 4 do
        for c in 0 .. 4 do
          CellGrid.get grid r c
          |> Expect.equal
               (sprintf "cell at (%d,%d) should be unchanged" r c)
               (CellGrid.get clone r c))

  testPropertyWithConfig propConfig "set then get at same position returns the set value" <|
    fun (PositiveInt rows) (PositiveInt cols) ->
      let rows = min (max rows 1) 30
      let cols = min (max cols 1) 30
      Prop.forAll (Arb.fromGen genCell) (fun cell ->
        Prop.forAll
          (Arb.fromGen (
            gen {
              let! r = Gen.choose (0, rows - 1)
              let! c = Gen.choose (0, cols - 1)
              return (r, c)
            }))
          (fun (r, c) ->
            let grid = CellGrid.create rows cols
            CellGrid.set grid r c cell
            CellGrid.get grid r c |> Expect.equal "roundtrip" cell))
]

let cellGridPoolTests = testList "CellGrid.rent/release" [
  testCase "rent creates grid filled with Cell.empty" <| fun _ ->
    let grid = CellGrid.rent 3 4
    CellGrid.rows grid |> Expect.equal "rows" 3
    CellGrid.cols grid |> Expect.equal "cols" 4
    CellGrid.get grid 0 0 |> Expect.equal "empty cell" Cell.empty
    CellGrid.get grid 2 3 |> Expect.equal "last cell empty" Cell.empty
    CellGrid.release grid

  testCase "rent then release does not throw" <| fun _ ->
    let grid = CellGrid.rent 10 20
    CellGrid.release grid

  testCase "rented grid supports set/get" <| fun _ ->
    let grid = CellGrid.rent 2 2
    let cell = Cell.create 'Z' 0xFF0000u 0u CellAttrs.Bold
    CellGrid.set grid 0 1 cell
    CellGrid.get grid 0 1 |> Expect.equal "roundtrip" cell
    CellGrid.release grid

  testCase "rent array length >= rows * cols" <| fun _ ->
    let grid = CellGrid.rent 5 7
    (grid.Cells.Length >= 5 * 7) |> Expect.isTrue "pool may over-allocate"
    CellGrid.release grid

  testCase "clear works on rented grid" <| fun _ ->
    let grid = CellGrid.rent 3 3
    CellGrid.set grid 1 1 (Cell.create 'A' 0u 0u CellAttrs.None)
    CellGrid.clear grid
    CellGrid.get grid 1 1 |> Expect.equal "cleared" Cell.empty
    CellGrid.release grid
]

[<Tests>]
let allCellGridTests = testList "CellGrid Rendering" [
  cellGridTests
  rectTests
  drawTests
  junctionTests
  ansiEmitterTests
  performanceTests
  cellGridPropertyTests
  cellGridPoolTests
]
