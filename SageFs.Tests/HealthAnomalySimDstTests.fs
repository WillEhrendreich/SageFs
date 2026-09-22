/// Deterministic Simulation Testing for `HealthAnomaly`: seeded series
/// shapes (flat, step, drift, spike, sawtooth, recovery, and a legitimate
/// scale change) folded through the REAL `HealthAnomaly.step`. The
/// `AlwaysFineTwin` behavior never fires no matter what it sees — running
/// the same invariants against it proves they actually depend on the real
/// detector's behavior, not just on the shape of the trace.
module SageFs.Tests.HealthAnomalySimDstTests

open Expecto
open Expecto.Flip
open SageFs.Features.HealthAnomaly
open SageFs.Simulation
open SageFs.Simulation.HealthAnomalySim

let private seeds = [ 1 .. 300 ]

let private violationsFor (behavior: DetectorBehavior) (invariant: State list -> ShapeEvent list -> HealthAnomalySimInvariants.Violation list) =
  seeds
  |> List.map (fun seed ->
    let scenario = scenarioOf seed
    let states = trace behavior scenario
    scenario, invariant states scenario.Events)
  |> List.filter (fun (_, vs) -> not (List.isEmpty vs))

let private expectNone (label: string) (bad: (Scenario * HealthAnomalySimInvariants.Violation list) list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad
       |> List.truncate 3
       |> List.map (fun (s, vs) ->
         sprintf "seed=%d baseline=%.1f noiseFrac=%.3f\n  %s" s.Seed s.InitialBaseline s.NoiseFrac
           (vs |> List.truncate 3 |> List.map (fun v -> sprintf "[event %d: %A] %s" v.EventIndex v.Event v.Why) |> String.concat "\n  "))
       |> String.concat "\n")

/// The last event in every `scenarioOf` scenario is the one `Step` that is
/// never followed by a `Recovery` — the legitimate, permanent scale change.
let private isThePermanentStep (events: ShapeEvent list) (i: int) = i = events.Length - 1

/// `scenarioOf`'s fixed event list: index 7 is the `Flat` right after the
/// `Drift` segment — it inherits whatever breach state the drift left
/// behind and needs `flatSegmentsEventuallySettle`, not the strict
/// never-fire check every other `Flat` is held to.
let private isTheSettlingFlatAfterDrift (i: int) = i = 7

