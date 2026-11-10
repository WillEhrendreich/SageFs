module SageFs.Tests.CohortSeatDashboardTests

open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.McpTools
open SageFs.MemberTable
open SageFs.Server.DashboardFragments
open SageFs.Server.DashboardPanelContext
open SageFs.Tests.ToolAuthorityGateTests

/// The dashboard's way to fill an empty conductor seat: a Seat button on the cohort panel's vacant-seat line, which asks the
/// person (a native confirm), then posts to a route that runs only for a request from that page in a browser.

let private epoch = DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"
let private carol = MemberId.Minted "carol"
let private token = MemberId.Capability "0123456789abcdef"

let private frameAfter (commands: CohortCommand<MemberId> list) : CohortFrame<MemberId> =
  let state, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(next, _, _) -> next, seq + 1L<Measures.ledgerSeq>
        | Error err -> failwithf "unexpected refusal building a test frame: %A" err)
      (CohortState.forScope machine, 0L<Measures.ledgerSeq>)
  project { Seq = seq; State = state } [||]

let private join who = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)

let private vacant =
  [ join alice; join bob; join carol; CohortCommand.Join(token, JoinableRole.Implementer, None, machine); CohortCommand.Depart(alice, machine) ]

let private render (frame: CohortFrame<MemberId>) : string = renderCohortPanel frame |> renderNode

let private seatButtons (html: string) : string list =
  Regex.Matches(html, "data-testid=\"cohort-seat\"[^>]*data-member=\"([^\"]*)\"|data-member=\"([^\"]*)\"[^>]*data-testid=\"cohort-seat\"")
  |> Seq.map (fun m -> if m.Groups.[1].Success then m.Groups.[1].Value else m.Groups.[2].Value)
  |> List.ofSeq

[<Tests>]
let panelTests =
  testList "Cohort panel: the Seat button on a vacant seat" [

    testCase "WHY - a vacant seat offers one Seat button per present member, in words the page carries" <| fun _ ->
      let html = render (frameAfter vacant)
      seatButtons html |> List.sort |> Expect.equal "bob and carol are offered, alice (departed) and the token holder are not" [ "bob"; "carol" ]
      html |> Expect.stringContains "the words are real text" "Seat bob"
      html |> Expect.stringContains "the click asks first" "confirm("
      html |> Expect.stringContains "and posts to the route the daemon registers" DashboardRoutes.CohortSeat

    testCase "WHY - a seat that is held offers no button, because there is nothing to fill" <| fun _ ->
      seatButtons (render (frameAfter [ join alice; join bob ])) |> Expect.isEmpty "no Seat button on a held seat"

    testCase "WHY - the vacant-seat line says how a person fills it, including the question seat_conductor asks" <| fun _ ->
      let html = render (frameAfter vacant)
      html |> Expect.stringContains "names the tool's question" "seat_conductor"
      html |> Expect.stringContains "names the command" "sagefs cohort seat"

    testCase "WHY - a member name with a quote in it cannot end the script string in the click handler" <| fun _ ->
      let odd = MemberId.Minted "o'brien\\x"
      let html = render (frameAfter [ join alice; join odd; CohortCommand.Depart(alice, machine) ])
      html |> Expect.stringContains "the quote is escaped inside the script" "o\\'brien"
  ]

[<Tests>]
let routeTests =
  testList "The dashboard's seat route" [

    testCase "WHY - the header is read into a closed set, every value a case of its own" <| fun _ ->
      let site = SageFs.Server.Dashboard.FetchSite.ofHeader
      let cases = SageFs.Server.Dashboard.FetchSite.SameOrigin, SageFs.Server.Dashboard.FetchSite.SameSite
      site (Some "same-origin") |> Expect.equal "same-origin" (fst cases)
      site (Some "same-site") |> Expect.equal "same-site" (snd cases)
      site (Some "cross-site") |> Expect.equal "cross-site" SageFs.Server.Dashboard.FetchSite.CrossSite
      site (Some "none") |> Expect.equal "none" SageFs.Server.Dashboard.FetchSite.UserInitiated
      site (Some "") |> Expect.equal "a blank value is not a known one" (SageFs.Server.Dashboard.FetchSite.Unrecognized "")
      site None |> Expect.equal "a shell or an HTTP client sends none" SageFs.Server.Dashboard.FetchSite.NotSent

    testCase "WHY - the literal the button posts to is the one the dashboard registers" <| fun _ ->
      let registered =
        EndpointContractTests.RouteScan.daemonSources ()
        |> List.collect (IO.File.ReadAllText >> EndpointContractTests.RouteScan.routesIn)
        |> List.map (fun route -> route.Path)
        |> Set.ofList
      registered |> Expect.contains "registered under that exact path" DashboardRoutes.CohortSeat
  ]

