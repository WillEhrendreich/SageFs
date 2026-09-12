/// Runs a session's executable project end to end, shared by the dashboard
/// and MCP: resolve the target, restart into WebLive when needed (hot reload
/// installs only at worker start), ask the worker to start the entry point,
/// and record what the user should see at every step.
///
/// The session's owner (the SessionManager mailbox) decides every Run and Stop
/// and hands each accepted Run a generation. Every step recorded here carries
/// that generation, so once a later Stop or Run claims the app, nothing this
/// run still has in flight can be applied — a Stop cannot be lost to a
/// rebuild that finishes after it, and two Runs cannot both start the app.
module SageFs.AppRunOrchestration

open System
open System.Threading.Tasks
open SageFs.AppRun
open SageFs.WorkerProtocol

let private newReplyId () = Guid.NewGuid().ToString("N").Substring(0, 8)

/// What must happen before the entry point can start, given where the session is.
let startPhaseFor (status: SessionLifecycleStatus) (workflow: WorkflowTypes.SessionWorkflow) : StartPhase =
  match status, workflow with
  // No worker (its last build failed, or it was stopped): Run rebuilds it first.
  | (SessionLifecycleStatus.Faulted _ | SessionLifecycleStatus.Stopped), _ -> StartPhase.RebuildingSession
  | _, WorkflowTypes.SessionWorkflow.WebLive _ -> StartPhase.LaunchingEntryPoint
  | _, WorkflowTypes.SessionWorkflow.Interactive -> StartPhase.RestartingIntoWebLive

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

/// How a failed start reads on the card: a failed build is not a crash, and
/// nothing else on this path ever ran, so it is not a crash either — the app
/// never started.
let private failedState (project: string) (previous: PreviousAddress) (err: SageFsError) (at: DateTime) =
  match err with
  | SageFsError.BuildFailed(_, diagnostics) -> AppRunState.BuildFailed (project, BuildDiagnostic.describe diagnostics, at, previous)
  | other -> AppRunState.CouldNotStart (project, other, at)

/// Starts the entry point on a Ready worker, records what happened, and
/// watches a running app until its run ends. A run a later Stop or Run has
/// superseded answers with the app state the owner holds now.
let rec private launch
  (ops: SessionManagementOps)
  (clock: unit -> DateTime)
  (readyTimeout: TimeSpan)
  (sessionId: SessionId)
  (generation: RunGeneration)
  (project: string)
  (previous: PreviousAddress)
  : Task<Result<AppRunState, SageFsError>> =
  task {
    match! ops.AdvanceRun sessionId generation (AppRunState.Starting (project, StartPhase.LaunchingEntryPoint, clock ())) with
    | StepOutcome.Stale current -> return Ok current
    | StepOutcome.Applied ->
      match! askWorker ops sessionId (WorkerMessage.RunApp (project, previous, newReplyId ())) with
      | Error e ->
        match! ops.AdvanceRun sessionId generation (failedState project previous e (clock ())) with
        | StepOutcome.Applied -> return Error e
        | StepOutcome.Stale current -> return Ok current
      | Ok state ->
        match! ops.AdvanceRun sessionId generation state with
        | StepOutcome.Applied ->
          match state with
          | AppRunState.Running app -> watchRun ops clock readyTimeout sessionId generation app
          | _ -> ()
          return Ok state
        | StepOutcome.Stale current ->
          // Stop was pressed while the worker started this app: nobody owns it,
          // so end it — only it, never a run someone started since.
          match state with
          | AppRunState.Running app ->
            let! _ = askWorker ops sessionId (WorkerMessage.StopApp (StopScope.OnlyRun app.RunId, newReplyId ()))
            ()
          | _ -> ()
          return Ok current
  }

/// Long-polls the worker until the run ends, then records its end. A run ended
/// for changes is rebuilt and relaunched where it listened, if its Run still
/// owns the app. A worker that goes away needs no report: its restart resets the app.
and private watchRun
  (ops: SessionManagementOps)
  (clock: unit -> DateTime)
  (readyTimeout: TimeSpan)
  (sessionId: SessionId)
  (generation: RunGeneration)
  (app: RunningApp)
  : unit =
  // A watch that cannot go on ends the run truthfully: the app may still be
  // serving, but this daemon no longer knows, and the card must say so.
  let lostTrack (reason: SageFsError) : Task<unit> =
    task {
      let! _ = ops.EndAppRun sessionId generation app.RunId (AppRunState.LostTrack (app.Project, reason, clock ()))
      ()
    }
  let rec poll () : Task<unit> =
    task {
      match! askWorker ops sessionId (WorkerMessage.AwaitAppChange (app.RunId, newReplyId ())) with
      | Ok (AppRunState.Running current) when current.RunId = app.RunId -> return! poll ()
      | Ok final ->
        match! ops.EndAppRun sessionId generation app.RunId final with
        | RunEnd.RebuildForChanges (project, previous) ->
          do! restartForChanges ops clock readyTimeout sessionId generation project previous
        | RunEnd.Recorded
        | RunEnd.NotCurrent -> ()
      | Error reason -> do! lostTrack reason
    }
  let watch () : Task<unit> =
    task {
      try
        do! poll ()
      with ex ->
        // Nothing else observes this task, so its failure is reported here and
        // the run is ended with it rather than left Running forever.
        Utils.Log.warn "[AppRunOrchestration] Watching %s's app failed: %s" (SessionId.value sessionId) ex.Message
        try do! lostTrack (SageFsError.Unexpected ex)
        with inner ->
          Utils.Log.error "[AppRunOrchestration] Could not record that %s's app was lost: %s" (SessionId.value sessionId) inner.Message
    }
  let watching = Task.Run(fun () -> watch () :> Task)
  watching.ContinueWith(
    (fun (t: Task) ->
      match t.Exception with
      | null -> ()
      | ex -> Utils.Log.error "[AppRunOrchestration] The app watch task faulted: %s" ex.Message),
    TaskContinuationOptions.OnlyOnFaulted)
  |> ignore

