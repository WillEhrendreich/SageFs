module SageFs.Tests.SeatNonceTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Server
open SageFs.Server.DashboardFragments
open SageFs.Server.SeatNonces

/// The dashboard's Seat route is open to any process that can reach the dashboard port, and a browser header proves nothing
/// against a process that sets the same header. These pin what the route does ask: a one-use ticket, issued by this daemon
/// for that member's button, not yet used and not yet expired. A request with the right header and no ticket is refused.

let private t0 = DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc)

/// A store on a clock the test moves.
type private Clocked(lifetime: TimeSpan, capacity: int) =
  let mutable now = t0
  member val Store = Store((fun () -> now), lifetime, capacity) with get
  member _.Advance(by: TimeSpan) = now <- now + by

let private lifetime = Timeouts.dashboardSeatNonceLifetime

let private fresh () = Clocked(lifetime, 8)

let private refusalOf (outcome: Result<unit, NonceRefusal>) : NonceRefusal option =
  match outcome with
  | Result.Error refusal -> Some refusal
  | Result.Ok() -> None

[<Tests>]
let storeTests =
  testList "Seat tickets: one use, for one member, for a while" [

    testCase "WHY - a ticket issued for a member is good for that member once" <| fun _ ->
      let c = fresh ()
      let ticket = c.Store.Issue "bob"
      c.Store.Redeem(ticket, "bob") |> Expect.equal "the first presentation is accepted" (Result.Ok())

    testCase "WHY - a replayed ticket is refused" <| fun _ ->
      let c = fresh ()
      let ticket = c.Store.Issue "bob"
      c.Store.Redeem(ticket, "bob") |> ignore
      c.Store.Redeem(ticket, "bob") |> refusalOf |> Expect.equal "the second is refused" (Some NonceRefusal.NotIssuedOrUsed)

    testCase "WHY - a stale ticket is refused, and says it expired" <| fun _ ->
      let c = fresh ()
      let ticket = c.Store.Issue "bob"
      c.Advance(lifetime + TestTimeouts.clockMargin)
      c.Store.Redeem(ticket, "bob") |> refusalOf |> Expect.equal "past its time" (Some NonceRefusal.Expired)

    testCase "WHY - a ticket is good right up to its time" <| fun _ ->
      let c = fresh ()
      let ticket = c.Store.Issue "bob"
      c.Advance(lifetime - TestTimeouts.clockMargin)
      c.Store.Redeem(ticket, "bob") |> Expect.equal "still good a margin before the end" (Result.Ok())

    testCase "WHY - a ticket for one member's button does not seat another, and the attempt spends it" <| fun _ ->
      let c = fresh ()
      let ticket = c.Store.Issue "bob"
      c.Store.Redeem(ticket, "carol") |> refusalOf |> Expect.equal "wrong member" (Some NonceRefusal.ForAnotherMember)
      c.Store.Redeem(ticket, "bob") |> refusalOf |> Expect.equal "and the guess cost the ticket" (Some NonceRefusal.NotIssuedOrUsed)

    testCase "WHY - a ticket nobody issued, and no ticket at all, are refused" <| fun _ ->
      let c = fresh ()
      c.Store.Redeem("made-up", "bob") |> refusalOf |> Expect.equal "unknown" (Some NonceRefusal.NotIssuedOrUsed)
      c.Store.Redeem("", "bob") |> refusalOf |> Expect.equal "blank" (Some NonceRefusal.Missing)
      c.Store.Redeem(null, "bob") |> refusalOf |> Expect.equal "null" (Some NonceRefusal.Missing)

    testCase "WHY - tickets cannot be guessed: they are long, and no two alike" <| fun _ ->
      let c = Clocked(lifetime, 5000)
      let tickets = [ for i in 1 .. 1000 -> c.Store.Issue(sprintf "member-%d" i) ]
      tickets |> List.distinct |> List.length |> Expect.equal "all different" 1000
      tickets |> List.forall (fun t -> t.Length >= 22 && t |> Seq.forall (fun ch -> Char.IsAsciiLetterOrDigit ch || ch = '-' || ch = '_'))
      |> Expect.isTrue "128 bits or more, safe in a URL"

    testCase "WHY - a page that renders again shows the same ticket until it is half spent, so a quiet page's button keeps working" <| fun _ ->
      let c = fresh ()
      let first = c.Store.Issue "bob"
      c.Advance(lifetime / 4.0)
      c.Store.Issue "bob" |> Expect.equal "same ticket on the next render" first
      c.Advance(lifetime / 2.0)
      let later = c.Store.Issue "bob"
      later |> Expect.notEqual "a new one once the old is past half" first
      c.Store.Redeem(first, "bob") |> Expect.equal "the old one is still good until its own end" (Result.Ok())

    testCase "WHY - a spent ticket is not shown again: the next render gets a new one" <| fun _ ->
      let c = fresh ()
      let first = c.Store.Issue "bob"
      c.Store.Redeem(first, "bob") |> ignore
      c.Store.Issue "bob" |> Expect.notEqual "fresh after use" first

    testCase "WHY - the store is bounded: the oldest tickets go first" <| fun _ ->
      let c = Clocked(lifetime, 3)
      let tickets = [ for i in 1 .. 6 -> c.Store.Issue(sprintf "m%d" i) ]
      (c.Store.Outstanding <= 3) |> Expect.isTrue "never more than the capacity"
      c.Store.Redeem(List.head tickets, "m1") |> refusalOf |> Expect.equal "the oldest was dropped" (Some NonceRefusal.NotIssuedOrUsed)
      c.Store.Redeem(List.last tickets, "m6") |> Expect.equal "the newest is kept" (Result.Ok())

    testCase "WHY - expired tickets are cleared when new ones are issued" <| fun _ ->
      let c = fresh ()
      for i in 1 .. 5 do
        c.Store.Issue(sprintf "m%d" i) |> ignore
      c.Advance(lifetime + TestTimeouts.clockMargin)
      c.Store.Issue "later" |> ignore
      c.Store.Outstanding |> Expect.equal "only the new one is held" 1
  ]

