module SageFs.Tests.BridgeIdentitySimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.BridgeIdentitySim
open SageFs.Simulation.BridgeIdentityInvariants

/// DST over stdio bridges that connect, call, lose their stream, ride through daemon restarts and sit quiet for longer
/// than the cohort's lease, folded through the REAL identity resolution, client key, liveness table and `Cohort.decide`.
/// The reaper's selection is the daemon's own `DaemonMode.cohortMembersToRenew`.
///
/// The twins are the two defects of the 2026-11-07 cohort run: `SessionIdIdentityTwin` makes the member id a fingerprint
/// of the session id (C3: every daemon restart made the conductor a stranger to its own seat), and `CallsOnlyLeaseTwin`
/// renews only a member that called (C5: an LLM conductor that waited on its agents lost its seat after 30 minutes).
/// The invariants must FAIL against them, or they prove nothing.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 50 }

let private steps = 60

let private bridges = 3

let private run (behavior: Behavior) (seed: int) (events: SimEvent list) =
  BridgeIdentitySim.run behavior SageFs.Server.DaemonMode.cohortMembersToRenew Timeouts.agentActivityFresh seed events

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | found -> failtestf "INVARIANT VIOLATION (first of %d): %s" found.Length (List.head found)

let private mentions (invariant: string) (found: string list) = found |> List.exists (fun message -> message.StartsWith invariant)

[<Tests>]
let tests =
  testList "DST bridge identity" [

    testList "the product holds every invariant" [

      testPropertyWithConfig simConfig "seeded lifecycles: one bridge is one member through restarts, and a connected bridge keeps its seat" <|
        fun (seed: int) -> assertHolds (run Behavior.Real seed (scenarioOf seed bridges steps))

      testProperty "same seed, same trace (replay by seed)" <| fun (seed: int) ->
        let events = scenarioOf seed bridges steps
        let idsAt trace = trace |> List.map (fun (s: State) -> s.LastId |> Map.map (fun _ who -> MemberTable.MemberId.display who), s.Cohort.Conductor)
        idsAt (run Behavior.Real seed events) |> Expect.equal "identical" (idsAt (run Behavior.Real seed events))

      testCase "a conductor that outlives a daemon restart is still the conductor, and answers to the same id" <| fun _ ->
        let states = run Behavior.Real 1 [ SimEvent.Connect 0; SimEvent.Call 0; SimEvent.DaemonRestart; SimEvent.Call 0 ]
        assertHolds states
        let last = List.last states
        (match last.Cohort.Conductor with
         | Cohort.ConductorBinding.Bound holder -> holder = last.LastId.[0]
         | _ -> false)
        |> Expect.isTrue "the seat is bound to the id the bridge calls as now"

      testCase "two bridges are two members" <| fun _ ->
        let last = run Behavior.Real 1 [ SimEvent.Connect 0; SimEvent.Connect 1; SimEvent.Call 0; SimEvent.Call 1; SimEvent.DaemonRestart; SimEvent.Call 0; SimEvent.Call 1 ] |> List.last
        (last.LastId.[0] <> last.LastId.[1]) |> Expect.isTrue "different keys, different members, before and after the restart"

      testCase "a bridge that has been quiet past the lease with its stream open is still there" <| fun _ ->
        let states = run Behavior.Real 1 [ SimEvent.Connect 0; SimEvent.Call 0; SimEvent.Idle pastTheLease ]
        assertHolds states
        let last = List.last states
        (match Map.tryFind last.LastId.[0] last.Cohort.Members with
         | Some { Presence = Cohort.MemberPresence.Present } -> true
         | _ -> false)
        |> Expect.isTrue "still Present"

      testCase "silence still costs a seat: a member with no stream and no call for the lease is departed" <| fun _ ->
        let last = run Behavior.Real 1 [ SimEvent.Connect 0; SimEvent.Call 0; SimEvent.StreamDrops 0; SimEvent.Idle pastTheLease ] |> List.last
        (match Map.tryFind last.LastId.[0] last.Cohort.Members with
         | Some { Presence = Cohort.MemberPresence.Departed _ } -> true
         | _ -> false)
        |> Expect.isTrue "departed, and the seat is no longer held by it"
        (match last.Cohort.Conductor with
         | Cohort.ConductorBinding.Bound _ -> false
         | _ -> true)
        |> Expect.isTrue "the seat is empty"
    ]

    testList "the twins reproduce the two defects" [

      testCase "REPRODUCED (C3) — identity from the session id: a daemon restart turns the conductor into a stranger" <| fun _ ->
        let events = [ SimEvent.Connect 0; SimEvent.Call 0; SimEvent.DaemonRestart; SimEvent.Call 0 ]
        let found = violations (run Behavior.SessionIdIdentityTwin 1 events)
        found |> mentions "IDENTITY-IS-STABLE" |> Expect.isTrue "the id changed"
        found |> mentions "SEAT-HOLDER-IS-STILL-THE-BRIDGE" |> Expect.isTrue "and the bridge no longer answers to the seat it holds"
        violations (run Behavior.Real 1 events) |> Expect.isEmpty "the same events against the product hold nothing"

      testCase "REPRODUCED (C5) — a lease that counts only calls departs a connected conductor that is waiting" <| fun _ ->
        let events = [ SimEvent.Connect 0; SimEvent.Call 0; SimEvent.Idle pastTheLease ]
        violations (run Behavior.CallsOnlyLeaseTwin 1 events) |> mentions "CONNECTED-BRIDGE-STAYS-PRESENT" |> Expect.isTrue "departed with its stream open"
        violations (run Behavior.Real 1 events) |> Expect.isEmpty "the same events against the product hold nothing"

      testProperty "some seed breaks each twin (the invariants have teeth)" <| fun (seed: int) ->
        let broken (behavior: Behavior) =
          [ for offset in 0 .. 20 -> violations (run behavior (seed + offset) (scenarioOf (seed + offset) bridges steps)) ]
          |> List.exists (List.isEmpty >> not)
        broken Behavior.SessionIdIdentityTwin |> Expect.isTrue "within 21 neighbouring seeds the session-id twin breaks an invariant"
        broken Behavior.CallsOnlyLeaseTwin |> Expect.isTrue "within 21 neighbouring seeds the calls-only twin breaks an invariant"
    ]
  ]
