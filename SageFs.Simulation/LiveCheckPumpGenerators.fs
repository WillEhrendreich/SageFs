namespace SageFs.Simulation

open System
open SageFs.Features.LiveTesting
open SageFs.Simulation.LiveCheckPumpSim

/// Seeded, dependency-free generators for live-check scenarios. Chaos is data: `fromSeed n` replays
/// identically forever.
module LiveCheckPumpGenerators =

  let private texts = 6

  /// A general scenario: 4-32 ops over a handful of texts. Same seed, identical list. Odd seeds are NewestWins
  /// (the type-check), even seeds EveryOneRuns (the eval).
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 33)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 20) with
          | 0 | 1 | 2 | 3 -> Op.Edit(rnd.Next texts)
          | 4 | 5 -> Op.WorkerStops
          | 6 | 7 -> Op.WorkerSpawns
          | 8 | 9 -> Op.WorkerWarmed
          | 10 -> Op.WorkerFaults
          | 11 | 12 | 13 | 14 | 15 -> Op.Answer(rnd.Next(0, 4))
          | _ -> Op.Deadline(rnd.Next(0, 3)) ]
    { Seed = seed
      Supersession = (match seed % 2 with | 0 -> Supersession.EveryOneRuns | _ -> Supersession.NewestWins)
      Ops = ops }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// Nothing goes wrong: a request, the worker answers.
  let quiet : Scenario =
    { Seed = 400
      Supersession = Supersession.NewestWins
      Ops = [ Op.Edit 1; Op.Answer 0 ] }

  /// A text is typed while the project builds and there is no proxy at all, and another after it. The worker comes
  /// back. The newest text must be judged.
  let editWhileBuilding : Scenario =
    { Seed = 401
      Supersession = Supersession.NewestWins
      Ops = [ Op.Edit 1; Op.Answer 0; Op.WorkerStops; Op.Edit 2; Op.Edit 4; Op.WorkerSpawns; Op.WorkerWarmed ] }

  /// A text is typed after the replacement worker has a proxy and before it is Ready. A worker still warming says
  /// errors that are not there, so it must not be asked.
  let editWhileWarming : Scenario =
    { Seed = 402
      Supersession = Supersession.NewestWins
      Ops = [ Op.WorkerStops; Op.WorkerSpawns; Op.Edit 1; Op.Answer 0; Op.WorkerWarmed ] }

  /// A text is asked of the worker, and the worker is retired before it answers. The answer is the retired
  /// worker's, so it counts for nothing, and the text is asked again of the replacement.
  let retiredUnderTheRequest : Scenario =
    { Seed = 403
      Supersession = Supersession.NewestWins
      Ops = [ Op.Edit 1; Op.WorkerStops; Op.Answer 0; Op.WorkerSpawns; Op.WorkerWarmed ] }

  /// Two texts are typed, the second while the first is in flight. The first one's answer is for text nobody has any
  /// more.
  let supersededWhileInFlight : Scenario =
    { Seed = 404
      Supersession = Supersession.NewestWins
      Ops = [ Op.Edit 1; Op.Edit 2; Op.Answer 0 ] }

  /// Two requests that must both run (evals), the second made while the first is in flight.
  let twoThatMustBothRun : Scenario =
    { Seed = 405
      Supersession = Supersession.EveryOneRuns
      Ops = [ Op.Edit 1; Op.Edit 2 ] }

  /// Evals that must all run, across a restart: made before, during and after, answered in order.
  let everyOneRunsAcrossARestart : Scenario =
    { Seed = 406
      Supersession = Supersession.EveryOneRuns
      Ops = [ Op.Edit 1; Op.Edit 2; Op.WorkerStops; Op.Answer 0; Op.Edit 3; Op.WorkerSpawns; Op.Edit 4; Op.WorkerWarmed ] }

  /// The session faults while a text waits for a worker: nothing is coming, and the text ends unanswered, saying why.
  let sessionFaults : Scenario =
    { Seed = 407
      Supersession = Supersession.NewestWins
      Ops = [ Op.WorkerStops; Op.Edit 1; Op.WorkerFaults ] }

  /// No worker comes in time: the wait reaches its deadline and the text ends unanswered, saying so. A worker that
  /// comes later does not revive it.
  let waitRunsOut : Scenario =
    { Seed = 408
      Supersession = Supersession.NewestWins
      Ops = [ Op.WorkerStops; Op.Edit 1; Op.Deadline 0; Op.WorkerSpawns; Op.WorkerWarmed ] }

  /// A wait that is over is woken again: nothing happens.
  let staleWake : Scenario =
    { Seed = 409
      Supersession = Supersession.NewestWins
      Ops = [ Op.WorkerStops; Op.Edit 1; Op.WorkerSpawns; Op.WorkerWarmed; Op.Deadline 0 ] }

  /// The replacement is retired while it warms, and a text is typed in the second gap.
  let restartTwice : Scenario =
    { Seed = 410
      Supersession = Supersession.NewestWins
      Ops = [ Op.WorkerStops; Op.WorkerSpawns; Op.Edit 1; Op.WorkerStops; Op.Edit 2; Op.WorkerSpawns; Op.WorkerWarmed ] }
