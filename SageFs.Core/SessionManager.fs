namespace SageFs

open System
open System.Diagnostics
open System.IO
open System.Threading
open SageFs.WorkerProtocol
open SageFs.Utils

/// ROLE: Erlang-style supervisor for FSI worker sub-processes via MailboxProcessor.
///   SessionCommand DU serializes all mutations through a single agent loop.
///   Immutable QuerySnapshot published after each command for lock-free CQRS reads.
/// Weight: Chesterton's fence — actor serialization prevents FSI worker threading bugs.
/// Assumes (2026-03): All write operations go through the MailboxProcessor loop.
///   CQRS snapshot bypass added 2026-01 so reads (dashboard, SSE) never block behind
///   slow writes (dotnet build ~30s). See SessionManagerCqrsTests for the problem demo.
/// Invalidates-when: Worker processes become thread-safe, making mailbox serialization
///   unnecessary. Or when read latency < 5ms without CQRS (measure, don't guess).
/// Danger: Adding reads inside the mailbox loop — causes p99 > 200ms during slow writes.
///   Spawning workers outside the agent — races on ManagerState.Sessions map.
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
    AutoOpenNamespaces: bool
    /// Per-session restart tracking.
    RestartState: RestartPolicy.State
  }

  [<RequireQualifiedAccess>]
  type SessionCommand =
    | CreateSession of
        projects: string list *
        workingDir: string *
        autoOpenNamespaces: bool *
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
    | StandbyReady of StandbyKey * workerPid: int * baseUrl: string * SessionProxy
    | StandbySpawnFailed of StandbyKey * workerPid: int * string
    | StandbyExited of StandbyKey * workerPid: int
    | StandbyProgress of StandbyKey * progress: string
    | WorkerWarmupProgress of SessionId * progress: string
    | UpdateSessionStatus of SessionId * WorkerProtocol.SessionStatus
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
    /// Per-session standby state, keyed by session ID.
    /// Each session is matched to its standby (if any) via StandbyKey.
    PerSessionStandby: Map<SessionId, StandbyInfo>
    /// Per-session warmup progress (e.g., "2/4 Scanned 12 files").
    WarmupProgress: Map<SessionId, string>
    /// Per-session worker HTTP base URLs (for hot-reload proxy, etc.).
    WorkerBaseUrls: Map<SessionId, string>
  }

  /// Compute standby info from pool state (pure function).
  let computeStandbyInfo (pool: PoolState) : StandbyInfo =
    match pool.Enabled with
    | false -> StandbyInfo.NoPool
    | true ->
    match pool.Standbys.IsEmpty with
    | true -> StandbyInfo.NoPool
    | false ->
      let states = pool.Standbys |> Map.toList |> List.map (fun (_, s) -> s.State)
      match states |> List.exists (fun s -> s = StandbyState.Invalidated) with
      | true -> StandbyInfo.Invalidated
      | false ->
      match states |> List.forall (fun s -> s = StandbyState.Ready) with
      | true -> StandbyInfo.Ready
      | false ->
        let progress =
          pool.Standbys
          |> Map.toList
          |> List.tryPick (fun (_, s) ->
            match s.State = StandbyState.Warming with
            | true -> s.WarmupProgress
            | false -> None)
          |> Option.defaultValue ""
        StandbyInfo.Warming progress

  /// Compute per-session standby info by matching each session to its StandbyKey.
  let computePerSessionStandby (pool: PoolState) (sessions: Map<SessionId, ManagedSession>) : Map<SessionId, StandbyInfo> =
    match pool.Enabled with
    | false -> Map.empty
    | true ->
      sessions
      |> Map.map (fun _id session ->
        let key = StandbyKey.fromSession session.Projects session.WorkingDir session.AutoOpenNamespaces
        match Map.tryFind key pool.Standbys with
        | None -> StandbyInfo.NoPool
        | Some s ->
          match s.State with
          | StandbyState.Invalidated -> StandbyInfo.Invalidated
          | StandbyState.Ready -> StandbyInfo.Ready
          | StandbyState.Warming ->
            StandbyInfo.Warming (s.WarmupProgress |> Option.defaultValue ""))

  module QuerySnapshot =
    let fromState (state: ManagerState) (standby: StandbyInfo) : QuerySnapshot =
      let sessions =
        state.Sessions
        |> Map.map (fun _id ms -> ms.Info)
      let workerUrls =
        state.Sessions
        |> Map.fold (fun acc id ms ->
          match ms.WorkerBaseUrl.Length > 0 with
          | true -> Map.add id ms.WorkerBaseUrl acc
          | false -> acc) Map.empty
      let perSession = computePerSessionStandby state.Pool state.Sessions
      { Sessions = sessions; StandbyInfo = standby; PerSessionStandby = perSession; WarmupProgress = state.WarmupProgress; WorkerBaseUrls = workerUrls }

    /// Project a snapshot directly from ManagerState (computes standby info).
    let fromManagerState (state: ManagerState) : QuerySnapshot =
      fromState state (computeStandbyInfo state.Pool)

    let tryGetSession (id: SessionId) (snap: QuerySnapshot) : SessionInfo option =
      snap.Sessions |> Map.tryFind id

    let allSessions (snap: QuerySnapshot) : SessionInfo list =
      snap.Sessions |> Map.toList |> List.map snd

    let empty = { Sessions = Map.empty; StandbyInfo = StandbyInfo.NoPool; PerSessionStandby = Map.empty; WarmupProgress = Map.empty; WorkerBaseUrls = Map.empty }

  type SessionManagerRuntime = {
    StartWorkerProcess: SessionId -> string list -> string -> bool -> (int -> int -> unit) -> Result<Process, SageFsError>
    AwaitWorkerPort: SessionId -> Process -> MailboxProcessor<SessionCommand> -> CancellationToken -> unit
    AwaitStandbyPort: StandbyKey -> Process -> MailboxProcessor<SessionCommand> -> CancellationToken -> unit
    StopWorker: ManagedSession -> Async<unit>
    StopStandbyWorker: StandbySession -> Async<unit>
    RunBuildAsync: string list -> string -> Async<Result<string, string>>
  }

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
    (autoOpenNamespaces: bool)
    (onExited: int -> int -> unit)
    : Result<Process, SageFsError> =
    let args, envVars = Args.buildWorkerSpawnConfig (SessionId.value sessionId) projects false false autoOpenNamespaces false

    let psi = ProcessStartInfo()
    psi.FileName <- "sagefs"
    psi.Arguments <- args
    psi.WorkingDirectory <- workingDir
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardOutput <- true

    // Propagate OTel env vars so workers export to the same collector
    for (key, value) in Instrumentation.workerOtelEnvVars (SessionId.value sessionId) do
      psi.Environment.[key] <- value

    // Propagate session config as env vars (replaces --sln/--proj CLI args)
    for (key, value) in envVars do
      psi.Environment.[key] <- value

    let proc = new Process()
    proc.StartInfo <- psi
    proc.EnableRaisingEvents <- true

    match proc.Start() with
    | false ->
      Error (SageFsError.WorkerSpawnFailed "Failed to start worker process")
    | true ->
      let workerPid = proc.Id
      proc.Exited.Add(fun _ -> onExited workerPid proc.ExitCode)
      Ok proc

  /// Read the worker's stdout until WORKER_PORT is reported, then post
  /// a WorkerReady (or WorkerSpawnFailed) message back to the agent.
  /// Runs completely off the agent loop — never blocks the MailboxProcessor.
  /// Times out after SageFsConfig.WorkerStartupTimeoutMs if no port is reported.
  let awaitWorkerPort
    (sessionId: SessionId)
    (proc: Process)
    (inbox: MailboxProcessor<SessionCommand>)
    (ct: CancellationToken)
    =
    Async.Start(async {
      use cts =
        CancellationTokenSource.CreateLinkedTokenSource(ct)
      cts.CancelAfter(SageFsConfig.WorkerStartupTimeoutMs)
      let linkedCt = cts.Token
      try
        let mutable found = None
        while Option.isNone found do
          let! line = proc.StandardOutput.ReadLineAsync(linkedCt).AsTask() |> Async.AwaitTask
          match isNull line with
          | true ->
            failwith "Worker process exited before reporting port"
          | false ->
          match line.StartsWith("WARMUP_PROGRESS=", System.StringComparison.Ordinal) with
          | true ->
            let payload = line.Substring("WARMUP_PROGRESS=".Length)
            inbox.Post(SessionCommand.WorkerWarmupProgress(sessionId, payload))
          | false ->
          match line.StartsWith("WORKER_PORT=", System.StringComparison.Ordinal) with
          | true ->
            found <- Some (line.Substring("WORKER_PORT=".Length))
          | false -> ()
        match found with
        | Some baseUrl ->
          let proxy = HttpWorkerClient.httpProxy baseUrl
          inbox.Post(SessionCommand.WorkerReady(sessionId, proc.Id, baseUrl, proxy))
        | None ->
          failwith "Worker process exited before reporting port"
      with
      | :? OperationCanceledException when not ct.IsCancellationRequested ->
        // Linked CTS fired: per-session startup timeout, NOT daemon shutdown.
        try proc.Kill() with ex2 ->
          Log.warn "[SessionManager] Kill on startup timeout: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
        try proc.Dispose() with :? ObjectDisposedException -> ()
        inbox.Post(
          SessionCommand.WorkerSpawnFailed(
            sessionId,
            proc.Id,
            sprintf
              "Worker startup timed out after %dms waiting for WORKER_PORT= \
               (set SAGEFS_WORKER_STARTUP_TIMEOUT_MS to adjust)"
              SageFsConfig.WorkerStartupTimeoutMs))
      | ex ->
        try proc.Kill() with ex2 ->
          Log.warn "[SessionManager] Kill on spawn failure: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
        try proc.Dispose() with :? ObjectDisposedException -> ()
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
      match exited with
      | false ->
        try session.Process.Kill() with ex -> Log.warn "[SessionManager] Kill after timeout: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        try session.Process.WaitForExit(2000) |> ignore with ex -> Log.warn "[SessionManager] WaitForExit after kill: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      | true -> ()
    with ex ->
      Log.warn "[SessionManager] Graceful shutdown failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      try session.Process.Kill() with ex2 -> Log.warn "[SessionManager] Force kill failed: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
      try session.Process.WaitForExit(2000) |> ignore with ex2 -> Log.warn "[SessionManager] WaitForExit after force kill: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
    try session.Process.Dispose() with :? ObjectDisposedException -> ()
  }

  /// Run `dotnet build` for the primary project.
  /// Called from the daemon process (worker is already stopped).
  /// Async so we don't block the MailboxProcessor during build.
  let resolveBuildProjectPath (workingDir: string) (projFile: string) =
    match Path.IsPathRooted projFile with
    | true -> projFile
    | false -> Path.Combine(workingDir, projFile)

  let runBuildAsync (projects: string list) (workingDir: string) : Async<Result<string, string>> =
    async {
      let primaryProject = projects |> List.tryHead
      match primaryProject with
      | None -> return Ok "No projects to build"
      | Some projFile ->
        let buildProject = resolveBuildProjectPath workingDir projFile
        let psi = ProcessStartInfo(
          "dotnet",
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          WorkingDirectory = workingDir)
        psi.ArgumentList.Add("build")
        psi.ArgumentList.Add(buildProject)
        psi.ArgumentList.Add("--no-restore")
        psi.ArgumentList.Add("--no-incremental")
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
        match proc.HasExited with
        | true -> tcs.TrySetResult(true) |> ignore
        | false -> ()
        let timeoutTask = System.Threading.Tasks.Task.Delay(600_000, ct)
        let! completed =
          System.Threading.Tasks.Task.WhenAny(tcs.Task, timeoutTask)
          |> Async.AwaitTask
        match Object.ReferenceEquals(completed, timeoutTask) with
        | true ->
          try proc.Kill(entireProcessTree = true) with ex -> Log.warn "[SessionManager] Kill build process on timeout: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          proc.Dispose()
          return Error "Build timed out (10 min limit)"
        | false ->
          let! _ = System.Threading.Tasks.Task.WhenAll(stderrTask, stdoutTask) |> Async.AwaitTask
          let exitCode = proc.ExitCode
          proc.Dispose()
          match exitCode <> 0 with
          | true ->
            return Error (sprintf "Build failed (exit %d): %s" exitCode (String.concat "\n" stderrLines))
          | false ->
            return Ok "Build succeeded"
    }

  /// Await standby worker port discovery — posts StandbyReady or StandbySpawnFailed.
  /// Also captures WARMUP_PROGRESS lines and posts StandbyProgress updates.
  /// Times out after SageFsConfig.WorkerStartupTimeoutMs if no port is reported.
  let awaitStandbyPort
    (key: StandbyKey)
    (proc: Process)
    (inbox: MailboxProcessor<SessionCommand>)
    (ct: CancellationToken)
    =
    Async.Start(async {
      use cts =
        CancellationTokenSource.CreateLinkedTokenSource(ct)
      cts.CancelAfter(SageFsConfig.WorkerStartupTimeoutMs)
      let linkedCt = cts.Token
      try
        let mutable found = None
        while Option.isNone found do
          let! line = proc.StandardOutput.ReadLineAsync(linkedCt).AsTask() |> Async.AwaitTask
          match isNull line with
          | true ->
            failwith "Standby worker exited before reporting port"
          | false ->
          match line.StartsWith("WARMUP_PROGRESS=", System.StringComparison.Ordinal) with
          | true ->
            let payload = line.Substring("WARMUP_PROGRESS=".Length)
            inbox.Post(SessionCommand.StandbyProgress(key, payload))
          | false ->
          match line.StartsWith("WORKER_PORT=", System.StringComparison.Ordinal) with
          | true ->
            found <- Some (line.Substring("WORKER_PORT=".Length))
          | false -> ()
        match found with
        | Some baseUrl ->
          let proxy = HttpWorkerClient.httpProxy baseUrl
          inbox.Post(SessionCommand.StandbyReady(key, proc.Id, baseUrl, proxy))
        | None ->
          failwith "Standby worker exited before reporting port"
      with
      | :? OperationCanceledException when not ct.IsCancellationRequested ->
        // Linked CTS fired: per-standby startup timeout, NOT daemon shutdown.
        try proc.Kill() with ex2 ->
          Log.warn "[SessionManager] Kill standby on startup timeout: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
        try proc.Dispose() with :? ObjectDisposedException -> ()
        inbox.Post(
          SessionCommand.StandbySpawnFailed(
            key,
            proc.Id,
            sprintf
              "Standby startup timed out after %dms waiting for WORKER_PORT= \
               (set SAGEFS_WORKER_STARTUP_TIMEOUT_MS to adjust)"
              SageFsConfig.WorkerStartupTimeoutMs))
      | ex ->
        try proc.Kill() with ex2 -> Log.warn "[SessionManager] Kill standby on spawn failure: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
        try proc.Dispose() with :? ObjectDisposedException -> ()
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
        match exited with
        | false ->
          try standby.Process.Kill() with ex -> Log.warn "[SessionManager] Kill standby after timeout: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        | true -> ()
      | None ->
        try standby.Process.Kill() with ex -> Log.warn "[SessionManager] Kill standby (no proxy): %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
    with ex ->
      Log.warn "[SessionManager] Standby shutdown failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      try standby.Process.Kill() with ex2 -> Log.warn "[SessionManager] Force kill standby: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
    try standby.Process.Dispose() with :? ObjectDisposedException -> ()
  }

  let private faultedTombstone (session: ManagedSession) =
    { session with
        Proxy = pendingProxy
        WorkerBaseUrl = ""
        Info =
          { session.Info with
              Status = SessionStatus.Faulted
              WorkerPid = None
              LastActivity = DateTime.UtcNow } }

  let internal defaultRuntime = {
    StartWorkerProcess = startWorkerProcess
    AwaitWorkerPort = awaitWorkerPort
    AwaitStandbyPort = awaitStandbyPort
    StopWorker = stopWorker
    StopStandbyWorker = stopStandbyWorker
    RunBuildAsync = runBuildAsync
  }

  /// Create the supervisor MailboxProcessor.
  /// Returns (mailbox, readSnapshot) where readSnapshot is a lock-free CQRS query function.
  let internal createWith
    (runtime: SessionManagerRuntime)
    (ct: CancellationToken)
    (onStandbyProgressChanged: unit -> unit)
    (onTestDiscovery: SessionId -> Features.LiveTesting.TestCase array -> Features.LiveTesting.ProviderDescription list -> unit)
    (onInstrumentationMaps: SessionId -> Features.LiveTesting.InstrumentationMap array -> unit)
    (onSessionReady: SessionId -> unit)
    (onWarmupProgress: SessionId -> string -> unit)
    (onSessionFaulted: SessionId -> string -> unit) =
    let snapshotRef = ref QuerySnapshot.empty
    let mailbox = MailboxProcessor<SessionCommand>.Start((fun inbox ->
      let publishSnapshot (state: ManagerState) =
        System.Threading.Interlocked.Exchange(snapshotRef, QuerySnapshot.fromManagerState state) |> ignore
      let rec loop (state: ManagerState) = async {
        publishSnapshot state
        let! cmd = inbox.Receive()
        match cmd with
        | SessionCommand.CreateSession(projects, workingDir, autoOpenNamespaces, reply) ->
          let sessionId = SessionId.newId()
          let span = Instrumentation.startSpan Instrumentation.sessionSource "session.create"
                       [("session.id", box sessionId); ("session.projects", box (String.concat "," projects)); ("session.working_dir", box workingDir)]
          let onExited workerPid exitCode =
            inbox.Post(SessionCommand.WorkerExited(sessionId, workerPid, exitCode))
          match runtime.StartWorkerProcess sessionId projects workingDir autoOpenNamespaces onExited with
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
              AutoOpenNamespaces = autoOpenNamespaces
              RestartState = RestartPolicy.emptyState
            }
            let newState = ManagerState.addSession sessionId managed state
            reply.Reply(Ok info)
            Instrumentation.sessionsCreated.Add(1L)
            Instrumentation.activeSessions.Add(1L)
            Instrumentation.succeedSpan span
            // Port discovery runs off the agent loop
            runtime.AwaitWorkerPort sessionId proc inbox ct
            return! loop newState
          | Error err ->
            reply.Reply(Error err)
            Instrumentation.failSpan span (sprintf "%A" err)
            return! loop state

        | SessionCommand.StopSession(id, reply) ->
          let span = Instrumentation.startSpan Instrumentation.sessionSource "session.stop" [("session.id", box id)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            do! runtime.StopWorker session
            let newState = ManagerState.removeSession id state
            reply.Reply(Ok ())
            Instrumentation.sessionsStopped.Add(1L)
            Instrumentation.activeSessions.Add(-1L)
            Instrumentation.succeedSpan span
            return! loop newState
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound (SessionId.value id)))
            Instrumentation.failSpan span (sprintf "Session %s not found" (SessionId.value id))
            return! loop state

        | SessionCommand.RestartSession(id, rebuild, reply) ->
          let span = Instrumentation.startSpan Instrumentation.sessionSource "session.restart"
                       [("session.id", box id); ("rebuild", box rebuild)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            let key = StandbyKey.fromSession session.Projects session.WorkingDir session.AutoOpenNamespaces
            let standby = PoolState.getStandby key state.Pool
            match StandbyPool.decideRestart rebuild standby with
            | RestartDecision.SwapStandby readyStandby ->
              // Fast path: swap the warm standby in
              match isNull span with
              | false -> span.SetTag("restart.decision", "standby_swap") |> ignore
              | true -> ()
              do! runtime.StopWorker session
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
                WorkerBaseUrl = readyStandby.BaseUrl
                Projects = session.Projects
                WorkingDir = session.WorkingDir
                AutoOpenNamespaces = session.AutoOpenNamespaces
                RestartState = session.RestartState
              }
              let poolAfterSwap = PoolState.removeStandby key stateAfterStop.Pool
              let newState =
                { ManagerState.addSession id swapped stateAfterStop with
                    Pool = poolAfterSwap }
              onStandbyProgressChanged ()
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
              match isNull span with
              | false -> span.SetTag("restart.decision", "cold_restart") |> ignore
              | true -> ()
              do! runtime.StopWorker session
              // Also kill any stale standby for this config
              let poolAfterKill =
                match standby with
                | Some s ->
                  Async.Start(runtime.StopStandbyWorker s, ct)
                  PoolState.removeStandby key state.Pool
                | None -> state.Pool
              let stateAfterStop =
                { ManagerState.removeSession id state with Pool = poolAfterKill }
              let! buildResult =
                match rebuild with
                | true -> runtime.RunBuildAsync session.Projects session.WorkingDir
                | false -> async { return Ok "No rebuild requested" }
              match buildResult with
              | Error msg ->
                let tombstone = faultedTombstone session
                let newState = ManagerState.addSession id tombstone stateAfterStop
                reply.Reply(Error (SageFsError.HardResetFailed msg))
                onSessionReady id
                onSessionFaulted id msg
                Instrumentation.failSpan span msg
                return! loop newState
              | Ok _buildMsg ->
                let onExited workerPid exitCode =
                  inbox.Post(SessionCommand.WorkerExited(id, workerPid, exitCode))
                match runtime.StartWorkerProcess id session.Projects session.WorkingDir session.AutoOpenNamespaces onExited with
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
                    AutoOpenNamespaces = session.AutoOpenNamespaces
                    RestartState = session.RestartState
                  }
                  let newState = ManagerState.addSession id restarted stateAfterStop
                  reply.Reply(Ok "Hard reset complete — worker respawning with fresh assemblies.")
                  Instrumentation.sessionsRestarted.Add(1L)
                  Instrumentation.coldRestarts.Add(1L)
                  Instrumentation.succeedSpan span
                  runtime.AwaitWorkerPort id proc inbox ct
                  return! loop newState
                | Error err ->
                  let tombstone = faultedTombstone session
                  let newState = ManagerState.addSession id tombstone stateAfterStop
                  reply.Reply(Error err)
                  onSessionReady id
                  onSessionFaulted id (SageFsError.describe err)
                  Instrumentation.failSpan span (sprintf "%A" err)
                  return! loop newState
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound (SessionId.value id)))
            Instrumentation.failSpan span (sprintf "Session %s not found" (SessionId.value id))
            return! loop state

        | SessionCommand.GetSession(id, reply) ->
          reply.Reply(ManagerState.tryGetSession id state)
          return! loop state

        | SessionCommand.ListSessions reply ->
          // Return CQRS snapshot directly — no live HTTP calls inside the mailbox.
          // Status is kept current by the poll-until-Ready loop on WorkerReady.
          // Danger: Adding reads inside the mailbox loop causes p99 > 200ms during slow writes.
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

        | SessionCommand.WorkerReady(id, _workerPid, baseUrl, proxy) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let updated =
              { session with Proxy = proxy; WorkerBaseUrl = baseUrl }
            let newState =
              { ManagerState.addSession id updated state with
                  WarmupProgress = Map.remove id state.WarmupProgress }
            // Trigger standby warmup for this session's config
            let key = StandbyKey.fromSession session.Projects session.WorkingDir session.AutoOpenNamespaces
            match state.Pool.Enabled && (PoolState.getStandby key state.Pool |> Option.isNone) with
            | true -> inbox.Post(SessionCommand.WarmStandby key)
            | false -> ()
            onStandbyProgressChanged ()
            onSessionReady id
            // Poll worker until it reports Ready, then update snapshot.
            // Uses while loop with CT check to stop cleanly on daemon shutdown
            // or when the session terminates before becoming Ready.
            Async.Start(async {
              let mutable done' = false
              while not done' && not ct.IsCancellationRequested do
                do! Async.Sleep 1000
                try
                  let rid = Guid.NewGuid().ToString("N").[..7]
                  let! resp = proxy (WorkerMessage.GetStatus rid)
                  match resp with
                  | WorkerResponse.StatusResult(_, snapshot) ->
                    match snapshot.Status with
                    | SessionStatus.Ready ->
                      inbox.Post(SessionCommand.UpdateSessionStatus(id, SessionStatus.Ready))
                      done' <- true
                    | SessionStatus.Faulted
                    | SessionStatus.Stopped -> done' <- true
                    | _ -> ()
                  | _ -> ()
                with ex ->
                    Log.warn "[SessionManager] Worker ready poll transport error for %s: %s (%s)\n%s" (SessionId.value id) ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
                    done' <- true  // Transport error — WorkerExited event handles cleanup
            }, ct)
            // Request initial test discovery from the worker
            Async.Start(async {
              try
                let rid = System.Guid.NewGuid().ToString("N")
                let! resp = proxy (WorkerMessage.GetTestDiscovery rid)
                match resp with
                | WorkerResponse.InitialTestDiscovery(tests, providers) ->
                  inbox.Post(SessionCommand.WorkerTestDiscovery(id, tests, providers))
                | _ -> ()
              with ex ->
                Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "test_discovery" :> obj))
                Log.error "[SessionManager] Test discovery failed for %s: %s\n%s" (SessionId.value id) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
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
              with ex ->
                Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "instrumentation_maps" :> obj))
                Log.error "[SessionManager] Instrumentation maps fetch failed for %s: %s\n%s" (SessionId.value id) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
            }, ct)
            return! loop newState
          | None ->
            // Session was stopped before port discovery completed — ignore
            return! loop state

        | SessionCommand.WorkerTestDiscovery(id, tests, providers) ->
          onTestDiscovery id tests providers
          return! loop state

        | SessionCommand.WorkerSpawnFailed(id, _workerPid, msg) ->
          Log.warn "[SessionManager] Worker spawn failed for session %s: %s" (SessionId.value id) msg
          match ManagerState.tryGetSession id state with
          | Some session ->
            let updated = faultedTombstone session
            let newState = ManagerState.addSession id updated state
            onSessionReady id  // notify clients of Faulted state change
            onSessionFaulted id msg
            return! loop newState
          | None ->
            return! loop state

        | SessionCommand.WorkerExited(id, workerPid, exitCode) ->
          let span = Instrumentation.startSpan Instrumentation.sessionSource "worker.exited"
                       [("session.id", box id); ("worker.pid", box workerPid); ("exit_code", box exitCode)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            // Ignore stale exit events from old workers (e.g., after RestartSession)
            // Also ignore synthetic NotifyWorkerDied events (workerPid = -1) which
            // should not be treated as real process exits.
            match session.Info.WorkerPid with
            | None when workerPid > 0 ->
              match isNull span with
              | false -> span.SetTag("stale_event", true) |> ignore
              | true -> ()
              Instrumentation.succeedSpan span
              return! loop state
            | Some currentPid when currentPid <> workerPid && workerPid > 0 ->
              match isNull span with
              | false -> span.SetTag("stale_event", true) |> ignore
              | true -> ()
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
              match isNull span with
              | false -> span.SetTag("outcome", "graceful") |> ignore
              | true -> ()
              Instrumentation.activeSessions.Add(-1L)
              Instrumentation.succeedSpan span
              let newState = ManagerState.removeSession id state
              return! loop newState
            | SessionLifecycle.ExitOutcome.Abandoned _ ->
              match isNull span with
              | false -> span.SetTag("outcome", "abandoned") |> ignore
              | true -> ()
              Instrumentation.activeSessions.Add(-1L)
              Instrumentation.succeedSpan span
              onSessionFaulted id (sprintf "Worker process exited with code %d (abandoned after max retries)" exitCode)
              let newState = ManagerState.removeSession id state
              return! loop newState
            | SessionLifecycle.ExitOutcome.RestartAfter(delay, newRestartState) ->
              match isNull span with
              | false ->
                span.SetTag("outcome", "restart_scheduled") |> ignore
                span.SetTag("restart.delay_ms", delay.TotalMilliseconds) |> ignore
              | true -> ()
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
          let recoverySpan =
            Instrumentation.startSpan Instrumentation.sessionSource "session.crash_recovery"
              [("session.id", box id)]
          match ManagerState.tryGetSession id state with
          | Some session when session.Info.Status = SessionStatus.Restarting ->
            let onExited workerPid exitCode =
              inbox.Post(SessionCommand.WorkerExited(id, workerPid, exitCode))
            match runtime.StartWorkerProcess id session.Projects session.WorkingDir session.AutoOpenNamespaces onExited with
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
              runtime.AwaitWorkerPort id proc inbox ct
              match isNull recoverySpan with
              | false -> recoverySpan.SetTag("recovery.outcome", "restarted") |> ignore
              | true -> ()
              Instrumentation.succeedSpan recoverySpan
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
                match isNull recoverySpan with
                | false -> recoverySpan.SetTag("recovery.outcome", "abandoned") |> ignore
                | true -> ()
                Instrumentation.succeedSpan recoverySpan
                let newState = ManagerState.removeSession id state
                return! loop newState
              | SessionLifecycle.ExitOutcome.RestartAfter(delay, newRestartState) ->
                match isNull recoverySpan with
                | false ->
                  recoverySpan.SetTag("recovery.outcome", "retry_scheduled") |> ignore
                  recoverySpan.SetTag("recovery.retry_delay_ms", delay.TotalMilliseconds) |> ignore
                | true -> ()
                Instrumentation.succeedSpan recoverySpan
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
            Instrumentation.succeedSpan recoverySpan
            return! loop state

        | SessionCommand.StopAll reply ->
          // Graceful shutdown of all sessions and standbys — run in parallel
          // to avoid N×5s sequential timeout during shutdown
          let sessionTasks =
            [ for KeyValue(_, session) in state.Sessions -> runtime.StopWorker session ]
          let standbyTasks =
            [ for KeyValue(_, standby) in state.Pool.Standbys -> runtime.StopStandbyWorker standby ]
          do! sessionTasks @ standbyTasks |> Async.Parallel |> Async.Ignore
          reply.Reply(())
          return! loop ManagerState.empty

        // --- Standby pool commands ---

        | SessionCommand.WarmStandby key ->
          // Only warm if enabled and no standby exists for this config
          match state.Pool.Enabled
                && (PoolState.getStandby key state.Pool |> Option.isNone) with
          | true ->
            // Generate a temporary session ID for the standby worker
            let standbyId = SessionId.newId()
            let onExited workerPid _exitCode =
              inbox.Post(SessionCommand.StandbyExited(key, workerPid))
            match runtime.StartWorkerProcess standbyId key.Projects key.WorkingDir key.AutoOpenNamespaces onExited with
            | Ok proc ->
              let standby = {
                Process = proc
                Proxy = None
                BaseUrl = ""
                State = StandbyState.Warming
                WarmupProgress = None
                Projects = key.Projects
                WorkingDir = key.WorkingDir
                CreatedAt = DateTime.UtcNow
              }
              let newPool = PoolState.setStandby key standby state.Pool
              onStandbyProgressChanged ()
              runtime.AwaitStandbyPort key proc inbox ct
              return! loop { state with Pool = newPool }
            | Error _ ->
              // Spawn failed — just skip, cold restart still works
              return! loop state
          | false ->
            return! loop state

        | SessionCommand.StandbyReady(key, _workerPid, baseUrl, proxy) ->
          match PoolState.getStandby key state.Pool with
          | Some standby when standby.State = StandbyState.Warming ->
            let ready =
              { standby with
                  Proxy = Some proxy
                  BaseUrl = baseUrl
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
          match PoolState.getStandby key state.Pool with
          | Some _ ->
            let newPool = PoolState.removeStandby key state.Pool
            onStandbyProgressChanged ()
            return! loop { state with Pool = newPool }
          | None ->
            return! loop state

        | SessionCommand.StandbyExited(key, _workerPid) ->
          // Standby worker exited — remove it
          match PoolState.getStandby key state.Pool with
          | Some _ ->
            let newPool = PoolState.removeStandby key state.Pool
            onStandbyProgressChanged ()
            return! loop { state with Pool = newPool }
          | None ->
            return! loop state

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
          onWarmupProgress id progress
          return! loop newState

        | SessionCommand.UpdateSessionStatus(id, newStatus) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let updated =
              { session with Info = { session.Info with Status = newStatus } }
            let newState = ManagerState.addSession id updated state
            onStandbyProgressChanged ()
            return! loop newState
          | None ->
            return! loop state

        | SessionCommand.InvalidateStandbys workingDir ->
          // Kill and remove standbys matching this working dir
          let toKill =
            state.Pool.Standbys
            |> Map.filter (fun k _ -> System.String.Equals(k.WorkingDir, workingDir, System.StringComparison.OrdinalIgnoreCase))
          for KeyValue(_, standby) in toKill do
            Instrumentation.standbyInvalidations.Add(1L)
            Instrumentation.standbyPoolSize.Add(-1L)
            Async.Start(runtime.StopStandbyWorker standby, ct)
          let newPool =
            toKill
            |> Map.fold (fun pool k _ -> PoolState.removeStandby k pool) state.Pool
          match toKill.IsEmpty with
          | true ->
            return! loop state
          | false ->
            onStandbyProgressChanged ()
            return! loop { state with Pool = newPool }

        | SessionCommand.GetStandbyInfo reply ->
          reply.Reply (computeStandbyInfo state.Pool)
          return! loop state
      }
      async {
        try
          return! loop ManagerState.empty
        with ex ->
          Log.error "[SessionManager] Mailbox died unexpectedly: %s\n%s" ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
      }
    ), cancellationToken = ct)
    (mailbox, fun () -> snapshotRef.Value)

  let create
    (ct: CancellationToken)
    (onStandbyProgressChanged: unit -> unit)
    (onTestDiscovery: SessionId -> Features.LiveTesting.TestCase array -> Features.LiveTesting.ProviderDescription list -> unit)
    (onInstrumentationMaps: SessionId -> Features.LiveTesting.InstrumentationMap array -> unit)
    (onSessionReady: SessionId -> unit)
    (onWarmupProgress: SessionId -> string -> unit)
    (onSessionFaulted: SessionId -> string -> unit) =
    createWith
      defaultRuntime
      ct
      onStandbyProgressChanged
      onTestDiscovery
      onInstrumentationMaps
      onSessionReady
      onWarmupProgress
      onSessionFaulted
