namespace SageFs

open System

/// Flags controlling how a render region behaves
[<Flags>]
type RegionFlags =
  | None        = 0
  | Clickable   = 1
  | Scrollable  = 2
  | Focusable   = 4
  | DrawBorder  = 8
  | LiveUpdate  = 16
  | Collapsible = 32
  | Draggable   = 64

/// A key combination — what the user physically presses
type KeyCombo = {
  Key: ConsoleKey
  Modifiers: ConsoleModifiers
  Char: char option
}

/// Cursor position within a region (line, column)
type RegionCursor = { Line: int; Col: int }

/// A rendered region of the UI
type RenderRegion = {
  Id: string
  Flags: RegionFlags
  Content: string
  Affordances: Affordance list
  Cursor: RegionCursor option
  Completions: CompletionOverlay option
}

/// Completion overlay data for rendering
and CompletionOverlay = {
  Items: string list
  SelectedIndex: int
}

/// A discoverable action the user can take
and Affordance = {
  Action: EditorAction
  Label: string
  KeyHint: KeyCombo option
  Enabled: bool
}

/// Cardinal directions for cursor movement
and [<RequireQualifiedAccess>] Direction =
  | Up
  | Down
  | Left
  | Right

/// Direction for history navigation
and [<RequireQualifiedAccess>] HistoryDirection =
  | Previous
  | Next

/// Editor mode (vi-style)
and [<RequireQualifiedAccess>] EditMode =
  | Normal
  | Insert
  | Visual

/// Every possible user action — exhaustive, serializable
and [<RequireQualifiedAccess>] EditorAction =
  // Text editing
  | InsertChar of char
  | DeleteBackward
  | DeleteForward
  | DeleteWord
  | DeleteToEndOfLine
  // Cursor movement
  | MoveCursor of Direction
  | SetCursorPosition of line: int * col: int
  | MoveWordForward
  | MoveWordBackward
  | MoveToLineStart
  | MoveToLineEnd
  // Selection
  | SelectAll
  | SelectWord
  // Completion
  | TriggerCompletion
  | AcceptCompletion
  | DismissCompletion
  | NextCompletion
  | PreviousCompletion
  // History
  | HistoryPrevious
  | HistoryNext
  | HistorySearch of string
  // Buffer
  | NewLine
  | Submit
  | Cancel
  | Undo
  | Redo
  // Mode switching
  | SwitchMode of EditMode
  // Session management
  | ListSessions
  | SwitchSession of string
  | CreateSession of projects: string list
  | StopSession of string
  | ToggleSessionPanel
  | ResetSession
  | HardResetSession
  // Session navigation (when Sessions pane focused)
  | SessionNavUp
  | SessionNavDown
  | SessionSelect
  | SessionDelete
  | SessionSetIndex of int
  // Quick session cycling (Ctrl+Tab / Ctrl+Shift+Tab)
  | SessionCycleNext
  | SessionCyclePrev
  // Output
  | ClearOutput
  // Prompt input (for inline prompts like session create)
  | PromptChar of char
  | PromptBackspace
  | PromptConfirm
  | PromptCancel

/// Every UI-level action (superset of EditorAction for renderers)
and [<RequireQualifiedAccess>] UiAction =
  | Editor of EditorAction
  | Quit
  | CycleFocus
  | FocusDir of Direction
  | ScrollUp
  | ScrollDown
  | Redraw
  | FontSizeUp
  | FontSizeDown
  | TogglePane of string
  | LayoutPreset of string
  | ResizeH of int   // delta for LeftRightSplit (-1 = narrower left, +1 = wider left)
  | ResizeV of int   // delta for OutputEditorSplit (-1 = less editor, +1 = more editor)
  | ResizeR of int   // delta for SessionsDiagSplit (-1 = less sessions, +1 = more sessions)

/// Maps physical keys to semantic actions
type KeyMap = Map<KeyCombo, UiAction>

