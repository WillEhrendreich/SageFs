/// Runs a session's executable project end to end, shared by the dashboard
/// and MCP: resolve the target, restart into WebLive when needed (hot reload
/// installs only at worker start), ask the worker to start the entry point,
/// and record what the user should see at every step.
module SageFs.AppRunOrchestration

open System
open System.Threading.Tasks
open SageFs.AppRun
open SageFs.WorkerProtocol

let private newReplyId () = Guid.NewGuid().ToString("N").Substring(0, 8)

let private askWorker (ops: SessionManagementOps) (sessionId: SessionId) (msg: WorkerMessage) : Task<Result<AppRunState, SageFsError>> =
  task {
    let sid = SessionId.value sessionId
    match! ops.GetProxy sessionId with
    | None -> return Error (SageFsError.WorkerCommunicationFailed (sid, "the session has no running worker"))
    | Some proxy ->
      try
        match! proxy msg |> Async.StartAsTask with
        | WorkerResponse.AppRunResult (_, result) -> return result
        | other -> return Error (SageFsError.WorkerCommunicationFailed (sid, sprintf "unexpected reply %A" other))
      with ex ->
        return Error (SageFsError.WorkerCommunicationFailed (sid, ex.Message))
  }

/// Long-polls the worker until the run is no longer running, then records its
/// end. A worker that goes away needs no report: its restart resets the app.
let private watchRun (ops: SessionManagementOps) (sessionId: SessionId) (runId: string) =
  let rec poll () : Task<unit> =
    task {
      match! askWorker ops sessionId (WorkerMessage.AwaitAppChange (runId, newReplyId ())) with
      | Ok (AppRunState.Running app) when app.RunId = runId -> return! poll ()
      | Ok final -> do! ops.EndAppRun sessionId runId final
      | Error _ -> ()
    }
  Task.Run(fun () -> poll () :> Task) |> ignore

/// The ready poll caches a session's projects just after the worker reports
/// Ready, so a run pressed in that gap asks the worker itself.
let private projectsOf (ops: SessionManagementOps) (sessionId: SessionId) (info: SessionInfo) : Task<ProjectLoading.ClassifiedProject list> =
  task {
    match info.ProjectRoles with
    | _ :: _ -> return info.ProjectRoles
    | [] ->
      match! ops.GetProxy sessionId with
      | None -> return []
      | Some proxy ->
        try
          match! proxy (WorkerMessage.GetStatus (newReplyId ())) |> Async.StartAsTask with
          | WorkerResponse.StatusResult (_, snapshot) -> return snapshot.Projects
          | _ -> return []
        with _ -> return []
  }

let runApp
  (ops: SessionManagementOps)
  (clock: unit -> DateTime)
  (readyTimeout: TimeSpan)
  (sessionId: SessionId)
  (request: RunRequest)
  : Task<Result<AppRunState, SageFsError>> =
  task {
    let sid = SessionId.value sessionId
    match! ops.GetSessionInfo sessionId with
    | None -> return Error (SageFsError.SessionNotFound sid)
    | Some info ->
      let requested =
        match request with
        | RunRequest.Named name -> name
        | RunRequest.DefaultTarget -> ""
      let! projects = projectsOf ops sessionId info
      match resolveTarget request info.ActiveProject projects with
      | Error e -> return Error (SageFsError.AppRunFailed (requested, RunTargetError.describe e))
      | Ok target ->
        let project = target.Path
        match info.App with
        | AppRunState.Running app when app.Project = project -> return Ok info.App
        | AppRunState.Running app ->
          return Error (SageFsError.AppRunFailed (project, sprintf "%s is already running. → Stop it first." (projectName app.Project)))
        | AppRunState.Starting (starting, _, _) ->
          return Error (SageFsError.AppRunFailed (project, sprintf "%s is already starting. → Wait for it, then retry." (projectName starting)))
        | AppRunState.NotRunning | AppRunState.Exited _ | AppRunState.Crashed _ ->
          let fail (err: SageFsError) =
            task {
              do! ops.SetAppState sessionId (AppRunState.Crashed (project, SageFsError.describe err, clock ()))
              return Error err
            }
          let! ready =
            match info.Workflow with
            | WorkflowTypes.SessionWorkflow.WebLive _ -> Task.FromResult(Ok ())
            | WorkflowTypes.SessionWorkflow.Interactive ->
              task {
                do! ops.SetAppState sessionId (AppRunState.Starting (project, StartPhase.RestartingIntoWebLive, clock ()))
                match! ops.SwitchWorkflow sid (WorkflowTypes.SessionWorkflow.WebLive WorkflowTypes.BrowserRefreshConfig.defaults) with
                | Error e -> return Error e
                | Ok _ -> return! ops.AwaitReady sessionId readyTimeout
              }
          match ready with
          | Error e -> return! fail e
          | Ok () ->
            do! ops.SetAppState sessionId (AppRunState.Starting (project, StartPhase.LaunchingEntryPoint, clock ()))
            match! askWorker ops sessionId (WorkerMessage.RunApp (project, newReplyId ())) with
            | Error e -> return! fail e
            | Ok state ->
              do! ops.SetAppState sessionId state
              do! ops.UpdateActiveProject sessionId (Some project)
              match state with
              | AppRunState.Running app -> watchRun ops sessionId app.RunId
              | _ -> ()
              return Ok state
  }

let stopApp (ops: SessionManagementOps) (sessionId: SessionId) : Task<Result<AppRunState, SageFsError>> =
  task {
    match! askWorker ops sessionId (WorkerMessage.StopApp (newReplyId ())) with
    | Ok state ->
      do! ops.SetAppState sessionId state
      return Ok state
    | Error e -> return Error e
  }
