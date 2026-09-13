module SageFs.Tests.TerminalUITests

open System
open Expecto
open Expecto.Flip
open SageFs

let key c k mods =
  ConsoleKeyInfo(c, k, (mods &&& 4) <> 0, (mods &&& 2) <> 0, (mods &&& 1) <> 0)


let terminalInputTests = testList "TerminalInput" [
  test "Tab cycles focus" {
    let result = TerminalInput.mapKey (key '\t' ConsoleKey.Tab 0)
    result |> Expect.equal "Tab should cycle focus" (Some TerminalCommand.CycleFocus)
  }

  test "Alt+Enter submits" {
    let result = TerminalInput.mapKey (key '\n' ConsoleKey.Enter 2)
    result |> Expect.equal "Alt+Enter submits" (Some (TerminalCommand.Action EditorAction.Submit))
  }

  test "Enter inserts newline" {
    let result = TerminalInput.mapKey (key '\n' ConsoleKey.Enter 0)
    result |> Expect.equal "Enter inserts newline" (Some (TerminalCommand.Action EditorAction.NewLine))
  }

  test "Ctrl+D does not quit" {
    let result = TerminalInput.mapKey (key '\x04' ConsoleKey.D 1)
    result |> Expect.isNone "Ctrl+D should not quit"
  }

  test "Ctrl+Q quits" {
    let result = TerminalInput.mapKey (key '\x11' ConsoleKey.Q 1)
    result |> Expect.equal "Ctrl+Q quits" (Some TerminalCommand.Quit)
  }

  test "Ctrl+C is not mapped (reserved for terminal)" {
    let result = TerminalInput.mapKey (key '\x03' ConsoleKey.C 1)
    result |> Expect.isNone "Ctrl+C not mapped in default keymap"
  }

  test "Arrow up moves cursor" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.UpArrow 0)
    result |> Expect.equal "up arrow" (Some (TerminalCommand.Action (EditorAction.MoveCursor Direction.Up)))
  }

  test "Arrow left moves cursor" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.LeftArrow 0)
    result |> Expect.equal "left arrow" (Some (TerminalCommand.Action (EditorAction.MoveCursor Direction.Left)))
  }

  test "Backspace deletes backward" {
    let result = TerminalInput.mapKey (key '\b' ConsoleKey.Backspace 0)
    result |> Expect.equal "backspace" (Some (TerminalCommand.Action EditorAction.DeleteBackward))
  }

  test "Printable chars are InsertChar" {
    let result = TerminalInput.mapKey (key 'a' ConsoleKey.A 0)
    result |> Expect.equal "printable char" (Some (TerminalCommand.Action (EditorAction.InsertChar 'a')))
  }

  test "Ctrl+L focuses right pane" {
    let result = TerminalInput.mapKey (key '\x0c' ConsoleKey.L 1)
    result |> Expect.equal "Ctrl+L focuses right" (Some (TerminalCommand.FocusDirection Direction.Right))
  }

  test "Alt+Up scrolls up" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.UpArrow 2)
    result |> Expect.equal "Alt+Up scrolls" (Some TerminalCommand.ScrollUp)
  }

  test "PageDown scrolls down" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.PageDown 0)
    result |> Expect.equal "PageDown scrolls" (Some TerminalCommand.ScrollDown)
  }

  test "Ctrl+Alt+A configures warmup auto-open opt-out" {
    let result = TerminalInput.mapKey (key '\x00' ConsoleKey.A 3)
    result |> Expect.equal "Ctrl+Alt+A configures auto-open opt-out" (Some (TerminalCommand.Action EditorAction.ConfigureWarmupAutoOpen))
  }
]


let paneIdTests = testList "PaneId" [
  test "toRegionId roundtrips with fromRegionId" {
    for pane in PaneId.all do
      let regionId = PaneId.toRegionId pane
      let back = PaneId.fromRegionId regionId
      back |> Expect.equal (sprintf "%A should roundtrip" pane) (Some pane)
  }

  test "fromRegionId returns None for unknown" {
    let result = PaneId.fromRegionId "unknown"
    result |> Expect.isNone "unknown region should return None"
  }

  test "next cycles through all panes" {
    let mutable current = PaneId.Output
    let visited = System.Collections.Generic.HashSet<PaneId>()
    for _ in 0 .. PaneId.all.Length - 1 do
      visited.Add(current) |> ignore
      current <- PaneId.next current
    visited.Count |> Expect.equal "should visit all panes" PaneId.all.Length
    current |> Expect.equal "should cycle back to start" PaneId.Output
  }

  test "next from Editor wraps to Tests" {
    let result = PaneId.next PaneId.Editor
    result |> Expect.equal "Editor -> Tests" PaneId.Tests
  }

  test "displayName returns human-readable names" {
    (PaneId.displayName PaneId.Output) |> Expect.equal "Output display name" "Output"
    (PaneId.displayName PaneId.Editor) |> Expect.equal "Editor display name" "Editor"
    (PaneId.displayName PaneId.Sessions) |> Expect.equal "Sessions display name" "Sessions"
    (PaneId.displayName PaneId.Diagnostics) |> Expect.equal "Diagnostics display name" "Diagnostics"
  }
]


let paneVisibilityTests = testList "PaneId visibility" [
  test "nextVisible cycles only visible panes" {
    let visible = set [PaneId.Output; PaneId.Sessions]
    let r1 = PaneId.nextVisible visible PaneId.Output
    let r2 = PaneId.nextVisible visible r1
    r1 |> Expect.equal "Output -> Sessions" PaneId.Sessions
    r2 |> Expect.equal "Sessions -> Output" PaneId.Output
  }

  test "nextVisible skips invisible panes" {
    let visible = set [PaneId.Output; PaneId.Diagnostics]
    let r = PaneId.nextVisible visible PaneId.Output
    r |> Expect.equal "should skip Editor and Sessions" PaneId.Diagnostics
  }

  test "nextVisible with single pane stays" {
    let visible = set [PaneId.Output]
    let r = PaneId.nextVisible visible PaneId.Output
    r |> Expect.equal "single pane stays" PaneId.Output
  }

  test "nextVisible when current not visible returns first visible" {
    let visible = set [PaneId.Sessions; PaneId.Diagnostics]
    let r = PaneId.nextVisible visible PaneId.Editor
    r |> Expect.equal "should jump to first visible" PaneId.Sessions
  }

  test "nextVisible empty set returns current" {
    let r = PaneId.nextVisible Set.empty PaneId.Output
    r |> Expect.equal "empty visible returns current" PaneId.Output
  }

  test "firstVisible returns first in cycle order" {
    let visible = set [PaneId.Sessions; PaneId.Diagnostics]
    let r = PaneId.firstVisible visible
    r |> Expect.equal "Sessions comes before Diagnostics" PaneId.Sessions
  }

  test "firstVisible empty set returns Output" {
    let r = PaneId.firstVisible Set.empty
    r |> Expect.equal "empty set defaults to Output" PaneId.Output
  }
]


[<Tests>]
let allTerminalUITests = testList "Terminal UI" [
  terminalInputTests
  paneIdTests
  paneVisibilityTests
]
