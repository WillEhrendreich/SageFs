module SageFs.Tests.PerfTests

open Expecto
open Expecto.Flip
open SageFs.Features

// Local, lightweight perf guards. These replace the BenchmarkDotNet CI job: the
// only benchmark that measured a real user-impacting hot path was recordEval
// scaling, and it was the one the CI threshold gate never checked. The deprecated
// TUI benchmarks (cell grid, ANSI diff) and the no-consumer feature benchmarks
// were measuring code no user runs. What survives is the one guard that matters,
// expressed as a machine-independent scaling ratio so it can run every time.

// A synthetic eval cell whose code references prior bindings, so the retained
// history gives the binding scan and dependency graph real work to do — the same
// shape the old RecordEvalScaling benchmark used.
let private cell i =
  match i % 3 with
  | 0 -> sprintf "let v%d = x + %d" i i, sprintf "val v%d: int = %d" i i
  | 1 -> sprintf "let x = v%d * 2" (i - 1), sprintf "val x: int = %d" i
  | _ -> sprintf "x + v%d" (i - 2), sprintf "val it: int = %d" i

// Build a push-state filled exactly to `cap`, so every further recordEval is a
// steady-state, at-cap call (one eviction per insert). Comparing two at-cap
// sizes isolates history-size scaling from the one-off below-cap-vs-at-cap jump.
let private buildAtCap (cap: int) =
  match EvalStore.HistoryCap.tryCreate cap with
  | Ok hc ->
    let start = FeatureHooks.FeaturePushState.withCap hc
    [ 1 .. cap ]
    |> List.fold (fun st i -> let c, r = cell i in FeatureHooks.recordEval c r 5L st) start
  | Error e -> failwithf "buildAtCap %d: %s" cap e

let private steadyBatch state =
  fun () ->
    let mutable s = state
    for i in 1 .. 200 do
      let c, r = cell i
      s <- FeatureHooks.recordEval c r 5L s

[<Tests>]
let perfTests =
  testList "Perf budgets (local)" [
    // Guards the roast's #1 performance finding: recordEval used to rebuild the
    // whole binding scope on every eval — regex per binding, per retained cell —
    // an O(n^2) hot path on the "sub-500ms feedback on every save" promise. It is
    // now incremental: the scope and dependency graph are deferred behind `lazy`
    // (forced only on the throttled push path) and the timeline is updated in
    // place, so recordEval no longer re-scans history.
    //
    // The guard is a machine-independent scaling RATIO, never a wall-clock budget,
    // so it can run in the normal suite without flaking on a slow or loaded runner
    // (see PerfBudget for why min-of-N + GC normalization). Steady-state cost for
    // a 10x larger history measures a stable ~5x — sub-linear, and that ~5x is GC
    // pressure from the larger retained history (each entry holds full code+result
    // strings), not algorithmic work. The regression this catches is the O(n^2)
    // rescan returning: at 10k history a single recordEval would re-scan 10k cells
    // and the ratio would explode to ~100x+. The 15x ceiling sits far above the
    // ~5x baseline (portability margin for differing GC configs) and far below the
    // ~100x a real regression produces.
    testCase "recordEval steady-state cost stays sub-linear as history grows (guards the per-eval O(n^2) regression)"
    <| fun _ ->
      let small = buildAtCap 1000
      let large = buildAtCap 10000
      let ratio = PerfBudget.scalingRatio 15 (steadyBatch small) (steadyBatch large)
      ratio < 15.0
      |> Expect.isTrue (
        sprintf
          "recordEval scaled %.1fx for 10x history; >15x means the per-eval O(n^2) scope rescan is back (baseline is ~5x)"
          ratio)
  ]
