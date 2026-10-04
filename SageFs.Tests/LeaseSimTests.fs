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
/// REAL `ExpensiveWorkLease.request`. Two of the agents share one MCP
/// connection and differ only by agent name, the shape that collapsed into a
/// single holder and made three agents wait up to 35 minutes behind a lease
/// that a sibling held.
///
/// Every invariant here is shown to have teeth: a twin that breaks exactly
/// that guarantee must VIOLATE it, or the invariant would pass vacuously.
///  - MUTUAL-EXCLUSION: no holder ever holds two leases at once, and a grant
///    never pushes the pool over its cap.
///  - EVERY-LEASE-EXPIRES: a lease lapses at its ttl whether or not anyone
///    releases it, and nothing moves that moment, and a waiter that stops
///    asking loses its place.
///  - REFUSAL-NAMES-THE-REAL-HOLDER: a decision that names a lease as the
///    caller's is the caller's, and a queued caller is shown the leases that
///    really hold the pool.
///  - SAME-HOLDER-IDEMPOTENT: asking again for what you hold gives you the
///    same lease back, with nothing renewed and nothing added.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION — steps=%d\n  Violations=%A" (List.last states).Step vs

/// Seeds swept when asking "does some scenario break this twin".
let private teethSeeds = [ 1 .. 200 ]

let private violatedBy (behavior: PoolBehavior) (invariant: Invariant) (scenario: Scenario) =
  match invariant.Check (trace behavior scenario) with
  | Outcome.Violated _ -> true
  | Outcome.Holds -> false

let private holdsFor (behavior: PoolBehavior) (invariant: Invariant) (scenario: Scenario) =
  match invariant.Check (trace behavior scenario) with
  | Outcome.Holds -> ()
  | Outcome.Violated msg -> failtestf "%s should hold for %A: %s" invariant.Id behavior msg

let private someSeedBreaks (behavior: PoolBehavior) (invariant: Invariant) =
  teethSeeds
  |> List.exists (fun seed -> violatedBy behavior invariant (scenarioOf seed))
  |> Expect.isTrue (sprintf "some seeded scenario must make the %A twin violate %s" behavior invariant.Id)

