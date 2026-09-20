module SageFs.Vscode.HotReloadTreeProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

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
let mutable isLoading: bool = false

// ── Path helpers ─────────────────────────────────────────────────

let getDirectory (path: string) =
  match jsIsNullOrUndefined (box path) with
  | true -> ""
  | false ->
    let normalized = path.Replace('\\', '/')
    match normalized.LastIndexOf('/') with
    | -1 -> ""
    | i -> normalized.Substring(0, i)

let getFileName (path: string) =
  match jsIsNullOrUndefined (box path) with
  | true -> ""
  | false ->
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
  item.contextValue <- "directory"
  item.description <- sprintf "%d/%d watched" watchedCount childCount
  item.iconPath <- Vscode.newThemeIcon "folder"
  // No `command`: in VS Code a tree-row click means select/expand, not mutate.
  // Toggling the whole directory is an explicit inline action
  // (contributes.menus view/item/context, group "inline").
  item

let createFileItem (file: Client.HotReloadFile) =
  let label = getFileName file.path
  let item = newTreeItem label TreeItemCollapsibleState.None
  let ctxVal, desc, iconColor =
    match file.watched with
    | true -> "watchedFile", "● watching", "testing.iconPassed"
    | false -> "unwatchedFile", "○ not watching", "testing.iconSkipped"
  item.contextValue <- ctxVal
  item.description <- desc
  item.tooltip <- file.path
  // WHY the checkbox, and why the click no longer toggles: a single click on a
  // row used to flip the watch flag, so there was no way to LOOK at a file row
  // without changing it — and the view contributed zero inline actions, so
  // there was no other way to change it either. In VS Code a row click means
  // select/open; an on/off state belongs on `checkboxState`. Clicking the row
  // now opens the file, which is what a file row does everywhere else in the
  // editor; the checkbox carries the watch flag.
  item?checkboxState <-
    createObj [
      "state" ==> (match file.watched with true -> TreeItemCheckboxState.Checked | false -> TreeItemCheckboxState.Unchecked)
      "tooltip" ==> (match file.watched with true -> "Watched — untick to stop hot-reloading this file" | false -> "Not watched — tick to hot-reload this file")
    ]
  item?sagefsPath <- file.path
  item.command <-
    createObj [
      "command" ==> "vscode.open"
      "title" ==> "Open File"
      "arguments" ==> [| box (uriFile file.path) |]
    ]
  item.iconPath <-
    match file.watched with
    | true -> Vscode.newThemeIcon "eye"
    | false -> Vscode.newThemeIcon "eye-closed"
  item

let groupByDirectory (files: Client.HotReloadFile array) =
  files
  |> Array.groupBy (fun f -> getDirectory f.path)
  |> Array.sortBy fst

