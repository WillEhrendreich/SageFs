/// Slice 1 of cohort-integration-plan.md: `CohortOwner` is the shell actor
/// around the pure `Cohort.decide`/`replay` core (Phase 1 item 7). These
/// tests cover the owner's own contract — command application publishes a
/// read frame, every accepted command lands one dense ledger entry, a fresh
/// owner replaying the same ledger reconstructs an identical frame (the
/// "ledger is the source of truth" property), and a refused command changes
/// neither the frame nor the ledger.
module SageFs.Tests.CohortOwnerTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// A deterministic clock so ledger/frame equality checks across two owner
/// instances don't depend on wall-clock timing.
let private fixedClock (at: DateTime) : unit -> DateTime = fun () -> at

/// Deterministic, distinct entropy per call — real CohortOwner.decide calls
/// mint claim/landing ids from this, so distinct values matter for realistic
/// coverage even though these tests don't inspect minted ids directly.
let private counterEntropy () : unit -> byte[] =
  let mutable n = 0
  fun () ->
    let bytes = BitConverter.GetBytes n
    n <- n + 1
    bytes

let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

/// The owner is started against one scope, and every command committed to it must
/// name that same scope or it is refused `WrongCohortScope`. These tests have no
/// session and no working directory, and the daemon's own `startWithPerformer`
/// call passes `SageFs.CohortScope.Machine`, so these owners are machine-wide.
let private machine = SageFs.CohortScope.Machine

