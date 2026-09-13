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

[<Tests>]
let cohortOwnerTests =
  testList "CohortOwner" [

    testTask "joining publishes the member in the read frame" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun () -> [||])
      let! result = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer))
      result |> Result.isOk |> Expect.isTrue "the join is accepted"
      owner.ReadFrame().MemberIds
      |> Array.contains alice
      |> Expect.isTrue "the member appears in the frame"
    }

    testTask "N accepted commands append N dense, increasing ledger entries" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun () -> [||])
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer))
      let! _ = owner.Commit(CohortCommand.Join(bob, JoinableRole.Verifier))
      let! _ = owner.Commit(CohortCommand.RenewLease alice)
      let entries = ledger.ReadAll ()
      entries |> List.length |> Expect.equal "three accepted commands, three entries" 3
      entries
      |> List.map (fun e -> e.Seq)
      |> Expect.equal "seq is dense and increasing from 0" [ 0L<ledgerSeq>; 1L<ledgerSeq>; 2L<ledgerSeq> ]
    }

    testTask "a fresh owner replaying the same ledger reconstructs an identical frame" {
      let ledger = InMemory.create<MemberId> ()
      use ownerA = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun () -> [||])
      let! _ = ownerA.Commit(CohortCommand.Join(alice, JoinableRole.Implementer))
      let! _ = ownerA.Commit(CohortCommand.Join(bob, JoinableRole.Verifier))
      let! _ = ownerA.Commit(CohortCommand.AcquireClaim(alice, ClaimScope.File "A.fs", "working on A"))
      let frameA = ownerA.ReadFrame()
      use ownerB = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun () -> [||])
      let frameB = ownerB.ReadFrame()
      frameB |> Expect.equal "replay over the same ledger reconstructs an identical frame" frameA
    }

    testTask "a refused command leaves the frame and the ledger unchanged" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun () -> [||])
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer))
      let frameBefore = owner.ReadFrame()
      let entriesBefore = ledger.ReadAll ()
      // alice never acquired this claim id, so releasing it is refused.
      let! result = owner.Commit(CohortCommand.ReleaseClaim(alice, ClaimId "nope", 0L<fence>))
      match result with
      | Error(CohortError.UnknownClaim _) -> ()
      | other -> failtestf "expected UnknownClaim, got %A" other
      owner.ReadFrame() |> Expect.equal "the frame is unchanged by a refused command" frameBefore
      ledger.ReadAll () |> Expect.equal "nothing new was appended for a refused command" entriesBefore
    }

    testTask "a command refused for one member does not let it touch another member's claim" {
      let ledger = InMemory.create<MemberId> ()
      use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun () -> [||])
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer))
      let! _ = owner.Commit(CohortCommand.Join(bob, JoinableRole.Verifier))
      let! _ = owner.Commit(CohortCommand.AcquireClaim(alice, ClaimScope.File "Shared.fs", "purpose"))
      let claimId =
        owner.ReadFrame().ClaimIds
        |> Array.tryHead
        |> Option.defaultWith (fun () -> failtest "expected one claim to exist")
      let fence = owner.ReadFrame().ClaimFence.[0]
      let! result = owner.Commit(CohortCommand.ReleaseClaim(bob, claimId, fence))
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

    testTask "session snapshots supplied by getSessionSnapshots populate the frame's test bitplanes" {
      let ledger = InMemory.create<MemberId> ()
      let t1 = Cohort.TestId "t1"
      let t2 = Cohort.TestId "t2"
      let snapshot: Cohort.SessionSnapshot<MemberId> = {
        Member = None
        SessionId = "sess-1"
        Generation = 7L
        PassingTests = [ t1 ]
        FailingTests = [ t2 ]
        StaleTests = []
      }
      use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun () -> [| snapshot |])
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer))
      let frame = owner.ReadFrame()
      frame.TestIds |> Expect.equal "TestIds is the distinct, sorted union of every session's tests" [| t1; t2 |]
      frame.SessionGens |> Expect.equal "SessionGens carries the supplied session's generation" [| 7L |]
      frame.Pass.[0] |> Expect.equal "session row 0 passes t1, not t2" [| true; false |]
      frame.Fail.[0] |> Expect.equal "session row 0 fails t2, not t1" [| false; true |]
      frame.Stale.[0] |> Expect.equal "session row 0 has no stale tests" [| false; false |]
    }

    testTask "getSessionSnapshots is re-read on every applied command, not cached from startup" {
      let ledger = InMemory.create<MemberId> ()
      let t1 = Cohort.TestId "t1"
      let live: Cohort.SessionSnapshot<MemberId>[] ref = ref [||]
      use owner = CohortOwner.start silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun () -> live.Value)
      owner.ReadFrame().TestIds
      |> Expect.equal "no session snapshots were supplied at startup" [||]
      live.Value <-
        [| { Member = None; SessionId = "sess-1"; Generation = 1L
             PassingTests = [ t1 ]; FailingTests = []; StaleTests = [] } |]
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer))
      owner.ReadFrame().TestIds
      |> Expect.equal "the next applied command re-reads getSessionSnapshots and picks up the new session" [| t1 |]
    }
  ]
