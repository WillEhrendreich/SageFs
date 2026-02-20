module SageFs.Tests.TestInfrastructure

open SageFs.ActorCreation
open SageFs.AppState
open SageFs.McpTools
open System.Collections.Concurrent

let quietLogger =
  { new SageFs.Utils.ILogger with
      member _.LogDebug msg = ()
      member _.LogInfo msg = ()
      member _.LogError msg = ()
      member _.LogWarning msg = ()
  }

/// Shared Marten store for tests — uses the same Testcontainer as EventStoreTests
let testStore = lazy(
  let container = EventStoreTests.sharedContainer.Value
  SageFs.EventStore.configureStore (container.GetConnectionString())
)

/// Single shared actor result for all read-only tests across the entire test suite.
/// Created once on first access, reused everywhere.
let globalActorResult = lazy(
  let args = mkCommonActorArgs quietLogger false ignore []
  createActor args |> Async.AwaitTask |> Async.RunSynchronously
)

/// Create a SessionProxy from a test actor result
let mkProxy (result: ActorResult) : SageFs.WorkerProtocol.SessionProxy =
  fun msg ->
    SageFs.Server.WorkerMain.handleMessage result.Actor result.GetSessionState result.GetEvalStats result.GetStatusMessage msg

/// Create a test SessionManagementOps that routes to the global actor
let mkTestSessionOps (result: ActorResult) (sessionId: string) : SageFs.SessionManagementOps =
  let proxy = mkProxy result
  { CreateSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test-session")
    ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
    StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
    RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
    GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(Some proxy)
    GetSessionInfo = fun _ ->
      System.Threading.Tasks.Task.FromResult(
        Some { SageFs.WorkerProtocol.SessionInfo.Id = sessionId
               Name = None
               Projects = []; WorkingDirectory = ""; SolutionRoot = None
               Status = SageFs.WorkerProtocol.SessionStatus.Ready
               WorkerPid = None; CreatedAt = System.DateTime.UtcNow; LastActivity = System.DateTime.UtcNow })
    GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
    GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool) }

/// Create a McpContext backed by the global shared actor and Marten store
let sharedCtx () =
  let result = globalActorResult.Value
  let sessionId = SageFs.EventStore.createSessionId ()
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- sessionId
  { Store = testStore.Value
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None } : McpContext

/// Create a McpContext with a custom session ID backed by the global shared actor
let sharedCtxWith sessionId =
  let result = globalActorResult.Value
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- sessionId
  { Store = testStore.Value
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None } : McpContext
