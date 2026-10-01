/// The MCP side of workspace hygiene: what `get_workspace_hygiene` and `tidy_workspace` say, and the one line the
/// replies an orchestrator already reads carry. The facts and the decisions are `HygieneService` and
/// `WorkspaceHygiene`; this adds what only the daemon's MCP layer knows (its sessions, who made them, which
/// connections are still around) and the words.
module SageFs.McpHygiene

open System
open System.Threading.Tasks
open SageFs
open SageFs.McpTools
open SageFs.WorkspaceHygiene
open SageFs.WorkspaceHygieneRender
open SageFs.HygieneGather

/// What the daemon knows that a scan of the disk cannot: its sessions, who created them, who is still connected.
let liveFactsOf (ctx: McpContext) : Task<LiveFacts> =
  task {
    let! sessions = ctx.SessionOps.GetAllSessions()
    let pairs = sessions |> List.map (fun s -> WorkerProtocol.SessionId.value s.Id, s.WorkingDirectory)
    return HygieneService.liveFactsWith pairs (Some ctx.ActivityTracker)
  }

/// The repository a call is about: the working directory it names, else the one repository its live sessions are in.
let resolveRepo (ctx: McpContext) (workingDirectory: string) : Task<Result<string, string>> =
  task {
    match String.IsNullOrWhiteSpace workingDirectory with
    | false ->
      match HygieneService.mainRepoOf workingDirectory with
      | Some repo -> return Result.Ok repo
      | None -> return Result.Error(sprintf "%s is not inside a git checkout, so there is no repository to look at." workingDirectory)
    | true ->
      let! sessions = ctx.SessionOps.GetAllSessions()
      let repos = sessions |> List.choose (fun s -> HygieneService.mainRepoOf s.WorkingDirectory) |> List.distinct
      match repos with
      | [ repo ] -> return Result.Ok repo
      | [] -> return Result.Error "Pass working_directory (a path inside the repository): no session says which repository to look at."
      | many -> return Result.Error(sprintf "Pass working_directory: sessions are in several repositories (%s)." (String.Join(", ", many)))
  }

/// Scan, plan and say. A dry run: nothing is touched.
let getWorkspaceHygieneAt (locate: string -> Locations) (ctx: McpContext) (workingDirectory: string) : Task<string> =
  task {
    match! resolveRepo ctx workingDirectory with
    | Result.Error message -> return sprintf "Error: %s" message
    | Result.Ok repo ->
      let! live = liveFactsOf ctx
      let loc = locate repo
      let snapshot = HygieneService.take loc live
      HygieneService.Cache.put snapshot
      return renderPlan snapshot.Leftovers snapshot.Plan
  }

/// Run the Safe steps of the plan the caller was shown. Needs `confirm` and the plan id it was shown; a plan that
/// changed since is refused with the new one's id.
let getWorkspaceHygiene (ctx: McpContext) (workingDirectory: string) : Task<string> =
  getWorkspaceHygieneAt HygieneService.locationsFor ctx workingDirectory

let tidyWorkspaceAt (locate: string -> Locations) (ctx: McpContext) (workingDirectory: string) (planId: string) (confirm: bool) : Task<string> =
  task {
    match String.IsNullOrWhiteSpace planId, confirm with
    | true, _
    | _, false ->
      return "Error: tidy_workspace runs only what get_workspace_hygiene showed you. Call it first, then pass confirm=true and its plan id as `plan`. Nothing was touched."
    | false, true ->
      match! resolveRepo ctx workingDirectory with
      | Result.Error message -> return sprintf "Error: %s" message
      | Result.Ok repo ->
        let loc = locate repo
        let! live = liveFactsOf ctx
        let outcome = HygieneService.tidy loc (fun () -> live) (PlanId planId)
        match outcome with
        | HygieneService.TidyOutcome.Tidied(report, snapshot) ->
          return sprintf "%s\n\n%s" (renderReport report) (renderPlan snapshot.Leftovers snapshot.Plan)
        | HygieneService.TidyOutcome.NotConfirmed(ConfirmError.PlanChanged(shown, current), plan) ->
          let (PlanId s) = shown
          let (PlanId c) = current
          return sprintf "Error: plan %s is not the plan that exists now (%s): the workspace changed since you looked. Nothing was touched. Call get_workspace_hygiene again, read the new plan, and pass its id as `plan`.\n(%d steps now, %s safe to reclaim)" s c (List.length plan.Steps) (formatBytes plan.SafeBytes)
  }

