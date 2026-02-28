module SageFs.Vscode.SessionContextTreeProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers

module Client = SageFs.Vscode.SageFsClient

// ── Mutable state ────────────────────────────────────────────────

let mutable currentClient: Client.Client option = None
let mutable currentSessionId: string option = None
let mutable cachedContext: Client.WarmupContextInfo option = None
let mutable refreshEmitter: EventEmitter<obj> option = None
let mutable autoRefreshTimer: obj option = None

// ── Tree item builders ───────────────────────────────────────────

let sectionItem (label: string) (desc: string) (icon: string) =
  let item = newTreeItem label TreeItemCollapsibleState.Expanded
  item?description <- desc
  item?iconPath <- Vscode.newThemeIcon icon
  item?contextValue <- "section"
  item

let leafItem (label: string) (desc: string) (icon: string) =
  let item = newTreeItem label TreeItemCollapsibleState.None
  item?description <- desc
  item?iconPath <- Vscode.newThemeIcon icon
  item

let summaryItem (ctx: Client.WarmupContextInfo) =
  let nsCount = ctx.NamespacesOpened.Length
  let failCount = ctx.FailedOpens.Length
  let desc =
    sprintf "%d assemblies | %d namespaces | %dms"
      ctx.AssembliesLoaded.Length nsCount ctx.WarmupDurationMs
  let item = newTreeItem "Session Warmup" TreeItemCollapsibleState.Expanded
  item?description <- desc
  item?iconPath <- Vscode.newThemeIcon "symbol-event"
  item?contextValue <- "summary"
  match failCount with
  | 0 -> ()
  | _ -> item?description <- sprintf "%s | %d failed" desc failCount
  item

// ── TreeDataProvider ─────────────────────────────────────────────

let getChildren (element: obj option) : JS.Promise<obj array> =
  promise {
    match element with
    | None ->
      match cachedContext with
      | None ->
        let item = newTreeItem "No session context" TreeItemCollapsibleState.None
        item?description <- "Waiting for session..."
        return [| item :> obj |]
      | Some ctx ->
        return [| summaryItem ctx :> obj |]
    | Some el ->
      let ctx = tryField<string> "contextValue" el |> Option.defaultValue ""
      match ctx with
      | "summary" ->
        match cachedContext with
        | None -> return [||]
        | Some wc ->
          let sections = ResizeArray<obj>()
          match wc.AssembliesLoaded with
          | [||] -> ()
          | loaded ->
            sections.Add(
              sectionItem
                "Assemblies"
                (sprintf "%d loaded" loaded.Length)
                "package" :> obj)
          match wc.NamespacesOpened with
          | [||] -> ()
          | opened ->
            sections.Add(
              sectionItem
                "Namespaces"
                (sprintf "%d opened" opened.Length)
                "symbol-namespace" :> obj)
          match wc.FailedOpens with
          | [||] -> ()
          | failed ->
            sections.Add(
              sectionItem
                "Failed Opens"
                (sprintf "%d failed" failed.Length)
                "error" :> obj)
          return sections.ToArray()
      | "section" ->
        let label = tryField<string> "label" el |> Option.defaultValue ""
        match cachedContext with
        | None -> return [||]
        | Some wc ->
          match label with
          | "Assemblies" ->
            return
              wc.AssembliesLoaded
              |> Array.map (fun a ->
                leafItem
                  a.Name
                  (sprintf "%d ns, %d mod" a.NamespaceCount a.ModuleCount)
                  "library" :> obj)
          | "Namespaces" ->
            return
              wc.NamespacesOpened
              |> Array.map (fun b ->
                let kind = match b.IsModule with true -> "module" | false -> "namespace"
                leafItem b.Name (sprintf "%s via %s" kind b.Source) "symbol-namespace" :> obj)
          | "Failed Opens" ->
            return
              wc.FailedOpens
              |> Array.map (fun pair ->
                let name =
                  match pair.Length with
                  | 0 -> "?"
                  | _ -> pair.[0]
                let err =
                  match pair.Length with
                  | n when n > 1 -> pair.[1]
                  | _ -> "unknown"
                leafItem name err "error" :> obj)
          | _ -> return [||]
      | _ -> return [||]
  }

let getTreeItem (element: obj) : obj = element

let createProvider () =
  let emitter = newEventEmitter<obj> ()
  refreshEmitter <- Some emitter
  createObj [
    "onDidChangeTreeData" ==> emitter.event
    "getChildren" ==> fun (el: obj) ->
      let elOpt = tryOfObj el
      getChildren elOpt
    "getTreeItem" ==> getTreeItem
  ]

// ── Public API ───────────────────────────────────────────────────

let refresh () =
  match currentClient, currentSessionId with
  | Some c, Some sid ->
    promise {
      let! ctx = Client.getWarmupContext sid c
      cachedContext <- ctx
      match refreshEmitter with
      | Some e -> e.fire null
      | None -> ()
    } |> ignore
  | _ ->
    cachedContext <- None
    match refreshEmitter with
    | Some e -> e.fire null
    | None -> ()

let setSession (c: Client.Client) (sessionId: string option) =
  currentClient <- Some c
  currentSessionId <- sessionId
  match autoRefreshTimer with
  | Some t -> jsClearInterval t; autoRefreshTimer <- None
  | None -> ()
  match sessionId with
  | Some _ ->
    autoRefreshTimer <- Some (jsSetInterval (fun () -> refresh ()) 10000)
  | None -> ()
  refresh ()

let register (ctx: ExtensionContext) =
  let provider = createProvider ()
  let tv = Window.createTreeView "sagefs-sessionContext" (createObj [
    "treeDataProvider" ==> provider
    "showCollapseAll" ==> true
  ])
  ctx.subscriptions.Add(tv :> obj :?> Disposable)

  let refreshCmd =
    Commands.registerCommand "sagefs.sessionContextRefresh" (fun _ -> refresh ())
  ctx.subscriptions.Add refreshCmd
