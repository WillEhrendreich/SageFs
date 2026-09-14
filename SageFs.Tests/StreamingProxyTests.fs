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
open SageFs.Tests.SharedGenerators

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

/// Every reason a requested test can end a run without a result of its own.
let private noResultReasons =
  [| NoResultReason.StreamEnded
     NoResultReason.StreamStalled (TimeSpan.FromSeconds 30.0)
     NoResultReason.TransportFailed "connection reset by worker"
     NoResultReason.RunCancelled |]

/// The per-test status the UI shows once `results` have been folded into a
/// state where `tests` were the run's requested set, in the given run phase.
let private uiStatuses (phase: TestRunPhase) (tests: TestCase array) (results: TestRunResult array) =
  let sid = "a1b2c3d4"
  let ids = tests |> Array.map (fun t -> t.Id) |> Set.ofArray
  let state =
    { LiveTestState.empty with
        DiscoveredTests = tests
        SessionDiscovery = Map.ofList [ sid, DiscoveryProgress.Completed ]
        AffectedTests =
          match phase with
          | TestRunPhase.Idle -> Set.empty
          | TestRunPhase.Running _ | TestRunPhase.RunningButEdited _ -> ids
        RunPhases = Map.ofList [ sid, phase ] }
  LiveTesting.mergeResults state results
  |> LiveTesting.computeStatusEntriesWithHistory Map.empty
  |> Array.map (fun e -> e.TestId, e.Status)

/// A status the UI can stop spinning on: the test has a verdict or is known
/// to have no current one. Running/Queued/Detected mean "still waiting".
let private isTerminal (status: TestRunStatus) =
  match status with
  | TestRunStatus.Running | TestRunStatus.Queued | TestRunStatus.Detected -> false
  | TestRunStatus.Passed _ | TestRunStatus.Failed _ | TestRunStatus.Skipped _
  | TestRunStatus.Stale | TestRunStatus.PolicyDisabled -> true

/// Serve one SSE response on a free loopback port; `write` gets the response
/// stream and decides what (and when) the fake worker sends.
let private serveOnce (write: IO.Stream -> Async<unit>) =
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
        ctx.Response.StatusCode <- 200
        ctx.Response.ContentType <- "text/event-stream"
        do! write ctx.Response.OutputStream
        ctx.Response.Close()
      with _ -> ()
    }
  Async.Start serve
  sprintf "http://127.0.0.1:%d" port, listener

