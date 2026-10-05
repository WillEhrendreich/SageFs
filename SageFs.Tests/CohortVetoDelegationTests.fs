module SageFs.Tests.CohortVetoDelegationTests

// The conductor could not hand its role on, and nobody could veto or withdraw a landing, because four
// commands (`DelegateConductor`, `WithdrawLanding`, `VetoLanding`, `ResolveVeto`) existed in the core and
// no tool issued them. These pin the RULES those four commands keep once a tool can issue them, against the
// real `Cohort.decide`:
//
//   * only the sitting conductor delegates, and only to a member who is present (a refusal names the roster);
//   * a veto names a landing that is still live and a reason, and comes from a seated member who is not an
//     observer; a veto after the landing is over is a REFUSAL, never a silent no-op;
//   * a withdrawal is by the landing's own requester;
//   * only the conductor clears a veto.

open System
open Expecto
open Expecto.Flip
open SageFs.Cohort

let epoch = DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)
let cohort = SageFs.CohortScope.Machine

let alice = "alice" // first joiner, so the conductor
let bob = "bob" // an implementer, requests the landing
let vera = "vera" // a verifier
let olive = "olive" // an observer
let stranger = "stranger" // never joined

let ent (n: int) : Entropy = [| byte n |]

let ok (n: int) (s: CohortState<string>) (cmd: CohortCommand<string>) =
  match decide epoch (ent n) s cmd with
  | Ok(s', events, _) -> s', events
  | Error e -> failwithf "expected %A to succeed, got %A" cmd e

let refusal (n: int) (s: CohortState<string>) (cmd: CohortCommand<string>) =
  match decide epoch (ent n) s cmd with
  | Ok _ -> failwithf "expected %A to be refused" cmd
  | Error e -> e

/// A cohort with all four roles seated and ONE landing of bob's in the queue.
let scene () =
  let s0, _ = ok 1 (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None, cohort))
  let s1, _ = ok 2 s0 (CohortCommand.Join(bob, JoinableRole.Implementer, None, cohort))
  let s2, _ = ok 3 s1 (CohortCommand.Join(vera, JoinableRole.Verifier, None, cohort))
  let s3, _ = ok 4 s2 (CohortCommand.Join(olive, JoinableRole.Observer, None, cohort))
  let s4, _ = ok 5 s3 (CohortCommand.RequestLanding(bob, [], [ "abc" ], "land it", cohort))
  let lid = s4.Landings |> Map.toList |> List.head |> fst
  s4, lid

let veto (by: string) (lid: LandingId) (reason: string) = CohortCommand.VetoLanding(by, lid, reason, cohort)

[<Tests>]
let delegateTests =
  testList "DelegateConductor: only the sitting conductor hands the seat on, to a present member" [

    test "the conductor delegates to a present member and the seat moves to exactly that member" {
      let s, _ = scene ()
      let s', events = ok 10 s (CohortCommand.DelegateConductor(alice, bob, cohort))
      s'.Conductor |> Expect.equal "the seat is bob's" (ConductorBinding.Bound bob)
      events |> Expect.contains "the move is on the record" (CohortEvent.ConductorDelegated(alice, bob))
      Authority.present alice s' |> Expect.equal "alice is an ordinary member now" (Authority.Member(alice, JoinableRole.Implementer))
      Authority.present bob s' |> Expect.equal "bob is the conductor" (Authority.Conductor bob)
    }

    test "a member who is not the conductor is refused, and the seat does not move" {
      let s, _ = scene ()
      refusal 10 s (CohortCommand.DelegateConductor(bob, bob, cohort))
      |> Expect.equal "bob is not the conductor" (CohortError.NotConductor bob)
      refusal 11 s (CohortCommand.DelegateConductor(stranger, bob, cohort))
      |> Expect.equal "a stranger is not the conductor" (CohortError.NotConductor stranger)
    }

    test "after a delegation the old conductor cannot delegate again" {
      let s, _ = scene ()
      let s', _ = ok 10 s (CohortCommand.DelegateConductor(alice, bob, cohort))
      refusal 11 s' (CohortCommand.DelegateConductor(alice, vera, cohort))
      |> Expect.equal "alice gave the seat away" (CohortError.NotConductor alice)
    }

    test "delegating to a member who never joined is refused WITH THE ROSTER of present members" {
      let s, _ = scene ()
      match refusal 10 s (CohortCommand.DelegateConductor(alice, stranger, cohort)) with
      | CohortError.DelegateTargetAbsent(target, present) ->
        target |> Expect.equal "names the target" stranger
        present |> List.sort |> Expect.equal "the roster is every present member, so the caller can pick one" [ alice; bob; olive; vera ]
      | other -> failtestf "expected DelegateTargetAbsent, got %A" other
    }

    test "delegating to a member who departed is refused, and the roster no longer lists them" {
      let s, _ = scene ()
      let s', _ = ok 10 s (CohortCommand.Depart(vera, cohort))
      match refusal 11 s' (CohortCommand.DelegateConductor(alice, vera, cohort)) with
      | CohortError.DelegateTargetAbsent(target, present) ->
        target |> Expect.equal "names the departed member" vera
        present |> List.sort |> Expect.equal "the departed member is not on the roster" [ alice; bob; olive ]
      | other -> failtestf "expected DelegateTargetAbsent, got %A" other
    }

    test "delegating to yourself is refused, not recorded as a handoff that handed nothing" {
      let s, _ = scene ()
      refusal 10 s (CohortCommand.DelegateConductor(alice, alice, cohort))
      |> Expect.equal "the seat is already alice's" (CohortError.DelegateToSelf alice)
    }

    test "a vacant seat cannot be delegated from: nobody is the conductor to do it" {
      let s, _ = scene ()
      let s', _ = ok 10 s (CohortCommand.Depart(alice, cohort))
      match refusal 11 s' (CohortCommand.DelegateConductor(bob, vera, cohort)) with
      | CohortError.ConductorVacant(Some former, _, VacancyReason.ConductorLeft) -> former |> Expect.equal "names who held it" alice
      | other -> failtestf "expected ConductorVacant, got %A" other
    }
  ]

