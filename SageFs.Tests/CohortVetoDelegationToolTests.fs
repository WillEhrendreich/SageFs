module SageFs.Tests.CohortVetoDelegationToolTests

// The four cohort commands that no tool issued are tools now: `delegate_conductor`, `withdraw_landing`,
// `veto_landing` and `resolve_veto`. Pure tables first (who may call what, how each routes), then the tool
// bodies through the real owner registry, then what `get_cohort_status` says afterwards.

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Affordances
open SageFs.Cohort
open SageFs.McpTools
open SageFs.MemberTable
open SageFs.Tests.ToolAuthorityGateTests

let newTools = [ "delegate_conductor"; "withdraw_landing"; "veto_landing"; "resolve_veto" ]

let alice = MemberId.Minted "alice"
let authorityOf (role: JoinableRole) = Authority.Member(alice, role)

let allowed (authority: Authority<MemberId>) (name: string) =
  match ToolName.tryParse name with
  | None -> failtestf "%s is not a ToolName" name
  | Some tool -> checkAuthorityAllowed authority tool |> Result.isOk

[<Tests>]
let tableTests =
  testList "the four tools are in every table a tool must be in" [

    testCase "WHY — each is a ToolName, so the authority gate knows it and an undeclared name is not admitted" <| fun _ ->
      for name in newTools do
        ToolName.tryParse name |> Option.isSome |> Expect.isTrue (sprintf "%s is a ToolName" name)
        ToolName.allToolNames |> Expect.contains (sprintf "%s is in the closed set" name) name

    testCase "WHY — each is a cohort verb, so the older cohort gate agrees with the whole-surface gate" <| fun _ ->
      for name in newTools do
        ToolName.tryParse name |> Option.bind ToolName.tryCohortTool |> Option.isSome
        |> Expect.isTrue (sprintf "%s is a CohortTool" name)
      CohortTool.all |> List.map CohortTool.toToolName |> Expect.containsAll "every new tool is named by CohortTool" newTools

    testCase "WHY — each has a tool class, so a member token is judged by class and never by an unclassified name" <| fun _ ->
      for name in newTools do
        Capability.ToolClass.ofTool name |> Option.isSome |> Expect.isTrue (sprintf "%s has a class" name)

    testCase "WHY — each routes by repository, so a token bound to a checkout is not refused it as unclassified" <| fun _ ->
      for name in newTools do
        Capability.RouteKind.ofTool name |> Expect.equal (sprintf "%s picks a cohort by directory" name) (Some Capability.RouteKind.InCohort)

    testCase "WHY — only the conductor delegates and clears a veto: neither is in any member token's classes" <| fun _ ->
      for name in [ "delegate_conductor"; "resolve_veto" ] do
        Capability.ToolClass.ofTool name |> Expect.equal (sprintf "%s is administration" name) (Some Capability.ToolClass.CohortAdmin)
        ToolRole.conductorOnlyTools |> Set.map ToolName.toString |> Expect.contains (sprintf "%s is conductor-only" name) name
        for role in [ JoinableRole.Observer; JoinableRole.Verifier; JoinableRole.Implementer ] do
          allowed (authorityOf role) name |> Expect.isFalse (sprintf "%A may not call %s" role name)
        allowed (Authority.Conductor alice) name |> Expect.isTrue (sprintf "the conductor may call %s" name)

    testCase "WHY — a veto is a verdict on a landing: a verifier and an implementer may issue one, an observer may not" <| fun _ ->
      allowed (authorityOf JoinableRole.Observer) "veto_landing" |> Expect.isFalse "an observer reads, it does not decide"
      allowed (authorityOf JoinableRole.Verifier) "veto_landing" |> Expect.isTrue "a verifier objects: it is the role that reads the tests"
      allowed (authorityOf JoinableRole.Implementer) "veto_landing" |> Expect.isTrue "an implementer objects to another's landing"
      allowed (Authority.Conductor alice) "veto_landing" |> Expect.isTrue "the conductor may"
      allowed Authority.Anonymous "veto_landing" |> Expect.isFalse "someone with no seat may not"

    testCase "WHY — a veto is in the Verifier token preset's classes, so a verifier TOKEN can issue it too, and an analysis token cannot" <| fun _ ->
      let classOf = Capability.ToolClass.ofTool "veto_landing" |> Option.get
      Capability.RolePreset.toolClasses Capability.RolePreset.Verifier |> Expect.contains "verifier token" classOf
      Capability.RolePreset.toolClasses Capability.RolePreset.Implementer |> Expect.contains "implementer token" classOf
      Capability.RolePreset.toolClasses Capability.RolePreset.Analysis
      |> Set.contains classOf
      |> Expect.isFalse "an analysis token reads and analyses, it does not object"

    testCase "WHY — withdrawing is the requester's own act, so it needs the working surface (the role that can request a landing)" <| fun _ ->
      allowed (authorityOf JoinableRole.Implementer) "withdraw_landing" |> Expect.isTrue "an implementer requests landings, so withdraws its own"
      allowed (authorityOf JoinableRole.Verifier) "withdraw_landing" |> Expect.isFalse "a verifier cannot request one, so has none to withdraw"
      allowed (authorityOf JoinableRole.Observer) "withdraw_landing" |> Expect.isFalse "an observer reads"
      allowed (Authority.Conductor alice) "withdraw_landing" |> Expect.isTrue "the conductor may (it is an implementer's act too)"
  ]

