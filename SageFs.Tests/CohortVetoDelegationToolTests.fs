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
      let! withdrawn = McpCohortTools.withdrawLanding ctx "bob" lid wd
      ok "withdraw_landing" withdrawn |> ignore
      let! result = McpCohortTools.vetoLanding ctx "vera" lid "too late" wd
      refused "a veto of a withdrawn landing" result |> Expect.stringContains "names the landing" lid
    }

    testTask "only the requester withdraws, and the refusal says who may" {
      let! ctx, lid = scene ()
      let! result = McpCohortTools.withdrawLanding ctx "alice" lid wd
      refused "the conductor withdrawing bob's landing" result |> Expect.stringContains "points at the requester" "requester"
      let! mine = McpCohortTools.withdrawLanding ctx "bob" lid wd
      ok "the requester withdrawing" mine |> Expect.stringContains "confirms" lid
    }

    testTask "only the conductor clears a veto, and then the landing is queued again" {
      let! ctx, lid = scene ()
      let! _ = McpCohortTools.vetoLanding ctx "vera" lid "hold" wd
      let! notConductor = McpCohortTools.resolveVeto ctx "bob" lid wd
      refused "a member clearing a veto" notConductor |> Expect.stringContains "says conductor-only" "conductor"
      let! cleared = McpCohortTools.resolveVeto ctx "alice" lid wd
      ok "resolve_veto" cleared |> Expect.stringContains "confirms" lid
      let! (text: string) = status ctx
      text.Contains("hold") |> Expect.isFalse "the veto is gone from the status"
    }

    testTask "delegate_conductor moves the seat, and get_cohort_status says who holds it now" {
      let! ctx, _ = scene ()
      let! moved = McpCohortTools.delegateConductor ctx "alice" "bob" wd
      ok "delegate_conductor" moved |> Expect.stringContains "names the new conductor" "bob"
      let! text = status ctx
      text |> Expect.stringContains "the seat is bob's" "Conductor: bob"
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
      text |> Expect.stringContains "says the seat is vacant" "VACANT"
      text |> Expect.stringContains "says why no member can fill it" "delegate_conductor"
    }
  ]