and private restartForChanges
  (ops: SessionManagementOps)
  (clock: unit -> DateTime)
  (readyTimeout: TimeSpan)
  (sessionId: SessionId)
  (generation: RunGeneration)
  (project: string)
  (previous: PreviousAddress)
  : Task<unit> =
  task {
    let! restarted = ops.RestartSession sessionId true
    let! ready =
      match restarted with
      | Error e -> Task.FromResult(Error e)
      | Ok _ -> ops.AwaitReady sessionId readyTimeout
    match ready with
    | Error e ->
      let! _ = ops.AdvanceRun sessionId generation (failedState project previous e (clock ()))
      ()
    | Ok () ->
      let! _ = launch ops clock readyTimeout sessionId generation project previous
      ()
  }

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
        match! ops.ClaimRun sessionId project with
        | Error e -> return Error e
        | Ok (RunClaim.AlreadyRunning app) when app.Project = project -> return Ok (AppRunState.Running app)
        | Ok (RunClaim.AlreadyRunning app) ->
          return Error (SageFsError.AppRunFailed (project, sprintf "%s is already running. → Stop it first." (projectName app.Project)))
        | Ok (RunClaim.AlreadyStarting (starting, _)) ->
          return Error (SageFsError.AppRunFailed (project, sprintf "%s is already starting. → Wait for it, then retry." (projectName starting)))
        | Ok (RunClaim.Begun (generation, phase, previous)) ->
          let! ready =
            match phase with
            | StartPhase.RebuildingSession ->
              task {
                match! ops.RestartSession sessionId true with
                | Error e -> return Error e
                | Ok _ -> return! ops.AwaitReady sessionId readyTimeout
              }
            | StartPhase.RestartingIntoWebLive ->
              task {
                match! ops.SwitchWorkflow sid (WorkflowTypes.SessionWorkflow.WebLive WorkflowTypes.BrowserRefreshConfig.defaults) with
                | Error e -> return Error e
                | Ok _ -> return! ops.AwaitReady sessionId readyTimeout
              }
            | StartPhase.LaunchingEntryPoint
            | StartPhase.RebuildingForChanges _ -> Task.FromResult(Ok ())
          match ready with
          | Error e ->
            match! ops.AdvanceRun sessionId generation (failedState project previous e (clock ())) with
            | StepOutcome.Applied -> return Error e
            | StepOutcome.Stale current -> return Ok current
          | Ok () -> return! launch ops clock readyTimeout sessionId generation project previous
  }

/// Stop claims the app first, so no step of an earlier run lands after it,
/// then asks the worker. A start still being prepared is cancelled on the spot.
let stopApp (ops: SessionManagementOps) (sessionId: SessionId) : Task<Result<AppRunState, SageFsError>> =
  task {
    match! ops.ClaimStop sessionId with
    | Error e -> return Error e
    | Ok (StopClaim.CancelledStart (_, phase)) ->
      match phase with
      // The worker may be starting the app right now: tell it to stop too.
      | StartPhase.LaunchingEntryPoint ->
        let! _ = askWorker ops sessionId (WorkerMessage.StopApp (StopScope.CurrentApp, newReplyId ()))
        ()
      | StartPhase.RestartingIntoWebLive
      | StartPhase.RebuildingSession
      | StartPhase.RebuildingForChanges _ -> ()
      return Ok AppRunState.NotRunning
    | Ok (StopClaim.StopWorkerApp generation) ->
      match! askWorker ops sessionId (WorkerMessage.StopApp (StopScope.CurrentApp, newReplyId ())) with
      | Error e -> return Error e
      | Ok state ->
        match! ops.AdvanceRun sessionId generation state with
        | StepOutcome.Applied -> return Ok state
        | StepOutcome.Stale current -> return Ok current
  }
