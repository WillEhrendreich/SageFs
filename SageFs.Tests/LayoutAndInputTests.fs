module SageFs.Tests.LayoutAndInputTests

open Expecto
open Expecto.Flip
open SageFs
open System

// ── KeyCombo.tryParse ──────────────────────────────────────────────

let keyComboParseTests = testList "KeyCombo.tryParse" [
  test "parses single letter key" {
    let kc = KeyCombo.tryParse "A"
    kc |> Expect.isSome "should parse A"
    kc.Value.Key |> Expect.equal "key should be A" ConsoleKey.A
  }
  test "parses Ctrl+letter" {
    let kc = (KeyCombo.tryParse "Ctrl+A").Value
    kc.Modifiers.HasFlag(ConsoleModifiers.Control)
    |> Expect.isTrue "should have Ctrl"
    kc.Key |> Expect.equal "key should be A" ConsoleKey.A
  }
  test "parses Alt+Shift+letter" {
    let kc = (KeyCombo.tryParse "Alt+Shift+B").Value
    kc.Modifiers.HasFlag(ConsoleModifiers.Alt) |> Expect.isTrue "should have Alt"
    kc.Modifiers.HasFlag(ConsoleModifiers.Shift) |> Expect.isTrue "should have Shift"
    kc.Key |> Expect.equal "key should be B" ConsoleKey.B
  }
  test "parses special keys" {
    (KeyCombo.tryParse "Enter").Value.Key |> Expect.equal "enter" ConsoleKey.Enter
    (KeyCombo.tryParse "Tab").Value.Key |> Expect.equal "tab" ConsoleKey.Tab
    (KeyCombo.tryParse "Escape").Value.Key |> Expect.equal "esc" ConsoleKey.Escape
    (KeyCombo.tryParse "Space").Value.Key |> Expect.equal "space" ConsoleKey.Spacebar
  }
  test "parses arrow keys" {
    (KeyCombo.tryParse "Up").Value.Key |> Expect.equal "up" ConsoleKey.UpArrow
    (KeyCombo.tryParse "Down").Value.Key |> Expect.equal "down" ConsoleKey.DownArrow
    (KeyCombo.tryParse "Left").Value.Key |> Expect.equal "left" ConsoleKey.LeftArrow
    (KeyCombo.tryParse "Right").Value.Key |> Expect.equal "right" ConsoleKey.RightArrow
  }
  test "parses digit keys" {
    (KeyCombo.tryParse "5").Value.Key |> Expect.equal "D5" ConsoleKey.D5
  }
  test "parses equals and minus" {
    (KeyCombo.tryParse "=").Value.Key |> Expect.equal "plus" ConsoleKey.OemPlus
    (KeyCombo.tryParse "-").Value.Key |> Expect.equal "minus" ConsoleKey.OemMinus
  }
  test "returns None for empty string" {
    KeyCombo.tryParse "" |> Expect.isNone "empty should fail"
  }
  test "returns None for modifier-only" {
    KeyCombo.tryParse "Ctrl" |> Expect.isNone "modifier-only should fail"
  }
  test "case insensitive" {
    KeyCombo.tryParse "ctrl+a" |> Expect.isSome "lowercase should work"
  }
  test "parses alternate names" {
    (KeyCombo.tryParse "Esc").Value.Key |> Expect.equal "esc alias" ConsoleKey.Escape
    (KeyCombo.tryParse "PgUp").Value.Key |> Expect.equal "pgup alias" ConsoleKey.PageUp
    (KeyCombo.tryParse "PgDn").Value.Key |> Expect.equal "pgdn alias" ConsoleKey.PageDown
    (KeyCombo.tryParse "Del").Value.Key |> Expect.equal "del alias" ConsoleKey.Delete
  }
]

// ── KeyCombo.format ────────────────────────────────────────────────

