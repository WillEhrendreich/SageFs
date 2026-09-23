module SageFs.Tests.LeaseSimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.ExpensiveWorkLease
open SageFs.Simulation
open SageFs.Simulation.LeaseSim
open SageFs.Simulation.LeaseSimInvariants

/// DST over several agents requesting, holding, releasing and abandoning
/// expensive-work leases while pressure rises and falls, folded through the
/// REAL `ExpensiveWorkLease.request`. `queueDrainsOnCooldown` is the "no
/// one starved forever" claim, and it must FAIL against
/// `PoolBehavior.NeverExpiresTwin` — an abandoned lease that is never
/// reclaimed permanently deadlocks the pool, which is exactly the failure
/// mode `request`'s reaping step exists to prevent.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION — steps=%d\n  Violations=%A" (List.last states).Step vs

[<Tests>]
let tests =
  testList "DST expensive-work lease" [

    testList "the real pool holds every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios — a grant never pushes the pool over cap, every Wait has a positive retry-after" <|
        fun (seed: int) -> assertHolds (trace PoolBehavior.Real (scenarioOf seed))

      // `LeaseId.create()` mints a fresh `Guid.NewGuid()` per grant — a
      // deliberate, correct impurity (production wants unguessable,
      // collision-free ids, not a seed-derived one) that this determinism
      // check has to see PAST: two replays of the identical seed grant the
      // SAME decisions in the SAME order for the SAME reasons, they just
      // don't share the literal id string of any one grant. So the
      // replayable claim is checked on a PROJECTION that drops the id
      // (Granted -> just its expiry) rather than full structural equality.
      let projectForDeterminism (states: State list) =
        states
        |> List.map (fun s ->
          s.Decisions
          |> List.map (fun r ->
            let decision =
              match r.Decision with
              | Decision.Granted(_, expiresAt) -> "granted", string expiresAt
              | Decision.Wait(retryAfter, reason) -> "wait", sprintf "%A|%s" retryAfter reason
              | Decision.Refused reason -> "refused", reason
            r.AtStep, r.Clock, r.Pressure, r.Holder, r.Kind, decision),
          s.Pool.Queue |> List.map (fun q -> q.Holder, q.Kind, q.Seq),
          List.length s.Pool.Active)

      testProperty "same seed => identical trace, up to the randomly-minted lease id (determinism/replay)" <|
        fun (seed: int) ->
          let scenario = scenarioOf seed
          let a = trace PoolBehavior.Real scenario
          let b = trace PoolBehavior.Real scenario
          projectForDeterminism a
          |> Expect.equal "replaying the same seed yields the identical decisions, queue order and active count" (projectForDeterminism b)
    ]

    testList "the never-expires twin deadlocks the pool" [

      testCase "REPRODUCED — an abandoned RunApp lease blocks the pool through the whole cooldown" <| fun _ ->
        let scenario =
          { Seed = -501
            Events =
              [ SimEvent.Request("agent-a", Kind.RunApp)
                SimEvent.Request("agent-b", Kind.RunApp)
                SimEvent.Request("agent-c", Kind.RunApp)
                SimEvent.Request("agent-d", Kind.RunApp)
                SimEvent.Abandon "agent-a"
                SimEvent.Abandon "agent-b"
                SimEvent.Abandon "agent-c"
                SimEvent.Abandon "agent-d"
                SimEvent.PressureChange MemoryPressure.Normal
                SimEvent.PassSeconds 20_000
                SimEvent.Request("agent-e", Kind.Rebuild) ] }
        let real = trace PoolBehavior.Real scenario
        let twin = trace PoolBehavior.NeverExpiresTwin scenario
        (List.last real).Pool.Queue |> Expect.isEmpty "the real pool reclaims all four abandoned leases and admits agent-e"
        (List.last twin).Pool.Queue |> Expect.isNonEmpty "the twin never reclaims them — agent-e is deadlocked behind four ghosts"
        match queueDrainsOnCooldown.Check twin with
        | Outcome.Violated _ -> ()
        | Outcome.Holds -> failtest "expected the never-expires twin to VIOLATE queue-drains-on-cooldown"

      testPropertyWithConfig simConfig "the invariant has teeth: some seeded scenario deadlocks the twin" <|
        fun () ->
          let deadlockedSomewhere =
            [ 1 .. 80 ]
            |> List.exists (fun seed ->
              match queueDrainsOnCooldown.Check (trace PoolBehavior.NeverExpiresTwin (scenarioOf seed)) with
              | Outcome.Violated _ -> true
              | Outcome.Holds -> false)
          deadlockedSomewhere |> Expect.isTrue "at least one seeded scenario must deadlock the never-expires twin"
    ]

    testList "named worked scenario: no one starved forever" [

      testCase "four agents pile onto Rebuild at Critical pressure — all four eventually run" <| fun _ ->
        let scenario =
          { Seed = -502
            Events =
              [ SimEvent.PressureChange MemoryPressure.Critical
                SimEvent.Request("agent-a", Kind.Rebuild)
                SimEvent.Request("agent-b", Kind.Rebuild)
                SimEvent.Request("agent-c", Kind.Rebuild)
                SimEvent.Request("agent-d", Kind.Rebuild)
                SimEvent.PassSeconds 30
                // retries while still Critical: nobody should be granted yet
                SimEvent.Request("agent-a", Kind.Rebuild)
                SimEvent.Request("agent-b", Kind.Rebuild)
                SimEvent.PressureChange MemoryPressure.Normal
                SimEvent.PassSeconds 5
                SimEvent.Request("agent-a", Kind.Rebuild)
                SimEvent.Request("agent-b", Kind.Rebuild)
                SimEvent.Request("agent-c", Kind.Rebuild)
                SimEvent.Request("agent-d", Kind.Rebuild) ] }
        let states = trace PoolBehavior.Real scenario
        assertHolds states
        let final = List.last states
        final.Pool.Queue |> Expect.isEmpty "pressure eased and everyone eventually got admitted"
        final.Pool.Active |> Expect.hasLength "all four ended up holding a lease" 4
        // Nothing was ever granted while pressure was Critical — Critical's
        // cap is 0, so this also re-confirms grantNeverExceedsCap concretely.
        final.Decisions
        |> List.forall (fun r -> r.Pressure <> MemoryPressure.Critical || (match r.Decision with Decision.Granted _ -> false | _ -> true))
        |> Expect.isTrue "no lease was ever granted while pressure was Critical"
    ]
  ]
