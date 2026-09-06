module SageFs.Tests.StreamingProxyTests

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.HttpWorkerClient
open SageFs.Features.LiveTesting
open SageFs.Features.LiveValueTree

let private sampleTestCase (id: string) =
  { Id = TestId.TestId id
    FullName = sprintf "SageFs.Tests.Fake.``sample %s``" id
    DisplayName = "sample " + id
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private passResult (tc: TestCase) =
  { TestId = tc.Id
    TestName = tc.FullName
    Result = TestResult.Passed TimeSpan.Zero
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

[<Tests>]
let streamingProxyTests =
  testList "StreamingProxy" [

    testList "TestRunResult.synthesizeMissing" [
      testCase "no received ids -> every test is synthesized" <| fun _ ->
        let tests = [| sampleTestCase "a"; sampleTestCase "b" |]
        let missing = TestRunResult.synthesizeMissing tests Set.empty passResult
        missing.Length |> Expect.equal "all missing" 2

      testCase "received ids are excluded (no double-reporting)" <| fun _ ->
        let tests = [| sampleTestCase "a"; sampleTestCase "b"; sampleTestCase "c" |]
        let received = Set.ofList [ TestId.TestId "a"; TestId.TestId "c" ]
        let missing = TestRunResult.synthesizeMissing tests received passResult
        missing
        |> Array.map (fun r -> r.TestId)
        |> Expect.equal "only 'b' is synthesized" [| TestId.TestId "b" |]

      testCase "empty test set -> empty result" <| fun _ ->
        TestRunResult.synthesizeMissing [||] Set.empty passResult
        |> Expect.isEmpty "no tests"

      testCase "synthesized entry carries the test name for the UI" <| fun _ ->
        let missing =
          TestRunResult.synthesizeMissing [| sampleTestCase "z" |] Set.empty passResult
        missing.[0].TestName
        |> Expect.stringContains "full name preserved" "sample z"
    ]

    testList "stream outcomes against a real SSE endpoint" [
      testTask "clean stream ending with event: done reports Completed" {
        let tcp = new TcpListener(IPAddress.Loopback, 0)
        tcp.Start()
        let port = (tcp.LocalEndpoint :?> IPEndPoint).Port
        tcp.Stop()
        let listener = new HttpListener()
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
        listener.Start()
        let serve =
          async {
            try
              let! ctx = listener.GetContextAsync() |> Async.AwaitTask
              let bytes = Encoding.UTF8.GetBytes("event: done\n\n")
              ctx.Response.StatusCode <- 200
              ctx.Response.ContentType <- "text/event-stream"
              do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
              ctx.Response.OutputStream.Flush()
              ctx.Response.Close()
            with _ -> ()
          }
        Async.Start serve
        try
          let proxy = streamingTestProxy (TimeSpan.FromMilliseconds 500.0) (sprintf "http://127.0.0.1:%d" port)
          let! outcome =
            proxy [| sampleTestCase "a" |] 1 (fun _ -> ())
            |> Async.StartAsTask
          outcome |> Expect.equal "completed on event: done" StreamOutcome.Completed
        finally
          listener.Stop()
      }

      testTask "worker that stalls mid-stream reports TimedOut instead of silent success" {
        let tcp = new TcpListener(IPAddress.Loopback, 0)
        tcp.Start()
        let port = (tcp.LocalEndpoint :?> IPEndPoint).Port
        tcp.Stop()
        let listener = new HttpListener()
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
        listener.Start()
        let serve =
          async {
            try
              let! ctx = listener.GetContextAsync() |> Async.AwaitTask
              let bytes = Encoding.UTF8.GetBytes("event: start\n\ndata: {}\n\n")
              ctx.Response.StatusCode <- 200
              ctx.Response.ContentType <- "text/event-stream"
              do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
              ctx.Response.OutputStream.Flush()
              // Hold the connection open without ever sending "event: done".
              do! Async.Sleep 5000
              ctx.Response.Close()
            with _ -> ()
          }
        Async.Start serve
        try
          let proxy = streamingTestProxy (TimeSpan.FromMilliseconds 150.0) (sprintf "http://127.0.0.1:%d" port)
          let work =
            proxy [| sampleTestCase "a" |] 1 (fun _ -> ())
            |> Async.StartAsTask
          let! _ = Task.WhenAny(work, Task.Delay 5000) :> Task
          if not work.IsCompleted then
            failtest "proxy did not time out within the deadline"
          match work.Result with
          | StreamOutcome.TimedOut _ -> ()
          | other -> failtestf "expected TimedOut, got %A" other
        finally
          listener.Stop()
      }
    ]
  ]