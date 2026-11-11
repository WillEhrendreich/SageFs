/// The cohort panel says who conducts, and its advice about an empty seat matches the buttons it draws.
///
/// A one-member cohort printed nothing about the seat, so a person could not tell whether that member conducts. A
/// vacant seat with nobody present printed "click Seat on the dashboard's cohort panel" beside zero Seat buttons,
/// which is exactly the state a seat goes vacant in. `CohortStatusText.conductorSentence` has one sentence per state
/// and `vacantSeatAdviceFor` counts the members the panel can actually offer a button to.
module SageFs.Tests.CohortSeatSentenceTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Features
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Server.DashboardFragments

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

/// The frame the commands produce, folded through the real `decide` and projected as the owner does.
let private frameAfter (commands: CohortCommand<MemberId> list) : CohortFrame<MemberId> =
  let finalState, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.forScope machine, 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } [||]

let private join who = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)
let private depart who = CohortCommand.Depart(who, machine)
let private claim who file = CohortCommand.AcquireClaim(who, ClaimScope.File file, "editing", machine)

/// The four states the panel has a sentence for: nobody ever, one member, an empty seat with nobody present (an orphaned
/// claim keeps the panel on the page), and an empty seat with someone present.
let private noCohort = frameAfter []
let private oneMember = frameAfter [ join alice ]
// The orphan is bob's: the conductor may not leave holding a claim (`ConductorHoldsClaims`), so alice leaves empty-handed.
let private vacantNobodyPresent = frameAfter [ join alice; join bob; claim bob "src/A.fs"; depart bob; depart alice ]
let private vacantWithMember = frameAfter [ join alice; join bob; depart alice; claim bob "src/B.fs" ]

let private html (frame: CohortFrame<MemberId>) = renderCohortPanel frame |> renderNode

let private seatButtons (page: string) : int =
  (page.Length - page.Replace("data-testid=\"cohort-seat\"", "").Length) / "data-testid=\"cohort-seat\"".Length

let private sentence (frame: CohortFrame<MemberId>) =
  CohortStatusText.conductorSentence epoch CohortStatusText.HolderSilence.NotSilent frame

[<Tests>]
let tests =
  testList "the cohort panel's seat sentence" [
    testCase "WHY — a one-member cohort says who conducts" <| fun _ ->
      sentence oneMember |> Expect.equal "the sentence names the holder" "Conductor: alice."
      html oneMember |> Expect.stringContains "and the panel prints it" "Conductor: alice."

    testCase "WHY — with no cohort the panel shows its empty state and no conductor line" <| fun _ ->
      let page = html noCohort
      page |> Expect.stringContains "the empty state" "join_cohort"
      page.Contains "cohort-conductor" |> Expect.isFalse "nothing to say about a seat"
      sentence noCohort |> Expect.stringContains "a seat nobody held says who takes it" "first member to join"

    testCase "WHY — a vacant seat with nobody present does not tell the reader to click a Seat button the panel does not draw" <| fun _ ->
      let page = html vacantNobodyPresent
      page |> Expect.stringContains "the seat is shown vacant" "Conductor seat vacant since"
      seatButtons page |> Expect.equal "no Seat button is drawn" 0
      page |> Expect.stringContains "the advice says there is no button yet" "no Seat button yet"
      page |> Expect.stringContains "and what makes one appear" "When one joins"

    testCase "WHY — a vacant seat with a member present offers that member and says to click Seat" <| fun _ ->
      let page = html vacantWithMember
      seatButtons page |> Expect.equal "one Seat button, for bob" 1
      page |> Expect.stringContains "the button is for bob" "Seat bob"
      page |> Expect.stringContains "the advice names the button" "click Seat on the dashboard"
      page.Contains "no Seat button yet" |> Expect.isFalse "the no-button sentence is for the no-button state"

    testCase "WHY — advice that sends the reader to a Seat button holds only when the panel draws one, in every state" <| fun _ ->
      for name, frame in [ "no cohort", noCohort; "one member", oneMember; "vacant, nobody present", vacantNobodyPresent; "vacant, member present", vacantWithMember ] do
        let page = html frame
        let promisesButton = page.Contains "click Seat on the dashboard" && not (page.Contains "no Seat button yet")
        match promisesButton with
        | true -> (seatButtons page, 0) |> Expect.isGreaterThan (sprintf "%s: a promised button exists" name)
        | false -> ()

    testCase "WHY — the status text agents read tells the same truth as the panel" <| fun _ ->
      let nobody = CohortStatusText.render vacantNobodyPresent
      nobody |> Expect.stringContains "no seatable member, no button" "no Seat button yet"
      let someone = CohortStatusText.render vacantWithMember
      someone |> Expect.stringContains "a seatable member, the full advice" CohortStatusText.vacantSeatAdvice

    testCase "WHY — a member under a token cannot be seated, so it does not count as someone to click Seat for" <| fun _ ->
      let tokenHolder = MemberId.Capability "tok-1"
      let frame = frameAfter [ join alice; join tokenHolder; depart alice; claim tokenHolder "src/T.fs" ]
      CohortStatusText.seatableMembers frame |> Expect.equal "the token holder is present and not seatable" 0
      seatButtons (html frame) |> Expect.equal "so no button" 0
      (html frame) |> Expect.stringContains "and the advice says so" "no Seat button yet"
  ]
