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
  ]
