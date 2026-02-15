module SageFs.Tests.TerminalUITests

open System
open System.IO
open Expecto
open VerifyExpecto
open VerifyTests
open SageFs

// Helpers
let mkRegion id content : RenderRegion =
  { Id = id; Flags = RegionFlags.None; Content = content; Affordances = [] }

let mkPane id title row col w h focused : TerminalPane =
  { RegionId = id; Title = title; Row = row; Col = col
    Width = w; Height = h; ScrollOffset = 0; Focused = focused }

let key c k mods =
  ConsoleKeyInfo(c, k, (mods &&& 4) <> 0, (mods &&& 2) <> 0, (mods &&& 1) <> 0)


// Phase 1: Terminal Renderer Tests

let terminalRenderTests = testList "TerminalRender" [
  test "fitToWidth pads short string" {
    let result = TerminalRender.fitToWidth 10 "hello"
    Expect.equal result "hello     " "should pad with spaces"
  }

  test "fitToWidth truncates long string" {
    let result = TerminalRender.fitToWidth 5 "hello world"
    Expect.equal result "hello" "should truncate to width"
  }

  test "fitToWidth exact length unchanged" {
    let result = TerminalRender.fitToWidth 5 "12345"
    Expect.equal result "12345" "exact length stays same"
  }

  test "fitToWidth preserves ANSI escapes when truncating" {
    let colored = sprintf "%shello world%s" AnsiCodes.green AnsiCodes.reset
    let result = TerminalRender.fitToWidth 5 colored
    Expect.stringContains result AnsiCodes.green "should preserve ANSI"
    Expect.equal (TerminalRender.visibleLength result) 5 "visible length should be 5"
  }

  test "visibleLength ignores ANSI escapes" {
    let colored = sprintf "%shello%s" AnsiCodes.cyan AnsiCodes.reset
    let result = TerminalRender.visibleLength colored
    Expect.equal result 5 "should only count visible chars"
  }

  test "visibleLines returns correct slice" {
    let content = "line1\nline2\nline3\nline4\nline5"
    let result = TerminalRender.visibleLines content 1 3
    Expect.equal result ["line2"; "line3"; "line4"] "should skip 1, take 3"
  }

  test "visibleLines with zero offset" {
    let content = "a\nb\nc"
    let result = TerminalRender.visibleLines content 0 2
    Expect.equal result ["a"; "b"] "should take first 2"
  }

  test "visibleLines clamps scroll beyond content" {
    let content = "a\nb"
    let result = TerminalRender.visibleLines content 100 5
    Expect.equal result ["a"; "b"] "should clamp to available"
  }

  test "visibleLines empty content" {
    let result = TerminalRender.visibleLines "" 0 5
    Expect.equal result [""] "empty content gives one empty line"
  }
]

let terminalLayoutTests = testList "TerminalLayout" [
  test "compute produces 4 panes" {
    let layout = TerminalLayout.compute 40 120
    Expect.equal layout.Panes.Length 4 "should have 4 panes"
  }

  test "compute has status bar at bottom" {
    let layout = TerminalLayout.compute 40 120
    Expect.equal layout.StatusBarRow 40 "status bar at row 40"
  }

  test "compute editor pane spans full width" {
    let layout = TerminalLayout.compute 40 120
    let editor = layout.Panes |> List.find (fun p -> p.RegionId = "editor")
    Expect.equal editor.Width 120 "editor spans full width"
  }

  test "compute editor is focused by default" {
    let layout = TerminalLayout.compute 40 120
    let editor = layout.Panes |> List.find (fun p -> p.RegionId = "editor")
    Expect.isTrue editor.Focused "editor should be focused"
  }

  test "compute output and right panes share same row range" {
    let layout = TerminalLayout.compute 40 120
    let output = layout.Panes |> List.find (fun p -> p.RegionId = "output")
    let sessions = layout.Panes |> List.find (fun p -> p.RegionId = "sessions")
    let diags = layout.Panes |> List.find (fun p -> p.RegionId = "diagnostics")
    Expect.equal output.Row sessions.Row "output and sessions start at same row"
    let rightBottom = diags.Row + diags.Height
    let leftBottom = output.Row + output.Height
    Expect.equal rightBottom leftBottom "right panes end at same row as output"
  }

  test "compute editor starts after content area" {
    let layout = TerminalLayout.compute 40 120
    let output = layout.Panes |> List.find (fun p -> p.RegionId = "output")
    let editor = layout.Panes |> List.find (fun p -> p.RegionId = "editor")
    Expect.equal editor.Row (output.Row + output.Height) "editor starts after output"
  }

  test "compute right-side panes are adjacent" {
    let layout = TerminalLayout.compute 40 120
    let sessions = layout.Panes |> List.find (fun p -> p.RegionId = "sessions")
    let diags = layout.Panes |> List.find (fun p -> p.RegionId = "diagnostics")
    Expect.equal diags.Row (sessions.Row + sessions.Height) "diagnostics starts after sessions"
  }
]

