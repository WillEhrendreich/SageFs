module SageFs.Tests.DeltaRouteSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.DeltaRouteSim
open SageFs.Simulation.DeltaRouteInvariants

/// DST for the order things happen in when a run_app app is patched by metadata delta: the runtime refusing a delta
/// at generation N, the process being replaced under a chain, a second save arriving while one waits to be applied, a
/// build between two saves, a request inside the method while its delta lands. The REAL decisions are folded
/// (`DeltaSession`, `ProbeRegistry`, `PatchConfirmation`); the op order IS the scheduler. See
/// `SageFs.Simulation/DeltaRouteSim.fs` for the model and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 600 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) - replay this scenario:\n  Seed=%d\n  Rude=%A\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Rude t.Scenario.Ops vs

let private firedBy (runTwin: Scenario -> Trace) (scenario: Scenario) : string list =
  violations (runTwin scenario) |> List.map fst

let private anySeedViolates (runTwin: Scenario -> Trace) : bool =
  [ 1 .. 400 ] |> List.exists (fun seed -> not (List.isEmpty (violations (runTwin (DeltaRouteGenerators.fromSeed seed)))))

let private saidAt (t: Trace) (op: Op) : Said list =
  t.Steps |> List.filter (fun s -> s.Op = op) |> List.map _.Said