let private wd = Some molinaRepo

/// `name` joins the cohort of the repository as an Implementer, through the tool body.
let private seated (ctx: McpContext) (name: string) = SageFs.Tests.ToolAuthorityGateTests.join ctx name "Implementer" molinaRepo

[<Tests>]
let seatFromDashboardTests =
  testList "seatFromDashboard: the same rules as the command line, answered in a sentence" [

    testTask "a present member is seated in a vacant seat, and the sentence says who and what it ended" {
      let ctx = mkContext ()
      do! seated ctx "alice"
      do! seated ctx "bob"
      let! _ = McpCohortTools.leaveCohort ctx "alice" wd
      let owner = (cohortOwnerFor ctx wd).Value
      let! outcome = CohortSeatRoute.seatFromDashboard owner "bob"
      match outcome with
      | Ok text ->
        text |> Expect.stringContains "names bob" "bob"
        text |> Expect.stringContains "names the vacancy" "alice"
      | Error text -> failtestf "expected a seating, got: %s" text
      owner.ReadFrame().Conductor |> Expect.equal "the seat is bob's" (ConductorBinding.Bound(MemberId.Minted "bob"))
    }

    testTask "a held seat is refused with its holder named, and nothing changes" {
      let ctx = mkContext ()
      do! seated ctx "alice"
      do! seated ctx "bob"
      let owner = (cohortOwnerFor ctx wd).Value
      let! outcome = CohortSeatRoute.seatFromDashboard owner "bob"
      match outcome with
      | Error text -> text |> Expect.stringContains "names the holder" "alice"
      | Ok text -> failtestf "a held seat must be refused, got: %s" text
      owner.ReadFrame().Conductor |> Expect.equal "still alice's" (ConductorBinding.Bound(MemberId.Minted "alice"))
    }
  ]

// ---- Replace: a button per present member when the holder is silent ----

let private heldBy (holder: MemberId) (others: MemberId list) =
  [ yield join holder
    for other in others do
      yield join other ]

/// How long the holder has been silent when the panel is drawn: as long as a lease lasts.
let private silentFor = Timeouts.cohortLeaseWindow

let private silentSince = Features.CohortStatusText.HolderSilence.SilentSince(epoch, epoch + silentFor)

let private renderWithSilence (silence: Features.CohortStatusText.HolderSilence) (issued: Collections.Generic.List<string>) (frame: CohortFrame<MemberId>) : string =
  renderCohortPanelCore
    (fun subject ->
      issued.Add subject
      "T/" + subject)
    silence
    (epoch + silentFor)
    frame
  |> renderNode

/// Each Replace button as (member to seat, holder it replaces).
let private replaceButtons (html: string) : (string * string) list =
  let attribute (name: string) (chunk: string) =
    let m = Regex.Match(chunk, name + "=\"([^\"]*)\"")
    match m.Success with
    | true -> m.Groups.[1].Value
    | false -> ""
  html.Split("<button")
  |> Array.filter (fun chunk -> chunk.Contains "data-testid=\"cohort-replace\"")
  |> Array.map (fun chunk -> attribute "data-member" chunk, attribute "data-holder" chunk)
  |> List.ofArray

