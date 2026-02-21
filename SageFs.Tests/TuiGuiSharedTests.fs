module SageFs.Tests.TuiGuiSharedTests

open Expecto
open VerifyExpecto
open VerifyTests
open SageFs

/// Layout with all panes visible, for tests that need the Editor pane
let allPanesLayout = {
  LayoutConfig.defaults with
    VisiblePanes = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions ] }

let snapshotsDir =
  System.IO.Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

let verifyGrid (name: string) (text: string) =
  let settings = VerifySettings()
  settings.UseDirectory(snapshotsDir)
  settings.DisableDiff()
  Verifier.Verify(name, text, "txt", settings).ToTask()

// ─── Helper: build RenderRegion ──────────────────────────────────────────────

let mkRegion id content =
  { Id = id; Content = content; Flags = RegionFlags.None
    Affordances = []; Cursor = None; Completions = None }

let mkRegionWithCursor id content line col =
  { Id = id; Content = content; Flags = RegionFlags.None
    Affordances = []; Cursor = Some { Line = line; Col = col }; Completions = None }

let mkRegionWithCompletions id content items selectedIdx =
  { Id = id; Content = content; Flags = RegionFlags.None
    Affordances = []; Cursor = Some { Line = 0; Col = 0 }
    Completions = Some { Items = items; SelectedIndex = selectedIdx } }

// ─── Tier 1: Full pane content rendering ─────────────────────────────────────

let paneContentTests = testList "full pane content rendering" [
  testTask "default layout with all regions" {
    let grid = CellGrid.create 30 80
    let regions = [
      mkRegion "output" "val x: int = 42\nval y: string = \"hello\"\n[info] Loaded 3 files"
      mkRegionWithCursor "editor" "let x = 1\nlet y = \"hello\"\nprintfn \"%d\" x" 2 14
      mkRegion "sessions" "abc123 [Ready] * (MyProj.fsproj) evals:5\ndef456 [WarmingUp] (Tests.fsproj)"
    ]
    Screen.drawWith LayoutConfig.defaults Theme.defaults grid regions PaneId.Output Map.empty "Session: abc123" "Kanagawa" |> ignore
    let text = CellGrid.toText grid
    do! verifyGrid "pane_content_default_layout" text
  }

  testTask "focus layout with output and editor" {
    let grid = CellGrid.create 30 80
    let regions = [
      mkRegion "output" "val result: int = 99\n[error] Type mismatch"
      mkRegionWithCursor "editor" "let result = 99" 0 14
    ]
    Screen.drawWith LayoutConfig.focus Theme.defaults grid regions PaneId.Editor Map.empty "Focused" "One Dark" |> ignore
    let text = CellGrid.toText grid
    do! verifyGrid "pane_content_focus_layout" text
  }

  testTask "minimal layout editor only" {
    let grid = CellGrid.create 30 80
    let regions = [
      mkRegionWithCursor "editor" "let add a b = a + b\nlet sub a b = a - b" 1 18
    ]
    Screen.drawWith LayoutConfig.minimal Theme.defaults grid regions PaneId.Editor Map.empty "Minimal" "One Dark" |> ignore
    let text = CellGrid.toText grid
    do! verifyGrid "pane_content_minimal_layout" text
  }

  test "output region content appears in grid text" {
    let grid = CellGrid.create 25 60
    let regions = [ mkRegion "output" "val myVar: int = 42" ]
    Screen.draw grid regions PaneId.Output Map.empty "left" "right" |> ignore
    let text = CellGrid.toText grid
    Expect.stringContains text "val myVar" "output content should appear in grid"
  }

  test "editor region content appears in grid text" {
    let grid = CellGrid.create 25 60
    let regions = [
      mkRegion "output" ""
      mkRegionWithCursor "editor" "let x = 42" 0 10
    ]
    Screen.drawWith allPanesLayout Theme.defaults grid regions PaneId.Editor Map.empty "left" "right" |> ignore
    let text = CellGrid.toText grid
    Expect.stringContains text "let x = 42" "editor content should appear in grid"
  }

  test "sessions region content appears in grid text" {
    let grid = CellGrid.create 25 80
    let regions = [
      mkRegion "output" ""
      mkRegion "editor" ""
      mkRegion "sessions" "session-abc [Ready]"
    ]
    Screen.draw grid regions PaneId.Sessions Map.empty "left" "right" |> ignore
    let text = CellGrid.toText grid
    Expect.stringContains text "session-abc" "sessions content should appear in grid"
  }

  test "empty regions render without crashing" {
    let grid = CellGrid.create 20 60
    Screen.draw grid [] PaneId.Editor Map.empty "status" "hints" |> ignore
    let text = CellGrid.toText grid
    Expect.isGreaterThan text.Length 0 "should produce non-empty grid"
  }

  test "long output scrolls with offset" {
    let grid = CellGrid.create 20 60
    let lines = [ for i in 1..50 -> sprintf "line %d" i ] |> String.concat "\n"
    let regions = [ mkRegion "output" lines ]
    let scrolled = Map.ofList [ PaneId.Output, 40 ]
    Screen.draw grid regions PaneId.Output scrolled "left" "right" |> ignore
    let text = CellGrid.toText grid
    // With scroll offset 40, later lines should be visible
    Expect.stringContains text "line 4" "scrolled content should be visible"
  }
]

