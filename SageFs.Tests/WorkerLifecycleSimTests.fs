module SageFs.Tests.WorkerLifecycleSimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.WorkerLifecycleSim
open SageFs.Simulation.WorkerLifecycleInvariants

/// Phase 2 DST: the SessionManager worker-lifecycle pid-race, reproduced
/// deterministically. See SageFs.Simulation/WorkerLifecycleSim.fs.
///
/// The payoff is twofold:
///   * The invariant `no-stale-pid-applied` HOLDS over generated scenarios when
///     folded through the REAL `WorkerEventGuard` extracted from the mailbox —
///     the guard is provably free of the pid-blind race.
///   * The SAME invariant is VIOLATED, deterministically, when folded through a
///     `pidBlind` reducer modelling the pre-fix behavior (WorkerReady /
///     WorkerSpawnFailed ignoring the pid). That is the historical race caught,
///     with the exact minimal replay scenario, and the proof the invariant has
///     teeth rather than passing vacuously.
///
/// NOTE (2026-09-17): the roast (§4) cited this race as a LIVE bug in the tree,
/// but the fix already shipped — WorkerReady/WorkerSpawnFailed/WorkerExited all
/// carry the pid guard (now centralised in WorkerEventGuard, and covered by the
/// mailbox-level tests T5–T9 in SessionManagerSpawnFirstRestartTests). This
/// harness turns that fix into a replayable, property-based regression test: the
/// pidBlind reducer is the pre-fix code, kept only so the invariant can prove it
/// catches the regression should it ever return.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// Assert every invariant holds for a trace, else fail with a replayable dump.
let private assertHolds (t: MgrTrace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Commands=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Commands vs

[<Tests>]
let tests =
  testList "DST worker-lifecycle pid-race" [

    testList "the real guard holds the invariant" [

      testPropertyWithConfig simConfig "seeded scenarios — no stale pid is ever applied (real WorkerEventGuard)" <|
        fun (seed: int) ->
          // fromSeed is a pure function of the seed: this case replays exactly.
          assertHolds (run (MgrGenerators.fromSeed seed))

      testCase "clean lifecycle holds" <| fun _ ->
        assertHolds (run MgrGenerators.cleanLifecycle)

      testCase "stale ready after commit is inert under the real guard" <| fun _ ->
        let t = run MgrGenerators.staleReadyAfterCommit
        // The straggler Ready 1000 must NOT re-register the dead old worker.
        (List.last t.Steps).StatusAfter
        |> Expect.equal "session stays on the committed new worker" (SimStatus.Ready 1001)
        assertHolds t

      testCase "stale spawn-failure after commit is inert under the real guard" <| fun _ ->
        let t = run MgrGenerators.staleSpawnFailedAfterCommit
        (List.last t.Steps).StatusAfter
        |> Expect.equal "a retired worker's spawn-failure must not fault the healthy session" (SimStatus.Ready 1001)
        assertHolds t

      testCase "stale ready DURING a swap does not commit the old pid (the T8 property)" <| fun _ ->
        let t = run MgrGenerators.staleReadyDuringSwap
        (List.last t.Steps).StatusAfter
        |> Expect.equal "the old worker's mid-swap ready leaves the session Swapping" (SimStatus.Swapping 1000)
        assertHolds t

      // Surfaced by this harness: the old WorkerSpawnFailed fallback faulted a
      // healthy swap when a straggler carried the parked OLD pid. The guard now
      // ignores it, uniform with Ready/Exited.
      testCase "stale spawn-failure DURING a swap (parked old pid) is inert, not a fault" <| fun _ ->
        let t = run MgrGenerators.staleSpawnFailedDuringSwap
        (List.last t.Steps).StatusAfter
        |> Expect.equal "the retiring worker's mid-swap spawn-failure must not fault the swap" (SimStatus.Swapping 1000)
        assertHolds t
    ]

    testList "the pid-blind (pre-fix) reducer reproduces the race" [

      testCase "REPRODUCED — a straggler Ready re-points the registry at the dead worker" <| fun _ ->
        let t = runPidBlind MgrGenerators.staleReadyAfterCommit
        // The pre-fix reducer commits the retired pid 1000 over the live pid 1001.
        (List.last t.Steps).StatusAfter
        |> Expect.equal "pid-blind wrongly commits the dead old worker" (SimStatus.Ready 1000)
        // The invariant catches it, with the exact scenario for replay.
        noStalePidApplied.Check t
        |> function
           | Outcome.Violated _ -> ()
           | Outcome.Holds -> failtest "expected the pid-blind reducer to VIOLATE no-stale-pid-applied"

      testCase "REPRODUCED — a straggler SpawnFailed tombstones the healthy session" <| fun _ ->
        let t = runPidBlind MgrGenerators.staleSpawnFailedAfterCommit
        match (List.last t.Steps).StatusAfter with
        | SimStatus.Faulted _ -> ()
        | other -> failtestf "expected the pid-blind reducer to fault the session, got %A" other
        noStalePidApplied.Check t
        |> function
           | Outcome.Violated _ -> ()
           | Outcome.Holds -> failtest "expected the pid-blind reducer to VIOLATE no-stale-pid-applied"

      testCase "REPRODUCED — a stale mid-swap Ready commits the old pid (T8)" <| fun _ ->
        let t = runPidBlind MgrGenerators.staleReadyDuringSwap
        (List.last t.Steps).StatusAfter
        |> Expect.equal "pid-blind commits the old worker mid-swap, clearing the pending swap" (SimStatus.Ready 1000)
        noStalePidApplied.Check t
        |> function
           | Outcome.Violated _ -> ()
           | Outcome.Holds -> failtest "expected the pid-blind reducer to VIOLATE no-stale-pid-applied"

      testPropertyWithConfig simConfig "the invariant has teeth: some seeded scenario violates it under pid-blind" <|
        fun () ->
          // Across the seed sweep, the generator's straggler traffic makes the
          // pid-blind reducer violate the invariant; this asserts the harness is
          // not vacuously green. (A single confirmed reproduction is enough.)
          let violated =
            [ 1 .. 60 ]
            |> List.exists (fun seed ->
              match noStalePidApplied.Check (runPidBlind (MgrGenerators.fromSeed seed)) with
              | Outcome.Violated _ -> true
              | Outcome.Holds -> false)
          violated
          |> Expect.isTrue "at least one seeded scenario must expose the pid-blind race"
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical trace (real reducer)" <|
        fun (seed: int) ->
          let a = run (MgrGenerators.fromSeed seed)
          let b = run (MgrGenerators.fromSeed seed)
          a.Steps |> Expect.equal "replaying the same seed yields the identical trace" b.Steps
    ]

    testList "WorkerEventGuard unit decisions (the extracted seam)" [

      testCase "a real event pid differing from the current worker is stale for all three events" <| fun _ ->
        WorkerEventGuard.classifyReady (Some 1001) None 1000
        |> Expect.equal "stale ready" WorkerEventGuard.ReadyDecision.IgnoreStale
        WorkerEventGuard.classifySpawnFailed (Some 1001) None 1000
        |> Expect.equal "stale spawn-failure" WorkerEventGuard.SpawnFailedDecision.IgnoreStale
        WorkerEventGuard.classifyExited (Some 1001) None 1000
        |> Expect.equal "stale exit" WorkerEventGuard.ExitDecision.IgnoreStale

      testCase "the current worker's own events are applied" <| fun _ ->
        WorkerEventGuard.classifyReady (Some 1000) None 1000
        |> Expect.equal "commit ready" WorkerEventGuard.ReadyDecision.Commit
        WorkerEventGuard.classifyExited (Some 1000) None 1000
        |> Expect.equal "apply exit" WorkerEventGuard.ExitDecision.Apply

      testCase "mid-swap, the old worker is inert and the replacement drives the swap" <| fun _ ->
        // Registered + parked both carry the old pid (1000); the new worker is 1001.
        WorkerEventGuard.classifyReady (Some 1000) (Some 1000) 1000
        |> Expect.equal "old worker's ready is stale mid-swap" WorkerEventGuard.ReadyDecision.IgnoreStale
        WorkerEventGuard.classifyReady (Some 1000) (Some 1000) 1001
        |> Expect.equal "the new worker's ready commits the swap" WorkerEventGuard.ReadyDecision.Commit
        WorkerEventGuard.classifySpawnFailed (Some 1000) (Some 1000) 1001
        |> Expect.equal "the new worker's spawn-failure reverts the swap" WorkerEventGuard.SpawnFailedDecision.RevertSwap
        WorkerEventGuard.classifySpawnFailed (Some 1000) (Some 1000) 1000
        |> Expect.equal "the retiring old worker's mid-swap spawn-failure is inert, not a fault" WorkerEventGuard.SpawnFailedDecision.IgnoreStale
        WorkerEventGuard.classifyExited (Some 1000) (Some 1000) 1000
        |> Expect.equal "the retiring old worker's exit is ignored" WorkerEventGuard.ExitDecision.IgnoreRetired

      testCase "synthetic / unspawned pids (<= 0) are never judged stale" <| fun _ ->
        WorkerEventGuard.isRealPid 0 |> Expect.isFalse "pid 0 is not real"
        WorkerEventGuard.isRealPid -1 |> Expect.isFalse "synthetic NotifyWorkerDied pid is not real"
        WorkerEventGuard.classifyExited (Some 1000) None -1
        |> Expect.equal "a synthetic exit is applied (forces a real lookup), not dropped as stale" WorkerEventGuard.ExitDecision.Apply
    ]
  ]
