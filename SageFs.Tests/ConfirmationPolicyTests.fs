/// Only manage_local_data asks a person. stop_session, reset_fsi_session, hard_reset_fsi_session, switch_workflow and
/// stop_app are normal steps of everyday MCP use and never ask, for any session, whoever made it and whoever has run
/// code in it. The decision is `ConfirmationPolicy.decide`, a pure function of the tool, and the gate in McpServer.fs
/// applies it before it makes any elicitation request.
module SageFs.Tests.ConfirmationPolicyTests

open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Protocol
open SageFs
open SageFs.Affordances
open SageFs.ConfirmationPolicy
open SageFs.MemberTable
open SageFs.Server.McpServer

let alice = MemberId.Mcp "m-aaaaaaaaaaaaaaaa"
let bob = MemberId.Mcp "m-bbbbbbbbbbbbbbbb"

let canAsk = ClientCapabilities(Elicitation = ElicitationCapability())

let counting () : (ElicitRequestParams -> CancellationToken -> Task<ElicitResult>) * (unit -> int) =
  let mutable asked = 0
  let ask (_: ElicitRequestParams) (_: CancellationToken) =
    Interlocked.Increment(&asked) |> ignore
    Task.FromResult(ElicitResult(Action = "accept"))
  ask, (fun () -> Volatile.Read(&asked))

/// The tools an agent calls as normal steps of its loop. A person is never asked about these, for any session, whoever
/// made it and whoever has run code in it: a prompt on a routine step trains people to click through.
let normalWorkflowTools =
  [ ToolName.StopSession
    ToolName.ResetFsiSession
    ToolName.HardResetFsiSession
    ToolName.SwitchWorkflow
    ToolName.StopApp ]

[<Tests>]
let normalWorkflowTests =
  testList "normal workflow tools never ask" [
    for tool in normalWorkflowTools do
      let name = ToolName.toString tool
      testCase (sprintf "WHY — %s is outside the confirm tier, so the decision is that nobody is asked" name) <| fun _ ->
        ConfirmationPolicy.decide tool |> Expect.equal "not required" Confirmation.NotRequired
        confirmationStep canAsk name |> Expect.equal "the step is NotInTier" NotInTier
      testTask (sprintf "WHY — %s makes no elicitation request even when the client can answer" name) {
        let ask, asked = counting ()
        let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk ask name CancellationToken.None
        outcome |> Expect.isOk "the call runs"
        asked () |> Expect.equal "nobody was asked" 0
      }
      testCase (sprintf "WHY — %s stays in the destructive tier, because that is information on tools/list and not a prompt" name) <| fun _ ->
        destructiveTools |> Set.contains tool |> Expect.isTrue "still published with destructiveHint"
  ]

[<Tests>]
let decisionTests =
  testList "the confirmation decision" [

    testCase "WHY — manage_local_data is the one tool that asks, and it names what it takes away" <| fun _ ->
      match ConfirmationPolicy.decide ToolName.ManageLocalData with
      | Confirmation.NotRequired -> failtest "manage_local_data clears local stores that belong to no session, so it asks"
      | Confirmation.Required loss -> loss |> Expect.stringContains "the loss is the local stores" "local stores"

    testCase "WHY — of every tool SageFs registers, manage_local_data alone is asked about" <| fun _ ->
      ToolName.all
      |> List.filter (fun tool -> ConfirmationPolicy.decide tool <> Confirmation.NotRequired)
      |> Expect.equal "the whole confirm tier" [ ToolName.ManageLocalData ]

    testCase "WHY — the decision and the tier table agree, so a tool cannot ask without saying what it costs" <| fun _ ->
      for tool in ToolName.all do
        match ConfirmationPolicy.decide tool, Map.tryFind tool confirmationLosses with
        | Confirmation.Required loss, Some named -> loss |> Expect.equal (ToolName.toString tool) named
        | Confirmation.NotRequired, None -> ()
        | Confirmation.Required _, None
        | Confirmation.NotRequired, Some _ -> failtestf "%s: the decision and the table disagree" (ToolName.toString tool)

    testTask "WHY — manage_local_data asks once when the client can answer, and a decline refuses it" {
      let ask, asked = counting ()
      let! accepted = confirmWithin TestTimeouts.confirmationBudget canAsk ask "manage_local_data" CancellationToken.None
      accepted |> Expect.isOk "the prompt was accepted"
      asked () |> Expect.equal "one person was asked" 1
      let declines (_: ElicitRequestParams) (_: CancellationToken) = Task.FromResult(ElicitResult(Action = "decline"))
      let! declined = confirmWithin TestTimeouts.confirmationBudget canAsk declines "manage_local_data" CancellationToken.None
      declined |> Expect.equal "declined" (Result.Error Declined)
    }

    testTask "WHY — a client that cannot be asked is not asked about manage_local_data either, and is not refused" {
      let ask, asked = counting ()
      let! outcome = confirmWithin TestTimeouts.confirmationBudget (ClientCapabilities()) ask "manage_local_data" CancellationToken.None
      outcome |> Expect.isOk "skipped, as before"
      asked () |> Expect.equal "no request" 0
    }

    testCase "WHY — the gate reads session_id only from a tool that declares it, so it routes to the session the body acts on" <| fun _ ->
      routingSessionId "stop_session" (Some "named") |> Expect.equal "stop_session takes session_id" (Some "named")
      routingSessionId "hard_reset_fsi_session" (Some "named") |> Expect.equal "hard_reset_fsi_session takes it" (Some "named")
      routingSessionId "reset_fsi_session" (Some "named") |> Expect.equal "reset_fsi_session takes it" (Some "named")
      routingSessionId "switch_workflow" (Some "named") |> Expect.equal "switch_workflow has no session_id, so one sent is dropped" None
  ]

