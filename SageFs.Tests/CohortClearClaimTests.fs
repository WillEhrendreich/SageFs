/// The conductor clears the claims a member left behind. A member that goes silent kept its `Held` claims, and the scope
/// stayed shut to everyone else until its 30 minute lease lapsed; an orphaned claim could leave the table only by being
/// handed to a member who was present. `ClearClaim` is the conductor's release of such a claim: the holder must be silent
/// (`Timeouts.cohortHolderSilentAfter`) or gone (orphaned), the fence is compared as `ReleaseClaim` compares it, and the
/// release moves the fence on. These tests drive the real `Cohort.decide` for each rule and each refusal.
module SageFs.Tests.CohortClearClaimTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Cohort
open SageFs.MemberTable

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine

let private alice = "alice"
let private bob = "bob"
let private carol = "carol"

let private entropy = [| 1uy |]
let private otherEntropy = [| 2uy |]

/// The window a holder must be silent for, from its one home.
let private silentAfter = Timeouts.cohortHolderSilentAfter

let private ok (clock: DateTime) (state: CohortState<'m>) (cmd: CohortCommand<'m>) : CohortState<'m> * CohortEvent<'m> list =
  match decide clock entropy state cmd with
  | Ok(s, events, _) -> s, events
  | Error e -> failwithf "expected %A to succeed, got %A" cmd e

let private next (clock: DateTime) (state: CohortState<'m>) (cmd: CohortCommand<'m>) : CohortState<'m> = ok clock state cmd |> fst

let private join who = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)
let private file = ClaimScope.File "SageFs.Core/A.fs"

/// Alice joins first and is the conductor; bob and carol join after her. Bob takes a claim and goes quiet.
let private bobsClaim () =
  let s1 = next epoch (CohortState.empty ()) (join alice)
  let s2 = next epoch s1 (join bob)
  let s3 = next epoch s2 (join carol)
  let s4, events = ok epoch s3 (CohortCommand.AcquireClaim(bob, file, "purpose", machine))
  match events with
  | [ CohortEvent.ClaimAcquired(claimId, _, _, fence) ] -> s4, claimId, fence
  | other -> failwithf "expected one ClaimAcquired, got %A" other

let private refused (clock: DateTime) state cmd =
  match decide clock entropy state cmd with
  | Error e -> e
  | Ok _ -> failwithf "expected %A to be refused" cmd

