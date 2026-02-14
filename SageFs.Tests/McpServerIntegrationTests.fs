module SageFs.Tests.McpServerIntegrationTests

open Expecto
open System
open System.Threading
open SageFs
open SageFs.AppState
open SageFs.Features.Events
open SageFs.McpTools
open SageFs.Tests.TestInfrastructure

// Integration tests for MCP server functionality
// Tests that MCP tools can interact with SageFs actor
// Tests the REAL McpTools module from SageFs.Mcp

[<Tests>]
let tests =
  testSequenced <| testList "[Integration] MCP Server Integration tests" [

    testCase "sendFSharpCode tool executes code"
    <| fun _ ->
      task {
        printfn "Testing sendFSharpCode tool..."
        let ctx = sharedCtxWith "test-session"

        let! result = sendFSharpCode ctx "test-agent" "let x = 42" OutputFormat.Text None

        printfn "Result: %s" result
        Expect.stringContains result "val x" "Should execute successfully"

        Expect.isGreaterThan (SageFs.EventTracking.getEventCount ctx.Store ctx.SessionId) 0 "Should track events"

        printfn "sendFSharpCode tool test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode tracks events for MCP and console to see"
    <| fun _ ->
      task {
        printfn "Testing event tracking..."
        let ctx = sharedCtxWith "collab-session"

        // MCP tool executes code
        let! _ = sendFSharpCode ctx "claude" "let aiValue = 100" OutputFormat.Text None

        // Check events are tracked
        let events = SageFs.EventTracking.getAllEvents ctx.Store ctx.SessionId
        Expect.isGreaterThanOrEqual events.Length 2 "Should have input and output events"

        let (_, src, _) =
          events |> List.find (fun (_, src, _) -> src.ToString().Contains("claude"))

        Expect.equal (src.ToString()) "mcp:claude" "Should show MCP source"

        printfn "Event tracking test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "getRecentEvents tool returns formatted events"
    <| fun _ ->
      task {
        printfn "Testing getRecentEvents tool..."
        let ctx = sharedCtxWith "test-session"

        // Generate some events
        let! _ = sendFSharpCode ctx "agent1" "let a = 1" OutputFormat.Text None
        let! _ = sendFSharpCode ctx "agent2" "let b = 2" OutputFormat.Text None

        // Get recent events
        let! result = getRecentEvents ctx 5

        printfn "Events result: %s" result
        Expect.stringContains result "mcp:agent" "Should show MCP sources"

        printfn "getRecentEvents tool test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "getStatus tool returns session info"
    <| fun _ ->
      task {
        printfn "Testing getStatus tool..."
        let ctx = sharedCtxWith "status-session"

        let! result = getStatus ctx

        printfn "Status: %s" result
        Expect.stringContains result "status-session" "Should show session ID"
        Expect.stringContains result "send_fsharp_code" "Should list available tools"

        printfn "getStatus tool test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "loadFSharpScript tool loads and executes script"
    <| fun _ ->
      task {
        printfn "Testing loadFSharpScript tool..."
        let actor = globalActorResult.Value.Actor
        let ctx = sharedCtxWith "test-session"

        // Create a temp script file
        let tempFile = System.IO.Path.GetTempFileName()
        let fsiFile = System.IO.Path.ChangeExtension(tempFile, ".fsx")
        let scriptContent = "let scriptVar1 = 10;;\nlet scriptVar2 = 20;;"
        System.IO.File.WriteAllText(fsiFile, scriptContent)

        try
          let! result = loadFSharpScript ctx "test-agent" fsiFile None

          printfn "Load result: %s" result
          Expect.stringContains result "Success" "Should load successfully"
          Expect.stringContains result "2 statements" "Should load 2 statements"

          // Verify variables are defined
          let request = {
            Code = "scriptVar1 + scriptVar2"
            Args = Map.empty
          }

          let! checkResult = actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))

          match checkResult.EvaluationResult with
          | Ok res ->
            printfn "Check result: %s" res
            Expect.stringContains res "30" "Should compute sum correctly"
          | Error ex -> failtestf "Failed to use loaded variables: %s" ex.Message

          printfn "loadFSharpScript tool test passed"
        finally
          System.IO.File.Delete(fsiFile)

          if System.IO.File.Exists(tempFile) then
            System.IO.File.Delete(tempFile)
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "Multiple MCP agents can collaborate in same session"
    <| fun _ ->
      task {
        printfn "Testing multi-agent collaboration..."
        let ctx = sharedCtxWith "collab-session"

        // Agent 1 defines something
        let! result1 = sendFSharpCode ctx "agent1" "let sharedData = [1; 2; 3]" OutputFormat.Text None
        Expect.stringContains result1 "val sharedData" "Agent 1 should succeed"

        // Agent 2 uses it
        let! result2 = sendFSharpCode ctx "agent2" "List.sum sharedData" OutputFormat.Text None
        Expect.stringContains result2 "6" "Agent 2 should use Agent 1's data"

        // Check all events are tracked
        let events = SageFs.EventTracking.getAllEvents ctx.Store ctx.SessionId

        let sources =
          events |> List.map (fun (_, src, _) -> src.ToString()) |> List.distinct

        Expect.contains sources "mcp:agent1" "Should have agent1 events"
        Expect.contains sources "mcp:agent2" "Should have agent2 events"

        printfn "Multi-agent collaboration test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "Console and MCP can work together (simulated)"
    <| fun _ ->
      task {
        printfn "Testing console+MCP collaboration..."
        let actor = globalActorResult.Value.Actor
        let ctx = sharedCtxWith "mixed-session"

        // Simulate console user input
        SageFs.EventTracking.trackInput ctx.Store ctx.SessionId Console "let userValue = 42"

        let request1 = {
          Code = "let userValue = 42"
          Args = Map.empty
        }

        let! _ = actor.PostAndAsyncReply(fun reply -> Eval(request1, CancellationToken.None, reply))

        // MCP tool uses console user's value
        let! result = sendFSharpCode ctx "ai-helper" "userValue * 2" OutputFormat.Text None
        Expect.stringContains result "84" "MCP should use console value"

        // Check mixed sources
        let events = SageFs.EventTracking.getAllEvents ctx.Store ctx.SessionId

        let sources =
          events |> List.map (fun (_, src, _) -> src.ToString()) |> List.distinct

        Expect.contains sources "console" "Should have console events"
        Expect.contains sources "mcp:ai-helper" "Should have MCP events"

        printfn "Console+MCP collaboration test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode handles compilation error"
    <| fun _ ->
      task {
        printfn "Testing sendFSharpCode with compilation error..."
        let ctx = sharedCtxWith "error-session"

        let! result = sendFSharpCode ctx "test-agent" "let x = invalid syntax" OutputFormat.Text None

        printfn "Error result: %s" result
        Expect.stringContains result "Error:" "Should return error message"

        // Should still track the failed attempt
        Expect.isGreaterThan (SageFs.EventTracking.getEventCount ctx.Store ctx.SessionId) 0 "Should track error event"

        printfn "Compilation error test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode handles runtime error"
    <| fun _ ->
      task {
        printfn "Testing sendFSharpCode with runtime error..."
        let ctx = sharedCtxWith "runtime-error-session"

        let! result = sendFSharpCode ctx "test-agent" "1 / 0" OutputFormat.Text None

        printfn "Runtime error result: %s" result
        // Division by zero might be caught at compile time or runtime, either way should track it
        Expect.isGreaterThan (SageFs.EventTracking.getEventCount ctx.Store ctx.SessionId) 0 "Should track execution"

        printfn "Runtime error test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "loadFSharpScript with non-existent file returns error"
    <| fun _ ->
      task {
        printfn "Testing loadFSharpScript with non-existent file..."
        let ctx = sharedCtxWith "test-session"

        let! result = loadFSharpScript ctx "test-agent" "C:\\nonexistent\\file.fsx" None

        printfn "Non-existent file result: %s" result
        Expect.stringContains result "Error" "Should return error for non-existent file"

        printfn "Non-existent file test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "loadFSharpScript with partially failing script"
    <| fun _ ->
      task {
        printfn "Testing loadFSharpScript with partial failures..."
        let ctx = sharedCtxWith "test-session"

        // Create script with one good and one bad statement
        let tempFile = System.IO.Path.GetTempFileName()
        let fsiFile = System.IO.Path.ChangeExtension(tempFile, ".fsx")
        let scriptContent = "let goodVar = 42;;\nlet badVar = this is broken;;"
        System.IO.File.WriteAllText(fsiFile, scriptContent)

        try
          let! result = loadFSharpScript ctx "test-agent" fsiFile None

          printfn "Partial failure result: %s" result
          Expect.stringContains result "Partial:" "Should report partial success"
          Expect.stringContains result "1 succeeded" "Should show success count"
          Expect.stringContains result "1 failed" "Should show failure count"

          printfn "Partial failure test passed"
        finally
          System.IO.File.Delete(fsiFile)

          if System.IO.File.Exists(tempFile) then
            System.IO.File.Delete(tempFile)
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode with Json format returns structured JSON"
    <| fun _ ->
      task {
        let ctx = sharedCtxWith "test-session"

        let! result = sendFSharpCode ctx "test-agent" "let jsonTestVal = 42;;" OutputFormat.Json None

        let doc = System.Text.Json.JsonDocument.Parse(result)
        let root = doc.RootElement
        Expect.isTrue (root.GetProperty("success").GetBoolean()) "should report success"
        Expect.stringContains (root.GetProperty("code").GetString()) "jsonTestVal" "should include code"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode with Json format returns error structure on failure"
    <| fun _ ->
      task {
        let ctx = sharedCtxWith "test-session"

        let! result = sendFSharpCode ctx "test-agent" "let x: int = \"not an int\";;" OutputFormat.Json None

        let doc = System.Text.Json.JsonDocument.Parse(result)
        let root = doc.RootElement
        Expect.isFalse (root.GetProperty("success").GetBoolean()) "should report failure"
        Expect.isNonEmpty (root.GetProperty("error").GetString()) "should have error message"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode with Json format returns array for multiple statements"
    <| fun _ ->
      task {
        let ctx = sharedCtxWith "test-session"

        let! result = sendFSharpCode ctx "test-agent" "let a1 = 1;;\nlet b1 = 2;;" OutputFormat.Json None

        let doc = System.Text.Json.JsonDocument.Parse(result)
        let root = doc.RootElement
        Expect.equal root.ValueKind System.Text.Json.JsonValueKind.Array "should be a JSON array"
        Expect.equal (root.GetArrayLength()) 2 "should have 2 results"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]
