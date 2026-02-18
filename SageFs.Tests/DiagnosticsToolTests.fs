module SageFs.Tests.DiagnosticsToolTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.Diagnostics
open SageFs.Features
open SageFs.AppState
open SageFs.McpTools
open SageFs.Tests.TestInfrastructure

[<Tests>]
let formatDiagnosticsTests =
  testList "Diagnostics tool formatting" [

    testCase "formatDiagnosticsResult with no diagnostics returns clean message"
    <| fun _ ->
      McpAdapter.formatDiagnosticsResult [||]
      |> Expect.stringContains "should indicate no issues" "No issues found"

    testCase "formatDiagnosticsResult with errors includes severity and message"
    <| fun _ ->
      let diags = [|
        {
          Message = "The value 'x' is not defined"
          Subcategory = "typecheck"
          Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
          Severity = DiagnosticSeverity.Error
        }
      |]
      let result = McpAdapter.formatDiagnosticsResult diags
      result
      |> Expect.stringContains "should contain error severity" "[error]"
      result
      |> Expect.stringContains "should contain the message" "The value 'x' is not defined"

    testCase "formatDiagnosticsResult with warnings includes severity"
    <| fun _ ->
      let diags = [|
        {
          Message = "This expression should have type 'unit'"
          Subcategory = "typecheck"
          Range = { StartLine = 3; StartColumn = 0; EndLine = 3; EndColumn = 10 }
          Severity = DiagnosticSeverity.Warning
        }
      |]
      McpAdapter.formatDiagnosticsResult diags
      |> Expect.stringContains "should contain warning severity" "[warning]"

    testCase "formatDiagnosticsResult includes line numbers"
    <| fun _ ->
      let diags = [|
        {
          Message = "Incomplete value"
          Subcategory = "parse"
          Range = { StartLine = 5; StartColumn = 3; EndLine = 5; EndColumn = 8 }
          Severity = DiagnosticSeverity.Error
        }
      |]
      McpAdapter.formatDiagnosticsResult diags
      |> Expect.stringContains "should include line and column" "(5,3)"

    testCase "formatDiagnosticsResult with multiple diagnostics includes all"
    <| fun _ ->
      let diags = [|
        {
          Message = "first error"
          Subcategory = "typecheck"
          Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
          Severity = DiagnosticSeverity.Error
        }
        {
          Message = "second warning"
          Subcategory = "typecheck"
          Range = { StartLine = 2; StartColumn = 0; EndLine = 2; EndColumn = 5 }
          Severity = DiagnosticSeverity.Warning
        }
      |]
      let result = McpAdapter.formatDiagnosticsResult diags
      result
      |> Expect.stringContains "should contain first diagnostic" "first error"
      result
      |> Expect.stringContains "should contain second diagnostic" "second warning"
  ]

[<Tests>]
let checkFSharpCodeTests =
  testList "[Integration] checkFSharpCode backing function" [

    testCase "checkFSharpCode with valid code returns no issues"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        let! result = checkFSharpCode ctx "test" "let x = 42" None
        result
        |> Expect.stringContains "valid code should have no issues" "No issues found"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "checkFSharpCode with type error returns diagnostic"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        let! result = checkFSharpCode ctx "test" "let x: int = \"hello\"" None
        result
        |> Expect.stringContains "should report error" "[error]"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "checkFSharpCode with undefined value returns diagnostic"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        let! result = checkFSharpCode ctx "test" "let y = undefinedValue + 1" None
        result
        |> Expect.stringContains "should report undefined" "[error]"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "checkFSharpCode does not execute code (no side effects)"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        // Check code that would throw at runtime — diagnostics should still work
        let! result = checkFSharpCode ctx "test" "let z = 1 / 0" None
        // Division by zero is a runtime error, not a compile error
        result
        |> Expect.stringContains "should pass compile check" "No issues found"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]

[<Tests>]
let accumulatedDiagnosticsTests =
  testList "[Integration] accumulated diagnostics in AppState" [

    testCase "AppState has Diagnostics field of type DiagnosticsStore"
    <| fun _ ->
      task {
        let result = globalActorResult.Value
        let! st = result.Actor.PostAndAsyncReply(GetAppState)
        // Just verify the field exists and is a valid store (may have accumulated entries from other tests)
        st.Diagnostics
        |> DiagnosticsStore.all
        |> ignore
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "GetDiagnostics with errors accumulates new diagnostics in state"
    <| fun _ ->
      task {
        let result = globalActorResult.Value
        let! stBefore = result.Actor.PostAndAsyncReply(GetAppState)
        let countBefore = stBefore.Diagnostics |> DiagnosticsStore.allFlat |> List.length
        let uniqueCode = sprintf "let accumTest_%d: int = \"oops\"" (System.Random.Shared.Next())
        let! _diags = result.Actor.PostAndAsyncReply(fun rc -> GetDiagnostics(uniqueCode, rc))
        let! stAfter = result.Actor.PostAndAsyncReply(GetAppState)
        let countAfter = stAfter.Diagnostics |> DiagnosticsStore.allFlat |> List.length
        (countAfter > countBefore)
        |> Expect.isTrue "diagnostics count should increase after GetDiagnostics with errors"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "DiagnosticsChanged event fires when diagnostics are updated"
    <| fun _ ->
      task {
        let result = globalActorResult.Value
        let mutable received = None
        use _sub = result.DiagnosticsChanged.Subscribe(fun store -> received <- Some store)
        let uniqueCode = sprintf "let eventTest_%d: int = \"wrong\"" (System.Random.Shared.Next())
        let! _diags = result.Actor.PostAndAsyncReply(fun rc -> GetDiagnostics(uniqueCode, rc))
        // Give the event a moment to fire (it's synchronous in the actor loop, but subscription is async)
        do! System.Threading.Tasks.Task.Delay(100)
        received
        |> Option.isSome
        |> Expect.isTrue "DiagnosticsChanged event should have fired"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]

[<Tests>]
let diagnosticsSseFormatTests =
  testList "Diagnostics SSE formatting" [

    testCase "formatDiagnosticsForSse with empty store returns empty array JSON"
    <| fun _ ->
      McpAdapter.formatDiagnosticsStoreAsJson DiagnosticsStore.empty
      |> Expect.stringContains "should contain empty array" "[]"

    testCase "formatDiagnosticsForSse with entries includes code hash and diagnostics"
    <| fun _ ->
      let store =
        DiagnosticsStore.empty
        |> DiagnosticsStore.add "let x = 1" [|
          { Message = "test warning"; Subcategory = "typecheck"
            Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
            Severity = DiagnosticSeverity.Warning }
        |]
      let result = McpAdapter.formatDiagnosticsStoreAsJson store
      result |> Expect.stringContains "should contain message" "test warning"
      result |> Expect.stringContains "should contain severity" "warning"
  ]
