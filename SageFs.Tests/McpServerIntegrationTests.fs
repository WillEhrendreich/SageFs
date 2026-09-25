module SageFs.Tests.McpServerIntegrationTests

open Expecto
open Expecto.Flip
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

    testTask "sendFSharpCode tool executes code" {
        printfn "Testing sendFSharpCode tool..."
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "let x = 42"OutputFormat.Text None None None None None None

        printfn "Result: %s" result
        result |> Expect.stringContains "Should execute successfully" "val x"

        printfn "sendFSharpCode tool test passed"
      }

    testTask "sendFSharpCode tool does not require event tracking" {
        printfn "Testing sendFSharpCode without event tracking..."
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "claude" "let aiValue = 100" OutputFormat.Text None None None None None None
        result |> Expect.stringContains "Should still execute successfully" "val aiValue"

        printfn "Non-event-tracking test passed"
      }

    testTask "getRecentEvents tool reports honestly when no feature state is wired" {
        // roast-7 §3a: this used to be a hardcoded stub returning
        // "Recent events: none recorded" REGARDLESS of what actually
        // happened — including when events genuinely were recorded (see the
        // "returns real recorded events" test below). This context has no
        // GetFeatureState getter wired (matches `agentCtx`/`sharedCtxWith`),
        // so the honest answer here is "no feature state", not a lie about
        // events.
        printfn "Testing getRecentEvents tool (no feature state wired)..."
        let ctx = agentCtx ()

        let! _ = sendFSharpCode ctx "agent1" "let a = 1" OutputFormat.Text None None None None None None
        let! _ = sendFSharpCode ctx "agent2" "let b = 2" OutputFormat.Text None None None None None None

        let! result = getRecentEvents ctx "test" 5 None

        printfn "Events result: %s" result
        result |> Expect.stringContains "should explain feature state is unavailable" "Feature state not available"
        Expect.isFalse "must not be the old hardcoded stub wording" (result = "Recent events: none recorded")

        printfn "getRecentEvents tool test passed"
      }

    testTask "getRecentEvents tool returns real recorded events, not a stub" {
        // roast-7 §3a/§16 item 3: send_fsharp_code's own tool description
        // tells agents to call get_recent_fsi_events "if the return value is
        // ambiguous" — it must actually reflect what was evaluated. Wires a
        // real FeaturePushState (the same EvalHistory the dashboard
        // filmstrip and plan_ripple/impact_forecast already read) to prove
        // the tool surfaces real data instead of a hardcoded string.
        printfn "Testing getRecentEvents tool (real feature state wired)..."
        let ctx = agentCtx ()
        let state =
          Features.FeatureHooks.FeaturePushState.empty
          |> Features.FeatureHooks.recordEval "let a = 1" "val a: int = 1" 5L
          |> Features.FeatureHooks.recordEval "let b = 2" "val b: int = 2" 7L
        let ctxWithState = { ctx with GetFeatureState = Some (fun () -> state) }

        let! result = getRecentEvents ctxWithState "test" 5 None

        printfn "Events result: %s" result
        result |> Expect.stringContains "should include the first eval's code" "let a = 1"
        result |> Expect.stringContains "should include the second eval's code" "let b = 2"
        result |> Expect.stringContains "should include a duration" "ms)"
        Expect.isFalse "must not be the old hardcoded stub" (result = "Recent events: none recorded")

        printfn "getRecentEvents tool test passed"
      }

    testTask "getRecentEvents keeps eval history per session" {
        let ctx = agentCtx ()
        let sessionA = SessionId.newId ()
        let sessionB = SessionId.newId ()
        ctx.SessionMap.["test-agent"] <- SessionId.value sessionA
        ctx.SessionMap.["claude"] <- SessionId.value sessionB
        let! _ = sendFSharpCode ctx "test-agent" "let sessionAValue = 1" OutputFormat.Text None None None None None None
        let! _ = sendFSharpCode ctx "claude" "let sessionBValue = 2" OutputFormat.Text None None None None None None
        let! aEvents = getRecentEvents ctx "test-agent" 20 None
        let! bEvents = getRecentEvents ctx "claude" 20 None
        aEvents |> Expect.stringContains "A sees its own eval" "sessionAValue"
        Expect.isFalse "A must not see B's eval" (aEvents.Contains("sessionBValue"))
        bEvents |> Expect.stringContains "B sees its own eval" "sessionBValue"
        Expect.isFalse "B must not see A's eval" (bEvents.Contains("sessionAValue"))
      }


    testTask "getRecentEvents tool says so honestly when history is genuinely empty" {
        printfn "Testing getRecentEvents tool (real but empty feature state)..."
        let ctx = agentCtx ()
        let ctxWithEmptyState = { ctx with GetFeatureState = Some (fun () -> Features.FeatureHooks.FeaturePushState.empty) }

        let! result = getRecentEvents ctxWithEmptyState "test" 5 None

        printfn "Events result: %s" result
        result |> Expect.stringContains "should say no events recorded" "No FSI events recorded"
        Expect.isFalse "must not be the old hardcoded stub wording used as if honest" (result = "Recent events: none recorded")

        printfn "getRecentEvents tool test passed"
      }

    testTask "getStatus tool returns session info" {
        printfn "Testing getStatus tool..."
        let ctx = agentCtx ()

        let! result = getStatus ctx "test" None None

        printfn "Status: %s" result
        result |> Expect.stringContains "Should show session ID" (sprintf "Session: %s" ctx.SessionMap.["test"])
        result |> Expect.stringContains "Should list available tools" "send_fsharp_code"
        result |> Expect.stringContains "Should report zero tracked events" "Events: 0"

        printfn "getStatus tool test passed"
      }

    testTask "loadFSharpScript tool loads and executes script" {
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
          result |> Expect.stringContains "Should define scriptVar1" "val scriptVar1"
          result |> Expect.stringContains "Should define scriptVar2" "val scriptVar2"

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
            res |> Expect.stringContains "Should compute sum correctly" "30"
          | Error ex -> failtestf "Failed to use loaded variables: %s" ex.Message

          printfn "loadFSharpScript tool test passed"
        finally
          System.IO.File.Delete(fsiFile)

          if System.IO.File.Exists(tempFile) then
            System.IO.File.Delete(tempFile)
      }

    testTask "Multiple MCP agents can collaborate in same session" {
        printfn "Testing multi-agent collaboration..."
        let ctx = agentCtx ()

        // Agent 1 defines something
        let! result1 = sendFSharpCode ctx "agent1" "let sharedData = [1; 2; 3]" OutputFormat.Text None None None None None None
        result1 |> Expect.stringContains "Agent 1 should succeed" "val sharedData"

        // Agent 2 uses it
        let! result2 = sendFSharpCode ctx "agent2" "List.sum sharedData" OutputFormat.Text None None None None None None
        result2 |> Expect.stringContains "Agent 2 should use Agent 1's data" "6"

        printfn "Multi-agent collaboration test passed"
      }

    testTask "Console and MCP can work together (simulated)" {
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
        result |> Expect.stringContains "MCP should use console value" "84"

        printfn "Console+MCP collaboration test passed"
      }

    testTask "sendFSharpCode handles compilation error" {
        printfn "Testing sendFSharpCode with compilation error..."
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "let x = invalid syntax" OutputFormat.Text None None None None None None

        printfn "Error result: %s" result
        result |> Expect.stringContains "Should return error message" "Error:"

        printfn "Compilation error test passed"
      }

    testTask "sendFSharpCode handles runtime error" {
        printfn "Testing sendFSharpCode with runtime error..."
        let ctx = agentCtx ()

        let! result = sendFSharpCode ctx "test-agent" "1 / 0" OutputFormat.Text None None None None None None

        printfn "Runtime error result: %s" result
        result |> Expect.isNotEmpty "Should return some result or error output"

        printfn "Runtime error test passed"
      }

    testTask "loadFSharpScript with non-existent file returns error" {
        printfn "Testing loadFSharpScript with non-existent file..."
        let ctx = agentCtx ()

        let! result = loadFSharpScript ctx "test-agent" "C:\\nonexistent\\file.fsx"None None

        printfn "Non-existent file result: %s" result
        result |> Expect.stringContains "Should return error for non-existent file" "Error"

        printfn "Non-existent file test passed"
      }

    // A script is #loaded as ONE compilation unit, so a broken statement fails
    // the whole load (FSI semantics) — reported as an Error with the reason,
    // not as "Partial: 1 succeeded, 1 failed" (that per-statement contract was
    // removed with the worker-only session architecture).
    testTask "loadFSharpScript with a failing statement reports the load error" {
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
          (result.StartsWith "Error:") |> Expect.isTrue "Should report the load as an error"
          result |> Expect.stringContains "Should say the script load failed" "Script load failed"

          printfn "Failing script test passed"
        finally
          System.IO.File.Delete(fsiFile)

          if System.IO.File.Exists(tempFile) then
            System.IO.File.Delete(tempFile)
      }

    testTask "sendFSharpCode with Json format returns structured JSON" {
        let ctx = agentCtx ()

        let! (result: string) = sendFSharpCode ctx "test-agent" "let jsonTestVal = 42;;"OutputFormat.Json None None None None None None

        let doc = System.Text.Json.JsonDocument.Parse(result)
        let root = doc.RootElement
        (root.GetProperty("success").GetBoolean()) |> Expect.isTrue "should report success"
        // Tool responses never echo the submitted code back (the agent already
        // has it); the evaluated binding is in `result`.
        (root.GetProperty("result").GetString()) |> Expect.stringContains "should include the evaluated binding" "jsonTestVal"
      }

    testTask "sendFSharpCode with Json format returns error structure on failure" {
        let ctx = agentCtx ()

        let! (result: string) = sendFSharpCode ctx "test-agent" "let x: int = \"not an int\";;"OutputFormat.Json None None None None None None

        let doc = System.Text.Json.JsonDocument.Parse(result)
        let root = doc.RootElement
        (root.GetProperty("success").GetBoolean()) |> Expect.isFalse "should report failure"
        (root.GetProperty("error").GetString()) |> Expect.isNonEmpty "should have error message"
      }

    testTask "sendFSharpCode with Json format returns array for multiple statements" {
        let ctx = agentCtx ()

        let! (result: string) = sendFSharpCode ctx "test-agent" "let a1 = 1;;\nlet b1 = 2;;"OutputFormat.Json None None None None None None

        let doc = System.Text.Json.JsonDocument.Parse(result)
        let root = doc.RootElement
        root.ValueKind |> Expect.equal "should be a JSON array" System.Text.Json.JsonValueKind.Array
        (root.GetArrayLength()) |> Expect.equal "should have 2 results" 2
      }
  ]
