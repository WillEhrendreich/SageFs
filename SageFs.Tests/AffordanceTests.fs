module SageFs.Tests.AffordanceTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Affordances

[<Tests>]
let affordanceTests =
  testList "Affordances" [

    testList "availableTools" [

      testCase "Uninitialized state offers only get_fsi_status"
      <| fun _ ->
        availableTools Uninitialized
        |> Expect.equal "only status" [ "get_fsi_status" ]

      testCase "WarmingUp state offers get_fsi_status and get_recent_fsi_events"
      <| fun _ ->
        availableTools WarmingUp
        |> Expect.containsAll "status and events"
          [ "get_fsi_status"; "get_recent_fsi_events" ]

      testCase "WarmingUp state does not offer send_fsharp_code"
      <| fun _ ->
        availableTools WarmingUp
        |> List.contains "send_fsharp_code"
        |> Expect.isFalse "should not offer eval during warmup"

      testCase "Ready state offers send_fsharp_code"
      <| fun _ ->
        availableTools Ready
        |> List.contains "send_fsharp_code"
        |> Expect.isTrue "should offer eval when ready"

      testCase "Ready state offers all read tools"
      <| fun _ ->
        availableTools Ready
        |> Expect.containsAll "all read tools"
          [ "get_fsi_status"; "get_recent_fsi_events"; "check_fsharp_code" ]

      testCase "Ready state offers reset and hard reset"
      <| fun _ ->
        availableTools Ready
        |> Expect.containsAll "reset tools"
          [ "reset_fsi_session"; "hard_reset_fsi_session" ]

      testCase "Evaluating state offers cancel_eval but not send_fsharp_code"
      <| fun _ ->
        let tools = availableTools Evaluating
        tools
        |> List.contains "cancel_eval"
        |> Expect.isTrue "should offer cancel during eval"
        tools
        |> List.contains "send_fsharp_code"
        |> Expect.isFalse "should not offer eval during eval"

      testCase "Evaluating state offers read-only tools"
      <| fun _ ->
        availableTools Evaluating
        |> Expect.containsAll "read tools during eval"
          [ "get_fsi_status"; "get_recent_fsi_events"; "check_fsharp_code" ]

      testCase "Faulted state offers reset tools"
      <| fun _ ->
        let tools = availableTools Faulted
        tools
        |> Expect.containsAll "reset tools"
          [ "reset_fsi_session"; "hard_reset_fsi_session"; "get_fsi_status" ]

      testCase "Faulted state does not offer send_fsharp_code"
      <| fun _ ->
        availableTools Faulted
        |> List.contains "send_fsharp_code"
        |> Expect.isFalse "should not offer eval when faulted"
    ]

    testList "checkToolAvailability" [

      testCase "returns Ok for available tool"
      <| fun _ ->
        checkToolAvailability Ready "send_fsharp_code"
        |> Expect.isOk "should allow eval when ready"

      testCase "returns Error with alternatives for unavailable tool"
      <| fun _ ->
        let result = checkToolAvailability Evaluating "send_fsharp_code"
        match result with
        | Error (SageFsError.ToolNotAvailable(toolName, state, available)) ->
          state |> Expect.equal "state" Evaluating
          toolName |> Expect.equal "tool" "send_fsharp_code"
          available |> Expect.contains "should have cancel" "cancel_eval"
        | Error other ->
          failtestf "unexpected error: %A" other
        | Ok _ ->
          failtest "should have returned Error"

      testCase "returns Error for eval on Faulted"
      <| fun _ ->
        let result = checkToolAvailability Faulted "send_fsharp_code"
        match result with
        | Error (SageFsError.ToolNotAvailable(_, state, available)) ->
          state |> Expect.equal "state" Faulted
          available |> Expect.contains "should have reset" "reset_fsi_session"
        | Error other ->
          failtestf "unexpected error: %A" other
        | Ok _ ->
          failtest "should have returned Error"
    ]
  ]
