/// A person replaces a conductor whose connection is gone. The seat of a conductor whose bridge changed or whose daemon
/// restarted stays bound to it until its lease lapses (30 minutes), and a seating over a bound seat is refused, so the
/// person at the machine could only wait. `ReplaceConductor` is the person's override: it names the holder it replaces
/// (so it cannot move a seat that changed under them), the target must be present, and the replaced holder becomes an
/// ordinary member. These tests drive the real `Cohort.decide` for each rule and each refusal.
module SageFs.Tests.CohortReplaceConductorTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine

let private alice = "alice"
let private bob = "bob"
let private carol = "carol"
let private dave = "dave"

let private entropy = [| 1uy |]

let private ok (clock: DateTime) (state: CohortState<string>) (cmd: CohortCommand<string>) =
  match decide clock entropy state cmd with
  | Ok(s, events, _) -> s, events
  | Error e -> failwithf "expected %A to succeed, got %A" cmd e

let private next clock state cmd = ok clock state cmd |> fst

let private join who = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)

/// Alice joins first and holds the seat, bob and carol join after her. Alice's connection is gone but her lease has not
/// lapsed, so she is still Present: exactly the state the person finds.
let private alicesSeat () =
  let s1 = next epoch (CohortState.empty ()) (join alice)
  let s2 = next epoch s1 (join bob)
  next epoch s2 (join carol)

let private replaced () =
  let before = alicesSeat ()
  let after, events = ok (epoch.AddMinutes 5.0) before (CohortCommand.ReplaceConductor(alice, bob, machine))
  before, after, events

[<Tests>]
let replaceConductorTests =
  testList "A person replaces a conductor whose connection is gone" [

    test "the holder is replaced by a present member, who is then the conductor" {
      let _, after, _ = replaced ()
      after.Conductor |> Expect.equal "the seat is bound to bob" (ConductorBinding.Bound bob)
      Authority.present bob after |> Expect.equal "bob holds conductor authority" (Authority.Conductor bob)
    }

    test "the replaced holder becomes an ordinary member: still present, no authority, claims kept" {
      let before = alicesSeat ()
      let claimed, _ = ok epoch before (CohortCommand.AcquireClaim(alice, ClaimScope.File "SageFs.Core/A.fs", "purpose", machine))
      let after, _ = ok epoch claimed (CohortCommand.ReplaceConductor(alice, bob, machine))
      Authority.present alice after |> Expect.equal "alice is an ordinary Implementer now" (Authority.Member(alice, JoinableRole.Implementer))
      after.Members.[alice].Presence |> Expect.equal "her presence is untouched" MemberPresence.Present
      after.Claims |> Expect.equal "the claim she holds is still hers, untouched" claimed.Claims
      decide epoch entropy after (CohortCommand.DelegateConductor(alice, carol, machine))
      |> Expect.equal "her conductor-only calls are refused as an ordinary member's" (Error(CohortError.NotConductor alice))
    }

    test "the event carries the former holder and the moment, as the seat she lost" {
      let _, _, events = replaced ()
      events
      |> Expect.equal
        "one event: bob is seated, and the ended seat names alice, when, and that she was replaced"
        [ CohortEvent.ConductorReplaced(bob, ConductorBinding.Vacant(alice, epoch.AddMinutes 5.0, VacancyReason.Replaced)) ]
    }

    test "when the replaced holder later lapses, the seat stays with the replacement and her claims orphan as usual" {
      let before = alicesSeat ()
      let claimed, _ = ok epoch before (CohortCommand.AcquireClaim(alice, ClaimScope.File "SageFs.Core/A.fs", "purpose", machine))
      let after, _ = ok epoch claimed (CohortCommand.ReplaceConductor(alice, bob, machine))
      let late = (epoch.Add leaseWindow).AddSeconds 1.0
      // Bob and carol keep renewing; alice does not.
      let s1 = next (late.AddSeconds -1.0) after (CohortCommand.RenewLease(bob, machine))
      let s2 = next (late.AddSeconds -1.0) s1 (CohortCommand.RenewLease(carol, machine))
      let s3 = next late s2 (CohortCommand.Tick machine)
      s3.Conductor |> Expect.equal "alice's departure moved no seat: bob still holds it" (ConductorBinding.Bound bob)
      s3.Members.[alice].Presence |> Expect.notEqual "she departed on the reaper's pass" MemberPresence.Present
      s3.Claims
      |> Map.toList
      |> List.exists (fun (_, c) -> match c.State with ClaimState.Orphaned(previous, _) -> previous = alice | _ -> false)
      |> Expect.isTrue "her claim is orphaned by the normal rule"
    }

    test "the former holder can be put back by a person, because she is still present" {
      let _, after, _ = replaced ()
      let back, _ = ok epoch after (CohortCommand.ReplaceConductor(bob, alice, machine))
      back.Conductor |> Expect.equal "alice holds the seat again" (ConductorBinding.Bound alice)
    }

    test "a stale holder is refused with the seat as it stands: the holder changed since the person looked" {
      let _, after, _ = replaced ()
      decide epoch entropy after (CohortCommand.ReplaceConductor(alice, carol, machine))
      |> Expect.equal "alice was replaced already, so naming her is stale" (Error(CohortError.ConductorHolderChanged(alice, ConductorBinding.Bound bob)))
      decide epoch entropy (alicesSeat ()) (CohortCommand.ReplaceConductor(carol, bob, machine))
      |> Expect.equal "carol never held it" (Error(CohortError.ConductorHolderChanged(carol, ConductorBinding.Bound alice)))
    }

    test "a seat that is empty has nobody to replace: the refusal says it is empty" {
      let s = alicesSeat ()
      let vacant = next epoch s (CohortCommand.Depart(alice, machine))
      decide epoch entropy vacant (CohortCommand.ReplaceConductor(alice, bob, machine))
      |> Expect.equal "a vacant seat is filled by seating, not replacing" (Error(CohortError.ConductorHolderChanged(alice, vacant.Conductor)))
      decide epoch entropy (CohortState.empty ()) (CohortCommand.ReplaceConductor(alice, bob, machine))
      |> Expect.equal "a seat never bound has no holder either" (Error(CohortError.ConductorHolderChanged(alice, ConductorBinding.NeverBound)))
    }

    test "an absent target is refused with the roster of those who are present" {
      let s = alicesSeat ()
      decide epoch entropy s (CohortCommand.ReplaceConductor(alice, dave, machine))
      |> Expect.equal "dave never joined" (Error(CohortError.SeatTargetAbsent(dave, [ alice; bob; carol ])))
      let departed = next epoch s (CohortCommand.Depart(carol, machine))
      decide epoch entropy departed (CohortCommand.ReplaceConductor(alice, carol, machine))
      |> Expect.equal "carol left, so she is absent too" (Error(CohortError.SeatTargetAbsent(carol, [ alice; bob ])))
    }

    test "the holder cannot replace itself: the seat would not move and nothing is recorded" {
      decide epoch entropy (alicesSeat ()) (CohortCommand.ReplaceConductor(alice, alice, machine))
      |> Expect.equal "named as its own replacement" (Error(CohortError.ReplaceByHolder alice))
    }

    test "a command for another cohort is refused WrongCohortScope and moves nothing" {
      match decide epoch entropy (alicesSeat ()) (CohortCommand.ReplaceConductor(alice, bob, CohortScope.Named "elsewhere")) with
      | Error(CohortError.WrongCohortScope _) -> ()
      | other -> failwithf "expected WrongCohortScope, got %A" other
    }

    test "a person's plain seating of a held seat is still refused: replacing is its own command" {
      decide epoch entropy (alicesSeat ()) (CohortCommand.SeatConductor(bob, machine))
      |> Expect.equal "SeatConductor fills an empty seat only" (Error(CohortError.ConductorSeatTaken alice))
    }

    test "replacing is a fold over the ledger: replay reproduces the seat and every recorded event" {
      let entries =
        [ epoch, join alice
          epoch, join bob
          epoch, CohortCommand.ReplaceConductor(alice, bob, machine) ]
      let ledger, final =
        entries
        |> List.fold
          (fun (acc, state) (clock, cmd) ->
            let newState, events = ok clock state cmd
            acc @ [ { Seq = LanguagePrimitives.Int64WithMeasure(int64 (List.length acc)); Clock = clock; Entropy = entropy; Command = cmd; Events = events } ], newState)
          ([], CohortState.empty ())
      let audit = replayAuditIn machine ledger
      audit.Head.State.Conductor |> Expect.equal "the replayed seat is the live one" final.Conductor
      audit.Head.State.Conductor |> Expect.equal "and it is bob" (ConductorBinding.Bound bob)
      audit.Dropped |> Expect.isEmpty "no row was dropped"
      audit.Diverged |> Expect.isEmpty "no recorded event was lost"
    }
  ]

