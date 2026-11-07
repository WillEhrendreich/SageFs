/// A person fills an empty conductor seat. The seat empties when its holder leaves or goes silent, and nothing in
/// the tool surface fills it: `delegate_conductor` needs a sitting conductor, and nobody is promoted by a timer. A
/// cohort whose conductor left therefore had no way back except wiping its ledger. `SeatConductor` is the person's
/// way in, and these tests drive the real `Cohort.decide` for every rule it carries: the seat must be empty, the
/// member must be present, the vacancy it ended is on the record, and a replay of the ledger lands on the same seat.
module SageFs.Tests.CohortSeatConductorTests

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

let private entropy = [| 1uy |]

let private ok (clock: DateTime) (state: CohortState<string>) (cmd: CohortCommand<string>) =
  match decide clock entropy state cmd with
  | Ok(s, events, _) -> s, events
  | Error e -> failwithf "expected %A to succeed, got %A" cmd e

let private next clock state cmd = ok clock state cmd |> fst

let private join who = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)

/// Alice joins first and is the conductor, bob joins second, then alice leaves: the seat is vacant and bob is here.
let private vacantWithBob () =
  let s1 = next epoch (CohortState.empty ()) (join alice)
  let s2 = next epoch s1 (join bob)
  next epoch s2 (CohortCommand.Depart(alice, machine))

let private seatedBob () =
  let vacant = vacantWithBob ()
  let seated, events = ok epoch vacant (CohortCommand.SeatConductor(bob, machine))
  vacant, seated, events

[<Tests>]
let seatConductorTests =
  testList "A person seats a conductor" [

    test "a vacant seat is filled by a present member, who is then the conductor" {
      let _, seated, _ = seatedBob ()
      seated.Conductor |> Expect.equal "the seat is bound to bob" (ConductorBinding.Bound bob)
      Authority.present bob seated |> Expect.equal "bob holds conductor authority" (Authority.Conductor bob)
      Authority.present alice seated |> Expect.equal "alice, who left, holds none" Authority.Anonymous
    }

    test "the event carries the vacancy it ended, who held the seat, since when and why" {
      let vacant, _, events = seatedBob ()
      events
      |> Expect.equal "one event, naming bob and the vacancy exactly as it stood" [ CohortEvent.ConductorSeated(bob, vacant.Conductor) ]
      match vacant.Conductor with
      | ConductorBinding.Vacant(former, _, why) ->
        former |> Expect.equal "the vacancy names alice" alice
        why |> Expect.equal "and says she left" VacancyReason.ConductorLeft
      | other -> failwithf "expected a vacant seat, got %A" other
    }

    test "a lapsed lease is a vacancy a person can end, and the event says LeaseLapsed" {
      let s1 = next epoch (CohortState.empty ()) (join alice)
      let s2 = next epoch s1 (join bob)
      // Alice stops renewing; bob renews just before the reaper runs, so only alice is silent.
      let late = (epoch.Add leaseWindow).AddSeconds 1.0
      let s3 = next (late.AddSeconds -1.0) s2 (CohortCommand.RenewLease(bob, machine))
      let s4 = next late s3 (CohortCommand.Tick machine)
      let _, events = ok late s4 (CohortCommand.SeatConductor(bob, machine))
      match events with
      | [ CohortEvent.ConductorSeated(seated, ConductorBinding.Vacant(former, _, why)) ] ->
        seated |> Expect.equal "bob is seated" bob
        former |> Expect.equal "alice held it" alice
        why |> Expect.equal "her lease lapsed" VacancyReason.LeaseLapsed
      | other -> failwithf "expected one ConductorSeated over a lapsed vacancy, got %A" other
    }

    test "a seat that is bound refuses, even for the member who holds it and for a present stranger" {
      let s1 = next epoch (CohortState.empty ()) (join alice)
      let s2 = next epoch s1 (join bob)
      decide epoch entropy s2 (CohortCommand.SeatConductor(bob, machine))
      |> Expect.equal "naming bob while alice sits" (Error(CohortError.ConductorSeatTaken alice))
      decide epoch entropy s2 (CohortCommand.SeatConductor(alice, machine))
      |> Expect.equal "naming the holder is refused too, nothing is recorded" (Error(CohortError.ConductorSeatTaken alice))
    }

    test "an absent member is refused with the roster of those who are present" {
      let vacant = vacantWithBob ()
      decide epoch entropy vacant (CohortCommand.SeatConductor(carol, machine))
      |> Expect.equal "carol never joined" (Error(CohortError.SeatTargetAbsent(carol, [ bob ])))
      decide epoch entropy vacant (CohortCommand.SeatConductor(alice, machine))
      |> Expect.equal "alice departed, so she is absent too" (Error(CohortError.SeatTargetAbsent(alice, [ bob ])))
    }

    test "a cohort nobody joined refuses with an empty roster" {
      decide epoch entropy (CohortState.empty ()) (CohortCommand.SeatConductor(alice, machine))
      |> Expect.equal "the seat was never bound and nobody is here" (Error(CohortError.SeatTargetAbsent(alice, [])))
    }

    test "the former conductor can be seated again once she is back" {
      let vacant = vacantWithBob ()
      let rejoined = next epoch vacant (join alice)
      let seated, _ = ok epoch rejoined (CohortCommand.SeatConductor(alice, machine))
      seated.Conductor |> Expect.equal "a rejoined former conductor is a valid choice" (ConductorBinding.Bound alice)
    }

    test "a command for another cohort is refused WrongCohortScope and moves nothing" {
      let vacant = vacantWithBob ()
      match decide epoch entropy vacant (CohortCommand.SeatConductor(bob, CohortScope.Named "elsewhere")) with
      | Error(CohortError.WrongCohortScope _) -> ()
      | other -> failwithf "expected WrongCohortScope, got %A" other
    }

    test "seating is a fold over the ledger: replay reproduces the seat and every recorded event" {
      let entries =
        [ epoch, CohortCommand.Join(alice, JoinableRole.Implementer, None, machine)
          epoch, join bob
          epoch, CohortCommand.Depart(alice, machine)
          epoch, CohortCommand.SeatConductor(bob, machine) ]
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
