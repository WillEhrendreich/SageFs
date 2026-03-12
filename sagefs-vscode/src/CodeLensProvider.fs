module SageFs.Vscode.CodeLensProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode

module Blocks = SageFs.Vscode.CodeBlocks

/// Creates a CodeLens provider object compatible with VSCode's API.
/// Shows "▶ Eval" at the start of each code block — either ;; delimited or blank-line separated.
/// Respects density setting: disabled in Minimal and Normal modes.
let create () =
  createObj [
    "provideCodeLenses" ==> fun (doc: TextDocument) (_token: obj) ->
      let cfg = Workspace.getConfiguration "sagefs"
      let density = cfg.get("density", "full")
      match density with
      | "minimal" | "normal" -> [||]
      | _ ->
      let lenses = ResizeArray<CodeLens>()
      let blocks = Blocks.getAllBlockRanges doc
      for blockStart, _blockEnd in blocks do
        let range = newRange blockStart 0 blockStart 0
        let cmd = createObj [
          "title" ==> "▶ Eval"
          "command" ==> "sagefs.eval"
          "arguments" ==> [| box blockStart |]
        ]
        lenses.Add(newCodeLens range cmd)
      lenses.ToArray()
  ]