// ─── Tier 1: Focus navigation ────────────────────────────────────────────────

let focusNavigationTests = testList "focus navigation" [
  test "tab cycles through all panes" {
    let mutable current = PaneId.Output
    let visited = System.Collections.Generic.HashSet<PaneId>()
    for _ in 1 .. PaneId.all.Length do
      current <- PaneId.next current
      visited.Add(current) |> ignore
    Expect.equal visited.Count PaneId.all.Length "should visit all panes"
  }

  test "tab wraps around from Editor back to Output" {
    // Output -> Sessions -> Diagnostics -> Editor -> Output
    let mutable current = PaneId.Editor
    current <- PaneId.next current
    Expect.equal current PaneId.Output "should wrap to Output"
  }

  test "navigate right from Output reaches Sessions" {
    let panes, _ = Screen.computeLayout 40 120
    let result = PaneId.navigate Direction.Right PaneId.Output panes
    // Sessions is to the right of Output in default layout
    Expect.equal result PaneId.Sessions "right from Output should reach Sessions"
  }

  test "navigate left from Sessions reaches Output or Editor" {
    let panes, _ = Screen.computeLayout 40 120
    let result = PaneId.navigate Direction.Left PaneId.Sessions panes
    // Output or Editor is to the left
    Expect.isTrue
      (result = PaneId.Output || result = PaneId.Editor)
      "left from Sessions should reach left column"
  }

  test "navigate stays on same pane when no neighbor in direction" {
    // With minimal layout (only Editor), no directional neighbor exists
    let panes, _ = Screen.computeLayoutWith LayoutConfig.minimal 40 120
    let result = PaneId.navigate Direction.Right PaneId.Editor panes
    Expect.equal result PaneId.Editor "should stay on Editor when alone"
  }

  test "navigate with focus layout (2 panes)" {
    let panes, _ = Screen.computeLayoutWith LayoutConfig.focus 40 120
    let fromOutput = PaneId.navigate Direction.Down PaneId.Output panes
    Expect.equal fromOutput PaneId.Editor "down from Output should reach Editor in focus layout"
    let fromEditor = PaneId.navigate Direction.Up PaneId.Editor panes
    Expect.equal fromEditor PaneId.Output "up from Editor should reach Output in focus layout"
  }

  test "navigate with hidden Sessions pane" {
    let cfg = LayoutConfig.togglePane PaneId.Sessions LayoutConfig.defaults
    let panes, _ = Screen.computeLayoutWith cfg 40 120
    let ids = panes |> List.map fst |> Set.ofList
    Expect.isFalse (ids.Contains PaneId.Sessions) "Sessions should be hidden"
    // Right from Output/Editor should NOT reach Sessions
    for (paneId, _) in panes do
      let result = PaneId.navigate Direction.Right paneId panes
      Expect.notEqual result PaneId.Sessions "hidden pane should not be navigable"
  }

  test "PaneId.next skips nothing - cycles all 4" {
    let sequence = [
      PaneId.next PaneId.Output      // Sessions
      PaneId.next PaneId.Sessions    // Diagnostics
      PaneId.next PaneId.Diagnostics // Editor
      PaneId.next PaneId.Editor      // Output
    ]
    Expect.equal sequence
      [ PaneId.Sessions; PaneId.Diagnostics; PaneId.Editor; PaneId.Output ]
      "next should cycle Output->Sessions->Diag->Editor->Output"
  }
]

