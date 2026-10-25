namespace SageFs.Simulation

open System
open SageFs.Simulation.LiveTestFileRoutingSim

/// Seeded generators for live-testing file-routing scenarios. Chaos is data: `run (fromSeed n)`
/// replays identically forever.
module LiveTestFileRoutingGenerators =

  /// A general scenario of 4 to 20 steps over the whole pool.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 21)
    let pick () = rnd.Next(0, poolSize)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 11) with
          | 0 | 1 -> RoutingOp.TurnOn (pick ())
          | 2 -> RoutingOp.TurnOff (pick ())
          | 3 -> RoutingOp.MakePrimary (pick ())
          | 4 -> RoutingOp.SaveOutsider
          | 5 -> RoutingOp.SaveLookalike
          | _ -> RoutingOp.Save (pick ()) ]
    { Seed = seed; Ops = ops }

  /// The reported case: the primary (session 0) has live testing off, session 1 has it on, and
  /// session 1 saves a file.
  let primaryOffOtherOn : Scenario =
    { Seed = 1
      Ops = [ RoutingOp.TurnOn 1; RoutingOp.MakePrimary 0; RoutingOp.Save 1 ] }

  /// Sessions 1 and 2 share a directory; only session 2 has live testing on.
  let sharedDirectoryOneOn : Scenario =
    { Seed = 2
      Ops = [ RoutingOp.TurnOn 2; RoutingOp.Save 1 ] }

  /// Session 3 lives inside session 0's directory. A save in session 3's directory belongs to
  /// session 3 alone even though session 0's directory contains it.
  let nestedWorktree : Scenario =
    { Seed = 3
      Ops = [ RoutingOp.TurnOn 0; RoutingOp.TurnOn 3; RoutingOp.Save 3; RoutingOp.Save 0 ] }

  /// A file no session owns goes to the fallback.
  let outsiderGoesToFallback : Scenario =
    { Seed = 4
      Ops = [ RoutingOp.TurnOn 0; RoutingOp.SaveOutsider ] }

  /// A save in `/sim/route/checkout-lookalike`, a sibling of session 0's directory whose name
  /// starts with the same text. Session 0 has live testing on and must not receive it.
  let lookalikeSibling : Scenario =
    { Seed = 5
      Ops = [ RoutingOp.TurnOn 0; RoutingOp.SaveLookalike ] }
