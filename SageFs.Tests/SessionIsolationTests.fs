module SageFs.Tests.SessionIsolationTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Tests.TestInfrastructure

/// Tests that MCP session switch does NOT leak into other clients.
/// These tests define the contract for per-client session isolation.
module McpSessionIsolation =

  /// Create a McpContext with a tracking dispatch that records all messages sent.
  let ctxWithTracking sessionId =
    let result = globalActorResult.Value
    let dispatched = System.Collections.Generic.List<SageFsMsg>()
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
        }
        ActiveSessionId = ref sessionId
        McpPort = 0
        Dispatch = Some (fun msg -> dispatched.Add(msg))
        GetElmModel = None
        GetElmRegions = None } : McpContext
    ctx, dispatched

  /// Call switchSession and return result, ignoring Marten stream errors.
  /// switchSession dispatches to Elm BEFORE appending to the event store,
  /// so dispatch tracking is valid even if the store throws.
  let switchSessionIgnoringStoreErrors ctx sessionId =
    task {
      try
        let! result = switchSession ctx sessionId
        return Ok result
      with ex ->
        // Marten may throw on concurrent stream access in tests.
        // The dispatch side-effects we care about already happened.
        return Error (ex.Message)
    }

  let tests = testSequenced <| testList "MCP session isolation" [

    testTask "switchSession updates only the given context's ActiveSessionId" {
      let ctx1, _ = ctxWithTracking "session-A"
      let ctx2, _ = ctxWithTracking "session-A"

      let! _ = switchSessionIgnoringStoreErrors ctx1 "session-B"

      !ctx1.ActiveSessionId
      |> Expect.equal "ctx1 should switch to B" "session-B"

      !ctx2.ActiveSessionId
      |> Expect.equal "ctx2 should remain on A" "session-A"
    }

    testCaseAsync "switchSession does NOT dispatch SessionSwitched to Elm" <| async {
      let ctx, dispatched = ctxWithTracking "session-A"

      let! _ = switchSessionIgnoringStoreErrors ctx "session-B" |> Async.AwaitTask

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

      let! _ = switchSessionIgnoringStoreErrors ctx "session-B" |> Async.AwaitTask

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
      let! _ = switchSession ctx "session-B"
      let! countAfter = EventStore.countEvents ctx.Store "daemon-sessions"

      countAfter - countBefore
      |> Expect.equal "should append exactly 1 event to daemon-sessions stream" 1
    }

    testTask "switchSession returns error for nonexistent session" {
      let result = globalActorResult.Value
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
          }
          ActiveSessionId = ref "session-A"
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None } : McpContext

      let! result = switchSession ctx "nonexistent"

      result
      |> Expect.stringContains "should contain error message" "not found"
    }

    testTask "two concurrent MCP connections maintain independent sessions" {
      let ctx1, _ = ctxWithTracking "session-A"
      let ctx2, _ = ctxWithTracking "session-B"

      let! _ = switchSessionIgnoringStoreErrors ctx1 "session-C"

      !ctx1.ActiveSessionId
      |> Expect.equal "ctx1 should be on C" "session-C"

      !ctx2.ActiveSessionId
      |> Expect.equal "ctx2 should still be on B" "session-B"

      let! _ = switchSessionIgnoringStoreErrors ctx2 "session-D"

      !ctx1.ActiveSessionId
      |> Expect.equal "ctx1 should still be on C" "session-C"

      !ctx2.ActiveSessionId
      |> Expect.equal "ctx2 should be on D" "session-D"
    }
  ]

[<Tests>]
let sessionIsolationTests = testList "Session Isolation" [
  McpSessionIsolation.tests
]
