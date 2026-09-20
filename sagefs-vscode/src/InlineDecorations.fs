module SageFs.Vscode.InlineDecorations

open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.InlineDecorationsPure

// ── Configuration ──────────────────────────────────────────────

let getInlineTimeout () =
  let config = Workspace.getConfiguration "sagefs"
  config.get("inlineResultTimeout", 30000)

// ── Mutable state ──────────────────────────────────────────────

let mutable blockDecorations: Map<int, TextEditorDecorationType> = Map.empty
let mutable staleDecorations: Map<int, TextEditorDecorationType> = Map.empty
let mutable private evalInProgressDecorations: Map<int, TextEditorDecorationType> = Map.empty
let mutable private bindingValueDecorationType: TextEditorDecorationType option = None

/// Clear all persistent binding-value ghost text decorations.
let clearBindingValueDecorations () =
  bindingValueDecorationType |> Option.iter (fun d -> d.dispose () |> ignore)
  bindingValueDecorationType <- None

// ── Cell highlight ─────────────────────────────────────────────

let mutable private cellHighlightDeco: TextEditorDecorationType option = None

let private cellBorderDeco =
  Window.createTextEditorDecorationType (createObj [
    "borderWidth" ==> "1px 0 0 0"
    "borderStyle" ==> "solid"
    "borderColor" ==> newThemeColor "sagefs.cellBorderColor"
    "isWholeLine" ==> true
  ])

/// Update the cell highlight to show the block the cursor is in.
/// Call on cursor change. startLine/endLine are the block bounds.
let updateCellHighlight (editor: TextEditor) (startLine: int) (endLine: int) =
  let config = Workspace.getConfiguration "sagefs"
  let enabled = config.get("cellHighlight", true)
  match enabled with
  | false ->
    cellHighlightDeco |> Option.iter (fun d -> d.dispose () |> ignore)
    cellHighlightDeco <- None
    editor.setDecorations(cellBorderDeco, ResizeArray<obj>())
  | true ->
    // Background highlight for entire cell
    cellHighlightDeco |> Option.iter (fun d -> d.dispose () |> ignore)
    let deco = Window.createTextEditorDecorationType (createObj [
      "backgroundColor" ==> newThemeColor "sagefs.cellHighlightBackground"
      "isWholeLine" ==> true
    ])
    let ranges = ResizeArray<obj>()
    for i in startLine .. endLine do
      ranges.Add(box (newRange i 0 i 0))
    editor.setDecorations(deco, ranges)
    cellHighlightDeco <- Some deco
    // Top border on first line of block
    editor.setDecorations(cellBorderDeco, ResizeArray [| box (newRange startLine 0 startLine 0) |])

let clearCellHighlight () =
  cellHighlightDeco |> Option.iter (fun d -> d.dispose () |> ignore)
  cellHighlightDeco <- None

// ── Helpers ────────────────────────────────────────────────────

/// `123ms` / `1.2s` — the decision itself lives in `InlineDecorationsPure`,
/// tested there; this is the same function, re-exported so existing callers
/// (`Extension.fs`) do not need to change.
let formatDuration (ms: float) = InlineDecorationsPure.formatDuration ms

// ── Core functions ─────────────────────────────────────────────

let clearBlockDecoration (line: int) =
  match Map.tryFind line blockDecorations with
  | Some deco ->
    deco.dispose () |> ignore
    blockDecorations <- Map.remove line blockDecorations
  | None -> ()
  match Map.tryFind line staleDecorations with
  | Some deco ->
    deco.dispose () |> ignore
    staleDecorations <- Map.remove line staleDecorations
  | None -> ()

let autoClearAfter (line: int) =
  let ms = getInlineTimeout ()
  match ms with
  | 0 -> ()
  | _ -> jsSetTimeout (fun () -> clearBlockDecoration line) ms |> ignore

let clearAllDecorations () =
  blockDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  blockDecorations <- Map.empty
  staleDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  staleDecorations <- Map.empty
  evalInProgressDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  evalInProgressDecorations <- Map.empty
  clearBindingValueDecorations ()

