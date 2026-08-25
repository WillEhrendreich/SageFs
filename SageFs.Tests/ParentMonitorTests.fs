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

  testCase "cancels cts when daemon pid disappears" <| fun _ ->
    use cts = new CancellationTokenSource()
    // Always report dead — monitor should cancel promptly.
    let mutable logLines = []
    let monitor =
      ParentMonitor.run (fun _ -> None) 999999 cts (fun msg -> logLines <- msg :: logLines)
    let task = monitor |> Async.StartAsTask
    task.Wait(10_000) |> ignore
    cts.IsCancellationRequested
    |> Expect.isTrue "cts should be cancelled after daemon death detected"
    (not (List.isEmpty logLines))
    |> Expect.isTrue "should log that the daemon died"

  testCase "does not cancel while daemon alive" <| fun _ ->
    use cts = new CancellationTokenSource()
    let self = Process.GetCurrentProcess()
    let monitor =
      ParentMonitor.run (fun _ -> Some self) self.Id cts ignore
    let task = monitor |> Async.StartAsTask
    // Give it a few poll cycles while the daemon (us) stays alive.
    Thread.Sleep(ParentMonitor.pollIntervalMs * 3)
    cts.IsCancellationRequested
    |> Expect.isFalse "should not cancel while daemon alive"
    task |> ignore
]
