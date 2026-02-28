module SageFs.Vscode.TypeExplorerProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode

module Client = SageFs.Vscode.SageFsClient

// ── Mutable state ────────────────────────────────────────────────

let mutable currentClient: Client.Client option = None
let mutable refreshEmitter: EventEmitter<obj> option = None

[<Emit("JSON.parse($0)")>]
let jsonParse (s: string) : obj = jsNative

// ── Tree item builders ───────────────────────────────────────────

let leafItem (label: string) (desc: string) (icon: string) =
  let item = newTreeItem label TreeItemCollapsibleState.None
  item?description <- desc
  item?iconPath <- Vscode.newThemeIcon icon
  item

let expandableItem (label: string) (desc: string) (icon: string) (contextValue: string) =
  let item = newTreeItem label TreeItemCollapsibleState.Collapsed
  item?description <- desc
  item?iconPath <- Vscode.newThemeIcon icon
  item?contextValue <- contextValue
  item

let private parseLine (line: string) : obj =
  let trimmed = line.Trim()
  match trimmed with
  | t when t.StartsWith("namespace") || t.StartsWith("module") ->
    let name = t.Split(' ') |> Array.last
    expandableItem name "" "symbol-namespace" (sprintf "ns:%s" name) :> obj
  | t when t.StartsWith("type") ->
    let name = t.Split(' ') |> Array.tryItem 1 |> Option.defaultValue t
    leafItem name "type" "symbol-class" :> obj
  | t ->
    leafItem t "" "symbol-misc" :> obj

let private parseExploreResponse (json: string) : obj array option =
  try
    let parsed = jsonParse json
    let text = parsed?content |> unbox<string>
    text.Split('\n')
    |> Array.filter (fun l -> l.Trim().Length > 0)
    |> Array.truncate 50
    |> Array.map parseLine
    |> Some
  with _ -> None

let private exploreAndParse (query: string) (errorMsg: string) (c: Client.Client) =
  promise {
    let! result = Client.explore query c
    match result with
    | Some json ->
      match parseExploreResponse json with
      | Some items -> return items
      | None -> return [| leafItem errorMsg "" "warning" :> obj |]
    | None ->
      return [| leafItem "Not connected" "" "warning" :> obj |]
  }

// ── TreeDataProvider ─────────────────────────────────────────────

let getChildren (element: obj option) : JS.Promise<obj array> =
  promise {
    match element, currentClient with
    | None, _ ->
      let item = expandableItem "Namespaces" "explore loaded types" "symbol-namespace" "ns-root"
      return [| item :> obj |]
    | Some el, Some c when (el?contextValue |> unbox<string>) = "ns-root" ->
      return! exploreAndParse "System" "Error parsing response" c
    | Some el, Some c ->
      let ctx = el?contextValue |> unbox<string>
      match ctx with
      | c' when c' <> null && c'.StartsWith("ns:") ->
        return! exploreAndParse (c'.Substring(3)) "Error parsing" c
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

let create (context: ExtensionContext) (c: Client.Client option) : TypeExplorer =
  currentClient <- c
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