let tidyWorkspace (ctx: McpContext) (workingDirectory: string) (planId: string) (confirm: bool) : Task<string> =
  tidyWorkspaceAt HygieneService.locationsFor ctx workingDirectory planId confirm

/// Make the cached snapshot for a repo current, in the background. Event-driven: called when something that
/// changes the answer happened (a session created or stopped, the daemon started), never on a timer.
let refreshSoon (ctx: McpContext) (workingDirectory: string) : unit =
  // Only a running daemon scans the machine; a context built by a test does not.
  match ctx.Dispatch, HygieneService.mainRepoOf workingDirectory with
  | Some _, Some repo ->
    let loc = HygieneService.locationsFor repo
    HygieneService.Cache.refresh loc (fun () -> (liveFactsOf ctx).GetAwaiter().GetResult()) |> ignore
  | _ -> ()

/// The line for a reply about a session in `workingDirectory`: the nudge from the cached snapshot, and a refresh in
/// the background when there is none yet.
let hygieneLine (ctx: McpContext) (workingDirectory: string) : string option =
  match ctx.Dispatch, HygieneService.mainRepoOf workingDirectory with
  | Some _, Some repo ->
    match HygieneService.Cache.tryGet repo with
    | Some snapshot -> nudge snapshot.Summary
    | None ->
      refreshSoon ctx workingDirectory
      None
  | _ -> None

/// Refresh the snapshot of every repository a live session is in: called when a session is stopped.
let refreshForSessions (ctx: McpContext) : unit =
  task {
    let! sessions = ctx.SessionOps.GetAllSessions()
    for dir in sessions |> List.map (fun s -> s.WorkingDirectory) |> List.distinct do
      refreshSoon ctx dir
  }
  |> ignore

/// A reply with the hygiene line under it, when there is one.
let withHygieneLine (ctx: McpContext) (workingDirectory: string) (reply: string) : string =
  match hygieneLine ctx workingDirectory with
  | Some line -> reply + "\n\n" + line
  | None -> reply

/// A JSON reply with a `workspace` field carrying the lines, when there are any. A reply that is not a JSON object
/// is left exactly as it was.
let private withWorkspaceField (lines: string list) (json: string) : string =
  match lines with
  | [] -> json
  | _ ->
    try
      match System.Text.Json.Nodes.JsonNode.Parse json with
      | :? System.Text.Json.Nodes.JsonObject as o ->
        o.["workspace"] <- System.Text.Json.Nodes.JsonValue.Create(String.Join("\n", lines))
        o.ToJsonString()
      | _ -> json
    with _ -> json

/// `get_session_status` with the line for the repository that session is in.
let withHygieneForSession (ctx: McpContext) (sessionId: string) (workingDirectory: string) (json: string) : Task<string> =
  task {
    let! sessions = ctx.SessionOps.GetAllSessions()
    let wanted =
      match String.IsNullOrWhiteSpace sessionId, String.IsNullOrWhiteSpace workingDirectory with
      | false, _ -> sessionId
      | true, false -> ""
      | true, true -> McpTools.activeSessionId ctx "mcp"
    let directory =
      match String.IsNullOrWhiteSpace workingDirectory with
      | false -> workingDirectory
      | true ->
        match sessions |> List.tryFind (fun s -> WorkerProtocol.SessionId.value s.Id = wanted) with
        | Some s -> s.WorkingDirectory
        | None -> ""
    return
      match String.IsNullOrWhiteSpace directory with
      | true -> json
      | false -> withWorkspaceField (hygieneLine ctx directory |> Option.toList) json
  }

/// `get_daemon_status` with a line for every repository its sessions are in that has something to say.
let withHygieneForDaemon (ctx: McpContext) (json: string) : Task<string> =
  task {
    let! sessions = ctx.SessionOps.GetAllSessions()
    let lines =
      sessions
      |> List.choose (fun s -> HygieneService.mainRepoOf s.WorkingDirectory)
      |> List.distinct
      |> List.choose (fun repo -> hygieneLine ctx repo)
    return withWorkspaceField lines json
  }
