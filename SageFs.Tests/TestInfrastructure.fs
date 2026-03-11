module SageFs.Tests.TestInfrastructure

open SageFs.ActorCreation
open SageFs.AppState
open SageFs.McpTools
open System.Collections.Concurrent
open System.Threading

let quietLogger =
  { new SageFs.Utils.ILogger with
      member _.LogDebug msg = ()
      member _.LogInfo msg = ()
      member _.LogError msg = ()
      member _.LogWarning msg = ()
  }

/// An in-memory EventPersistence that actually stores events.
/// Use in tests that verify event-append behavior (unlike noop which silently drops all writes).
let inMemoryPersistence () : SageFs.EventStore.EventPersistence =
  let store = ConcurrentDictionary<string, ResizeArray<SageFs.Features.Events.SageFsEvent>>()
  { AppendEvents = fun stream events ->
      System.Threading.Tasks.Task.FromResult(
        let bucket = store.GetOrAdd(stream, fun _ -> ResizeArray())
        lock bucket (fun () ->
          bucket.AddRange(events)
          Ok ()))
    FetchStream = fun stream ->
      System.Threading.Tasks.Task.FromResult(
        match store.TryGetValue(stream) with
        | true, bucket ->
          lock bucket (fun () ->
            bucket |> Seq.map (fun e -> System.DateTimeOffset.UtcNow, e) |> List.ofSeq)
        | _ -> [])
    CountEvents = fun stream ->
      System.Threading.Tasks.Task.FromResult(
        match store.TryGetValue(stream) with
        | true, bucket -> lock bucket (fun () -> bucket.Count)
        | _ -> 0) }

/// Poll a condition with 10ms intervals until it returns true or timeout expires.
/// Returns the final condition value.
let waitFor (timeoutMs: int) (condition: unit -> bool) =
  let sw = System.Diagnostics.Stopwatch.StartNew()
  while not (condition ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
    Thread.Sleep 10
  condition ()

/// Async version of waitFor for task-based tests.
let waitForAsync (timeoutMs: int) (condition: unit -> System.Threading.Tasks.Task<bool>) =
  task {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable result = false
    while not result && sw.ElapsedMilliseconds < int64 timeoutMs do
      let! ok = condition ()
      result <- ok
      if not result then
        do! System.Threading.Tasks.Task.Delay 50
    return result
  }

/// Single shared actor result for all read-only tests across the entire test suite.
/// Created once on first access, reused everywhere.
let globalActorResult = lazy(
  let args = mkCommonActorArgs quietLogger false ignore SageFs.Args.ProjectLoadConfig.empty true
  createActor args |> Async.AwaitTask |> Async.RunSynchronously
)

/// Create a SessionProxy from a test actor result
let mkProxy (result: ActorResult) : SageFs.WorkerProtocol.SessionProxy =
  fun msg ->
    SageFs.Server.WorkerMain.handleMessage result.Actor result.GetSessionState result.GetEvalStats result.GetStatusMessage (fun () -> SageFs.Features.LiveTesting.LiveTestHookResult.noOp) (fun _ -> ()) (fun () -> [||], []) msg

/// Create a test SessionManagementOps that routes to the global actor
let mkTestSessionOps (result: ActorResult) (sessionId: SageFs.WorkerProtocol.SessionId) : SageFs.SessionManagementOps =
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
    GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool)
    NotifyWorkerDied = fun _ -> () }

/// Create a McpContext backed by the global shared actor
let sharedCtx () =
  let result = globalActorResult.Value
  let sessionId = SageFs.WorkerProtocol.SessionId.newId()
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- SageFs.WorkerProtocol.SessionId.value sessionId
  { Persistence = SageFs.EventStore.EventPersistence.noop
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None } : McpContext

/// Create a McpContext with a custom session ID backed by the global shared actor
let sharedCtxWith (sessionId: SageFs.WorkerProtocol.SessionId) =
  let result = globalActorResult.Value
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- SageFs.WorkerProtocol.SessionId.value sessionId
  { Persistence = SageFs.EventStore.EventPersistence.noop
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None } : McpContext
