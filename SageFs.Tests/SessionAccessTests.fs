/// One rule for who may stop, reset, hard-reset, switch the workflow of, or stop the app of a session: the member that made
/// it, the conductor of the cohort it sits in, or the operator. A different bound member is refused, with the next action
/// it can take, and nobody is ever asked a question. `reset_fsi_session`, `hard_reset_fsi_session`, `switch_workflow`,
/// `stop_app` and `POST /api/sessions/stop` had no check at all, and `stop_session` had a rule of its own that named
/// "a privileged daemon control" that does not exist (roast 2026-11-08, improvement 9 and the UI/UX refusal).
module SageFs.Tests.SessionAccessTests

open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Affordances
open SageFs.ConfirmationPolicy
open SageFs.MemberTable

let private alice = MemberId.Mcp "m-aaaaaaaaaaaaaaaa"
let private bob = MemberId.Mcp "m-bbbbbbbbbbbbbbbb"
let private carol = MemberId.Capability "cccccccccccccccc"

/// A session id in the shape the registry accepts, so the gate resolves it like a real one.
let private freshSessionId () : string = WorkerProtocol.SessionId.value (WorkerProtocol.SessionId.newId ())

/// The whole domain is small enough to walk: every maker against every caller.
let private everyMember : MemberId list = [ alice; bob; carol; MemberId.Minted "dave"; MemberId.Operator ]

let private everyMaker : SessionMaker list = SessionMaker.NoRecord :: (everyMember |> List.map SessionMaker.MadeBy)

let private everyCaller : SessionCaller list =
  [ SessionCaller.Operator; SessionCaller.Unbound ]
  @ (everyMember |> List.map SessionCaller.Conductor)
  @ (everyMember |> List.map SessionCaller.Member)

let private isAllowed (access: SessionAccess) : bool =
  match access with
  | SessionAccess.Allowed _ -> true
  | SessionAccess.Refused _ -> false

