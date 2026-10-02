/// The dashboard's live-testing panel: at 14k+ tests the old duration-proportional
/// squarified treemap packed ~14,360 cells into a fixed 320x180 box, giving every
/// test a ~2x2px rectangle — an unreadable micro-grid, and zero cells large enough
/// to carry a label. Worse, `TestTreemap.fromStatusEntries` writes Running/Skipped
/// as `Timeouts.notRun` (TimeSpan.Zero), so the untimed majority all had identical
/// area and encoded no information at all.
///
/// These tests pin the replacement: a uniform status grid that keeps every test
/// visible at a legible size and still conveys per-test status.
module SageFs.Tests.DashboardTestStatusGridTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Tests

/// Mirrors the real screenshot: 76 passed, 14283 skipped, 1 failed, total 238ms
/// of real duration carried entirely by the passed tests.
let private bigRunEntries =
  let statuses =
    Array.append
      (Array.create 76 TreemapStatus.Passed)
      (Array.append (Array.create 14283 TreemapStatus.Skipped) [| TreemapStatus.Failed |])
  statuses
  |> Array.mapi (fun i st ->
    { DisplayName = sprintf "test %d" i
      FullName = sprintf "Ns.test %d" i
      DurationMs =
        match st with
        | TreemapStatus.Passed -> TestMagnitudes.treemapPanelTotalMs / float TestMagnitudes.treemapPanelPassedCount
        // Timeouts.notRun.TotalMilliseconds — an untimed test is written as zero.
        | _ -> FixtureDurations.notRunMs
      Status = st })

[<Tests>]
let allTests = testList "Dashboard test status grid" [

  test "the old layout packed 14360 tests into ~2px cells and could label none of them" {
    // RED: this is the defect, stated as an executable measurement of the old
    // production call `TestTreemap.layout 320.0 180.0 entries`.
    let rects = TestTreemap.layout TestMagnitudes.treemapPanelWidthPx TestMagnitudes.treemapPanelHeightPx bigRunEntries
    let labelable = rects |> Array.filter (fun r -> r.W >= TestMagnitudes.treemapMinLabelWidthPx && r.H >= TestMagnitudes.treemapMinLabelHeightPx)
    labelable.Length |> Expect.equal "old treemap could label none of 14360 cells" 0
    TestMagnitudes.treemapPanelHeightPx |> Expect.equal "old panel was a fixed 180px tall" 180.0
  }

  testList "statusGrid" [

    test "no entries produces no grid" {
      TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx [||]
      |> Expect.equal "empty input renders no grid" None
    }

    test "one cell per test: none are dropped or duplicated at 14k tests" {
      let layout = TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries
      layout |> Expect.isSome "should lay out"
      let cells = layout.Value.Cells
      cells.Length |> Expect.equal "every test keeps its own cell" bigRunEntries.Length
      let slots = cells |> Array.map (fun c -> c.Row, c.Column) |> Set.ofArray
      slots.Count |> Expect.equal "cells never share a slot (no overlap)" bigRunEntries.Length
    }

    test "every cell stays inside the grid bounds" {
      let layout = (TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries).Value
      let strays =
        layout.Cells
        |> Array.filter (fun c ->
          c.Column < 0 || c.Column >= layout.Columns || c.Row < 0 || c.Row >= layout.Rows)
      strays.Length |> Expect.equal "no cell is laid out past the grid" 0
    }

    test "every cell is at least the legibility floor at 14k tests" {
      let layout = (TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries).Value
      // The invariant is the floor, not a pixel-perfect match: a grid may leave
      // slack, but it must never render a cell under it.
      (layout.CellPx >= TestMagnitudes.treemapMinCellPx)
      |> Expect.isTrue "cells never shrink below the legibility floor"
    }

    test "the grid stays within the panel width it is given" {
      let layout = (TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries).Value
      layout.Width
      |> Expect.equal "grid never overflows its panel" TestMagnitudes.treemapPanelWidthPx
    }

    test "cells never grow beyond the size cap, so a small run is not one huge block" {
      let one = (TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries.[0..0]).Value
      one.CellPx |> Expect.equal "a single test renders as one capped cell" TestMagnitudes.treemapMaxCellPx
      let five = (TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries.[0..4]).Value
      five.CellPx |> Expect.equal "five tests render as capped cells, not a full-width bar" TestMagnitudes.treemapMaxCellPx
    }

    test "failures sort to the front so a red test is never buried under 14k cells" {
      let layout = (TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries).Value
      let failedIdx =
        layout.Cells
        |> Array.tryFindIndex (fun c -> c.Entry.Status = TreemapStatus.Failed)
        |> Option.defaultValue -1
      failedIdx |> Expect.equal "the failing test is the very first cell" 0
    }

    test "every status is still conveyed per cell even when DurationMs is zero" {
      // The untimed majority all carry the not-run duration marker; the grid must not use
      // duration as its layout basis, so they stay individually visible.
      let untimed = bigRunEntries |> Array.filter (fun e -> e.DurationMs = FixtureDurations.notRunMs)
      untimed.Length
      |> Expect.equal "most of the run is untimed" (bigRunEntries.Length - TestMagnitudes.treemapPanelPassedCount)
      let layout = (TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries).Value
      let timed = layout.Cells |> Array.filter (fun c -> c.Entry.DurationMs = FixtureDurations.notRunMs)
      timed.Length |> Expect.equal "every untimed test still gets its own cell" untimed.Length
    }

    test "rows are enough to hold every cell" {
      let layout = (TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx bigRunEntries).Value
      let capacity = layout.Columns * layout.Rows
      (capacity >= bigRunEntries.Length)
      |> Expect.isTrue "the grid has room for every cell"
    }

    test "the layout is stable across a wide sweep of test counts" {
      [ 0; 1; 2; 5; 17; 100; 500; 106; 2000; 14360 ] |> List.iter (fun n ->
        let slice = bigRunEntries.[0 .. max 0 (n - 1)]
        match TestStatusGrid.layout TestMagnitudes.treemapPanelWidthPx slice with
        | None -> n |> Expect.equal "only zero entries render no grid" 0
        | Some l ->
          // The expectation is the LENGTH OF THE SLICE THAT WAS PASSED IN, not `n`: the slice is
          // `[0 .. max 0 (n-1)]`, so at n=0 it is one entry, not zero. Deriving the expectation from
          // `n` instead made this row assert 0 cells for a 1-entry input, which contradicted itself
          // and failed for a reason that had nothing to do with the layout.
          l.Cells.Length
          |> Expect.equal (sprintf "n=%d keeps one cell per test" n) slice.Length
          l.CellPx
          |> Expect.equal (sprintf "n=%d keeps cells legible" n) (max TestMagnitudes.treemapMinCellPx (min TestMagnitudes.treemapMaxCellPx l.CellPx)))
    }
  ]
]
