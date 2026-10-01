namespace SageFs.Simulation

open System
open SageFs.Simulation.BuildConfirmationSim

/// Seeded, dependency-free generators for build-confirmation scenarios. Chaos is data:
/// `fromSeed n` replays identically forever.
module BuildConfirmationGenerators =

  let private contents = 4

  /// A general scenario: 4-32 ops over a handful of contents. Same seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 33)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 14) with
          | 0 | 1 | 2 -> Op.Evaluated(rnd.Next contents)
          | 3 -> Op.EvaluatedNothing
          | 4 | 5 -> Op.Edited(rnd.Next contents)
          | 6 | 7 -> Op.Quiet
          | 8 | 9 -> Op.BuildDone(rnd.Next(0, 4), (match rnd.Next 4 with | 0 -> BuildEnds.Fails | _ -> BuildEnds.Builds))
          | 10 | 11 -> Op.RunDone(rnd.Next(0, 4), (match rnd.Next 3 with | 0 -> Agreement.Differs | _ -> Agreement.Agrees))
          | _ -> Op.Deadline(rnd.Next(0, 4)) ]
    { Seed = seed; Ops = ops }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// The happy path: evaluated, quiet, the build works, the run against it agrees.
  let agrees : Scenario =
    { Seed = 300
      Ops = [ Op.Evaluated 1; Op.Quiet; Op.BuildDone(0, BuildEnds.Builds); Op.RunDone(0, Agreement.Agrees) ] }

  /// The build works and the tests run against it disagree with the eval.
  let differs : Scenario =
    { Seed = 301
      Ops = [ Op.Evaluated 1; Op.Quiet; Op.BuildDone(0, BuildEnds.Builds); Op.RunDone(0, Agreement.Differs) ] }

  /// The project does not build.
  let buildFails : Scenario =
    { Seed = 302
      Ops = [ Op.Evaluated 1; Op.Quiet; Op.BuildDone(0, BuildEnds.Fails) ] }

  /// A typing burst: three evaluated runs with no quiet window between them cost one build, for the last one.
  let burst : Scenario =
    { Seed = 303
      Ops = [ Op.Evaluated 0; Op.Evaluated 1; Op.Evaluated 2; Op.Quiet; Op.BuildDone(0, BuildEnds.Builds); Op.RunDone(0, Agreement.Agrees) ] }

  /// A newer buffer arrives while the build runs, and the build then finishes for text nobody has any more.
  let editedWhileBuilding : Scenario =
    { Seed = 304
      Ops = [ Op.Evaluated 0; Op.Quiet; Op.Edited 1; Op.BuildDone(0, BuildEnds.Builds) ] }

  /// A newer evaluated run arrives while the build runs, and a build of it starts: the older build then answers.
  /// Its answer must not land on the newer rows.
  let supersededWhileBuilding : Scenario =
    { Seed = 305
      Ops = [ Op.Evaluated 0; Op.Quiet; Op.Evaluated 1; Op.Quiet; Op.BuildDone(0, BuildEnds.Builds); Op.RunDone(0, Agreement.Differs) ] }

  /// The build never answers.
  let hungBuild : Scenario =
    { Seed = 306
      Ops = [ Op.Evaluated 0; Op.Quiet; Op.Deadline 0 ] }

  /// The build works but the run against it never answers.
  let hungRun : Scenario =
    { Seed = 307
      Ops = [ Op.Evaluated 0; Op.Quiet; Op.BuildDone(0, BuildEnds.Builds); Op.Deadline 0 ] }

  /// An eval that ran no tests: nothing to confirm, so nothing is built.
  let evaluatedNothing : Scenario =
    { Seed = 308
      Ops = [ Op.EvaluatedNothing; Op.Quiet ] }
