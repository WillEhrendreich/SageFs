namespace SageFs

open System

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

  // Box-drawing characters
  let boxH = "\u2500"
  let boxV = "\u2502"
  let boxTL = "\u250C"
  let boxTR = "\u2510"
  let boxBL = "\u2514"
  let boxBR = "\u2518"

  let hline width =
    String.replicate width boxH

  let boxTop title width borderColor =
    let titleLen = min (String.length title) (width - 4)
    let t = if titleLen > 0 then title.Substring(0, titleLen) else ""
    let lineLen = max 0 (width - titleLen - 4)
    sprintf "%s%s%s %s%s%s %s%s"
      borderColor boxTL boxH
      (fg256 255) t
      borderColor (hline lineLen) boxTR

  let boxBottom width borderColor =
    sprintf "%s%s%s%s"
      borderColor boxBL (hline (width - 2)) boxBR


/// A positioned region in the terminal
type TerminalPane = {
  RegionId: string
  Title: string
  Row: int
  Col: int
  Width: int
  Height: int
  ScrollOffset: int
  Focused: bool
}

/// Computed terminal layout
type TerminalLayout = {
  Rows: int
  Cols: int
  Panes: TerminalPane list
  StatusBarRow: int
}

module TerminalLayout =
  let compute (rows: int) (cols: int) : TerminalLayout =
    let statusRow = rows
    let leftW = int (float cols * 0.65) |> max 20
    let rightW = cols - leftW
    let topH = int (float (rows - 6) * 0.65) |> max 4
    let sessH = (rows - 6 - topH) / 2 |> max 2
    let diagH = rows - 6 - topH - sessH |> max 2
    let editorH = 6

    let output =
      { RegionId = "output"; Title = "Output"
        Row = 1; Col = 1; Width = leftW; Height = topH
        ScrollOffset = 0; Focused = false }
    let sessions =
      { RegionId = "sessions"; Title = "Sessions"
        Row = 1; Col = leftW + 1; Width = rightW; Height = sessH
        ScrollOffset = 0; Focused = false }
    let diagnostics =
      { RegionId = "diagnostics"; Title = "Diagnostics"
        Row = 1 + sessH; Col = leftW + 1; Width = rightW; Height = diagH
        ScrollOffset = 0; Focused = false }
    let editor =
      { RegionId = "editor"; Title = "Editor"
        Row = topH + 1; Col = 1; Width = cols; Height = editorH
        ScrollOffset = 0; Focused = true }

    { Rows = rows; Cols = cols
      Panes = [ output; sessions; diagnostics; editor ]
      StatusBarRow = statusRow }


/// Pure terminal rendering functions
module TerminalRender =
  let fitToWidth (width: int) (s: string) : string =
    if String.length s >= width then s.Substring(0, width)
    else s.PadRight(width)

  let visibleLines (content: string) (scrollOffset: int) (height: int) : string list =
    let lines = content.Split('\n') |> Array.toList
    let safeOffset = max 0 (min scrollOffset (max 0 (lines.Length - height)))
    lines
    |> List.skip safeOffset
    |> List.truncate height

  let renderPane (pane: TerminalPane) (region: RenderRegion option) : string =
    let sb = System.Text.StringBuilder()
    let borderColor = if pane.Focused then AnsiCodes.cyan else AnsiCodes.dimWhite

    // Top border with title
    sb.Append(AnsiCodes.moveTo pane.Row pane.Col) |> ignore
    sb.Append(AnsiCodes.boxTop pane.Title pane.Width borderColor) |> ignore

    // Content lines
    let contentHeight = pane.Height - 2
    let content = region |> Option.map (fun r -> r.Content) |> Option.defaultValue ""
    let lines = visibleLines content pane.ScrollOffset contentHeight

    for i in 0 .. contentHeight - 1 do
      sb.Append(AnsiCodes.moveTo (pane.Row + 1 + i) pane.Col) |> ignore
      let line = if i < lines.Length then lines.[i] else ""
      let inner = fitToWidth (pane.Width - 2) line
      sb.Append(sprintf "%s%s%s%s%s%s"
        borderColor AnsiCodes.boxV AnsiCodes.reset
        inner borderColor AnsiCodes.boxV) |> ignore

    // Bottom border
    sb.Append(AnsiCodes.moveTo (pane.Row + pane.Height - 1) pane.Col) |> ignore
    sb.Append(AnsiCodes.boxBottom pane.Width borderColor) |> ignore
    sb.Append(AnsiCodes.reset) |> ignore
    sb.ToString()

  let renderStatusBar (row: int) (cols: int) (sessionState: string) (evalCount: int) (focusedPane: string) : string =
    let sb = System.Text.StringBuilder()
    sb.Append(AnsiCodes.moveTo row 1) |> ignore
    sb.Append(AnsiCodes.inverse) |> ignore
    let status =
      sprintf " %s | evals: %d | focus: %s | Ctrl+Enter:eval Tab:focus Ctrl+D:quit "
        sessionState evalCount focusedPane
    sb.Append(fitToWidth cols status) |> ignore
    sb.Append(AnsiCodes.reset) |> ignore
    sb.ToString()

  let renderFrame (layout: TerminalLayout) (regions: RenderRegion list) (sessionState: string) (evalCount: int) : string =
    let sb = System.Text.StringBuilder()
    sb.Append(AnsiCodes.hideCursor) |> ignore

    for pane in layout.Panes do
      let region = regions |> List.tryFind (fun r -> r.Id = pane.RegionId)
      sb.Append(renderPane pane region) |> ignore

    // Find focused pane for status bar
    let focusedName =
      layout.Panes
      |> List.tryFind (fun p -> p.Focused)
      |> Option.map (fun p -> p.Title)
      |> Option.defaultValue "?"
    sb.Append(renderStatusBar layout.StatusBarRow layout.Cols sessionState evalCount focusedName) |> ignore

    // Position cursor in editor if focused
    let editorPane = layout.Panes |> List.tryFind (fun p -> p.RegionId = "editor" && p.Focused)
    match editorPane with
    | Some ep ->
      sb.Append(AnsiCodes.moveTo (ep.Row + 1) (ep.Col + 1)) |> ignore
      sb.Append(AnsiCodes.showCursor) |> ignore
    | None -> ()

    sb.ToString()


