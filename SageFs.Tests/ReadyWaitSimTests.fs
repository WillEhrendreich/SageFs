module SageFs.Tests.ReadyWaitSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.ReadyWaitSim
open SageFs.Simulation.ReadyWaitInvariants

/// DST for the claim a `wait_seconds` caller relies on: "when you asked me to wait I answered when the
/// thing you waited for was done, and I said which way it ended". The decisions under test are the real
/// `ReadyWait.plan`, `ReadyWait.ofSession` and `ReadyWait.ofRebuildEnd`; see
/// `SageFs.Simulation/ReadyWaitSim.fs` for the world around them, the op order that serves as the
/// scheduler, and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Start=%A\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Start t.Scenario.Ops vs

let private answers (t: Trace) : Answer list =
  t.Steps |> List.collect (fun s -> s.Answered |> List.map snd)

[<Tests>]
let readyWaitSimTests =
  testList "DST ready wait" [

    testList "the real decisions hold the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios — never NotNeeded during a rebuild, wakes on Ready, a deadline is TimedOut, a failed rebuild is not Ready"
        <| fun (seed: int) -> assertHolds (run (ReadyWaitGenerators.fromSeed seed))

      testCase "a caller who arrives during a rebuild is parked, and answered BecameReady when the new worker is up" <| fun _ ->
        let t = run ReadyWaitGenerators.arrivesDuringRebuild
        answers t |> Expect.equal "one answer, and it is BecameReady" [ Answer.BecameReady ]
        assertHolds t

      testCase "a rebuild that fails answers the parked caller Faulted" <| fun _ ->
        let t = run ReadyWaitGenerators.rebuildFails
        answers t |> Expect.equal "one answer, and it is Faulted" [ Answer.Faulted ]
        assertHolds t

      testCase "a caller who gives up before the rebuild ends is TimedOut" <| fun _ ->
        let t = run ReadyWaitGenerators.givesUp
        answers t |> Expect.equal "one answer, and it is TimedOut" [ Answer.TimedOut ]
        assertHolds t

      testCase "a caller on a warming session is answered BecameReady when its first worker is up" <| fun _ ->
        let t = run ReadyWaitGenerators.warmsUp
        answers t |> Expect.equal "BecameReady" [ Answer.BecameReady ]
        assertHolds t

      testCase "a session that faults during the rebuild answers the parked caller Faulted" <| fun _ ->
        let t = run ReadyWaitGenerators.faultsDuringRebuild
        answers t |> Expect.equal "Faulted" [ Answer.Faulted ]
        assertHolds t

      testCase "with nothing rebuilding a caller is told NotNeeded at once" <| fun _ ->
        let t = run ReadyWaitGenerators.nothingToWaitFor
        answers t |> Expect.equal "NotNeeded" [ Answer.NotNeeded ]
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED: the returns-early twin answers NotNeeded while a rebuild runs" <| fun _ ->
        let t = runReturnsEarly ReadyWaitGenerators.arrivesDuringRebuild
        violations t |> List.map fst |> Expect.contains "NEVER-NOT-NEEDED-DURING-REBUILD must fire" "NEVER-NOT-NEEDED-DURING-REBUILD"

      testCase "REPRODUCED: the polls twin leaves a caller parked on a session that is already Ready" <| fun _ ->
        let t = runPolls ReadyWaitGenerators.warmsUp
        violations t |> List.map fst |> Expect.contains "WAKES-ON-READY must fire" "WAKES-ON-READY"

      testCase "REPRODUCED: the never-times-out twin leaves a caller parked after its deadline" <| fun _ ->
        let t = runNeverTimesOut ReadyWaitGenerators.givesUp
        violations t |> List.map fst |> Expect.contains "TIMEOUT-NAMED must fire" "TIMEOUT-NAMED"

      testCase "REPRODUCED: the ready-after-failed-rebuild twin calls a failed rebuild BecameReady" <| fun _ ->
        let t = runReadyAfterFailedRebuild ReadyWaitGenerators.rebuildFails
        violations t |> List.map fst |> Expect.contains "FAILED-REBUILD-NOT-READY must fire" "FAILED-REBUILD-NOT-READY"

      testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 300 ]
        let violatesFor (runTwin: Scenario -> Trace) (id: string) =
          seeds |> List.exists (fun seed ->
            violations (runTwin (ReadyWaitGenerators.fromSeed seed)) |> List.exists (fun (found, _) -> found = id))
        violatesFor runReturnsEarly "NEVER-NOT-NEEDED-DURING-REBUILD" |> Expect.isTrue "returns-early is caught by a generated scenario"
        violatesFor runPolls "WAKES-ON-READY" |> Expect.isTrue "polls is caught by a generated scenario"
        violatesFor runNeverTimesOut "TIMEOUT-NAMED" |> Expect.isTrue "never-times-out is caught by a generated scenario"
        violatesFor runReadyAfterFailedRebuild "FAILED-REBUILD-NOT-READY" |> Expect.isTrue "ready-after-failed-rebuild is caught by a generated scenario"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical steps (real decisions)" <| fun (seed: int) ->
        let a = run (ReadyWaitGenerators.fromSeed seed)
        let b = run (ReadyWaitGenerators.fromSeed seed)
        a.Steps = b.Steps
    ]
  ]
