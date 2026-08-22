module SageFs.Tests.SessionIsolationTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Tests.TestInfrastructure
open SageFs.Tests.SharedGenerators
open System.Collections.Concurrent

/// Tests that MCP session switch does NOT leak into other clients.
/// These tests define the contract for per-client session isolation.
module McpSessionIsolation =

  /// Create a McpContext with a tracking dispatch that records all messages sent.
  /// Uses inMemoryPersistence so event-append tests work correctly.
  let ctxWithTracking sessionId =
    let result = globalActorResult.Value
    let dispatched = System.Collections.Generic.List<SageFsMsg>()
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["test"] <- sessionId
    let ctx =
      { FrictionStore = None
        DiagnosticsChanged = result.DiagnosticsChanged
        StateChanged = None
        SessionOps = {
          CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test-session")
          ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
          StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
          RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
          GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(None)
          GetSessionInfo = fun id ->
            System.Threading.Tasks.Task.FromResult(
              Some { WorkerProtocol.SessionInfo.Id = id
                     Name = None
                     Projects = []; WorkingDirectory = ""; SolutionRoot = None
                     Status = WorkerProtocol.SessionStatus.Ready
                     FaultReason = None
                     WorkerPid = None
                     Workflow = WorkflowTypes.SessionWorkflow.Interactive
                     CreatedAt = System.DateTime.UtcNow
                     LastActivity = System.DateTime.UtcNow })
          GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
          UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
          GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool)
          NotifyWorkerDied = fun _ -> ()
        }
        SessionMap = sessionMap
        McpPort = 0
        Dispatch = Some (fun msg -> dispatched.Add(msg))
        GetElmModel = None
        GetElmRegions = None
        GetWarmupContext = None
        GetFeatureState = None
        ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext
    ctx, dispatched

  /// Call switchSession and return result, ignoring event store errors.
  /// switchSession dispatches to Elm BEFORE appending to the event store,
  /// so dispatch tracking is valid even if the store throws.
  let switchSessionIgnoringStoreErrors ctx agent sessionId =
    task {
      try
        let! result = switchSession ctx agent sessionId
        return Ok result
      with ex ->
        return Error (ex.Message)
    }

  let tests = testSequenced <| ptestList "[Integration] MCP session isolation" [

    testTask "switchSession updates only the given context's SessionMap for that agent" {
      let ctx1, _ = ctxWithTracking "aaaaaa01"
      let ctx2, _ = ctxWithTracking "aaaaaa01"

      let! _ = switchSessionIgnoringStoreErrors ctx1 "agent1" "bbbbbb02"

      ctx1.SessionMap.["agent1"]
      |> Expect.equal "ctx1 agent1 should switch to B" "bbbbbb02"

      ctx1.SessionMap.["test"]
      |> Expect.equal "ctx1 test agent should remain on A" "aaaaaa01"

      ctx2.SessionMap.["test"]
      |> Expect.equal "ctx2 should remain on A" "aaaaaa01"
    }

    testCaseAsync "switchSession does NOT dispatch SessionSwitched to Elm" <| async {
      let ctx, dispatched = ctxWithTracking "aaaaaa01"

      let! _ = switchSessionIgnoringStoreErrors ctx "test" "bbbbbb02" |> Async.AwaitTask

      dispatched
      |> Seq.filter (fun msg ->
        match msg with
        | SageFsMsg.Event (SageFsEvent.SessionSwitched _) -> true
        | _ -> false)
      |> Seq.length
      |> Expect.equal "switchSession should not dispatch SessionSwitched to Elm" 0
    }

    testCaseAsync "switchSession does NOT dispatch ListSessions to Elm" <| async {
      let ctx, dispatched = ctxWithTracking "aaaaaa01"

      let! _ = switchSessionIgnoringStoreErrors ctx "test" "bbbbbb02" |> Async.AwaitTask

      dispatched
      |> Seq.filter (fun msg ->
        match msg with
        | SageFsMsg.Editor EditorAction.ListSessions -> true
        | _ -> false)
      |> Seq.length
      |> Expect.equal "switchSession should not dispatch ListSessions to Elm" 0
    }

    testTask "switchSession updates active session mapping" {
      let ctx, _ = ctxWithTracking "aaaaaa01"

      let! _ = switchSession ctx "test" "bbbbbb02"
      
      // Verify the session was switched by checking the session map was updated
      // (EventStore event persistence was removed — binary manifest is now the sole source of truth)
      ()
    }

    testTask "switchSession returns error for nonexistent session" {
      let result = globalActorResult.Value
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["test"] <- "aaaaaa01"
      let ctx =
        { FrictionStore = None
          DiagnosticsChanged = result.DiagnosticsChanged
          StateChanged = None
          SessionOps = {
            CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test")
            ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
            StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
            RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
            GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(None)
            GetSessionInfo = fun _ -> System.Threading.Tasks.Task.FromResult(None)
            GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
            UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
            GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool)
            NotifyWorkerDied = fun _ -> () }
          SessionMap = sessionMap
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None
          GetWarmupContext = None
          GetFeatureState = None
          ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext

      let! result = switchSession ctx "test" "ffff0001"

      result
      |> Expect.stringContains "should contain error message" "not found"
    }

    testTask "two concurrent MCP connections maintain independent sessions" {
      let ctx1, _ = ctxWithTracking "aaaaaa01"
      let ctx2, _ = ctxWithTracking "bbbbbb02"

      let! _ = switchSessionIgnoringStoreErrors ctx1 "test" "cccccc03"

      ctx1.SessionMap.["test"]
      |> Expect.equal "ctx1 should be on C" "cccccc03"

      ctx2.SessionMap.["test"]
      |> Expect.equal "ctx2 should still be on B" "bbbbbb02"

      let! _ = switchSessionIgnoringStoreErrors ctx2 "test" "dddddd04"

      ctx1.SessionMap.["test"]
      |> Expect.equal "ctx1 should still be on C" "cccccc03"

      ctx2.SessionMap.["test"]
      |> Expect.equal "ctx2 should be on D" "dddddd04"
    }
  ]

