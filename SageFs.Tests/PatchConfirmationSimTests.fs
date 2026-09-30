module SageFs.Tests.PatchConfirmationSimTests

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.Features.PatchConfirmation
open SageFs.Simulation
open SageFs.Simulation.PatchConfirmationSim
open SageFs.Simulation.PatchConfirmationInvariants

/// DST for the claim "the new code is live": a patch is Patched only after its
/// new body was seen running. See `SageFs.Simulation/PatchConfirmationSim.fs`
/// for the fold, the op order that serves as the scheduler, and the twins.

type private Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private outcomesOf (t: Trace) : Outcome list =
  t.Final.Settlements
  |> List.choose (fun s ->
    match s.Step with
    | WatchStep.Settled o -> Some o
    | WatchStep.StillWaiting _
    | WatchStep.Abandoned -> None)

[<Tests>]
let tests =
  testList "DST patch confirmation" [

    testList "the real decision holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios — Patched is only claimed for functions that ran, every save resolves, nobody is blamed for a replaced function"
        <| fun (seed: int) -> assertHolds (run (PatchConfirmationGenerators.fromSeed seed))

      testCase "savedButNeverCalled: the inlined-callee shape ends in NeverEntered, never Patched" <| fun _ ->
        let t = run PatchConfirmationGenerators.savedButNeverCalled
        outcomesOf t |> Expect.equal "never entered" [ Outcome.NeverEntered("Sim.f0", [], 0, 1, []) ]
        assertHolds t

      testCase "savedThenCalled: a function that ran confirms the patch" <| fun _ ->
        let t = run PatchConfirmationGenerators.savedThenCalled
        outcomesOf t |> Expect.equal "confirmed" [ Outcome.Patched(1, 1) ]
        assertHolds t

      testCase "oneOfTwoCalled: the silent function is named and the one that ran is counted" <| fun _ ->
        let t = run PatchConfirmationGenerators.oneOfTwoCalled
        outcomesOf t |> Expect.equal "partial" [ Outcome.NeverEntered("Sim.f1", [], 1, 2, []) ]
        assertHolds t

      testCase "replacedBeforeItRan: the older save says nothing, the newer save is confirmed" <| fun _ ->
        let t = run PatchConfirmationGenerators.replacedBeforeItRan
        outcomesOf t |> Expect.equal "only the newer save reports" [ Outcome.Patched(1, 1) ]
        assertHolds t

      testCase "calledAfterTheBound: a function that runs after the wait gave up does not change what was reported" <| fun _ ->
        let t = run PatchConfirmationGenerators.calledAfterTheBound
        outcomesOf t |> Expect.equal "the bound won" [ Outcome.NeverEntered("Sim.f0", [], 0, 1, []) ]
        assertHolds t

      testCase "unobservableFunction: a function with no probe is never confirmed, even if it runs" <| fun _ ->
        let t = run PatchConfirmationGenerators.unobservableFunction
        outcomesOf t |> Expect.equal "unobservable" [ Outcome.NeverEntered("Sim.f3", [], 0, 1, []) ]
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — the claims-at-save twin reports Patched for a function nobody called" <| fun _ ->
        let t = runClaimsAtSave PatchConfirmationGenerators.savedButNeverCalled
        outcomesOf t |> Expect.equal "the pre-probe behaviour" [ Outcome.Patched(1, 1) ]
        violations t |> List.map fst |> Expect.contains "patched-means-seen-running must fire" "patched-means-seen-running"

      testCase "REPRODUCED — the bound-never-fires twin leaves a save pending forever" <| fun _ ->
        let t = runBoundNeverFires PatchConfirmationGenerators.savedButNeverCalled
        violations t |> List.map fst |> Expect.contains "every-watch-resolves must fire" "every-watch-resolves"

      testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 120 ]
        let anyViolates runTwin =
          seeds |> List.exists (fun seed -> violations (runTwin (PatchConfirmationGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runClaimsAtSave |> Expect.isTrue "claims-at-save is caught by a generated scenario"
        anyViolates runBoundNeverFires |> Expect.isTrue "bound-never-fires is caught by a generated scenario"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical settlements (real decision)" <| fun (seed: int) ->
        let a = run (PatchConfirmationGenerators.fromSeed seed)
        let b = run (PatchConfirmationGenerators.fromSeed seed)
        a.Final.Settlements = b.Final.Settlements
    ]
  ]
