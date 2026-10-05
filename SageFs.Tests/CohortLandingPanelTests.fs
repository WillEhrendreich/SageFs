/// The dashboard's cohort panel lists the landings, and a vetoed one says who vetoed it and why.
///
/// The panel showed members and claims and never a landing, so a veto (now a tool) was visible in
/// `get_cohort_status` and nowhere on screen. Pure render over a frame built by the real `decide`, like
/// CohortPanelTests.
module SageFs.Tests.CohortLandingPanelTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Server.DashboardFragments

let epoch = DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)
let alice = MemberId.Minted "alice"
let bob = MemberId.Minted "bob"
let machine = CohortScope.Machine

let frameAfter (commands: CohortCommand<MemberId> list) : CohortFrame<MemberId> =
  let finalState, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.forScope machine, 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } [||]

let seated =
  [ CohortCommand.Join(alice, JoinableRole.Implementer, None, machine)
    CohortCommand.Join(bob, JoinableRole.Implementer, None, machine)
    CohortCommand.RequestLanding(bob, [], [ "abc" ], "land bob's change", machine) ]

let landingIdOf (frame: CohortFrame<MemberId>) : LandingId = frame.LandingIds.[0]

let render (frame: CohortFrame<MemberId>) : string = renderCohortPanel frame |> renderNode

[<Tests>]
let tests =
  testList "Cohort panel landings" [

    testCase "WHY — a landing is listed with its requester and state, so the queue is on screen and not only in a tool reply" <| fun _ ->
      let html = render (frameAfter seated)
      html |> Expect.stringContains "the landings header carries the total" "Landings (1)"
      html |> Expect.stringContains "names the requester" "bob"

    testCase "WHY — a vetoed landing says who vetoed it and why, because that is what the conductor has to act on" <| fun _ ->
      let queued = frameAfter seated
      let html = render (frameAfter (seated @ [ CohortCommand.VetoLanding(alice, landingIdOf queued, "the migration is not ready", machine) ]))
      html |> Expect.stringContains "says it was vetoed" "vetoed by"
      html |> Expect.stringContains "names the vetoer" "alice"
      html |> Expect.stringContains "gives the reason" "the migration is not ready"
      html |> Expect.stringContains "says who it is waiting for" "awaiting the conductor"

    testCase "WHY — a veto reason is text, never markup" <| fun _ ->
      let queued = frameAfter seated
      let html = render (frameAfter (seated @ [ CohortCommand.VetoLanding(alice, landingIdOf queued, "<script>alert(1)</script>", machine) ]))
      Expect.isFalse "the reason is encoded" (html.Contains "<script>alert(1)</script>")

    testCase "WHY — a cohort with no landing shows no landings section" <| fun _ ->
      let html = render (frameAfter (seated |> List.take 2))
      Expect.isFalse "no empty section" (html.Contains "Landings (")
  ]