let getChildren (element: obj option) : JS.Promise<obj array> =
  promise {
    match element with
    | None ->
      match isLoading with
      | true ->
        // A codicon token in TreeItem.label renders literally — VS Code only
        // expands `$(...)` in iconPath (or a MarkdownString/status bar text).
        let item = newTreeItem "Loading..." TreeItemCollapsibleState.None
        item.iconPath <- Vscode.newThemeIcon "loading~spin"
        return [| item :> obj |]
      | false ->
      let groups = groupByDirectory cachedFiles
      match groups with
      | [||] ->
        // Empty, not a synthesized placeholder row — lets `viewsWelcome`
        // (gated on sagefs:daemonRunning / sagefs:hasSession) show instead.
        return [||]
      | [| (_, files) |] ->
        return files |> Array.map (fun f -> createFileItem f :> obj)
      | _ ->
        return
          groups
          |> Array.map (fun (dir, files) ->
            let watchedCount = files |> Array.filter (fun f -> f.watched) |> Array.length
            createDirItem dir files.Length watchedCount :> obj)
    | Some el ->
      let ctx = fieldString "contextValue" el |> Option.defaultValue ""
      match ctx with
      | "directory" ->
        let label = fieldString "label" el |> Option.defaultValue ""
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
    c.log (sprintf "[hotreload] refresh: sessionId=%s" sid)
    isLoading <- true
    match refreshEmitter with Some e -> e.fire null | None -> ()
    promise {
      let! state = Client.getHotReloadState sid c
      match state with
      | Some s ->
        c.log (sprintf "[hotreload] got %d files" s.files.Length)
        cachedFiles <- s.files
      | None ->
        c.log "[hotreload] getHotReloadState returned None"
        cachedFiles <- [||]
      isLoading <- false
      // "Session exists, no files yet" is a real state with its own remedy and
      // must not render as an indistinguishable blank panel — see
      // contributes.viewsWelcome.
      Vscode.setContextKey "sagefs:hotReloadLoaded" (state.IsSome && (not (Array.isEmpty cachedFiles)))
      match refreshEmitter with
      | Some e -> e.fire null
      | None -> ()
    } |> promiseIgnore
  | c, sid ->
    isLoading <- false
    cachedFiles <- [||]
    Vscode.setContextKey "sagefs:hotReloadLoaded" false
    match refreshEmitter with
    | Some e -> e.fire null
    | None -> ()

let setSession (c: Client.Client) (sessionId: string option) =
  // Skip redundant calls — avoids timer churn from periodic refreshStatus
  match currentSessionId = sessionId with
  | true ->
    currentClient <- Some c
    ()
  | false ->
    currentClient <- Some c
    currentSessionId <- sessionId
    match autoRefreshTimer with
    | Some t -> jsClearInterval t; autoRefreshTimer <- None
    | None -> ()
    match sessionId with
    | Some _ ->
      autoRefreshTimer <- Some (jsSetInterval (fun () -> refresh ()) 30000)
    | None -> ()
    refresh ()

let stopAutoRefresh () =
  match autoRefreshTimer with
  | Some t -> jsClearInterval t; autoRefreshTimer <- None
  | None -> ()

/// Every hot-reload mutation used to be `let! _ = Client.…`, discarding an
/// `ApiOutcome` that is `Succeeded | Failed of error`. A rejected toggle
/// produced no dialog, no status line, not even an output-channel warning: the
/// tree re-rendered unchanged and the user concluded the control was broken.
/// (Neovim has the identical defect one step worse — it reports success on the
/// failure branch. Same subsystem, both editors, independently.)
///
/// One reporter, so a new mutation cannot forget.
let private reportOutcome (what: string) (outcome: Client.ApiOutcome) =
  match outcome with
  | Client.Succeeded _ -> ()
  | Client.Failed err ->
    Window.showWarningMessage (sprintf "%s failed: %s" what err) [||] |> ignore

let register (ctx: ExtensionContext) =
  let provider = createProvider ()
  let tv = Window.createTreeView "sagefs-hotReload" (createObj [
    "treeDataProvider" ==> provider
    "showCollapseAll" ==> true
  ])
  treeView <- Some tv
  ctx.subscriptions.Add(tv :> obj :?> Disposable)

  // The checkbox IS the watch flag now. VS Code hands back the rows whose
  // state changed, so one tick/untick is one toggle — and the outcome is
  // reported, unlike the click handler it replaces.
  let checkboxSub =
    tv.onDidChangeCheckboxState (fun ev ->
      match currentClient, currentSessionId with
      | Some c, Some sid ->
        let pairs: obj array = try unbox (ev?items) with _ -> [||]
        promise {
          for pair in pairs do
            // Each entry is [item, newState].
            let item = try (unbox<obj array> pair).[0] with _ -> pair
            let path = tryCastString (item?sagefsPath) |> Option.defaultValue ""
            match path with
            | "" -> ()
            | p ->
              let! outcome = Client.toggleHotReload sid p c
              reportOutcome (sprintf "Toggling hot reload for %s" (getFileName p)) outcome
          refresh ()
        } |> promiseIgnore
      | _ -> ()
    )
  ctx.subscriptions.Add checkboxSub

  // Toggle command — still registered, because the inline action and the
  // palette both route through it.
  let toggleCmd =
    Commands.registerCommand "sagefs.hotReloadToggle" (fun arg ->
      match currentClient, currentSessionId with
      | Some c, Some sid ->
        // From an inline tree action the argument is the TreeItem, not a
        // string; from the palette it is neither.
        let path =
          tryCastString arg
          |> Option.orElseWith (fun () -> tryCastString (arg?sagefsPath))
          |> Option.defaultValue ""
        promise {
          match path with
          | "" ->
            Window.showWarningMessage "Open an F# file, or pick one in the Hot Reload view, to toggle it." [||] |> ignore
          | p ->
            let! outcome = Client.toggleHotReload sid p c
            reportOutcome (sprintf "Toggling hot reload for %s" (getFileName p)) outcome
            refresh ()
        } |> promiseIgnore
      | _ -> ()
    )
  ctx.subscriptions.Add toggleCmd

  // Watch All command
  let watchAllCmd =
    Commands.registerCommand "sagefs.hotReloadWatchAll" (fun _ ->
      match currentClient, currentSessionId with
      | Some c, Some sid ->
        promise {
          // "Watch All with zero files is a silent no-op" — say so instead.
          match cachedFiles with
          | [||] ->
            Window.showInformationMessage
              "No hot-reloadable files in this session yet.\n→ Warmup lists the session's source files; if it found none, the project may not have been built."
              [||]
            |> ignore
          | _ ->
            let! outcome = Client.watchAllHotReload sid c
            reportOutcome "Watch All" outcome
            refresh ()
        } |> promiseIgnore
      | _ -> ()
    )
  ctx.subscriptions.Add watchAllCmd

  // Unwatch All command
  let unwatchAllCmd =
    Commands.registerCommand "sagefs.hotReloadUnwatchAll" (fun _ ->
      match currentClient, currentSessionId with
      | Some c, Some sid ->
        promise {
          match cachedFiles with
          | [||] ->
            Window.showInformationMessage "No hot-reloadable files in this session yet." [||] |> ignore
          | _ ->
            let! outcome = Client.unwatchAllHotReload sid c
            reportOutcome "Unwatch All" outcome
            refresh ()
        } |> promiseIgnore
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
        // From an inline tree action the argument is the TreeItem whose label
        // is the directory; from a string argument it is the path itself.
        let dir =
          tryCastString arg
          |> Option.orElseWith (fun () ->
            tryCastString (arg?label) |> Option.map (function "(root)" -> "" | d -> d))
          |> Option.defaultValue ""
        let allWatched: bool =
          cachedFiles
          |> Array.filter (fun f -> getDirectory f.path = dir)
          |> Array.forall (fun f -> f.watched)
        promise {
          let! outcome =
            match allWatched with
            | true -> Client.unwatchDirectoryHotReload sid dir c
            | false -> Client.watchDirectoryHotReload sid dir c
          reportOutcome (sprintf "Toggling hot reload for %s" (match dir with "" -> "(root)" | d -> d)) outcome
          refresh ()
        } |> promiseIgnore
      | _ -> ()
    )
  ctx.subscriptions.Add toggleDirCmd
