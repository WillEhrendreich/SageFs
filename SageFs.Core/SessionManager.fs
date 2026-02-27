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
    /// Worker HTTP base URL for direct endpoint access.
    WorkerBaseUrl: string
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
    | WorkerReady of SessionId * workerPid: int * baseUrl: string * SessionProxy
    | WorkerTestDiscovery of SessionId * tests: Features.LiveTesting.TestCase array * providers: Features.LiveTesting.ProviderDescription list
    | WorkerSpawnFailed of SessionId * workerPid: int * string
    | ScheduleRestart of SessionId
    | StopAll of AsyncReplyChannel<unit>
    // Standby pool commands
    | WarmStandby of StandbyKey
    | StandbyReady of StandbyKey * workerPid: int * SessionProxy
    | StandbySpawnFailed of StandbyKey * workerPid: int * string
    | StandbyExited of StandbyKey * workerPid: int
    | StandbyProgress of StandbyKey * progress: string
    | WorkerWarmupProgress of SessionId * progress: string
    | InvalidateStandbys of workingDir: string
    | GetStandbyInfo of AsyncReplyChannel<StandbyInfo>

  type ManagerState = {
    Sessions: Map<SessionId, ManagedSession>
    RestartPolicy: RestartPolicy.Policy
    Pool: PoolState
    /// Per-session warmup progress from worker stdout (e.g., "2/4 Scanned 12 files").
    /// Cleared when WorkerReady is received or session is removed.
    WarmupProgress: Map<SessionId, string>
  }

  module ManagerState =
    let empty = {
      Sessions = Map.empty
      RestartPolicy = RestartPolicy.defaultPolicy
      Pool = PoolState.empty
      WarmupProgress = Map.empty
    }

    let addSession id session state =
      { state with Sessions = Map.add id session state.Sessions }

    let removeSession id state =
      { state with
          Sessions = Map.remove id state.Sessions
          WarmupProgress = Map.remove id state.WarmupProgress }

    let tryGetSession id state =
      Map.tryFind id state.Sessions

    let allInfos state =
      state.Sessions
      |> Map.toList
      |> List.map (fun (_, s) -> s.Info)

  /// Immutable snapshot of ManagerState for lock-free CQRS reads.
  /// Published after every command — reads go here, never to the mailbox.
  type QuerySnapshot = {
    Sessions: Map<SessionId, SessionInfo>
    StandbyInfo: StandbyInfo
    /// Per-session warmup progress (e.g., "2/4 Scanned 12 files").
    WarmupProgress: Map<SessionId, string>
    /// Per-session worker HTTP base URLs (for hot-reload proxy, etc.).
    WorkerBaseUrls: Map<SessionId, string>
  }

  /// Compute standby info from pool state (pure function).
  let computeStandbyInfo (pool: PoolState) : StandbyInfo =
    if not pool.Enabled then StandbyInfo.NoPool
    elif pool.Standbys.IsEmpty then StandbyInfo.NoPool
    else
      let states = pool.Standbys |> Map.toList |> List.map (fun (_, s) -> s.State)
      if states |> List.exists (fun s -> s = StandbyState.Invalidated) then StandbyInfo.Invalidated
      elif states |> List.forall (fun s -> s = StandbyState.Ready) then StandbyInfo.Ready
      else
        let progress =
          pool.Standbys
          |> Map.toList
          |> List.tryPick (fun (_, s) ->
            if s.State = StandbyState.Warming then s.WarmupProgress
            else None)
          |> Option.defaultValue ""
        StandbyInfo.Warming progress

  module QuerySnapshot =
    let fromState (state: ManagerState) (standby: StandbyInfo) : QuerySnapshot =
      let sessions =
        state.Sessions
        |> Map.map (fun _id ms -> ms.Info)
      let workerUrls =
        state.Sessions
        |> Map.fold (fun acc id ms ->
          if ms.WorkerBaseUrl.Length > 0 then Map.add id ms.WorkerBaseUrl acc else acc) Map.empty
      { Sessions = sessions; StandbyInfo = standby; WarmupProgress = state.WarmupProgress; WorkerBaseUrls = workerUrls }

    /// Project a snapshot directly from ManagerState (computes standby info).
    let fromManagerState (state: ManagerState) : QuerySnapshot =
      fromState state (computeStandbyInfo state.Pool)

    let tryGetSession (id: SessionId) (snap: QuerySnapshot) : SessionInfo option =
      snap.Sessions |> Map.tryFind id

    let allSessions (snap: QuerySnapshot) : SessionInfo list =
      snap.Sessions |> Map.toList |> List.map snd

    let empty = { Sessions = Map.empty; StandbyInfo = StandbyInfo.NoPool; WarmupProgress = Map.empty; WorkerBaseUrls = Map.empty }

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
    psi.FileName <- "sagefs"
    psi.Arguments <-
      sprintf "worker --session-id %s --http-port 0 %s" sessionId projArgs
    psi.WorkingDirectory <- workingDir
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardOutput <- true

    // Propagate OTel env vars so workers export to the same collector
    for (key, value) in Instrumentation.workerOtelEnvVars sessionId do
      psi.Environment.[key] <- value

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
          elif line.StartsWith("WARMUP_PROGRESS=", System.StringComparison.Ordinal) then
            let payload = line.Substring("WARMUP_PROGRESS=".Length)
            inbox.Post(SessionCommand.WorkerWarmupProgress(sessionId, payload))
          elif line.StartsWith("WORKER_PORT=", System.StringComparison.Ordinal) then
            found <- Some (line.Substring("WORKER_PORT=".Length))
        match found with
        | Some baseUrl ->
          let proxy = HttpWorkerClient.httpProxy baseUrl
          inbox.Post(SessionCommand.WorkerReady(sessionId, proc.Id, baseUrl, proxy))
        | None ->
          failwith "Worker process exited before reporting port"
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
    session.Process.Dispose()
  }

  /// Run `dotnet build` for the primary project.
  /// Called from the daemon process (worker is already stopped).
  /// Async so we don't block the MailboxProcessor during build.
  let runBuildAsync (projects: string list) (workingDir: string) : Async<Result<string, string>> =
    async {
      let primaryProject = projects |> List.tryHead
      match primaryProject with
      | None -> return Ok "No projects to build"
      | Some projFile ->
        let psi =
          ProcessStartInfo(
            "dotnet",
            sprintf "build \"%s\" --no-restore --no-incremental" projFile,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir)
        let proc = Process.Start(psi)
        let stderrLines = System.Collections.Generic.List<string>()
        let stderrTask =
          System.Threading.Tasks.Task.Run(fun () ->
            let mutable line = proc.StandardError.ReadLine()
            while not (isNull line) do
              stderrLines.Add(line)
              line <- proc.StandardError.ReadLine())
        let stdoutTask =
          System.Threading.Tasks.Task.Run(fun () ->
            let mutable line = proc.StandardOutput.ReadLine()
            while not (isNull line) do
              line <- proc.StandardOutput.ReadLine())
        let! ct = Async.CancellationToken
        let tcs = System.Threading.Tasks.TaskCompletionSource<bool>()
        proc.EnableRaisingEvents <- true
        proc.Exited.Add(fun _ -> tcs.TrySetResult(true) |> ignore)
        if proc.HasExited then tcs.TrySetResult(true) |> ignore
        let timeoutTask = System.Threading.Tasks.Task.Delay(600_000, ct)
        let! completed =
          System.Threading.Tasks.Task.WhenAny(tcs.Task, timeoutTask)
          |> Async.AwaitTask
        if Object.ReferenceEquals(completed, timeoutTask) then
          try proc.Kill(entireProcessTree = true) with _ -> ()
          proc.Dispose()
          return Error "Build timed out (10 min limit)"
        else
          try stderrTask.Wait(5000) |> ignore with _ -> ()
          try stdoutTask.Wait(5000) |> ignore with _ -> ()
          let exitCode = proc.ExitCode
          proc.Dispose()
          if exitCode <> 0 then
            return Error (sprintf "Build failed (exit %d): %s" exitCode (String.concat "\n" stderrLines))
          else
            return Ok "Build succeeded"
    }

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
          elif line.StartsWith("WARMUP_PROGRESS=", System.StringComparison.Ordinal) then
            let payload = line.Substring("WARMUP_PROGRESS=".Length)
            inbox.Post(SessionCommand.StandbyProgress(key, payload))
          elif line.StartsWith("WORKER_PORT=", System.StringComparison.Ordinal) then
            found <- Some (line.Substring("WORKER_PORT=".Length))
        match found with
        | Some baseUrl ->
          let proxy = HttpWorkerClient.httpProxy baseUrl
          inbox.Post(SessionCommand.StandbyReady(key, proc.Id, proxy))
        | None ->
          failwith "Standby worker exited before reporting port"
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
  /// Returns (mailbox, readSnapshot) where readSnapshot is a lock-free CQRS query function.
  let create
    (ct: CancellationToken)
    (onStandbyProgressChanged: unit -> unit)
    (onTestDiscovery: SessionId -> Features.LiveTesting.TestCase array -> Features.LiveTesting.ProviderDescription list -> unit)
    (onInstrumentationMaps: SessionId -> Features.LiveTesting.InstrumentationMap array -> unit) =
    let snapshotRef = ref QuerySnapshot.empty
    let mailbox = MailboxProcessor<SessionCommand>.Start((fun inbox ->
      let publishSnapshot (state: ManagerState) =
        System.Threading.Interlocked.Exchange(snapshotRef, QuerySnapshot.fromManagerState state) |> ignore
      let rec loop (state: ManagerState) = async {
        publishSnapshot state
        let! cmd = inbox.Receive()
        match cmd with
        | SessionCommand.CreateSession(projects, workingDir, reply) ->
          let sessionId = Guid.NewGuid().ToString("N").[..7]
          let span = Instrumentation.startSpan Instrumentation.sessionSource "session.create"
                       [("session.id", box sessionId); ("session.projects", box (String.concat "," projects)); ("session.working_dir", box workingDir)]
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
              WorkerBaseUrl = ""
              Projects = projects
              WorkingDir = workingDir
              RestartState = RestartPolicy.emptyState
            }
            let newState = ManagerState.addSession sessionId managed state
            reply.Reply(Ok info)
            Instrumentation.sessionsCreated.Add(1L)
            Instrumentation.activeSessions.Add(1L)
            Instrumentation.succeedSpan span
            // Port discovery runs off the agent loop
            awaitWorkerPort sessionId proc inbox ct
            return! loop newState
          | Error err ->
            reply.Reply(Error err)
            Instrumentation.failSpan span (sprintf "%A" err)
            return! loop state

        | SessionCommand.StopSession(id, reply) ->
          let span = Instrumentation.startSpan Instrumentation.sessionSource "session.stop" [("session.id", box id)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            do! stopWorker session
            let newState = ManagerState.removeSession id state
            reply.Reply(Ok ())
            Instrumentation.sessionsStopped.Add(1L)
            Instrumentation.activeSessions.Add(-1L)
            Instrumentation.succeedSpan span
            return! loop newState
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound id))
            Instrumentation.failSpan span (sprintf "Session %s not found" id)
            return! loop state

        | SessionCommand.RestartSession(id, rebuild, reply) ->
          let span = Instrumentation.startSpan Instrumentation.sessionSource "session.restart"
                       [("session.id", box id); ("rebuild", box rebuild)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            let key = StandbyKey.fromSession session.Projects session.WorkingDir
            let standby = PoolState.getStandby key state.Pool
            match StandbyPool.decideRestart rebuild standby with
            | RestartDecision.SwapStandby readyStandby ->
              // Fast path: swap the warm standby in
              if not (isNull span) then span.SetTag("restart.decision", "standby_swap") |> ignore
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
                Proxy =
                  match readyStandby.Proxy with
                  | Some p -> p
                  | None -> failwith "SwapStandby with no proxy"
                WorkerBaseUrl = ""
                Projects = session.Projects
                WorkingDir = session.WorkingDir
                RestartState = session.RestartState
              }
              let poolAfterSwap = PoolState.removeStandby key stateAfterStop.Pool
              let newState =
                { ManagerState.addSession id swapped stateAfterStop with
                    Pool = poolAfterSwap }
              reply.Reply(Ok "Hard reset complete — swapped warm standby (instant).")
              Instrumentation.sessionsRestarted.Add(1L)
              Instrumentation.standbySwaps.Add(1L)
              let ageMs = (DateTime.UtcNow - readyStandby.CreatedAt).TotalMilliseconds
              Instrumentation.standbyAgeAtSwapMs.Record(ageMs)
              Instrumentation.standbyPoolSize.Add(-1L)
              Instrumentation.succeedSpan span
              // Start warming a new standby for next time
              inbox.Post(SessionCommand.WarmStandby key)
              return! loop newState
            | RestartDecision.ColdRestart ->
              // Slow path: traditional stop → build → spawn
              if not (isNull span) then span.SetTag("restart.decision", "cold_restart") |> ignore
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
              let! buildResult =
                if rebuild then runBuildAsync session.Projects session.WorkingDir
                else async { return Ok "No rebuild requested" }
              match buildResult with
              | Error msg ->
                reply.Reply(Error (SageFsError.HardResetFailed msg))
                Instrumentation.failSpan span msg
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
                  WorkerBaseUrl = ""
                  Projects = session.Projects
                  WorkingDir = session.WorkingDir
                  RestartState = session.RestartState
                }
                let newState = ManagerState.addSession id restarted stateAfterStop
                reply.Reply(Ok "Hard reset complete — worker respawning with fresh assemblies.")
                Instrumentation.sessionsRestarted.Add(1L)
                Instrumentation.coldRestarts.Add(1L)
                Instrumentation.succeedSpan span
                awaitWorkerPort id proc inbox ct
                return! loop newState
              | Error err ->
                reply.Reply(Error err)
                Instrumentation.failSpan span (sprintf "%A" err)
                return! loop stateAfterStop
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound id))
            Instrumentation.failSpan span (sprintf "Session %s not found" id)
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

        | SessionCommand.WorkerReady(id, _workerPid, baseUrl, proxy) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let updated =
              { session with Proxy = proxy; WorkerBaseUrl = baseUrl }
            let newState =
              { ManagerState.addSession id updated state with
                  WarmupProgress = Map.remove id state.WarmupProgress }
            // Trigger standby warmup for this session's config
            let key = StandbyKey.fromSession session.Projects session.WorkingDir
            if state.Pool.Enabled && PoolState.getStandby key state.Pool |> Option.isNone then
              inbox.Post(SessionCommand.WarmStandby key)
            onStandbyProgressChanged ()
            // Request initial test discovery from the worker
            Async.Start(async {
              try
                let rid = System.Guid.NewGuid().ToString("N")
                let! resp = proxy (WorkerMessage.GetTestDiscovery rid)
                match resp with
                | WorkerResponse.InitialTestDiscovery(tests, providers) ->
                  inbox.Post(SessionCommand.WorkerTestDiscovery(id, tests, providers))
                | _ -> ()
              with _ -> ()
            }, ct)
            // Fetch instrumentation maps from the worker
            Async.Start(async {
              try
                let rid = System.Guid.NewGuid().ToString("N")
                let! resp = proxy (WorkerMessage.GetInstrumentationMaps rid)
                match resp with
                | WorkerResponse.InstrumentationMapsResult(_, maps) when not (Array.isEmpty maps) ->
                  onInstrumentationMaps id maps
                | _ -> ()
              with _ -> ()
            }, ct)
            return! loop newState
          | None ->
            // Session was stopped before port discovery completed — ignore
            return! loop state

        | SessionCommand.WorkerTestDiscovery(id, tests, providers) ->
          onTestDiscovery id tests providers
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
          let span = Instrumentation.startSpan Instrumentation.sessionSource "worker.exited"
                       [("session.id", box id); ("worker.pid", box workerPid); ("exit_code", box exitCode)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            // Ignore stale exit events from old workers (e.g., after RestartSession)
            match session.Info.WorkerPid with
            | Some currentPid when currentPid <> workerPid ->
              if not (isNull span) then span.SetTag("stale_event", true) |> ignore
              Instrumentation.succeedSpan span
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
              if not (isNull span) then span.SetTag("outcome", "graceful") |> ignore
              Instrumentation.activeSessions.Add(-1L)
              Instrumentation.succeedSpan span
              let newState = ManagerState.removeSession id state
              return! loop newState
            | SessionLifecycle.ExitOutcome.Abandoned _ ->
              if not (isNull span) then span.SetTag("outcome", "abandoned") |> ignore
              Instrumentation.activeSessions.Add(-1L)
              Instrumentation.succeedSpan span
              let newState = ManagerState.removeSession id state
              return! loop newState
            | SessionLifecycle.ExitOutcome.RestartAfter(delay, newRestartState) ->
              if not (isNull span) then
                span.SetTag("outcome", "restart_scheduled") |> ignore
                span.SetTag("restart.delay_ms", delay.TotalMilliseconds) |> ignore
              Instrumentation.succeedSpan span
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
            Instrumentation.succeedSpan span
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
            let warmupMs = (DateTime.UtcNow - standby.CreatedAt).TotalMilliseconds
            Instrumentation.standbyWarmupMs.Record(warmupMs)
            Instrumentation.standbyPoolSize.Add(1L)
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

        | SessionCommand.WorkerWarmupProgress(id, progress) ->
          let newState =
            { state with WarmupProgress = Map.add id progress state.WarmupProgress }
          onStandbyProgressChanged ()
          return! loop newState

        | SessionCommand.InvalidateStandbys workingDir ->
          // Kill and remove standbys matching this working dir
          let toKill =
            state.Pool.Standbys
            |> Map.filter (fun k _ -> k.WorkingDir = workingDir)
          for KeyValue(_, standby) in toKill do
            Instrumentation.standbyInvalidations.Add(1L)
            Instrumentation.standbyPoolSize.Add(-1L)
            Async.Start(stopStandbyWorker standby, ct)
          let newPool =
            toKill
            |> Map.fold (fun pool k _ -> PoolState.removeStandby k pool) state.Pool
          return! loop { state with Pool = newPool }

        | SessionCommand.GetStandbyInfo reply ->
          reply.Reply (computeStandbyInfo state.Pool)
          return! loop state
      }
      loop ManagerState.empty
    ), cancellationToken = ct)
    (mailbox, fun () -> snapshotRef.Value)
