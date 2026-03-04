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
          let offset = int (doc.offsetAt pos)
          let! items = Client.getCompletions text offset (getWorkDir ()) c
          return
            items
            |> Array.map (fun item ->
              let ci = newCompletionItem item.label (kindToVscode item.kind)
              ci.insertText <- item.insertText
              item.detail |> Option.iter (fun d -> ci.detail <- d)
              ci)
      }
  ]