module KeyCombo =
  let create key mods =
    { Key = key; Modifiers = mods; Char = None }

  let ctrl key = create key ConsoleModifiers.Control
  let alt key = create key ConsoleModifiers.Alt
  let ctrlAlt key = create key (ConsoleModifiers.Control ||| ConsoleModifiers.Alt)
  let ctrlShift key = create key (ConsoleModifiers.Control ||| ConsoleModifiers.Shift)
  let plain key = create key (enum<ConsoleModifiers> 0)

  /// Parse a string like "Ctrl+Q", "Alt+Up", "Ctrl+Shift+Z", "Enter", "PageUp"
  let tryParse (s: string) : KeyCombo option =
    let parts = s.Split('+') |> Array.map (fun p -> p.Trim())
    let mutable mods = enum<ConsoleModifiers> 0
    let mutable keyPart = None
    for p in parts do
      match p.ToLowerInvariant() with
      | "ctrl" | "control" -> mods <- mods ||| ConsoleModifiers.Control
      | "alt" -> mods <- mods ||| ConsoleModifiers.Alt
      | "shift" -> mods <- mods ||| ConsoleModifiers.Shift
      | _ -> keyPart <- Some p
    match keyPart with
    | None -> None
    | Some kp ->
      let parsed =
        match kp.ToLowerInvariant() with
        | "enter" | "return" -> Some ConsoleKey.Enter
        | "tab" -> Some ConsoleKey.Tab
        | "escape" | "esc" -> Some ConsoleKey.Escape
        | "space" | "spacebar" -> Some ConsoleKey.Spacebar
        | "backspace" -> Some ConsoleKey.Backspace
        | "delete" | "del" -> Some ConsoleKey.Delete
        | "up" | "uparrow" -> Some ConsoleKey.UpArrow
        | "down" | "downarrow" -> Some ConsoleKey.DownArrow
        | "left" | "leftarrow" -> Some ConsoleKey.LeftArrow
        | "right" | "rightarrow" -> Some ConsoleKey.RightArrow
        | "home" -> Some ConsoleKey.Home
        | "end" -> Some ConsoleKey.End
        | "pageup" | "pgup" -> Some ConsoleKey.PageUp
        | "pagedown" | "pgdn" -> Some ConsoleKey.PageDown
        | "=" | "equal" | "equals" -> Some ConsoleKey.OemPlus
        | "-" | "minus" -> Some ConsoleKey.OemMinus
        | s when s.Length = 1 ->
          let c = System.Char.ToUpper(s.[0])
          if c >= 'A' && c <= 'Z' then
            Some (enum<ConsoleKey> (int c))
          elif c >= '0' && c <= '9' then
            Some (enum<ConsoleKey> (int ConsoleKey.D0 + int c - int '0'))
          else None
        | _ -> None
      parsed |> Option.map (fun k -> { Key = k; Modifiers = mods; Char = None })

  /// Format a KeyCombo as a human-readable string
  let format (kc: KeyCombo) : string =
    let parts = ResizeArray<string>()
    if kc.Modifiers.HasFlag(ConsoleModifiers.Control) then parts.Add("Ctrl")
    if kc.Modifiers.HasFlag(ConsoleModifiers.Alt) then parts.Add("Alt")
    if kc.Modifiers.HasFlag(ConsoleModifiers.Shift) then parts.Add("Shift")
    let keyName =
      match kc.Key with
      | ConsoleKey.OemPlus -> "="
      | ConsoleKey.OemMinus -> "-"
      | ConsoleKey.UpArrow -> "Up"
      | ConsoleKey.DownArrow -> "Down"
      | ConsoleKey.LeftArrow -> "Left"
      | ConsoleKey.RightArrow -> "Right"
      | ConsoleKey.Spacebar -> "Space"
      | k -> sprintf "%A" k
    parts.Add(keyName)
    String.Join("+", parts)

