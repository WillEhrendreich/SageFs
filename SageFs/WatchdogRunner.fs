module SageFs.Server.WatchdogRunner

open System
open System.Diagnostics
open System.Threading
open SageFs
open SageFs.Utils
open SageFs.Watchdog

/// The MCP port the supervised daemon will bind: `--mcp-port <n>` in its args, else the configured
/// default. The watchdog does not bind it, it only uses it to tell daemons apart.
let daemonPort (daemonArgs: string list) : int =
  let rec find (args: string list) =
    match args with
    | "--mcp-port" :: value :: rest ->
      match Int32.TryParse value with
      | true, port -> port
      | false, _ -> find rest
    | _ :: rest -> find rest
    | [] -> SageFsConfig.McpPortFromEnv
  find daemonArgs

/// The jitter seed for one crash of one daemon: the working directory and port identify the daemon,
/// the crash time makes each crash of it different. Two daemons in different directories (or on
/// different ports) get different seeds for the same crash, which is what breaks the herd.
let jitterSeed (workingDirectory: string) (port: int) (crashedAt: DateTime) : RestartPolicy.JitterSeed =
  SessionLifecycle.jitterSeedFor (sprintf "%s:%d" workingDirectory port) crashedAt

/// Impure watchdog runner that monitors and restarts the daemon process.
/// Uses the pure Watchdog module for all decisions.
let run
  (config: Config)
  (daemonArgs: string list)
  (workingDirectory: string)
  (ct: CancellationToken)
  = task {
  let mutable state = emptyState DateTime.UtcNow
  let port = daemonPort daemonArgs

  let checkDaemonStatus (pid: int option) =
    match pid with
    | None -> DaemonStatus.NotRunning
    | Some pid ->
      match DaemonState.isProcessAlive pid with
      | true -> DaemonStatus.Running
      | false -> DaemonStatus.NotRunning

  let startDaemon () =
    let exePath =
      Process.GetCurrentProcess().MainModule.FileName
    let args = String.concat " " daemonArgs
    let psi =
      ProcessStartInfo(
        exePath, args,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false)
    // This process worked its own tier out and published it; the daemon works out its own, from the same profile.
    MachineStartup.withoutDerivedTier psi.Environment
    psi.Environment["SAGEFS_SUPERVISED"] <- "1"
    psi.Environment["SAGEFS_RESTART_COUNT"] <- state.RestartState.RestartCount.ToString()
    let proc = Process.Start(psi)
    Log.info "[watchdog] Started daemon PID %d" proc.Id
    proc.Id

  Log.info "[watchdog] Supervisor started (check interval: %gs, grace period: %gs)"
    config.CheckInterval.TotalSeconds config.GracePeriod.TotalSeconds

  while not ct.IsCancellationRequested do
    let status = checkDaemonStatus state.DaemonPid
    let checkedAt = DateTime.UtcNow
    let action, newState = decide config (jitterSeed workingDirectory port checkedAt) state status checkedAt
    state <- newState

    match action with
    | Action.StartDaemon ->
      let pid = startDaemon ()
      state <- recordStart pid DateTime.UtcNow state
    | Action.RestartDaemon delay ->
      Log.warn "[watchdog] Daemon crashed. Restarting in %gs..." delay.TotalSeconds
      do! Threading.Tasks.Task.Delay(delay, ct)
      let pid = startDaemon ()
      state <- recordStart pid DateTime.UtcNow state
    | Action.Wait -> ()
    | Action.GiveUp reason ->
      Log.error "[watchdog] Giving up: %s" reason
      return ()

    try
      do! Threading.Tasks.Task.Delay(config.CheckInterval, ct)
    with
    | :? OperationCanceledException -> ()
}