/// Terminal-specific commands beyond EditorAction
[<RequireQualifiedAccess>]
type TerminalCommand =
  | Action of EditorAction
  | CycleFocus
  | ScrollUp
  | ScrollDown
  | Redraw
  | Quit


/// Map console key presses to terminal commands
module TerminalInput =
  let mapKey (key: ConsoleKeyInfo) : TerminalCommand option =
    let ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control)
    let alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt)
    let shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift)

    match key.Key, ctrl, alt, shift with
    // Terminal-level commands
    | ConsoleKey.Tab, false, false, false -> Some TerminalCommand.CycleFocus
    | ConsoleKey.L, true, false, false -> Some TerminalCommand.Redraw
    | ConsoleKey.D, true, false, false -> Some TerminalCommand.Quit
    | ConsoleKey.C, true, false, false -> Some (TerminalCommand.Action EditorAction.Cancel)

    // Scroll (Alt+Up/Down or PageUp/PageDown)
    | ConsoleKey.UpArrow, false, true, false -> Some TerminalCommand.ScrollUp
    | ConsoleKey.DownArrow, false, true, false -> Some TerminalCommand.ScrollDown
    | ConsoleKey.PageUp, false, false, false -> Some TerminalCommand.ScrollUp
    | ConsoleKey.PageDown, false, false, false -> Some TerminalCommand.ScrollDown

    // Submit (Ctrl+Enter)
    | ConsoleKey.Enter, true, false, false -> Some (TerminalCommand.Action EditorAction.Submit)
    // New line (plain Enter)
    | ConsoleKey.Enter, false, false, false -> Some (TerminalCommand.Action EditorAction.NewLine)

    // Reset (Ctrl+R)
    | ConsoleKey.R, true, false, false -> Some (TerminalCommand.Action EditorAction.Undo)

    // Cursor movement
    | ConsoleKey.UpArrow, false, false, false -> Some (TerminalCommand.Action (EditorAction.MoveCursor Direction.Up))
    | ConsoleKey.DownArrow, false, false, false -> Some (TerminalCommand.Action (EditorAction.MoveCursor Direction.Down))
    | ConsoleKey.LeftArrow, false, false, false -> Some (TerminalCommand.Action (EditorAction.MoveCursor Direction.Left))
    | ConsoleKey.RightArrow, false, false, false -> Some (TerminalCommand.Action (EditorAction.MoveCursor Direction.Right))

    // Word movement (Ctrl+Left/Right)
    | ConsoleKey.LeftArrow, true, false, false -> Some (TerminalCommand.Action EditorAction.MoveWordBackward)
    | ConsoleKey.RightArrow, true, false, false -> Some (TerminalCommand.Action EditorAction.MoveWordForward)

    // Home/End
    | ConsoleKey.Home, false, false, false -> Some (TerminalCommand.Action EditorAction.MoveToLineStart)
    | ConsoleKey.End, false, false, false -> Some (TerminalCommand.Action EditorAction.MoveToLineEnd)

    // Backspace / Delete
    | ConsoleKey.Backspace, false, false, false -> Some (TerminalCommand.Action EditorAction.DeleteBackward)
    | ConsoleKey.Delete, false, false, false -> Some (TerminalCommand.Action EditorAction.DeleteForward)
    | ConsoleKey.Backspace, true, false, false -> Some (TerminalCommand.Action EditorAction.DeleteWord)

    // History
    | ConsoleKey.UpArrow, true, false, false -> Some (TerminalCommand.Action EditorAction.HistoryPrevious)
    | ConsoleKey.DownArrow, true, false, false -> Some (TerminalCommand.Action EditorAction.HistoryNext)

    // Completion
    | ConsoleKey.Spacebar, true, false, false -> Some (TerminalCommand.Action EditorAction.TriggerCompletion)
    | ConsoleKey.Escape, false, false, false -> Some (TerminalCommand.Action EditorAction.DismissCompletion)

    // Select all
    | ConsoleKey.A, true, false, false -> Some (TerminalCommand.Action EditorAction.SelectAll)

    // Session panel toggle
    | ConsoleKey.S, true, true, false -> Some (TerminalCommand.Action EditorAction.ToggleSessionPanel)

    // Printable characters
    | _, false, false, _ when key.KeyChar >= ' ' && key.KeyChar <= '~' ->
      Some (TerminalCommand.Action (EditorAction.InsertChar key.KeyChar))
    | _, false, false, _ when key.KeyChar > '\x7f' ->
      Some (TerminalCommand.Action (EditorAction.InsertChar key.KeyChar))

    | _ -> None
