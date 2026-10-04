/// Five agents each rebuilding/warming up against one daemon, all at once,
/// is how a night's incident happened — nobody misbehaved, nothing
/// coordinated. `ExpensiveWorkLease` is the coordination point: ask before
/// spending memory, get back Granted/Queued/Refused. These tests pin the
/// named shapes (a quiet machine, four leases out at Tight, an expired lease
/// being reclaimed, one member holding the cap while another waits), the
/// fairness and expiry guarantees, and the part three agents lost an hour to:
/// a refusal that names the REAL holder, so sub-agents that share one MCP
/// connection can tell their own lease from a sibling's.
module SageFs.Tests.ExpensiveWorkLeaseTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ExpensiveWorkLease

let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

/// Daemon-internal and HTTP callers know only a name for themselves.
let private ha = Holder.ofConnection "agent-a"
let private hb = Holder.ofConnection "agent-b"
let private hc = Holder.ofConnection "agent-c"

/// Two sub-agents that share ONE MCP connection, the shape that collapsed into one holder.
let private siblingOne = Holder.make "mcp:shared" "sub-agent-one" "/work/one"
let private siblingTwo = Holder.make "mcp:shared" "sub-agent-two" "/work/two"

let private grantedId (decision: Decision) : LeaseId =
  match decision with
  | Decision.Granted(id, _) -> id
  | other -> failtestf "expected Granted, got %A" other

[<Tests>]
let kindWireTests =
  testList "ExpensiveWorkLease.Kind wire tokens" [
    testCase "every Kind round-trips through its wire token" <| fun () ->
      Kind.all
      |> List.iter (fun k -> Kind.tryParse (Kind.toToken k) |> Expect.equal (sprintf "%A round-trips" k) (Some k))

    testCase "an unrecognized token parses to None, never a guess" <| fun () ->
      Kind.tryParse "not_a_real_kind" |> Expect.isNone "closed set — no silent fallback"

    testCase "WHY — every kind of lease has a finite, positive ttl, so a holder that never releases always lapses" <| fun () ->
      Kind.all
      |> List.iter (fun k -> (Kind.defaultTtl k > TimeSpan.Zero) |> Expect.isTrue (sprintf "%A has a ttl" k))
  ]

[<Tests>]
let holderIdentityTests =
  testList "ExpensiveWorkLease.Holder — who a lease is attributable to" [
    testCase "WHY — the same connection with a different agent name is a different holder" <| fun () ->
      (siblingOne = siblingTwo) |> Expect.isFalse "sub-agents on one connection are distinguishable"
      (siblingOne.Connection = siblingTwo.Connection) |> Expect.isTrue "and they do share the connection"

    testCase "the same connection and agent name in another working directory is another holder" <| fun () ->
      (Holder.make "mcp:shared" "worker" "/work/one" = Holder.make "mcp:shared" "worker" "/work/two")
      |> Expect.isFalse "the working directory is part of the identity"

    testCase "blank names and directories are the absence of a name, not a name" <| fun () ->
      Holder.make "mcp:shared" "  " "" |> Expect.equal "blank means unnamed" (Holder.ofConnection "mcp:shared")

    testCase "a holder describes itself with its connection, agent and directory" <| fun () ->
      let text = Holder.describe siblingOne
      text |> Expect.stringContains "names the agent" "sub-agent-one"
      text |> Expect.stringContains "names the connection" "mcp:shared"
      text |> Expect.stringContains "names the directory" "/work/one"
  ]

