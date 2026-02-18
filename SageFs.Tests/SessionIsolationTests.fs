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

  let tests = testSequenced <| testList "MCP session isolation" [

    testTask "switchSession updates only the given context's ActiveSessionId" {
      let ctx1, _ = ctxWithTracking "session-A"
      let ctx2, _ = ctxWithTracking "session-A"

      let! _ = switchSession ctx1 "session-B"

      !ctx1.ActiveSessionId
      |> Expect.equal "ctx1 should switch to B" "session-B"

      !ctx2.ActiveSessionId
      |> Expect.equal "ctx2 should remain on A" "session-A"
    }

    testTask "switchSession does NOT dispatch SessionSwitched to Elm" {
      let ctx, dispatched = ctxWithTracking "session-A"

      let! _ = switchSession ctx "session-B"

      dispatched
      |> Seq.filter (fun msg ->
        match msg with
        | SageFsMsg.Event (SageFsEvent.SessionSwitched _) -> true
        | _ -> false)
      |> Seq.toList
      |> Expect.isEmpty "switchSession should not dispatch SessionSwitched to Elm"
    }

    testTask "switchSession does NOT dispatch ListSessions to Elm" {
      let ctx, dispatched = ctxWithTracking "session-A"

      let! _ = switchSession ctx "session-B"

      dispatched
      |> Seq.filter (fun msg ->
        match msg with
        | SageFsMsg.Editor EditorAction.ListSessions -> true
        | _ -> false)
      |> Seq.toList
      |> Expect.isEmpty "switchSession should not dispatch ListSessions to Elm"
    }

    testTask "switchSession persists DaemonSessionSwitched event to store" {
      let ctx, _ = ctxWithTracking "session-A"

      let! _ = switchSession ctx "session-B"

      let! eventsWithTs = EventStore.fetchStream ctx.Store "daemon-sessions"
      let switchEvents =
        eventsWithTs
        |> List.choose (fun (_, e) ->
          match e with
          | Features.Events.SageFsEvent.DaemonSessionSwitched s -> Some s
          | _ -> None)
        |> List.filter (fun s -> s.ToId = "session-B")

      switchEvents
      |> Expect.isNonEmpty "should persist DaemonSessionSwitched to store"
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

      let! _ = switchSession ctx1 "session-C"

      !ctx1.ActiveSessionId
      |> Expect.equal "ctx1 should be on C" "session-C"

      !ctx2.ActiveSessionId
      |> Expect.equal "ctx2 should still be on B" "session-B"

      let! _ = switchSession ctx2 "session-D"

      !ctx1.ActiveSessionId
      |> Expect.equal "ctx1 should still be on C" "session-C"

      !ctx2.ActiveSessionId
      |> Expect.equal "ctx2 should be on D" "session-D"
    }
  ]

/// Tests for dashboard connection banner rendering (no Ds.show dependency).
module DashboardConnectivityRendering =
  open Falco.Markup

  let tests = testList "Dashboard connectivity rendering" [

    testCase "connection banner has no data-show attribute" <| fun _ ->
      let html = SageFs.Server.Dashboard.renderShell "1.0" |> renderNode

      html.Contains("data-show=\"!$serverConnected\"")
      |> Expect.isFalse "banner should not use Ds.show (data-show attribute)"

    testCase "connection banner has id server-status" <| fun _ ->
      let html = SageFs.Server.Dashboard.renderShell "1.0" |> renderNode

      html.Contains("id=\"server-status\"")
      |> Expect.isTrue "banner should have id=server-status"

    testCase "connection banner shows initial connecting message" <| fun _ ->
      let html = SageFs.Server.Dashboard.renderShell "1.0" |> renderNode

      html.Contains("Connecting to server")
      |> Expect.isTrue "banner should show connecting message"

    testCase "reconnection script polls daemon-info endpoint" <| fun _ ->
      let html = SageFs.Server.Dashboard.renderShell "1.0" |> renderNode

      html.Contains("/api/daemon-info")
      |> Expect.isTrue "shell should include daemon-info reconnect poller"
  ]

[<Tests>]
let sessionIsolationTests = testList "Session Isolation" [
  McpSessionIsolation.tests
  DashboardConnectivityRendering.tests
]
