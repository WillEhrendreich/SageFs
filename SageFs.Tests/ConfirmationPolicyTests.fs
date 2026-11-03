/// A session an agent made itself does not ask a person before that agent resets or stops it. Every other
/// case still asks. The decision is `ConfirmationPolicy.decide`, a pure function that fails
/// closed, and the gate in McpServer.fs applies it before it makes any elicitation request.
module SageFs.Tests.ConfirmationPolicyTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open ModelContextProtocol.Protocol
open SageFs
open SageFs.Affordances
open SageFs.ConfirmationPolicy
open SageFs.MemberTable
open SageFs.McpSessionRouting
open SageFs.Server.McpServer

let alice = MemberId.Mcp "m-aaaaaaaaaaaaaaaa"
let bob = MemberId.Mcp "m-bbbbbbbbbbbbbbbb"
let token = MemberId.Capability "cap-one"
let otherToken = MemberId.Capability "cap-two"
let tab = MemberId.Browser "tab-1"
let unbound = MemberId.Minted "mcp"

let oneSessionTools = singleSessionConfirmTools |> Set.toList

/// The decision for a session nobody else has been in, which every case about the creator record is about.
let decide tool target creator caller = ConfirmationPolicy.decide tool target creator SessionActivity.quiet caller

/// The decision with who else has been in the session.
let decideWith activity tool target creator caller = ConfirmationPolicy.decide tool target creator activity caller

let ranCode (sources: EvalSource list) : SessionActivity = { SessionActivity.quiet with RanCode = Set.ofList sources }

let mine = TargetSession.Session "s-1"