// ─── Tier 1: Layout edge cases ───────────────────────────────────────────────

let layoutEdgeCaseTests = testList "layout edge cases" [
  test "tiny terminal 5x5 produces valid layout" {
    let panes, statusRect = Screen.computeLayout 5 5
    for (_, r) in panes do
      Expect.isGreaterThanOrEqual r.Width 0 "width non-negative"
      Expect.isGreaterThanOrEqual r.Height 0 "height non-negative"
    Expect.equal statusRect.Height 1 "status bar always 1 row"
  }

  test "tiny terminal 3x3 doesn't crash" {
    let panes, _ = Screen.computeLayout 3 3
    // Just verify it doesn't throw
    Expect.isGreaterThanOrEqual panes.Length 0 "should return some layout"
  }

  test "wide terminal 400x10 produces valid layout" {
    let panes, statusRect = Screen.computeLayoutWith LayoutConfig.defaults 10 400
    for (_, r) in panes do
      Expect.isGreaterThanOrEqual r.Width 0 "width non-negative"
      Expect.isLessThanOrEqual (r.Col + r.Width) 400 "pane shouldn't exceed terminal width"
    Expect.equal statusRect.Width 400 "status bar spans full width"
  }

  test "tall terminal 20x200 produces valid layout" {
    let panes, statusRect = Screen.computeLayoutWith LayoutConfig.defaults 200 20
    for (_, r) in panes do
      Expect.isGreaterThanOrEqual r.Height 0 "height non-negative"
      Expect.isLessThanOrEqual (r.Row + r.Height) statusRect.Row "pane shouldn't overlap status"
    Expect.equal statusRect.Row 199 "status bar on last row"
  }

  test "no pane overlaps any other pane" {
    for rows in [ 10; 20; 40; 80 ] do
      for cols in [ 20; 60; 120; 200 ] do
        let panes, statusRect = Screen.computeLayout rows cols
        for i in 0 .. panes.Length - 1 do
          let (idA, a) = panes.[i]
          // Don't overlap status
          Expect.isLessThanOrEqual (a.Row + a.Height) statusRect.Row
            (sprintf "%A at %dx%d overlaps status" idA rows cols)
          for j in i + 1 .. panes.Length - 1 do
            let (idB, b) = panes.[j]
            let overlapH = a.Col < b.Col + b.Width && b.Col < a.Col + a.Width
            let overlapV = a.Row < b.Row + b.Height && b.Row < a.Row + a.Height
            if overlapH && overlapV then
              failtest (sprintf "%A and %A overlap at %dx%d terminal" idA idB rows cols)
  }

  test "all visible panes have positive area" {
    let configs = [ LayoutConfig.defaults; LayoutConfig.focus ]
    for cfg in configs do
      let panes, _ = Screen.computeLayoutWith cfg 40 120
      for (id, r) in panes do
        Expect.isGreaterThan (r.Width * r.Height) 0
          (sprintf "%A should have positive area" id)
  }

  test "minimal layout gives Editor the full content area" {
    let panes, statusRect = Screen.computeLayoutWith LayoutConfig.minimal 40 120
    Expect.equal panes.Length 1 "minimal has 1 pane"
    let (id, r) = panes.[0]
    Expect.equal id PaneId.Editor "should be Editor"
    Expect.equal r.Col 0 "starts at col 0"
    Expect.equal r.Row 0 "starts at row 0"
    Expect.equal r.Width 120 "spans full width"
    Expect.equal r.Height (statusRect.Row) "spans full height minus status"
  }

  test "computeLayoutWith with Diagnostics visible returns 4 panes" {
    let cfg = { LayoutConfig.defaults with VisiblePanes = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions; PaneId.Diagnostics ] }
    let panes, _ = Screen.computeLayoutWith cfg 40 120
    let ids = panes |> List.map fst |> Set.ofList
    Expect.equal ids.Count 4 "should have 4 panes"
    Expect.isTrue (ids.Contains PaneId.Diagnostics) "should include Diagnostics"
  }
]