[<Tests>]
let namedShapeTests =
  testList "ExpensiveWorkLease.request — named shapes from the brief" [

    testCase "a quiet machine grants immediately" <| fun () ->
      let state', decision = request epoch MemoryPressure.Normal empty ha Kind.Rebuild
      match decision with
      | Decision.Granted(_, expiresAt) ->
        (expiresAt > epoch) |> Expect.isTrue "expiry is in the future"
        state'.Active |> Expect.hasLength "one lease now active" 1
      | other -> failtestf "expected Granted on a quiet machine, got %A" other

    testCase "four leases out at Tight: cap is 1, a fifth agent is queued and told who holds them" <| fun () ->
      let fourActive =
        [ for i in 1 .. 4 ->
            { Id = LeaseId.create ()
              Kind = Kind.SessionCreateOrWarmup
              Holder = Holder.ofConnection (sprintf "agent-%d" i)
              GrantedAt = epoch
              ExpiresAt = epoch.AddMinutes 5.0 } ]
      let state = { empty with Active = fourActive }
      let _, decision = request epoch MemoryPressure.Tight state (Holder.ofConnection "agent-5") Kind.Rebuild
      match decision with
      | Decision.Queued waiting ->
        (waiting.RetryAfter > TimeSpan.Zero) |> Expect.isTrue "a real, positive retry-after"
        waiting.Position |> Expect.equal "first in line" 1
        waiting.Holding |> Expect.hasLength "every active lease is named as a blocker" 4
        waiting.Cap |> Expect.equal "Tight's cap" 1
        let text = explain epoch decision
        text |> Expect.stringContains "names how many are out" "4 lease(s) active"
        text |> Expect.stringContains "names the pressure" "tight"
      | other -> failtestf "expected Queued — the pool is already over Tight's cap of 1, got %A" other

    testCase "an expired lease is reclaimed, freeing the slot for the next request" <| fun () ->
      let expired =
        { Id = LeaseId.create (); Kind = Kind.Rebuild; Holder = ha; GrantedAt = epoch.AddMinutes -20.0; ExpiresAt = epoch.AddMinutes -10.0 }
      let state = { empty with Active = [ expired ] }
      let state', decision = request epoch MemoryPressure.Tight state hb Kind.Rebuild
      match decision with
      | Decision.Granted _ ->
        state'.Active |> List.exists (fun l -> l.Holder = ha) |> Expect.isFalse "the expired lease is gone"
        state'.Active |> List.exists (fun l -> l.Holder = hb) |> Expect.isTrue "the new one is granted"
      | other -> failtestf "expected the expired lease to be reclaimed and the new request Granted, got %A" other

    testCase "a holder that holds a lease and asks for another kind is refused and told which lease is its own" <| fun () ->
      let held, first = request epoch MemoryPressure.Normal empty ha Kind.FullBuild
      let id = grantedId first
      let _, second = request (epoch.AddSeconds 5.0) MemoryPressure.Normal held ha Kind.TestSuiteRun
      match second with
      | Decision.Refused(Refusal.HoldsOtherKind(lease, asked)) ->
        lease.Id |> Expect.equal "it names the lease the caller already holds" id
        asked |> Expect.equal "and what was asked for" Kind.TestSuiteRun
        let text = explain (epoch.AddSeconds 5.0) second
        text |> Expect.stringContains "gives the lease id to release" (LeaseId.value id)
        text |> Expect.stringContains "says what to do" "release_work_lease"
      | other -> failtestf "expected the second kind to be refused with the holder's own lease, got %A" other
      let _, other = request epoch MemoryPressure.Normal held hb Kind.TestSuiteRun
      match other with
      | Decision.Granted _ -> ()
      | unexpected -> failtestf "agent-b holds nothing and Normal has room, expected Granted, got %A" unexpected

    testCase "a holder with a different kind already queued is refused until that ask resolves" <| fun () ->
      let s1, _ = request epoch MemoryPressure.Critical empty ha Kind.Rebuild
      let _, second = request (epoch.AddSeconds 1.0) MemoryPressure.Critical s1 ha Kind.FullBuild
      match second with
      | Decision.Refused(Refusal.OtherKindQueued(queued, asked)) ->
        queued |> Expect.equal "names the queued kind" Kind.Rebuild
        asked |> Expect.equal "and the asked kind" Kind.FullBuild
      | other -> failtestf "expected a refusal naming the other queued kind, got %A" other
  ]

