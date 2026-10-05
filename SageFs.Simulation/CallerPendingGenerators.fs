namespace SageFs.Simulation

open System
open SageFs.Simulation.CallerPendingSim

/// Seeded, dependency-free generators for multi-file edit histories. Chaos is data: `fromSeed n` replays identically
/// forever.
module CallerPendingGenerators =

  let private op (rnd: Random) : Op =
    let fn = rnd.Next 2
    let file = rnd.Next(1, 3)
    match rnd.Next 15 with
    | 0 | 1 | 2 -> Op.ResignLib fn
    | 3 -> Op.BodyEditLib fn
    | 4 -> Op.RemoveLib fn
    | 5 | 6 -> Op.RenameLib fn
    | 7 | 8 | 9 | 10 -> Op.SaveCaller file
    | 11 -> Op.SaveCallerUnchanged file
    | 12 -> Op.SaveCallerBroken file
    | _ -> Op.Restart

  /// A general history: 2-14 saves over the library and the two caller files. Same seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(2, 15)
    { Seed = seed; Ops = [ for _ in 1 .. n -> op rnd ] }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// The window in one line: the library re-signs `f0`, then the caller of `f0` is saved.
  let resignThenCallerSaved : Scenario = { Seed = 300; Ops = [ Op.ResignLib 0; Op.SaveCaller 1 ] }

  /// The caller was saved first, then the library re-signed what it calls.
  let callerSavedBeforeTheResign : Scenario = { Seed = 301; Ops = [ Op.SaveCaller 1; Op.ResignLib 0 ] }

  /// A caller that already landed is saved again.
  let callerSavedTwice : Scenario = { Seed = 302; Ops = [ Op.ResignLib 0; Op.SaveCaller 1; Op.SaveCaller 1 ] }

  /// The library re-signs, the caller lands, and the library re-signs again.
  let resignedTwice : Scenario = { Seed = 303; Ops = [ Op.ResignLib 0; Op.SaveCaller 1; Op.ResignLib 0 ] }

  /// A removed function's caller is pending until it drops the call.
  let removalStrandsTheCaller : Scenario = { Seed = 304; Ops = [ Op.RemoveLib 0; Op.SaveCaller 1 ] }

  /// A rename is a removal of the old name.
  let renameStrandsTheOldName : Scenario = { Seed = 305; Ops = [ Op.RenameLib 0 ] }

  /// A body-only edit is the same method, so nobody is stranded.
  let bodyEditStrandsNobody : Scenario = { Seed = 306; Ops = [ Op.BodyEditLib 0 ] }

  /// Saving the caller with no edit does not move its call.
  let unchangedCallerSave : Scenario = { Seed = 307; Ops = [ Op.ResignLib 0; Op.SaveCallerUnchanged 1 ] }

  /// A restart is a process with no old methods.
  let restartClears : Scenario = { Seed = 308; Ops = [ Op.ResignLib 0; Op.Restart ] }

  /// Two functions re-signed, two callers stranded; saving one leaves the other pending.
  let twoCallersOneSaved : Scenario = { Seed = 309; Ops = [ Op.ResignLib 0; Op.ResignLib 1; Op.SaveCaller 1 ] }

  /// A caller saved broken does not land, so it is still stranded.
  let brokenCallerSaveDoesNotLand : Scenario = { Seed = 310; Ops = [ Op.ResignLib 0; Op.SaveCallerBroken 1 ] }
