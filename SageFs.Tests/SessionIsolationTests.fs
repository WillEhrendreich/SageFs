module SageFs.Tests.SessionIsolationTests

open System
open System.IO
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
      { Persistence = inMemoryPersistence ()
        DiagnosticsChanged = result.DiagnosticsChanged
        StateChanged = None
        SessionOps = {
          CreateSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test-session")
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
                     WorkerPid = None
                     CreatedAt = System.DateTime.UtcNow
                     LastActivity = System.DateTime.UtcNow })
          GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
          GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool)
          NotifyWorkerDied = fun _ -> ()
        }
        SessionMap = sessionMap
        McpPort = 0
        Dispatch = Some (fun msg -> dispatched.Add(msg))
        GetElmModel = None
        GetElmRegions = None
        GetWarmupContext = None
        GetFeatureState = None } : McpContext
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

    testTask "switchSession persists DaemonSessionSwitched event to store" {
      let ctx, _ = ctxWithTracking "aaaaaa01"

      let! countBefore = ctx.Persistence.CountEvents "daemon-sessions"
      let! _ = switchSession ctx "test" "bbbbbb02"
      let! countAfter = ctx.Persistence.CountEvents "daemon-sessions"

      countAfter - countBefore
      |> Expect.equal "should append exactly 1 event to daemon-sessions stream" 1
    }

    testTask "switchSession returns error for nonexistent session" {
      let result = globalActorResult.Value
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["test"] <- "aaaaaa01"
      let ctx =
        { Persistence = SageFs.EventStore.EventPersistence.noop
          DiagnosticsChanged = result.DiagnosticsChanged
          StateChanged = None
          SessionOps = {
            CreateSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test")
            ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
            StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
            RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
            GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(None)
            GetSessionInfo = fun _ -> System.Threading.Tasks.Task.FromResult(None)
            GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
            GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool)
            NotifyWorkerDied = fun _ -> () }
          SessionMap = sessionMap
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None
          GetWarmupContext = None
          GetFeatureState = None } : McpContext

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
      WorkerPid = None
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

