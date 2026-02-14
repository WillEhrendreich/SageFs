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

/// A rendered region of the UI
type RenderRegion = {
  Id: string
  Flags: RegionFlags
  Content: string
  Affordances: Affordance list
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

/// Maps physical keys to semantic actions
type KeyMap = Map<KeyCombo, EditorAction>

module KeyMap =
  let hintFor (keyMap: KeyMap) (action: EditorAction) : KeyCombo option =
    keyMap
    |> Map.tryFindKey (fun _ a -> a = action)
