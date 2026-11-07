module SageFs.Tests.PerfBudget

open System.Diagnostics
open System.Runtime.InteropServices

// A deliberately lightweight replacement for the BenchmarkDotNet suite that used
// to run in CI. BDN is accurate but slow (separate processes, many iterations)
// and it was gating releases on noisy shared runners for metrics no user feels.
// These helpers run in-process, in the normal test suite, on the developer's own
// machine — where perf work belongs (local-first). The design goal is a timing
// check robust enough to never flake, so it earns its place in the default run.

/// Which clock a reading came from. A closed set: `describe` is its one to-text function.
type ClockKind =
  /// The CPU time the calling thread has used. Time the thread spends descheduled (a loaded machine, a neighbour's
  /// build, a pause for a collection another thread runs) does not count, so a budget judged on it measures the work.
  | ThreadCpu
  /// Wall time, on a host that has no per-thread CPU clock here (Windows). It can only be too slow, never too fast.
  | Wall

let describe (kind: ClockKind) : string =
  match kind with
  | ThreadCpu -> "thread CPU time"
  | Wall -> "wall time"

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Timespec =
  val mutable Seconds: int64
  val mutable Nanoseconds: int64

[<DllImport("libc", EntryPoint = "clock_gettime")>]
extern int clock_gettime(int clockId, Timespec& ts)

/// CLOCK_THREAD_CPUTIME_ID: 3 on Linux, 16 on macOS.
let private threadCpuClockId : int option =
  match RuntimeInformation.IsOSPlatform OSPlatform.Linux, RuntimeInformation.IsOSPlatform OSPlatform.OSX with
  | true, _ -> Some 3
  | _, true -> Some 16
  | _ -> None

let private nanosecondsPerMillisecond = 1e6
let private nanosecondsPerSecond = int64 1e9

/// The CPU nanoseconds the calling thread has used, when the host can say.
let private threadCpuNanos (clockId: int) : int64 option =
  let mutable reading = Timespec()
  match clock_gettime (clockId, &reading) with
  | 0 -> Some (reading.Seconds * nanosecondsPerSecond + reading.Nanoseconds)
  | _ -> None

/// The clock the budgets in this file read: the thread's CPU time when the host has it, wall time otherwise.
let clock : ClockKind =
  match threadCpuClockId |> Option.bind threadCpuNanos with
  | Some _ -> ThreadCpu
  | None -> Wall

/// Milliseconds on `clock`. Differences between two readings taken on the SAME thread are the work between them;
/// a reading is meaningless alone, and work another thread does is not in it.
let nowMs () : float =
  match clock, threadCpuClockId with
  | ThreadCpu, Some id ->
    match threadCpuNanos id with
    | Some nanos -> float nanos / nanosecondsPerMillisecond
    | None -> float (Stopwatch.GetTimestamp ()) / float Stopwatch.Frequency * 1000.0
  | _ -> float (Stopwatch.GetTimestamp ()) / float Stopwatch.Frequency * 1000.0

/// Minimum elapsed milliseconds of `body` across `repeats` runs, after 2 warmups, on `clock`.
/// MIN — not mean or median — is the least-contaminated estimate of the true
/// compute cost: GC pauses, the OS scheduler, and a loaded machine only ever ADD
/// time, so the fastest observed run is the one closest to the real cost. The thread CPU clock
/// removes the largest of those additions (time spent waiting for a core), and the minimum
/// removes what is left (cache misses another process caused, a collection this thread ran).
/// `body` must do its work on the calling thread: work it hands to another thread is not on this
/// thread's clock.
let minMs (repeats: int) (body: unit -> unit) : float =
  body ()
  body ()
  let mutable best = System.Double.MaxValue
  for _ in 1 .. repeats do
    // Normalize GC state before each timed block so a large live heap in one
    // measurement doesn't inflate it relative to a small one — the scaling ratio
    // must reflect the algorithm, not incidental collection pressure.
    System.GC.Collect ()
    System.GC.WaitForPendingFinalizers ()
    let t0 = nowMs ()
    body ()
    let ms = nowMs () - t0
    best <- min best ms
  best

/// The ratio of a large-workload cost to a small-workload cost. Because BOTH
/// timings scale with the hardware, the ratio does not — it isolates the
/// algorithm's own growth (O(1) ~ 1x, O(n) ~ the workload span, O(n^2) ~ the
/// span squared). A scaling guard built on this ratio catches an algorithmic
/// regression without ever depending on an absolute nanosecond budget that would
/// flake on a slow or busy runner. This is the honest, portable way to assert
/// "this hot path stays cheap as the data grows."
let scalingRatio (repeats: int) (small: unit -> unit) (large: unit -> unit) : float =
  let s = minMs repeats small
  let l = minMs repeats large
  l / s
