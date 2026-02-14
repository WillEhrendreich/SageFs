module SageFs.Tests.EditorTests

open Expecto
open Expecto.Flip
open SageFs

let private initial = EditorState.initial

let private applyActions actions =
  actions |> List.fold (fun (s, _) a -> EditorUpdate.update a s) (initial, [])

let private bufferText state =
  ValidatedBuffer.text state.Buffer

let private cursorPos state =
  ValidatedBuffer.cursor state.Buffer

[<Tests>]
let validatedBufferTests = testList "ValidatedBuffer" [
  testCase "empty buffer has one empty line" <| fun _ ->
    ValidatedBuffer.empty
    |> ValidatedBuffer.lines
    |> Expect.equal "should have one empty line" [""]

  testCase "empty buffer cursor at 0,0" <| fun _ ->
    ValidatedBuffer.empty
    |> ValidatedBuffer.cursor
    |> Expect.equal "cursor at origin" { Line = 0; Column = 0 }

  testCase "create with valid cursor succeeds" <| fun _ ->
    ValidatedBuffer.create ["hello"] { Line = 0; Column = 3 }
    |> Result.isOk
    |> Expect.isTrue "should succeed"

  testCase "create with empty lines fails" <| fun _ ->
    ValidatedBuffer.create [] { Line = 0; Column = 0 }
    |> Expect.equal "should fail" (Error BufferError.EmptyLines)

  testCase "create with out-of-bounds cursor fails" <| fun _ ->
    ValidatedBuffer.create ["hi"] { Line = 0; Column = 10 }
    |> Result.isError
    |> Expect.isTrue "should fail with out of bounds"

  testCase "insertChar adds character at cursor" <| fun _ ->
    ValidatedBuffer.empty
    |> ValidatedBuffer.insertChar 'a'
    |> ValidatedBuffer.text
    |> Expect.equal "should have 'a'" "a"

  testCase "insertChar advances cursor" <| fun _ ->
    ValidatedBuffer.empty
    |> ValidatedBuffer.insertChar 'a'
    |> ValidatedBuffer.cursor
    |> Expect.equal "cursor at col 1" { Line = 0; Column = 1 }

  testCase "deleteBackward removes character before cursor" <| fun _ ->
    ValidatedBuffer.empty
    |> ValidatedBuffer.insertChar 'a'
    |> ValidatedBuffer.insertChar 'b'
    |> ValidatedBuffer.deleteBackward
    |> ValidatedBuffer.text
    |> Expect.equal "should have 'a'" "a"

  testCase "deleteBackward at start of line joins with previous" <| fun _ ->
    ValidatedBuffer.empty
    |> ValidatedBuffer.insertChar 'a'
    |> ValidatedBuffer.newLine
    |> ValidatedBuffer.insertChar 'b'
    |> ValidatedBuffer.moveCursor Direction.Left
    |> ValidatedBuffer.deleteBackward
    |> ValidatedBuffer.text
    |> Expect.equal "should join lines" "ab"

  testCase "newLine splits current line" <| fun _ ->
    ValidatedBuffer.empty
    |> ValidatedBuffer.insertChar 'a'
    |> ValidatedBuffer.insertChar 'b'
    |> ValidatedBuffer.moveCursor Direction.Left
    |> ValidatedBuffer.newLine
    |> ValidatedBuffer.lines
    |> Expect.equal "should be two lines" ["a"; "b"]

  testCase "moveCursor left wraps to previous line" <| fun _ ->
    let buf =
      ValidatedBuffer.empty
      |> ValidatedBuffer.insertChar 'a'
      |> ValidatedBuffer.newLine
    buf
    |> ValidatedBuffer.moveCursor Direction.Left
    |> ValidatedBuffer.cursor
    |> Expect.equal "should wrap to end of line 0" { Line = 0; Column = 1 }

  testCase "moveCursor right wraps to next line" <| fun _ ->
    let buf =
      ValidatedBuffer.empty
      |> ValidatedBuffer.insertChar 'a'
      |> ValidatedBuffer.newLine
      |> ValidatedBuffer.insertChar 'b'
    // Move to end of first line, then right should wrap
    let atEndOfLine0 =
      match ValidatedBuffer.create ["a"; "b"] { Line = 0; Column = 1 } with
      | Ok b -> b
      | Error _ -> failwith "unreachable"
    atEndOfLine0
    |> ValidatedBuffer.moveCursor Direction.Right
    |> ValidatedBuffer.cursor
    |> Expect.equal "should wrap to start of line 1" { Line = 1; Column = 0 }
]

