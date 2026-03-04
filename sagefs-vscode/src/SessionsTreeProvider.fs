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

let private stripExt (name: string) =
  match name with
  | n when n.EndsWith(".fsproj") -> n.[..n.Length - 8]
  | n when n.EndsWith(".slnx") -> n.[..n.Length - 6]
  | n when n.EndsWith(".sln") -> n.[..n.Length - 5]
  | n -> n

let private projectLabel (s: Client.SessionInfo) =
  match s.projects with
  | [||] -> "no project"
  | ps ->
    ps
    |> Array.choose (fun p ->
      match jsIsNullOrUndefined (box p) with
      | true -> None
      | false -> p.Split([|'/'; '\\'|]) |> Array.last |> stripExt |> Some)
    |> String.concat ", "

let private statusIcon (status: string) =
  match status with
  | "Ready" | "Evaluating" -> "$(zap)"
  | "Starting" | "Restarting" -> "$(loading~spin)"
  | "Faulted" -> "$(error)"
  | "Stopped" -> "$(circle-slash)"
  | _ -> "$(question)"

// ── TreeDataProvider ─────────────────────────────────────────────

let getChildren (_element: obj option) : JS.Promise<obj array> =
  promise {
    match cachedSessions with
    | [||] ->
      let item = newTreeItem "No sessions" TreeItemCollapsibleState.None
      item.description <- "Create one with $(add) above"
      item.iconPath <- Vscode.newThemeIcon "info"
      return [| item :> obj |]
    | sessions ->
      return
        sessions
        |> Array.map (fun s ->
          let isActive =
            match activeId with
            | Some id -> id = s.id
            | None -> false
          let label = sprintf "%s %s" (statusIcon s.status) (projectLabel s)
          let item = newTreeItem label TreeItemCollapsibleState.None
          item.description <-
            match s.evalCount with
            | 0 -> s.status
            | n -> sprintf "%s [%d evals]" s.status n
          item.iconPath <-
            match isActive with
            | true -> Vscode.newThemeIcon "star-full"
            | false -> Vscode.newThemeIcon "terminal"
          item.contextValue <-
            match isActive, s.status with
            | true, "Ready" -> "session-active-ready"
            | true, _ -> "session-active"
            | false, "Stopped" -> "session-stopped"
            | false, _ -> "session-inactive"
          item.tooltip <-
            sprintf "ID: %s\nStatus: %s\nProject: %s\nEvals: %d"
              s.id s.status (projectLabel s) s.evalCount
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
