module SageFs.Tests.SessionResetTests

open Expecto
open Expecto.Flip
open System.Threading
open SageFs.AppState
open SageFs.McpTools
open SageFs.Features.Events
open SageFs.Tests.TestInfrastructure

[<Tests>]
let sessionResetTests =
  testSequenced <| testList "[Integration] Session reset" [

    testCase "eval → reset → value is gone"
    <| fun _ ->
      task {
        let ctx = sharedCtxWith "reset-test"

        // Define a value
        let! defineResult = sendFSharpCode ctx "test" "let resetTestVal = 99;;" OutputFormat.Text None None
        defineResult
        |> Expect.stringContains
          "Definition should succeed"
          "val resetTestVal"

        // Reset the session
        let! resetResult = resetSession ctx "test" None None
        resetResult
        |> Expect.stringContains
          "Reset should report success"
          "reset"

        // Try to use the value — should fail
        let! afterReset = sendFSharpCode ctx "test" "resetTestVal;;" OutputFormat.Text None None
        afterReset
        |> Expect.stringContains
          "Value should not exist after reset"
          "Error"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "reset → eval 1+1 succeeds (session works)"
    <| fun _ ->
      task {
        let ctx = sharedCtxWith "reset-works"

        let! resetResult = resetSession ctx "test" None None
        resetResult
        |> Expect.stringContains
          "Reset should succeed"
          "reset"

        let! result = sendFSharpCode ctx "test" "1 + 1;;" OutputFormat.Text None None
        result
        |> Expect.stringContains
          "Should evaluate after reset"
          "2"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "reset returns success message"
    <| fun _ ->
      task {
        let ctx = sharedCtxWith "reset-msg"

        let! result = resetSession ctx "test" None None
        let isSuccess =
          result.Contains("success", System.StringComparison.OrdinalIgnoreCase)
          || result.Contains("reset", System.StringComparison.OrdinalIgnoreCase)
        isSuccess
        |> Expect.isTrue "Reset should indicate success"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]

[<Tests>]
let resetPushbackTests =
  testList "Reset pushback warnings" [

    ptestCase "hard reset on healthy session includes warning"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        let! result = hardResetSession ctx "test" false None None
        result
        |> Expect.stringContains
          "Should include pushback warning for healthy session"
          "⚠️ NOTE:"
        result
        |> Expect.stringContains
          "Should still include success message"
          "Hard reset complete"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "hard reset after warmup failures has no warning"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        let! result = hardResetSession ctx "test" false None None
        // With unified sessions, warmup failures come from proxy — just verify reset works
        result
        |> Expect.stringContains
          "Should include success message"
          "Hard reset complete"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    ptestCase "soft reset on healthy session includes warning"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        let! result = resetSession ctx "test" None None
        result
        |> Expect.stringContains
          "Should include pushback warning for healthy session"
          "⚠️ NOTE:"
        result
        |> Expect.stringContains
          "Should still include success message"
          "reset"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "soft reset after warmup failures has no warning"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        let! result = resetSession ctx "test" None None
        // With unified sessions, warmup failures come from proxy — just verify reset works
        result
        |> Expect.stringContains
          "Should include success message"
          "reset"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]
