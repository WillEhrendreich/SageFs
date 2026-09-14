module SageFs.Tests.McpToolExecutionTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure

[<Tests>]
let tests =
  testList "MCP tool execution" [
    testTask "hard_reset_fsi_session with rebuild returns before background restart completes" {
      let result = globalActorResult.Value
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["mcp"] <- "aaa00001"
      let restartStarted = TaskCompletionSource<unit>()
      let allowRestartFinish = TaskCompletionSource<unit>()

      let ops : SessionManagementOps = {
        CreateSession = fun _ _ _ -> Task.FromResult(Ok "test-session")
        ListSessions = fun () -> Task.FromResult("No sessions")
        StopSession = fun _ -> Task.FromResult(Ok "stopped")
        PurgeSession = fun _ -> Task.FromResult(Ok "purged")
        RestartSession = fun _ _ ->
          task {
            restartStarted.TrySetResult(()) |> ignore
            do! allowRestartFinish.Task
            return Ok "restarted"
          }
        GetProxy = fun _ -> Task.FromResult(Some (fun _ -> async { return WorkerProtocol.WorkerResponse.WorkerReady }))
        GetSessionInfo = fun id ->
          Task.FromResult(
            Some { WorkerProtocol.SessionInfo.Id = id
                   Name = None
                   Projects = []
                   WorkingDirectory = ""
                   SolutionRoot = None
                   Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }
                   Workflow = WorkflowTypes.SessionWorkflow.Interactive
                   CreatedAt = DateTime.UtcNow
                   LastActivity = DateTime.UtcNow
                   ActiveProject = None
                   ProjectRoles = []
                   App = SageFs.AppRun.AppRunState.NotRunning })
        GetAllSessions = fun () -> Task.FromResult([])
        UpdateSessionStatus = fun _ _ -> Task.FromResult(())
        NotifyWorkerDied = fun _ -> ()
        ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
        ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
        AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
        EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
        AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
        SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
      }

      let ctx : McpContext =
        { FrictionStore = None
          DiagnosticsChanged = result.DiagnosticsChanged
          StateChanged = None
          SessionOps = ops
          SessionMap = sessionMap
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None
          GetWarmupContext = None
          GetFeatureState = None
          ActivityTracker = SageFs.AgentActivityTracker.create()
          LiveSnapshotSink = None
          CohortOwner = None }

      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)
      let toolTask = tools.hard_reset_fsi_session(true, "")
      let! completed = Task.WhenAny(toolTask, Task.Delay(1000))

      obj.ReferenceEquals(completed, toolTask)
      |> Expect.isTrue "tool call should return immediately for rebuild hard reset"

      let! message = toolTask
      message
      |> Expect.stringContains "tool result should acknowledge background rebuild" "Hard reset initiated"

      restartStarted.Task.IsCompleted
      |> Expect.isTrue "background restart should begin"

      allowRestartFinish.TrySetResult(()) |> ignore
    }

    // roast-7 §2/§16 item 2: send_fsharp_code used to flatten every result
    // to a `sprintf "Result: %s"` / `"Error: %s"` string even though
    // structured data (diagnostics with spans, a classified SageFsError)
    // already existed and was thrown away. These exercise the ACTUAL tool
    // member (not just the pure formatter — see McpAdapterTests.fs for
    // that) so the wiring itself is proven, not just the formatting logic.
    testTask "send_fsharp_code returns structuredContent with success=true on a clean eval" {
      let ctx = sharedCtx ()
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      let! (result: ModelContextProtocol.Protocol.CallToolResult) =
        tools.send_fsharp_code("test", "let structuredTestValue = 99", "", "", "", 0, "")

      (result.IsError.HasValue && result.IsError.Value)
      |> Expect.isFalse "a clean eval must not be flagged IsError"
      result.StructuredContent.HasValue
      |> Expect.isTrue "structured content must be attached"
      let root = result.StructuredContent.Value
      root.GetProperty("success").GetBoolean() |> Expect.isTrue "success flag"
      root.GetProperty("result").GetString()
      |> Expect.stringContains "result text should be present" "structuredTestValue"
      root.GetProperty("diagnostics").GetArrayLength() >= 0
      |> Expect.isTrue "diagnostics array must be present (possibly empty)"
      // The text block is unchanged from before this change — nothing that
      // reads plain text should break.
      let textBlock =
        result.Content
        |> Seq.tryPick (fun c -> match c with :? ModelContextProtocol.Protocol.TextContentBlock as t -> Some t.Text | _ -> None)
      textBlock |> Expect.isSome "a human-readable text block must still be present"
    }

    testTask "send_fsharp_code returns structuredContent with the SageFsError algebra and IsError=true on a compile failure" {
      let ctx = sharedCtx ()
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      let! (result: ModelContextProtocol.Protocol.CallToolResult) =
        tools.send_fsharp_code("test", "let thisDoesNotCompile : int = \"not an int\"", "", "", "", 0, "")

      (result.IsError.HasValue && result.IsError.Value)
      |> Expect.isTrue "a compile failure must be flagged IsError"
      result.StructuredContent.HasValue
      |> Expect.isTrue "structured content must be attached on failure too"
      let root = result.StructuredContent.Value
      // Same shape as every other tool's structured error (SageFsError.toJson):
      // case/message/suggestedAction — not a flattened {success,error} string.
      root.TryGetProperty("case") |> fst |> Expect.isTrue "must carry a case token"
      root.TryGetProperty("message") |> fst |> Expect.isTrue "must carry a message"
      root.TryGetProperty("suggestedAction") |> fst |> Expect.isTrue "must carry a suggestedAction"
    }
  ]
