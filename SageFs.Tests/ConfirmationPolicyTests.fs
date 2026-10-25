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

let decide tool target creator caller = ConfirmationPolicy.decide tool target creator caller

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

    testCase "WHY — a recorded key reads back to the identity it was written from" <| fun _ ->
      [ alice; bob; token; otherToken; tab; unbound ]
      |> List.iter (fun who ->
        SessionCreator.ofRecordedKey (MemberId.display who)
        |> Expect.equal (sprintf "%s round trips" (MemberId.display who)) (SessionCreator.Recorded who))
      SessionCreator.ofRecordedKey "" |> Expect.equal "empty key is no record" SessionCreator.Unrecorded

    testCase "WHY — every reason has a sentence" <| fun _ ->
      [ ConfirmationReason.ToolSpansMoreThanOneSession
        ConfirmationReason.NoSessionToJudge
        ConfirmationReason.CreatorNotRecorded
        ConfirmationReason.CreatedBySomeoneElse
        ConfirmationReason.CallerNotBound ]
      |> List.map ConfirmationReason.describe
      |> List.filter String.IsNullOrWhiteSpace
      |> Expect.isEmpty "no reason is left without words"

    testCase "WHY — the tools that skip the prompt on an own session are exactly the ones that act on one session, and all are on the confirm tier" <| fun _ ->
      singleSessionConfirmTools
      |> Expect.equal "the one-session confirm tools"
           (set [ ToolName.StopSession; ToolName.ResetFsiSession; ToolName.HardResetFsiSession; ToolName.SwitchWorkflow; ToolName.StopApp ])
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
        confirmCallWithin TestTimeouts.confirmationBudget canAsk ask (madeBy alice "s-1") alice (resolved "s-1") "hard_reset_fsi_session" CancellationToken.None
      outcome |> Expect.isOk "the call runs"
      asked () |> Expect.equal "nobody was asked" 0
    }

    testTask "WHY — a call by another connection on the same session still makes an elicitation request" {
      let ask, asked = counting ()
      let! outcome =
        confirmCallWithin TestTimeouts.confirmationBudget canAsk ask (madeBy alice "s-1") bob (resolved "s-1") "hard_reset_fsi_session" CancellationToken.None
      outcome |> Expect.isOk "the prompt was accepted"
      asked () |> Expect.equal "one person was asked" 1
    }

    testTask "WHY — a session with no creator record still asks, and a decline still refuses it" {
      let declines (_: ElicitRequestParams) (_: CancellationToken) = Task.FromResult(ElicitResult(Action = "decline"))
      let! outcome =
        confirmCallWithin TestTimeouts.confirmationBudget canAsk declines (fun _ -> SessionCreator.Unrecorded) alice (resolved "s-1") "stop_session" CancellationToken.None
      outcome |> Expect.equal "declined" (Result.Error Declined)
    }

    testTask "WHY — manage_local_data asks even when the caller made the session in its resolution" {
      let ask, asked = counting ()
      let admission = admissionFor "manage_local_data" None (SageFs.McpTools.GateResolution.Resolved(Routable "s-1"))
      let! _ = confirmCallWithin TestTimeouts.confirmationBudget canAsk ask (madeBy alice "s-1") alice admission "manage_local_data" CancellationToken.None
      asked () |> Expect.equal "asked once" 1
    }

    testTask "WHY — a client that cannot be asked is still not asked, and an own session needs no ask in the first place" {
      let ask, asked = counting ()
      let! outcome =
        confirmCallWithin TestTimeouts.confirmationBudget (ClientCapabilities()) ask (madeBy bob "s-1") alice (resolved "s-1") "hard_reset_fsi_session" CancellationToken.None
      outcome |> Expect.isOk "skipped as before"
      asked () |> Expect.equal "no request" 0
    }
  ]