module UiAction =
  /// Parse a string like "Quit", "Submit", "FocusLeft", "Editor.DeleteBackward"
  let tryParse (s: string) : UiAction option =
    match s.Trim() with
    | "Quit" -> Some UiAction.Quit
    | "CycleFocus" -> Some UiAction.CycleFocus
    | "FocusLeft" -> Some (UiAction.FocusDir Direction.Left)
    | "FocusRight" -> Some (UiAction.FocusDir Direction.Right)
    | "FocusUp" -> Some (UiAction.FocusDir Direction.Up)
    | "FocusDown" -> Some (UiAction.FocusDir Direction.Down)
    | "ScrollUp" -> Some UiAction.ScrollUp
    | "ScrollDown" -> Some UiAction.ScrollDown
    | "Redraw" -> Some UiAction.Redraw
    | "FontSizeUp" -> Some UiAction.FontSizeUp
    | "FontSizeDown" -> Some UiAction.FontSizeDown
    | "Submit" -> Some (UiAction.Editor EditorAction.Submit)
    | "NewLine" -> Some (UiAction.Editor EditorAction.NewLine)
    | "Cancel" -> Some (UiAction.Editor EditorAction.Cancel)
    | "Undo" -> Some (UiAction.Editor EditorAction.Undo)
    | "Redo" -> Some (UiAction.Editor EditorAction.Redo)
    | "DeleteBackward" -> Some (UiAction.Editor EditorAction.DeleteBackward)
    | "DeleteForward" -> Some (UiAction.Editor EditorAction.DeleteForward)
    | "DeleteWord" -> Some (UiAction.Editor EditorAction.DeleteWord)
    | "DeleteToEndOfLine" -> Some (UiAction.Editor EditorAction.DeleteToEndOfLine)
    | "MoveWordForward" -> Some (UiAction.Editor EditorAction.MoveWordForward)
    | "MoveWordBackward" -> Some (UiAction.Editor EditorAction.MoveWordBackward)
    | "MoveToLineStart" -> Some (UiAction.Editor EditorAction.MoveToLineStart)
    | "MoveToLineEnd" -> Some (UiAction.Editor EditorAction.MoveToLineEnd)
    | "MoveUp" -> Some (UiAction.Editor (EditorAction.MoveCursor Direction.Up))
    | "MoveDown" -> Some (UiAction.Editor (EditorAction.MoveCursor Direction.Down))
    | "MoveLeft" -> Some (UiAction.Editor (EditorAction.MoveCursor Direction.Left))
    | "MoveRight" -> Some (UiAction.Editor (EditorAction.MoveCursor Direction.Right))
    | "SelectAll" -> Some (UiAction.Editor EditorAction.SelectAll)
    | "SelectWord" -> Some (UiAction.Editor EditorAction.SelectWord)
    | "TriggerCompletion" -> Some (UiAction.Editor EditorAction.TriggerCompletion)
    | "AcceptCompletion" -> Some (UiAction.Editor EditorAction.AcceptCompletion)
    | "DismissCompletion" -> Some (UiAction.Editor EditorAction.DismissCompletion)
    | "NextCompletion" -> Some (UiAction.Editor EditorAction.NextCompletion)
    | "PreviousCompletion" -> Some (UiAction.Editor EditorAction.PreviousCompletion)
    | "HistoryPrevious" -> Some (UiAction.Editor EditorAction.HistoryPrevious)
    | "HistoryNext" -> Some (UiAction.Editor EditorAction.HistoryNext)
    | "ListSessions" -> Some (UiAction.Editor EditorAction.ListSessions)
    | "ToggleSessionPanel" -> Some (UiAction.Editor EditorAction.ToggleSessionPanel)
    | "CreateSession" -> Some (UiAction.Editor (EditorAction.CreateSession []))
    | "ResetSession" -> Some (UiAction.Editor EditorAction.ResetSession)
    | "HardResetSession" -> Some (UiAction.Editor EditorAction.HardResetSession)
    | "SessionNavUp" -> Some (UiAction.Editor EditorAction.SessionNavUp)
    | "SessionNavDown" -> Some (UiAction.Editor EditorAction.SessionNavDown)
    | "SessionSelect" -> Some (UiAction.Editor EditorAction.SessionSelect)
    | "SessionDelete" -> Some (UiAction.Editor EditorAction.SessionDelete)
    | "SessionCycleNext" -> Some (UiAction.Editor EditorAction.SessionCycleNext)
    | "SessionCyclePrev" -> Some (UiAction.Editor EditorAction.SessionCyclePrev)
    | "ClearOutput" -> Some (UiAction.Editor EditorAction.ClearOutput)
    | "PromptConfirm" -> Some (UiAction.Editor EditorAction.PromptConfirm)
    | "PromptCancel" -> Some (UiAction.Editor EditorAction.PromptCancel)
    | s when s.StartsWith("TogglePane.") -> Some (UiAction.TogglePane (s.Substring(11)))
    | s when s.StartsWith("Layout.") -> Some (UiAction.LayoutPreset (s.Substring(7)))
    | "ResizeHGrow" -> Some (UiAction.ResizeH 1)
    | "ResizeHShrink" -> Some (UiAction.ResizeH -1)
    | "ResizeVGrow" -> Some (UiAction.ResizeV 1)
    | "ResizeVShrink" -> Some (UiAction.ResizeV -1)
    | "ResizeRGrow" -> Some (UiAction.ResizeR 1)
    | "ResizeRShrink" -> Some (UiAction.ResizeR -1)
    | _ -> None