[<Tests>]
let vetoTests =
  testList "VetoLanding: a seated, non-observing member objects to a live landing, with a reason" [

    test "an implementer, a verifier and the conductor may each veto a live landing" {
      for by in [ bob; vera; alice ] do
        let s, lid = scene ()
        let s', events = ok 10 s (veto by lid "the migration is not ready")
        s'.Landings.[lid].State
        |> Expect.equal
          (sprintf "%s's veto blocks the landing for the conductor" by)
          (LandingState.Blocked(LandingBlocker.VetoedBy(by, "the migration is not ready"), NextAction.AwaitConductor))
        events |> Expect.contains "the veto is on the record" (CohortEvent.LandingVetoed(lid, by, "the migration is not ready"))
    }

    test "an observer may not veto: the role reads, it does not decide" {
      let s, lid = scene ()
      refusal 10 s (veto olive lid "no")
      |> Expect.equal "refused as read-only" (CohortError.VetoRefused(olive, VetoRefusal.ReadOnlyRole))
    }

    test "someone who never joined may not veto" {
      let s, lid = scene ()
      refusal 10 s (veto stranger lid "no")
      |> Expect.equal "refused as not a member" (CohortError.VetoRefused(stranger, VetoRefusal.NotAMember))
    }

    test "a member who departed may not veto" {
      let s, lid = scene ()
      let s', _ = ok 10 s (CohortCommand.Depart(vera, cohort))
      refusal 11 s' (veto vera lid "no")
      |> Expect.equal "a departed seat has no standing" (CohortError.VetoRefused(vera, VetoRefusal.NotAMember))
    }

    test "a veto names a reason: a blank one is refused" {
      let s, lid = scene ()
      match refusal 10 s (veto bob lid "   ") with
      | CohortError.InvalidVetoReason _ -> ()
      | other -> failtestf "expected InvalidVetoReason, got %A" other
    }

    test "a veto reason is bounded like a landing statement" {
      let s, lid = scene ()
      match refusal 10 s (veto bob lid (String('x', Statement.maxLength + 1))) with
      | CohortError.InvalidVetoReason _ -> ()
      | other -> failtestf "expected InvalidVetoReason, got %A" other
    }

    test "a veto of a landing nobody queued is refused as unknown" {
      let s, _ = scene ()
      refusal 10 s (veto bob (LandingId "nope") "no")
      |> Expect.equal "unknown landing" (CohortError.UnknownLanding(LandingId "nope"))
    }

    test "a veto AFTER the landing is withdrawn is a refusal, never a silent no-op" {
      let s, lid = scene ()
      let s', _ = ok 10 s (CohortCommand.WithdrawLanding(bob, lid, cohort))
      match refusal 11 s' (veto vera lid "too late") with
      | CohortError.LandingNotInExpectedState(id, _) -> id |> Expect.equal "names the landing" lid
      | other -> failtestf "expected LandingNotInExpectedState, got %A" other
    }

    test "a veto AFTER the landing landed is a refusal, never a silent no-op" {
      let s, lid = scene ()
      let landed =
        { s with Landings = s.Landings |> Map.add lid { s.Landings.[lid] with State = LandingState.Landed "deadbeef" }; Queue = [] }
      match refusal 10 landed (veto vera lid "too late") with
      | CohortError.LandingNotInExpectedState(id, _) -> id |> Expect.equal "names the landing" lid
      | other -> failtestf "expected LandingNotInExpectedState, got %A" other
    }

    test "a second veto of a vetoed landing is refused, and the first reason and vetoer stand" {
      let s, lid = scene ()
      let s', _ = ok 10 s (veto vera lid "first reason")
      refusal 11 s' (veto bob lid "second reason")
      |> Expect.equal "already vetoed, by whom" (CohortError.LandingAlreadyVetoed(lid, vera))
    }

    test "a landing already blocked for another reason is not re-labelled by a veto" {
      let s, lid = scene ()
      let blocked =
        { s with
            Landings =
              s.Landings
              |> Map.add lid { s.Landings.[lid] with State = LandingState.Blocked(LandingBlocker.RebaseConflict [ "x.fs" ], NextAction.RebaseAndResubmit) }
            Queue = [] }
      match refusal 10 blocked (veto vera lid "no") with
      | CohortError.LandingNotInExpectedState(id, _) -> id |> Expect.equal "names the landing" lid
      | other -> failtestf "expected LandingNotInExpectedState, got %A" other
    }
  ]