let renderPaneTests = testList "renderPane" [
  test "renderPane produces ANSI with border chars" {
    let pane = mkPane "output" "Output" 1 1 30 5 false
    let region = mkRegion "output" "hello"
    let result = TerminalRender.renderPane pane (Some region)
    Expect.stringContains result AnsiCodes.boxTL "should have top-left corner"
    Expect.stringContains result AnsiCodes.boxBR "should have bottom-right corner"
    Expect.stringContains result "Output" "should have title"
    Expect.stringContains result "hello" "should have content"
  }

  test "renderPane focused pane uses cyan border" {
    let pane = mkPane "editor" "Editor" 1 1 20 4 true
    let result = TerminalRender.renderPane pane None
    Expect.stringContains result AnsiCodes.cyan "focused pane should use cyan"
  }

  test "renderPane unfocused pane uses dim border" {
    let pane = mkPane "output" "Output" 1 1 20 4 false
    let result = TerminalRender.renderPane pane None
    Expect.stringContains result AnsiCodes.dimWhite "unfocused pane should use dimWhite"
  }

  test "renderPane with None region shows empty lines" {
    let pane = mkPane "output" "Out" 1 1 20 4 false
    let result = TerminalRender.renderPane pane None
    Expect.stringContains result AnsiCodes.boxV "should have side borders"
  }
]

let renderFrameTests = testList "renderFrame" [
  test "renderFrame produces all panes" {
    let layout = TerminalLayout.compute 30 80
    let edR = mkRegion "editor" "let x = 1"
    let outR = mkRegion "output" "result val x = 1"
    let diagR = mkRegion "diagnostics" ""
    let sessR = mkRegion "sessions" "sess-1 running"
    let regions = [ edR; outR; diagR; sessR ]
    let result = TerminalRender.renderFrame layout regions "Ready" 5
    Expect.stringContains result "Editor" "should render editor pane"
    Expect.stringContains result "Output" "should render output pane"
    Expect.stringContains result "Sessions" "should render sessions pane"
    Expect.stringContains result "Diagnostics" "should render diagnostics pane"
    Expect.stringContains result "Ready" "should show session state"
    Expect.stringContains result "evals: 5" "should show eval count"
  }

  test "renderFrame hides cursor during render" {
    let layout = TerminalLayout.compute 20 60
    let result = TerminalRender.renderFrame layout [] "Ready" 0
    Expect.stringContains result AnsiCodes.hideCursor "should hide cursor at start"
  }

  test "renderFrame shows cursor at editor position" {
    let layout = TerminalLayout.compute 20 60
    let regions = [ mkRegion "editor" "hello" ]
    let result = TerminalRender.renderFrame layout regions "Ready" 0
    Expect.stringContains result AnsiCodes.showCursor "should show cursor for focused editor"
  }
]

let statusBarTests = testList "statusBar" [
  test "renderStatusBar contains session state" {
    let result = TerminalRender.renderStatusBar 40 80 "Running" 10 "Editor"
    Expect.stringContains result "Running" "should show session state"
  }

  test "renderStatusBar contains eval count" {
    let result = TerminalRender.renderStatusBar 40 80 "Ready" 42 "Editor"
    Expect.stringContains result "42" "should show eval count"
  }

  test "renderStatusBar contains key hints" {
    let result = TerminalRender.renderStatusBar 40 80 "Ready" 0 "Editor"
    Expect.stringContains result "Ctrl+Enter" "should show eval hint"
    Expect.stringContains result "Tab:focus" "should show focus hint"
  }

  test "renderStatusBar uses inverse video" {
    let result = TerminalRender.renderStatusBar 40 80 "Ready" 0 "Editor"
    Expect.stringContains result AnsiCodes.inverse "should use inverse video"
  }
]

// Phase 2: Terminal Input Tests

