/// Cohort keystone P2 (cohort-member-identity-as-capability.md H1): proves
/// cohort coordination correct UNDER REAL CONCURRENCY — something no
/// deterministic pure fold (CohortLandingSimTests.fs's DST) can do, because
/// DST folds one scripted order by construction. Here a burst of ops is
/// issued CONCURRENTLY to the real `CohortOwner` actor (its mailbox is the
/// only serialization point, per `CohortOwner.fs`'s doc comment), and
/// `SageFs.Simulation.Linearizability.isLinearizable` (pure permutation
/// search over the real `Cohort.decide`) checks that the observed outcome
/// corresponds to SOME sequential order.
///
/// The keystone (H1): two DISTINCT members concurrently `AcquireClaim` on
/// OVERLAPPING scope. Exclusivity requires exactly one Held / one refused —
/// under real concurrency, not just in a single-threaded DST fold. A
/// deliberately-BROKEN twin (§4, "the teeth") applies `decide`'s check
/// against a per-thread snapshot and writes its own delta into a SHARED
/// dictionary without a mailbox serializing the check-then-write — a
/// textbook TOCTOU lost-update race — and is proven to fail linearizability,
/// so this harness is shown to catch a REAL race, not pass vacuously.
module SageFs.Tests.CohortLinearizabilityTests

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger
open SageFs.Simulation.Linearizability

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private fixedClock (at: DateTime) : unit -> DateTime = fun () -> at

/// Deterministic, distinct entropy per applied command — `CohortOwnerTests`'s
/// helper. Because the real owner calls this in MAILBOX ARRIVAL order (not
/// per-op), the entropy any given op actually gets depends on the race —
/// exactly the thing we don't know ahead of time. We read it back from the
/// ledger AFTER the burst settles (`entropyOfFromLedger` below) rather than
/// trying to predict it, so the checker's per-op entropy is always the exact
/// bytes `decide` actually used for that op in the real run.
let private counterEntropy () : unit -> byte[] =
  let mutable n = 0
  fun () ->
    let bytes = BitConverter.GetBytes n
    n <- n + 1
    bytes

let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"
let private carol = MemberId.Minted "carol"
let private dave = MemberId.Minted "dave"

/// Every distinct member joins, sequentially (never concurrently — setup is
/// not the subject), the first becoming conductor per v1 semantics.
let private joinAll (owner: CohortOwner.Handle) (members: MemberId list) : Task<unit> =
  task {
    for who in members do
      let! _ = owner.Commit(CohortCommand.Join(who, JoinableRole.Implementer, None))
      ()
  }

/// After a burst settles, recover the EXACT entropy bytes `decide` used for
/// each op from the ledger (`LedgerEntry.Entropy`), matched by exact command
/// equality — sound as long as the burst's ops carry distinct `CohortCommand`
/// values (distinct requester and/or distinct purpose/statement text), which
/// every scenario below ensures.
let private entropyOfFromLedger
  (ledger: LedgerPort<MemberId>)
  (ops: (OpId * CohortCommand<MemberId>) list)
  : OpId -> Entropy =
  let entries = ledger.ReadAll()
  let map =
    ops
    |> List.map (fun (opId, cmd) ->
      // A REFUSED op never appends a ledger entry — `CohortOwner.handle`
      // only calls `ledger.Append` on `decide`'s `Ok` branch — so a losing
      // op in the real burst (e.g. H1's ClaimConflict loser) has no
      // recorded entropy to recover. That's fine: entropy is only consumed
      // by `AcquireClaim`/`RequestLanding`'s id-minting step, which the
      // conflict check short-circuits BEFORE reaching whenever this op is
      // ALSO refused in a candidate permutation — and the real arrival
      // order (always a valid witness, since the WINNER's real entropy IS
      // recoverable) never needs the loser's entropy at all, because the
      // loser stays refused in that exact order too. A permutation where
      // this op hypothetically wins instead gets a placeholder-minted id
      // that may not match `observed.Final` — but that permutation isn't
      // the order that actually happened, so a spurious non-match there
      // costs nothing; the search only needs ONE witness.
      let entropy =
        entries
        |> List.tryFind (fun e -> e.Command = cmd)
        |> Option.map (fun e -> e.Entropy)
        |> Option.defaultValue (BitConverter.GetBytes opId)
      opId, entropy)
    |> Map.ofList
  fun opId -> map.[opId]

