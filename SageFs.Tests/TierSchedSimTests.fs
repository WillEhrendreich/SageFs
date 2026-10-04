module SageFs.Tests.TierSchedSimTests

open Expecto
open Expecto.Flip
open SageFs.Build.TierPlan
open SageFs.Simulation
open SageFs.Simulation.TierSched
open SageFs.Simulation.TierSchedInvariants

/// DST over the tier scheduler: a fake clock, a fake machine whose neighbours spike the CPU and hold memory, units
/// that complete or die, all folded through the REAL `TierPlan.advanceWith` that ci-pipeline.fsx's `runTiers` calls.
/// Chaos is data (a seed makes a scenario), so a failing seed replays exactly.
///
/// Every invariant is shown to have teeth: a twin that breaks exactly that guarantee must VIOLATE it for some seed,
/// or the invariant would pass vacuously.
///  - NEVER-ADMIT-PAST-THE-RESERVE: a start leaves the reserve free, unless the starvation guard started it.
///  - NEVER-OVER-THE-CAP: no more tier processes than the hard cap, ever.
///  - EVERY-UNIT-STARTS-ONCE: none is dropped, none starts twice.
///  - THE-RUN-ENDS: with units that die and neighbours that never leave, the line still drains.
///  - STARTS-IN-ORDER: the order the caller chose is the order units start in.
///  - STARTS-ARE-SETTLED: two starts are never closer than the settle time while anything runs.
///  - BLIND-MEANS-FALLBACK: an unreadable machine never runs more than the static fallback.
///  - GUARD-ONLY-WHEN-IDLE: the starvation guard fires only with none of our units running.

let private assertHolds (behavior: Behavior) (seed: int) =
  let trace = trace behavior (scenarioOf seed)
  match violations trace with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION — seed=%d\n  Violations=%A" seed vs

/// Seeds swept when asking "does some scenario break this twin".
let private teethSeeds = [ 1 .. 300 ]

let private violatedBy (behavior: Behavior) (invariant: Invariant) (seed: int) =
  match invariant.Check (trace behavior (scenarioOf seed)) with
  | Outcome.Violated _ -> true
  | Outcome.Holds -> false

let private someSeedBreaks (behavior: Behavior) (invariant: Invariant) =
  teethSeeds
  |> List.exists (violatedBy behavior invariant)
  |> Expect.isTrue (sprintf "some seeded scenario must make the %A twin violate %s" behavior invariant.Id)

[<Tests>]
let tests =
  testList "DST tier scheduler" [

    testList "the real scheduler holds every invariant" [

      testCase "every seed in the sweep holds every invariant" <| fun _ ->
        teethSeeds |> List.iter (assertHolds Behavior.Real)

      testCase "the same seed replays the same starts at the same moments" <| fun _ ->
        teethSeeds
        |> List.iter (fun seed ->
          let a = trace Behavior.Real (scenarioOf seed)
          let b = trace Behavior.Real (scenarioOf seed)
          b.Starts |> Expect.equal (sprintf "seed %d replays" seed) a.Starts)

      testCase "the sweep reaches each fault: a unit that dies, a neighbour that holds the memory, a machine that cannot be read" <| fun _ ->
        let traces = teethSeeds |> List.map (fun s -> trace Behavior.Real (scenarioOf s))
        traces |> List.exists (fun t -> t.Ended |> List.exists (fun (_, _, how) -> how = Dies))
        |> Expect.isTrue "some unit dies"
        traces |> List.exists (fun t -> t.Starts |> List.exists (fun s -> s.Basis = StarvationGuard))
        |> Expect.isTrue "the starvation guard fires for some seed"
        traces |> List.exists (fun t -> t.Scenario.Feed = PressureUnreadable)
        |> Expect.isTrue "the machine cannot be read for some seed"
    ]

    testList "each twin breaks exactly one guarantee" [

      testCase "the memory-blind twin starts a unit past the reserve" <| fun _ ->
        someSeedBreaks Behavior.IgnoresMemoryTwin neverAdmitPastReserve

      testCase "the cap-blind twin runs more processes than the cap" <| fun _ ->
        someSeedBreaks Behavior.IgnoresCapTwin neverOverCap

      testCase "the twin that starts a unit and keeps it in the line starts it twice" <| fun _ ->
        someSeedBreaks Behavior.StartsTwiceTwin everyUnitStartsOnce

      testCase "the impatient-less twin never ends the run when a neighbour holds the memory for good" <| fun _ ->
        someSeedBreaks Behavior.NoPatienceTwin runEnds

      testCase "the settle-less twin starts units back to back" <| fun _ ->
        someSeedBreaks Behavior.NoSettleTwin startsAreSettled

      testCase "the twin that starts the line from the back breaks the order" <| fun _ ->
        someSeedBreaks Behavior.OutOfOrderTwin startsInOrder

      testCase "the twin that reads a blind machine as an idle one runs past the fallback" <| fun _ ->
        someSeedBreaks Behavior.BlindIsIdleTwin blindMeansFallback

      testCase "the twin that starves nothing but starts despite our own units breaks the guard's rule" <| fun _ ->
        someSeedBreaks Behavior.GuardAlwaysTwin guardOnlyWhenIdle
    ]

    testCase "WHY — a neighbour that holds memory for good costs the gate a wait per unit, not the gate" <| fun _ ->
      let scenario = hoggedScenario
      let t = trace Behavior.Real scenario
      match t.RunEnd with
      | RunEnd.Drained _ -> ()
      | RunEnd.CutOff at -> failtestf "the run never ended (cut off at %.0fs)" at
      t.Starts |> List.length |> Expect.equal "every unit started" scenario.Units.Length
      t.Starts |> List.forall (fun s -> s.Basis = StarvationGuard)
      |> Expect.isTrue "each started by the guard, one at a time"
  ]
