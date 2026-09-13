// WHY — pins the pure geometry math behind `sagefs.debug.rectFor`
// (demo-actors-plan.md §2.1): the VS Code demo actor's ONLY way to resolve a
// click target without CDP is this module's `resolve`, fed real window/
// chrome/caret facts by `Extension.fs`. RED before `DebugRects.fs` existed
// (the command itself did not exist in the extension — confirmed by
// grepping `sagefs-vscode/src/` for `rectFor`/`debug.` before this file was
// written); GREEN once the module's geometry carve-outs are correct.
//
// Runs under plain `dotnet fsi` (no Fable, no VS Code, no Node) — mirrors
// AppRunContractTests.fsx/DaemonDiscoveryContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/DebugRects.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.DebugRects

let private window: WindowRect = { X = 0.0; Y = 0.0; W = 1280.0; H = 720.0 }

let private chromeFull: ChromeConfig =
  { ActivityBarVisible = true
    SideBarVisible = true
    SideBarOnLeft = true
    SideBarWidth = 300.0
    StatusBarVisible = true }

let private chromeBare: ChromeConfig =
  { ActivityBarVisible = false
    SideBarVisible = false
    SideBarOnLeft = true
    SideBarWidth = 300.0
    StatusBarVisible = false }

let tests =
  testList "VS Code sagefs.debug.rectFor contract - pure geometry" [

    testCase "WHY - target vocabulary - parses the four documented target forms" <| fun _ ->
      RectTarget.parse "caret" |> Expect.equal "caret" (Some RectTarget.Caret)
      RectTarget.parse "editor" |> Expect.equal "editor" (Some RectTarget.Editor)
      RectTarget.parse "statusBar" |> Expect.equal "statusBar" (Some RectTarget.StatusBar)
      RectTarget.parse "view:hotReload" |> Expect.equal "view" (Some(RectTarget.View "hotReload"))

    testCase "WHY - target vocabulary - an unrecognized string is a real absence, not a fallback guess" <| fun _ ->
      RectTarget.parse "" |> Expect.equal "empty" None
      RectTarget.parse "banana" |> Expect.equal "unknown" None

    testCase "WHY - editor rect - a bare window (no chrome) resolves to the whole window minus title+tab" <| fun _ ->
      let rect = resolve window chromeBare None RectTarget.Editor
      rect
      |> Expect.equal
        "bare editor rect"
        (Some
          { X = 0.0
            Y = TitleBarHeight + TabRowHeight
            W = 1280.0
            H = 720.0 - (TitleBarHeight + TabRowHeight) })

    testCase "WHY - editor rect - full chrome (activity bar + left sidebar + status bar) is carved out of every edge it occupies" <| fun _ ->
      match resolve window chromeFull None RectTarget.Editor with
      | None -> failwith "expected Some rect"
      | Some rect ->
        rect.X |> Expect.equal "left inset" (ActivityBarWidth + chromeFull.SideBarWidth)
        rect.Y |> Expect.equal "top inset" (TitleBarHeight + TabRowHeight)
        rect.W |> Expect.equal "width shrunk by left chrome only" (1280.0 - ActivityBarWidth - chromeFull.SideBarWidth)
        rect.H |> Expect.equal "height shrunk by top+bottom chrome" (720.0 - (TitleBarHeight + TabRowHeight) - StatusBarHeight)

    testCase "WHY - editor rect - never returns a negative or NaN-width rect for a pathologically small window" <| fun _ ->
      let tiny: WindowRect = { X = 0.0; Y = 0.0; W = 10.0; H = 10.0 }
      match resolve tiny chromeFull None RectTarget.Editor with
      | None -> failwith "expected Some rect"
      | Some rect ->
        Expect.isTrue "width floors at zero" (rect.W >= 0.0)
        Expect.isTrue "height floors at zero" (rect.H >= 0.0)

    testCase "WHY - status bar - hidden means a real None, not a zero-height rect pretending to exist" <| fun _ ->
      resolve window chromeBare None RectTarget.StatusBar
      |> Expect.equal "hidden status bar" None

    testCase "WHY - status bar - visible resolves to the bottom strip at the documented height" <| fun _ ->
      resolve window chromeFull None RectTarget.StatusBar
      |> Expect.equal
        "status bar rect"
        (Some
          { X = 0.0
            Y = 720.0 - StatusBarHeight
            W = 1280.0
            H = StatusBarHeight })

    testCase "WHY - caret - with no active editor there is nothing to resolve" <| fun _ ->
      resolve window chromeFull None RectTarget.Caret
      |> Expect.equal "no active editor" None

    testCase "WHY - caret - a real line/character and font size project into the editor content area" <| fun _ ->
      let caret: CaretConfig = { Line = 2.0; Character = 5.0; FontSize = 14.0; LineHeight = 0.0 }
      let editorRect = (resolve window chromeBare None RectTarget.Editor).Value

      match resolve window chromeBare (Some caret) RectTarget.Caret with
      | None -> failwith "expected Some rect"
      | Some rect ->
        Expect.isTrue "caret sits inside the editor content area" (rect.X >= editorRect.X)
        Expect.isTrue "caret sits inside the editor content area" (rect.Y >= editorRect.Y)
        Expect.isTrue "caret has non-zero width" (rect.W > 0.0)
        Expect.isTrue "caret has non-zero height" (rect.H > 0.0)

    testCase "WHY - caret - an explicit editor.lineHeight setting overrides the derived default" <| fun _ ->
      let derived: CaretConfig = { Line = 1.0; Character = 0.0; FontSize = 14.0; LineHeight = 0.0 }
      let pinned: CaretConfig = { derived with LineHeight = 40.0 }
      let editorRect = (resolve window chromeBare None RectTarget.Editor).Value

      let derivedRect = (resolve window chromeBare (Some derived) RectTarget.Caret).Value
      let pinnedRect = (resolve window chromeBare (Some pinned) RectTarget.Caret).Value

      (pinnedRect.Y - editorRect.Y)
      |> Expect.equal "pinned line height wins over the derived default" (1.0 * 40.0)

      (derivedRect.Y - editorRect.Y)
      |> Expect.notEqual "derived default differs from the pinned value" (1.0 * 40.0)

    testCase "WHY - sidebar view - visible on the left resolves next to the activity bar" <| fun _ ->
      match resolve window chromeFull None (RectTarget.View "sidebar") with
      | None -> failwith "expected Some rect"
      | Some rect -> rect.X |> Expect.equal "sidebar starts right after the activity bar" ActivityBarWidth

    testCase "WHY - sidebar view - hidden means a real None" <| fun _ ->
      resolve window chromeBare None (RectTarget.View "sidebar")
      |> Expect.equal "hidden sidebar" None

    testCase "WHY - unknown named view - honest absence, never a fabricated rect" <| fun _ ->
      resolve window chromeFull None (RectTarget.View "hotReload")
      |> Expect.equal "unresolvable named view" None
  ]

let _ = Expecto.Tests.runTestsWithCLIArgs [] [||] tests
