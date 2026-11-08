/// `sagefs data clear` and the route behind it. `POST /api/local-data/clear` used to be open to every caller, and the
/// refusal a confirmation prompt gave named that route as the way round the prompt. The route is the operator's now
/// (`EndpointContracts`), the person reaches it with `sagefs data clear`, and no refusal text sends an agent to it.
module SageFs.Tests.LocalDataCommandTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.EdgeCredential
open SageFs.LocalDataCommand
open SageFs.Server.McpServer

[<Tests>]
let tests =
  testList "sagefs data clear" [

    testList "the words after 'data'" [
      testCase "WHY - clear alone clears every store, the same default the route and manage_local_data have" <| fun _ ->
        match parseArgs [ "clear" ] with
        | Ok ask -> ask.Store |> Expect.equal "all of them" LocalData.ClearTarget.All
        | Error refusal -> failtestf "refused: %A" refusal

      testCase "WHY - --store names one store, and the route's own parser decides what a store is" <| fun _ ->
        match parseArgs [ "clear"; "--store"; "friction" ] with
        | Ok ask ->
          ask.Store |> Expect.equal "friction" LocalData.ClearTarget.Friction
          ask.StoreText |> Expect.equal "the text goes to the route as typed" "friction"
        | Error refusal -> failtestf "refused: %A" refusal
        match parseArgs [ "clear"; "--store"; "cohort" ] with
        | Ok ask -> ask.Store |> Expect.equal "cohort" LocalData.ClearTarget.CohortLedger
        | Error refusal -> failtestf "refused: %A" refusal

      testCase "WHY - every mistake is a named refusal that says what to type" <| fun _ ->
        [ [], UsageRefusal.NoVerb
          [ "wipe" ], UsageRefusal.NotClear "wipe"
          [ "clear"; "--store" ], UsageRefusal.FlagNeedsValue "--store"
          [ "clear"; "--force" ], UsageRefusal.UnknownFlag "--force" ]
        |> List.iter (fun (words, expected) ->
          match parseArgs words with
          | Ok ask -> failtestf "%A should be refused, and gave %A" words ask
          | Error refusal ->
            refusal |> Expect.equal (sprintf "%A" words) expected
            UsageRefusal.describe refusal |> Expect.isNotEmpty "and it says something")
        match parseArgs [ "clear"; "--store"; "everything" ] with
        | Ok ask -> failtestf "an unknown store should be refused, and gave %A" ask
        | Error(UsageRefusal.NotAStore reason) -> reason |> Expect.stringContains "naming the stores" "friction, cohort or all"
        | Error other -> failtestf "expected NotAStore, got %A" other
    ]

    testList "what the daemon answers" [
      testCase "WHY - a cleared store reads as the line the route wrote for it" <| fun _ ->
        let body = """{"success":true,"results":[{"store":"friction.db","outcome":"friction.db: cleared 3 rows"}]}"""
        match parseReply 200 body with
        | Ok reply -> render reply |> Expect.equal "the outcome line" "friction.db: cleared 3 rows"
        | Error failure -> failtestf "unreadable: %A" failure

      testCase "WHY - the operator refusal is carried with its own message and next action" <| fun _ ->
        let body = Json.serialize Json.camelCase (McpMintRoute.refusalBody EdgeRefusal.NoCredential)
        match parseReply 401 body with
        | Error(MemberMintCommand.MintFailure.Refused(status, message, next)) ->
          status |> Expect.equal "status" 401
          message |> Expect.stringContains "says why" "operator credential"
          next |> Expect.isNotEmpty "and what to do"
        | other -> failtestf "expected Refused, got %A" other

      testCase "WHY - a 400 for an unknown store keeps the route's sentence" <| fun _ ->
        match parseReply 400 """{"success":false,"error":"Unknown store 'x'. Use friction, cohort or all."}""" with
        | Error(MemberMintCommand.MintFailure.Refused(400, message, _)) -> message |> Expect.stringContains "the route's words" "Use friction, cohort or all"
        | other -> failtestf "expected Refused 400, got %A" other
    ]

    testList "the route and the contract" [
      testCase "WHY - the clear route is declared operator-only, so the open-route count drops and the contract says who may call" <| fun _ ->
        EndpointContracts.all
        |> List.filter (fun endpoint -> endpoint.path = Path)
        |> List.map (fun endpoint -> endpoint.access)
        |> Expect.equal "the one route, for the operator" [ EdgeAccess.OperatorOnly ]

      testCase "WHY - a refusal for a person who cannot answer never names a route that skips the question, because the route now needs the operator" <| fun _ ->
        let text = ConfirmationRefusal.wayOut "manage_local_data"
        text |> Expect.stringContains "it names the command a person runs" "sagefs data clear"
        text.Contains Path |> Expect.isFalse "and does not hand an agent the route"
    ]
  ]
