module SageFs.Server.ConsoleInput

open System
open System.Collections.Concurrent
open System.Threading
open SageFs

/// VT escape sequence parser state machine.
/// Parses both keyboard VT sequences and SGR mouse events (mode 1006).
/// Cross-platform: works in Windows Terminal, iTerm2, and Linux terminals.
type private ParserState =
  | Normal
  | GotEsc
  | GotCsi
  | ReadingCsiParams
  | ReadingMouse
  | GotEscO

type VtInputParser() =
  let events = ConcurrentQueue<InputEvent>()
  let paramBuf = Text.StringBuilder()
  let mutable state = Normal

  let emitKey key ch mods = events.Enqueue(KeyEvent(key, ch, mods))
  let emitMouse me = events.Enqueue(InputEvent.MouseEvent me)

  let parseCsiCommand (rawParams: string) (cmd: char) =
    let parts = if rawParams.Length > 0 then rawParams.Split(';') else [||]
    let param n =
      if n < parts.Length then
        match Int32.TryParse(parts.[n]) with true, v -> v | _ -> 0
      else 0
    let modCode = if parts.Length >= 2 then param (parts.Length - 1) else 1
    let mods =
      match modCode with
      | 2 -> ConsoleModifiers.Shift
      | 3 -> ConsoleModifiers.Alt
      | 4 -> ConsoleModifiers.Shift ||| ConsoleModifiers.Alt
      | 5 -> ConsoleModifiers.Control
      | 6 -> ConsoleModifiers.Control ||| ConsoleModifiers.Shift
      | 7 -> ConsoleModifiers.Control ||| ConsoleModifiers.Alt
      | 8 -> ConsoleModifiers.Control ||| ConsoleModifiers.Shift ||| ConsoleModifiers.Alt
      | _ -> enum 0
    match cmd with
    | 'A' -> emitKey ConsoleKey.UpArrow '\000' mods
    | 'B' -> emitKey ConsoleKey.DownArrow '\000' mods
    | 'C' -> emitKey ConsoleKey.RightArrow '\000' mods
    | 'D' -> emitKey ConsoleKey.LeftArrow '\000' mods
    | 'H' -> emitKey ConsoleKey.Home '\000' mods
    | 'F' -> emitKey ConsoleKey.End '\000' mods
    | 'Z' -> emitKey ConsoleKey.Tab '\009' ConsoleModifiers.Shift
    | '~' ->
      match param 0 with
      | 2 -> emitKey ConsoleKey.Insert '\000' mods
      | 3 -> emitKey ConsoleKey.Delete '\000' mods
      | 5 -> emitKey ConsoleKey.PageUp '\000' mods
      | 6 -> emitKey ConsoleKey.PageDown '\000' mods
      | 15 -> emitKey ConsoleKey.F5 '\000' mods
      | 17 -> emitKey ConsoleKey.F6 '\000' mods
      | 18 -> emitKey ConsoleKey.F7 '\000' mods
      | 19 -> emitKey ConsoleKey.F8 '\000' mods
      | 20 -> emitKey ConsoleKey.F9 '\000' mods
      | 21 -> emitKey ConsoleKey.F10 '\000' mods
      | 23 -> emitKey ConsoleKey.F11 '\000' mods
      | 24 -> emitKey ConsoleKey.F12 '\000' mods
      | _ -> ()
    | _ -> ()

  let parseMouseEvent (isPress: bool) =
    let parts = paramBuf.ToString().Split(';')
    if parts.Length = 3 then
      match Int32.TryParse(parts.[0]), Int32.TryParse(parts.[1]), Int32.TryParse(parts.[2]) with
      | (true, btnCode), (true, col), (true, row) ->
        let isWheel = btnCode &&& 64 <> 0
        let isMotion = btnCode &&& 32 <> 0
        let baseBtn = btnCode &&& 3
        let button =
          if isWheel then MouseButton.None
          else
            match baseBtn with
            | 0 -> MouseButton.Left
            | 1 -> MouseButton.Middle
            | 2 -> MouseButton.Right
            | _ -> MouseButton.None
        let action =
          if isWheel then
            if baseBtn = 0 then MouseAction.WheelUp else MouseAction.WheelDown
          elif isMotion then MouseAction.Move
          elif isPress then MouseAction.Press
          else MouseAction.Release
        emitMouse { Button = button; Action = action; Col = col - 1; Row = row - 1 }
      | _ -> ()

  member _.Feed(ch: char) =
    match state, ch with
    | Normal, '\x1b' ->
      state <- GotEsc
    | Normal, '\x0d' ->
      emitKey ConsoleKey.Enter '\x0d' (enum 0)
    | Normal, '\x0a' ->
      emitKey ConsoleKey.Enter '\x0a' (enum 0)
    | Normal, '\x09' ->
      emitKey ConsoleKey.Tab '\x09' (enum 0)
    | Normal, '\x08' | Normal, '\x7f' ->
      emitKey ConsoleKey.Backspace '\x08' (enum 0)
    | Normal, c when c >= '\x01' && c <= '\x1a' ->
      let letter = char (int c + int 'a' - 1)
      let key = enum<ConsoleKey>(int (Char.ToUpper letter))
      emitKey key letter ConsoleModifiers.Control
    | Normal, c ->
      emitKey (enum 0) c (enum 0)

    | GotEsc, '[' ->
      state <- GotCsi
      paramBuf.Clear() |> ignore
    | GotEsc, 'O' ->
      state <- GotEscO
    | GotEsc, '\x1b' ->
      emitKey ConsoleKey.Escape '\x1b' (enum 0)
      state <- GotEsc
    | GotEsc, c ->
      emitKey ConsoleKey.Escape '\x1b' (enum 0)
      state <- Normal
      // Re-process char in Normal state
      match c with
      | c when c >= '\x01' && c <= '\x1a' ->
        let letter = char (int c + int 'a' - 1)
        let key = enum<ConsoleKey>(int (Char.ToUpper letter))
        emitKey key letter ConsoleModifiers.Control
      | c -> emitKey (enum 0) c (enum 0)

    | GotCsi, '<' ->
      state <- ReadingMouse
      paramBuf.Clear() |> ignore
    | GotCsi, c when c >= '@' && c <= '~' ->
      parseCsiCommand "" c
      state <- Normal
    | GotCsi, c ->
      paramBuf.Clear().Append(c) |> ignore
      state <- ReadingCsiParams

    | ReadingCsiParams, c when c >= '@' && c <= '~' ->
      parseCsiCommand (paramBuf.ToString()) c
      state <- Normal
    | ReadingCsiParams, c ->
      paramBuf.Append(c) |> ignore

    | ReadingMouse, 'M' ->
      parseMouseEvent true
      state <- Normal
    | ReadingMouse, 'm' ->
      parseMouseEvent false
      state <- Normal
    | ReadingMouse, c ->
      paramBuf.Append(c) |> ignore

    | GotEscO, 'P' -> emitKey ConsoleKey.F1 '\000' (enum 0); state <- Normal
    | GotEscO, 'Q' -> emitKey ConsoleKey.F2 '\000' (enum 0); state <- Normal
    | GotEscO, 'R' -> emitKey ConsoleKey.F3 '\000' (enum 0); state <- Normal
    | GotEscO, 'S' -> emitKey ConsoleKey.F4 '\000' (enum 0); state <- Normal
    | GotEscO, c ->
      emitKey ConsoleKey.Escape '\x1b' (enum 0)
      state <- Normal
      emitKey (enum 0) c (enum 0)

  /// Flush pending state (e.g., standalone Escape after timeout)
  member _.Flush() =
    match state with
    | GotEsc ->
      emitKey ConsoleKey.Escape '\x1b' (enum 0)
      state <- Normal
    | GotCsi ->
      emitKey ConsoleKey.Escape '\x1b' (enum 0)
      emitKey (enum 0) '[' (enum 0)
      state <- Normal
    | GotEscO ->
      emitKey ConsoleKey.Escape '\x1b' (enum 0)
      emitKey (enum 0) 'O' (enum 0)
      state <- Normal
    | _ -> ()

  member _.TryDequeue() =
    let mutable ev = Unchecked.defaultof<InputEvent>
    if events.TryDequeue(&ev) then Some ev else None

  member _.HasEvents = not events.IsEmpty
  member _.IsMidSequence =
    match state with Normal -> false | _ -> true


