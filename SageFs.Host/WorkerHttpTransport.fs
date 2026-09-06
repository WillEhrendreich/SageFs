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

  /// True for paths that execute F# or mutate worker state — the endpoints a
  /// cross-site browser page must never reach. (`/diag/threadpool` and
  /// `/status` are read-only and intentionally absent.)
  let private isMutatingOrRpcPath (path: string) =
    path.StartsWith("/eval", StringComparison.Ordinal)
    || path.StartsWith("/check", StringComparison.Ordinal)
    || path.StartsWith("/typecheck-symbols", StringComparison.Ordinal)
    || path.StartsWith("/completions", StringComparison.Ordinal)
    || path.StartsWith("/shutdown", StringComparison.Ordinal)
    || path.StartsWith("/reset", StringComparison.Ordinal)
    || path.StartsWith("/hard-reset", StringComparison.Ordinal)
    || path.StartsWith("/cancel", StringComparison.Ordinal)
    || path.StartsWith("/load-script", StringComparison.Ordinal)
    || path.StartsWith("/run-tests", StringComparison.Ordinal)
    || path.StartsWith("/run-tests-stream", StringComparison.Ordinal)
    || path.StartsWith("/hotreload/", StringComparison.Ordinal)

  /// Origin/CSRF gate for the worker HTTP surface — the F#-executing server.
  ///
  /// The daemon proxies to the worker with NO browser headers (loopback Host,
  /// no Origin/Sec-Fetch-Site), so those requests pass. A browser page is the
  /// only realistic attacker: cross-site fetches carry Sec-Fetch-Site:
  /// cross-site and a foreign Origin. Fail closed on those for every endpoint
  /// except the DevReload SSE stream, which the user's local dev app (a
  /// loopback origin) legitimately reads cross-origin — that one reflects the
  /// specific loopback origin instead of `*`.
  let workerOriginGuard (ctx: HttpContext) (next: Func<Task>) = task {
    let hostHeader =
      match ctx.Request.Host.HasValue with
      | true -> Some ctx.Request.Host.Host
      | false -> None
    let secFetchSite =
      match ctx.Request.Headers.TryGetValue("Sec-Fetch-Site") with
      | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace(string v)) -> Some (string v)
      | _ -> None
    let origin =
      match ctx.Request.Headers.TryGetValue("Origin") with
      | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace(string v)) -> Some (string v)
      | _ -> None
    // Non-loopback Host = DNS rebinding / proxy — reject everything.
    match hostHeader with
    | Some h when not (SageFs.Server.HttpOriginGuard.isLoopbackHost h) ->
      ctx.Response.StatusCode <- 403
      do! ctx.Response.WriteAsync("Forbidden: non-loopback Host")
    | _ ->
      let path = ctx.Request.Path.Value |> Option.ofObj |> Option.defaultValue ""
      let isSse = path.StartsWith("/__sagefs__/reload", StringComparison.Ordinal)
      match secFetchSite, origin with
      // Browser signals absent: daemon proxy, curl, editors — allow.
      | None, None -> do! next.Invoke()
      // The SSE stream is the one surface a browser legitimately reads
      // cross-origin (user's dev app on a loopback origin). Reject remote
      // origins; echo the specific loopback origin, never *.
      | _, _ when isSse ->
        match origin with
        | Some o when SageFs.Server.HttpOriginGuard.isLoopbackOrigin o ->
          ctx.Response.Headers["Access-Control-Allow-Origin"] <- o
          do! next.Invoke()
        | Some o ->
          ctx.Response.StatusCode <- 403
          do! ctx.Response.WriteAsync(sprintf "Forbidden: non-loopback Origin %s" o)
        | None -> do! next.Invoke()
      // Mutating/RPC endpoints: any cross-site signal or foreign origin is
      // rejected before it can execute F# or mutate watch state.
      | _ when isMutatingOrRpcPath path ->
        match secFetchSite with
        | Some site when site <> "same-origin" && site <> "same-site" && site <> "none" ->
          ctx.Response.StatusCode <- 403
          do! ctx.Response.WriteAsync(sprintf "Forbidden: cross-site Sec-Fetch-Site %s" site)
        | _ ->
          match origin with
          | Some o when not (SageFs.Server.HttpOriginGuard.isLoopbackOrigin o) ->
            ctx.Response.StatusCode <- 403
            do! ctx.Response.WriteAsync(sprintf "Forbidden: non-loopback Origin %s" o)
          | _ -> do! next.Invoke()
      // Read-only GETs (/status, /hotreload, /test-discovery, ...) with
      // browser signals: allow same-origin/loopback-origin, reject foreign.
      | _ ->
        match origin with
        | Some o when not (SageFs.Server.HttpOriginGuard.isLoopbackOrigin o) ->
          ctx.Response.StatusCode <- 403
          do! ctx.Response.WriteAsync(sprintf "Forbidden: non-loopback Origin %s" o)
        | _ -> do! next.Invoke()
  }

  /// Start a Kestrel HTTP server dispatching to the given handler.
  /// Pass port=0 for OS-assigned dynamic port.
  let startServer
    (handler: WorkerMessage -> Async<WorkerResponse>)
    (hotReloadStateRef: HotReloadState.T ref)
    (projectFiles: string list)
    (getWarmupContext: unit -> WarmupContext)
    (getRunTest: unit -> Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>)
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

      // Diagnostic: ThreadPool state for measuring starvation
      app.MapGet("/diag/threadpool", Func<HttpContext, Task>(fun ctx -> task {
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

      app.MapGet("/status", Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetStatus rid)
      })) |> ignore

      app.MapPost("/eval", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.EvalCode(code, rid))
      })) |> ignore

      app.MapPost("/check", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.CheckCode(code, rid))
      })) |> ignore

      app.MapPost("/typecheck-symbols", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let filePath = (jsonProp doc "filePath").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.TypeCheckWithSymbols(code, filePath, rid))
      })) |> ignore

      app.MapPost("/completions", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let code = (jsonProp doc "code").GetString()
        let cursorPos = (jsonProp doc "cursorPos").GetInt32()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.GetCompletions(code, cursorPos, rid))
      })) |> ignore

      app.MapPost("/cancel", Func<HttpContext, Task>(fun ctx ->
        respond' ctx WorkerMessage.CancelEval)) |> ignore

      app.MapPost("/load-script", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let filePath = (jsonProp doc "filePath").GetString()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.LoadScript(filePath, rid))
      })) |> ignore

      app.MapPost("/reset", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.ResetSession rid)
      })) |> ignore

      app.MapPost("/hard-reset", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let rebuild = (jsonProp doc "rebuild").GetBoolean()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.HardResetSession(rebuild, rid))
      })) |> ignore

      app.MapPost("/run-tests", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let testsJson = (jsonProp doc "tests").GetRawText()
        let tests = Serialization.deserialize<Features.LiveTesting.TestCase array> testsJson
        let maxParallelism = (jsonProp doc "maxParallelism").GetInt32()
        let rid = (jsonProp doc "replyId").GetString()
        return! respond' ctx (WorkerMessage.RunTests(tests, maxParallelism, rid))
      })) |> ignore

      app.MapPost("/run-tests-stream", Func<HttpContext, Task>(fun ctx -> task {
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

        // Collect IL coverage hits from instrumented assemblies
        let loadedAssemblies =
          System.AppDomain.CurrentDomain.GetAssemblies()
          |> Array.filter (fun a ->
            try not a.IsDynamic && not (isNull a.Location) && a.Location <> ""
            with _ -> false)
        match Features.LiveTesting.CoverageInstrumenter.discoverAndCollectHits loadedAssemblies with
        | Some hits ->
          let coverageJson = Serialization.serialize {| hits = hits |}
          let coverageLine = sprintf "event: coverage\ndata: %s\n\n" coverageJson
          let coverageBytes = Text.Encoding.UTF8.GetBytes(coverageLine)
          do! writer.WriteAsync(coverageBytes, 0, coverageBytes.Length)
          do! writer.FlushAsync()
          // Reset hits for next test run
          Features.LiveTesting.CoverageInstrumenter.discoverAndResetHits loadedAssemblies
        | None -> ()

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

      app.MapGet("/test-discovery", Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetTestDiscovery rid)
      })) |> ignore

      app.MapGet("/instrumentation-maps", Func<HttpContext, Task>(fun ctx -> task {
        let rid = ctx.Request.Query["replyId"].ToString()
        return! respond' ctx (WorkerMessage.GetInstrumentationMaps rid)
      })) |> ignore

      app.MapPost("/shutdown", Func<HttpContext, Task>(fun ctx ->
        respond' ctx WorkerMessage.Shutdown)) |> ignore

      // Session context endpoint
      app.MapGet("/warmup-context", Func<HttpContext, Task>(fun ctx -> task {
        let wCtx = getWarmupContext ()
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize wCtx)
      })) |> ignore

      // Hot-reload state endpoints
      app.MapGet("/hotreload", Func<HttpContext, Task>(fun ctx -> task {
        let state = !hotReloadStateRef
        let files =
          projectFiles
          |> List.map (fun f -> {| path = f; watched = HotReloadState.isWatched f state |})
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| files = files; watchedCount = HotReloadState.watchedCount state |})
      })) |> ignore

      app.MapPost("/hotreload/toggle", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let path = (jsonProp doc "path").GetString()
        hotReloadStateRef.Value <- HotReloadState.toggle path !hotReloadStateRef
        let isNowWatched = HotReloadState.isWatched path !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| path = path; watched = isNowWatched |})
      })) |> ignore

      app.MapPost("/hotreload/watch-all", Func<HttpContext, Task>(fun ctx -> task {
        hotReloadStateRef.Value <- HotReloadState.watchAll projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      app.MapPost("/hotreload/unwatch-all", Func<HttpContext, Task>(fun ctx -> task {
        hotReloadStateRef.Value <- HotReloadState.unwatchAll !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| watchedCount = 0 |})
      })) |> ignore

      app.MapPost("/hotreload/watch-project", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        hotReloadStateRef.Value <- HotReloadState.watchByDirectory project projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| project = project; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      app.MapPost("/hotreload/unwatch-project", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let project = (jsonProp doc "project").GetString()
        hotReloadStateRef.Value <- HotReloadState.unwatchByDirectory project !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| project = project; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
      })) |> ignore

      app.MapPost("/hotreload/watch-directory", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let dir = (jsonProp doc "directory").GetString()
        hotReloadStateRef.Value <- HotReloadState.watchByDirectory dir projectFiles !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        let watched = HotReloadState.watchedInDirectory dir !hotReloadStateRef
        do! ctx.Response.WriteAsync(Serialization.serialize {| directory = dir; watchedCount = List.length watched |})
      })) |> ignore

      app.MapPost("/hotreload/unwatch-directory", Func<HttpContext, Task>(fun ctx -> task {
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let dir = (jsonProp doc "directory").GetString()
        hotReloadStateRef.Value <- HotReloadState.unwatchByDirectory dir !hotReloadStateRef
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(Serialization.serialize {| directory = dir; watchedCount = HotReloadState.watchedCount !hotReloadStateRef |})
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
      let compilingBytes = Text.Encoding.UTF8.GetBytes("""data: {"type":"compiling"}""" + "\n\n")
      let reloadBytes = Text.Encoding.UTF8.GetBytes("""data: {"type":"reload"}""" + "\n\n")

      app.MapGet("/__sagefs__/reload", Func<HttpContext, Task>(fun ctx -> task {
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
            let mutable evt = DevReload.DevReloadEvent.Reload
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
                let bytes =
                  match evt with
                  | DevReload.DevReloadEvent.Compiling None -> compilingBytes
                  | DevReload.DevReloadEvent.Compiling (Some file) ->
                    // Dynamic payload with filename — can't pre-allocate.
                    // Use JsonSerializer.Serialize for proper escaping of control chars,
                    // Unicode, and special characters in filenames.
                    Text.Encoding.UTF8.GetBytes(
                      sprintf """data: {"type":"compiling","file":%s}""" (System.Text.Json.JsonSerializer.Serialize(file)) + "\n\n")
                  | DevReload.DevReloadEvent.Reload -> reloadBytes
                  | DevReload.DevReloadEvent.CompilationFailed(summary, diagnostics) ->
                    // Chesterton's fence: send both "error" (legacy string) and "diagnostics"
                    // (structured array). Browser script checks for diagnostics first and falls
                    // back to error string — backward compatible with older injected scripts.
                    let diagJson = System.Text.Json.JsonSerializer.Serialize(diagnostics)
                    Text.Encoding.UTF8.GetBytes(
                      sprintf """data: {"type":"failed","error":%s,"diagnostics":%s}""" (System.Text.Json.JsonSerializer.Serialize(summary)) diagJson + "\n\n")
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
