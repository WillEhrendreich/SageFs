module SageFs.Tests.SageTuiAllocationBenchmarks

open System
open Expecto
open Expecto.Flip
open SageTUI

// ---------------------------------------------------------------------------
// Test data factories — realistic Element trees mimicking SageFs view() output
// ---------------------------------------------------------------------------

let private mkTextLines (prefix: string) (count: int) : Element list =
  [ for i in 1 .. count ->
      El.text (sprintf "%s line %d: Lorem ipsum dolor sit amet" prefix i) ]

let private mkPane (title: string) (lineCount: int) : Element =
  El.column (mkTextLines title lineCount)
  |> El.bordered BorderStyle.Rounded
  |> El.fill

let private mkSimpleDashboard () : Element =
  El.column [
    El.text "=== SageFs Dashboard ==="
    |> El.bold
    El.row [
      mkPane "Editor" 10
      mkPane "Output" 10
    ]
    |> El.fill
  ]

let private mkFullDashboard () : Element =
  let mkScrolledPane title lines =
    El.column (mkTextLines title lines)
    |> El.scroll 5
    |> El.borderedWithTitle title BorderStyle.Rounded
    |> El.fill
  El.column [
    El.text "=== SageFs Full Dashboard ==="
    |> El.bold
    |> El.padded (Padding.hv 1 0)
    El.row [
      El.column [
        mkScrolledPane "Editor" 30
        mkScrolledPane "Diagnostics" 30
        mkScrolledPane "Terminal" 30
      ]
      |> El.fill
      El.column [
        mkScrolledPane "Output" 30
        mkScrolledPane "Tests" 30
        mkScrolledPane "Coverage" 30
      ]
      |> El.fill
    ]
    |> El.fill
  ]

let private mkStressDashboard () : Element =
  let mkNestedPane title lines =
    El.column (mkTextLines title lines)
    |> El.padded (Padding.all 1)
    |> El.bordered BorderStyle.Double
    |> El.scroll 5
    |> El.bordered BorderStyle.Light
    |> El.fill
  El.column [
    El.row [
      El.text "=== SageFs Stress Dashboard ==="
      |> El.bold
    ]
    |> El.padded (Padding.hv 2 0)
    El.row [
      El.column [
        mkNestedPane "Editor" 100
        mkNestedPane "Diagnostics" 100
        mkNestedPane "Terminal" 100
      ]
      |> El.percentage 50
      El.column [
        mkNestedPane "Output" 100
        mkNestedPane "Tests" 100
        mkNestedPane "Coverage" 100
      ]
      |> El.percentage 50
    ]
    |> El.fill
  ]

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let rec private countNodes (elem: Element) : int =
  match elem with
  | Empty | Text _ | Canvas _ | Filled _ | Hyperlink _ -> 1
  | Styled (_, child)
  | Constrained (_, child)
  | Bordered (_, _, child)
  | Padded (_, child)
  | Keyed (_, _, _, child)
  | Aligned (_, _, child)
  | Gapped (_, child)
  | Scroll (_, child) -> 1 + countNodes child
  | Row children | Column children | Overlay children ->
    1 + (children |> List.sumBy countNodes)
  | Responsive breakpoints | ResponsiveH breakpoints ->
    1 + (breakpoints |> List.sumBy (snd >> countNodes))

