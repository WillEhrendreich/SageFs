module SageFs.Tests.TuiGuiSharedTests

open Expecto
open Expecto.Flip
open VerifyExpecto
open VerifyTests
open SageFs

/// Layout with all panes visible, for tests that need the Editor pane
let allPanesLayout = {
  LayoutConfig.defaults with
    VisiblePanes = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions ] }

let verifyGrid (name: string) (text: string) =
  SageFs.Tests.TestInfrastructure.Snapshots.verify "TuiGuiSharedTests" name "txt" text

// ─── Helper: build RenderRegion ──────────────────────────────────────────────

let mkRegion id content =
  { Id = id; Content = content; Flags = RegionFlags.None
    Affordances = []; Cursor = None; Completions = None; LineAnnotations = [||] }

let mkRegionWithCursor id content line col =
  { Id = id; Content = content; Flags = RegionFlags.None
    Affordances = []; Cursor = Some { Line = line; Col = col }; Completions = None; LineAnnotations = [||] }

let mkRegionWithCompletions id content items selectedIdx =
  { Id = id; Content = content; Flags = RegionFlags.None
    Affordances = []; Cursor = Some { Line = 0; Col = 0 }
    Completions = Some { Items = items; SelectedIndex = selectedIdx }; LineAnnotations = [||] }

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
    text |> Expect.stringContains "output content should appear in grid" "val myVar"
  }

  test "editor region content appears in grid text" {
    let grid = CellGrid.create 25 60
    let regions = [
      mkRegion "output" ""
      mkRegionWithCursor "editor" "let x = 42" 0 10
    ]
    Screen.drawWith allPanesLayout Theme.defaults grid regions PaneId.Editor Map.empty "left" "right" |> ignore
    let text = CellGrid.toText grid
    text |> Expect.stringContains "editor content should appear in grid" "let x = 42"
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
    text |> Expect.stringContains "sessions content should appear in grid" "session-abc"
  }

  test "empty regions render without crashing" {
    let grid = CellGrid.create 20 60
    Screen.draw grid [] PaneId.Editor Map.empty "status" "hints" |> ignore
    let text = CellGrid.toText grid
    (text.Length, 0) |> Expect.isGreaterThan "should produce non-empty grid"
  }

  test "long output scrolls with offset" {
    let grid = CellGrid.create 20 60
    let lines = [ for i in 1..50 -> sprintf "line %d" i ] |> String.concat "\n"
    let regions = [ mkRegion "output" lines ]
    let scrolled = Map.ofList [ PaneId.Output, 40 ]
    Screen.draw grid regions PaneId.Output scrolled "left" "right" |> ignore
    let text = CellGrid.toText grid
    // With scroll offset 40, later lines should be visible
    text |> Expect.stringContains "scrolled content should be visible" "line 4"
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
    visited.Count |> Expect.equal "should visit all panes" PaneId.all.Length
  }

  test "tab wraps around from Editor back to Tests" {
    // Output -> Sessions -> Context -> Diagnostics -> Editor -> Tests -> Output
    let mutable current = PaneId.Editor
    current <- PaneId.next current
    current |> Expect.equal "should wrap to Tests" PaneId.Tests
  }

  test "navigate right from Output reaches Sessions" {
    let panes, _ = Screen.computeLayout 40 120
    let result = PaneId.navigate Direction.Right PaneId.Output panes
    // Sessions is to the right of Output in default layout
    result |> Expect.equal "right from Output should reach Sessions" PaneId.Sessions
  }

  test "navigate left from Sessions reaches Output or Editor" {
    let panes, _ = Screen.computeLayout 40 120
    let result = PaneId.navigate Direction.Left PaneId.Sessions panes
    // Output or Editor is to the left
    (result = PaneId.Output || result = PaneId.Editor) |> Expect.isTrue "left from Sessions should reach left column"
  }

  test "navigate stays on same pane when no neighbor in direction" {
    // With minimal layout (only Editor), no directional neighbor exists
    let panes, _ = Screen.computeLayoutWith LayoutConfig.minimal 40 120
    let result = PaneId.navigate Direction.Right PaneId.Editor panes
    result |> Expect.equal "should stay on Editor when alone" PaneId.Editor
  }

  test "navigate with focus layout (2 panes)" {
    let panes, _ = Screen.computeLayoutWith LayoutConfig.focus 40 120
    let fromOutput = PaneId.navigate Direction.Down PaneId.Output panes
    fromOutput |> Expect.equal "down from Output should reach Editor in focus layout" PaneId.Editor
    let fromEditor = PaneId.navigate Direction.Up PaneId.Editor panes
    fromEditor |> Expect.equal "up from Editor should reach Output in focus layout" PaneId.Output
  }

  test "navigate with hidden Sessions pane" {
    let cfg = LayoutConfig.togglePane PaneId.Sessions LayoutConfig.defaults
    let panes, _ = Screen.computeLayoutWith cfg 40 120
    let ids = panes |> List.map fst |> Set.ofList
    (ids.Contains PaneId.Sessions) |> Expect.isFalse "Sessions should be hidden"
    // Right from Output/Editor should NOT reach Sessions
    for (paneId, _) in panes do
      let result = PaneId.navigate Direction.Right paneId panes
      result |> Expect.notEqual "hidden pane should not be navigable" PaneId.Sessions
  }

  test "PaneId.next skips nothing - cycles all 6" {
    let sequence = [
      PaneId.next PaneId.Output      // Sessions
      PaneId.next PaneId.Sessions    // Context
      PaneId.next PaneId.Context     // Diagnostics
      PaneId.next PaneId.Diagnostics // Editor
      PaneId.next PaneId.Editor      // Tests
      PaneId.next PaneId.Tests       // Output
    ]
    sequence |> Expect.equal "next should cycle Output->Sessions->Context->Diag->Editor->Tests->Output" [ PaneId.Sessions; PaneId.Context; PaneId.Diagnostics; PaneId.Editor; PaneId.Tests; PaneId.Output ]
  }
]

