module SageFs.Tests.DaemonModeProxyTests

open Expecto
open Expecto.Flip
open System
open System.Threading.Tasks
open SageFs
open SageFs.WorkerProtocol
open SageFs.Server.DaemonMode

let private pingMsg = WorkerMessage.GetStatus "test"

// ---------------------------------------------------------------------------
// Helpers — minimal stubs for testing proxyToSession callback behaviour.
// ---------------------------------------------------------------------------

let private makeThrowingProxy (ex: exn) : string -> Task<(WorkerProtocol.WorkerMessage -> Async<WorkerProtocol.WorkerResponse>) option> =
  fun _ -> task {
    return Some (fun _ -> async { return raise ex })
  }

let private makeNoneProxy () : string -> Task<(WorkerProtocol.WorkerMessage -> Async<WorkerProtocol.WorkerResponse>) option> =
  fun _ -> task { return None }

[<Tests>]
let proxyTests =
  testList "DaemonMode.proxyToSession" [

    testCaseAsync "returns WorkerCommunicationFailed on IOException" <| async {
      let notified = System.Collections.Generic.List<string>()
      let getProxy = makeThrowingProxy (IO.IOException("pipe broken"))
      let! result =
        proxyToSession getProxy (fun sid -> notified.Add(sid)) "session-1" pingMsg
        |> Async.AwaitTask
      match result with
      | Error (SageFsError.WorkerCommunicationFailed(sid, msg)) ->
        sid |> Expect.equal "session id" "session-1"
        msg.Contains("pipe broken") |> Expect.isTrue "message should contain exception text"
      | other ->
        failtest $"Expected WorkerCommunicationFailed, got {other}"
    }

    testCaseAsync "calls onWorkerDied on IOException" <| async {
      let notified = System.Collections.Generic.List<string>()
      let getProxy = makeThrowingProxy (IO.IOException("pipe broken"))
      let! _ =
        proxyToSession getProxy (fun sid -> notified.Add(sid)) "session-1" pingMsg
        |> Async.AwaitTask
      notified |> Seq.toList |> Expect.equal "should have notified" ["session-1"]
    }

    testCaseAsync "calls onWorkerDied on ObjectDisposedException" <| async {
      let notified = System.Collections.Generic.List<string>()
      let getProxy = makeThrowingProxy (ObjectDisposedException("transport"))
      let! _ =
        proxyToSession getProxy (fun sid -> notified.Add(sid)) "session-x" pingMsg
        |> Async.AwaitTask
      notified |> Seq.toList |> Expect.equal "should have notified" ["session-x"]
    }

    testCaseAsync "does NOT call onWorkerDied when proxy is None" <| async {
      let notified = System.Collections.Generic.List<string>()
      let getProxy = makeNoneProxy ()
      let! _ =
        proxyToSession getProxy (fun sid -> notified.Add(sid)) "session-1" pingMsg
        |> Async.AwaitTask
      notified |> Seq.toList |> Expect.equal "no notification when proxy missing" []
    }

    testCaseAsync "does NOT call onWorkerDied on empty session id" <| async {
      let notified = System.Collections.Generic.List<string>()
      let getProxy = makeNoneProxy ()
      let! _ =
        proxyToSession getProxy (fun sid -> notified.Add(sid)) "" pingMsg
        |> Async.AwaitTask
      notified |> Seq.toList |> Expect.equal "no notification for empty sid" []
    }

    testCaseAsync "returns WorkerCommunicationFailed on ObjectDisposedException" <| async {
      let notified = System.Collections.Generic.List<string>()
      let getProxy = makeThrowingProxy (ObjectDisposedException("channel"))
      let! result =
        proxyToSession getProxy (fun sid -> notified.Add(sid)) "sess" pingMsg
        |> Async.AwaitTask
      match result with
      | Error (SageFsError.WorkerCommunicationFailed(_, msg)) ->
        msg.Contains("channel") |> Expect.isTrue "message should contain exception text"
      | other ->
        failtest $"Expected WorkerCommunicationFailed, got {other}"
    }

    testCaseAsync "returns SessionNotFound on empty session id" <| async {
      let getProxy = makeNoneProxy ()
      let! result =
        proxyToSession getProxy (fun _ -> ()) "" pingMsg
        |> Async.AwaitTask
      match result with
      | Error (SageFsError.SessionNotFound _) -> ()
      | other -> failtest $"Expected SessionNotFound, got {other}"
    }

    // W8: AggregateException wrapping ObjectDisposedException must trigger onWorkerDied
    testCaseAsync "AggregateException(ObjectDisposedException) triggers onWorkerDied" <| async {
      let notified = System.Collections.Generic.List<string>()
      let aggExn = AggregateException(ObjectDisposedException("mock channel"))
      let getProxy = makeThrowingProxy aggExn
      let! result =
        proxyToSession getProxy (fun sid -> notified.Add(sid)) "sess-disposed" pingMsg
        |> Async.AwaitTask
      notified.Count |> Expect.equal "onWorkerDied must be called once" 1
      notified.[0] |> Expect.equal "called with correct session id" "sess-disposed"
      match result with
      | Error (SageFsError.WorkerCommunicationFailed _) -> ()
      | other -> failtest $"Expected WorkerCommunicationFailed, got {other}"
    }

    testCaseAsync "AggregateException(ObjectDisposedException) returns pipe-closed message" <| async {
      let aggExn = AggregateException(ObjectDisposedException("proxy socket"))
      let getProxy = makeThrowingProxy aggExn
      let! result =
        proxyToSession getProxy (fun _ -> ()) "sess2" pingMsg
        |> Async.AwaitTask
      match result with
      | Error (SageFsError.WorkerCommunicationFailed(_, msg)) ->
        (msg.ToLowerInvariant().Contains("closed") || msg.ToLowerInvariant().Contains("proxy socket"))
        |> Expect.isTrue $"message should describe disposal, got: '{msg}'"
      | other -> failtest $"Expected WorkerCommunicationFailed, got {other}"
    }
  ]

