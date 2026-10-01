module LemDrive.Tests.SnapshotTests

open Expecto
open Expecto.Flip
open LemDrive.Snapshot

let private tab (label: string) (active: bool) : Tab = { Label = label; Active = active; Unsaved = false }

let private facts : Facts =
  { WindowTitle = "DemoEnv.fs - run"
    Focus = "editor"
    ActivityBar = [ tab "Explorer" false; tab "SageFs" true ]
    Tabs = [ { Label = "DemoEnv.fs"; Active = true; Unsaved = true } ]
    Editor =
      Some
        { Lines = [ { Number = 9; Text = "" }; { Number = 10; Text = "open System" }; { Number = 11; Text = "" } ]
          Notes =
            [ { Line = 10; Kind = CodeLens; Text = "▶ Eval" }
              { Line = 10; Kind = InlineText; Text = "  // → 4.6s" }
              { Line = 11; Kind = GutterMark; Text = "failing test" } ] }
    Quick = None
    SideBar =
      Some
        { Title = "SAGEFS"
          Panes =
            [ { Title = "Sessions"
                Expanded = true
                Body = ""
                Rows = [ { Label = "DemoEnv — Ready"; Level = 1; Expanded = None; Selected = true } ] }
              { Title = "Hot Reload Files"; Expanded = true; Body = "No hot-reloadable files yet."; Rows = [] } ] }
    Panel = None
    StatusBar = [ "SageFs: ready"; "10/11 passed" ]
    Toasts = [ { Severity = "error"; Message = "Build failed"; Buttons = [ "Show Output" ] } ]
    Dialog = None
    Hover = None
    Suggest = [] }

[<Tests>]
let tests =
  testList "the text a model and a reviewer read" [
    testCase "editor lines carry their numbers and what is drawn on them" <| fun _ ->
      let text = render facts
      text |> Expect.stringContains "line number and text" "10 | open System"
      text |> Expect.stringContains "the code lens" "[lens] ▶ Eval"
      text |> Expect.stringContains "the inline text" "[deco] // → 4.6s"
      text |> Expect.stringContains "the gutter mark" "[gutter] failing test"

    testCase "an unsaved tab says so" <| fun _ ->
      render facts |> Expect.stringContains "unsaved" "[*] DemoEnv.fs (unsaved)"

    testCase "the side bar views show their rows and their text" <| fun _ ->
      let text = render facts
      text |> Expect.stringContains "a row" "DemoEnv — Ready"
      text |> Expect.stringContains "a view's own text" "No hot-reloadable files yet."

    testCase "the brief has focus, notifications and the status bar but no editor" <| fun _ ->
      let brief = renderBrief facts
      brief |> Expect.stringContains "focus" "focus: editor"
      brief |> Expect.stringContains "toast with its button" "[error] Build failed  buttons: [Show Output]"
      brief |> Expect.stringContains "status bar" "10/11 passed"
      Expect.isFalse "no editor lines" (brief.Contains "open System")

    testCase "a long row is cut, not allowed to eat the context" <| fun _ ->
      let long = { facts with Editor = Some { Lines = [ { Number = 1; Text = String.replicate 500 "x" } ]; Notes = [] } }
      Expect.isLessThan "bounded" ((render long).Length, 3000)

    testCase "the sidecar leads with the state a reviewer cross-checks the image against" <| fun _ ->
      let text =
        renderSidecar
          { Name = "003-lt-failing"
            Regions = [ "sidebar" ]
            Width = 1280
            Height = 800
            TakenAt = "2026-10-01T00:00:00Z"
            Activation = "activated 412 ms after the host"
            Facts = facts }
      text |> Expect.stringContains "name" "# shot 003-lt-failing"
      text |> Expect.stringContains "size and crops" "window: 1280x800 (full window, plus sidebar)"
      text |> Expect.stringContains "activation" "activation: activated 412 ms after the host"
      text |> Expect.stringContains "status bar item" "  - 10/11 passed"
      text |> Expect.stringContains "a view with its row count" "[Sessions] expanded, 1 row(s)"
      text |> Expect.stringContains "notifications" "[error] Build failed"
      text |> Expect.stringContains "the text snapshot follows" "## text snapshot"

    testCase "a closed side bar is said, not left blank" <| fun _ ->
      let closed = { facts with SideBar = None }
      let text = renderSidecar { Name = "n"; Regions = []; Width = 1; Height = 1; TakenAt = "t"; Activation = "a"; Facts = closed }
      text |> Expect.stringContains "says so" "the side bar is closed in this shot"
  ]
