namespace SageFs

open System
open System.Net.Http
open System.Text
open SageFs.WorkerProtocol

/// HTTP client for communicating with a worker's Kestrel server.
/// Lives in SageFs.Core so SessionManager can create proxies.
module HttpWorkerClient =

  /// Map WorkerMessage → (httpMethod, path, bodyJson option).
  let toRoute (msg: WorkerMessage) : string * string * string option =
    match msg with
    | WorkerMessage.GetStatus rid ->
      "GET", sprintf "/status?replyId=%s" (Uri.EscapeDataString rid), None
    | WorkerMessage.GetLiveValues rid ->
      "GET", sprintf "/live-values?replyId=%s" (Uri.EscapeDataString rid), None
    | WorkerMessage.EvaluateLiveMember(binding, path, rid) ->
      "POST", "/live-values/evaluate",
      Some (Serialization.serialize {| binding = binding; path = path; replyId = rid |})
    | WorkerMessage.SetValueWalk(mode, rid) ->
      "POST", "/live-values/mode",
      Some (Serialization.serialize {| mode = ValueWalk.name mode; replyId = rid |})
    | WorkerMessage.EvalCode(code, rid) ->
      "POST", "/eval",
      Some (Serialization.serialize {| code = code; replyId = rid |})
    | WorkerMessage.CheckCode(code, rid) ->
      "POST", "/check",
      Some (Serialization.serialize {| code = code; replyId = rid |})
    | WorkerMessage.TypeCheckWithSymbols(code, filePath, rid) ->
      "POST", "/typecheck-symbols",
      Some (Serialization.serialize {| code = code; filePath = filePath; replyId = rid |})
    | WorkerMessage.GetCompletions(code, cursorPos, rid) ->
      "POST", "/completions",
      Some (Serialization.serialize {| code = code; cursorPos = cursorPos; replyId = rid |})
    | WorkerMessage.CancelEval ->
      "POST", "/cancel", None
    | WorkerMessage.LoadScript(filePath, rid) ->
      "POST", "/load-script",
      Some (Serialization.serialize {| filePath = filePath; replyId = rid |})
    | WorkerMessage.ResetSession rid ->
      "POST", "/reset",
      Some (Serialization.serialize {| replyId = rid |})
    | WorkerMessage.HardResetSession(rebuild, rid) ->
      "POST", "/hard-reset",
      Some (Serialization.serialize {| rebuild = rebuild; replyId = rid |})
    | WorkerMessage.RunTests(tests, maxParallelism, rid) ->
      "POST", "/run-tests",
      Some (Serialization.serialize {| tests = tests; maxParallelism = maxParallelism; replyId = rid |})
    | WorkerMessage.GetTestDiscovery rid ->
      "GET", sprintf "/test-discovery?replyId=%s" (Uri.EscapeDataString rid), None
    | WorkerMessage.EvalLiveTestFile(filePath, content, rid) ->
      "POST", "/eval-live-test-file",
      Some (Serialization.serialize {| filePath = filePath; content = content; replyId = rid |})
    | WorkerMessage.GetInstrumentationMaps rid ->
      "GET", sprintf "/instrumentation-maps?replyId=%s" (Uri.EscapeDataString rid), None
    | WorkerMessage.RunApp(project, previous, rid) ->
      "POST", "/run-app",
      Some (Serialization.serialize {| project = project; previous = previous; replyId = rid |})
    | WorkerMessage.StopApp(scope, rid) ->
      "POST", "/stop-app",
      Some (Serialization.serialize {| scope = scope; replyId = rid |})
    | WorkerMessage.AwaitAppChange(runId, rid) ->
      "POST", "/await-app-change",
      Some (Serialization.serialize {| runId = runId; replyId = rid |})
    | WorkerMessage.DebugTestBegin(test, rid) ->
      "POST", "/debug-test",
      Some (Serialization.serialize {| test = test; replyId = rid |})
    | WorkerMessage.DebugTestContinue(ticket, park, rid) ->
      "POST", "/debug-test-continue",
      Some (Serialization.serialize {| ticket = ticket; park = park; replyId = rid |})
    | WorkerMessage.Shutdown ->
      "POST", "/shutdown", None

  /// Create a SessionProxy backed by HTTP to the given base URL.
  let httpProxy (baseUrl: string) : SessionProxy =
    let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
    let client = new HttpClient(handler, BaseAddress = Uri(baseUrl), Timeout = Timeouts.workerHttpRequest)
    fun msg ->
      async {
        let method, path, body = toRoute msg
        let! resp =
          match method with
          | "GET" ->
            client.GetAsync(path) |> Async.AwaitTask
          | _ ->
            let content =
              body
              |> Option.map (fun b ->
                new StringContent(b, Encoding.UTF8, "application/json") :> HttpContent)
              |> Option.defaultValue null
            client.PostAsync(path, content) |> Async.AwaitTask
        resp.EnsureSuccessStatusCode() |> ignore
        let! json = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        match Serialization.tryDeserialize<WorkerResponse> json with
        | Ok response -> return response
        | Error error -> return WorkerResponse.WorkerError error
      }

  /// Cached proxy factory — reuses HttpClient instances per worker URL.
  /// Use this for CQRS-based proxy resolution to avoid mailbox contention.
  let private proxyCache =
    System.Collections.Concurrent.ConcurrentDictionary<string, SessionProxy>()

  let cachedProxy (baseUrl: string) : SessionProxy =
    proxyCache.GetOrAdd(baseUrl, System.Func<string, SessionProxy>(httpProxy))

  /// Resolve a proxy from worker base URLs — lock-free, no mailbox.
  let proxyFromUrls
    (sessionId: string)
    (workerBaseUrls: Map<string, string>)
    : SessionProxy option =
    match Map.tryFind sessionId workerBaseUrls with
    | Some url when url.Length > 0 -> Some (cachedProxy url)
    | _ -> None

  /// Result of a streaming test-proxy read loop.
  [<RequireQualifiedAccess>]
  type StreamOutcome =
    /// The worker closed the stream cleanly (EOF or an explicit `event: done`).
    | Completed
    /// The worker went silent for longer than the read timeout without
    /// finishing — the loop exits with an explicit timeout instead of silently
    /// reporting success (roast queue item 1).
    | TimedOut of after: TimeSpan
    /// The caller cancelled the run (a newer run superseded it, or the daemon
    /// is stopping) — the in-flight read was abandoned, not waited out.
    | Cancelled

  /// Where a run's inactivity window stands.
  [<RequireQualifiedAccess>]
  type WindowState =
    /// The worker has spoken within the window.
    | Open
    /// The worker stayed silent for the whole window.
    | Expired
    /// The caller cancelled the run.
    | CallerCancelled

  /// ONE deadline per streaming run: a single cancellation source linked to the
  /// caller's token, re-armed on every line the worker sends. The deadline is
  /// therefore an inactivity window — a slow but steady run is never cut off —
  /// and cancelling the run cancels the in-flight read with it. Nothing is
  /// allocated per line.
  type InactivityWindow(span: TimeSpan, caller: System.Threading.CancellationToken) =
    let cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(caller)
    do cts.CancelAfter span
    member _.Token = cts.Token
    /// The worker just proved it is alive: restart the window.
    member _.Touch() =
      match cts.IsCancellationRequested with
      | true -> ()
      | false -> cts.CancelAfter span
    member _.State =
      match caller.IsCancellationRequested, cts.IsCancellationRequested with
      | true, _ -> WindowState.CallerCancelled
      | false, true -> WindowState.Expired
      | false, false -> WindowState.Open
    interface IDisposable with
      member _.Dispose() = cts.Dispose()

  /// Why the tests a stream never reported have no result, given how it ended.
  let noResultReason (outcome: StreamOutcome) : Features.LiveTesting.NoResultReason =
    match outcome with
    | StreamOutcome.Completed -> Features.LiveTesting.NoResultReason.StreamEnded
    | StreamOutcome.TimedOut after -> Features.LiveTesting.NoResultReason.StreamStalled after
    | StreamOutcome.Cancelled -> Features.LiveTesting.NoResultReason.RunCancelled

  /// What the worker streams, as `event: coverage`, right after each test's own result: the coverage
  /// that test recorded and nothing else, as the probe count and the packed hit words
  /// (`CoverageBitmap.toBase64`). One shape, written by the worker and read here.
  type CoverageFrame =
    { TestId: Features.LiveTesting.TestId
      Count: int
      Words: string }

  /// One SSE payload the worker streamed during a test run.
  [<RequireQualifiedAccess>]
  type private SseData =
    | TestResult of json: string
    | Coverage of json: string

  /// Run one streaming test request against the worker. The run has ONE
  /// inactivity window linked to the caller's token: every line re-arms it,
  /// silence past `readTimeout` ends the run as TimedOut, and cancelling the
  /// caller abandons the in-flight read immediately and ends it as Cancelled.
  /// Written as a task so cancellation arrives as a catchable exception and is
  /// reported as an outcome, never as a silently-cancelled async.
  let private streamRun
    (readTimeout: TimeSpan)
    (client: HttpClient)
    (tests: Features.LiveTesting.TestCase array)
    (maxParallelism: int)
    (onData: SseData -> unit)
    (caller: System.Threading.CancellationToken)
    : System.Threading.Tasks.Task<StreamOutcome> =
    task {
      let sw = Diagnostics.Stopwatch.StartNew()
      try
        let body = Serialization.serialize {| tests = tests; maxParallelism = maxParallelism |}
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        use msg = new HttpRequestMessage(HttpMethod.Post, "/run-tests-stream", Content = content)
        use! resp = client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, caller)
        resp.EnsureSuccessStatusCode() |> ignore
        use! stream = resp.Content.ReadAsStreamAsync(caller)
        use reader = new IO.StreamReader(stream)
        use window = new InactivityWindow(readTimeout, caller)
        let mutable ended = ValueNone
        let mutable isCoverageEvent = false
        while ended.IsNone do
          let! line = reader.ReadLineAsync(window.Token)
          match line with
          | null -> ended <- ValueSome StreamOutcome.Completed // EOF: the worker closed the stream
          | line ->
            window.Touch()
            match line.StartsWith("event: done") with
            | true -> ended <- ValueSome StreamOutcome.Completed
            | false ->
              match line.StartsWith("event: coverage") with
              | true -> isCoverageEvent <- true
              | false ->
                match line.StartsWith("data: ") with
                | true ->
                  let json = line.Substring(6)
                  match isCoverageEvent with
                  | true ->
                    isCoverageEvent <- false
                    onData (SseData.Coverage json)
                  | false -> onData (SseData.TestResult json)
                | false -> ()
        match ended with
        | ValueSome outcome -> return outcome
        | ValueNone -> return StreamOutcome.Completed
      with
      | :? OperationCanceledException ->
        match caller.IsCancellationRequested with
        | true -> return StreamOutcome.Cancelled
        | false -> return StreamOutcome.TimedOut sw.Elapsed
    }

  /// Wrap an already-cancellation-safe Task as an Async, WITHOUT reading the
  /// ambient `Async.CancellationToken`. This used to be `async { let! caller
  /// = Async.CancellationToken; return! run caller |> Async.AwaitTask }` —
  /// which reads the SAME token object a caller hands to
  /// `Async.StartAsTask(_, cancellationToken = ct)` / `Async.Start(_, ct)`.
  /// That starter registers its OWN forced-cancellation callback against
  /// `ct` the instant it is called — independent of whatever this
  /// computation is doing — and that callback can complete the caller's
  /// Task as Canceled (raising `TaskCanceledException` at the await site)
  /// BEFORE `streamRun`'s own `with :? OperationCanceledException ->
  /// return StreamOutcome.Cancelled` ever gets to run. Reproduced 600/600 in
  /// the SageFs REPL by cancelling `run.Token` immediately after starting
  /// `Async.StartAsTask(proxy, cancellationToken = run.Token)`: the task's
  /// own graceful catch never got a chance to answer — the starter's
  /// registration wins the race, every time cancellation lands before the
  /// task's own handling has registered.
  ///
  /// Fix: `run` already carries its own CancellationToken as a plain VALUE
  /// (threaded explicitly by the caller, not read from the ambient token),
  /// and it already resolves cancellation to a normal `StreamOutcome.
  /// Cancelled` VALUE via `streamRun`'s try/with — never throws. Wrapping
  /// that already-resolved Task with `Async.AwaitTask` and nothing else
  /// means there is no SECOND, independent cancellation source for a
  /// starter to race against: whoever starts the returned Async must do so
  /// WITHOUT also passing that same token to the starter's own
  /// `cancellationToken` parameter (verified 0/400 throws in the REPL under
  /// that calling convention, cancel-before-start and cancel-after-start
  /// alike) — see StreamingProxyTests.fs and
  /// SageFs.Simulation/StreamingProxySim.fs for the replayable contract.
  let private safeAwait (task: System.Threading.Tasks.Task<StreamOutcome>) : Async<StreamOutcome> =
    Async.AwaitTask task

  let private newStreamingClient (baseUrl: string) =
    let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
    new HttpClient(handler, BaseAddress = Uri(baseUrl), Timeout = Timeouts.workerHttpRequest)

  /// Deliver one streamed test result; the worker's `{}` keep-alive is skipped. A result line that
  /// cannot be read is reported on stderr with its reason and skipped: its test then never reports,
  /// and the caller gives it a NoResult, which is what happened.
  let private deliverResult (onResult: Features.LiveTesting.TestRunResult -> unit) (json: string) =
    match json with
    | "{}" -> ()
    | _ ->
      match Serialization.tryDeserialize<Features.LiveTesting.TestRunResult> json with
      | Ok result -> onResult result
      | Error error -> eprintfn "SageFs: a streamed test result could not be read and was skipped: %s" (SageFsError.describe error)

  /// Create a streaming test proxy that reads SSE events from the worker.
  /// Each test result is dispatched individually via the onResult callback.
  /// Returns how the stream ended so the caller can give every test that never
  /// reported a truthful NoResult — not silence, and not a fabricated failure.
  ///
  /// The caller's `CancellationToken` is a plain VALUE parameter, not the
  /// ambient one — start the returned Async with `Async.StartAsTask(result)`
  /// (no `cancellationToken` argument) or `Async.AwaitTask`/`await` it
  /// directly. Handing that SAME token a second time to the starter's own
  /// `cancellationToken` parameter reopens the pre-start-cancellation race
  /// `safeAwait`'s doc comment describes — the contract this function makes
  /// (a cancelled run always answers `StreamOutcome.Cancelled`, never
  /// throws) only holds when the token is threaded this way.
  let streamingTestProxy (readTimeout: TimeSpan) (baseUrl: string)
    : Features.LiveTesting.TestCase array
      -> int
      -> (Features.LiveTesting.TestRunResult -> unit)
      -> System.Threading.CancellationToken
      -> Async<StreamOutcome> =
    let client = newStreamingClient baseUrl
    fun tests maxParallelism onResult ct ->
      safeAwait (
        streamRun readTimeout client tests maxParallelism (fun data ->
          match data with
          | SseData.TestResult json -> deliverResult onResult json
          // Coverage is collected by streamingTestProxyWithCoverage.
          | SseData.Coverage _ -> ()) ct)

  /// Streaming test proxy that also collects IL coverage hits. Same
  /// explicit-token contract as `streamingTestProxy` above.
  let streamingTestProxyWithCoverage (readTimeout: TimeSpan) (baseUrl: string)
    : Features.LiveTesting.TestCase array
      -> int
      -> (Features.LiveTesting.TestRunResult -> unit)
      -> (Features.LiveTesting.TestId -> bool array -> unit)
      -> System.Threading.CancellationToken
      -> Async<StreamOutcome> =
    let client = newStreamingClient baseUrl
    fun tests maxParallelism onResult onCoverage ct ->
      safeAwait (
        streamRun readTimeout client tests maxParallelism (fun data ->
          match data with
          | SseData.TestResult json -> deliverResult onResult json
          | SseData.Coverage json ->
            match Serialization.tryDeserialize<CoverageFrame> json with
            | Ok frame ->
              try
                // Packed base64 words (CoverageBitmap.toBase64), not one JSON bool per probe, unpacked
                // back to a bool array here. The reading belongs to the test the frame names.
                let hits =
                  Features.LiveTesting.CoverageBitmap.ofBase64 frame.Count frame.Words
                  |> Features.LiveTesting.CoverageBitmap.toBoolArray
                onCoverage frame.TestId hits
              with ex ->
                Utils.Log.warn "[HttpWorkerClient] Coverage data could not be unpacked: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
            | Error error ->
              Utils.Log.warn "[HttpWorkerClient] A coverage frame could not be read and was skipped: %s" (SageFsError.describe error)) ct)
