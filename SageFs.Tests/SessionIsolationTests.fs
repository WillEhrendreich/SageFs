module SageFs.Tests.SessionIsolationTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Tests.TestInfrastructure
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
      { Store = testStore.Value
        DiagnosticsChanged = result.DiagnosticsChanged
        StateChanged = None
        SessionOps = {
          CreateSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test-session")
          ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
          StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
          RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
          GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(None)
          GetSessionInfo = fun id ->
            if System.String.IsNullOrEmpty(id) then
              System.Threading.Tasks.Task.FromResult(None)
            else
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
        }
        SessionMap = sessionMap
        McpPort = 0
        Dispatch = Some (fun msg -> dispatched.Add(msg))
        GetElmModel = None
        GetElmRegions = None } : McpContext
    ctx, dispatched

  /// Call switchSession and return result, ignoring Marten stream errors.
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

  let tests = testSequenced <| testList "MCP session isolation" [

    testTask "switchSession updates only the given context's SessionMap for that agent" {
      let ctx1, _ = ctxWithTracking "session-A"
      let ctx2, _ = ctxWithTracking "session-A"

      let! _ = switchSessionIgnoringStoreErrors ctx1 "agent1" "session-B"

      ctx1.SessionMap.["agent1"]
      |> Expect.equal "ctx1 agent1 should switch to B" "session-B"

      ctx1.SessionMap.["test"]
      |> Expect.equal "ctx1 test agent should remain on A" "session-A"

      ctx2.SessionMap.["test"]
      |> Expect.equal "ctx2 should remain on A" "session-A"
    }

    testCaseAsync "switchSession does NOT dispatch SessionSwitched to Elm" <| async {
      let ctx, dispatched = ctxWithTracking "session-A"

      let! _ = switchSessionIgnoringStoreErrors ctx "test" "session-B" |> Async.AwaitTask

      dispatched
      |> Seq.filter (fun msg ->
        match msg with
        | SageFsMsg.Event (SageFsEvent.SessionSwitched _) -> true
        | _ -> false)
      |> Seq.length
      |> Expect.equal "switchSession should not dispatch SessionSwitched to Elm" 0
    }

    testCaseAsync "switchSession does NOT dispatch ListSessions to Elm" <| async {
      let ctx, dispatched = ctxWithTracking "session-A"

      let! _ = switchSessionIgnoringStoreErrors ctx "test" "session-B" |> Async.AwaitTask

      dispatched
      |> Seq.filter (fun msg ->
        match msg with
        | SageFsMsg.Editor EditorAction.ListSessions -> true
        | _ -> false)
      |> Seq.length
      |> Expect.equal "switchSession should not dispatch ListSessions to Elm" 0
    }

    testTask "switchSession persists DaemonSessionSwitched event to store" {
      let ctx, _ = ctxWithTracking "session-A"

      let! countBefore = EventStore.countEvents ctx.Store "daemon-sessions"
      let! _ = switchSession ctx "test" "session-B"
      let! countAfter = EventStore.countEvents ctx.Store "daemon-sessions"

      countAfter - countBefore
      |> Expect.equal "should append exactly 1 event to daemon-sessions stream" 1
    }

    testTask "switchSession returns error for nonexistent session" {
      let result = globalActorResult.Value
      let sessionMap = ConcurrentDictionary<string, string>()
      sessionMap.["test"] <- "session-A"
      let ctx =
        { Store = testStore.Value
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
          }
          SessionMap = sessionMap
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None } : McpContext

      let! result = switchSession ctx "test" "nonexistent"

      result
      |> Expect.stringContains "should contain error message" "not found"
    }

    testTask "two concurrent MCP connections maintain independent sessions" {
      let ctx1, _ = ctxWithTracking "session-A"
      let ctx2, _ = ctxWithTracking "session-B"

      let! _ = switchSessionIgnoringStoreErrors ctx1 "test" "session-C"

      ctx1.SessionMap.["test"]
      |> Expect.equal "ctx1 should be on C" "session-C"

      ctx2.SessionMap.["test"]
      |> Expect.equal "ctx2 should still be on B" "session-B"

      let! _ = switchSessionIgnoringStoreErrors ctx2 "test" "session-D"

      ctx1.SessionMap.["test"]
      |> Expect.equal "ctx1 should still be on C" "session-C"

      ctx2.SessionMap.["test"]
      |> Expect.equal "ctx2 should be on D" "session-D"
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
      let sessions = [ mkInfo "s1" @"C:\Code\Repos\Other" ]
      resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      |> Expect.isNone "no match yields None"
    }

    test "finds exact match" {
      let sessions = [
        mkInfo "s1" @"C:\Code\Repos\Other"
        mkInfo "s2" @"C:\Code\Repos\SageFs"
      ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "should find matching session"
      result.Value.Id
      |> Expect.equal "should return s2" "s2"
    }

    test "matches with trailing separator on query" {
      let sessions = [ mkInfo "s1" @"C:\Code\Repos\SageFs" ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs\"
      result |> Expect.isSome "trailing sep should match"
      result.Value.Id
      |> Expect.equal "should return s1" "s1"
    }

    test "matches case-insensitively on Windows" {
      let sessions = [ mkInfo "s1" @"C:\Code\Repos\SageFs" ]
      let result = resolveSessionByWorkingDir sessions @"c:\code\repos\sagefs"
      result |> Expect.isSome "case-insensitive match"
      result.Value.Id
      |> Expect.equal "should return s1" "s1"
    }

    test "returns first match when multiple sessions share dir" {
      let sessions = [
        mkInfo "s1" @"C:\Code\Repos\SageFs"
        mkInfo "s2" @"C:\Code\Repos\SageFs"
      ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "should find a match"
      result.Value.Id
      |> Expect.equal "should return first" "s1"
    }

    test "session trailing separator matches clean input" {
      let sessions = [ mkInfo "s1" @"C:\Code\Repos\SageFs\" ]
      let result = resolveSessionByWorkingDir sessions @"C:\Code\Repos\SageFs"
      result |> Expect.isSome "session trailing sep should match"
      result.Value.Id
      |> Expect.equal "should return s1" "s1"
    }
  ]

module WorkingDirRoutingPriority =

  open System.Threading.Tasks

  let mkInfo id workDir : WorkerProtocol.SessionInfo =
    { Id = id; Name = Some id; Projects = []
      WorkingDirectory = workDir; SolutionRoot = None
      Status = WorkerProtocol.SessionStatus.Ready
      WorkerPid = Some 1234
      CreatedAt = System.DateTime.UtcNow
      LastActivity = System.DateTime.UtcNow }

  let dummyProxy : WorkerProtocol.SessionProxy =
    fun _msg -> async { return WorkerProtocol.WorkerResponse.WorkerReady }

  let mkCtx (sessions: WorkerProtocol.SessionInfo list) (proxies: Map<string, WorkerProtocol.SessionProxy>) : McpContext =
    let sessionMap = ConcurrentDictionary<string, string>()
    { Store = Unchecked.defaultof<_>; DiagnosticsChanged = Unchecked.defaultof<_>
      StateChanged = None
      SessionOps =
        { CreateSession = fun _ _ -> Task.FromResult(Error(SageFsError.SessionCreationFailed "n/a"))
          ListSessions = fun () -> Task.FromResult("")
          StopSession = fun _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          RestartSession = fun _ _ -> Task.FromResult(Error(SageFsError.SessionNotFound "n/a"))
          GetProxy = fun sid -> Task.FromResult(Map.tryFind sid proxies)
          GetSessionInfo = fun sid -> Task.FromResult(sessions |> List.tryFind (fun s -> s.Id = sid))
          GetAllSessions = fun () -> Task.FromResult(sessions)
          GetStandbyInfo = fun () -> Task.FromResult(StandbyInfo.NoPool) }
      SessionMap = sessionMap; McpPort = 0; Dispatch = None
      GetElmModel = None; GetElmRegions = None }

  let tests = testList "workingDirectory routing priority" [
    testTask "workingDirectory should override cached session" {
      let s1 = mkInfo "sage-id" @"C:\Code\Repos\SageFs"
      let s2 = mkInfo "harmony-id" @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["sage-id",dummyProxy;"harmony-id",dummyProxy])
      setActiveSessionId ctx "mcp" "sage-id"
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "should route to Harmony based on workingDirectory" "harmony-id"
    }
    testTask "workingDirectory routes correctly when no cached session" {
      let s1 = mkInfo "sage-id" @"C:\Code\Repos\SageFs"
      let s2 = mkInfo "harmony-id" @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["sage-id",dummyProxy;"harmony-id",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "should route to Harmony via workingDirectory" "harmony-id"
    }
    testTask "explicit sessionId always wins over workingDirectory" {
      let s1 = mkInfo "sage-id" @"C:\Code\Repos\SageFs"
      let s2 = mkInfo "harmony-id" @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["sage-id",dummyProxy;"harmony-id",dummyProxy])
      let! resolved = resolveSessionId ctx "mcp" (Some "sage-id") (Some @"C:\Code\Repos\Harmony")
      resolved |> Expect.equal "explicit sessionId takes priority" "sage-id"
    }
    testTask "workingDirectory updates the cached session" {
      let s1 = mkInfo "sage-id" @"C:\Code\Repos\SageFs"
      let s2 = mkInfo "harmony-id" @"C:\Code\Repos\Harmony"
      let ctx = mkCtx [s1;s2] (Map.ofList ["sage-id",dummyProxy;"harmony-id",dummyProxy])
      setActiveSessionId ctx "mcp" "sage-id"
      let! _ = resolveSessionId ctx "mcp" None (Some @"C:\Code\Repos\Harmony")
      activeSessionId ctx "mcp" |> Expect.equal "cached session should update" "harmony-id"
    }
    testTask "falls back to cached session when workingDirectory is None" {
      let s1 = mkInfo "sage-id" @"C:\Code\Repos\SageFs"
      let ctx = mkCtx [s1] (Map.ofList ["sage-id",dummyProxy])
      setActiveSessionId ctx "mcp" "sage-id"
      let! resolved = resolveSessionId ctx "mcp" None None
      resolved |> Expect.equal "should fall back to cached when no workingDirectory" "sage-id"
    }
  ]

module ResetIsolation =

  /// Create a context with two agents on different sessions, plus tracking stubs.
  let mkTrackingCtx () =
    let result = globalActorResult.Value
    let sessionMap = ConcurrentDictionary<string, string>()
    sessionMap.["agent1"] <- "session-AAA"
    sessionMap.["agent2"] <- "session-BBB"
    let restartLog = System.Collections.Generic.List<string * bool>()
    let routedSessions = System.Collections.Generic.List<string>()
    let ops : SessionManagementOps = {
      CreateSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "new-session")
      ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
      StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
      RestartSession = fun sid rebuild ->
        restartLog.Add((sid, rebuild))
        System.Threading.Tasks.Task.FromResult(Ok "restarted")
      GetProxy = fun sid ->
        routedSessions.Add(sid)
        System.Threading.Tasks.Task.FromResult(None)
      GetSessionInfo = fun id ->
        if System.String.IsNullOrEmpty(id) then
          System.Threading.Tasks.Task.FromResult(None)
        else
          System.Threading.Tasks.Task.FromResult(
            Some { WorkerProtocol.SessionInfo.Id = id
                   Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
                   Status = WorkerProtocol.SessionStatus.Ready; WorkerPid = None
                   CreatedAt = System.DateTime.UtcNow; LastActivity = System.DateTime.UtcNow })
      GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
      GetStandbyInfo = fun () -> System.Threading.Tasks.Task.FromResult(SageFs.StandbyInfo.NoPool)
    }
    let ctx =
      { Store = testStore.Value
        DiagnosticsChanged = result.DiagnosticsChanged
        StateChanged = None
        SessionOps = ops
        SessionMap = sessionMap
        McpPort = 0
        Dispatch = None
        GetElmModel = None
        GetElmRegions = None } : McpContext
    ctx, restartLog, routedSessions

  let tests = testList "Reset isolation" [
    testTask "hardResetSession with rebuild only restarts the targeted session" {
      let ctx, restartLog, _ = mkTrackingCtx ()

      let! _ = hardResetSession ctx "agent1" true (Some "session-AAA") None

      restartLog |> Seq.toList
      |> Expect.equal "only session-AAA restarted" [("session-AAA", true)]

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "session-BBB"
    }

    testTask "hardResetSession without rebuild only routes to the targeted session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      let! _ = hardResetSession ctx "agent1" false (Some "session-AAA") None

      routedSessions |> Seq.toList
      |> Expect.equal "only session-AAA routed" ["session-AAA"]

      restartLog.Count
      |> Expect.equal "no process restarts" 0

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "session-BBB"
    }

    testTask "resetSession only routes to the targeted session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      let! _ = resetSession ctx "agent1" (Some "session-AAA") None

      routedSessions |> Seq.toList
      |> Expect.equal "only session-AAA routed" ["session-AAA"]

      restartLog.Count
      |> Expect.equal "no process restarts for soft reset" 0

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 session untouched" "session-BBB"
    }

    testTask "concurrent agents: resetting one never touches the other's session" {
      let ctx, restartLog, routedSessions = mkTrackingCtx ()

      // Agent1 hard resets their session
      let! _ = hardResetSession ctx "agent1" true (Some "session-AAA") None
      // Agent2 soft resets their session
      let! _ = resetSession ctx "agent2" (Some "session-BBB") None

      restartLog |> Seq.toList
      |> Expect.equal "only AAA was restarted" [("session-AAA", true)]

      routedSessions |> Seq.toList
      |> Expect.equal "only BBB was routed for soft reset" ["session-BBB"]

      ctx.SessionMap.["agent1"]
      |> Expect.equal "agent1 still on AAA" "session-AAA"

      ctx.SessionMap.["agent2"]
      |> Expect.equal "agent2 still on BBB" "session-BBB"
    }
  ]

[<Tests>]
let sessionIsolationTests = testList "Session Isolation" [
  McpSessionIsolation.tests
  SessionResolutionByWorkingDir.tests
  WorkingDirRoutingPriority.tests
  ResetIsolation.tests
]