/// Cross-platform raw terminal input reader.
/// Background thread reads stdin character-by-character, feeds VT parser.
/// Main loop drains parsed InputEvent values.
module RawInput =
  let private parser = VtInputParser()
  let private charQueue = ConcurrentQueue<char>()
  let mutable private running = false
  let mutable private readerThread : Thread option = None

  /// Start background stdin reader. Call after raw mode is enabled.
  let start () =
    if running then ()
    else
      running <- true
      let t = Thread(fun () ->
        let stdin = Console.OpenStandardInput()
        let buf = Array.zeroCreate<byte> 64
        while running do
          try
            let n = stdin.Read(buf, 0, buf.Length)
            if n > 0 then
              for i in 0 .. n - 1 do
                charQueue.Enqueue(char buf.[i])
          with _ -> ())
      t.IsBackground <- true
      t.Name <- "SageFs-RawInput"
      t.Start()
      readerThread <- Some t

  /// Stop the reader thread
  let stop () =
    running <- false

  /// Process any available raw chars through the VT parser.
  /// Call this from the main loop.
  let processChars () =
    let mutable ch = '\000'
    while charQueue.TryDequeue(&ch) do
      parser.Feed(ch)
    // If parser is mid-sequence and no more chars, flush after natural delay
    if parser.IsMidSequence && charQueue.IsEmpty then
      parser.Flush()

  /// Try to dequeue the next parsed InputEvent
  let tryRead () = parser.TryDequeue()

  /// Check if there are parsed events available
  let hasEvents () = parser.HasEvents