let keyComboFormatTests = testList "KeyCombo.format" [
  test "formats simple key" {
    { Key = ConsoleKey.A; Modifiers = enum 0; Char = None }
    |> KeyCombo.format
    |> Expect.equal "just A" "A"
  }
  test "formats Ctrl+key" {
    { Key = ConsoleKey.A; Modifiers = ConsoleModifiers.Control; Char = None }
    |> KeyCombo.format
    |> Expect.equal "Ctrl+A" "Ctrl+A"
  }
  test "formats Alt+Shift+key" {
    { Key = ConsoleKey.B; Modifiers = ConsoleModifiers.Alt ||| ConsoleModifiers.Shift; Char = None }
    |> KeyCombo.format
    |> Expect.equal "Alt+Shift+B" "Alt+Shift+B"
  }
  test "formats special keys" {
    { Key = ConsoleKey.OemPlus; Modifiers = enum 0; Char = None }
    |> KeyCombo.format |> Expect.equal "equals" "="
    { Key = ConsoleKey.OemMinus; Modifiers = enum 0; Char = None }
    |> KeyCombo.format |> Expect.equal "minus" "-"
  }
  test "formats arrow keys" {
    { Key = ConsoleKey.UpArrow; Modifiers = enum 0; Char = None }
    |> KeyCombo.format |> Expect.equal "up" "Up"
    { Key = ConsoleKey.DownArrow; Modifiers = enum 0; Char = None }
    |> KeyCombo.format |> Expect.equal "down" "Down"
  }
  test "formats Spacebar" {
    { Key = ConsoleKey.Spacebar; Modifiers = enum 0; Char = None }
    |> KeyCombo.format |> Expect.equal "space" "Space"
  }
]

// ── KeyCombo roundtrip (property) ──────────────────────────────────

let keyComboRoundtripTests = testList "KeyCombo roundtrip" [
  testProperty "format then tryParse roundtrips for letter keys" (fun (letter: char) ->
    let c = System.Char.ToUpper letter
    if c >= 'A' && c <= 'Z' then
      let key = enum<ConsoleKey> (int c)
      let original = { Key = key; Modifiers = enum 0; Char = None }
      let formatted = KeyCombo.format original
      let parsed = KeyCombo.tryParse formatted
      parsed.IsSome && parsed.Value.Key = original.Key && parsed.Value.Modifiers = original.Modifiers
    else true
  )
  testProperty "format then tryParse roundtrips with modifiers" (fun (useCtrl: bool) (useAlt: bool) ->
    let mods =
      (if useCtrl then ConsoleModifiers.Control else enum 0)
      ||| (if useAlt then ConsoleModifiers.Alt else enum 0)
    let original = { Key = ConsoleKey.A; Modifiers = mods; Char = None }
    let formatted = KeyCombo.format original
    let parsed = KeyCombo.tryParse formatted
    parsed.IsSome && parsed.Value.Key = original.Key && parsed.Value.Modifiers = original.Modifiers
  )
]

// ── PaneId navigation ──────────────────────────────────────────────

