module SageFs.Tests.ShutdownLifecycleTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.Tests.TestInfrastructure

/// Spawn a real, long-running child process that the tests can kill.
/// Uses `cmd /c ping -n N 127.0.0.1` which lives for roughly N-1 seconds.
let spawnLongRunning (label: string) =
  let psi = ProcessStartInfo()
  psi.FileName <- "cmd.exe"
  psi.Arguments <- "/c ping -n 30 127.0.0.1 > nul"
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let proc = new Process()
  proc.StartInfo <- psi
  proc.EnableRaisingEvents <- true
  match proc.Start() with
  | true -> proc
  | false -> failwithf "%s: failed to spawn test process" label

let mkHangingSession (proc: Process) =
  { Info =
      { WorkerProtocol.SessionInfo.Id = WorkerProtocol.SessionId.newId()
        Name = None
        Projects = []
        WorkingDirectory = ""
        SolutionRoot = None
        Status = WorkerProtocol.SessionStatus.Ready
        FaultReason = None
        WorkerPid = Some proc.Id
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow }
    Process = proc
    // A proxy that never responds — simulates a hung worker whose HTTP server
    // is wedged (the real proxy has no request timeout).
    Proxy = fun _ -> async {
      do! Async.Sleep 60000
      return WorkerProtocol.WorkerResponse.WorkerReady }
    WorkerBaseUrl = ""
    Projects = []
    WorkingDir = ""
    AutoOpenNamespaces = false
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    RestartState = RestartPolicy.emptyState }

[<Tests>]
let shutdownLifecycleTests =
  testList "Shutdown lifecycle" [
    testTask "stopWorker terminates a hung worker within a bounded time" {
      // Regression: HttpWorkerClient.httpProxy uses Timeout.InfiniteTimeSpan,
      // so a hung worker made stopWorker hang forever, StopAll timed out, and
      // the daemon exited leaving worker processes orphaned (issue #126).
      let proc = spawnLongRunning "bounded-stop"
      let pid = proc.Id
      let session = mkHangingSession proc
      try
        let stop = SessionManager.stopWorker session |> Async.StartAsTask
        let! winner = Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10.0)))
        obj.ReferenceEquals(winner, stop)
        |> Expect.isTrue
          (sprintf "stopWorker must return within a bound even when the Shutdown proxy hangs (pid %d)" pid)
        do! stop
        let hasExited =
          try
            use running = Process.GetProcessById(pid)
            running.HasExited
          with :? ArgumentException -> true
        hasExited
        |> Expect.isTrue "the hung worker process must be dead after stopWorker"
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
    }

    testTask "killWorkerPids terminates all listed processes" {
      // The daemon's force-exit watchdog sweeps worker PIDs from the last
      // snapshot before exiting; the sweep must kill every listed process.
      let procs = [ spawnLongRunning "sweep-a"; spawnLongRunning "sweep-b" ]
      let pids = procs |> List.map (fun p -> p.Id)
      try
        SessionManager.killWorkerPids pids
        let deadline = DateTime.UtcNow.AddSeconds(5.0)
        while DateTime.UtcNow < deadline && procs |> List.exists (fun p -> not p.HasExited) do
          do! Task.Delay(100)
        procs
        |> List.iter (fun p ->
          p.HasExited
          |> Expect.isTrue (sprintf "sweep must kill pid %d" p.Id))
      finally
        for p in procs do
          try p.Kill(entireProcessTree = true) with _ -> ()
          try p.Dispose() with _ -> ()
    }
  ]