[<Tests>]
let tests =
  testList "Confirmation for a session the caller made" [

    testCase "WHY — the connection that made a session is not asked to reset it" <| fun _ ->
      decide ToolName.HardResetFsiSession mine (SessionCreator.Recorded alice) alice
      |> Expect.equal "own session by connection" (Confirmation.NotRequired(OwnSession.CreatedByThisCaller "s-1"))

    testCase "WHY — a member token that made a session is not asked either, because the token outranks the connection" <| fun _ ->
      decide ToolName.StopSession mine (SessionCreator.Recorded token) token
      |> Expect.equal "own session by token" (Confirmation.NotRequired(OwnSession.CreatedByThisCaller "s-1"))

    testCase "WHY — the operator is bound like a token, so a session it made is its own and another caller's is not" <| fun _ ->
      decide ToolName.StopSession mine (SessionCreator.Recorded MemberId.Operator) MemberId.Operator
      |> Expect.equal "own session by operator" (Confirmation.NotRequired(OwnSession.CreatedByThisCaller "s-1"))
      decide ToolName.StopSession mine (SessionCreator.Recorded alice) MemberId.Operator
      |> Expect.equal "a connection made it" (Confirmation.Required ConfirmationReason.CreatedBySomeoneElse)
      decide ToolName.StopSession mine (SessionCreator.Recorded MemberId.Operator) alice
      |> Expect.equal "the operator made it, a connection calls" (Confirmation.Required ConfirmationReason.CreatedBySomeoneElse)

    testCase "WHY — a session a person made (no record) still asks" <| fun _ ->
      decide ToolName.HardResetFsiSession mine SessionCreator.Unrecorded alice
      |> Expect.equal "no record" (Confirmation.Required ConfirmationReason.CreatorNotRecorded)

    testCase "WHY — a session another connection made still asks" <| fun _ ->
      decide ToolName.StopSession mine (SessionCreator.Recorded bob) alice
      |> Expect.equal "other connection" (Confirmation.Required ConfirmationReason.CreatedBySomeoneElse)

    testCase "WHY — a session made over the connection still asks a caller that now acts under a token, and the other way round" <| fun _ ->
      decide ToolName.StopSession mine (SessionCreator.Recorded alice) token
      |> Expect.equal "connection made it, token calls" (Confirmation.Required ConfirmationReason.CreatedBySomeoneElse)
      decide ToolName.StopSession mine (SessionCreator.Recorded token) alice
      |> Expect.equal "token made it, connection calls" (Confirmation.Required ConfirmationReason.CreatedBySomeoneElse)
      decide ToolName.StopSession mine (SessionCreator.Recorded token) otherToken
      |> Expect.equal "another token" (Confirmation.Required ConfirmationReason.CreatedBySomeoneElse)

    testCase "WHY — a tool that acts beyond one session asks even on a session the caller made" <| fun _ ->
      [ ToolName.ManageLocalData; ToolName.TidyWorkspace; ToolName.MintMember; ToolName.RevokeMember ]
      |> List.iter (fun tool ->
        decide tool mine (SessionCreator.Recorded alice) alice
        |> Expect.equal (sprintf "%s spans more than one session" (ToolName.toString tool)) (Confirmation.Required ConfirmationReason.ToolSpansMoreThanOneSession))

    testCase "WHY — a call that names no session asks, and so does a blank id" <| fun _ ->
      decide ToolName.HardResetFsiSession TargetSession.NoSession (SessionCreator.Recorded alice) alice
      |> Expect.equal "no session" (Confirmation.Required ConfirmationReason.NoSessionToJudge)
      decide ToolName.HardResetFsiSession (TargetSession.Session "  ") (SessionCreator.Recorded alice) alice
      |> Expect.equal "blank id" (Confirmation.Required ConfirmationReason.NoSessionToJudge)

    testCase "WHY — a caller that is not bound to a connection or token asks, even when the recorded name matches its own" <| fun _ ->
      decide ToolName.StopSession mine (SessionCreator.Recorded unbound) unbound
      |> Expect.equal "unbound caller" (Confirmation.Required ConfirmationReason.CallerNotBound)
      decide ToolName.StopSession mine (SessionCreator.Recorded tab) tab
      |> Expect.equal "browser caller" (Confirmation.Required ConfirmationReason.CallerNotBound)

    testCase "WHY — every reason has a sentence" <| fun _ ->
      [ ConfirmationReason.ToolSpansMoreThanOneSession
        ConfirmationReason.NoSessionToJudge
        ConfirmationReason.CreatorNotRecorded
        ConfirmationReason.CreatedBySomeoneElse
        ConfirmationReason.SomeoneElseRanCode
        ConfirmationReason.AnotherAgentIsWorkingInIt
        ConfirmationReason.CallerNotBound ]
      |> List.map ConfirmationReason.describe
      |> List.filter String.IsNullOrWhiteSpace
      |> Expect.isEmpty "no reason is left without words"

    testCase "WHY — the tools that skip the prompt on an own session are exactly the ones that act on one session, and all are on the confirm tier" <| fun _ ->
      singleSessionConfirmTools
      |> Expect.equal "the one-session confirm tools"
           (set [ ToolName.StopSession; ToolName.ResetFsiSession; ToolName.HardResetFsiSession; ToolName.SwitchWorkflow ])
      singleSessionConfirmTools
      |> Set.filter (fun tool -> not (Map.containsKey tool confirmationLosses))
      |> Expect.isEmpty "every one-session tool is on the confirm tier"

    // ── the property ────────────────────────────────────────────────────────────────────────────────

    testProperty "WHY — over any tool, target, creator and caller, nobody is spared the prompt unless the tool acts on one session and the recorded creator is the caller" <| fun () ->
      let tools = ToolName.all |> Gen.elements
      let identities = [ alice; bob; token; otherToken; tab; unbound ] |> Gen.elements
      let creators =
        Gen.oneof [ Gen.constant SessionCreator.Unrecorded; identities |> Gen.map SessionCreator.Recorded ]
      let targets =
        Gen.oneof
          [ Gen.constant TargetSession.NoSession
            Gen.elements [ "s-1"; "s-2"; ""; "  " ] |> Gen.map TargetSession.Session ]
      let scenario = gen {
        let! tool = tools
        let! target = targets
        let! creator = creators
        let! caller = identities
        return tool, target, creator, caller }
      Prop.forAll (Arb.fromGen scenario) (fun (tool, target, creator, caller) ->
        match decide tool target creator caller with
        | Confirmation.Required _ -> true
        | Confirmation.NotRequired(OwnSession.CreatedByThisCaller sid) ->
          Set.contains tool singleSessionConfirmTools
          && creator = SessionCreator.Recorded caller
          && target = TargetSession.Session sid
          && not (String.IsNullOrWhiteSpace sid))

    testProperty "WHY — the converse: a one-session tool on a named session made by a bound caller is never asked, so the rule is not an always-ask" <| fun () ->
      let boundCallers = [ alice; bob; token; otherToken ] |> Gen.elements
      let tools = oneSessionTools |> Gen.elements
      Prop.forAll (Arb.fromGen (Gen.zip tools boundCallers)) (fun (tool, caller) ->
        decide tool mine (SessionCreator.Recorded caller) caller = Confirmation.NotRequired(OwnSession.CreatedByThisCaller "s-1"))
  ]