let private measureNs (iterations: int) (f: unit -> 'a) : float =
  for _ in 1 .. 10 do f () |> ignore // warmup
  let sw = Diagnostics.Stopwatch.StartNew ()
  for _ in 1 .. iterations do f () |> ignore
  sw.Stop ()
  float sw.ElapsedTicks
  / float Diagnostics.Stopwatch.Frequency
  * 1_000_000_000.0
  / float iterations

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[<Tests>]
let sageTuiAllocationBenchmarks =
  testList "[Benchmark] SageTUI allocation benchmarks" [

    // ----- arena lifecycle -----
    testList "arena lifecycle" [
      testCase "reset is O(1)" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        let nsPerReset = measureNs 100_000 (fun () -> FrameArena.reset arena)
        printfn "FrameArena.reset: %.1f ns/op" nsPerReset
        (nsPerReset, 1000.0)
        |> Expect.isLessThan "reset should be < 1μs"

      testCase "arena peak tracking" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        let tree = mkFullDashboard ()
        Arena.lower arena tree |> ignore
        FrameArena.reset arena
        (arena.PeakNodes, 0)
        |> Expect.isGreaterThan "PeakNodes should be > 0"
        (arena.PeakChars, 0)
        |> Expect.isGreaterThan "PeakChars should be > 0"

      testCase "arena reuse across frames" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        let tree = mkFullDashboard ()
        Arena.lower arena tree |> ignore
        let firstNodeCount = arena.NodeCount
        let firstTextPos = arena.TextPos
        FrameArena.reset arena
        Arena.lower arena tree |> ignore
        arena.NodeCount
        |> Expect.equal "same node count after reuse" firstNodeCount
        arena.TextPos
        |> Expect.equal "same text pos after reuse" firstTextPos
    ]

    // ----- element tree construction -----
    testList "element tree construction" [
      testCase "simple dashboard node count" <| fun _ ->
        let count = countNodes (mkSimpleDashboard ())
        printfn "simple dashboard: %d nodes" count
        (count, 10)
        |> Expect.isGreaterThan "should have > 10 nodes"
        (count, 200)
        |> Expect.isLessThan "should have < 200 nodes"

      testCase "full dashboard node count" <| fun _ ->
        let count = countNodes (mkFullDashboard ())
        printfn "full dashboard: %d nodes" count
        (count, 100)
        |> Expect.isGreaterThan "should have > 100 nodes"
        (count, 2000)
        |> Expect.isLessThan "should have < 2000 nodes"

      testCase "stress dashboard node count" <| fun _ ->
        let count = countNodes (mkStressDashboard ())
        printfn "stress dashboard: %d nodes" count
        (count, 400)
        |> Expect.isGreaterThan "should have > 400 nodes"
        (count, 10000)
        |> Expect.isLessThan "should have < 10000 nodes"
    ]

    // ----- arena lowering latency -----
    testList "arena lowering latency" [
      testCase "simple dashboard under 2ms" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        let tree = mkSimpleDashboard ()
        let nsPerOp =
          measureNs 1000 (fun () ->
            FrameArena.reset arena
            Arena.lower arena tree)
        let usPerOp = nsPerOp / 1000.0
        printfn "Arena.lower simple: %.1f μs/op" usPerOp
        (usPerOp, 2000.0)
        |> Expect.isLessThan "simple lower should be < 2ms"

      testCase "full dashboard under 10ms" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        let tree = mkFullDashboard ()
        let nsPerOp =
          measureNs 500 (fun () ->
            FrameArena.reset arena
            Arena.lower arena tree)
        let usPerOp = nsPerOp / 1000.0
        printfn "Arena.lower full: %.1f μs/op" usPerOp
        (usPerOp, 10_000.0)
        |> Expect.isLessThan "full lower should be < 10ms"

      testCase "stress dashboard under 50ms" <| fun _ ->
        let arena = FrameArena.create 8192 131072 4096
        let tree = mkStressDashboard ()
        let nsPerOp =
          measureNs 100 (fun () ->
            FrameArena.reset arena
            Arena.lower arena tree)
        let usPerOp = nsPerOp / 1000.0
        printfn "Arena.lower stress: %.1f μs/op" usPerOp
        (usPerOp, 50_000.0)
        |> Expect.isLessThan "stress lower should be < 50ms"
    ]

    // ----- render pipeline latency -----
    testList "render pipeline latency" [
      testCase "full pipeline simple under 5ms" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        let area = { X = 0; Y = 0; Width = 120; Height = 40 }
        let prev = Buffer.create 120 40
        let curr = Buffer.create 120 40
        let changes = ResizeArray<int> 256
        let tree = mkSimpleDashboard ()
        let nsPerOp =
          measureNs 500 (fun () ->
            FrameArena.reset arena
            let root = Arena.lower arena tree
            ArenaRender.renderRoot arena root area curr
            changes.Clear ()
            Buffer.diffInto changes prev curr)
        let usPerOp = nsPerOp / 1000.0
        printfn "full pipeline simple: %.1f μs/op" usPerOp
        (usPerOp, 5000.0)
        |> Expect.isLessThan "simple pipeline should be < 5ms"

      testCase "full pipeline full under 20ms" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        let area = { X = 0; Y = 0; Width = 200; Height = 60 }
        let prev = Buffer.create 200 60
        let curr = Buffer.create 200 60
        let changes = ResizeArray<int> 1024
        let tree = mkFullDashboard ()
        let nsPerOp =
          measureNs 200 (fun () ->
            FrameArena.reset arena
            let root = Arena.lower arena tree
            ArenaRender.renderRoot arena root area curr
            changes.Clear ()
            Buffer.diffInto changes prev curr)
        let usPerOp = nsPerOp / 1000.0
        printfn "full pipeline full: %.1f μs/op" usPerOp
        (usPerOp, 20_000.0)
        |> Expect.isLessThan "full pipeline should be < 20ms"

      testCase "diff identical buffers under 500μs" <| fun _ ->
        let buf1 = Buffer.create 200 60
        let buf2 = Buffer.create 200 60
        let changes = ResizeArray<int> 64
        let nsPerOp =
          measureNs 5000 (fun () ->
            changes.Clear ()
            Buffer.diffInto changes buf1 buf2)
        let usPerOp = nsPerOp / 1000.0
        printfn "diff identical: %.1f μs/op" usPerOp
        (usPerOp, 500.0)
        |> Expect.isLessThan "identical diff should be < 500μs (SIMD skip)"

      testCase "diff 10% changed under 1ms" <| fun _ ->
        let width, height = 200, 60
        let buf1 = Buffer.create width height
        let buf2 = Buffer.create width height
        let totalCells = width * height
        let changedCount = totalCells / 10
        let rng = Random 42
        for _ in 1 .. changedCount do
          let idx = rng.Next totalCells
          let x = idx % width
          let y = idx / width
          let cell =
            { Rune = int32 'X'
              Fg = 0x00FF0000
              Bg = 0
              Attrs = 0us
              _pad = 0us }
          Buffer.set x y cell buf2
        let changes = ResizeArray<int> changedCount
        let nsPerOp =
          measureNs 2000 (fun () ->
            changes.Clear ()
            Buffer.diffInto changes buf1 buf2)
        let usPerOp = nsPerOp / 1000.0
        printfn "diff 10%% changed: %.1f μs/op (%d cells changed)" usPerOp changedCount
        (usPerOp, 1000.0)
        |> Expect.isLessThan "10% diff should be < 1ms"
    ]

    // ----- allocation pressure -----
    testList "allocation pressure" [
      testCase "arena node consumption simple" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        Arena.lower arena (mkSimpleDashboard ()) |> ignore
        printfn "simple dashboard: %d nodes, %d chars" arena.NodeCount arena.TextPos
        (arena.NodeCount, 0)
        |> Expect.isGreaterThan "should use some nodes"
        (arena.NodeCount, 500)
        |> Expect.isLessThan "should use < 500 nodes"

      testCase "arena text consumption full" <| fun _ ->
        let arena = FrameArena.create 4096 32768 2048
        Arena.lower arena (mkFullDashboard ()) |> ignore
        printfn "full dashboard: %d nodes, %d chars" arena.NodeCount arena.TextPos
        (arena.TextPos, 0)
        |> Expect.isGreaterThan "should use some text"
        (arena.TextPos, 25000)
        |> Expect.isLessThan "should use < 25000 chars"

      testCase "arena capacity sufficient for stress" <| fun _ ->
        let maxNodes = 8192
        let maxChars = 131072
        let arena = FrameArena.create maxNodes maxChars 4096
        Arena.lower arena (mkStressDashboard ()) |> ignore
        printfn "stress dashboard: %d/%d nodes, %d/%d chars"
          arena.NodeCount maxNodes arena.TextPos maxChars
        (arena.NodeCount, maxNodes)
        |> Expect.isLessThan "nodes should not overflow"
        (arena.TextPos, maxChars)
        |> Expect.isLessThan "chars should not overflow"
    ]
  ]
