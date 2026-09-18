namespace SageFs.Simulation

open System
open SageFs.Simulation.FileReloadRoutingSim

/// Seeded, dependency-free generators for file-reload claim-routing
/// scenarios (mirrors `EvalActorGenerators` / `CohortLandingGenerators`).
/// Chaos is data: `run (fromSeed n)` replays identically forever. The pool
/// mixes `Claim`/`Unclaim`/`Save`/`Drain` so a claim-removed-mid-debounce
/// and a shared-dir save are both actually exercised.
module FileReloadRoutingGenerators =

  /// Claimable dir indices — dir 0 is the reserved fallback and is
  /// deliberately excluded from Claim/Unclaim so it never acquires a real
  /// session claim (matches production: the fallback dir claims no
  /// session).
  let private claimableDirs = [| 1; 2; 3 |]

  /// Save may target any dir, including the fallback (0).
  let private allDirs = [| 0; 1; 2; 3 |]

  let private sessionIdxs = [| 0; 1; 2 |]
  let private fileIdxs = [| 0; 1 |]

  /// A general scenario: 3-14 ops over the full pool. Same seed => identical
  /// list, forever.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(3, 15)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 4) with
          | 0 -> RoutingOp.Claim(claimableDirs.[rnd.Next(claimableDirs.Length)], sessionIdxs.[rnd.Next(sessionIdxs.Length)])
          | 1 -> RoutingOp.Unclaim(claimableDirs.[rnd.Next(claimableDirs.Length)], sessionIdxs.[rnd.Next(sessionIdxs.Length)])
          | 2 -> RoutingOp.Save(allDirs.[rnd.Next(allDirs.Length)], fileIdxs.[rnd.Next(fileIdxs.Length)])
          | _ -> RoutingOp.Drain ]
    { Seed = seed; Ops = ops }

  // ── Named minimal scenarios (the exact replays the tests assert on) ─────

  /// Two sessions share one claimed dir; a save under it drains to BOTH —
  /// the headline replay for the deleted real-watcher test "file save in a
  /// shared dir fires FileReloaded for every owning session."
  let sharedDirBothOwnersReload : Scenario =
    { Seed = 1
      Ops = [ RoutingOp.Claim(1, 0); RoutingOp.Claim(1, 1); RoutingOp.Save(1, 0); RoutingOp.Drain ] }

  /// Two sessions share a dir; one's claim is removed before the next save
  /// drains — only the remaining claimant is notified. The headline replay
  /// for the deleted real-watcher test "removing one session's claim stops
  /// only its FileReloaded events."
  let removingOneClaimStopsOnlyItsEvents : Scenario =
    { Seed = 2
      Ops =
        [ RoutingOp.Claim(1, 0)
          RoutingOp.Claim(1, 1)
          RoutingOp.Unclaim(1, 1)
          RoutingOp.Save(1, 0)
          RoutingOp.Drain ] }

  /// A save under the session-less fallback dir (dir 0) never fires
  /// FileReloaded for anyone, even though a session is actively claiming a
  /// different dir at the time.
  let fallbackSaveReloadsNoOne : Scenario =
    { Seed = 3
      Ops = [ RoutingOp.Claim(1, 0); RoutingOp.Save(0, 0); RoutingOp.Drain ] }

  /// A claim is removed AFTER the save but BEFORE the debounce drains — the
  /// stale reload is dropped, not misattributed (mirrors
  /// LiveTestWatcherCoreTests T1, replayed here through the full
  /// claim/save/drain routing fold rather than the bare pure-core calls).
  let staleClaimRemovedDuringDebounce : Scenario =
    { Seed = 4
      Ops = [ RoutingOp.Claim(1, 0); RoutingOp.Save(1, 1); RoutingOp.Unclaim(1, 0); RoutingOp.Drain ] }

  /// Two independently-claimed dirs, each with its own save, drained
  /// together — proves no cross-dir leakage (dir A's save never reaches
  /// dir B's claimant, and vice versa).
  let twoDirsIndependentClaims : Scenario =
    { Seed = 5
      Ops =
        [ RoutingOp.Claim(1, 0)
          RoutingOp.Claim(2, 1)
          RoutingOp.Save(1, 0)
          RoutingOp.Save(2, 1)
          RoutingOp.Drain ] }
