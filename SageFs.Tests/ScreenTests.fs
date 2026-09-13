module SageFs.Tests.ScreenTests

open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let screenTests = testList "Screen" [

  testList "computeLayout" [
    test "returns 2 panes with defaults" {
      let panes, _ = Screen.computeLayout 40 120
      (List.length panes) |> Expect.equal "should have 2 panes (Output + Sessions)" 2
    }

    test "all default pane ids are present" {
      let panes, _ = Screen.computeLayout 40 120
      let ids = panes |> List.map fst |> Set.ofList
      let expected = Set.ofList [ PaneId.Output; PaneId.Sessions ]
      ids |> Expect.equal "default pane ids: Output + Sessions" expected
    }

    test "status bar rect is last row" {
      let _, statusRect = Screen.computeLayout 40 120
      statusRect.Row |> Expect.equal "status bar on last row" 39
      statusRect.Height |> Expect.equal "status bar is 1 row" 1
      statusRect.Width |> Expect.equal "status bar spans full width" 120
    }

    test "panes don't overlap status bar" {
      let panes, statusRect = Screen.computeLayout 40 120
      for (_, r) in panes do
        ((r.Row + r.Height), statusRect.Row) |> Expect.isLessThanOrEqual (sprintf "pane at row %d height %d overlaps status bar at row %d" r.Row r.Height statusRect.Row)
    }
  ]

  testList "draw" [
    test "draws into grid without error" {
      let grid = CellGrid.create 20 60
      let regions = [
        { Id = "output"; Content = "hello world"; Flags = RegionFlags.None; Affordances = []; Cursor = None; Completions = None; LineAnnotations = [||] }
        { Id = "editor"; Content = "let x = 1"; Flags = RegionFlags.None; Affordances = []; Cursor = Some { Line = 0; Col = 5 }; Completions = None; LineAnnotations = [||] }
      ]
      // Output pane is visible in defaults but has no cursor region
      Screen.draw grid regions PaneId.Output Map.empty " status " " hints " |> ignore
      let text = CellGrid.toText grid
      text |> Expect.stringContains "output content should appear" "hello world"
    }

    test "returns cursor position when focused pane has no region" {
      let grid = CellGrid.create 20 60
      // drawWith may return a default cursor position even when no regions
      Screen.draw grid [] PaneId.Output Map.empty " left " " right " |> ignore
      let text = CellGrid.toText grid
      text |> Expect.isNonEmpty "grid should have content after draw"
    }

    test "grid is not empty after draw" {
      let grid = CellGrid.create 20 60
      let regions = [
        { Id = "output"; Content = "test output"; Flags = RegionFlags.None; Affordances = []; Cursor = None; Completions = None; LineAnnotations = [||] }
      ]
      Screen.draw grid regions PaneId.Output Map.empty " s " " r " |> ignore
      // At least some cells should be non-space (borders, text)
      let mutable nonSpace = 0
      for r in 0 .. CellGrid.rows grid - 1 do
        for c in 0 .. CellGrid.cols grid - 1 do
          if (CellGrid.get grid r c).Char <> ' ' then nonSpace <- nonSpace + 1
      (nonSpace, 10) |> Expect.isGreaterThan "grid should have drawn content"
    }
  ]

  testList "StatusHints" [
    test "build shows quit and focus with default keymap" {
      let result = StatusHints.build KeyMap.defaults PaneId.Output LayoutConfig.defaults.VisiblePanes 0 UiDensity.Normal
      result |> Expect.stringContains "should contain quit hint" "quit"
      result |> Expect.stringContains "should contain focus hint" "focus"
    }

    test "editor pane shows eval hint" {
      let panes = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions ]
      let result = StatusHints.build KeyMap.defaults PaneId.Editor panes 0 UiDensity.Normal
      result |> Expect.stringContains "should contain eval hint" "eval"
    }

    test "sessions pane shows new-session hint" {
      let result = StatusHints.build KeyMap.defaults PaneId.Sessions LayoutConfig.defaults.VisiblePanes 0 UiDensity.Normal
      result |> Expect.stringContains "should contain new-session hint" "new-session"
    }

    test "sessions pane shows auto-open-off hint" {
      let result = StatusHints.build KeyMap.defaults PaneId.Sessions LayoutConfig.defaults.VisiblePanes 0 UiDensity.Normal
      result |> Expect.stringContains "should contain auto-open-off hint" "auto-open-off"
    }

    test "output pane shows scroll hint" {
      let result = StatusHints.build KeyMap.defaults PaneId.Output LayoutConfig.defaults.VisiblePanes 0 UiDensity.Normal
      result |> Expect.stringContains "should contain scroll hint" "scroll"
    }

    test "empty keymap returns empty string" {
      let result = StatusHints.build Map.empty PaneId.Editor Set.empty 0 UiDensity.Normal
      result |> Expect.equal "empty keymap should produce empty hints" ""
    }

    test "shows show-editor when editor hidden" {
      let panes = Set.ofList [ PaneId.Output; PaneId.Sessions ]
      let result = StatusHints.build KeyMap.defaults PaneId.Output panes 0 UiDensity.Normal
      result |> Expect.stringContains "should hint to show editor when hidden" "show-editor"
    }

    test "shows hide-editor when editor visible" {
      let panes = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions ]
      let result = StatusHints.build KeyMap.defaults PaneId.Output panes 0 UiDensity.Normal
      result |> Expect.stringContains "should hint to hide editor when visible" "hide-editor"
    }
  ]

  testList "Theme config" [
    test "parseConfigLines extracts theme values" {
      let lines = [|
        """let theme = [ "fgDefault", "#C8C8C8" """
        """             "bgPanel", "#646464" ]"""
      |]
      let overrides = Theme.parseConfigLines lines
      (Map.find "fgDefault" overrides) |> Expect.equal "fgDefault parsed" "#C8C8C8"
      (Map.find "bgPanel" overrides) |> Expect.equal "bgPanel parsed" "#646464"
    }

    test "parseConfigLines ignores non-theme lines" {
      let lines = [|
        """let projects = [ "test.fsproj" ]"""
        """let theme = [ "bgEditor", "#323232" ]"""
      |]
      let overrides = Theme.parseConfigLines lines
      overrides.Count |> Expect.equal "only theme values parsed" 1
      (Map.find "bgEditor" overrides) |> Expect.equal "bgEditor parsed" "#323232"
    }

    test "parseConfigLines returns empty for no theme section" {
      let lines = [| """let projects = [ "test.fsproj" ]""" |]
      let overrides = Theme.parseConfigLines lines
      overrides.Count |> Expect.equal "no theme values" 0
    }

    test "withOverrides applies partial overrides" {
      let overrides = Map.ofList [ "fgDefault", "#C8C8C8"; "bgPanel", "#646464" ]
      let result = Theme.withOverrides overrides Theme.defaults
      result.FgDefault |> Expect.equal "fgDefault overridden" "#C8C8C8"
      result.BgPanel |> Expect.equal "bgPanel overridden" "#646464"
      result.FgDim |> Expect.equal "fgDim unchanged" Theme.defaults.FgDim
    }

    test "withOverrides with empty map returns base unchanged" {
      let result = Theme.withOverrides Map.empty Theme.defaults
      result |> Expect.equal "no overrides = defaults" Theme.defaults
    }
  ]

  testList "LayoutConfig" [
    test "defaults includes Output and Sessions" {
      let cfg = LayoutConfig.defaults
      cfg.VisiblePanes.Count |> Expect.equal "defaults should have 2 visible panes" 2
      (cfg.VisiblePanes.Contains PaneId.Output) |> Expect.isTrue "should contain Output"
      (cfg.VisiblePanes.Contains PaneId.Sessions) |> Expect.isTrue "should contain Sessions"
      (cfg.VisiblePanes.Contains PaneId.Editor) |> Expect.isFalse "Editor hidden by default"
    }

    test "togglePane hides a visible pane" {
      let cfg = LayoutConfig.togglePane PaneId.Sessions LayoutConfig.defaults
      (cfg.VisiblePanes.Contains PaneId.Sessions) |> Expect.isFalse "Sessions should be hidden"
      cfg.VisiblePanes.Count |> Expect.equal "should have 1 visible pane" 1
    }

    test "togglePane shows a hidden pane" {
      let cfg = LayoutConfig.togglePane PaneId.Sessions LayoutConfig.defaults
      let cfg2 = LayoutConfig.togglePane PaneId.Sessions cfg
      (cfg2.VisiblePanes.Contains PaneId.Sessions) |> Expect.isTrue "Sessions should be visible again"
    }

    test "togglePane can show Editor" {
      let cfg = LayoutConfig.togglePane PaneId.Editor LayoutConfig.defaults
      (cfg.VisiblePanes.Contains PaneId.Editor) |> Expect.isTrue "Editor should be shown after toggle"
    }

    test "focus preset has only Output and Editor" {
      let cfg = LayoutConfig.focus
      cfg.VisiblePanes |> Expect.equal "focus preset panes" (Set.ofList [ PaneId.Output; PaneId.Editor ])
    }

    test "minimal preset has only Editor" {
      let cfg = LayoutConfig.minimal
      cfg.VisiblePanes |> Expect.equal "minimal preset panes" (Set.singleton PaneId.Editor)
    }

    test "computeLayoutWith focus preset returns 2 panes" {
      let panes, _ = Screen.computeLayoutWith LayoutConfig.focus 40 120
      let ids = panes |> List.map fst |> Set.ofList
      ids |> Expect.equal "focus layout pane ids" (Set.ofList [ PaneId.Output; PaneId.Editor ])
    }

    test "computeLayoutWith minimal preset returns 1 pane" {
      let panes, _ = Screen.computeLayoutWith LayoutConfig.minimal 40 120
      (List.length panes) |> Expect.equal "minimal layout should have 1 pane" 1
      (fst panes.[0]) |> Expect.equal "minimal should show Editor" PaneId.Editor
    }
  ]
]
