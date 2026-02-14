namespace SageFs

open System
open System.Diagnostics
open System.Threading
open SageFs.WorkerProtocol

/// Manages worker sub-processes, each owning an FSI session.
/// Erlang-style supervisor: spawn, monitor, restart on crash.
module SessionManager =

  type ManagedSession = {
    Info: SessionInfo
    Process: Process
    Proxy: SessionProxy
    PipeDisposable: IDisposable
    /// Original spawn config — needed for restart.
    Projects: string list
    WorkingDir: string
    /// Per-session restart tracking.
    RestartState: RestartPolicy.State
  }

  [<RequireQualifiedAccess>]
  type SessionCommand =
    | CreateSession of
        projects: string list *
        workingDir: string *
        AsyncReplyChannel<Result<SessionInfo, SageFsError>>
    | StopSession of
        SessionId *
        AsyncReplyChannel<Result<unit, SageFsError>>
    | GetSession of
        SessionId *
        AsyncReplyChannel<ManagedSession option>
    | ListSessions of
        AsyncReplyChannel<SessionInfo list>
    | TouchSession of SessionId
    | WorkerExited of SessionId * exitCode: int
    | ScheduleRestart of SessionId
    | StopAll of AsyncReplyChannel<unit>

  type ManagerState = {
    Sessions: Map<SessionId, ManagedSession>
    RestartPolicy: RestartPolicy.Policy
  }

  module ManagerState =
    let empty = {
      Sessions = Map.empty
      RestartPolicy = RestartPolicy.defaultPolicy
    }

    let addSession id session state =
      { state with Sessions = Map.add id session state.Sessions }

    let removeSession id state =
      { state with Sessions = Map.remove id state.Sessions }

    let tryGetSession id state =
      Map.tryFind id state.Sessions

    let allInfos state =
      state.Sessions
      |> Map.toList
      |> List.map (fun (_, s) -> s.Info)

  /// Spawn a worker sub-process and connect via named pipe.
  let private spawnWorker
    (sessionId: SessionId)
    (projects: string list)
    (workingDir: string)
    (ct: CancellationToken)
    (onExited: int -> unit)
    = async {
    let pipe = NamedPipeTransport.pipeName sessionId

    let projArgs =
      projects
      |> List.collect (fun p -> [ "--proj"; p ])
      |> String.concat " "

    let psi = ProcessStartInfo()
    psi.FileName <- "SageFs"
    psi.Arguments <-
      sprintf "worker --session-id %s --pipe-name %s %s" sessionId pipe projArgs
    psi.WorkingDirectory <- workingDir
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    let proc = new Process()
    proc.StartInfo <- psi
    proc.EnableRaisingEvents <- true
    proc.Exited.Add(fun _ -> onExited proc.ExitCode)

    if not (proc.Start()) then
      return Error (SageFsError.WorkerSpawnFailed "Failed to start worker process")
    else
      // Connect to the worker's named pipe
      do! Async.Sleep 500 // Brief delay for pipe to be created
      try
        let! proxy, disposable =
          NamedPipeTransport.connect pipe ct
        let info : SessionInfo = {
          Id = sessionId
          Projects = projects
          WorkingDirectory = workingDir
          SolutionRoot = SessionInfo.findSolutionRoot workingDir
          CreatedAt = DateTime.UtcNow
          LastActivity = DateTime.UtcNow
          Status = SessionStatus.Starting
          WorkerPid = Some proc.Id
        }
        return
          Ok {
            Info = info
            Process = proc
            Proxy = proxy
            PipeDisposable = disposable
            Projects = projects
            WorkingDir = workingDir
            RestartState = RestartPolicy.emptyState
          }
      with ex ->
        try proc.Kill() with _ -> ()
        return Error (SageFsError.WorkerSpawnFailed (sprintf "Failed to connect to worker: %s" ex.Message))
  }

  /// Stop a worker gracefully: send Shutdown, wait, then kill.
  let private stopWorker (session: ManagedSession) = async {
    try
      let! _ = session.Proxy WorkerMessage.Shutdown
      // Wait briefly for clean exit
      let exited = session.Process.WaitForExit(3000)
      if not exited then
        try session.Process.Kill() with _ -> ()
    with _ ->
      try session.Process.Kill() with _ -> ()
    session.PipeDisposable.Dispose()
  }

  /// Create the supervisor MailboxProcessor.
  let create (ct: CancellationToken) =
    MailboxProcessor<SessionCommand>.Start((fun inbox ->
      let rec loop (state: ManagerState) = async {
        let! cmd = inbox.Receive()
        match cmd with
        | SessionCommand.CreateSession(projects, workingDir, reply) ->
          let sessionId = Guid.NewGuid().ToString("N").[..7]
          let onExited exitCode =
            inbox.Post(SessionCommand.WorkerExited(sessionId, exitCode))
          let! result = spawnWorker sessionId projects workingDir ct onExited
          match result with
          | Ok managed ->
            let newState = ManagerState.addSession sessionId managed state
            reply.Reply(Ok managed.Info)
            return! loop newState
          | Error err ->
            reply.Reply(Error err)
            return! loop state

        | SessionCommand.StopSession(id, reply) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            do! stopWorker session
            let newState = ManagerState.removeSession id state
            reply.Reply(Ok ())
            return! loop newState
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound id))
            return! loop state

        | SessionCommand.GetSession(id, reply) ->
          reply.Reply(ManagerState.tryGetSession id state)
          return! loop state

        | SessionCommand.ListSessions reply ->
          reply.Reply(ManagerState.allInfos state)
          return! loop state

        | SessionCommand.TouchSession id ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let updated =
              { session with
                  Info =
                    { session.Info with
                        LastActivity = DateTime.UtcNow } }
            let newState = ManagerState.addSession id updated state
            return! loop newState
          | None ->
            return! loop state

        | SessionCommand.WorkerExited(id, exitCode) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            session.PipeDisposable.Dispose()
            let outcome =
              SessionLifecycle.onWorkerExited
                state.RestartPolicy
                session.RestartState
                exitCode
                DateTime.UtcNow
            let newStatus = SessionLifecycle.statusAfterExit outcome
            match outcome with
            | SessionLifecycle.ExitOutcome.Graceful ->
              let newState = ManagerState.removeSession id state
              return! loop newState
            | SessionLifecycle.ExitOutcome.Abandoned _ ->
              let newState = ManagerState.removeSession id state
              return! loop newState
            | SessionLifecycle.ExitOutcome.RestartAfter(delay, newRestartState) ->
              let updated =
                { session with
                    RestartState = newRestartState
                    Info = { session.Info with Status = newStatus } }
              let newState = ManagerState.addSession id updated state
              Async.Start(async {
                do! Async.Sleep(int delay.TotalMilliseconds)
                inbox.Post(SessionCommand.ScheduleRestart id)
              }, ct)
              return! loop newState
          | None ->
            return! loop state

        | SessionCommand.ScheduleRestart id ->
          match ManagerState.tryGetSession id state with
          | Some session when session.Info.Status = SessionStatus.Restarting ->
            let onExited exitCode =
              inbox.Post(SessionCommand.WorkerExited(id, exitCode))
            let! result =
              spawnWorker id session.Projects session.WorkingDir ct onExited
            match result with
            | Ok newManaged ->
              let restarted =
                { newManaged with
                    RestartState = session.RestartState }
              let newState = ManagerState.addSession id restarted state
              return! loop newState
            | Error _msg ->
              // Spawn failed — treat as another crash
              let outcome =
                SessionLifecycle.onWorkerExited
                  state.RestartPolicy
                  session.RestartState
                  1
                  DateTime.UtcNow
              match outcome with
              | SessionLifecycle.ExitOutcome.Abandoned _
              | SessionLifecycle.ExitOutcome.Graceful ->
                let newState = ManagerState.removeSession id state
                return! loop newState
              | SessionLifecycle.ExitOutcome.RestartAfter(delay, newRestartState) ->
                let updated =
                  { session with
                      RestartState = newRestartState }
                let newState = ManagerState.addSession id updated state
                Async.Start(async {
                  do! Async.Sleep(int delay.TotalMilliseconds)
                  inbox.Post(SessionCommand.ScheduleRestart id)
                }, ct)
                return! loop newState
          | _ ->
            return! loop state

        | SessionCommand.StopAll reply ->
          // Graceful shutdown of all sessions
          for KeyValue(_, session) in state.Sessions do
            do! stopWorker session
          reply.Reply(())
          return! loop ManagerState.empty
      }
      loop ManagerState.empty
    ), cancellationToken = ct)