[<Tests>]
let tests =
  testList "DST expensive-work lease" [

    testList "the real pool holds every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios hold every invariant" <|
        fun (seed: int) -> assertHolds (trace PoolBehavior.Real (scenarioOf seed))

      // `LeaseId.create()` mints a fresh `Guid.NewGuid()` per grant, a
      // deliberate, correct impurity (production wants unguessable ids), so
      // determinism is checked on a projection that drops the id.
      // Each state carries EVERY decision so far, so the last state's list holds them all once, and projecting
      // each state's copy again was the whole cost of this test (a `%A` per decision, per state, per run: about
      // 140s under load). The per-step part is the pool's queue and active count. Equality of this projection says
      // exactly what equality of the per-state projections said, because a state's list is a prefix of the last.
      // `%A` is reflection-based pretty printing; a decision's parts are plain data, so they are compared as data.
      let projectDecision (r: DecisionRecord) =
        let decision =
          match r.Decision with
          | Decision.Granted(_, expiresAt) -> "granted", string expiresAt
          | Decision.AlreadyHeld lease -> "already-held", string lease.ExpiresAt
          | Decision.Queued waiting -> "queued", String.concat "|" [ string waiting.RetryAfter.Ticks; string waiting.Position; string (List.length waiting.Holding) ]
          | Decision.Refused refusal ->
            "refused",
            (match refusal with
             | Refusal.HoldsOtherKind(_, asked) -> Kind.toToken asked
             | Refusal.OtherKindQueued(q, asked) -> Kind.toToken q + "/" + Kind.toToken asked)
        r.AtStep, r.Clock, r.Pressure, r.Holder, r.Kind, decision

      let projectForDeterminism (states: State list) =
        (List.last states).Decisions |> List.map projectDecision,
        states |> List.map (fun s -> s.Pool.Queue |> List.map (fun q -> q.Holder, q.Kind, q.Seq), List.length s.Pool.Active)

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
              [ SimEvent.Request(agentA, Kind.RunApp)
                SimEvent.Request(agentB, Kind.RunApp)
                SimEvent.Request(agentC, Kind.RunApp)
                SimEvent.Request(agentD, Kind.RunApp)
                SimEvent.Abandon agentA
                SimEvent.Abandon agentB
                SimEvent.Abandon agentC
                SimEvent.Abandon agentD
                SimEvent.PressureChange MemoryPressure.Normal
                SimEvent.PassSeconds FixtureDurations.passPastEveryLease
                SimEvent.Request(agentE, Kind.Rebuild) ] }
        let real = trace PoolBehavior.Real scenario
        let twin = trace PoolBehavior.NeverExpiresTwin scenario
        (List.last real).Pool.Queue |> Expect.isEmpty "the real pool reclaims all four abandoned leases and admits agent-e"
        (List.last twin).Pool.Queue |> Expect.isNonEmpty "the twin never reclaims them — agent-e is deadlocked behind four ghosts"
        match queueDrainsOnCooldown.Check twin with
        | Outcome.Violated _ -> ()
        | Outcome.Holds -> failtest "expected the never-expires twin to VIOLATE queue-drains-on-cooldown"
        match everyLeaseExpires.Check twin with
        | Outcome.Violated _ -> ()
        | Outcome.Holds -> failtest "expected the never-expires twin to VIOLATE every-lease-expires"
        holdsFor PoolBehavior.Real everyLeaseExpires scenario

      testCase "the invariant has teeth: some seeded scenario deadlocks the twin" <| fun () ->
        teethSeeds
        |> List.exists (fun seed -> violatedBy PoolBehavior.NeverExpiresTwin queueDrainsOnCooldown (scenarioOf seed))
        |> Expect.isTrue "at least one seeded scenario must deadlock the never-expires twin"

      testCase "every-lease-expires has teeth against the never-expires twin" <| fun () ->
        someSeedBreaks PoolBehavior.NeverExpiresTwin everyLeaseExpires
    ]

    testList "the collapsed-identity twin names the wrong holder" [

      testCase "REPRODUCED — two sub-agents on one connection are told they hold each other's lease" <| fun _ ->
        // agentA and agentB share a connection and differ only by agent name.
        let scenario =
          { Seed = -503
            Events = [ SimEvent.Request(agentA, Kind.FullBuild); SimEvent.Request(agentB, Kind.FullBuild) ] }
        holdsFor PoolBehavior.Real refusalNamesTheRealHolder scenario
        violatedBy PoolBehavior.CollapsedIdentityTwin refusalNamesTheRealHolder scenario
        |> Expect.isTrue "collapsing identity to the connection hands agent-b agent-a's lease as its own"

      testCase "refusal-names-the-real-holder has teeth against the collapsed-identity twin" <| fun () ->
        someSeedBreaks PoolBehavior.CollapsedIdentityTwin refusalNamesTheRealHolder
    ]

    testList "the duplicating twin lets one holder take the pool twice" [

      testCase "REPRODUCED — asking again for the same kind takes a second lease" <| fun _ ->
        let scenario =
          { Seed = -504
            Events = [ SimEvent.Request(agentA, Kind.TestSuiteRun); SimEvent.Request(agentA, Kind.TestSuiteRun) ] }
        holdsFor PoolBehavior.Real mutualExclusion scenario
        holdsFor PoolBehavior.Real sameHolderIdempotent scenario
        violatedBy PoolBehavior.DuplicatesOnReAskTwin mutualExclusion scenario
        |> Expect.isTrue "one holder now holds two leases"
        violatedBy PoolBehavior.DuplicatesOnReAskTwin sameHolderIdempotent scenario
        |> Expect.isTrue "the second ask did not return the same lease"

      testCase "mutual-exclusion has teeth against the duplicating twin" <| fun () ->
        someSeedBreaks PoolBehavior.DuplicatesOnReAskTwin mutualExclusion

      testCase "same-holder-idempotent has teeth against the duplicating twin" <| fun () ->
        someSeedBreaks PoolBehavior.DuplicatesOnReAskTwin sameHolderIdempotent
    ]

    testList "head-of-line blocking: a waiter that cannot go does not hold up one that can" [

      let lastDecision (states: State list) = (List.last (List.last states).Decisions).Decision

      // Tight (cap 1): a runs, b queues first and c second. Pressure eases to Normal (cap 4) and c asks first.
      let roomBeyondTheHead =
        { Seed = -601
          Events =
            [ SimEvent.PressureChange MemoryPressure.Tight
              SimEvent.Request(agentA, Kind.Rebuild)
              SimEvent.Request(agentB, Kind.Rebuild)
              SimEvent.Request(agentC, Kind.Rebuild)
              SimEvent.PressureChange MemoryPressure.Normal
              SimEvent.PassSeconds 2
              SimEvent.Request(agentC, Kind.Rebuild) ] }

      // The same line, but b never asks again (its caller gave up) and a finishes; c is still asking.
      let ghostHead =
        { Seed = -602
          Events =
            [ SimEvent.PressureChange MemoryPressure.Tight
              SimEvent.Request(agentA, Kind.Rebuild)
              SimEvent.Request(agentB, Kind.Rebuild)
              SimEvent.Request(agentC, Kind.Rebuild)
              SimEvent.Release agentA
              SimEvent.PassSeconds FixtureDurations.passPastAGoneWaiter
              SimEvent.Request(agentC, Kind.Rebuild) ] }

      testCase "REPRODUCED — with room for everyone the asker behind a live head is granted at once" <| fun _ ->
        let states = trace PoolBehavior.Real roomBeyondTheHead
        assertHolds states
        match lastDecision states with
        | Decision.Granted _ -> ()
        | other -> failtestf "there was room for b and c, expected c Granted, got %A" other

      testCase "REPRODUCED — a head that stopped asking does not hold up the asker behind it" <| fun _ ->
        let states = trace PoolBehavior.Real ghostHead
        assertHolds states
        match lastDecision states with
        | Decision.Granted _ -> ()
        | other -> failtestf "b is gone, expected c Granted, got %A" other
    ]

    testList "named worked scenario: no one starved forever" [

      testCase "four agents pile onto Rebuild at Critical pressure — all four eventually run" <| fun _ ->
        let scenario =
          { Seed = -502
            Events =
              [ SimEvent.PressureChange MemoryPressure.Critical
                SimEvent.Request(agentA, Kind.Rebuild)
                SimEvent.Request(agentB, Kind.Rebuild)
                SimEvent.Request(agentC, Kind.Rebuild)
                SimEvent.Request(agentD, Kind.Rebuild)
                SimEvent.PassSeconds 30
                // retries while still Critical: nobody should be granted yet
                SimEvent.Request(agentA, Kind.Rebuild)
                SimEvent.Request(agentB, Kind.Rebuild)
                SimEvent.PressureChange MemoryPressure.Normal
                SimEvent.PassSeconds 5
                SimEvent.Request(agentA, Kind.Rebuild)
                SimEvent.Request(agentB, Kind.Rebuild)
                SimEvent.Request(agentC, Kind.Rebuild)
                SimEvent.Request(agentD, Kind.Rebuild) ] }
        let states = trace PoolBehavior.Real scenario
        assertHolds states
        let final = List.last states
        final.Pool.Queue |> Expect.isEmpty "pressure eased and everyone eventually got admitted"
        final.Pool.Active |> Expect.hasLength "all four ended up holding a lease" 4
        final.Decisions
        |> List.forall (fun r -> r.Pressure <> MemoryPressure.Critical || (match r.Decision with Decision.Granted _ -> false | _ -> true))
        |> Expect.isTrue "no lease was ever granted while pressure was Critical"
    ]
  ]
