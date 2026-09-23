/// Five agents each rebuilding/warming up against one daemon, all at once,
/// is how a night's incident happened — nobody misbehaved, nothing
/// coordinated. `ExpensiveWorkLease` is the coordination point: ask before
/// spending memory, get back Granted/Wait/Refused. These tests pin the
/// named shapes from the brief (a quiet machine, four leases out at Tight,
/// an expired lease being reclaimed, one member holding the cap while
/// another waits), plus the fairness and expiry guarantees.
module SageFs.Tests.ExpensiveWorkLeaseTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ExpensiveWorkLease

let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

[<Tests>]
let kindWireTests =
  testList "ExpensiveWorkLease.Kind wire tokens" [
    testCase "every Kind round-trips through its wire token" <| fun () ->
      Kind.all
      |> List.iter (fun k -> Kind.tryParse (Kind.toToken k) |> Expect.equal (sprintf "%A round-trips" k) (Some k))

    testCase "an unrecognized token parses to None, never a guess" <| fun () ->
      Kind.tryParse "not_a_real_kind" |> Expect.isNone "closed set — no silent fallback"
  ]

[<Tests>]
let namedShapeTests =
  testList "ExpensiveWorkLease.request — named shapes from the brief" [

    testCase "a quiet machine grants immediately" <| fun () ->
      let state', decision = request epoch MemoryPressure.Normal empty "agent-a" Kind.Rebuild
      match decision with
      | Decision.Granted(_, expiresAt) ->
        (expiresAt > epoch) |> Expect.isTrue "expiry is in the future"
        state'.Active |> Expect.hasLength "one lease now active" 1
      | other -> failtestf "expected Granted on a quiet machine, got %A" other

    testCase "four leases out at Tight: cap is 1, a fifth agent waits" <| fun () ->
      // Four leases already active (perhaps granted back when pressure was
      // Normal) — pressure has since risen to Tight, whose cap is 1.
      let fourActive =
        [ for i in 1 .. 4 ->
            { Id = LeaseId.create ()
              Kind = Kind.SessionCreateOrWarmup
              Holder = sprintf "agent-%d" i
              GrantedAt = epoch
              ExpiresAt = epoch.AddMinutes 5.0 } ]
      let state = { empty with Active = fourActive }
      let _, decision = request epoch MemoryPressure.Tight state "agent-5" Kind.Rebuild
      match decision with
      | Decision.Wait(retryAfter, reason) ->
        (retryAfter > TimeSpan.Zero) |> Expect.isTrue "a real, positive retry-after"
        reason |> Expect.stringContains "names how many are out" "4 lease(s) active"
        reason |> Expect.stringContains "names the pressure" "tight"
      | other -> failtestf "expected Wait — the pool is already over Tight's cap of 1, got %A" other

    testCase "an expired lease is reclaimed, freeing the slot for the next request" <| fun () ->
      let expired =
        { Id = LeaseId.create (); Kind = Kind.Rebuild; Holder = "agent-a"; GrantedAt = epoch.AddMinutes -20.0; ExpiresAt = epoch.AddMinutes -10.0 }
      let state = { empty with Active = [ expired ] }
      let state', decision = request epoch MemoryPressure.Tight state "agent-b" Kind.Rebuild
      match decision with
      | Decision.Granted _ ->
        state'.Active |> List.exists (fun l -> l.Holder = "agent-a") |> Expect.isFalse "the expired lease is gone"
        state'.Active |> List.exists (fun l -> l.Holder = "agent-b") |> Expect.isTrue "the new one is granted"
      | other -> failtestf "expected the expired lease to be reclaimed and the new request Granted, got %A" other

    testCase "one member holding the per-holder cap is Refused; another member still waits normally" <| fun () ->
      let holderALeases =
        [ for _ in 1 .. maxPerHolder ->
            { Id = LeaseId.create (); Kind = Kind.Rebuild; Holder = "agent-a"; GrantedAt = epoch; ExpiresAt = epoch.AddMinutes 5.0 } ]
      let state = { empty with Active = holderALeases }
      let _, decisionA = request epoch MemoryPressure.Normal state "agent-a" Kind.Rebuild
      match decisionA with
      | Decision.Refused reason -> reason |> Expect.stringContains "names the cap" (sprintf "%d/%d" maxPerHolder maxPerHolder)
      | other -> failtestf "expected agent-a (already at its per-holder cap) to be Refused, got %A" other
      let _, decisionB = request epoch MemoryPressure.Normal state "agent-b" Kind.Rebuild
      match decisionB with
      | Decision.Granted _ -> ()
      | other -> failtestf "agent-b holds nothing and Normal has spare capacity — expected Granted, got %A" other
  ]

