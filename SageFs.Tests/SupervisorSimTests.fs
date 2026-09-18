module SageFs.Tests.SupervisorSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.SupervisorSim
open SageFs.Simulation.SupervisorInvariants

/// Phase 3 (B6) DST: SessionManager mailbox supervision ("supervise the
/// supervisor" — `SessionManager.fs:1740-1832`), reproduced deterministically.
/// See SageFs.Simulation/SupervisorSim.fs for the full model writeup.
///
/// The payoff is twofold, exactly the Phase 2 twin-reducer pattern:
///   * `sessions-preserved-across-fault` HOLDS over generated scenarios when
///     folded through the real (supervised, restart-from-last-good) model —
///     a mailbox fault never orphans an already-committed session.
///   * The SAME invariant is VIOLATED, deterministically, when folded through
///     the `stepNoSupervisor` twin (a mailbox with no supervision at all,
///     which drops every session on the first fault). That is the exact
///     failure "supervise the supervisor" exists to prevent, caught with a
///     replayable minimal reproduction, proving the invariant has teeth
///     rather than passing vacuously.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// Assert every invariant holds for a trace, else fail with a replayable dump.
let private assertHolds (t: SupTrace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Commands=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Commands vs

/// Assert a single named invariant holds, else fail naming it and its message.
let private assertHoldsInvariant (inv: Invariant) (t: SupTrace) =
  match inv.Check t with
  | Outcome.Holds -> ()
  | Outcome.Violated msg -> failtestf "invariant %s violated: %s" inv.Id msg

[<Tests>]
let tests =
  testList "DST supervisor mailbox restart" [

    testList "the real supervised model holds the invariant" [

      testPropertyWithConfig simConfig "seeded scenarios — sessions survive every fault (real supervise/last-good)" <|
        fun (seed: int) ->
          // fromSeed is a pure function of the seed: this case replays exactly.
          assertHolds (run (SupervisorSim.fromSeed seed))

      testCase "clean lifecycle holds" <| fun _ ->
        assertHolds (run cleanLifecycle)

      testCase "two registrations survive a Poison, and a later register still lands" <| fun _ ->
        let t = run registerThenPoison
        (List.last t.Steps).StateAfter.Sessions
        |> Expect.equal "both checkpointed sessions plus the post-fault registration all survive" (Set.ofList [ 1; 2; 3 ])
        assertHolds t

      testCase "a fault right after the first registration still preserves it" <| fun _ ->
        let t = run singleRegisterThenPoison
        (List.last t.Steps).StateAfter.Sessions
        |> Expect.equal "the single checkpointed session survives the fault" (Set.ofList [ 1 ])
        assertHolds t

      testCase "Cancel is terminal: nothing after it mutates Sessions, even a Poison" <| fun _ ->
        let t = run registerCancelThenRegister
        (List.last t.Steps).StateAfter.Sessions
        |> Expect.equal "Sessions frozen at what existed when Cancel landed" (Set.ofList [ 1 ])
        assertHolds t
    ]

    testList "the no-supervisor twin reproduces the orphaning fault" [

      testCase "REPRODUCED — an unsupervised Poison wipes both checkpointed sessions" <| fun _ ->
        let t = runNoSupervisor registerThenPoison
        // The twin drops everything on the fault; the post-fault Register(3)
        // lands on an empty registry instead of alongside 1 and 2.
        (List.last t.Steps).StateAfter.Sessions
        |> Expect.equal "no-supervisor twin orphans the pre-fault sessions" (Set.ofList [ 3 ])
        // The invariant catches it, with the exact scenario for replay.
        sessionsPreservedAcrossFault.Check t
        |> function
           | Outcome.Violated _ -> ()
           | Outcome.Holds -> failtest "expected the no-supervisor twin to VIOLATE sessions-preserved-across-fault"

      testCase "REPRODUCED — even a single registration is lost to an unsupervised fault" <| fun _ ->
        let t = runNoSupervisor singleRegisterThenPoison
        (List.last t.Steps).StateAfter.Sessions
        |> Expect.equal "the twin orphans the only session it was holding" Set.empty
        sessionsPreservedAcrossFault.Check t
        |> function
           | Outcome.Violated _ -> ()
           | Outcome.Holds -> failtest "expected the no-supervisor twin to VIOLATE sessions-preserved-across-fault"

      testPropertyWithConfig simConfig "the invariant has teeth: some seeded scenario violates it under no-supervisor" <|
        fun () ->
          // Across the seed sweep, generated Register-then-Poison traffic
          // makes the no-supervisor twin violate the invariant; this asserts
          // the harness is not vacuously green. (A single confirmed
          // reproduction is enough, mirroring WorkerLifecycleSimTests.)
          let violated =
            [ 1 .. 60 ]
            |> List.exists (fun seed ->
              match sessionsPreservedAcrossFault.Check (runNoSupervisor (SupervisorSim.fromSeed seed)) with
              | Outcome.Violated _ -> true
              | Outcome.Holds -> false)
          violated
          |> Expect.isTrue "at least one seeded scenario must expose the no-supervisor orphaning fault"
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical trace (real reducer)" <|
        fun (seed: int) ->
          let a = run (SupervisorSim.fromSeed seed)
          let b = run (SupervisorSim.fromSeed seed)
          a.Steps |> Expect.equal "replaying the same seed yields the identical trace" b.Steps

      testProperty "same seed => identical trace (no-supervisor twin)" <|
        fun (seed: int) ->
          let a = runNoSupervisor (SupervisorSim.fromSeed seed)
          let b = runNoSupervisor (SupervisorSim.fromSeed seed)
          a.Steps |> Expect.equal "replaying the same seed yields the identical trace" b.Steps
    ]

    testList "invariant unit checks (the extracted oracle)" [

      testCase "cancel-is-terminal holds even under the no-supervisor twin" <| fun _ ->
        // Both reducers implement Cancel identically (absorbing) — this is
        // the control showing the invariant genuinely distinguishes the
        // fault-handling difference rather than being tripped by any
        // reducer difference at all.
        assertHoldsInvariant cancelIsTerminal (runNoSupervisor registerCancelThenRegister)

      testCase "fault-count-matches counts only Poisons processed before Cancel" <| fun _ ->
        let t = run registerCancelThenRegister
        // registerCancelThenRegister = [Register 1; Cancel; Register 2; Poison]
        // — the trailing Poison happens AFTER Cancel, so it must not count.
        t.FaultCount |> Expect.equal "no Poison was processed while still Running" 0
        assertHoldsInvariant faultCountMatches t

      testCase "fault-count-matches counts a Poison that lands before Cancel" <| fun _ ->
        let t = run registerThenPoison
        t.FaultCount |> Expect.equal "one Poison processed while Running" 1
        assertHoldsInvariant faultCountMatches t
    ]
  ]
