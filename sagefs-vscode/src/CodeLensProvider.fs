module SageFs.Vscode.CodeLensProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode

/// Creates a CodeLens provider object compatible with VSCode's API.
/// Returns a plain JS object with the provideCodeLenses method.
let create () =
  createObj [
    "provideCodeLenses" ==> fun (doc: TextDocument) (_token: obj) ->
      let text = doc.getText ()
      let lines = text.Split('\n')
      let lenses = ResizeArray<CodeLens>()
      for i in 0 .. lines.Length - 1 do
        let line = lines.[i].TrimEnd()
        if line.EndsWith(";;") then
          let range = newRange i 0 i line.Length
          let cmd = createObj [
            "title" ==> "▶ Eval"
            "command" ==> "sagefs.eval"
            "arguments" ==> [| box i |]
          ]
          lenses.Add(newCodeLens range cmd)
      lenses.ToArray()
  ]
