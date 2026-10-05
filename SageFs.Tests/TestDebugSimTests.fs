module SageFs.Tests.TestDebugSimTests

open Expecto
open Expecto.Flip
open SageFs.HostAgent.TestDebug
open SageFs.Simulation
open SageFs.Simulation.TestDebugSim
open SageFs.Simulation.TestDebugSimInvariants

/// DST for debugging one test: begins, releases with and without a debugger, a debugger that attaches early, inside the
/// wait for it, after the wait, or never, expiries early and late, finishes and a dying host, in every order a seed produces,
/// folded through the REAL `TestDebug.step`. Each twin must FAIL the invariant built to catch it, which is what shows the
/// invariants can fail at all.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (behavior: Behavior) (scenario: Scenario) =
  match violations (trace behavior scenario) with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION — replay this scenario:\n  Seed=%d\n  Events=%A\n  Violations=%A" scenario.Seed scenario.Events vs

let private violatedIds (behavior: Behavior) (scenario: Scenario) : string list =
  violations (trace behavior scenario) |> List.map fst

/// Whether the attach race of this seed ended with the test running under the debugger.
let private ran (seed: int) : bool =
  (final Behavior.Real (attachRace seed)).Applied
  |> List.exists (fun a -> match a.Effect with HoldEffect.StartTest _ -> true | _ -> false)

[<Tests>]
let tests =
  testList "DST debugging one test" [

    testList "the real reducer holds every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios: nothing runs without a debugger, nothing runs twice, every hold settles, no wait outlasts its bound, and every answer is backed by what happened"
      <| fun (seed: int) -> assertHolds Behavior.Real (scenarioOf seed)

      testPropertyWithConfig simConfig "a debugger that is mid-attach: seeded attach latency, and the test runs exactly when the debugger arrives inside the wait"
      <| fun (seed: int) ->
        assertHolds Behavior.Real (attachRace seed)
        ran seed = (latencyOf seed < graceTicks)

      testCase "an attached run: held, released, finished, and the editor is told how it ended" <| fun _ ->
        let states = trace Behavior.Real attachedRun
        assertHolds Behavior.Real attachedRun
        match (List.last states).Answers with
        | [ Answer.Continued(_, DebugProgress.StillRunning); Answer.Continued(_, DebugProgress.Ended(DebugEnd.Attached result)) ] ->
          result |> Expect.equal "the run's own result comes back" simResult
        | other -> failtestf "expected StillRunning then Attached, got %A" other

      testCase "a release that finds no debugger waits for one, and when none comes the test does not run" <| fun _ ->
        assertHolds Behavior.Real releaseWithoutDebugger
        let final = final Behavior.Real releaseWithoutDebugger
        final.Hold |> Expect.equal "ended without a debugger" (Hold.Ended(DebugTicket "sim-1", DebugEnd.ReleasedWithoutDebugger))
        final.Running |> Expect.isEmpty "the test never started"
        match final.Answers with
        | [ Answer.Continued(_, DebugProgress.StillRunning) ] -> ()
        | other -> failtestf "the release is answered 'still going' while the wait lasts, got %A" other

      testCase "a debugger that arrives inside the wait runs the test" <| fun _ ->
        assertHolds Behavior.Real attachesInsideTheWait
        match (List.last (trace Behavior.Real attachesInsideTheWait)).Answers with
        | [ Answer.Continued(_, DebugProgress.StillRunning); Answer.Continued(_, DebugProgress.Ended(DebugEnd.Attached result)) ] ->
          result |> Expect.equal "the run's own result" simResult
        | other -> failtestf "expected StillRunning then Attached, got %A" other

      testCase "a debugger that arrives after the wait ran out is too late: the test does not run" <| fun _ ->
        assertHolds Behavior.Real attachesAfterTheWait
        let final = final Behavior.Real attachesAfterTheWait
        final.Running |> Expect.isEmpty "the test never started"
        match List.last final.Answers with
        | Answer.Continued(_, DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger) -> ()
        | other -> failtestf "expected ReleasedWithoutDebugger, got %A" other

      testCase "a host that dies in the middle of the wait is said to be lost, and later calls cannot reach it" <| fun _ ->
        assertHolds Behavior.Real hostDiesWhileWaiting
        let final = final Behavior.Real hostDiesWhileWaiting
        match final.Hold with
        | Hold.Ended(_, DebugEnd.HostLost _) -> ()
        | other -> failtestf "expected HostLost, got %A" other
        match List.last final.Answers with
        | Answer.Unreachable -> ()
        | other -> failtestf "expected Unreachable, got %A" other

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

      testCase "REPRODUCED: a host that refuses the moment the test is released loses a debugger that was still attaching" <| fun _ ->
        violatedIds Behavior.RefusesAtOnceTwin attachesInsideTheWait
        |> Expect.contains "a-refusal-comes-only-from-a-spent-wait must fire against the refuses-at-once twin" "a-refusal-comes-only-from-a-spent-wait"

      testCase "REPRODUCED: a wait that never ends keeps the hold, and the door, for ever" <| fun _ ->
        let ids = violatedIds Behavior.WaitNeverEndsTwin releaseWithoutDebugger
        ids |> Expect.contains "never-waits-past-the-bound must fire against the wait-never-ends twin" "never-waits-past-the-bound"
        ids |> Expect.contains "and so must every-hold-settles" "every-hold-settles"

      testCase "REPRODUCED: a wait that does not hear the debugger arrive refuses a debugger that is attached" <| fun _ ->
        violatedIds Behavior.DeafToArrivalTwin attachedThenTimePasses
        |> Expect.contains "never-refuses-a-debugger-that-is-attached must fire against the deaf-to-arrival twin" "never-refuses-a-debugger-that-is-attached"

      testCase "REPRODUCED: a wait that does not hear the host die leaves a dead host holding a test" <| fun _ ->
        violatedIds Behavior.DeafToHostDeathTwin hostDiesWhileWaiting
        |> Expect.contains "a-dead-host-holds-nothing must fire against the deaf-to-host-death twin" "a-dead-host-holds-nothing"

      testCase "the seeded scenarios catch every twin too, so the teeth are in the random space and not only in the hand-built shapes" <| fun _ ->
        let seeds = [ 1..400 ]
        let caught (behavior: Behavior) (scenarioOf: int -> Scenario) =
          seeds |> List.exists (fun seed -> not (List.isEmpty (violations (trace behavior (scenarioOf seed)))))
        caught Behavior.NeverExpiresTwin scenarioOf |> Expect.isTrue "some seed catches the never-expires twin"
        caught Behavior.RunsUnattachedTwin scenarioOf |> Expect.isTrue "some seed catches the runs-unattached twin"
        caught Behavior.RefusesAtOnceTwin attachRace |> Expect.isTrue "some attach race catches the refuses-at-once twin"
        caught Behavior.WaitNeverEndsTwin attachRace |> Expect.isTrue "some attach race catches the wait-never-ends twin"
        caught Behavior.DeafToArrivalTwin attachRace |> Expect.isTrue "some attach race catches the deaf-to-arrival twin"
        caught Behavior.DeafToHostDeathTwin hostDeathRace |> Expect.isTrue "some host death inside the wait catches the deaf-to-host-death twin"
    ]

    testList "determinism and replay" [
      testProperty "the same seed gives the same final state" <| fun (seed: int) ->
        final Behavior.Real (scenarioOf seed) = final Behavior.Real (scenarioOf seed)

      testProperty "the same seed gives the same attach race" <| fun (seed: int) ->
        final Behavior.Real (attachRace seed) = final Behavior.Real (attachRace seed)
    ]
  ]
