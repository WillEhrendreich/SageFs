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

  /// Cancel before anything was ever submitted — nothing to cancel, and it
  /// must not gate the Submit that follows.
  let cancelBeforeSubmit : Scenario =
    { Seed = 7; Ops = [ EvalOp.Cancel; EvalOp.Submit ] }

  /// THE headline replay for the orphaned-loop bug this fix closes: an eval
  /// is cancelled and never confirms it stopped (no Finished follows), then
  /// a second eval is submitted. Live repro that motivated it: cancel an
  /// unbounded `while true do ()`, then submit a trivial `1+1` — pre-fix,
  /// the trivial eval hung for the caller's full timeout because the second
  /// Submit was silently accepted onto the same orphaned session.
  let cancelThenResubmit : Scenario =
    { Seed = 8; Ops = [ EvalOp.Submit; EvalOp.Cancel; EvalOp.Submit ] }

  /// A Cancel and the eval's own completion land back to back — the cancel
  /// request loses the race to a `Finished` for the CURRENT generation.
  /// Activity must still land on Idle (the completion wins), and a THIRD
  /// Submit afterward must be allowed again.
  let cancelRacingCompletion : Scenario =
    { Seed = 9; Ops = [ EvalOp.Submit; EvalOp.Cancel; EvalOp.StragglerFinished 0; EvalOp.Submit ] }
