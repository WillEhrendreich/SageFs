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

  // REGRESSION (CI integration-host daemon self-terminated "Owner no longer
  // alive" on a live owner): a process's self-read of Process.StartTime and
  // another process's cross-process read of it via /proc differ by sub-second
  // jitter on Linux (observed ~1661 ticks / 166µs between a parent's self-read
  // and a child's GetProcessById read of the parent). An EXACT-equality fence
  // therefore declares a live owner dead. The fence must tolerate sub-second
  // jitter while still catching pid reuse (a genuinely different process
  // started seconds-to-hours later).
  testCase "sub-second start-time jitter keeps a live owner alive (cross-process /proc read)" <| fun _ ->
    let self = Process.GetCurrentProcess()
    // The fence was recorded by the owner's self-read; the monitor reads the
    // live process's start slightly differently — simulate that skew.
    let jitteredFence = startTimeTicksOf self + 1661L
    let owner = { Pid = self.Id; StartTimeTicks = Some jitteredFence }
    isAlive (fun _ -> Some self) owner
    |> Expect.isTrue "sub-second (166µs) skew between a self-recorded fence and the monitor's cross-process read is the SAME owner, not pid reuse"
]

[<Tests>]
let fenceTests = testList "OwnerMonitor.fenceMatches" [

  testCase "exact match is the same owner" <| fun _ ->
    fenceMatches 1_000_000_000L 1_000_000_000L
    |> Expect.isTrue "identical start ticks are the same process"

  testCase "observed cross-process jitter (166µs) is the same owner" <| fun _ ->
    fenceMatches 1_000_000_000L (1_000_000_000L + 1661L)
    |> Expect.isTrue "the exact skew observed on Linux must be tolerated"

  testCase "skew just under the tolerance is the same owner" <| fun _ ->
    fenceMatches 1_000_000_000L (1_000_000_000L + startTimeToleranceTicks - 1L)
    |> Expect.isTrue "within tolerance is the same process"

  testCase "skew exactly at the tolerance is the same owner" <| fun _ ->
    fenceMatches 1_000_000_000L (1_000_000_000L + startTimeToleranceTicks)
    |> Expect.isTrue "the tolerance bound is inclusive"

  testCase "negative skew within tolerance is the same owner" <| fun _ ->
    fenceMatches 1_000_000_000L (1_000_000_000L - 1661L)
    |> Expect.isTrue "skew is symmetric — the monitor may read earlier OR later than the fence"

  testCase "skew beyond the tolerance is pid reuse (a different process)" <| fun _ ->
    fenceMatches 1_000_000_000L (1_000_000_000L + startTimeToleranceTicks + 1L)
    |> Expect.isFalse "just past the tolerance is a different process"

  testCase "a day apart is unambiguously pid reuse" <| fun _ ->
    fenceMatches 1_000_000_000L (1_000_000_000L + TimeSpan.FromDays(1.0).Ticks)
    |> Expect.isFalse "a process started a day later that reused the pid must read as dead"

  testCase "tolerance is far below any real pid-reuse gap and far above read jitter" <| fun _ ->
    // Sub-jiffy jitter is ~microseconds; a reused pid on Linux (sequential
    // allocation up to pid_max) is seconds-to-hours later. The tolerance sits
    // safely between: generous vs jitter, negligible vs reuse.
    (startTimeToleranceTicks > TimeSpan.FromMilliseconds(10.0).Ticks
     && startTimeToleranceTicks < TimeSpan.FromMinutes(1.0).Ticks)
    |> Expect.isTrue "tolerance must exceed OS read jitter yet stay well under any realistic reuse gap"
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

/// The generic fail-safe give-up threshold: a periodic self-check (the
/// daemon `--ttl` idle-check is the first caller — SageFs.DaemonMode's
/// ttlCallback) that keeps throwing on every tick was, before this fix,
/// logged as a warning and silently retried forever — the opposite of
/// fail-safe. `hasGivenUp` is the pure decision a caller consults to know
/// when to stop hoping and exit instead.
[<Tests>]
let giveUpTests = testList "OwnerMonitor.hasGivenUp" [

  testCase "zero failures has not given up" <| fun _ ->
    hasGivenUp 0 |> Expect.isFalse "no failures yet is not a reason to give up"

  testCase "one failure has not given up (almost certainly transient)" <| fun _ ->
    hasGivenUp 1 |> Expect.isFalse "a single failure is retried, not fatal"

  testCase "one failure short of the threshold has not given up" <| fun _ ->
    hasGivenUp (giveUpAfterFailures - 1) |> Expect.isFalse "still below the threshold"

  testCase "WHY — reaching the threshold gives up (fail-safe: exit rather than run forever)" <| fun _ ->
    hasGivenUp giveUpAfterFailures |> Expect.isTrue "the threshold itself must trigger give-up"

  testCase "past the threshold has given up" <| fun _ ->
    hasGivenUp (giveUpAfterFailures + 100) |> Expect.isTrue "more failures never un-gives-up"

  testCase "the bug this proves: an unbounded 'always retry' policy never gives up, but this one must" <| fun _ ->
    // Proves the test has teeth: a broken `hasGivenUp _ = false` (the old
    // fail-open behavior — log and keep retrying forever) would fail this.
    [ 5; 6; 10; 1000 ] |> List.forall hasGivenUp
    |> Expect.isTrue "every failure count at or above the threshold must give up"
]
