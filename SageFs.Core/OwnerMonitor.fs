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
      | Some expected -> startTimeTicksOf p = expected
  with _ -> false

/// Poll interval between owner liveness checks.
let pollIntervalMs = 2000

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