[<Tests>]
let clearClaimTests =
  testList "The conductor clears the claims a lapsed member left behind" [

    test "a held claim of a member silent for the window is cleared, and the scope is free to everyone else at once" {
      let state, claimId, fence = bobsClaim ()
      let late = epoch + silentAfter
      let after, events = ok late state (CohortCommand.ClearClaim(alice, claimId, fence, machine))
      let released = after.Claims.[claimId]
      released.State |> Expect.equal "the claim is released, by the conductor, at the clock of the clear" (ClaimState.Released(alice, late))
      (released.Fence > fence) |> Expect.isTrue "the fence moved on"
      events
      |> Expect.equal
        "one event: whose claim, how long it had been silent, who cleared it, and the new fence"
        [ CohortEvent.ClaimCleared(claimId, bob, silentAfter, alice, released.Fence) ]
      match decide late otherEntropy after (CohortCommand.AcquireClaim(carol, file, "purpose", machine)) with
      | Ok _ -> ()
      | Error e -> failwithf "carol could not take the scope after the clear: %A" e
    }

    test "a holder that is not silent yet is refused, with how long it has been silent and how long it must be" {
      let state, claimId, fence = bobsClaim ()
      let early = epoch + silentAfter - TimeSpan.FromTicks 1L
      refused early state (CohortCommand.ClearClaim(alice, claimId, fence, machine))
      |> Expect.equal
        "bob was heard from inside the window"
        (CohortError.ClearRefused(claimId, ClearRefusal.HolderActive(bob, silentAfter - TimeSpan.FromTicks 1L, silentAfter)))
    }

    test "a renewal puts the holder back inside the window, so the claim is not clearable" {
      let state, claimId, fence = bobsClaim ()
      let renewed = next (epoch + silentAfter) state (CohortCommand.RenewLease(bob, machine))
      match refused (epoch + silentAfter) renewed (CohortCommand.ClearClaim(alice, claimId, fence, machine)) with
      | CohortError.ClearRefused(_, ClearRefusal.HolderActive(holder, silentFor, _)) ->
        holder |> Expect.equal "the holder named is bob" bob
        silentFor |> Expect.equal "he was heard from at this very moment" TimeSpan.Zero
      | other -> failwithf "expected HolderActive, got %A" other
    }

    test "an orphaned claim is cleared with no present member to hand it to" {
      let state, claimId, _ = bobsClaim ()
      let departed = next epoch state (CohortCommand.Depart(bob, machine))
      let orphanFence = departed.Claims.[claimId].Fence
      let alone = next epoch (next epoch departed (CohortCommand.Depart(carol, machine))) (CohortCommand.RenewLease(alice, machine))
      decide epoch entropy alone (CohortCommand.ReassignClaim(alice, claimId, bob, machine))
      |> Expect.equal "reassigning needs a present member, and bob and carol are gone" (Error(CohortError.MemberNotPresent bob))
      let after, events = ok epoch alone (CohortCommand.ClearClaim(alice, claimId, orphanFence, machine))
      after.Claims.[claimId].State |> Expect.equal "the claim is released" (ClaimState.Released(alice, epoch))
      match events with
      | [ CohortEvent.ClaimCleared(cleared, former, _, by, _) ] ->
        cleared |> Expect.equal "the claim named" claimId
        former |> Expect.equal "bob is the one who left it" bob
        by |> Expect.equal "the conductor cleared it" alice
      | other -> failwithf "expected one ClaimCleared, got %A" other
    }

    test "a member that is not the conductor is refused, whether the holder is silent or not" {
      let state, claimId, fence = bobsClaim ()
      let late = epoch + silentAfter
      refused late state (CohortCommand.ClearClaim(carol, claimId, fence, machine))
      |> Expect.equal "carol is an ordinary member" (CohortError.NotConductor carol)
      refused late state (CohortCommand.ClearClaim(bob, claimId, fence, machine))
      |> Expect.equal "the holder is not the conductor either" (CohortError.NotConductor bob)
      refused late state (CohortCommand.ClearClaim("stranger", claimId, fence, machine))
      |> Expect.equal "someone who never joined has no seat" (CohortError.NotConductor "stranger")
    }

    test "an empty seat is refused as vacant: nobody holds the power to clear" {
      let state, claimId, fence = bobsClaim ()
      let vacant = next epoch state (CohortCommand.Depart(alice, machine))
      match refused (epoch + silentAfter) vacant (CohortCommand.ClearClaim(alice, claimId, fence, machine)) with
      | CohortError.ConductorVacant(Some former, _, _) -> former |> Expect.equal "alice held the seat" alice
      | other -> failwithf "expected ConductorVacant, got %A" other
    }

    test "the old holder's late release is refused as stale: the clear moved the fence" {
      let state, claimId, fence = bobsClaim ()
      let late = epoch + silentAfter
      let after, _ = ok late state (CohortCommand.ClearClaim(alice, claimId, fence, machine))
      refused late after (CohortCommand.ReleaseClaim(bob, claimId, fence, machine))
      |> Expect.equal "bob presents the fence he knew" (CohortError.StaleClaimFence(claimId, fence, after.Claims.[claimId].Fence))
    }

    test "a stale fence on the clear is refused, as it is on a release: the conductor clears the claim it looked at" {
      let state, claimId, fence = bobsClaim ()
      let stale = fence - 1L<Measures.fence>
      refused (epoch + silentAfter) state (CohortCommand.ClearClaim(alice, claimId, stale, machine))
      |> Expect.equal "the fence is checked before the holder's standing" (CohortError.StaleClaimFence(claimId, stale, fence))
    }

    test "an unknown claim and a claim already released are refused with the reason" {
      let state, claimId, fence = bobsClaim ()
      refused epoch state (CohortCommand.ClearClaim(alice, ClaimId "c-nope", fence, machine))
      |> Expect.equal "no such claim" (CohortError.UnknownClaim(ClaimId "c-nope"))
      let released = next epoch state (CohortCommand.ReleaseClaim(bob, claimId, fence, machine))
      refused epoch released (CohortCommand.ClearClaim(alice, claimId, released.Claims.[claimId].Fence, machine))
      |> Expect.equal "bob released it himself" (CohortError.ClearRefused(claimId, ClearRefusal.AlreadyReleased(bob, epoch)))
    }

    test "a command for another cohort is refused WrongCohortScope and clears nothing" {
      let state, claimId, fence = bobsClaim ()
      match decide (epoch + silentAfter) entropy state (CohortCommand.ClearClaim(alice, claimId, fence, CohortScope.Named "elsewhere")) with
      | Error(CohortError.WrongCohortScope _) -> ()
      | other -> failwithf "expected WrongCohortScope, got %A" other
    }

    test "a landing the silent holder had queued is blocked as stale when it comes to land" {
      let state, claimId, fence = bobsClaim ()
      let queued, _, effects =
        match decide epoch [| 7uy |] state (CohortCommand.RequestLanding(bob, [ claimId, fence ], [ "abc123" ], "bob's change", machine)) with
        | Ok result -> result
        | Error e -> failwithf "bob could not queue a landing: %A" e
      let landingId =
        match effects with
        | CohortEffect.Rebase(id, _, _) :: _ -> id
        | other -> failwithf "expected a Rebase effect first, got %A" other
      let late = epoch + silentAfter
      let cleared, _ = ok late queued (CohortCommand.ClearClaim(alice, claimId, fence, machine))
      let rebased = next late cleared (CohortCommand.RebaseCompleted(landingId, Ok "def456", machine))
      let affected = next late rebased (CohortCommand.AffectedComputed(landingId, [], machine))
      let finished = next late affected (CohortCommand.FastForwardCompleted(landingId, "landed-sha", machine))
      match finished.Landings.[landingId].State with
      | LandingState.Blocked(LandingBlocker.StaleClaimFence blocked, NextAction.AwaitConductor) -> blocked |> Expect.equal "the cleared claim" claimId
      | other -> failwithf "expected Blocked(StaleClaimFence), got %A" other
    }

    test "clearing is a fold over the ledger: replay reproduces the state and every recorded event" {
      let state, claimId, fence = bobsClaim ()
      let late = epoch + silentAfter
      let _, events = ok late state (CohortCommand.ClearClaim(alice, claimId, fence, machine))
      // Recorded the way the live owner records: each command is decided over the fold so far and keeps its events.
      let recorded =
        [ epoch, join alice
          epoch, join bob
          epoch, join carol
          epoch, CohortCommand.AcquireClaim(bob, file, "purpose", machine)
          late, CohortCommand.ClearClaim(alice, claimId, fence, machine) ]
        |> List.fold
          (fun (acc: LedgerEntry<string> list, st: CohortState<string>) (clock, command) ->
            let s2, evs = ok clock st command
            let entry =
              { Seq = LanguagePrimitives.Int64WithMeasure(int64 (List.length acc)); Clock = clock; Entropy = entropy; Command = command; Events = evs }
            acc @ [ entry ], s2)
          ([], CohortState.empty ())
        |> fst
      let audit = replayAuditIn machine recorded
      audit.Dropped |> Expect.isEmpty "no row was dropped"
      audit.Diverged |> Expect.isEmpty "no recorded event was lost"
      audit.Head.State.Claims.[claimId].State |> Expect.equal "the replayed claim is released by the conductor" (ClaimState.Released(alice, late))
      (recorded |> List.last).Events |> Expect.equal "the ledger row carries the same event the direct call returned" events
    }
  ]

