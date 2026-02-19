module SageFs.Tests.NamedPipeTransportTests

open Expecto
open SageFs
open SageFs.WorkerProtocol
open SageFs.NamedPipeTransport
open System
open System.IO
open System.Threading

let withTimeout (ms: int) f = async {
  use cts = new CancellationTokenSource(ms)
  return! f cts.Token
}

[<Tests>]
let pipeNameTests =
  testList "NamedPipeTransport.pipeName" [
    testCase "produces expected format" <| fun _ ->
      let name = pipeName "abc-123"
      Expect.equal name "sagefs-session-abc-123" "should prefix with sagefs-session-"
  ]

[<Tests>]
let framingTests =
  testList "NamedPipeTransport.framing" [
    testCase "round-trip through MemoryStream" <| fun _ ->
      let ms = new MemoryStream()
      let json = """{"type":"WorkerReady","value":{}}"""

      writeMessage ms json |> Async.RunSynchronously
      ms.Position <- 0L
      let result = readMessage ms |> Async.RunSynchronously

      Expect.equal result (Some json) "should round-trip JSON"

    testCase "empty stream returns None" <| fun _ ->
      let ms = new MemoryStream([||])
      let result = readMessage ms |> Async.RunSynchronously
      Expect.equal result None "should return None for empty stream"

    testCase "large payload round-trips" <| fun _ ->
      let ms = new MemoryStream()
      let json = String.replicate 100_000 "x"

      writeMessage ms json |> Async.RunSynchronously
      ms.Position <- 0L
      let result = readMessage ms |> Async.RunSynchronously

      Expect.equal result (Some json) "should handle large payloads"

    testCase "multiple messages round-trip" <| fun _ ->
      let ms = new MemoryStream()
      let msgs = [ "first"; "second"; "third" ]

      for m in msgs do
        writeMessage ms m |> Async.RunSynchronously

      ms.Position <- 0L
      let results =
        [ for _ in msgs do
            yield readMessage ms |> Async.RunSynchronously ]

      Expect.equal results (msgs |> List.map Some) "should read all messages in order"
  ]

[<Tests>]
let pipeIntegrationTests =
  testList "NamedPipeTransport.pipe integration" [
    testCase "client-server round-trip with WorkerMessage" <| fun _ ->
      let name = sprintf "sagefs-test-%s" (Guid.NewGuid().ToString("N").[..7])

      let handler (msg: WorkerMessage) = async {
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
        | WorkerMessage.Shutdown ->
          return WorkerResponse.WorkerShuttingDown
        | _ ->
          return WorkerResponse.WorkerError (SageFsError.EvalFailed "unexpected")
      }

      let test = async {
        use cts = new CancellationTokenSource(10_000)
        let ct = cts.Token

        // Start server in background
        let serverTask =
          Async.StartAsTask(listen name handler ct, cancellationToken = ct)

        // Give server a moment to start listening
        do! Async.Sleep 200

        // Connect client
        let! proxy, disposable = connect name ct
        use _d = disposable

        // Send GetStatus
        let rid = "test-1"
        let! resp = proxy (WorkerMessage.GetStatus rid)
        match resp with
        | WorkerResponse.StatusResult(r, status) ->
          Expect.equal r rid "replyId should match"
          Expect.equal status.Status SessionStatus.Ready "status should be Ready"
          Expect.equal status.EvalCount 42 "eval count should be 42"
        | other ->
          failwithf "unexpected response: %A" other

        // Send Shutdown
        let! resp2 = proxy WorkerMessage.Shutdown
        Expect.equal resp2 WorkerResponse.WorkerShuttingDown "should get shutdown"

        // Server should finish
        do! serverTask |> Async.AwaitTask
      }

      test |> Async.RunSynchronously

    testCase "EvalCode round-trip" <| fun _ ->
      let name = sprintf "sagefs-test-%s" (Guid.NewGuid().ToString("N").[..7])

      let handler (msg: WorkerMessage) = async {
        match msg with
        | WorkerMessage.EvalCode(code, rid) ->
          return
            WorkerResponse.EvalResult(
              rid, Ok (sprintf "val it: int = %s" code), [])
        | WorkerMessage.Shutdown ->
          return WorkerResponse.WorkerShuttingDown
        | _ ->
          return WorkerResponse.WorkerError (SageFsError.EvalFailed "unexpected")
      }

      let test = async {
        use cts = new CancellationTokenSource(10_000)
        let ct = cts.Token

        let serverTask =
          Async.StartAsTask(listen name handler ct, cancellationToken = ct)
        do! Async.Sleep 200

        let! proxy, disposable = connect name ct
        use _d = disposable

        let! resp = proxy (WorkerMessage.EvalCode("1+1", "eval-1"))
        match resp with
        | WorkerResponse.EvalResult(rid, Ok output, diags) ->
          Expect.equal rid "eval-1" "replyId should match"
          Expect.stringContains output "1+1" "output should contain code"
          Expect.isEmpty diags "no diagnostics expected"
        | other ->
          failwithf "unexpected response: %A" other

        let! _ = proxy WorkerMessage.Shutdown
        do! serverTask |> Async.AwaitTask
      }

      test |> Async.RunSynchronously

    testCase "CancelEval round-trip" <| fun _ ->
      let name = sprintf "sagefs-test-%s" (Guid.NewGuid().ToString("N").[..7])

      let handler (msg: WorkerMessage) = async {
        match msg with
        | WorkerMessage.CancelEval ->
          return WorkerResponse.EvalCancelled true
        | WorkerMessage.Shutdown ->
          return WorkerResponse.WorkerShuttingDown
        | _ ->
          return WorkerResponse.WorkerError (SageFsError.EvalFailed "unexpected")
      }

      let test = async {
        use cts = new CancellationTokenSource(10_000)
        let ct = cts.Token

        let serverTask =
          Async.StartAsTask(listen name handler ct, cancellationToken = ct)
        do! Async.Sleep 200

        let! proxy, disposable = connect name ct
        use _d = disposable

        let! resp = proxy WorkerMessage.CancelEval
        Expect.equal resp (WorkerResponse.EvalCancelled true) "should cancel"

        let! _ = proxy WorkerMessage.Shutdown
        do! serverTask |> Async.AwaitTask
      }

      test |> Async.RunSynchronously
  ]
