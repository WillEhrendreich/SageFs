module SageFs.Vscode.CompletionProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode

module Client = SageFs.Vscode.SageFsClient

let kindToVscode (kind: string) =
  match kind with
  | "Method" -> CompletionItemKind.Method
  | "Function" -> CompletionItemKind.Function
  | "Property" -> CompletionItemKind.Property
  | "Field" -> CompletionItemKind.Field
  | "Class" | "Type" -> CompletionItemKind.Class
  | "Interface" -> CompletionItemKind.Interface
  | "Module" | "Namespace" -> CompletionItemKind.Module
  | "Enum" -> CompletionItemKind.Enum
  | "Keyword" -> CompletionItemKind.Keyword
  | "Event" -> CompletionItemKind.Event
  | _ -> CompletionItemKind.Variable

let create (getClient: unit -> Client.Client option) (getWorkDir: unit -> string option) =
  createObj [
    "provideCompletionItems" ==> fun (doc: TextDocument) (pos: Position) (_token: obj) ->
      promise {
        match getClient () with
        | None -> return [||]
        | Some c ->
          let text = doc.getText ()
          let lines = text.Split('\n')
          let mutable offset = 0
          for i in 0 .. int pos.line - 1 do
            offset <- offset + lines.[i].Length + 1
          offset <- offset + int pos.character
          let! items = Client.getCompletions text offset (getWorkDir ()) c
          return
            items
            |> Array.map (fun item ->
              let ci = newCompletionItem item.label (kindToVscode item.kind)
              ci?insertText <- item.insertText
              ci)
      }
  ]