[<Tests>]
let refusalNamesTheHolderTests =
  testList "ExpensiveWorkLease — a refusal names the real holder" [

    testCase "WHY — a sibling on the same connection with another agent name is queued behind the real holder, never told 'you already hold'" <| fun () ->
      // Cap 1 (Tight) so the second sub-agent has to wait for the first.
      let held, first = request epoch MemoryPressure.Tight empty siblingOne Kind.FullBuild
      let _, second = request (epoch.AddSeconds 30.0) MemoryPressure.Tight held siblingTwo Kind.FullBuild
      match second with
      | Decision.Queued waiting ->
        waiting.Position |> Expect.equal "first in line behind the holder" 1
        match waiting.Holding with
        | [ lease ] ->
          lease.Holder |> Expect.equal "the named holder is sub-agent one, not the caller" siblingOne
          lease.Id |> Expect.equal "and it is the lease that was granted" (grantedId first)
        | other -> failtestf "expected exactly the one real holder, got %A" other
      | other -> failtestf "expected the sibling to queue, got %A" other
      let text = explain (epoch.AddSeconds 30.0) second
      text |> Expect.stringContains "names the holding agent" "sub-agent-one"
      text |> Expect.stringContains "names its connection" "mcp:shared"
      text |> Expect.stringContains "names its working directory" "/work/one"
      text |> Expect.stringContains "names the kind of work" "full dotnet build"
      text |> Expect.stringContains "says when it was granted" "granted 00:00:00Z"
      text |> Expect.stringContains "says when it lapses" "expires"
      text |> Expect.stringContains "says the caller's place in line" "#1"
      text |> Expect.stringContains "says to wait" "Ask again"
      text |> Expect.stringContains "says what cannot be done" "cannot release"
      text.Contains "you already hold" |> Expect.isFalse "never the old wording that blamed the caller"

    testCase "a queued caller is told how many asks are ahead and who they are" <| fun () ->
      let s1, _ = request epoch MemoryPressure.Tight empty ha Kind.Rebuild
      let s2, _ = request epoch MemoryPressure.Tight s1 hb Kind.Rebuild
      let _, third = request epoch MemoryPressure.Tight s2 hc Kind.Rebuild
      match third with
      | Decision.Queued waiting ->
        waiting.Position |> Expect.equal "third caller is second in line" 2
        waiting.Ahead |> List.map (fun q -> q.Holder) |> Expect.equal "agent-b asked first" [ hb ]
        explain epoch third |> Expect.stringContains "says the position" "#2"
      | other -> failtestf "expected Queued, got %A" other

    testCase "at Critical with nobody holding anything the wait is about pressure, not a holder" <| fun () ->
      let _, decision = request epoch MemoryPressure.Critical empty ha Kind.Rebuild
      match decision with
      | Decision.Queued waiting ->
        waiting.Holding |> Expect.isEmpty "no lease is blocking, so none is named"
        waiting.Cap |> Expect.equal "Critical admits nothing new" 0
        explain epoch decision |> Expect.stringContains "says why" "critical"
      | other -> failtestf "expected Queued, got %A" other
  ]

[<Tests>]
let idempotenceTests =
  testList "ExpensiveWorkLease — the same holder asking again" [

    testCase "WHY — the same connection, agent and directory asking again for the same kind gets its own lease back" <| fun () ->
      let held, first = request epoch MemoryPressure.Normal empty siblingOne Kind.FullBuild
      let id = grantedId first
      let later = epoch.AddMinutes 3.0
      let held', again = request later MemoryPressure.Normal held siblingOne Kind.FullBuild
      match again with
      | Decision.AlreadyHeld lease ->
        lease.Id |> Expect.equal "the same lease id" id
        held'.Active |> Expect.hasLength "no second lease was taken" 1
        lease.ExpiresAt |> Expect.equal "asking again does not renew it" (epoch + Kind.defaultTtl Kind.FullBuild)
        explain later again |> Expect.stringContains "says it is the holder's own" "already hold"
      | other -> failtestf "expected AlreadyHeld, got %A" other

    testCase "asking again over and over never pushes the expiry out" <| fun () ->
      let held, first = request epoch MemoryPressure.Normal empty ha Kind.TestSuiteRun
      ignore (grantedId first)
      let expiry = (List.head held.Active).ExpiresAt
      let final =
        [ 1 .. 50 ]
        |> List.fold (fun s i -> fst (request (epoch.AddMinutes(float i * 0.2)) MemoryPressure.Normal s ha Kind.TestSuiteRun)) held
      (List.head final.Active).ExpiresAt |> Expect.equal "the lease still lapses when it first said it would" expiry
  ]

