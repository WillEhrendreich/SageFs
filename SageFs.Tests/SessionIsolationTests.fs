module SageFs.Tests.SessionIsolationTests

open System
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
  let ctxWithTracking sessionId =
    let result = globalActorResult.Value
    let dispatched = System.Collections.Generic.List<SageFsMsg>()
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["test"] <- sessionId
    let ctx =
      { Persistence = SageFs.EventStore.EventPersistence.noop
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
        GetWarmupContext = None } : McpContext
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

  let tests = testSequenced <| testList "[Integration] MCP session isolation" [

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
          GetWarmupContext = None } : McpContext

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
      GetElmModel = None; GetElmRegions = None; GetWarmupContext = None }

  let tests = testList "workingDirectory routing priority" [
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
        GetWarmupContext = None } : McpContext
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

[<Tests>]
let sessionIsolationTests = testList "Session Isolation" [
  McpSessionIsolation.tests
  SessionResolutionByWorkingDir.tests
  WorkingDirRoutingPriority.tests
  ResetIsolation.tests
]