[<Tests>]
let verdictTests =
  testList "The Seat route's verdict: the header is an extra check, never proof" [

    testCase "WHY - the right header and no ticket is refused" <| fun _ ->
      let c = fresh ()
      Dashboard.seatRequestVerdict c.Store Dashboard.FetchSite.SameOrigin "bob" ""
      |> Expect.equal "refused for want of a ticket" (Result.Error(Dashboard.SeatRequestRefusal.Ticket NonceRefusal.Missing))

    testCase "WHY - the right header and a ticket nobody issued is refused" <| fun _ ->
      let c = fresh ()
      Dashboard.seatRequestVerdict c.Store Dashboard.FetchSite.SameOrigin "bob" "guess"
      |> Expect.equal "refused" (Result.Error(Dashboard.SeatRequestRefusal.Ticket NonceRefusal.NotIssuedOrUsed))

    testCase "WHY - a good ticket without the header is refused, and keeps the ticket for the page that has the header" <| fun _ ->
      let c = fresh ()
      let ticket = c.Store.Issue "bob"
      for site in
        [ Dashboard.FetchSite.NotSent
          Dashboard.FetchSite.CrossSite
          Dashboard.FetchSite.SameSite
          Dashboard.FetchSite.UserInitiated
          Dashboard.FetchSite.Unrecognized "weird" ] do
        Dashboard.seatRequestVerdict c.Store site "bob" ticket
        |> Expect.equal (sprintf "%A is not the page's own request" site) (Result.Error Dashboard.SeatRequestRefusal.NotFromABrowser)
      Dashboard.seatRequestVerdict c.Store Dashboard.FetchSite.SameOrigin "bob" ticket |> Expect.equal "the page's own request" (Result.Ok())

    testCase "WHY - a replayed ticket is refused even with the header" <| fun _ ->
      let c = fresh ()
      let ticket = c.Store.Issue "bob"
      Dashboard.seatRequestVerdict c.Store Dashboard.FetchSite.SameOrigin "bob" ticket |> ignore
      Dashboard.seatRequestVerdict c.Store Dashboard.FetchSite.SameOrigin "bob" ticket
      |> Expect.equal "replay" (Result.Error(Dashboard.SeatRequestRefusal.Ticket NonceRefusal.NotIssuedOrUsed))

    testCase "WHY - a stale ticket is refused even with the header" <| fun _ ->
      let c = fresh ()
      let ticket = c.Store.Issue "bob"
      c.Advance(lifetime + TestTimeouts.clockMargin)
      Dashboard.seatRequestVerdict c.Store Dashboard.FetchSite.SameOrigin "bob" ticket
      |> Expect.equal "stale" (Result.Error(Dashboard.SeatRequestRefusal.Ticket NonceRefusal.Expired))

    testCase "WHY - no member named is refused first" <| fun _ ->
      let c = fresh ()
      Dashboard.seatRequestVerdict c.Store Dashboard.FetchSite.SameOrigin "  " "anything" |> Expect.equal "blank member" (Result.Error Dashboard.SeatRequestRefusal.NoMemberNamed)

    testCase "WHY - every refusal says nothing was done and what to do instead" <| fun _ ->
      for refusal in
        [ Dashboard.SeatRequestRefusal.NoMemberNamed
          Dashboard.SeatRequestRefusal.NotFromABrowser
          Dashboard.SeatRequestRefusal.Ticket NonceRefusal.Missing
          Dashboard.SeatRequestRefusal.Ticket NonceRefusal.NotIssuedOrUsed
          Dashboard.SeatRequestRefusal.Ticket NonceRefusal.ForAnotherMember
          Dashboard.SeatRequestRefusal.Ticket NonceRefusal.Expired ] do
        Dashboard.SeatRequestRefusal.describe refusal |> Expect.stringContains "says nothing was done" "Nothing was done"
  ]

[<Tests>]
let renderTests =
  testList "The Seat button carries the ticket the store issued for its member" [

    testCase "WHY - the button's post names the member and the ticket issued for that member, escaped for a URL" <| fun _ ->
      let epoch = DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc)
      let machine = CohortScope.Machine
      let join who = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)
      let alice = MemberId.Minted "alice"
      let bob = MemberId.Minted "bob"
      let state =
        [ join alice; join bob; CohortCommand.Depart(alice, machine) ]
        |> List.fold
          (fun s command ->
            match decide epoch [| 1uy |] s command with
            | Ok(next, _, _) -> next
            | Error e -> failtestf "%A" e)
          (CohortState.forScope machine)
      let frame = project { Seq = 0L<Measures.ledgerSeq>; State = state } [||]
      let issued = Collections.Generic.List<string>()
      let html =
        renderCohortPanelWith
          (fun name ->
            issued.Add name
            "T/" + name)
          frame
        |> renderNode
      issued |> List.ofSeq |> Expect.equal "a ticket was asked for the one present member, and only for them" [ "bob" ]
      html |> Expect.stringContains "the post carries the member and its ticket" "member=bob&amp;nonce=T%2Fbob"
  ]
