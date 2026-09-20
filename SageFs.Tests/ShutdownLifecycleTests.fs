module SageFs.Tests.ShutdownLifecycleTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.Tests.TestInfrastructure

/// How this platform spawns a child that simply lives for a while.
/// A DU rather than a flag: the two arms carry different programs and
/// different argument shapes, and the choice is exhaustive by construction.
type private LongRunningProgram =
  /// `cmd.exe /c ping -n N 127.0.0.1` — Windows has no `sleep` binary.
  | WindowsPing of seconds: int
  /// `/bin/sh -c "sleep N"` — POSIX-guaranteed on Linux and macOS.
  | PosixSleep of seconds: int

module private LongRunningProgram =

  let forThisPlatform (seconds: int) =
    match OperatingSystem.IsWindows() with
    | true -> WindowsPing seconds
    | false -> PosixSleep seconds

  /// Executable + argument list (never a single Arguments string — the shell
  /// quoting rules differ per platform and ArgumentList sidesteps them).
  let command (program: LongRunningProgram) : string * string list =
    match program with
    // `ping -n N` sends N echoes one second apart, so it lives ~N-1 seconds.
    | WindowsPing seconds -> "cmd.exe", [ "/c"; sprintf "ping -n %d 127.0.0.1 > nul" (seconds + 1) ]
    | PosixSleep seconds -> "/bin/sh", [ "-c"; sprintf "sleep %d" seconds ]

/// Spawn a real, long-running child process that the tests can kill.
///
/// WHY this is not OS-guarded: this file holds the repo's only tests that
/// spawn real child processes and kill them through the daemon's own
/// `stopWorker` / `killWorkerPids` sweep. It used to open with
/// `if not (OperatingSystem.IsWindows()) then skiptest ...` while CI runs on
/// Linux only, so the one real process-cleanup outcome gate executed on no CI
/// run at all — and `skiptest` reports pending, not failing, so nothing ever
/// surfaced it (outcome-gate-sweep.md §2.5). The child is chosen per platform
/// instead of skipped, so the gate runs everywhere.
let spawnLongRunning (label: string) =
  let fileName, args =
    LongRunningProgram.command (LongRunningProgram.forThisPlatform 30)
  let psi = ProcessStartInfo()
  psi.FileName <- fileName
  for arg in args do
    psi.ArgumentList.Add(arg)
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  // Neither child writes to stdout/stderr (ping redirects to nul, sleep is
  // silent), so these redirected pipes are never filled and cannot deadlock
  // the child; they exist only so no stray output reaches the test console.
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let proc = new Process()
  proc.StartInfo <- psi
  proc.EnableRaisingEvents <- true
  match proc.Start() with
  | true -> proc
  | false -> failwithf "%s: failed to spawn test process" label

/// True once `pid` names no live process. On Linux a just-killed child can
/// still have a /proc entry for the moment between SIGKILL and the runtime's
/// child reaper waiting on it, so "dead" is polled to a short ceiling rather
/// than sampled once.
let private isDead (pid: int) =
  try
    use running = Process.GetProcessById(pid)
    running.HasExited
  with
  | :? ArgumentException -> true
  | :? InvalidOperationException -> true

let mkHangingSession (proc: Process) =
  { Info =
      { WorkerProtocol.SessionInfo.Id = WorkerProtocol.SessionId.newId()
        Name = None
        Projects = []
        WorkingDirectory = ""
        SolutionRoot = None
        Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = proc.Id; Port = None }
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        ActiveProject = None
        ProjectRoles = []
        App = SageFs.AppRun.AppRunState.NotRunning }
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
    RestartState = RestartPolicy.emptyState
    AppGeneration = SageFs.AppRun.AppSlot.initial.Generation
    ProjectRoles = []
    AdoptedCore = None }

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
        let! dead = awaitCondition 5000 (fun () -> isDead pid)
        dead
        |> Expect.isTrue (sprintf "the hung worker process (pid %d) must be dead after stopWorker" pid)
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

[<Tests>]
let webHostStopTests =
  testList "Daemon web host stop" [
    testTask "WHY — McpServer.runUntilCancelled — cancelling the daemon's stop token stops its web host because sagefs stop must end the daemon, not just report success" {
      let app = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build()
      app.Urls.Add "http://127.0.0.1:0"
      let stopping = new System.Threading.CancellationTokenSource()
      try
        let started = TaskCompletionSource()
        app.Lifetime.ApplicationStarted.Register(fun () -> started.TrySetResult() |> ignore) |> ignore
        let running = SageFs.Server.McpServer.runUntilCancelled app stopping.Token
        let! _ = Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds 10.))
        started.Task.IsCompleted |> Expect.isTrue "the host started"
        stopping.Cancel()
        let! first = Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds 10.))
        (first = running) |> Expect.isTrue "the host stops once the stop token is cancelled"
      finally
        stopping.Dispose()
        (app :> IAsyncDisposable).DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds 10.) |> ignore
    }
  ]

[<Tests>]
let timerStopTests =
  testList "Daemon timer stop" [
    testTask "WHY — DaemonMode.disposeTimerAndWait — an idle timer is joined at once because every daemon shutdown used to sit out the full wait timeouts" {
      let timer = new System.Threading.Timer((fun _ -> ()), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
      let watch = Stopwatch.StartNew()
      let! stop = SageFs.Server.DaemonMode.disposeTimerAndWait timer (TimeSpan.FromSeconds 2.)
      watch.Stop()
      stop |> Expect.equal "an idle timer has no callback to wait for" SageFs.Server.DaemonMode.TimerStop.Joined
      (watch.Elapsed < TimeSpan.FromSeconds 1.5) |> Expect.isTrue (sprintf "joined without sitting out the timeout (took %O)" watch.Elapsed)
    }
  ]