/// Build a VS Code decoration-options object from a zero-based line/column
/// pair, an "after" ghost-text label, and (unlike the label) the FULL,
/// untruncated hover text — so whatever the label summarised away is still
/// reachable by hovering. The one place a zero-based `Range` for this
/// module's decorations is built.
let private ghostTextDeco (line: int) (endCol: int) (color: string) (contentText: string) (hoverText: string) =
  let opts = createObj [
    "after" ==> createObj [
      "contentText" ==> contentText
      "color" ==> newThemeColor color
      "fontStyle" ==> "italic"
    ]
  ]
  let deco = Window.createTextEditorDecorationType opts
  let range = newRange line endCol line endCol
  let rangeWithHover = createObj [
    "range" ==> box range
    "hoverMessage" ==> hoverText
  ]
  deco, rangeWithHover

/// Show an "⏳ evaluating…" ghost-text suffix at the end of the given (0-based) line.
/// Uses a separate decoration type from result/stale markers so it can be cleared independently.
let showEvalInProgress (editor: TextEditor) (line: int) : unit =
  match Map.tryFind line evalInProgressDecorations with
  | Some existing ->
    existing.dispose () |> ignore
    evalInProgressDecorations <- Map.remove line evalInProgressDecorations
  | None -> ()
  let opts = createObj [
    "after" ==> createObj [
      "contentText" ==> InlineDecorationsPure.evalInProgressLabel None
      "color" ==> newThemeColor "sagefs.staleForeground"
      "fontStyle" ==> "italic"
    ]
  ]
  let deco = Window.createTextEditorDecorationType opts
  let lineText = editor.document.lineAt(float line).text
  let endCol = lineText.Length
  let range = newRange line endCol line endCol
  editor.setDecorations(deco, ResizeArray [| box range |])
  evalInProgressDecorations <- Map.add line deco evalInProgressDecorations

/// Remove all "evaluating" decorations (call when eval_result arrives).
let clearEvalInProgress (_editor: TextEditor) : unit =
  evalInProgressDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  evalInProgressDecorations <- Map.empty

/// Update the "⏳ evaluating…" decoration with elapsed time.
/// Replaces the existing in-progress decoration so the elapsed time ticks visibly.
let updateEvalInProgressElapsed (editor: TextEditor) (line: int) (elapsedMs: int64) : unit =
  match Map.tryFind line evalInProgressDecorations with
  | None -> ()
  | Some existing ->
    existing.dispose () |> ignore
    evalInProgressDecorations <- Map.remove line evalInProgressDecorations
    let opts = createObj [
      "after" ==> createObj [
        "contentText" ==> InlineDecorationsPure.evalInProgressLabel (Some elapsedMs)
        "color" ==> newThemeColor "sagefs.staleForeground"
        "fontStyle" ==> "italic"
      ]
    ]
    let deco = Window.createTextEditorDecorationType opts
    let lineText = editor.document.lineAt(float line).text
    let endCol = lineText.Length
    let range = newRange line endCol line endCol
    editor.setDecorations(deco, ResizeArray [| box range |])
    evalInProgressDecorations <- Map.add line deco evalInProgressDecorations

let markDecorationsStale (editor: TextEditor) =
  let lines = blockDecorations |> Map.toList |> List.map fst
  let alreadyStale = staleDecorations |> Map.toList |> List.map fst |> Set.ofList
  let newlyStale = InlineDecorationsPure.staleTransition lines alreadyStale |> Set.ofList
  for line in lines do
    match Map.tryFind line blockDecorations with
    | Some deco ->
      deco.dispose () |> ignore
      blockDecorations <- Map.remove line blockDecorations
    | None -> ()
    if Set.contains line newlyStale then
      let staleOpts = createObj [
        "after" ==> createObj [
          "contentText" ==> "  // ⏸ stale"
          "color" ==> newThemeColor "sagefs.staleForeground"
          "fontStyle" ==> "italic"
        ]
      ]
      let staleDeco = Window.createTextEditorDecorationType staleOpts
      let lineText = editor.document.lineAt(float line).text
      let endCol = lineText.Length
      let range = newRange line endCol line endCol
      editor.setDecorations(staleDeco, ResizeArray [| box range |])
      staleDecorations <- Map.add line staleDeco staleDecorations

/// Get the line number for inline decoration placement.
let private getEditorLine (editor: TextEditor) =
  if editor.selection.isEmpty
  then int editor.selection.active.line
  else int editor.selection.``end``.line

/// Flash-highlight a range of lines briefly to indicate eval started.
let flashEvalRange (editor: TextEditor) (startLine: int) (endLine: int) =
  let opts = createObj [
    "backgroundColor" ==> newThemeColor "sagefs.evalFlashBackground"
    "isWholeLine" ==> true
  ]
  let deco = Window.createTextEditorDecorationType opts
  let ranges = ResizeArray<obj>()
  for i in startLine .. endLine do
    ranges.Add(box (newRange i 0 i 0))
  editor.setDecorations(deco, ranges)
  jsSetTimeout (fun () -> deco.dispose () |> ignore) 300 |> ignore

