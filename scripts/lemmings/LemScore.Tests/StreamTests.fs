module LemScore.Tests.StreamTests

open Expecto
open Expecto.Flip
open LemScore.CmdcStream
open LemScore.Classify
open LemScore.Tests.Samples

[<Tests>]
let streamTests =
  testList "CmdcStream on real captures" [
    testCase "a plain answer: one turn, no tools, a success result" <| fun _ ->
      let s = sampleStream "plain-answer.ndjson"
      s.Turns |> Expect.equal "one turn" 1
      s.Calls |> Expect.isEmpty "no tool calls"
      s.EndStopReason |> Expect.equal "stop reason" (Some "end_turn")
      match s.Result with
      | Some r ->
        r.Subtype |> Expect.equal "subtype" "success"
        r.FinalText |> Expect.equal "the model said OK" "OK"
      | None -> failtest "no result line"
      s.Unreadable |> Expect.isEmpty "every line parsed"

    testCase "a SageFs refusal and a shell call are both read, in order, with their turns" <| fun _ ->
      let s = sampleStream "sagefs-error-and-shell.ndjson"
      s.Calls |> List.map _.Name |> Expect.equal "order" [ "mcp__sagefs__stop_session"; "shell_command" ]
      s.Calls |> List.map _.Turn |> Expect.equal "turns" [ 1; 2 ]
      s.Unreadable |> Expect.isEmpty "every line parsed"

    testCase "SageFs's 'Failed ... Next:' refusal counts as a SageFs error, a plain ls does not" <| fun _ ->
      let s = sampleStream "sagefs-error-and-shell.ndjson"
      s.Calls |> List.map isSagefsError |> Expect.equal "errors" [ true; false ]

    testCase "the shell command is read off the tool input" <| fun _ ->
      let s = sampleStream "sagefs-error-and-shell.ndjson"
      s.Calls |> List.choose shellCommand |> Expect.equal "command" [ "ls -la" ]
  ]

[<Tests>]
let lineTests =
  testList "CmdcStream line parsing" [
    testCase "a line that is not JSON is counted, not dropped" <| fun _ ->
      match parseLine "this is not json" with
      | Unreadable why -> why |> Expect.stringContains "says why" "not JSON"
      | other -> failtestf "expected Unreadable, got %A" other

    testCase "an unknown event type is noise, not an error" <| fun _ ->
      parseLine """{"type":"event","event":{"type":"thinking_delta","delta":"hm"}}"""
      |> Expect.equal "noise" Noise

    testCase "a blank line is noise" <| fun _ ->
      parseLine "   " |> Expect.equal "noise" Noise

    testCase "tool_errored carries its error text" <| fun _ ->
      match parseLine (toolErrored "c1" "mcp__sagefs__send_fsharp_code" "boom") with
      | ToolErroredEvent (_, name, text) ->
        name |> Expect.equal "name" "mcp__sagefs__send_fsharp_code"
        text |> Expect.equal "text" "boom"
      | other -> failtestf "got %A" other

    testCase "a run_error with a message is read" <| fun _ ->
      match parseLine (eventLine """{"type":"run_error","error":{"message":"You've reached today's limit on X."}}""") with
      | RunErrorEvent text -> text |> Expect.stringContains "message" "today's limit"
      | other -> failtestf "got %A" other

    testCase "the result line's error text is kept" <| fun _ ->
      match parseLine (resultLine "error" (Some "Rate limit exceeded") "") with
      | Result r ->
        r.Subtype |> Expect.equal "subtype" "error"
        r.Error |> Expect.equal "error" (Some "Rate limit exceeded")
      | other -> failtestf "got %A" other

    testCase "a call that never finished is marked so" <| fun _ ->
      let s = stream [ [ toolQueued "c1" "mcp__sagefs__list_sessions" "{}" ] ] []
      s.Calls |> List.map _.Outcome |> Expect.equal "outcome" [ ToolNeverFinished ]
  ]