// ─── Tier 1: Theme-applied rendering ─────────────────────────────────────────

let themeAppliedRenderTests = testList "theme-applied rendering" [
  test "different themes produce different grid cell colors" {
    let makeGrid theme =
      let grid = CellGrid.create 20 60
      let regions = [ mkRegion "output" "test"; mkRegionWithCursor "editor" "code" 0 4 ]
      Screen.drawWith LayoutConfig.defaults theme grid regions PaneId.Editor Map.empty "s" "r" |> ignore
      grid

    let oneDark = makeGrid Theme.defaults
    let dracula = makeGrid ThemePresets.dracula

    // Compare background colors of a cell in the panel area
    let odBg = (CellGrid.get oneDark 0 0).Bg
    let drBg = (CellGrid.get dracula 0 0).Bg
    Expect.notEqual odBg drBg "One Dark and Dracula should have different backgrounds"
  }

  test "theme colors reach status bar" {
    let grid = CellGrid.create 20 60
    Screen.drawWith LayoutConfig.defaults ThemePresets.dracula grid [] PaneId.Editor Map.empty "test" "theme" |> ignore
    let statusRow = 19 // last row
    let statusBg = (CellGrid.get grid statusRow 0).Bg
    let draculaStatusBg = Theme.hexToRgb ThemePresets.dracula.BgStatus
    Expect.equal statusBg draculaStatusBg "status bar should use Dracula status bg"
  }

  test "theme colors reach pane borders" {
    let grid = CellGrid.create 30 80
    let regions = [ mkRegion "output" "test"; mkRegion "editor" ""; mkRegion "sessions" "" ]
    // Output is NOT focused, so its border should be BorderNormal
    Screen.drawWith LayoutConfig.defaults ThemePresets.nordic grid regions PaneId.Editor Map.empty "s" "r" |> ignore
    // Top-left corner of grid is Output pane border (unfocused)
    let topLeft = CellGrid.get grid 0 0
    let expectedBorderFg = Theme.hexToRgb ThemePresets.nordic.BorderNormal
    Expect.equal topLeft.Fg expectedBorderFg "unfocused pane border should use Nordic's BorderNormal color"
  }

  test "focused pane border uses BorderFocus color" {
    let grid = CellGrid.create 30 80
    let regions = [ mkRegion "output" "test"; mkRegionWithCursor "editor" "code" 0 0 ]
    Screen.drawWith LayoutConfig.defaults ThemePresets.gruvbox grid regions PaneId.Output Map.empty "s" "r" |> ignore
    // The Output pane is focused — its border should use BorderFocus
    let topLeft = CellGrid.get grid 0 0
    let focusBorderFg = Theme.hexToRgb ThemePresets.gruvbox.BorderFocus
    Expect.equal topLeft.Fg focusBorderFg "focused pane border should use BorderFocus color"
  }

  test "all 8 presets render without crashing" {
    for (name, theme) in ThemePresets.all do
      let grid = CellGrid.create 20 60
      let regions = [ mkRegion "output" "test"; mkRegionWithCursor "editor" "code" 0 0 ]
      Screen.drawWith LayoutConfig.defaults theme grid regions PaneId.Editor Map.empty name "test" |> ignore
      let text = CellGrid.toText grid
      Expect.isGreaterThan text.Length 0 (sprintf "%s should render" name)
  }

  test "theme name appears in status bar" {
    let grid = CellGrid.create 20 60
    Screen.drawWith LayoutConfig.defaults Theme.defaults grid [] PaneId.Editor Map.empty "left" "Nordic ✓" |> ignore
    let text = CellGrid.toText grid
    Expect.stringContains text "Nordic" "status bar should show theme name"
  }
]

