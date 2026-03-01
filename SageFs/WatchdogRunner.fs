module SageFs.Server.WatchdogRunner

open System
open System.Diagnostics
open System.Threading
open SageFs
open SageFs.Watchdog

/// Impure watchdog runner that monitors and restarts the daemon process.
/// Uses the pure Watchdog module for all decisions.
let run
  (config: Config)
  (daemonArgs: string list)
  (workingDirectory: string)
  (ct: CancellationToken)
  = task {
  let mutable state = emptyState DateTime.UtcNow

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
    psi.Environment["SAGEFS_SUPERVISED"] <- "1"
    psi.Environment["SAGEFS_RESTART_COUNT"] <- state.RestartState.RestartCount.ToString()
    let proc = Process.Start(psi)
    eprintfn "[watchdog] Started daemon PID %d" proc.Id
    proc.Id

  eprintfn "[watchdog] Supervisor started (check interval: %gs, grace period: %gs)"
    config.CheckInterval.TotalSeconds config.GracePeriod.TotalSeconds

  while not ct.IsCancellationRequested do
    let status = checkDaemonStatus state.DaemonPid
    let action, newState = decide config state status DateTime.UtcNow
    state <- newState

    match action with
    | Action.StartDaemon ->
      let pid = startDaemon ()
      state <- recordStart pid DateTime.UtcNow state
    | Action.RestartDaemon delay ->
      eprintfn "[watchdog] Daemon crashed. Restarting in %gs..." delay.TotalSeconds
      do! Threading.Tasks.Task.Delay(delay, ct)
      let pid = startDaemon ()
      state <- recordStart pid DateTime.UtcNow state
    | Action.Wait -> ()
    | Action.GiveUp reason ->
      eprintfn "[watchdog] Giving up: %s" reason
      return ()

    try
      do! Threading.Tasks.Task.Delay(config.CheckInterval, ct)
    with
    | :? OperationCanceledException -> ()
}
