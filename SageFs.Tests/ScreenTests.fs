module SageFs.Tests.ScreenTests

open Expecto
open SageFs

[<Tests>]
let screenTests = testList "Screen" [

  testList "computeLayout" [
    test "returns 4 panes" {
      let panes, _ = Screen.computeLayout 40 120
      Expect.equal (List.length panes) 4 "should have 4 panes"
    }

    test "all pane ids are present" {
      let panes, _ = Screen.computeLayout 40 120
      let ids = panes |> List.map fst |> Set.ofList
      let expected = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions; PaneId.Diagnostics ]
      Expect.equal ids expected "all pane ids present"
    }

    test "status bar rect is last row" {
      let _, statusRect = Screen.computeLayout 40 120
      Expect.equal statusRect.Row 39 "status bar on last row"
      Expect.equal statusRect.Height 1 "status bar is 1 row"
      Expect.equal statusRect.Width 120 "status bar spans full width"
    }

    test "panes don't overlap status bar" {
      let panes, statusRect = Screen.computeLayout 40 120
      for (_, r) in panes do
        Expect.isLessThanOrEqual (r.Row + r.Height) statusRect.Row
          (sprintf "pane at row %d height %d overlaps status bar at row %d" r.Row r.Height statusRect.Row)
    }
  ]

  testList "draw" [
    test "draws into grid without error" {
      let grid = CellGrid.create 20 60
      let regions = [
        { Id = "output"; Content = "hello world"; Flags = RegionFlags.None; Affordances = []; Cursor = None; Completions = None }
        { Id = "editor"; Content = "let x = 1"; Flags = RegionFlags.None; Affordances = []; Cursor = Some { Line = 0; Col = 5 }; Completions = None }
      ]
      let cursor = Screen.draw grid regions PaneId.Editor Map.empty " status " " hints "
      Expect.isSome cursor "should return cursor position for focused pane"
    }

    test "returns default cursor when no region for focused pane" {
      let grid = CellGrid.create 20 60
      let cursor = Screen.draw grid [] PaneId.Editor Map.empty " left " " right "
      Expect.isSome cursor "should return default cursor for focused pane without content"
    }

    test "grid is not empty after draw" {
      let grid = CellGrid.create 20 60
      let regions = [
        { Id = "output"; Content = "test output"; Flags = RegionFlags.None; Affordances = []; Cursor = None; Completions = None }
      ]
      Screen.draw grid regions PaneId.Output Map.empty " s " " r " |> ignore
      // At least some cells should be non-space (borders, text)
      let mutable nonSpace = 0
      for r in 0 .. CellGrid.rows grid - 1 do
        for c in 0 .. CellGrid.cols grid - 1 do
          if grid.[r, c].Char <> ' ' then nonSpace <- nonSpace + 1
      Expect.isGreaterThan nonSpace 10 "grid should have drawn content"
    }
  ]
]
