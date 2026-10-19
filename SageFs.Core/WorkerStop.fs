namespace SageFs

open System.Diagnostics
open SageFs.Utils
open SageFs.WorkerProtocol

/// How a worker process is stopped, and whether one that failed to shut down gracefully still needs killing.
/// Split out of SessionManager.fs, which is at its line budget. The decision is kept pure: the process handle comes
/// in as a probe, so a test needs no real worker.
module WorkerStop =

  type ForceKillDecision =
    /// The worker may still be running, so kill it.
    | ForceKill
    /// The worker is confirmed gone, so there is nothing to kill.
    | AlreadyExited

  /// `hasExited` asks the process handle and may throw (a disposed or never-started handle).
  /// An exception means we cannot tell, and a leaked worker costs more than a kill on a dead one, so it fails
  /// closed to `ForceKill`.
  let needsForceKill (hasExited: unit -> bool) : ForceKillDecision =
    try (if hasExited () then AlreadyExited else ForceKill) with _ -> ForceKill

  let stackOf (ex: exn) : string = ex.StackTrace |> Option.ofObj |> Option.defaultValue ""

  /// Kill the whole process tree and wait a bounded time to see it gone. Kill uses entireProcessTree so any child
  /// processes (dotnet restore, compiler server) spawned by the worker die with it instead of lingering on Windows.
  let killTree (proc: Process) : unit =
    try proc.Kill(entireProcessTree = true) with ex -> Log.warn "[SessionManager] Kill failed: %s\n%s" ex.Message (stackOf ex)
    try proc.WaitForExit(int Timeouts.processKillVerify.TotalMilliseconds) |> ignore with ex -> Log.warn "[SessionManager] WaitForExit after kill: %s\n%s" ex.Message (stackOf ex)

  /// Stop a worker gracefully: send Shutdown with a bounded wait, then kill the whole process tree. The bounded wait
  /// is essential: HttpWorkerClient.httpProxy has no request timeout, so a hung worker would otherwise block daemon
  /// shutdown forever (StopAll times out and the daemon exits, orphaning workers, issue #126).
  let stopProcess (proc: Process) (proxy: SessionProxy) : Async<unit> = async {
    let hasExited = try proc.HasExited with _ -> true
    match hasExited with
    | true ->
      try proc.EnableRaisingEvents <- false with _ -> ()
      try proc.Dispose() with _ -> ()
    | false ->
      try
        let shutdownTask = proxy WorkerMessage.Shutdown |> Async.StartAsTask
        let! completed =
          System.Threading.Tasks.Task.WhenAny(
            [| shutdownTask :> System.Threading.Tasks.Task
               System.Threading.Tasks.Task.Delay(Timeouts.workerShutdownDelay) |])
          |> Async.AwaitTask
        if obj.ReferenceEquals(completed, shutdownTask) then
          let! _ = shutdownTask |> Async.AwaitTask
          ()
        match proc.WaitForExit(int Timeouts.processNormalExit.TotalMilliseconds) with
        | false -> killTree proc
        | true -> ()
      with ex ->
        Log.warn "[SessionManager] Graceful shutdown failed: %s\n%s" ex.Message (stackOf ex)
        match needsForceKill (fun () -> proc.HasExited) with
        | ForceKill -> killTree proc
        | AlreadyExited -> ()
      try proc.EnableRaisingEvents <- false with _ -> ()
      try proc.Dispose() with _ -> ()
  }
