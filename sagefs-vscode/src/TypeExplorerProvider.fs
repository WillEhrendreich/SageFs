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

// ── TreeDataProvider ─────────────────────────────────────────────

let getChildren (element: obj option) : JS.Promise<obj array> =
  promise {
    match element, currentClient with
    | None, _ ->
      // Root: show prompt to explore
      let item = expandableItem "Namespaces" "explore loaded types" "symbol-namespace" "ns-root"
      return [| item :> obj |]
    | Some el, Some c when (el?contextValue |> unbox<string>) = "ns-root" ->
      // Load top-level namespaces from the session
      let! result = Client.explore "System" c
      match result with
      | Some json ->
        try
          let parsed = jsonParse json
          let text = parsed?content |> unbox<string>
          let lines =
            text.Split('\n')
            |> Array.filter (fun l -> l.Trim().Length > 0)
            |> Array.truncate 50
          return
            lines |> Array.map (fun line ->
              let trimmed = line.Trim()
              if trimmed.StartsWith("namespace") || trimmed.StartsWith("module") then
                let name = trimmed.Split(' ') |> Array.last
                expandableItem name "" "symbol-namespace" (sprintf "ns:%s" name) :> obj
              elif trimmed.StartsWith("type") then
                let name = trimmed.Split(' ') |> Array.tryItem 1 |> Option.defaultValue trimmed
                leafItem name "type" "symbol-class" :> obj
              else
                leafItem trimmed "" "symbol-misc" :> obj)
        with _ ->
          return [| leafItem "Error parsing response" "" "warning" :> obj |]
      | None ->
        return [| leafItem "Not connected" "" "warning" :> obj |]
    | Some el, Some c ->
      let ctx = el?contextValue |> unbox<string>
      if ctx <> null && ctx.StartsWith("ns:") then
        let nsName = ctx.Substring(3)
        let! result = Client.explore nsName c
        match result with
        | Some json ->
          try
            let parsed = jsonParse json
            let text = parsed?content |> unbox<string>
            let lines =
              text.Split('\n')
              |> Array.filter (fun l -> l.Trim().Length > 0)
              |> Array.truncate 50
            return
              lines |> Array.map (fun line ->
                let trimmed = line.Trim()
                if trimmed.StartsWith("namespace") || trimmed.StartsWith("module") then
                  let name = trimmed.Split(' ') |> Array.last
                  expandableItem name "" "symbol-namespace" (sprintf "ns:%s" name) :> obj
                elif trimmed.StartsWith("type") then
                  let name = trimmed.Split(' ') |> Array.tryItem 1 |> Option.defaultValue trimmed
                  leafItem name "type" "symbol-class" :> obj
                else
                  leafItem trimmed "" "symbol-misc" :> obj)
          with _ ->
            return [| leafItem "Error parsing" "" "warning" :> obj |]
        | None ->
          return [| leafItem "Could not explore" "" "warning" :> obj |]
      else
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
