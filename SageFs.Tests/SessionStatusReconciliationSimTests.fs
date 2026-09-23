module SageFs.Tests.SessionStatusReconciliationSimTests

/// DST for the registry-vs-worker reconciliation loop
/// (`SageFs.Core/WorkerProtocol.fs`'s `SessionLifecycleStatus.ofWorkerReport`
/// via `SageFs.Simulation.SessionStatusReconciliationSim`) — see that
/// module's header for the historical bug (a live worker reply could
/// resurrect a session the registry had already recorded as Faulted or
/// Stopped) and why this is a seeded model over the real pure function, not
/// a reimplementation.
///
/// Covers the three shapes named in the brief — a save racing a fault, a
/// fault during warmup on the hot reload path, a status poll racing both —
/// plus a twin that reproduces the historical regression.
open Expecto
open Expecto.Flip
open SageFs.WorkerProtocol
open SageFs.Simulation
open SageFs.Simulation.SessionStatusReconciliationSim
open SageFs.Simulation.SessionStatusReconciliationInvariants

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Initial=%A\n  Events=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Initial t.Scenario.Events vs

[<Tests>]
let tests =
  testList "DST session-status reconciliation (ofWorkerReport)" [

    testList "the fixed contract holds — seeded scenarios" [
      testPropertyWithConfig simConfig
        "seeded scenarios — the real function never lets a stale poll resurrect a terminal status, and health always agrees with a terminal status"
        <| fun (seed: int) -> assertHolds (run (SessionStatusReconciliationGenerators.fromSeed seed))
    ]

    testList "the three brief shapes, named" [

      testCase "a fault during warmup on the hot reload path: a live-sounding poll after the fault never resurrects Starting" <| fun _ ->
        let t = run SessionStatusReconciliationGenerators.faultDuringWarmup
        match t.Final with
        | SessionLifecycleStatus.Faulted _ -> ()
        | other -> failtestf "expected the fault to stick, got %A" other
        assertHolds t

      testCase "a save racing a fault: stray Evaluating/Ready polls after the fault never undo it" <| fun _ ->
        let t = run SessionStatusReconciliationGenerators.saveRacingFault
        match t.Final with
        | SessionLifecycleStatus.Faulted _ -> ()
        | other -> failtestf "expected the fault to stick through the racing polls, got %A" other
        assertHolds t

      testCase "a status poll racing both a fault and a stop: whichever lands last among stray polls, the final read is the last DIRECT daemon decision, not a resurrected live status" <| fun _ ->
        let t = run SessionStatusReconciliationGenerators.statusPollRacingBoth
        match t.Final with
        | SessionLifecycleStatus.Stopped -> ()
        | other -> failtestf "expected Stopped (the last direct daemon decision), got %A" other
        assertHolds t

      testCase "a genuine restart after a fault is the one way a terminal status moves — and it moves to what the restart says, not what a stray poll says" <| fun _ ->
        let t = run SessionStatusReconciliationGenerators.restartAfterFault
        match t.Final with
        | SessionLifecycleStatus.Ready h -> h.Pid |> Expect.equal "the restarted worker's pid carries through" 9999
        | other -> failtestf "expected Ready(pid=9999) after the restart's own poll reconciled, got %A" other
        assertHolds t
    ]

    testList "the twin reproduces the historical regression (teeth)" [

      testCase "REPRODUCED — the non-sticky twin resurrects a faulted session on the very next stale poll" <| fun _ ->
        let t = runNonStickyTwin SessionStatusReconciliationGenerators.faultDuringWarmup
        match t.Final with
        | SessionLifecycleStatus.Starting _ -> ()
        | other -> failtestf "expected the twin to reproduce the historical bug (resurrected to Starting), got %A" other
        violations t
        |> List.map fst
        |> Expect.contains "no-stale-poll-resurrection must fire against the twin" "no-stale-poll-resurrection"

      testCase "the twin's resurrection also breaks the health/status agreement" <| fun _ ->
        // Not just a wrong label: SessionHealth would call the resurrected
        // Starting status "Starting", while a caller who read the registry
        // a moment earlier saw Failed — the exact disagreement the incident
        // surfaced.
        let t = runNonStickyTwin SessionStatusReconciliationGenerators.faultDuringWarmup
        let realT = run SessionStatusReconciliationGenerators.faultDuringWarmup
        t.Final |> Expect.notEqual "the twin and the real reducer disagree on the same scenario" realT.Final

      testCase "the twin still honors an explicit restart — restart's handling didn't change between real and twin" <| fun _ ->
        let t = runNonStickyTwin SessionStatusReconciliationGenerators.restartAfterFault
        match t.Final with
        | SessionLifecycleStatus.Ready _ -> ()
        | other -> failtestf "the twin should still honor a genuine restart, got %A" other

      testPropertyWithConfig simConfig
        "the invariant has teeth: ANY scenario with a fault followed by a non-Faulted poll discriminates real from twin" <|
        fun (seed: int) ->
          let handle = { Pid = 4242; Port = Some 5000 }
          let scenario =
            { Seed = seed
              Initial = SessionLifecycleStatus.Starting handle
              Events =
                [ Event.DaemonFault (Some (sprintf "seed-%d fault" seed))
                  Event.Poll SessionStatus.Ready ] }
          let realT = run scenario
          let twinT = runNonStickyTwin scenario
          let realViolated = violations realT |> List.map fst |> List.contains "no-stale-poll-resurrection"
          let twinViolated = violations twinT |> List.map fst |> List.contains "no-stale-poll-resurrection"
          match realViolated, twinViolated with
          | false, true -> ()
          | _ ->
            failtestf
              "expected the real reducer clean and the twin violated for seed %d — real violated=%b twin violated=%b"
              seed realViolated twinViolated
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical trace" <|
        fun (seed: int) ->
          let a = run (SessionStatusReconciliationGenerators.fromSeed seed)
          let b = run (SessionStatusReconciliationGenerators.fromSeed seed)
          a.Final |> Expect.equal "replaying the same seed yields the identical final fold state" b.Final
    ]
  ]
