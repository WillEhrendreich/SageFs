namespace SageFs.Simulation

open System
open SageFs.Simulation.ReadyWaitSim

/// Seeded, dependency-free generators for ready-wait scenarios. Chaos is data: `fromSeed n`
/// replays identically forever.
module ReadyWaitGenerators =

  /// A general scenario: 4-32 ops, starting either serving or warming. Same seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 33)
    let start = match rnd.Next 4 with | 0 -> Start.Warming | _ -> Start.Serving
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 16) with
          | 0 | 1 | 2 | 3 -> Op.Arrive
          | 4 | 5 -> Op.StartRebuild
          | 6 | 7 -> Op.BuildSucceeds
          | 8 -> Op.BuildFails
          | 9 | 10 -> Op.WorkerReady
          | 11 -> Op.Fault
          | 12 | 13 -> Op.Deadline(rnd.Next(0, 4))
          | _ -> Op.Poll ]
    { Seed = seed; Start = start; Ops = ops }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// A caller arrives while a rebuild runs; the build works; the new worker comes up.
  let arrivesDuringRebuild : Scenario =
    { Seed = 400
      Start = Start.Serving
      Ops = [ Op.StartRebuild; Op.Arrive; Op.BuildSucceeds; Op.WorkerReady ] }

  /// A caller arrives while a rebuild runs, and the build fails.
  let rebuildFails : Scenario =
    { Seed = 401
      Start = Start.Serving
      Ops = [ Op.StartRebuild; Op.Arrive; Op.BuildFails ] }

  /// A caller arrives while a rebuild runs, and gives up before it ends.
  let givesUp : Scenario =
    { Seed = 402
      Start = Start.Serving
      Ops = [ Op.StartRebuild; Op.Arrive; Op.Deadline 0; Op.BuildSucceeds; Op.WorkerReady ] }

  /// A caller arrives on a session that is warming, and the first worker comes up.
  let warmsUp : Scenario =
    { Seed = 403
      Start = Start.Warming
      Ops = [ Op.Arrive; Op.WorkerReady ] }

  /// A caller arrives while a rebuild runs and the session then faults.
  let faultsDuringRebuild : Scenario =
    { Seed = 404
      Start = Start.Serving
      Ops = [ Op.StartRebuild; Op.Arrive; Op.BuildSucceeds; Op.Fault ] }

  /// Nothing is rebuilding: a caller is told there is nothing to wait for.
  let nothingToWaitFor : Scenario =
    { Seed = 405
      Start = Start.Serving
      Ops = [ Op.Arrive ] }
