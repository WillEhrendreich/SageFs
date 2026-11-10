/// P12, run by real processes: `seat_conductor` against a real daemon, with real MCP clients, one of which answers the question.
///
/// alice is the conductor of this repository's cohort and leaves, so the seat is empty. bob and carol are present. bob's client
/// offers MCP elicitation and says what the test tells it to: a decline leaves the seat empty, a yes seats bob. carol's client
/// offers no elicitation at all, and her ask is refused with the other routes named. Then the dashboard's Seat route: a request
/// that is not from the page is refused, and one that is seats a member.
///
/// Each run gets its own daemon (own port pair, own data directory), owned by this test process.
module SageFs.Tests.SeatConductorIntegrationTests

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private repoRoot = RepoPaths.repoPathFull [||]

let private memberIdPattern = Regex(@"mcp:m-[0-9a-f]{16}")

/// A client whose person answers what `action` holds right now ("accept", "decline", ...) to every question and writes each
/// question down; with no answer given the client offers no elicitation at all.
let private connectAnswering (port: int) (answer: (string ref * List<string>) option) : Task<McpClient> =
  let transport =
    HttpClientTransport(HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port)), (null: Microsoft.Extensions.Logging.ILoggerFactory))
  let options = McpClientOptions()
  match answer with
  | Some(action, questions) ->
    options.Handlers.ElicitationHandler <-
      Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>(fun request _ ->
        questions.Add request.Message
        // A person who says yes ticks the box. The form starts unticked, so an accept with nothing in it is not a yes.
        let content =
          match action.Value with
          | "accept" -> Dictionary<string, Text.Json.JsonElement>(dict [ "confirmed", Text.Json.JsonDocument.Parse("true").RootElement ])
          | _ -> null
        ValueTask<ElicitResult>(ElicitResult(Action = action.Value, Content = content)))
  | None -> ()
  McpClient.CreateAsync(transport, options, null, CancellationToken.None)

let private callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, CancellationToken.None)
    return
      result.Content
      |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
      |> Seq.tryHead
      |> Option.defaultValue ""
  }

// No working_directory on any call: every call acts in the cohort of the repository the daemon was started in, which is the
// cohort the dashboard's panel shows. (A directory inside a git worktree resolves to the repository's common root, and the
// panel reads the daemon's own cohort, so the two would be different cohorts in the checkout this test runs from.)
let private join (client: McpClient) (name: string) =
  callTool client "join_cohort" [ "agentName", box name; "role", box "Implementer" ]

let private status (client: McpClient) =
  callTool client "get_cohort_status" []

let private ask (client: McpClient) (name: string) (toMember: string) =
  callTool client "seat_conductor" [ "agentName", box name; "toMember", box toMember ]

let private askReplacing (client: McpClient) (name: string) (holder: string) =
  callTool client "seat_conductor" [ "agentName", box name; "replaces", box holder ]

let private idsIn (text: string) : Set<string> = memberIdPattern.Matches text |> Seq.map (fun m -> m.Value) |> Set.ofSeq

let private conductorLine (text: string) : string =
  text.Split('\n') |> Array.tryFind (fun line -> line.StartsWith "Conductor:") |> Option.defaultValue "(no Conductor line)"

