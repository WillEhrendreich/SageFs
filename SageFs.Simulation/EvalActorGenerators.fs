namespace SageFs.Simulation

open System
open SageFs.Simulation.EvalActorSim

/// Seeded, dependency-free generators for eval-actor scenarios (mirrors
/// `CohortLandingGenerators`). Chaos is data: `run (fromSeed n)` replays
/// identically forever. The pool deliberately mixes `Reset` and
/// `StragglerFinished` so a generation-superseded straggler is actually
/// exercised, and includes `PoisonPill` so the loop-survival twin is too.
module EvalActorGenerators =

  let private opPool =
    [| EvalOp.Submit
       EvalOp.Query
       EvalOp.Cancel
       EvalOp.Reset
       EvalOp.StragglerFinished 1
       EvalOp.StragglerFinished 2
       EvalOp.PoisonPill |]

  /// A general scenario: 3–12 ops over the full pool. Same seed => identical
  /// list, forever.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(3, 13)
    let ops = [ for _ in 1 .. n -> opPool.[rnd.Next(0, opPool.Length)] ]
    { Seed = seed; Ops = ops }

  // ── Named minimal scenarios (the exact replays the tests assert on) ─────

  /// A Reset fires while an eval is in flight, and the eval's eventual
  /// Finished belongs to the generation the reset superseded — the
  /// headline no-resurrection replay.
  let resetDuringEval : Scenario =
    { Seed = 1; Ops = [ EvalOp.Submit; EvalOp.Reset; EvalOp.StragglerFinished 1 ] }

  /// A Query lands squarely mid-eval — the headline query-liveness replay.
  let queryDuringEval : Scenario =
    { Seed = 2; Ops = [ EvalOp.Submit; EvalOp.Query ] }

  /// Cancel issued twice in a row while an eval is in flight — must be
  /// idempotently served both times.
  let doubleCancel : Scenario =
    { Seed = 3; Ops = [ EvalOp.Submit; EvalOp.Cancel; EvalOp.Cancel ] }

  /// A straggler Finished arrives after TWO resets — two generations stale,
  /// the strict worst case for the resurrection bug.
  let stragglerAfterReset : Scenario =
    { Seed = 4; Ops = [ EvalOp.Submit; EvalOp.Reset; EvalOp.Reset; EvalOp.StragglerFinished 2 ] }

  /// A PoisonPill mid-scenario with real ops both before and after — the
  /// headline loop-survival replay: the guarded reducer must still process
  /// every op after it; the unwrapped twin must not.
  let poisonMidScenario : Scenario =
    { Seed = 5; Ops = [ EvalOp.Submit; EvalOp.PoisonPill; EvalOp.Query; EvalOp.Cancel; EvalOp.Reset ] }

  /// A Finished for the CURRENT generation (not stale) — proves
  /// `ApplyFinished` is reached too, not just the drop path (teeth against a
  /// vacuous "everything is dropped" reducer).
  let freshFinished : Scenario =
    { Seed = 6; Ops = [ EvalOp.Submit; EvalOp.StragglerFinished 0 ] }
