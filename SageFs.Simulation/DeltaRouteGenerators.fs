namespace SageFs.Simulation

open System
open SageFs.Simulation.DeltaRouteSim

/// Seeded, dependency-free generators for delta-route scenarios. Chaos is data: `fromSeed n` replays identically forever.
module DeltaRouteGenerators =

  /// A general scenario: 6 to 40 ops over three saves and three requests, a few versions the emitter refuses. Same
  /// seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(6, 41)
    let slot () = rnd.Next(0, slotCount)
    let rude = Set.ofList [ for v in 1 .. 12 do if rnd.Next(0, 6) = 0 then yield v ]
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 26) with
          | 0 | 1 | 2 | 3 -> Op.Edit
          | 4 | 5 | 6 | 7 -> Op.Prepare(slot ())
          | 8 | 9 | 10 | 11 -> Op.Apply(slot ())
          | 12 -> Op.ArmFault
          | 13 -> Op.ReplaceProcess(rnd.Next(0, 2) = 0)
          | 14 | 15 -> Op.Respawn
          | 16 -> (match rnd.Next(0, 2) with | 0 -> Op.AttachDebugger | _ -> Op.DetachDebugger)
          | 17 | 18 | 19 -> Op.RequestStarts(slot ())
          | 20 | 21 | 22 -> Op.RequestEnds(slot ())
          | _ -> Op.BoundElapses(slot ()) ]
    { Seed = seed; Rude = rude; Ops = ops }

  // Named minimal scenarios (the exact replays the tests assert on).

  /// The runtime refuses the second delta, and a third save arrives before the app is back.
  let failedApplyThenAnotherSave : Scenario =
    { Seed = 400
      Rude = Set.empty
      Ops = [ Op.Edit; Op.Prepare 0; Op.Apply 0; Op.Edit; Op.Prepare 1; Op.ArmFault; Op.Apply 1; Op.Edit; Op.Prepare 2; Op.Apply 2 ] }

  /// A second save is picked up while the first is still waiting to be applied: both were prepared from the same generation.
  let secondSaveLandsDuringAnApply : Scenario =
    { Seed = 401
      Rude = Set.empty
      Ops = [ Op.Edit; Op.Prepare 0; Op.Edit; Op.Prepare 1; Op.Apply 0; Op.Apply 1 ] }

  /// The process is replaced and the route is not told: its chain is for a module that is gone.
  let hostRestartLosesTheChain : Scenario =
    { Seed = 402
      Rude = Set.empty
      Ops = [ Op.Edit; Op.Prepare 0; Op.Apply 0; Op.Edit; Op.ReplaceProcess false; Op.Edit; Op.Prepare 1; Op.Apply 1 ] }

  /// A build lands between two saves: the first was prepared and never applied, the second is built after it.
  let rebuildBetweenTwoSaves : Scenario =
    { Seed = 403
      Rude = Set.empty
      Ops = [ Op.Edit; Op.Prepare 0; Op.Edit; Op.Prepare 1; Op.Apply 1 ] }

  /// A request is inside the method when the delta lands and finishes afterwards: it ran the OLD body.
  let reloadDuringARequest : Scenario =
    { Seed = 404
      Rude = Set.empty
      Ops = [ Op.RequestStarts 0; Op.Edit; Op.Prepare 0; Op.Apply 0; Op.RequestEnds 0; Op.BoundElapses 0 ] }

  /// The same, and then a request that enters the new body.
  let reloadThenANewRequest : Scenario =
    { Seed = 405
      Rude = Set.empty
      Ops = [ Op.RequestStarts 0; Op.Edit; Op.Prepare 0; Op.Apply 0; Op.RequestEnds 0; Op.RequestStarts 1; Op.BoundElapses 0 ] }

  /// A version the emitter refuses is never handed to the runtime.
  let refusedEdit : Scenario =
    { Seed = 406
      Rude = Set.ofList [ 1 ]
      Ops = [ Op.Edit; Op.Prepare 0; Op.Apply 0; Op.Respawn; Op.Edit; Op.Prepare 1; Op.Apply 1 ] }

  /// A debugger attaches before a save, and detaches after.
  let debuggerAttached : Scenario =
    { Seed = 407
      Rude = Set.empty
      Ops = [ Op.AttachDebugger; Op.Edit; Op.Prepare 0; Op.Apply 0; Op.DetachDebugger; Op.Respawn; Op.Edit; Op.Prepare 1; Op.Apply 1 ] }
