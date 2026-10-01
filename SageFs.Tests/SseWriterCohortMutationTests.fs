/// ## SseWriter Cohort-Events Mutation Tests
///
/// CohortSseEventsTests.fs round-trips `formatCohortMatrixEvent`/
/// `formatClaimChangedEvent`/`formatLandingChangedEvent`, but only exercises
/// ONE case of each closed DU->wire-string mapping SseWriter.fs's private
/// `claimStateToWire`/`landingBlockerToWire`/`nextActionToWire`/
/// `landingStateKind` helpers implement: `ClaimState.Held`,
/// `LandingBlocker.FailingTests`, `NextAction.FixTests`, and
/// `LandingState.Blocked`/`Rebasing`. The other cases of each DU — reachable
/// only through the PUBLIC formatters, since the mapping helpers themselves
/// are private — have zero test coverage anywhere: a swapped or dropped
/// match arm (e.g. `"rebase_conflict"` and `"stale_claim_fence"` swapped, or
/// `LandingState.Landed` mapped to `"landed"` instead of `"withdrawn"`)
/// would silently corrupt the wire protocol every editor client parses,
/// undetected by any test in the suite. This file pins every remaining case
/// via direct record construction (`LandingRequest`/`Claim` are plain public
/// records — no need to drive `Cohort.decide` to reach an exotic state) and,
/// for the two `ClaimState` cases `formatClaimChangedEvent` doesn't reach
/// (`claimStateToWire` is only actually called from `formatCohortMatrixEvent`),
/// via `Cohort.decide`/`Cohort.project`. Each case asserts EXACT equality
/// against the correct value so a mutant that returns any other wrong value
/// is killed too.
module SseWriterCohortMutationTests

open System
open System.Text.Json
open Expecto
open Expecto.Flip

let private jsonOpts = SageFs.Json.optionsOf SageFs.Json.camelCase

let private clock = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private noEntropy : byte[] = [||]
let private alice = SageFs.MemberTable.MemberId.Minted "alice"
let private bob = SageFs.MemberTable.MemberId.Minted "bob"

let private dataOf (sse: string) : string =
  sse.Split('\n')
  |> Array.choose (fun l -> if l.StartsWith("data: ") then Some (l.Substring 6) else None)
  |> String.concat "\n"

let private baseLanding : SageFs.Cohort.LandingRequest<SageFs.MemberTable.MemberId> =
  { Id = SageFs.Cohort.LandingId "l-test"
    Requester = alice
    Claims = []
    Commits = [ "abc123" ]
    BaseAtQueue = "base-sha"
    Statement =
      SageFs.Cohort.Statement.tryCreate "test landing"
      |> function Ok s -> s | Error e -> failwith e
    State = SageFs.Cohort.LandingState.Queued
    FastForwardAttempts = 0
    Settlement = SageFs.Cohort.LandingSettlement.Unsettled }

let private landingStateWire (state: SageFs.Cohort.LandingState<SageFs.MemberTable.MemberId>) : string =
  let landing = { baseLanding with State = state }
  SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing
  |> dataOf
  |> JsonDocument.Parse
  |> fun doc -> doc.RootElement.GetProperty("state").GetString()

