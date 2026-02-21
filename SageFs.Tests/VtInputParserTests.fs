module SageFs.Tests.VtInputParserTests

open System
open Expecto
open SageFs
open SageFs.Server.ConsoleInput

let private feed (parser: VtInputParser) (s: string) =
  for c in s do parser.Feed(c)

let private drain (parser: VtInputParser) =
  let mutable events = []
  let mutable ev = parser.TryDequeue()
  while ev.IsSome do
    events <- ev.Value :: events
    ev <- parser.TryDequeue()
  List.rev events

[<Tests>]
let vtInputParserTests = testList "VtInputParser" [
  test "printable char emits KeyEvent" {
    let p = VtInputParser()
    feed p "a"
    let evs = drain p
    Expect.equal evs.Length 1 "one event"
    match evs.[0] with
    | KeyEvent (_, ch, _) -> Expect.equal ch 'a' "char is a"
    | _ -> failtest "expected KeyEvent"
  }

  test "Ctrl+Q emits Control+Q" {
    let p = VtInputParser()
    feed p "\x11"
    let evs = drain p
    Expect.equal evs.Length 1 "one event"
    match evs.[0] with
    | KeyEvent (key, _, mods) ->
      Expect.equal key ConsoleKey.Q "key is Q"
      Expect.isTrue (mods.HasFlag ConsoleModifiers.Control) "has Control"
    | _ -> failtest "expected KeyEvent"
  }

  test "arrow up sequence" {
    let p = VtInputParser()
    feed p "\x1b[A"
    let evs = drain p
    Expect.equal evs.Length 1 "one event"
    match evs.[0] with
    | KeyEvent (key, _, _) -> Expect.equal key ConsoleKey.UpArrow "up arrow"
    | _ -> failtest "expected KeyEvent"
  }

  test "Ctrl+Up arrow with modifier" {
    let p = VtInputParser()
    feed p "\x1b[1;5A"
    let evs = drain p
    Expect.equal evs.Length 1 "one event"
    match evs.[0] with
    | KeyEvent (key, _, mods) ->
      Expect.equal key ConsoleKey.UpArrow "up"
      Expect.isTrue (mods.HasFlag ConsoleModifiers.Control) "ctrl"
    | _ -> failtest "expected KeyEvent"
  }

  test "PageUp tilde sequence" {
    let p = VtInputParser()
    feed p "\x1b[5~"
    let evs = drain p
    Expect.equal evs.Length 1 "one event"
    match evs.[0] with
    | KeyEvent (key, _, _) -> Expect.equal key ConsoleKey.PageUp "PageUp"
    | _ -> failtest "expected KeyEvent"
  }

  test "F1 via ESC O P" {
    let p = VtInputParser()
    feed p "\x1bOP"
    let evs = drain p
    Expect.equal evs.Length 1 "one event"
    match evs.[0] with
    | KeyEvent (key, _, _) -> Expect.equal key ConsoleKey.F1 "F1"
    | _ -> failtest "expected KeyEvent"
  }

  test "standalone Escape via Flush" {
    let p = VtInputParser()
    feed p "\x1b"
    Expect.isTrue p.IsMidSequence "mid sequence"
    p.Flush()
    let evs = drain p
    Expect.equal evs.Length 1 "one event"
    match evs.[0] with
    | KeyEvent (key, _, _) -> Expect.equal key ConsoleKey.Escape "Escape"
    | _ -> failtest "expected KeyEvent"
  }

  test "SGR mouse left click" {
    let p = VtInputParser()
    feed p "\x1b[<0;10;20M"
    let evs = drain p
    Expect.equal evs.Length 1 "one event"
    match evs.[0] with
    | InputEvent.MouseEvent me ->
      Expect.equal me.Button MouseButton.Left "left button"
      Expect.equal me.Action MouseAction.Press "press"
      Expect.equal me.Col 9 "col (1-based to 0-based)"
      Expect.equal me.Row 19 "row (1-based to 0-based)"
    | _ -> failtest "expected MouseEvent"
  }

  test "SGR mouse release" {
    let p = VtInputParser()
    feed p "\x1b[<0;5;5m"
    let evs = drain p
    match evs.[0] with
    | InputEvent.MouseEvent me ->
      Expect.equal me.Action MouseAction.Release "release"
    | _ -> failtest "expected MouseEvent"
  }

  test "SGR mouse right click" {
    let p = VtInputParser()
    feed p "\x1b[<2;1;1M"
    let evs = drain p
    match evs.[0] with
    | InputEvent.MouseEvent me ->
      Expect.equal me.Button MouseButton.Right "right"
      Expect.equal me.Action MouseAction.Press "press"
    | _ -> failtest "expected MouseEvent"
  }

  test "SGR wheel up" {
    let p = VtInputParser()
    feed p "\x1b[<64;1;1M"
    let evs = drain p
    match evs.[0] with
    | InputEvent.MouseEvent me ->
      Expect.equal me.Action MouseAction.WheelUp "wheel up"
    | _ -> failtest "expected MouseEvent"
  }

  test "SGR wheel down" {
    let p = VtInputParser()
    feed p "\x1b[<65;1;1M"
    let evs = drain p
    match evs.[0] with
    | InputEvent.MouseEvent me ->
      Expect.equal me.Action MouseAction.WheelDown "wheel down"
    | _ -> failtest "expected MouseEvent"
  }

  test "SGR mouse drag (motion flag)" {
    let p = VtInputParser()
    feed p "\x1b[<32;3;4M"
    let evs = drain p
    match evs.[0] with
    | InputEvent.MouseEvent me ->
      Expect.equal me.Action MouseAction.Move "motion"
    | _ -> failtest "expected MouseEvent"
  }

  test "mixed keyboard and mouse sequence" {
    let p = VtInputParser()
    feed p "x\x1b[<0;1;1M\x1b[A"
    let evs = drain p
    Expect.equal evs.Length 3 "three events"
    match evs.[0] with KeyEvent (_, ch, _) -> Expect.equal ch 'x' "char" | _ -> failtest "expected key"
    match evs.[1] with InputEvent.MouseEvent _ -> () | _ -> failtest "expected mouse"
    match evs.[2] with KeyEvent (k, _, _) -> Expect.equal k ConsoleKey.UpArrow "arrow" | _ -> failtest "expected key"
  }

  test "Tab key" {
    let p = VtInputParser()
    feed p "\x09"
    let evs = drain p
    match evs.[0] with
    | KeyEvent (key, _, _) -> Expect.equal key ConsoleKey.Tab "Tab"
    | _ -> failtest "expected KeyEvent"
  }

  test "Enter key" {
    let p = VtInputParser()
    feed p "\x0d"
    let evs = drain p
    match evs.[0] with
    | KeyEvent (key, _, _) -> Expect.equal key ConsoleKey.Enter "Enter"
    | _ -> failtest "expected KeyEvent"
  }
]
