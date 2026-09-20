/// Pure logic for the dashboard's workflow switcher (`DashboardTypes.WorkflowSwitch`
/// — sagefs-ux-roast.md §4.1/§4.2/§11 Island B item 4: the read-only workflow
/// badge upgraded to a real control). Covers the picker's option list, the
/// wire value each option posts, and the parser for `POST
/// /api/sessions/{sid}/workflow`'s response body — which has TWO distinct
/// shapes on failure (`{success:false; error}` and a bare `SageFsError.toJson`
/// object with no `success` key at all), both of which must parse.
module SageFs.Tests.DashboardWorkflowSwitchTests

open Expecto
open Expecto.Flip
open SageFs.WorkflowTypes
open SageFs.Server.DashboardTypes

[<Tests>]
let optionsTests =
  testList "WorkflowSwitch.options" [
    testCase "WHY — the picker offers exactly the three workflows the brief names, in a stable order" <| fun _ ->
      WorkflowSwitch.options
      |> List.map SessionWorkflow.label
      |> Expect.equal "Interactive, LiveTesting, HotReload — in that order" [ "REPL"; "Live Testing"; "Hot Reload" ]
  ]

[<Tests>]
let requestValueTests =
  testList "WorkflowSwitch.requestValue" [
    testCase "WHY — every option's wire value round-trips through the SAME parser the real endpoint uses (SessionWorkflow.tryOfString), so the dashboard can never send a value the server rejects" <| fun _ ->
      for w in WorkflowSwitch.options do
        let value = WorkflowSwitch.requestValue w
        match SessionWorkflow.tryOfString value with
        | None -> failtestf "requestValue %s ('%s') does not round-trip through tryOfString" (SessionWorkflow.label w) value
        | Some parsed ->
          parsed
          |> SessionWorkflow.label
          |> Expect.equal (sprintf "'%s' should round-trip to the same label" value) (SessionWorkflow.label w)

    testCase "WHY — a deliberately-wrong wire value would be caught by the round-trip check above (mutation-proofing the table)" <| fun _ ->
      // A broken requestValue that always returned "interactive" would still
      // "round trip" for the Interactive case but would fail the LiveTesting
      // and HotReload round-trips above — this just documents the values are
      // pairwise distinct, so no case can silently alias another.
      WorkflowSwitch.options
      |> List.map WorkflowSwitch.requestValue
      |> List.distinct
      |> List.length
      |> Expect.equal "three distinct wire values for three distinct workflows" 3
  ]

[<Tests>]
let parseResponseTests =
  testList "WorkflowSwitch.parseResponse" [
    testCase "WHY — a successful switch reads the server's own message, not an invented one" <| fun _ ->
      let body = """{"success":true,"message":"Switching to Hot Reload","sessionId":"0a0b0c0d","workflow":"Hot Reload"}"""
      WorkflowSwitch.parseResponse 200 body
      |> Expect.equal "Ok with the server's message" (Ok "Switching to Hot Reload")

    testCase "WHY — a successful switch with no message field falls back to a message naming the workflow, never a blank string" <| fun _ ->
      let body = """{"success":true,"workflow":"Hot Reload"}"""
      WorkflowSwitch.parseResponse 200 body
      |> Expect.equal "Ok, synthesized from the workflow field" (Ok "Switched to Hot Reload")

    testCase "WHY — a 400 unrecognized-workflow body ({success:false; error}) reads its error text" <| fun _ ->
      let body = """{"success":false,"error":"Unknown workflow 'hotreoad'"}"""
      WorkflowSwitch.parseResponse 400 body
      |> Expect.equal "Error with the server's error text" (Error "Unknown workflow 'hotreoad'")

    testCase "WHY — a 404 SessionNotFound body (bare SageFsError.toJson — case/fields/message/suggestedAction, NO success key) still parses to an Error, not a false success" <| fun _ ->
      let body = """{"case":"SessionNotFound","fields":{"sessionId":"deadbeef"},"message":"Session deadbeef not found","suggestedAction":"Check the session id"}"""
      WorkflowSwitch.parseResponse 404 body
      |> Expect.equal "Error with the SageFsError message" (Error "Session deadbeef not found")

    testCase "WHY — an unreadable body (network garbage, HTML error page, empty string) degrades to a generic message naming the HTTP status rather than throwing" <| fun _ ->
      match WorkflowSwitch.parseResponse 502 "<html>Bad Gateway</html>" with
      | Error msg -> msg |> Expect.stringContains "the generic message names the HTTP status" "502"
      | Ok _ -> failtest "an unparseable body must never read as success"

    testCase "WHY — an empty body degrades to a generic message rather than throwing" <| fun _ ->
      match WorkflowSwitch.parseResponse 500 "" with
      | Error _ -> ()
      | Ok _ -> failtest "an empty body must never read as success"
  ]
