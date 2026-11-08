module SageFs.Tests.CohortSeatIntegrationTests

/// `sagefs cohort seat`, end to end against a real daemon. The cohort's conductor leaves, so its seat is vacant
/// (the live defect: nothing could fill it and the only way out was wiping the ledger). A person looks at the seat
/// and seats a present member with the operator credential, and the seating survives a daemon restart because the
/// ledger replays it. No MCP tool and no token can do the same.
///
/// Each run gets its own daemon (own port, own data directory, own ledger). The restart reuses the data directory
/// on a new port, which is the point of the last case.

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private repoRoot = RepoPaths.repoPathFull [||]

let private daemonIn (dataDir: string) = SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith [ "SAGEFS_DATA_DIR", dataDir ]

let private callTool (client: ModelContextProtocol.Client.McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, Threading.CancellationToken.None)
    return
      result.Content
      |> Seq.choose (function :? ModelContextProtocol.Protocol.TextContentBlock as t -> Some t.Text | _ -> None)
      |> Seq.tryHead
      |> Option.defaultValue ""
  }

/// A connection that presents `key` the way the stdio bridge does, so the same key is the same member across
/// connections: a client that comes back is the member it was.
let private connectKeyed (port: int) (key: string) : Task<ModelContextProtocol.Client.McpClient> =
  let opts = ModelContextProtocol.Client.HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  opts.AdditionalHeaders <-
    System.Collections.Generic.Dictionary<string, string>(dict [ MemberTable.ClientKey.headerName, key ])
  let transport = ModelContextProtocol.Client.HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  ModelContextProtocol.Client.McpClient.CreateAsync(transport, null, null, Threading.CancellationToken.None)

let private expectReply (what: string) (result: Result<CohortSeatRoute.SeatReply, MemberMintCommand.MintFailure>) : CohortSeatRoute.SeatReply =
  match result with
  | Ok reply -> reply
  | Error failure -> failwithf "%s: %s" what (MemberMintCommand.MintFailure.describe failure)