let paneIdTests = testList "PaneId" [
  testList "nextVisible" [
    test "cycles to next pane in order" {
      let visible = Set.ofList [ PaneId.Output; PaneId.Sessions; PaneId.Editor ]
      PaneId.nextVisible visible PaneId.Output
      |> Expect.equal "after Output" PaneId.Sessions
    }
    test "wraps around to first" {
      let visible = Set.ofList [ PaneId.Output; PaneId.Sessions; PaneId.Editor ]
      PaneId.nextVisible visible PaneId.Editor
      |> Expect.equal "wrap to Output" PaneId.Output
    }
    test "returns current when visible is empty" {
      PaneId.nextVisible Set.empty PaneId.Output
      |> Expect.equal "stays" PaneId.Output
    }
    test "returns first visible when current not in set" {
      let visible = Set.ofList [ PaneId.Sessions; PaneId.Editor ]
      PaneId.nextVisible visible PaneId.Output
      |> Expect.equal "jumps to first visible" PaneId.Sessions
    }
    test "single visible pane returns itself" {
      let visible = Set.ofList [ PaneId.Editor ]
      PaneId.nextVisible visible PaneId.Editor
      |> Expect.equal "stays on self" PaneId.Editor
    }
  ]
  testList "firstVisible" [
    test "returns first from ordered all array" {
      let visible = Set.ofList [ PaneId.Sessions; PaneId.Editor ]
      PaneId.firstVisible visible
      |> Expect.equal "Sessions comes before Editor in all" PaneId.Sessions
    }
    test "returns Output when it is visible" {
      let visible = Set.ofList [ PaneId.Output; PaneId.Editor ]
      PaneId.firstVisible visible
      |> Expect.equal "Output is first in all" PaneId.Output
    }
    test "returns Output as default when empty" {
      PaneId.firstVisible Set.empty
      |> Expect.equal "default is Output" PaneId.Output
    }
  ]
  testList "navigate" [
    test "navigates right to closest pane" {
      let panes = [
        (PaneId.Output, Rect.create 0 0 10 10)
        (PaneId.Sessions, Rect.create 0 12 10 10)
      ]
      PaneId.navigate Direction.Right PaneId.Output panes
      |> Expect.equal "goes to Sessions" PaneId.Sessions
    }
    test "navigates left" {
      let panes = [
        (PaneId.Output, Rect.create 0 0 10 10)
        (PaneId.Sessions, Rect.create 0 12 10 10)
      ]
      PaneId.navigate Direction.Left PaneId.Sessions panes
      |> Expect.equal "goes to Output" PaneId.Output
    }
    test "navigates down" {
      let panes = [
        (PaneId.Output, Rect.create 0 0 5 10)
        (PaneId.Editor, Rect.create 6 0 5 10)
      ]
      PaneId.navigate Direction.Down PaneId.Output panes
      |> Expect.equal "goes to Editor" PaneId.Editor
    }
    test "stays on current when no candidate in direction" {
      let panes = [
        (PaneId.Output, Rect.create 0 0 10 10)
      ]
      PaneId.navigate Direction.Right PaneId.Output panes
      |> Expect.equal "stays" PaneId.Output
    }
    test "stays on current when pane not found" {
      PaneId.navigate Direction.Right PaneId.Output []
      |> Expect.equal "stays when empty" PaneId.Output
    }
    test "picks closest by distance when multiple candidates" {
      let panes = [
        (PaneId.Output, Rect.create 0 0 10 10)
        (PaneId.Sessions, Rect.create 0 12 10 10)
        (PaneId.Editor, Rect.create 0 30 10 10)
      ]
      PaneId.navigate Direction.Right PaneId.Output panes
      |> Expect.equal "closest" PaneId.Sessions
    }
  ]
]

// ── LayoutConfig ───────────────────────────────────────────────────

let layoutConfigTests = testList "LayoutConfig" [
  test "togglePane adds pane when not visible" {
    let cfg = LayoutConfig.defaults
    let toggled = LayoutConfig.togglePane PaneId.Editor cfg
    toggled.VisiblePanes.Contains PaneId.Editor
    |> Expect.isTrue "Editor should be visible after toggle"
  }
  test "togglePane removes pane when visible" {
    let cfg = LayoutConfig.defaults
    let toggled = LayoutConfig.togglePane PaneId.Output cfg
    toggled.VisiblePanes.Contains PaneId.Output
    |> Expect.isFalse "Output should be hidden after toggle"
  }
  test "togglePane is idempotent pair" {
    let cfg = LayoutConfig.defaults
    let toggled = cfg |> LayoutConfig.togglePane PaneId.Editor |> LayoutConfig.togglePane PaneId.Editor
    toggled.VisiblePanes |> Expect.equal "back to original" cfg.VisiblePanes
  }
  test "defaults have Output and Sessions visible" {
    LayoutConfig.defaults.VisiblePanes.Contains PaneId.Output |> Expect.isTrue "Output visible"
    LayoutConfig.defaults.VisiblePanes.Contains PaneId.Sessions |> Expect.isTrue "Sessions visible"
    LayoutConfig.defaults.VisiblePanes.Contains PaneId.Editor |> Expect.isFalse "Editor not visible"
    LayoutConfig.defaults.VisiblePanes.Contains PaneId.Diagnostics |> Expect.isFalse "Diags not visible"
    LayoutConfig.defaults.VisiblePanes.Contains PaneId.Context |> Expect.isFalse "Context not visible"
  }
  testProperty "togglePane always changes membership" (fun (paneIdx: int) ->
    let pane = PaneId.all.[abs paneIdx % PaneId.all.Length]
    let cfg = LayoutConfig.defaults
    let toggled = LayoutConfig.togglePane pane cfg
    toggled.VisiblePanes.Contains pane <> cfg.VisiblePanes.Contains pane
  )
]

