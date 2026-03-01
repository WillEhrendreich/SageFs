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
open Microsoft.Extensions.Logging
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open SageFs.WorkerProtocol

module WorkerHttpTransport =

  /// Opaque server handle — exposes BaseUrl and Dispose.
  type HttpWorkerServer internal (baseUrl: string, app: WebApplication) =
    member _.BaseUrl = baseUrl
    interface IAsyncDisposable with
      member _.DisposeAsync() = app.StopAsync() |> ValueTask
    interface IDisposable with
      member _.Dispose() =
        app.StopAsync().GetAwaiter().GetResult()

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

      // Configure OTel for worker process when OTEL endpoint is available
      let otelEndpoint =
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
        |> Option.ofObj
        |> Option.filter (fun s -> not (String.IsNullOrEmpty s))
      match otelEndpoint with
      | Some _ ->
        let svcName =
          Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
          |> Option.ofObj
          |> Option.defaultValue "sagefs-worker"
        builder.Services.AddOpenTelemetry()
          .ConfigureResource(fun resource ->
            resource
              .AddService(svcName)
              .AddAttributes([
                KeyValuePair<string, obj>("worker.pid", Environment.ProcessId :> obj)
              ]) |> ignore)
          .WithTracing(fun tracing ->
            for source in SageFs.Instrumentation.allSources do
              tracing.AddSource(source) |> ignore
            tracing.AddAspNetCoreInstrumentation(fun opts ->
                opts.Filter <- fun ctx ->
                  SageFs.Instrumentation.shouldFilterHttpSpan (ctx.Request.Path.ToString())
              ) |> ignore
            tracing.AddOtlpExporter() |> ignore)
          .WithMetrics(fun metrics ->
            for meter in SageFs.Instrumentation.allMeters do
              metrics.AddMeter(meter) |> ignore
            metrics.AddAspNetCoreInstrumentation() |> ignore
            metrics.SetExemplarFilter(OpenTelemetry.Metrics.ExemplarFilterType.TraceBased) |> ignore
            metrics.AddOtlpExporter() |> ignore)
        |> ignore
      | None -> ()

      let app = builder.Build()

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
        let! body = readBody ctx
        use doc = JsonDocument.Parse(body)
        let testsJson = (jsonProp doc "tests").GetRawText()
        let tests = Serialization.deserialize<Features.LiveTesting.TestCase array> testsJson
        let maxParallelism = (jsonProp doc "maxParallelism").GetInt32()

        ctx.Response.ContentType <- "text/event-stream"
        ctx.Response.Headers["Cache-Control"] <- "no-cache"
        ctx.Response.Headers["Connection"] <- "keep-alive"

        let channel = System.Threading.Channels.Channel.CreateUnbounded<Features.LiveTesting.TestRunResult>()

        let executionTask = task {
          let onResult (result: Features.LiveTesting.TestRunResult) =
            channel.Writer.TryWrite(result) |> ignore
          let runTest = getRunTest()
          use cts = new CancellationTokenSource()
          try
            do! Features.LiveTesting.TestOrchestrator.executeFiltered
                  runTest onResult maxParallelism tests cts.Token
                |> Async.StartAsTask
          with ex ->
            System.Diagnostics.Activity.Current
            |> Option.ofObj
            |> Option.iter (fun a -> a.SetTag("error", ex.Message) |> ignore)
          channel.Writer.Complete()
        }
        let _ = executionTask

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

      do! app.StartAsync()

      let server = app.Services.GetRequiredService<IServer>()
      let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
      let actualUrl = addresses |> Seq.head
      return new HttpWorkerServer(actualUrl, app)
    }

  /// Create a SessionProxy backed by HTTP to the given base URL.
  /// Delegates to HttpWorkerClient in SageFs.Core.
  let httpProxy = HttpWorkerClient.httpProxy