module WorkingDirRoutingPriority =

  open System.Threading.Tasks

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = Some (WorkerProtocol.SessionId.value id); Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionStatus.Ready
      WorkerPid = Some 1234
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow }

  let dummyProxy : WorkerProtocol.SessionProxy =
    fun _msg -> async { return WorkerProtocol.WorkerResponse.WorkerReady }

  let mkCtx (sessions: WorkerProtocol.SessionInfo list) (proxies: Map<string, WorkerProtocol.SessionProxy>) : McpContext =
    let sessionMap = ConcurrentDictionary<string, string>()
    let stubPersistence : SageFs.EventStore.EventPersistence = {
      AppendEvents = fun _ _ -> Task.FromResult(Ok ())
      FetchStream = fun _ -> Task.FromResult([])
      CountEvents = fun _ -> Task.FromResult(0)
    }
    { Persistence = stubPersistence; DiagnosticsChanged = Unchecked.defaultof<_>
      StateChanged = None
      SessionOps =
        { CreateSession = fun _ _ -> Task.FromResult(Error(SageFsError.SessionCreationFailed "n/a"))
          ListSessions = fun () -> Task.FromResult("")
          StopSession = fun _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          RestartSession = fun _ _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          GetProxy = fun sid -> Task.FromResult(Map.tryFind (WorkerProtocol.SessionId.value sid) proxies)
          GetSessionInfo = fun sid -> Task.FromResult(sessions |> List.tryFind (fun s -> s.Id = sid))
          GetAllSessions = fun () -> Task.FromResult(sessions)
          GetStandbyInfo = fun () -> Task.FromResult(StandbyInfo.NoPool)
          NotifyWorkerDied = fun _ -> () }
      SessionMap = sessionMap; McpPort = 0; Dispatch = None
      GetElmModel = None; GetElmRegions = None; GetWarmupContext = None
      GetFeatureState = None }

  let tests = testSequenced <| testList "workingDirectory routing priority" [
    testTask "workingDirectory should override cached session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "should route to Harmony based on workingDirectory" (Ok "4a120002")
    }
    testTask "workingDirectory routes correctly when no cached session" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "should route to Harmony via workingDirectory" (Ok "4a120002")
    }
    testTask "workingDirectory returns an ambiguity error when multiple sessions share the same directory" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1; s2] (Map.ofList ["5a6e0001",dummyProxy; "4a120002",dummyProxy])
      setActiveSessionId ctx "mcp" "5a6e0001"
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\SageFs")
      match resolved with
      | Ok sid ->
        failtestf "expected workingDirectory ambiguity error but resolved '%s'" sid
      | Error msg ->
        msg |> Expect.stringContains "should describe the ambiguity" "Multiple sessions match workingDirectory"
        msg |> Expect.stringContains "should list the first matching session" "5a6e0001"
        msg |> Expect.stringContains "should list the second matching session" "4a120002"
        activeSessionId ctx "mcp"
        |> Expect.equal "cached session should remain unchanged after ambiguity" "5a6e0001"
    }
    testTask "explicit sessionId always wins over workingDirectory" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let s2 = mkInfo (testSessionId "4a120002") @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["5a6e0001",dummyProxy;"4a120002",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" (Some "5a6e0001") (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "explicit sessionId takes priority" (Ok "5a6e0001")
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
      resolved |> Expect.equal "should fall back to cached when no workingDirectory" (Ok "5a6e0001")
    }
    testTask "falls back to the only session when no active session is cached" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["5a6e0001",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None None
      resolved |> Expect.equal "single session should be used automatically" (Ok "5a6e0001")
      activeSessionId ctx "mcp" |> Expect.equal "single session should become cached" "5a6e0001"
    }
    testTask "falls back to the only session when workingDirectory does not match" {
      let s1 = mkInfo (testSessionId "5a6e0001") @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["5a6e0001",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Other")
      resolved |> Expect.equal "single session should still be used" (Ok "5a6e0001")
      activeSessionId ctx "mcp" |> Expect.equal "single session should become cached" "5a6e0001"
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
        resolved |> Expect.equal "current directory session should be selected" (Ok "5a6e0001")
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
        | Ok sid ->
          failtestf "expected current directory ambiguity error but resolved '%s'" sid
        | Error msg ->
          msg |> Expect.stringContains "should describe the ambiguity" "Multiple sessions match the current working directory"
          msg |> Expect.stringContains "should list the first matching session" "5a6e0001"
          msg |> Expect.stringContains "should list the second matching session" "4a120002"
          activeSessionId ctx "mcp"
          |> Expect.equal "cache should remain empty after ambiguity" ""
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
      CreateSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "new-session")
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
                 CreatedAt = System.DateTime.UtcNow; LastActivity = System.DateTime.UtcNow })
      GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
      GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool)
      NotifyWorkerDied = fun _ -> ()
    }
    let ctx =
      { Persistence = SageFs.EventStore.EventPersistence.noop
        DiagnosticsChanged = result.DiagnosticsChanged
        StateChanged = None
        SessionOps = ops
        SessionMap = sessionMap
        McpPort = 0
        Dispatch = None
        GetElmModel = None
        GetElmRegions = None
        GetWarmupContext = None
        GetFeatureState = None } : McpContext
    ctx, restartLog, routedSessions

  let tests = testList "[Integration] Reset isolation" [
    testTask "hardResetSession with rebuild only restarts the targeted session" {
      let ctx, restartLog, _ = mkTrackingCtx ()

      let! _ = hardResetSession ctx "agent1" true (Some "aaa00001") None

      restartLog |> Seq.toList
      |> Expect.equal "only session-AAA restarted" [("aaa00001", true)]

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "hardResetSession without rebuild only routes to the targeted session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      let! _ = hardResetSession ctx "agent1" false (Some "aaa00001") None

      routedSessions |> Seq.toList
      |> Expect.equal "only session-AAA routed" ["aaa00001"]

      restartLog.Count
      |> Expect.equal "no process restarts" 0

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "resetSession only routes to the targeted session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      let! _ = resetSession ctx "agent1" (Some "aaa00001") None

      routedSessions |> Seq.toList
      |> Expect.equal "only session-AAA routed" ["aaa00001"]

      restartLog.Count
      |> Expect.equal "no process restarts for soft reset" 0

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "bbb00002"
    }

    testTask "concurrent agents: resetting one never touches the other's session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      // Agent1 hard resets their session
      let! _ = hardResetSession ctx "agent1" true (Some "aaa00001") None
      // Agent2 soft resets their session
      let! _ = resetSession ctx "agent2" (Some "bbb00002") None

      restartLog |> Seq.toList
      |> Expect.equal "only AAA was restarted" [("aaa00001", true)]

      routedSessions |> Seq.toList
      |> Expect.equal "only BBB was routed for soft reset" ["bbb00002"]

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
        StatusEntries = entries
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
