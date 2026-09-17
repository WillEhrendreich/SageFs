namespace SageFs

open System
open System.Diagnostics
open System.IO
open System.Threading
open SageFs.WorkerProtocol
open SageFs.Utils
open SageFs.ProjectLoading

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

  /// A freshly spawned worker OS process, plus the identity of the session
  /// project's own SageFs.Core build adopted into it (`None` when the session
  /// does not self-host SageFs.Core). `AdoptedCore` is captured at spawn — the
  /// ground truth for the self-host staleness signal surfaced in status.
  type SpawnedWorker = {
    Process: Process
    AdoptedCore: (string * DateTime) option
  }

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
    /// Session workflow — preserved across restarts and standby swaps.
    Workflow: WorkflowTypes.SessionWorkflow
    /// Per-session restart tracking.
    RestartState: RestartPolicy.State
    /// The generation of the Run or Stop that last claimed the session's app
    /// (its state is Info.App). This mailbox is the app's single owner.
    AppGeneration: AppRun.RunGeneration
    /// Classification of all projects loaded in this session.
    ProjectRoles: ClassifiedProject list
    /// Identity `(assemblyVersion, originalBuildWriteTimeUtc)` of the session
    /// project's own SageFs.Core build adopted into this worker at spawn, or
    /// `None` when the session does not self-host SageFs.Core. Compared against
    /// the newest build on disk to surface the self-host staleness signal.
    AdoptedCore: (string * DateTime) option
  }

  /// What a worker said when asked for its tests.
  [<RequireQualifiedAccess>]
  type TestDiscoveryReport =
    | Discovered of tests: Features.LiveTesting.TestCase array * providers: Features.LiveTesting.ProviderDescription list
    | DiscoveryFailed of reason: string

  module TestDiscoveryReport =
    let ofResponse (response: WorkerResponse) : TestDiscoveryReport =
      match response with
      | WorkerResponse.InitialTestDiscovery (tests, providers) -> TestDiscoveryReport.Discovered (tests, providers)
      | WorkerResponse.WorkerError err -> TestDiscoveryReport.DiscoveryFailed (SageFsError.describe err)
      | other ->
        let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(other, typeof<WorkerResponse>)
        TestDiscoveryReport.DiscoveryFailed (sprintf "Unexpected reply to test discovery: %s" case.Name)

  [<RequireQualifiedAccess>]
  type SessionCommand =
    | CreateSession of
        projects: string list *
        workingDir: string *
        autoOpenNamespaces: bool *
        workflow: WorkflowTypes.SessionWorkflow *
        AsyncReplyChannel<Result<SessionInfo, SageFsError>>
    | StopSession of
        SessionId *
        AsyncReplyChannel<Result<unit, SageFsError>>
    | RestartSession of
        SessionId *
        rebuild: bool *
        AsyncReplyChannel<Result<string, SageFsError>>
    /// Internal: a cold rebuild started off the mailbox loop has finished.
    /// The carrying reply channel is replied to here so the original caller
    /// still receives the FINAL restart result (Ok respawn / Error) without
    /// ever blocking the loop on `dotnet build`.
    | RebuildCompleted of
        SessionId *
        buildResult: Result<string, SageFsError> *
        AsyncReplyChannel<Result<string, SageFsError>>
    | GetSession of
        SessionId *
        AsyncReplyChannel<ManagedSession option>
    | ListSessions of
        AsyncReplyChannel<SessionInfo list>
    | TouchSession of SessionId
    | WorkerExited of SessionId * workerPid: int * exitCode: int
    | WorkerReady of SessionId * workerPid: int * baseUrl: string * SessionProxy
    | WorkerTestDiscovery of SessionId * TestDiscoveryReport
    | WorkerSpawnFailed of SessionId * workerPid: int * string
    | ScheduleRestart of SessionId
    | StopAll of AsyncReplyChannel<unit>
    | WorkerWarmupProgress of SessionId * progress: string
    /// One APP_OUTPUT= line of a run_app'd app's stdout, routed to the session
    /// output stream so the dashboard shows a running console/game app (#82).
    | WorkerAppOutput of SessionId * line: string
    | UpdateSessionStatus of SessionId * WorkerProtocol.SessionLifecycleStatus
    /// A worker's ready poll saw Ready; it carries the worker's pid and its classified projects.
    | WorkerReportedReady of SessionId * workerPid: int * ClassifiedProject list
    /// A worker's ready poll saw it fault during warmup, with the worker's own reason.
    | WorkerReportedFaulted of SessionId * workerPid: int * reason: string
    /// Run App: the owner decides whether the run may begin and how it starts.
    | ClaimRun of SessionId * project: string * AsyncReplyChannel<Result<AppRun.RunClaim, SageFsError>>
    /// Stop App: takes a new generation so no earlier run's step can land after it.
    | ClaimStop of SessionId * AsyncReplyChannel<Result<AppRun.StopClaim, SageFsError>>
    /// One step of a run, applied only while its generation owns the app.
    | AdvanceRun of SessionId * AppRun.RunGeneration * AppRun.AppRunState * AsyncReplyChannel<AppRun.StepOutcome>
    | EndAppRun of SessionId * AppRun.RunGeneration * runId: string * AppRun.AppRunState * AsyncReplyChannel<AppRun.RunEnd>
    /// Answered when the session is Ready (or has failed) — parked until then.
    | AwaitReady of SessionId * AsyncReplyChannel<Result<unit, SageFsError>>
    | SwitchWorkflow of SessionId * WorkflowTypes.SessionWorkflow * AsyncReplyChannel<Result<string, SageFsError>>

  type ManagerState = {
    Sessions: Map<SessionId, ManagedSession>
    RestartPolicy: RestartPolicy.Policy
    /// Per-session warmup progress from worker stdout (e.g., "2/4 Scanned 12 files").
    /// Cleared when WorkerReady is received or session is removed.
    WarmupProgress: Map<SessionId, string>
    /// Sessions with a cold `dotnet build` currently running OFF the mailbox
    /// loop (started by RestartSession rebuild=true). The owner records the
    /// original reply channel so the mailbox stays free while the build runs
    /// and the caller is answered when RebuildCompleted arrives. Rejects a
    /// second hard reset and suppresses crash-recovery respawns for the same
    /// session while present.
    RebuildsInFlight: Map<SessionId, AsyncReplyChannel<Result<string, SageFsError>>>
    /// Spawn-first restart in progress (rebuild=false): the NEW worker has been
    /// spawned and is warming up; the OLD worker is still serving and is parked
    /// here so it can be retired when the new worker reports Ready. The old
    /// worker's exit event must be inert against the registered session (its
    /// pid no longer matches once WorkerReady commits the swap).
    PendingSwap: Map<SessionId, ManagedSession>
    /// Callers waiting for a session to become Ready, settled after every step.
    ReadyWaiters: Map<SessionId, AsyncReplyChannel<Result<unit, SageFsError>> list>
  }

  module ManagerState =
    let empty = {
      Sessions = Map.empty
      RestartPolicy = RestartPolicy.defaultPolicy
      WarmupProgress = Map.empty
      RebuildsInFlight = Map.empty
      PendingSwap = Map.empty
      ReadyWaiters = Map.empty
    }

    let addSession id session state =
      { state with Sessions = Map.add id session state.Sessions }

    let removeSession id state =
      { state with
          Sessions = Map.remove id state.Sessions
          WarmupProgress = Map.remove id state.WarmupProgress
          RebuildsInFlight = Map.remove id state.RebuildsInFlight
          PendingSwap = Map.remove id state.PendingSwap }

    let tryGetSession id state =
      Map.tryFind id state.Sessions

    let allInfos state =
      state.Sessions
      |> Map.toList
      |> List.map (fun (_, s) -> s.Info)

    let tryGetRebuildChannel id state =
      Map.tryFind id state.RebuildsInFlight

    let setRebuildInFlight id reply state =
      { state with RebuildsInFlight = Map.add id reply state.RebuildsInFlight }

    let clearRebuildInFlight id state =
      { state with RebuildsInFlight = Map.remove id state.RebuildsInFlight }

    let setPendingSwap id oldSession state =
      { state with PendingSwap = Map.add id oldSession state.PendingSwap }

    let clearPendingSwap id state =
      { state with PendingSwap = Map.remove id state.PendingSwap }

    let tryGetPendingSwap id state =
      Map.tryFind id state.PendingSwap

    /// Find an existing session with the same working directory and project
    /// set. Uniqueness is enforced HERE at the single owner (the mailbox) —
    /// the CQRS advisory guard in Mcp.fs is check-then-act and cannot prevent
    /// two concurrent create_session calls from both passing it.
    let tryFindDuplicate (projects: string list) (workingDir: string) state =
      state.Sessions
      |> Map.toList
      |> List.tryFind (fun (_, s) ->
        let sameDir =
          String.Equals(s.Info.WorkingDirectory, workingDir, StringComparison.OrdinalIgnoreCase)
        let sameProjects =
          List.sort s.Info.Projects = List.sort projects
        sameDir && sameProjects)
      |> Option.map fst

  /// Immutable snapshot of ManagerState for lock-free CQRS reads.
  /// Published after every command — reads go here, never to the mailbox.
  type QuerySnapshot = {
    Sessions: Map<SessionId, SessionInfo>
    /// Per-session warmup progress (e.g., "2/4 Scanned 12 files").
    WarmupProgress: Map<SessionId, string>
    /// Per-session worker HTTP base URLs (for hot-reload proxy, etc.).
    WorkerBaseUrls: Map<SessionId, string>
    /// Per-session identity `(assemblyVersion, originalBuildWriteTimeUtc)` of
    /// the SageFs.Core build adopted into the worker at spawn — present only
    /// for sessions that self-host SageFs.Core. Compared against the newest
    /// build on disk to surface the self-host staleness affordance.
    AdoptedCore: Map<SessionId, string * DateTime>
  }

  module QuerySnapshot =
    let fromState (state: ManagerState) : QuerySnapshot =
      let sessions =
        state.Sessions
        |> Map.map (fun _id ms -> ms.Info)
      let workerUrls =
        state.Sessions
        |> Map.fold (fun acc id ms ->
          match ms.WorkerBaseUrl.Length > 0 with
          | true -> Map.add id ms.WorkerBaseUrl acc
          | false -> acc) Map.empty
      let adoptedCore =
        state.Sessions
        |> Map.fold (fun acc id ms ->
          match ms.AdoptedCore with
          | Some identity -> Map.add id identity acc
          | None -> acc) Map.empty
      { Sessions = sessions; WarmupProgress = state.WarmupProgress; WorkerBaseUrls = workerUrls; AdoptedCore = adoptedCore }

    let fromManagerState (state: ManagerState) : QuerySnapshot =
      fromState state

    let tryGetSession (id: SessionId) (snap: QuerySnapshot) : SessionInfo option =
      snap.Sessions |> Map.tryFind id

    let allSessions (snap: QuerySnapshot) : SessionInfo list =
      snap.Sessions |> Map.toList |> List.map snd

    let empty = { Sessions = Map.empty; WarmupProgress = Map.empty; WorkerBaseUrls = Map.empty; AdoptedCore = Map.empty }

  type SessionManagerRuntime = {
    StartWorkerProcess: SessionId -> string list -> string -> bool -> WorkflowTypes.SessionWorkflow -> (int -> int -> unit) -> Result<SpawnedWorker, SageFsError>
    AwaitWorkerPort: SessionId -> Process -> MailboxProcessor<SessionCommand> -> CancellationToken -> unit
    StopWorker: ManagedSession -> Async<unit>
    RunBuildAsync: string list -> string -> Async<Result<string, SageFsError>>
  }

  /// A proxy that rejects calls while the worker is still starting up.
  let pendingProxy : SessionProxy =
    fun _msg -> async {
      return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "Session is still starting up")
    }

  /// Real health probe: one `GetStatus` round-trip over the worker's own
  /// proxy, hard-timed out via `Async.StartChild`'s timeout overload. Any
  /// answer at all — regardless of the worker's self-reported status — means
  /// the worker is alive and responsive; only a timeout or a transport
  /// exception counts as `Missed` (fail-closed, per `WorkerHealthProbe`'s own
  /// doctrine). Not a `SessionManagerRuntime` field — like
  /// `OwnerMonitor.getProcessById`, the injection seam tests actually use is
  /// one level down, in `WorkerHealthProbe.run`'s own `probe` parameter; this
  /// is production's real implementation of it, called directly where the
  /// probe loop is started so `SessionManagerRuntime`'s shape (and every
  /// existing literal construction of it, several outside this file) stays
  /// unchanged.
  let probeWorkerHealthOnce (timeoutMs: int) (proxy: SessionProxy) : Async<WorkerHealthProbe.ProbeOutcome> =
    async {
      try
        let rid = Guid.NewGuid().ToString("N")
        let! child = Async.StartChild(proxy (WorkerMessage.GetStatus rid), timeoutMs)
        let! _resp = child
        return WorkerHealthProbe.ProbeOutcome.Healthy
      with _ ->
        return WorkerHealthProbe.ProbeOutcome.Missed
    }

  let private hasValidReadyProxy (proxy: SessionProxy) =
    not (isNull (box proxy))

  let private hasValidReadyTransport (baseUrl: string) (proxy: SessionProxy) =
    not (String.IsNullOrWhiteSpace baseUrl)
    && hasValidReadyProxy proxy

  let private describeInvalidReadyTransport (transportKind: string) (baseUrl: string) (proxy: SessionProxy) =
    match String.IsNullOrWhiteSpace baseUrl, hasValidReadyProxy proxy with
    | true, false -> sprintf "%s reported ready without a valid base URL or proxy" transportKind
    | true, true -> sprintf "%s reported ready without a valid base URL" transportKind
    | false, false -> sprintf "%s reported ready without a valid proxy" transportKind
    | false, true -> sprintf "%s reported ready with a valid transport" transportKind

  /// Start a worker OS process. Returns immediately with the Process
  /// (does NOT wait for the worker to report its port).
  let startWorkerProcess
    (sessionId: SessionId)
    (projects: string list)
    (workingDir: string)
    (autoOpenNamespaces: bool)
    (workflow: WorkflowTypes.SessionWorkflow)
    (onExited: int -> int -> unit)
    : Result<SpawnedWorker, SageFsError> =
    let args, envVars = Args.buildWorkerSpawnConfig (SessionId.value sessionId) projects false false autoOpenNamespaces workflow
    // Spawn the FSI HOST (separate minimal-closure process), resolved relative
    // to the daemon's own location (see plan: fsi-host-supervisor).
    let dotnetMuxer =
      match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
      | null | "" ->
        Args.muxerFromRuntimeDir
          (System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
          (OperatingSystem.IsWindows())
      | hostPath -> hostPath
    // roast-4 #0(a): if the session's own projects ship a build of
    // SageFs.Core matching the running daemon's version — dogfooding SageFs
    // on SageFs — launch the worker from a fresh PRIVATE copy of the host
    // directory with SageFs.Core.dll substituted for the project's own
    // build, so the worker's REPL runs the code the session was created to
    // develop instead of the shared host's statically-linked copy. See
    // HostCoreAdoption's doc comment for why this can't be done by any
    // trick inside the spawned host process itself (SageFs.Core.dll is a
    // Trusted-Platform-Assembly for that process; the native binder wins
    // before any managed AssemblyLoadContext gets a say). A genuine version
    // mismatch is refused (fail-closed) rather than spawning a worker whose
    // Core build cannot match the daemon supervising it.
    let hostVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    match HostCoreAdoption.resolveLaunchRoot System.AppContext.BaseDirectory (SessionId.value sessionId) projects hostVersion with
    | Error reason -> Error (SageFsError.WorkerSpawnFailed reason)
    | Ok launchPlan ->
    let launchRoot = launchPlan.LaunchRoot
    let hostCleanup = launchPlan.Cleanup
    match Args.resolveHostLaunch launchRoot (OperatingSystem.IsWindows()) dotnetMuxer File.Exists with
    | Error reason ->
      hostCleanup |> Option.iter (fun cleanup -> try cleanup () with _ -> ())
      Error (SageFsError.WorkerSpawnFailed reason)
    | Ok launch ->

    let psi = ProcessStartInfo()
    match launch with
    | Args.HostLaunch.NativeExecutable exe ->
      psi.FileName <- exe
      psi.Arguments <- args
    | Args.HostLaunch.ViaDotnetMuxer (dotnet, hostDll) ->
      psi.FileName <- dotnet
      psi.Arguments <- sprintf "\"%s\" %s" hostDll args
    psi.WorkingDirectory <- workingDir
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true

    // The host's stdout carries the WORKER_PORT= ready line (validated by the
    // supervisor); its stderr carries diagnostics (forwarded to the daemon log).
    // No OTel env vars — the host carries no OpenTelemetry (minimal closure).

    // Propagate session config as env vars so worker startup stays independent of daemon CLI flags
    for (key, value) in envVars do
      psi.Environment.[key] <- value

    let proc = new Process()
    proc.StartInfo <- psi
    proc.EnableRaisingEvents <- true

    match proc.Start() with
    | false ->
      hostCleanup |> Option.iter (fun cleanup -> try cleanup () with _ -> ())
      Error (SageFsError.WorkerSpawnFailed "Failed to start worker process")
    | true ->
      let workerPid = proc.Id
      proc.Exited.Add(fun _ ->
        try
          onExited workerPid proc.ExitCode
        with _ -> ()
        // The private per-session host copy (if one was materialized) is
        // only needed while this worker process is running — once it has
        // exited, its own SageFs.Host.dll/SageFs.Core.dll are no longer
        // read from disk, so it is safe to remove.
        hostCleanup |> Option.iter (fun cleanup -> try cleanup () with _ -> ()))
      Ok { Process = proc; AdoptedCore = launchPlan.AdoptedCore }

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
        let stderrLines = System.Collections.Concurrent.ConcurrentQueue<string>()
        let stderrTask =
          System.Threading.Tasks.Task.Run(fun () ->
            try
              let mutable line = proc.StandardError.ReadLine()
              while not (isNull line) do
                stderrLines.Enqueue(line)
                line <- proc.StandardError.ReadLine()
            with _ -> ())
        let mutable found = None
        while Option.isNone found do
          let! line = proc.StandardOutput.ReadLineAsync(linkedCt).AsTask() |> Async.AwaitTask
          match isNull line with
          | true ->
            let workerPid = proc.Id
            let stderrSummary =
              stderrLines.ToArray()
              |> Array.truncate 20
              |> String.concat "\n"
            try proc.EnableRaisingEvents <- false with _ -> ()
            try proc.Dispose() with _ -> ()
            inbox.Post(
              SessionCommand.WorkerSpawnFailed(
                sessionId,
                workerPid,
                match String.IsNullOrWhiteSpace stderrSummary with
                | true -> "Worker process exited before reporting port"
                | false -> sprintf "Worker process exited before reporting port. stderr:\n%s" stderrSummary))
            found <- Some ""
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
        | Some baseUrl when baseUrl.Length > 0 ->
          // Port found: disable the startup-timeout guard so the long-lived
          // post-startup stdout read below can't trip it and kill a live worker.
          cts.CancelAfter(System.Threading.Timeout.Infinite)
          let proxy = HttpWorkerClient.httpProxy baseUrl
          inbox.Post(SessionCommand.WorkerReady(sessionId, proc.Id, baseUrl, proxy))
          // #82: keep reading stdout past the port line for a run_app'd app's
          // APP_OUTPUT= lines (to EOF; read errors/EOF swallowed, not a spawn fail).
          let appOutTask =
            System.Threading.Tasks.Task.Run(fun () ->
              try
                let mutable l = proc.StandardOutput.ReadLine()
                while not (isNull l) do
                  (match AppOutput.tryParse l with
                   | Some payload -> inbox.Post(SessionCommand.WorkerAppOutput(sessionId, payload))
                   | None -> ())
                  l <- proc.StandardOutput.ReadLine()
              with _ -> ())
          do! stderrTask |> Async.AwaitTask
          do! appOutTask |> Async.AwaitTask
        | _ ->
          do! stderrTask |> Async.AwaitTask
      with
      | :? OperationCanceledException when not ct.IsCancellationRequested ->
        // Linked CTS fired: per-session startup timeout, NOT daemon shutdown.
        let stderrSummary =
          try
            proc.StandardError.ReadToEnd()
          with _ -> ""
        try proc.Kill(entireProcessTree = true) with ex2 ->
          Log.warn "[SessionManager] Kill on startup timeout: %s" ex2.Message
        try proc.EnableRaisingEvents <- false with _ -> ()
        try proc.Dispose() with _ -> ()
        inbox.Post(
          SessionCommand.WorkerSpawnFailed(
            sessionId,
            proc.Id,
            sprintf
              "Worker startup timed out after %dms waiting for WORKER_PORT= \
               (set SAGEFS_WORKER_STARTUP_TIMEOUT_MS to adjust)%s"
              SageFsConfig.WorkerStartupTimeoutMs
              (match String.IsNullOrWhiteSpace stderrSummary with
               | true -> ""
               | false -> sprintf "\nstderr:\n%s" stderrSummary)))
      | ex ->
        let stderrSummary =
          try
            proc.StandardError.ReadToEnd()
          with _ -> ""
        try proc.Kill(entireProcessTree = true) with ex2 ->
          Log.warn "[SessionManager] Kill on spawn failure: %s" ex2.Message
        try proc.EnableRaisingEvents <- false with _ -> ()
        try proc.Dispose() with _ -> ()
        inbox.Post(
          SessionCommand.WorkerSpawnFailed(
            sessionId, proc.Id,
            sprintf "Failed to connect to worker: %s%s"
              ex.Message
              (match String.IsNullOrWhiteSpace stderrSummary with
               | true -> ""
               | false -> sprintf "\nstderr:\n%s" stderrSummary)))
    }, ct)

  /// Stop a worker gracefully: send Shutdown with a bounded wait, then kill the
  /// whole process tree. The bounded wait is essential — HttpWorkerClient.httpProxy
  /// has no request timeout, so a hung worker would otherwise block daemon shutdown
  /// forever (StopAll times out and the daemon exits, orphaning workers — issue #126).
  /// Kill uses entireProcessTree so any child processes (dotnet restore, compiler
  /// server) spawned by the worker die with it instead of lingering on Windows.
  let stopWorker (session: ManagedSession) = async {
    let proc = session.Process
    let hasExited = try proc.HasExited with _ -> true
    match hasExited with
    | true ->
      try proc.EnableRaisingEvents <- false with _ -> ()
      try proc.Dispose() with _ -> ()
    | false ->
      try
        let shutdownTask =
          session.Proxy WorkerMessage.Shutdown
          |> Async.StartAsTask
        let! completed =
          System.Threading.Tasks.Task.WhenAny(
            [| shutdownTask :> System.Threading.Tasks.Task
               System.Threading.Tasks.Task.Delay(Timeouts.workerShutdownDelay) |])
          |> Async.AwaitTask
        if obj.ReferenceEquals(completed, shutdownTask) then
          let! _ = shutdownTask |> Async.AwaitTask
          ()
        let exited = proc.WaitForExit(3000)
        match exited with
        | false ->
          try proc.Kill(entireProcessTree = true) with ex -> Log.warn "[SessionManager] Kill after timeout: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          try proc.WaitForExit(2000) |> ignore with ex -> Log.warn "[SessionManager] WaitForExit after kill: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        | true -> ()
      with ex ->
        Log.warn "[SessionManager] Graceful shutdown failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        let procAlive = try proc.HasExited with _ -> true |> not
        match procAlive with
        | true ->
          try proc.Kill(entireProcessTree = true) with ex2 -> Log.warn "[SessionManager] Force kill failed: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
          try proc.WaitForExit(2000) |> ignore with ex2 -> Log.warn "[SessionManager] WaitForExit after force kill: %s\n%s" ex2.Message (ex2.StackTrace |> Option.ofObj |> Option.defaultValue "")
        | false -> ()
      try proc.EnableRaisingEvents <- false with _ -> ()
      try proc.Dispose() with _ -> ()
  }

  /// Force-kill a set of worker process trees by PID, tolerating already-exited
  /// or reaped PIDs. Used by the daemon's force-exit watchdog so a shutdown that
  /// exceeds the graceful budget still kills every worker (issue #126: "sessions
  /// not ending when main SageFs exit").
  let killWorkerPids (pids: int list) : unit =
    for pid in pids do
      if pid > 0 then
        try
          use proc = Process.GetProcessById(pid)
          if not proc.HasExited then
            proc.Kill(entireProcessTree = true)
        with
        | :? ArgumentException -> ()   // no such process
        | :? InvalidOperationException -> () // already exited
        | ex ->
          Log.warn "[SessionManager] KillWorkerPids failed for pid %d: %s\n%s" pid ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")

  /// Run `dotnet build` for the primary project.
  /// Called from the daemon process (worker is already stopped).
  /// Async so we don't block the MailboxProcessor during build.
  let resolveBuildProjectPath (workingDir: string) (projFile: string) =
    match Path.IsPathRooted projFile with
    | true -> projFile
    | false -> Path.Combine(workingDir, projFile)

  /// The diagnostics from a failed `dotnet build`, as structured data — no
  /// surface-specific call to action baked in (see BuildDiagnostic.describe
  /// and SageFsError.BuildFailed's own doc comment for why).
  let buildDiagnosticsOf (stdout: string list) (stderr: string list) : BuildDiagnostic list =
    let output = stdout @ stderr
    // MSBuild ends each diagnostic with " [<project path>]"; the path is noise on a card.
    let withoutProject (line: string) =
      let trimmed = line.Trim()
      match trimmed.EndsWith("]", StringComparison.Ordinal), trimmed.LastIndexOf(" [", StringComparison.Ordinal) with
      | true, cut when cut > 0 -> trimmed.Substring(0, cut)
      | _ -> trimmed
    let errors =
      output
      |> List.filter (fun l -> l.Contains(": error ", StringComparison.Ordinal))
      |> List.map withoutProject
      |> List.distinct
    match errors with
    | [] ->
      output
      |> List.filter (fun l -> l.Trim() <> "")
      |> List.rev |> List.truncate 15 |> List.rev
      |> List.map BuildDiagnostic.ofLine
    | found -> found |> List.truncate 10 |> List.map BuildDiagnostic.ofLine

  /// The `dotnet` arguments of a session rebuild. Incremental on purpose: a
  /// clean build deletes the last good output before compiling, so one compile
  /// error would leave the project with nothing to run until built by hand.
  let buildArguments (buildProject: string) : string list =
    [ "build"; buildProject; "--no-restore" ]

  /// Daemon-wide cap on concurrent `dotnet build` child processes (vision
  /// §3.4 "diff-touches-compiled-file fraction" / roast-6 Phase 0 item 1).
  /// `RunBuildAsync` runs off the mailbox loop (each session's cold-restart
  /// build is a separate async), so with no cap here N concurrent warmups
  /// launch N unbounded `dotnet build`s — each its own MSBuild node pool
  /// fighting the others for CPU. Default cores/4 (min 1): a `dotnet build`
  /// is itself internally parallel, so one build slot already uses several
  /// cores; the cap bounds how many *builds* run at once, not how many
  /// cores each one may use.
  /// Not private: `SessionManagerBuildSemaphoreTests` asserts this matches
  /// the documented "cores/4, min 1" policy without needing to spawn real
  /// `dotnet build` processes in the default test suite.
  let buildConcurrencyLimit = max 1 (Environment.ProcessorCount / 4)
  let private buildSemaphore = new SemaphoreSlim(buildConcurrencyLimit, buildConcurrencyLimit)

  /// Free build slots right now — full capacity when no build is in flight.
  /// Lets a test observe the semaphore exists at the right capacity without
  /// spawning a real (multi-second) `dotnet build` in the default suite.
  let availableBuildSlots () = buildSemaphore.CurrentCount

  let runBuildAsync (projects: string list) (workingDir: string) : Async<Result<string, SageFsError>> =
    async {
      let primaryProject = projects |> List.tryHead
      match primaryProject with
      | None -> return Ok "No projects to build"
      | Some projFile ->
        let buildProject = resolveBuildProjectPath workingDir projFile
        let! ct = Async.CancellationToken
        do! buildSemaphore.WaitAsync(ct) |> Async.AwaitTask
        try
          let psi = ProcessStartInfo(
            "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir)
          for arg in buildArguments buildProject do
            psi.ArgumentList.Add(arg)
          let proc = Process.Start(psi)
          let stderrLines = System.Collections.Generic.List<string>()
          let stderrTask =
            System.Threading.Tasks.Task.Run(fun () ->
              let mutable line = proc.StandardError.ReadLine()
              while not (isNull line) do
                stderrLines.Add(line)
                line <- proc.StandardError.ReadLine())
          // dotnet build prints compiler errors on stdout, so both streams are kept.
          let stdoutLines = System.Collections.Generic.List<string>()
          let stdoutTask =
            System.Threading.Tasks.Task.Run(fun () ->
              let mutable line = proc.StandardOutput.ReadLine()
              while not (isNull line) do
                stdoutLines.Add(line)
                line <- proc.StandardOutput.ReadLine())
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
            let timeoutDiagnostic =
              { File = None; Line = None; Column = None; Code = None
                Severity = BuildDiagnosticSeverity.Error
                Message = "Build timed out (10 min limit)" }
            return Error (SageFsError.BuildFailed(-1, [ timeoutDiagnostic ]))
          | false ->
            let! _ = System.Threading.Tasks.Task.WhenAll(stderrTask, stdoutTask) |> Async.AwaitTask
            let exitCode = proc.ExitCode
            proc.Dispose()
            match exitCode <> 0 with
            | true ->
              return Error (SageFsError.BuildFailed(exitCode, CompileOrderInsight.enrich buildProject (buildDiagnosticsOf (List.ofSeq stdoutLines) (List.ofSeq stderrLines))))
            | false ->
              return Ok "Build succeeded"
        finally
          buildSemaphore.Release() |> ignore
    }

  let private appSlotOf (session: ManagedSession) : AppRun.AppSlot =
    { Generation = session.AppGeneration; State = session.Info.App }

  let private withAppSlot (slot: AppRun.AppSlot) (session: ManagedSession) : ManagedSession =
    { session with AppGeneration = slot.Generation; Info = { session.Info with App = slot.State } }

  let private faultedTombstone (reason: string option) (session: ManagedSession) =
    { session with
        Proxy = pendingProxy
        WorkerBaseUrl = ""
        Info =
          { session.Info with
              Status = SessionLifecycleStatus.Faulted reason
              LastActivity = DateTime.UtcNow } }

  let internal defaultRuntime = {
    StartWorkerProcess = startWorkerProcess
    AwaitWorkerPort = awaitWorkerPort
    StopWorker = stopWorker
    RunBuildAsync = runBuildAsync
  }

  /// Create the supervisor MailboxProcessor.
  /// Returns (mailbox, readSnapshot) where readSnapshot is a lock-free CQRS query function.
  let internal createWith
    (runtime: SessionManagerRuntime)
    (ct: CancellationToken)
    (onSessionProgressChanged: unit -> unit)
    (onTestDiscovery: SessionId -> TestDiscoveryReport -> unit)
    (onInstrumentationMaps: SessionId -> Features.LiveTesting.InstrumentationMap array -> unit)
    (onSessionReady: SessionId -> unit)
    (onWarmupProgress: SessionId -> string -> unit)
    (onSessionFaulted: SessionId -> string -> unit)
    (onAppOutput: SessionId -> string -> unit) =
    let snapshotRef = ref QuerySnapshot.empty
    // default policy: this predicate is defined as "true iff Restarting" — every
    // other SessionLifecycleStatus (present or future) is false by that same
    // definition, so there is nothing here for a new case to silently absorb.
    let isRestarting = function SessionLifecycleStatus.Restarting _ -> true | _ -> false
    /// Spawn a cold replacement worker for a session whose old worker was
    /// already stopped. Used by the plain rebuild=false restart (inline) and by
    /// the RebuildCompleted handler (off-mailbox build). Returns the next state
    /// plus the spawn outcome; the caller replies to its own channel (Ok with
    /// the "Hard reset complete" message, or the raw spawn SageFsError).
    let spawnColdReplacement
      (id: SessionId)
      (session: ManagedSession)
      (inbox: MailboxProcessor<SessionCommand>)
      (span: System.Diagnostics.Activity)
      (state: ManagerState)
      : ManagerState * Result<unit, SageFsError> =
      let onExited workerPid exitCode =
        inbox.Post(SessionCommand.WorkerExited(id, workerPid, exitCode))
      match runtime.StartWorkerProcess id session.Projects session.WorkingDir session.AutoOpenNamespaces session.Workflow onExited with
      | Ok spawned ->
        let proc = spawned.Process
        let info : SessionInfo = {
          Id = id
          Name = session.Info.Name
          Projects = session.Projects
          WorkingDirectory = session.WorkingDir
          SolutionRoot = session.Info.SolutionRoot
          CreatedAt = session.Info.CreatedAt
          LastActivity = DateTime.UtcNow
          Status = SessionLifecycleStatus.Starting { Pid = proc.Id; Port = None }
          Workflow = session.Workflow
          ActiveProject = session.Info.ActiveProject
          ProjectRoles = session.ProjectRoles
          App = AppRun.acrossWorkerRestart session.Info.App
        }
        let restarted = {
          Info = info
          Process = proc
          Proxy = pendingProxy
          WorkerBaseUrl = ""
          Projects = session.Projects
          WorkingDir = session.WorkingDir
          AutoOpenNamespaces = session.AutoOpenNamespaces
          Workflow = session.Workflow
          RestartState = session.RestartState
          AppGeneration = session.AppGeneration
          ProjectRoles = session.ProjectRoles
          AdoptedCore = spawned.AdoptedCore
        }
        let newState = ManagerState.addSession id restarted state
        Instrumentation.sessionsRestarted.Add(1L)
        Instrumentation.coldRestarts.Add(1L)
        Instrumentation.succeedSpan span
        runtime.AwaitWorkerPort id proc inbox ct
        (newState, Ok ())
      | Error err ->
        let reason = SageFsError.describe err
        let tombstone = faultedTombstone (Some reason) session
        let newState = ManagerState.addSession id tombstone state
        onSessionReady id
        onSessionFaulted id reason
        Instrumentation.failSpan span (sprintf "%A" err)
        (newState, Error err)
    let mailbox = MailboxProcessor<SessionCommand>.Start((fun inbox ->
      let publishSnapshot (state: ManagerState) =
        System.Threading.Interlocked.Exchange(snapshotRef, QuerySnapshot.fromManagerState state) |> ignore
      /// Single dispatch step of the mailbox loop, wrapped in the supervise step.
      /// Keeps the giant existing match; callers must end with `return state` for
      /// the untouched case and `return nextState` after a transition.
      // Spawn-first restart: start the replacement worker (with `workflow`)
      // BEFORE stopping the old one, so the old worker keeps serving while the
      // new one warms up, and a spawn failure leaves the session untouched —
      // still Ready, old worker serving, its true workflow still recorded. The
      // old worker is parked in PendingSwap and retired when the new worker
      // reports Ready (see the WorkerReady commit point).
      let spawnFirst
        (state: ManagerState)
        (id: SessionId)
        (session: ManagedSession)
        (workflow: WorkflowTypes.SessionWorkflow)
        (reply: AsyncReplyChannel<Result<string, SageFsError>>)
        (acceptedMessage: string)
        (span: Activity)
        : ManagerState =
        match isNull span with
        | false -> span.SetTag("restart.decision", "spawn_first") |> ignore
        | true -> ()
        let onExited workerPid exitCode =
          inbox.Post(SessionCommand.WorkerExited(id, workerPid, exitCode))
        match runtime.StartWorkerProcess id session.Projects session.WorkingDir session.AutoOpenNamespaces workflow onExited with
        | Error err ->
          reply.Reply(Error err)
          Instrumentation.failSpan span (SageFsError.describe err)
          state
        | Ok spawned ->
          let proc = spawned.Process
          // Registry continuity (P7): the session stays registered for the
          // whole restart, marked Restarting with a pending proxy. The old
          // worker's pid stays on Info.WorkerPid until the swap commits, so
          // its real exit during warmup is ignored by the stale-pid guard (P2).
          // The fresh spawn re-adopted the newest SageFs.Core on disk, so the
          // session's AdoptedCore is updated now; the WorkerReady commit
          // (`{ session with ... }`) then inherits this fresh value.
          //
          // Process MUST become the fresh spawn here: `session` (this
          // record's pre-update value) is what a LATER swap parks in
          // PendingSwap to retire. Leaving `.Process` stale here made a
          // second consecutive swap retire an already-dead process and leak
          // the real outgoing worker.
          let restarting =
            { session with
                Process = proc
                Proxy = pendingProxy
                WorkerBaseUrl = ""
                Workflow = workflow
                AdoptedCore = spawned.AdoptedCore
                Info =
                  { session.Info with
                      Status = SessionLifecycleStatus.Restarting (SessionLifecycleStatus.workerPid session.Info.Status)
                      Workflow = workflow
                      LastActivity = DateTime.UtcNow } }
          let newState =
            ManagerState.setPendingSwap id session
              { ManagerState.addSession id restarting state with
                  WarmupProgress = Map.remove id state.WarmupProgress }
          runtime.AwaitWorkerPort id proc inbox ct
          reply.Reply(Ok acceptedMessage)
          Instrumentation.sessionsRestarted.Add(1L)
          Instrumentation.succeedSpan span
          newState

      // Answer callers parked by AwaitReady once their session is Ready or can
      // no longer become Ready — whichever step caused it.
      let settleReadyWaiters (state: ManagerState) : ManagerState =
        state.ReadyWaiters
        |> Map.fold (fun (acc: ManagerState) id waiters ->
          let answer (result: Result<unit, SageFsError>) =
            for waiter in waiters do waiter.Reply result
            { acc with ReadyWaiters = Map.remove id acc.ReadyWaiters }
          match ManagerState.tryGetSession id acc with
          | None -> answer (Error (SageFsError.SessionNotFound (SessionId.value id)))
          | Some session ->
            match session.Info.Status with
            | SessionLifecycleStatus.Ready _ | SessionLifecycleStatus.Evaluating _ -> answer (Ok ())
            | SessionLifecycleStatus.Faulted reason ->
              answer (Error (SageFsError.WorkerSpawnFailed (reason |> Option.defaultValue "the session stopped before it became Ready")))
            | SessionLifecycleStatus.Stopped ->
              answer (Error (SageFsError.WorkerSpawnFailed "the session stopped before it became Ready"))
            | SessionLifecycleStatus.Starting _ | SessionLifecycleStatus.Restarting _ | SessionLifecycleStatus.Building _ -> acc) state

      let lastGoodState = ref ManagerState.empty
      // publishSnapshot is a fire-and-forget notification; a throwing snapshot
      // projection must never take the whole supervisor down with it.
      let publishSnapshotSafe (state: ManagerState) =
        try publishSnapshot state
        with ex -> Log.warn "[SessionManager] publishSnapshot threw (continuing): %s" ex.Message
      // settleReadyWaiters is a state transform; if it throws, keep the
      // post-command state rather than letting the loop die.
      let settleReadyWaitersSafe (state: ManagerState) =
        try settleReadyWaiters state
        with ex ->
          Log.warn "[SessionManager] settleReadyWaiters threw (keeping state'): %s" ex.Message
          state
      let rec loop (state: ManagerState) = async {
        lastGoodState.Value <- state
        publishSnapshotSafe state
        let! cmd = inbox.Receive()
        let! state' = superviseStep state cmd
        return! loop (settleReadyWaitersSafe state')
      }
      and step (state: ManagerState) (cmd: SessionCommand) : Async<ManagerState> = async {
        match cmd with
        | SessionCommand.CreateSession(projects, workingDir, autoOpenNamespaces, workflow, reply) ->
          // Enforce one session per (projects, workingDir) AT THE OWNER — the
          // mailbox is the only place that can make this atomic. Two
          // concurrent create_session calls both passing the CQRS advisory
          // guard would otherwise spawn duplicate workers, breaking the
          // "Multiple sessions match workingDirectory" routing invariant.
          match ManagerState.tryFindDuplicate projects workingDir state with
          | Some existingId ->
            reply.Reply(Error (SageFsError.DuplicateSession (SessionId.value existingId, workingDir)))
            return state
          | None ->
            let sessionId = SessionId.newId()
            let span = Instrumentation.startSpan Instrumentation.sessionSource "session.create"
                         [("session.id", box sessionId); ("session.projects", box (String.concat "," projects)); ("session.working_dir", box workingDir)]
            let onExited workerPid exitCode =
              inbox.Post(SessionCommand.WorkerExited(sessionId, workerPid, exitCode))
            match runtime.StartWorkerProcess sessionId projects workingDir autoOpenNamespaces workflow onExited with
            | Ok spawned ->
              let proc = spawned.Process
              // Register session immediately with pending proxy — don't block
              let info : SessionInfo = {
                Id = sessionId
                Name = None
                Projects = projects
                WorkingDirectory = workingDir
                SolutionRoot = SessionInfo.findSolutionRoot workingDir
                CreatedAt = DateTime.UtcNow
                LastActivity = DateTime.UtcNow
                Status = SessionLifecycleStatus.Starting { Pid = proc.Id; Port = None }
                Workflow = workflow
                ActiveProject = None
                ProjectRoles = []
                App = AppRun.AppRunState.NotRunning
              }
              let managed = {
                Info = info
                Process = proc
                Proxy = pendingProxy
                WorkerBaseUrl = ""
                Projects = projects
                WorkingDir = workingDir
                AutoOpenNamespaces = autoOpenNamespaces
                Workflow = workflow
                RestartState = RestartPolicy.emptyState
                AppGeneration = AppRun.AppSlot.initial.Generation
                ProjectRoles = []
                AdoptedCore = spawned.AdoptedCore
              }
              let newState = ManagerState.addSession sessionId managed state
              reply.Reply(Ok info)
              Instrumentation.sessionsCreated.Add(1L)
              Instrumentation.activeSessions.Add(1L)
              Instrumentation.succeedSpan span
              // Port discovery runs off the agent loop
              runtime.AwaitWorkerPort sessionId proc inbox ct
              return newState
            | Error err ->
              reply.Reply(Error err)
              Instrumentation.failSpan span (sprintf "%A" err)
              return state

        | SessionCommand.StopSession(id, reply) ->
          let span = Instrumentation.startSpan Instrumentation.sessionSource "session.stop" [("session.id", box id)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            try
              do! runtime.StopWorker session
              let newState = ManagerState.removeSession id state
              reply.Reply(Ok ())
              Instrumentation.sessionsStopped.Add(1L)
              Instrumentation.activeSessions.Add(-1L)
              Instrumentation.succeedSpan span
              return newState
            with ex ->
              // Fail-closed: an unexpected stop-worker exception must not kill
              // the mailbox. Reply the error to the caller and keep the session
              // registered so the operator can retry / hard-reset it.
              Log.warn "[SessionManager] StopWorker threw for session %s: %s\n%s" (SessionId.value id) ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
              reply.Reply(Error (SageFsError.SessionStopFailed (SessionId.value id, ex.Message)))
              Instrumentation.failSpan span ex.Message
              return state
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound (SessionId.value id)))
            Instrumentation.failSpan span (sprintf "Session %s not found" (SessionId.value id))
            return state

        | SessionCommand.RestartSession(id, rebuild, reply) ->
          let span = Instrumentation.startSpan Instrumentation.sessionSource "session.restart"
                       [("session.id", box id); ("rebuild", box rebuild)]
          match ManagerState.tryGetSession id state with
          | Some session when ManagerState.tryGetRebuildChannel id state |> Option.isSome ->
            // Reject another hard reset while a cold rebuild of this session is
            // still in flight (started by a previous RestartSession). The
            // RebuildCompleted handler is the single respawn point, so an
            // accepted second reset would otherwise double-spawn the worker.
            reply.Reply(Error (SageFsError.HardResetFailed "Hard reset already in progress for this session"))
            Instrumentation.failSpan span "Hard reset already in progress for this session"
            return state
          | Some session ->
            match rebuild with
            | false ->
              return spawnFirst state id session session.Workflow reply "Hard reset accepted — replacement worker spawning." span
            | true ->
              // rebuild=true runs `dotnet build` OFF the mailbox loop so other
              // session operations (list/create/stop) stay responsive for the
              // whole build. The caller's reply channel is parked in state and
              // NOT answered yet — the FINAL Ok/Error is delivered when
              // RebuildCompleted is processed (the mailbox is the single respawn
              // point).
              let buildInBackground stateInFlight =
                Async.Start(async {
                  let! buildResult = runtime.RunBuildAsync session.Projects session.WorkingDir
                  inbox.Post(SessionCommand.RebuildCompleted(id, buildResult, reply))
                }, ct)
                stateInFlight
              match SessionLifecycleStatus.workerPid session.Info.Status with
              | Some _ ->
                // Build first: the live worker keeps serving the last good build
                // for the whole build, so a build that fails (a typo mid-edit)
                // never costs the user a working session. Only a good build swaps
                // in the replacement, spawn-first. The worker loads shadow
                // copies, so the build never fights it for file locks.
                match isNull span with
                | false -> span.SetTag("restart.decision", "build_first") |> ignore
                | true -> ()
                Instrumentation.succeedSpan span
                return buildInBackground (ManagerState.setRebuildInFlight id reply state)
              | None ->
              // No live worker (faulted or stopped): nothing to keep serving.
              match isNull span with
              | false -> span.SetTag("restart.decision", "cold_restart") |> ignore
              | true -> ()
              do! runtime.StopWorker session
              // INVARIANT (registry preservation): a session undergoing a hard reset
              // stays registered for the whole restart, marked `Restarting`, so no
              // reader can observe a missing session mid-restart — that previously
              // produced "Session is no longer running. Use create_session" guidance
              // and duplicate-session churn. WorkerPid is cleared so the old worker's
              // real exit event is ignored by the WorkerExited stale-pid guard.
              // Mirrors the crash-recovery path (ExitOutcome.RestartAfter → Restarting).
              let stateAfterStop =
                let restarting =
                  { session with
                      Info = { session.Info with Status = SessionLifecycleStatus.Restarting None }
                      Proxy = pendingProxy
                      WorkerBaseUrl = "" }
                let afterMark = ManagerState.addSession id restarting state
                { afterMark with
                    WarmupProgress = Map.remove id afterMark.WarmupProgress }
              Instrumentation.succeedSpan span
              return buildInBackground (ManagerState.setRebuildInFlight id reply stateAfterStop)
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound (SessionId.value id)))
            Instrumentation.failSpan span (sprintf "Session %s not found" (SessionId.value id))
            return state

        | SessionCommand.RebuildCompleted(id, buildResult, reply) ->
          // Single respawn point for an off-mailbox cold rebuild. The session is
          // still registered as Restarting (registry-preservation invariant) with
          // the rebuild flag cleared and the original caller's channel parked.
          let rebuildSpan =
            Instrumentation.startSpan Instrumentation.sessionSource "session.rebuild_completed"
              [("session.id", box id)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            let stateCleared = ManagerState.clearRebuildInFlight id state
            match buildResult, SessionLifecycleStatus.workerPid session.Info.Status with
            | Error err, Some _ ->
              // The live worker still serves the last good build: the failure is
              // the caller's to show, not a reason to kill a working session.
              let msg = SageFsError.describe err
              reply.Reply(Error err)
              Instrumentation.failSpan rebuildSpan msg
              return stateCleared
            | Ok _buildMsg, Some _ ->
              return spawnFirst stateCleared id session session.Workflow reply "Hard reset complete — worker respawning with fresh assemblies." rebuildSpan
            | Error err, None ->
              // No worker to fall back to → faulted tombstone that says why.
              let msg = SageFsError.describe err
              let tombstone = faultedTombstone (Some msg) session
              let newState = ManagerState.addSession id tombstone stateCleared
              reply.Reply(Error err)
              onSessionReady id
              onSessionFaulted id msg
              Instrumentation.failSpan rebuildSpan msg
              return newState
            | Ok _buildMsg, None ->
              let newState, spawnResult = spawnColdReplacement id session inbox rebuildSpan stateCleared
              match spawnResult with
              | Ok () ->
                reply.Reply(Ok "Hard reset complete — worker respawning with fresh assemblies.")
                return newState
              | Error err ->
                reply.Reply(Error err)
                return newState
          | None ->
            // Session was stopped while the build ran (StopSession/StopAll
            // removed it and cleared the in-flight flag). Answer the carried
            // channel so the caller never hangs, then leave state untouched.
            reply.Reply(Error (SageFsError.SessionNotFound (SessionId.value id)))
            Instrumentation.failSpan rebuildSpan (sprintf "Session %s no longer registered" (SessionId.value id))
            return state

        | SessionCommand.GetSession(id, reply) ->
          reply.Reply(ManagerState.tryGetSession id state)
          return state

        | SessionCommand.ListSessions reply ->
          // Return CQRS snapshot directly — no live HTTP calls inside the mailbox.
          // Status is kept current by the poll-until-Ready loop on WorkerReady.
          // Danger: Adding reads inside the mailbox loop causes p99 > 200ms during slow writes.
          reply.Reply(ManagerState.allInfos state)
          return state

        | SessionCommand.TouchSession id ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let updated =
              { session with
                  Info =
                    { session.Info with
                        LastActivity = DateTime.UtcNow } }
            let newState = ManagerState.addSession id updated state
            return newState
          | None ->
            return state

        | SessionCommand.WorkerReady(id, workerPid, baseUrl, proxy) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            // Stale-ready guard (mirrors WorkerExited/WorkerSpawnFailed): during a
            // spawn-first restart the OLD session is parked in PendingSwap with
            // its pid still registered. A late WorkerReady from the retired
            // worker (carrying the OLD pid) must never commit — it would point
            // the registry back at the dying process and clear the pending swap,
            // after which the NEW worker's ready would be ignored as "stale" and
            // the session would be left serving a dead worker. The pid decision
            // is the single source of truth in WorkerEventGuard, shared with
            // WorkerSpawnFailed/WorkerExited so the guard can never drift.
            let currentPid = SessionLifecycleStatus.workerPid session.Info.Status
            let pendingSwapPid =
              ManagerState.tryGetPendingSwap id state
              |> Option.bind (fun oldSession -> SessionLifecycleStatus.workerPid oldSession.Info.Status)
            match WorkerEventGuard.classifyReady currentPid pendingSwapPid workerPid with
            | WorkerEventGuard.ReadyDecision.IgnoreStale ->
              Log.warn "[SessionManager] Ignoring stale WorkerReady for session %s (event pid %d != current pid)" (SessionId.value id) workerPid
              return state
            | WorkerEventGuard.ReadyDecision.Commit ->
              match hasValidReadyTransport baseUrl proxy with
              | false ->
                let msg = describeInvalidReadyTransport "Worker" baseUrl proxy
                do! runtime.StopWorker session
                let faulted = faultedTombstone (Some msg) session
                let newState =
                  { ManagerState.addSession id faulted state with
                      WarmupProgress = Map.remove id state.WarmupProgress }
                onSessionReady id
                onSessionFaulted id msg
                return newState
              | true ->
                let workerPort =
                  let mutable u : System.Uri = null
                  match System.Uri.TryCreate(baseUrl, System.UriKind.Absolute, &u) with
                  | true when u.Port > 0 -> Some u.Port
                  | _ -> None
                // Commit point for a spawn-first restart: when the old worker is
                // parked in PendingSwap, point the registry at the NEW pid FIRST
                // (so the old worker's eventual exit event is stale/inert — P2),
                // then install the new transport, then retire the old worker and
                // clear the pending entry. If no swap is pending this is a plain
                // create/rebuild-recovery WorkerReady and pid/transport install
                // is the same as before.
                // Committing a spawn-first swap retires the old worker, and any
                // app it hosted with it.
                let app =
                  match ManagerState.tryGetPendingSwap id state with
                  | Some _ -> AppRun.acrossWorkerRestart session.Info.App
                  | None -> session.Info.App
                let updated =
                  { session with
                      Proxy = proxy
                      WorkerBaseUrl = baseUrl
                      Info =
                        { session.Info with
                            Status = SessionLifecycleStatus.Starting { Pid = workerPid; Port = workerPort }
                            App = app } }
                let stateAfterInstall =
                  { ManagerState.addSession id updated state with
                      WarmupProgress = Map.remove id state.WarmupProgress }
                let newState =
                  match ManagerState.tryGetPendingSwap id state with
                  | Some oldSession ->
                    // Retire the old worker off the critical path; its exit event
                    // now carries a pid that no longer matches Info.WorkerPid, so
                    // WorkerExited will ignore it (stale-pid guard).
                    //
                    // Reaping the outgoing worker is SAFETY-CRITICAL: run it on a
                    // dedicated thread, NOT Async.Start (the thread pool). Under
                    // pool saturation / memory pressure a pool-queued retirement
                    // can be starved indefinitely, leaking the outgoing worker
                    // exactly when memory is scarcest — repeated hard_resets under
                    // load then pile up multi-GB of un-reaped workers (observed
                    // 2026-09-15). A dedicated background thread can't be starved
                    // behind other pool work. StopWorker is invoked SYNCHRONOUSLY
                    // (registering the retirement intent before the swap returns);
                    // its awaitable — which ends in proc.Kill on the real path —
                    // runs to completion on the dedicated thread.
                    let retireAsync = runtime.StopWorker oldSession
                    let retire () =
                      try Async.RunSynchronously retireAsync
                      with ex ->
                        Log.warn "[SessionManager] Old-worker retirement failed for %s: %s" (SessionId.value id) ex.Message
                    let thread = System.Threading.Thread(System.Threading.ThreadStart retire)
                    thread.IsBackground <- true
                    thread.Name <- sprintf "sagefs-retire-%s" (SessionId.value id)
                    thread.Start()
                    ManagerState.clearPendingSwap id stateAfterInstall
                  | None ->
                    stateAfterInstall
                onSessionReady id
                // Poll worker until it reports Ready, then update snapshot.
                // Uses while loop with CT check to stop cleanly on daemon shutdown
                // or when the session terminates before becoming Ready.
                // Watchdog: faults the session if it hasn't become Ready within the timeout.
                Async.Start(async {
                  let mutable done' = false
                  let started = DateTime.UtcNow
                  let timeout = Timeouts.warmupReadyPollMax
                  while not done' && not ct.IsCancellationRequested do
                    do! Async.Sleep 1000
                    let elapsed = DateTime.UtcNow - started
                    match elapsed > timeout with
                    | true ->
                      let reason = sprintf "Session warmup timed out after %.0fs — worker did not reach Ready state. Use hard_reset_fsi_session with rebuild=true to retry." elapsed.TotalSeconds
                      Log.warn "[SessionManager] %s (session %s)" reason (SessionId.value id)
                      inbox.Post(SessionCommand.UpdateSessionStatus(id, SessionLifecycleStatus.Faulted None))
                      onSessionFaulted id reason
                      done' <- true
                    | false ->
                      try
                        let rid = Guid.NewGuid().ToString("N").[..7]
                        let! resp = proxy (WorkerMessage.GetStatus rid)
                        match resp with
                        | WorkerResponse.StatusResult(_, snapshot) ->
                          match snapshot.Status with
                          | SessionStatus.Ready ->
                            inbox.Post(SessionCommand.WorkerReportedReady(id, workerPid, snapshot.Projects))
                            done' <- true
                          | SessionStatus.Faulted
                          | SessionStatus.Stopped ->
                            let reason =
                              snapshot.StatusMessage
                              |> Option.defaultValue "The worker failed during warmup. → Check the daemon log, then hard-reset the session with rebuild=true."
                            inbox.Post(SessionCommand.WorkerReportedFaulted(id, workerPid, reason))
                            done' <- true
                          // still warming up — keep polling.
                          | SessionStatus.Starting
                          | SessionStatus.Evaluating
                          | SessionStatus.Building _
                          | SessionStatus.Restarting -> ()
                        // default policy: this poll only cares about a StatusResult
                        // reply to its own GetStatus request; WorkerResponse is an
                        // 18-case wire DU shared by every request/response pair in
                        // the protocol, and any other reply here is simply not what
                        // was asked for, whatever future cases it grows.
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
                    inbox.Post(SessionCommand.WorkerTestDiscovery(id, TestDiscoveryReport.ofResponse resp))
                  with
                  | :? OperationCanceledException -> ()
                  | ex ->
                    Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "test_discovery" :> obj))
                    Log.error "[SessionManager] Test discovery failed for %s: %s\n%s" (SessionId.value id) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
                    inbox.Post(SessionCommand.WorkerTestDiscovery(id, TestDiscoveryReport.DiscoveryFailed ex.Message))
                }, ct)
                // Fetch instrumentation maps from the worker
                Async.Start(async {
                  try
                    let rid = System.Guid.NewGuid().ToString("N")
                    let! resp = proxy (WorkerMessage.GetInstrumentationMaps rid)
                    match resp with
                    | WorkerResponse.InstrumentationMapsResult(_, maps) when not (Array.isEmpty maps) ->
                      onInstrumentationMaps id maps
                    // default policy: an empty maps array, or any WorkerResponse
                    // other than InstrumentationMapsResult, has nothing to publish
                    // — WorkerResponse is the same 18-case wire DU as above.
                    | _ -> ()
                  with ex ->
                    Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "instrumentation_maps" :> obj))
                    Log.error "[SessionManager] Instrumentation maps fetch failed for %s: %s\n%s" (SessionId.value id) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
                }, ct)
                return newState
          | None ->
            // Session was stopped before port discovery completed — ignore
            return state

        | SessionCommand.WorkerTestDiscovery(id, report) ->
          onTestDiscovery id report
          return state

        | SessionCommand.WorkerSpawnFailed(id, workerPid, msg) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            // The pid decision is the single source of truth in WorkerEventGuard,
            // shared with WorkerReady/WorkerExited. RevertSwap: a spawn-first
            // restart is in progress and the failure is for the NEW (pending)
            // worker — its pid differs from the registered session's pid (still
            // the OLD, still-serving worker) — so fail-closed (P3/P4) by reverting
            // the swap: restore the old session and clear the pending entry.
            // IgnoreStale: a straggler spawn-failure from a replaced worker (a
            // hard reset killed the old process while its awaitWorkerPort task was
            // still reading stdout; on EOF it posts WorkerSpawnFailed, which would
            // tombstone the FRESH worker). Fault: the current worker failed.
            let currentPid = SessionLifecycleStatus.workerPid session.Info.Status
            let pendingSwapPid =
              ManagerState.tryGetPendingSwap id state
              |> Option.bind (fun oldSession -> SessionLifecycleStatus.workerPid oldSession.Info.Status)
            match WorkerEventGuard.classifySpawnFailed currentPid pendingSwapPid workerPid with
            | WorkerEventGuard.SpawnFailedDecision.RevertSwap ->
              match ManagerState.tryGetPendingSwap id state with
              | Some oldSession ->
                Log.warn "[SessionManager] Replacement worker spawn failed for session %s; reverting to the still-serving old worker: %s" (SessionId.value id) msg
                let newState =
                  ManagerState.clearPendingSwap id
                    { ManagerState.addSession id oldSession state with
                        WarmupProgress = Map.remove id state.WarmupProgress }
                onSessionReady id
                return newState
              | None ->
                // Unreachable: RevertSwap is only returned when a swap is pending.
                Log.warn "[SessionManager] Worker spawn failed for session %s: %s" (SessionId.value id) msg
                let updated = faultedTombstone (Some msg) session
                let newState = ManagerState.addSession id updated state
                onSessionReady id
                onSessionFaulted id msg
                return newState
            | WorkerEventGuard.SpawnFailedDecision.IgnoreStale ->
              Log.warn "[SessionManager] Ignoring stale WorkerSpawnFailed for session %s (event pid %d != current pid %A)" (SessionId.value id) workerPid currentPid
              return state
            | WorkerEventGuard.SpawnFailedDecision.Fault ->
              Log.warn "[SessionManager] Worker spawn failed for session %s: %s" (SessionId.value id) msg
              let updated = faultedTombstone (Some msg) session
              let newState = ManagerState.addSession id updated state
              onSessionReady id  // notify clients of Faulted state change
              onSessionFaulted id msg
              return newState
          | None ->
            return state

        | SessionCommand.WorkerExited(id, workerPid, exitCode) ->
          let span = Instrumentation.startSpan Instrumentation.sessionSource "worker.exited"
                       [("session.id", box id); ("worker.pid", box workerPid); ("exit_code", box exitCode)]
          match ManagerState.tryGetSession id state with
          | Some session ->
            // During a spawn-first restart (pending swap), the OLD worker's exit
            // is expected — it is being retired once the new worker reports
            // Ready (or the swap reverts). Treat its exit as inert so it can
            // never be mistaken for a real crash of the registered session.
            // Stale exits from an already-replaced worker (e.g. after
            // RestartSession) and synthetic NotifyWorkerDied events (workerPid
            // = -1) are inert too. The pid decision is the single source of
            // truth in WorkerEventGuard, shared with WorkerReady/WorkerSpawnFailed.
            let currentPid = SessionLifecycleStatus.workerPid session.Info.Status
            let pendingSwapPid =
              ManagerState.tryGetPendingSwap id state
              |> Option.bind (fun oldSession -> SessionLifecycleStatus.workerPid oldSession.Info.Status)
            let markStaleSpan () =
              match isNull span with
              | false -> span.SetTag("stale_event", true) |> ignore
              | true -> ()
              Instrumentation.succeedSpan span
            match WorkerEventGuard.classifyExited currentPid pendingSwapPid workerPid with
            | WorkerEventGuard.ExitDecision.IgnoreRetired ->
              Log.warn "[SessionManager] Ignoring retired worker exit for session %s during spawn-first restart (pid %d)" (SessionId.value id) workerPid
              markStaleSpan ()
              return state
            | WorkerEventGuard.ExitDecision.IgnoreStale ->
              markStaleSpan ()
              return state
            | WorkerEventGuard.ExitDecision.Apply ->
            let outcome =
              SessionLifecycle.onWorkerExited
                state.RestartPolicy
                session.RestartState
                exitCode
                DateTime.UtcNow
            let newStatus = SessionLifecycle.statusAfterExit (Some workerPid) outcome
            match outcome with
            | SessionLifecycle.ExitOutcome.Graceful ->
              match isNull span with
              | false -> span.SetTag("outcome", "graceful") |> ignore
              | true -> ()
              Instrumentation.activeSessions.Add(-1L)
              Instrumentation.succeedSpan span
              let newState = ManagerState.removeSession id state
              return newState
            | SessionLifecycle.ExitOutcome.Abandoned _ ->
              match isNull span with
              | false -> span.SetTag("outcome", "abandoned") |> ignore
              | true -> ()
              Instrumentation.activeSessions.Add(-1L)
              Instrumentation.succeedSpan span
              let reason = sprintf "Worker process exited with code %d (abandoned after max retries)" exitCode
              let tombstone = faultedTombstone (Some reason) session
              let newState = ManagerState.addSession id tombstone state
              onSessionReady id
              onSessionFaulted id reason
              return newState
            | SessionLifecycle.ExitOutcome.RestartAfter(delay, newRestartState) ->
              match isNull span with
              | false ->
                span.SetTag("outcome", "restart_scheduled") |> ignore
                span.SetTag("restart.delay_ms", delay.TotalMilliseconds) |> ignore
              | true -> ()
              Instrumentation.succeedSpan span
              let updated =
                { session with
                    Proxy = pendingProxy
                    WorkerBaseUrl = ""
                    RestartState = newRestartState
                    Info = { session.Info with Status = newStatus } }
              let newState = ManagerState.addSession id updated state
              Async.Start(async {
                do! Async.Sleep(int delay.TotalMilliseconds)
                inbox.Post(SessionCommand.ScheduleRestart id)
              }, ct)
              return newState
          | None ->
            Instrumentation.succeedSpan span
            return state

        | SessionCommand.ScheduleRestart id ->
          let recoverySpan =
            Instrumentation.startSpan Instrumentation.sessionSource "session.crash_recovery"
              [("session.id", box id)]
          match ManagerState.tryGetSession id state with
          // Crash-recovery must NOT respawn while a cold rebuild of the same
          // session is in flight — the RebuildCompleted handler is the single
          // respawn point, and a worker spawned here would be orphaned/raced by
          // the rebuild's own replacement.
          | Some session when isRestarting session.Info.Status
                              && (ManagerState.tryGetRebuildChannel id state |> Option.isSome) ->
            // Rebuild in flight: do nothing. The off-mailbox build completion
            // owns the respawn. The timer that fired us will not re-fire (the
            // WorkerExited that scheduled it belonged to the pre-rebuild worker).
            match isNull recoverySpan with
            | false -> recoverySpan.SetTag("recovery.outcome", "deferred_to_rebuild") |> ignore
            | true -> ()
            Instrumentation.succeedSpan recoverySpan
            return state
          | Some session when isRestarting session.Info.Status ->
            let onExited workerPid exitCode =
              inbox.Post(SessionCommand.WorkerExited(id, workerPid, exitCode))
            match runtime.StartWorkerProcess id session.Projects session.WorkingDir session.AutoOpenNamespaces session.Workflow onExited with
            | Ok spawned ->
              let proc = spawned.Process
              let restarted =
                { session with
                    Process = proc
                    Proxy = pendingProxy
                    WorkerBaseUrl = ""
                    AdoptedCore = spawned.AdoptedCore
                    Info =
                      { session.Info with
                          Status = SessionLifecycleStatus.Starting { Pid = proc.Id; Port = None }
                          LastActivity = DateTime.UtcNow } }
              let newState = ManagerState.addSession id restarted state
              runtime.AwaitWorkerPort id proc inbox ct
              match isNull recoverySpan with
              | false -> recoverySpan.SetTag("recovery.outcome", "restarted") |> ignore
              | true -> ()
              Instrumentation.succeedSpan recoverySpan
              return newState
            | Error err ->
              // Spawn failed — treat as another crash
              let outcome =
                SessionLifecycle.onWorkerExited
                  state.RestartPolicy
                  session.RestartState
                  1
                  DateTime.UtcNow
              match outcome with
              | SessionLifecycle.ExitOutcome.Abandoned _ ->
                match isNull recoverySpan with
                | false -> recoverySpan.SetTag("recovery.outcome", "abandoned") |> ignore
                | true -> ()
                Instrumentation.succeedSpan recoverySpan
                let reason = SageFsError.describe err
                let tombstone = faultedTombstone (Some reason) session
                let newState = ManagerState.addSession id tombstone state
                onSessionReady id
                onSessionFaulted id reason
                return newState
              | SessionLifecycle.ExitOutcome.Graceful ->
                match isNull recoverySpan with
                | false -> recoverySpan.SetTag("recovery.outcome", "graceful") |> ignore
                | true -> ()
                Instrumentation.succeedSpan recoverySpan
                let newState = ManagerState.removeSession id state
                return newState
              | SessionLifecycle.ExitOutcome.RestartAfter(delay, newRestartState) ->
                match isNull recoverySpan with
                | false ->
                  recoverySpan.SetTag("recovery.outcome", "retry_scheduled") |> ignore
                  recoverySpan.SetTag("recovery.retry_delay_ms", delay.TotalMilliseconds) |> ignore
                | true -> ()
                Instrumentation.succeedSpan recoverySpan
                let updated =
                  { session with
                      Proxy = pendingProxy
                      WorkerBaseUrl = ""
                      RestartState = newRestartState }
                let newState = ManagerState.addSession id updated state
                Async.Start(async {
                  do! Async.Sleep(int delay.TotalMilliseconds)
                  inbox.Post(SessionCommand.ScheduleRestart id)
                }, ct)
                return newState
          | _ ->
            Instrumentation.succeedSpan recoverySpan
            return state

        | SessionCommand.StopAll reply ->
          // Graceful shutdown of all sessions — run in parallel to avoid N×5s
          // sequential timeout during shutdown.
          let sessionTasks =
            [ for KeyValue(_, session) in state.Sessions -> runtime.StopWorker session ]
          do! sessionTasks |> Async.Parallel |> Async.Ignore
          reply.Reply(())
          return ManagerState.empty

        | SessionCommand.WorkerWarmupProgress(id, progress) ->
          let newState =
            { state with WarmupProgress = Map.add id progress state.WarmupProgress }
          onSessionProgressChanged ()
          onWarmupProgress id progress
          return newState

        | SessionCommand.WorkerAppOutput(id, line) ->
          onAppOutput id line  // route a running app's stdout to session output (#82)
          return state

        | SessionCommand.UpdateSessionStatus(id, newStatus) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            // Faulted None means "no explicit reason given" — keep whatever
            // reason the session already carries, or fall back to a default.
            let resolvedStatus =
              match newStatus with
              | SessionLifecycleStatus.Faulted None ->
                let existing = SessionLifecycleStatus.faultReason session.Info.Status
                SessionLifecycleStatus.Faulted (existing |> Option.orElse (Some "Session warmup timed out — worker did not reach Ready state."))
              // default policy: every SessionLifecycleStatus other than
              // `Faulted None` is used verbatim — the only special case this
              // command handles is "faulted with no reason given"; any other
              // status (present or future) is a plain pass-through by
              // definition, never a decision that needs re-review.
              | _ -> newStatus
            let updated =
              { session with Info = { session.Info with Status = resolvedStatus } }
            let newState = ManagerState.addSession id updated state
            onSessionProgressChanged ()
            return newState
          | None ->
            return state
        | SessionCommand.WorkerReportedFaulted(id, workerPid, reason) ->
          // Only the session's current worker may fault it (see WorkerReportedReady).
          match ManagerState.tryGetSession id state, ManagerState.tryGetPendingSwap id state with
          | Some session, None when SessionLifecycleStatus.workerPid session.Info.Status = Some workerPid ->
            Log.warn "[SessionManager] Worker for session %s faulted during warmup: %s" (SessionId.value id) reason
            let newState = ManagerState.addSession id (faultedTombstone (Some reason) session) state
            onSessionFaulted id reason
            onSessionProgressChanged ()
            return newState
          | _ ->
            Log.warn "[SessionManager] Ignoring a fault from worker pid %d for session %s: it is no longer the session's worker" workerPid (SessionId.value id)
            return state
        | SessionCommand.WorkerReportedReady(id, workerPid, roles) ->
          // Only the session's current worker may declare it Ready: a ready poll
          // that outlives a spawn-first swap is reporting for the retired worker,
          // and its Ready would release AwaitReady into a session with no worker.
          let current =
            match ManagerState.tryGetSession id state, ManagerState.tryGetPendingSwap id state with
            | Some session, None when SessionLifecycleStatus.workerPid session.Info.Status = Some workerPid -> Some session
            | _ -> None
          match current with
          | Some session ->
            let handle : WorkerHandle = { Pid = workerPid; Port = SessionLifecycleStatus.workerPort session.Info.Status }
            let updated =
              { session with
                  ProjectRoles = roles
                  Info = { session.Info with Status = SessionLifecycleStatus.Ready handle; ProjectRoles = roles } }
            let newState = ManagerState.addSession id updated state
            onSessionProgressChanged ()
            // Roast-6 #3: start the per-worker health-probe loop now that the
            // worker is confirmed Ready (session.Proxy is installed and
            // answering). `shouldContinue` reads the wait-free published
            // snapshot — the same CQRS discipline every other read in this
            // module uses, never the mailbox — so the loop notices for
            // itself once this pid stops being the session's current worker
            // (a normal restart, hard reset, or stop) and simply exits: it
            // never restarts a worker it no longer owns.
            let probeProxy = session.Proxy
            let probeShouldContinue () =
              match Map.tryFind id snapshotRef.Value.Sessions with
              | Some info -> SessionLifecycleStatus.workerPid info.Status = Some workerPid
              | None -> false
            let onHealthRestart () =
              match probeShouldContinue () with
              | false -> ()
              | true ->
                Log.warn "[SessionManager] Worker pid %d for session %s missed %d consecutive health checks — restarting" workerPid (SessionId.value id) WorkerHealthProbe.defaultThreshold
                killWorkerPids [ workerPid ]
                // Mirrors NotifyWorkerDied (DaemonMode.fs): posting pid=-1
                // always falls into WorkerExited's "handle as real exit"
                // branch regardless of the session's CURRENT recorded pid,
                // and poisons Status to Restarting(Some -1) — so the
                // worker's own genuine Process.Exited event (which
                // killWorkerPids above will trigger, carrying the REAL pid)
                // is then recognized as stale by the pid-mismatch guard and
                // ignored, closing the same double-restart race
                // NotifyWorkerDied already closes.
                inbox.Post(SessionCommand.WorkerExited(id, -1, -1))
            Async.Start(
              WorkerHealthProbe.run
                (fun () -> probeWorkerHealthOnce WorkerHealthProbe.defaultProbeTimeoutMs probeProxy)
                WorkerHealthProbe.defaultThreshold
                WorkerHealthProbe.defaultProbeIntervalMs
                probeShouldContinue
                onHealthRestart,
              ct)
            return newState
          | None ->
            Log.warn "[SessionManager] Ignoring Ready from worker pid %d for session %s: it is no longer the session's worker" workerPid (SessionId.value id)
            return state
        // The app's single owner: every Run, Stop and run step is decided here,
        // against the state and generation this mailbox holds (AppRun.AppSlot).
        | SessionCommand.ClaimRun(id, project, reply) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let phase = AppRunOrchestration.startPhaseFor session.Info.Status session.Workflow
            let claim, slot = AppRun.AppSlot.claimRun project phase DateTime.UtcNow (appSlotOf session)
            reply.Reply(Ok claim)
            onSessionProgressChanged ()
            return ManagerState.addSession id (withAppSlot slot session) state
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound (SessionId.value id)))
            return state
        | SessionCommand.ClaimStop(id, reply) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let claim, slot = AppRun.AppSlot.claimStop (appSlotOf session)
            reply.Reply(Ok claim)
            onSessionProgressChanged ()
            return ManagerState.addSession id (withAppSlot slot session) state
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound (SessionId.value id)))
            return state
        | SessionCommand.AdvanceRun(id, generation, next, reply) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let outcome, slot = AppRun.AppSlot.advance generation next (appSlotOf session)
            reply.Reply outcome
            match outcome with
            | AppRun.StepOutcome.Applied ->
              // The project that last ran is the one Run picks next time.
              // default policy: ActiveProject only moves when the app is
              // actually Running — AppRunState is a 9-case DU (NotRunning/
              // Starting/Exited/Crashed/CouldNotStart/RestartRequired/
              // BuildFailed/LostTrack besides Running) and every one of them
              // means "nothing new is running," so keeping the existing
              // ActiveProject is the correct default for any of them,
              // present or future.
              let activeProject =
                match next with
                | AppRun.AppRunState.Running app -> Some app.Project
                | _ -> session.Info.ActiveProject
              let updated = withAppSlot slot session
              onSessionProgressChanged ()
              return ManagerState.addSession id { updated with Info = { updated.Info with ActiveProject = activeProject } } state
            | AppRun.StepOutcome.Stale _ -> return state
          | None ->
            reply.Reply(AppRun.StepOutcome.Stale AppRun.AppRunState.NotRunning)
            return state
        | SessionCommand.EndAppRun(id, generation, runId, final, reply) ->
          match ManagerState.tryGetSession id state with
          | Some session ->
            let ended, slot = AppRun.AppSlot.endRun generation runId final (appSlotOf session)
            reply.Reply ended
            onSessionProgressChanged ()
            return ManagerState.addSession id (withAppSlot slot session) state
          | None ->
            reply.Reply AppRun.RunEnd.NotCurrent
            return state
        | SessionCommand.AwaitReady(id, reply) ->
          // Parked here and settled after every step (settleReadyWaiters), so
          // any path that makes the session Ready or fails it answers the caller.
          let waiting = state.ReadyWaiters |> Map.tryFind id |> Option.defaultValue []
          return { state with ReadyWaiters = Map.add id (reply :: waiting) state.ReadyWaiters }
        | SessionCommand.SwitchWorkflow(id, workflow, reply) ->
          match ManagerState.tryGetSession id state with
          | Some session when session.Workflow = workflow ->
            reply.Reply(Ok (sprintf "Session is already in %A mode" workflow))
            return state
          | Some _ when ManagerState.tryGetRebuildChannel id state |> Option.isSome ->
            reply.Reply(Error (SageFsError.HardResetFailed "A rebuild is in progress for this session. → Switch the workflow after it finishes."))
            return state
          | Some session ->
            // The workflow only takes effect in a fresh worker (hot reload
            // installs at worker start): restart spawn-first INTO it. The new
            // workflow is recorded only if that spawn succeeds, so a failed
            // switch never claims a workflow the serving worker does not have.
            let span =
              Instrumentation.startSpan Instrumentation.sessionSource "session.switch_workflow" [("session.id", box id)]
            let newState = spawnFirst state id session workflow reply "Hard reset accepted — replacement worker spawning." span
            onSessionProgressChanged ()
            return newState
          | None ->
            reply.Reply(Error (SageFsError.SessionNotFound (WorkerProtocol.SessionId.value id)))
            return state
      }
      /// Supervision wrapper: an UNEXPECTED exception escaping a handler must
      /// not kill the mailbox (that would orphan every session — no snapshot
      /// updates, no stops, no restarts). Handlers that legitimately report
      /// failure do so through reply.Reply(Error ...) (the fail-closed path);
      /// this wrapper is only the backstop for bugs/transient faults. The loop
      /// continues with the last known good state so the CQRS snapshot stays
      /// consistent and nothing is silently orphaned.
      and superviseStep (state: ManagerState) (cmd: SessionCommand) : Async<ManagerState> = async {
        try
          return! step state cmd
        with
        | :? OperationCanceledException -> return! raise (OperationCanceledException())
        | ex ->
          Log.error "[SessionManager] Unhandled exception processing command %A, continuing with previous state: %s\n%s"
            (cmd.GetType().Name) ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
          Instrumentation.actorErrors.Add(
            1L,
            System.Collections.Generic.KeyValuePair("actor.name", "session-manager" :> obj))
          // Fail-closed: if the crashing handler owned a reply channel, answer
          // it with a SageFsError so the caller never hangs forever waiting on
          // a mailbox that has moved on. Each reply is guarded: a handler that
          // already replied before throwing must not let the guard reply throw
          // (that would kill the mailbox, defeating the supervision).
          let tryReply (ch: AsyncReplyChannel<_>) (value: _) =
            try ch.Reply(value) with _ -> ()
          match cmd with
          | SessionCommand.StopSession(id, reply) ->
            tryReply reply (Error (SageFsError.SessionStopFailed (SessionId.value id, ex.Message)))
          | SessionCommand.RestartSession(id, _, reply) ->
            tryReply reply (Error (SageFsError.HardResetFailed (sprintf "Hard reset failed: %s" ex.Message)))
          | SessionCommand.CreateSession(_, _, _, _, reply) ->
            tryReply reply (Error (SageFsError.SessionCreationFailed ex.Message))
          | SessionCommand.RebuildCompleted(id, _, reply) ->
            // The parked channel and the carried channel are the same object in
            // the normal flow; answer whichever is still unresolved, exactly once.
            match ManagerState.tryGetRebuildChannel id state with
            | Some parked -> tryReply parked (Error (SageFsError.HardResetFailed (sprintf "Hard reset failed: %s" ex.Message)))
            | None -> tryReply reply (Error (SageFsError.HardResetFailed (sprintf "Hard reset failed: %s" ex.Message)))
          | SessionCommand.GetSession(id, reply) ->
            tryReply reply (ManagerState.tryGetSession id state)
          | SessionCommand.ListSessions reply ->
            tryReply reply (ManagerState.allInfos state)
          | SessionCommand.StopAll reply ->
            tryReply reply ()
          | SessionCommand.AwaitReady(_, reply) ->
            tryReply reply (Error (SageFsError.Unexpected ex))
          | SessionCommand.ClaimRun(_, _, reply) ->
            tryReply reply (Error (SageFsError.Unexpected ex))
          | SessionCommand.ClaimStop(_, reply) ->
            tryReply reply (Error (SageFsError.Unexpected ex))
          | SessionCommand.AdvanceRun(id, _, _, reply) ->
            let current =
              ManagerState.tryGetSession id state
              |> Option.map (fun s -> s.Info.App)
              |> Option.defaultValue AppRun.AppRunState.NotRunning
            tryReply reply (AppRun.StepOutcome.Stale current)
          | SessionCommand.EndAppRun(_, _, _, _, reply) ->
            tryReply reply AppRun.RunEnd.NotCurrent
          // SwitchWorkflow carries a reply channel (AsyncReplyChannel<Result<
          // string, SageFsError>>) and MUST be answered on a crash — an
          // exception in its handler (it spawn-first restarts the worker into
          // the new workflow) otherwise left the caller hanging forever, unlike
          // every other reply-carrying command above. Fail closed with the same
          // HardResetFailed shape the handler's own error paths use, since a
          // workflow switch IS a restart (roast-7 §5 follow-up).
          | SessionCommand.SwitchWorkflow(_, _, reply) ->
            tryReply reply (Error (SageFsError.HardResetFailed (sprintf "Workflow switch failed: %s" ex.Message)))
          // Every command below carries no reply channel to guard.
          | SessionCommand.TouchSession _
          | SessionCommand.WorkerExited _
          | SessionCommand.WorkerReady _
          | SessionCommand.WorkerTestDiscovery _
          | SessionCommand.WorkerSpawnFailed _
          | SessionCommand.ScheduleRestart _
          | SessionCommand.WorkerWarmupProgress _
          | SessionCommand.WorkerAppOutput _
          | SessionCommand.UpdateSessionStatus _
          | SessionCommand.WorkerReportedReady _
          | SessionCommand.WorkerReportedFaulted _ -> ()
          return state
      }
      // Supervise the supervisor: per-message superviseStep + the guarded
      // scaffolding above mean the loop should only ever exit on cancellation.
      // But if anything unforeseen still escapes, restart from the last-good
      // state (sessions preserved) rather than orphaning every session forever.
      let rec supervise () = async {
        try
          return! loop lastGoodState.Value
        with
        | :? OperationCanceledException -> ()  // cancellation stops the supervisor, as intended
        | ex ->
          Log.error "[SessionManager] Mailbox loop threw unexpectedly; restarting from last-good state (sessions preserved): %s\n%s" ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
          Instrumentation.actorErrors.Add(1L, System.Collections.Generic.KeyValuePair("actor.name", "session-manager-loop" :> obj))
          return! supervise ()
      }
      supervise ()
    ), cancellationToken = ct)
    (mailbox, fun () -> snapshotRef.Value)

  let create
    (ct: CancellationToken)
    (onSessionProgressChanged: unit -> unit)
    (onTestDiscovery: SessionId -> TestDiscoveryReport -> unit)
    (onInstrumentationMaps: SessionId -> Features.LiveTesting.InstrumentationMap array -> unit)
    (onSessionReady: SessionId -> unit)
    (onWarmupProgress: SessionId -> string -> unit)
    (onSessionFaulted: SessionId -> string -> unit)
    (onAppOutput: SessionId -> string -> unit) =
    createWith
      defaultRuntime
      ct
      onSessionProgressChanged
      onTestDiscovery
      onInstrumentationMaps
      onSessionReady
      onWarmupProgress
      onSessionFaulted
      onAppOutput