[<Tests>]
let fairnessTests =
  testList "ExpensiveWorkLease — fairness and expiry" [

    testCase "queue is FIFO by arrival: the first to ask is the first granted when a slot frees" <| fun () ->
      // Tight caps at 1. agent-a gets the only slot; agent-b then agent-c ask
      // and both wait, in that order.
      let s0 = empty
      let s1, dA = request epoch MemoryPressure.Tight s0 "agent-a" Kind.Rebuild
      match dA with Decision.Granted _ -> () | other -> failtestf "expected agent-a granted, got %A" other
      let s2, dB = request epoch MemoryPressure.Tight s1 "agent-b" Kind.Rebuild
      match dB with Decision.Wait _ -> () | other -> failtestf "expected agent-b to wait, got %A" other
      let s3, dC = request epoch MemoryPressure.Tight s2 "agent-c" Kind.Rebuild
      match dC with Decision.Wait _ -> () | other -> failtestf "expected agent-c to wait, got %A" other
      // agent-a releases. Now agent-b (asked first) must be the one granted
      // next, not agent-c.
      let leaseA = match dA with Decision.Granted(id, _) -> id | _ -> failtest "unreachable"
      let s4, releaseOutcome = release leaseA s3
      releaseOutcome |> Expect.equal "agent-a's lease was live" ReleaseOutcome.Released
      let s5, dBRetry = request (epoch.AddSeconds 1.0) MemoryPressure.Tight s4 "agent-b" Kind.Rebuild
      match dBRetry with Decision.Granted _ -> () | other -> failtestf "agent-b asked first — expected Granted, got %A" other
      let _, dCRetry = request (epoch.AddSeconds 1.0) MemoryPressure.Tight s5 "agent-c" Kind.Rebuild
      match dCRetry with
      | Decision.Wait _ -> ()
      | other -> failtestf "agent-c asked second and the slot just went to agent-b — expected Wait, got %A" other

    testCase "a retrying holder does not create a duplicate queue entry" <| fun () ->
      let s0 = empty
      let s1, _ = request epoch MemoryPressure.Critical s0 "agent-a" Kind.Rebuild // Wait, enqueued
      let s2, _ = request (epoch.AddSeconds 5.0) MemoryPressure.Critical s1 "agent-a" Kind.Rebuild // retry, same ask
      s2.Queue |> Expect.hasLength "still one queue entry for agent-a" 1

    testCase "release makes the lease immediately unavailable" <| fun () ->
      let state, decision = request epoch MemoryPressure.Normal empty "agent-a" Kind.FullBuild
      let leaseId = match decision with Decision.Granted(id, _) -> id | _ -> failtest "expected Granted"
      let released, outcome1 = release leaseId state
      outcome1 |> Expect.equal "the lease was live" ReleaseOutcome.Released
      released.Active |> Expect.isEmpty "released lease is gone"
      // Releasing an already-released (or never-existing) id is a no-op,
      // not an error — but it DOES say AlreadyGone, the fencing signal.
      let releasedAgain, outcome2 = release leaseId released
      outcome2 |> Expect.equal "a second release of the same id finds nothing left to release" ReleaseOutcome.AlreadyGone
      releasedAgain.Active |> Expect.isEmpty "idempotent"

    testCase "Critical pressure admits nothing new but does not touch already-active leases" <| fun () ->
      let active = { Id = LeaseId.create (); Kind = Kind.RunApp; Holder = "agent-a"; GrantedAt = epoch; ExpiresAt = epoch.AddHours 1.0 }
      let state = { empty with Active = [ active ] }
      let state', decision = request epoch MemoryPressure.Critical state "agent-b" Kind.Rebuild
      match decision with
      | Decision.Wait _ -> ()
      | other -> failtestf "Critical's cap is 0 — expected Wait, got %A" other
      state'.Active |> Expect.contains "agent-a's already-granted lease is untouched" active
  ]

[<Tests>]
let neverExpiresTwinTests =
  testList "ExpensiveWorkLease.requestNeverExpiresTwin — reproduces the deadlock" [

    testCase "REPRODUCED — an abandoned lease under the twin blocks the pool forever" <| fun () ->
      // Tight caps concurrency at 1, so a single abandoned lease consumes
      // the WHOLE pool — the cleanest reproduction of the deadlock.
      let state, decision = requestNeverExpiresTwin epoch MemoryPressure.Tight empty "crashed-agent" Kind.Rebuild
      match decision with Decision.Granted _ -> () | other -> failtestf "expected the crashed agent granted first, got %A" other
      // Time passes well beyond the lease's TTL — under the real `request`
      // this would be reclaimed; under the twin it never is.
      let farFuture = epoch.AddHours 100.0
      let _, laterDecision = requestNeverExpiresTwin farFuture MemoryPressure.Tight state "agent-b" Kind.Rebuild
      match laterDecision with
      | Decision.Wait _ -> () // the deadlock: agent-b waits forever, since nothing ever reclaims the crashed lease
      | other -> failtestf "expected the twin to deadlock agent-b behind the abandoned lease, got %A" other

    testCase "the REAL request reclaims the identical abandoned lease instead" <| fun () ->
      let state, decision = request epoch MemoryPressure.Tight empty "crashed-agent" Kind.Rebuild
      match decision with Decision.Granted _ -> () | other -> failtestf "expected granted first, got %A" other
      let farFuture = epoch.AddHours 100.0
      let _, laterDecision = request farFuture MemoryPressure.Tight state "agent-b" Kind.Rebuild
      match laterDecision with
      | Decision.Granted _ -> ()
      | other -> failtestf "the real request must reclaim the expired lease and grant agent-b, got %A" other
  ]
