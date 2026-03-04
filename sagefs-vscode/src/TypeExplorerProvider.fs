module SageFs.Vscode.TypeExplorerProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

module Client = SageFs.Vscode.SageFsClient

// ── Mutable state ────────────────────────────────────────────────

let mutable currentClient: Client.Client option = None
let mutable refreshEmitter: EventEmitter<obj> option = None
let mutable getSessionId: unit -> string option = fun () -> None

// ── Kind → icon mapping ─────────────────────────────────────────

let private kindIcon (kind: string) =
  match kind with
  | "Namespace" -> "symbol-namespace"
  | "Module" -> "symbol-module"
  | "Class" -> "symbol-class"
  | "Struct" -> "symbol-struct"
  | "Interface" -> "symbol-interface"
  | "Enum" -> "symbol-enum"
  | "Union" -> "symbol-enum"
  | "Type" -> "symbol-class"
  | "Method" | "OverriddenMethod" | "ExtensionMethod" -> "symbol-method"
  | "Property" -> "symbol-property"
  | "Field" -> "symbol-field"
  | "Event" -> "symbol-event"
  | "Constant" -> "symbol-constant"
  | "Variable" -> "symbol-variable"
  | "EnumMember" -> "symbol-enum-member"
  | "Keyword" -> "symbol-keyword"
  | _ -> "symbol-misc"

let private drillableKinds =
  Set [ "Namespace"; "Module"; "Class"; "Struct"; "Interface"; "Enum"; "Union"; "Type" ]

// ── Tree item builders───────────────────────────────────────────

let leafItem (label: string) (desc: string) (icon: string) =
  let item = newTreeItem label TreeItemCollapsibleState.None
  item.description <- desc
  item.iconPath <- Vscode.newThemeIcon icon
  item

let expandableItem (label: string) (desc: string) (icon: string) (contextValue: string) =
  let item = newTreeItem label TreeItemCollapsibleState.Collapsed
  item.description <- desc
  item.iconPath <- Vscode.newThemeIcon icon
  item.contextValue <- contextValue
  item

// ── Completions parsing ─────────────────────────────────────────

let private parseCompletionsJson (parentContext: string) (json: string) : obj array option =
  try
    let parsed = jsonParse json
    let completions: obj array =
      match jsIsNullOrUndefined parsed?completions with
      | true -> [||]
      | false -> !!parsed?completions
    match completions.Length with
    | 0 -> Some [| leafItem "No members" "" "info" :> obj |]
    | _ ->
      completions
      |> Array.truncate 200
      |> Array.map (fun c ->
        let label = fieldString "label" c |> Option.defaultValue "?"
        let kind = fieldString "kind" c |> Option.defaultValue ""
        let insertText = fieldString "insertText" c |> Option.defaultValue label
        let detail = fieldString "detail" c |> Option.defaultValue ""
        let icon = kindIcon kind
        let fullName =
          match parentContext with
          | "" | null -> insertText
          | p -> sprintf "%s.%s" p insertText
        match drillableKinds.Contains kind with
        | true ->
          let desc = match kind with "" -> "" | k -> k
          expandableItem label desc icon (sprintf "explore:%s" fullName) :> obj
        | false ->
          let desc = match detail with "" -> kind | d -> d
          leafItem label desc icon :> obj)
      |> Some
  with _ -> None

let private exploreAndParse (query: string) (c: Client.Client) =
  promise {
    let sid = getSessionId () |> Option.defaultValue ""
    let! result = Client.exploreCompletions query sid c
    match result with
    | Some json ->
      match parseCompletionsJson query json with
      | Some items -> return items
      | None -> return [| leafItem "Error parsing response" "" "warning" :> obj |]
    | None ->
      return [| leafItem "Not connected" "" "warning" :> obj |]
  }

// ── Common roots ────────────────────────────────────────────────

let private commonRoots =
  [| "System"; "System.Collections.Generic"; "System.IO"; "System.Linq"
     "System.Text"; "Microsoft.FSharp.Collections"; "Microsoft.FSharp.Core" |]

// ── TreeDataProvider ─────────────────────────────────────────────

let getChildren (element: obj option) : JS.Promise<obj array> =
  promise {
    match element, currentClient with
    | None, _ ->
      let roots =
        commonRoots
        |> Array.map (fun ns ->
          expandableItem ns "" "symbol-namespace" (sprintf "explore:%s" ns) :> obj)
      return roots
    | Some el, Some c ->
      let ctx = fieldString "contextValue" el |> Option.defaultValue ""
      match ctx with
      | c' when c' <> null && c'.StartsWith("explore:") ->
        return! exploreAndParse (c'.Substring(8)) c
      | _ ->
        return [||]
    | _, None ->
      return [| leafItem "Not connected" "" "warning" :> obj |]
  }

let getTreeItem (element: obj) : obj = element

// ── Public API ──────────────────────────────────────────────────

type TypeExplorer = {
  treeView: TreeView<obj>
  dispose: unit -> unit
}

let create (context: ExtensionContext) (c: Client.Client option) (sessionIdFn: unit -> string option) : TypeExplorer =
  currentClient <- c
  getSessionId <- sessionIdFn
  let emitter = newEventEmitter<obj> ()
  refreshEmitter <- Some emitter
  let provider =
    createObj [
      "getTreeItem" ==> System.Func<obj, obj>(getTreeItem)
      "getChildren" ==> System.Func<obj option, JS.Promise<obj array>>(getChildren)
      "onDidChangeTreeData" ==> emitter.event
    ]
  let tv = Window.createTreeView "sagefs-types" (createObj [ "treeDataProvider" ==> provider ])
  context.subscriptions.Add (tv :> obj :?> Disposable)
  { treeView = tv
    dispose = fun () ->
      tv.dispose ()
      emitter.dispose () }

let refresh () =
  match refreshEmitter with
  | Some e -> e.fire null
  | None -> ()

let setClient (c: Client.Client option) =
  currentClient <- c
  refresh ()
