module SageFs.Tests.SseReplaySimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.SseReplaySim
open SageFs.Simulation.SseReplayInvariants

/// DST over editors that connect to `/events`, lose the connection, and come back with the last
/// id they saw, while the daemon keeps publishing, evicts old frames to stay inside its limits,
/// and sometimes restarts. The subject is the REAL `SseReplay.catchUp`; a client answers a
/// `Resync` the way the daemon does, with a snapshot of everything up to now.
///
/// Every invariant is shown to have teeth: a twin that breaks exactly that guarantee must
/// VIOLATE it, or the invariant would pass vacuously.
///  - NOTHING-MISSED: after a client has reconnected, every frame it may see is either delivered
///    or covered by a snapshot. Broken by a replay that trusts an id from another run, and by one
///    that carries on after the frame it wanted was evicted.
///  - DELIVERED-ONCE-IN-ORDER: a client never gets a frame twice, and never out of order.
///    Broken by a replay that starts at the frame the client already has.
///  - NEVER-ANOTHER-SESSIONS-FRAMES: a stream scoped to one session never receives another
///    session's frame. Broken by a replay that ignores the scope.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

/// Seeds swept when asking "does some scenario break this twin".
let private teethSeeds = [ 1 .. 200 ]

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION steps=%d\n  Violations=%A" (List.last states).Step vs

let private violatedBy (behavior: ReplayBehavior) (invariant: Invariant) (scenario: Scenario) =
  match invariant.Check (trace behavior scenario) with
  | Outcome.Violated _ -> true
  | Outcome.Holds -> false

let private someSeedBreaks (behavior: ReplayBehavior) (invariant: Invariant) =
  teethSeeds
  |> List.exists (fun seed -> violatedBy behavior invariant (scenarioOf seed))
  |> Expect.isTrue (sprintf "some seeded scenario must make the %A twin violate %s" behavior invariant.Id)

let private holdsFor (behavior: ReplayBehavior) (invariant: Invariant) (scenario: Scenario) =
  match invariant.Check (trace behavior scenario) with
  | Outcome.Holds -> ()
  | Outcome.Violated msg -> failtestf "%s should hold for %A: %s" invariant.Id behavior msg

/// More bytes than any scenario's frames add up to, so only the frame count ever bites.
let private plentyOfBytes = 1 <<< 20

let private roomy = { MaxFrames = 100; MaxBytes = plentyOfBytes }

/// Client 0 sees every session, client 1 only session aaaaaaaa, client 2 only bbbbbbbb.
let private everyone = [ 0; 1; 2 ]

let private reconnectAll = everyone |> List.map SimEvent.Reconnect