[<Tests>]
let withdrawAndResolveTests =
  testList "WithdrawLanding is the requester's; ResolveVeto is the conductor's" [

    test "the requester withdraws their own live landing" {
      let s, lid = scene ()
      let s', events = ok 10 s (CohortCommand.WithdrawLanding(bob, lid, cohort))
      s'.Landings.[lid].State |> Expect.equal "withdrawn" LandingState.Withdrawn
      events |> Expect.contains "on the record" (CohortEvent.LandingWithdrawn lid)
    }

    test "nobody else withdraws it, not even the conductor" {
      let s, lid = scene ()
      for by in [ alice; vera; olive; stranger ] do
        refusal 10 s (CohortCommand.WithdrawLanding(by, lid, cohort))
        |> Expect.equal (sprintf "%s did not request it" by) (CohortError.NotLandingRequester(lid, by))
    }

    test "withdrawing twice is refused the second time" {
      let s, lid = scene ()
      let s', _ = ok 10 s (CohortCommand.WithdrawLanding(bob, lid, cohort))
      match refusal 11 s' (CohortCommand.WithdrawLanding(bob, lid, cohort)) with
      | CohortError.LandingNotInExpectedState _ -> ()
      | other -> failtestf "expected LandingNotInExpectedState, got %A" other
    }

    test "the requester may withdraw a landing the conductor has not yet cleared of a veto" {
      let s, lid = scene ()
      let s', _ = ok 10 s (veto vera lid "hold")
      let s'', _ = ok 11 s' (CohortCommand.WithdrawLanding(bob, lid, cohort))
      s''.Landings.[lid].State |> Expect.equal "withdrawn out of AwaitConductor" LandingState.Withdrawn
    }

    test "only the conductor clears a veto, and the landing queues again" {
      let s, lid = scene ()
      let s', _ = ok 10 s (veto vera lid "hold")
      refusal 11 s' (CohortCommand.ResolveVeto(bob, lid, cohort))
      |> Expect.equal "bob is not the conductor" (CohortError.NotConductor bob)
      let s'', events = ok 12 s' (CohortCommand.ResolveVeto(alice, lid, cohort))
      events |> Expect.contains "on the record" (CohortEvent.LandingVetoResolved(lid, alice))
      s''.Landings.[lid].State |> Expect.notEqual "no longer vetoed" (LandingState.Blocked(LandingBlocker.VetoedBy(vera, "hold"), NextAction.AwaitConductor))
    }

    test "resolving a landing that was never vetoed is refused" {
      let s, lid = scene ()
      match refusal 10 s (CohortCommand.ResolveVeto(alice, lid, cohort)) with
      | CohortError.LandingNotInExpectedState(id, _) -> id |> Expect.equal "names the landing" lid
      | other -> failtestf "expected LandingNotInExpectedState, got %A" other
    }
  ]
