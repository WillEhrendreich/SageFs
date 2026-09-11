module SageFs.Tests.ParentMonitorTests

open System
open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs.Server.WorkerMain

/// Tests for the worker parent-death watchdog (issue #126).
/// Workers self-exit when their daemon process dies, so hard kills
/// (Task Manager, taskkill /F, crash, OS shutdown) don't orphan workers.

[<Tests>]
let parentMonitorAliveTests = testList "ParentMonitor.isDaemonAlive" [

  testCase "live pid reports alive" <| fun _ ->
    let self = Process.GetCurrentProcess()
    ParentMonitor.isDaemonAlive (fun _ -> Some self) self.Id
    |> Expect.isTrue "current process should be alive"

  testCase "missing pid reports dead" <| fun _ ->
    ParentMonitor.isDaemonAlive (fun _ -> None) 999999
    |> Expect.isFalse "none lookup should be dead"

  testCase "exited process reports dead" <| fun _ ->
    // Spawn a throwaway process and let it exit, then check HasExited.
    let psi = ProcessStartInfo(
      FileName = Environment.ProcessPath,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true)
    psi.ArgumentList.Add("--help")
    use p = Process.Start(psi)
    p.WaitForExit(10_000) |> ignore
    ParentMonitor.isDaemonAlive (fun _ -> Some p) p.Id
    |> Expect.isFalse "exited process should be dead"

  testCase "lookup exception reports dead" <| fun _ ->
    ParentMonitor.isDaemonAlive (fun _ -> failwith "boom") 123
    |> Expect.isFalse "throwing lookup should be treated as dead"
]

[<Tests>]
let parentMonitorRunTests = testList "ParentMonitor.run" [

  testTask "cancels cts when daemon pid disappears" {
    let cts = new CancellationTokenSource()
    try
      // Always report dead — monitor should cancel promptly.
      let logLines = ref []
      let monitor =
        ParentMonitor.run (fun _ -> None) 999999 cts (fun msg -> logLines.Value <- msg :: logLines.Value)
      let running = monitor |> Async.StartAsTask
      let! _ = Tasks.Task.WhenAny(running :> Tasks.Task, Tasks.Task.Delay 10_000)
      cts.IsCancellationRequested
      |> Expect.isTrue "cts should be cancelled after daemon death detected"
      (not (List.isEmpty logLines.Value))
      |> Expect.isTrue "should log that the daemon died"
    finally
      cts.Dispose()
  }

  testTask "does not cancel while daemon alive" {
    let cts = new CancellationTokenSource()
    try
      let self = Process.GetCurrentProcess()
      let monitor =
        ParentMonitor.run (fun _ -> Some self) self.Id cts ignore
      let _running = monitor |> Async.StartAsTask
      // Give it a few poll cycles while the daemon (us) stays alive.
      do! Tasks.Task.Delay(ParentMonitor.pollIntervalMs * 3)
      cts.IsCancellationRequested
      |> Expect.isFalse "should not cancel while daemon alive"
      // Stop the monitor: it exits at its next poll instead of running on.
      cts.Cancel()
    finally
      cts.Dispose()
  }
]
