module SageFs.Tests.LiveTestingCoverageStreamTests

open System
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Server
open SageFs.WorkerProtocol

/// Per-test coverage on the wire and at the daemon's edge: the worker takes a reading right after
/// each test (so a reading belongs to ONE test), the stream says which test it belongs to, the
/// daemon client hands it over by test, and the effect handler records it per test.

let private mkTest (name: string) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private first = mkTest "Sample.Tests.first"
let private second = mkTest "Sample.Tests.second"
let private third = mkTest "Sample.Tests.third"

/// Four probes. Each test hits its own, `first` also hits probe 3 (shared startup code).
let private probesOf (tc: TestCase) : int list =
  match tc.FullName with
  | n when n = first.FullName -> [ 0; 3 ]
  | n when n = second.FullName -> [ 1 ]
  | _ -> [ 2 ]

let private probeCount = 4

let private bitmapOf (hit: int list) : CoverageBitmap =
  CoverageBitmap.ofBoolArray (Array.init probeCount (fun i -> List.contains i hit))

let private passed (tc: TestCase) : TestRunResult =
  { TestId = tc.Id
    TestName = tc.FullName
    Result = TestResult.Passed FixtureDurations.quickResult
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

/// Instrumented assemblies in miniature: tests set probes in one shared array, and taking coverage
/// reads the array and clears it, exactly as `HostAgent.TakeCoverage` does over the real probes.
type private FakeProbes() =
  let hits = Array.zeroCreate<bool> probeCount
  let gate = obj ()
  let mutable running = 0
  let mutable mostRunningAtOnce = 0
  member _.MostRunningAtOnce = mostRunningAtOnce

  /// Run one test: it overlaps with whatever else is running for a measurable moment, then sets its probes.
  member _.RunTest(tc: TestCase) : Async<TestResult> =
    async {
      lock gate (fun () ->
        running <- running + 1
        mostRunningAtOnce <- max mostRunningAtOnce running)
      do! Async.Sleep TestTimeouts.settle
      lock gate (fun () ->
        for p in probesOf tc do hits.[p] <- true
        running <- running - 1)
      return TestResult.Passed FixtureDurations.quickResult
    }

  member _.TakeCoverage() : HostAgent.AgentReply<HostAgent.CoverageReading> =
    lock gate (fun () ->
      let reading = CoverageBitmap.ofBoolArray (Array.copy hits)
      Array.fill hits 0 hits.Length false
      HostAgent.AgentAnswered(HostAgent.CoverageTaken(reading.Count, CoverageBitmap.toBase64 reading)))

/// One SSE frame of the worker's run stream.
type private Frame =
  | ResultFrame of testId: string
  | CoverageFrame of testId: string * bitmap: CoverageBitmap
  | DoneFrame

/// The wire writes a test id as `{"type":"TestId","value":["..."]}` in every frame that names a test.
let private idOf (root: JsonElement) : string =
  root.GetProperty("testId").GetProperty("value").[0].GetString()

let private parseFrames (body: string) : Frame list =
  body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
  |> Array.toList
  |> List.choose (fun block ->
    let lines = block.Split('\n') |> Array.map (fun l -> l.TrimEnd('\r'))
    let data = lines |> Array.tryPick (fun l -> match l.StartsWith("data: ") with | true -> Some(l.Substring 6) | false -> None)
    match lines |> Array.exists (fun l -> l = "event: done"), lines |> Array.exists (fun l -> l = "event: coverage"), data with
    | true, _, _ -> Some DoneFrame
    | false, true, Some json ->
      use doc = JsonDocument.Parse json
      let root = doc.RootElement
      match root.TryGetProperty "testId" with
      | true, _ ->
        Some(CoverageFrame(idOf root, CoverageBitmap.ofBase64 (root.GetProperty("count").GetInt32()) (root.GetProperty("words").GetString())))
      | false, _ -> Some(CoverageFrame("", CoverageBitmap.ofBase64 (root.GetProperty("count").GetInt32()) (root.GetProperty("words").GetString())))
    | false, false, Some "{}" -> None
    | false, false, Some json ->
      use doc = JsonDocument.Parse json
      Some(ResultFrame(idOf doc.RootElement))
    | _ -> None)

let private runStream (probes: FakeProbes) (takeCoverage: unit -> HostAgent.AgentReply<HostAgent.CoverageReading>) (tests: TestCase array) (maxParallelism: int) : Task<Frame list * int> =
  task {
    let! server =
      WorkerHttpTransport.startServer
        (fun _ -> async { return WorkerResponse.WorkerShuttingDown })
        (ref HotReloadState.empty)
        Features.KeptState.Access.none
        []
        (fun () -> WarmupContext.empty)
        (fun () -> probes.RunTest)
        takeCoverage
        0
    use _server = server
    use client = new HttpClient(BaseAddress = Uri server.BaseUrl, Timeout = TestTimeouts.requestPatience)
    let body = Serialization.serialize {| tests = tests; maxParallelism = maxParallelism |}
    use content = new StringContent(body, Encoding.UTF8, "application/json")
    let! response = client.PostAsync("/run-tests-stream", content)
    let! text = response.Content.ReadAsStringAsync()
    return parseFrames text, probes.MostRunningAtOnce
  }

[<Tests>]
let transportTests =
  testList "per-test coverage: the worker's run stream" [

    testTask "each test's coverage arrives right after its own result and holds only its own probes" {
      let probes = FakeProbes()
      let! frames, _ = runStream probes probes.TakeCoverage [| first; second; third |] 4
      let coverage =
        frames
        |> List.choose (function CoverageFrame(id, bitmap) -> Some(id, bitmap) | _ -> None)
        |> Map.ofList
      coverage.Count |> Expect.equal "one coverage reading per test" 3
      for tc in [ first; second; third ] do
        coverage
        |> Map.tryFind (TestId.value tc.Id)
        |> Option.map (CoverageBitmap.equivalent (bitmapOf (probesOf tc)))
        |> Expect.equal (sprintf "%s's reading holds exactly its own probes" tc.DisplayName) (Some true)
      // Order: a reading follows the result of the test it belongs to, before the next test's result.
      let order =
        frames
        |> List.choose (function
          | ResultFrame id -> Some("result", id)
          | CoverageFrame(id, _) -> Some("coverage", id)
          | DoneFrame -> None)
      order
      |> List.chunkBySize 2
      |> List.iter (fun pair ->
        match pair with
        | [ ("result", a); ("coverage", b) ] -> a |> Expect.equal "the reading names the test that just reported" b
        | other -> failtestf "expected result then coverage for the same test, got %A" other)
    }

    testTask "tests run one at a time while coverage is being attributed, so a reading cannot mix two tests" {
      let probes = FakeProbes()
      let! _, mostAtOnce = runStream probes probes.TakeCoverage [| first; second; third |] 4
      mostAtOnce |> Expect.equal "never two tests at once, even though four were allowed" 1
    }

    testTask "with no instrumented assemblies the run stays parallel and sends no coverage" {
      let probes = FakeProbes()
      let none () = HostAgent.AgentAnswered HostAgent.NoCoverage
      let! frames, mostAtOnce = runStream probes none [| first; second; third |] 4
      frames
      |> List.exists (function CoverageFrame _ -> true | _ -> false)
      |> Expect.isFalse "nothing to report"
      frames
      |> List.filter (function ResultFrame _ -> true | _ -> false)
      |> List.length
      |> Expect.equal "every test still reports" 3
      (mostAtOnce, 1) |> Expect.isGreaterThan "attribution is not worth serial runs when nothing is recorded"
    }
  ]

/// A one-shot fake worker: whatever `write` sends is the response.
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

let private sendText (stream: IO.Stream) (text: string) =
  async {
    let bytes = Encoding.UTF8.GetBytes text
    do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
    do! stream.FlushAsync() |> Async.AwaitTask
  }

let private coverageFrameText (tc: TestCase) =
  let bitmap = bitmapOf (probesOf tc)
  sprintf "event: coverage\ndata: %s\n\n" (Serialization.serialize {| testId = tc.Id; count = bitmap.Count; words = CoverageBitmap.toBase64 bitmap |})

[<Tests>]
let clientTests =
  testList "per-test coverage: the daemon's stream client" [

    testTask "a coverage frame is handed over with the test it names" {
      let url, listener =
        serveOnce (fun stream ->
          async {
            for tc in [ first; second ] do
              do! sendText stream (sprintf "data: %s\n\n" (Serialization.serialize (passed tc)))
              do! sendText stream (coverageFrameText tc)
            do! sendText stream "event: done\ndata: {}\n\n"
          })
      try
        let received = System.Collections.Concurrent.ConcurrentQueue<TestId * bool array>()
        let proxy = HttpWorkerClient.streamingTestProxyWithCoverage TestTimeouts.streamReadPatience url
        let! outcome =
          proxy [| first; second |] 4 ignore (fun id hits -> received.Enqueue((id, hits))) CancellationToken.None
          |> Async.StartAsTask
        outcome |> Expect.equal "the stream completed" HttpWorkerClient.StreamOutcome.Completed
        received
        |> Seq.toList
        |> Expect.equal
          "each reading arrives with its test, unpacked to probe hits"
          [ for tc in [ first; second ] -> tc.Id, CoverageBitmap.toBoolArray (bitmapOf (probesOf tc)) ]
      finally
        listener.Close()
    }
  ]

/// The daemon's edge: the effect handler runs the selected tests against a stub proxy that reports
/// per-test coverage, and what it dispatches is what the model records.
module private Edge =
  let sid = SageFs.Tests.SharedGenerators.testSessionId "c0de0001"

  let deps (proxy: TestCase array -> int -> (TestRunResult -> unit) -> (TestId -> bool array -> unit) -> CancellationToken -> Async<HttpWorkerClient.StreamOutcome>) : EffectDeps =
    { ResolveSession = fun _ -> Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ -> Some proxy
      CreateSession = fun _ _ _ -> async { return Result.Error SageFsError.NoActiveSessions }
      ConfigureWarmupAutoOpen = SageFs.Tests.SageFsEffectHandlerTests.TestDeps.ensureAutoOpenNoop
      StopSession = fun _ -> async { return Result.Error SageFsError.NoActiveSessions }
      RestartSession = fun _ _ -> async { return Result.Error SageFsError.NoActiveSessions }
      ListSessions = fun () -> async { return [] }
      AwaitReady = fun _ -> async { return Result.Ok () }
      ReadyDeadline = Timeouts.rebuildReadyWait
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = TestCycleCancellation.create () }

[<Tests>]
let edgeTests =
  testList "per-test coverage: the effect handler" [

    testTask "each reading is dispatched for its own test, and the line coverage view gets the union once" {
      let map : InstrumentationMap =
        { Slots = Array.init probeCount (fun i -> { File = "Sample.fs"; Line = i + 1; Column = 0; EndLine = i + 1; EndColumn = 9; BranchId = i })
          TotalProbes = probeCount
          TrackerTypeName = "T"
          HitsFieldName = "H"
          Source = MapSource.none }
      let proxy _ _ (onResult: TestRunResult -> unit) (onCoverage: TestId -> bool array -> unit) _ =
        async {
          for tc in [ first; second ] do
            onResult (passed tc)
            onCoverage tc.Id (CoverageBitmap.toBoolArray (bitmapOf (probesOf tc)))
          return HttpWorkerClient.StreamOutcome.Completed
        }
      let messages = System.Collections.Concurrent.ConcurrentQueue<SageFsMsg>()
      let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let dispatch (m: SageFsMsg) =
        messages.Enqueue m
        match m with
        | SageFsMsg.Event (TuiEvent.TestRunCompleted _) -> finished.TrySetResult() |> ignore
        | _ -> ()
      do! SageFsEffectHandler.execute (Edge.deps proxy) dispatch
            (SageFsEffect.TestCycle (
              TestCycleEffect.RunAffectedTests
                { TestRunRequest.empty with
                    Tests = [| first; second |]
                    Trigger = RunTrigger.FileSave
                    SessionId = Some (SessionId.value Edge.sid)
                    InstrumentationMaps = [| map |] }))
      let! _ = finished.Task.WaitAsync TestTimeouts.patienceInProcess
      let collected =
        messages
        |> Seq.choose (function
          | SageFsMsg.Event (TuiEvent.CoverageBitmapCollected (_, ids, bitmap)) -> Some(ids, bitmap)
          | _ -> None)
        |> Seq.toList
      collected
      |> List.map fst
      |> Expect.equal "one dispatch per test, each for that one test" [ [| first.Id |]; [| second.Id |] ]
      collected
      |> List.map (fun (_, bitmap) -> bitmap.Bits)
      |> Expect.equal "each carries only its own probes" [ (bitmapOf (probesOf first)).Bits; (bitmapOf (probesOf second)).Bits ]
      let updates =
        messages
        |> Seq.choose (function
          | SageFsMsg.Event (TuiEvent.CoverageUpdated state) -> Some state.Hits
          | _ -> None)
        |> Seq.toList
      updates
      |> Expect.equal "the line coverage view is updated once, with the union of the run" [ CoverageBitmap.toBoolArray (bitmapOf (probesOf first @ probesOf second)) ]
    }
  ]
