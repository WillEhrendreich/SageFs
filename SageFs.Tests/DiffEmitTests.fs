module SageFs.Tests.DiffEmitTests

open Expecto
open Expecto.Flip
open SageFs

let fg = Theme.hexToRgb "#ffffff"
let bg = Theme.hexToRgb "#000000"

let diffEmitTests =
  testList "AnsiEmitter.emitDiff" [
    testCase "identical grids produce minimal output" <| fun _ ->
      let g = CellGrid.create 10 40
      CellGrid.writeString g 0 0 fg bg CellAttrs.None "Hello"
      let prev = CellGrid.clone g
      let output = AnsiEmitter.emitDiff prev g 0 0
      (output.Length < 30) |> Expect.isTrue (sprintf "minimal output expected but got %d chars" output.Length)

    testCase "single changed cell emits only that cell" <| fun _ ->
      let g = CellGrid.create 10 40
      CellGrid.writeString g 0 0 fg bg CellAttrs.None "Hello"
      let prev = CellGrid.clone g
      CellGrid.set g 0 2 { Char = 'X'; Fg = fg; Bg = bg; Attrs = CellAttrs.None }
      let output = AnsiEmitter.emitDiff prev g 0 0
      output |> Expect.stringContains "should contain the changed char" "X"
      let fullOutput = AnsiEmitter.emit g 0 0
      (output.Length < fullOutput.Length / 2) |> Expect.isTrue "diff should be much shorter"

    testCase "large change falls back to full emit" <| fun _ ->
      let g1 = CellGrid.create 10 40
      for r in 0..9 do CellGrid.writeString g1 r 0 fg bg CellAttrs.None (System.String('A', 40))
      let g2 = CellGrid.create 10 40
      for r in 0..9 do CellGrid.writeString g2 r 0 fg bg CellAttrs.None (System.String('B', 40))
      let diffOut = AnsiEmitter.emitDiff g1 g2 0 0
      let fullOut = AnsiEmitter.emit g2 0 0
      diffOut |> Expect.equal "should fall back to full emit for >30%% changed" fullOut

    testCase "different sized grids fall back to full emit" <| fun _ ->
      let g1 = CellGrid.create 10 40
      let g2 = CellGrid.create 12 40
      let diffOut = AnsiEmitter.emitDiff g1 g2 0 0
      let fullOut = AnsiEmitter.emit g2 0 0
      diffOut |> Expect.equal "should fall back for different sizes" fullOut

    testCase "contiguous changed cells share cursor position" <| fun _ ->
      let g = CellGrid.create 10 40
      CellGrid.writeString g 0 0 fg bg CellAttrs.None "AAAA"
      let prev = CellGrid.clone g
      CellGrid.writeString g 0 0 fg bg CellAttrs.None "BBBB"
      let output = AnsiEmitter.emitDiff prev g 0 0
      output |> Expect.stringContains "contiguous cells should appear together" "BBBB"

    testCase "performance: no-change diff under 100µs for 60x200" <| fun _ ->
      let g = CellGrid.create 60 200
      CellGrid.writeString g 0 0 fg bg CellAttrs.None "Hello World"
      let prev = CellGrid.clone g
      AnsiEmitter.emitDiff prev g 0 0 |> ignore
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let iters = 100
      for _ in 1 .. iters do AnsiEmitter.emitDiff prev g 0 0 |> ignore
      sw.Stop()
      let usPerOp = float sw.Elapsed.TotalMicroseconds / float iters
      printfn "emitDiff (no changes, 60x200): %.1f µs/op" usPerOp
      (usPerOp, 100.0) |> Expect.isLessThan "no-change diff should be under 100µs"

    testCase "clone produces independent copy" <| fun _ ->
      let g = CellGrid.create 5 10
      CellGrid.writeString g 0 0 fg bg CellAttrs.None "Hello"
      let c = CellGrid.clone g
      CellGrid.set g 0 0 { Char = 'X'; Fg = fg; Bg = bg; Attrs = CellAttrs.None }
      (CellGrid.get c 0 0).Char |> Expect.equal "clone should not be affected by original changes" 'H'
      (CellGrid.get g 0 0).Char |> Expect.equal "original should have changed" 'X'
  ]