[<Tests>]
let fairnessTests =
  testList "ExpensiveWorkLease — fairness and expiry" [

    testCase "queue is FIFO by arrival: the first to ask is the first granted when a slot frees" <| fun () ->
      let s0 = empty
      let s1, dA = request epoch MemoryPressure.Tight s0 ha Kind.Rebuild
      match dA with Decision.Granted _ -> () | other -> failtestf "expected agent-a granted, got %A" other
      let s2, dB = request epoch MemoryPressure.Tight s1 hb Kind.Rebuild
      match dB with Decision.Queued _ -> () | other -> failtestf "expected agent-b to wait, got %A" other
      let s3, dC = request epoch MemoryPressure.Tight s2 hc Kind.Rebuild
      match dC with Decision.Queued _ -> () | other -> failtestf "expected agent-c to wait, got %A" other
      let leaseA = grantedId dA
      let s4, releaseOutcome = release leaseA s3
      releaseOutcome |> Expect.equal "agent-a's lease was live" ReleaseOutcome.Released
      let s5, dBRetry = request (epoch.AddSeconds 1.0) MemoryPressure.Tight s4 hb Kind.Rebuild
      match dBRetry with Decision.Granted _ -> () | other -> failtestf "agent-b asked first — expected Granted, got %A" other
      let _, dCRetry = request (epoch.AddSeconds 1.0) MemoryPressure.Tight s5 hc Kind.Rebuild
      match dCRetry with
      | Decision.Queued _ -> ()
      | other -> failtestf "agent-c asked second and the slot just went to agent-b — expected Queued, got %A" other

    testCase "a retrying holder does not create a duplicate queue entry" <| fun () ->
      let s1, _ = request epoch MemoryPressure.Critical empty ha Kind.Rebuild
      let s2, _ = request (epoch.AddSeconds 5.0) MemoryPressure.Critical s1 ha Kind.Rebuild
      s2.Queue |> Expect.hasLength "still one queue entry for agent-a" 1

    testCase "WHY — a waiter that stops asking loses its place, so a crashed agent at the front cannot hold up everyone behind it" <| fun () ->
      let s1, _ = request epoch MemoryPressure.Tight empty ha Kind.Rebuild
      let s2, _ = request epoch MemoryPressure.Tight s1 hb Kind.Rebuild // queued first, then never asks again
      let s3, _ = request epoch MemoryPressure.Tight s2 hc Kind.Rebuild // queued second
      let leaseA = (List.head s3.Active).Id
      let stillAsking = epoch + Timeouts.leaseAskStaleAfter - FixtureDurations.insideTheBoundary
      let s3', _ = request stillAsking MemoryPressure.Tight s3 hc Kind.Rebuild // agent-c keeps asking
      let s4, _ = release leaseA s3'
      let muchLater = epoch + Timeouts.leaseAskStaleAfter + FixtureDurations.pastTheBoundary
      let s5, decision = request muchLater MemoryPressure.Tight s4 hc Kind.Rebuild
      match decision with
      | Decision.Granted _ -> ()
      | other -> failtestf "agent-b vanished long ago — agent-c should be granted, got %A" other
      s5.Queue |> Expect.isEmpty "the stale ask is gone from the queue"

    testCase "release makes the lease immediately unavailable" <| fun () ->
      let state, decision = request epoch MemoryPressure.Normal empty ha Kind.FullBuild
      let leaseId = grantedId decision
      let released, outcome1 = release leaseId state
      outcome1 |> Expect.equal "the lease was live" ReleaseOutcome.Released
      released.Active |> Expect.isEmpty "released lease is gone"
      let releasedAgain, outcome2 = release leaseId released
      outcome2 |> Expect.equal "a second release of the same id finds nothing left to release" ReleaseOutcome.AlreadyGone
      releasedAgain.Active |> Expect.isEmpty "idempotent"

    testCase "only the connection that holds a lease can release it" <| fun () ->
      let state, decision = request epoch MemoryPressure.Normal empty siblingOne Kind.FullBuild
      let leaseId = grantedId decision
      let _, stranger = releaseOwned "mcp:another-connection" leaseId state
      stranger |> Expect.equal "another connection cannot release it" ReleaseOutcome.AlreadyGone
      let after, owner = releaseOwned siblingOne.Connection leaseId state
      owner |> Expect.equal "the holder's connection can" ReleaseOutcome.Released
      after.Active |> Expect.isEmpty "and it is gone"

    testCase "Critical pressure admits nothing new but does not touch already-active leases" <| fun () ->
      let active = { Id = LeaseId.create (); Kind = Kind.RunApp; Holder = ha; GrantedAt = epoch; ExpiresAt = epoch.AddHours 1.0 }
      let state = { empty with Active = [ active ] }
      let state', decision = request epoch MemoryPressure.Critical state hb Kind.Rebuild
      match decision with
      | Decision.Queued _ -> ()
      | other -> failtestf "Critical's cap is 0 — expected Queued, got %A" other
      state'.Active |> Expect.contains "agent-a's already-granted lease is untouched" active
  ]

