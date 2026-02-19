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
          AvgDurationMs = 100L
          MinDurationMs = 5L
          MaxDurationMs = 500L })
  | WorkerMessage.EvalCode(code, rid) ->
    return WorkerResponse.EvalResult(rid, Ok (sprintf "val it: string = \"%s\"" code), [])
  | WorkerMessage.CheckCode(_, rid) ->
    return WorkerResponse.CheckResult(rid, [])
  | WorkerMessage.GetCompletions(_, _, rid) ->
    return WorkerResponse.CompletionResult(rid, ["System"; "String"])
  | WorkerMessage.CancelEval ->
    return WorkerResponse.EvalCancelled true
  | WorkerMessage.LoadScript(path, rid) ->
    return WorkerResponse.ScriptLoaded(rid, Ok (sprintf "Loaded %s" path))
  | WorkerMessage.ResetSession rid ->
    return WorkerResponse.ResetResult(rid, Ok ())
  | WorkerMessage.HardResetSession(_, rid) ->
    return WorkerResponse.HardResetResult(rid, Ok "Reset complete")
  | WorkerMessage.Shutdown ->
    return WorkerResponse.WorkerShuttingDown
}

/// Slow handler that simulates a long eval — the critical test.
let slowEvalHandler (msg: WorkerMessage) : Async<WorkerResponse> = async {
  match msg with
  | WorkerMessage.EvalCode(_, rid) ->
    // Simulate a long-running eval
    do! Async.Sleep 3000
    return WorkerResponse.EvalResult(rid, Ok "done", [])
  | WorkerMessage.GetStatus rid ->
    // Status is always instant
    return
      WorkerResponse.StatusResult(
        rid,
        { Status = SessionStatus.Evaluating
          EvalCount = 1
          AvgDurationMs = 0L
          MinDurationMs = 0L
          MaxDurationMs = 0L })
  | _ -> return WorkerResponse.WorkerError (SageFsError.EvalFailed "unexpected")
}

let disposeServer (server: WorkerHttpTransport.HttpWorkerServer) =
  (server :> IDisposable).Dispose()

[<Tests>]
let httpRoundTripTests =
  testList "WorkerHttpTransport.roundTrip" [
    testTask "GetStatus round-trips through HTTP" {
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer testHandler 0
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
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer testHandler 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! resp = proxy (WorkerMessage.EvalCode("hello", "e1")) |> Async.StartAsTask
        match resp with
        | WorkerResponse.EvalResult(rid, Ok output, _) ->
          rid |> Expect.equal "replyId" "e1"
          output |> Expect.stringContains "output" "hello"
        | other -> failwithf "unexpected: %A" other
      finally
        disposeServer server
    }

    testTask "CancelEval round-trips through HTTP" {
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer testHandler 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! resp = proxy WorkerMessage.CancelEval |> Async.StartAsTask
        resp
        |> Expect.equal "cancel response" (WorkerResponse.EvalCancelled true)
      finally
        disposeServer server
    }

    testTask "Shutdown round-trips through HTTP" {
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer testHandler 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! resp = proxy WorkerMessage.Shutdown |> Async.StartAsTask
        resp
        |> Expect.equal "shutdown response" WorkerResponse.WorkerShuttingDown
      finally
        disposeServer server
    }
  ]

// ─── THE critical test: concurrent status during eval ──────────────

[<Tests>]
let concurrencyTests =
  testList "WorkerHttpTransport.concurrency" [
    testTask "GetStatus responds instantly during long eval" {
      let! (server: WorkerHttpTransport.HttpWorkerServer) = WorkerHttpTransport.startServer slowEvalHandler 0
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl

        // Start a long eval in the background
        let evalTask =
          proxy (WorkerMessage.EvalCode("slow", "eval-1"))
          |> Async.StartAsTask

        // Give it a moment to start processing
        do! Task.Delay 100

        // GetStatus should respond instantly — NOT block behind eval
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let! statusResp = proxy (WorkerMessage.GetStatus "s1") |> Async.StartAsTask
        sw.Stop()

        // Status should complete in under 1 second (eval takes 3s)
        (sw.ElapsedMilliseconds < 1000L)
        |> Expect.isTrue "status should complete in under 1 second"

        match statusResp with
        | WorkerResponse.StatusResult(rid, snap) ->
          rid |> Expect.equal "replyId" "s1"
          snap.Status |> Expect.equal "status" SessionStatus.Evaluating
        | other -> failwithf "unexpected: %A" other

        // Wait for eval to finish
        let! evalResp = evalTask |> Async.AwaitTask
        match evalResp with
        | WorkerResponse.EvalResult(_, Ok _, _) -> ()
        | other -> failwithf "eval unexpected: %A" other
      finally
        disposeServer server
    }
  ]
