module SageFs.Tests.WorkflowSwitchSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.WorkflowSwitchSim
open SageFs.Simulation.WorkflowSwitchInvariants

/// DST for Gap F of `outcome-gate-sweep.md` — workflow switching
/// (`switch_workflow` / `SessionCommand.SwitchWorkflow`). See
/// `SageFs.Simulation/WorkflowSwitchSim.fs` for the full model writeup
/// (including the honesty note: the pid-guard is the REAL, extracted
/// `WorkerEventGuard`; the surrounding switch/spawn-first control flow is a
/// faithful miniature model, per `SupervisorSim`'s precedent).
///
/// The payoff is the same twin-reducer shape as `FileReloadRoutingSimTests`
/// / `SupervisorSimTests`:
///   * `no-dead-window` and `settled-workflow-matches-last-requested` HOLD
///     over seeded clean scenarios folded through the real `spawnFirst`
///     acceptance strategy.
///   * `no-dead-window` is VIOLATED, deterministically, by the
///     `stopBeforeSpawn` twin — proving the invariant has teeth rather than
///     passing vacuously.
///   * A separate "known transition defect" section pins a REAL bug this
///     sim surfaced against the actual reducer (not a twin): an overlapping
///     switch request loses track of the true pre-switch workflow on
///     revert. See `WorkflowSwitchGenerators.overlappingSwitchThenRevertLosesOriginalWorkflow`.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// Assert every invariant holds for a trace, else fail with a replayable dump.
let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