// ─── Tier 1: Layout edge cases ───────────────────────────────────────────────

let layoutEdgeCaseTests = testList "layout edge cases" [
  test "tiny terminal 5x5 produces valid layout" {
    let panes, statusRect = Screen.computeLayout 5 5
    for (_, r) in panes do
      (r.Width, 0) |> Expect.isGreaterThanOrEqual "width non-negative"
      (r.Height, 0) |> Expect.isGreaterThanOrEqual "height non-negative"
    statusRect.Height |> Expect.equal "status bar always 1 row" 1
  }

  test "tiny terminal 3x3 doesn't crash" {
    let panes, _ = Screen.computeLayout 3 3
    // Just verify it doesn't throw
    (panes.Length, 0) |> Expect.isGreaterThanOrEqual "should return some layout"
  }

  test "wide terminal 400x10 produces valid layout" {
    let panes, statusRect = Screen.computeLayoutWith LayoutConfig.defaults 10 400
    for (_, r) in panes do
      (r.Width, 0) |> Expect.isGreaterThanOrEqual "width non-negative"
      ((r.Col + r.Width), 400) |> Expect.isLessThanOrEqual "pane shouldn't exceed terminal width"
    statusRect.Width |> Expect.equal "status bar spans full width" 400
  }

  test "tall terminal 20x200 produces valid layout" {
    let panes, statusRect = Screen.computeLayoutWith LayoutConfig.defaults 200 20
    for (_, r) in panes do
      (r.Height, 0) |> Expect.isGreaterThanOrEqual "height non-negative"
      ((r.Row + r.Height), statusRect.Row) |> Expect.isLessThanOrEqual "pane shouldn't overlap status"
    statusRect.Row |> Expect.equal "status bar on last row" 199
  }

  test "no pane overlaps any other pane" {
    for rows in [ 10; 20; 40; 80 ] do
      for cols in [ 20; 60; 120; 200 ] do
        let panes, statusRect = Screen.computeLayout rows cols
        for i in 0 .. panes.Length - 1 do
          let (idA, a) = panes.[i]
          // Don't overlap status
          ((a.Row + a.Height), statusRect.Row) |> Expect.isLessThanOrEqual (sprintf "%A at %dx%d overlaps status" idA rows cols)
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
        ((r.Width * r.Height), 0) |> Expect.isGreaterThan (sprintf "%A should have positive area" id)
  }

  test "minimal layout gives Editor the full content area" {
    let panes, statusRect = Screen.computeLayoutWith LayoutConfig.minimal 40 120
    panes.Length |> Expect.equal "minimal has 1 pane" 1
    let (id, r) = panes.[0]
    id |> Expect.equal "should be Editor" PaneId.Editor
    r.Col |> Expect.equal "starts at col 0" 0
    r.Row |> Expect.equal "starts at row 0" 0
    r.Width |> Expect.equal "spans full width" 120
    r.Height |> Expect.equal "spans full height minus status" (statusRect.Row)
  }

  test "computeLayoutWith with Diagnostics visible returns 4 panes" {
    let cfg = { LayoutConfig.defaults with VisiblePanes = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions; PaneId.Diagnostics ] }
    let panes, _ = Screen.computeLayoutWith cfg 40 120
    let ids = panes |> List.map fst |> Set.ofList
    ids.Count |> Expect.equal "should have 4 panes" 4
    (ids.Contains PaneId.Diagnostics) |> Expect.isTrue "should include Diagnostics"
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
    odBg |> Expect.notEqual "One Dark and Dracula should have different backgrounds" drBg
  }

  test "theme colors reach status bar" {
    let grid = CellGrid.create 20 60
    Screen.drawWith LayoutConfig.defaults ThemePresets.dracula grid [] PaneId.Editor Map.empty "test" "theme" |> ignore
    let statusRow = 19 // last row
    let statusBg = (CellGrid.get grid statusRow 0).Bg
    let draculaStatusBg = Theme.hexToRgb ThemePresets.dracula.BgStatus
    statusBg |> Expect.equal "status bar should use Dracula status bg" draculaStatusBg
  }

  test "theme colors reach pane borders" {
    let grid = CellGrid.create 30 80
    let regions = [ mkRegion "output" "test"; mkRegion "editor" ""; mkRegion "sessions" "" ]
    // Output is NOT focused, so its border should be BorderNormal
    Screen.drawWith LayoutConfig.defaults ThemePresets.nordic grid regions PaneId.Editor Map.empty "s" "r" |> ignore
    // Top-left corner of grid is Output pane border (unfocused)
    let topLeft = CellGrid.get grid 0 0
    let expectedBorderFg = Theme.hexToRgb ThemePresets.nordic.BorderNormal
    topLeft.Fg |> Expect.equal "unfocused pane border should use Nordic's BorderNormal color" expectedBorderFg
  }

  test "focused pane border uses BorderFocus color" {
    let grid = CellGrid.create 30 80
    let regions = [ mkRegion "output" "test"; mkRegionWithCursor "editor" "code" 0 0 ]
    Screen.drawWith LayoutConfig.defaults ThemePresets.gruvbox grid regions PaneId.Output Map.empty "s" "r" |> ignore
    // The Output pane is focused — its border should use BorderFocus
    let topLeft = CellGrid.get grid 0 0
    let focusBorderFg = Theme.hexToRgb ThemePresets.gruvbox.BorderFocus
    topLeft.Fg |> Expect.equal "focused pane border should use BorderFocus color" focusBorderFg
  }

  test "all 8 presets render without crashing" {
    for (name, theme) in ThemePresets.all do
      let grid = CellGrid.create 20 60
      let regions = [ mkRegion "output" "test"; mkRegionWithCursor "editor" "code" 0 0 ]
      Screen.drawWith LayoutConfig.defaults theme grid regions PaneId.Editor Map.empty name "test" |> ignore
      let text = CellGrid.toText grid
      (text.Length, 0) |> Expect.isGreaterThan (sprintf "%s should render" name)
  }

  test "theme name appears in status bar" {
    let grid = CellGrid.create 20 60
    Screen.drawWith LayoutConfig.defaults Theme.defaults grid [] PaneId.Editor Map.empty "left" "Nordic ✓" |> ignore
    let text = CellGrid.toText grid
    text |> Expect.stringContains "status bar should show theme name" "Nordic"
  }
]