[<Tests>]
let editorUpdateTests = testList "EditorUpdate" [
  testCase "InsertChar adds to buffer" <| fun _ ->
    let s, effs = EditorUpdate.update (EditorAction.InsertChar 'x') initial
    bufferText s |> Expect.equal "buffer has x" "x"
    effs |> Expect.equal "no effects" []

  testCase "multiple InsertChar builds text" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'h'
      EditorAction.InsertChar 'i'
    ]
    bufferText s |> Expect.equal "buffer has hi" "hi"

  testCase "DeleteBackward removes last char" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'a'
      EditorAction.InsertChar 'b'
      EditorAction.DeleteBackward
    ]
    bufferText s |> Expect.equal "buffer has a" "a"

  testCase "DeleteForward removes char at cursor" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'a'
      EditorAction.InsertChar 'b'
      EditorAction.MoveCursor Direction.Left
      EditorAction.DeleteForward
    ]
    bufferText s |> Expect.equal "buffer has a" "a"

  testCase "Submit emits RequestEval" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'h'
      EditorAction.InsertChar 'i'
    ]
    let _, effs = EditorUpdate.update EditorAction.Submit s
    effs |> Expect.equal "should request eval" [EditorEffect.RequestEval "hi"]

  testCase "Cancel clears buffer" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'x'
      EditorAction.Cancel
    ]
    bufferText s |> Expect.equal "buffer empty" ""

  testCase "NewLine creates multiline" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'a'
      EditorAction.NewLine
      EditorAction.InsertChar 'b'
    ]
    bufferText s |> Expect.equal "two lines" "a\nb"

  testCase "TriggerCompletion emits RequestCompletion" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'x'
    ]
    let _, effs = EditorUpdate.update EditorAction.TriggerCompletion s
    effs |> Expect.equal "should request completion" [EditorEffect.RequestCompletion ("x", 1)]

  testCase "DismissCompletion clears menu" <| fun _ ->
    let withMenu = {
      initial with
        CompletionMenu = Some {
          Items = [{ Label = "test"; Kind = "value"; Detail = None }]
          SelectedIndex = 0
          FilterText = "t"
        }
    }
    let s, _ = EditorUpdate.update EditorAction.DismissCompletion withMenu
    s.CompletionMenu |> Expect.isNone "menu should be cleared"

  testCase "NextCompletion advances index" <| fun _ ->
    let items = [
      { Label = "a"; Kind = "v"; Detail = None }
      { Label = "b"; Kind = "v"; Detail = None }
    ]
    let withMenu = {
      initial with
        CompletionMenu = Some { Items = items; SelectedIndex = 0; FilterText = "" }
    }
    let s, _ = EditorUpdate.update EditorAction.NextCompletion withMenu
    s.CompletionMenu
    |> Option.map (fun m -> m.SelectedIndex)
    |> Expect.equal "should advance" (Some 1)

  testCase "PreviousCompletion decrements index" <| fun _ ->
    let items = [
      { Label = "a"; Kind = "v"; Detail = None }
      { Label = "b"; Kind = "v"; Detail = None }
    ]
    let withMenu = {
      initial with
        CompletionMenu = Some { Items = items; SelectedIndex = 1; FilterText = "" }
    }
    let s, _ = EditorUpdate.update EditorAction.PreviousCompletion withMenu
    s.CompletionMenu
    |> Option.map (fun m -> m.SelectedIndex)
    |> Expect.equal "should decrement" (Some 0)

  testCase "NextCompletion clamps at end" <| fun _ ->
    let items = [{ Label = "only"; Kind = "v"; Detail = None }]
    let withMenu = {
      initial with
        CompletionMenu = Some { Items = items; SelectedIndex = 0; FilterText = "" }
    }
    let s, _ = EditorUpdate.update EditorAction.NextCompletion withMenu
    s.CompletionMenu
    |> Option.map (fun m -> m.SelectedIndex)
    |> Expect.equal "should stay at 0" (Some 0)

  testCase "SwitchMode changes mode" <| fun _ ->
    let s, _ = EditorUpdate.update (EditorAction.SwitchMode EditMode.Normal) initial
    s.Mode |> Expect.equal "should be Normal" EditMode.Normal

  testCase "ToggleSessionPanel toggles visibility" <| fun _ ->
    let s1, _ = EditorUpdate.update EditorAction.ToggleSessionPanel initial
    s1.SessionPanelVisible |> Expect.isTrue "should be visible"
    let s2, _ = EditorUpdate.update EditorAction.ToggleSessionPanel s1
    s2.SessionPanelVisible |> Expect.isFalse "should be hidden"

  testCase "SelectAll creates full selection" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'a'
      EditorAction.NewLine
      EditorAction.InsertChar 'b'
      EditorAction.SelectAll
    ]
    s.Selection
    |> Expect.isSome "should have selection"

  testCase "HistoryPrevious emits RequestHistory" <| fun _ ->
    let _, effs = EditorUpdate.update EditorAction.HistoryPrevious initial
    effs |> Expect.equal "should request history" [EditorEffect.RequestHistory HistoryDirection.Previous]

  testCase "HistoryNext emits RequestHistory" <| fun _ ->
    let _, effs = EditorUpdate.update EditorAction.HistoryNext initial
    effs |> Expect.equal "should request history" [EditorEffect.RequestHistory HistoryDirection.Next]

  testCase "ListSessions emits effect" <| fun _ ->
    let _, effs = EditorUpdate.update EditorAction.ListSessions initial
    effs |> Expect.equal "should request session list" [EditorEffect.RequestSessionList]

  testCase "SwitchSession emits effect" <| fun _ ->
    let _, effs = EditorUpdate.update (EditorAction.SwitchSession "s1") initial
    effs |> Expect.equal "should request switch" [EditorEffect.RequestSessionSwitch "s1"]

  testCase "StopSession emits effect" <| fun _ ->
    let _, effs = EditorUpdate.update (EditorAction.StopSession "s1") initial
    effs |> Expect.equal "should request stop" [EditorEffect.RequestSessionStop "s1"]

  testCase "DeleteToEndOfLine truncates" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'a'
      EditorAction.InsertChar 'b'
      EditorAction.InsertChar 'c'
      EditorAction.MoveCursor Direction.Left
      EditorAction.MoveCursor Direction.Left
      EditorAction.DeleteToEndOfLine
    ]
    bufferText s |> Expect.equal "should keep only a" "a"

  testCase "MoveToLineStart moves cursor to column 0" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'a'
      EditorAction.InsertChar 'b'
      EditorAction.MoveToLineStart
    ]
    cursorPos s |> Expect.equal "cursor at start" { Line = 0; Column = 0 }

  testCase "MoveToLineEnd moves cursor to end" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'a'
      EditorAction.InsertChar 'b'
      EditorAction.MoveToLineStart
      EditorAction.MoveToLineEnd
    ]
    cursorPos s |> Expect.equal "cursor at end" { Line = 0; Column = 2 }

  testCase "AcceptCompletion inserts selected item" <| fun _ ->
    let afterType, _ = applyActions [
      EditorAction.InsertChar 't'
      EditorAction.InsertChar 'e'
    ]
    let withMenu = {
      afterType with
        CompletionMenu = Some {
          Items = [{ Label = "test"; Kind = "value"; Detail = None }]
          SelectedIndex = 0
          FilterText = "te"
        }
    }
    let s, _ = EditorUpdate.update EditorAction.AcceptCompletion withMenu
    bufferText s |> Expect.equal "should complete" "test"
    s.CompletionMenu |> Expect.isNone "menu should close"

  testCase "MoveCursor up/down navigates lines" <| fun _ ->
    let s, _ = applyActions [
      EditorAction.InsertChar 'a'
      EditorAction.NewLine
      EditorAction.InsertChar 'b'
      EditorAction.MoveCursor Direction.Up
    ]
    cursorPos s |> Expect.equal "should be on line 0" { Line = 0; Column = 1 }
]

