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

  /// How a run's coverage is attributed. Probes are one shared array in the process, so a reading taken
  /// while two tests run belongs to both: attribution to ONE test needs tests that do not overlap.
  [<RequireQualifiedAccess>]
  type private CoverageAttribution =
    /// The process records coverage: tests run one at a time, a reading is taken after each, and it
    /// belongs to the test that just ran.
    | PerTest
    /// Nothing here records coverage: tests run in parallel and no reading is sent.
    | NotRecorded

  /// What a run puts on the stream, in the order it happened.
  [<RequireQualifiedAccess>]
  type private StreamItem =
    | Result of Features.LiveTesting.TestRunResult
    | Coverage of HttpWorkerClient.CoverageFrame

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
        // `IDisposable.Dispose` cannot await, and `IAsyncDisposable` above is what an awaiting caller uses; this
        // fallback is bounded by `workerHttpServerStop`, so a stop that hangs gives up and no wait outlives it.
        use cts = new System.Threading.CancellationTokenSource(Timeouts.workerHttpServerStop)
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

  /// A request field that is not the shape the wire says it is: a 400 that names the field and why.
  let rejectUnreadable (ctx: HttpContext) (field: string) (error: SageFsError) : Task = task {
    ctx.Response.StatusCode <- 400
    ctx.Response.ContentType <- "application/json"
    do! ctx.Response.WriteAsync(Serialization.serialize {| message = sprintf "the request's %s could not be read: %s" field (SageFsError.describe error) |})
  }

  /// The worker's route table: every case of `WorkerRoute` (SageFs.Core/WorkerRoute.fs), the one home of the wire paths.
  /// `startServer` maps endpoints only through it, and the origin gate classifies requests from it.
  let routes : WorkerRoute list = WorkerRoute.all

  /// Declared access of every GET route, matched the way ASP.NET routing
  /// matches: case-insensitively, trailing slash optional.
  let private getAccessByPath =
    let d = Dictionary<string, RouteAccess>(StringComparer.OrdinalIgnoreCase)
    for route in routes do
      match WorkerRoute.getAccess route with
      | Some access -> d[WorkerRoute.path route] <- access
      | None -> ()
    d

  /// Classify one request. Method-driven: every non-GET/HEAD request is
  /// Mutating, whatever its path — so a newly mapped POST (or a path the table
  /// does not know, or `/EVAL` in any casing) can never land on a weaker check.
  /// GET/HEAD take the declared access of their route; unknown GETs 404 and
  /// are read-only.
  let classify (route: RequestRoute) : RouteAccess =
    let path = route.Path
    match route.HttpMethod.Trim().ToUpperInvariant() with
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
      match classify { HttpMethod = request.Method; Path = path } with
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

  /// What a route that threw tells its caller: the route, the exception's type and its message (the message of a
  /// failed assembly load names the assembly). The full exception, with its stack, goes to the worker log.
  let describeRouteFailure (route: RequestRoute) (ex: exn) : string =
    sprintf "the worker threw while handling %s %s: %s: %s" route.HttpMethod route.Path (ex.GetType().Name) ex.Message

  /// Outermost middleware: a route that throws is logged through the worker's own log (this host has no `ILogger`
  /// provider, so ASP.NET's own record of the exception goes nowhere) and answered as a 500 whose body says what threw.
  /// Before this, such a request reached the daemon as a bare "500 (Internal Server Error)" with nothing in the worker log.
  let workerFailureGuard (ctx: HttpContext) (next: Func<Task>) = task {
    try
      do! next.Invoke()
    with
    | ex when not (ex :? OperationCanceledException) ->
      let path = ctx.Request.Path.Value |> Option.ofObj |> Option.defaultValue ""
      let text = describeRouteFailure { HttpMethod = ctx.Request.Method; Path = path } ex
      Log.error "[WorkerHttp] %s\n%s" text (ex.ToString())
      match ctx.Response.HasStarted with
      | true -> ()
      | false ->
        ctx.Response.StatusCode <- 500
        ctx.Response.ContentType <- "text/plain; charset=utf-8"
        do! ctx.Response.WriteAsync text
  }

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

  /// The worker's credential gate: every route but the browser's dev-reload stream needs the secret the daemon gave this
  /// worker at spawn (`WorkerAuth`). Before it, anything that could reach loopback could POST /eval.
  let workerCredentialGate (expected: WorkerAuth.Expected) (ctx: HttpContext) (next: Func<Task>) = task {
    let path = ctx.Request.Path.Value |> Option.ofObj |> Option.defaultValue ""
    let credential = WorkerAuth.credentialOfRequest { HttpMethod = ctx.Request.Method; Path = path }
    let authorization =
      match ctx.Request.Headers.TryGetValue "Authorization" with
      | true, v when v.Count > 0 -> Some (string v)
      | _ -> None
    match WorkerAuth.decide expected credential authorization with
    | WorkerAuth.Verdict.Admit
    | WorkerAuth.Verdict.Exempt
    | WorkerAuth.Verdict.Unsecured -> do! next.Invoke()
    | WorkerAuth.Verdict.Refuse refusal ->
      ctx.Response.StatusCode <- WorkerAuth.Refusal.statusCode refusal
      ctx.Response.Headers["WWW-Authenticate"] <- "Bearer"
      do! ctx.Response.WriteAsync(WorkerAuth.Refusal.describe refusal)
  }

  /// Start a Kestrel HTTP server dispatching to the given handler.
  /// Pass port=0 for OS-assigned dynamic port.
  /// `beforeListen` runs after the app is built and its routes are mapped, right before Kestrel binds `port`. The worker
  /// frees the port it reserved there, so the time in which another process could take that port is only the bind.
  let startServerAuthenticated
    (expected: WorkerAuth.Expected)
    (runHold: TestRunHold)
    (onShutdown: unit -> unit)
    (beforeListen: unit -> unit)
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
      // This host deliberately has NO ILogger provider: the worker's stdout
      // carries the WORKER_PORT=/WARMUP_PROGRESS=/APP_OUTPUT= protocol and
      // ASP.NET's own console logging would interleave with it. So anything
      // written through `ILogger` (ASP.NET plumbing) goes nowhere. SageFs's own
      // diagnostics do NOT go through `ILogger`: they go through `SageFs.Utils.Log`,
      // whose sinks the process entry point points at
      // `<data dir>/workers/<sessionId>.log` (`WorkerLogFile.tryInstall`) and at
      // stderr. This function must leave those sinks alone.
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

      app.Use(Func<HttpContext, Func<Task>, Task>(fun ctx next ->
        workerFailureGuard ctx next :> Task)) |> ignore

      app.UseResponseCompression() |> ignore

      // Origin/CSRF gate for the worker HTTP surface — see workerOriginGuard.
      app.Use(Func<HttpContext, Func<Task>, Task>(fun ctx next ->
        workerOriginGuard ctx next :> Task)) |> ignore

      app.Use(Func<HttpContext, Func<Task>, Task>(fun ctx next ->
        workerCredentialGate expected ctx next :> Task)) |> ignore

      let inline respond' ctx msg = respond handler ctx msg

      // Every endpoint is mapped through its `WorkerRoute` case, so the verb it is
      // mapped with and the access the origin gate applies come from one table.
      let mapped = HashSet<WorkerRoute>()
      let map (route: WorkerRoute) (endpoint: Func<HttpContext, Task>) =
        mapped.Add route |> ignore
        match WorkerRoute.getAccess route with
        | Some _ -> app.MapGet(WorkerRoute.path route, endpoint)
        | None -> app.MapPost(WorkerRoute.path route, endpoint)

      // Diagnostic: ThreadPool state for measuring starvation
      map WorkerRoute.DiagThreadpool (Func<HttpContext, Task>(fun ctx -> task {
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

      map WorkerRoute.Status (Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetStatus rid)
      })) |> ignore

      map WorkerRoute.LiveValues (Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetLiveValues rid)
      })) |> ignore

      map WorkerRoute.LiveValuesEvaluate (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let binding = (jsonProp doc "binding").GetString()
        let path = [ for label in (jsonProp doc "path").EnumerateArray() -> label.GetString() ]
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.EvaluateLiveMember(binding, path, rid))
      })) |> ignore

      map WorkerRoute.LiveValuesMode (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let name = (jsonProp doc "mode").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        match ValueWalk.parse name with
        | Result.Error unknown -> return! rejectUnreadable ctx "mode" (SageFsError.JsonParseError("the worker wire", ValueWalk.describeUnknown unknown))
        | Result.Ok mode -> return! respond' ctx (WorkerMessage.SetValueWalk(mode, rid))
      })) |> ignore

      map WorkerRoute.Eval (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.EvalCode(code, rid))
      })) |> ignore

      map WorkerRoute.Check (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.CheckCode(code, rid))
      })) |> ignore

      map WorkerRoute.TypecheckSymbols (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let filePath = (jsonProp doc "filePath").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.TypeCheckWithSymbols(code, filePath, rid))
      })) |> ignore

      map WorkerRoute.Completions (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let cursorPos = (jsonProp doc "cursorPos").GetInt32()
        let limit = Serialization.deserialize<SageFs.Features.AutoCompletion.CompletionLimit> ((jsonProp doc "limit").GetRawText())
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.GetCompletions(code, cursorPos, limit, rid))
      })) |> ignore

      map WorkerRoute.References (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let qualifiedName = (jsonProp doc "qualifiedName").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.GetReferences(qualifiedName, rid))
      })) |> ignore

      map WorkerRoute.HostProcess (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.GetHostProcess rid)
      })) |> ignore

      map WorkerRoute.Cancel (Func<HttpContext, Task>(fun ctx ->
        respond' ctx WorkerMessage.CancelEval)) |> ignore

      map WorkerRoute.CancelRequest (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.CancelEvalRequest rid)
      })) |> ignore

      map WorkerRoute.LoadScript (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let filePath = (jsonProp doc "filePath").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.LoadScript(filePath, rid))
      })) |> ignore

      map WorkerRoute.Reset (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.ResetSession rid)
      })) |> ignore

      map WorkerRoute.HardReset (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rebuild = (jsonProp doc "rebuild").GetBoolean()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.HardResetSession(rebuild, rid))
      })) |> ignore

      map WorkerRoute.LevelRepl (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.LevelRepl rid)
      })) |> ignore

      map WorkerRoute.RunTests (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let testsJson = (jsonProp doc "tests").GetRawText()
        match Serialization.tryDeserialize<Features.LiveTesting.TestCase array> testsJson with
        | Result.Error error -> return! rejectUnreadable ctx "tests" error
        | Result.Ok tests ->
          let maxParallelism = (jsonProp doc "maxParallelism").GetInt32()
          let rid = (jsonProp doc "replyId").GetString()
          return! respond' ctx (WorkerMessage.RunTests(tests, maxParallelism, rid))
      })) |> ignore

      map WorkerRoute.RunTestsStream (Func<HttpContext, Task>(fun ctx -> task {
        let streamActivity = Features.LiveTesting.LiveTestingInstrumentation.activitySource.StartActivity("live_testing.stream")
        let streamSw = System.Diagnostics.Stopwatch.StartNew()
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let testsJson = (jsonProp doc "tests").GetRawText()
        match Serialization.tryDeserialize<Features.LiveTesting.TestCase array> testsJson with
        | Result.Error error ->
          match isNull streamActivity with
          | false ->
            streamActivity.Stop()
            streamActivity.Dispose()
          | true -> ()
          do! rejectUnreadable ctx "tests" error
        | Result.Ok tests ->
          let maxParallelism = (jsonProp doc "maxParallelism").GetInt32()

          match isNull streamActivity with
          | false ->
            streamActivity.SetTag("stream.test_count", tests.Length) |> ignore
            streamActivity.SetTag("stream.max_parallelism", maxParallelism) |> ignore
          | true -> ()

          ctx.Response.ContentType <- "text/event-stream"
          ctx.Response.Headers["Cache-Control"] <- "no-cache"
          ctx.Response.Headers["Connection"] <- "keep-alive"

          let channel = System.Threading.Channels.Channel.CreateUnbounded<StreamItem>()
          let mutable resultsEmitted = 0L

          // Coverage is recorded by the instrumented assemblies IN the process that ran the tests: ask its
          // agent. This first reading drains whatever warm-up, discovery or an earlier eval left behind, so
          // the first test's reading is its own, and it says whether anything is recorded here at all.
          let attribution =
            match takeCoverage () with
            | HostAgent.AgentAnswered (HostAgent.CoverageTaken _) -> CoverageAttribution.PerTest
            | HostAgent.AgentAnswered HostAgent.NoCoverage -> CoverageAttribution.NotRecorded
            | HostAgent.AgentUnavailable reason ->
              Log.warn "[WorkerHttpTransport] no coverage for this run: %s" reason
              CoverageAttribution.NotRecorded

          let parallelism =
            match attribution with
            | CoverageAttribution.PerTest -> 1
            | CoverageAttribution.NotRecorded -> maxParallelism

          // A reading is taken the moment a test returns and sent right after that test's result.
          let readings = System.Collections.Concurrent.ConcurrentDictionary<Features.LiveTesting.TestId, HttpWorkerClient.CoverageFrame>()

          let executionTask = task {
            try
              // A run holds the worker's test-run gate for its whole life, so a level that is pending waits for it and a level
              // that is under way holds it back until the host is replaced.
              let! hold = Async.StartAsTask(runHold.Hold(), cancellationToken = ctx.RequestAborted)
              use _hold = hold
              let onResult (result: Features.LiveTesting.TestRunResult) =
                channel.Writer.TryWrite(StreamItem.Result result) |> ignore
                match readings.TryRemove result.TestId with
                | true, frame -> channel.Writer.TryWrite(StreamItem.Coverage frame) |> ignore
                | false, _ -> ()
              let runTest =
                let run = getRunTest()
                match attribution with
                | CoverageAttribution.NotRecorded -> run
                | CoverageAttribution.PerTest ->
                  fun (tc: Features.LiveTesting.TestCase) ->
                    async {
                      let! outcome = run tc
                      match takeCoverage () with
                      | HostAgent.AgentAnswered (HostAgent.CoverageTaken (count, words)) ->
                        readings.[tc.Id] <- { TestId = tc.Id; Count = count; Words = words }
                      | HostAgent.AgentAnswered HostAgent.NoCoverage -> ()
                      | HostAgent.AgentUnavailable reason -> Log.warn "[WorkerHttpTransport] no coverage for %s: %s" tc.FullName reason
                      return outcome
                    }
              // The run ends when it finishes or when no test has reported for a stall window. It is never cut at a clock
              // while it is still reporting: it used to be cancelled two minutes after it started, which left 18529 of a
              // 19067-test run with no result. A stall is logged with the count that did not report.
              let window = Features.LiveTesting.RunStall.effectiveWindow (Timeouts.testRunStallWindow ()) (Timeouts.perTestDefault ())
              try
                let! ended =
                  Features.LiveTesting.RunStall.watch window tests.Length ctx.RequestAborted (fun reported token ->
                    Features.LiveTesting.TestOrchestrator.executeFiltered
                      runTest (fun result -> reported (); onResult result) parallelism tests token)
                  |> Async.StartAsTask
                match ended with
                | Features.LiveTesting.RunStall.RunEnd.Stalled (cause, stalledWindow) ->
                  Log.warn "[run-tests-stream] the run was cancelled: %s" (Features.LiveTesting.RunStall.describeCause stalledWindow cause)
                | Features.LiveTesting.RunStall.RunEnd.Completed
                | Features.LiveTesting.RunStall.RunEnd.Cancelled -> ()
              with ex ->
                System.Diagnostics.Activity.Current
                |> Option.ofObj
                |> Option.iter (fun a -> a.SetTag("error", ex.Message) |> ignore)
                Log.error "[run-tests-stream] execution error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
            finally
              channel.Writer.TryComplete() |> ignore
          }

          // Start execution — don't await, let the channel reader loop drive the SSE stream
          // Not `use`: a Task may only be disposed once it has completed, and this continuation has not
          // necessarily run by the time the stream has been written. Disposing it here threw after the last
          // frame, which the caller saw as "the response ended prematurely" under load.
          executionTask.ContinueWith(fun (t: Threading.Tasks.Task) ->
            match t.IsFaulted with
            | true -> Log.error "[run-tests-stream] unhandled: %s" t.Exception.Message
            | false -> ()
          ) |> ignore

          let writer = ctx.Response.Body
          let mutable keepReading = true
          // A stream that dies half way reaches the caller only as "the response ended prematurely", which says
          // nothing about why, so the reason is logged here, where it is known.
          try
            while keepReading do
              let! canRead = channel.Reader.WaitToReadAsync(ctx.RequestAborted)
              match canRead with
              | true ->
                let mutable hasItem = true
                while hasItem do
                  let (success, item) = channel.Reader.TryRead()
                  match success with
                  | true ->
                    // Packed base64 words on the wire, not one JSON bool per probe: see CoverageBitmap.toBase64.
                    let line =
                      match item with
                      | StreamItem.Result result ->
                        resultsEmitted <- resultsEmitted + 1L
                        Features.LiveTesting.LiveTestingInstrumentation.streamResultsEmitted.Add(1L)
                        sprintf "data: %s\n\n" (Serialization.serialize result)
                      | StreamItem.Coverage frame -> sprintf "event: coverage\ndata: %s\n\n" (Serialization.serialize frame)
                    let bytes = Text.Encoding.UTF8.GetBytes(line)
                    do! writer.WriteAsync(bytes, 0, bytes.Length)
                    do! writer.FlushAsync()
                  | false ->
                    hasItem <- false
              | false ->
                keepReading <- false

            let doneBytes = Text.Encoding.UTF8.GetBytes("event: done\ndata: {}\n\n")

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
          with
          | ex when not (ex :? OperationCanceledException) ->
            Log.error "[run-tests-stream] the stream failed after %d result(s): %s\n%s" resultsEmitted ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
            raise ex
      })) |> ignore

      map WorkerRoute.TestDiscovery (Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetTestDiscovery rid)
      })) |> ignore

      map WorkerRoute.EvalLiveTestFile (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let filePath = (jsonProp doc "filePath").GetString()
        let content = (jsonProp doc "content").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.EvalLiveTestFile(filePath, content, rid))
      })) |> ignore

      map WorkerRoute.InstrumentationMaps (Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetInstrumentationMaps rid)
      })) |> ignore

      map WorkerRoute.RunApp (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        match SageFs.WorkerProtocol.Serialization.tryDeserialize<AppRun.PreviousAddress> ((jsonProp doc "previous").GetRawText()) with
        | Result.Error error -> return! rejectUnreadable ctx "previous" error
        | Result.Ok previous ->
          let rid = (jsonProp doc "replyId").GetString()
          return! respond' ctx (WorkerMessage.RunApp(project, previous, rid))
      })) |> ignore

      map WorkerRoute.StopApp (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        match SageFs.WorkerProtocol.Serialization.tryDeserialize<AppRun.StopScope> ((jsonProp doc "scope").GetRawText()) with
        | Result.Error error -> return! rejectUnreadable ctx "scope" error
        | Result.Ok scope ->
          let rid = (jsonProp doc "replyId").GetString()
          return! respond' ctx (WorkerMessage.StopApp(scope, rid))
      })) |> ignore

      map WorkerRoute.AwaitAppChange (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let runId = (jsonProp doc "runId").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.AwaitAppChange(runId, rid))
      })) |> ignore

      map WorkerRoute.DebugTest (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        match Serialization.tryDeserialize<Features.LiveTesting.TestCase> ((jsonProp doc "test").GetRawText()) with
        | Result.Error error -> return! rejectUnreadable ctx "test" error
        | Result.Ok test ->
          let rid = (jsonProp doc "replyId").GetString()
          return! respond' ctx (WorkerMessage.DebugTestBegin(test, rid))
      })) |> ignore

      map WorkerRoute.DebugTestContinue (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        match Serialization.tryDeserialize<TimeSpan> ((jsonProp doc "park").GetRawText()) with
        | Result.Error error -> return! rejectUnreadable ctx "park" error
        | Result.Ok park ->
          let ticket = (jsonProp doc "ticket").GetString()
          let rid = (jsonProp doc "replyId").GetString()
          return! respond' ctx (WorkerMessage.DebugTestContinue(ticket, park, rid))
      })) |> ignore

      map WorkerRoute.SaveSource (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        match Serialization.tryDeserialize<Features.TrunkFollow.SaveSource> ((jsonProp doc "source").GetRawText()) with
        | Result.Error error -> return! rejectUnreadable ctx "source" error
        | Result.Ok source ->
          let rid = (jsonProp doc "replyId").GetString()
          return! respond' ctx (WorkerMessage.SetSaveSource(source, rid))
      })) |> ignore

      map WorkerRoute.ApplySaves (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        match Serialization.tryDeserialize<Features.TrunkFollow.SavedFile list> ((jsonProp doc "files").GetRawText()) with
        | Result.Error error -> return! rejectUnreadable ctx "files" error
        | Result.Ok files ->
          let rid = (jsonProp doc "replyId").GetString()
          return! respond' ctx (WorkerMessage.ApplySaves(files, rid))
      })) |> ignore

      map WorkerRoute.Shutdown (Func<HttpContext, Task>(fun ctx -> task {
        do! respond' ctx WorkerMessage.Shutdown
        onShutdown ()
      })) |> ignore

      // Session context endpoint
      map WorkerRoute.SessionWarmupContext (Func<HttpContext, Task>(fun ctx -> task {
        let wCtx = getWarmupContext ()
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize wCtx)
      })) |> ignore

      // Hot-reload state endpoints
      map WorkerRoute.HotReload (Func<HttpContext, Task>(fun ctx -> task {
        let state = !hotReloadStateRef
        let files =
          projectFiles
          |> List.map (fun f -> {| path = f; watched = HotReloadState.isWatched f state |})
        // `kept` lists the initializers saves have kept waiting for a reset,
        // so the dashboard can show the notice and the button.
        let kept =
          keptState.Pending ()
          |> List.map (fun k -> {| binding = k.Binding; keptValue = k.KeptValue; newInitializer = k.NewInitializer |})
        // `reflectionReads` is rule 2's mode and the hot-loop questions, so the
        // dashboard shows them from the same fetch. Without an agent it says why.
        let reflectionReads : obj =
          match keptState.ReflectionReads () with
          | Result.Ok report ->
            use doc = JsonDocument.Parse(Features.KeptState.ReflectionReadsJson.render report)
            box (doc.RootElement.Clone())
          | Result.Error why -> box {| unavailable = Features.KeptState.ReflectionReadsError.describe why |}
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| files = files; watchedCount = HotReloadState.watchedCount state; kept = kept; reflectionReads = reflectionReads |})
      })) |> ignore

      map WorkerRoute.HotReloadReflectionMode (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let requested = (jsonProp doc "mode").GetString() |> Option.ofObj |> Option.defaultValue ""
        ctx.Response.ContentType <- "application/json"
        match SageFs.Middleware.ValueReads.ReflectionReadMode.parse requested with
        | Result.Error unknown ->
          ctx.Response.StatusCode <- 400
          do! ctx.Response.WriteAsync(Serialization.serialize {| message = SageFs.Middleware.ValueReads.ReflectionReadMode.describeUnknown unknown |})
        | Result.Ok mode ->
          match keptState.SetReflectionMode mode with
          | Result.Error why ->
            ctx.Response.StatusCode <- 503
            do! ctx.Response.WriteAsync(Serialization.serialize {| message = Features.KeptState.ReflectionReadsError.describe why |})
          | Result.Ok report ->
            do! ctx.Response.WriteAsync(Features.KeptState.ReflectionReadsJson.render report)
      })) |> ignore

      map WorkerRoute.HotReloadResetState (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let binding = (jsonProp doc "binding").GetString() |> Option.ofObj |> Option.defaultValue ""
        let! outcome = keptState.Reset binding |> Async.StartAsTask
        ctx.Response.StatusCode <- Features.KeptState.ResetOutcome.status outcome
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Features.KeptState.ResetOutcome.toJson outcome)
      })) |> ignore

      map WorkerRoute.HotReloadToggle (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let path = (jsonProp doc "path").GetString()
        hotReloadStateRef.Value <- HotReloadState.toggle path !hotReloadStateRef
        let isNowWatched = HotReloadState.isWatched path !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| path = path; watched = isNowWatched |})
      })) |> ignore

      map WorkerRoute.HotReloadWatchAll (Func<HttpContext, Task>(fun ctx -> task {
        hotReloadStateRef.Value <- HotReloadState.watchAll projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      map WorkerRoute.HotReloadUnwatchAll (Func<HttpContext, Task>(fun ctx -> task {
        hotReloadStateRef.Value <- HotReloadState.unwatchAll !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| watchedCount = 0 |})
      })) |> ignore

      map WorkerRoute.HotReloadWatchProject (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        hotReloadStateRef.Value <- HotReloadState.watchByDirectory project projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| project = project; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      map WorkerRoute.HotReloadUnwatchProject (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        hotReloadStateRef.Value <- HotReloadState.unwatchByDirectory project !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| project = project; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      map WorkerRoute.HotReloadWatchDirectory (Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let dir = (jsonProp doc "directory").GetString()
        hotReloadStateRef.Value <- HotReloadState.watchByDirectory dir projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        let watched = HotReloadState.watchedInDirectory dir !hotReloadStateRef
        do! ctx.Response.WriteAsync(Serialization.serialize {| directory = dir; watchedCount = List.length watched |})
      })) |> ignore

      map WorkerRoute.HotReloadUnwatchDirectory (Func<HttpContext, Task>(fun ctx -> task {
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
      map WorkerRoute.HotReloadLastOutcome (Func<HttpContext, Task>(fun ctx -> task {
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(DevReload.LastReload.json ())
      })) |> ignore

      // DevReload SSE endpoint — browsers connect here for hot-reload notifications.
      // Long-lived: sends heartbeats every Timeouts.reloadStreamHeartbeat, compiling/reload/failed events as they happen.
      // Cross-origin (user's app port → worker port), so CORS header is required.
      //
      // Chesterton's fence: pre-allocated byte arrays avoid per-event allocation.
      // The heartbeat fires every Timeouts.reloadStreamHeartbeat for the lifetime of every connected browser tab —
      // that's a long-lived allocation pattern worth eliminating.
      let heartbeatBytes = Text.Encoding.UTF8.GetBytes(": heartbeat\n\n")
      let connectedBytes = Text.Encoding.UTF8.GetBytes(": connected\n\nretry: 1000\n\n")
      let compilingBytes = Text.Encoding.UTF8.GetBytes(DevReload.DevReloadEvent.sseData (DevReload.Compiling None))

      map WorkerRoute.DevReloadStream (Func<HttpContext, Task>(fun ctx -> task {
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
                  use cts = new CancellationTokenSource(Timeouts.reloadStreamHeartbeat)
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

      // A `WorkerRoute` case with no endpoint above would answer 404 to the daemon at runtime, so the worker refuses to start.
      match routes |> List.filter (fun route -> not (mapped.Contains route)) with
      | [] -> ()
      | unserved ->
        failwithf "The worker has no endpoint for these routes: %s" (unserved |> List.map WorkerRoute.path |> String.concat ", ")

      beforeListen ()
      do! app.StartAsync()

      let server = app.Services.GetRequiredService<IServer>()
      let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
      match addresses |> Seq.tryHead with
      | Some actualUrl -> return new HttpWorkerServer(actualUrl, app)
      | None -> return failwith "Worker server started but reported no addresses"
    }

  /// The worker's own server: the credential is the secret the daemon put in this process's environment, if any.
  let startServerGated (runHold: TestRunHold) =
    startServerAuthenticated (WorkerAuth.readExpectedOnce.Force()) runHold

  /// A server with no test-run gate: every run is admitted at once. The worker's own host passes its gate to `startServerGated`.
  let startServerWithShutdown
    (onShutdown: unit -> unit)
    (beforeListen: unit -> unit)
    (handler: WorkerMessage -> Async<WorkerResponse>)
    (hotReloadStateRef: HotReloadState.T ref)
    (keptState: Features.KeptState.Access)
    (projectFiles: string list)
    (getWarmupContext: unit -> WarmupContext)
    (getRunTest: unit -> Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>)
    (takeCoverage: unit -> HostAgent.AgentReply<HostAgent.CoverageReading>)
    (port: int)
    : Task<HttpWorkerServer> =
    startServerGated TestRunHold.none onShutdown beforeListen handler hotReloadStateRef keptState projectFiles getWarmupContext getRunTest takeCoverage port

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
    startServerWithShutdown (fun () -> ()) ignore handler hotReloadStateRef keptState projectFiles getWarmupContext getRunTest takeCoverage port

  /// Create a SessionProxy backed by HTTP to the given base URL.
  /// Delegates to HttpWorkerClient in SageFs.Core.
  let httpProxy = HttpWorkerClient.httpProxy