let landingIdFrom (text: string) : string =
  let m = System.Text.RegularExpressions.Regex.Match(text, @"Landing (\S+) queued")
  if not m.Success then failtestf "no landing id in %s" text
  m.Groups.[1].Value

let ok (what: string) (result: Result<string, SageFsError>) : string =
  match result with
  | Result.Ok text -> text
  | Result.Error err -> failtestf "%s failed: %s" what (SageFsError.describeForAgent err)

let refused (what: string) (result: Result<string, SageFsError>) : string =
  match result with
  | Result.Ok text -> failtestf "%s should have been refused, got: %s" what text
  | Result.Error err -> SageFsError.describeForAgent err

let repo = molinaRepo
let wd = Some molinaRepo

let status (ctx: McpContext) : System.Threading.Tasks.Task<string> = task {
  match! McpCohortIntegration.getCohortStatus ctx wd with
  | Result.Ok text -> return text
  | Result.Error err -> return failtestf "status: %s" (SageFsError.describeForAgent err)
}

/// alice (conductor), bob (implementer), vera (verifier), olive (observer), with a landing of bob's.
let scene () = task {
  let ctx = mkContext ()
  do! join ctx "alice" "Implementer" repo
  do! join ctx "bob" "Implementer" repo
  do! join ctx "vera" "Verifier" repo
  do! join ctx "olive" "Observer" repo
  let! queued = McpCohortTools.requestLanding ctx "bob" "" "abc123" "land bob's change" wd
  return ctx, landingIdFrom (ok "request_landing" queued)
}

[<Tests>]
let toolBodyTests =
  testList "the tool bodies keep the rules the cohort already enforces, and say what to do" [

    testTask "a verifier's veto blocks the landing and get_cohort_status names the vetoer, the reason and the way out" {
      let! ctx, lid = scene ()
      let! vetoed = McpCohortTools.vetoLanding ctx "vera" lid "the migration is not ready" wd
      ok "veto_landing" vetoed |> Expect.stringContains "says which landing" lid
      let! text = status ctx
      text |> Expect.stringContains "names the vetoer" "vera"
      text |> Expect.stringContains "gives the reason" "the migration is not ready"
      text |> Expect.stringContains "says it waits for the conductor, and the tool that clears it" "resolve_veto"
    }

    testTask "an observer's veto is refused and says why and what to do" {
      let! ctx, lid = scene ()
      let! result = McpCohortTools.vetoLanding ctx "olive" lid "no" wd
      let text = refused "an observer's veto" result
      text |> Expect.stringContains "says the role is read-only" "Observer"
    }

    testTask "a veto with no reason is refused" {
      let! ctx, lid = scene ()
      let! result = McpCohortTools.vetoLanding ctx "vera" lid "  " wd
      refused "a blank reason" result |> Expect.stringContains "says a reason is needed" "reason"
    }

    testTask "a veto after the landing was withdrawn is refused, not swallowed" {
      let! ctx, lid = scene ()
      let! withdrawn = McpCohortTools.withdrawLanding ctx "bob" (LandingId lid) wd
      ok "withdraw_landing" withdrawn |> ignore
      let! result = McpCohortTools.vetoLanding ctx "vera" lid "too late" wd
      refused "a veto of a withdrawn landing" result |> Expect.stringContains "names the landing" lid
    }

    testTask "only the requester withdraws, and the refusal says who may" {
      let! ctx, lid = scene ()
      let! result = McpCohortTools.withdrawLanding ctx "alice" (LandingId lid) wd
      refused "the conductor withdrawing bob's landing" result |> Expect.stringContains "points at the requester" "requester"
      let! mine = McpCohortTools.withdrawLanding ctx "bob" (LandingId lid) wd
      ok "the requester withdrawing" mine |> Expect.stringContains "confirms" lid
    }

    testTask "only the conductor clears a veto, and then the landing is queued again" {
      let! ctx, lid = scene ()
      // A reason no other status line can contain: "hold" is in the lease line of every status ("a member that holds an open push stream").
      let vetoReason = "veto-reason-7f3a91"
      let! _ = McpCohortTools.vetoLanding ctx "vera" lid vetoReason wd
      let! notConductor = McpCohortTools.resolveVeto ctx "bob" (LandingId lid) wd
      refused "a member clearing a veto" notConductor |> Expect.stringContains "says conductor-only" "conductor"
      let! cleared = McpCohortTools.resolveVeto ctx "alice" (LandingId lid) wd
      ok "resolve_veto" cleared |> Expect.stringContains "confirms" lid
      let! (text: string) = status ctx
      text.Contains vetoReason |> Expect.isFalse "the veto is gone from the status"
    }

    testTask "delegate_conductor moves the seat, and get_cohort_status says who holds it now" {
      let! ctx, _ = scene ()
      let! moved = McpCohortTools.delegateConductor ctx "alice" "bob" wd
      ok "delegate_conductor" moved |> Expect.stringContains "names the new conductor" "bob"
      let! (text: string) = status ctx
      // The line names the holder; a holder whose lease is silent is shown as "held by bob, silent for ...", so the line is read for the name.
      let conductorLine = text.Split '\n' |> Array.find (fun line -> line.StartsWith "Conductor: ")
      conductorLine |> Expect.stringContains "the seat is bob's" "bob"
      let! again = McpCohortTools.delegateConductor ctx "alice" "vera" wd
      refused "the old conductor delegating again" again |> Expect.stringContains "says conductor-only" "conductor"
    }

    testTask "delegating to someone who is not here lists who is" {
      let! ctx, _ = scene ()
      let! result = McpCohortTools.delegateConductor ctx "alice" "nobody-here" wd
      let text = refused "delegating to an absent member" result
      for present in [ "alice"; "bob"; "vera"; "olive" ] do
        text |> Expect.stringContains (sprintf "the roster names %s" present) present
    }

    testTask "a vacant seat refusal says nobody can fill it from here, and who could" {
      let! ctx, _ = scene ()
      let! _ = McpCohortTools.leaveCohort ctx "alice" wd
      let! result = McpCohortTools.delegateConductor ctx "bob" "vera" wd
      let text = refused "delegating from a vacant seat" result
      text |> Expect.stringContains "says the seat is vacant" "seat is vacant"
      text |> Expect.stringContains "says why no member can fill it" "delegate_conductor"
    }
  ]