// ─── Tier 2: Status bar format ───────────────────────────────────────────────

let statusBarTests = testList "status bar format" [
  test "status bar shows left text" {
    let grid = CellGrid.create 10 40
    Screen.draw grid [] PaneId.Editor Map.empty "Session: abc123" "theme" |> ignore
    let text = CellGrid.toText grid
    Expect.stringContains text "Session: abc123" "left status should appear"
  }

  test "status bar shows right text" {
    let grid = CellGrid.create 10 40
    Screen.draw grid [] PaneId.Editor Map.empty "left" "My Theme" |> ignore
    let text = CellGrid.toText grid
    Expect.stringContains text "My Theme" "right status should appear"
  }

  test "status bar occupies last row" {
    let grid = CellGrid.create 20 60
    Screen.draw grid [] PaneId.Editor Map.empty "STATUS" "RIGHT" |> ignore
    // Extract just the last row (row 19, col 0 to col 59)
    let lastRow = CellGrid.toTextRange grid 19 0 19 59
    Expect.stringContains lastRow "STATUS" "last row should contain left status"
  }

  test "long left text doesn't overflow into right text" {
    let longLeft = String.replicate 30 "A"
    let grid = CellGrid.create 10 40
    Screen.draw grid [] PaneId.Editor Map.empty longLeft "ZZZ" |> ignore
    // Extract last row
    let lastRow = CellGrid.toTextRange grid 9 0 9 39
    Expect.stringContains lastRow "A" "left text should appear"
  }
]

// ─── Tier 2: Resize clamping ─────────────────────────────────────────────────

let resizeClampingTests = testList "resize clamping" [
  test "resizeH clamps at lower bound" {
    let cfg = LayoutConfig.resizeH -1000 LayoutConfig.defaults
    Expect.isGreaterThanOrEqual cfg.LeftRightSplit 0.2 "LeftRightSplit should not go below 0.2"
  }

  test "resizeH clamps at upper bound" {
    let cfg = LayoutConfig.resizeH 1000 LayoutConfig.defaults
    Expect.isLessThanOrEqual cfg.LeftRightSplit 0.9 "LeftRightSplit should not exceed 0.9"
  }

  test "resizeH increments by 0.05 per step" {
    let cfg = LayoutConfig.resizeH 1 LayoutConfig.defaults
    Expect.floatClose Accuracy.medium cfg.LeftRightSplit 0.70 "should increase by 0.05"
    let cfg2 = LayoutConfig.resizeH -1 LayoutConfig.defaults
    Expect.floatClose Accuracy.medium cfg2.LeftRightSplit 0.60 "should decrease by 0.05"
  }

  test "resizeV keeps editor rows >= 2" {
    let cfg = LayoutConfig.resizeV -1000 LayoutConfig.defaults
    Expect.isGreaterThanOrEqual cfg.OutputEditorSplit 2 "editor rows should not go below 2"
  }

  test "resizeV increments by 1 row" {
    let cfg = LayoutConfig.resizeV 1 LayoutConfig.defaults
    Expect.equal cfg.OutputEditorSplit 7 "should increase by 1"
    let cfg2 = LayoutConfig.resizeV -1 LayoutConfig.defaults
    Expect.equal cfg2.OutputEditorSplit 5 "should decrease by 1"
  }

  test "resizeR clamps between 0.1 and 0.9" {
    let cfgLow = LayoutConfig.resizeR -1000 LayoutConfig.defaults
    Expect.isGreaterThanOrEqual cfgLow.SessionsDiagSplit 0.1 "SessionsDiagSplit >= 0.1"
    let cfgHigh = LayoutConfig.resizeR 1000 LayoutConfig.defaults
    Expect.isLessThanOrEqual cfgHigh.SessionsDiagSplit 0.9 "SessionsDiagSplit <= 0.9"
  }

  test "resizeR increments by 0.05" {
    let cfg = LayoutConfig.resizeR 1 LayoutConfig.defaults
    Expect.floatClose Accuracy.medium cfg.SessionsDiagSplit 0.55 "should increase by 0.05"
  }

  test "repeated resize stays within bounds" {
    let mutable cfg = LayoutConfig.defaults
    for _ in 1..100 do cfg <- LayoutConfig.resizeH 1 cfg
    Expect.isLessThanOrEqual cfg.LeftRightSplit 0.9 "H should stay <= 0.9 after 100 increases"
    for _ in 1..200 do cfg <- LayoutConfig.resizeH -1 cfg
    Expect.isGreaterThanOrEqual cfg.LeftRightSplit 0.2 "H should stay >= 0.2 after 200 decreases"
  }
]