/// Three holders, the first one running at Tight (cap 1) and the other two queued behind it in the order b, c. The
/// shape every head-of-line case below starts from.
let private holdingWithTwoQueued () : PoolState * LeaseId =
  let s1, first = request epoch MemoryPressure.Tight empty ha Kind.Rebuild
  let s2, _ = request epoch MemoryPressure.Tight s1 hb Kind.Rebuild
  let s3, _ = request epoch MemoryPressure.Tight s2 hc Kind.Rebuild
  s3, grantedId first

[<Tests>]
let headOfLineTests =
  testList "ExpensiveWorkLease — a waiter that cannot go does not hold up one that can" [

    testCase "WHY — room that no earlier waiter needs is not withheld: a later asker is granted while a live head still waits" <| fun () ->
      // Tight (cap 1): a runs, b is queued first and c second. Pressure eases to Normal (cap 4): there is room for
      // all three, and b has not come back yet. c asking first must not be told to wait for b to ask.
      let s3, _ = holdingWithTwoQueued ()
      let _, decision = request (epoch.AddSeconds 2.0) MemoryPressure.Normal s3 hc Kind.Rebuild
      match decision with
      | Decision.Granted _ -> ()
      | other -> failtestf "there was room for b AND c, so c should be granted, got %A" other

    testCase "fairness — a live head still goes first when only one slot is left" <| fun () ->
      // Normal (cap 4) with three leases out: exactly one slot, and b (earlier) and c (later) both want it.
      let s3, _ = holdingWithTwoQueued ()
      let others =
        [ for i in 1 .. 2 ->
            { Id = LeaseId.create ()
              Kind = Kind.SessionCreateOrWarmup
              Holder = Holder.ofConnection (sprintf "other-%d" i)
              GrantedAt = epoch
              ExpiresAt = epoch.AddMinutes 5.0 } ]
      let crowded = { s3 with Active = s3.Active @ others }
      let at = epoch.AddSeconds 2.0
      let afterC, forC = request at MemoryPressure.Normal crowded hc Kind.Rebuild
      match forC with
      | Decision.Queued waiting -> waiting.Why |> Expect.equal "c waits for b's slot, it does not take it" WaitReason.NotYourTurn
      | other -> failtestf "c is behind a live b and the one slot is b's, expected Queued, got %A" other
      let _, forB = request at MemoryPressure.Normal afterC hb Kind.Rebuild
      match forB with
      | Decision.Granted _ -> ()
      | other -> failtestf "b asked first and the slot is its own, expected Granted, got %A" other

    testCase "WHY — a head that has stopped asking is skipped, not waited for: the asker behind it is granted" <| fun () ->
      // b asked once and is never heard from again (a daemon call that gave up, an agent that went away). a
      // finishes. c is still asking. b's ask has not yet aged out of the queue.
      let s3, leaseA = holdingWithTwoQueued ()
      let s4, _ = release leaseA s3
      let stillQueued = epoch + Timeouts.leaseAskStaleAfter - FixtureDurations.insideTheBoundary
      let _, decision = request stillQueued MemoryPressure.Tight s4 hc Kind.Rebuild
      match decision with
      | Decision.Granted _ -> ()
      | other -> failtestf "b is gone, so c should not be held up behind it, got %A" other

    testCase "a skipped head keeps its place: when it comes back it is served before a later arrival" <| fun () ->
      let s3, leaseA = holdingWithTwoQueued ()
      let s4, _ = release leaseA s3
      let stillQueued = epoch + Timeouts.leaseAskStaleAfter - FixtureDurations.insideTheBoundary
      let s5, forC = request stillQueued MemoryPressure.Tight s4 hc Kind.Rebuild
      let leaseC = grantedId forC
      // b comes back while c runs: it is first in line again, and d, who arrives after it, is second.
      let backAt = stillQueued + FixtureDurations.pastTheBoundary
      let s6, forB' = request backAt MemoryPressure.Tight s5 hb Kind.Rebuild
      (match forB' with
       | Decision.Queued waiting -> waiting.Position |> Expect.equal "b kept its place at the head" 1
       | other -> failtestf "c holds the only slot, expected b queued, got %A" other)
      let hd = Holder.ofConnection "agent-d"
      let s7, forD = request backAt MemoryPressure.Tight s6 hd Kind.Rebuild
      (match forD with
       | Decision.Queued waiting -> waiting.Position |> Expect.equal "d is behind b, who was here first" 2
       | other -> failtestf "expected d queued behind b, got %A" other)
      let s8, _ = release leaseC s7
      let _, forD' = request backAt MemoryPressure.Tight s8 hd Kind.Rebuild
      (match forD' with
       | Decision.Queued _ -> ()
       | other -> failtestf "b is live and first, d must not take its slot, got %A" other)
      let _, forB = request backAt MemoryPressure.Tight s8 hb Kind.Rebuild
      match forB with
      | Decision.Granted _ -> ()
      | other -> failtestf "b kept its place and the slot is free, expected Granted, got %A" other
  ]