[<Tests>]
let cohortOwnerTests =
  testList "CohortOwner" [

    testTask "joining publishes the member in the read frame" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      let! result = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
      result |> Result.isOk |> Expect.isTrue "the join is accepted"
      owner.ReadFrame().MemberIds
      |> Array.contains alice
      |> Expect.isTrue "the member appears in the frame"
    }

    testTask "N accepted commands append N dense, increasing ledger entries" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
      let! _ = owner.Commit(CohortCommand.Join(bob, JoinableRole.Verifier, None, machine))
      let! _ = owner.Commit(CohortCommand.RenewLease(alice, machine))
      let entries = ledger.ReadAll machine
      entries |> List.length |> Expect.equal "three accepted commands, three entries" 3
      entries
      |> List.map (fun e -> e.Seq)
      |> Expect.equal "seq is dense and increasing from 0" [ 0L<ledgerSeq>; 1L<ledgerSeq>; 2L<ledgerSeq> ]
    }

    testTask "a fresh owner replaying the same ledger reconstructs an identical frame" {
      let ledger = InMemory.create<MemberId> ()
      use ownerA = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      let! _ = ownerA.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
      let! _ = ownerA.Commit(CohortCommand.Join(bob, JoinableRole.Verifier, None, machine))
      let! _ = ownerA.Commit(CohortCommand.AcquireClaim(alice, ClaimScope.File "A.fs", "working on A", machine))
      let frameA = ownerA.ReadFrame()
      use ownerB = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      let frameB = ownerB.ReadFrame()
      frameB |> Expect.equal "replay over the same ledger reconstructs an identical frame" frameA
    }

    testTask "a refused command leaves the frame and the ledger unchanged" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
      let frameBefore = owner.ReadFrame()
      let entriesBefore = ledger.ReadAll machine
      // alice never acquired this claim id, so releasing it is refused.
      let! result = owner.Commit(CohortCommand.ReleaseClaim(alice, ClaimId "nope", 0L<fence>, machine))
      match result with
      | Error(CohortError.UnknownClaim _) -> ()
      | other -> failtestf "expected UnknownClaim, got %A" other
      owner.ReadFrame() |> Expect.equal "the frame is unchanged by a refused command" frameBefore
      ledger.ReadAll machine |> Expect.equal "nothing new was appended for a refused command" entriesBefore
    }

    testTask "a command refused for one member does not let it touch another member's claim" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
      let! _ = owner.Commit(CohortCommand.Join(bob, JoinableRole.Verifier, None, machine))
      let! _ = owner.Commit(CohortCommand.AcquireClaim(alice, ClaimScope.File "Shared.fs", "purpose", machine))
      let claimId =
        owner.ReadFrame().ClaimIds
        |> Array.tryHead
        |> Option.defaultWith (fun () -> failtest "expected one claim to exist")
      let fence = owner.ReadFrame().ClaimFence.[0]
      let! result = owner.Commit(CohortCommand.ReleaseClaim(bob, claimId, fence, machine))
      match result with
      | Error(CohortError.NotClaimHolder(errClaimId, requester)) ->
        errClaimId |> Expect.equal "the refusal names the contested claim" claimId
        requester |> Expect.equal "the refusal names the non-holder who tried" bob
      | other -> failtestf "expected NotClaimHolder, got %A" other
      let frameAfter = owner.ReadFrame()
      let aliceIndex = frameAfter.MemberIds |> Array.findIndex (fun m -> m = alice)
      frameAfter.ClaimHolderIndex.[0]
      |> Expect.equal "the claim is still held by alice, not bob" aliceIndex
    }

    testTask "WHY — a member joined WITH a session shows that session's outcomes in the frame (item 13c)" {
      let ledger = InMemory.create<MemberId> ()
      let t1 = Cohort.TestId "t1"
      let t2 = Cohort.TestId "t2"
      let outcomesFor (sid: string) : CohortOwner.SessionTestOutcomes =
        if sid = "sess-1" then [ t1 ], [ t2 ], [], 7L else [], [], [], 0L
      use owner = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) outcomesFor
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, Some "sess-1", machine))
      let frame = owner.ReadFrame()
      frame.TestIds |> Expect.equal "TestIds is the distinct, sorted union of every bound session's tests" [| t1; t2 |]
      frame.SessionGens |> Expect.equal "SessionGens carries the joined member's session generation" [| 7L |]
      frame.Pass.[0] |> Expect.equal "the member's row passes t1, not t2" [| true; false |]
      frame.Fail.[0] |> Expect.equal "the member's row fails t2, not t1" [| false; true |]
      frame.Stale.[0] |> Expect.equal "the member's row has no stale tests" [| false; false |]
    }

    testTask "WHY — a member joined WITHOUT a session contributes no row to the matrix" {
      let ledger = InMemory.create<MemberId> ()
      let t1 = Cohort.TestId "should-never-appear"
      // If frameOf ever attributed a row to a session-less member, this
      // outcome would leak into the frame even though nothing bound "sess-1".
      let outcomesFor (_: string) : CohortOwner.SessionTestOutcomes = [ t1 ], [], [], 1L
      use owner = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) outcomesFor
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
      owner.ReadFrame().TestIds
      |> Expect.equal "no session means no row, so no tests appear" [||]
    }

    testTask "getSessionTestOutcomes is re-read on every applied command, not cached from startup" {
      let ledger = InMemory.create<MemberId> ()
      let t1 = Cohort.TestId "t1"
      let live: (Cohort.TestId list * Cohort.TestId list * Cohort.TestId list * int64) ref = ref ([], [], [], 0L)
      use owner = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> live.Value)
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, Some "sess-1", machine))
      owner.ReadFrame().TestIds
      |> Expect.equal "no outcomes were supplied at startup" [||]
      live.Value <- [ t1 ], [], [], 1L
      let! _ = owner.Commit(CohortCommand.Join(bob, JoinableRole.Verifier, Some "sess-2", machine))
      owner.ReadFrame().TestIds
      |> Expect.equal "the next applied command re-reads getSessionTestOutcomes and picks up the new value" [| t1 |]
    }

    testTask "WHY — replay preserves the joined member's session (item 13c)" {
      let ledger = InMemory.create<MemberId> ()
      use ownerA = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      let! _ = ownerA.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, Some "sess-1", machine))
      // A fresh owner over the same ledger must reconstruct the SAME
      // Session-bound member — proving `replay` (which folds `decide` over
      // the recorded commands, Cohort.fs) round-trips `MemberRecord.Session`
      // via the ledger alone, not via any in-memory state carried forward.
      let t1 = Cohort.TestId "t1"
      let outcomesFor (sid: string) : CohortOwner.SessionTestOutcomes =
        if sid = "sess-1" then [ t1 ], [], [], 3L else [], [], [], 0L
      use ownerB = CohortOwner.start silentLogger machine ledger (fixedClock epoch) (counterEntropy ()) outcomesFor
      ownerB.ReadFrame().TestIds
      |> Expect.equal "replay reconstructed alice's session binding, so her outcomes populate the frame" [| t1 |]
    }

    // ── What the owner says about a ledger it could not replay cleanly ──────────────────────────
    //
    // `replayIn` used to skip any row `decide` now refuses and `startCore` said nothing about it, so a daemon could
    // start from a state missing rows its ledger holds with no trace anywhere. Both cases below are rows written by
    // an EARLIER build, which is the only way a ledger gets into this shape.

    testTask "WHY — a ledger row decide now refuses is logged once at Warning with its sequence number and the refusal" {
      let ledger = InMemory.create<MemberId> ()
      let join = CohortCommand.Join(alice, JoinableRole.Implementer, None, machine)
      let joinEvents =
        match decide epoch [||] (CohortState.empty ()) join with
        | Ok(_, events, _) -> events
        | Error refusal -> failtestf "setup: the join was refused: %A" refusal
      ledger.Append { Seq = 0L<ledgerSeq>; Clock = epoch; Entropy = [||]; Command = join; Events = joinEvents }
      // Releasing a claim that never existed: decide refuses it, as it would refuse any row it no longer accepts.
      ledger.Append
        { Seq = 1L<ledgerSeq>; Clock = epoch.AddMinutes 1.; Entropy = [||]
          Command = CohortCommand.ReleaseClaim(alice, ClaimId "ghost", 0L<fence>, machine); Events = [] }
      let warnings = System.Collections.Concurrent.ConcurrentQueue<string>()
      let capturing =
        { new SageFs.Utils.ILogger with
            member _.LogInfo _ = ()
            member _.LogDebug _ = ()
            member _.LogWarning message = warnings.Enqueue message
            member _.LogError _ = () }
      use owner = CohortOwner.start capturing machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      owner.ReadFrame().MemberIds
      |> Array.contains alice
      |> Expect.isTrue "the owner still starts, from the rows that did apply"
      let logged = warnings.ToArray()
      logged |> Array.length |> Expect.equal "exactly one warning, for the one dropped row" 1
      logged.[0] |> Expect.stringContains "it names the sequence number" "seq=1"
      logged.[0] |> Expect.stringContains "it names the refusal" "UnknownClaim"
    }

    testTask "WHY — a ledger recorded under a different lease window is reported as one Warning, and the owner still starts" {
      let ledger = InMemory.create<MemberId> ()
      let join = CohortCommand.Join(alice, JoinableRole.Implementer, None, machine)
      let joinEvents =
        match decide epoch [||] (CohortState.empty ()) join with
        | Ok(_, events, _) -> events
        | Error refusal -> failtestf "setup: the join was refused: %A" refusal
      ledger.Append { Seq = 0L<ledgerSeq>; Clock = epoch; Entropy = [||]; Command = join; Events = joinEvents }
      // Twenty minutes later alice is within today's 30 minute window. This row says she lapsed, which is what a
      // 10 minute window would have recorded: the ledger holds no policy constants, only what decide returned.
      let tickAt = epoch.AddMinutes 20.
      ledger.Append
        { Seq = 1L<ledgerSeq>; Clock = tickAt; Entropy = [||]; Command = CohortCommand.Tick machine
          Events = [ CohortEvent.MemberDeparted(alice, tickAt) ] }
      let warnings = System.Collections.Concurrent.ConcurrentQueue<string>()
      let capturing =
        { new SageFs.Utils.ILogger with
            member _.LogInfo _ = ()
            member _.LogDebug _ = ()
            member _.LogWarning message = warnings.Enqueue message
            member _.LogError _ = () }
      use owner = CohortOwner.start capturing machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      owner.ReadFrame().MemberIds
      |> Array.contains alice
      |> Expect.isTrue "the owner starts from what decide says today, where alice has not lapsed"
      let logged = warnings.ToArray()
      logged |> Array.length |> Expect.equal "one warning, however many rows differ" 1
      logged.[0] |> Expect.stringContains "it says the recorded events are no longer reproduced" "no longer reproduces"
      logged.[0] |> Expect.stringContains "it names the first row" "seq 1 to 1"
    }

    testTask "an honest ledger replays in silence" {
      let ledger = InMemory.create<MemberId> ()
      let warnings = System.Collections.Concurrent.ConcurrentQueue<string>()
      let capturing =
        { new SageFs.Utils.ILogger with
            member _.LogInfo _ = ()
            member _.LogDebug _ = ()
            member _.LogWarning message = warnings.Enqueue message
            member _.LogError _ = () }
      use first = CohortOwner.start capturing machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      let! _ = first.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
      let! _ = first.Commit(CohortCommand.AcquireClaim(alice, ClaimScope.File "A.fs", "working on A", machine))
      use second = CohortOwner.start capturing machine ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L))
      second.ReadFrame() |> Expect.equal "the second owner holds what the first wrote" (first.ReadFrame())
      warnings.ToArray() |> Expect.equal "nothing was dropped or diverged, so nothing was logged" [||]
    }
  ]