let claimIdAndFence (text: string) : string * string =
  let m = System.Text.RegularExpressions.Regex.Match(text, @"claim (\S+) over \S+ \(fence=(\d+)\)")
  if not m.Success then failtestf "no claim id and fence in %s" text
  m.Groups.[1].Value, m.Groups.[2].Value

[<Tests>]
let claimToolTests =
  testList "acquire_claim and leave_cohort say what the caller already holds" [

    testTask "WHY — asking again for a claim you hold answers with that claim and its fence, and renews the lease" {
      let! ctx, _ = scene ()
      let! first = McpCohortTools.acquireClaim ctx "bob" "file:src/A.fs" "edit A" wd
      let firstId, firstFence = claimIdAndFence (ok "the first acquire_claim" first)
      let! again = McpCohortTools.acquireClaim ctx "bob" "file:src/A.fs" "edit A" wd
      let text = ok "the repeat acquire_claim" again
      text |> Expect.stringContains "says the caller already holds it" "already hold"
      claimIdAndFence text |> Expect.equal "names the same claim and the fence release_claim needs" (firstId, firstFence)
      text |> Expect.stringContains "says the lease is renewed" "renewed"
    }

    testTask "WHY — the conductor holding a claim is refused leave_cohort with the claim and the way out, and stays seated" {
      let! ctx, _ = scene ()
      let! claimed = McpCohortTools.acquireClaim ctx "alice" "file:src/A.fs" "edit A" wd
      let claimId, _ = claimIdAndFence (ok "alice's acquire_claim" claimed)
      let! left = McpCohortTools.leaveCohort ctx "alice" wd
      let text = refused "the conductor leaving with a claim" left
      text |> Expect.stringContains "names the claim it would orphan" claimId
      text |> Expect.stringContains "says to hand the seat on first" "delegate_conductor"
      text |> Expect.stringContains "or to release the claim" "release_claim"
      let! (status: string) = status ctx
      let conductorLine = status.Split '\n' |> Array.find (fun line -> line.StartsWith "Conductor: ")
      conductorLine |> Expect.stringContains "alice still holds the seat" "alice"
    }

    testTask "WHY — a member leaving with a claim is told which claim it orphaned, over which scope, and who hands it on" {
      let! ctx, _ = scene ()
      let! claimed = McpCohortTools.acquireClaim ctx "bob" "file:src/B.fs" "edit B" wd
      let claimId, _ = claimIdAndFence (ok "bob's acquire_claim" claimed)
      let! left = McpCohortTools.leaveCohort ctx "bob" wd
      let text = ok "bob's leave_cohort" left
      text |> Expect.stringContains "names the orphaned claim" claimId
      text |> Expect.stringContains "names its scope" "file:src/B.fs"
      text |> Expect.stringContains "says who hands it on" "reassign_claim"
    }

    test "WHY — decide refuses Depart for the conductor holding a claim, and leaves every other departure alone" {
      let alice, bob = MemberId.Minted "alice", MemberId.Minted "bob"
      let at = DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc)
      let step (state: CohortState<MemberId>) cmd =
        match Cohort.decide at [| 1uy |] state cmd with
        | Result.Ok(next, _, _) -> next
        | Result.Error err -> failtestf "%A was refused: %A" cmd err
      let scope = CohortScope.Machine
      let joined =
        [ CohortCommand.Join(alice, JoinableRole.Implementer, None, scope); CohortCommand.Join(bob, JoinableRole.Implementer, None, scope) ]
        |> List.fold step (CohortState.empty ())
      let claimed = step joined (CohortCommand.AcquireClaim(alice, ClaimScope.File "src/A.fs", "edit A", scope))
      let claimId = claimed.Claims |> Map.toList |> List.head |> fst
      Cohort.decide at [||] claimed (CohortCommand.Depart(alice, scope))
      |> Result.map ignore
      |> Expect.equal "the conductor holding a claim may not leave" (Result.Error(CohortError.ConductorHoldsClaims(alice, [ claimId ])))
      Cohort.decide at [||] joined (CohortCommand.Depart(alice, scope)) |> Result.isOk |> Expect.isTrue "a conductor with no claim may leave"
      let bobClaimed = step joined (CohortCommand.AcquireClaim(bob, ClaimScope.File "src/B.fs", "edit B", scope))
      Cohort.decide at [||] bobClaimed (CohortCommand.Depart(bob, scope)) |> Result.isOk |> Expect.isTrue "a member with a claim may leave"
    }
  ]

