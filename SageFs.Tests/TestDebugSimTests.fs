module SageFs.Tests.TestDebugSimTests

open Expecto
open Expecto.Flip
open SageFs.HostAgent.TestDebug
open SageFs.Simulation
open SageFs.Simulation.TestDebugSim
open SageFs.Simulation.TestDebugSimInvariants

/// DST for debugging one test: begins, releases with and without a debugger, expiries early and late, finishes and a dying
/// host, in every order a seed produces, folded through the REAL `TestDebug.step`. Each twin must FAIL the invariant built
/// to catch it, which is what shows the invariants can fail at all.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (behavior: Behavior) (scenario: Scenario) =
  match violations (trace behavior scenario) with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION — replay this scenario:\n  Seed=%d\n  Events=%A\n  Violations=%A" scenario.Seed scenario.Events vs

let private violatedIds (behavior: Behavior) (scenario: Scenario) : string list =
  violations (trace behavior scenario) |> List.map fst

[<Tests>]
let tests =
  testList "DST debugging one test" [

    testList "the real reducer holds every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios: nothing runs without a debugger, nothing runs twice, every hold settles, and every answer is backed by what happened"
      <| fun (seed: int) -> assertHolds Behavior.Real (scenarioOf seed)

      testCase "an attached run: held, released, finished, and the editor is told how it ended" <| fun _ ->
        let states = trace Behavior.Real attachedRun
        assertHolds Behavior.Real attachedRun
        match (List.last states).Answers with
        | [ Answer.Continued(_, DebugProgress.StillRunning); Answer.Continued(_, DebugProgress.Ended(DebugEnd.Attached result)) ] ->
          result |> Expect.equal "the run's own result comes back" simResult
        | other -> failtestf "expected StillRunning then Attached, got %A" other

      testCase "a release that finds no debugger does not run the test" <| fun _ ->
        assertHolds Behavior.Real releaseWithoutDebugger
        match (List.last (trace Behavior.Real releaseWithoutDebugger)).Answers with
        | [ Answer.Continued(_, DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger) ] -> ()
        | other -> failtestf "expected ReleasedWithoutDebugger, got %A" other

      testCase "a release after the expiry is told no debugger came in time, and the test never ran" <| fun _ ->
        assertHolds Behavior.Real lateRelease
        match (List.last (trace Behavior.Real lateRelease)).Answers with
        | [ Answer.Continued(_, DebugProgress.Ended(DebugEnd.NoDebuggerWithin _)) ] -> ()
        | other -> failtestf "expected NoDebuggerWithin, got %A" other

      testCase "a host that dies with the test running is reported lost, and later calls cannot reach it" <| fun _ ->
        assertHolds Behavior.Real hostDiesWhileRunning
        match (List.last (trace Behavior.Real hostDiesWhileRunning)).Answers with
        | [ Answer.Continued(_, DebugProgress.StillRunning); Answer.Unreachable ] -> ()
        | other -> failtestf "expected StillRunning then Unreachable, got %A" other
    ]

    testList "the twins reproduce the failures the invariants exist to catch (teeth)" [

      testCase "REPRODUCED: a hold whose expiry never fires stays held for ever" <| fun _ ->
        violatedIds Behavior.NeverExpiresTwin abandonedHold
        |> Expect.contains "every-hold-settles must fire against the never-expires twin" "every-hold-settles"

      testCase "REPRODUCED: a hold that ignores the debugger runs the test anyway" <| fun _ ->
        violatedIds Behavior.RunsUnattachedTwin releaseWithoutDebugger
        |> Expect.contains "test-runs-only-with-a-debugger must fire against the runs-unattached twin" "test-runs-only-with-a-debugger"

      testCase "the seeded scenarios catch both twins too, so the teeth are in the random space and not only in the hand-built shapes" <| fun _ ->
        let seeds = [ 1..200 ]
        let caught (behavior: Behavior) = seeds |> List.exists (fun seed -> not (List.isEmpty (violations (trace behavior (scenarioOf seed)))))
        caught Behavior.NeverExpiresTwin |> Expect.isTrue "some seed catches the never-expires twin"
        caught Behavior.RunsUnattachedTwin |> Expect.isTrue "some seed catches the runs-unattached twin"
    ]

    testList "determinism and replay" [
      testProperty "the same seed gives the same final state" <| fun (seed: int) ->
        final Behavior.Real (scenarioOf seed) = final Behavior.Real (scenarioOf seed)
    ]
  ]
