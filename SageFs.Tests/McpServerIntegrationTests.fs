module SageFs.Tests.McpServerIntegrationTests

open Expecto
open System
open System.Threading
open SageFs
open SageFs.AppState
open SageFs.Features.Events
open SageFs.McpTools
open SageFs.Tests.TestInfrastructure
open SageFs.WorkerProtocol

// Integration tests for MCP server functionality
// Tests that MCP tools can interact with SageFs actor
// Tests the REAL McpTools module from SageFs.Mcp

/// sharedCtxWith routes only the "test" agent to its session (MCP resolves the
/// active session per agent through SessionMap). These tests speak as several
/// named agents collaborating in ONE session, so every agent they use is
/// routed to the same session — otherwise each call correctly answers
/// "No active session".
let private agentCtx () =
  let sessionId = SessionId.newId ()
  let ctx = sharedCtxWith sessionId
  for agent in [ "test-agent"; "claude"; "agent1"; "agent2"; "ai-helper" ] do
    ctx.SessionMap.[agent] <- SessionId.value sessionId
  ctx

[<Tests>]
let tests =
  testSequenced <| Integration.hostList "MCP Server Integration tests" [

    testCase "sendFSharpCode tool executes code"
    <| fun _ ->
      task {
        printfn "Testing sendFSharpCode tool..."
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "let x = 42"OutputFormat.Text None None None None None None

        printfn "Result: %s" result
        Expect.stringContains result "val x" "Should execute successfully"

        printfn "sendFSharpCode tool test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode tool does not require event tracking"
    <| fun _ ->
      task {
        printfn "Testing sendFSharpCode without event tracking..."
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "claude" "let aiValue = 100" OutputFormat.Text None None None None None None
        Expect.stringContains result "val aiValue" "Should still execute successfully"

        printfn "Non-event-tracking test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "getRecentEvents tool returns formatted events"
    <| fun _ ->
      task {
        printfn "Testing getRecentEvents tool..."
        let ctx = agentCtx ()

        let! _ = sendFSharpCode ctx "agent1" "let a = 1" OutputFormat.Text None None None None None None
        let! _ = sendFSharpCode ctx "agent2" "let b = 2" OutputFormat.Text None None None None None None

        let! result = getRecentEvents ctx "test" 5 None

        printfn "Events result: %s" result
        Expect.equal result "Recent events: none recorded" "Should return stub response when event tracking is removed"

        printfn "getRecentEvents tool test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "getStatus tool returns session info"
    <| fun _ ->
      task {
        printfn "Testing getStatus tool..."
        let ctx = agentCtx ()

        let! result = getStatus ctx "test" None None

        printfn "Status: %s" result
        Expect.stringContains result (sprintf "Session: %s" ctx.SessionMap.["test"]) "Should show session ID"
        Expect.stringContains result "send_fsharp_code" "Should list available tools"
        Expect.stringContains result "Events: 0" "Should report zero tracked events"

        printfn "getStatus tool test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "loadFSharpScript tool loads and executes script"
    <| fun _ ->
      task {
        printfn "Testing loadFSharpScript tool..."
        let actor = globalActorResult.Value.Actor
        let ctx = agentCtx ()

        // Create a temp script file
        let tempFile = System.IO.Path.GetTempFileName()
        let fsiFile = System.IO.Path.ChangeExtension(tempFile, ".fsx")
        let scriptContent = "let scriptVar1 = 10;;\nlet scriptVar2 = 20;;"
        System.IO.File.WriteAllText(fsiFile, scriptContent)

        try
          let! result = loadFSharpScript ctx "test-agent" fsiFile None None

          printfn "Load result: %s" result
          // The worker #loads the script as one unit and returns FSI's own
          // load output (the per-statement "Success: N statements" summary went
          // away with the worker-only session architecture).
          Expect.stringContains result "val scriptVar1" "Should define scriptVar1"
          Expect.stringContains result "val scriptVar2" "Should define scriptVar2"

          // Verify the variables are usable. #load binds them in the script's
          // own module (FSI names it after the file — the "module FSI_NNNN.X"
          // line above), so qualify through the module FSI reported.
          let loadedModule =
            result.Split('\n')
            |> Array.tryPick (fun line ->
              let line = line.Trim()
              match line.StartsWith "module " with
              | true -> Some (line.Substring("module ".Length).Split('.') |> Array.last)
              | false -> None)
            |> Option.defaultWith (fun () -> failtestf "load output should name the loaded module: %s" result)
          let request = {
            Code = sprintf "%s.scriptVar1 + %s.scriptVar2" loadedModule loadedModule
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
        let ctx = agentCtx ()

        // Agent 1 defines something
        let! result1 = sendFSharpCode ctx "agent1" "let sharedData = [1; 2; 3]" OutputFormat.Text None None None None None None
        Expect.stringContains result1 "val sharedData" "Agent 1 should succeed"

        // Agent 2 uses it
        let! result2 = sendFSharpCode ctx "agent2" "List.sum sharedData" OutputFormat.Text None None None None None None
        Expect.stringContains result2 "6" "Agent 2 should use Agent 1's data"

        printfn "Multi-agent collaboration test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "Console and MCP can work together (simulated)"
    <| fun _ ->
      task {
        printfn "Testing console+MCP collaboration..."
        let actor = globalActorResult.Value.Actor
        let ctx = agentCtx ()

        let request1 = {
          Code = "let userValue = 42"
          Args = Map.empty
        }

        let! _ = actor.PostAndAsyncReply(fun reply -> Eval(request1, CancellationToken.None, reply))

        // MCP tool uses console user's value
        let! result = sendFSharpCode ctx "ai-helper" "userValue * 2" OutputFormat.Text None None None None None None
        Expect.stringContains result "84" "MCP should use console value"

        printfn "Console+MCP collaboration test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode handles compilation error"
    <| fun _ ->
      task {
        printfn "Testing sendFSharpCode with compilation error..."
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "let x = invalid syntax" OutputFormat.Text None None None None None None

        printfn "Error result: %s" result
        Expect.stringContains result "Error:" "Should return error message"

        printfn "Compilation error test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode handles runtime error"
    <| fun _ ->
      task {
        printfn "Testing sendFSharpCode with runtime error..."
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "1 / 0" OutputFormat.Text None None None None None None

        printfn "Runtime error result: %s" result
        Expect.isNotEmpty result "Should return some result or error output"

        printfn "Runtime error test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "loadFSharpScript with non-existent file returns error"
    <| fun _ ->
      task {
        printfn "Testing loadFSharpScript with non-existent file..."
        let ctx = agentCtx ()

        let! result = loadFSharpScript ctx "test-agent" "C:\\nonexistent\\file.fsx"None None

        printfn "Non-existent file result: %s" result
        Expect.stringContains result "Error" "Should return error for non-existent file"

        printfn "Non-existent file test passed"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    // A script is #loaded as ONE compilation unit, so a broken statement fails
    // the whole load (FSI semantics) — reported as an Error with the reason,
    // not as "Partial: 1 succeeded, 1 failed" (that per-statement contract was
    // removed with the worker-only session architecture).
    testCase "loadFSharpScript with a failing statement reports the load error"
    <| fun _ ->
      task {
        printfn "Testing loadFSharpScript with a failing statement..."
        let ctx = agentCtx ()

        // Create script with one good and one bad statement
        let tempFile = System.IO.Path.GetTempFileName()
        let fsiFile = System.IO.Path.ChangeExtension(tempFile, ".fsx")
        let scriptContent = "let goodVar = 42;;\nlet badVar = this is broken;;"
        System.IO.File.WriteAllText(fsiFile, scriptContent)

        try
          let! result = loadFSharpScript ctx "test-agent" fsiFile None None

          printfn "Failing script result: %s" result
          Expect.isTrue (result.StartsWith "Error:") "Should report the load as an error"
          Expect.stringContains result "Script load failed" "Should say the script load failed"

          printfn "Failing script test passed"
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
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "let jsonTestVal = 42;;"OutputFormat.Json None None None None None None

        let doc = System.Text.Json.JsonDocument.Parse(result)
        let root = doc.RootElement
        Expect.isTrue (root.GetProperty("success").GetBoolean()) "should report success"
        // Tool responses never echo the submitted code back (the agent already
        // has it); the evaluated binding is in `result`.
        Expect.stringContains (root.GetProperty("result").GetString()) "jsonTestVal" "should include the evaluated binding"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "sendFSharpCode with Json format returns error structure on failure"
    <| fun _ ->
      task {
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "let x: int = \"not an int\";;"OutputFormat.Json None None None None None None

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
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "let a1 = 1;;\nlet b1 = 2;;"OutputFormat.Json None None None None None None

        let doc = System.Text.Json.JsonDocument.Parse(result)
        let root = doc.RootElement
        Expect.equal root.ValueKind System.Text.Json.JsonValueKind.Array "should be a JSON array"
        Expect.equal (root.GetArrayLength()) 2 "should have 2 results"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]
