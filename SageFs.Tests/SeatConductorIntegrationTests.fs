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
        ValueTask<ElicitResult>(ElicitResult(Action = action.Value)))
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

let private join (client: McpClient) (name: string) =
  callTool client "join_cohort" [ "agentName", box name; "role", box "Implementer"; "working_directory", box repoRoot ]

let private status (client: McpClient) =
  callTool client "get_cohort_status" [ "working_directory", box repoRoot ]

let private ask (client: McpClient) (name: string) (toMember: string) =
  callTool client "seat_conductor" [ "agentName", box name; "toMember", box toMember; "working_directory", box repoRoot ]

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
        let! _ = callTool alice "leave_cohort" [ "agentName", box "alice"; "working_directory", box repoRoot ]
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

        // From the page: bob leaves, the seat is empty, and a request that carries the browser's own header seats carol.
        let! _ = callTool bob "leave_cohort" [ "agentName", box "bob"; "working_directory", box repoRoot ]
        let pageRequest = new HttpRequestMessage(HttpMethod.Post, seatUrl carolId, Content = new StringContent("{}", Text.Encoding.UTF8, "application/json"))
        pageRequest.Headers.Add("Sec-Fetch-Site", "same-origin")
        let! (fromPage: HttpResponseMessage) = http.SendAsync pageRequest
        let! pageBody = fromPage.Content.ReadAsStringAsync()
        pageBody |> Expect.stringContains "the page is told carol is the conductor" "is the conductor now"
        let! final = status carol
        conductorLine final |> Expect.stringContains "carol holds the seat" carolId
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
    }
  ]
