namespace SageFs

open System
open System.Diagnostics
open System.IO
open System.Threading
open SageFs.WorkerProtocol
open SageFs.Utils
open SageFs.ProjectLoading

/// Starting a worker OS process. Split out of SessionManager.fs, which is over its
/// line budget: nothing here depends on the session manager's state or its command
/// loop, only on the record it returns. SessionManager re-exports both names, so
/// callers that say `SessionManager.startWorkerProcess` are unaffected.
module WorkerSpawn =

  /// A freshly spawned worker OS process, plus the identity of the session
  /// project's own SageFs.Core build adopted into it (`None` when the session
  /// does not self-host SageFs.Core). `AdoptedCore` is captured at spawn — the
  /// ground truth for the self-host staleness signal surfaced in status.
  type SpawnedWorker = {
    Process: Process
    AdoptedCore: (string * DateTime) option
  }

  /// Start a worker OS process. Returns immediately with the Process
  /// (does NOT wait for the worker to report its port).
  let startWorkerProcess
    (sessionId: SessionId)
    (targets: SessionProjectTarget list)
    (workingDir: string)
    (autoOpenNamespaces: bool)
    (workflow: WorkflowTypes.SessionWorkflow)
    (onExited: int -> int -> unit)
    : Result<SpawnedWorker, SageFsError> =
    let args, envVars = Args.buildWorkerSpawnConfig (SessionId.value sessionId) targets false autoOpenNamespaces workflow
    let projects = SessionProjectTarget.paths targets
    // A project built for a newer runtime than the worker host's needs that runtime (roll-forward);
    // one that needs a runtime nobody installed is refused with what to install, not left to fail
    // warmup with a bare "assembly not referenced".
    // Every session is Isolated: the user's code runs in the FSI host (which is launched on the runtime the
    // project needs), so the worker itself has nothing to roll forward for.
    let runtimeChoice = RuntimeCompat.HostFits
    match runtimeChoice with
    | RuntimeCompat.RuntimeMissing _ -> Error (SageFsError.WorkerSpawnFailed (RuntimeCompat.describe runtimeChoice))
    | _ ->
    (match runtimeChoice with
     | RuntimeCompat.RollForward _ -> Log.info "[SessionManager] session %s: %s" (SessionId.value sessionId) (RuntimeCompat.describe runtimeChoice)
     | _ -> ())
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
    // Visibility (roast: a materialized private launch root costs 680-835MB
    // of /tmp per self-hosting session — worth logging as it happens, not
    // just when a startup/periodic sweep later finds it abandoned).
    hostCleanup
    |> Option.iter (fun _ ->
      let sizeMb = float (OrphanTempDirSweep.directorySizeBytes launchRoot) / 1024.0 / 1024.0
      Log.info
        "[SessionManager] session %s: adopted a private SageFs.Core build into %s (%.0fMB)"
        (SessionId.value sessionId) launchRoot sizeMb)
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

    // Propagate session config as env vars so worker startup stays independent
    // of daemon CLI flags, and strip MSBuild-resolution variables this daemon
    // may have picked up so the worker and the isolated FSI host it spawns
    // never inherit them (SageFs.ProcessEnvironment).
    //
    // `applyToWithForwarding` also forwards any environment a tool has asked to
    // pass through to every spawn (SAGEFS_FORWARD_PREFIXES), so an external
    // agent can reach a worker it does not launch.
    //
    // The worker is told this daemon's machine tier, so every wait it scales (the FSI host build, the
    // host's start, its own probes) agrees with the daemon's instead of reading the machine again.
    SageFs.ProcessEnvironment.applyToWithForwarding
      psi
      (envVars @ RuntimeCompat.rollForwardEnv runtimeChoice @ [ (MachineTier.envVar, MachineTier.toString Timeouts.machineTier) ])

    let proc = new Process()
    proc.StartInfo <- psi
    proc.EnableRaisingEvents <- true

    match proc.Start() with
    | false ->
      hostCleanup |> Option.iter (fun cleanup -> try cleanup () with _ -> ())
      Error (SageFsError.WorkerSpawnFailed "Failed to start worker process")
    | true ->
      let workerPid = proc.Id
      // Record this worker as the owner of its private launch root (when one
      // was materialized) as soon as its pid is known — BEFORE anything else
      // touches the directory. This is what lets a startup or periodic sweep
      // run by any OTHER SageFs process (including one that starts long
      // after this daemon is hard-killed and never gets to run the
      // `Exited` handler below) prove the root is orphaned instead of
      // guessing. See HostCoreAdoption.sweepStaleAdoptedRoots.
      hostCleanup |> Option.iter (fun _ -> HostCoreAdoption.markAdoptedRootOwner launchRoot workerPid)
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


  /// Run a blocking action on a dedicated background thread, never a
  /// thread-pool thread. Returns a Task that completes when the action
  /// returns, so a caller can `Async.AwaitTask` it exactly like a
  /// `Task.Run` result — without pinning a pool thread for the action's
  /// entire lifetime.
  ///
  /// WHY: a long-lived blocking `proc.StandardError/Output.ReadLine()` loop
  /// wrapped in `Task.Run` pins a real thread-pool thread for as long as
  /// the loop runs — for a live session's stderr/stdout reader, that is the
  /// session's ENTIRE lifetime. `SessionManager.fs:1126-1145`'s old-worker
  /// retirement already uses a dedicated thread for exactly this reason
  /// (its own comment: "under pool saturation / memory pressure a
  /// pool-queued retirement can be starved indefinitely"); this generalizes
  /// the same fix to `awaitWorkerPort`'s two readers and
  /// `SessionBuild.runOnce`'s two readers, which were the ones actually
  /// observed starving the pool under 5 concurrent session warmups on
  /// 2026-09-22 (`/health`/`/api/sessions` — lock-free, no I/O — timing out
  /// for a full minute; Kestrel logging `heartbeat has been running for
  /// "00:01:00"`). Two pool threads pinned per live session, scaling with
  /// session count and bounded by nothing, is the actual mechanism behind
  /// that lockup and very plausibly behind the onboarding trials' "stuck
  /// for 20 minutes" / "stop_session timed out at 300s" reports too: the
  /// daemon's own 120s safety-net timers are themselves pool continuations,
  /// and a starved pool can delay the very watchdogs meant to catch a stuck
  /// session.
  let runOnDedicatedThread (name: string) (action: unit -> unit) : System.Threading.Tasks.Task =
    let tcs = System.Threading.Tasks.TaskCompletionSource()
    let thread =
      System.Threading.Thread(fun () ->
        try
          action ()
          tcs.SetResult()
        with ex ->
          tcs.SetException(ex))
    thread.IsBackground <- true
    thread.Name <- name
    thread.Start()
    tcs.Task

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