/// #184: `stop_session` refused "owned by another bound MCP connection" for a session that no connection had a record for.
/// The record (`SessionOwners`) lives in memory and is written when a connection creates a session, so a daemon
/// restart empties it, and so does a session made from the dashboard or the CLI. Nothing could ever satisfy the check for a
/// session the restart resumed. Only a session recorded to a DIFFERENT connection is another connection's to stop; one with
/// no record is stoppable, and nobody is asked first.
[<Tests>]
let stopSessionOwnershipTests =
  testList "stop_session ownership" [
    let refusal = "owned by another bound MCP connection"

    let onConnection (handle: string) (body: unit -> Task<string>) : Task<string> =
      task {
        let previous = SageFs.McpTools.currentTransportSessionId.Value
        SageFs.McpTools.currentTransportSessionId.Value <- Some handle
        try
          return! body ()
        finally
          SageFs.McpTools.currentTransportSessionId.Value <- previous
      }

    let stopOn (handle: string) (sessionId: string) : Task<string> =
      onConnection handle (fun () -> SageFs.McpTools.stopSessionOwned (SageFs.Tests.TestInfrastructure.sharedCtx ()) sessionId)

    let ownerOn (handle: string) : MemberId =
      SageFs.MemberTable.MemberId.ofConnectionHandle handle

    testCase "WHY — only a session recorded to a different member is refused; no record is not an owner" <| fun _ ->
      let who = MemberId.Mcp "m-1111111111111111"
      let other = MemberId.Mcp "m-2222222222222222"
      SessionOwnership.decide true None who |> Expect.equal "no record" SessionOwnership.NoRecord
      SessionOwnership.decide true (Some who) who |> Expect.equal "the caller's own" SessionOwnership.RecordedToCaller
      SessionOwnership.decide true (Some other) who |> Expect.equal "someone else's" SessionOwnership.RecordedToAnother
      SessionOwnership.decide false (Some other) who |> Expect.equal "off a connection there is no one to compare" SessionOwnership.NotOnAConnection

    testTask "WHY — a session with no owner on record can be stopped from a new connection, because a restart empties the record" {
      let sessionId = "no-owner-on-record-184"
      SessionOwners.ownerOf sessionId |> Expect.isNone "the fixture has no record"
      let! (reply: string) = stopOn "a-connection-that-came-after-the-restart" sessionId
      reply.Contains refusal |> Expect.isFalse (sprintf "no one else owns it, so the answer is not a refusal over ownership: %s" reply)
    }

    testTask "WHY — a session another connection created is still its creator's to stop" {
      let sessionId = "made-by-someone-else-184"
      SessionOwners.claim (ownerOn "the-creating-connection") sessionId
      try
        let! reply = stopOn "a-different-connection" sessionId
        reply |> Expect.stringContains "refused, and says whose it is" refusal
      finally
        SessionOwners.release sessionId
    }

    testTask "WHY — the connection that created a session can stop it" {
      let sessionId = "made-by-me-184"
      SessionOwners.claim (ownerOn "the-creating-connection") sessionId
      try
        let! (reply: string) = stopOn "the-creating-connection" sessionId
        reply.Contains refusal |> Expect.isFalse (sprintf "the creator is not refused: %s" reply)
      finally
        SessionOwners.release sessionId
    }
  ]