[<Tests>]
let holderSilenceTests =
  let silence = SageFs.Features.CohortStatusText.HolderSilence.ofState
  let window = Timeouts.cohortHolderSilentAfter
  testList "A conductor holder's silence is read off its lease" [

    test "a holder renewed inside the silence threshold is not silent" {
      silence (alicesSeat ()) (epoch + window - TimeSpan.FromSeconds 1.0)
      |> Expect.equal "between reaper passes" SageFs.Features.CohortStatusText.HolderSilence.NotSilent
    }

    test "a holder not renewed for the threshold is silent since its last renewal, and its lease lapses a window after it" {
      silence (alicesSeat ()) (epoch + window)
      |> Expect.equal "silent since the join, lapsing a lease window after it" (SageFs.Features.CohortStatusText.HolderSilence.SilentSince(epoch, epoch.Add leaseWindow))
    }

    test "a renewal moves the silence forward" {
      let renewed = next (epoch.AddMinutes 10.0) (alicesSeat ()) (CohortCommand.RenewLease(alice, machine))
      silence renewed (epoch.AddMinutes 10.0 + window - TimeSpan.FromSeconds 1.0)
      |> Expect.equal "renewed ten minutes in, so not silent yet" SageFs.Features.CohortStatusText.HolderSilence.NotSilent
    }

    test "a seat that is empty has no holder to be silent" {
      let vacant = next epoch (alicesSeat ()) (CohortCommand.Depart(alice, machine))
      silence vacant (epoch.AddHours 3.0) |> Expect.equal "vacant" SageFs.Features.CohortStatusText.HolderSilence.NotSilent
      silence (CohortState.empty ()) (epoch.AddHours 3.0) |> Expect.equal "never bound" SageFs.Features.CohortStatusText.HolderSilence.NotSilent
    }

    test "the text says how many minutes and when the lease lapses" {
      SageFs.Features.CohortStatusText.HolderSilence.text (epoch.AddMinutes 12.0) (SageFs.Features.CohortStatusText.HolderSilence.SilentSince(epoch, epoch.Add leaseWindow))
      |> Expect.equal "twelve minutes" (sprintf "silent for 12 min, lapses at %s" ((epoch.Add leaseWindow).ToString "u"))
    }
  ]
