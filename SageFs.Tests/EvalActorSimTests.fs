module SageFs.Tests.EvalActorSimTests

open Expecto
open Expecto.Flip
open SageFs.EvalActorDecision
open SageFs.Simulation
open SageFs.Simulation.EvalActorSim
open SageFs.Simulation.EvalActorInvariants

/// DST replacement for four slow FSI-warmup Integration suites — the eval
/// actor's mailbox routing (query-liveness, cancel-idempotence,
/// loop-survival, generation-supersession/no-resurrection), folded through
/// the REAL `SageFs.EvalActorDecision.decide` in milliseconds
/// instead of real FSI warm-up (each of the four suites previously spun up
/// a real FSI session — `SageFs.Tests/ActorSplitTests.fs`,
/// `EvalCancellationTests.fs`, `EvalActorResilienceTests.fs`,
/// `EvalActorStragglerTests.fs`, all trimmed to point here).
///
/// See `SageFs.Core/EvalActorDecision.fs` and
/// `SageFs.Simulation/EvalActorSim.fs` for the reducer and its two
/// routing twins (generation-blind, single-actor) plus the unwrapped
/// loop-survival twin.
///
/// `EvalActorStragglerTests.fs`'s `supersededAtWorkerBoundaryTests` stays as
/// the one real process-boundary smoke worth keeping — it proves the
/// structured `SageFsError.EvalSupersededByReset` case crosses the worker
/// HTTP boundary intact, which this pure DST cannot exercise.

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
  testList "DST eval actor" [

    testList "the real decide holds the invariants" [

      testPropertyWithConfig simConfig "seeded scenarios — the real reducer never violates query-liveness, cancel-idempotence, no-resurrection, or loop-survival" <|
        fun (seed: int) ->
          // fromSeed is a pure function of the seed: this case replays exactly.
          assertHolds (run (EvalActorGenerators.fromSeed seed))

      testCase "resetDuringEval: a straggler Finished for the superseded generation is dropped" <| fun _ ->
        let t = run EvalActorGenerators.resetDuringEval
        t.Final.Activity |> Expect.equal "activity is Idle — the straggler never resurrected the eval" Activity.Idle
        assertHolds t

      testCase "queryDuringEval: the Query mid-eval is served, not blocked" <| fun _ ->
        let t = run EvalActorGenerators.queryDuringEval
        t.Final.Log
        |> List.exists (fun e -> e.Op = EvalOp.Query && e.Decision = EvalDecision.ServeQuery)
        |> Expect.isTrue "the mid-eval Query was decided ServeQuery"
        assertHolds t

      testCase "doubleCancel: both cancels are served" <| fun _ ->
        let t = run EvalActorGenerators.doubleCancel
        let cancels = t.Final.Log |> List.filter (fun e -> e.Op = EvalOp.Cancel)
        cancels |> List.length |> Expect.equal "both Cancel ops were logged" 2
        cancels |> List.forall (fun e -> e.Decision = EvalDecision.AckCancel) |> Expect.isTrue "both cancels served"
        assertHolds t

      testCase "stragglerAfterReset: two generations stale still drops" <| fun _ ->
        assertHolds (run EvalActorGenerators.stragglerAfterReset)

      testCase "poisonMidScenario: every op after the poison still runs (guarded)" <| fun _ ->
        let t = run EvalActorGenerators.poisonMidScenario
        t.Final.Log |> List.length |> Expect.equal "Submit, Query, Cancel, Reset all logged despite the poison" 4
        assertHolds t

      testCase "freshFinished: a current-generation Finished is applied, not dropped" <| fun _ ->
        let t = run EvalActorGenerators.freshFinished
        t.Final.Activity |> Expect.equal "activity returned to Idle via ApplyFinished" Activity.Idle
        assertHolds t

      testCase "cancelBeforeSubmit: a Cancel with nothing running never gates the Submit that follows" <| fun _ ->
        let t = run EvalActorGenerators.cancelBeforeSubmit
        let submitDecision = t.Final.Log |> List.rev |> List.last |> fun e -> e.Decision
        submitDecision |> Expect.equal "the Submit after an idle Cancel is RunEval" EvalDecision.RunEval
        assertHolds t

      testCase "cancelThenResubmit: the second Submit is rejected while the first eval's cancel is unconfirmed" <| fun _ ->
        let t = run EvalActorGenerators.cancelThenResubmit
        let entries = t.Final.Log |> List.rev
        entries |> List.length |> Expect.equal "all three ops were decided" 3
        let secondSubmitDecision = entries |> List.last |> fun e -> e.Decision
        match secondSubmitDecision with
        | EvalDecision.RejectEval _ -> ()
        | other -> failtestf "expected the second Submit to be RejectEval, got %A" other
        t.Final.Activity |> Expect.equal "activity stays Cancelling — nothing ever confirmed the orphan stopped" (Activity.Cancelling SessionGeneration.initial)
        assertHolds t

      testCase "cancelRacingCompletion: a Finished that wins the race clears Cancelling, and a later Submit is allowed again" <| fun _ ->
        let t = run EvalActorGenerators.cancelRacingCompletion
        let entries = t.Final.Log |> List.rev
        let finishedDecision = entries |> List.item 2 |> fun e -> e.Decision
        finishedDecision |> Expect.equal "the racing Finished for the current generation is applied" EvalDecision.ApplyFinished
        let secondSubmitDecision = entries |> List.last |> fun e -> e.Decision
        secondSubmitDecision |> Expect.equal "a Submit after the confirmed completion is RunEval again" EvalDecision.RunEval
        t.Final.Activity |> Expect.equal "activity is Evaluating — the second Submit ran" (Activity.Evaluating SessionGeneration.initial)
        assertHolds t
    ]

    testList "the twins reproduce the historical bugs" [

      testCase "REPRODUCED — generation-blind twin resurrects a superseded straggler" <| fun _ ->
        let t = runGenerationBlind EvalActorGenerators.resetDuringEval
        violations t
        |> List.map fst
        |> Expect.contains "the no-resurrection invariant must fire" "no-resurrection"

      testCase "REPRODUCED — single-actor twin blocks a Query mid-eval" <| fun _ ->
        let t = runSingleActor EvalActorGenerators.queryDuringEval
        violations t
        |> List.map fst
        |> Expect.contains "the query-liveness invariant must fire" "query-liveness"

      testCase "REPRODUCED — unwrapped twin dies on the poison and drops every later op" <| fun _ ->
        let t = runUnwrapped EvalActorGenerators.poisonMidScenario
        t.Died |> Expect.isTrue "the unguarded fold died at the poison"
        violations t
        |> List.map fst
        |> Expect.contains "the loop-survival invariant must fire" "loop-survival"

      testCase "REPRODUCED — no-cancelling-gate twin runs a second eval onto an orphaned session (the live bug this fix closes)" <| fun _ ->
        let t = runNoCancellingGate EvalActorGenerators.cancelThenResubmit
        let secondSubmitDecision = t.Final.Log |> List.rev |> List.last |> fun e -> e.Decision
        secondSubmitDecision |> Expect.equal "the twin runs the second eval instead of rejecting it" EvalDecision.RunEval
        violations t
        |> List.map fst
        |> Expect.contains "the cancel-blocks-resubmit invariant must fire" "cancel-blocks-resubmit"

      testPropertyWithConfig simConfig "the invariants have teeth: some seeded scenario violates them under each twin" <|
        fun () ->
          let seeds = [ 1 .. 80 ]
          let anyViolates run' =
            seeds |> List.exists (fun seed -> violations (run' (EvalActorGenerators.fromSeed seed)) |> List.isEmpty |> not)
          (anyViolates runGenerationBlind || anyViolates runSingleActor || anyViolates runUnwrapped || anyViolates runNoCancellingGate)
          |> Expect.isTrue "at least one seeded scenario must expose a bug under at least one twin"
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical final state (real reducer)" <|
        fun (seed: int) ->
          let a = run (EvalActorGenerators.fromSeed seed)
          let b = run (EvalActorGenerators.fromSeed seed)
          a.Final |> Expect.equal "replaying the same seed yields the identical final fold state" b.Final
    ]
  ]