/// #176 as the PR first framed it: a session the caller made is only the caller's to lose while nobody else has worked
/// in it. Someone else's code in it, or another agent in it now, brings the prompt back.
[<Tests>]
let activityTests =
  testList "Confirmation when someone else has been in the caller's session" [
    let own = SessionCreator.Recorded alice

    testCase "WHY — the caller's own evals leave the session its own" <| fun _ ->
      decideWith (ranCode [ EvalSource.Member alice ]) ToolName.HardResetFsiSession mine own alice
      |> Expect.equal "only the caller ran code" (Confirmation.NotRequired(OwnSession.CreatedByThisCaller "s-1"))

    testCase "WHY — code from the dashboard keeps the prompt, though the occupancy table calls a tab an Observer" <| fun _ ->
      decideWith (ranCode [ EvalSource.Member alice; EvalSource.Dashboard ]) ToolName.StopSession mine own alice
      |> Expect.equal "a person's definitions from the dashboard are at stake" (Confirmation.Required ConfirmationReason.SomeoneElseRanCode)

    testCase "WHY — code an editor sent over /exec keeps the prompt, though an editor records no worker presence" <| fun _ ->
      decideWith (ranCode [ EvalSource.Member alice; EvalSource.Member(MemberId.Minted "cli-integrated") ]) ToolName.StopSession mine own alice
      |> Expect.equal "a person's definitions from VS Code or Neovim are at stake" (Confirmation.Required ConfirmationReason.SomeoneElseRanCode)

    testCase "WHY — code from the deprecated TUI keeps the prompt, though its route names no caller" <| fun _ ->
      decideWith (ranCode [ EvalSource.Tui ]) ToolName.ResetFsiSession mine own alice
      |> Expect.equal "a person's definitions from the TUI are at stake" (Confirmation.Required ConfirmationReason.SomeoneElseRanCode)

    testCase "WHY — an agent that ran code here and then moved to another session keeps the prompt, though presence forgot it" <| fun _ ->
      decideWith (ranCode [ EvalSource.Member alice; EvalSource.Member bob ]) ToolName.SwitchWorkflow mine own alice
      |> Expect.equal "its definitions are still in the session" (Confirmation.Required ConfirmationReason.SomeoneElseRanCode)

    testCase "WHY — another agent in the caller's own session keeps the prompt, since its work would go too" <| fun _ ->
      decideWith { SessionActivity.quiet with Presence = Presence.AnotherAgentIsIn } ToolName.HardResetFsiSession mine own alice
      |> Expect.equal "another worker is in it" (Confirmation.Required ConfirmationReason.AnotherAgentIsWorkingInIt)

    testCase "WHY — stop_app asks on the caller's own, quiet session, since its app may have users SageFs cannot see" <| fun _ ->
      decide ToolName.StopApp mine own alice
      |> Expect.equal "a browser on its port or a game window is invisible here" (Confirmation.Required ConfirmationReason.ToolSpansMoreThanOneSession)

    testCase "WHY — activity only ever adds a prompt: on a session that already asks, it does not change the reason" <| fun _ ->
      let busy = { RanCode = Set.ofList [ EvalSource.Dashboard ]; Presence = Presence.AnotherAgentIsIn }
      decideWith busy ToolName.StopSession mine (SessionCreator.Recorded bob) alice
      |> Expect.equal "someone else made it" (Confirmation.Required ConfirmationReason.CreatedBySomeoneElse)
      decideWith busy ToolName.StopSession mine SessionCreator.Unrecorded alice
      |> Expect.equal "no record" (Confirmation.Required ConfirmationReason.CreatorNotRecorded)

    testProperty "WHY — any source of code besides the caller brings the prompt back" <| fun (other: string) (pick: byte) ->
      let foreign =
        match pick % 4uy with
        | 0uy -> EvalSource.Dashboard
        | 1uy -> EvalSource.Tui
        | 2uy -> EvalSource.Member(MemberId.Mcp other)
        | _ -> EvalSource.Member(MemberId.Minted other)
      foreign = EvalSource.Member alice
      || decideWith (ranCode [ EvalSource.Member alice; foreign ]) ToolName.StopSession mine own alice
         = Confirmation.Required ConfirmationReason.SomeoneElseRanCode

    testProperty "WHY — activity is read exactly when the session is otherwise the caller's own, so skipping the read never changes the answer" <| fun () ->
      let tools = ToolName.all |> Gen.elements
      let identities = [ alice; bob; token; tab; unbound ] |> Gen.elements
      let creators = Gen.oneof [ Gen.constant SessionCreator.Unrecorded; identities |> Gen.map SessionCreator.Recorded ]
      let targets = Gen.oneof [ Gen.constant TargetSession.NoSession; Gen.elements [ "s-1"; "" ] |> Gen.map TargetSession.Session ]
      let busy = { RanCode = Set.ofList [ EvalSource.Dashboard ]; Presence = Presence.AnotherAgentIsIn }
      let scenario = gen {
        let! tool = tools
        let! target = targets
        let! creator = creators
        let! caller = identities
        return tool, target, creator, caller }
      Prop.forAll (Arb.fromGen scenario) (fun (tool, target, creator, caller) ->
        readsActivity tool target creator caller
        || decideWith busy tool target creator caller = decideWith SessionActivity.quiet tool target creator caller)

    testCase "WHY — a watching dashboard tab is not another worker, or an open dashboard would undo the fix" <| fun _ ->
      let worker name : SessionOperations.SessionOccupancy = { AgentName = name; Role = SessionOperations.OccupantRole.Worker }
      let observer name : SessionOperations.SessionOccupancy = { AgentName = name; Role = SessionOperations.OccupantRole.Observer }
      let me = MemberId.display alice
      SessionOperations.SessionOccupancy.hasWorkerOtherThan me [ worker me ] |> Expect.isFalse "the caller alone"
      SessionOperations.SessionOccupancy.hasWorkerOtherThan me [ worker me; observer "browser:tab-1" ] |> Expect.isFalse "a tab that watches"
      SessionOperations.SessionOccupancy.hasWorkerOtherThan me [ worker me; worker (MemberId.display bob) ] |> Expect.isTrue "another agent"

    testCase "WHY — a session's record of who ran code goes with its creator record, so a stopped session leaves nothing behind" <| fun _ ->
      let session = Guid.NewGuid().ToString "N"
      SessionOwners.claim alice session
      SessionEvaluators.record session (EvalSource.Member alice)
      SessionEvaluators.record session EvalSource.Dashboard
      SessionEvaluators.record session (EvalSource.Member alice)
      SessionEvaluators.ofSession session |> Expect.equal "each source once" (Set.ofList [ EvalSource.Member alice; EvalSource.Dashboard ])
      SessionOwners.release session
      SessionEvaluators.ofSession session |> Expect.isEmpty "released with the owner"
      creatorOfSession session |> Expect.equal "and the owner is gone" SessionCreator.Unrecorded

    testCase "WHY — the gate reads session_id only from a tool that declares it, so the prompt judges the session the body acts on" <| fun _ ->
      routingSessionId "stop_session" (Some "named") |> Expect.equal "stop_session takes session_id" (Some "named")
      routingSessionId "hard_reset_fsi_session" (Some "named") |> Expect.equal "hard_reset_fsi_session takes it" (Some "named")
      routingSessionId "reset_fsi_session" (Some "named") |> Expect.equal "reset_fsi_session takes it" (Some "named")
      routingSessionId "switch_workflow" (Some "named") |> Expect.equal "switch_workflow has no session_id, so one sent is dropped" None
  ]

