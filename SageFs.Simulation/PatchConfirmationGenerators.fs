namespace SageFs.Simulation

open System
open SageFs.Simulation.PatchConfirmationSim

/// Seeded, dependency-free generators for patch-confirmation scenarios. Chaos
/// is data: `fromSeed n` replays identically forever.
module PatchConfirmationGenerators =

  let private pool = [| 0; 1; 2; unprobeableDecl |]

  let private decls (rnd: Random) : int list =
    let count = rnd.Next(1, 4)
    [ for _ in 1 .. count -> pool.[rnd.Next pool.Length] ] |> List.distinct

  /// A general scenario: 3-24 ops over the pool. Same seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(3, 25)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 6) with
          | 0 | 1 -> Op.Save(decls rnd)
          | 2 | 3 -> Op.Enter(pool.[rnd.Next pool.Length])
          | _ -> Op.Bound(rnd.Next(0, 8)) ]
    { Seed = seed; Ops = ops }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// The shipped bug in one line: a save lands, and the function it patched is
  /// never called (the caller inlined the old body).
  let savedButNeverCalled : Scenario =
    { Seed = 200; Ops = [ Op.Save [ 0 ] ] }

  /// The happy path: a save lands and the page calls the function.
  let savedThenCalled : Scenario =
    { Seed = 201; Ops = [ Op.Save [ 0 ]; Op.Enter 0 ] }

  /// One function of two runs, the other never does.
  let oneOfTwoCalled : Scenario =
    { Seed = 202; Ops = [ Op.Save [ 0; 1 ]; Op.Enter 0; Op.Bound 0 ] }

  /// A second save replaces the first before the first's function ran.
  let replacedBeforeItRan : Scenario =
    { Seed = 203; Ops = [ Op.Save [ 0 ]; Op.Save [ 0 ]; Op.Enter 0 ] }

  /// The function ran AFTER the bound: the wait gave up first, and that is
  /// what it reports.
  let calledAfterTheBound : Scenario =
    { Seed = 204; Ops = [ Op.Save [ 0 ]; Op.Bound 0; Op.Enter 0 ] }

  /// A function that could not be given a probe can never be confirmed.
  let unobservableFunction : Scenario =
    { Seed = 205; Ops = [ Op.Save [ unprobeableDecl ]; Op.Enter unprobeableDecl ] }
