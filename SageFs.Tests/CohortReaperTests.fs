module SageFs.Tests.CohortReaperTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Server

// roast-7 §5: the daemon's cohort lease reaper. The pure DECIDE logic (Tick /
// RenewLease) lives in Cohort.fs; the daemon wires a 60s timer that renews the
// lease of each active Present member, then posts Tick to depart the silent
// ones. These tests guard BOTH halves:
//   1. DaemonMode.cohortMembersToRenew — the selection (Present ∧ active).
//   2. The renew-then-reap SEQUENCE against the real Cohort.decide — the exact
//      property the earlier (reverted) attempt broke: an ACTIVE member must
//      never be reaped, however long the daemon runs.

let private entropy : Cohort.Entropy = [| 1uy |]

/// Apply one command through the real decide, ignoring refusals (a RenewLease
/// of a non-member is a harmless no-op, exactly as in production).
let inline private step (clock: DateTime) (st: CohortState<'m>) (cmd: CohortCommand<'m>) : CohortState<'m> =
  match Cohort.decide clock entropy st cmd with
  | Result.Ok(st', _, _) -> st'
  | Result.Error _ -> st

/// The daemon's reaper tick as a pure sequence over the real decide: renew the
/// active Present members, then Tick. Mirrors DaemonMode.cohortReaperCallback.
let private reaperTick (isActive: string -> bool) (clock: DateTime) (st: CohortState<string>) : CohortState<string> =
  let renewed =
    st.Members
    |> Map.toList
    |> List.choose (fun (m, r) -> match r.Presence with | MemberPresence.Present when isActive m -> Some m | _ -> None)
    |> List.fold (fun s who -> step clock s (CohortCommand.RenewLease who)) st
  step clock renewed CohortCommand.Tick

let private presenceOf who (st: CohortState<string>) =
  match Map.tryFind who st.Members with
  | Some r -> Some r.Presence
  | None -> None

let private heldClaims (st: CohortState<string>) =
  st.Claims |> Map.toList |> List.filter (fun (_, c) -> match c.State with | ClaimState.Held _ -> true | _ -> false) |> List.length

[<Tests>]
let tests =
  testList "Cohort lease reaper (roast-7 §5)" [

    testCase "cohortMembersToRenew selects exactly the Present, active members"
    <| fun _ ->
      // Build a cohort with a Present active member, a Present idle member, and
      // a Departed one — only the Present+active member should be renewed.
      let t0 = DateTime(2026, 1, 1, 12, 0, 0)
      let s =
        (CohortState.empty () : CohortState<MemberTable.MemberId>)
        |> fun s -> step t0 s (CohortCommand.Join(MemberTable.MemberId.Minted "active", JoinableRole.Implementer, None))
        |> fun s -> step t0 s (CohortCommand.Join(MemberTable.MemberId.Minted "idle", JoinableRole.Implementer, None))
        |> fun s -> step t0 s (CohortCommand.Join(MemberTable.MemberId.Minted "left", JoinableRole.Implementer, None))
        // "left" departs (its own tick past the lease, in isolation)
        |> fun s -> step (t0 + Cohort.leaseWindow + TimeSpan.FromMinutes 1.0) s CohortCommand.Tick
      // After that Tick everyone silent is Departed. Re-join active + idle fresh.
      let now = t0 + Cohort.leaseWindow + TimeSpan.FromMinutes 2.0
      let s2 =
        s
        |> fun s -> step now s (CohortCommand.Join(MemberTable.MemberId.Minted "active", JoinableRole.Implementer, None))
        |> fun s -> step now s (CohortCommand.Join(MemberTable.MemberId.Minted "idle", JoinableRole.Implementer, None))
      let isActive (m: MemberTable.MemberId) = MemberTable.MemberId.display m = "active"
      DaemonMode.cohortMembersToRenew isActive s2.Members
      |> Expect.equal "only the Present, active member is renewed" [ MemberTable.MemberId.Minted "active" ]

    testCase "an ACTIVE member is never reaped, however long the daemon runs — the property the earlier attempt broke"
    <| fun _ ->
      let t0 = DateTime(2026, 1, 1, 12, 0, 0)
      let start =
        (CohortState.empty () : CohortState<string>)
        |> fun s -> step t0 s (CohortCommand.Join("busy", JoinableRole.Implementer, Some "sA"))
        |> fun s -> step t0 s (CohortCommand.Join("idle", JoinableRole.Implementer, Some "sB"))
        |> fun s -> step t0 s (CohortCommand.AcquireClaim("busy", ClaimScope.File "/repo/a.fs", "editing"))
        |> fun s -> step t0 s (CohortCommand.AcquireClaim("idle", ClaimScope.File "/repo/b.fs", "editing"))
      // 90 minutes of 60s ticks (3x the 30-min lease). "busy" active every tick.
      let final =
        [ 1 .. 90 ]
        |> List.fold (fun st minute -> reaperTick (fun who -> who = "busy") (t0.AddMinutes(float minute)) st) start
      presenceOf "busy" final |> Expect.equal "busy stays Present — renewed every tick" (Some MemberPresence.Present)
      // idle departs at the lease window (minute 30), and — settled history is
      // not kept forever (Cohort.Retention) — is purged one retention window
      // later (minute 60), so by minute 90 its seat and orphaned claim are gone.
      presenceOf "idle" final |> Expect.isNone "idle's departed seat is purged once the retention window has elapsed"
      // busy keeps its claim; idle's claim was orphaned by the reap, then pruned.
      heldClaims final |> Expect.equal "only busy's claim is still Held" 1
      final.Claims |> Map.count |> Expect.equal "idle's orphaned claim has aged out of state" 1
      // Midway (minute 45 — departed at 30, inside the retention window) the
      // seat is still visible as Departed, so the conductor can still act on it.
      let midway =
        [ 1 .. 45 ]
        |> List.fold (fun st minute -> reaperTick (fun who -> who = "busy") (t0.AddMinutes(float minute)) st) start
      match presenceOf "idle" midway with
      | Some (MemberPresence.Departed _) -> ()
      | other -> failtestf "idle should still be Departed inside the retention window, was %A" other

    testCase "an idle member is reaped exactly at the lease window, not before"
    <| fun _ ->
      let t0 = DateTime(2026, 1, 1, 12, 0, 0)
      let start = step t0 (CohortState.empty () : CohortState<string>) (CohortCommand.Join("idle", JoinableRole.Implementer, None))
      // One minute BEFORE the lease elapses: still Present.
      let before = reaperTick (fun _ -> false) (t0 + Cohort.leaseWindow - TimeSpan.FromMinutes 1.0) start
      presenceOf "idle" before |> Expect.equal "still Present just before the lease" (Some MemberPresence.Present)
      // At the lease window: reaped.
      let at = reaperTick (fun _ -> false) (t0 + Cohort.leaseWindow) start
      match presenceOf "idle" at with
      | Some (MemberPresence.Departed _) -> ()
      | other -> failtestf "idle should be Departed at the lease window, was %A" other
  ]
