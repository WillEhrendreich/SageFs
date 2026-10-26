namespace SageFs.Simulation

open System
open SageFs.Simulation.SymbolTestsRoutingSim

/// Seeded generators for symbol-to-tests routing scenarios. Chaos is data: `run (fromSeed n)`
/// replays identically forever.
module SymbolTestsRoutingGenerators =

  /// A general scenario of 4 to 20 steps over the whole pool.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 21)
    let pick () = rnd.Next(0, poolSize)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 10) with
          | 0 | 1 -> SymbolOp.Analyse (pick ())
          | 2 | 3 -> SymbolOp.Run (pick ())
          | 4 -> SymbolOp.MakePrimary (pick ())
          | _ -> SymbolOp.Ask (pick ()) ]
    { Seed = seed; Ops = ops }

  /// The case seen on a real daemon: session 0 is primary and has analysed nothing, session 1 has
  /// analysed its file and run its tests, and session 1 is asked.
  let primaryEmptyOtherAnalysed : Scenario =
    { Seed = 1
      Ops =
        [ SymbolOp.MakePrimary 0
          SymbolOp.Analyse 1
          SymbolOp.Run 1
          SymbolOp.Ask 1 ] }

  /// Two sessions that both ran: each is answered with its own symbols, and the primary is the other.
  let twoSessionsEachItsOwn : Scenario =
    { Seed = 2
      Ops =
        [ SymbolOp.Analyse 0
          SymbolOp.Run 0
          SymbolOp.Analyse 2
          SymbolOp.Run 2
          SymbolOp.MakePrimary 2
          SymbolOp.Ask 0
          SymbolOp.Ask 2 ] }

  /// Analysed but not run: the answer is empty and says coverage is what is missing.
  let analysedNotRun : Scenario =
    { Seed = 3
      Ops = [ SymbolOp.Analyse 1; SymbolOp.Ask 1 ] }

  /// Nothing happened yet: the answer is empty and says nothing has been analysed.
  let nothingYet : Scenario =
    { Seed = 4
      Ops = [ SymbolOp.Ask 3 ] }
