namespace SageFs.Simulation

open SageFs.Simulation.ReplLevelSim

/// Scenarios for the leveling sim. Every op is valid in every state (an op that has nothing to act on does nothing), so
/// a generated scenario never fails on its own fixture. `fromSeed` is a pure function of the seed: a failing seed replays.
module ReplLevelGenerators =

  /// The ops with their weights. Saves and asks are frequent, the host-resetting ops are rare, and defining something in the
  /// host is rare enough that a level still gets to act on a clean one.
  let private weighted : (int * Op) list =
    [ 3, Op.Submit
      3, Op.EvalFinished
      1, Op.Cancel
      4, Op.RunStart
      4, Op.RunEnd
      1, Op.DefineDynamicTests
      1, Op.RunInitScript
      3, Op.Save
      4, Op.DaemonAsks
      4, Op.LevelBegin
      5, Op.LevelAct
      4, Op.AnswerArrives
      1, Op.WorkerSwap
      1, Op.HostRebuilt ]

  let private total = weighted |> List.sumBy fst

  let private pick (rnd: System.Random) : Op =
    let mutable roll = rnd.Next total
    let mutable chosen = snd weighted.Head
    let mutable found = false
    for weight, op in weighted do
      match found with
      | true -> ()
      | false ->
        match roll < weight with
        | true ->
          chosen <- op
          found <- true
        | false -> roll <- roll - weight
    chosen

  let fromSeed (seed: int) : Scenario =
    let rnd = System.Random seed
    { Seed = seed; Ops = [ for _ in 1 .. rnd.Next(5, 70) -> pick rnd ] }

  /// A run is in flight, then a level is asked for: the level waits for it, the run ends, and the level acts.
  let levelWaitsForRun : Scenario =
    { Seed = 1
      Ops = [ Op.RunStart; Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.RunEnd; Op.LevelAct; Op.AnswerArrives ] }

  /// A run arrives while the level is pending, and is held, then served when the level ends.
  let runArrivesDuringLevel : Scenario =
    { Seed = 2
      Ops = [ Op.RunStart; Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.RunStart; Op.RunEnd; Op.LevelAct; Op.AnswerArrives ] }

  /// The handler reads the facts and a run arrives before the actor acts.
  let runBetweenReadAndAct : Scenario =
    { Seed = 3
      Ops = [ Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.RunStart; Op.LevelAct; Op.AnswerArrives ] }

  /// An eval finished since the host was built, so the REPL holds its definitions.
  let evalThenLevel : Scenario =
    { Seed = 4
      Ops = [ Op.Submit; Op.EvalFinished; Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.LevelAct; Op.AnswerArrives ] }

  /// The init script defined values in the host.
  let initScriptThenLevel : Scenario =
    { Seed = 10
      Ops = [ Op.RunInitScript; Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.LevelAct; Op.AnswerArrives ] }

  /// Tests an editor buffer defined live in the host.
  let dynamicTestsThenLevel : Scenario =
    { Seed = 11
      Ops = [ Op.DefineDynamicTests; Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.LevelAct; Op.AnswerArrives ] }

  /// An eval is still running when the level is handled.
  let levelDuringEval : Scenario =
    { Seed = 5
      Ops = [ Op.Submit; Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.LevelAct; Op.AnswerArrives ] }

  /// A worker answers, the worker is replaced, a save lands and the session asks the new worker, and then the old worker's
  /// answer arrives.
  let answerFromReplacedWorker : Scenario =
    { Seed = 6
      Ops = [ Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.LevelAct; Op.WorkerSwap; Op.Save; Op.DaemonAsks; Op.AnswerArrives ] }

  /// A save lands between the reset and its answer arriving.
  let saveBeforeAnswer : Scenario =
    { Seed = 7
      Ops = [ Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.LevelAct; Op.Save; Op.AnswerArrives ] }

  /// A clean host, one save, nothing in the way: the whole protocol runs and the state ends level.
  let cleanLevel : Scenario =
    { Seed = 8
      Ops = [ Op.Save; Op.DaemonAsks; Op.LevelBegin; Op.LevelAct; Op.AnswerArrives ] }

  /// Runs arrive during a pending level and the level then defers (an eval ran first), and the held runs are still served.
  let deferredLevelStillServesHeld : Scenario =
    { Seed = 9
      Ops =
        [ Op.Submit; Op.EvalFinished; Op.RunStart; Op.Save; Op.DaemonAsks; Op.LevelBegin
          Op.RunStart; Op.RunEnd; Op.LevelAct; Op.AnswerArrives ] }
