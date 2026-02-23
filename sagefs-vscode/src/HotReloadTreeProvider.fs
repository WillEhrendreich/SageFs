module SageFs.Vscode.HotReloadTreeProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode

module Client = SageFs.Vscode.SageFsClient

// ── Types ────────────────────────────────────────────────────────

type HotReloadItem =
  { path: string
    watched: bool
    isDirectory: bool
    children: HotReloadItem array }

// ── Mutable state ────────────────────────────────────────────────

let mutable private currentClient: Client.Client option = None
let mutable private currentSessionId: string option = None
let mutable private cachedFiles: Client.HotReloadFile array = [||]
let mutable private refreshEmitter: EventEmitter<obj> option = None
let mutable private treeView: TreeView<obj> option = None

// ── Path helpers ─────────────────────────────────────────────────

let private getDirectory (path: string) =
  if isNull path then ""
  else
    let normalized = path.Replace('\\', '/')
    match normalized.LastIndexOf('/') with
    | -1 -> ""
    | i -> normalized.Substring(0, i)

let private getFileName (path: string) =
  if isNull path then ""
  else
    let normalized = path.Replace('\\', '/')
    match normalized.LastIndexOf('/') with
    | -1 -> normalized
    | i -> normalized.Substring(i + 1)

// ── TreeDataProvider ─────────────────────────────────────────────

let private createDirItem (dirPath: string) (childCount: int) (watchedCount: int) =
  let label = if dirPath = "" then "(root)" else dirPath
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

let private createFileItem (file: Client.HotReloadFile) =
  let label = getFileName file.path
  let item = newTreeItem label TreeItemCollapsibleState.None
  item?contextValue <- if file.watched then "watchedFile" else "unwatchedFile"
  item?description <- if file.watched then "● watching" else "○ not watching"
  item?tooltip <- file.path
  item?command <-
    createObj [
      "command" ==> "sagefs.hotReloadToggle"
      "title" ==> "Toggle Hot Reload"
      "arguments" ==> [| file.path |]
    ]
  item?iconPath <-
    if file.watched then
      Vscode.newThemeColor "testing.iconPassed"
    else
      Vscode.newThemeColor "testing.iconSkipped"
  item

let private groupByDirectory (files: Client.HotReloadFile array) =
  files
  |> Array.groupBy (fun f -> getDirectory f.path)
  |> Array.sortBy fst

let private getChildren (element: obj option) : JS.Promise<obj array> =
  promise {
    match element with
    | None ->
      let groups = groupByDirectory cachedFiles
      if groups.Length = 0 then
        let item = newTreeItem "No session active" TreeItemCollapsibleState.None
        item?description <- "Start a session to manage hot reload"
        return [| item :> obj |]
      elif groups.Length = 1 then
        let (_, files) = groups.[0]
        return files |> Array.map (fun f -> createFileItem f :> obj)
      else
        return
          groups
          |> Array.map (fun (dir, files) ->
            let watchedCount = files |> Array.filter (fun f -> f.watched) |> Array.length
            createDirItem dir files.Length watchedCount :> obj)
    | Some el ->
      let ctx: string = el?contextValue |> unbox
      if ctx = "directory" then
        let label: string = el?label |> unbox
        let dir = if label = "(root)" then "" else label
        let files =
          cachedFiles
          |> Array.filter (fun f -> getDirectory f.path = dir)
        return files |> Array.map (fun f -> createFileItem f :> obj)
      else
        return [||]
  }

let private getTreeItem (element: obj) : obj = element

let private createProvider () =
  let emitter = newEventEmitter<obj> ()
  refreshEmitter <- Some emitter
  createObj [
    "onDidChangeTreeData" ==> emitter.event
    "getChildren" ==> fun (el: obj) ->
      let elOpt = if isNull el then None else Some el
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
        let path: string = arg |> unbox
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
        let dir: string = arg |> unbox
        let allWatched: bool =
          cachedFiles
          |> Array.filter (fun f -> getDirectory f.path = dir)
          |> Array.forall (fun f -> f.watched)
        promise {
          if allWatched then
            let! _ = Client.unwatchDirectoryHotReload sid dir c
            ()
          else
            let! _ = Client.watchDirectoryHotReload sid dir c
            ()
          refresh ()
        } |> ignore
      | _ -> ()
    )
  ctx.subscriptions.Add toggleDirCmd
