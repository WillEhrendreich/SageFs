module SageFs.Tests.StartEscalationSimTests

/// DST for starting a worker on a machine of unknown speed (`SageFs.Core/StartEscalation.fs` via
/// `SageFs.Simulation.StartEscalationSim`). See `StartEscalationSim.fs`'s header for the historical
/// bug: five identical 30 second attempts that could never succeed on a machine that needed 40, ended
/// by "Worker process exited with code 137 (abandoned after max retries)".
///
/// The real escalation is the subject. The two twins reproduce the old loop and a loop with no end;
/// the invariants are shown to FAIL on them, which is what proves they have teeth.
open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Simulation
open SageFs.Simulation.StartEscalationSim
open SageFs.Simulation.StartEscalationInvariants

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s). Replay this scenario:\n  Seed=%d Tier=%A\n  History=%A\n  Machine=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Tier t.Scenario.History t.Scenario.Machine vs

let private violatedIds (t: Trace) : string list = violations t |> List.map fst

[<Tests>]
let tests =
  testList "DST starting a worker on a machine of unknown speed" [

    testList "the real escalation holds every invariant" [

      testPropertyWithConfig simConfig
        "seeded scenarios: any tier, any history, any machine (fast, slow, loaded once, hung)"
        <| fun (seed: int) -> assertHolds (run (StartEscalationGenerators.fromSeed seed))

      testCase "the Phenom II's case: a start needing 40 s on a tier that allows 30 s is rescued by the second attempt, not killed five times" <| fun _ ->
        let t = run StartEscalationGenerators.slowButHealthyOnTheWrongTier
        match t.Outcome with
        | StartOutcome.Succeeded (attempt, _) -> attempt |> Expect.equal "the second attempt" 2
        | other -> failtestf "expected success on the second attempt, got %A" other
        assertHolds t

      testCase "a hung machine is given up on within the attempt limit, with the whole story" <| fun _ ->
        let t = run StartEscalationGenerators.hangs
        (match t.Outcome with
         | StartOutcome.GaveUp (FailureReport.Named failure) ->
           failure.Attempts |> Expect.isLessThanOrEqual "within the limit" StartEscalation.MaxAttempts
           failure.Tier |> Expect.equal "the tier is named" MachineTier.Standard
         | other -> failtestf "expected a named failure, got %A" other)
        assertHolds t

      testCase "a busy first attempt followed by a quiet machine: the second attempt starts at once" <| fun _ ->
        let t = run StartEscalationGenerators.loadedOnce
        (match t.Outcome with
         | StartOutcome.Succeeded (attempt, _) -> attempt |> Expect.equal "the second attempt" 2
         | other -> failtestf "expected success, got %A" other)
        assertHolds t

      testCase "a machine whose history says 90 s: the first attempt already waits long enough" <| fun _ ->
        let t = run StartEscalationGenerators.knownSlowMachine
        (match t.Outcome with
         | StartOutcome.Succeeded (attempt, _) -> attempt |> Expect.equal "first attempt" 1
         | other -> failtestf "expected success on the first attempt, got %A" other)
        assertHolds t
    ]

    testList "twins: the invariants have teeth" [

      testCase "the old loop (five identical attempts) violates never-retry-with-a-smaller-budget's cousin: it retries with the SAME budget and fails a machine that needed 40 s" <| fun _ ->
        let t = runSameBudget StartEscalationGenerators.slowButHealthyOnTheWrongTier
        (match t.Outcome with
         | StartOutcome.GaveUp (FailureReport.Vague _) -> ()
         | other -> failtestf "expected the old loop to give up vaguely, got %A" other)
        let ids = violatedIds t
        ids |> Expect.contains "an identical retry is not strictly more patient" "never-retry-with-a-smaller-budget"
        ids |> Expect.contains "a machine the schedule could have started is given up on" "eventually-succeeds-if-the-machine-can"
        ids |> Expect.contains "the old message names nothing" "failure-names-the-wait"

      testCase "the loop with no end violates no-unbounded-loop on a machine that hangs" <| fun _ ->
        let t = runUnbounded StartEscalationGenerators.hangs
        violatedIds t |> Expect.contains "it makes far more attempts than the limit" "no-unbounded-loop"

      testCase "the old loop and the real escalation disagree on the same scenario, and only the real one is clean" <| fun _ ->
        let scenario = StartEscalationGenerators.slowButHealthyOnTheWrongTier
        violations (run scenario) |> Expect.isEmpty "the real escalation is clean"
        violations (runSameBudget scenario) |> List.isEmpty |> Expect.isFalse "the old loop is not"

      testPropertyWithConfig simConfig
        "teeth: for ANY healthy machine that needs more than the first allowance and no more than the schedule reaches, the old loop fails and the real one succeeds"
        <| fun (PositiveInt extraSeconds) ->
          let needs = StartEscalationGenerators.slowButHealthyOnTheWrongTier.StaticInactivity + TimeSpan.FromSeconds(float (1 + extraSeconds % 50))
          let scenario =
            { StartEscalationGenerators.slowButHealthyOnTheWrongTier with
                Seed = extraSeconds
                Machine = Machine.NeedsPerAttempt [ needs ] }
          let real = run scenario
          let old = runSameBudget scenario
          match real.Outcome, old.Outcome with
          | StartOutcome.Succeeded _, StartOutcome.GaveUp _ -> ()
          | _ ->
            failtestf "expected real=Succeeded and old loop=GaveUp for a start needing %.0fs, got real=%A old=%A" needs.TotalSeconds real.Outcome old.Outcome
    ]

    testList "determinism / replay" [
      testProperty "same seed gives an identical trace" <|
        fun (seed: int) ->
          let a = run (StartEscalationGenerators.fromSeed seed)
          let b = run (StartEscalationGenerators.fromSeed seed)
          a.Budgets = b.Budgets && a.Waited = b.Waited
    ]
  ]