// ─── Tier 2: Status bar format ───────────────────────────────────────────────

let statusBarTests = testList "status bar format" [
  test "status bar shows left text" {
    let grid = CellGrid.create 10 40
    Screen.draw grid [] PaneId.Editor Map.empty "Session: abc123" "theme" |> ignore
    let text = CellGrid.toText grid
    text |> Expect.stringContains "left status should appear" "Session: abc123"
  }

  test "status bar shows right text" {
    let grid = CellGrid.create 10 40
    Screen.draw grid [] PaneId.Editor Map.empty "left" "My Theme" |> ignore
    let text = CellGrid.toText grid
    text |> Expect.stringContains "right status should appear" "My Theme"
  }

  test "status bar occupies last row" {
    let grid = CellGrid.create 20 60
    Screen.draw grid [] PaneId.Editor Map.empty "STATUS" "RIGHT" |> ignore
    // Extract just the last row (row 19, col 0 to col 59)
    let lastRow = CellGrid.toTextRange grid 19 0 19 59
    lastRow |> Expect.stringContains "last row should contain left status" "STATUS"
  }

  test "long left text doesn't overflow into right text" {
    let longLeft = String.replicate 30 "A"
    let grid = CellGrid.create 10 40
    Screen.draw grid [] PaneId.Editor Map.empty longLeft "ZZZ" |> ignore
    // Extract last row
    let lastRow = CellGrid.toTextRange grid 9 0 9 39
    lastRow |> Expect.stringContains "left text should appear" "A"
  }
]