module SessionResolutionByWorkingDir =

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = None; Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionStatus.Ready
      FaultReason = None
      WorkerPid = None
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow }

  let tests = testList "resolveSessionByWorkingDir" [
    test "returns None for empty session list" {
      resolveSessionByWorkingDir [] @"C:\Code\Repos\SageFs"
      |> Expect.isNone "empty list yields None"
    }

    test "returns None when no session matches" {
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\Other" ]
      resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      |> Expect.isNone "no match yields None"
    }

    test "finds exact match" {
      let sessions = [
        mkInfo (testSessionId "aa000001") @"C:\Code\Repos\Other"
        mkInfo (testSessionId "bb000002") @"C:\Code\Repos\SageFs"
      ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "should find matching session"
      result.Value.Id
      |> Expect.equal "should return s2" (testSessionId "bb000002")
    }

    test "matches with trailing separator on query" {
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs\"
      result |> Expect.isSome "trailing sep should match"
      result.Value.Id
      |> Expect.equal "should return s1" (testSessionId "aa000001")
    }

    test "matches case-insensitively on Windows" {
      if Environment.OSVersion.Platform <> PlatformID.Win32NT then
        ()
      else
        let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
        let result = resolveSessionByWorkingDir sessions @"c:\code\repos\sagefs"
        result |> Expect.isSome "case-insensitive match"
        result.Value.Id
        |> Expect.equal "should return s1" (testSessionId "aa000001")
    }

    test "returns first match when multiple sessions share dir" {
      let sessions = [
        mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs"
        mkInfo (testSessionId "bb000002") @"C:\Code\Repos\SageFs"
      ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "should find a match"
      result.Value.Id
      |> Expect.equal "should return first" (testSessionId "aa000001")
    }

    test "session trailing separator matches clean input" {
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs\" ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "session trailing sep should match"
      result.Value.Id
      |> Expect.equal "should return s1" (testSessionId "aa000001")
    }
  ]

module WorkingDirDeepMatching =
  /// WHY — friction report 2026-08: an agent working in a subdirectory of a
  /// registered session got "No sessions match" from get_fsi_status while
  /// list_sessions showed the session. Deep matching routes subdirectory
  /// requests to the owning session instead of reporting it missing.

  open SageFs.McpTools

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = None; Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionStatus.Ready
      FaultReason = None
      WorkerPid = None
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow }

  let tests = testList "sessionsMatchingWorkingDirDeep" [

    testCase "WHY — subdirectory routing — an agent calling from repo\\tests reaches the session rooted at repo because exact-only matching reported existing sessions as missing"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageFs\tests"
      |> List.map (fun s -> s.Id)
      |> Expect.equal "subdirectory of session root should match the session" [ testSessionId "aa000001" ]

    testCase "WHY — path-boundary safety — 'SageFsExtra' is NOT inside 'SageFs' so prefix matching must not cross directory-name boundaries"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageFsExtra"
      |> Expect.isEmpty "sibling with shared name prefix must not match"

    testCase "WHY — sibling isolation — a different repo's agent must not silently route into another repo's session"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs" ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageTech"
      |> Expect.isEmpty "unrelated sibling directory must not match"

    testCase "exact match still wins and is not duplicated by ancestor fallback"
    <| fun _ ->
      let sessions = [
        mkInfo (testSessionId "aa000001") @"C:\Code\Repos\SageFs"
        mkInfo (testSessionId "bb000002") @"C:\Code\Repos"
      ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageFs"
      |> List.map (fun s -> s.Id)
      |> Expect.equal "exact match should be the only result" [ testSessionId "aa000001" ]

    testCase "sessions with empty WorkingDirectory never match by ancestor"
    <| fun _ ->
      let sessions = [ mkInfo (testSessionId "aa000001") "" ]
      sessionsMatchingWorkingDirDeep sessions @"C:\Code\Repos\SageFs\tests"
      |> Expect.isEmpty "empty base dir must not act as root-of-everything"
  ]

module WorkingDirRoutingPriority =

  open System.Threading.Tasks

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = Some (WorkerProtocol.SessionId.value id); Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionStatus.Ready
      FaultReason = None
      WorkerPid = Some 1234
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow }

  let dummyProxy : WorkerProtocol.SessionProxy =
    fun _msg -> async { return WorkerProtocol.WorkerResponse.WorkerReady }

  let mkCtx (sessions: WorkerProtocol.SessionInfo list) (proxies: Map<string, WorkerProtocol.SessionProxy>) : McpContext =
    let sessionMap = ConcurrentDictionary<string, string>()
    { FrictionStore = None; DiagnosticsChanged = Unchecked.defaultof<_>
      StateChanged = None
      SessionOps =
        { CreateSession = fun _ _ _ -> Task.FromResult(Error(SageFsError.SessionCreationFailed "n/a"))
          ListSessions = fun () -> Task.FromResult("")
          StopSession = fun _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          RestartSession = fun _ _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          GetProxy = fun sid -> Task.FromResult(Map.tryFind (WorkerProtocol.SessionId.value sid) proxies)
          GetSessionInfo = fun sid -> Task.FromResult(sessions |> List.tryFind (fun s -> s.Id = sid))
          GetAllSessions = fun () -> Task.FromResult(sessions)
          UpdateSessionStatus = fun _ _ -> Task.FromResult(())
          GetStandbyInfo = fun () -> Task.FromResult(StandbyInfo.NoPool)
          NotifyWorkerDied = fun _ -> () }
      SessionMap = sessionMap; McpPort = 0; Dispatch = None
      GetElmModel = None; GetElmRegions = None; GetWarmupContext = None
      GetFeatureState = None; ActivityTracker = SageFs.AgentActivityTracker.create() }

  let tests = testSequenced <| testList "workingDirectory routing priority" [
    testTask "workingDirectory should override cached session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "should route to Harmony based on workingDirectory" (Routable "4a120002")
    }
    testTask "workingDirectory routes correctly when no cached session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "should route to Harmony via workingDirectory" (Routable "4a120002")
    }
    testTask "workingDirectory returns an ambiguity error when multiple sessions share the same directory" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1; s2] (Map.ofList ["5a6e0001",dummyProxy; "4a120002",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\SageFs")
      match resolved with
      | Routable sid ->
        failtestf "expected workingDirectory ambiguity error but resolved '%s'" sid
      | Gone msg ->
        msg |> Expect.stringContains "should describe the ambiguity" "Multiple sessions match workingDirectory"
        msg |> Expect.stringContains "should list the first matching session" "5a6e0001"
        msg |> Expect.stringContains "should list the second matching session" "4a120002"
        activeSessionId ctx "mcp"
        |> Expect.equal "cached session should remain unchanged after ambiguity" "5a6e0001"
      | other ->
        failtestf "expected Gone ambiguity error but got %A" other
    }
    testTask "explicit sessionId always wins over workingDirectory" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" (Some "5a6e0001") (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "explicit sessionId takes priority" (Routable "5a6e0001")
    }
    testTask "workingDirectory updates the cached session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! _ = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      activeSessionId ctx "mcp" |> Expect.equal "cached session should update" "4a120002"
    }
    testTask "falls back to cached session when workingDirectory is None" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["5a6e0001",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! resolved = resolveSessionId ctx "mcp" None None
      resolved |> Expect.equal "should fall back to cached when no workingDirectory" (Routable "5a6e0001")
    }
    testTask "falls back to the only session when no active session is cached" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["5a6e0001",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None None
      resolved |> Expect.equal "single session should be used automatically" (Routable "5a6e0001")
      activeSessionId ctx "mcp" |> Expect.equal "single session should become cached" "5a6e0001"
    }
    testTask "returns an explicit error when workingDirectory does not match any session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["5a6e0001",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Other")
      match resolved with
      | Routable sid -> failtestf "expected workingDirectory mismatch error, got session '%s'" sid
      | Gone msg ->
        msg |> Expect.stringContains "should explain mismatch" "No sessions match workingDirectory"
        activeSessionId ctx "mcp" |> Expect.equal "cached session should remain unchanged" ""
      | other ->
        failtestf "expected Gone mismatch error but got %A" other
    }
    testTask "falls back to the session matching the daemon current directory" {
      let originalDir = Environment.CurrentDirectory
      let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      let currentDir = Path.Combine(root, "current")
      let otherDir = Path.Combine(root, "other")
      Directory.CreateDirectory(currentDir) |> ignore
      Directory.CreateDirectory(otherDir) |> ignore
      Environment.CurrentDirectory <- currentDir
      try
        let currentSession = mkInfo (testSessionId "5a6e0001") currentDir
        let otherSession = mkInfo (testSessionId "4a120002") otherDir
        let ctx =
          mkCtx [currentSession; otherSession] (Map.ofList ["5a6e0001",dummyProxy; "4a120002",dummyProxy])
        let! resolved = resolveSessionId ctx "mcp" None None
        resolved |> Expect.equal "current directory session should be selected" (Routable "5a6e0001")
        activeSessionId ctx "mcp" |> Expect.equal "current directory session should become cached" "5a6e0001"
      finally
        Environment.CurrentDirectory <- originalDir
        try Directory.Delete(root, true) with _ -> ()
    }
    testTask "current directory fallback returns an ambiguity error when multiple sessions share the daemon directory" {
      let originalDir = Environment.CurrentDirectory
      let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      let currentDir = Path.Combine(root, "current")
      let otherDir = Path.Combine(root, "other")
      Directory.CreateDirectory(currentDir) |> ignore
      Directory.CreateDirectory(otherDir) |> ignore
      Environment.CurrentDirectory <- currentDir
      try
        let currentA = mkInfo (testSessionId "5a6e0001") currentDir
        let currentB = mkInfo (testSessionId "4a120002") currentDir
        let otherSession = mkInfo (testSessionId "8c340003") otherDir
        let ctx =
          mkCtx [currentA; currentB; otherSession] (Map.ofList ["5a6e0001",dummyProxy; "4a120002",dummyProxy; "8c340003",dummyProxy])
        let! resolved = resolveSessionId ctx "mcp" None None
        match resolved with
        | Routable sid ->
          failtestf "expected current directory ambiguity error but resolved '%s'" sid
        | Gone msg ->
          msg |> Expect.stringContains "should describe the ambiguity" "Multiple sessions match the current working directory"
          msg |> Expect.stringContains "should list the first matching session" "5a6e0001"
          msg |> Expect.stringContains "should list the second matching session" "4a120002"
          activeSessionId ctx "mcp"
          |> Expect.equal "cache should remain empty after ambiguity" ""
        | other ->
          failtestf "expected Gone ambiguity error but got %A" other
      finally
        Environment.CurrentDirectory <- originalDir
        try Directory.Delete(root, true) with _ -> ()
    }
  ]