let private sendLine (stream: IO.Stream) (text: string) =
  async {
    let bytes = Encoding.UTF8.GetBytes(text)
    do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
    do! stream.FlushAsync() |> Async.AwaitTask
  }

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

      testTask "cancelling the run cancels the in-flight read instead of waiting out the read timeout" {
        let url, listener =
          serveOnce (fun stream ->
            async {
              do! sendLine stream "event: start\n\n"
              // A worker that is still busy: nothing more for a long time.
              do! Async.Sleep 10000
            })
        let run = new CancellationTokenSource()
        try
          let proxy = streamingTestProxy (TimeSpan.FromSeconds 10.0) url
          let work =
            Async.StartAsTask(proxy [| sampleTestCase "a" |] 1 ignore, cancellationToken = run.Token)
          run.CancelAfter(200)
          let! winner = Task.WhenAny(work :> Task, Task.Delay 3000)
          obj.ReferenceEquals(winner, work)
          |> Expect.isTrue "a cancelled run stops reading within the ceiling, not after the 10s read timeout"
          let! outcome = work
          outcome |> Expect.equal "the run says it was cancelled, not timed out" StreamOutcome.Cancelled
        finally
          run.Dispose()
          listener.Stop()
      }

      testTask "a slow but steady stream is not timed out — the deadline is an inactivity window" {
        let url, listener =
          serveOnce (fun stream ->
            async {
              for _ in 1 .. 6 do
                do! sendLine stream "data: {}\n\n"
                do! Async.Sleep 100
              do! sendLine stream "event: done\n\n"
            })
        try
          // 6 x 100ms = 600ms of streaming, well past a 350ms window that is
          // re-armed on every line.
          let proxy = streamingTestProxy (TimeSpan.FromMilliseconds 350.0) url
          let! outcome = proxy [| sampleTestCase "a" |] 1 ignore |> Async.StartAsTask
          outcome |> Expect.equal "steady stream completes" StreamOutcome.Completed
        finally
          listener.Stop()
      }
    ]

    testList "InactivityWindow — one deadline per run" [
      testCase "touching the window re-arms the same deadline instead of allocating a new one" <| fun _ ->
        use window = new InactivityWindow(TimeSpan.FromSeconds 30.0, CancellationToken.None)
        let before = window.Token
        window.Touch()
        window.Touch()
        window.Token |> Expect.equal "same token across touches" before
        window.State |> Expect.equal "still open" WindowState.Open

      testCase "cancelling the caller cancels the window and says so" <| fun _ ->
        use caller = new CancellationTokenSource()
        use window = new InactivityWindow(TimeSpan.FromSeconds 30.0, caller.Token)
        caller.Cancel()
        window.Token.IsCancellationRequested
        |> Expect.isTrue "the window token follows the caller's token"
        window.State |> Expect.equal "caller cancelled, not expired" WindowState.CallerCancelled

      testTask "a window nobody touches expires on its own" {
        let window = new InactivityWindow(TimeSpan.FromMilliseconds 50.0, CancellationToken.None)
        let fired = TaskCompletionSource<bool>()
        let registration = window.Token.Register(fun () -> fired.TrySetResult true |> ignore)
        try
          let! winner = Task.WhenAny(fired.Task :> Task, Task.Delay 3000)
          obj.ReferenceEquals(winner, fired.Task)
          |> Expect.isTrue "the window expired within the ceiling"
          window.State |> Expect.equal "expired" WindowState.Expired
        finally
          registration.Dispose()
          (window :> IDisposable).Dispose()
      }
    ]

    testList "every requested test ends terminal" [
      testCase "a test that never reported is shown as having no current result, never as running" <| fun _ ->
        let tests = [| sampleTestCase "a"; sampleTestCase "b" |]
        let received = [| passResult tests.[0] |]
        let missing =
          TestRunResult.neverReported
            NoResultReason.StreamEnded DateTimeOffset.UnixEpoch tests (Set.ofList [ tests.[0].Id ])
        let final = Array.append received missing
        for phase in [ TestRunPhase.Running (RunGeneration 1); TestRunPhase.Idle ] do
          uiStatuses phase tests final
          |> Map.ofArray
          |> Map.find tests.[1].Id
          |> Expect.equal (sprintf "unreported test in phase %A" phase) TestRunStatus.Stale

      testPropertyWithConfig propConfig "received tests keep their outcome; the rest are marked never-reported; all are terminal" <|
        fun (count: byte) (receivedMask: bool list) (failMask: bool list) (reasonPick: byte) ->
          let n = int count % 24
          let tests = Array.init n (fun i -> sampleTestCase (sprintf "t%d" i))
          let flag (mask: bool list) i = mask |> List.tryItem i |> Option.defaultValue false
          let received =
            tests
            |> Array.indexed
            |> Array.choose (fun (i, tc) ->
              match flag receivedMask i with
              | false -> None
              | true ->
                let outcome =
                  match flag failMask i with
                  | true -> TestResult.Failed (TestFailure.AssertionFailed "boom", TimeSpan.FromMilliseconds 2.0)
                  | false -> TestResult.Passed (TimeSpan.FromMilliseconds 1.0)
                Some { passResult tc with Result = outcome })
          let reason = noResultReasons.[int reasonPick % noResultReasons.Length]
          let receivedIds = received |> Array.map (fun r -> r.TestId) |> Set.ofArray
          let missing = TestRunResult.neverReported reason DateTimeOffset.UnixEpoch tests receivedIds
          let final = Array.append received missing
          final
          |> Array.map (fun r -> r.TestId)
          |> Array.sort
          |> Expect.equal "every requested test has exactly one result" (tests |> Array.map (fun t -> t.Id) |> Array.sort)
          for r in received do
            final
            |> Array.find (fun f -> f.TestId = r.TestId)
            |> fun f -> f.Result
            |> Expect.equal "a reported outcome is never replaced" r.Result
          for m in missing do
            m.Result |> Expect.equal "an unreported test says why it has no result" (TestResult.NoResult reason)
          for phase in [ TestRunPhase.Running (RunGeneration 1); TestRunPhase.Idle ] do
            uiStatuses phase tests final
            |> Array.filter (fun (_, s) -> not (isTerminal s))
            |> Expect.isEmpty (sprintf "no requested test is left waiting in phase %A" phase)
    ]
  ]