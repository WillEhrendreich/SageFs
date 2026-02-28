module SageFs.Vscode.HotReloadTreeProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers

module Client = SageFs.Vscode.SageFsClient

// ── Types ────────────────────────────────────────────────────────

type HotReloadItem =
  { path: string
    watched: bool
    isDirectory: bool
    children: HotReloadItem array }

// ── Mutable state ────────────────────────────────────────────────

let mutable currentClient: Client.Client option = None
let mutable currentSessionId: string option = None
let mutable cachedFiles: Client.HotReloadFile array = [||]
let mutable refreshEmitter: EventEmitter<obj> option = None
let mutable treeView: TreeView<obj> option = None
let mutable autoRefreshTimer: obj option = None

// ── Path helpers ─────────────────────────────────────────────────

let getDirectory (path: string) =
  match path with
  | null -> ""
  | _ ->
    let normalized = path.Replace('\\', '/')
    match normalized.LastIndexOf('/') with
    | -1 -> ""
    | i -> normalized.Substring(0, i)

let getFileName (path: string) =
  match path with
  | null -> ""
  | _ ->
    let normalized = path.Replace('\\', '/')
    match normalized.LastIndexOf('/') with
    | -1 -> normalized
    | i -> normalized.Substring(i + 1)

// ── TreeDataProvider ─────────────────────────────────────────────

let createDirItem (dirPath: string) (childCount: int) (watchedCount: int) =
  let label =
    match dirPath with
    | "" -> "(root)"
    | p -> p
  let item = newTreeItem label TreeItemCollapsibleState.Expanded
  item?contextValue <- "directory"
  item?description <- sprintf "%d/%d watched" watchedCount childCount
  item?iconPath <- Vscode.newThemeColor "symbolIcon.folderForeground"
  item?command <-
    createObj [
      "command" ==> "sagefs.hotReloadToggleDirectory"
      "title" ==> "Toggle Directory"
      "arguments" ==> [| box dirPath |]
    ]
  item

let createFileItem (file: Client.HotReloadFile) =
  let label = getFileName file.path
  let item = newTreeItem label TreeItemCollapsibleState.None
  let ctxVal, desc, iconColor =
    match file.watched with
    | true -> "watchedFile", "● watching", "testing.iconPassed"
    | false -> "unwatchedFile", "○ not watching", "testing.iconSkipped"
  item?contextValue <- ctxVal
  item?description <- desc
  item?tooltip <- file.path
  item?command <-
    createObj [
      "command" ==> "sagefs.hotReloadToggle"
      "title" ==> "Toggle Hot Reload"
      "arguments" ==> [| file.path |]
    ]
  item?iconPath <- Vscode.newThemeColor iconColor
  item

let groupByDirectory (files: Client.HotReloadFile array) =
  files
  |> Array.groupBy (fun f -> getDirectory f.path)
  |> Array.sortBy fst

let getChildren (element: obj option) : JS.Promise<obj array> =
  promise {
    match element with
    | None ->
      let groups = groupByDirectory cachedFiles
      match groups with
      | [||] ->
        let item = newTreeItem "No session active" TreeItemCollapsibleState.None
        item?description <- "Start a session to manage hot reload"
        return [| item :> obj |]
      | [| (_, files) |] ->
        return files |> Array.map (fun f -> createFileItem f :> obj)
      | _ ->
        return
          groups
          |> Array.map (fun (dir, files) ->
            let watchedCount = files |> Array.filter (fun f -> f.watched) |> Array.length
            createDirItem dir files.Length watchedCount :> obj)
    | Some el ->
      let ctx = tryField<string> "contextValue" el |> Option.defaultValue ""
      match ctx with
      | "directory" ->
        let label = tryField<string> "label" el |> Option.defaultValue ""
        let dir = match label with "(root)" -> "" | d -> d
        let files =
          cachedFiles
          |> Array.filter (fun f -> getDirectory f.path = dir)
        return files |> Array.map (fun f -> createFileItem f :> obj)
      | _ ->
        return [||]
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
      let! state = Client.getHotReloadState sid c
      match state with
      | Some s -> cachedFiles <- s.files
      | None -> cachedFiles <- [||]
      match refreshEmitter with
      | Some e -> e.fire null
      | None -> ()
    } |> ignore
  | _ ->
    cachedFiles <- [||]
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
    autoRefreshTimer <- Some (jsSetInterval (fun () -> refresh ()) 5000)
  | None -> ()
  refresh ()

let register (ctx: ExtensionContext) =
  let provider = createProvider ()
  let tv = Window.createTreeView "sagefs-hotReload" (createObj [
    "treeDataProvider" ==> provider
    "showCollapseAll" ==> true
  ])
  treeView <- Some tv
  ctx.subscriptions.Add(tv :> obj :?> Disposable)

  // Toggle command
  let toggleCmd =
    Commands.registerCommand "sagefs.hotReloadToggle" (fun arg ->
      match currentClient, currentSessionId with
      | Some c, Some sid ->
        let path = tryOfObj (unbox<string> arg) |> Option.defaultValue ""
        promise {
          let! _ = Client.toggleHotReload sid path c
          refresh ()
        } |> ignore
      | _ -> ()
    )
  ctx.subscriptions.Add toggleCmd

  // Watch All command
  let watchAllCmd =
    Commands.registerCommand "sagefs.hotReloadWatchAll" (fun _ ->
      match currentClient, currentSessionId with
      | Some c, Some sid ->
        promise {
          let! _ = Client.watchAllHotReload sid c
          refresh ()
        } |> ignore
      | _ -> ()
    )
  ctx.subscriptions.Add watchAllCmd

  // Unwatch All command
  let unwatchAllCmd =
    Commands.registerCommand "sagefs.hotReloadUnwatchAll" (fun _ ->
      match currentClient, currentSessionId with
      | Some c, Some sid ->
        promise {
          let! _ = Client.unwatchAllHotReload sid c
          refresh ()
        } |> ignore
      | _ -> ()
    )
  ctx.subscriptions.Add unwatchAllCmd

  // Refresh command
  let refreshCmd =
    Commands.registerCommand "sagefs.hotReloadRefresh" (fun _ -> refresh ())
  ctx.subscriptions.Add refreshCmd

  // Toggle Directory command
  let toggleDirCmd =
    Commands.registerCommand "sagefs.hotReloadToggleDirectory" (fun arg ->
      match currentClient, currentSessionId with
      | Some c, Some sid ->
        let dir = tryOfObj (unbox<string> arg) |> Option.defaultValue ""
        let allWatched: bool =
          cachedFiles
          |> Array.filter (fun f -> getDirectory f.path = dir)
          |> Array.forall (fun f -> f.watched)
        promise {
          match allWatched with
          | true ->
            let! _ = Client.unwatchDirectoryHotReload sid dir c
            ()
          | false ->
            let! _ = Client.watchDirectoryHotReload sid dir c
            ()
          refresh ()
        } |> ignore
      | _ -> ()
    )
  ctx.subscriptions.Add toggleDirCmd