// ─── Tier 2: Resize clamping ─────────────────────────────────────────────────

let resizeClampingTests = testList "resize clamping" [
  test "resizeH clamps at lower bound" {
    let cfg = LayoutConfig.resizeH -1000 LayoutConfig.defaults
    (cfg.LeftRightSplit, 0.2) |> Expect.isGreaterThanOrEqual "LeftRightSplit should not go below 0.2"
  }

  test "resizeH clamps at upper bound" {
    let cfg = LayoutConfig.resizeH 1000 LayoutConfig.defaults
    (cfg.LeftRightSplit, 0.9) |> Expect.isLessThanOrEqual "LeftRightSplit should not exceed 0.9"
  }

  test "resizeH increments by 0.05 per step" {
    let cfg = LayoutConfig.resizeH 1 LayoutConfig.defaults
    cfg.LeftRightSplit |> Expect.floatClose "should increase by 0.05" Accuracy.medium 0.70
    let cfg2 = LayoutConfig.resizeH -1 LayoutConfig.defaults
    cfg2.LeftRightSplit |> Expect.floatClose "should decrease by 0.05" Accuracy.medium 0.60
  }

  test "resizeV keeps editor rows >= 2" {
    let cfg = LayoutConfig.resizeV -1000 LayoutConfig.defaults
    (cfg.OutputEditorSplit, 2) |> Expect.isGreaterThanOrEqual "editor rows should not go below 2"
  }

  test "resizeV increments by 1 row" {
    let cfg = LayoutConfig.resizeV 1 LayoutConfig.defaults
    cfg.OutputEditorSplit |> Expect.equal "should increase by 1" 7
    let cfg2 = LayoutConfig.resizeV -1 LayoutConfig.defaults
    cfg2.OutputEditorSplit |> Expect.equal "should decrease by 1" 5
  }

  test "resizeR clamps between 0.1 and 0.9" {
    let cfgLow = LayoutConfig.resizeR -1000 LayoutConfig.defaults
    (cfgLow.SessionsDiagSplit, 0.1) |> Expect.isGreaterThanOrEqual "SessionsDiagSplit >= 0.1"
    let cfgHigh = LayoutConfig.resizeR 1000 LayoutConfig.defaults
    (cfgHigh.SessionsDiagSplit, 0.9) |> Expect.isLessThanOrEqual "SessionsDiagSplit <= 0.9"
  }

  test "resizeR increments by 0.05" {
    let cfg = LayoutConfig.resizeR 1 LayoutConfig.defaults
    cfg.SessionsDiagSplit |> Expect.floatClose "should increase by 0.05" Accuracy.medium 0.55
  }

  test "repeated resize stays within bounds" {
    let mutable cfg = LayoutConfig.defaults
    for _ in 1..100 do cfg <- LayoutConfig.resizeH 1 cfg
    (cfg.LeftRightSplit, 0.9) |> Expect.isLessThanOrEqual "H should stay <= 0.9 after 100 increases"
    for _ in 1..200 do cfg <- LayoutConfig.resizeH -1 cfg
    (cfg.LeftRightSplit, 0.2) |> Expect.isGreaterThanOrEqual "H should stay >= 0.2 after 200 decreases"
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
    text |> Expect.stringContains "completion items should appear in grid" "List"
  }

  test "completion with no items renders without crash" {
    let grid = CellGrid.create 20 60
    let regions = [
      mkRegionWithCompletions "editor" "let x = " [] 0
    ]
    Screen.drawWith allPanesLayout Theme.defaults grid regions PaneId.Editor Map.empty "s" "r" |> ignore
    ((CellGrid.toText grid).Length, 0) |> Expect.isGreaterThan "grid should render"
  }

  test "selected completion index is highlighted" {
    let grid = CellGrid.create 25 60
    let regions = [
      mkRegionWithCompletions "editor" "let x = Lis"
        [ "List"; "List.map"; "List.filter" ] 1
    ]
    Screen.drawWith allPanesLayout Theme.defaults grid regions PaneId.Editor Map.empty "s" "r" |> ignore
    let text = CellGrid.toText grid
    text |> Expect.stringContains "selected item should be visible" "List.map"
  }
]

// ─── Tier 3: Raylib key mapping (testable without Raylib) ────────────────────

