namespace SageFs.Simulation

open System
open SageFs.Simulation.WorkflowSwitchSim

/// Seeded, dependency-free generators for workflow-switch scenarios (mirrors
/// `FileReloadRoutingGenerators` / `MgrGenerators`). Chaos is data: `run
/// (fromSeed n)` replays identically forever.
module WorkflowSwitchGenerators =

  /// A CLEAN scenario: every accepted switch resolves via its own matching
  /// Ready before the next op is emitted (no overlapping switches, no spawn
  /// failures) — see `WorkflowSwitchInvariants`'s scope note for why the
  /// general property sweep is restricted this way. Same-workflow requests
  /// ("no-ops") are mixed in freely since they need no resolving Ready. Same
  /// seed => identical scenario, forever. Exercises all 3x3 ordered pairs of
  /// `SessionWorkflow` (including same-workflow no-ops), not just
  /// Interactive<->HotReload.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let mutable nextPid = 0
    let fresh () =
      nextPid <- nextPid + 1
      nextPid
    let startWorkflow = rnd.Next(0, workflows.Length)
    let p0 = fresh ()
    let ops = ResizeArray<SwitchOp>()
    ops.Add(SwitchOp.Create(p0, startWorkflow))
    let mutable curWorkflow = startWorkflow
    let n = rnd.Next(2, 9)
    for _ in 1 .. n do
      let target = rnd.Next(0, workflows.Length)
      let newPid = fresh ()
      ops.Add(SwitchOp.RequestSwitch(target, newPid))
      if target <> curWorkflow then
        ops.Add(SwitchOp.Ready newPid)
        curWorkflow <- target
      // else: same-workflow request — AlreadyActive, no worker was ever
      // spawned for it, so no Ready follows.
    { Seed = seed; Ops = List.ofSeq ops }

  // ── Named minimal scenarios (the exact replays the tests assert on) ─────

  /// A switch request whose target equals the current workflow is a pure
  /// no-op: zero side effects, no worker touched. Mirrors
  /// `SessionCommand.SwitchWorkflow`'s `AlreadyActive` branch
  /// (`SessionManager.fs:1621-1623`).
  let sameWorkflowRequestIsANoOp : Scenario =
    { Seed = 1
      Ops = [ SwitchOp.Create(1, 0); SwitchOp.RequestSwitch(0, 999) ] }

  /// Interactive -> LiveTesting -> HotReload -> Interactive, each switch
  /// resolved cleanly — exercises the full 3-case transition space, not just
  /// a single Interactive<->HotReload round trip.
  let allThreeWorkflowsInSequence : Scenario =
    { Seed = 2
      Ops =
        [ SwitchOp.Create(1, 0) // Interactive, pid 1
          SwitchOp.RequestSwitch(1, 2)
          SwitchOp.Ready 2 // -> LiveTesting, pid 2
          SwitchOp.RequestSwitch(2, 3)
          SwitchOp.Ready 3 // -> HotReload, pid 3
          SwitchOp.RequestSwitch(0, 4)
          SwitchOp.Ready 4 ] } // -> Interactive, pid 4

  /// The replacement worker fails to come up: `WorkerEventGuard`'s
  /// `RevertSwap` restores the session to the still-serving OLD worker on
  /// its OLD workflow — a switch's failure must not leave the session
  /// stranded believing it is in the target workflow with no worker to back
  /// it.
  let spawnFailureRevertsToPreSwitchWorkflow : Scenario =
    { Seed = 3
      Ops = [ SwitchOp.Create(1, 0); SwitchOp.RequestSwitch(2, 2); SwitchOp.SpawnFailed 2 ] }

  /// The OLD (still-registered) worker's own late Ready arrives DURING the
  /// swap, before the replacement's. `WorkerEventGuard.classifyReady`'s
  /// swap-pending branch calls this stale (it equals the parked pid) — the
  /// session must stay `Swapping`, not commit the old pid a second time.
  let staleReadyDuringSwapIsIgnored : Scenario =
    { Seed = 4
      Ops = [ SwitchOp.Create(1, 0); SwitchOp.RequestSwitch(1, 2); SwitchOp.Ready 1 ] }

  /// The retiring old worker's own late SpawnFailed arrives DURING the swap
  /// (e.g. its `awaitWorkerPort` task posting on EOF after being killed) —
  /// `classifySpawnFailed`'s swap-pending branch calls this stale (the event
  /// pid equals the parked OLD pid, not the awaited replacement's), so it
  /// must not fault a healthy in-flight swap.
  let staleSpawnFailedFromRetiringWorkerDuringSwapIsInert : Scenario =
    { Seed = 5
      Ops = [ SwitchOp.Create(1, 0); SwitchOp.RequestSwitch(1, 2); SwitchOp.SpawnFailed 1 ] }

  /// KNOWN TRANSITION DEFECT (see the "known transition defect" section of
  /// `WorkflowSwitchSimTests.fs` for the assertion and full writeup):
  /// `SessionCommand.SwitchWorkflow` (`SessionManager.fs:1620-1642`) has no
  /// guard against a SECOND switch request landing while the first is still
  /// warming (`Some session -> spawnFirst ...` is reached unconditionally
  /// once the target differs and no REBUILD is in flight — the only guard
  /// present, `ManagerState.tryGetRebuildChannel`, has nothing to do with an
  /// in-flight spawn-first swap). `spawnFirst`'s `ManagerState.setPendingSwap
  /// id session state` (`SessionManager.fs:750`) is `Map.add` — an
  /// unconditional overwrite — so the SECOND call's parked entry replaces the
  /// first's, carrying the INTERMEDIATE (first-target) workflow as
  /// `OldWorkflowIdx` instead of the TRUE pre-switch one. If that second
  /// attempt then fails to spawn, `RevertSwap` restores the intermediate
  /// workflow, not the one the session was actually in before any switch was
  /// requested.
  let overlappingSwitchThenRevertLosesOriginalWorkflow : Scenario =
    { Seed = 6
      Ops =
        [ SwitchOp.Create(1, 0) // Interactive, pid 1
          SwitchOp.RequestSwitch(1, 2) // -> LiveTesting requested, pid 2 warming (still Swapping 1)
          SwitchOp.RequestSwitch(2, 3) // OVERLAPPING second request -> HotReload, pid 3 warming, while pid 2 is still in flight
          SwitchOp.SpawnFailed 3 ] } // pid 3 fails to come up -- reverts to WHICH workflow?
