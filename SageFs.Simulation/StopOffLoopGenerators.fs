namespace SageFs.Simulation

open System
open SageFs.Simulation.StopOffLoopSim

/// Seeded, dependency-free generators for the stop scenarios. Chaos is data: `fromSeed n` replays identically forever.
/// Every generated operation is legal against the sessions that exist at that point: a stop only names a session
/// still registered, a release only names a worker still leaving, and a session is restarted at most once.
module StopOffLoopGenerators =

  type private Plan =
    { Live: Set<int>
      Leaving: Set<int>
      Restarted: Set<int>
      Next: int }

  let private pick (rnd: Random) (from: Set<int>) : int =
    let items = Set.toArray from
    items.[rnd.Next items.Length]

  /// The next legal operation, and the plan after it.
  let private next (rnd: Random) (plan: Plan) : Op * Plan =
    let restartable = Set.difference plan.Live plan.Restarted
    match rnd.Next 100 with
    | n when n < 28 && not (Set.isEmpty plan.Live) ->
      let s = pick rnd plan.Live
      Op.Stop s, { plan with Live = Set.remove s plan.Live; Leaving = Set.add s plan.Leaving }
    | n when n < 58 && not (Set.isEmpty plan.Leaving) ->
      let s = pick rnd plan.Leaving
      Op.Release s, { plan with Leaving = Set.remove s plan.Leaving }
    | n when n < 68 && not (Set.isEmpty restartable) ->
      let s = pick rnd restartable
      Op.Restart s, { plan with Restarted = Set.add s plan.Restarted }
    | n when n < 76 -> Op.Create, { plan with Live = Set.add plan.Next plan.Live; Next = plan.Next + 1 }
    | n when n < 88 && not (Set.isEmpty plan.Live) -> Op.Get (pick rnd plan.Live), plan
    | _ -> Op.List, plan

  /// A general history: 2-5 sessions, 5-24 operations. Same seed, identical scenario.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let sessions = rnd.Next(2, 6)
    let count = rnd.Next(5, 25)
    let start = { Live = Set.ofList [ 1 .. sessions ]; Leaving = Set.empty; Restarted = Set.empty; Next = sessions + 1 }
    let ops, _ =
      [ 1 .. count ]
      |> List.fold (fun (ops, plan) _ -> let op, plan' = next rnd plan in op :: ops, plan') ([], start)
    { Seed = seed; Sessions = sessions; Ops = List.rev ops }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// Roast R-SERVER-3 in one line: a stop is in flight and a list comes in.
  let aListDuringAStop : Scenario =
    { Seed = 700; Sessions = 2; Ops = [ Op.Stop 1; Op.List; Op.Release 1; Op.List ] }

  /// Four agents each stop a session at once: all four must be answered and leaving together (the 22 s queue).
  let fourStopsAtOnce : Scenario =
    { Seed = 701; Sessions = 4; Ops = [ Op.Stop 1; Op.Stop 2; Op.Stop 3; Op.Stop 4; Op.List; Op.Release 3; Op.Release 1; Op.Release 4; Op.Release 2; Op.List ] }

  /// Every other command, one by one, while a stop is in flight.
  let everyCommandDuringAStop : Scenario =
    { Seed = 702; Sessions = 3; Ops = [ Op.Stop 1; Op.Get 2; Op.Create; Op.Restart 2; Op.List; Op.Release 1 ] }
