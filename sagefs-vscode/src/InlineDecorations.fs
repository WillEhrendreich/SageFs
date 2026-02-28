module SageFs.Vscode.InlineDecorations

open Fable.Core
open Fable.Core.JsInterop
open Vscode

// ── Mutable state ──────────────────────────────────────────────

let mutable blockDecorations: Map<int, TextEditorDecorationType> = Map.empty
let mutable staleDecorations: Map<int, TextEditorDecorationType> = Map.empty

// ── JS Interop ─────────────────────────────────────────────────

[<Emit("new vscode.ThemeColor($0)")>]
let newThemeColor (id: string) : obj = jsNative

[<Emit("new vscode.Range($0, $1, $2, $3)")>]
let newRange (startLine: int) (startChar: int) (endLine: int) (endChar: int) : Range = jsNative

[<Emit("setTimeout($0, $1)")>]
let setTimeout (fn: unit -> unit) (ms: int) : obj = jsNative

[<Emit("performance.now()")>]
let performanceNow () : float = jsNative

// ── Helpers ────────────────────────────────────────────────────

let formatDuration (ms: float) =
  if ms < 1000.0 then sprintf "%dms" (int ms) else sprintf "%.1fs" (ms / 1000.0)

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

let clearAllDecorations () =
  blockDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  blockDecorations <- Map.empty
  staleDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  staleDecorations <- Map.empty

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

let showInlineResult (editor: TextEditor) (text: string) (durationMs: float option) =
  let trimmed = text.Trim()
  match trimmed with
  | "" -> ()
  | _ ->
    let line =
      if editor.selection.isEmpty then int editor.selection.active.line
      else int editor.selection.``end``.line
    clearBlockDecoration line
    let lines = trimmed.Split('\n')
    let firstLine = if lines.Length > 0 then lines.[0] else ""
    let durSuffix =
      match durationMs with
      | Some ms -> sprintf "  %s" (formatDuration ms)
      | None -> ""
    let contentText =
      if lines.Length <= 1 then
        sprintf "  // → %s%s" firstLine durSuffix
      else
        let summary =
          if lines.Length <= 4 then lines |> String.concat "  │  "
          else sprintf "%s  │  ... (%d lines)" firstLine lines.Length
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
    setTimeout (fun () -> clearBlockDecoration line) 30000 |> ignore

let showInlineDiagnostic (editor: TextEditor) (text: string) =
  let firstLine =
    let parts = text.Split('\n')
    if parts.Length > 0 then parts.[0].Trim() else ""
  match firstLine with
  | "" -> ()
  | _ ->
    let line =
      if editor.selection.isEmpty then int editor.selection.active.line
      else int editor.selection.``end``.line
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
    setTimeout (fun () -> clearBlockDecoration line) 30000 |> ignore
