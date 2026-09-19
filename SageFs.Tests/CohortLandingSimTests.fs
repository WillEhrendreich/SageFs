module SageFs.Tests.CohortLandingSimTests

open Expecto
open Expecto.Flip
open SageFs.Cohort
open SageFs.Simulation
open SageFs.Simulation.CohortLandingSim
open SageFs.Simulation.CohortLandingInvariants

/// Phase 3 DST: the cohort's serial-landing queue-jam, reproduced deterministically.
/// See SageFs.Simulation/CohortLandingSim.fs.
///
/// The payoff is twofold:
///   * The invariants (a passing landing always lands; the queue never retains a
///     terminal landing; every landing terminates) HOLD over 500 SEEDED
///     scenarios (`simConfig.maxTest`, below) when folded through the REAL
///     `Cohort.decide` — the serial pipeline is free of the dead-lock under
///     those 500 sampled scenarios. This is SAMPLING, not a proof (armfix,
///     cmd-handoff.md item B — roast-2day §2 named this exact drift): the
///     generator's own bounded alphabet (`CohortLandingGenerators.fs`) has no
///     Veto/HeadMove/StaleClaimFence routes, so it cannot sample those arms
///     either. `CohortSpec.fs`'s exhaustive BFS is where those arms are
///     actually proven, over its own stated bounded alphabet — see that
///     module's COVERAGE comment for exactly what it covers.
///   * The SAME invariants are VIOLATED, deterministically, when folded through a
///     `jammed` reducer modelling the pre-fix behavior (a failing `TestsCompleted`
///     blocks the landing but leaves it at the queue head). That is the historical
///     queue-jam caught, with the exact minimal replay, and the proof the
///     invariants have teeth rather than passing vacuously.
///
/// NOTE (2026-09-18): the fix already shipped (396ee1c3 — a failing landing now
/// pops the queue and advances, mirroring Inconclusive/Withdraw). This harness
/// turns that fix into a replayable, property-based regression test: the `jammed`
/// reducer is the pre-fix code, kept only so the invariants can prove they catch
/// the regression should it ever return.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// Assert every invariant holds for a trace, else fail with a replayable dump.
let private assertHolds (t: LandingTrace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Landings=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Landings vs

/// The final landing-state of a given member's (single) landing in a trace.
let private stateOf (who: Member) (t: LandingTrace) : LandingState<Member> option =
  t.Final.Landings
  |> Map.toList
  |> List.tryPick (fun (_, req) -> if req.Requester = who then Some req.State else None)

[<Tests>]
let tests =
  testList "DST cohort landing queue-jam" [

    testList "the real decide holds the invariants" [

      testPropertyWithConfig simConfig "seeded scenarios — a failing landing never freezes the queue (real Cohort.decide)" <|
        fun (seed: int) ->
          // fromSeed is a pure function of the seed: this case replays exactly.
          assertHolds (run (CohortLandingGenerators.fromSeed seed))

      testCase "failing-then-passing: the failure pops and the healthy landing lands" <| fun _ ->
        let t = run CohortLandingGenerators.failingThenPassing
        stateOf "bob" t
        |> function
           | Some(LandingState.Landed _) -> ()
           | other -> failtestf "bob's healthy landing should have landed, got %A" other
        t.Final.Queue |> Expect.isEmpty "the queue drains empty once both landings are terminal"
        assertHolds t

      testCase "conflict-then-passing: a rebase conflict also pops, never jams" <| fun _ ->
        assertHolds (run CohortLandingGenerators.conflictThenPassing)

      testCase "inconclusive-then-passing: a transient inconclusive pops and the next lands" <| fun _ ->
        assertHolds (run CohortLandingGenerators.inconclusiveThenPassing)

      testCase "every failure stacked ahead of a healthy landing — all pop, the last lands" <| fun _ ->
        let t = run CohortLandingGenerators.everyFailureThenPassing
        stateOf "dave" t
        |> function
           | Some(LandingState.Landed _) -> ()
           | other -> failtestf "dave's landing behind three distinct blockers should have landed, got %A" other
        assertHolds t

      testCase "all-pass: every healthy landing lands in turn" <| fun _ ->
        let t = run CohortLandingGenerators.allPass
        let landed =
          t.Final.Landings
          |> Map.toList
          |> List.forall (fun (_, req) -> match req.State with LandingState.Landed _ -> true | _ -> false)
        landed |> Expect.isTrue "every landing in an all-pass run must reach Landed"
        assertHolds t
    ]

    testList "the jammed (pre-fix) reducer reproduces the queue-jam" [

      testCase "REPRODUCED — a failing landing at the head freezes the healthy landing behind it" <| fun _ ->
        let t = runJammed CohortLandingGenerators.failingThenPassing
        // The pre-fix reducer leaves the failing landing at the head; bob never
        // even starts rebasing.
        stateOf "bob" t
        |> Expect.equal "bob's landing is frozen Queued behind the jam" (Some LandingState.Queued)
        // The invariants catch it, with the exact scenario for replay.
        violations t
        |> List.map fst
        |> Expect.contains "the no-deadlock invariant must fire" "passing-landing-always-lands"

      testCase "REPRODUCED — the blocked failing landing is never popped from the queue" <| fun _ ->
        let t = runJammed CohortLandingGenerators.failingThenPassing
        queueHoldsOnlyInFlight.Check t
        |> function
           | Outcome.Violated _ -> ()
           | Outcome.Holds -> failtest "expected the jammed reducer to leave a terminal landing stuck in the queue"

      testPropertyWithConfig simConfig "the invariants have teeth: some seeded scenario violates them under the jam" <|
        fun () ->
          // Across the seed sweep, a non-passing landing frequently sits ahead of
          // a healthy one, so the jammed reducer violates the invariants; this
          // asserts the harness is not vacuously green. (One reproduction suffices.)
          let violated =
            [ 1 .. 80 ]
            |> List.exists (fun seed ->
              match violations (runJammed (CohortLandingGenerators.fromSeed seed)) with
              | [] -> false
              | _ -> true)
          violated
          |> Expect.isTrue "at least one seeded scenario must expose the queue-jam under the jammed reducer"
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical final state (real reducer)" <|
        fun (seed: int) ->
          let a = run (CohortLandingGenerators.fromSeed seed)
          let b = run (CohortLandingGenerators.fromSeed seed)
          a.Final |> Expect.equal "replaying the same seed yields the identical final cohort state" b.Final
    ]
  ]
