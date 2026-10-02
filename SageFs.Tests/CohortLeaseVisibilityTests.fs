/// Lease VISIBILITY for the agents that read `get_cohort_status`.
///
/// WHAT LANDS HERE, AND WHY NOT MORE — read this before "fixing" the gap
/// below.
///
/// Before this, a seat rendered as the bare word `present`, and the header said
/// nothing about the lease at all. An agent could not see how long it had, and
/// could not even name the numbers it was reasoning about. The header now states
/// all three (the window, the reaper cadence, and the activity-freshness window
/// that decides whether the reaper renews a seat), read live from `Timeouts`, so
/// the text cannot drift from the reaper that uses the same values.
///
/// What is NOT here is the per-member "renewed N ago, expires in M" line, and
/// that is a real gap with a named owner, not an omission. It needs
/// `MemberRecord.LastRenewal`, which is on the `CohortState` but NOT on the
/// frame: `CohortFrame`'s `MemberSeat : SeatState[]` carries presence and, for a
/// departed member, its `since`. The projection that would fix it is
/// `Cohort.project` (`SageFs.Core/Cohort.fs`, the `MemberSeat =` field), and
/// `Cohort.fs` is owned by another agent right now, so this file does not touch
/// it. The exact change is in the final report; nothing is stored — both stamps
/// are `LastRenewal` and `LastRenewal + leaseWindow`, derived at read time.
///
/// WHY the departure reason is not rendered either, and is asserted not to be
/// invented: `MemberPresence.Departed` carries `since` and nothing else, and
/// `decide` records WHICH departure it was only on the CONDUCTOR binding
/// (`ConductorBinding.Vacant(former, since, why)`, from commit `81008822`).
/// The only two producers of a departure are `CohortCommand.Depart`
/// (`ConductorLeft`) and `Tick` (`LeaseLapsed`); `VacancyReason.Revoked` is
/// produced by no command at all. So a `Departed of since * reason` seat field
/// would carry a case no projection could ever fill. The reason the frame CAN
/// know — a departed conductor's — is rendered by the conductor line, from the
/// binding that holds it.
///
/// These tests drive `Cohort.decide`/`project` and the renderer directly — no
/// owner, no daemon, no ledger — the same pure-core discipline
/// `CohortPanelTests.fs` uses for the dashboard half of this surface.
module SageFs.Tests.CohortLeaseVisibilityTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable

/// The instant every seat in this file is stamped with. NOT a tunable: it is
/// the zero point of the scenarios below, not a duration anything waits on.
let private t0 = DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)

let private entropy : Entropy = [| 1uy |]