let terminalInputTests = testList "TerminalInput" [
  test "Tab cycles focus" {
    let result = TerminalInput.mapKey (key '\t' ConsoleKey.Tab 0)
    Expect.equal result (Some TerminalCommand.CycleFocus) "Tab should cycle focus"
  }

  test "Ctrl+Enter submits" {
    let result = TerminalInput.mapKey (key '\n' ConsoleKey.Enter 1)
    Expect.equal result (Some (TerminalCommand.Action EditorAction.Submit)) "Ctrl+Enter submits"
  }

  test "Enter inserts newline" {
    let result = TerminalInput.mapKey (key '\n' ConsoleKey.Enter 0)
    Expect.equal result (Some (TerminalCommand.Action EditorAction.NewLine)) "Enter inserts newline"
  }

  test "Ctrl+D quits" {
    let result = TerminalInput.mapKey (key '\x04' ConsoleKey.D 1)
    Expect.equal result (Some TerminalCommand.Quit) "Ctrl+D quits"
  }

  test "Ctrl+Q quits" {
    let result = TerminalInput.mapKey (key '\x11' ConsoleKey.Q 1)
    Expect.equal result (Some TerminalCommand.Quit) "Ctrl+Q quits"
  }

  test "Ctrl+C cancels" {
    let result = TerminalInput.mapKey (key '\x03' ConsoleKey.C 1)
    Expect.equal result (Some (TerminalCommand.Action EditorAction.Cancel)) "Ctrl+C cancels"
  }

  test "Arrow up moves cursor" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.UpArrow 0)
    Expect.equal result (Some (TerminalCommand.Action (EditorAction.MoveCursor Direction.Up))) "up arrow"
  }

  test "Arrow left moves cursor" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.LeftArrow 0)
    Expect.equal result (Some (TerminalCommand.Action (EditorAction.MoveCursor Direction.Left))) "left arrow"
  }

  test "Backspace deletes backward" {
    let result = TerminalInput.mapKey (key '\b' ConsoleKey.Backspace 0)
    Expect.equal result (Some (TerminalCommand.Action EditorAction.DeleteBackward)) "backspace"
  }

  test "Printable chars are InsertChar" {
    let result = TerminalInput.mapKey (key 'a' ConsoleKey.A 0)
    Expect.equal result (Some (TerminalCommand.Action (EditorAction.InsertChar 'a'))) "printable char"
  }

  test "Ctrl+L redraws" {
    let result = TerminalInput.mapKey (key '\x0c' ConsoleKey.L 1)
    Expect.equal result (Some TerminalCommand.Redraw) "Ctrl+L redraws"
  }

  test "Alt+Up scrolls up" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.UpArrow 2)
    Expect.equal result (Some TerminalCommand.ScrollUp) "Alt+Up scrolls"
  }

  test "PageDown scrolls down" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.PageDown 0)
    Expect.equal result (Some TerminalCommand.ScrollDown) "PageDown scrolls"
  }
]


let outputColorizerTests = testList "OutputColorizer" [
  test "result lines get green" {
    let result = OutputColorizer.colorize "[12:30:45] [result] val x: int = 42"
    Expect.stringContains result AnsiCodes.green "result should be green"
  }

  test "error lines get red" {
    let result = OutputColorizer.colorize "[12:30:46] [error] FS0001: Type mismatch"
    Expect.stringContains result AnsiCodes.red "error should be red"
  }

  test "info lines get cyan" {
    let result = OutputColorizer.colorize "[12:30:47] [info] Loaded module Foo"
    Expect.stringContains result AnsiCodes.cyan "info should be cyan"
  }

  test "warning lines get yellow" {
    let result = OutputColorizer.colorize "[12:30:49] [warning] Unused variable"
    Expect.stringContains result AnsiCodes.yellow "warning should be yellow"
  }

  test "timestamps are dimmed" {
    let result = OutputColorizer.colorize "[12:30:45] [result] val x: int = 42"
    Expect.stringContains result AnsiCodes.dimWhite "timestamp should be dimmed"
  }

  test "short lines pass through" {
    let result = OutputColorizer.colorize "hi"
    Expect.equal result "hi" "short lines unchanged"
  }

  test "empty lines pass through" {
    let result = OutputColorizer.colorize ""
    Expect.equal result "" "empty lines unchanged"
  }
]