// ── UiAction.tryParse ──────────────────────────────────────────────

let uiActionParseTests = testList "UiAction.tryParse" [
  test "parses Quit" {
    UiAction.tryParse "Quit" |> Expect.equal "Quit" (Some UiAction.Quit)
  }
  test "parses CycleFocus" {
    UiAction.tryParse "CycleFocus" |> Expect.equal "CycleFocus" (Some UiAction.CycleFocus)
  }
  test "parses FocusDir variants" {
    UiAction.tryParse "FocusLeft" |> Expect.equal "left" (Some (UiAction.FocusDir Direction.Left))
    UiAction.tryParse "FocusRight" |> Expect.equal "right" (Some (UiAction.FocusDir Direction.Right))
    UiAction.tryParse "FocusUp" |> Expect.equal "up" (Some (UiAction.FocusDir Direction.Up))
    UiAction.tryParse "FocusDown" |> Expect.equal "down" (Some (UiAction.FocusDir Direction.Down))
  }
  test "parses editor actions" {
    UiAction.tryParse "Submit" |> Expect.equal "submit" (Some (UiAction.Editor EditorAction.Submit))
    UiAction.tryParse "Undo" |> Expect.equal "undo" (Some (UiAction.Editor EditorAction.Undo))
    UiAction.tryParse "SelectAll" |> Expect.equal "selectall" (Some (UiAction.Editor EditorAction.SelectAll))
  }
  test "parses movement editor actions" {
    UiAction.tryParse "MoveUp" |> Expect.equal "moveup" (Some (UiAction.Editor (EditorAction.MoveCursor Direction.Up)))
    UiAction.tryParse "MoveDown" |> Expect.equal "movedn" (Some (UiAction.Editor (EditorAction.MoveCursor Direction.Down)))
  }
  test "parses resize actions" {
    UiAction.tryParse "ResizeHGrow" |> Expect.equal "hgrow" (Some (UiAction.ResizeH 1))
    UiAction.tryParse "ResizeHShrink" |> Expect.equal "hshrink" (Some (UiAction.ResizeH -1))
    UiAction.tryParse "ResizeVGrow" |> Expect.equal "vgrow" (Some (UiAction.ResizeV 1))
    UiAction.tryParse "ResizeVShrink" |> Expect.equal "vshrink" (Some (UiAction.ResizeV -1))
  }
  test "parses TogglePane prefix" {
    UiAction.tryParse "TogglePane.Editor" |> Expect.equal "toggle editor" (Some (UiAction.TogglePane "Editor"))
  }
  test "parses Layout prefix" {
    UiAction.tryParse "Layout.Default" |> Expect.equal "layout default" (Some (UiAction.LayoutPreset "Default"))
  }
  test "parses live testing actions" {
    UiAction.tryParse "EnableLiveTesting" |> Expect.equal "enable lt" (Some UiAction.EnableLiveTesting)
    UiAction.tryParse "DisableLiveTesting" |> Expect.equal "disable lt" (Some UiAction.DisableLiveTesting)
    UiAction.tryParse "CycleRunPolicy" |> Expect.equal "cycle rp" (Some UiAction.CycleRunPolicy)
    UiAction.tryParse "ToggleCoverage" |> Expect.equal "toggle cov" (Some UiAction.ToggleCoverage)
  }
  test "returns None for unknown action" {
    UiAction.tryParse "NonExistent" |> Expect.isNone "unknown"
  }
  test "trims whitespace" {
    UiAction.tryParse "  Quit  " |> Expect.equal "trimmed" (Some UiAction.Quit)
  }
  test "parses session actions" {
    UiAction.tryParse "SessionNavUp" |> Expect.equal "nav up" (Some (UiAction.Editor EditorAction.SessionNavUp))
    UiAction.tryParse "SessionCycleNext" |> Expect.equal "cycle next" (Some (UiAction.Editor EditorAction.SessionCycleNext))
    UiAction.tryParse "CreateSession" |> Expect.equal "create" (Some (UiAction.Editor (EditorAction.CreateSession [])))
  }
]

