/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "Territory map"):
/// pure render tests for the territory section wired into
/// `renderCohortPanel` (`DashboardFragments.fs`). A separate file from
/// `CohortPanelTests.fs` so this island's tests never collide with edits
/// another agent makes to that file for the matrix/member/claim sections.
module SageFs.Tests.CohortTerritoryPanelTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Server.DashboardFragments

let private render (node: XmlNode) = renderNode node

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

let private frameAfter (commands: CohortCommand<MemberId> list) : CohortFrame<MemberId> =
  let finalState, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.empty (), 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } [||]

let private emptyFrame : CohortFrame<MemberId> = project (replayHead []) [||]

[<Tests>]
let cohortTerritoryPanelTests =
  testList "Cohort dashboard panel — territory map" [

    testCase "WHY — an empty cohort (no claims) renders no territory section at all" <| fun _ ->
      let html = renderCohortPanel emptyFrame |> render
      (html.Contains "cohort-territory") |> Expect.isFalse "no dom id when nothing is claimed"
      (html.Contains "Territory map") |> Expect.isFalse "no heading when nothing is claimed"

    testCase "WHY — a held claim renders the territory section carrying the claimed path" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing") ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "carries the territory dom id" "cohort-territory"
      html |> Expect.stringContains "carries the section heading" "Territory map (1 claimed path)"
      html |> Expect.stringContains "embeds an inline svg, no served route" "<svg "
      html |> Expect.stringContains "the claim's path appears in the svg" "src/Foo.fs"

    testCase "WHY — two members' held claims each render as distinctly colored territory" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.Join(bob, JoinableRole.Verifier, None)
            CohortCommand.AcquireClaim(alice, ClaimScope.File "src/A.fs", "a")
            CohortCommand.AcquireClaim(bob, ClaimScope.File "src/B.fs", "b") ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "carries the section heading, pluralized" "Territory map (2 claimed paths)"
      html |> Expect.stringContains "carries alice's claimed path" "src/A.fs"
      html |> Expect.stringContains "carries bob's claimed path" "src/B.fs"

    testCase "WHY — an orphaned claim still renders as neutral territory, not dropped" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
            CohortCommand.Depart alice ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "the orphaned claim is still shown as territory" "src/Foo.fs"
      html |> Expect.stringContains "the text-fallback legend marks it unclaimed" "unclaimed"

    testCase "WHY — the text fallback legend names the holder by display id, not the raw MemberId case" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.AcquireClaim(alice, ClaimScope.Project "SageFs.Tests/SageFs.Tests.fsproj", "working") ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "labels the text fallback" "Text fallback"
      html |> Expect.stringContains "legend names the holder's display id" (MemberId.display alice)

    testCase "WHY — renderCohortPanel with claims but no territory-eligible state stays within the panel's own dom id" <| fun _ ->
      // A member joins but claims nothing: the panel renders (members section),
      // the territory section must not appear.
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Observer, None) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "the panel itself still renders" "cohort-panel"
      (html.Contains "cohort-territory") |> Expect.isFalse "no claims, no territory section"
  ]
