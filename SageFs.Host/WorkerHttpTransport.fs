namespace SageFs

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Metadata
open Microsoft.AspNetCore.Routing
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.ResponseCompression
open Microsoft.Extensions.Logging
open SageFs.Utils
open SageFs.WorkerProtocol

module WorkerHttpTransport =

  /// Opaque server handle — exposes BaseUrl and Dispose.
  type HttpWorkerServer internal (baseUrl: string, app: WebApplication) =
    member _.BaseUrl = baseUrl
    /// (HTTP method, route template) for every endpoint this server maps.
    /// Lets tests prove no endpoint bypasses the declared route table.
    member _.MappedRoutes : (string * string) list =
      (app :> IEndpointRouteBuilder).DataSources
      |> Seq.collect (fun ds -> ds.Endpoints)
      |> Seq.collect (fun e ->
        match e with
        | :? RouteEndpoint as re ->
          let template = re.RoutePattern.RawText |> Option.ofObj |> Option.defaultValue ""
          let methods =
            match re.Metadata.GetMetadata<IHttpMethodMetadata>() with
            | null -> [ "*" ]
            | m -> m.HttpMethods |> Seq.toList
          methods |> List.map (fun m -> m, template)
        | _ -> [])
      |> Seq.toList
    interface IAsyncDisposable with
      member _.DisposeAsync() = app.StopAsync() |> ValueTask
    interface IDisposable with
      member _.Dispose() =
        use cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(5.0))
        try app.StopAsync(cts.Token).GetAwaiter().GetResult()
        with _ -> ()

  /// Map WorkerMessage → (httpMethod, path, bodyJson option).
  /// Delegates to HttpWorkerClient in SageFs.Core.
  let toRoute = HttpWorkerClient.toRoute

  let readBody (ctx: HttpContext) : Task<string> = task {
    use reader = new StreamReader(ctx.Request.Body)
    return! reader.ReadToEndAsync()
  }

  let jsonProp (doc: JsonDocument) (name: string) =
    doc.RootElement.GetProperty(name)

  let respond
    (handler: WorkerMessage -> Async<WorkerResponse>)
    (ctx: HttpContext)
    (msg: WorkerMessage)
    = task {
    let! resp = handler msg |> Async.StartAsTask
    ctx.Response.ContentType <- "application/json"
    do! ctx.Response.WriteAsync(Serialization.serialize resp)
  }

  /// How the origin gate treats a request.
  [<RequireQualifiedAccess>]
  type RouteAccess =
    /// Executes F# or mutates worker state: any cross-site signal or foreign
    /// origin is rejected (HttpOriginGuard.decide).
    | Mutating
    /// Reads state only: loopback origins pass, foreign origins are rejected.
    | ReadOnly
    /// The DevReload SSE stream — the one surface a browser page (the user's
    /// dev app on a loopback origin) legitimately reads cross-origin.
    | CrossOriginStream

  /// Access for a GET route. There is no way to declare a POST read-only:
  /// every `Post` is Mutating by construction.
  [<RequireQualifiedAccess>]
  type GetAccess =
    | ReadOnly
    | CrossOriginStream

  /// One worker HTTP route. `routes` below is the single source of truth:
  /// startServer maps endpoints only through it, and the origin gate
  /// classifies requests from it.
  [<RequireQualifiedAccess>]
  type WorkerRoute =
    | Post of path: string
    | Get of path: string * access: GetAccess

  [<RequireQualifiedAccess>]
  module WorkerRoute =
    let path (route: WorkerRoute) =
      match route with
      | WorkerRoute.Post p -> p
      | WorkerRoute.Get (p, _) -> p
    let httpMethod (route: WorkerRoute) =
      match route with
      | WorkerRoute.Post _ -> "POST"
      | WorkerRoute.Get _ -> "GET"
    let access (route: WorkerRoute) =
      match route with
      | WorkerRoute.Post _ -> RouteAccess.Mutating
      | WorkerRoute.Get (_, GetAccess.ReadOnly) -> RouteAccess.ReadOnly
      | WorkerRoute.Get (_, GetAccess.CrossOriginStream) -> RouteAccess.CrossOriginStream

  /// The worker's route table. (`/diag/threadpool` and `/status` are read-only
  /// GETs; nothing reachable by GET executes code or mutates state.)
  module Routes =
    let diagThreadpool = WorkerRoute.Get ("/diag/threadpool", GetAccess.ReadOnly)
    let status = WorkerRoute.Get ("/status", GetAccess.ReadOnly)
    let liveValues = WorkerRoute.Get ("/live-values", GetAccess.ReadOnly)
    let eval = WorkerRoute.Post "/eval"
    let check = WorkerRoute.Post "/check"
    let typecheckSymbols = WorkerRoute.Post "/typecheck-symbols"
    let completions = WorkerRoute.Post "/completions"
    let cancel = WorkerRoute.Post "/cancel"
    let loadScript = WorkerRoute.Post "/load-script"
    let reset = WorkerRoute.Post "/reset"
    let hardReset = WorkerRoute.Post "/hard-reset"
    let runTests = WorkerRoute.Post "/run-tests"
    let runTestsStream = WorkerRoute.Post "/run-tests-stream"
    let testDiscovery = WorkerRoute.Get ("/test-discovery", GetAccess.ReadOnly)
    /// Identity-preserving buffer eval for as-you-type live testing (Brief 3
    /// of live-testing-asyoutype-plan.md). Executes F# from the request body —
    /// same mutating/CSRF-gated class as `/eval`.
    let evalLiveTestFile = WorkerRoute.Post "/eval-live-test-file"
    let instrumentationMaps = WorkerRoute.Get ("/instrumentation-maps", GetAccess.ReadOnly)
    let shutdown = WorkerRoute.Post "/shutdown"
    let warmupContext = WorkerRoute.Get ("/warmup-context", GetAccess.ReadOnly)
    let hotReload = WorkerRoute.Get ("/hotreload", GetAccess.ReadOnly)
    let hotReloadToggle = WorkerRoute.Post "/hotreload/toggle"
    /// Run the new initializer of a `let mutable` a save kept (rule 3 of the
    /// state spec). Writes the app's live state, so it's a POST like every
    /// other mutating route.
    let hotReloadResetState = WorkerRoute.Post "/hotreload/reset-state"
    let hotReloadWatchAll = WorkerRoute.Post "/hotreload/watch-all"
    let hotReloadUnwatchAll = WorkerRoute.Post "/hotreload/unwatch-all"
    let hotReloadWatchProject = WorkerRoute.Post "/hotreload/watch-project"
    let hotReloadUnwatchProject = WorkerRoute.Post "/hotreload/unwatch-project"
    let hotReloadWatchDirectory = WorkerRoute.Post "/hotreload/watch-directory"
    let hotReloadUnwatchDirectory = WorkerRoute.Post "/hotreload/unwatch-directory"
    let runApp = WorkerRoute.Post "/run-app"
    let stopApp = WorkerRoute.Post "/stop-app"
    let awaitAppChange = WorkerRoute.Post "/await-app-change"
    let devReload = WorkerRoute.Get ("/__sagefs__/reload", GetAccess.CrossOriginStream)
    /// The last terminal `ReloadOutcome`, past the server boundary, for a
    /// client that cannot hold the `devReload` SSE stream open — a dashboard
    /// poll, a health check, an editor extension with no standing connection
    /// to this port. Same JSON shape a live SSE subscriber gets
    /// (DevReload.LastReload.json); loopback-only like every other read
    /// (not the DevReload stream's cross-origin carve-out, since this is a
    /// plain same-origin poll, not the user's dev app reading its own
    /// reload channel).
    let hotReloadLastOutcome = WorkerRoute.Get ("/hotreload/last-outcome", GetAccess.ReadOnly)

  let routes : WorkerRoute list = [
    Routes.diagThreadpool; Routes.status; Routes.liveValues; Routes.eval; Routes.check
    Routes.typecheckSymbols; Routes.completions; Routes.cancel; Routes.loadScript
    Routes.reset; Routes.hardReset; Routes.runTests; Routes.runTestsStream
    Routes.testDiscovery; Routes.evalLiveTestFile; Routes.instrumentationMaps; Routes.shutdown
    Routes.warmupContext; Routes.hotReload; Routes.hotReloadToggle; Routes.hotReloadResetState
    Routes.hotReloadWatchAll; Routes.hotReloadUnwatchAll
    Routes.hotReloadWatchProject; Routes.hotReloadUnwatchProject
    Routes.hotReloadWatchDirectory; Routes.hotReloadUnwatchDirectory
    Routes.runApp; Routes.stopApp; Routes.awaitAppChange
    Routes.devReload; Routes.hotReloadLastOutcome
  ]

  /// Declared access of every GET route, matched the way ASP.NET routing
  /// matches: case-insensitively, trailing slash optional.
  let private getAccessByPath =
    let d = Dictionary<string, RouteAccess>(StringComparer.OrdinalIgnoreCase)
    for route in routes do
      match route with
      | WorkerRoute.Get (p, _) -> d[p] <- WorkerRoute.access route
      | WorkerRoute.Post _ -> ()
    d

  /// Classify one request. Method-driven: every non-GET/HEAD request is
  /// Mutating, whatever its path — so a newly mapped POST (or a path the table
  /// does not know, or `/EVAL` in any casing) can never land on a weaker check.
  /// GET/HEAD take the declared access of their route; unknown GETs 404 and
  /// are read-only.
  let classify (httpMethod: string) (path: string) : RouteAccess =
    match httpMethod.Trim().ToUpperInvariant() with
    | "GET" | "HEAD" ->
      let normalized =
        match path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal) with
        | true -> path.TrimEnd('/')
        | false -> path
      match getAccessByPath.TryGetValue normalized with
      | true, access -> access
      | false, _ -> RouteAccess.ReadOnly
    | _ -> RouteAccess.Mutating

  [<RequireQualifiedAccess>]
  type GuardVerdict =
    | Allow
    /// Allow, and reflect this (loopback) origin in Access-Control-Allow-Origin.
    | AllowCrossOrigin of origin: string
    | Reject of SageFs.Server.HttpOriginGuard.Rejection

  /// The origin/CSRF decision for one worker request — pure, so every route
  /// and header combination is testable without a server.
  ///
  /// Same threat model as the daemon (see HttpOriginGuard). The daemon proxies
  /// to the worker with NO browser headers and JSON bodies, so it passes.
  /// Mutating requests get exactly the daemon's gate (HttpOriginGuard.decide
  /// against `own`, the worker's own listener origins): a page on another
  /// localhost port — same-site, loopback Origin — cannot eval. The DevReload
  /// stream is the one surface the user's dev app reads cross-origin, so it
  /// reflects a loopback origin (never `*`); read-only GETs reject non-loopback
  /// origins (the browser's same-origin policy already hides their responses).
  let decide
    (own: SageFs.Server.HttpOriginGuard.OwnOrigins)
    (path: string)
    (request: SageFs.Server.HttpOriginGuard.Request)
    : GuardVerdict =
    match request.Host with
    // Non-loopback Host = DNS rebinding / proxy — reject everything.
    | Some h when not (SageFs.Server.HttpOriginGuard.isLoopbackHost h) ->
      GuardVerdict.Reject (SageFs.Server.HttpOriginGuard.Rejection.ForeignHost h)
    | _ ->
      match classify request.Method path with
      | RouteAccess.Mutating ->
        match SageFs.Server.HttpOriginGuard.decide own request with
        | SageFs.Server.HttpOriginGuard.Verdict.Allow -> GuardVerdict.Allow
        | SageFs.Server.HttpOriginGuard.Verdict.Reject rejection -> GuardVerdict.Reject rejection
      | RouteAccess.CrossOriginStream ->
        match request.Origin with
        | Some o when SageFs.Server.HttpOriginGuard.isLoopbackOrigin o -> GuardVerdict.AllowCrossOrigin o
        | Some o -> GuardVerdict.Reject (SageFs.Server.HttpOriginGuard.Rejection.ForeignOrigin o)
        | None -> GuardVerdict.Allow
      | RouteAccess.ReadOnly ->
        match request.Origin with
        | Some o when not (SageFs.Server.HttpOriginGuard.isLoopbackOrigin o) ->
          GuardVerdict.Reject (SageFs.Server.HttpOriginGuard.Rejection.ForeignOrigin o)
        | _ -> GuardVerdict.Allow

  /// The parts of a request the origin gate decides on.
  let guardRequestOf (ctx: HttpContext) : SageFs.Server.HttpOriginGuard.Request =
    let header (name: string) =
      match ctx.Request.Headers.TryGetValue(name) with
      | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace(string v)) -> Some (string v)
      | _ -> None
    { Method = ctx.Request.Method
      Host =
        match ctx.Request.Host.HasValue with
        | true -> Some (string ctx.Request.Host)
        | false -> None
      SecFetchSite = header "Sec-Fetch-Site"
      Origin = header "Origin"
      ContentType = header "Content-Type"
      Body =
        match ctx.Request.ContentLength with
        | length when length.HasValue ->
          match length.Value > 0L with
          | true -> SageFs.Server.HttpOriginGuard.Body.Present
          | false -> SageFs.Server.HttpOriginGuard.Body.Empty
        | _ ->
          // No Content-Length: a chunked body is still a body.
          match ctx.Request.Headers.TransferEncoding.Count with
          | 0 -> SageFs.Server.HttpOriginGuard.Body.Empty
          | _ -> SageFs.Server.HttpOriginGuard.Body.Present }

  /// Origin/CSRF gate middleware for the worker HTTP surface — applies `decide`
  /// with the listener's own origins.
  let workerOriginGuard (ctx: HttpContext) (next: Func<Task>) = task {
    let own = SageFs.Server.HttpOriginGuard.OwnOrigins.ofPorts [ ctx.Connection.LocalPort ]
    let path = ctx.Request.Path.Value |> Option.ofObj |> Option.defaultValue ""
    match decide own path (guardRequestOf ctx) with
    | GuardVerdict.Allow -> do! next.Invoke()
    | GuardVerdict.AllowCrossOrigin o ->
      ctx.Response.Headers["Access-Control-Allow-Origin"] <- o
      do! next.Invoke()
    | GuardVerdict.Reject rejection ->
      ctx.Response.StatusCode <- SageFs.Server.HttpOriginGuard.Rejection.statusCode rejection
      do! ctx.Response.WriteAsync(sprintf "Forbidden: %s" (SageFs.Server.HttpOriginGuard.Rejection.describe rejection))
  }

  /// Start a Kestrel HTTP server dispatching to the given handler.
  /// Pass port=0 for OS-assigned dynamic port.
  let startServer
    (handler: WorkerMessage -> Async<WorkerResponse>)
    (hotReloadStateRef: HotReloadState.T ref)
    (keptState: Features.KeptState.Access)
    (projectFiles: string list)
    (getWarmupContext: unit -> WarmupContext)
    (getRunTest: unit -> Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>)
    (takeCoverage: unit -> HostAgent.AgentReply<HostAgent.CoverageReading>)
    (port: int)
    : Task<HttpWorkerServer> =
    task {
      let builder = WebApplication.CreateBuilder([||])
      builder.WebHost.UseUrls(sprintf "http://127.0.0.1:%d" port) |> ignore
      builder.Logging.ClearProviders() |> ignore
      // Silence ASP.NET plumbing but allow SageFs logs
      builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore
      builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning) |> ignore
      builder.Logging.AddFilter("SageFs", LogLevel.Information) |> ignore

      // Response compression: Brotli at fastest level for all responses
      builder.Services.AddResponseCompression(fun opts ->
        opts.EnableForHttps <- true
        opts.Providers.Add<BrotliCompressionProvider>()
        opts.Providers.Add<GzipCompressionProvider>()
      ) |> ignore
      builder.Services.Configure<BrotliCompressionProviderOptions>(fun (opts: BrotliCompressionProviderOptions) ->
        opts.Level <- System.IO.Compression.CompressionLevel.Fastest
      ) |> ignore

      // The host process carries NO OpenTelemetry dependency (minimal closure —
      // see plan: fsi-host-supervisor-isolation). The daemon owns OTel.

      let app = builder.Build()

      // Wire SageFs.Core Log module to OTEL-connected ILogger in worker process
      let workerLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SageFs.Worker")
      SageFs.Utils.Log.logInfo <- fun msg -> workerLogger.LogInformation(msg)
      SageFs.Utils.Log.logDebug <- fun msg -> workerLogger.LogDebug(msg)
      SageFs.Utils.Log.logWarn <- fun msg -> workerLogger.LogWarning(msg)
      SageFs.Utils.Log.logError <- fun msg -> workerLogger.LogError(msg)

      app.UseResponseCompression() |> ignore

      // Origin/CSRF gate for the worker HTTP surface — see workerOriginGuard.
      app.Use(Func<HttpContext, Func<Task>, Task>(fun ctx next ->
        workerOriginGuard ctx next :> Task)) |> ignore

      let inline respond' ctx msg = respond handler ctx msg

      // Every endpoint is mapped through its `Routes` entry, so the verb it is
      // mapped with and the access the origin gate applies come from one table.
      let map (route: WorkerRoute) (endpoint: Func<HttpContext, Task>) =
        match route with
        | WorkerRoute.Post p -> app.MapPost(p, endpoint)
        | WorkerRoute.Get (p, _) -> app.MapGet(p, endpoint)

      // Diagnostic: ThreadPool state for measuring starvation
      map Routes.diagThreadpool (Func<HttpContext, Task>(fun ctx -> task {
        let workerThreads = ref 0
        let completionPortThreads = ref 0
        let maxWorkerThreads = ref 0
        let maxCompletionPortThreads = ref 0
        let minWorkerThreads = ref 0
        let minCompletionPortThreads = ref 0
        Threading.ThreadPool.GetAvailableThreads(workerThreads, completionPortThreads)
        Threading.ThreadPool.GetMaxThreads(maxWorkerThreads, maxCompletionPortThreads)
        Threading.ThreadPool.GetMinThreads(minWorkerThreads, minCompletionPortThreads)
        let pending = Threading.ThreadPool.PendingWorkItemCount
        let threadCount = Threading.ThreadPool.ThreadCount
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(sprintf
          """{"available":%d,"max":%d,"min":%d,"pending":%d,"threadCount":%d,"completionPort":{"available":%d,"max":%d,"min":%d}}"""
          workerThreads.Value maxWorkerThreads.Value minWorkerThreads.Value pending threadCount
          completionPortThreads.Value maxCompletionPortThreads.Value minCompletionPortThreads.Value)
      })) |> ignore

      map Routes.status (Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetStatus rid)
      })) |> ignore

      map Routes.liveValues (Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetLiveValues rid)
      })) |> ignore

      map Routes.eval (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.EvalCode(code, rid))
      })) |> ignore

      map Routes.check (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.CheckCode(code, rid))
      })) |> ignore

      map Routes.typecheckSymbols (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let filePath = (jsonProp doc "filePath").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.TypeCheckWithSymbols(code, filePath, rid))
      })) |> ignore

      map Routes.completions (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let cursorPos = (jsonProp doc "cursorPos").GetInt32()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.GetCompletions(code, cursorPos, rid))
      })) |> ignore

      map Routes.cancel (Func<HttpContext, Task>(fun ctx ->
        respond' ctx WorkerMessage.CancelEval)) |> ignore

      map Routes.loadScript (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let filePath = (jsonProp doc "filePath").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.LoadScript(filePath, rid))
      })) |> ignore

      map Routes.reset (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.ResetSession rid)
      })) |> ignore

      map Routes.hardReset (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rebuild = (jsonProp doc "rebuild").GetBoolean()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.HardResetSession(rebuild, rid))
      })) |> ignore

      map Routes.runTests (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let testsJson = (jsonProp doc "tests").GetRawText()
        let tests = Serialization.deserialize<Features.LiveTesting.TestCase array> testsJson
        let maxParallelism = (jsonProp doc "maxParallelism").GetInt32()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.RunTests(tests, maxParallelism, rid))
      })) |> ignore

      map Routes.runTestsStream (Func<HttpContext, Task>(fun ctx -> task {
        let streamActivity = Features.LiveTesting.LiveTestingInstrumentation.activitySource.StartActivity("live_testing.stream")
        let streamSw = System.Diagnostics.Stopwatch.StartNew()
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let testsJson = (jsonProp doc "tests").GetRawText()
        let tests = Serialization.deserialize<Features.LiveTesting.TestCase array> testsJson
        let maxParallelism = (jsonProp doc "maxParallelism").GetInt32()

        match isNull streamActivity with
        | false ->
          streamActivity.SetTag("stream.test_count", tests.Length) |> ignore
          streamActivity.SetTag("stream.max_parallelism", maxParallelism) |> ignore
        | true -> ()

        ctx.Response.ContentType <- "text/event-stream"
        ctx.Response.Headers["Cache-Control"] <- "no-cache"
        ctx.Response.Headers["Connection"] <- "keep-alive"

        let channel = System.Threading.Channels.Channel.CreateUnbounded<Features.LiveTesting.TestRunResult>()
        let mutable resultsEmitted = 0L

        let executionTask = task {
          try
            let onResult (result: Features.LiveTesting.TestRunResult) =
              channel.Writer.TryWrite(result) |> ignore
            let runTest = getRunTest()
            use cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted)
            try
              do! Features.LiveTesting.TestOrchestrator.executeFiltered
                    runTest onResult maxParallelism tests cts.Token
                  |> Async.StartAsTask
            with ex ->
              System.Diagnostics.Activity.Current
              |> Option.ofObj
              |> Option.iter (fun a -> a.SetTag("error", ex.Message) |> ignore)
              Log.error "[run-tests-stream] execution error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          finally
            channel.Writer.TryComplete() |> ignore
        }

        // Start execution — don't await, let the channel reader loop drive the SSE stream
        use _ = executionTask.ContinueWith(fun (t: Threading.Tasks.Task) ->
          match t.IsFaulted with
          | true -> Log.error "[run-tests-stream] unhandled: %s" t.Exception.Message
          | false -> ()
        )

        let writer = ctx.Response.Body
        let mutable keepReading = true
        while keepReading do
          let! canRead = channel.Reader.WaitToReadAsync(ctx.RequestAborted)
          match canRead with
          | true ->
            let mutable hasItem = true
            while hasItem do
              let (success, result) = channel.Reader.TryRead()
              match success with
              | true ->
                let json = Serialization.serialize result
                let line = sprintf "data: %s\n\n" json
                let bytes = Text.Encoding.UTF8.GetBytes(line)
                do! writer.WriteAsync(bytes, 0, bytes.Length)
                do! writer.FlushAsync()
                resultsEmitted <- resultsEmitted + 1L
                Features.LiveTesting.LiveTestingInstrumentation.streamResultsEmitted.Add(1L)
              | false ->
                hasItem <- false
          | false ->
            keepReading <- false

        let doneBytes = Text.Encoding.UTF8.GetBytes("event: done\ndata: {}\n\n")

        // Coverage is recorded by the instrumented assemblies IN the process that ran the tests: ask its agent.
        match takeCoverage () with
        | HostAgent.AgentAnswered(HostAgent.CoverageTaken(count, words)) ->
          // Packed base64 words on the wire, not one JSON bool per probe —
          // see CoverageBitmap.toBase64 for the size rationale.
          let coverageJson = Serialization.serialize {| count = count; words = words |}
          let coverageLine = sprintf "event: coverage\ndata: %s\n\n" coverageJson
          let coverageBytes = Text.Encoding.UTF8.GetBytes(coverageLine)
          do! writer.WriteAsync(coverageBytes, 0, coverageBytes.Length)
          do! writer.FlushAsync()
        | HostAgent.AgentAnswered HostAgent.NoCoverage -> ()
        | HostAgent.AgentUnavailable reason -> Log.warn "[WorkerHttpTransport] no coverage for this run: %s" reason

        do! writer.WriteAsync(doneBytes, 0, doneBytes.Length)
        do! writer.FlushAsync()

        streamSw.Stop()
        Features.LiveTesting.LiveTestingInstrumentation.streamDurationMs.Record(streamSw.Elapsed.TotalMilliseconds)
        match isNull streamActivity with
        | false ->
          streamActivity.SetTag("stream.results_emitted", resultsEmitted) |> ignore
          streamActivity.SetTag("stream.duration_ms", streamSw.Elapsed.TotalMilliseconds) |> ignore
          streamActivity.Stop()
          streamActivity.Dispose()
        | true -> ()
      })) |> ignore

      map Routes.testDiscovery (Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetTestDiscovery rid)
      })) |> ignore

      map Routes.evalLiveTestFile (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let filePath = (jsonProp doc "filePath").GetString()
        let content = (jsonProp doc "content").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.EvalLiveTestFile(filePath, content, rid))
      })) |> ignore

      map Routes.instrumentationMaps (Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetInstrumentationMaps rid)
      })) |> ignore

      map Routes.runApp (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        let previous = SageFs.WorkerProtocol.Serialization.deserialize<AppRun.PreviousAddress> ((jsonProp doc "previous").GetRawText())
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.RunApp(project, previous, rid))
      })) |> ignore

      map Routes.stopApp (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let scope = SageFs.WorkerProtocol.Serialization.deserialize<AppRun.StopScope> ((jsonProp doc "scope").GetRawText())
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.StopApp(scope, rid))
      })) |> ignore

      map Routes.awaitAppChange (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let runId = (jsonProp doc "runId").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.AwaitAppChange(runId, rid))
      })) |> ignore

      map Routes.shutdown (Func<HttpContext, Task>(fun ctx ->
        respond' ctx WorkerMessage.Shutdown)) |> ignore

      // Session context endpoint
      map Routes.warmupContext (Func<HttpContext, Task>(fun ctx -> task {
        let wCtx = getWarmupContext ()
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize wCtx)
      })) |> ignore

      // Hot-reload state endpoints
      map Routes.hotReload (Func<HttpContext, Task>(fun ctx -> task {
        let state = !hotReloadStateRef
        let files =
          projectFiles
          |> List.map (fun f -> {| path = f; watched = HotReloadState.isWatched f state |})
        // `kept` lists the initializers saves have kept waiting for a reset,
        // so the dashboard can show the notice and the button.
        let kept =
          keptState.Pending ()
          |> List.map (fun k -> {| binding = k.Binding; keptValue = k.KeptValue; newInitializer = k.NewInitializer |})
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| files = files; watchedCount = HotReloadState.watchedCount state; kept = kept |})
      })) |> ignore

      map Routes.hotReloadResetState (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let binding = (jsonProp doc "binding").GetString() |> Option.ofObj |> Option.defaultValue ""
        let! outcome = keptState.Reset binding |> Async.StartAsTask
        ctx.Response.StatusCode <- Features.KeptState.ResetOutcome.status outcome
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Features.KeptState.ResetOutcome.toJson outcome)
      })) |> ignore

      map Routes.hotReloadToggle (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let path = (jsonProp doc "path").GetString()
        hotReloadStateRef.Value <- HotReloadState.toggle path !hotReloadStateRef
        let isNowWatched = HotReloadState.isWatched path !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| path = path; watched = isNowWatched |})
      })) |> ignore

      map Routes.hotReloadWatchAll (Func<HttpContext, Task>(fun ctx -> task {
        hotReloadStateRef.Value <- HotReloadState.watchAll projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      map Routes.hotReloadUnwatchAll (Func<HttpContext, Task>(fun ctx -> task {
        hotReloadStateRef.Value <- HotReloadState.unwatchAll !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| watchedCount = 0 |})
      })) |> ignore

      map Routes.hotReloadWatchProject (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        hotReloadStateRef.Value <- HotReloadState.watchByDirectory project projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| project = project; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      map Routes.hotReloadUnwatchProject (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        hotReloadStateRef.Value <- HotReloadState.unwatchByDirectory project !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| project = project; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      map Routes.hotReloadWatchDirectory (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let dir = (jsonProp doc "directory").GetString()
        hotReloadStateRef.Value <- HotReloadState.watchByDirectory dir projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        let watched = HotReloadState.watchedInDirectory dir !hotReloadStateRef
        do! ctx.Response.WriteAsync(Serialization.serialize {| directory = dir; watchedCount = List.length watched |})
      })) |> ignore

      map Routes.hotReloadUnwatchDirectory (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let dir = (jsonProp doc "directory").GetString()
        hotReloadStateRef.Value <- HotReloadState.unwatchByDirectory dir !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| directory = dir; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      // Past the server boundary (roast: "Carry ReloadOutcome past the server
      // boundary"): the same payload a live DevReload SSE subscriber gets for
      // the last terminal save, for a client that only polls plain HTTP.
      map Routes.hotReloadLastOutcome (Func<HttpContext, Task>(fun ctx -> task {
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(DevReload.LastReload.json ())
      })) |> ignore

      // DevReload SSE endpoint — browsers connect here for hot-reload notifications.
      // Long-lived: sends heartbeats every 15s, compiling/reload/failed events as they happen.
      // Cross-origin (user's app port → worker port), so CORS header is required.
      //
      // Chesterton's fence: pre-allocated byte arrays avoid per-event allocation.
      // The heartbeat fires every 15s for the lifetime of every connected browser tab —
      // that's a long-lived allocation pattern worth eliminating.
      let heartbeatBytes = Text.Encoding.UTF8.GetBytes(": heartbeat\n\n")
      let connectedBytes = Text.Encoding.UTF8.GetBytes(": connected\n\nretry: 1000\n\n")
      let compilingBytes = Text.Encoding.UTF8.GetBytes(DevReload.DevReloadEvent.sseData (DevReload.Compiling None))

      map Routes.devReload (Func<HttpContext, Task>(fun ctx -> task {
        ctx.Response.ContentType <- "text/event-stream"
        ctx.Response.Headers["Cache-Control"] <- "no-cache"
        ctx.Response.Headers["Connection"] <- "keep-alive"
        ctx.Response.Headers["X-Accel-Buffering"] <- "no"
        // CORS is set by the origin-gate middleware above (reflects the specific
        // loopback origin — never a wildcard). Nothing to do here.
        do! ctx.Response.Body.FlushAsync()

        let id = Guid.NewGuid().ToString("N")
        Log.debug "[SSE] Client %s connecting from %s" id (ctx.Connection.RemoteIpAddress |> Option.ofObj |> Option.map string |> Option.defaultValue "unknown")

        do! ctx.Response.Body.WriteAsync(ReadOnlyMemory connectedBytes)
        do! ctx.Response.Body.FlushAsync()

        let reader = DevReload.registerClient id
        // Chesterton's fence: use ONLY RequestAborted.Register for cleanup.
        // Previously had both `use cleanup` IDisposable AND RequestAborted.Register,
        // which caused double-unregister on cancellation. unregisterClient is idempotent
        // (second call is a no-op) but the double-fire is confusing and the IDisposable
        // cleanup is unnecessary when RequestAborted covers all exit paths.
        use _ = ctx.RequestAborted.Register(fun () ->
          Log.debug "[SSE] Client %s request aborted" id
          DevReload.unregisterClient id)

        try
          let ct = ctx.RequestAborted
          while not ct.IsCancellationRequested do
            let mutable evt = DevReload.Compiling None
            let! hasEvent =
              task {
                try
                  use cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
                  use linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct)
                  return! reader.WaitToReadAsync(linked.Token).AsTask()
                with
                | :? OperationCanceledException -> return false
              }
            match hasEvent with
            | true ->
              while reader.TryRead(&evt) do
                // The wire format lives with the event type (DevReload.fs), so
                // adding a terminal outcome can never leave a transport behind
                // rendering a stale shape. Only the bare "compiling" frame is
                // pre-allocated — it is the one payload with no dynamic part.
                let bytes =
                  match evt with
                  | DevReload.Compiling None -> compilingBytes
                  | other -> Text.Encoding.UTF8.GetBytes(DevReload.DevReloadEvent.sseData other)
                do! ctx.Response.Body.WriteAsync(ReadOnlyMemory bytes)
                do! ctx.Response.Body.FlushAsync()
            | false ->
              do! ctx.Response.Body.WriteAsync(ReadOnlyMemory heartbeatBytes)
              do! ctx.Response.Body.FlushAsync()
        with
        // Chesterton's fence: match Dashboard's exception handling pattern.
        // All of these occur in real-world ASP.NET SSE when browsers disconnect
        // mid-write, proxies reset connections, or Kestrel's response stream
        // enters an invalid state. Without catching them, the SSE loop dies with
        // a noisy stack trace in the logs.
        | :? Tasks.TaskCanceledException -> Log.debug "[SSE] Client %s disconnected (task cancelled)" id
        | :? OperationCanceledException -> Log.debug "[SSE] Client %s disconnected (operation cancelled)" id
        | :? IOException as ex -> Log.debug "[SSE] Client %s disconnected (IO: %s)" id ex.Message
        | :? ObjectDisposedException -> Log.debug "[SSE] Client %s disconnected (response disposed)" id
        | :? ArgumentOutOfRangeException as ex -> Log.debug "[SSE] Client %s write error: %s" id ex.Message
        | :? InvalidOperationException as ex -> Log.debug "[SSE] Client %s invalid op: %s" id ex.Message
      })) |> ignore

      do! app.StartAsync()

      let server = app.Services.GetRequiredService<IServer>()
      let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
      match addresses |> Seq.tryHead with
      | Some actualUrl -> return new HttpWorkerServer(actualUrl, app)
      | None -> return failwith "Worker server started but reported no addresses"
    }

  /// Create a SessionProxy backed by HTTP to the given base URL.
  /// Delegates to HttpWorkerClient in SageFs.Core.
  let httpProxy = HttpWorkerClient.httpProxy
