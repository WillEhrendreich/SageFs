module SageFs.Tests.OwnerMonitorTests

open System
open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs.OwnerMonitor

/// Tests for the Core-owned, pid-and-start-time-fenced watchdog
/// (multi-agent vision §3.1 rule 1, §10 item 2). Moved out of
/// `SageFs.Host/WorkerMain.fs`'s `ParentMonitor` (still tested unchanged in
/// ParentMonitorTests.fs, as a pid-only back-compat wrapper) so any owned
/// process — worker, Run App child, `dotnet build`, or an
/// externally-spawned daemon — can share it, fenced against pid reuse.

[<Tests>]
let isAliveTests = testList "OwnerMonitor.isAlive" [

  testCase "live pid with no fence reports alive" <| fun _ ->
    let self = Process.GetCurrentProcess()
    isAlive (fun _ -> Some self) (Owner.ofPid self.Id)
    |> Expect.isTrue "current process should be alive"

  testCase "missing pid reports dead" <| fun _ ->
    isAlive (fun _ -> None) (Owner.ofPid 999999)
    |> Expect.isFalse "none lookup should be dead"

  testCase "exited process reports dead" <| fun _ ->
    let psi = ProcessStartInfo(
      FileName = Environment.ProcessPath,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true)
    psi.ArgumentList.Add("--help")
    use p = Process.Start(psi)
    p.WaitForExit(10_000) |> ignore
    isAlive (fun _ -> Some p) (Owner.ofPid p.Id)
    |> Expect.isFalse "exited process should be dead"

  testCase "lookup exception reports dead" <| fun _ ->
    isAlive (fun _ -> failwith "boom") (Owner.ofPid 123)
    |> Expect.isFalse "throwing lookup should be treated as dead"

  testCase "matching start-time fence keeps a live owner alive" <| fun _ ->
    let self = Process.GetCurrentProcess()
    let owner = { Pid = self.Id; StartTimeTicks = Some (startTimeTicksOf self) }
    isAlive (fun _ -> Some self) owner
    |> Expect.isTrue "a live process whose recorded start time matches the fence is the same owner"

  // The fence: a recycled pid must NOT keep the watched process alive.
  // Simulated by a live process (the pid genuinely resolves and hasn't
  // exited) whose recorded fence start-time does NOT match — exactly what
  // a pid reused by a different process after the original owner exited
  // would look like to the monitor.
  testCase "recycled pid does not keep the owner alive (start-time fence)" <| fun _ ->
    let self = Process.GetCurrentProcess()
    let staleStartTicks = startTimeTicksOf self - TimeSpan.FromDays(1.0).Ticks
    let owner = { Pid = self.Id; StartTimeTicks = Some staleStartTicks }
    isAlive (fun _ -> Some self) owner
    |> Expect.isFalse "a live process whose start time doesn't match the fence is a DIFFERENT process (pid reuse) and must be treated as dead"
]

[<Tests>]
let runTests = testList "OwnerMonitor.run" [

  testTask "cancels cts when owner disappears" {
    let cts = new CancellationTokenSource()
    try
      let logLines = ref []
      let monitor =
        run (fun _ -> None) (Owner.ofPid 999999) cts (fun msg -> logLines.Value <- msg :: logLines.Value)
      let running = monitor |> Async.StartAsTask
      let! _ = Tasks.Task.WhenAny(running :> Tasks.Task, Tasks.Task.Delay 10_000)
      cts.IsCancellationRequested
      |> Expect.isTrue "cts should be cancelled after owner death detected"
      (not (List.isEmpty logLines.Value))
      |> Expect.isTrue "should log that the owner died"
    finally
      cts.Dispose()
  }

  testTask "does not cancel while owner alive, matched by fence" {
    let cts = new CancellationTokenSource()
    try
      let self = Process.GetCurrentProcess()
      let owner = { Pid = self.Id; StartTimeTicks = Some (startTimeTicksOf self) }
      let monitor = run (fun _ -> Some self) owner cts ignore
      let _running = monitor |> Async.StartAsTask
      do! Tasks.Task.Delay(pollIntervalMs * 3)
      cts.IsCancellationRequested
      |> Expect.isFalse "should not cancel while the fenced owner is alive"
      cts.Cancel()
    finally
      cts.Dispose()
  }

  testTask "cancels promptly when the fence detects a recycled pid" {
    let cts = new CancellationTokenSource()
    try
      let self = Process.GetCurrentProcess()
      let staleStartTicks = startTimeTicksOf self - TimeSpan.FromDays(1.0).Ticks
      let owner = { Pid = self.Id; StartTimeTicks = Some staleStartTicks }
      let monitor = run (fun _ -> Some self) owner cts ignore
      let running = monitor |> Async.StartAsTask
      let! _ = Tasks.Task.WhenAny(running :> Tasks.Task, Tasks.Task.Delay 10_000)
      cts.IsCancellationRequested
      |> Expect.isTrue "a recycled pid must not keep the monitor from cancelling — the fence must detect it within a couple of poll intervals"
    finally
      cts.Dispose()
  }
]
