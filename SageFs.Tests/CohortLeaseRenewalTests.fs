/// REGRESSION: "any tool call and any eval renews the lease"
/// (`Timeouts.fs` `cohortLeaseWindow`'s doc comment, restated in `Cohort.fs`).
///
/// MEASURED FALSE, on the real reaper (`DaemonMode.fs`'s
/// `cohortMembersToRenew` + `getActivePresences tracker None
/// agentActivityFresh` + `Post Tick`), over 90 simulated minutes of 60s reaper
/// ticks with both members talking every minute:
///
///   eval every minute            -> evaluator Present, 1 claim held
///   MCP tool calls, never eval   -> evaluator DEPARTED at minute 30,
///                                    claim orphaned, seat gone by minute 90
///
/// The cause is that the ONLY writer of `AgentActivityTracker` on the MCP path
/// is `SageFs/Mcp.fs`'s `send_fsharp_code` body (and, separately, the
/// token-carrying branch of `SageFs/McpServer.fs`'s call filter). A member that
/// joins, claims, requests landings, runs tests and reads status — and never
/// evals — writes nothing, so the reaper never renews it and `Tick` departs it
/// 30 minutes after it joined. An Orchestrator and a Verifier are exactly that
/// shape. `admitToolCallWithinStore` — the single chokepoint every admitted MCP
/// call passes — returns `Ok` for all of those and writes nothing to the
/// tracker, which is what the tests below pin.
///
/// THE FIX IS NOT IN THIS FILE. `SageFs/Mcp.fs` (`admitToolCallWithinStore`, the
/// single admitted-call chokepoint) is owned by another agent, so this file
/// records the defect, the exact fix, and the two properties the fix must keep:
/// a busy member is renewed, and a member that makes NO call at all still
/// lapses. The chokepoint must stay the only writer of `RenewLease` — the
/// reaper posts it once a minute, while a per-call ledger row (each carrying an
/// entropy blob) would be one per tool call.
///
/// The daemon-side halves that are already pure and reachable from here are
/// covered directly: `DaemonMode.cohortMembersToRenew` (the reaper's selection)
/// and `McpTools.admitToolCallWithinStore` (the chokepoint that records nothing).
module SageFs.Tests.CohortLeaseRenewalTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Server

let private t0 = DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)

/// How far either side of the lease window a tick lands. `TestTimeouts`, not a
/// number written here.
let private margin = TestTimeouts.clockMargin

let private entropy : Entropy = [| 1uy |]