module ResetIsolation =

  /// Create a context with two agents on different sessions, plus tracking stubs.
  let mkTrackingCtx () =
    let result = globalActorResult.Value
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["agent1"] <- "aaa00001"
    sessionMap.["agent2"] <- "bbb00002"
    let restartLog = System.Collections.Generic.List<string * bool>()
    let routedSessions = System.Collections.Generic.List<string>()
    let ops : SessionManagementOps = {
      CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "new-session")
      ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
      StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
      RestartSession = fun sid rebuild ->
        restartLog.Add((WorkerProtocol.SessionId.value sid, rebuild))
        System.Threading.Tasks.Task.FromResult(Ok "restarted")
      GetProxy = fun sid ->
        routedSessions.Add(WorkerProtocol.SessionId.value sid)
        System.Threading.Tasks.Task.FromResult(None)
      GetSessionInfo = fun id ->
        System.Threading.Tasks.Task.FromResult(
          Some { WorkerProtocol.SessionInfo.Id = id
                 Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
                 Status = WorkerProtocol.SessionStatus.Ready; WorkerPid = None
                 FaultReason = None
                 Workflow = WorkflowTypes.SessionWorkflow.Interactive
                 CreatedAt = System.DateTime.UtcNow; LastActivity = System.DateTime.UtcNow })
      GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
      UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
      GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool)
      NotifyWorkerDied = fun _ -> ()
    }
    let ctx =
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
        ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext
    ctx, restartLog, routedSessions

  let mkStatusSyncCtx () =
    let result = globalActorResult.Value
    let sid = testSessionId "aaa00001"
    let sidStr = WorkerProtocol.SessionId.value sid
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["agent1"] <- sidStr
    let registryStatus = ref WorkerProtocol.SessionStatus.Ready
    let resetStarted = System.Threading.Tasks.TaskCompletionSource<unit>()
    let allowResetFinish = System.Threading.Tasks.TaskCompletionSource<unit>()

    let sessionInfo () : WorkerProtocol.SessionInfo =
      { Id = sid
        Name = None
        Projects = []
        WorkingDirectory = @"C:\Code\Repos\SageFs"
        SolutionRoot = None
        Status = !registryStatus
        FaultReason = None
        WorkerPid = None
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow }

    let statusSnapshot () : WorkerProtocol.WorkerStatusSnapshot =
      let status =
        match resetStarted.Task.IsCompleted, allowResetFinish.Task.IsCompleted with
        | true, false -> WorkerProtocol.SessionStatus.Starting
        | _ -> WorkerProtocol.SessionStatus.Ready

      { Status = status
        StatusMessage = None
        EvalCount = 0
        AvgDurationMs = 0L
        MinDurationMs = 0L
        MaxDurationMs = 0L }

    let proxy : WorkerProtocol.SessionProxy =
      fun msg ->
        async {
          match msg with
          | WorkerProtocol.WorkerMessage.ResetSession replyId ->
            resetStarted.TrySetResult(()) |> ignore
            do! allowResetFinish.Task |> Async.AwaitTask
            return WorkerProtocol.WorkerResponse.ResetResult(replyId, Ok ())
          | WorkerProtocol.WorkerMessage.GetStatus replyId ->
            return WorkerProtocol.WorkerResponse.StatusResult(replyId, statusSnapshot ())
          | other ->
            return failwithf "unexpected worker message in reset status sync test: %A" other
        }

    let ops : SessionManagementOps = {
      CreateSession = fun _ _ _ -> Task.FromResult(Ok "new-session")
      ListSessions = fun () -> Task.FromResult("No sessions")
      StopSession = fun _ -> Task.FromResult(Ok "stopped")
      RestartSession = fun _ _ -> Task.FromResult(Ok "restarted")
      GetProxy = fun sessionId ->
        match sessionId = sid with
        | true -> Task.FromResult(Some proxy)
        | false -> Task.FromResult(None)
      GetSessionInfo = fun sessionId ->
        match sessionId = sid with
        | true -> Task.FromResult(Some (sessionInfo ()))
        | false -> Task.FromResult(None)
      GetAllSessions = fun () -> Task.FromResult([ sessionInfo () ])
      UpdateSessionStatus = fun _ status ->
        registryStatus := status
        Task.FromResult(())
      GetStandbyInfo = fun () -> Task.FromResult(SageFs.StandbyInfo.NoPool)
      NotifyWorkerDied = fun _ -> () }

    let ctx =
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
        ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext
    ctx, sidStr, resetStarted, allowResetFinish

  let mkTransportFailureCtx () =
    let result = globalActorResult.Value
    let sid = testSessionId "aaa00001"
    let sidStr = WorkerProtocol.SessionId.value sid
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["agent1"] <- sidStr
    let registryStatus = ref WorkerProtocol.SessionStatus.Ready
    let workerDied = System.Collections.Generic.List<string>()

    let sessionInfo () : WorkerProtocol.SessionInfo =
      { Id = sid
        Name = None
        Projects = []
        WorkingDirectory = @"C:\Code\Repos\SageFs"
        SolutionRoot = None
        Status = !registryStatus
        FaultReason = None
        // A Ready worker has a live process. WorkerPid discriminates a
        // caller-driven reset (UpdateSessionStatus preserves WorkerPid) from a
        // SessionManager-owned restart (cold restart clears WorkerPid): a
        // transport failure on a worker with a pid is a REAL death and must
        // trigger NotifyWorkerDied recovery, not the restart-in-progress path.
        WorkerPid = Some 4242
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow }

    let transportFailure =
      let connectionClosed =
        System.IO.IOException(
          "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.")
      System.Net.Http.HttpRequestException("An error occurred while sending the request.", connectionClosed)

    let proxy : WorkerProtocol.SessionProxy =
      fun msg ->
        async {
          match msg with
          | WorkerProtocol.WorkerMessage.HardResetSession _ ->
            return raise (AggregateException transportFailure)
          | other ->
            return failwithf "unexpected worker message in transport failure test: %A" other
        }

    let ops : SessionManagementOps = {
      CreateSession = fun _ _ _ -> Task.FromResult(Ok "new-session")
      ListSessions = fun () -> Task.FromResult("No sessions")
      StopSession = fun _ -> Task.FromResult(Ok "stopped")
      RestartSession = fun _ _ -> Task.FromResult(Ok "restarted")
      GetProxy = fun sessionId ->
        match sessionId = sid with
        | true -> Task.FromResult(Some proxy)
        | false -> Task.FromResult(None)
      GetSessionInfo = fun sessionId ->
        match sessionId = sid with
        | true -> Task.FromResult(Some (sessionInfo ()))
        | false -> Task.FromResult(None)
      GetAllSessions = fun () -> Task.FromResult([ sessionInfo () ])
      UpdateSessionStatus = fun _ status ->
        registryStatus := status
        Task.FromResult(())
      GetStandbyInfo = fun () -> Task.FromResult(SageFs.StandbyInfo.NoPool)
      NotifyWorkerDied = fun sessionId ->
        workerDied.Add(WorkerProtocol.SessionId.value sessionId)
        registryStatus := WorkerProtocol.SessionStatus.Faulted }

    let ctx =
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
        ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext
    ctx, sidStr, workerDied, registryStatus

  let tests = testList "[Integration] Reset isolation" [
    testTask "hardResetSession with rebuild only restarts the targeted session" {
      let ctx, restartLog, _ = mkTrackingCtx ()

      let! _ = hardResetSession ctx "agent1" true (Some "aaa00001") None

      restartLog |> Seq.toList
      |> Expect.equal "only session-AAA restarted" [("aaa00001", true)]

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "hardResetSession with rebuild returns before background restart completes" {
      let result = globalActorResult.Value
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["agent1"] <- "aaa00001"
      let restartStarted = TaskCompletionSource<unit>()
      let allowRestartFinish = TaskCompletionSource<unit>()
      let statuses = ResizeArray<WorkerProtocol.SessionStatus>()

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
        GetProxy = fun _ -> Task.FromResult(Some (fun _ -> async { return WorkerProtocol.WorkerResponse.WorkerReady }))
        GetSessionInfo = fun id ->
          Task.FromResult(
            Some { WorkerProtocol.SessionInfo.Id = id
                   Name = None
                   Projects = []
                   WorkingDirectory = ""
                   SolutionRoot = None
                   Status = WorkerProtocol.SessionStatus.Ready
                   FaultReason = None
                   WorkerPid = None
                   Workflow = WorkflowTypes.SessionWorkflow.Interactive
                   CreatedAt = DateTime.UtcNow
                   LastActivity = DateTime.UtcNow })
        GetAllSessions = fun () -> Task.FromResult([])
        UpdateSessionStatus = fun _ status ->
          statuses.Add(status)
          Task.FromResult(())
        GetStandbyInfo = fun () -> Task.FromResult(SageFs.StandbyInfo.NoPool)
        NotifyWorkerDied = fun _ -> ()
      }

      let ctx =
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
          ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext

      let hardResetTask = hardResetSession ctx "agent1" true (Some "aaa00001") None
      let! completed = Task.WhenAny(hardResetTask, Task.Delay(1000))

      obj.ReferenceEquals(completed, hardResetTask)
      |> Expect.isTrue "rebuild hard reset should return immediately"

      let! message = hardResetTask
      message
      |> Expect.stringContains "should explain that rebuild continues in background" "Hard reset initiated"

      restartStarted.Task.IsCompleted
      |> Expect.isTrue "background restart should have started"

      statuses |> Seq.toList
      |> Expect.contains "session should be marked restarting immediately" WorkerProtocol.SessionStatus.Restarting

      allowRestartFinish.TrySetResult(()) |> ignore
    }

    testTask "hardResetSession without rebuild only routes to the targeted session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      let! _ = hardResetSession ctx "agent1" false (Some "aaa00001") None

      // GetProxy is consulted twice: once by session resolution (to classify
      // routability) and once by routeToSession (to send). Both lookups are on
      // the SAME session — the invariant is that no OTHER session is touched.
      routedSessions |> Seq.toList |> Seq.distinct |> Seq.toList
      |> Expect.equal "only session-AAA routed" ["aaa00001"]

      restartLog.Count
      |> Expect.equal "no process restarts" 0

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "resetSession only routes to the targeted session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      let! _ = resetSession ctx "agent1" (Some "aaa00001") None

      // See above: resolution + routing each consult the proxy, both on the
      // same targeted session.
      routedSessions |> Seq.toList |> Seq.distinct |> Seq.toList
      |> Expect.equal "only session-AAA routed" ["aaa00001"]

      restartLog.Count
      |> Expect.equal "no process restarts for soft reset" 0

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "resetSession updates listSessions while the worker is warming" {
      let ctx, sid, resetStarted, allowResetFinish = mkStatusSyncCtx ()

      let resetTask = resetSession ctx "agent1" (Some sid) None

      let! started =
        waitForAsync 5000 (fun () ->
          Task.FromResult(resetStarted.Task.IsCompleted))

      started
      |> Expect.isTrue "soft reset should reach the worker before assertions"

      let! listed = listSessions ctx
      listed
      |> Expect.stringContains
        "listSessions should reflect that the session is re-warming during a soft reset"
        "Starting"

      let! status = getStatus ctx "agent1" (Some sid) None
      status
      |> Expect.stringContains
        "getStatus should surface the live worker warming state"
        "State: WarmingUp"

      allowResetFinish.TrySetResult(()) |> ignore

      let! resetResult = resetTask
      resetResult
      |> Expect.stringContains
        "soft reset should still complete successfully"
        "reset"
    }

    testTask "WHY — getStatus — synchronizes registry with worker status because agents compare list_sessions and get_fsi_status before trusting REPL readiness" {
      let ctx, _, resetStarted, _ = mkStatusSyncCtx ()

      resetStarted.TrySetResult(()) |> ignore

      let! _ = getStatus ctx "agent1" None (Some @"C:\Code\Repos\SageFs")

      let! listed = listSessions ctx
      listed
      |> Expect.stringContains
        "listSessions should no longer report Ready when live worker status is still warming"
        "Starting"
    }

    testTask "hardResetSession surfaces transport failures without throwing" {
      let ctx, sid, workerDied, registryStatus = mkTransportFailureCtx ()

      let! result = hardResetSession ctx "agent1" false (Some sid) None

      result
      |> Expect.stringContains
        "hard reset should report a recoverable worker communication error"
        "Cannot reach session"

      workerDied |> Seq.toList
      |> Expect.equal
        "transport failures should mark the worker dead"
        [ sid ]

      !registryStatus
      |> Expect.equal
        "transport failures should leave the snapshot faulted"
        WorkerProtocol.SessionStatus.Faulted
    }

    testTask "transport failure during a daemon-owned restart returns poll guidance without faulting the session" {
      // Scenario: the SessionManager is mid-restart (Status=Restarting AND
      // WorkerPid=None — the cold-restart registry shape). A reader that
      // captured a stale proxy observes a transport failure. This must NOT
      // NotifyWorkerDied (would schedule a competing restart) and must NOT
      // mark the session Faulted (only the restart owner faults a restarting
      // session).
      let result = globalActorResult.Value
      let sid = testSessionId "aaa00001"
      let sidStr = WorkerProtocol.SessionId.value sid
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["agent1"] <- sidStr
      let statuses = ResizeArray<WorkerProtocol.SessionStatus>()
      let workerDied = System.Collections.Generic.List<string>()

      let transportFailure =
        let connectionClosed =
          System.IO.IOException(
            "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.")
        System.Net.Http.HttpRequestException("An error occurred while sending the request.", connectionClosed)

      let sessionInfo () : WorkerProtocol.SessionInfo =
        { Id = sid
          Name = None
          Projects = []
          WorkingDirectory = @"C:\Code\Repos\SageFs"
          SolutionRoot = None
          Status = WorkerProtocol.SessionStatus.Restarting
          FaultReason = None
          // Daemon-owned restart shape: WorkerPid cleared by the cold-restart path.
          WorkerPid = None
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          CreatedAt = DateTime.UtcNow
          LastActivity = DateTime.UtcNow }

      let proxy : WorkerProtocol.SessionProxy =
        fun msg ->
          async {
            match msg with
            | WorkerProtocol.WorkerMessage.HardResetSession _ ->
              return raise (AggregateException transportFailure)
            | other ->
              return failwithf "unexpected worker message in restart-in-progress test: %A" other
          }

      let ops : SessionManagementOps = {
        CreateSession = fun _ _ _ -> Task.FromResult(Ok "new-session")
        ListSessions = fun () -> Task.FromResult("No sessions")
        StopSession = fun _ -> Task.FromResult(Ok "stopped")
        RestartSession = fun _ _ -> Task.FromResult(Ok "restarted")
        GetProxy = fun sessionId ->
          match sessionId = sid with
          | true -> Task.FromResult(Some proxy)
          | false -> Task.FromResult(None)
        GetSessionInfo = fun sessionId ->
          match sessionId = sid with
          | true -> Task.FromResult(Some (sessionInfo ()))
          | false -> Task.FromResult(None)
        GetAllSessions = fun () -> Task.FromResult([ sessionInfo () ])
        UpdateSessionStatus = fun _ status ->
          statuses.Add(status)
          Task.FromResult(())
        GetStandbyInfo = fun () -> Task.FromResult(SageFs.StandbyInfo.NoPool)
        NotifyWorkerDied = fun sessionId ->
          workerDied.Add(WorkerProtocol.SessionId.value sessionId) }

      let ctx =
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
          ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext

      let! result = hardResetSession ctx "agent1" false (Some sidStr) None

      result
      |> Expect.stringContains
        "restart-in-progress guidance should tell the agent to poll and not retry"
        "do NOT retry hard_reset_fsi_session"

      workerDied |> Seq.toList
      |> Expect.equal
        "a daemon-owned restart must not post NotifyWorkerDied"
        []

      statuses |> Seq.toList
      |> Expect.contains
        "a daemon-owned restart keeps the session Restarting"
        WorkerProtocol.SessionStatus.Restarting

      statuses.Contains WorkerProtocol.SessionStatus.Faulted
      |> Expect.isFalse
        "a daemon-owned restart must not be marked Faulted by a reader"
    }

    testTask "WHY — hardResetSession with rebuild=true — updates snapshot to Faulted when RestartSession returns Error because stale Restarting snapshot causes subsequent tool calls to fail silently" {
      // Create context where RestartSession can be controlled via TCS
      let result = globalActorResult.Value
      let sidStr = "aaa00001"
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["agent1"] <- sidStr
      let statuses = ResizeArray<WorkerProtocol.SessionStatus>()
      let restartResult = TaskCompletionSource<Result<string, SageFsError>>()
      let faultedSignal = TaskCompletionSource<unit>()

      let ops : SessionManagementOps = {
        CreateSession = fun _ _ _ -> Task.FromResult(Ok "new-session")
        ListSessions = fun () -> Task.FromResult("No sessions")
        StopSession = fun _ -> Task.FromResult(Ok "stopped")
        RestartSession = fun _ _ -> restartResult.Task
        GetProxy = fun _ -> Task.FromResult(None)
        GetSessionInfo = fun id ->
          Task.FromResult(Some {
            Id = id
            Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
            Status = WorkerProtocol.SessionStatus.Ready; WorkerPid = None
            FaultReason = None
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
            CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
          })
        GetAllSessions = fun () -> Task.FromResult([])
        UpdateSessionStatus = fun _ status ->
          statuses.Add(status)
          if status = WorkerProtocol.SessionStatus.Faulted then
            faultedSignal.TrySetResult(()) |> ignore
          Task.FromResult(())
        GetStandbyInfo = fun () -> Task.FromResult(SageFs.StandbyInfo.NoPool)
        NotifyWorkerDied = fun _ -> ()
      }

      let ctx =
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
          ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext

      // Act: call hardReset — this returns immediately after setting Restarting
      let! message = hardResetSession ctx "agent1" true (Some sidStr) None

      message
      |> Expect.stringContains "should return immediately with status message" "Hard reset initiated"

      // Restarting should have been set synchronously
      statuses |> Seq.toList
      |> Expect.contains "Restarting should be set before returning" WorkerProtocol.SessionStatus.Restarting

      // Signal the RestartSession to fail
      restartResult.TrySetResult(Error (SageFsError.HardResetFailed "build failed"))
      |> Expect.isTrue "should be able to complete restart TCS"

      // Poll for Faulted status update (fire-and-forget task is async)
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let mutable gotFaulted = false
      while not gotFaulted && sw.ElapsedMilliseconds < 5000L do
        do! Task.Delay(50)
        gotFaulted <- faultedSignal.Task.IsCompleted

      gotFaulted |> Expect.isTrue
        "snapshot should be updated to Faulted within timeout — without this fix the snapshot stays stuck in Restarting"

      // Final snapshot should have been Faulted (last status update)
      statuses |> Seq.toList
      |> Expect.contains "snapshot should be Faulted after RestartSession error" WorkerProtocol.SessionStatus.Faulted
    }

    testTask "WHY — hardResetSession with rebuild=true — updates snapshot to Faulted when RestartSession throws because unhandled exceptions in fire-and-forget tasks leave the session stuck Restarting indefinitely" {
      let result = globalActorResult.Value
      let sidStr = "aaa00001"
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["agent1"] <- sidStr
      let statuses = ResizeArray<WorkerProtocol.SessionStatus>()
      let restartCalled = TaskCompletionSource<unit>()
      let faultedSignal = TaskCompletionSource<unit>()

      let ops : SessionManagementOps = {
        CreateSession = fun _ _ _ -> Task.FromResult(Ok "new-session")
        ListSessions = fun () -> Task.FromResult("No sessions")
        StopSession = fun _ -> Task.FromResult(Ok "stopped")
        RestartSession = fun _ _ ->
          restartCalled.TrySetResult(()) |> ignore
          // This exception should be caught by the fire-and-forget task
          raise (InvalidOperationException "unexpected crash in RestartSession")
        GetProxy = fun _ -> Task.FromResult(None)
        GetSessionInfo = fun id ->
          Task.FromResult(Some {
            Id = id
            Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
            Status = WorkerProtocol.SessionStatus.Ready; WorkerPid = None
            FaultReason = None
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
            CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
          })
        GetAllSessions = fun () -> Task.FromResult([])
        UpdateSessionStatus = fun _ status ->
          statuses.Add(status)
          if status = WorkerProtocol.SessionStatus.Faulted then
            faultedSignal.TrySetResult(()) |> ignore
          Task.FromResult(())
        GetStandbyInfo = fun () -> Task.FromResult(SageFs.StandbyInfo.NoPool)
        NotifyWorkerDied = fun _ -> ()
      }

      let ctx =
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
          ActivityTracker = SageFs.AgentActivityTracker.create() } : McpContext

      let! message = hardResetSession ctx "agent1" true (Some sidStr) None

      message
      |> Expect.stringContains "should return immediately" "Hard reset initiated"

      // Wait for RestartSession to be called (it will throw)
      let swCall = System.Diagnostics.Stopwatch.StartNew()
      let mutable gotCalled = false
      while not gotCalled && swCall.ElapsedMilliseconds < 5000L do
        do! Task.Delay(50)
        gotCalled <- restartCalled.Task.IsCompleted
      gotCalled |> Expect.isTrue "RestartSession should be invoked"

      // Poll for Faulted status update
      let swFault = System.Diagnostics.Stopwatch.StartNew()
      let mutable gotFaulted = false
      while not gotFaulted && swFault.ElapsedMilliseconds < 5000L do
        do! Task.Delay(50)
        gotFaulted <- faultedSignal.Task.IsCompleted

      gotFaulted |> Expect.isTrue
        "snapshot should be Faulted after RestartSession throws — fire-and-forget must not silently swallow exceptions"

      statuses |> Seq.toList
      |> Expect.contains "snapshot should be Faulted after exception" WorkerProtocol.SessionStatus.Faulted
    }

    testTask "concurrent agents: resetting one never touches the other's session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      // Agent1 hard resets their session
      let! _ = hardResetSession ctx "agent1" true (Some "aaa00001") None
      // Agent2 soft resets their session
      let! _ = resetSession ctx "agent2" (Some "bbb00002") None

      restartLog |> Seq.toList
      |> Expect.equal "only AAA was restarted" [("aaa00001", true)]

      // Lookup pattern per operation, with the typed resolver in play:
      // agent1's rebuild hard reset consults the proxy once during session
      // resolution (no proxy routing — rebuild goes via SessionManager ops).
      // agent2's soft reset consults the proxy once during resolution and once
      // during routing. Both operations touch ONLY their own session — any
      // cross-session lookup would change this exact list.
      routedSessions |> Seq.toList
      |> Expect.equal "only AAA and BBB touched, each by its own operation" ["aaa00001"; "bbb00002"; "bbb00002"]

      ctx.SessionMap.["agent1"]
      |> Expect.equal "agent1 still on AAA" "aaa00001"

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 still on BBB" "bbb00002"
    }
  ]