module KeyMap =
  let hintFor (keyMap: KeyMap) (action: EditorAction) : KeyCombo option =
    keyMap
    |> Map.tryFindKey (fun _ a -> a = UiAction.Editor action)

  /// Default keybindings shared by all UI renderers
  let defaults : KeyMap =
    let e a = UiAction.Editor a
    [ // Quit
      KeyCombo.ctrl ConsoleKey.Q, UiAction.Quit
      KeyCombo.ctrl ConsoleKey.D, UiAction.Quit
      // Focus
      KeyCombo.plain ConsoleKey.Tab, UiAction.CycleFocus
      KeyCombo.ctrl ConsoleKey.H, UiAction.FocusDir Direction.Left
      KeyCombo.ctrl ConsoleKey.J, UiAction.FocusDir Direction.Down
      KeyCombo.ctrl ConsoleKey.K, UiAction.FocusDir Direction.Up
      KeyCombo.ctrl ConsoleKey.L, UiAction.FocusDir Direction.Right
      // Scroll
      KeyCombo.alt ConsoleKey.UpArrow, UiAction.ScrollUp
      KeyCombo.alt ConsoleKey.DownArrow, UiAction.ScrollDown
      KeyCombo.plain ConsoleKey.PageUp, UiAction.ScrollUp
      KeyCombo.plain ConsoleKey.PageDown, UiAction.ScrollDown
      // Font size
      KeyCombo.ctrl ConsoleKey.OemPlus, UiAction.FontSizeUp
      KeyCombo.ctrl ConsoleKey.OemMinus, UiAction.FontSizeDown
      // Session management
      KeyCombo.ctrl ConsoleKey.N, e (EditorAction.CreateSession [])
      KeyCombo.ctrlAlt ConsoleKey.S, e EditorAction.ToggleSessionPanel
      KeyCombo.ctrlAlt ConsoleKey.R, e EditorAction.ResetSession
      KeyCombo.ctrlAlt ConsoleKey.H, e EditorAction.HardResetSession
      // Quick session cycling
      KeyCombo.ctrl ConsoleKey.Tab, e EditorAction.SessionCycleNext
      KeyCombo.ctrlShift ConsoleKey.Tab, e EditorAction.SessionCyclePrev
      // Submit / NewLine
      KeyCombo.ctrl ConsoleKey.Enter, e EditorAction.Submit
      KeyCombo.plain ConsoleKey.Enter, e EditorAction.NewLine
      // Editing
      KeyCombo.plain ConsoleKey.Backspace, e EditorAction.DeleteBackward
      KeyCombo.plain ConsoleKey.Delete, e EditorAction.DeleteForward
      KeyCombo.ctrl ConsoleKey.Backspace, e EditorAction.DeleteWord
      // History
      KeyCombo.ctrl ConsoleKey.UpArrow, e EditorAction.HistoryPrevious
      KeyCombo.ctrl ConsoleKey.DownArrow, e EditorAction.HistoryNext
      // Cursor movement
      KeyCombo.plain ConsoleKey.UpArrow, e (EditorAction.MoveCursor Direction.Up)
      KeyCombo.plain ConsoleKey.DownArrow, e (EditorAction.MoveCursor Direction.Down)
      KeyCombo.plain ConsoleKey.LeftArrow, e (EditorAction.MoveCursor Direction.Left)
      KeyCombo.plain ConsoleKey.RightArrow, e (EditorAction.MoveCursor Direction.Right)
      KeyCombo.ctrl ConsoleKey.LeftArrow, e EditorAction.MoveWordBackward
      KeyCombo.ctrl ConsoleKey.RightArrow, e EditorAction.MoveWordForward
      KeyCombo.plain ConsoleKey.Home, e EditorAction.MoveToLineStart
      KeyCombo.plain ConsoleKey.End, e EditorAction.MoveToLineEnd
      // Selection & completion
      KeyCombo.ctrl ConsoleKey.A, e EditorAction.SelectAll
      KeyCombo.ctrl ConsoleKey.Spacebar, e EditorAction.TriggerCompletion
      KeyCombo.plain ConsoleKey.Escape, e EditorAction.DismissCompletion
      // Undo/Redo
      KeyCombo.ctrl ConsoleKey.Z, e EditorAction.Undo
      KeyCombo.ctrlShift ConsoleKey.Z, e EditorAction.Redo
      KeyCombo.ctrl ConsoleKey.R, e EditorAction.Undo
      // Cancel
      KeyCombo.ctrl ConsoleKey.C, e EditorAction.Cancel
      // Layout presets
      KeyCombo.ctrlAlt ConsoleKey.D1, UiAction.LayoutPreset "default"
      KeyCombo.ctrlAlt ConsoleKey.D2, UiAction.LayoutPreset "focus"
      KeyCombo.ctrlAlt ConsoleKey.D3, UiAction.LayoutPreset "minimal"
      // Pane toggle
      KeyCombo.ctrlAlt ConsoleKey.O, UiAction.TogglePane "Output"
      KeyCombo.ctrlAlt ConsoleKey.D, UiAction.TogglePane "Diagnostics"
      // Pane resize
      KeyCombo.ctrlAlt ConsoleKey.LeftArrow, UiAction.ResizeH -1
      KeyCombo.ctrlAlt ConsoleKey.RightArrow, UiAction.ResizeH 1
      KeyCombo.ctrlAlt ConsoleKey.UpArrow, UiAction.ResizeV 1
      KeyCombo.ctrlAlt ConsoleKey.DownArrow, UiAction.ResizeV -1
      // Clear output
      KeyCombo.ctrlShift ConsoleKey.L, e EditorAction.ClearOutput
    ] |> Map.ofList

  /// Merge user overrides onto defaults (overrides win)
  let merge (overrides: KeyMap) (base': KeyMap) : KeyMap =
    overrides |> Map.fold (fun acc k v -> Map.add k v acc) base'

  /// Parse keybinding lines from config.fsx format:
  ///   let keybindings = [ "Ctrl+Q", "Quit"; "Ctrl+Enter", "Submit" ]
  let parseConfigLines (lines: string array) : KeyMap =
    let mutable bindings = Map.empty
    let mutable inBindings = false
    for line in lines do
      let trimmed = line.Trim()
      if trimmed.StartsWith("let keybindings") || trimmed.StartsWith("let Keybindings") then
        inBindings <- true
      if inBindings then
        // Extract "Key", "Action" pairs
        let mutable i = 0
        while i < trimmed.Length do
          let q1 = trimmed.IndexOf('"', i)
          if q1 >= 0 then
            let q2 = trimmed.IndexOf('"', q1 + 1)
            if q2 > q1 then
              let keyStr = trimmed.Substring(q1 + 1, q2 - q1 - 1)
              let q3 = trimmed.IndexOf('"', q2 + 1)
              if q3 > q2 then
                let q4 = trimmed.IndexOf('"', q3 + 1)
                if q4 > q3 then
                  let actionStr = trimmed.Substring(q3 + 1, q4 - q3 - 1)
                  match KeyCombo.tryParse keyStr, UiAction.tryParse actionStr with
                  | Some kc, Some act -> bindings <- Map.add kc act bindings
                  | _ -> ()
                  i <- q4 + 1
                else i <- trimmed.Length
              else i <- trimmed.Length
            else i <- trimmed.Length
          else i <- trimmed.Length
        if trimmed.Contains(']') then inBindings <- false
    bindings