// ── The gate in McpServer.fs ────────────────────────────────────────────────────────────────────────

let canAsk = ClientCapabilities(Elicitation = ElicitationCapability())

let admissionFor (toolName: string) (sessionId: string option) (resolution: SageFs.McpTools.GateResolution) : SageFs.McpTools.GateAdmission =
  { ToolName = toolName; SessionId = sessionId; WorkingDirectory = None; Resolution = resolution }

let resolved (sid: string) =
  admissionFor "hard_reset_fsi_session" None (SageFs.McpTools.GateResolution.Resolved(Routable sid))

let madeBy (maker: MemberId) (sid: string) : string -> SessionCreator =
  fun asked ->
    match asked = sid with
    | true -> SessionCreator.Recorded maker
    | false -> SessionCreator.Unrecorded

/// Nobody but the caller has been in any session.
let nobodyElse : string -> SessionActivity = fun _ -> SessionActivity.quiet

let counting () : (ElicitRequestParams -> CancellationToken -> Task<ElicitResult>) * (unit -> int) =
  let mutable asked = 0
  let ask (_: ElicitRequestParams) (_: CancellationToken) =
    Interlocked.Increment(&asked) |> ignore
    Task.FromResult(ElicitResult(Action = "accept"))
  ask, (fun () -> Volatile.Read(&asked))

