/// RED for the conductor-vacancy defect: a conductor that LEFT (or whose lease
/// lapsed) kept the highest authority as long as its transport connection was
/// still alive, because `Authority.present` checked the conductor binding
/// BEFORE checking presence. `CohortState.Conductor` was a bare `'m option` and
/// `departMember` never touched it, so "departed but still conductor" was a
/// perfectly writable state — and `Retention.membersToPurge` explicitly
/// EXEMPTED the conductor, pinning that seat in the ledger forever.
///
/// Live symptom this pins down: `get_cohort_status` reported
/// `Conductor: mcp:m-d250815688321fe7` with that same member marked
/// `departed 2026-09-18` — a conductor that left 13 days earlier and still held
/// the seat that `set_integration_ref`/`reassign_claim`/`revoke_member` all
/// require.
///
/// Every test here drives the REAL `Cohort.decide`/`Authority.present`, so it
/// fails on the pre-fix core and passes on the typed `ConductorBinding` model.
module SageFs.Tests.CohortVacancyTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// Alice is the FIRST joiner, so v1's implicit create_cohort binds her conductor
/// without anybody delegating anything.
let private alice = "alice"
let private bob = "bob"

let private ok (clock: DateTime) (state: CohortState<string>) (cmd: CohortCommand<string>) =
  match decide clock [| 1uy |] state cmd with
  | Ok(s, events, effects) -> s, events, effects
  | Error e -> failwithf "expected %A to succeed, got %A" cmd e

/// The joint the daemon's silent-member reaper produces: one `Tick` at a clock
/// past `leaseWindow` departs everyone who has not renewed.
let private lapsed t = ok (t.Add leaseWindow).AddSeconds 1.0 (ok t (ok t (CohortState.empty ()) (CohortCommand.Tick)) CohortCommand.Tick) CohortCommand.Tick

