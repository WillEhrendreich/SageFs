/// An MCP tool's explicit `sessionId` argument used to reach `toSessionId`
/// unvalidated, so anything that was not an 8-hex id became
/// `failwithf "Invalid resolved session ID ..."`, which the MCP layer can only
/// render as "tool call threw". The MCP server log shows the shape that
/// produced it: a refusal message copied back into the argument
/// ("Invalid resolved session ID 'A session with this exact target and working
/// directory already exists ...'"). These pin that the argument is classified
/// before it is used, and that the refusal names the value and the shape.
module SageFs.Tests.SessionIdArgumentTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.McpSessionRouting

/// The exact value from the MCP server log, so the regression has a name.
let private pastedRefusal =
  "A session with this exact target and working directory already exists (session '801ed663', status: Starting). Use switch_session to target it instead."

[<Tests>]
let tests =
  testList "an explicit sessionId argument" [

    testCase "WHY — a refusal message pasted into sessionId is classified, not thrown" <| fun _ ->
      match classifyExplicitSessionId pastedRefusal with
      | Ok id -> failtestf "a refusal message must not be accepted as a session id: %A" id
      | Error (InvalidSessionId (given, _)) ->
        given |> Expect.equal "the refusal quotes the argument as it arrived" pastedRefusal
      | Error other -> failtestf "expected InvalidSessionId, got %A" other

    testCase "a well-formed id is accepted and comes back parsed" <| fun _ ->
      match classifyExplicitSessionId "03b4e1a2" with
      | Ok id -> WorkerProtocol.SessionId.value id |> Expect.equal "the id round-trips" "03b4e1a2"
      | Error refusal -> failtestf "a valid id must be accepted, got %A" refusal

    testCase "WHY — the refusal names the value, the shape, and where to get a real one" <| fun _ ->
      let text =
        match classifyExplicitSessionId pastedRefusal with
        | Ok _ -> failtest "expected a refusal"
        | Error refusal -> formatSessionResolution refusal
      text |> Expect.stringContains "the value that was sent is quoted" "801ed663"
      text |> Expect.stringContains "the accepted shape is stated" "8 lowercase hex"
      text |> Expect.stringContains "the way to get one is named" "list_sessions"

    testCase "WHY — routed turns the refusal into an error result, never an exception" <| fun _ ->
      match classifyExplicitSessionId pastedRefusal with
      | Ok _ -> failtest "expected a refusal"
      | Error refusal ->
        match routed refusal with
        | Ok sid -> failtestf "a malformed id must not route, got '%s'" sid
        | Error (SageFsError.SessionNotRoutable reason) ->
          reason
          |> Expect.stringContains "the caller gets the guidance, not a thrown exception" "8 lowercase hex"
        | Error other -> failtestf "expected SessionNotRoutable, got %A" other

    testCase "an empty argument is refused with the validator's own reason" <| fun _ ->
      match classifyExplicitSessionId "" with
      | Ok _ -> failtest "the empty string is not a session id"
      | Error (InvalidSessionId (_, reason)) ->
        reason |> Expect.stringContains "the validator's own words are kept" "empty"
      | Error other -> failtestf "expected InvalidSessionId, got %A" other

    testProperty "PROPERTY: every string outside the 8-lowercase-hex shape is refused, never accepted" <|
      fun (s: string) ->
        let given = if isNull s then "" else s
        let isShape =
          given.Length = 8
          && given |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
        match classifyExplicitSessionId given with
        | Ok _ -> isShape
        | Error _ -> not isShape
  ]
