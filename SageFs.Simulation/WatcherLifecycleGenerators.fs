namespace SageFs.Simulation

open System
open SageFs.Simulation.WatcherLifecycleSim

/// Seeded, dependency-free generators for watcher-lifecycle scenarios
/// (mirrors `FileReloadRoutingGenerators`). Chaos is data: `run (fromSeed
/// n)` replays identically forever. The pool mixes session
/// starts/stops/saves/overflows so a session that stops mid-claim, and an
/// overflow arriving while sessions come and go, are both actually
/// exercised.
module WatcherLifecycleGenerators =

  let private sessionIdxs = [| 0; 1; 2 |]
  let private claimableDirs = [| 1; 2; 3 |]
  let private allDirs = [| 0; 1; 2; 3 |]
  let private fileIdxs = [| 0; 1 |]

  /// A general scenario: 3-20 ops over the full pool. Same seed => identical
  /// list, forever.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(3, 21)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 4) with
          | 0 -> LifecycleOp.SessionStarts(sessionIdxs.[rnd.Next(sessionIdxs.Length)], claimableDirs.[rnd.Next(claimableDirs.Length)])
          | 1 -> LifecycleOp.SessionStops(sessionIdxs.[rnd.Next(sessionIdxs.Length)])
          | 2 -> LifecycleOp.Save(allDirs.[rnd.Next(allDirs.Length)], fileIdxs.[rnd.Next(fileIdxs.Length)])
          | _ -> LifecycleOp.Overflow(allDirs.[rnd.Next(allDirs.Length)]) ]
    { Seed = seed; Ops = ops }

  // ── Named minimal scenarios (the exact replays the tests assert on) ─────

  /// The headline leak reproduction: three sessions each claim their own
  /// directory, then all three stop. Real production behaviour (the
  /// periodic/prompt reconciliation this whole fix exists to make prompt)
  /// releases every one of them — mirrors "create and stop a session N
  /// times, watch count returns to baseline."
  let threeSessionsCreateThenStopAll : Scenario =
    { Seed = 100
      Ops =
        [ LifecycleOp.SessionStarts(0, 1)
          LifecycleOp.SessionStarts(1, 2)
          LifecycleOp.SessionStarts(2, 3)
          LifecycleOp.SessionStops 0
          LifecycleOp.SessionStops 1
          LifecycleOp.SessionStops 2 ] }

  /// A directory shared by two sessions must stay watched while EITHER one
  /// is still live, and only lose its watch once BOTH have stopped.
  let sharedDirReleasedOnlyWhenBothStop : Scenario =
    { Seed = 101
      Ops =
        [ LifecycleOp.SessionStarts(0, 1)
          LifecycleOp.SessionStarts(1, 1)
          LifecycleOp.SessionStops 0
          LifecycleOp.SessionStops 1 ] }

  /// A save arrives, then the watch on its directory overflows, then
  /// another save arrives after the recovery reset — every one of the
  /// three ops must still produce its own outcome.
  let saveThenOverflowThenSave : Scenario =
    { Seed = 102
      Ops =
        [ LifecycleOp.SessionStarts(0, 1)
          LifecycleOp.Save(1, 0)
          LifecycleOp.Overflow 1
          LifecycleOp.Save(1, 1) ] }

  /// An overflow on a directory nobody currently claims (e.g. the fallback)
  /// still produces its own honest outcome rather than being dropped.
  let overflowOnUnclaimedDir : Scenario =
    { Seed = 103
      Ops = [ LifecycleOp.Overflow 0 ] }

  /// Repeated create/stop cycles on the SAME directory index — the stress
  /// shape from the "rapid lifecycle cycles" testing style this repo
  /// already uses for process management; here for watch bookkeeping.
  let rapidCreateStopCycles : Scenario =
    { Seed = 104
      Ops =
        [ for _ in 1 .. 8 do
            yield LifecycleOp.SessionStarts(0, 1)
            yield LifecycleOp.SessionStops 0 ] }
