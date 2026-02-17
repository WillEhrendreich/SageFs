namespace SageFs

open System

open System.Runtime.InteropServices

/// ANSI escape codes for terminal rendering
module AnsiCodes =
  let esc = "\x1b["
  let reset = sprintf "%s0m" esc
  let bold = sprintf "%s1m" esc
  let dim = sprintf "%s2m" esc
  let inverse = sprintf "%s7m" esc
  let hideCursor = sprintf "%s?25l" esc
  let showCursor = sprintf "%s?25h" esc
  let clearScreen = sprintf "%s2J" esc
  let clearLine = sprintf "%s2K" esc
  let home = sprintf "%sH" esc
  let enterAltScreen = sprintf "%s?1049h" esc
  let leaveAltScreen = sprintf "%s?1049l" esc

  let moveTo row col = sprintf "%s%d;%dH" esc row col
  let moveUp n = sprintf "%s%dA" esc n
  let moveDown n = sprintf "%s%dB" esc n

  let fg256 n = sprintf "%s38;5;%dm" esc n
  let bg256 n = sprintf "%s48;5;%dm" esc n

  // Color scheme (matching dashboard CSS)
  let green = fg256 114
  let red = fg256 203
  let yellow = fg256 179
  let cyan = fg256 116
  let dimWhite = fg256 245
  let white = fg256 255
  let bgPanel = bg256 235
  let bgEditor = bg256 234

  // Box-drawing characters (Unicode — requires VT100 terminal)
  let boxH = "\u2500"
  let boxV = "\u2502"
  let boxTL = "\u250C"
  let boxTR = "\u2510"
  let boxBL = "\u2514"
  let boxBR = "\u2518"
  let boxHD = "\u252C" // horizontal + down junction
  let boxHU = "\u2534" // horizontal + up junction
  let boxVR = "\u251C" // vertical + right junction
  let boxVL = "\u2524" // vertical + left junction
  let boxCross = "\u253C" // cross junction

  let hline width =
    String.replicate width boxH

  let boxTop title width borderColor =
    let titleLen = min (String.length title) (width - 4)
    let t = if titleLen > 0 then title.Substring(0, titleLen) else ""
    let lineLen = max 0 (width - titleLen - 5)
    sprintf "%s%s%s %s%s%s %s%s"
      borderColor boxTL boxH
      (fg256 255) t
      borderColor (hline lineLen) boxTR

  let boxBottom width borderColor =
    sprintf "%s%s%s%s"
      borderColor boxBL (hline (width - 2)) boxBR

  /// Delegate types for Windows console P/Invoke (byref can't be used in Func<>)
  [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
  type GetConsoleModeDelegate = delegate of nativeint * byref<uint32> -> bool
  [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
  type SetConsoleModeDelegate = delegate of nativeint * uint32 -> bool

  /// Try to enable Windows VT100 processing for ANSI escape support.
  /// Returns true if VT100 is available.
  let enableVT100 () =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
      try
        // P/Invoke kernel32 for VT100 on Windows
        let STD_OUTPUT_HANDLE = -11
        let ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004u
        let handle =
          NativeLibrary.GetExport(
            NativeLibrary.Load("kernel32.dll"),
            "GetStdHandle")
          |> fun ptr ->
            let fn = Marshal.GetDelegateForFunctionPointer<Func<int, nativeint>>(ptr)
            fn.Invoke(STD_OUTPUT_HANDLE)
        if handle <> nativeint -1 then
          let getMode =
            NativeLibrary.GetExport(
              NativeLibrary.Load("kernel32.dll"),
              "GetConsoleMode")
          let setMode =
            NativeLibrary.GetExport(
              NativeLibrary.Load("kernel32.dll"),
              "SetConsoleMode")
          let mutable mode = 0u
          let getModeResult =
            let fn = Marshal.GetDelegateForFunctionPointer<GetConsoleModeDelegate>(getMode)
            fn.Invoke(handle, &mode)
          if getModeResult then
            let newMode = mode ||| ENABLE_VIRTUAL_TERMINAL_PROCESSING
            let fn = Marshal.GetDelegateForFunctionPointer<SetConsoleModeDelegate>(setMode)
            fn.Invoke(handle, newMode) |> ignore
        Console.OutputEncoding <- Text.Encoding.UTF8
        true
      with _ -> false
    else
      // Unix terminals generally support VT100 natively
      Console.OutputEncoding <- Text.Encoding.UTF8
      true


/// Strongly-typed pane identifier — eliminates stringly-typed region matching
[<RequireQualifiedAccess>]
type PaneId =
  | Output
  | Sessions
  | Diagnostics
  | Editor

module PaneId =
  let all = [| PaneId.Output; PaneId.Sessions; PaneId.Diagnostics; PaneId.Editor |]

  let toRegionId = function
    | PaneId.Output -> "output"
    | PaneId.Sessions -> "sessions"
    | PaneId.Diagnostics -> "diagnostics"
    | PaneId.Editor -> "editor"

  let fromRegionId = function
    | "output" -> Some PaneId.Output
    | "sessions" -> Some PaneId.Sessions
    | "diagnostics" -> Some PaneId.Diagnostics
    | "editor" -> Some PaneId.Editor
    | _ -> None

  let next (current: PaneId) : PaneId =
    let idx = all |> Array.findIndex ((=) current)
    all.[(idx + 1) % all.Length]

  /// Navigate to the nearest pane in the given direction based on layout positions.
  /// Returns the current pane if no neighbor exists in that direction.
  let navigate (direction: Direction) (current: PaneId) (paneRects: (PaneId * Rect) list) : PaneId =
    let currentRect =
      paneRects |> List.tryFind (fun (id, _) -> id = current) |> Option.map snd
    match currentRect with
    | None -> current
    | Some cr ->
      let centerRow = cr.Row + cr.Height / 2
      let centerCol = cr.Col + cr.Width / 2
      let candidates =
        paneRects
        |> List.filter (fun (id, r) ->
          if id = current then false
          else
            let cRow = r.Row + r.Height / 2
            let cCol = r.Col + r.Width / 2
            match direction with
            | Direction.Left  -> cCol < centerCol
            | Direction.Right -> cCol > centerCol
            | Direction.Up    -> cRow < centerRow
            | Direction.Down  -> cRow > centerRow)
      match candidates with
      | [] -> current
      | _ ->
        candidates
        |> List.minBy (fun (_, r) ->
          let cRow = r.Row + r.Height / 2
          let cCol = r.Col + r.Width / 2
          let dr = cRow - centerRow
          let dc = cCol - centerCol
          dr * dr + dc * dc)
        |> fst

  let displayName = function
    | PaneId.Output -> "Output"
    | PaneId.Sessions -> "Sessions"
    | PaneId.Diagnostics -> "Diagnostics"
    | PaneId.Editor -> "Editor"

  let tryParse = function
    | "Output" | "output" -> Some PaneId.Output
    | "Sessions" | "sessions" -> Some PaneId.Sessions
    | "Diagnostics" | "diagnostics" -> Some PaneId.Diagnostics
    | "Editor" | "editor" -> Some PaneId.Editor
    | _ -> None


/// Frame diffing — only redraw rows that changed between frames
module FrameDiff =
  /// Split a rendered frame into (row, content) pairs based on moveTo commands
  let private parsePositionedLines (frame: string) : Map<int, string> =
    let mutable result = Map.empty
    let mutable currentRow = -1
    let sb = System.Text.StringBuilder()
    let mutable i = 0

    while i < frame.Length do
      if i + 2 < frame.Length && frame.[i] = '\x1b' && frame.[i+1] = '[' then
        let mutable j = i + 2
        while j < frame.Length
          && frame.[j] <> 'H' && frame.[j] <> 'A'
          && frame.[j] <> 'B' && frame.[j] <> 'J'
          && frame.[j] <> 'K' && frame.[j] <> 'm' do
          j <- j + 1
        if j < frame.Length && frame.[j] = 'H' then
          let coords = frame.Substring(i + 2, j - i - 2)
          let parts = coords.Split(';')
          if parts.Length >= 1 then
            if currentRow >= 0 then
              result <- result |> Map.add currentRow (sb.ToString())
              sb.Clear() |> ignore
            match Int32.TryParse(parts.[0]) with
            | true, row -> currentRow <- row
            | _ -> ()
          sb.Append(frame.Substring(i, j - i + 1)) |> ignore
          i <- j + 1
        else
          sb.Append(frame.Substring(i, j - i + 1)) |> ignore
          i <- j + 1
      else
        sb.Append(frame.[i]) |> ignore
        i <- i + 1

    if currentRow >= 0 then
      result <- result |> Map.add currentRow (sb.ToString())
    result

  /// Create a diff frame containing only rows that changed
  let diff (prevFrame: string) (newFrame: string) : string =
    let prevLines = parsePositionedLines prevFrame
    let newLines = parsePositionedLines newFrame
    let sb = System.Text.StringBuilder()

    for kv in newLines do
      match prevLines |> Map.tryFind kv.Key with
      | Some prev when prev = kv.Value -> ()
      | _ -> sb.Append(kv.Value) |> ignore

    sb.ToString()
module TerminalUIState =
  /// When true, eprintfn output should be suppressed (terminal UI owns the console)
  let mutable IsActive = false
  /// Lock for synchronized console writes
  let consoleLock = obj ()


/// Terminal-specific commands beyond EditorAction
[<RequireQualifiedAccess>]
type TerminalCommand =
  | Action of EditorAction
  | CycleFocus
  | FocusDirection of Direction
  | ScrollUp
  | ScrollDown
  | Redraw
  | Quit
  | TogglePane of string
  | LayoutPreset of string
  | ResizeH of int
  | ResizeV of int
  | ResizeR of int


/// Map console key presses to terminal commands
module TerminalInput =
  /// Convert ConsoleKeyInfo to a KeyCombo for map lookup
  let private toKeyCombo (key: ConsoleKeyInfo) : KeyCombo =
    { Key = key.Key; Modifiers = key.Modifiers; Char = None }

  /// Look up action in the keybinding map, then fall back to char insertion
  let mapKeyWith (keyMap: KeyMap) (key: ConsoleKeyInfo) : TerminalCommand option =
    let combo = toKeyCombo key
    match keyMap |> Map.tryFind combo with
    | Some (UiAction.Quit) -> Some TerminalCommand.Quit
    | Some (UiAction.CycleFocus) -> Some TerminalCommand.CycleFocus
    | Some (UiAction.FocusDir d) -> Some (TerminalCommand.FocusDirection d)
    | Some (UiAction.ScrollUp) -> Some TerminalCommand.ScrollUp
    | Some (UiAction.ScrollDown) -> Some TerminalCommand.ScrollDown
    | Some (UiAction.Redraw) -> Some TerminalCommand.Redraw
    | Some (UiAction.FontSizeUp) -> None // TUI can't change font size
    | Some (UiAction.FontSizeDown) -> None
    | Some (UiAction.TogglePane p) -> Some (TerminalCommand.TogglePane p)
    | Some (UiAction.LayoutPreset p) -> Some (TerminalCommand.LayoutPreset p)
    | Some (UiAction.ResizeH d) -> Some (TerminalCommand.ResizeH d)
    | Some (UiAction.ResizeV d) -> Some (TerminalCommand.ResizeV d)
    | Some (UiAction.ResizeR d) -> Some (TerminalCommand.ResizeR d)
    | Some (UiAction.Editor action) -> Some (TerminalCommand.Action action)
    | None ->
      // Fall through to character insertion for printable chars
      if key.KeyChar >= ' ' && key.KeyChar <= '~' then
        Some (TerminalCommand.Action (EditorAction.InsertChar key.KeyChar))
      elif key.KeyChar > '\x7f' then
        Some (TerminalCommand.Action (EditorAction.InsertChar key.KeyChar))
      else None

  /// Map using default keybindings (backwards compatibility)
  let mapKey (key: ConsoleKeyInfo) : TerminalCommand option =
    mapKeyWith KeyMap.defaults key