/// `release_claim` is one tool for both acts. The conductor naming a claim that is not its own is clearing it; everyone
/// else, and a call under a member token, is a member releasing what it names.
[<Tests>]
let releaseOrClearTests =
  testList "release_claim becomes a clear only for the conductor naming another member's claim" [

    test "the conductor naming bob's claim is a clear" {
      let state, claimId, fence = bobsClaim ()
      releaseOrClear state alice claimId fence machine
      |> Expect.equal "a clear" (CohortCommand.ClearClaim(alice, claimId, fence, machine))
    }

    test "a member naming its own claim, or another member's claim, is a release" {
      let state, claimId, fence = bobsClaim ()
      releaseOrClear state bob claimId fence machine
      |> Expect.equal "bob's own claim" (CohortCommand.ReleaseClaim(bob, claimId, fence, machine))
      releaseOrClear state carol claimId fence machine
      |> Expect.equal "carol is not the conductor, so decide answers with whose claim it is" (CohortCommand.ReleaseClaim(carol, claimId, fence, machine))
    }

    test "the conductor naming its own claim is a release, and an unknown claim is the release's refusal" {
      let state, _, _ = bobsClaim ()
      let own, events =
        match decide epoch otherEntropy state (CohortCommand.AcquireClaim(alice, ClaimScope.File "SageFs.Core/B.fs", "purpose", machine)) with
        | Ok(s, evs, _) -> s, evs
        | Error e -> failwithf "alice could not claim: %A" e
      let ownId, ownFence =
        match events with
        | [ CohortEvent.ClaimAcquired(id, _, _, f) ] -> id, f
        | other -> failwithf "expected one ClaimAcquired, got %A" other
      releaseOrClear own alice ownId ownFence machine
      |> Expect.equal "the conductor's own claim is released, not cleared" (CohortCommand.ReleaseClaim(alice, ownId, ownFence, machine))
    }
  ]