[<Tests>]
let tests =
  testList "DST SSE replay" [

    testList "the real replay holds every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios hold every invariant" <|
        fun (seed: int) -> assertHolds (trace ReplayBehavior.Real (scenarioOf seed))

      testProperty "same seed => identical trace (determinism/replay)" <|
        fun (seed: int) ->
          let scenario = scenarioOf seed
          trace ReplayBehavior.Real scenario
          |> Expect.equal "replaying the same seed yields the identical states" (trace ReplayBehavior.Real scenario)
    ]

    testList "a twin that trusts an id from another run loses frames" [

      testCase "REPRODUCED: a restart reuses seq 3, and the twin replays from there" <| fun _ ->
        let scenario =
          { Seed = -601
            Limits = roomy
            Events =
              reconnectAll
              @ [ SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.RestartDaemon
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Publish(FrameScope.Daemon, 10) ]
              @ reconnectAll }
        holdsFor ReplayBehavior.Real nothingMissedAfterReconnect scenario
        violatedBy ReplayBehavior.IgnoresEpochTwin nothingMissedAfterReconnect scenario
        |> Expect.isTrue "the twin replays only seq 4 of the new run and never mentions 1 to 3"

      testCase "some seeded scenario makes the twin violate nothing-missed" <| fun _ ->
        someSeedBreaks ReplayBehavior.IgnoresEpochTwin nothingMissedAfterReconnect
    ]

    testList "a twin that carries on past an evicted frame loses frames" [

      testCase "REPRODUCED: the frame after the client's id was dropped, and the twin replays what is left" <| fun _ ->
        let scenario =
          { Seed = -602
            Limits = { MaxFrames = 2; MaxBytes = plentyOfBytes }
            Events =
              reconnectAll
              @ [ SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Disconnect 0
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Publish(FrameScope.Daemon, 10) ]
              @ reconnectAll }
        holdsFor ReplayBehavior.Real nothingMissedAfterReconnect scenario
        violatedBy ReplayBehavior.ForgetsEvictionTwin nothingMissedAfterReconnect scenario
        |> Expect.isTrue "the twin never delivers seq 2 and no snapshot covers it"

      testCase "some seeded scenario makes the twin violate nothing-missed" <| fun _ ->
        someSeedBreaks ReplayBehavior.ForgetsEvictionTwin nothingMissedAfterReconnect
    ]

    testList "a twin that starts at the frame the client has delivers it twice" [

      testCase "REPRODUCED: the client saw seq 2, and the twin sends seq 2 again" <| fun _ ->
        let scenario =
          { Seed = -603
            Limits = roomy
            Events =
              reconnectAll
              @ [ SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Disconnect 0
                  SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Reconnect 0 ] }
        holdsFor ReplayBehavior.Real deliveredOnceInOrder scenario
        violatedBy ReplayBehavior.ReplaysTheLastSeenTwin deliveredOnceInOrder scenario
        |> Expect.isTrue "seq 2 reaches client 0 twice"

      testCase "some seeded scenario makes the twin violate delivered-once-in-order" <| fun _ ->
        someSeedBreaks ReplayBehavior.ReplaysTheLastSeenTwin deliveredOnceInOrder
    ]

    testList "a twin that ignores the stream's scope leaks another session's frames" [

      testCase "REPRODUCED: client 1 asked for aaaaaaaa and is replayed bbbbbbbb's frame" <| fun _ ->
        let scenario =
          { Seed = -604
            Limits = roomy
            Events =
              reconnectAll
              @ [ SimEvent.Publish(FrameScope.Session "aaaaaaaa", 10)
                  SimEvent.Disconnect 1
                  SimEvent.Publish(FrameScope.Session "bbbbbbbb", 10)
                  SimEvent.Reconnect 1 ] }
        holdsFor ReplayBehavior.Real neverSeesAnotherSessionsFrames scenario
        violatedBy ReplayBehavior.IgnoresScopeTwin neverSeesAnotherSessionsFrames scenario
        |> Expect.isTrue "client 1 is handed bbbbbbbb's frame"

      testCase "some seeded scenario makes the twin violate never-sees-another-sessions-frames" <| fun _ ->
        someSeedBreaks ReplayBehavior.IgnoresScopeTwin neverSeesAnotherSessionsFrames
    ]

    // The other twins break what `catchUp` computes. This one breaks the step around it: `SseLog.Attach`
    // works out what a connection missed and registers it for live frames under one lock, so no publish
    // can fall between. The scenarios above step an attach as one event and could not tell the lock from
    // no lock; here the event after a `Reconnect` lands between its two halves.
    testList "a twin whose attach is two steps loses a frame published between them" [

      testCase "REPRODUCED: a frame lands after the twin worked out the replay and before it registered the client" <| fun _ ->
        let scenario =
          { Seed = -605
            Limits = roomy
            Events =
              reconnectAll
              @ [ SimEvent.Publish(FrameScope.Daemon, 10)
                  SimEvent.Disconnect 0
                  SimEvent.Reconnect 0
                  SimEvent.Publish(FrameScope.Daemon, 10) ] }
        holdsFor ReplayBehavior.Real nothingMissedAfterReconnect scenario
        violatedBy ReplayBehavior.TwoStepAttachTwin nothingMissedAfterReconnect scenario
        |> Expect.isTrue "the second frame is in neither the replay nor the live feed, and no snapshot covers it"

      testCase "the same scenario with the attach in one step is whole, and the halves split only where a Reconnect was" <| fun _ ->
        let events = [ SimEvent.Reconnect 0; SimEvent.Publish(FrameScope.Daemon, 10); SimEvent.Disconnect 0 ]
        splitAttaches events
        |> Expect.equal "the publish sits between the halves" [ SimEvent.AttachBegin 0; SimEvent.Publish(FrameScope.Daemon, 10); SimEvent.AttachEnd 0; SimEvent.Disconnect 0 ]

      testCase "some seeded scenario makes the twin violate nothing-missed" <| fun _ ->
        someSeedBreaks ReplayBehavior.TwoStepAttachTwin nothingMissedAfterReconnect
    ]
  ]