[<Tests>]
let refusalTextTests =
  testList "a cohort refusal says what happened, from the facts the cohort holds" [

    testTask "WHY — a second join names the agentName, says sub-agents on one connection are one member, and points to mint_member" {
      let! ctx, _ = scene ()
      let! again = McpCohortTools.joinCohort ctx "bob" "Implementer" wd
      let text = refused "a second join" again
      text |> Expect.stringContains "names the agentName passed" "agentName 'bob'"
      text |> Expect.stringContains "says sub-agents share the member" "Sub-agents"
      text |> Expect.stringContains "points to a token of its own" "mint_member"
    }

    testTask "WHY — a member that left is told when, and that its claims were orphaned, and one that never joined is told to join" {
      let! ctx, _ = scene ()
      let! _ = McpCohortTools.leaveCohort ctx "bob" wd
      let! afterLeaving = McpCohortTools.acquireClaim ctx "bob" "file:src/B.fs" "edit B" wd
      let left = refused "a claim by a member that left" afterLeaving
      left |> Expect.stringContains "says it left, and when" "left this cohort at"
      left |> Expect.stringContains "says what leaving cost" "orphaned"
      let! stranger = McpCohortTools.acquireClaim ctx "zed" "file:src/Z.fs" "edit Z" wd
      refused "a claim by a stranger" stranger |> Expect.stringContains "says it never joined" "has not joined"
    }

    test "WHY — a stale fence says the claim moved on and how to take the scope again, with no advice to retry the fence" {
      let text =
        match CohortErrorMapping.toSageFsError (Cohort.CohortError.StaleClaimFence(ClaimId "c-1", 1L<Measures.fence>, 2L<Measures.fence>)) with
        | SageFsError.CohortRefused(CohortFailureKind.StaleClaimFence, reason, suggestion) -> reason + " " + suggestion
        | other -> failtestf "expected CohortRefused(StaleClaimFence), got %A" other
      text |> Expect.stringContains "says it was released, cleared or handed on" "released, cleared"
      text |> Expect.stringContains "says where to see the holder" "get_cohort_status"
      text |> Expect.stringContains "says how to take the scope" "acquire_claim"
      text.Contains "retry with" |> Expect.isFalse "a fence that moved on is never right again"
    }

    test "WHY — a pruned claim and a pruned landing say they finished and when, never that they do not exist" {
      let at = DateTime(2026, 11, 11, 1, 0, 0, DateTimeKind.Utc)
      for err in [ Cohort.CohortError.PrunedClaim(ClaimId "c-1", ClaimPruneReason.ReleasedPastRetention at); Cohort.CohortError.PrunedLanding(LandingId "l-1", at) ] do
        match CohortErrorMapping.toSageFsError err with
        | SageFsError.CohortRefused(_, reason, _) ->
          reason |> Expect.stringContains "says when" "2026-11-11 01:00:00Z"
          reason.Contains "exist" |> Expect.isFalse "it existed"
        | other -> failtestf "expected CohortRefused, got %A" other
    }
  ]
