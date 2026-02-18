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

  let private mkInfo id workDir : WorkerProtocol.SessionInfo =
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

[<Tests>]
let sessionIsolationTests = testList "Session Isolation" [
  McpSessionIsolation.tests
  SessionResolutionByWorkingDir.tests
]