let sseWriterCohortMutationTests = testList "SseWriter cohort-events mutations" [

  testList "landingStateKind — every LandingState case" [
    testCase "WHY — queued_state_maps_to_queued — LandingState.Queued must map to the wire string \"queued\"" <| fun () ->
      landingStateWire SageFs.Cohort.LandingState.Queued
      |> Expect.equal "Queued must map to \"queued\"" "queued"

    testCase "WHY — rebasing_state_maps_to_rebasing — LandingState.Rebasing must map to the wire string \"rebasing\"" <| fun () ->
      landingStateWire (SageFs.Cohort.LandingState.Rebasing "onto-sha")
      |> Expect.equal "Rebasing must map to \"rebasing\"" "rebasing"

    testCase "WHY — verifying_state_maps_to_verifying — LandingState.Verifying must map to the wire string \"verifying\"" <| fun () ->
      landingStateWire (SageFs.Cohort.LandingState.Verifying("base-sha", "head-sha", 3, 1))
      |> Expect.equal "Verifying must map to \"verifying\"" "verifying"

    testCase "WHY — landed_state_maps_to_landed — LandingState.Landed must map to the wire string \"landed\", not confused with any other terminal state" <| fun () ->
      landingStateWire (SageFs.Cohort.LandingState.Landed "committed-sha")
      |> Expect.equal "Landed must map to \"landed\"" "landed"

    testCase "WHY — withdrawn_state_maps_to_withdrawn — LandingState.Withdrawn must map to the wire string \"withdrawn\", not confused with Landed" <| fun () ->
      landingStateWire SageFs.Cohort.LandingState.Withdrawn
      |> Expect.equal "Withdrawn must map to \"withdrawn\"" "withdrawn"

    testCase "WHY — blocked_state_maps_to_blocked — LandingState.Blocked must map to the wire string \"blocked\"" <| fun () ->
      landingStateWire (
        SageFs.Cohort.LandingState.Blocked(
          SageFs.Cohort.LandingBlocker.RebaseConflict [ "f.fs" ], SageFs.Cohort.NextAction.RebaseAndResubmit))
      |> Expect.equal "Blocked must map to \"blocked\"" "blocked"
  ]

  testList "landingBlockerToWire — every LandingBlocker case" [
    testCase "WHY — rebase_conflict_blocker_kind_and_files — RebaseConflict must carry kind \"rebase_conflict\" and its file list, not the stale_claim_fence/head_moved shape" <| fun () ->
      let landing = { baseLanding with State = SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.RebaseConflict [ "A.fs"; "B.fs" ], SageFs.Cohort.NextAction.RebaseAndResubmit) }
      let doc = SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing |> dataOf |> JsonDocument.Parse
      let blocker = doc.RootElement.GetProperty("blocker")
      blocker.GetProperty("kind").GetString() |> Expect.equal "kind must be rebase_conflict" "rebase_conflict"
      blocker.GetProperty("files").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
      |> Expect.equal "files must carry the real conflicting files, in order" [ "A.fs"; "B.fs" ]

    testCase "WHY — stale_claim_fence_blocker_kind_and_claimId — StaleClaimFence must carry kind \"stale_claim_fence\" and the stale claim's id" <| fun () ->
      let landing = { baseLanding with State = SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.StaleClaimFence(SageFs.Cohort.ClaimId "c-stale"), SageFs.Cohort.NextAction.AwaitConductor) }
      let doc = SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing |> dataOf |> JsonDocument.Parse
      let blocker = doc.RootElement.GetProperty("blocker")
      blocker.GetProperty("kind").GetString() |> Expect.equal "kind must be stale_claim_fence" "stale_claim_fence"
      blocker.GetProperty("claimId").GetString() |> Expect.equal "claimId must be the stale claim's id" "c-stale"

    testCase "WHY — head_moved_blocker_kind_and_from_to — HeadMoved must carry kind \"head_moved\" and the from/to shas, not swapped" <| fun () ->
      let landing = { baseLanding with State = SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.HeadMoved("old-head", "new-head"), SageFs.Cohort.NextAction.RebaseAndResubmit) }
      let doc = SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing |> dataOf |> JsonDocument.Parse
      let blocker = doc.RootElement.GetProperty("blocker")
      blocker.GetProperty("kind").GetString() |> Expect.equal "kind must be head_moved" "head_moved"
      blocker.GetProperty("from").GetString() |> Expect.equal "from must be the sha the landing was verified against" "old-head"
      blocker.GetProperty("to").GetString() |> Expect.equal "to must be the sha the head actually moved to" "new-head"

    testCase "WHY — vetoed_by_blocker_kind_by_and_reason — VetoedBy must carry kind \"vetoed_by\", the vetoing member's display name, and the reason" <| fun () ->
      let landing = { baseLanding with State = SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.VetoedBy(bob, "not ready"), SageFs.Cohort.NextAction.AwaitConductor) }
      let doc = SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing |> dataOf |> JsonDocument.Parse
      let blocker = doc.RootElement.GetProperty("blocker")
      blocker.GetProperty("kind").GetString() |> Expect.equal "kind must be vetoed_by" "vetoed_by"
      blocker.GetProperty("by").GetString() |> Expect.equal "by must be the vetoing member, displayed" "bob"
      blocker.GetProperty("reason").GetString() |> Expect.equal "reason must be carried verbatim" "not ready"
  ]

  testList "nextActionToWire — every NextAction case" [
    testCase "WHY — rebase_and_resubmit_next_action_kind — NextAction.RebaseAndResubmit must map to kind \"rebase_and_resubmit\"" <| fun () ->
      let landing = { baseLanding with State = SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.RebaseConflict [], SageFs.Cohort.NextAction.RebaseAndResubmit) }
      let doc = SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing |> dataOf |> JsonDocument.Parse
      doc.RootElement.GetProperty("nextAction").GetProperty("kind").GetString()
      |> Expect.equal "kind must be rebase_and_resubmit" "rebase_and_resubmit"

    testCase "WHY — await_conductor_next_action_kind — NextAction.AwaitConductor must map to kind \"await_conductor\", not confused with rebase_and_resubmit" <| fun () ->
      let landing = { baseLanding with State = SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.VetoedBy(bob, "x"), SageFs.Cohort.NextAction.AwaitConductor) }
      let doc = SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing |> dataOf |> JsonDocument.Parse
      doc.RootElement.GetProperty("nextAction").GetProperty("kind").GetString()
      |> Expect.equal "kind must be await_conductor" "await_conductor"

    testCase "WHY — withdraw_next_action_kind — NextAction.Withdraw must map to kind \"withdraw\"" <| fun () ->
      let landing = { baseLanding with State = SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.RebaseConflict [], SageFs.Cohort.NextAction.Withdraw) }
      let doc = SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing |> dataOf |> JsonDocument.Parse
      doc.RootElement.GetProperty("nextAction").GetProperty("kind").GetString()
      |> Expect.equal "kind must be withdraw" "withdraw"
  ]

  testList "claimStateToWire (via formatCohortMatrixEvent) — Orphaned and Released" [
    testCase "WHY — orphaned_claim_wire_kind_and_holder — an Orphaned claim must project as kind \"orphaned\" naming the PREVIOUS holder, with a non-null since" <| fun () ->
      let applyOk state cmd =
        match SageFs.Cohort.decide clock noEntropy state cmd with
        | Ok(s, _, _) -> s
        | Error e -> failwithf "unexpected cohort decide error: %A" e
      let state =
        SageFs.Cohort.CohortState.empty ()
        |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.Join(alice, SageFs.Cohort.JoinableRole.Implementer, None))
        |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.AcquireClaim(alice, SageFs.Cohort.ClaimScope.File "src/Orphan.fs", "testing"))
        |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.Depart alice)
      let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> = { Seq = 3L<SageFs.Measures.ledgerSeq>; State = state }
      let frame = SageFs.Cohort.project head [||]
      let doc = SageFs.SseWriter.formatCohortMatrixEvent jsonOpts frame |> dataOf |> JsonDocument.Parse
      let claim = doc.RootElement.GetProperty("claims").EnumerateArray() |> Seq.exactlyOne
      let claimState = claim.GetProperty("state")
      claimState.GetProperty("kind").GetString() |> Expect.equal "kind must be orphaned" "orphaned"
      claimState.GetProperty("holder").GetString() |> Expect.equal "holder must name the previous (departed) holder" "alice"
      claimState.GetProperty("since").ValueKind |> Expect.notEqual "since must be populated for an orphaned claim" JsonValueKind.Null

    testCase "WHY — released_claim_wire_kind_and_holder — a Released claim must project as kind \"released\" naming the releaser, with a non-null since" <| fun () ->
      let applyOk state cmd =
        match SageFs.Cohort.decide clock noEntropy state cmd with
        | Ok(s, _, _) -> s
        | Error e -> failwithf "unexpected cohort decide error: %A" e
      let state0 =
        SageFs.Cohort.CohortState.empty ()
        |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.Join(alice, SageFs.Cohort.JoinableRole.Implementer, None))
        |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.AcquireClaim(alice, SageFs.Cohort.ClaimScope.File "src/Rel.fs", "testing"))
      let claimId, claim = state0.Claims |> Map.toList |> List.exactlyOne
      let state1 = applyOk state0 (SageFs.Cohort.CohortCommand.ReleaseClaim(alice, claimId, claim.Fence))
      let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> = { Seq = 4L<SageFs.Measures.ledgerSeq>; State = state1 }
      let frame = SageFs.Cohort.project head [||]
      let doc = SageFs.SseWriter.formatCohortMatrixEvent jsonOpts frame |> dataOf |> JsonDocument.Parse
      let claimEl = doc.RootElement.GetProperty("claims").EnumerateArray() |> Seq.exactlyOne
      let claimState = claimEl.GetProperty("state")
      claimState.GetProperty("kind").GetString() |> Expect.equal "kind must be released" "released"
      claimState.GetProperty("holder").GetString() |> Expect.equal "holder must name the releasing member" "alice"
      claimState.GetProperty("since").ValueKind |> Expect.notEqual "since must be populated for a released claim" JsonValueKind.Null
  ]

  testList "claimScopeToWire (via formatClaimChangedEvent) — Project" [
    testCase "WHY — project_scope_wire_kind_and_path — ClaimScope.Project must project as kind \"project\" (not \"file\") carrying the project path" <| fun () ->
      let applyOk state cmd =
        match SageFs.Cohort.decide clock noEntropy state cmd with
        | Ok(s, _, _) -> s
        | Error e -> failwithf "unexpected cohort decide error: %A" e
      let state =
        SageFs.Cohort.CohortState.empty ()
        |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.Join(alice, SageFs.Cohort.JoinableRole.Implementer, None))
        |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.AcquireClaim(alice, SageFs.Cohort.ClaimScope.Project "SageFs.Core/SageFs.Core.fsproj", "testing"))
      let _, claim = state.Claims |> Map.toList |> List.exactlyOne
      let doc = SageFs.SseWriter.formatClaimChangedEvent jsonOpts "acquired" claim |> dataOf |> JsonDocument.Parse
      let scope = doc.RootElement.GetProperty("scope")
      scope.GetProperty("kind").GetString() |> Expect.equal "kind must be project, not file" "project"
      scope.GetProperty("path").GetString() |> Expect.equal "path must be the project path" "SageFs.Core/SageFs.Core.fsproj"
  ]
]