[<Tests>]
let elmLoopTests = testList "ElmLoop" [
  testCase "start renders initial model" <| fun _ ->
    let mutable rendered = []
    let program : ElmProgram<int, int, string, string> = {
      Update = fun msg model -> model + msg, []
      Render = fun model -> [sprintf "%d" model]
      ExecuteEffect = fun _ _ -> async { () }
      OnModelChanged = fun _ regions -> rendered <- regions
    }
    let _ = ElmLoop.start program 42
    rendered |> Expect.equal "should render initial" ["42"]

  testCase "dispatch updates model and re-renders" <| fun _ ->
    let mutable rendered = []
    let program : ElmProgram<int, int, string, string> = {
      Update = fun msg model -> model + msg, []
      Render = fun model -> [sprintf "%d" model]
      ExecuteEffect = fun _ _ -> async { () }
      OnModelChanged = fun _ regions -> rendered <- regions
    }
    let dispatch = ElmLoop.start program 0
    dispatch 5
    rendered |> Expect.equal "should render 5" ["5"]
    dispatch 3
    rendered |> Expect.equal "should render 8" ["8"]
]

[<Tests>]
let renderPipelineTests = testList "RenderPipeline" [
  testCase "RegionFlags can be combined" <| fun _ ->
    let flags = RegionFlags.Clickable ||| RegionFlags.Focusable
    flags.HasFlag RegionFlags.Clickable |> Expect.isTrue "should have Clickable"
    flags.HasFlag RegionFlags.Focusable |> Expect.isTrue "should have Focusable"
    flags.HasFlag RegionFlags.Scrollable |> Expect.isFalse "should not have Scrollable"

  testCase "KeyMap.hintFor finds matching action" <| fun _ ->
    let combo = { Key = System.ConsoleKey.Tab; Modifiers = System.ConsoleModifiers.Control; Char = None }
    let keyMap = Map.ofList [(combo, EditorAction.TriggerCompletion)]
    KeyMap.hintFor keyMap EditorAction.TriggerCompletion
    |> Expect.equal "should find hint" (Some combo)

  testCase "KeyMap.hintFor returns None for missing action" <| fun _ ->
    let keyMap : KeyMap = Map.empty
    KeyMap.hintFor keyMap EditorAction.Submit
    |> Expect.isNone "should be None"
]