[<Tests>]
let clearClaimProperties =
  testList "ClearClaim properties" [

    testProperty "a clear is accepted exactly when the holder has been silent for the window" <| fun (silentSeconds: uint16) ->
      let state, claimId, fence = bobsClaim ()
      let silent = TimeSpan.FromSeconds(float silentSeconds)
      let result = decide (epoch + silent) entropy state (CohortCommand.ClearClaim(alice, claimId, fence, machine))
      match result with
      | Ok _ -> silent >= silentAfter
      | Error(CohortError.ClearRefused(_, ClearRefusal.HolderActive _)) -> silent < silentAfter
      | Error _ -> false

    testProperty "after any history no scope has two held claims, and a cleared claim is never held under its old fence" <| fun (raw: (byte * byte * byte) list) ->
      let members = [| alice; bob; carol; "dave" |]
      let files = [| ClaimScope.File "SageFs.Core/A.fs"; ClaimScope.File "SageFs.Core/B.fs"; ClaimScope.File "SageFs.Core/C.fs" |]
      let step (state: CohortState<string>, clock: DateTime, n: int, clearedAt: Map<ClaimId, int64<Measures.fence>>) (k: byte, p: byte, q: byte) =
        let who = members.[int p % members.Length]
        let n = n + 1
        let attempt (command: CohortCommand<string>) =
          match decide clock [| byte n; byte (n >>> 8) |] state command with
          | Ok(s, events, _) -> s, events
          | Error _ -> state, []
        let claimIds = state.Claims |> Map.toList |> List.map fst
        let nth = if claimIds.IsEmpty then None else Some claimIds.[int q % claimIds.Length]
        let clock, command =
          match int k % 8, nth with
          | 0, _ -> clock, join who
          | 1, _ -> clock, CohortCommand.Depart(who, machine)
          | 2, _ -> clock, CohortCommand.AcquireClaim(who, files.[int q % files.Length], "purpose", machine)
          | 3, _ -> clock, CohortCommand.RenewLease(who, machine)
          | 4, _ -> clock + TimeSpan.FromSeconds(float (int q * 2)), CohortCommand.Tick machine
          | _, Some id -> clock, CohortCommand.ClearClaim(who, id, state.Claims.[id].Fence, machine)
          | _, None -> clock, CohortCommand.RenewLease(who, machine)
        let s2, events = attempt command
        let cleared =
          events
          |> List.fold
            (fun acc ev ->
              match ev with
              | CohortEvent.ClaimCleared(id, _, _, _, _) -> Map.add id state.Claims.[id].Fence acc
              | _ -> acc)
            clearedAt
        s2, clock, n, cleared
      let final, _, _, cleared = raw |> List.fold step (CohortState.empty (), epoch, 0, Map.empty)
      let heldScopes =
        final.Claims |> Map.toList |> List.choose (fun (_, c) -> match c.State with ClaimState.Held _ -> Some c.Scope | _ -> None)
      let rec noOverlap = function
        | [] -> true
        | x :: rest -> rest |> List.forall (fun y -> not (ClaimScope.overlaps x y)) && noOverlap rest
      let neverHeldAgain =
        cleared
        |> Map.forall (fun id oldFence ->
          match Map.tryFind id final.Claims with
          | Some { State = ClaimState.Held _; Fence = fence } -> fence > oldFence
          | Some _
          | None -> true)
      noOverlap heldScopes && neverHeldAgain
  ]

