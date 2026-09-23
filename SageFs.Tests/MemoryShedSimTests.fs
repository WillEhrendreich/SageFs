module SageFs.Tests.MemoryShedSimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.MemorySupervisor
open SageFs.Simulation
open SageFs.Simulation.MemoryShedSim
open SageFs.Simulation.MemoryShedInvariants

/// DST over the whole memory-shedding lifecycle: sessions created, touched,
/// faulted, hung and stopped, while machine memory pressure rises and falls,
/// folded through the REAL `MemorySupervisor.step`. Same rules as the other
/// DST harnesses here: chaos is data (a seeded event list), the real
/// decision function is the subject, and the `NeverReapTwin` shows
/// `dead-memory-is-released` has teeth — it FAILS against a daemon that
/// never reaps, which is exactly the regression this whole effort exists to
/// prevent.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | vs ->
    let scenario = (List.last states)
    failtestf "INVARIANT VIOLATION — replay seed=%d, steps=%d\n  Violations=%A" scenario.Step scenario.Step vs

[<Tests>]
let tests =
  testList "DST memory shedding" [

    testList "the real supervisor holds every invariant" [

      testPropertyWithConfig simConfig "seeded lifecycles — every invariant holds (real reducer)" <|
        fun (seed: int) ->
          let scenario = scenarioOf seed
          assertHolds (trace SupervisorBehavior.Real scenario)

      testProperty "same seed => identical trace (determinism/replay)" <|
        fun (seed: int) ->
          let scenario = scenarioOf seed
          let a = trace SupervisorBehavior.Real scenario
          let b = trace SupervisorBehavior.Real scenario
          a |> Expect.equal "replaying the same seed yields the identical trace" b
    ]

    testList "the never-reap twin reproduces the leak" [

      testCase "REPRODUCED — a faulted session's bytes are never released under the twin" <| fun _ ->
        let scenario =
          { Seed = -401
            Thresholds = defaultThresholds
            MachineTotal = 62_000_000_000L
            Events =
              [ SimEvent.CreateSessionRequest("leaky", 5_000_000_000L)
                SimEvent.Tick
                SimEvent.Fault "leaky"
                SimEvent.Tick
                SimEvent.Tick ] }
        let real = trace SupervisorBehavior.Real scenario
        let twin = trace SupervisorBehavior.NeverReapTwin scenario
        (List.last real).Sessions
        |> Map.containsKey "leaky"
        |> Expect.isFalse "the real supervisor reaps the dead session"
        (List.last twin).Sessions
        |> Map.containsKey "leaky"
        |> Expect.isTrue "the never-reap twin leaves it attributed forever — this is the incident"
        match deadMemoryIsReleased.Check twin with
        | Outcome.Violated _ -> ()
        | Outcome.Holds -> failtest "expected the never-reap twin to VIOLATE dead-memory-is-released"

      testPropertyWithConfig simConfig "the invariant has teeth: some seeded scenario violates it under the twin" <|
        fun () ->
          let violatedSomewhere =
            [ 1 .. 60 ]
            |> List.exists (fun seed ->
              let scenario = scenarioOf seed
              match deadMemoryIsReleased.Check (trace SupervisorBehavior.NeverReapTwin scenario) with
              | Outcome.Violated _ -> true
              | Outcome.Holds -> false)
          violatedSomewhere
          |> Expect.isTrue "at least one seeded scenario must expose the never-reap leak"
    ]

    testList "named worked scenarios (the brief's own shapes)" [

      testCase "a machine crushed by five sessions, one dead, refuses admission and sheds idle" <| fun _ ->
        let scenario =
          { Seed = -402
            Thresholds = defaultThresholds
            MachineTotal = 62_000_000_000L
            Events =
              [ SimEvent.CreateSessionRequest("viewed", 1_000_000_000L)
                SimEvent.SetUserActive("viewed", true)
                SimEvent.CreateSessionRequest("idle-long", 1_000_000_000L)
                SimEvent.PassMinutes 45
                SimEvent.CreateSessionRequest("dead", 1_000_000_000L)
                SimEvent.Fault "dead"
                SimEvent.ExternalMemoryDelta 58_000_000_000L
                SimEvent.Tick
                SimEvent.CreateSessionRequest("refused-please", 1_000_000_000L) ] }
        let states = trace SupervisorBehavior.Real scenario
        assertHolds states
        let final = List.last states
        Map.containsKey "dead" final.Sessions |> Expect.isFalse "the dead session was reaped"
        Map.containsKey "viewed" final.Sessions |> Expect.isTrue "the active/viewed session was never touched"
        final.Refusals |> Expect.isNonEmpty "the machine is critically low — new sessions get refused"

      testCase "a healthy machine with three faulted sessions still reaps all three" <| fun _ ->
        let scenario =
          { Seed = -403
            Thresholds = defaultThresholds
            MachineTotal = 64_000_000_000L
            Events =
              [ SimEvent.CreateSessionRequest("f1", 500_000_000L)
                SimEvent.CreateSessionRequest("f2", 500_000_000L)
                SimEvent.CreateSessionRequest("f3", 500_000_000L)
                SimEvent.Fault "f1"
                SimEvent.Fault "f2"
                SimEvent.WorkerHungDetected "f3"
                SimEvent.Tick ] }
        let states = trace SupervisorBehavior.Real scenario
        assertHolds states
        let final = List.last states
        [ "f1"; "f2"; "f3" ]
        |> List.iter (fun id -> Map.containsKey id final.Sessions |> Expect.isFalse (sprintf "%s was reaped despite plenty of memory" id))
        final.Refusals |> Expect.isEmpty "plenty of memory — nothing should ever be refused here"
    ]
  ]
