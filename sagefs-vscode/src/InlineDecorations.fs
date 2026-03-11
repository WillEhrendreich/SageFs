module SageFs.Vscode.InlineDecorations

open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers

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

let formatDuration (ms: float) =
  if ms < 1000.0 then sprintf "%dms" (int ms)
  else sprintf "%.1fs" (ms / 1000.0)

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
      "contentText" ==> "  // ⏳ evaluating…"
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
    let secs = float elapsedMs / 1000.0
    let label = sprintf "  // ⏳ evaluating… %.1fs" secs
    let opts = createObj [
      "after" ==> createObj [
        "contentText" ==> label
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
  for line in lines do
    match Map.tryFind line blockDecorations with
    | Some deco ->
      deco.dispose () |> ignore
      blockDecorations <- Map.remove line blockDecorations
      if not (Map.containsKey line staleDecorations) then
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
    | None -> ()

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

let showInlineResult (editor: TextEditor) (text: string) (durationMs: float option) (atLine: int option) =
  let trimmed = text.Trim()
  match trimmed with
  | "" -> ()
  | _ ->
    let line = atLine |> Option.defaultWith (fun () -> getEditorLine editor)
    clearBlockDecoration line
    let lines = trimmed.Split('\n')
    let firstLine = match lines.Length with 0 -> "" | _ -> lines.[0]
    let durSuffix =
      match durationMs with
      | Some ms -> sprintf "  %s" (formatDuration ms)
      | None -> ""
    let contentText =
      match lines.Length with
      | 0 | 1 ->
        sprintf "  // → %s%s" firstLine durSuffix
      | n ->
        let summary =
          if n <= 4 then lines |> String.concat "  │  "
          else sprintf "%s  │  ... (%d lines)" firstLine n
        sprintf "  // → %s%s" summary durSuffix
    let opts = createObj [
      "after" ==> createObj [
        "contentText" ==> contentText
        "color" ==> newThemeColor "sagefs.successForeground"
        "fontStyle" ==> "italic"
      ]
    ]
    let deco = Window.createTextEditorDecorationType opts
    let lineText = editor.document.lineAt(float line).text
    let endCol = lineText.Length
    let range = newRange line endCol line endCol
    editor.setDecorations(deco, ResizeArray [| box range |])
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
  let visible =
    bindingValues
    |> List.filter (fun bv ->
      not bv.IsFunctionValue && bv.SourceLine > 0)
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
    let ranges = ResizeArray<obj>()
    for bv in visible do
      let lineIdx = blockStartLine + bv.SourceLine - 1
      let lineCount = editor.document.lineCount
      match lineIdx >= 0 && float lineIdx < lineCount with
      | false -> ()
      | true ->
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

let showInlineDiagnostic (editor: TextEditor) (text: string) (atLine: int option) =
  let firstLine =
    let parts = text.Split('\n')
    match parts.Length with 0 -> "" | _ -> parts.[0].Trim()
  match firstLine with
  | "" -> ()
  | _ ->
    let line = atLine |> Option.defaultWith (fun () -> getEditorLine editor)
    clearBlockDecoration line
    let opts = createObj [
      "after" ==> createObj [
        "contentText" ==> sprintf "  // ❌ %s" firstLine
        "color" ==> newThemeColor "sagefs.errorForeground"
        "fontStyle" ==> "italic"
      ]
    ]
    let deco = Window.createTextEditorDecorationType opts
    let lineText = editor.document.lineAt(float line).text
    let endCol = lineText.Length
    let range = newRange line endCol line endCol
    editor.setDecorations(deco, ResizeArray [| box range |])
    blockDecorations <- Map.add line deco blockDecorations
    autoClearAfter line
