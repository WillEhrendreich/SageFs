module SageFs.Tests.WorkerHttpTransportTests

open System
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol


// ─── Route mapping tests ───────────────────────────────────────────

[<Tests>]
let routeMappingTests =
  testList "WorkerHttpTransport.routeMapping" [
    testCase "GetStatus maps to GET /status" <| fun _ ->
      let method, path, _ =
        WorkerHttpTransport.toRoute (WorkerMessage.GetStatus "r1")
      method |> Expect.equal "method" "GET"
      path |> Expect.stringStarts "path" "/status"

    testCase "EvalCode maps to POST /eval" <| fun _ ->
      let method, path, body =
        WorkerHttpTransport.toRoute (WorkerMessage.EvalCode("1+1", "e1"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/eval"
      body |> Expect.isSome "should have body"

    testCase "CheckCode maps to POST /check" <| fun _ ->
      let method, path, _ =
        WorkerHttpTransport.toRoute (WorkerMessage.CheckCode("let x = 1", "c1"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/check"

    testCase "GetCompletions maps to POST /completions" <| fun _ ->
      let method, path, _ =
        WorkerHttpTransport.toRoute (WorkerMessage.GetCompletions("Sys", 3, "comp1"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/completions"

    testCase "CancelEval maps to POST /cancel" <| fun _ ->
      let method, path, body =
        WorkerHttpTransport.toRoute WorkerMessage.CancelEval
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/cancel"
      body |> Expect.isNone "cancel has no body"

    testCase "LoadScript maps to POST /load-script" <| fun _ ->
      let method, path, _ =
        WorkerHttpTransport.toRoute (WorkerMessage.LoadScript("test.fsx", "ls1"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/load-script"

    testCase "ResetSession maps to POST /reset" <| fun _ ->
      let method, path, _ =
        WorkerHttpTransport.toRoute (WorkerMessage.ResetSession "rs1")
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/reset"

    testCase "HardResetSession maps to POST /hard-reset" <| fun _ ->
      let method, path, _ =
        WorkerHttpTransport.toRoute (WorkerMessage.HardResetSession(true, "hr1"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/hard-reset"

    testCase "SetSaveSource maps to POST /save-source" <| fun _ ->
      let method, path, body =
        WorkerHttpTransport.toRoute (WorkerMessage.SetSaveSource(SageFs.Features.TrunkFollow.SaveSource.LandedOnly, "ss1"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/save-source"
      body |> Expect.isSome "the source travels in the body"

    testCase "ApplySaves maps to POST /apply-saves" <| fun _ ->
      let method, path, body =
        WorkerHttpTransport.toRoute (WorkerMessage.ApplySaves([], "as1"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/apply-saves"
      body |> Expect.isSome "the files travel in the body"

    testCase "Shutdown maps to POST /shutdown" <| fun _ ->
      let method, path, body =
        WorkerHttpTransport.toRoute WorkerMessage.Shutdown
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/shutdown"
      body |> Expect.isNone "shutdown has no body"
  ]

// ─── HTTP round-trip tests ─────────────────────────────────────────

let testHandler (msg: WorkerMessage) : Async<WorkerResponse> = async {
  match msg with
  | WorkerMessage.GetStatus rid ->
    return
      WorkerResponse.StatusResult(
        rid,
        { Status = SessionStatus.Ready
          EvalCount = 42
          AvgDurationMs = FixtureDurations.evalAvgMs
          MinDurationMs = FixtureDurations.evalMinMs
          MaxDurationMs = FixtureDurations.evalMaxMs; Projects = []
          StatusMessage = None
          CoreVersion = "0.0.0-test" })
  | WorkerMessage.EvalCode(code, rid) ->
    return WorkerResponse.EvalResult(rid, Ok (sprintf "val it: string = \"%s\"" code), [], Map.empty)
  | WorkerMessage.CheckCode(_, rid) ->
    return WorkerResponse.CheckResult(rid, [])
  | WorkerMessage.TypeCheckWithSymbols(_, _, rid) ->
    return WorkerResponse.TypeCheckWithSymbolsResult(rid, [], [])
  | WorkerMessage.GetCompletions(_, _, rid) ->
    return WorkerResponse.CompletionResult(rid, ["System"; "String"])
  | WorkerMessage.CancelEval ->
    return WorkerResponse.EvalCancelled true
  | WorkerMessage.RunApp(_, _, rid)
  | WorkerMessage.StopApp(_, rid)
  | WorkerMessage.AwaitAppChange(_, rid) ->
    return WorkerResponse.AppRunResult(rid, Ok AppRun.AppRunState.NotRunning)
  | WorkerMessage.LoadScript(path, rid) ->
    return WorkerResponse.ScriptLoaded(rid, Ok (sprintf "Loaded %s" path))
  | WorkerMessage.ResetSession rid ->
    return WorkerResponse.ResetResult(rid, Ok ())
  | WorkerMessage.HardResetSession(_, rid) ->
    return WorkerResponse.HardResetResult(rid, Ok "Reset complete")
  | WorkerMessage.RunTests(_, _, rid) ->
    return WorkerResponse.TestRunResults(rid, [||])
  | WorkerMessage.GetTestDiscovery rid ->
    return WorkerResponse.InitialTestDiscovery([||], [])
  | WorkerMessage.EvalLiveTestFile(_, _, rid) ->
    return WorkerResponse.EvalLiveTestFileResult(rid, Ok ([||], []))
  | WorkerMessage.GetInstrumentationMaps rid ->
    return WorkerResponse.InstrumentationMapsResult(rid, [||])
  | WorkerMessage.GetLiveValues rid ->
    return WorkerResponse.LiveValuesResult(rid, "{}")
  | WorkerMessage.DebugTestBegin(_, rid)
  | WorkerMessage.DebugTestContinue(_, _, rid) ->
    return WorkerResponse.DebugTestAnswer(rid, "{}")
  | WorkerMessage.SetValueWalk(_, rid) ->
    return WorkerResponse.LiveValuesResult(rid, "{}")
  | WorkerMessage.EvaluateLiveMember(_, _, rid) ->
    return WorkerResponse.LiveMemberResult(rid, "{}")
  | WorkerMessage.SetSaveSource(source, rid) ->
    return WorkerResponse.SaveSourceSet(rid, source)
  | WorkerMessage.ApplySaves(_, rid) ->
    return WorkerResponse.SavesApplied(rid, SageFs.Features.TrunkFollow.SessionOutcome.Delivered [])
  | WorkerMessage.Shutdown ->
    return WorkerResponse.WorkerShuttingDown
}

/// Handler that simulates a long eval — the critical test. The eval says when it has started and then
/// stays in flight until the test releases it, so "a status read answered while the eval was running" is
/// a fact about the order of events and not about how many milliseconds either took.
let gatedEvalHandler (evalStarted: TaskCompletionSource<unit>) (release: Task) (msg: WorkerMessage) : Async<WorkerResponse> = async {
  match msg with
  | WorkerMessage.EvalCode(_, rid) ->
    evalStarted.TrySetResult() |> ignore
    do! release |> Async.AwaitTask
    return WorkerResponse.EvalResult(rid, Ok "done", [], Map.empty)
  | WorkerMessage.GetStatus rid ->
    // Status is always instant
    return
      WorkerResponse.StatusResult(
        rid,
        { Status = SessionStatus.Evaluating
          EvalCount = 1
          AvgDurationMs = FixtureDurations.unmeasuredMs
          MinDurationMs = FixtureDurations.unmeasuredMs
          MaxDurationMs = FixtureDurations.unmeasuredMs; Projects = []
          StatusMessage = None
          CoreVersion = "0.0.0-test" })
  | _ -> return WorkerResponse.WorkerError (SageFsError.EvalFailed "unexpected")
}

let disposeServer (server: WorkerHttpTransport.HttpWorkerServer) =
  (server :> IDisposable).Dispose()

[<Tests>]
let httpRoundTripTests =
  testList "WorkerHttpTransport.roundTrip" [
    testTask "GetStatus round-trips through HTTP" {
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer testHandler (ref HotReloadState.empty) SageFs.Features.KeptState.Access.none [] (fun () -> WarmupContext.empty) (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun }) (fun () -> SageFs.HostAgent.AgentAnswered SageFs.HostAgent.NoCoverage) 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! resp = proxy (WorkerMessage.GetStatus "s1") |> Async.StartAsTask
        match resp with
        | WorkerResponse.StatusResult(rid, snap) ->
          rid |> Expect.equal "replyId" "s1"
          snap.Status |> Expect.equal "status" SessionStatus.Ready
          snap.EvalCount |> Expect.equal "eval count" 42
        | other -> failwithf "unexpected: %A" other
      finally
        disposeServer server
    }

    testTask "EvalCode round-trips through HTTP" {
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer testHandler (ref HotReloadState.empty) SageFs.Features.KeptState.Access.none [] (fun () -> WarmupContext.empty) (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun }) (fun () -> SageFs.HostAgent.AgentAnswered SageFs.HostAgent.NoCoverage) 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! resp = proxy (WorkerMessage.EvalCode("hello", "e1")) |> Async.StartAsTask
        match resp with
        | WorkerResponse.EvalResult(rid, Ok output, _, _) ->
          rid |> Expect.equal "replyId" "e1"
          output |> Expect.stringContains "output" "hello"
        | other -> failwithf "unexpected: %A" other
      finally
        disposeServer server
    }

    testTask "CancelEval round-trips through HTTP" {
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer testHandler (ref HotReloadState.empty) SageFs.Features.KeptState.Access.none [] (fun () -> WarmupContext.empty) (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun }) (fun () -> SageFs.HostAgent.AgentAnswered SageFs.HostAgent.NoCoverage) 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! resp = proxy WorkerMessage.CancelEval |> Async.StartAsTask
        resp
        |> Expect.equal "cancel response" (WorkerResponse.EvalCancelled true)
      finally
        disposeServer server
    }

    testTask "Shutdown acknowledges before invoking shutdown callback" {
      let callbackCalled = ref false
      let! (server: WorkerHttpTransport.HttpWorkerServer) =
        WorkerHttpTransport.startServerWithShutdown
          (fun () -> callbackCalled.Value <- true)
          testHandler (ref HotReloadState.empty) SageFs.Features.KeptState.Access.none [] (fun () -> WarmupContext.empty) (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun }) (fun () -> SageFs.HostAgent.AgentAnswered SageFs.HostAgent.NoCoverage) 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! resp = proxy WorkerMessage.Shutdown |> Async.StartAsTask
        resp
        |> Expect.equal "shutdown response precedes callback" WorkerResponse.WorkerShuttingDown
        callbackCalled.Value
        |> Expect.isTrue "shutdown callback runs after response"
      finally
        disposeServer server
    }

  ]

// ─── THE critical test: concurrent status during eval ──────────────

[<Tests>]
let concurrencyTests =
  testList "WorkerHttpTransport.concurrency" [
    testTask "GetStatus responds instantly during long eval" {
      let evalStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer (gatedEvalHandler evalStarted release.Task) (ref HotReloadState.empty) SageFs.Features.KeptState.Access.none [] (fun () -> WarmupContext.empty) (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun }) (fun () -> SageFs.HostAgent.AgentAnswered SageFs.HostAgent.NoCoverage) 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl

        // Start a long eval in the background; it stays in flight until this test releases it
        let evalTask =
          proxy (WorkerMessage.EvalCode("slow", "eval-1"))
          |> Async.StartAsTask

        // The server says when the eval is running, so no guess at how long it takes to get there
        do! evalStarted.Task.WaitAsync TestTimeouts.patience

        // GetStatus answers while the eval is still in flight: the eval cannot finish before the release below
        // (A server that queued the status behind the eval would never answer it, because the release
        // comes after: the ceiling turns that into a failure instead of a hang.)
        let! statusResp = (proxy (WorkerMessage.GetStatus "s1") |> Async.StartAsTask).WaitAsync TestTimeouts.patience
        evalTask.IsCompleted
        |> Expect.isFalse "the status was answered while the eval was still running"

        match statusResp with
        | WorkerResponse.StatusResult(rid, snap) ->
          rid |> Expect.equal "replyId" "s1"
          snap.Status |> Expect.equal "status" SessionStatus.Evaluating
        | other -> failwithf "unexpected: %A" other

        // Let the eval finish and say so, so nothing is left running behind the test
        release.TrySetResult() |> ignore
        let! evalReply = evalTask.WaitAsync TestTimeouts.patience
        match evalReply with
        | WorkerResponse.EvalResult(rid, _, _, _) -> rid |> Expect.equal "the eval's own replyId" "eval-1"
        | other -> failwithf "unexpected eval reply: %A" other
      finally
        release.TrySetResult() |> ignore
        disposeServer server
    }
  ] |> testSequenced