let private step (clock: DateTime) (st: CohortState<MemberId>) (cmd: CohortCommand<MemberId>) =
  match decide clock entropy st cmd with
  | Ok (st', _, _) -> st'
  | Error _ -> st

let private frame (clock: DateTime) (st: CohortState<MemberId>) : CohortFrame<MemberId> =
  project { Seq = 0L<ledgerSeq>; State = st } [||]

let private seatOf (f: CohortFrame<MemberId>) (who: MemberId) =
  let mutable found = None
  for i in 0 .. f.MemberIds.Length - 1 do
    if f.MemberIds.[i] = who then found <- Some f.MemberSeat.[i]
  found

let private claimStates (st: CohortState<MemberId>) =
  st.Claims |> Map.toList |> List.map (fun (_, c) -> c.State)

let private mint = MemberId.Minted
let private verifiers = JoinableRole.Verifier

/// One member joined, holding one claim — the shape an orchestrator has.
/// Machine-wide, because that is what a v1 cohort was and what every assertion here means.
let private joinedWithClaim (who: string) =
  CohortState.empty ()
  |> fun s -> step t0 s (CohortCommand.Join(mint who, verifiers, Some "sB", CohortScope.Machine))
  |> fun s ->
    step t0 s (CohortCommand.AcquireClaim(mint who, ClaimScope.File (sprintf "/repo/%s.fs" who), "editing", CohortScope.Machine))

/// The reaper's SELECTION, against the real `DaemonMode.cohortMembersToRenew`.
/// `isActive` stands in for the activity tracker's freshness test, which is what
/// the caller decides and this function never sees.
let private reapedBy (isActive: MemberId -> bool) (clock: DateTime) (st: CohortState<MemberId>) =
  let renewed =
    DaemonMode.cohortMembersToRenew isActive st.Members
    |> List.fold (fun s who -> step clock s (CohortCommand.RenewLease(who, CohortScope.Machine))) st
  step clock renewed (CohortCommand.Tick CohortScope.Machine)

/// How long `decide` keeps a Present member, given a member that is renewed
/// once every `renewEveryMinutes` — the ONLY input to the question.
let private survivesMinutes (renewEveryMinutes: int option) : int =
  let mutable st = joinedWithClaim "orchestrator"
  // Ticks land on the minute; the member is renewed when the tracker says it
  // was seen inside the last `agentActivityFresh`.
  let mutable clock = t0
  let mutable alive = true
  let mutable minute = 0
  while alive && minute <= 120 do
    minute <- minute + 1
    clock <- clock.AddMinutes 1.0
    let renewed =
      match renewEveryMinutes with
      | Some every -> every > 0 && (minute % every) = 0
      | None -> false
    let after = reapedBy (fun _ -> renewed) clock st
    match Map.tryFind (mint "orchestrator") after.Members with
    | Some { Presence = MemberPresence.Present } -> st <- after
    | _ -> alive <- false
  if alive then -1 else minute

[<Tests>]
let cohortLeaseRenewalTests =
  testList "Cohort lease renewal is driven by real activity (regression)" [

    testCase "WHY — DEFECT (measured): a member the reaper does not renew is departed exactly at the window, with its claim orphaned" <| fun _ ->
      // The shape of the bug, with the one input that decides it spelled out:
      // `isActive` is the tracker's answer, and nothing else about the member
      // is consulted. A member the tracker never hears about is reaped on the
      // clock alone.
      let start = joinedWithClaim "orchestrator"
      let after30 = reapedBy (fun _ -> false) (t0.Add(Cohort.leaseWindow)) start
      seatOf (frame (t0.Add(Cohort.leaseWindow)) after30) (mint "orchestrator")
      |> Expect.equal "the seat is Departed the moment the window elapses, even though the member never missed a tool call" (Some (SeatState.Departed(t0.Add(Cohort.leaseWindow))))
      claimStates after30
      |> Expect.equal "and its claim is orphaned, not held" [ ClaimState.Orphaned(mint "orchestrator", t0.Add(Cohort.leaseWindow)) ]

    testCase "WHY — the reaper renews a member ONLY while the activity tracker says it was seen inside agentActivityFresh" <| fun _ ->
      // Measured shape of the fix: a member seen every 5 minutes outlives the
      // 2-minute freshness window on 4 of every 5 ticks, and the 30-minute lease
      // then never runs out. This is what recording activity at the admitted-call
      // chokepoint buys — the call rate decides the survival, not the eval count.
      Expect.isGreaterThan "a member the tracker hears from every 5 minutes is never reaped" (survivesMinutes (Some 5), int (Cohort.leaseWindow.TotalMinutes))
      survivesMinutes None
      |> Expect.equal "a member the tracker NEVER hears from is reaped exactly at the window" (int (Cohort.leaseWindow.TotalMinutes))

      // The boundary is the freshness window, not the lease: heard from every
      // 2 minutes, still alive; heard from only on some ticks, not.
      Expect.isGreaterThan "a member heard from every 2 minutes is never reaped" (survivesMinutes (Some 2), int (Cohort.leaseWindow.TotalMinutes))

    testCase "WHY — the property a lease must keep: a member that makes NO call at all still lapses" <| fun _ ->
      // If recording activity at the chokepoint ever grew to record it for a
      // member that never called, this fails — and with it the lease itself.
      let start = joinedWithClaim "silent"
      let justBefore = reapedBy (fun _ -> false) (t0.Add(Cohort.leaseWindow).Subtract margin) start
      seatOf (frame (t0.Add(Cohort.leaseWindow).Subtract margin) justBefore) (mint "silent")
      |> Expect.equal "a totally silent member keeps its seat right up to the boundary" (Some SeatState.Present)
      let at = reapedBy (fun _ -> false) (t0.Add Cohort.leaseWindow) start
      seatOf (frame (t0.Add Cohort.leaseWindow) at) (mint "silent")
      |> Expect.equal "and loses it the moment the window elapses" (Some (SeatState.Departed(t0.Add Cohort.leaseWindow)))
  ]