[<Tests>]
let replacePanelTests =
  testList "Cohort panel: the Replace button on a silent holder" [

    testCase "WHY - a silent holder gets one Replace button per present member who is neither the holder nor under a token" <| fun _ ->
      let frame = frameAfter (heldBy alice [ bob; carol ] @ [ CohortCommand.Join(token, JoinableRole.Implementer, None, machine) ])
      let html = renderWithSilence silentSince (Collections.Generic.List<string>()) frame
      replaceButtons html |> List.sort |> Expect.equal "bob and carol replace alice; alice and the token holder do not" [ "bob", "alice"; "carol", "alice" ]
      html |> Expect.stringContains "the words are real text" "Replace alice with bob"
      html |> Expect.stringContains "the click asks first" "confirm("
      html |> Expect.stringContains "and posts to the seat route with the holder named" "replace=alice"
      html |> Expect.stringContains "and the route the daemon registers" DashboardRoutes.CohortSeat

    testCase "WHY - the question the click asks names the holder and how long it has been silent" <| fun _ ->
      let html = renderWithSilence silentSince (Collections.Generic.List<string>()) (frameAfter (heldBy alice [ bob ]))
      let confirmText = Regex.Match(html, "confirm\\('([^']*)'").Groups.[1].Value
      confirmText |> Expect.stringContains "names the holder" "alice"
      confirmText |> Expect.stringContains "names the member who would sit" "bob"
      confirmText |> Expect.stringContains "says how long it has been silent" (sprintf "silent for %d min" (int silentFor.TotalMinutes))

    testCase "WHY - a holder that renewed its lease lately gets no Replace button, because it is not gone" <| fun _ ->
      let html = renderWithSilence Features.CohortStatusText.HolderSilence.NotSilent (Collections.Generic.List<string>()) (frameAfter (heldBy alice [ bob; carol ]))
      replaceButtons html |> Expect.isEmpty "no button for a holder that is not silent"
      seatButtons html |> Expect.isEmpty "and no Seat button over a held seat"

    testCase "WHY - an empty seat gets Seat buttons and no Replace button" <| fun _ ->
      let html = renderWithSilence silentSince (Collections.Generic.List<string>()) (frameAfter vacant)
      replaceButtons html |> Expect.isEmpty "nothing to replace"
      seatButtons html |> List.sort |> Expect.equal "the Seat buttons stay" [ "bob"; "carol" ]

    testCase "WHY - each Replace button carries a ticket issued for its own pair" <| fun _ ->
      let issued = Collections.Generic.List<string>()
      let html = renderWithSilence silentSince issued (frameAfter (heldBy alice [ bob; carol ]))
      issued
      |> List.ofSeq
      |> List.sort
      |> Expect.equal
        "one ticket per pair, and none for a Seat button"
        ([ SageFs.Server.SeatNonces.replaceSubject "alice" "bob"; SageFs.Server.SeatNonces.replaceSubject "alice" "carol" ] |> List.sort)
      html |> Expect.stringContains "the post carries the ticket for the pair, escaped" (sprintf "nonce=%s" (Uri.EscapeDataString("T/" + SageFs.Server.SeatNonces.replaceSubject "alice" "bob")))

    testCase "WHY - a holder or member name with a quote in it cannot end the script string in the click handler" <| fun _ ->
      let odd = MemberId.Minted "o'brien\\x"
      let html = renderWithSilence silentSince (Collections.Generic.List<string>()) (frameAfter (heldBy odd [ bob ]))
      html |> Expect.stringContains "the holder's quote is escaped inside the script" "o\\'brien"
  ]

[<Tests>]
let replaceFromDashboardTests =
  testList "replaceFromDashboard: the same rules as the command line, answered in a sentence" [

    testTask "a present member replaces the named holder, and the sentence says who and who was replaced" {
      let ctx = mkContext ()
      do! seated ctx "alice"
      do! seated ctx "bob"
      let owner = (cohortOwnerFor ctx wd).Value
      let! outcome = CohortSeatRoute.replaceFromDashboard owner "bob" "alice"
      match outcome with
      | Ok text ->
        text |> Expect.stringContains "names bob" "bob"
        text |> Expect.stringContains "names the replaced holder" "alice"
      | Error text -> failtestf "expected a replacement, got: %s" text
      owner.ReadFrame().Conductor |> Expect.equal "the seat is bob's" (ConductorBinding.Bound(MemberId.Minted "bob"))
    }

    testTask "a holder that is not the holder is refused with the actual holder named, and nothing changes" {
      let ctx = mkContext ()
      do! seated ctx "alice"
      do! seated ctx "bob"
      do! seated ctx "carol"
      let owner = (cohortOwnerFor ctx wd).Value
      let! outcome = CohortSeatRoute.replaceFromDashboard owner "bob" "carol"
      match outcome with
      | Error text -> text |> Expect.stringContains "names the actual holder" "alice"
      | Ok text -> failtestf "a stale holder must be refused, got: %s" text
      owner.ReadFrame().Conductor |> Expect.equal "still alice's" (ConductorBinding.Bound(MemberId.Minted "alice"))
    }

    testTask "a member under a token cannot be put in the seat" {
      let ctx = mkContext ()
      do! seated ctx "alice"
      let owner = (cohortOwnerFor ctx wd).Value
      let! _ = owner.Commit(CohortCommand.Join(token, JoinableRole.Implementer, None, owner.Scope))
      let! outcome = CohortSeatRoute.replaceFromDashboard owner (MemberId.display token) "alice"
      match outcome with
      | Error _ -> ()
      | Ok text -> failtestf "a token holder must be refused, got: %s" text
    }
  ]
