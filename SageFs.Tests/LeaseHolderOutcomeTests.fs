/// Outcome gate for the lease-holder item: sub-agents that share ONE MCP connection are told the
/// truth about who holds a lease.
///
/// Claude sub-agents of one session share one MCP connection id, and a lease used to belong to "the
/// connection", so three agents were refused for 17 to 35 minutes with "you already hold 1/1 leases"
/// while a sibling held it, and `get_daemon_status` could not say which sibling. This drives a REAL MCP
/// client (one connection, so every call below is "a sibling on the same connection") against a REAL
/// daemon this file spawns, and asserts on what an agent reads.
module SageFs.Tests.LeaseHolderOutcomeTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Http = SageFs.Tests.HttpApiIntegrationTests

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let private callText (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCall)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    return textOf result
  }

let private json (text: string) : JsonElement = (JsonDocument.Parse text).RootElement.Clone()

let private asAgent (name: string) (directory: string) : (string * obj) list =
  [ "agent_name", box name; "working_directory", box directory ]

/// A first ask is granted and attributed; asking again by the same agent is the same lease, not a second.
let private sameAgentAsksTwice (client: McpClient) : Task<string> =
  task {
    let! first = callText client "acquire_full_build_lease" (asAgent "sub-agent-one" "/work/one")
    let a = json first
    a.GetProperty("decision").GetString() |> Expect.equal (sprintf "granted. Text: %s" first) "granted"
    a.GetProperty("grant").GetString() |> Expect.equal "a new lease" "new"
    let id = a.GetProperty("leaseId").GetString()
    let! again = callText client "acquire_full_build_lease" (asAgent "sub-agent-one" "/work/one")
    let b = json again
    b.GetProperty("decision").GetString() |> Expect.equal (sprintf "still granted. Text: %s" again) "granted"
    b.GetProperty("grant").GetString() |> Expect.equal "and it says so" "already_held"
    b.GetProperty("leaseId").GetString() |> Expect.equal "the same lease id" id
    b.GetProperty("expiresAt").GetDateTimeOffset()
    |> Expect.equal "asking again does not renew it" (a.GetProperty("expiresAt").GetDateTimeOffset())
    return id
  }

/// A sibling on the same connection under another agent name has its own lease, and status tells them apart.
let private siblingIsADifferentHolder (client: McpClient) : Task<string> =
  task {
    let! second = callText client "acquire_test_suite_lease" (asAgent "sub-agent-two" "/work/two")
    let s = json second
    s.GetProperty("decision").GetString() |> Expect.equal (sprintf "the sibling is not told it already holds one. Text: %s" second) "granted"
    s.GetProperty("heldBy").GetProperty("agentName").GetString() |> Expect.equal "attributed to the sibling" "sub-agent-two"

    let! status = callText client "get_daemon_status" []
    let leases = (json status).GetProperty("leases")
    leases.GetProperty("activeCount").GetInt32() |> Expect.equal "two leases are out" 2
    let rows = [ for row in leases.GetProperty("active").EnumerateArray() -> row ]
    // The status rows keep the key spelling they always had (as written, PascalCase), unlike the camelCase acquire reply.
    rows |> List.map (fun r -> r.GetProperty("AgentName").GetString()) |> List.sort
    |> Expect.equal "each row names its agent" [ "sub-agent-one"; "sub-agent-two" ]
    rows |> List.map (fun r -> r.GetProperty("WorkingDirectory").GetString()) |> List.sort
    |> Expect.equal "and its directory" [ "/work/one"; "/work/two" ]
    rows |> List.map (fun r -> r.GetProperty("Connection").GetString()) |> List.distinct |> List.length
    |> Expect.equal "both are on ONE connection" 1
    rows |> List.iter (fun r -> (r.GetProperty("ExpiresInSeconds").GetInt32() > 0) |> Expect.isTrue "every lease says how long it has left")
    return s.GetProperty("leaseId").GetString()
  }

/// A holder asking for a second kind is refused, and the refusal names its own lease and the way out.
let private refusalNamesTheLeaseAndTheWayOut (client: McpClient) (firstId: string) : Task<unit> =
  task {
    let! refused = callText client "acquire_test_suite_lease" (asAgent "sub-agent-one" "/work/one")
    let r = json refused
    r.GetProperty("decision").GetString() |> Expect.equal (sprintf "refused. Text: %s" refused) "refused"
    let reason = r.GetProperty("reason").GetString()
    reason |> Expect.stringContains "names the holder" "sub-agent-one"
    reason |> Expect.stringContains "gives the lease id" firstId
    reason |> Expect.stringContains "says what to do" "release_work_lease"
    r.GetProperty("heldLease").GetProperty("leaseId").GetString() |> Expect.equal "structured too" firstId
  }

/// The id is the capability: release by id frees the slot, and status shows it gone.
let private releaseClearsStatus (client: McpClient) (ids: string list) : Task<unit> =
  task {
    for id in ids do
      let! released = callText client "release_work_lease" [ "lease_id", box id ]
      released |> Expect.stringContains "released" "released"
    let! status = callText client "get_daemon_status" []
    (json status).GetProperty("leases").GetProperty("activeCount").GetInt32() |> Expect.equal "nothing is left out" 0
  }

let private runGate (client: McpClient) : Task<unit> =
  task {
    let! one = sameAgentAsksTwice client
    let! two = siblingIsADifferentHolder client
    do! refusalNamesTheLeaseAndTheWayOut client one
    do! releaseClearsStatus client [ one; two ]
  }

let private runLeaseHolderGate () : Task<unit> =
  task {
    let dir = Directory.CreateTempSubdirectory("sagefs-lease-holder-").FullName
    let port = Http.reserveLoopbackPort ()
    let! proc, httpClient = Http.startDaemonWithArgs port dir [ "--no-resume" ]
    let! client = connect port

    // No try/finally around an await: observe the outcome through a continuation, clean up, then re-raise.
    let! outcome =
      (runGate client)
        .ContinueWith(fun (t: Task<unit>) ->
          match t.IsFaulted with
          | true -> Error(t.Exception :> exn)
          | false -> Ok())

    try (client :> IAsyncDisposable).DisposeAsync() |> ignore with _ -> ()
    httpClient.Dispose()
    Http.killDaemon proc
    try Directory.Delete(dir, true) with _ -> ()

    match outcome with
    | Error e -> raise e
    | Ok() -> ()
  }

[<Tests>]
let leaseHolderOutcomeTests =
  Integration.hostList "Lease holder outcome gate" [
    testTask "WHY — sub-agents sharing one MCP connection get their own leases, a refusal names the real holder, and get_daemon_status tells them apart" {
      do! runLeaseHolderGate ()
    }
  ]