/// Fire every op's `Commit` at (as close to) the same instant as `Task.Run`
/// scheduling allows, and collect each op's own accept/refuse result plus
/// the final settled state — the "what the real run produced" the checker
/// needs.
let private fireBurstAgainstRealOwner
  (owner: CohortOwner.Handle)
  (ledger: LedgerPort<MemberId>)
  (ops: (OpId * CohortCommand<MemberId>) list)
  : Task<Observed<MemberId> * (OpId -> Entropy)> =
  task {
    let! results =
      ops
      |> List.map (fun (opId, cmd) ->
        // `Task.Run` (not a plain `let! ` chain) so the actual `mailbox.Post`
        // each `Commit` performs happens from genuinely independent
        // thread-pool threads racing to reach the mailbox — a `let!` chain
        // built via `List.map` would post them back-to-back, in list order,
        // from the SAME calling thread, which is not a real race at all.
        Task.Run<OpId * Result<unit, CohortError<MemberId>>>(fun () ->
          task {
            let! result = owner.Commit(cmd)
            return opId, Result.map ignore result
          }))
      |> Task.WhenAll
    do! owner.Flush()
    let observed = { PerOp = Map.ofArray results; Final = owner.ReadCohortState() }
    let entropyOf = entropyOfFromLedger ledger ops
    return observed, entropyOf
  }

// ── The BROKEN twin (§4 — the teeth) ──────────────────────────────────────

/// Applies `Cohort.decide`'s OWN check against a per-thread SNAPSHOT of a
/// shared claims dictionary, then writes only its own newly-minted claim(s)
/// into that SAME shared dictionary — no mailbox, no lock across the
/// check-then-write. A `System.Threading.Barrier` forces every thread to
/// take its snapshot before ANY thread writes, so the lost-update race —
/// two overlapping `AcquireClaim`s both passing the "no conflict" check
/// against the identical pre-race snapshot, then both landing their claim —
/// is reproduced DETERMINISTICALLY every run, not left to OS scheduling
/// luck. This is the realistic shape of the bug class the roast calls
/// "check-then-act TOCTOU" (duplicate-session creation, `Mcp.fs:1881`):
/// the CHECK runs against a snapshot, but the WRITE lands independently of
/// it, so two racing writers can both pass a check that was only ever true
/// for one of them.
let private runRacyTwin
  (clock: Clock)
  (initial: CohortState<MemberId>)
  (ops: (OpId * Entropy * CohortCommand<MemberId>) list)
  : Task<Map<OpId, Result<unit, CohortError<MemberId>>> * CohortState<MemberId>> =
  task {
    let claimsDict = ConcurrentDictionary<ClaimId, Claim<MemberId>>()
    for KeyValue(cid, claim) in initial.Claims do
      claimsDict.[cid] <- claim
    let results = ConcurrentDictionary<OpId, Result<unit, CohortError<MemberId>>>()
    let fences = ConcurrentBag<int64<fence>>()
    let barrier = new Barrier(List.length ops)
    let run (opId: OpId, entropy: Entropy, cmd: CohortCommand<MemberId>) =
      Task.Run(fun () ->
        // Phase 1: every racing thread arrives here before any proceeds.
        barrier.SignalAndWait()
        let snapshot =
          { initial with
              Claims = claimsDict |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq }
        // Phase 2: every thread has taken ITS snapshot before any thread
        // writes — this is what makes the lost update deterministic rather
        // than a matter of timing luck.
        barrier.SignalAndWait()
        match decide clock entropy snapshot cmd with
        | Ok(newState, _events, _effects) ->
          for KeyValue(cid, claim) in newState.Claims do
            if not (Map.containsKey cid snapshot.Claims) then
              claimsDict.[cid] <- claim
              fences.Add claim.Fence
          results.[opId] <- Ok()
        | Error err -> results.[opId] <- Error err)
    do! ops |> List.map run |> Task.WhenAll
    let finalFence = fences |> Seq.fold max initial.NextFence
    let final =
      { initial with
          Claims = claimsDict |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
          NextFence = finalFence }
    return (results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq), final
  }