let diagnosticsColorizerTests = testList "DiagnosticsColorizer" [
  test "error diagnostics get red" {
    let result = DiagnosticsColorizer.colorize "[error] (5,10) Type mismatch"
    Expect.stringContains result AnsiCodes.red "error should be red"
  }

  test "warning diagnostics get yellow" {
    let result = DiagnosticsColorizer.colorize "[warning] (1,1) Unused"
    Expect.stringContains result AnsiCodes.yellow "warning should be yellow"
  }

  test "plain lines pass through" {
    let result = DiagnosticsColorizer.colorize "no bracket"
    Expect.equal result "no bracket" "plain lines unchanged"
  }
]


let contentColorizerTests = testList "ContentColorizer" [
  test "output region uses output colorizer" {
    let result = ContentColorizer.colorizeLine "output" "[12:00:00] [error] fail"
    Expect.stringContains result AnsiCodes.red "output error should be red"
  }

  test "diagnostics region uses diagnostics colorizer" {
    let result = ContentColorizer.colorizeLine "diagnostics" "[warning] (1,1) msg"
    Expect.stringContains result AnsiCodes.yellow "diagnostics warning should be yellow"
  }

  test "sessions with active marker get green" {
    let result = ContentColorizer.colorizeLine "sessions" "session-abc [Ready] *"
    Expect.stringContains result AnsiCodes.green "active session should be green"
  }

  test "sessions without active marker are dimmed" {
    let result = ContentColorizer.colorizeLine "sessions" "session-abc [Ready]"
    Expect.stringContains result AnsiCodes.dimWhite "inactive session should be dimmed"
  }

  test "editor region passes through" {
    let result = ContentColorizer.colorizeLine "editor" "let x = 42"
    Expect.equal result "let x = 42" "editor content unchanged"
  }
]


let frameDiffTests = testList "FrameDiff" [
  test "identical frames produce empty diff" {
    let r1 = AnsiCodes.moveTo 1 1
    let r2 = AnsiCodes.moveTo 2 1
    let frame = sprintf "%sHello%sWorld" r1 r2
    let result = FrameDiff.diff frame frame
    Expect.equal result "" "identical frames should produce empty diff"
  }

  test "changed row included in diff" {
    let r1 = AnsiCodes.moveTo 1 1
    let r2 = AnsiCodes.moveTo 2 1
    let prev = sprintf "%sHello%sWorld" r1 r2
    let next = sprintf "%sHello%sChanged" r1 r2
    let result = FrameDiff.diff prev next
    Expect.stringContains result "Changed" "changed row should be in diff"
  }

  test "new row included in diff" {
    let r1 = AnsiCodes.moveTo 1 1
    let r2 = AnsiCodes.moveTo 2 1
    let prev = sprintf "%sHello" r1
    let next = sprintf "%sHello%sNew" r1 r2
    let result = FrameDiff.diff prev next
    Expect.stringContains result "New" "new row should be in diff"
  }
]


// === Snapshot Tests ===

let snapshotsDir = Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

let verifyTerminal name (value: string) =
  let settings = VerifySettings()
  settings.UseDirectory(snapshotsDir)
  Verifier.Verify(name, value, "txt", settings).ToTask()

/// Replace ANSI escape sequences with readable tokens for snapshot comparison
let scrubAnsi (s: string) =
  s
    .Replace("\x1b[?25l", "«hide-cursor»")
    .Replace("\x1b[?25h", "«show-cursor»")
    .Replace("\x1b[0m", "«reset»")
    .Replace("\x1b[7m", "«inverse»")
    .Replace("\x1b[27m", "«/inverse»")
    .Replace(AnsiCodes.green, "«green»")
    .Replace(AnsiCodes.red, "«red»")
    .Replace(AnsiCodes.yellow, "«yellow»")
    .Replace(AnsiCodes.cyan, "«cyan»")
    .Replace(AnsiCodes.dimWhite, "«dim»")
    .Replace(AnsiCodes.white, "«white»")
    |> fun s ->
      Text.RegularExpressions.Regex.Replace(s, @"\x1b\[(\d+);(\d+)H", "«@$1,$2»")
    |> fun s ->
      Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*[A-Za-z]", "«esc»")

/// Put each positioned row on its own line for readability
let formatForSnapshot (scrubbed: string) =
  Text.RegularExpressions.Regex.Replace(scrubbed, @"«@(\d+),(\d+)»", "\n«@$1,$2»")
    .TrimStart('\n')

