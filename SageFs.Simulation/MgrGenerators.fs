namespace SageFs.Simulation

open System
open SageFs.Simulation.WorkerLifecycleSim

/// Seeded, dependency-free generators for worker-lifecycle scenarios (mirrors
/// Phase 1's `Generators`). Chaos is data: `run (fromSeed n)` replays identically
/// forever. The generator deliberately reuses OLD pids as stragglers so a
/// pid-blind reducer is actually exercised — a generator that only ever emitted
/// the freshest pid could never expose the race.
module MgrGenerators =

  /// A general scenario: Create, then a mixed stream of hard resets, readies,
  /// exits, spawn-failures and stops. Every pid ever introduced stays in the
  /// pool, so ~half of all pid-carrying events reuse an earlier (often retired)
  /// pid — the straggler traffic the guard exists to reject.
  let fromSeed (seed: int) : MgrScenario =
    let rnd = Random(seed)
    let mutable nextPid = 1000
    let fresh () =
      let p = nextPid
      nextPid <- nextPid + 1
      p
    let allPids = ResizeArray<int>()
    let p0 = fresh ()
    allPids.Add p0
    let cmds = ResizeArray<MgrCommand>()
    cmds.Add(MgrCommand.Create p0)
    // Bias toward the freshest pid (the awaited worker) but frequently reuse an
    // earlier one, producing stragglers.
    let pickPid () =
      if allPids.Count > 1 && rnd.Next(0, 2) = 0 then allPids.[rnd.Next(0, allPids.Count)]
      else allPids.[allPids.Count - 1]
    let n = rnd.Next(4, 24)
    for _ in 1 .. n do
      match rnd.Next(0, 10) with
      | 0 | 1 ->
        let np = fresh ()
        allPids.Add np
        cmds.Add(MgrCommand.HardReset np)
      | 2 | 3 | 4 -> cmds.Add(MgrCommand.Ready(pickPid ()))
      | 5 | 6 -> cmds.Add(MgrCommand.Exited(pickPid (), (if rnd.Next(0, 3) = 0 then 0 else 1)))
      | 7 | 8 -> cmds.Add(MgrCommand.SpawnFailed(pickPid ()))
      | _ -> cmds.Add(MgrCommand.Stop)
    { Seed = seed; Commands = List.ofSeq cmds }

  // ── Named canonical race scenarios (worked examples) ──────────────────────

  /// The retired old worker's late Ready arrives AFTER the swap already
  /// committed the new worker. Real guard: inert. Pid-blind: re-points the
  /// registry at the dead old worker.
  let staleReadyAfterCommit : MgrScenario =
    { Seed = -101
      Commands =
        [ MgrCommand.Create 1000
          MgrCommand.HardReset 1001
          MgrCommand.Ready 1001        // new worker commits: 1000 retired
          MgrCommand.Ready 1000 ] }    // straggler from the dead old worker

  /// A retired worker's late SpawnFailed arrives after the swap committed. Real
  /// guard: inert. Pid-blind: tombstones the healthy session.
  let staleSpawnFailedAfterCommit : MgrScenario =
    { Seed = -102
      Commands =
        [ MgrCommand.Create 1000
          MgrCommand.HardReset 1001
          MgrCommand.Ready 1001        // new worker commits: 1000 retired
          MgrCommand.SpawnFailed 1000 ] } // straggler spawn-failure from the dead old worker

  /// The old worker's late Ready arrives DURING the swap (before the new worker
  /// is ready). Real guard: inert (session stays Swapping). Pid-blind: commits
  /// the old pid, clearing the pending swap — the T8 bug.
  let staleReadyDuringSwap : MgrScenario =
    { Seed = -103
      Commands =
        [ MgrCommand.Create 1000
          MgrCommand.HardReset 1001
          MgrCommand.Ready 1000 ] }    // old worker's stale ready mid-swap

  /// The retiring old worker's late SpawnFailed arrives DURING the swap (its
  /// awaitWorkerPort task posting on EOF after it was killed). It carries the
  /// parked OLD pid. It must be inert — faulting the session here would kill a
  /// healthy swap. This is the case the DST harness surfaced (the old
  /// `WorkerSpawnFailed` fallback faulted); WorkerEventGuard.classifySpawnFailed
  /// now ignores it, uniform with Ready/Exited.
  let staleSpawnFailedDuringSwap : MgrScenario =
    { Seed = -105
      Commands =
        [ MgrCommand.Create 1000
          MgrCommand.HardReset 1001
          MgrCommand.SpawnFailed 1000 ] } // retiring old worker's straggler mid-swap

  /// A clean, race-free lifecycle: create, swap, commit, graceful stop. Holds
  /// under BOTH reducers — the control that shows the invariant is not vacuous.
  let cleanLifecycle : MgrScenario =
    { Seed = -104
      Commands =
        [ MgrCommand.Create 1000
          MgrCommand.HardReset 1001
          MgrCommand.Ready 1001
          MgrCommand.Stop ] }