[<Tests>]
let cohortLinearizabilityTests =
  testList "CohortLinearizability" [

    // ── Step 2: the concurrency driver, on independent (non-conflicting)
    //    ops — every accepted op should be explainable by some order. ──────

    testTask "WHY — a burst of independent (non-overlapping) concurrent ops on the real CohortOwner is linearizable" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      do! joinAll owner [ alice; bob; carol ]
      let initial = owner.ReadCohortState()

      let ops : (OpId * CohortCommand<MemberId>) list =
        [ 0, CohortCommand.AcquireClaim(alice, ClaimScope.File "A.fs", "op-0 alice claims A.fs")
          1, CohortCommand.AcquireClaim(bob, ClaimScope.File "B.fs", "op-1 bob claims B.fs")
          2, CohortCommand.RenewLease carol ]

      let! observed, entropyOf = fireBurstAgainstRealOwner owner ledger ops

      observed.PerOp
      |> Map.forall (fun _ r -> r |> Result.isOk)
      |> Expect.isTrue "every independent op is accepted regardless of arrival order"

      match isLinearizable initial (fixedClock epoch ()) entropyOf ops observed with
      | LinearizationResult.Linearizable _ -> ()
      | LinearizationResult.NotLinearizable reason -> failtestf "expected linearizable, got: %s" reason
    }

    // ── Step 3: the H1 keystone — exclusivity under real concurrency. ─────

    testTask "H1 KEYSTONE — two members racing AcquireClaim on overlapping scope: exactly one Held, and the outcome is linearizable (200 iterations)" {
      let mutable heldCounts = []
      for iteration in 1 .. 200 do
        let ledger = InMemory.create<MemberId> ()
        use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
        do! joinAll owner [ alice; bob ]
        let initial = owner.ReadCohortState()

        let ops : (OpId * CohortCommand<MemberId>) list =
          [ 0, CohortCommand.AcquireClaim(alice, ClaimScope.File "Shared.fs", sprintf "op-0 alice race #%d" iteration)
            1, CohortCommand.AcquireClaim(bob, ClaimScope.File "Shared.fs", sprintf "op-1 bob race #%d" iteration) ]

        let! observed, entropyOf = fireBurstAgainstRealOwner owner ledger ops

        // (a) exactly one op Held, the other refused with ClaimConflict —
        // never both Held, never both refused.
        let oks = observed.PerOp |> Map.toList |> List.filter (fun (_, r) -> Result.isOk r)
        let errs = observed.PerOp |> Map.toList |> List.filter (fun (_, r) -> Result.isError r)
        oks |> List.length |> Expect.equal (sprintf "iteration %d: exactly one op wins the claim" iteration) 1
        errs |> List.length |> Expect.equal (sprintf "iteration %d: exactly one op is refused" iteration) 1
        match errs with
        | [ (_, Error(CohortError.ClaimConflict _)) ] -> ()
        | other -> failtestf "iteration %d: expected the loser to be refused with ClaimConflict, got %A" iteration other

        let heldClaims =
          observed.Final.Claims
          |> Map.toList
          |> List.filter (fun (_, c) -> match c.State with ClaimState.Held _ -> true | _ -> false)
        heldClaims |> List.length |> Expect.equal (sprintf "iteration %d: exactly one claim is Held in the final state" iteration) 1
        heldCounts <- (List.length heldClaims) :: heldCounts

        // (b) the outcome is linearizable — some sequential order gives
        // exactly this one-Held-one-Conflict shape.
        match isLinearizable initial (fixedClock epoch ()) entropyOf ops observed with
        | LinearizationResult.Linearizable _ -> ()
        | LinearizationResult.NotLinearizable reason -> failtestf "iteration %d: expected linearizable, got: %s" iteration reason

      heldCounts |> List.forall ((=) 1) |> Expect.isTrue "every iteration landed exactly one Held claim"
    }

    // ── Step 4: the teeth — a shell that mutates outside the mailbox MUST
    //    fail linearizability, proving the checker isn't vacuous. ─────────

    testTask "TEETH — a check-then-write shell racing outside the mailbox can grant BOTH overlapping claims, and the checker catches it as NOT linearizable" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      do! joinAll owner [ alice; bob ]
      let initial = owner.ReadCohortState()

      let opsWithEntropy : (OpId * Entropy * CohortCommand<MemberId>) list =
        [ 0, BitConverter.GetBytes 0, CohortCommand.AcquireClaim(alice, ClaimScope.File "Racy.fs", "op-0 alice racy claim")
          1, BitConverter.GetBytes 1, CohortCommand.AcquireClaim(bob, ClaimScope.File "Racy.fs", "op-1 bob racy claim") ]
      let ops = opsWithEntropy |> List.map (fun (id, _, cmd) -> id, cmd)
      let entropyOf = opsWithEntropy |> List.map (fun (id, e, _) -> id, e) |> Map.ofList |> fun m -> fun id -> m.[id]

      let! twinResults, twinFinal = runRacyTwin (fixedClock epoch ()) initial opsWithEntropy

      // The bug the twin reproduces: BOTH ops report Ok, and BOTH claims are
      // Held over the SAME overlapping scope in the final state — an
      // outcome no sequential permutation of two overlapping AcquireClaims
      // can ever produce (the second one in any order must see the first's
      // claim already Held).
      twinResults
      |> Map.forall (fun _ r -> r |> Result.isOk)
      |> Expect.isTrue "WHY the twin is broken: the racy check-then-write shell grants BOTH claims Ok"

      let heldClaims =
        twinFinal.Claims
        |> Map.toList
        |> List.filter (fun (_, c) -> match c.State with ClaimState.Held _ -> true | _ -> false)
      heldClaims |> List.length |> Expect.equal "WHY the twin is broken: BOTH overlapping claims are Held simultaneously" 2

      let observed = { PerOp = twinResults; Final = twinFinal }
      match isLinearizable initial (fixedClock epoch ()) entropyOf ops observed with
      | LinearizationResult.NotLinearizable _ -> ()
      | LinearizationResult.Linearizable order ->
        failtestf "the checker must catch the twin's lost-update race — it should NEVER find a witness order, but found %A" order
    }
  ]
