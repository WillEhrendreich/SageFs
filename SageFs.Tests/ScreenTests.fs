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

  testList "StatusHints" [
    test "build shows quit and focus with default keymap" {
      let result = StatusHints.build KeyMap.defaults PaneId.Output
      Expect.stringContains result "quit" "should contain quit hint"
      Expect.stringContains result "focus" "should contain focus hint"
    }

    test "editor pane shows eval hint" {
      let result = StatusHints.build KeyMap.defaults PaneId.Editor
      Expect.stringContains result "eval" "should contain eval hint"
    }

    test "sessions pane shows new-session hint" {
      let result = StatusHints.build KeyMap.defaults PaneId.Sessions
      Expect.stringContains result "new-session" "should contain new-session hint"
    }

    test "output pane shows scroll hint" {
      let result = StatusHints.build KeyMap.defaults PaneId.Output
      Expect.stringContains result "scroll" "should contain scroll hint"
    }

    test "empty keymap returns empty string" {
      let result = StatusHints.build Map.empty PaneId.Editor
      Expect.equal result "" "empty keymap should produce empty hints"
    }
  ]

  testList "Theme config" [
    test "parseConfigLines extracts theme values" {
      let lines = [|
        """let theme = [ "fgDefault", 200; "bgPanel", 100 ]"""
      |]
      let overrides = Theme.parseConfigLines lines
      Expect.equal (Map.find "fgDefault" overrides) 200uy "fgDefault parsed"
      Expect.equal (Map.find "bgPanel" overrides) 100uy "bgPanel parsed"
    }

    test "parseConfigLines ignores non-theme lines" {
      let lines = [|
        """let projects = [ "test.fsproj" ]"""
        """let theme = [ "bgEditor", 50 ]"""
      |]
      let overrides = Theme.parseConfigLines lines
      Expect.equal overrides.Count 1 "only theme values parsed"
      Expect.equal (Map.find "bgEditor" overrides) 50uy "bgEditor parsed"
    }

    test "parseConfigLines returns empty for no theme section" {
      let lines = [| """let projects = [ "test.fsproj" ]""" |]
      let overrides = Theme.parseConfigLines lines
      Expect.equal overrides.Count 0 "no theme values"
    }

    test "withOverrides applies partial overrides" {
      let overrides = Map.ofList [ "fgDefault", 200uy; "bgPanel", 100uy ]
      let result = Theme.withOverrides overrides Theme.defaults
      Expect.equal result.FgDefault 200uy "fgDefault overridden"
      Expect.equal result.BgPanel 100uy "bgPanel overridden"
      Expect.equal result.FgDim Theme.defaults.FgDim "fgDim unchanged"
    }

    test "withOverrides with empty map returns base unchanged" {
      let result = Theme.withOverrides Map.empty Theme.defaults
      Expect.equal result Theme.defaults "no overrides = defaults"
    }
  ]
]