[<Tests>]
let tests =
  testList "DST workflow switching" [

    testList "the real spawn-first switch holds the invariants" [

      testPropertyWithConfig simConfig
        "seeded clean scenarios — real spawn-first never violates no-dead-window or settled-workflow-matches-last-requested"
        <| fun (seed: int) -> assertHolds (run (WorkflowSwitchGenerators.fromSeed seed))

      testCase "a same-workflow request is a pure no-op: zero side effects" <| fun _ ->
        let t = run WorkflowSwitchGenerators.sameWorkflowRequestIsANoOp
        // Steps.[0] is Create; Steps.[1] is the same-workflow RequestSwitch —
        // its `Before` is the state right after Create, and AlreadyActive must
        // leave it byte-for-byte unchanged.
        let afterCreate = t.Steps.[0].After
        t.Final |> Expect.equal "AlreadyActive touches nothing — status, workflow, and pending swap are all unchanged from right after Create" afterCreate
        assertHolds t

      testCase "Interactive -> LiveTesting -> HotReload -> Interactive, each switch settling before the next" <| fun _ ->
        let t = run WorkflowSwitchGenerators.allThreeWorkflowsInSequence
        t.Final.WorkflowIdx |> Expect.equal "settled back on Interactive (index 0)" 0
        t.Final.Status |> Expect.equal "the fourth worker (pid 4) is the one serving" (Status.Ready 4)
        assertHolds t

      testCase "a spawn failure reverts to the pre-switch workflow, not the failed target" <| fun _ ->
        let t = run WorkflowSwitchGenerators.spawnFailureRevertsToPreSwitchWorkflow
        t.Final.WorkflowIdx |> Expect.equal "reverted to Interactive (index 0), the workflow before the failed switch" 0
        t.Final.Status |> Expect.equal "the still-serving original worker (pid 1) is restored" (Status.Ready 1)
        // Not assertHolds: settledWorkflowMatchesLastRequested's ground truth
        // deliberately does not model a SpawnFailed revert (see
        // WorkflowSwitchInvariants' header) -- its "last requested" scan would
        // otherwise expect the FAILED target, not the correctly-reverted one.
        // noDeadWindow alone is still meaningful here.
        match noDeadWindow.Check t with
        | Outcome.Holds -> ()
        | Outcome.Violated msg -> failtestf "no-dead-window violated: %s" msg

      testCase "the old worker's own stale Ready mid-swap never commits early" <| fun _ ->
        let t = run WorkflowSwitchGenerators.staleReadyDuringSwapIsIgnored
        t.Final.Status |> Expect.equal "still Swapping on the old pid — the stale ready changed nothing" (Status.Swapping 1)
        t.Final.WorkflowIdx |> Expect.equal "the target workflow is still recorded, unaffected by the stale event" 1
        assertHolds t

      testCase "the retiring old worker's own stale SpawnFailed mid-swap never faults a healthy swap" <| fun _ ->
        let t = run WorkflowSwitchGenerators.staleSpawnFailedFromRetiringWorkerDuringSwapIsInert
        t.Final.Status |> Expect.equal "still Swapping — the stale spawn-failure from the OLD worker is inert" (Status.Swapping 1)
        assertHolds t
    ]

    testList "the stop-before-spawn twin reproduces a dead window" [

      testCase "REPRODUCED — stopping the old worker before the replacement exists opens a dead window" <| fun _ ->
        let t = runStopBeforeSpawn WorkflowSwitchGenerators.allThreeWorkflowsInSequence
        violations t
        |> List.map fst
        |> Expect.contains "no-dead-window must fire against the stop-before-spawn twin" "no-dead-window"

      testPropertyWithConfig simConfig "the invariant has teeth: some seeded scenario violates no-dead-window under the twin" <|
        fun () ->
          [ 1 .. 80 ]
          |> List.exists (fun seed ->
            violations (runStopBeforeSpawn (WorkflowSwitchGenerators.fromSeed seed))
            |> List.map fst
            |> List.contains "no-dead-window")
          |> Expect.isTrue "at least one seeded scenario must expose the dead window under the twin"
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical trace (real spawn-first)" <|
        fun (seed: int) ->
          let a = run (WorkflowSwitchGenerators.fromSeed seed)
          let b = run (WorkflowSwitchGenerators.fromSeed seed)
          a.Steps |> Expect.equal "replaying the same seed yields the identical trace" b.Steps

      testProperty "same seed => identical trace (stop-before-spawn twin)" <|
        fun (seed: int) ->
          let a = runStopBeforeSpawn (WorkflowSwitchGenerators.fromSeed seed)
          let b = runStopBeforeSpawn (WorkflowSwitchGenerators.fromSeed seed)
          a.Steps |> Expect.equal "replaying the same seed yields the identical trace" b.Steps
    ]

    testList "known transition defect — overlapping switch requests lose the true pre-switch workflow on revert" [

      testCase
        "PINNED — a second switch request landing while the first is still warming reverts to the INTERMEDIATE workflow, not the original one"
        <| fun _ ->
          // See WorkflowSwitchGenerators.overlappingSwitchThenRevertLosesOriginalWorkflow
          // for the full citation. Walking it by hand:
          //   Create(pid=1, Interactive)
          //   RequestSwitch(LiveTesting, pid=2)   -- spawnFirst parks {old=1, wf=Interactive(0)}, still warming
          //   RequestSwitch(HotReload,  pid=3)    -- OVERLAPS the first: SwitchWorkflow has no in-flight
          //                                          guard (SessionManager.fs:1620-1642 only rejects a
          //                                          concurrent REBUILD, not a concurrent switch), so
          //                                          spawnFirst runs again and ManagerState.setPendingSwap
          //                                          (SessionManager.fs:750, `Map.add`) OVERWRITES the
          //                                          parked entry with {old=1, wf=LiveTesting(1)} --
          //                                          losing the TRUE original workflow (Interactive/0).
          //   SpawnFailed(3)                      -- pid 3 never came up; RevertSwap restores pid 1...
          //                                          but on workflow index 1 (LiveTesting), not 0
          //                                          (Interactive) -- the session record now claims a
          //                                          workflow the still-running worker (pid 1) was never
          //                                          actually started in.
          let t = run WorkflowSwitchGenerators.overlappingSwitchThenRevertLosesOriginalWorkflow
          t.Final.Status |> Expect.equal "the original worker (pid 1) is what's actually still running" (Status.Ready 1)
          t.Final.WorkflowIdx
          |> Expect.equal
               "TODAY: reverts to the intermediate LiveTesting (index 1) -- the CORRECT value is Interactive (index 0), \
                the workflow pid 1 was actually spawned into. This is the real transition defect this sim surfaced; \
                see the report for the SessionManager.fs fix (an in-flight-switch guard, mirroring RestartSession's \
                rebuild-channel check, or making spawnFirst MERGE into the existing PendingSwap entry instead of \
                overwriting it)."
               1
          // settledWorkflowMatchesLastRequested's ground truth deliberately does not
          // model overlapping switches (see WorkflowSwitchInvariants' header), so
          // this scenario is asserted directly rather than through `assertHolds`.
    ]
  ]