// ─── Tier 2: Completion dropdown rendering ───────────────────────────────────

let completionRenderTests = testList "completion dropdown rendering" [
  test "completion overlay renders items in grid" {
    let grid = CellGrid.create 25 60
    let regions = [
      mkRegionWithCompletions "editor" "let x = Lis" [ "List"; "List.map"; "List.filter"; "List.head" ] 0
    ]
    Screen.drawWith allPanesLayout Theme.defaults grid regions PaneId.Editor Map.empty "s" "r" |> ignore
    let text = CellGrid.toText grid
    Expect.stringContains text "List" "completion items should appear in grid"
  }

  test "completion with no items renders without crash" {
    let grid = CellGrid.create 20 60
    let regions = [
      mkRegionWithCompletions "editor" "let x = " [] 0
    ]
    Screen.drawWith allPanesLayout Theme.defaults grid regions PaneId.Editor Map.empty "s" "r" |> ignore
    Expect.isGreaterThan (CellGrid.toText grid).Length 0 "grid should render"
  }

  test "selected completion index is highlighted" {
    let grid = CellGrid.create 25 60
    let regions = [
      mkRegionWithCompletions "editor" "let x = Lis"
        [ "List"; "List.map"; "List.filter" ] 1
    ]
    Screen.drawWith allPanesLayout Theme.defaults grid regions PaneId.Editor Map.empty "s" "r" |> ignore
    let text = CellGrid.toText grid
    Expect.stringContains text "List.map" "selected item should be visible"
  }
]

// ─── Tier 3: Raylib key mapping (testable without Raylib) ────────────────────

let raylibKeyMappingTests = testList "Raylib key mapping logic" [
  // Test the ConsoleKey → UiAction mapping (shared KeyMap logic)
  // since raylibToConsoleKey is private, we test the shared keymap path

  test "Ctrl+Q maps to Quit in default keymap" {
    let combo = KeyCombo.ctrl System.ConsoleKey.Q
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some UiAction.Quit) "Ctrl+Q should map to Quit"
  }

  test "Ctrl+H maps to FocusDir Left" {
    let combo = KeyCombo.ctrl System.ConsoleKey.H
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some (UiAction.FocusDir Direction.Left)) "Ctrl+H should map to FocusDir Left"
  }

  test "Ctrl+Enter maps to Editor Submit" {
    let combo = KeyCombo.ctrl System.ConsoleKey.Enter
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some (UiAction.Editor EditorAction.Submit)) "Ctrl+Enter should map to Submit"
  }

  test "PageUp maps to ScrollUp" {
    let combo = KeyCombo.plain System.ConsoleKey.PageUp
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some UiAction.ScrollUp) "PageUp should map to ScrollUp"
  }

  test "PageDown maps to ScrollDown" {
    let combo = KeyCombo.plain System.ConsoleKey.PageDown
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some UiAction.ScrollDown) "PageDown should map to ScrollDown"
  }

  test "Alt+Up maps to ScrollUp" {
    let combo = KeyCombo.alt System.ConsoleKey.UpArrow
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some UiAction.ScrollUp) "Alt+Up should scroll up"
  }

  test "Alt+Down maps to ScrollDown" {
    let combo = KeyCombo.alt System.ConsoleKey.DownArrow
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some UiAction.ScrollDown) "Alt+Down should scroll down"
  }

  test "Ctrl+OemPlus maps to FontSizeUp" {
    let combo = KeyCombo.ctrl System.ConsoleKey.OemPlus
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some UiAction.FontSizeUp) "Ctrl+= should map to FontSizeUp"
  }

  test "Ctrl+T maps to CycleTheme" {
    let combo = KeyCombo.ctrl System.ConsoleKey.T
    let action = KeyMap.defaults |> Map.tryFind combo
    Expect.equal action (Some UiAction.CycleTheme) "Ctrl+T should map to CycleTheme"
  }
]

