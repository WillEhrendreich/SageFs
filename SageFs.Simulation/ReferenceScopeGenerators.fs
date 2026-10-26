namespace SageFs.Simulation

open System
open SageFs.Simulation.ReferenceScopeSim

/// Seeded, dependency-free generators for sessions of several projects checked under one budget. Chaos is data:
/// `fromSeed n` replays identically forever. Costs and budgets are whole seconds so the simulated clock and the truth
/// agree exactly.
module ReferenceScopeGenerators =

  let project (rnd: Random) (index: int) : ProjectSpec =
    { Name = sprintf "P%d" index
      Cost = float (rnd.Next(1, 40))
      Behavior = (match rnd.Next 6 with | 0 -> Behavior.Raises | _ -> Behavior.Answers)
      Sites = rnd.Next(0, 6) }

  /// A general session: 1-5 projects under a budget of 10-90 seconds. Same seed, identical scenario.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let count = rnd.Next(1, 6)
    { Seed = seed
      Budget = float (rnd.Next(10, 91))
      Projects = [ for i in 1 .. count -> project rnd i ] }

  let spec (name: string) (cost: float) (sites: int) : ProjectSpec =
    { Name = name; Cost = cost; Behavior = Behavior.Answers; Sites = sites }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// Two projects that fit the budget together.
  let bothFit : Scenario = { Seed = 400; Budget = 60.0; Projects = [ spec "Core" 20.0 3; spec "Host" 20.0 2 ] }

  /// The first project uses most of the budget, so the second is not reached.
  let secondDoesNotFit : Scenario = { Seed = 401; Budget = 60.0; Projects = [ spec "Core" 40.0 3; spec "Host" 40.0 2 ] }

  /// Each project fits alone and the pair does not: a per-project budget would call this project-wide.
  let pairExceedsBudget : Scenario = { Seed = 402; Budget = 50.0; Projects = [ spec "A" 30.0 1; spec "B" 30.0 1; spec "C" 30.0 1 ] }

  /// The only project does not fit.
  let nothingFits : Scenario = { Seed = 403; Budget = 20.0; Projects = [ spec "Core" 90.0 4 ] }

  /// The first project's compiler fails and the second answers.
  let firstRaises : Scenario =
    { Seed = 404; Budget = 60.0; Projects = [ { spec "Core" 10.0 3 with Behavior = Behavior.Raises }; spec "Host" 10.0 2 ] }

  /// A session with no project at all.
  let bare : Scenario = { Seed = 405; Budget = 60.0; Projects = [] }
