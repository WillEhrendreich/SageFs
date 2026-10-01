namespace SageFs

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.InteropServices
open System.Text

/// A fixed single-thread workload that looks like compiler work (string hashing, dictionary and
/// sort churn, string building), timed. It is the one number that says how fast THIS CPU is for
/// the kind of work a SageFs start is made of, because core count and memory do not: a 2009 four
/// core and a 2024 four core read the same on both and differ by several times in speed.
///
/// This file has no dependency on the rest of SageFs on purpose. `scripts/machine-bench.fsx`
/// `#load`s it, so the benchmark and the product measure the same workload and their numbers
/// can be compared.
module MachineCalibration =

  /// How many keys the workload inserts. Chosen so the workload takes about 20 ms on a fast
  /// machine (a 2020 desktop core) and a few tenths of a second on a slow one: long enough that
  /// the timer's own noise is small, short enough to run at daemon start without being noticed.
  [<Literal>]
  let private KeyCount = 150000

  /// The distinct keys the workload cycles through. A prime, so `i * Stride % KeySpace` revisits
  /// keys in a scattered order and the dictionary sees both inserts and updates.
  [<Literal>]
  let private KeySpace = 50021

  /// Steps through the key space. Also prime, so it does not share a factor with `KeySpace`.
  [<Literal>]
  let private Stride = 7919

  /// How many times the workload runs after the first. The first run pays the JIT, which a cold
  /// SageFs start pays too, so it is reported on its own. The steady figure is the lowest of
  /// these, because noise only ever adds time.
  [<Literal>]
  let private SteadyRuns = 2

  /// What the time is measured with. The CPU time of the calling thread is what says how fast the CPU
  /// is: another process taking the CPU for a moment stretches the wall clock of the same work (a fast
  /// desktop read 24 ms on a quiet minute and 89 ms while a build ran on every core) but not the thread's
  /// own CPU time. Where the platform has no such clock the wall clock is used, and says so.
  [<RequireQualifiedAccess>]
  type Clock =
    | ThreadCpu
    | Wall

  /// What one measurement says. Both numbers are milliseconds of `Clock` for the same workload;
  /// `FirstMs` includes the JIT of the workload itself.
  type Measurement =
    { FirstMs: float
      SteadyMs: float
      Clock: Clock }

  [<Struct; StructLayout(LayoutKind.Sequential)>]
  type private Timespec =
    val mutable Seconds: int64
    val mutable Nanoseconds: int64

  [<DllImport("libc", EntryPoint = "clock_gettime")>]
  extern int private clock_gettime(int clockId, Timespec& result)

  /// CLOCK_THREAD_CPUTIME_ID: 3 on Linux, 16 on macOS.
  let private threadCpuClockId : int = (match OperatingSystem.IsMacOS() with | true -> 16 | false -> 3)

  [<Literal>]
  let private NanosecondsPerMillisecond = 1000000.0

  [<Literal>]
  let private MillisecondsPerSecond = 1000.0

  /// What the thread CPU clock said, or that this platform has none.
  [<RequireQualifiedAccess>]
  type private CpuReading =
    | Milliseconds of float
    | NoClock

  /// The calling thread's CPU time so far, where the platform has the clock.
  let private threadCpuMs () : CpuReading =
    match OperatingSystem.IsWindows() with
    | true -> CpuReading.NoClock
    | false ->
      try
        let mutable now = Unchecked.defaultof<Timespec>
        match clock_gettime (threadCpuClockId, &now) with
        | 0 -> CpuReading.Milliseconds (float now.Seconds * MillisecondsPerSecond + float now.Nanoseconds / NanosecondsPerMillisecond)
        | _ -> CpuReading.NoClock
      with _ -> CpuReading.NoClock

  /// How long the workload took by the best clock there is, and which clock that was.
  let private workload () : float * Clock =
    let timer = Stopwatch.StartNew()
    let cpuBefore = threadCpuMs ()
    let counts = Dictionary<string, int>()
    let mutable churn = 0
    for i in 0 .. KeyCount do
      let key = "key" + string (i * Stride % KeySpace)
      match counts.TryGetValue key with
      | true, seen -> counts.[key] <- seen + i
      | false, _ -> counts.[key] <- i
      churn <- churn + key.GetHashCode() % 7
    let rows = counts |> Seq.map (fun pair -> pair.Key + ":" + string pair.Value) |> Seq.toArray
    Array.sortInPlace rows
    let builder = StringBuilder()
    for row in rows do
      builder.Append(row.Length) |> ignore
    ignore (churn + builder.Length)
    match cpuBefore, threadCpuMs () with
    | CpuReading.Milliseconds before, CpuReading.Milliseconds after -> after - before, Clock.ThreadCpu
    | _ -> timer.Elapsed.TotalMilliseconds, Clock.Wall

  /// Run the workload and report. Blocks the calling thread for the length of the workload.
  let measure () : Measurement =
    let first, clock = workload ()
    let steady = [ for _ in 1 .. SteadyRuns -> fst (workload ()) ] |> List.min
    { FirstMs = first; SteadyMs = steady; Clock = clock }