[<Tests>]
let cohortSeatIntegrationTests =
  Integration.hostList "A person seats a conductor (real daemon)" [

    testTask "the seat of a conductor who left is filled by a person, refuses a second seating, and survives a restart" {
      let dataDir = Path.Combine(Path.GetTempPath(), "sagefs-test-cohort-seat", Guid.NewGuid().ToString "N")
      Directory.CreateDirectory dataDir |> ignore
      let! proc, port = daemonIn dataDir
      let mutable running = Some proc
      try
        // Two connections are two members. The first to join holds the seat.
        use! first = SageFs.Tests.CohortMcpToolsIntegrationTests.connect port
        use! second = SageFs.Tests.CohortMcpToolsIntegrationTests.connect port
        let! _ = callTool first "join_cohort" [ "agentName", box "first"; "role", box "Implementer" ]
        let! _ = callTool second "join_cohort" [ "agentName", box "second"; "role", box "Implementer" ]
        let! left = callTool first "leave_cohort" [ "agentName", box "first" ]
        left |> Expect.isNotEmpty "the conductor's leave was answered"

        let! looked = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Look repoRoot)
        let look = expectReply "look at the seat" looked
        look.Seat |> Expect.equal "the seat is vacant" "vacant"
        look.Present |> List.length |> Expect.equal "one member is present" 1
        let secondId = (List.head look.Present).Id

        // No tool fills it: the member who is not the conductor asks to hand it to themselves and is refused.
        let! (delegated: string) = callTool second "delegate_conductor" [ "agentName", box "second"; "toMember", box secondId ]
        delegated.Contains("is the conductor now") |> Expect.isFalse "no tool fills the empty seat: delegating from it is refused, whichever gate says so first"

        // Without the operator credential the route refuses.
        let anonymous = new System.Net.Http.HttpClient()
        let! (rejected: System.Net.Http.HttpResponseMessage) = anonymous.GetAsync(sprintf "http://localhost:%d%s" port CohortSeatRoute.Path)
        anonymous.Dispose()
        int rejected.StatusCode |> Expect.equal "a request with no credential is refused" 401

        let! seatedResult = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Seat(repoRoot, secondId))
        let seated = expectReply "seat the second member" seatedResult
        seated.Seat |> Expect.equal "the seat is bound" "bound"
        seated.Holder |> Expect.equal "to the member the person named" secondId
        seated.Seated |> Expect.equal "the reply says who was seated" secondId

        let! again = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Seat(repoRoot, secondId))
        match again with
        | Error(MemberMintCommand.MintFailure.Refused(_, message, _)) -> message |> Expect.stringContains "a held seat is refused and names its holder" secondId
        | other -> failwithf "expected a refusal for a held seat, got %A" other

        // The restart: the ledger replays the seating.
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
        running <- None
        let! restarted, port2 = daemonIn dataDir
        running <- Some restarted
        let! afterRestart = CohortSeatCommand.call port2 dataDir (CohortSeatCommand.Ask.Look repoRoot)
        let after = expectReply "look after the restart" afterRestart
        after.Seat |> Expect.equal "the seat is still bound after a restart" "bound"
        after.Holder |> Expect.equal "to the same member" secondId
      finally
        match running with
        | Some p -> SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon p
        | None -> ()
    }

    testTask "a conductor whose connection is gone is replaced by a person without waiting for its lease, and the replaced member is an ordinary member" {
      let dataDir = Path.Combine(Path.GetTempPath(), "sagefs-test-cohort-replace", Guid.NewGuid().ToString "N")
      Directory.CreateDirectory dataDir |> ignore
      let! proc, port = daemonIn dataDir
      let mutable running = Some proc
      try
        // A is a client with a key of its own, so it can come back as the same member. B is a second member.
        let keyA = MemberTable.ClientKey.generate ()
        let! firstConnection = connectKeyed port keyA
        use! second = SageFs.Tests.CohortMcpToolsIntegrationTests.connect port
        let! _ = callTool firstConnection "join_cohort" [ "agentName", box "first"; "role", box "Implementer" ]
        let! _ = callTool second "join_cohort" [ "agentName", box "second"; "role", box "Implementer" ]

        // A's connection is gone. Its lease has not lapsed, so the seat is still bound to it.
        do! firstConnection.DisposeAsync().AsTask()
        let! looked = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Look repoRoot)
        let look = expectReply "look at the seat" looked
        look.Seat |> Expect.equal "the seat is still bound to the member whose connection is gone" "bound"
        look.Present |> List.length |> Expect.equal "both members are present" 2
        let holderId = look.Holder
        let secondId = (look.Present |> List.find (fun m -> m.Id <> holderId)).Id

        // A plain seating is refused, and the refusal prints the exact command to run.
        let! plain = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Seat(repoRoot, secondId))
        match plain with
        | Error(MemberMintCommand.MintFailure.Refused(_, message, suggestion)) ->
          message |> Expect.stringContains "the refusal names the holder" holderId
          suggestion |> Expect.stringContains "and prints the command to replace it" (sprintf "%s --replace %s" secondId holderId)
        | other -> failwithf "expected a refusal for a held seat, got %A" other

        // The person replaces the holder, naming it.
        let! stale = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Replace(repoRoot, secondId, secondId))
        match stale with
        | Error(MemberMintCommand.MintFailure.Refused(_, message, _)) ->
          message |> Expect.stringContains "a holder that is not the holder is refused, and the refusal says who holds the seat" holderId
        | other -> failwithf "expected a refusal for a stale holder, got %A" other
        let! replacedResult = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Replace(repoRoot, secondId, holderId))
        let replaced = expectReply "replace the holder" replacedResult
        replaced.Seat |> Expect.equal "the seat is bound" "bound"
        replaced.Holder |> Expect.equal "to the member the person put in it" secondId
        replaced.Replaced |> Expect.equal "the reply says who was replaced" holderId

        // The same command again names a holder that no longer holds the seat: refused, nothing moves.
        let! again = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Replace(repoRoot, secondId, holderId))
        match again with
        | Error(MemberMintCommand.MintFailure.Refused(_, message, _)) -> message |> Expect.stringContains "the replaced member is not the holder any more" secondId
        | other -> failwithf "expected a refusal for a stale holder, got %A" other

        // B holds conductor authority: a conductor-only call reaches the rule that refuses it as a hand-off to itself.
        let! asConductor = callTool second "delegate_conductor" [ "agentName", box "second"; "toMember", box secondId ]
        asConductor |> Expect.stringContains "B is the conductor, so the refusal is the one only a conductor reaches" "already holds the conductor seat"

        // A comes back as the same member (same key) and is an ordinary member: it is told who the conductor is and
        // its conductor-only calls are refused.
        use! returned = connectKeyed port keyA
        let! status = callTool returned "get_cohort_status" [ "working_directory", box repoRoot ]
        status |> Expect.stringContains "A reads that B is the conductor" (sprintf "Conductor: %s" secondId)
        let! (asMember: string) = callTool returned "delegate_conductor" [ "agentName", box "first"; "toMember", box holderId ]
        asMember.Contains("is the conductor now") |> Expect.isFalse "A holds no conductor authority any more"
        asMember.Contains("already holds the conductor seat") |> Expect.isFalse "A is refused before the hand-off rule, as an ordinary member is"

        // The restart: the ledger replays the replacement.
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
        running <- None
        let! restarted, port2 = daemonIn dataDir
        running <- Some restarted
        let! afterRestart = CohortSeatCommand.call port2 dataDir (CohortSeatCommand.Ask.Look repoRoot)
        let after = expectReply "look after the restart" afterRestart
        after.Holder |> Expect.equal "the replacement is still the conductor after a restart" secondId
      finally
        match running with
        | Some p -> SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon p
        | None -> ()
    }
  ]