/// Fold `decide` over `(clock, command)` steps from empty, then project exactly
/// as `CohortOwner.frameOf` does. Every refusal is a test bug, so it throws
/// rather than silently producing an empty frame.
///
/// The steps carry their OWN clock on purpose: `decide`'s `Tick` departs by
/// `clock - LastRenewal >= leaseWindow`, so a scene that needs a lease to lapse
/// has to join at one clock and tick at another. One clock for the whole list
/// would make `LastRenewal` equal the tick's own instant and nothing would ever
/// lapse — a scene that looks right in the frame's member count and is not.
let private frameOver (steps: (DateTime * CohortCommand<MemberId>) list) : CohortFrame<MemberId> =
  let finalState, seq =
    steps
    |> List.fold
      (fun (state, seq) (clock, cmd) ->
        match decide clock [| byte seq |] state cmd with
        | Ok (newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.empty (), 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } [||]

/// The common case: every step at the same instant.
let private frameAfter (commands: CohortCommand<MemberId> list) = frameOver (commands |> List.map (fun cmd -> t0, cmd))

let private seatOf (frame: CohortFrame<MemberId>) (who: MemberId) =
  let mutable found = None
  for i in 0 .. frame.MemberIds.Length - 1 do
    if frame.MemberIds.[i] = who then found <- Some frame.MemberSeat.[i]
  match found with
  | Some seat -> seat
  | None -> failwithf "no seat for %A in frame" who

let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

[<Tests>]
let cohortLeaseVisibilityTests =
  testList "Cohort lease visibility (get_cohort_status)" [

    testCase "WHY — the status text states the lease window, the reaper cadence and the freshness window, so an agent knows the numbers it is reasoning about" <| fun _ ->
      let text = Features.CohortStatusText.render (frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None) ])
      text |> Expect.stringContains "the window is stated" "Lease: 30m window"
      text |> Expect.stringContains "the reaper cadence is stated" "the reaper runs every 1m"
      text |> Expect.stringContains "the renewal window is stated" "the activity tracker saw in the last 2m"

      // The header quotes the named homes rather than numbers written into the
      // renderer, so a wrong number fails here instead of misleading a reader.
      let expected =
        sprintf
          "Lease: %dm window; the reaper runs every %dm"
          (int Timeouts.cohortLeaseWindow.TotalMinutes)
          (int Timeouts.cohortReaperInterval.TotalMinutes)
      text |> Expect.stringContains "the rendered numbers are the product's, read from Timeouts" expected

    testCase "WHY — the header says a seat lapses on the first TICK at or after the window, not at the window" <| fun _ ->
      // This is the fact that makes "expires in 0m, still present" coherent:
      // `Cohort.decide`'s Tick runs `(clock - LastRenewal) >= leaseWindow`, and
      // the clock only moves when the reaper posts a Tick. An agent that does
      // not know the cadence reads "0m left" as "about to be reaped NOW", and
      // that is a wrong reading of a seat that is still perfectly held.
      let text = Features.CohortStatusText.render (frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None) ])
      text |> Expect.stringContains "the boundary is named as the first tick at or after the window" "first tick at or after 30m"

    testCase "WHY — a present member reads as present WITH the clock the stamps were taken against, never as a bare 'present'" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
      seatOf frame alice |> Expect.equal "the seat is Present" SeatState.Present
      let t3 = Features.CohortStatusText.render frame
      // The one thing a `Present` seat CAN say without `LastRenewal`: the clock
      // its page was read at, so the (soon to arrive) lease stamps on the same
      // page have a stated reference point instead of an implied one.
      (t3.Contains "present (as of ") |> Expect.isTrue "the present seat names the clock it is read at"
      // Anchored at the END of a line — `"... [Implementer] present"` is a
      // PREFIX of `"... [Implementer] present (as of ...)`, so an unanchored
      // `Contains` here would be true for the new rendering too and the
      // assertion would never mean anything.
      let endsBarePresent =
        t3.Split('\n') |> Array.exists (fun l -> l.EndsWith "present")
      endsBarePresent
      |> Expect.isFalse "and it is never a line that ENDS at the bare word 'present'"

    testCase "WHY — a departed member keeps its `since`, and no reason is invented for it" <| fun _ ->
      let departed =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.Join(bob, JoinableRole.Verifier, None)
            CohortCommand.Depart bob ]
      seatOf departed bob
      |> Expect.equal "the frame's seat carries the departure and its since" (SeatState.Departed t0)
      let text = Features.CohortStatusText.render departed
      text |> Expect.stringContains "the departure still says when" (sprintf "departed at %s" (t0.ToString "u"))
      text |> Expect.stringContains "the departed member is still listed" (MemberId.display bob)
      // A member's `MemberPresence.Departed` carries no reason, so none can be
      // projected — asserting the text makes none up keeps the alternative
      // ("lease lapsed" / "left" / "revoked") from creeping in as a guess.
      (text.Contains "lease lapsed") |> Expect.isFalse "no departure reason is invented for an ordinary member"
      (text.Contains "revoked") |> Expect.isFalse "nor one that no command can produce"

    testCase "WHY — the departure reason the frame CAN know is rendered once, from the conductor binding that holds it" <| fun _ ->
      // `decide` records the reason on `ConductorBinding.Vacant` — for the
      // conductor. The conductor line is the one place it is knowable, and it is
      // rendered there; a member row must not restate it, or the same fact would
      // have two sources on one page.
      let vacated =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.Join(bob, JoinableRole.Verifier, None)
            CohortCommand.Depart alice ]
      vacated.Conductor
      |> Expect.equal "alice's departure moved the conductor seat to Vacant"
        (ConductorBinding.Vacant(alice, t0, VacancyReason.ConductorLeft))
      let text = Features.CohortStatusText.render vacated
      text |> Expect.stringContains "the vacated seat says who held it and that it is vacant" "VACANT"
      (text.Split("VACANT").Length - 1)
      |> Expect.equal "and the reason is stated exactly once, on the conductor line" 1

    testCase "WHY — a member whose lease lapsed on the Tick is rendered as departed, the same as one that left" <| fun _ ->
      // Both producers of a departure reach the same `MemberPresence.Departed`,
      // so they MUST render the same way from the member row. What
      // distinguishes them — silence vs a person walking away — is carried only
      // by the conductor binding, for a departed conductor.
      let byDepart =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.Join(bob, JoinableRole.Verifier, None)
            CohortCommand.Depart bob ]
      // bob joined at t0; one Tick at the window, with no renewal in between.
      let atWindow = t0.Add Cohort.leaseWindow
      let byLease =
        frameOver
          [ t0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            t0, CohortCommand.Join(bob, JoinableRole.Verifier, None)
            atWindow, CohortCommand.Tick ]
      seatOf byDepart bob |> Expect.equal "leaving produces Departed" (SeatState.Departed t0)
      seatOf byLease bob
      |> Expect.equal "a lapsed lease produces the same state" (SeatState.Departed(t0.Add Cohort.leaseWindow))
      // Nothing about the row may distinguish them: there is nothing to
      // distinguish them with. Both render `departed at <instant>`, and only the
      // instant differs.
      let departedRow (frame: CohortFrame<MemberId>) =
        (Features.CohortStatusText.render frame)
          .Split('\n')
        |> Array.filter (fun l -> l.Contains (MemberId.display bob) && l.Contains "departed")
        |> Array.map (fun l -> l.Substring(0, l.IndexOf "departed"))
        |> Array.distinct
      departedRow byDepart
      |> Expect.equal "both departures render the same member row, apart from the instant" (departedRow byLease)
      (departedRow byDepart).Length
      |> Expect.equal "and it is one distinct row each" 1

    testCase "WHY — nothing about the lease is STORED: every number on the page is read from Timeouts at render time" <| fun _ ->
      // The header's three numbers and the per-seat stamps must all be derived.
      // If any of them became a field on the frame (or on the state), the frame
      // would have to be re-projected to stay honest — and the ratchet below
      // fails the moment a lease field appears on `CohortFrame`.
      let fields =
        typeof<CohortFrame<MemberId>>.GetFields()
        |> Array.filter (fun f -> f.Name.Contains "Lease" || f.Name.Contains "Renewal")
      fields
      |> Expect.isEmpty "no lease/renewal field exists on CohortFrame — every stamp is derived in project or in the renderer"
      // And the window the text prints is the one the reaper lapses on: one
      // value, two names, never two numbers.
      Cohort.leaseWindow
      |> Expect.equal "the window the status text prints is the value decide's Tick uses" Timeouts.cohortLeaseWindow
      Timeouts.cohortReaperInterval
      |> Expect.equal "the cadence the status text prints is the one the daemon timer is built from" Timeouts.cohortReaperInterval
  ]