/// Giving a lemming its own session, and proving the window is attached to it. The extension
/// binds a new window to the head of the shared daemon's session list (or to the one a person
/// picks), so a lemming that just pressed Alt+Enter evaluated in another agent's session. The
/// runner now creates a session for the run's own `w` directory through the daemon's HTTP API,
/// waits for it, has the window pick it in the extension's own session picker, and reads the
/// Sessions view to see that the active row is the run's. If it cannot prove that, the run
/// does not start. The decisions are here, pure; the HTTP and the window are in VscHost.
module LemDrive.Binding

open System
open System.Text.Json

/// The project the run's session loads: the tests project when the fixture has one (it
/// brings the library with it, so one session serves the eval, edit, live-test and
/// hot-reload tasks), else the first project. Paths are relative to the workspace.
let projectFor (projectFiles: string list) : string option =
  match projectFiles |> List.tryFind (fun f -> f.EndsWith(".Tests.fsproj", StringComparison.Ordinal)) with
  | Some tests -> Some tests
  | None -> List.tryHead projectFiles

/// The body of POST /api/sessions/create for the run's workspace.
let createBody (workspace: string) (project: string option) : string =
  let root = workspace.TrimEnd('/')
  let projects =
    match project with
    | Some rel -> [ root + "/" + rel ]
    | None -> []
  JsonSerializer.Serialize {| workingDirectory = root; projects = projects |}

/// What the session's status says about waiting for it.
type Readiness =
  | Ready
  | Waiting
  | Refused of reason: string

/// The daemon's status words: Ready and Evaluating are usable; Faulted, Stopped and a host
/// crash will not become so; everything else (Starting, Restarting, Warming Up, Building,
/// Disconnected for a moment) is worth waiting through.
let readiness (s: Daemon.DaemonSession) : Readiness =
  match s.Status with
  | "Ready"
  | "Evaluating" -> Ready
  | "Faulted"
  | "Stopped"
  | "HostCrashed" -> Refused(sprintf "the run's session %s is %s" s.Id s.Status)
  | _ -> Waiting

/// Does a Sessions-view row mark its session as the active one.
let private isActiveRow (row: string) : bool = row.Contains "— active"

/// The window is attached to the run's session when the Sessions view lists as many rows as the
/// daemon lists sessions, and the active row is the one at the run's session's place in the
/// daemon's list (the view is that list, in order). Says which row is wrong when it is not.
let boundTo (sessions: Daemon.DaemonSession list) (mine: string) (rows: string list) : Result<unit, string> =
  match sessions |> List.tryFindIndex (fun s -> s.Id = mine) with
  | None -> Result.Error(sprintf "the daemon does not list the run's session %s" mine)
  | Some index ->
    match List.length rows = List.length sessions with
    | false ->
      Result.Error(sprintf "the Sessions view shows %d row(s) and the daemon lists %d session(s); the view has not caught up" (List.length rows) (List.length sessions))
    | true ->
      match rows |> List.tryFindIndex isActiveRow with
      | Some active when active = index -> Ok()
      | Some active -> Result.Error(sprintf "the active row is row %d, but the run's session is row %d" (active + 1) (index + 1))
      | None -> Result.Error "no row of the Sessions view is marked active"