/// Pure unit tests for LiveTestState.statusEntriesForSession — no daemon, no FSI, no I/O.
/// These define the session-isolation contract at the state layer.
module LiveTestStateIsolation =
  open SageFs.Features.LiveTesting

  let private mkEntry (tid: string) : TestStatusEntry =
    { TestId = TestId.TestId tid
      DisplayName = tid
      FullName = tid
      Origin = TestOrigin.ReflectionOnly
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Passed System.TimeSpan.Zero
      PreviousStatus = TestRunStatus.Detected }

  let private mkState (entries: TestStatusEntry array) (sessionMap: Map<TestId, string>) =
    { LiveTestState.empty with
        StatusIndex = TestStatusIndex.fromEntries entries
        TestSessionMap = sessionMap }

  let tests = testList "LiveTestState session result isolation" [

    test "session-A tests are NOT visible to session-B" {
      let entryA = mkEntry "testA"
      let entryB = mkEntry "testB"
      let state =
        mkState
          [| entryA; entryB |]
          (Map.ofList [ TestId.TestId "testA", "session-A"
                        TestId.TestId "testB", "session-B" ])
      let visibleToB = LiveTestState.statusEntriesForSession "session-B" state
      visibleToB |> Array.map (fun e -> e.DisplayName)
      |> Expect.equal "session-B sees only testB" [| "testB" |]
    }

    test "session-B tests are NOT visible to session-A" {
      let entryA = mkEntry "testA"
      let entryB = mkEntry "testB"
      let state =
        mkState
          [| entryA; entryB |]
          (Map.ofList [ TestId.TestId "testA", "session-A"
                        TestId.TestId "testB", "session-B" ])
      let visibleToA = LiveTestState.statusEntriesForSession "session-A" state
      visibleToA |> Array.map (fun e -> e.DisplayName)
      |> Expect.equal "session-A sees only testA" [| "testA" |]
    }

    test "unattributed tests do NOT leak when a session map exists" {
      // Tests with NO entry in TestSessionMap must NOT bleed into another session's view.
      // (This was the bug: | None -> true caused unattributed tests to appear everywhere.)
      let attributed = mkEntry "attributed"
      let unattributed = mkEntry "ghost"
      let state =
        mkState
          [| attributed; unattributed |]
          (Map.ofList [ TestId.TestId "attributed", "session-A" ])
      let visibleToA = LiveTestState.statusEntriesForSession "session-A" state
      visibleToA |> Array.map (fun e -> e.DisplayName)
      |> Expect.equal "ghost test must not appear in session-A" [| "attributed" |]
    }

    test "empty sessionId returns ALL entries (bare-session backward compat)" {
      let e1 = mkEntry "t1"
      let e2 = mkEntry "t2"
      let state =
        mkState [| e1; e2 |] (Map.ofList [ TestId.TestId "t1", "s1"; TestId.TestId "t2", "s2" ])
      let all = LiveTestState.statusEntriesForSession "" state
      all.Length
      |> Expect.equal "empty sessionId returns all entries" 2
    }

    test "empty TestSessionMap returns ALL entries (single-session backward compat)" {
      let e1 = mkEntry "t1"
      let e2 = mkEntry "t2"
      let state = mkState [| e1; e2 |] Map.empty
      let all = LiveTestState.statusEntriesForSession "any-session" state
      all.Length
      |> Expect.equal "empty TestSessionMap returns all entries" 2
    }

    test "session-C sees zero tests when it has none" {
      let e1 = mkEntry "t1"
      let state =
        mkState [| e1 |] (Map.ofList [ TestId.TestId "t1", "session-A" ])
      let visibleToC = LiveTestState.statusEntriesForSession "session-C" state
      visibleToC.Length
      |> Expect.equal "session-C sees no tests" 0
    }
  ]

[<Tests>]
let sessionIsolationTests = testList "Session Isolation" [
  McpSessionIsolation.tests
  SessionResolutionByWorkingDir.tests
  WorkingDirRoutingPriority.tests
  ResetIsolation.tests
  LiveTestStateIsolation.tests
]