[<Tests>]
let tests =
  testList "DST delta route" [

    testList "the real decisions hold the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios - never patched with a stale baseline, a failed apply restarts, the chain never forks, patched means running, confirmed only once the new body ran"
        (fun (seed: int) -> assertHolds (run (DeltaRouteGenerators.fromSeed seed)))

      testCase "failedApplyThenAnotherSave: the refusal is a restart, and the save that follows is a restart too, not a delta on a process nobody knows" <| fun _ ->
        let t = run DeltaRouteGenerators.failedApplyThenAnotherSave
        t.Steps |> List.filter _.RuntimeRefused |> List.length |> Expect.equal "the runtime refused one delta" 1
        saidAt t (Op.Prepare 2) |> List.exists (function Said.Restarted _ -> true | _ -> false)
        |> Expect.isTrue "the third save is a restart that says why"
        t.Applications |> List.map _.Save |> Expect.equal "only the first delta landed" [ 0 ]
        assertHolds t

      testCase "secondSaveLandsDuringAnApply: the delta prepared from a generation the process has left is not applied" <| fun _ ->
        let t = run DeltaRouteGenerators.secondSaveLandsDuringAnApply
        t.Applications |> List.map _.Save |> Expect.equal "only the first landed" [ 0 ]
        saidAt t (Op.Apply 1) |> List.exists (function Said.Restarted _ -> true | _ -> false)
        |> Expect.isTrue "the stale one is a restart that says why"
        assertHolds t

      testCase "hostRestartLosesTheChain: a chain for a module that is gone refuses, because the module that runs is asked, not the one remembered" <| fun _ ->
        let t = run DeltaRouteGenerators.hostRestartLosesTheChain
        t.Applications |> List.map _.Save |> Expect.equal "only the delta for the first process landed" [ 0 ]
        saidAt t (Op.Prepare 1) |> List.exists (function Said.Restarted _ -> true | _ -> false)
        |> Expect.isTrue "and said why"
        assertHolds t

      testCase "rebuildBetweenTwoSaves: the delta is computed against what the process runs, so the one that was prepared and not applied is carried too" <| fun _ ->
        let t = run DeltaRouteGenerators.rebuildBetweenTwoSaves
        match t.Applications with
        | [ a ] ->
          a.Basis |> Expect.equal "against the build the process runs" a.ProcessRan
          a.Target |> Expect.equal "to the newest" 2
        | other -> failtestf "one delta should land: %A" other
        assertHolds t

      testCase "reloadDuringARequest: a request that was inside the method when the delta landed ran the old body, so finishing proves nothing" <| fun _ ->
        let t = run DeltaRouteGenerators.reloadDuringARequest
        saidAt t (Op.BoundElapses 0) |> Expect.equal "never confirmed" [ Said.NotConfirmed(0, 1) ]
        assertHolds t

      testCase "reloadThenANewRequest: a request that enters the new body confirms it" <| fun _ ->
        let t = run DeltaRouteGenerators.reloadThenANewRequest
        saidAt t (Op.BoundElapses 0) |> Expect.equal "confirmed" [ Said.Confirmed(0, 1) ]
        assertHolds t

      testCase "refusedEdit: a version the emitter refuses is a restart and never reaches the runtime" <| fun _ ->
        let t = run DeltaRouteGenerators.refusedEdit
        t.Applications |> List.map _.Target |> Expect.equal "only the version the emitter takes was applied, after the app came back" [ 2 ]
        assertHolds t

      testCase "debuggerAttached: a save with a debugger attached is a restart that says so, and nothing is applied" <| fun _ ->
        let t = run DeltaRouteGenerators.debuggerAttached
        saidAt t (Op.Prepare 0) |> List.exists (function Said.Restarted why -> why.Contains "debugger" | _ -> false)
        |> Expect.isTrue "the debugger is named"
        t.Applications |> List.map _.Save |> Expect.equal "the save after the debugger left is applied" [ 1 ]
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED - asking the module the chain remembers lets a delta for a gone module reach the new process" <| fun _ ->
        firedBy runIgnoresTheRunningModule DeltaRouteGenerators.hostRestartLosesTheChain
        |> Expect.contains "never-patched-with-a-stale-baseline must fire" "never-patched-with-a-stale-baseline"

      testCase "REPRODUCED - a route that forgets a failure applies the next delta to a process that may hold part of one" <| fun _ ->
        firedBy runForgetsAFailure DeltaRouteGenerators.failedApplyThenAnotherSave
        |> Expect.contains "a-failed-apply-restarts-not-lies must fire" "a-failed-apply-restarts-not-lies"

      testCase "REPRODUCED - checking the generation only at prepare lets two saves from one generation both land" <| fun _ ->
        firedBy runChecksOnlyWhenPrepared DeltaRouteGenerators.secondSaveLandsDuringAnApply
        |> Expect.contains "never-patched-with-a-stale-baseline must fire" "never-patched-with-a-stale-baseline"

      testCase "REPRODUCED - diffing against the last build instead of what the process runs misses what was never applied" <| fun _ ->
        firedBy runDiffsAgainstTheLastBuild DeltaRouteGenerators.rebuildBetweenTwoSaves
        |> Expect.contains "never-patched-with-a-stale-baseline must fire" "never-patched-with-a-stale-baseline"

      testCase "REPRODUCED - advancing the chain before the runtime answers leaves it ahead of a process that refused" <| fun _ ->
        firedBy runAdvancesBeforeTheAnswer DeltaRouteGenerators.failedApplyThenAnotherSave
        |> Expect.contains "chain-never-forks must fire" "chain-never-forks"

      testCase "REPRODUCED - taking a request finishing as proof the new body ran confirms a patch nothing entered" <| fun _ ->
        firedBy runConfirmsOnAnyCompletion DeltaRouteGenerators.reloadDuringARequest
        |> Expect.contains "confirmed-only-after-the-new-body-ran must fire" "confirmed-only-after-the-new-body-ran"

      testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        anySeedViolates runIgnoresTheRunningModule |> Expect.isTrue "ignores-the-running-module is caught by a generated scenario"
        anySeedViolates runForgetsAFailure |> Expect.isTrue "forgets-a-failure is caught by a generated scenario"
        anySeedViolates runChecksOnlyWhenPrepared |> Expect.isTrue "checks-only-when-prepared is caught by a generated scenario"
        anySeedViolates runDiffsAgainstTheLastBuild |> Expect.isTrue "diffs-against-the-last-build is caught by a generated scenario"
        anySeedViolates runAdvancesBeforeTheAnswer |> Expect.isTrue "advances-before-the-answer is caught by a generated scenario"
        anySeedViolates runConfirmsOnAnyCompletion |> Expect.isTrue "confirms-on-any-completion is caught by a generated scenario"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical trace (real decisions)" <| fun (seed: int) ->
        let a = run (DeltaRouteGenerators.fromSeed seed)
        let b = run (DeltaRouteGenerators.fromSeed seed)
        a.Steps |> List.map (fun s -> s.Said, s.Truth) = (b.Steps |> List.map (fun s -> s.Said, s.Truth))
    ]
  ]