// ---------------------------------------------------------------------------
// listenerBindFailureOf — the pure classification behind the startup fix:
// a required listener (MCP and/or dashboard) that never stays up must fail
// the daemon closed rather than let it announce "ready" (roast-class flake:
// a taken dashboard port logged "Dashboard failed to start" then "SageFs
// daemon ready" anyway). See DaemonIntegrationTests.fs "Daemon startup fails
// closed" for the real-process proof.
// ---------------------------------------------------------------------------

[<Tests>]
let listenerBindFailureTests =
  testList "DaemonMode.listenerBindFailureOf" [

    testCase "neither listener completing early is healthy — None" <| fun _ ->
      listenerBindFailureOf false false
      |> Expect.isNone "both host tasks are still running, as they should be"

    testCase "only the MCP task completing early names the MCP listener" <| fun _ ->
      listenerBindFailureOf true false
      |> Expect.equal "MCP bind failed" (Some ListenerBindFailure.Mcp)

    testCase "only the dashboard task completing early names the dashboard listener" <| fun _ ->
      listenerBindFailureOf false true
      |> Expect.equal "dashboard bind failed" (Some ListenerBindFailure.Dashboard)

    testCase "both tasks completing early names both listeners" <| fun _ ->
      listenerBindFailureOf true true
      |> Expect.equal "both binds failed" (Some ListenerBindFailure.Both)

    testCase "describeListenerBindFailure names every case" <| fun _ ->
      describeListenerBindFailure ListenerBindFailure.Mcp
      |> Expect.equal "names the MCP listener" "the MCP listener"
      describeListenerBindFailure ListenerBindFailure.Dashboard
      |> Expect.equal "names the dashboard listener" "the dashboard listener"
      describeListenerBindFailure ListenerBindFailure.Both
      |> Expect.equal "names both listeners" "the MCP and dashboard listeners"
  ]