[<Tests>]
let snapshotTests =
  testList "ExpensiveWorkLease.snapshot — what get_daemon_status shows" [
    testCase "WHY — two sub-agents on one connection are two rows, each attributable" <| fun () ->
      let s1, _ = request epoch MemoryPressure.Normal empty siblingOne Kind.FullBuild
      let s2, _ = request epoch MemoryPressure.Normal s1 siblingTwo Kind.TestSuiteRun
      let view = snapshot (epoch.AddSeconds 10.0) s2
      view.ActiveCount |> Expect.equal "two leases" 2
      view.Active |> List.map (fun row -> row.AgentName) |> List.sort |> Expect.equal "both agents are named" [ "sub-agent-one"; "sub-agent-two" ]
      view.Active |> List.map (fun row -> row.WorkingDirectory) |> List.sort |> Expect.equal "both directories are named" [ "/work/one"; "/work/two" ]
      view.Active |> List.forall (fun row -> row.Connection = "mcp:shared") |> Expect.isTrue "on the same connection"
      view.Active |> List.forall (fun row -> row.ExpiresInSeconds > 0) |> Expect.isTrue "each says how long it has left"

    testCase "a lease past its expiry is not shown" <| fun () ->
      let s1, _ = request epoch MemoryPressure.Normal empty ha Kind.SessionCreateOrWarmup
      let view = snapshot (epoch + Kind.defaultTtl Kind.SessionCreateOrWarmup + FixtureDurations.pastTheBoundary) s1
      view.ActiveCount |> Expect.equal "lapsed" 0
  ]

[<Tests>]
let neverExpiresTwinTests =
  testList "ExpensiveWorkLease.requestNeverExpiresTwin — reproduces the deadlock" [

    testCase "REPRODUCED — an abandoned lease under the twin blocks the pool forever" <| fun () ->
      let state, decision = requestNeverExpiresTwin epoch MemoryPressure.Tight empty (Holder.ofConnection "crashed-agent") Kind.Rebuild
      match decision with Decision.Granted _ -> () | other -> failtestf "expected the crashed agent granted first, got %A" other
      let farFuture = epoch.AddHours 100.0
      let _, laterDecision = requestNeverExpiresTwin farFuture MemoryPressure.Tight state hb Kind.Rebuild
      match laterDecision with
      | Decision.Queued _ -> ()
      | other -> failtestf "expected the twin to deadlock agent-b behind the abandoned lease, got %A" other

    testCase "the REAL request reclaims the identical abandoned lease instead" <| fun () ->
      let state, decision = request epoch MemoryPressure.Tight empty (Holder.ofConnection "crashed-agent") Kind.Rebuild
      match decision with Decision.Granted _ -> () | other -> failtestf "expected granted first, got %A" other
      let farFuture = epoch.AddHours 100.0
      let _, laterDecision = request farFuture MemoryPressure.Tight state hb Kind.Rebuild
      match laterDecision with
      | Decision.Granted _ -> ()
      | other -> failtestf "the real request must reclaim the expired lease and grant agent-b, got %A" other
  ]
