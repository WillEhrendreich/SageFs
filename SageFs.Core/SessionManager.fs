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
    | RestartSession of
        SessionId *
        rebuild: bool *
        AsyncReplyChannel<Result<string, SageFsError>>
    | GetSession of
        SessionId *
        AsyncReplyChannel<ManagedSession option>
    | ListSessions of
        AsyncReplyChannel<SessionInfo list>
    | TouchSession of SessionId
    | WorkerExited of SessionId * workerPid: int * exitCode: int
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
    (onExited: int -> int -> unit)
    = async {
    let pipe = NamedPipeTransport.pipeName sessionId

    let projArgs =
      projects
      |> List.collect (fun p ->
        let ext = System.IO.Path.GetExtension(p).ToLowerInvariant()
        if ext = ".sln" || ext = ".slnx" then [ "--sln"; p ]
        else [ "--proj"; p ])
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

    if not (proc.Start()) then
      return Error (SageFsError.WorkerSpawnFailed "Failed to start worker process")
    else
      let workerPid = proc.Id
      // Register exit handler AFTER Start() so we have the PID.
      // EnableRaisingEvents=true ensures the event is queued even if the
      // process exits before we attach — .NET fires it when we subscribe.
      proc.Exited.Add(fun _ -> onExited workerPid proc.ExitCode)
      // Connect to the worker's named pipe
      do! Async.Sleep 500 // Brief delay for pipe to be created
      try
        let! proxy, disposable =
          NamedPipeTransport.connect pipe ct
        let info : SessionInfo = {
          Id = sessionId
          Name = None
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
      let exited = session.Process.WaitForExit(3000)
      if not exited then
        try session.Process.Kill() with _ -> ()
        try session.Process.WaitForExit(2000) |> ignore with _ -> ()
    with _ ->
      try session.Process.Kill() with _ -> ()
      try session.Process.WaitForExit(2000) |> ignore with _ -> ()
    session.PipeDisposable.Dispose()
  }

  /// Run `dotnet build` for the primary project.
  /// Called from the daemon process (worker is already stopped).
  let private runBuild (projects: string list) (workingDir: string) =
    let primaryProject =
      projects
      |> List.tryHead
    match primaryProject with
    | None -> Ok "No projects to build"
    | Some projFile ->
      let psi =
        ProcessStartInfo(
          "dotnet",
          sprintf "build \"%s\" --no-restore --no-incremental" projFile,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          WorkingDirectory = workingDir)
      use proc = Process.Start(psi)
      let stderrLines = System.Collections.Generic.List<string>()
      let stderrTask =
        System.Threading.Tasks.Task.Run(fun () ->
          let mutable line = proc.StandardError.ReadLine()
          while not (isNull line) do
            stderrLines.Add(line)
            line <- proc.StandardError.ReadLine())
      // Drain stdout
      let _stdoutTask =
        System.Threading.Tasks.Task.Run(fun () ->
          let mutable line = proc.StandardOutput.ReadLine()
          while not (isNull line) do
            line <- proc.StandardOutput.ReadLine())
      let exited = proc.WaitForExit(600_000) // 10 min max
      if not exited then
        try proc.Kill(entireProcessTree = true) with _ -> ()
        Error "Build timed out (10 min limit)"
      else
        try stderrTask.Wait(5000) |> ignore with _ -> ()
        if proc.ExitCode <> 0 then
          Error (sprintf "Build failed (exit %d): %s" proc.ExitCode (String.concat "\n" stderrLines))
        else
          Ok "Build succeeded"

  /// Create the supervisor MailboxProcessor.
  let create (ct: CancellationToken) =
    MailboxProcessor<SessionCommand>.Start((fun inbox ->
      let rec loop (state: ManagerState) = async {
        let! cmd = inbox.Receive()
        match cmd with
        | SessionCommand.CreateSession(projects, workingDir, reply) ->
          let sessionId = Guid.NewGuid().ToString("N").[..7]
          let onExited workerPid exitCode =
            inbox.Post(SessionCommand.WorkerExited(sessionId, workerPid, exitCode))
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

        | SessionCommand.RestartSession(id, rebuild, reply) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            // 1. Stop the worker (releases all assembly locks)
            do! stopWorker session
            let stateAfterStop = ManagerState.removeSession id state
            // 2. Optionally rebuild
            let buildResult =
              if rebuild then runBuild session.Projects session.WorkingDir
              else Ok "No rebuild requested"
            match buildResult with
            | Error msg ->
              reply.Reply(Error (SageFsError.HardResetFailed msg))
              return! loop stateAfterStop
            | Ok _buildMsg ->
            // 3. Respawn worker with same session ID — fresh process = fresh CLR cache
            let onExited workerPid exitCode =
              inbox.Post(SessionCommand.WorkerExited(id, workerPid, exitCode))
            let! result = spawnWorker id session.Projects session.WorkingDir ct onExited
            match result with
            | Ok newManaged ->
              let restarted =
                { newManaged with
                    RestartState = session.RestartState }
              let newState = ManagerState.addSession id restarted stateAfterStop
              reply.Reply(Ok "Hard reset complete — worker respawned with fresh assemblies.")
              return! loop newState
            | Error err ->
              reply.Reply(Error err)
              return! loop stateAfterStop
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound id))
            return! loop state

        | SessionCommand.GetSession(id, reply) ->
          reply.Reply(ManagerState.tryGetSession id state)
          return! loop state

        | SessionCommand.ListSessions reply ->
          // Refresh status from each alive worker before returning
          let! updatedState =
            state.Sessions
            |> Map.fold (fun stAsync id session ->
              async {
                let! st = stAsync
                if SessionStatus.isAlive session.Info.Status then
                  try
                    let replyId = Guid.NewGuid().ToString("N").[..7]
                    let! resp = session.Proxy (WorkerMessage.GetStatus replyId)
                    match resp with
                    | WorkerResponse.StatusResult(_, snapshot) ->
                      let updated =
                        { session with
                            Info = { session.Info with Status = snapshot.Status } }
                      return ManagerState.addSession id updated st
                    | _ -> return st
                  with _ -> return st
                else return st
              }
            ) (async { return state })
          reply.Reply(ManagerState.allInfos updatedState)
          return! loop updatedState

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

        | SessionCommand.WorkerExited(id, workerPid, exitCode) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            // Ignore stale exit events from old workers (e.g., after RestartSession)
            match session.Info.WorkerPid with
            | Some currentPid when currentPid <> workerPid ->
              return! loop state
            | _ ->
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
            let onExited workerPid exitCode =
              inbox.Post(SessionCommand.WorkerExited(id, workerPid, exitCode))
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
