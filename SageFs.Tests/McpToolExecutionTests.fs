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
        RestartSession = fun _ _ ->
          task {
            restartStarted.TrySetResult(()) |> ignore
            do! allowRestartFinish.Task
            return Ok "restarted"
          }
        GetProxy = fun _ -> Task.FromResult(None)
        GetSessionInfo = fun id ->
          Task.FromResult(
            Some { WorkerProtocol.SessionInfo.Id = id
                   Name = None
                   Projects = []
                   WorkingDirectory = ""
                   SolutionRoot = None
                   Status = WorkerProtocol.SessionStatus.Ready
                   WorkerPid = None
                   Workflow = WorkflowTypes.SessionWorkflow.Interactive
                   CreatedAt = DateTime.UtcNow
                   LastActivity = DateTime.UtcNow })
        GetAllSessions = fun () -> Task.FromResult([])
        UpdateSessionStatus = fun _ _ -> Task.FromResult(())
        GetStandbyInfo = fun () -> Task.FromResult(StandbyInfo.NoPool)
        NotifyWorkerDied = fun _ -> ()
      }

      let ctx : McpContext =
        { Persistence = inMemoryPersistence ()
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
          ActivityTracker = SageFs.AgentActivityTracker.create() }

      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)
      let toolTask = tools.hard_reset_fsi_session(Some true, "")
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
  ]