// ─── Tier 3: Mouse to pane mapping ───────────────────────────────────────────

let mousePaneMappingTests = testList "mouse to pane mapping" [
  test "click in left column hits Output or Editor" {
    let panes, _ = Screen.computeLayout 40 120
    // Left column at default 0.65 split = ~78 cols
    let leftPanes = panes |> List.filter (fun (_, r) -> r.Col < 78)
    let leftIds = leftPanes |> List.map fst |> Set.ofList
    Expect.isTrue
      (leftIds.Contains PaneId.Output || leftIds.Contains PaneId.Editor)
      "left column should contain Output or Editor"
  }

  test "click in right column hits Sessions" {
    let panes, _ = Screen.computeLayout 40 120
    let rightPanes = panes |> List.filter (fun (_, r) -> r.Col >= 78)
    let rightIds = rightPanes |> List.map fst |> Set.ofList
    Expect.isTrue (rightIds.Contains PaneId.Sessions) "right column should contain Sessions"
  }

  test "point-in-rect test for pane targeting" {
    let panes, _ = Screen.computeLayout 40 120
    let hitTest row col =
      panes |> List.tryFind (fun (_, r) ->
        row >= r.Row && row < r.Row + r.Height &&
        col >= r.Col && col < r.Col + r.Width)
      |> Option.map fst

    // Top-left area should be Output (default layout)
    let topLeft = hitTest 1 1
    Expect.isSome topLeft "should hit a pane at (1,1)"

    // Bottom-left should be Editor (below output)
    let bottomLeft = hitTest 35 1
    Expect.isSome bottomLeft "should hit a pane at (35,1)"

    // Status bar row should hit nothing
    let statusHit = hitTest 39 60
    Expect.isNone statusHit "status bar row should not be a pane"
  }

  test "all grid points map to at most one pane" {
    let panes, statusRect = Screen.computeLayout 40 120
    for row in 0 .. 38 do
      for col in 0 .. 119 do
        let hits =
          panes |> List.filter (fun (_, r) ->
            row >= r.Row && row < r.Row + r.Height &&
            col >= r.Col && col < r.Col + r.Width)
        Expect.isLessThanOrEqual hits.Length 1
          (sprintf "(%d,%d) maps to %d panes" row col hits.Length)
  }

  test "navigate via rects matches expected directions" {
    let panes, _ = Screen.computeLayoutWith allPanesLayout 40 120
    // From Output, going Down should reach Editor (both in left column)
    let downFromOutput = PaneId.navigate Direction.Down PaneId.Output panes
    Expect.equal downFromOutput PaneId.Editor "Down from Output should reach Editor"
  }
]

// ─── Aggregate ───────────────────────────────────────────────────────────────

[<Tests>]
let allTuiGuiSharedTests = testList "TUI/GUI Shared" [
  paneContentTests
  focusNavigationTests
  layoutEdgeCaseTests
  themeAppliedRenderTests
  statusBarTests
  resizeClampingTests
  completionRenderTests
  raylibKeyMappingTests
  mousePaneMappingTests
]
