namespace SageFs.Simulation

open System
open SageFs.Simulation.GuardSim

/// Seeded, dependency-free generators for guard scenarios. Chaos is data: `fromSeed n` replays identically forever.
module GuardSimGenerators =

  /// A general scenario: 4 to 30 ops over the clicks and methods. Same seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 31)
    let click () = rnd.Next(0, clickCount)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 12) with
          | 0 | 1 | 2 -> Op.Start(click (), rnd.Next(0, methodCount))
          | 3 | 4 -> Op.Deadline(click ())
          | 5 | 6 -> Op.ThreadEnds(click ())
          | 7 -> Op.Finish(click ())
          | 8 | 9 -> Op.GiveUp(click ())
          | _ -> Op.Reload(rnd.Next(0, methodCount)) ]
    { Seed = seed; Ops = ops }

  // Named minimal scenarios (the exact replays the tests assert on). Method 0 reaches 0, 1, 2 and 3.

  /// The click gives up on a thread that is still running guarded code, and the thread ends later.
  let abandonedThreadKeepsGuards : Scenario =
    { Seed = 300; Ops = [ Op.Start(0, 0); Op.Deadline 0; Op.GiveUp 0; Op.ThreadEnds 0 ] }

  /// The getter ends just after the stop was asked for, and the click sees it within its grace.
  let endsJustAtTheDeadline : Scenario =
    { Seed = 301; Ops = [ Op.Start(0, 0); Op.Deadline 0; Op.ThreadEnds 0; Op.Finish 0 ] }

  /// The thread ended in the instant before the click's verdict: the click had not looked yet, and gives up on nothing.
  let endsBeforeTheVerdict : Scenario =
    { Seed = 302; Ops = [ Op.Start(0, 0); Op.Deadline 0; Op.ThreadEnds 0; Op.GiveUp 0 ] }

  /// Two clicks reach method 3 (one through 1, one through 2); the first is done, the second is not.
  let sharedHelperTwoClicks : Scenario =
    { Seed = 303; Ops = [ Op.Start(0, 1); Op.Start(1, 2); Op.ThreadEnds 0; Op.Finish 0; Op.ThreadEnds 1; Op.Finish 1 ] }

  /// Hot reload re-points method 3 while a click holds it.
  let reloadDuringClick : Scenario =
    { Seed = 304; Ops = [ Op.Start(0, 0); Op.Reload 3; Op.Deadline 0; Op.ThreadEnds 0; Op.Finish 0 ] }