/// Show an eval result's ghost text at `atLine` (or the current selection's
/// line). The summarised/truncated form comes from `renderInlineResult`;
/// the full result is attached as `hoverMessage` so a multi-line or
/// truncated result is always fully reachable, not only its summary.
let showInlineResult (editor: TextEditor) (text: string) (durationMs: float option) (atLine: int option) =
  match InlineDecorationsPure.renderInlineResult durationMs text with
  | None -> ()
  | Some rendered ->
    let line = atLine |> Option.defaultWith (fun () -> getEditorLine editor)
    clearBlockDecoration line
    let lineText = editor.document.lineAt(float line).text
    let endCol = lineText.Length
    let deco, rangeWithHover =
      ghostTextDeco line endCol "sagefs.successForeground" rendered.ContentText rendered.HoverText
    editor.setDecorations(deco, ResizeArray [| box rangeWithHover |])
    blockDecorations <- Map.add line deco blockDecorations
    autoClearAfter line

// ── Binding value decorations (persistent, from bindings_snapshot) ────────

/// Show binding values as persistent inline ghost text at their source lines.
/// Only non-function bindings are shown. Decorations persist until the next
/// bindings_snapshot replaces them (no auto-clear timeout).
/// blockStartLine is 0-based. bv.SourceLine is 1-based (0 = unknown → skip).
let showBindingValues
    (editor: TextEditor)
    (blockStartLine: int)
    (bindingValues: SageFs.Vscode.FeatureTypes.ClientBindingValue list) =
  clearBindingValueDecorations ()
  let cfg = Workspace.getConfiguration "sagefs"
  let density = SageFs.Vscode.DensityPure.Density.ofString (cfg.get("density", "full"))
  match SageFs.Vscode.DensityPure.shows density SageFs.Vscode.DensityPure.AnnotationSurface.BindingGhostText with
  | false -> ()
  | true ->
  let visible =
    bindingValues
    |> List.filter (fun bv -> InlineDecorationsPure.isVisibleBinding bv.IsFunctionValue bv.SourceLine)
  match visible with
  | [] -> ()
  | _ ->
    let opts = createObj [
      "after" ==> createObj [
        "color" ==> newThemeColor "sagefs.bindingValueForeground"
        "fontStyle" ==> "italic"
      ]
    ]
    let deco = Window.createTextEditorDecorationType opts
    bindingValueDecorationType <- Some deco
    let lineCount = int editor.document.lineCount
    let ranges = ResizeArray<obj>()
    for bv in visible do
      match InlineDecorationsPure.bindingTargetLine blockStartLine bv.SourceLine lineCount with
      | None -> ()
      | Some lineIdx ->
        let lineText = editor.document.lineAt(float lineIdx).text
        let endCol = lineText.Length
        let contentText = sprintf "  %s" (SageFs.Vscode.FeatureTypes.toGhostText bv)
        let rangeWithText = createObj [
          "range" ==> box (newRange lineIdx endCol lineIdx endCol)
          "renderOptions" ==> createObj [
            "after" ==> createObj [
              "contentText" ==> contentText
            ]
          ]
        ]
        ranges.Add(box rangeWithText)
    editor.setDecorations(deco, ranges)

/// Show a diagnostic's ghost text at `atLine` (or the current selection's
/// line). Only the first line renders inline; the full diagnostic text is
/// attached as `hoverMessage` so a multi-line error message is always fully
/// reachable, not only its first line.
let showInlineDiagnostic (editor: TextEditor) (text: string) (atLine: int option) =
  match InlineDecorationsPure.renderInlineDiagnostic text with
  | None -> ()
  | Some rendered ->
    let line = atLine |> Option.defaultWith (fun () -> getEditorLine editor)
    clearBlockDecoration line
    let lineText = editor.document.lineAt(float line).text
    let endCol = lineText.Length
    let deco, rangeWithHover =
      ghostTextDeco line endCol "sagefs.errorForeground" rendered.ContentText rendered.HoverText
    editor.setDecorations(deco, ResizeArray [| box rangeWithHover |])
    blockDecorations <- Map.add line deco blockDecorations
    autoClearAfter line
