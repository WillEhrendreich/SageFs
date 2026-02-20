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
    | WorkerReady of SessionId * workerPid: int * SessionProxy
    | WorkerSpawnFailed of SessionId * workerPid: int * string
    | ScheduleRestart of SessionId
    | StopAll of AsyncReplyChannel<unit>
    // Standby pool commands
    | WarmStandby of StandbyKey
    | StandbyReady of StandbyKey * workerPid: int * SessionProxy
    | StandbySpawnFailed of StandbyKey * workerPid: int * string
    | StandbyExited of StandbyKey * workerPid: int
    | StandbyProgress of StandbyKey * progress: string
    | InvalidateStandbys of workingDir: string
    | GetStandbyInfo of AsyncReplyChannel<StandbyInfo>

  type ManagerState = {
    Sessions: Map<SessionId, ManagedSession>
    RestartPolicy: RestartPolicy.Policy
    Pool: PoolState
  }

  module ManagerState =
    let empty = {
      Sessions = Map.empty
      RestartPolicy = RestartPolicy.defaultPolicy
      Pool = PoolState.empty
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

  /// A proxy that rejects calls while the worker is still starting up.
  let pendingProxy : SessionProxy =
    fun _msg -> async {
      return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "Session is still starting up")
    }

  /// Start a worker OS process. Returns immediately with the Process
  /// (does NOT wait for the worker to report its port).
  let startWorkerProcess
    (sessionId: SessionId)
    (projects: string list)
    (workingDir: string)
    (onExited: int -> int -> unit)
    : Result<Process, SageFsError> =
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
      sprintf "worker --session-id %s --http-port 0 %s" sessionId projArgs
    psi.WorkingDirectory <- workingDir
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardOutput <- true

    let proc = new Process()
    proc.StartInfo <- psi
    proc.EnableRaisingEvents <- true

    if not (proc.Start()) then
      Error (SageFsError.WorkerSpawnFailed "Failed to start worker process")
    else
      let workerPid = proc.Id
      proc.Exited.Add(fun _ -> onExited workerPid proc.ExitCode)
      Ok proc

  /// Read the worker's stdout until WORKER_PORT is reported, then post
  /// a WorkerReady (or WorkerSpawnFailed) message back to the agent.
  /// Runs completely off the agent loop — never blocks the MailboxProcessor.
  let awaitWorkerPort
    (sessionId: SessionId)
    (proc: Process)
    (inbox: MailboxProcessor<SessionCommand>)
    (ct: CancellationToken)
    =
    Async.Start(async {
      try
        let mutable found = None
        while Option.isNone found do
          let! line = proc.StandardOutput.ReadLineAsync(ct).AsTask() |> Async.AwaitTask
          if isNull line then
            failwith "Worker process exited before reporting port"
          elif line.StartsWith("WORKER_PORT=") then
            found <- Some (line.Substring("WORKER_PORT=".Length))
        let baseUrl = found.Value
        let proxy = HttpWorkerClient.httpProxy baseUrl
        inbox.Post(SessionCommand.WorkerReady(sessionId, proc.Id, proxy))
      with ex ->
        try proc.Kill() with _ -> ()
        inbox.Post(
          SessionCommand.WorkerSpawnFailed(
            sessionId, proc.Id,
            sprintf "Failed to connect to worker: %s" ex.Message))
    }, ct)

  /// Stop a worker gracefully: send Shutdown, wait, then kill.
  let stopWorker (session: ManagedSession) = async {
    try
      let! _ = session.Proxy WorkerMessage.Shutdown
      let exited = session.Process.WaitForExit(3000)
      if not exited then
        try session.Process.Kill() with _ -> ()
        try session.Process.WaitForExit(2000) |> ignore with _ -> ()
    with _ ->
      try session.Process.Kill() with _ -> ()
      try session.Process.WaitForExit(2000) |> ignore with _ -> ()
  }

  /// Run `dotnet build` for the primary project.
  /// Called from the daemon process (worker is already stopped).
  let runBuild (projects: string list) (workingDir: string) =
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

  /// Await standby worker port discovery — posts StandbyReady or StandbySpawnFailed.
  /// Also captures WARMUP_PROGRESS lines and posts StandbyProgress updates.
  let awaitStandbyPort
    (key: StandbyKey)
    (proc: Process)
    (inbox: MailboxProcessor<SessionCommand>)
    (ct: CancellationToken)
    =
    Async.Start(async {
      try
        let mutable found = None
        while Option.isNone found do
          let! line = proc.StandardOutput.ReadLineAsync(ct).AsTask() |> Async.AwaitTask
          if isNull line then
            failwith "Standby worker exited before reporting port"
          elif line.StartsWith("WARMUP_PROGRESS=") then
            let payload = line.Substring("WARMUP_PROGRESS=".Length)
            inbox.Post(SessionCommand.StandbyProgress(key, payload))
          elif line.StartsWith("WORKER_PORT=") then
            found <- Some (line.Substring("WORKER_PORT=".Length))
        let baseUrl = found.Value
        let proxy = HttpWorkerClient.httpProxy baseUrl
        inbox.Post(SessionCommand.StandbyReady(key, proc.Id, proxy))
      with ex ->
        try proc.Kill() with _ -> ()
        inbox.Post(
          SessionCommand.StandbySpawnFailed(
            key, proc.Id,
            sprintf "Standby failed: %s" ex.Message))
    }, ct)

  /// Stop a standby worker process (fire-and-forget).
  let stopStandbyWorker (standby: StandbySession) = async {
    try
      match standby.Proxy with
      | Some proxy ->
        let! _ = proxy WorkerMessage.Shutdown
        let exited = standby.Process.WaitForExit(3000)
        if not exited then
          try standby.Process.Kill() with _ -> ()
      | None ->
        try standby.Process.Kill() with _ -> ()
    with _ ->
      try standby.Process.Kill() with _ -> ()
  }

  /// Create the supervisor MailboxProcessor.
  let create (ct: CancellationToken) (onStandbyProgressChanged: unit -> unit) =
    MailboxProcessor<SessionCommand>.Start((fun inbox ->
      let rec loop (state: ManagerState) = async {
        let! cmd = inbox.Receive()
        match cmd with
        | SessionCommand.CreateSession(projects, workingDir, reply) ->
          let sessionId = Guid.NewGuid().ToString("N").[..7]
          let onExited workerPid exitCode =
            inbox.Post(SessionCommand.WorkerExited(sessionId, workerPid, exitCode))
          match startWorkerProcess sessionId projects workingDir onExited with
          | Ok proc ->
            // Register session immediately with pending proxy — don't block
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
            let managed = {
              Info = info
              Process = proc
              Proxy = pendingProxy
              Projects = projects
              WorkingDir = workingDir
              RestartState = RestartPolicy.emptyState
            }
            let newState = ManagerState.addSession sessionId managed state
            reply.Reply(Ok info)
            // Port discovery runs off the agent loop
            awaitWorkerPort sessionId proc inbox ct
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
            let key = StandbyKey.fromSession session.Projects session.WorkingDir
            let standby = PoolState.getStandby key state.Pool
            match StandbyPool.decideRestart rebuild standby with
            | RestartDecision.SwapStandby readyStandby ->
              // Fast path: swap the warm standby in
              do! stopWorker session
              let stateAfterStop = ManagerState.removeSession id state
              let info : SessionInfo = {
                Id = id
                Name = session.Info.Name
                Projects = session.Projects
                WorkingDirectory = session.WorkingDir
                SolutionRoot = session.Info.SolutionRoot
                CreatedAt = session.Info.CreatedAt
                LastActivity = DateTime.UtcNow
                Status = SessionStatus.Ready
                WorkerPid = Some readyStandby.Process.Id
              }
              let swapped = {
                Info = info
                Process = readyStandby.Process
                Proxy = readyStandby.Proxy.Value
                Projects = session.Projects
                WorkingDir = session.WorkingDir
                RestartState = session.RestartState
              }
              let poolAfterSwap = PoolState.removeStandby key stateAfterStop.Pool
              let newState =
                { ManagerState.addSession id swapped stateAfterStop with
                    Pool = poolAfterSwap }
              reply.Reply(Ok "Hard reset complete — swapped warm standby (instant).")
              // Start warming a new standby for next time
              inbox.Post(SessionCommand.WarmStandby key)
              return! loop newState
            | RestartDecision.ColdRestart ->
              // Slow path: traditional stop → build → spawn
              do! stopWorker session
              // Also kill any stale standby for this config
              let poolAfterKill =
                match standby with
                | Some s ->
                  Async.Start(stopStandbyWorker s, ct)
                  PoolState.removeStandby key state.Pool
                | None -> state.Pool
              let stateAfterStop =
                { ManagerState.removeSession id state with Pool = poolAfterKill }
              let buildResult =
                if rebuild then runBuild session.Projects session.WorkingDir
                else Ok "No rebuild requested"
              match buildResult with
              | Error msg ->
                reply.Reply(Error (SageFsError.HardResetFailed msg))
                return! loop stateAfterStop
              | Ok _buildMsg ->
              let onExited workerPid exitCode =
                inbox.Post(SessionCommand.WorkerExited(id, workerPid, exitCode))
              match startWorkerProcess id session.Projects session.WorkingDir onExited with
              | Ok proc ->
                let info : SessionInfo = {
                  Id = id
                  Name = session.Info.Name
                  Projects = session.Projects
                  WorkingDirectory = session.WorkingDir
                  SolutionRoot = session.Info.SolutionRoot
                  CreatedAt = session.Info.CreatedAt
                  LastActivity = DateTime.UtcNow
                  Status = SessionStatus.Starting
                  WorkerPid = Some proc.Id
                }
                let restarted = {
                  Info = info
                  Process = proc
                  Proxy = pendingProxy
                  Projects = session.Projects
                  WorkingDir = session.WorkingDir
                  RestartState = session.RestartState
                }
                let newState = ManagerState.addSession id restarted stateAfterStop
                reply.Reply(Ok "Hard reset complete — worker respawning with fresh assemblies.")
                awaitWorkerPort id proc inbox ct
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

        | SessionCommand.WorkerReady(id, _workerPid, proxy) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let updated =
              { session with Proxy = proxy }
            let newState = ManagerState.addSession id updated state
            // Trigger standby warmup for this session's config
            let key = StandbyKey.fromSession session.Projects session.WorkingDir
            if state.Pool.Enabled && PoolState.getStandby key state.Pool |> Option.isNone then
              inbox.Post(SessionCommand.WarmStandby key)
            return! loop newState
          | None ->
            // Session was stopped before port discovery completed — ignore
            return! loop state

        | SessionCommand.WorkerSpawnFailed(id, _workerPid, msg) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let updated =
              { session with
                  Info = { session.Info with Status = SessionStatus.Faulted } }
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
            match startWorkerProcess id session.Projects session.WorkingDir onExited with
            | Ok proc ->
              let restarted =
                { session with
                    Process = proc
                    Proxy = pendingProxy
                    Info =
                      { session.Info with
                          Status = SessionStatus.Starting
                          WorkerPid = Some proc.Id
                          LastActivity = DateTime.UtcNow } }
              let newState = ManagerState.addSession id restarted state
              awaitWorkerPort id proc inbox ct
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
          // Graceful shutdown of all sessions and standbys
          for KeyValue(_, session) in state.Sessions do
            do! stopWorker session
          for KeyValue(_, standby) in state.Pool.Standbys do
            do! stopStandbyWorker standby
          reply.Reply(())
          return! loop ManagerState.empty

        // --- Standby pool commands ---

        | SessionCommand.WarmStandby key ->
          // Only warm if enabled and no standby exists for this config
          if state.Pool.Enabled
             && PoolState.getStandby key state.Pool |> Option.isNone then
            // Generate a temporary session ID for the standby worker
            let standbyId = sprintf "standby-%s" (Guid.NewGuid().ToString("N").[..7])
            let onExited workerPid _exitCode =
              inbox.Post(SessionCommand.StandbyExited(key, workerPid))
            match startWorkerProcess standbyId key.Projects key.WorkingDir onExited with
            | Ok proc ->
              let standby = {
                Process = proc
                Proxy = None
                State = StandbyState.Warming
                WarmupProgress = None
                Projects = key.Projects
                WorkingDir = key.WorkingDir
                CreatedAt = DateTime.UtcNow
              }
              let newPool = PoolState.setStandby key standby state.Pool
              awaitStandbyPort key proc inbox ct
              return! loop { state with Pool = newPool }
            | Error _ ->
              // Spawn failed — just skip, cold restart still works
              return! loop state
          else
            return! loop state

        | SessionCommand.StandbyReady(key, _workerPid, proxy) ->
          match PoolState.getStandby key state.Pool with
          | Some standby when standby.State = StandbyState.Warming ->
            let ready =
              { standby with
                  Proxy = Some proxy
                  State = StandbyState.Ready
                  WarmupProgress = None }
            let newPool = PoolState.setStandby key ready state.Pool
            onStandbyProgressChanged ()
            return! loop { state with Pool = newPool }
          | _ ->
            // Stale or unexpected — ignore
            return! loop state

        | SessionCommand.StandbySpawnFailed(key, _workerPid, _msg) ->
          // Remove the failed standby
          let newPool = PoolState.removeStandby key state.Pool
          return! loop { state with Pool = newPool }

        | SessionCommand.StandbyExited(key, _workerPid) ->
          // Standby worker exited — remove it
          let newPool = PoolState.removeStandby key state.Pool
          return! loop { state with Pool = newPool }

        | SessionCommand.StandbyProgress(key, progress) ->
          match PoolState.getStandby key state.Pool with
          | Some standby when standby.State = StandbyState.Warming ->
            let updated = { standby with WarmupProgress = Some progress }
            let newPool = PoolState.setStandby key updated state.Pool
            onStandbyProgressChanged ()
            return! loop { state with Pool = newPool }
          | _ ->
            return! loop state

        | SessionCommand.InvalidateStandbys workingDir ->
          // Kill and remove standbys matching this working dir
          let toKill =
            state.Pool.Standbys
            |> Map.filter (fun k _ -> k.WorkingDir = workingDir)
          for KeyValue(_, standby) in toKill do
            Async.Start(stopStandbyWorker standby, ct)
          let newPool =
            toKill
            |> Map.fold (fun pool k _ -> PoolState.removeStandby k pool) state.Pool
          return! loop { state with Pool = newPool }

        | SessionCommand.GetStandbyInfo reply ->
          let info =
            if not state.Pool.Enabled then StandbyInfo.NoPool
            elif state.Pool.Standbys.IsEmpty then StandbyInfo.NoPool
            else
              let states = state.Pool.Standbys |> Map.toList |> List.map (fun (_, s) -> s.State)
              if states |> List.exists (fun s -> s = StandbyState.Invalidated) then StandbyInfo.Invalidated
              elif states |> List.forall (fun s -> s = StandbyState.Ready) then StandbyInfo.Ready
              else
                let progress =
                  state.Pool.Standbys
                  |> Map.toList
                  |> List.tryPick (fun (_, s) ->
                    if s.State = StandbyState.Warming then s.WarmupProgress
                    else None)
                  |> Option.defaultValue ""
                StandbyInfo.Warming progress
          reply.Reply info
          return! loop state
      }
      loop ManagerState.empty
    ), cancellationToken = ct)