[<Tests>]
let cohortVacancyTests =
  testList "Cohort conductor vacancy (typed, presence-first)" [

    test "a departed conductor is NOT Conductor — it cannot run conductor-only commands" {
      let s1 = ok epoch (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let s2, _, _ = ok epoch s1 (CohortCommand.Depart alice)

      Cohort.Authority.present alice s2
      |> Expect.equal "the departed conductor resolves to Anonymous, not Conductor" Authority.Anonymous

      Cohort.decide epoch [| 2uy |] s2 (CohortCommand.SetIntegrationHead(alice, "deadbeef"))
      |> Expect.equal "the departed conductor cannot re-seed the integration head"
                    (Error(CohortError.NotConductor alice))
    }

    test "a conductor whose lease LAPSED is not Conductor either (the Tick path)" {
      let s1 = ok epoch (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let late = epoch.Add leaseWindow
      let s2, _, _ = ok late s1 CohortCommand.Tick

      s2.Members.[alice].Presence
      |> Expect.equal "the reaper departed the silent conductor" (MemberPresence.Departed late)

      Cohort.Authority.present alice s2
      |> Expect.equal "a silent, reaped conductor keeps no authority" Authority.Anonymous

      Cohort.decide late [| 2uy |] s2 (CohortCommand.ResolveVeto(alice, LandingId "nope"))
      |> Expect.equal "a reaped conductor cannot resolve a veto"
                    (Error(CohortError.NotConductor alice))
    }

    test "a departed conductor does not get its powers back by rejoining — the seat is Vacant, not reset" {
      // This is the case the bare `'m option` made impossible to distinguish:
      // `Join` on a departed member used to fall into the same "conductor
      // already bound" arm as any other joiner, so the rejoined member was
      // simultaneously Present AND still the conductor.
      let s1 = ok epoch (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let s2, _, _ = ok epoch s1 (CohortCommand.Depart alice)
      let s3, _, _ = ok epoch s2 (CohortCommand.Join(alice, JoinableRole.Implementer, None))

      Cohort.Authority.present alice s3
      |> Expect.equal "a rejoined former conductor is an ordinary Member, not Conductor" (Authority.Member(alice, JoinableRole.Implementer))

      Cohort.decide epoch [| 2uy |] s3 (CohortCommand.SetIntegrationHead(alice, "deadbeef"))
      |> Expect.equal "a rejoined former conductor still cannot re-seed the head"
                    (Error(CohortError.NotConductor alice))
    }

    test "a departed conductor's claim-orphaning, seat departure and vacancy are ONE step, with the reason recorded" {
      let s1 = ok epoch (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let s2, _, _ = ok epoch s1 (CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing"))
      let s3, events, _ = ok epoch s2 (CohortCommand.Depart alice)

      events
      |> List.exists (function CohortEvent.MemberDeparted(m, _) -> m = alice | _ -> false)
      |> Expect.isTrue "the departure is still recorded"
      s3.Claims |> Map.toList |> List.forall (fun (_, c) -> match c.State with ClaimState.Orphaned _ -> true | _ -> false)
      |> Expect.isTrue "the departing conductor's claim is orphaned in the same step"
    }

    test "the typed refusal for a vacant seat is ConductorVacant, NOT the generic NotConductor" {
      // Distinct refusals stay distinct cases: "nobody is conductor and you are
      // not them" (`NotConductor`) and "the conductor seat is EMPTY, nobody can
      // do this" (`ConductorVacant`) imply different user actions — the second
      // needs a human to appoint a conductor, and no tool can.
      let s1 = ok epoch (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let s2, _, _ = ok epoch s1 (CohortCommand.Depart alice)
      let s3, _, _ = ok epoch s2 (CohortCommand.Join(bob, JoinableRole.Implementer, None))

      // bob is an ordinary present member, but the seat is empty — the refusal
      // must name the VACANCY, not merely say "you are not the conductor".
      Cohort.decide epoch [| 3uy |] s3 (CohortCommand.SetIntegrationHead(bob, "deadbeef"))
      |> Expect.equal "a present member facing a vacant seat gets ConductorVacant"
                    (Error(CohortError.ConductorVacant(alice, epoch, VacancyReason.ConductorLeft)))

      // a NEVER-BOUND cohort (nobody has ever joined) is also vacant, with no
      // former conductor to name
      Cohort.decide epoch [| 4uy |] (CohortState.empty ()) (CohortCommand.SetIntegrationHead(bob, "deadbeef"))
      |> Expect.equal "a never-bound cohort has no conductor to point at"
                    (Error(CohortError.ConductorVacant(None, epoch, VacancyReason.NeverBound)))
    }

    test "a VACANT former conductor is purged like any other departed seat" {
      // Before the fix `membersToPurge` skipped the conductor outright, so this
      // seat could never age out no matter how stale it got.
      let s1 = ok epoch (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let s2, _, _ = ok epoch s1 (CohortCommand.Join(bob, JoinableRole.Implementer, None))
      let s3, _, _ = ok epoch s2 (CohortCommand.Depart alice)
      let longAfter = epoch.Add(settledRetention).AddSeconds 1.0

      Cohort.Retention.membersToPurge longAfter s3
      |> Expect.equal "the departed conductor ages out like any other seat" [ (alice, epoch) ]

      let swept, sweepEvents, _ = ok longAfter s3 CohortCommand.Tick
      swept.Members.ContainsKey alice
      |> Expect.isFalse "the vacant conductor's record is gone"
      sweepEvents
      |> List.exists (function CohortEvent.MemberPurged(m, _) -> m = alice | _ -> false)
      |> Expect.isTrue "and the purge is on the ledger"
    }

    test "a LIVE (Bound) conductor is still exempt from purge — authority must still resolve" {
      let s1 = ok epoch (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None))
      // bob leaves; alice (still Bound and Present) must not be swept
      let s2, _, _ = ok epoch s1 (CohortCommand.Join(bob, JoinableRole.Implementer, None))
      let s3, _, _ = ok epoch s2 (CohortCommand.Depart bob)
      let longAfter = epoch.Add(settledRetention).AddSeconds 1.0
      Cohort.Retention.membersToPurge longAfter s3
      |> Expect.equal "only the departed NON-conductor is purgeable" [ (bob, epoch) ]
      s3.Conductor
      |> Expect.equal "the bound conductor is unchanged" (ConductorBinding.Bound alice)
    }

    test "INVARIANT: Bound m implies m is Present — checked after every step of a lifecycle" {
      // departMember is the only site that moves Bound -> Vacant, and it does
      // so in the same step it marks the seat Departed; this walks the real
      // lifecycle and checks the invariant after each step.
      let invariantHolds (s: CohortState<string>) =
        match s.Conductor with
        | ConductorBinding.Bound m ->
          match Map.tryFind m s.Members with
          | Some { Presence = MemberPresence.Present } -> true
          | _ -> false
        | ConductorBinding.NeverBound
        | ConductorBinding.Vacant _ -> true

      let steps =
        [ (CohortState.empty () |> ok epoch (CohortCommand.Join(alice, JoinableRole.Implementer, None)))
          |> fst
          ok epoch (CohortCommand.Join(bob, JoinableRole.Implementer, None)) |> fst
          ok epoch (CohortCommand.AcquireClaim(bob, ClaimScope.File "src/Foo.fs", "editing")) |> fst
          ok epoch (CohortCommand.Depart bob) |> fst
          ok epoch (CohortCommand.DelegateConductor(alice, bob)) |> fst
          ok epoch (CohortCommand.Depart alice) |> fst ]

      steps
      |> List.forall invariantHolds
      |> Expect.isTrue "Bound m implies m is Present at every step of the lifecycle"

      steps.[steps.Length - 1].Conductor
      |> Expect.equal "and the last step left the seat Vacant naming the former conductor"
                    (ConductorBinding.Vacant(bob, epoch, VacancyReason.ConductorLeft))
    }

    test "there is NO auto-promotion: a surviving member is not silently made conductor" {
      // Automatic promotion of a worker on a lease timeout, with no human in the
      // loop, is the wrong direction for a system whose premise is that effects
      // need approval. A vacancy is a typed state that someone must fill.
      let s1 = ok epoch (CohortState.empty ()) (CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let s2, _, _ = ok epoch s1 (CohortCommand.Join(bob, JoinableRole.Implementer, None))
      let s3, _, _ = ok epoch s2 (CohortCommand.Depart alice)

      s3.Conductor
      |> Expect.equal "the seat stays Vacant — nobody is promoted into it" (ConductorBinding.Vacant(alice, epoch, VacancyReason.ConductorLeft))

      Cohort.Authority.present bob s3
      |> Expect.equal "the surviving member is an ordinary Member, not the new conductor" (Authority.Member(bob, JoinableRole.Implementer))

      // ...and a TICK that reaps everyone does not promote either.
      let late = epoch.Add leaseWindow
      let s4, _, _ = ok late s2 CohortCommand.Tick
      s4.Conductor
      |> Expect.equal "after the reaper, the seat is Vacant(named, LeaseLapsed) — still no auto-promotion"
                    (ConductorBinding.Vacant(alice, late, VacancyReason.LeaseLapsed))
    }
  ]