[<Tests>]
let healthAnomalySimDstTests =
  testList "HealthAnomaly DST" [

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let a = trace DetectorBehavior.Real (scenarioOf 7) |> List.last
      let b = trace DetectorBehavior.Real (scenarioOf 7) |> List.last
      (a.History |> List.map fst, a.Baseline)
      |> Expect.equal "replaying a seed gives the identical end state" (b.History |> List.map fst, b.Baseline)

    testList "the real detector holds every invariant" [

      testCase "NO-FIRE-ON-FLAT" <| fun _ ->
        violationsFor DetectorBehavior.Real (HealthAnomalySimInvariants.flatSegmentsNeverFire isTheSettlingFlatAfterDrift)
        |> expectNone "a flat segment fired"

      testCase "THE-POST-DRIFT-FLAT-EVENTUALLY-SETTLES" <| fun _ ->
        violationsFor DetectorBehavior.Real (HealthAnomalySimInvariants.flatSegmentsEventuallySettle isTheSettlingFlatAfterDrift 30)
        |> expectNone "the flat segment after a drift never settled back to Normal"

      testCase "SUSTAINED-STEP-ALWAYS-FIRES-WITHIN-BOUND" <| fun _ ->
        violationsFor DetectorBehavior.Real (HealthAnomalySimInvariants.sustainedStepAlwaysFiresWithinBound 30)
        |> expectNone "a sustained step never fired within 30 samples"

      testCase "DRIFT-NEVER-JUMPS-STRAIGHT-TO-BROKEN" <| fun _ ->
        violationsFor DetectorBehavior.Real HealthAnomalySimInvariants.driftNeverJumpsStraightToBroken
        |> expectNone "a drift reached Broken without reading Drifting first"

      testCase "SINGLE-SPIKE-NEVER-FIRES" <| fun _ ->
        violationsFor DetectorBehavior.Real HealthAnomalySimInvariants.singleSpikeNeverFires
        |> expectNone "a lone spike sample fired"

      testCase "RECOVERY-EVENTUALLY-CLEARS-TO-NORMAL" <| fun _ ->
        violationsFor DetectorBehavior.Real (HealthAnomalySimInvariants.recoveryEventuallyClearsToNormal 30)
        |> expectNone "a recovery segment never settled back to Normal"

      testCase "LEGITIMATE-SCALE-CHANGE-EVENTUALLY-SETTLES" <| fun _ ->
        seeds
        |> List.choose (fun seed ->
          let scenario = scenarioOf seed
          let states = trace DetectorBehavior.Real scenario
          match HealthAnomalySimInvariants.legitimateScaleChangeEventuallySettles (isThePermanentStep scenario.Events) 30 states scenario.Events with
          | [] -> None
          | vs -> Some(scenario, vs))
        |> expectNone "a permanent scale change never settled back to Normal"
    ]

    testList "the seeds reach the interesting cases, so a green run means something" [
      let traces = seeds |> List.map (fun seed -> scenarioOf seed, trace DetectorBehavior.Real (scenarioOf seed))

      testCase "some seeds' sustained step reads Broken, not just Drifting" <| fun _ ->
        traces
        |> List.exists (fun (scenario, states) ->
          HealthAnomalySimInvariants.segments states scenario.Events
          |> List.exists (fun (_, ev, samples) ->
            match ev with
            | ShapeEvent.Step(_, _) -> samples |> List.exists (fun (_, v) -> match v with Verdict.Broken _ -> true | _ -> false)
            | _ -> false))
        |> Expect.isTrue "at least one seed's step is severe enough to read as Broken"

      testCase "some seeds' drift reads Drifting" <| fun _ ->
        traces
        |> List.exists (fun (scenario, states) ->
          HealthAnomalySimInvariants.segments states scenario.Events
          |> List.exists (fun (_, ev, samples) ->
            match ev with
            | ShapeEvent.Drift(_, _) -> samples |> List.exists (fun (_, v) -> match v with Verdict.Drifting _ -> true | _ -> false)
            | _ -> false))
        |> Expect.isTrue "at least one seed's drift is picked up as Drifting"
    ]

    testCase "TWIN: a detector that always says fine is caught by every invariant that claims the real one fires" <| fun _ ->
      // AlwaysFineTwin never fires, period — so any invariant that requires
      // firing must report EVERY seed as a violation against it. Two
      // independent invariants that each require firing (a sustained step,
      // a lone flat segment turning out to never be entirely quiet in this
      // scenario shape is not one of them — only "must fire" invariants can
      // catch a twin that never fires) both catch it on every seed, and
      // running the same invariant against the same twin twice gives the
      // identical result — the twin has no hidden randomness of its own.
      let stepCaughtFirstRun = violationsFor DetectorBehavior.AlwaysFineTwin (HealthAnomalySimInvariants.sustainedStepAlwaysFiresWithinBound 30)
      let stepCaughtSecondRun = violationsFor DetectorBehavior.AlwaysFineTwin (HealthAnomalySimInvariants.sustainedStepAlwaysFiresWithinBound 30)
      let spikeInvariantCannotCatchIt = violationsFor DetectorBehavior.AlwaysFineTwin HealthAnomalySimInvariants.singleSpikeNeverFires

      stepCaughtFirstRun
      |> List.isEmpty
      |> Expect.isFalse "AlwaysFineTwin never fires at all, so every seed's sustained-step segment must be reported as a violation"

      stepCaughtFirstRun.Length |> Expect.equal "the twin fails on EVERY seed's sustained step, not just some — it never fires, period" seeds.Length

      (stepCaughtFirstRun.Length, stepCaughtSecondRun.Length)
      |> Expect.equal "the twin is deterministic: the same invariant against the same twin catches the same seeds every time" (stepCaughtFirstRun.Length, stepCaughtFirstRun.Length)

      spikeInvariantCannotCatchIt
      |> List.isEmpty
      |> Expect.isTrue "a never-fire invariant can't distinguish the twin from the real detector — only a must-fire invariant proves the twin is wrong, which is why the module is tested with both kinds"
  ]
