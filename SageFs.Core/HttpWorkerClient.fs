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
    | WorkerMessage.Shutdown ->
      "POST", "/shutdown", None

  /// Create a SessionProxy backed by HTTP to the given base URL.
  let httpProxy (baseUrl: string) : SessionProxy =
    let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
    let client = new HttpClient(handler, BaseAddress = Uri(baseUrl), Timeout = System.Threading.Timeout.InfiniteTimeSpan)
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

  /// Create a streaming test proxy that reads SSE events from the worker.
  /// Each test result is dispatched individually via the onResult callback.
  let streamingTestProxy (baseUrl: string)
    : Features.LiveTesting.TestCase array
      -> int
      -> (Features.LiveTesting.TestRunResult -> unit)
      -> Async<unit> =
    let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
    let client = new HttpClient(handler, BaseAddress = Uri(baseUrl), Timeout = System.Threading.Timeout.InfiniteTimeSpan)
    fun tests maxParallelism onResult ->
      async {
        let body = Serialization.serialize {| tests = tests; maxParallelism = maxParallelism |}
        let content = new StringContent(body, Encoding.UTF8, "application/json")
        let msg = new HttpRequestMessage(HttpMethod.Post, "/run-tests-stream", Content = content)
        let! resp = client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask
        resp.EnsureSuccessStatusCode() |> ignore
        use! stream = resp.Content.ReadAsStreamAsync() |> Async.AwaitTask
        use reader = new IO.StreamReader(stream)
        let mutable keepReading = true
        let mutable isCoverageEvent = false
        while keepReading do
          let! line = reader.ReadLineAsync() |> Async.AwaitTask
          match isNull line with
          | true -> keepReading <- false
          | false ->
            match line.StartsWith("event: done") with
            | true -> keepReading <- false
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
                    // Coverage data is ignored here — collected via separate proxy
                  | false ->
                    match json <> "{}" with
                    | true ->
                      let result = Serialization.deserialize<Features.LiveTesting.TestRunResult> json
                      onResult result
                    | false -> ()
                | false -> ()
      }

  /// Streaming test proxy that also collects IL coverage hits.
  let streamingTestProxyWithCoverage (baseUrl: string)
    : Features.LiveTesting.TestCase array
      -> int
      -> (Features.LiveTesting.TestRunResult -> unit)
      -> (bool array -> unit)
      -> Async<unit> =
    let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
    let client = new HttpClient(handler, BaseAddress = Uri(baseUrl), Timeout = System.Threading.Timeout.InfiniteTimeSpan)
    fun tests maxParallelism onResult onCoverage ->
      async {
        let body = Serialization.serialize {| tests = tests; maxParallelism = maxParallelism |}
        let content = new StringContent(body, Encoding.UTF8, "application/json")
        let msg = new HttpRequestMessage(HttpMethod.Post, "/run-tests-stream", Content = content)
        let! resp = client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask
        resp.EnsureSuccessStatusCode() |> ignore
        use! stream = resp.Content.ReadAsStreamAsync() |> Async.AwaitTask
        use reader = new IO.StreamReader(stream)
        let mutable keepReading = true
        let mutable isCoverageEvent = false
        while keepReading do
          let! line = reader.ReadLineAsync() |> Async.AwaitTask
          match isNull line with
          | true -> keepReading <- false
          | false ->
            match line.StartsWith("event: done") with
            | true -> keepReading <- false
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
                    try
                      let doc = System.Text.Json.JsonDocument.Parse(json)
                      let hitsArr = doc.RootElement.GetProperty("hits")
                      let hits = [| for i in 0 .. hitsArr.GetArrayLength() - 1 -> hitsArr.[i].GetBoolean() |]
                      onCoverage hits
                    with _ -> ()
                  | false ->
                    match json <> "{}" with
                    | true ->
                      let result = Serialization.deserialize<Features.LiveTesting.TestRunResult> json
                      onResult result
                    | false -> ()
                | false -> ()
      }
