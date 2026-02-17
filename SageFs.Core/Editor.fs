namespace SageFs

/// Cursor position within the editor buffer
type CursorPosition = { Line: int; Column: int }

/// A text selection range
type Selection = { Start: CursorPosition; End: CursorPosition }

/// A single completion item
type CompletionItem = { Label: string; Kind: string; Detail: string option }

/// State of the completion popup
type CompletionMenu = {
  Items: CompletionItem list
  SelectedIndex: int
  FilterText: string
}

/// History navigation state
type HistoryState = {
  Entries: string list
  Position: int
  Draft: string
}

/// Inline prompt for gathering user input (e.g. session create directory)
type PromptState = {
  Label: string
  Input: string
  Purpose: PromptPurpose
}

and [<RequireQualifiedAccess>] PromptPurpose =
  | CreateSessionDir

/// Errors when constructing a ValidatedBuffer
[<RequireQualifiedAccess>]
type BufferError =
  | EmptyLines
  | CursorOutOfBounds of cursor: CursorPosition * lineCount: int * maxCol: int

/// A buffer with invariants enforced: never empty, cursor always in bounds
type ValidatedBuffer = private {
  Lines: string list
  Cursor: CursorPosition
}

module ValidatedBuffer =
  let create (lines: string list) (cursor: CursorPosition) : Result<ValidatedBuffer, BufferError> =
    match lines with
    | [] -> Error BufferError.EmptyLines
    | _ ->
      let lineCount = lines.Length
      let maxLine = lineCount - 1
      let clampedLine = min (max 0 cursor.Line) maxLine
      let maxCol = lines.[clampedLine].Length
      if cursor.Line < 0 || cursor.Line > maxLine || cursor.Column < 0 || cursor.Column > maxCol then
        Error (BufferError.CursorOutOfBounds (cursor, lineCount, maxCol))
      else
        Ok { Lines = lines; Cursor = cursor }

  let empty =
    { Lines = [""]; Cursor = { Line = 0; Column = 0 } }

  let lines (buf: ValidatedBuffer) = buf.Lines
  let cursor (buf: ValidatedBuffer) = buf.Cursor
  let currentLine (buf: ValidatedBuffer) = buf.Lines.[buf.Cursor.Line]

  let setCursor (pos: CursorPosition) (buf: ValidatedBuffer) : ValidatedBuffer =
    let maxLine = buf.Lines.Length - 1
    let line = min maxLine (max 0 pos.Line)
    let maxCol = buf.Lines.[line].Length
    let col = min maxCol (max 0 pos.Column)
    { buf with Cursor = { Line = line; Column = col } }

  let text (buf: ValidatedBuffer) =
    buf.Lines |> String.concat "\n"

  let insertChar (c: char) (buf: ValidatedBuffer) : ValidatedBuffer =
    let line = buf.Lines.[buf.Cursor.Line]
    let col = buf.Cursor.Column
    let newLine = line.Insert(col, string c)
    let newLines =
      buf.Lines
      |> List.mapi (fun i l -> if i = buf.Cursor.Line then newLine else l)
    { Lines = newLines; Cursor = { buf.Cursor with Column = col + 1 } }

  let deleteBackward (buf: ValidatedBuffer) : ValidatedBuffer =
    let line = buf.Lines.[buf.Cursor.Line]
    let col = buf.Cursor.Column
    if col > 0 then
      let newLine = line.Remove(col - 1, 1)
      let newLines =
        buf.Lines
        |> List.mapi (fun i l -> if i = buf.Cursor.Line then newLine else l)
      { Lines = newLines; Cursor = { buf.Cursor with Column = col - 1 } }
    elif buf.Cursor.Line > 0 then
      let prevLine = buf.Lines.[buf.Cursor.Line - 1]
      let joined = prevLine + line
      let newLines =
        buf.Lines
        |> List.indexed
        |> List.choose (fun (i, l) ->
          if i = buf.Cursor.Line - 1 then Some joined
          elif i = buf.Cursor.Line then None
          else Some l)
      { Lines = newLines; Cursor = { Line = buf.Cursor.Line - 1; Column = prevLine.Length } }
    else
      buf

  let newLine (buf: ValidatedBuffer) : ValidatedBuffer =
    let line = buf.Lines.[buf.Cursor.Line]
    let col = buf.Cursor.Column
    let before = line.[..col-1]
    let after = if col < line.Length then line.[col..] else ""
    let before' = if col = 0 then "" else before
    let newLines =
      buf.Lines
      |> List.indexed
      |> List.collect (fun (i, l) ->
        if i = buf.Cursor.Line then [before'; after]
        else [l])
    { Lines = newLines; Cursor = { Line = buf.Cursor.Line + 1; Column = 0 } }

  let moveCursor (dir: Direction) (buf: ValidatedBuffer) : ValidatedBuffer =
    let pos = buf.Cursor
    let newPos =
      match dir with
      | Direction.Left ->
        if pos.Column > 0 then { pos with Column = pos.Column - 1 }
        elif pos.Line > 0 then
          let prevLen = buf.Lines.[pos.Line - 1].Length
          { Line = pos.Line - 1; Column = prevLen }
        else pos
      | Direction.Right ->
        let lineLen = buf.Lines.[pos.Line].Length
        if pos.Column < lineLen then { pos with Column = pos.Column + 1 }
        elif pos.Line < buf.Lines.Length - 1 then
          { Line = pos.Line + 1; Column = 0 }
        else pos
      | Direction.Up ->
        if pos.Line > 0 then
          let prevLen = buf.Lines.[pos.Line - 1].Length
          { Line = pos.Line - 1; Column = min pos.Column prevLen }
        else pos
      | Direction.Down ->
        if pos.Line < buf.Lines.Length - 1 then
          let nextLen = buf.Lines.[pos.Line + 1].Length
          { Line = pos.Line + 1; Column = min pos.Column nextLen }
        else pos
    { buf with Cursor = newPos }

/// Side effects described as data — never executed in the pure domain
[<RequireQualifiedAccess>]
type EditorEffect =
  | RequestCompletion of text: string * cursor: int
  | RequestEval of code: string
  | RequestHistory of direction: HistoryDirection
  | RequestSessionList
  | RequestSessionSwitch of sessionId: string
  | RequestSessionCreate of projects: string list
  | RequestSessionStop of sessionId: string
  | RequestReset
  | RequestHardReset

/// The full editor state
type EditorState = {
  Buffer: ValidatedBuffer
  Selection: Selection option
  CompletionMenu: CompletionMenu option
  History: HistoryState
  Mode: EditMode
  SessionPanelVisible: bool
  SelectedSessionIndex: int option
  Prompt: PromptState option
}

module EditorState =
  let initial = {
    Buffer = ValidatedBuffer.empty
    Selection = None
    CompletionMenu = None
    History = { Entries = []; Position = 0; Draft = "" }
    Mode = EditMode.Insert
    SessionPanelVisible = false
    SelectedSessionIndex = None
    Prompt = None
  }

/// Pure state transition: action + state → new state + side effects
module EditorUpdate =
  let private deleteForward (buf: ValidatedBuffer) : ValidatedBuffer =
    let lines = ValidatedBuffer.lines buf
    let pos = ValidatedBuffer.cursor buf
    let line = lines.[pos.Line]
    if pos.Column < line.Length then
      let newLine = line.Remove(pos.Column, 1)
      let newLines = lines |> List.mapi (fun i l -> if i = pos.Line then newLine else l)
      match ValidatedBuffer.create newLines pos with
      | Ok b -> b
      | Error _ -> buf
    elif pos.Line < lines.Length - 1 then
      let nextLine = lines.[pos.Line + 1]
      let joined = line + nextLine
      let newLines =
        lines
        |> List.indexed
        |> List.choose (fun (i, l) ->
          if i = pos.Line then Some joined
          elif i = pos.Line + 1 then None
          else Some l)
      match ValidatedBuffer.create newLines pos with
      | Ok b -> b
      | Error _ -> buf
    else buf

  let private moveToLineStart (buf: ValidatedBuffer) : ValidatedBuffer =
    let pos = ValidatedBuffer.cursor buf
    match ValidatedBuffer.create (ValidatedBuffer.lines buf) { pos with Column = 0 } with
    | Ok b -> b
    | Error _ -> buf

  let private moveToLineEnd (buf: ValidatedBuffer) : ValidatedBuffer =
    let pos = ValidatedBuffer.cursor buf
    let lineLen = (ValidatedBuffer.lines buf).[pos.Line].Length
    match ValidatedBuffer.create (ValidatedBuffer.lines buf) { pos with Column = lineLen } with
    | Ok b -> b
    | Error _ -> buf

  let update (action: EditorAction) (state: EditorState) : EditorState * EditorEffect list =
    match action with
    | EditorAction.InsertChar c ->
      { state with Buffer = ValidatedBuffer.insertChar c state.Buffer }, []
    | EditorAction.DeleteBackward ->
      { state with Buffer = ValidatedBuffer.deleteBackward state.Buffer }, []
    | EditorAction.DeleteForward ->
      { state with Buffer = deleteForward state.Buffer }, []
    | EditorAction.MoveCursor dir ->
      { state with Buffer = ValidatedBuffer.moveCursor dir state.Buffer }, []
    | EditorAction.SetCursorPosition (line, col) ->
      { state with Buffer = ValidatedBuffer.setCursor { Line = line; Column = col } state.Buffer }, []
    | EditorAction.MoveToLineStart ->
      { state with Buffer = moveToLineStart state.Buffer }, []
    | EditorAction.MoveToLineEnd ->
      { state with Buffer = moveToLineEnd state.Buffer }, []
    | EditorAction.NewLine ->
      { state with Buffer = ValidatedBuffer.newLine state.Buffer }, []
    | EditorAction.Submit ->
      let code = ValidatedBuffer.text state.Buffer
      state, [EditorEffect.RequestEval code]
    | EditorAction.Cancel ->
      { state with Buffer = ValidatedBuffer.empty }, []
    | EditorAction.TriggerCompletion ->
      let text = ValidatedBuffer.text state.Buffer
      let col = (ValidatedBuffer.cursor state.Buffer).Column
      state, [EditorEffect.RequestCompletion (text, col)]
    | EditorAction.AcceptCompletion ->
      match state.CompletionMenu with
      | Some menu when menu.SelectedIndex >= 0 && menu.SelectedIndex < menu.Items.Length ->
        let item = menu.Items.[menu.SelectedIndex]
        let buf = state.Buffer
        let pos = ValidatedBuffer.cursor buf
        let line = (ValidatedBuffer.lines buf).[pos.Line]
        let filterLen = menu.FilterText.Length
        let start = max 0 (pos.Column - filterLen)
        let before = line.[..start-1]
        let before' = if start = 0 then "" else before
        let after = if pos.Column < line.Length then line.[pos.Column..] else ""
        let newLine = before' + item.Label + after
        let newCol = (if start = 0 then 0 else before.Length) + item.Label.Length
        let lines = ValidatedBuffer.lines buf
        let newLines = lines |> List.mapi (fun i l -> if i = pos.Line then newLine else l)
        let newBuf =
          match ValidatedBuffer.create newLines { pos with Column = newCol } with
          | Ok b -> b
          | Error _ -> buf
        { state with Buffer = newBuf; CompletionMenu = None }, []
      | _ ->
        { state with CompletionMenu = None }, []
    | EditorAction.DismissCompletion ->
      { state with CompletionMenu = None }, []
    | EditorAction.NextCompletion ->
      match state.CompletionMenu with
      | Some menu ->
        let next = min (menu.SelectedIndex + 1) (menu.Items.Length - 1)
        { state with CompletionMenu = Some { menu with SelectedIndex = next } }, []
      | None -> state, []
    | EditorAction.PreviousCompletion ->
      match state.CompletionMenu with
      | Some menu ->
        let prev = max (menu.SelectedIndex - 1) 0
        { state with CompletionMenu = Some { menu with SelectedIndex = prev } }, []
      | None -> state, []
    | EditorAction.HistoryPrevious ->
      state, [EditorEffect.RequestHistory HistoryDirection.Previous]
    | EditorAction.HistoryNext ->
      state, [EditorEffect.RequestHistory HistoryDirection.Next]
    | EditorAction.HistorySearch _ ->
      state, []
    | EditorAction.SwitchMode mode ->
      { state with Mode = mode }, []
    | EditorAction.SelectAll ->
      let lines = ValidatedBuffer.lines state.Buffer
      let lastLine = lines.Length - 1
      let lastCol = lines.[lastLine].Length
      { state with
          Selection = Some {
            Start = { Line = 0; Column = 0 }
            End = { Line = lastLine; Column = lastCol } } }, []
    | EditorAction.SelectWord ->
      state, []
    | EditorAction.DeleteWord ->
      state, []
    | EditorAction.DeleteToEndOfLine ->
      let pos = ValidatedBuffer.cursor state.Buffer
      let lines = ValidatedBuffer.lines state.Buffer
      let line = lines.[pos.Line]
      let newLine = line.[..pos.Column-1]
      let newLine' = if pos.Column = 0 then "" else newLine
      let newLines = lines |> List.mapi (fun i l -> if i = pos.Line then newLine' else l)
      match ValidatedBuffer.create newLines pos with
      | Ok b -> { state with Buffer = b }, []
      | Error _ -> state, []
    | EditorAction.MoveWordForward ->
      state, []
    | EditorAction.MoveWordBackward ->
      state, []
    | EditorAction.Undo ->
      state, []
    | EditorAction.Redo ->
      state, []
    | EditorAction.ListSessions ->
      state, [EditorEffect.RequestSessionList]
    | EditorAction.SwitchSession id ->
      state, [EditorEffect.RequestSessionSwitch id]
    | EditorAction.CreateSession projects ->
      if projects.IsEmpty && state.Prompt.IsNone then
        // Open prompt to ask for working directory
        { state with
            Prompt = Some {
              Label = "Working directory"
              Input = System.IO.Directory.GetCurrentDirectory()
              Purpose = PromptPurpose.CreateSessionDir } }, []
      else
        state, [EditorEffect.RequestSessionCreate projects]
    | EditorAction.StopSession id ->
      state, [EditorEffect.RequestSessionStop id]
    | EditorAction.ToggleSessionPanel ->
      { state with SessionPanelVisible = not state.SessionPanelVisible }, []
    | EditorAction.ResetSession ->
      state, [EditorEffect.RequestReset]
    | EditorAction.HardResetSession ->
      state, [EditorEffect.RequestHardReset]
    | EditorAction.SessionNavUp ->
      let idx =
        match state.SelectedSessionIndex with
        | None -> 0
        | Some i -> max 0 (i - 1)
      { state with SelectedSessionIndex = Some idx }, []
    | EditorAction.SessionNavDown ->
      let idx =
        match state.SelectedSessionIndex with
        | None -> 0
        | Some i -> i + 1
      { state with SelectedSessionIndex = Some idx }, []
    | EditorAction.SessionSelect ->
      // The actual session ID resolution happens in SageFsApp
      // where the model has access to the session list
      state, []
    | EditorAction.SessionDelete ->
      // Same — resolved in SageFsApp where session list is available
      state, []
    | EditorAction.SessionSetIndex idx ->
      { state with SelectedSessionIndex = Some (max 0 idx) }, []
    | EditorAction.ClearOutput ->
      state, []
    | EditorAction.PromptChar c ->
      match state.Prompt with
      | Some prompt ->
        { state with Prompt = Some { prompt with Input = prompt.Input + string c } }, []
      | None -> state, []
    | EditorAction.PromptBackspace ->
      match state.Prompt with
      | Some prompt when prompt.Input.Length > 0 ->
        { state with Prompt = Some { prompt with Input = prompt.Input.[..prompt.Input.Length - 2] } }, []
      | _ -> state, []
    | EditorAction.PromptConfirm ->
      match state.Prompt with
      | Some prompt ->
        let effects =
          match prompt.Purpose with
          | PromptPurpose.CreateSessionDir ->
            [EditorEffect.RequestSessionCreate [prompt.Input]]
        { state with Prompt = None }, effects
      | None -> state, []
    | EditorAction.PromptCancel ->
      { state with Prompt = None }, []
