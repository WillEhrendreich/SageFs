module SageFs.Vscode.TestDecorations

open Fable.Core
open Fable.Core.JsInterop
open Vscode

open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.TestDecorationsPure

// ── Decoration types ────────────────────────────────────────────

let mutable passedType: TextEditorDecorationType option = None
let mutable failedType: TextEditorDecorationType option = None
let mutable runningType: TextEditorDecorationType option = None
let mutable coveredPassingType: TextEditorDecorationType option = None
let mutable coveredFailingType: TextEditorDecorationType option = None
let mutable notCoveredType: TextEditorDecorationType option = None
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
  coveredPassingType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "isWholeLine" ==> false
        "before" ==> createObj [
          "contentText" ==> "▸"
          "color" ==> newThemeColor "testing.iconPassed"
          "margin" ==> "0 0.5em 0 0"
        ]
      ]))
  coveredFailingType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "isWholeLine" ==> false
        "before" ==> createObj [
          "contentText" ==> "▸"
          "color" ==> newThemeColor "testing.iconFailed"
          "margin" ==> "0 0.5em 0 0"
        ]
      ]))
  notCoveredType <- Some (
    Window.createTextEditorDecorationType (
      createObj [
        "isWholeLine" ==> false
        "before" ==> createObj [
          "contentText" ==> "○"
          "color" ==> newThemeColor "disabledForeground"
          "margin" ==> "0 0.5em 0 0"
        ]
      ]))

// ── Decoration application ──────────────────────────────────────

/// Build VS Code decoration options from a decoration already computed by
/// `TestDecorationsPure` — the only place a zero-based `Range` is built.
let private toRangeOption (zeroBasedLine: int) (hoverText: string) : obj =
  let range = newRange zeroBasedLine 0 zeroBasedLine 0
  createObj [
    "range" ==> range
    "hoverMessage" ==> hoverText
  ]

let private toRanges (entries: DecorationEntry list) : ResizeArray<obj> =
  ResizeArray<obj>(entries |> List.map (fun e -> toRangeOption e.ZeroBasedLine e.HoverText))

let private toCoverageRanges (entries: CoverageEntry list) : ResizeArray<obj> =
  ResizeArray<obj>(entries |> List.map (fun e -> toRangeOption e.ZeroBasedLine e.HoverText))

/// Apply test decorations to a single text editor based on current test state
let applyToEditor (state: VscLiveTestState) (editor: TextEditor) =
  let filePath = editor.document.fileName
  let decorations = decorationsForFile state filePath

  passedType |> Option.iter (fun dt -> editor.setDecorations(dt, toRanges decorations.Passed))
  failedType |> Option.iter (fun dt -> editor.setDecorations(dt, toRanges decorations.Failed))
  runningType |> Option.iter (fun dt -> editor.setDecorations(dt, toRanges decorations.Running))

/// Apply coverage decorations to a single text editor
let applyCoverageToEditor (state: VscLiveTestState) (editor: TextEditor) =
  let filePath = editor.document.fileName
  let decorations = coverageDecorationsForFile state filePath

  coveredPassingType |> Option.iter (fun dt -> editor.setDecorations(dt, toCoverageRanges decorations.Passing))
  coveredFailingType |> Option.iter (fun dt -> editor.setDecorations(dt, toCoverageRanges decorations.Failing))
  notCoveredType |> Option.iter (fun dt -> editor.setDecorations(dt, toCoverageRanges decorations.NotCovered))

/// Apply coverage decorations to all visible editors
let applyCoverageToAllEditors (state: VscLiveTestState) =
  let editors = Window.getVisibleTextEditors ()
  for editor in editors do
    applyCoverageToEditor state editor

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
        let zeroBasedLine = toZeroBasedLine line
        let range = newRange zeroBasedLine 0 zeroBasedLine 100
        let diagnostic = newDiagnostic range (sprintf "%s: %s" testName msg) VDiagnosticSeverity.Error
        diagnostics.Add diagnostic
      dc.set(uri, diagnostics)

// ── Lifecycle ───────────────────────────────────────────────────

let dispose () =
  passedType |> Option.iter (fun dt -> dt.dispose ())
  failedType |> Option.iter (fun dt -> dt.dispose ())
  runningType |> Option.iter (fun dt -> dt.dispose ())
  coveredPassingType |> Option.iter (fun dt -> dt.dispose ())
  coveredFailingType |> Option.iter (fun dt -> dt.dispose ())
  notCoveredType |> Option.iter (fun dt -> dt.dispose ())
  diagnosticCollection |> Option.iter (fun dc -> dc.dispose ())
  passedType <- None
  failedType <- None
  runningType <- None
  coveredPassingType <- None
  coveredFailingType <- None
  notCoveredType <- None
  diagnosticCollection <- None