[<Tests>]
let tests =
  Integration.hostList "seat_conductor and the dashboard's Seat route (real daemon, real clients)" [

    testTask "a decline and a client that cannot be asked leave the seat empty, a yes seats the caller, and only the page's own request seats from the dashboard" {
      let! proc, port = SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith []
      try
        let bobQuestions = List<string>()
        let bobSays = ref "decline"
        use! alice = SageFs.Tests.CohortMcpToolsIntegrationTests.connect port
        use! bob = connectAnswering port (Some(bobSays, bobQuestions))
        use! carol = connectAnswering port None

        // alice is the conductor; bob and carol join; alice leaves.
        let! _ = join alice "alice"
        let! first = status alice
        let aliceId = idsIn first |> Seq.exactlyOne
        let! _ = join bob "bob"
        let! afterBob = status alice
        let bobId = Set.difference (idsIn afterBob) (Set.singleton aliceId) |> Seq.exactlyOne
        let! _ = join carol "carol"
        let! afterCarol = status alice
        let carolId = Set.difference (idsIn afterCarol) (Set.ofList [ aliceId; bobId ]) |> Seq.exactlyOne
        let! _ = callTool alice "leave_cohort" [ "agentName", box "alice" ]
        let! vacant = status bob
        conductorLine vacant |> Expect.stringContains "the seat is empty" "VACANT"

        // A person who says no.
        let! declined = ask bob "bob" ""
        declined |> Expect.stringContains "refused, and says nothing was done" "Nothing was done"
        declined |> Expect.stringContains "says the person said no" "said no"
        declined |> Expect.stringContains "names the command line" "sagefs cohort seat"
        bobQuestions.Count |> Expect.equal "the person was asked once" 1
        // A worktree's cohort is its repository's, so the scope reads `repo:<the repository's own root>`.
        bobQuestions.[0] |> Expect.stringContains "the question names the repository" "cohort of repo:/"
        bobQuestions.[0] |> Expect.stringContains "and who held the seat" aliceId
        bobQuestions.[0] |> Expect.stringContains "and who would sit" bobId
        let! stillVacant = status bob
        conductorLine stillVacant |> Expect.stringContains "still empty after a no" "VACANT"

        // A client that cannot be asked.
        let! noElicitation = ask carol "carol" ""
        noElicitation |> Expect.stringContains "refused, and says nothing was done" "Nothing was done"
        noElicitation |> Expect.stringContains "says nobody can be asked" "does not offer MCP elicitation"
        noElicitation |> Expect.stringContains "names the other route" "Seat button"
        let! stillVacant2 = status carol
        conductorLine stillVacant2 |> Expect.stringContains "still empty with nobody to ask" "VACANT"

        // The dashboard's route, from a shell: not from the page.
        let http = new HttpClient()
        let seatUrl (member': string) = sprintf "http://localhost:%d%s?member=%s" (port + 1) "/dashboard/cohort/seat" (Uri.EscapeDataString member')
        let! (fromShell: HttpResponseMessage) = http.PostAsync(seatUrl carolId, new StringContent("{}", Text.Encoding.UTF8, "application/json"))
        let! shellBody = fromShell.Content.ReadAsStringAsync()
        shellBody |> Expect.stringContains "a request that is not from the page is told so" "Nothing was done"
        let! afterShell = status carol
        conductorLine afterShell |> Expect.stringContains "still empty after a request from a shell" "VACANT"

        // The browser's header without a ticket proves nothing: any local process can send it.
        let headerOnly = new HttpRequestMessage(HttpMethod.Post, seatUrl carolId, Content = new StringContent("{}", Text.Encoding.UTF8, "application/json"))
        headerOnly.Headers.Add("Sec-Fetch-Site", "same-origin")
        let! (fromHeader: HttpResponseMessage) = http.SendAsync headerOnly
        let! headerBody = fromHeader.Content.ReadAsStringAsync()
        headerBody |> Expect.stringContains "the header alone is refused" "Nothing was done"
        headerBody |> Expect.stringContains "and says the ticket is missing" "no ticket"
        let! afterHeader = status carol
        conductorLine afterHeader |> Expect.stringContains "still empty after the header alone" "VACANT"

        // The person changes their mind and says yes: bob is seated.
        bobSays.Value <- "accept"
        let! seated = ask bob "bob" ""
        seated |> Expect.stringContains "bob is the conductor" "is the conductor now"
        seated |> Expect.stringContains "naming bob" bobId
        let! after = status carol
        conductorLine after |> Expect.stringContains "the status agrees" bobId

        // A held seat cannot be filled again.
        let! again = ask carol "carol" ""
        again |> Expect.stringContains "refused for a held seat" "already holds the conductor seat"
        again |> Expect.stringContains "names the holder" bobId
        bobQuestions.Count |> Expect.equal "nobody was asked about a seating the cohort would refuse" 2

        // Bob leaves, so the seat is empty again.
        let! _ = callTool bob "leave_cohort" [ "agentName", box "bob" ]
        // The header and a ticket this daemon never issued: refused. (The success path with a ticket the page was rendered with
        // is `seatRequestVerdict` plus `seatFromDashboard` in the default suite: the cohort panel is part of a session's view,
        // and this test has no session open.)
        let guessed = new HttpRequestMessage(HttpMethod.Post, seatUrl carolId + "&nonce=not-a-ticket-this-daemon-issued", Content = new StringContent("{}", Text.Encoding.UTF8, "application/json"))
        guessed.Headers.Add("Sec-Fetch-Site", "same-origin")
        let! (fromGuess: HttpResponseMessage) = http.SendAsync guessed
        let! guessBody = fromGuess.Content.ReadAsStringAsync()
        guessBody |> Expect.stringContains "a ticket nobody issued is refused" "not one this daemon issued"
        let! afterGuess = status carol
        conductorLine afterGuess |> Expect.stringContains "the seat is still empty" "VACANT"
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
    }

    testTask "a holder whose client is gone and whose lease has not lapsed is replaced when the person ticks the box, and not before" {
      let! proc, port = SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith []
      try
        let bobQuestions = List<string>()
        let bobSays = ref "decline"
        let! alice = SageFs.Tests.CohortMcpToolsIntegrationTests.connect port
        use! bob = connectAnswering port (Some(bobSays, bobQuestions))

        let! _ = join alice "alice"
        let! first = status alice
        let aliceId = idsIn first |> Seq.exactlyOne
        let! _ = join bob "bob"
        let! afterBob = status bob
        let bobId = Set.difference (idsIn afterBob) (Set.singleton aliceId) |> Seq.exactlyOne
        // alice's client disappears. Her lease is thirty minutes long, so the seat stays hers.
        do! alice.DisposeAsync().AsTask()
        let! held = status bob
        conductorLine held |> Expect.stringContains "the seat is still alice's: her lease has not lapsed" aliceId

        // A person who says no leaves her in the seat, and the question named her.
        let! declined = askReplacing bob "bob" aliceId
        declined |> Expect.stringContains "refused, and says nothing was done" "Nothing was done"
        bobQuestions.Count |> Expect.equal "the person was asked once" 1
        bobQuestions.[0] |> Expect.stringContains "the question names the holder" aliceId
        bobQuestions.[0] |> Expect.stringContains "and who would sit" bobId
        let! stillHers = status bob
        conductorLine stillHers |> Expect.stringContains "alice still holds the seat after a no" aliceId

        // A holder that does not hold the seat is refused with the real holder named, and nobody is asked.
        let! stale = askReplacing bob "bob" bobId
        stale |> Expect.stringContains "refused: bob does not hold it" aliceId
        bobQuestions.Count |> Expect.equal "nobody was asked about a replacement the cohort would refuse" 1

        // The person ticks the box.
        bobSays.Value <- "accept"
        let! replaced = askReplacing bob "bob" aliceId
        replaced |> Expect.stringContains "bob is the conductor" "is the conductor now"
        let! after = status bob
        conductorLine after |> Expect.stringContains "the status agrees" bobId
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
    }
  ]
