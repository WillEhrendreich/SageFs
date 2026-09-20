module SageFs.OwnerMonitor

open System
open System.Diagnostics
open System.Threading

/// A process identity fenced by PID *and* start time, so PID reuse — the OS
/// recycling a numeric PID after heavy spawn/exit churn — can never be
/// mistaken for "the same owner is still alive". This is the pure decision
/// core moved out of `SageFs.Host/WorkerMain.fs`'s `ParentMonitor` (issue
/// #126's watchdog polled a bare pid; under heavy spawn a recycled pid kept
/// a worker alive forever) so any owned process — a worker, a Run App
/// child, a `dotnet build`, or an externally-spawned daemon (`sagefs
/// --owner-pid`) — can share one watchdog.
///
/// `StartTimeTicks` is `Process.StartTime` (normalized to UTC ticks) at the
/// moment the fence was recorded. `None` means "no fence recorded" — a
/// weaker, pid-only check kept for callers that never had a start time to
/// record (e.g. a legacy caller passing only a pid); every caller that CAN
/// record a start time should.
type Owner = { Pid: int; StartTimeTicks: int64 option }

module Owner =
  /// A pid-only owner: alive means "some process is running under this
  /// pid", with no protection against pid reuse. Prefer `ofProcess`.
  let ofPid (pid: int) : Owner = { Pid = pid; StartTimeTicks = None }

/// UTC ticks of a process's start time — the value recorded into an
/// `Owner` fence.
let startTimeTicksOf (p: Process) : int64 =
  p.StartTime.ToUniversalTime().Ticks

/// Tolerance for the start-time fence comparison. `Process.StartTime` is NOT
/// bit-stable across the read paths that record vs. check the fence: a
/// process's own self-read (`GetCurrentProcess().StartTime`, used by a
/// spawner to record the fence) and another process's cross-process read of
/// it via `/proc` (`GetProcessById(pid).StartTime`, used by the watchdog)
/// differ by sub-second jitter on Linux — observed ~1661 ticks (166µs)
/// between a parent's self-read and a child's read of the parent, and
/// btime-rounding across read paths can push this toward ~1s. An EXACT
/// comparison therefore declared a live owner dead, self-terminating a
/// daemon that was still owned (CI integration-host regression). The fence's
/// only job is pid-reuse detection: a recycled pid belongs to a DIFFERENT
/// process started seconds-to-hours later (Linux allocates pids sequentially
/// up to pid_max, so reuse takes far longer than seconds under any load), so
/// a 2s tolerance sits orders of magnitude above the read jitter and orders
/// of magnitude below any realistic reuse gap — it can never mask reuse.
let startTimeToleranceTicks : int64 = TimeSpan.FromSeconds(2.0).Ticks

/// Whether a live process's observed start-time ticks match a recorded fence,
/// within `startTimeToleranceTicks`. Pure and directly testable (a real
/// `Process.StartTime` cannot be set, so the decision is extracted from
/// `isAlive`). `expected` is the fenced value; `actual` is the monitor's
/// current read.
let fenceMatches (expected: int64) (actual: int64) : bool =
  abs (actual - expected) <= startTimeToleranceTicks

/// Real process lookup for production use.
let getProcessById (pid: int) : Process option =
  try
    Some (Process.GetProcessById(pid))
  with
  | :? ArgumentException -> None // no such process
  | :? InvalidOperationException -> None // process already exited

/// Decide whether the given owner is still alive: the pid exists, has not
/// exited, and — when a start time was fenced — the live process at that
/// pid still has that exact start time. A pid whose live start time does
/// not match the fence is a DIFFERENT process that reused the number and
/// is treated as dead, closing the pid-reuse race structurally rather than
/// by timing. Any lookup failure is "dead" — fail-closed so a monitor error
/// can never orphan the process it watches.
let isAlive (getProcessById: int -> Process option) (owner: Owner) : bool =
  try
    match getProcessById owner.Pid with
    | None -> false
    | Some p when p.HasExited -> false
    | Some p ->
      match owner.StartTimeTicks with
      | None -> true
      | Some expected -> fenceMatches expected (startTimeTicksOf p)
  with _ -> false

/// Poll interval between owner liveness checks.
let pollIntervalMs = 2000

/// A generic fail-safe threshold for any periodic self-check whose own
/// success is a precondition for a liveness/lifetime decision — the daemon
/// `--ttl` idle-check (`SageFs.DaemonOwnership`/`DaemonMode`) is the first
/// caller: it must read session and activity state on every tick, and a
/// daemon that can never successfully do that has failed the one job `--ttl`
/// gives it. A single failure is almost certainly transient and is retried
/// on the next tick; `giveUpAfterFailures` consecutive failures means the
/// check itself is unable to determine the process's own state at all. The
/// fail-safe answer mirrors `isAlive`'s own `with _ -> false`: a check that
/// cannot be trusted is treated as "cannot determine the deadline", never as
/// "keep running forever by default".
let giveUpAfterFailures = 5

/// Whether a periodic self-check that has failed `consecutiveFailures` times
/// in a row should be treated as unable to determine its own answer — the
/// caller should fail safe (exit, logging why) rather than keep retrying
/// forever on the strength of hope alone.
let hasGivenUp (consecutiveFailures: int) : bool =
  consecutiveFailures >= giveUpAfterFailures

/// Run the monitor loop. Cancels the provided CTS once the owner is gone
/// (exited, missing, or a pid-reuse mismatch against the fence).
let run
  (getProcessById: int -> Process option)
  (owner: Owner)
  (cts: CancellationTokenSource)
  (log: string -> unit)
  : Async<unit> =
  async {
    let mutable ownerGone = false
    while not ownerGone && not cts.IsCancellationRequested do
      do! Async.Sleep pollIntervalMs
      match isAlive getProcessById owner with
      | true -> ()
      | false ->
        log (sprintf "Owner (PID %d) no longer alive — exiting to avoid orphan" owner.Pid)
        ownerGone <- true
        try cts.Cancel() with :? ObjectDisposedException -> ()
  }