let renderPaneSnapshotTests = testList "renderPane snapshots" [
  testTask "output pane with colorized content" {
    let pane = mkPane "output" "Output" 1 1 50 6 false
    let content = "[12:30:45] [result] val x: int = 42\n[12:30:46] [error] Type mismatch"
    let region = mkRegion "output" content
    let rendered = TerminalRender.renderPane pane (Some region) |> scrubAnsi |> formatForSnapshot
    do! verifyTerminal "renderPane_output_colorized" rendered
  }

  testTask "focused editor pane" {
    let pane = mkPane "editor" "Editor" 1 1 50 4 true
    let region = mkRegion "editor" "let x = 42\nprintfn \"%d\" x"
    let rendered = TerminalRender.renderPane pane (Some region) |> scrubAnsi |> formatForSnapshot
    do! verifyTerminal "renderPane_editor_focused" rendered
  }

  testTask "sessions pane with active session" {
    let pane = mkPane "sessions" "Sessions" 1 1 40 5 false
    let content = "session-abc [Ready] *\nsession-def [WarmingUp]"
    let region = mkRegion "sessions" content
    let rendered = TerminalRender.renderPane pane (Some region) |> scrubAnsi |> formatForSnapshot
    do! verifyTerminal "renderPane_sessions" rendered
  }

  testTask "diagnostics pane with errors" {
    let pane = mkPane "diagnostics" "Diagnostics" 1 1 50 5 false
    let content = "[error] (5,10) Type mismatch\n[warning] (1,1) Unused binding"
    let region = mkRegion "diagnostics" content
    let rendered = TerminalRender.renderPane pane (Some region) |> scrubAnsi |> formatForSnapshot
    do! verifyTerminal "renderPane_diagnostics" rendered
  }

  testTask "empty pane" {
    let pane = mkPane "output" "Output" 1 1 30 4 false
    let rendered = TerminalRender.renderPane pane None |> scrubAnsi |> formatForSnapshot
    do! verifyTerminal "renderPane_empty" rendered
  }
]

let renderFrameSnapshotTests = testList "renderFrame snapshots" [
  testTask "full frame with all regions" {
    let layout = TerminalLayout.compute 25 80
    let regions = [
      mkRegion "editor" "let x = 42"
      mkRegion "output" "[12:00:00] [result] val x = 42"
      mkRegion "diagnostics" "[error] (1,5) Unknown"
      mkRegion "sessions" "sess-1 [Ready] *"
    ]
    let rendered = TerminalRender.renderFrame layout regions "Ready" 5
                   |> scrubAnsi |> formatForSnapshot
    do! verifyTerminal "renderFrame_full" rendered
  }
]

let statusBarSnapshotTests = testList "statusBar snapshots" [
  testTask "status bar layout" {
    let rendered = TerminalRender.renderStatusBar 25 80 "Ready" 42 "Editor"
                   |> scrubAnsi |> formatForSnapshot
    do! verifyTerminal "renderStatusBar" rendered
  }
]

let colorizerSnapshotTests = testList "colorizer snapshots" [
  testTask "output colorizer all kinds" {
    let lines =
      [ "[12:30:45] [result] val x: int = 42"
        "[12:30:46] [error] FS0001: Type mismatch"
        "[12:30:47] [info] Loaded module Foo"
        "[12:30:48] [warning] Unused variable"
        "[12:30:49] [system] Hot reload triggered"
        "just plain text"
        "" ]
      |> List.map (fun line ->
        sprintf "%s -> %s" line (OutputColorizer.colorize line |> scrubAnsi))
      |> String.concat "\n"
    do! verifyTerminal "outputColorizer_all_kinds" lines
  }

  testTask "diagnostics colorizer all severities" {
    let lines =
      [ "[error] (5,10) Type mismatch"
        "[warning] (1,1) Unused binding"
        "[info] (3,0) Some message"
        "plain text" ]
      |> List.map (fun line ->
        sprintf "%s -> %s" line (DiagnosticsColorizer.colorize line |> scrubAnsi))
      |> String.concat "\n"
    do! verifyTerminal "diagnosticsColorizer_all" lines
  }
]


[<Tests>]
let allTerminalUITests = testList "Terminal UI" [
  terminalRenderTests
  terminalLayoutTests
  renderPaneTests
  renderFrameTests
  statusBarTests
  terminalInputTests
  outputColorizerTests
  diagnosticsColorizerTests
  contentColorizerTests
  frameDiffTests
  renderPaneSnapshotTests
  renderFrameSnapshotTests
  statusBarSnapshotTests
  colorizerSnapshotTests
]
