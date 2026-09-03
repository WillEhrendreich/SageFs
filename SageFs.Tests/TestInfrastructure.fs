module SageFs.Tests.TestInfrastructure

open SageFs.ActorCreation
open SageFs.AppState
open SageFs.McpTools
open SageFs.WorkflowTypes
open System.Collections.Concurrent
open System.Threading

let quietLogger =
  { new SageFs.Utils.ILogger with
      member _.LogDebug msg = ()
      member _.LogInfo msg = ()
      member _.LogError msg = ()
      member _.LogWarning msg = ()
  }

/// Serialize process-global environment-variable mutations across test lists.
/// Expecto runs test LISTS in parallel, so two lists mutating the same env
/// var (SAGEFS_DEVRELOAD kill-switch tests + HotReloadTool env-reading tests)
/// race: one test's SetEnvironmentVariable can be observed mid-flight by the
/// other, producing intermittent failures that pass in isolation.
let envLock = obj()

let withEnvVar (name: string) (value: string option) (f: unit -> 'T) : 'T =
  lock envLock (fun () ->
    let original = System.Environment.GetEnvironmentVariable(name)
    System.Environment.SetEnvironmentVariable(name, (match value with Some v -> v | None -> null))
    try f ()
    finally
      System.Environment.SetEnvironmentVariable(name, original))

/// Create a temporary file-based SQLite friction store for tests.
/// Each call creates a new database file in the temp directory.
/// The file is NOT automatically cleaned up — tests should delete it if needed,
/// or rely on OS temp cleanup. For most unit tests, leaving small temp files
/// is acceptable (they'll be cleaned eventually).
let tempFrictionStore () : SageFs.Features.FrictionSqlite.FrictionStore =
  let dbPath =
    System.IO.Path.Combine(
      System.IO.Path.GetTempPath(),
      sprintf "sagefs-test-friction-%s.db" (System.Guid.NewGuid().ToString("N")))
  let connStr = sprintf "Data Source=%s" dbPath
  let store = SageFs.Features.FrictionSqlite.Store.create connStr
  match store.Initialize() with
  | Ok () -> store
  | Error err -> failwithf "Failed to initialize test friction store: %s" err

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

/// Await a condition with a hard ceiling, without sleep-polling.
/// Yields via Task.Delay so the thread pool is never hogged; returns true only
/// when the condition was satisfied before the ceiling elapsed.
let awaitCondition (timeoutMs: int) (condition: unit -> bool) =
  task {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable ok = false
    while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
      if condition () then ok <- true
      else do! System.Threading.Tasks.Task.Delay 10
    return ok
  }

/// Await a TaskCompletionSource with a hard ceiling. Completes the TCS with
/// false when the timeout elapses, so a timed-out wait fails the test with a
/// clear signal instead of hanging.
let awaitTcs (timeoutMs: int) (tcs: System.Threading.Tasks.TaskCompletionSource<bool>) =
  task {
    let! winner =
      System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(timeoutMs))
    let completed = obj.ReferenceEquals(winner, tcs.Task)
    if not completed then tcs.TrySetResult false |> ignore
    return completed
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
  { CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test-session")
    ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
    StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
    PurgeSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "purged")
    RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
    GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(Some proxy)
    GetSessionInfo = fun _ ->
      System.Threading.Tasks.Task.FromResult(
        Some {
          Id = sessionId
          Name = None
          Projects = []
          WorkingDirectory = ""
          SolutionRoot = None
          Status = SageFs.WorkerProtocol.SessionStatus.Ready
          FaultReason = None
          WorkerPid = None
          WorkerPort = None
          Workflow = SessionWorkflow.Interactive
          CreatedAt = System.DateTime.UtcNow
          LastActivity = System.DateTime.UtcNow
        })
    GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
    UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
    NotifyWorkerDied = fun _ -> () }

/// Create a McpContext backed by the global shared actor
let sharedCtx () =
  let result = globalActorResult.Value
  let sessionId = SageFs.WorkerProtocol.SessionId.newId()
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- SageFs.WorkerProtocol.SessionId.value sessionId
  { FrictionStore = Some (tempFrictionStore())
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None } : McpContext

/// Create a McpContext with a custom session ID backed by the global shared actor
let sharedCtxWith (sessionId: SageFs.WorkerProtocol.SessionId) =
  let result = globalActorResult.Value
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- SageFs.WorkerProtocol.SessionId.value sessionId
  { FrictionStore = Some (tempFrictionStore())
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None } : McpContext