[<Tests>]
let gateTests =
  testList "The confirmation gate in McpServer" [

    testCase "WHY — a session the gate resolved is the target, whatever state it is in, and a session it could not find is no target" <| fun _ ->
      let target resolution = targetSessionOf (admissionFor "hard_reset_fsi_session" None resolution)
      target (SageFs.McpTools.GateResolution.Resolved(Routable "s-1")) |> Expect.equal "routable" (TargetSession.Session "s-1")
      target (SageFs.McpTools.GateResolution.Resolved(Gone "no session")) |> Expect.equal "gone" TargetSession.NoSession
      target SageFs.McpTools.GateResolution.NotResolved |> Expect.equal "not resolved, no id" TargetSession.NoSession

    testCase "WHY — stop_session names its session in the call, because the gate does not resolve it" <| fun _ ->
      targetSessionOf (admissionFor "stop_session" (Some "s-9") SageFs.McpTools.GateResolution.NotResolved)
      |> Expect.equal "the id the caller passed" (TargetSession.Session "s-9")

    testTask "WHY — a call by the connection that made the session makes no elicitation request" {
      let ask, asked = counting ()
      let! outcome =
        confirmCallWithin TestTimeouts.confirmationBudget canAsk ask (madeBy alice "s-1") nobodyElse alice (resolved "s-1") "hard_reset_fsi_session" CancellationToken.None
      outcome |> Expect.isOk "the call runs"
      asked () |> Expect.equal "nobody was asked" 0
    }

    testTask "WHY — a call by another connection on the same session still makes an elicitation request" {
      let ask, asked = counting ()
      let! outcome =
        confirmCallWithin TestTimeouts.confirmationBudget canAsk ask (madeBy alice "s-1") nobodyElse bob (resolved "s-1") "hard_reset_fsi_session" CancellationToken.None
      outcome |> Expect.isOk "the prompt was accepted"
      asked () |> Expect.equal "one person was asked" 1
    }

    testTask "WHY — a call by the connection that made the session still asks once a person has run code in it" {
      let ask, asked = counting ()
      let aPersonWorkedHere (_: string) = { SessionActivity.quiet with RanCode = Set.ofList [ EvalSource.Dashboard ] }
      let! outcome =
        confirmCallWithin TestTimeouts.confirmationBudget canAsk ask (madeBy alice "s-1") aPersonWorkedHere alice (resolved "s-1") "stop_session" CancellationToken.None
      outcome |> Expect.isOk "the prompt was accepted"
      asked () |> Expect.equal "one person was asked" 1
    }

    testCase "WHY — who else has been in a session is read only for a call on the caller's own session, so an eval does not pay for an occupancy scan" <| fun _ ->
      let mutable reads = 0
      let counted (_: string) =
        reads <- reads + 1
        SessionActivity.quiet
      for tool in [ "send_fsharp_code"; "list_sessions"; "stop_app"; "manage_local_data"; "frobnicate" ] do
        confirmationFor (madeBy alice "s-1") counted alice (resolved "s-1") tool |> ignore
      confirmationFor (madeBy bob "s-1") counted alice (resolved "s-1") "stop_session" |> ignore
      confirmationFor (madeBy alice "s-1") counted alice (admissionFor "stop_session" None SageFs.McpTools.GateResolution.NotResolved) "stop_session" |> ignore
      reads |> Expect.equal "nothing that already asks, or never asks, read it" 0
      confirmationFor (madeBy alice "s-1") counted alice (resolved "s-1") "stop_session" |> ignore
      reads |> Expect.equal "a call on the caller's own session reads it once" 1

    testTask "WHY — a session with no creator record still asks, and a decline still refuses it" {
      let declines (_: ElicitRequestParams) (_: CancellationToken) = Task.FromResult(ElicitResult(Action = "decline"))
      let! outcome =
        confirmCallWithin TestTimeouts.confirmationBudget canAsk declines (fun _ -> SessionCreator.Unrecorded) nobodyElse alice (resolved "s-1") "stop_session" CancellationToken.None
      outcome |> Expect.equal "declined" (Result.Error Declined)
    }

    testTask "WHY — manage_local_data asks even when the caller made the session in its resolution" {
      let ask, asked = counting ()
      let admission = admissionFor "manage_local_data" None (SageFs.McpTools.GateResolution.Resolved(Routable "s-1"))
      let! _ = confirmCallWithin TestTimeouts.confirmationBudget canAsk ask (madeBy alice "s-1") nobodyElse alice admission "manage_local_data" CancellationToken.None
      asked () |> Expect.equal "asked once" 1
    }

    testTask "WHY — a client that cannot be asked is still not asked, and an own session needs no ask in the first place" {
      let ask, asked = counting ()
      let! outcome =
        confirmCallWithin TestTimeouts.confirmationBudget (ClientCapabilities()) ask (madeBy bob "s-1") nobodyElse alice (resolved "s-1") "hard_reset_fsi_session" CancellationToken.None
      outcome |> Expect.isOk "skipped as before"
      asked () |> Expect.equal "no request" 0
    }

    // ── the daemon's own creator record, read the way the live gate reads it ───────────────────────

    testCase "WHY — the record keeps the identity it was given, so only that identity skips the prompt on its own session" <| fun _ ->
      let everyone = [ alice; bob; token; otherToken; tab; unbound; MemberId.Operator ]
      for owner in everyone do
        let sessionId = sprintf "owner-record-%s" (MemberId.display owner)
        SessionOwners.claim owner sessionId
        try
          creatorOfSession sessionId
          |> Expect.equal (sprintf "%s is read back as itself" (MemberId.display owner)) (SessionCreator.Recorded owner)
          for caller in everyone do
            let expected =
              match caller, owner = caller with
              | MemberId.Browser _, _
              | MemberId.Minted _, _ -> Confirmation.Required ConfirmationReason.CallerNotBound
              | MemberId.Mcp _, true
              | MemberId.Capability _, true
              | MemberId.Operator, true -> Confirmation.NotRequired(OwnSession.CreatedByThisCaller sessionId)
              | MemberId.Mcp _, false
              | MemberId.Capability _, false
              | MemberId.Operator, false -> Confirmation.Required ConfirmationReason.CreatedBySomeoneElse
            confirmationFor creatorOfSession nobodyElse caller (resolved sessionId) "hard_reset_fsi_session"
            |> Expect.equal (sprintf "made by %s, called by %s" (MemberId.display owner) (MemberId.display caller)) expected
        finally
          SessionOwners.release sessionId

    testCase "WHY — a session nobody recorded reads back as unrecorded, and releasing a record that is not there does nothing" <| fun _ ->
      SessionOwners.release "owner-never-recorded"
      creatorOfSession "owner-never-recorded" |> Expect.equal "no record" SessionCreator.Unrecorded

    testTask "WHY — an unbound agent that names itself with another identity's display text is still an unbound agent, so that identity is asked" {
      for who in [ alice; bob; token; otherToken ] do
        let sessionId = sprintf "owner-lookalike-%s" (MemberId.display who)
        let ask, asked = counting ()
        // No connection is bound in a test, so the agent name is the identity: `Minted` text that looks like `who`.
        SessionOwners.claim (SageFs.McpTools.memberIdFor (MemberId.display who)) sessionId
        try
          let! outcome =
            confirmCallWithin TestTimeouts.confirmationBudget canAsk ask creatorOfSession nobodyElse who (resolved sessionId) "hard_reset_fsi_session" CancellationToken.None
          outcome |> Expect.isOk (sprintf "%s: the prompt was accepted" (MemberId.display who))
          asked () |> Expect.equal (sprintf "%s did not make that session, so one person was asked" (MemberId.display who)) 1
        finally
          SessionOwners.release sessionId
    }
  ]

/// #184: `stop_session` refused "owned by another bound MCP connection" for a session that no connection had a record for.
/// The record (`SessionOwners`) lives in memory and is written when a connection creates a session, so a daemon
/// restart empties it, and so does a session made from the dashboard or the CLI. Nothing could ever satisfy the check for a
/// session the restart resumed. Only a session recorded to a DIFFERENT connection is another connection's to stop; one with
/// no record is stoppable, and the confirmation gate (`SessionCreator.Unrecorded`) still asks a person first.
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