// ── TerminalInput ──────────────────────────────────────────────────

let terminalInputTests = testList "TerminalInput" [
  test "toKeyCombo converts ConsoleKeyInfo" {
    let ki = ConsoleKeyInfo('a', ConsoleKey.A, false, false, true)
    let kc = TerminalInput.toKeyCombo ki
    kc.Key |> Expect.equal "key" ConsoleKey.A
    kc.Modifiers |> Expect.equal "mods" ConsoleModifiers.Control
    kc.Char |> Expect.isNone "char always None"
  }
  test "mapKeyWith matches bound key" {
    let keyMap = Map.ofList [ { Key = ConsoleKey.Q; Modifiers = ConsoleModifiers.Control; Char = None }, UiAction.Quit ]
    let ki = ConsoleKeyInfo('\x11', ConsoleKey.Q, false, false, true)
    TerminalInput.mapKeyWith keyMap ki
    |> Expect.equal "Ctrl+Q → Quit" (Some TerminalCommand.Quit)
  }
  test "mapKeyWith falls through to char insertion for printable" {
    let keyMap = Map.empty
    let ki = ConsoleKeyInfo('a', ConsoleKey.A, false, false, false)
    TerminalInput.mapKeyWith keyMap ki
    |> Expect.equal "a → InsertChar" (Some (TerminalCommand.Action (EditorAction.InsertChar 'a')))
  }
  test "mapKeyWith returns None for unbound non-printable" {
    let keyMap = Map.empty
    let ki = ConsoleKeyInfo('\000', ConsoleKey.F1, false, false, false)
    TerminalInput.mapKeyWith keyMap ki
    |> Expect.isNone "F1 unbound"
  }
  test "mapKeyWith maps FocusDir" {
    let keyMap = Map.ofList [ { Key = ConsoleKey.LeftArrow; Modifiers = ConsoleModifiers.Alt; Char = None }, UiAction.FocusDir Direction.Left ]
    let ki = ConsoleKeyInfo('\000', ConsoleKey.LeftArrow, false, true, false)
    TerminalInput.mapKeyWith keyMap ki
    |> Expect.equal "Alt+Left → FocusLeft" (Some (TerminalCommand.FocusDirection Direction.Left))
  }
  test "mapKeyWith maps editor actions" {
    let keyMap = Map.ofList [ { Key = ConsoleKey.Enter; Modifiers = enum 0; Char = None }, UiAction.Editor EditorAction.Submit ]
    let ki = ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)
    TerminalInput.mapKeyWith keyMap ki
    |> Expect.equal "Enter → Submit" (Some (TerminalCommand.Action EditorAction.Submit))
  }
  test "mapKeyWith ignores FontSizeUp/Down in TUI" {
    let keyMap = Map.ofList [ { Key = ConsoleKey.OemPlus; Modifiers = ConsoleModifiers.Control; Char = None }, UiAction.FontSizeUp ]
    let ki = ConsoleKeyInfo('\000', ConsoleKey.OemPlus, false, false, true)
    TerminalInput.mapKeyWith keyMap ki
    |> Expect.isNone "FontSizeUp ignored in TUI"
  }
  test "mapKeyWith inserts unicode chars above 0x7f" {
    let keyMap = Map.empty
    let ki = ConsoleKeyInfo('é', ConsoleKey.A, false, false, false)
    TerminalInput.mapKeyWith keyMap ki
    |> Expect.equal "unicode insertion" (Some (TerminalCommand.Action (EditorAction.InsertChar 'é')))
  }
]

// ── Combined ───────────────────────────────────────────────────────

[<Tests>]
let allTests = testList "Layout and Input Coverage" [
  keyComboParseTests
  keyComboFormatTests
  keyComboRoundtripTests
  paneIdTests
  layoutConfigTests
  uiActionParseTests
  terminalInputTests
]