[<Tests>]
let tests =
  testList "Session access (who may act on a session someone made)" [

    testList "the rule" [
      testCase "WHY - the maker, the conductor and the operator act, a different bound member is refused with the maker named" <| fun _ ->
        let made = SessionMaker.MadeBy alice
        SessionAccess.decide made (SessionCaller.Member alice) |> Expect.equal "the maker" (SessionAccess.Allowed AccessReason.ByMaker)
        SessionAccess.decide made (SessionCaller.Conductor bob) |> Expect.equal "the conductor" (SessionAccess.Allowed AccessReason.ByConductor)
        SessionAccess.decide made SessionCaller.Operator |> Expect.equal "the operator" (SessionAccess.Allowed AccessReason.ByOperator)
        SessionAccess.decide made (SessionCaller.Member bob) |> Expect.equal "another member" (SessionAccess.Refused alice)

      testCase "WHY - a session nobody is recorded as having made, and a call off any connection, are not another member's to guard" <| fun _ ->
        SessionAccess.decide SessionMaker.NoRecord (SessionCaller.Member bob)
        |> Expect.equal "no record (a dashboard or CLI session, or one a restart emptied)" (SessionAccess.Allowed AccessReason.NoMakerOnRecord)
        SessionAccess.decide (SessionMaker.MadeBy alice) SessionCaller.Unbound
        |> Expect.equal "the dashboard, an editor or the CLI is on no connection to compare" (SessionAccess.Allowed AccessReason.NotOnAConnection)

      testCase "WHY - over every maker and every caller, a call is refused exactly when a bound member that is not the maker meets a recorded maker" <| fun _ ->
        for maker in everyMaker do
          for caller in everyCaller do
            let refused =
              match maker, caller with
              | SessionMaker.MadeBy owner, SessionCaller.Member who -> who <> owner
              | _ -> false
            isAllowed (SessionAccess.decide maker caller) |> Expect.equal (sprintf "%A by %A" maker caller) (not refused)

      testCase "WHY - the operator is never refused, whoever made the session" <| fun _ ->
        for maker in everyMaker do
          isAllowed (SessionAccess.decide maker SessionCaller.Operator) |> Expect.isTrue (sprintf "%A" maker)
    ]

    testList "who is calling" [
      testCase "WHY - the operator credential outranks everything else on the request" <| fun _ ->
        SessionCaller.resolve true (Some alice) (Cohort.Authority.Conductor alice) |> Expect.equal "operator" SessionCaller.Operator
        SessionCaller.resolve true None Cohort.Authority.Anonymous |> Expect.equal "operator with no member" SessionCaller.Operator

      testCase "WHY - no bound identity is an unbound call, whatever the cohort says" <| fun _ ->
        SessionCaller.resolve false None Cohort.Authority.Anonymous |> Expect.equal "unbound" SessionCaller.Unbound

      testCase "WHY - the conductor's own id, and only that, is the conductor" <| fun _ ->
        SessionCaller.resolve false (Some alice) (Cohort.Authority.Conductor alice) |> Expect.equal "the conductor" (SessionCaller.Conductor alice)
        SessionCaller.resolve false (Some alice) (Cohort.Authority.Member(alice, Cohort.JoinableRole.Implementer))
        |> Expect.equal "an ordinary member" (SessionCaller.Member alice)
        SessionCaller.resolve false (Some alice) Cohort.Authority.Anonymous |> Expect.equal "bound but not in the cohort" (SessionCaller.Member alice)
        SessionCaller.resolve false (Some alice) (Cohort.Authority.Conductor bob)
        |> Expect.equal "another member holds the seat" (SessionCaller.Member alice)
    ]

    testList "the five actions" [
      testCase "WHY - the guarded tools are the five that stop or restart a session, each published as destructive" <| fun _ ->
        SessionAction.all
        |> List.map SessionAction.toolName
        |> Expect.equal
          "in one place"
          [ ToolName.StopSession; ToolName.ResetFsiSession; ToolName.HardResetFsiSession; ToolName.SwitchWorkflow; ToolName.StopApp ]
        for action in SessionAction.all do
          destructiveTools
          |> Set.contains (SessionAction.toolName action)
          |> Expect.isTrue (sprintf "%A is in the destructive tier, which is what tools/list tells a client" action)

      testCase "WHY - a tool maps to its action and back, and every other tool is outside the rule" <| fun _ ->
        for action in SessionAction.all do
          SessionAction.ofTool (SessionAction.toolName action) |> Expect.equal "round trip" (Some action)
        ToolName.all
        |> List.filter (fun tool -> not (List.contains tool (SessionAction.all |> List.map SessionAction.toolName)))
        |> List.iter (fun tool -> SessionAction.ofTool tool |> Expect.isNone (sprintf "%s is not guarded" (ToolName.toString tool)))
    ]

    testList "the refusal" [
      testCase "WHY - it names the session, the maker and the action, and gives three next steps that exist" <| fun _ ->
        for action in SessionAction.all do
          match SessionAccess.refusal action "s-42" alice with
          | SageFsError.CohortActionFailed(reason, next) ->
            reason |> Expect.stringContains "names the session" "s-42"
            reason |> Expect.stringContains "names the maker" (MemberId.display alice)
            reason |> Expect.stringContains "names the action" (SessionAction.verb action)
            next |> Expect.stringContains "the maker can do it" "maker"
            next |> Expect.stringContains "the conductor can do it" "conductor"
            next |> Expect.stringContains "the operator can do it" "operator"
            next |> Expect.stringContains "or work in a session of your own" "create_project_session"
            (reason + next).Contains "privileged daemon control" |> Expect.isFalse "no control that does not exist"
          | other -> failtestf "expected an authority refusal (reason and next action), got %A" other

      testCase "WHY - the refusal is in the client category and its next action rides in the text an agent reads" <| fun _ ->
        let error = SessionAccess.refusal SessionAction.Stop "s-42" alice
        SageFsError.category error |> Expect.equal "the caller's own to fix" ErrorCategory.Client
        SageFsError.describeForAgent error |> Expect.stringContains "the next action rides in the text" "operator"
    ]

    testList "applied to an HTTP request (the stop, stop-app and workflow routes)" [
      let lookups = McpTools.sessionAccessLookups (TestInfrastructure.sharedCtx ())
      let store = McpTools.capabilityStore
      let maker = MemberId.ofConnectionHandle "the-making-connection"

      let userOf (clientKey: MemberId option) (operator: bool) : System.Security.Claims.ClaimsPrincipal =
        let withKey =
          match clientKey with
          | Some who -> McpCapability.withClientKey who null
          | None -> null
        match operator with
        | true -> McpCapability.withOperatorPresentation EdgeCredential.OperatorPresentation.Recognized withKey
        | false -> withKey

      let refusedFor (sessionId: string) (user: System.Security.Claims.ClaimsPrincipal) : Task<bool> =
        task {
          let! refusal = McpHttpAccess.refusal lookups store user SessionAction.Stop sessionId
          return Option.isSome refusal
        }

      testTask "WHY - a request bound to another member is refused, and the maker, the operator and an unbound request are not" {
        let sessionId = freshSessionId ()
        SessionOwners.claim maker sessionId
        try
          let! other = refusedFor sessionId (userOf (Some bob) false)
          other |> Expect.isTrue "another member's client key is refused"
          let! makers = refusedFor sessionId (userOf (Some maker) false)
          makers |> Expect.isFalse "the maker's own client key is not"
          let! operator = refusedFor sessionId (userOf (Some bob) true)
          operator |> Expect.isFalse "the operator credential is not, whoever else the request names"
          let! dashboard = refusedFor sessionId (userOf None false)
          dashboard |> Expect.isFalse "a request bound to nobody (the dashboard, an editor, the CLI) is not"
        finally
          SessionOwners.release sessionId
      }

      testTask "WHY - a session with no maker on record is open to every request" {
        let! other = refusedFor (freshSessionId ()) (userOf (Some bob) false)
        other |> Expect.isFalse "nobody else owns it"
      }
    ]

    testList "applied at the tool gate" [
      let ctx = TestInfrastructure.sharedCtx ()

      let asMember (handle: string) (operator: bool) (body: unit -> Task<'a>) : Task<'a> =
        task {
          let previous = McpTools.currentTransportSessionId.Value
          let previousOperator = McpTools.currentOperatorClaim.Value
          McpTools.currentTransportSessionId.Value <- Some handle
          McpTools.currentOperatorClaim.Value <- (match operator with true -> Some EdgeCredential.OperatorClaim.Recognized | false -> None)
          try
            return! body ()
          finally
            McpTools.currentTransportSessionId.Value <- previous
            McpTools.currentOperatorClaim.Value <- previousOperator
        }

      let gate (handle: string) (operator: bool) (tool: string) (sessionId: string) : Task<string option> =
        asMember handle operator (fun () ->
          task {
            let! verdict = McpTools.admitToolCallWithin TestTimeouts.patienceBrief ctx "mcp" (Some sessionId) None tool
            return
              match verdict with
              | Ok _ -> None
              | Error message -> Some message
          })

      // `stop_session` is checked in its own body (its gate class lets it reach a faulted session), the other four at the gate.
      let gatedTools = [ "reset_fsi_session"; "hard_reset_fsi_session"; "switch_workflow"; "stop_app" ]
      let aboutAnotherMember (message: string option) : bool =
        match message with
        | Some text -> text.Contains "made by another member"
        | None -> false

      for tool in gatedTools do
        testTask (sprintf "WHY - %s on a session another member made is refused at the gate, and the maker and the operator are not" tool) {
          let sessionId = freshSessionId ()
          SessionOwners.claim (MemberId.ofConnectionHandle "the-making-connection") sessionId
          try
            let! other = gate "a-different-connection" false tool sessionId
            aboutAnotherMember other |> Expect.isTrue (sprintf "refused over whose it is: %A" other)
            let! maker = gate "the-making-connection" false tool sessionId
            aboutAnotherMember maker |> Expect.isFalse (sprintf "the maker is not refused: %A" maker)
            let! operator = gate "a-different-connection" true tool sessionId
            aboutAnotherMember operator |> Expect.isFalse (sprintf "the operator is not refused: %A" operator)
          finally
            SessionOwners.release sessionId
        }

      testTask "WHY - a session with no maker on record is open to any connection, because a restart empties the record" {
        let sessionId = freshSessionId ()
        SessionOwners.ownerOf sessionId |> Expect.isNone "the fixture has no record"
        let! reply = gate "a-connection-after-the-restart" false "reset_fsi_session" sessionId
        aboutAnotherMember reply |> Expect.isFalse (sprintf "nobody else owns it: %A" reply)
      }
    ]
  ]
