module SageFs.Tests.PerfBudget

open System.Diagnostics

// A deliberately lightweight replacement for the BenchmarkDotNet suite that used
// to run in CI. BDN is accurate but slow (separate processes, many iterations)
// and it was gating releases on noisy shared runners for metrics no user feels.
// These helpers run in-process, in the normal test suite, on the developer's own
// machine — where perf work belongs (local-first). The design goal is a timing
// check robust enough to never flake, so it earns its place in the default run.

/// Minimum elapsed milliseconds of `body` across `repeats` runs, after 2 warmups.
/// MIN — not mean or median — is the least-contaminated estimate of the true
/// compute cost: GC pauses, the OS scheduler, and a loaded machine only ever ADD
/// time, so the fastest observed run is the one closest to the real cost. Using
/// the minimum is what lets a wall-clock measurement live in the normal suite
/// without turning machine noise into red builds.
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
    let t0 = Stopwatch.GetTimestamp ()
    body ()
    let ms = float (Stopwatch.GetTimestamp () - t0) / float Stopwatch.Frequency * 1000.0
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