let raylibKeyMappingTests = testList "Raylib key mapping logic" [
  // Test the ConsoleKey → UiAction mapping (shared KeyMap logic)
  // raylibToConsoleKey — we test via the shared keymap path

  test "Ctrl+Q maps to Quit in default keymap" {
    let combo = KeyCombo.ctrl System.ConsoleKey.Q
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "Ctrl+Q should map to Quit" (Some UiAction.Quit)
  }

  test "Ctrl+H maps to FocusDir Left" {
    let combo = KeyCombo.ctrl System.ConsoleKey.H
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "Ctrl+H should map to FocusDir Left" (Some (UiAction.FocusDir Direction.Left))
  }

  test "Alt+Enter maps to Editor Submit" {
    let combo = KeyCombo.alt System.ConsoleKey.Enter
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "Alt+Enter should map to Submit" (Some (UiAction.Editor EditorAction.Submit))
  }

  test "PageUp maps to ScrollUp" {
    let combo = KeyCombo.plain System.ConsoleKey.PageUp
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "PageUp should map to ScrollUp" (Some UiAction.ScrollUp)
  }

  test "PageDown maps to ScrollDown" {
    let combo = KeyCombo.plain System.ConsoleKey.PageDown
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "PageDown should map to ScrollDown" (Some UiAction.ScrollDown)
  }

  test "Alt+Up maps to ScrollUp" {
    let combo = KeyCombo.alt System.ConsoleKey.UpArrow
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "Alt+Up should scroll up" (Some UiAction.ScrollUp)
  }

  test "Alt+Down maps to ScrollDown" {
    let combo = KeyCombo.alt System.ConsoleKey.DownArrow
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "Alt+Down should scroll down" (Some UiAction.ScrollDown)
  }

  test "Ctrl+OemPlus maps to FontSizeUp" {
    let combo = KeyCombo.ctrl System.ConsoleKey.OemPlus
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "Ctrl+= should map to FontSizeUp" (Some UiAction.FontSizeUp)
  }

  test "Ctrl+T maps to CycleTheme" {
    let combo = KeyCombo.ctrl System.ConsoleKey.T
    let action = KeyMap.defaults |> Map.tryFind combo
    action |> Expect.equal "Ctrl+T should map to CycleTheme" (Some UiAction.CycleTheme)
  }
]

// ─── Tier 3: Mouse to pane mapping ───────────────────────────────────────────

let mousePaneMappingTests = testList "mouse to pane mapping" [
  test "click in left column hits Output or Editor" {
    let panes, _ = Screen.computeLayout 40 120
    // Left column at default 0.65 split = ~78 cols
    let leftPanes = panes |> List.filter (fun (_, r) -> r.Col < 78)
    let leftIds = leftPanes |> List.map fst |> Set.ofList
    (leftIds.Contains PaneId.Output || leftIds.Contains PaneId.Editor) |> Expect.isTrue "left column should contain Output or Editor"
  }

  test "click in right column hits Sessions" {
    let panes, _ = Screen.computeLayout 40 120
    let rightPanes = panes |> List.filter (fun (_, r) -> r.Col >= 78)
    let rightIds = rightPanes |> List.map fst |> Set.ofList
    (rightIds.Contains PaneId.Sessions) |> Expect.isTrue "right column should contain Sessions"
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
    topLeft |> Expect.isSome "should hit a pane at (1,1)"

    // Bottom-left should be Editor (below output)
    let bottomLeft = hitTest 35 1
    bottomLeft |> Expect.isSome "should hit a pane at (35,1)"

    // Status bar row should hit nothing
    let statusHit = hitTest 39 60
    statusHit |> Expect.isNone "status bar row should not be a pane"
  }

  test "all grid points map to at most one pane" {
    let panes, statusRect = Screen.computeLayout 40 120
    for row in 0 .. 38 do
      for col in 0 .. 119 do
        let hits =
          panes |> List.filter (fun (_, r) ->
            row >= r.Row && row < r.Row + r.Height &&
            col >= r.Col && col < r.Col + r.Width)
        (hits.Length, 1) |> Expect.isLessThanOrEqual (sprintf "(%d,%d) maps to %d panes" row col hits.Length)
  }

  test "navigate via rects matches expected directions" {
    let panes, _ = Screen.computeLayoutWith allPanesLayout 40 120
    // From Output, going Down should reach Editor (both in left column)
    let downFromOutput = PaneId.navigate Direction.Down PaneId.Output panes
    downFromOutput |> Expect.equal "Down from Output should reach Editor" PaneId.Editor
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
