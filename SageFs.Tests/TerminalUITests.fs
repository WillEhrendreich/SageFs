module SageFs.Tests.TerminalUITests

open System
open Expecto
open SageFs

let key c k mods =
  ConsoleKeyInfo(c, k, (mods &&& 4) <> 0, (mods &&& 2) <> 0, (mods &&& 1) <> 0)


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

  test "Ctrl+D does not quit" {
    let result = TerminalInput.mapKey (key '\x04' ConsoleKey.D 1)
    Expect.isNone result "Ctrl+D should not quit"
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

  test "Ctrl+L focuses right pane" {
    let result = TerminalInput.mapKey (key '\x0c' ConsoleKey.L 1)
    Expect.equal result (Some (TerminalCommand.FocusDirection Direction.Right)) "Ctrl+L focuses right"
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


let paneIdTests = testList "PaneId" [
  test "toRegionId roundtrips with fromRegionId" {
    for pane in PaneId.all do
      let regionId = PaneId.toRegionId pane
      let back = PaneId.fromRegionId regionId
      Expect.equal back (Some pane) (sprintf "%A should roundtrip" pane)
  }

  test "fromRegionId returns None for unknown" {
    let result = PaneId.fromRegionId "unknown"
    Expect.isNone result "unknown region should return None"
  }

  test "next cycles through all panes" {
    let mutable current = PaneId.Output
    let visited = System.Collections.Generic.HashSet<PaneId>()
    for _ in 0 .. PaneId.all.Length - 1 do
      visited.Add(current) |> ignore
      current <- PaneId.next current
    Expect.equal visited.Count PaneId.all.Length "should visit all panes"
    Expect.equal current PaneId.Output "should cycle back to start"
  }

  test "next from Editor wraps to Output" {
    let result = PaneId.next PaneId.Editor
    Expect.equal result PaneId.Output "Editor -> Output"
  }

  test "displayName returns human-readable names" {
    Expect.equal (PaneId.displayName PaneId.Output) "Output" "Output display name"
    Expect.equal (PaneId.displayName PaneId.Editor) "Editor" "Editor display name"
    Expect.equal (PaneId.displayName PaneId.Sessions) "Sessions" "Sessions display name"
    Expect.equal (PaneId.displayName PaneId.Diagnostics) "Diagnostics" "Diagnostics display name"
  }
]


let paneVisibilityTests = testList "PaneId visibility" [
  test "nextVisible cycles only visible panes" {
    let visible = set [PaneId.Output; PaneId.Sessions]
    let r1 = PaneId.nextVisible visible PaneId.Output
    let r2 = PaneId.nextVisible visible r1
    Expect.equal r1 PaneId.Sessions "Output -> Sessions"
    Expect.equal r2 PaneId.Output "Sessions -> Output"
  }

  test "nextVisible skips invisible panes" {
    let visible = set [PaneId.Output; PaneId.Diagnostics]
    let r = PaneId.nextVisible visible PaneId.Output
    Expect.equal r PaneId.Diagnostics "should skip Editor and Sessions"
  }

  test "nextVisible with single pane stays" {
    let visible = set [PaneId.Output]
    let r = PaneId.nextVisible visible PaneId.Output
    Expect.equal r PaneId.Output "single pane stays"
  }

  test "nextVisible when current not visible returns first visible" {
    let visible = set [PaneId.Sessions; PaneId.Diagnostics]
    let r = PaneId.nextVisible visible PaneId.Editor
    Expect.equal r PaneId.Sessions "should jump to first visible"
  }

  test "nextVisible empty set returns current" {
    let r = PaneId.nextVisible Set.empty PaneId.Output
    Expect.equal r PaneId.Output "empty visible returns current"
  }

  test "firstVisible returns first in cycle order" {
    let visible = set [PaneId.Sessions; PaneId.Diagnostics]
    let r = PaneId.firstVisible visible
    Expect.equal r PaneId.Sessions "Sessions comes before Diagnostics"
  }

  test "firstVisible empty set returns Output" {
    let r = PaneId.firstVisible Set.empty
    Expect.equal r PaneId.Output "empty set defaults to Output"
  }
]


[<Tests>]
let allTerminalUITests = testList "Terminal UI" [
  terminalInputTests
  paneIdTests
  paneVisibilityTests
]
