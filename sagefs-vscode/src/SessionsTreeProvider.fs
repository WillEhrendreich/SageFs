module SageFs.Vscode.SessionsTreeProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

module Client = SageFs.Vscode.SageFsClient

// ── Mutable state ────────────────────────────────────────────────

let mutable currentClient: Client.Client option = None
let mutable cachedSessions: Client.SessionInfo array = [||]
let mutable activeId: string option = None
let mutable refreshEmitter: EventEmitter<obj> option = None

// ── Helpers ──────────────────────────────────────────────────────

/// Shape one session into a row. All the decisions live in SessionsTreePure so
/// they are tested under `dotnet fsi` (tests/SessionsTreeContractTests.fsx);
/// this only adapts the wire type and guards against JS nulls.
let private rowFor (s: Client.SessionInfo) (isActive: bool) : SessionsTreePure.SessionRow =
  let paths (arr: string array) =
    match jsIsNullOrUndefined (box arr) with
    | true -> [||]
    | false -> arr |> Array.filter (fun p -> not (jsIsNullOrUndefined (box p)))
  SessionsTreePure.renderRow
    { Id = s.id
      Status = s.status
      DeclaredProjects = paths s.projects
      LoadedProjects = paths s.loadedProjects
      EvalCount = s.evalCount
      WorkingDirectory = s.workingDirectory
      IsActive = isActive
      Health = s.health }

// ── TreeDataProvider ─────────────────────────────────────────────

let getChildren (_element: obj option) : JS.Promise<obj array> =
  promise {
    match cachedSessions with
    | [||] ->
      // An empty array — not a synthesized placeholder row — is what lets
      // VS Code show `viewsWelcome` instead: a static "No sessions" row here
      // used to mask the state-aware welcome content (Start SageFs / Create
      // Session / no F# project) entirely, in every state.
      return [||]
    | sessions ->
      return
        sessions
        |> Array.map (fun s ->
          let isActive =
            match activeId with
            | Some id -> id = s.id
            | None -> false
          let row = rowFor s isActive
          let item = newTreeItem row.Label TreeItemCollapsibleState.None
          item.description <- row.Description
          // The status lives in the icon slot — VS Code prints `$(zap)` literally
          // in a label, which is what produced "$(zap) no projectReady".
          item.iconPath <- Vscode.newThemeIcon row.Icon
          item.contextValue <- row.ContextValue
          item.tooltip <- row.Tooltip
          item?accessibilityInformation <- createObj [ "label" ==> row.AccessibleName ]
          // Store session id for command args (custom property, not in VS Code API)
          item?sessionId <- s.id
          item :> obj)
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
  match currentClient with
  | Some c ->
    promise {
      let! sessions = Client.listSessions c
      cachedSessions <- sessions
      match refreshEmitter with
      | Some e -> e.fire null
      | None -> ()
    } |> promiseIgnore
  | None -> ()

let setSession (c: Client.Client) (sessionId: string option) =
  currentClient <- Some c
  activeId <- sessionId
  refresh ()

let register (ctx: ExtensionContext) =
  let provider = createProvider ()
  let tv = Window.createTreeView "sagefs-sessions" (createObj [
    "treeDataProvider" ==> provider
    "showCollapseAll" ==> false
  ])
  ctx.subscriptions.Add(tv :> obj :?> Disposable)

  let refreshCmd =
    Commands.registerCommand "sagefs.sessionsRefresh" (fun _ -> refresh ())
  ctx.subscriptions.Add refreshCmd
