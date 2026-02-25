module SageFs.Vscode.TestDecorations

open Fable.Core
open Fable.Core.JsInterop
open Vscode

open SageFs.Vscode.LiveTestingTypes

// ── Decoration types ────────────────────────────────────────────

let mutable passedType: TextEditorDecorationType option = None
let mutable failedType: TextEditorDecorationType option = None
let mutable runningType: TextEditorDecorationType option = None
let mutable diagnosticCollection: DiagnosticCollection option = None

let initialize () =
  passedType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "gutterIconPath" ==> ""
        "isWholeLine" ==> true
        "after" ==> createObj [
          "contentText" ==> " ✓"
          "color" ==> newThemeColor "testing.iconPassed"
          "fontStyle" ==> "italic"
          "margin" ==> "0 0 0 1em"
        ]
        "overviewRulerColor" ==> newThemeColor "testing.iconPassed"
        "overviewRulerLane" ==> 1
      ]))
  failedType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "isWholeLine" ==> true
        "after" ==> createObj [
          "contentText" ==> " ✗"
          "color" ==> newThemeColor "testing.iconFailed"
          "fontStyle" ==> "italic"
          "margin" ==> "0 0 0 1em"
        ]
        "overviewRulerColor" ==> newThemeColor "testing.iconFailed"
        "overviewRulerLane" ==> 1
        "backgroundColor" ==> "rgba(244, 71, 71, 0.08)"
      ]))
  runningType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "isWholeLine" ==> true
        "after" ==> createObj [
          "contentText" ==> " ●"
          "color" ==> newThemeColor "testing.iconQueued"
          "fontStyle" ==> "italic"
          "margin" ==> "0 0 0 1em"
        ]
      ]))
  diagnosticCollection <-
    Some (Languages.createDiagnosticCollection "sagefs-tests")

// ── Decoration application ──────────────────────────────────────

/// Build decoration options for a test at a specific line with hover message
let decorationRange (line: int) (hoverText: string) : obj =
  let range = newRange (line - 1) 0 (line - 1) 0
  createObj [
    "range" ==> range
    "hoverMessage" ==> hoverText
  ]

/// Apply test decorations to a single text editor based on current test state
let applyToEditor (state: VscLiveTestState) (editor: TextEditor) =
  let filePath = editor.document.fileName
  let testsInFile = VscLiveTestState.testsForFile filePath state

  let mutable passedRanges = ResizeArray<obj>()
  let mutable failedRanges = ResizeArray<obj>()
  let mutable runningRanges = ResizeArray<obj>()

  for test in testsInFile do
    match test.Line with
    | Some line ->
      let result = VscLiveTestState.resultFor test.Id state
      match result with
      | Some r ->
        match r.Outcome with
        | VscTestOutcome.Passed ->
          let durationText =
            match r.DurationMs with
            | Some ms -> sprintf "✓ %s (%.0fms)" test.DisplayName ms
            | None -> sprintf "✓ %s" test.DisplayName
          passedRanges.Add(decorationRange line durationText)
        | VscTestOutcome.Failed msg ->
          let text = sprintf "✗ %s: %s" test.DisplayName msg
          failedRanges.Add(decorationRange line text)
        | VscTestOutcome.Errored msg ->
          let text = sprintf "✗ %s: %s" test.DisplayName msg
          failedRanges.Add(decorationRange line text)
        | VscTestOutcome.Running ->
          runningRanges.Add(decorationRange line (sprintf "● Running: %s" test.DisplayName))
        | VscTestOutcome.Skipped reason ->
          passedRanges.Add(decorationRange line (sprintf "⊘ Skipped: %s — %s" test.DisplayName reason))
      | None ->
        runningRanges.Add(decorationRange line (sprintf "◆ %s (not yet run)" test.DisplayName))
    | None -> ()

  passedType |> Option.iter (fun dt -> editor.setDecorations(dt, passedRanges))
  failedType |> Option.iter (fun dt -> editor.setDecorations(dt, failedRanges))
  runningType |> Option.iter (fun dt -> editor.setDecorations(dt, runningRanges))

/// Apply decorations to all visible editors
let applyToAllEditors (state: VscLiveTestState) =
  let editors = Window.getVisibleTextEditors ()
  for editor in editors do
    applyToEditor state editor

// ── Failure diagnostics ─────────────────────────────────────────

/// Update the diagnostics collection with test failures
let updateDiagnostics (state: VscLiveTestState) =
  match diagnosticCollection with
  | None -> ()
  | Some dc ->
    dc.clear ()
    // Group failed tests by file
    let failedByFile =
      state.Results
      |> Map.toList
      |> List.choose (fun (id, result) ->
        match result.Outcome with
        | VscTestOutcome.Failed msg | VscTestOutcome.Errored msg ->
          let testInfo = Map.tryFind id state.Tests
          match testInfo with
          | Some info ->
            match info.FilePath, info.Line with
            | Some fp, Some line -> Some (fp, line, info.DisplayName, msg)
            | _ -> None
          | None -> None
        | _ -> None)
      |> List.groupBy (fun (fp, _, _, _) -> fp)

    for (filePath, failures) in failedByFile do
      let uri = uriFile filePath
      let diagnostics = ResizeArray<Diagnostic>()
      for (_, line, testName, msg) in failures do
        let range = newRange (line - 1) 0 (line - 1) 100
        let diagnostic = newDiagnostic range (sprintf "%s: %s" testName msg) VDiagnosticSeverity.Error
        diagnostics.Add diagnostic
      dc.set(uri, diagnostics)

// ── Lifecycle ───────────────────────────────────────────────────

let dispose () =
  passedType |> Option.iter (fun dt -> dt.dispose ())
  failedType |> Option.iter (fun dt -> dt.dispose ())
  runningType |> Option.iter (fun dt -> dt.dispose ())
  diagnosticCollection |> Option.iter (fun dc -> dc.dispose ())
  passedType <- None
  failedType <- None
  runningType <- None
  diagnosticCollection <- None
