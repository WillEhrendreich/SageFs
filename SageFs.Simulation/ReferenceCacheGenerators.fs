namespace SageFs.Simulation

open System
open SageFs
open SageFs.Features
open SageFs.Simulation.ReferenceCacheSim

/// Seeded, dependency-free generators for a worker that answers `list_references` over a long afternoon. `fromSeed n`
/// replays identically forever.
module ReferenceCacheGenerators =

  let project (name: string) (coldSeconds: int) (megabytes: int) : ProjectCost =
    { Name = name; ColdSeconds = coldSeconds; CachedBytes = int64 megabytes * megabyte }

  let projectFrom (rnd: Random) (index: int) : ProjectCost =
    project (sprintf "P%d" index) (rnd.Next(2, 30)) (rnd.Next(100, 1600))

  let stepFrom (rnd: Random) : Step =
    match rnd.Next 8 with
    | 0
    | 1
    | 2
    | 3 -> Step.Ask [ for i in 1 .. rnd.Next(1, 5) -> projectFrom rnd i ]
    | 4
    | 5 -> Step.Idle(rnd.Next(1, 1200))
    | 6 -> Step.Squeeze(match rnd.Next 2 with | 0 -> MemoryPressure.Tight | _ -> MemoryPressure.Critical)
    | _ -> Step.Squeeze MemoryPressure.Normal

  /// The machine sizes an afternoon may run on, in gigabytes: the sizes people own, from the smallest that runs SageFs to
  /// the largest workstation. The ceiling is the real one for that machine, so the invariants are proven for the default
  /// a machine actually gets and not for a number picked to suit them.
  let machineSizesGb : int64 list = [ 2L; 4L; 8L; 16L; 32L; 64L; 128L ]

  let ceilingOnMachine (totalGb: int64) : int64 =
    (ResidentCeiling.ceilingOf (totalGb * 1024L * megabyte)).Bytes

  /// A general afternoon: asks, silences and a machine that tightens and eases. Same seed, identical scenario.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    { Seed = seed
      BaselineBytes = int64 (rnd.Next(200, 900)) * megabyte
      CeilingBytes = ceilingOnMachine machineSizesGb.[rnd.Next machineSizesGb.Length]
      RetentionSeconds = rnd.Next(60, 900)
      SweepEverySeconds = rnd.Next(5, 60)
      BudgetSeconds = rnd.Next(20, 90)
      Steps = [ for _ in 1 .. rnd.Next(4, 14) -> stepFrom rnd ] }

  /// Ten minutes of silence before an idle cache goes, a sweep every half minute, a one minute budget for an ask. The
  /// named scenarios below are written against these.
  let baselineRetention = 600
  let baselineSweep = 30
  let baselineBudget = 60

  let baseline : Scenario =
    { Seed = 500
      BaselineBytes = 500L * megabyte
      CeilingBytes = 3000L * megabyte
      RetentionSeconds = baselineRetention
      SweepEverySeconds = baselineSweep
      BudgetSeconds = baselineBudget
      Steps = [] }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// Two asks that together hold more than the ceiling: the second answer must leave the worker within it.
  let growsPastTheCeiling : Scenario =
    { baseline with
        Seed = 501
        Steps = [ Step.Ask [ project "Core" 10 1500 ]; Step.Ask [ project "Tests" 10 1800 ] ] }

  /// One ask, then nobody for longer than the retention.
  let goesQuiet : Scenario =
    { baseline with
        Seed = 502
        Steps = [ Step.Ask [ project "Core" 10 800 ]; Step.Idle 900 ] }

  /// One ask, then the machine tightens and a sweep comes round.
  let machineTightens : Scenario =
    { baseline with
        Seed = 503
        Steps = [ Step.Ask [ project "Core" 10 800 ]; Step.Squeeze MemoryPressure.Tight; Step.Idle 60 ] }

  /// The largest session measured, SageFs.Tests: eight projects of about 450 MB each over a 770 MB worker, 4.4 GB in all.
  /// The budget is long enough for eight cold checks, so only the ceiling can stop the ask.
  let eightProjects : ProjectCost list =
    [ for i in 1 .. 8 -> project (sprintf "P%d" i) 10 450 ]

  let eightProjectsTwice : Scenario =
    { baseline with
        Seed = 505
        BaselineBytes = 770L * megabyte
        BudgetSeconds = baselineBudget * 2
        Steps = [ Step.Ask eightProjects; Step.Ask eightProjects ] }

  /// Two asks of the same project inside the retention and under the ceiling: the second is warm.
  let staysWarm : Scenario =
    { baseline with
        Seed = 504
        Steps = [ Step.Ask [ project "Core" 20 800 ]; Step.Idle 60; Step.Ask [ project "Core" 20 800 ] ] }
