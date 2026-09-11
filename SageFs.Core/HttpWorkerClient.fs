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
        return Serialization.deserialize<WorkerResponse> json
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

  /// Run a stream under the caller's Async cancellation token, so cancelling the
  /// run (a newer run superseding it, the daemon stopping) reaches the read.
  let private underCallerToken
    (run: System.Threading.CancellationToken -> System.Threading.Tasks.Task<StreamOutcome>)
    : Async<StreamOutcome> =
    async {
      let! caller = Async.CancellationToken
      return! run caller |> Async.AwaitTask
    }

  let private newStreamingClient (baseUrl: string) =
    let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
    new HttpClient(handler, BaseAddress = Uri(baseUrl), Timeout = Timeouts.workerHttpRequest)

  /// Deliver one streamed test result; the worker's `{}` keep-alive is skipped.
  let private deliverResult (onResult: Features.LiveTesting.TestRunResult -> unit) (json: string) =
    match json with
    | "{}" -> ()
    | _ -> onResult (Serialization.deserialize<Features.LiveTesting.TestRunResult> json)

  /// Create a streaming test proxy that reads SSE events from the worker.
  /// Each test result is dispatched individually via the onResult callback.
  /// Returns how the stream ended so the caller can give every test that never
  /// reported a truthful NoResult — not silence, and not a fabricated failure.
  let streamingTestProxy (readTimeout: TimeSpan) (baseUrl: string)
    : Features.LiveTesting.TestCase array
      -> int
      -> (Features.LiveTesting.TestRunResult -> unit)
      -> Async<StreamOutcome> =
    let client = newStreamingClient baseUrl
    fun tests maxParallelism onResult ->
      underCallerToken (
        streamRun readTimeout client tests maxParallelism (fun data ->
          match data with
          | SseData.TestResult json -> deliverResult onResult json
          // Coverage is collected by streamingTestProxyWithCoverage.
          | SseData.Coverage _ -> ()))

  /// Streaming test proxy that also collects IL coverage hits.
  let streamingTestProxyWithCoverage (readTimeout: TimeSpan) (baseUrl: string)
    : Features.LiveTesting.TestCase array
      -> int
      -> (Features.LiveTesting.TestRunResult -> unit)
      -> (bool array -> unit)
      -> Async<StreamOutcome> =
    let client = newStreamingClient baseUrl
    fun tests maxParallelism onResult onCoverage ->
      underCallerToken (
        streamRun readTimeout client tests maxParallelism (fun data ->
          match data with
          | SseData.TestResult json -> deliverResult onResult json
          | SseData.Coverage json ->
            try
              use doc = System.Text.Json.JsonDocument.Parse(json)
              let hitsArr = doc.RootElement.GetProperty("hits")
              let hits = [| for i in 0 .. hitsArr.GetArrayLength() - 1 -> hitsArr.[i].GetBoolean() |]
              onCoverage hits
            with ex ->
              Utils.Log.warn "[HttpWorkerClient] Coverage data parse failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")))