/// What the status text says. A claim a conductor could clear says so, whose it is and the call that clears it.
[<Tests>]
let clearableStatusTests =
  testList "get_cohort_status says which claims are clearable" [

    test "a silent holder's claims are marked, and an active holder's claim is not" {
      let id (s: string) = MemberId.Minted s
      let s1 = next epoch (CohortState.empty ()) (CohortCommand.Join(id "alice", JoinableRole.Implementer, None, machine))
      let s2 = next epoch s1 (CohortCommand.Join(id "bob", JoinableRole.Implementer, None, machine))
      let s3 = next epoch s2 (CohortCommand.Join(id "carol", JoinableRole.Implementer, None, machine))
      let acquire (st: CohortState<MemberId>) who path entropy =
        match decide epoch entropy st (CohortCommand.AcquireClaim(id who, ClaimScope.File path, "purpose", machine)) with
        | Ok(s, _, _) -> s
        | Error e -> failwithf "%A" e
      let s4 = acquire s3 "bob" "B.fs" [| 1uy |]
      let s5 = acquire s4 "carol" "C.fs" [| 2uy |]
      let s6 = acquire s5 "bob" "D.fs" [| 3uy |]
      let late = epoch + silentAfter
      // Carol keeps renewing and bob does not, so bob's two claims are the silent holder's.
      let s7 = match decide late [||] s6 (CohortCommand.RenewLease(id "carol", machine)) with Ok(s, _, _) -> s | Error e -> failwithf "%A" e
      let clearances = ClaimStanding.clearable s7 late
      let head : LedgerHead<MemberId> = { Seq = 0L<Measures.ledgerSeq>; State = s7 }
      let frame = project head [||]
      let text = Features.CohortStatusText.renderWithClearancesAndReports Features.CohortStatusText.HolderSilence.NotSilent clearances (fun _ -> None) frame
      let lines = text.Split('\n') |> Array.filter (fun l -> l.TrimStart().StartsWith "- c-")
      (lines |> Array.filter (fun l -> l.Contains "| clearable: ") |> Array.length)
      |> Expect.equal "bob's two claims are clearable and carol's is not" 2
      lines |> Array.length |> Expect.equal "three claims are listed" 3
      text |> Expect.stringContains "the line names the call that clears" "release_claim"
      text |> Expect.stringContains "and how long the holder has been silent" "has been silent for 2 min"
    }

    test "an orphaned claim is marked at once, with who left it and the two calls that take it from here" {
      let id (s: string) = MemberId.Minted s
      let s1 = next epoch (CohortState.empty ()) (CohortCommand.Join(id "alice", JoinableRole.Implementer, None, machine))
      let s2 = next epoch s1 (CohortCommand.Join(id "bob", JoinableRole.Implementer, None, machine))
      let s3 =
        match decide epoch entropy s2 (CohortCommand.AcquireClaim(id "bob", ClaimScope.File "B.fs", "purpose", machine)) with
        | Ok(s, _, _) -> s
        | Error e -> failwithf "%A" e
      let s4 = next epoch s3 (CohortCommand.Depart(id "bob", machine))
      let clearances = ClaimStanding.clearable s4 epoch
      let head : LedgerHead<MemberId> = { Seq = 0L<Measures.ledgerSeq>; State = s4 }
      let text = Features.CohortStatusText.renderWithClearancesAndReports Features.CohortStatusText.HolderSilence.NotSilent clearances (fun _ -> None) (project head [||])
      text |> Expect.stringContains "the claim says it has no holder" "left it with no holder"
      text |> Expect.stringContains "and that the conductor clears it" "release_claim"
      text |> Expect.stringContains "or hands it on" "reassign_claim"
    }

    test "nothing is marked when every holder is here" {
      let clearances = ClaimStanding.clearable (CohortState.empty () : CohortState<string>) epoch
      clearances |> Expect.isEmpty "an empty cohort has nothing to clear"
    }
  ]

/// The refusals name the rule and the next action, each in its own words.
[<Tests>]
let clearRefusalTextTests =
  testList "the refusals of a clear say what to do next" [

    test "a holder that is here is named with how long it was silent and how long it must be" {
      let bobId = MemberId.Minted "bob"
      match CohortErrorMapping.toSageFsError (CohortError.ClearRefused(ClaimId "c-1", ClearRefusal.HolderActive(bobId, TimeSpan.FromTicks(silentAfter.Ticks / 4L), silentAfter))) with
      | SageFsError.CohortActionFailed(reason, suggestion) ->
        reason |> Expect.stringContains "who and how long" "30 s"
        reason |> Expect.stringContains "the window" "2 min"
        suggestion |> Expect.stringContains "the next action" "release_claim"
      | other -> failwithf "expected CohortActionFailed, got %A" other
    }

    test "an already released claim says who released it and that the scope is free" {
      match CohortErrorMapping.toSageFsError (CohortError.ClearRefused(ClaimId "c-1", ClearRefusal.AlreadyReleased(MemberId.Minted "bob", epoch))) with
      | SageFsError.CohortActionFailed(reason, suggestion) ->
        reason |> Expect.stringContains "who released it" "bob"
        suggestion |> Expect.stringContains "the scope is free" "acquire_claim"
      | other -> failwithf "expected CohortActionFailed, got %A" other
    }

    test "a claim held by someone else says the conductor can clear it when the holder is gone" {
      match CohortErrorMapping.toSageFsError (CohortError.NotClaimHolder(ClaimId "c-1", MemberId.Minted "carol")) with
      | SageFsError.CohortActionFailed(_, suggestion) -> suggestion |> Expect.stringContains "the way out" "the conductor can clear it"
      | other -> failwithf "expected CohortActionFailed, got %A" other
    }
  ]
