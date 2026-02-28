module SageFs.Server.McpServer

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open OpenTelemetry.Logs
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open SageFs.AppState
open SageFs.McpTools

// ---------------------------------------------------------------------------
// MCP Push Notifications — tracks active connections and broadcasts events
// ---------------------------------------------------------------------------

open SageFs.Features.Diagnostics
open SageFs.Features.LiveTesting

/// Structured events for the push notification accumulator.
/// Stored as data, formatted for the LLM only on drain.
[<RequireQualifiedAccess>]
type PushEvent =
  /// Diagnostics changed — carries the current set of errors/warnings.
  | DiagnosticsChanged of errors: (string * int * string) list
  /// Elm model state changed — carries output & diag counts.
  | StateChanged of outputCount: int * diagCount: int
  /// A watched file was reloaded by the file watcher.
  | FileReloaded of path: string
  /// Session became faulted.
  | SessionFaulted of error: string
  /// Warmup completed.
  | WarmupCompleted
  /// Test summary changed — carries summary record.
  | TestSummaryChanged of summary: SageFs.Features.LiveTesting.TestSummary
  /// Test results batch — carries enriched payload with generation, freshness, entries, summary.
  | TestResultsBatch of payload: SageFs.Features.LiveTesting.TestResultsBatchPayload
  /// File annotations — per-file inline feedback (test status, coverage, CodeLens, failures).
  | FileAnnotationsUpdated of annotations: SageFs.Features.LiveTesting.FileAnnotations

/// Whether an event REPLACES the previous instance of the same kind
/// (state/set semantics) or ACCUMULATES alongside it (delta/list semantics).
[<RequireQualifiedAccess>]
type MergeStrategy = Replace | Accumulate

module PushEvent =
  /// Determine how an event merges with existing events of the same type.
  let mergeStrategy = function
    | PushEvent.DiagnosticsChanged _ -> MergeStrategy.Replace
    | PushEvent.StateChanged _ -> MergeStrategy.Replace
    | PushEvent.SessionFaulted _ -> MergeStrategy.Replace
    | PushEvent.FileReloaded _ -> MergeStrategy.Accumulate
    | PushEvent.WarmupCompleted -> MergeStrategy.Replace
    | PushEvent.TestSummaryChanged _ -> MergeStrategy.Replace
    | PushEvent.TestResultsBatch _ -> MergeStrategy.Replace
    | PushEvent.FileAnnotationsUpdated _ -> MergeStrategy.Replace

  /// Discriminator tag used for Replace dedup.
  let tag = function
    | PushEvent.DiagnosticsChanged _ -> 0
    | PushEvent.StateChanged _ -> 1
    | PushEvent.FileReloaded _ -> 2
    | PushEvent.SessionFaulted _ -> 3
    | PushEvent.WarmupCompleted -> 4
    | PushEvent.TestSummaryChanged _ -> 5
    | PushEvent.TestResultsBatch _ -> 6
    | PushEvent.FileAnnotationsUpdated _ -> 7

  /// Format a single event for LLM consumption — actionable, concise.
  let formatForLlm = function
    | PushEvent.DiagnosticsChanged errors when errors.IsEmpty ->
      "✓ diagnostics cleared"
    | PushEvent.DiagnosticsChanged errors ->
      let lines =
        errors
        |> List.truncate 5
        |> List.map (fun (file, line, msg) ->
          sprintf "  %s:%d — %s" (IO.Path.GetFileName file) line msg)
      let header = sprintf "⚠ %d diagnostic(s):" errors.Length
      let truncNote =
        if errors.Length > 5 then sprintf "\n  … and %d more" (errors.Length - 5)
        else ""
      sprintf "%s\n%s%s" header (String.concat "\n" lines) truncNote
    | PushEvent.StateChanged (outputCount, diagCount) ->
      sprintf "state: output=%d diags=%d" outputCount diagCount
    | PushEvent.FileReloaded path ->
      sprintf "📄 reloaded %s" (IO.Path.GetFileName path)
    | PushEvent.SessionFaulted error ->
      sprintf "🔴 session faulted: %s" error
    | PushEvent.WarmupCompleted ->
      "✓ warmup complete"
    | PushEvent.TestSummaryChanged s ->
      sprintf "🧪 tests: %d total, %d passed, %d failed, %d stale, %d running" s.Total s.Passed s.Failed s.Stale s.Running
    | PushEvent.TestResultsBatch payload ->
      sprintf "🧪 %d test result(s) received (%A)" payload.Entries.Length payload.Freshness
    | PushEvent.FileAnnotationsUpdated ann ->
      sprintf "📝 file annotations for %s (%d tests, %d lenses, %d failures)"
        (IO.Path.GetFileName ann.FilePath) ann.TestAnnotations.Length ann.CodeLenses.Length ann.InlineFailures.Length

type AccumulatedEvent = {
  Timestamp: DateTimeOffset
  Event: PushEvent
}

/// Thread-safe accumulator with smart dedup.
/// Replace-strategy events overwrite the previous instance.
/// Accumulate-strategy events are appended.
type EventAccumulator() =
  let events = ConcurrentQueue<AccumulatedEvent>()
  let maxEvents = 50

  member _.Add(evt: PushEvent) =
    let entry = { Timestamp = DateTimeOffset.UtcNow; Event = evt }
    match PushEvent.mergeStrategy evt with
    | MergeStrategy.Replace ->
      // For Replace events, we can't efficiently remove from ConcurrentQueue.
      // Instead, drain-and-requeue minus old same-tag events, then add new.
      // With max 50 events this is O(n) and fast.
      let tag = PushEvent.tag evt
      let temp = ResizeArray()
      let mutable item = Unchecked.defaultof<AccumulatedEvent>
      while events.TryDequeue(&item) do
        if PushEvent.tag item.Event <> tag then
          temp.Add(item)
      for e in temp do events.Enqueue(e)
      events.Enqueue(entry)
    | MergeStrategy.Accumulate ->
      events.Enqueue(entry)
      while events.Count > maxEvents do
        events.TryDequeue() |> ignore

  member _.Drain() =
    let result = ResizeArray()
    let mutable item = Unchecked.defaultof<AccumulatedEvent>
    while events.TryDequeue(&item) do
      result.Add(item)
    result.ToArray()

  member _.Count = events.Count

/// Tracks active MCP server connections for push notifications.
type McpServerTracker() =
  let servers = ConcurrentDictionary<string, McpServer>()
  let accumulator = EventAccumulator()

  member _.Register(server: McpServer) =
    servers.[server.SessionId] <- server

  member _.Remove(sessionId: string) =
    servers.TryRemove(sessionId) |> ignore

  /// Broadcast a structured logging notification to all connected MCP clients.
  member _.NotifyLogAsync(level: LoggingLevel, logger: string, data: obj) =
    task {
      if servers.IsEmpty then return ()
      else
        let jsonElement =
          let json = JsonSerializer.Serialize(data)
          let doc = JsonDocument.Parse(json)
          doc.RootElement.Clone()
        let dead = ResizeArray()
        for kvp in servers do
          try
            let payload =
              LoggingMessageNotificationParams(
                Level = level, Logger = logger, Data = jsonElement)
            do! kvp.Value.SendNotificationAsync(
              NotificationMethods.LoggingMessageNotification, payload)
          with _ -> dead.Add(kvp.Key)
        for id in dead do servers.TryRemove(id) |> ignore
    }

  /// Accumulate a structured event for delivery on the next tool response.
  member _.AccumulateEvent(evt: PushEvent) = accumulator.Add(evt)

  /// Drain accumulated events, format for LLM, return as string array.
  member _.DrainEvents() =
    accumulator.Drain()
    |> Array.map (fun e -> PushEvent.formatForLlm e.Event)

  member _.Count = servers.Count
  member _.PendingEvents = accumulator.Count

/// CallToolFilter that captures the McpServer and appends accumulated events
/// to tool responses. This ensures the LLM sees events even if the client
/// doesn't surface MCP notifications directly.
let createServerCaptureFilter (tracker: McpServerTracker) =
  let mutable logged = false
  McpRequestFilter<CallToolRequestParams, CallToolResult>(fun next ->
    McpRequestHandler<CallToolRequestParams, CallToolResult>(fun ctx ct ->
      let wasEmpty = tracker.Count = 0
      tracker.Register(ctx.Server)
      if wasEmpty && not logged then
        logged <- true
        let logger =
          ctx.Services.GetService(typeof<ILoggerFactory>)
          |> Option.ofObj
          |> Option.map (fun f -> (f :?> ILoggerFactory).CreateLogger("SageFs.McpServer.Filter"))
        match logger with
        | Some l -> l.LogInformation("First MCP client connected")
        | None -> ()

      let inline appendEvents (result: CallToolResult) =
        let events = tracker.DrainEvents()
        if events.Length > 0 then
          let eventText =
            events
            |> Array.map (sprintf "  • %s")
            |> String.concat "\n"
          let banner = sprintf "\n\n📡 SageFs events since last call:\n%s" eventText
          result.Content.Add(TextContentBlock(Text = banner))
        result

      let vt = next.Invoke(ctx, ct)
      if vt.IsCompleted then
        ValueTask<CallToolResult>(appendEvents vt.Result)
      else
        ValueTask<CallToolResult>(
          task {
            let! result = vt.AsTask()
            return appendEvents result
          })))

/// Write a JSON response with the given status code.
let jsonResponse (ctx: Microsoft.AspNetCore.Http.HttpContext) (statusCode: int) (data: obj) = task {
  ctx.Response.StatusCode <- statusCode
  ctx.Response.ContentType <- "application/json"
  let json = System.Text.Json.JsonSerializer.Serialize(data)
  do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json))
}

/// Read JSON body and extract a string property, with fallback to raw body.
let readJsonProp (ctx: Microsoft.AspNetCore.Http.HttpContext) (prop: string) = task {
  use reader = new System.IO.StreamReader(ctx.Request.Body)
  let! body = reader.ReadToEndAsync()
  try
    let json = System.Text.Json.JsonDocument.Parse(body)
    if json.RootElement.TryGetProperty(prop) |> fst then
      return json.RootElement.GetProperty(prop).GetString()
    else
      return body
  with _ -> return body
}

/// Wrap an async handler with try/catch and JSON error response.
let withErrorHandling (ctx: Microsoft.AspNetCore.Http.HttpContext) (handler: unit -> Task) = task {
  try do! handler ()
  with ex ->
    do! jsonResponse ctx 500 {| success = false; error = ex.Message |}
}

// Create shared MCP context
let mkContext (persistence: SageFs.EventStore.EventPersistence) (diagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>) (stateChanged: IEvent<string> option) (sessionOps: SageFs.SessionManagementOps) (mcpPort: int) (dispatch: (SageFs.SageFsMsg -> unit) option) (getElmModel: (unit -> SageFs.SageFsModel) option) (getElmRegions: (unit -> SageFs.RenderRegion list) option) (getWarmupContext: (string -> System.Threading.Tasks.Task<SageFs.WarmupContext option>) option) : McpContext =
  { Persistence = persistence; DiagnosticsChanged = diagnosticsChanged; StateChanged = stateChanged; SessionOps = sessionOps; SessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>(); McpPort = mcpPort; Dispatch = dispatch; GetElmModel = getElmModel; GetElmRegions = getElmRegions; GetWarmupContext = getWarmupContext }

// Start MCP server in background
let startMcpServer (diagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>) (stateChanged: IEvent<string> option) (persistence: SageFs.EventStore.EventPersistence) (port: int) (sessionOps: SageFs.SessionManagementOps) (elmRuntime: SageFs.ElmRuntime<SageFs.SageFsModel, SageFs.SageFsMsg, SageFs.RenderRegion> option) (getWarmupContext: (string -> System.Threading.Tasks.Task<SageFs.WarmupContext option>) option) =
    task {
        try
            let dispatch = elmRuntime |> Option.map (fun r -> r.Dispatch)
            let getElmModel = elmRuntime |> Option.map (fun r -> r.GetModel)
            let getElmRegions = elmRuntime |> Option.map (fun r -> r.GetRegions)
            let logPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SageFs", "mcp-server.log")
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)) |> ignore
            
            let builder = WebApplication.CreateBuilder([||])
            let bindHost =
              match System.Environment.GetEnvironmentVariable("SAGEFS_BIND_HOST") with
              | null | "" -> "localhost"
              | h -> h
            builder.WebHost.UseUrls($"http://%s{bindHost}:%d{port}") |> ignore

            // Get version from assembly
            let version = 
                System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName()
                    .Version
                    .ToString()

            // Only register OTLP exporter when endpoint is configured
            let otelConfigured =
              Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
              |> Option.ofObj
              |> Option.isSome

            // Configure OpenTelemetry with resource attributes
            let otelBuilder =
              builder.Services.AddOpenTelemetry()
                .ConfigureResource(fun resource ->
                    resource
                        .AddService("sagefs-mcp-server", serviceVersion = version)
                        .AddAttributes([
                            KeyValuePair<string, obj>("mcp.port", port :> obj)
                            KeyValuePair<string, obj>("mcp.session", "cli-integrated" :> obj)
                        ]) |> ignore
                )
                .WithTracing(fun tracing ->
                    let t = tracing
                    for source in SageFs.Instrumentation.allSources do
                      t.AddSource(source) |> ignore
                    t.AddAspNetCoreInstrumentation(fun opts ->
                        opts.Filter <- fun ctx ->
                          SageFs.Instrumentation.shouldFilterHttpSpan (ctx.Request.Path.ToString())
                      )
                      .AddHttpClientInstrumentation() |> ignore
                    if otelConfigured then tracing.AddOtlpExporter() |> ignore
                )
                .WithMetrics(fun metrics ->
                    let m = metrics
                    for meter in SageFs.Instrumentation.allMeters do
                      m.AddMeter(meter) |> ignore
                    m.AddAspNetCoreInstrumentation()
                      .AddHttpClientInstrumentation() |> ignore
                    metrics.SetExemplarFilter(OpenTelemetry.Metrics.ExemplarFilterType.TraceBased) |> ignore
                    if otelConfigured then metrics.AddOtlpExporter() |> ignore
                )

            if otelConfigured then
              otelBuilder.WithLogging(fun logging ->
                logging.AddOtlpExporter() |> ignore
              ) |> ignore
            else
              otelBuilder |> ignore

            // Configure standard logging (file and console)
            builder.WebHost.ConfigureLogging(fun logging -> 
                logging.AddConsole() |> ignore
                logging.AddFile(logPath, minimumLevel = LogLevel.Information) |> ignore
                // Silence ASP.NET plumbing on both console and file
                logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore
                logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning) |> ignore
                logging.AddFilter("Microsoft.Hosting", LogLevel.Warning) |> ignore
                logging.AddFilter("ModelContextProtocol.Server.McpServer", fun level -> level > LogLevel.Information) |> ignore
                logging.AddFilter("ModelContextProtocol.AspNetCore.SseHandler", LogLevel.Warning) |> ignore
                // Let SageFs-namespaced logs flow through at Information+ for OTEL
                logging.AddFilter("SageFs", LogLevel.Information) |> ignore
            ) |> ignore
            
            // Create MCP context
            let mcpContext = (mkContext persistence diagnosticsChanged stateChanged sessionOps port dispatch getElmModel getElmRegions getWarmupContext)
            
            // Register MCP services
            builder.Services.AddSingleton<McpContext>(mcpContext) |> ignore
            builder.Services.AddSingleton<SageFs.Server.McpTools.SageFsTools>(fun serviceProvider ->
                let logger = serviceProvider.GetRequiredService<ILogger<SageFs.Server.McpTools.SageFsTools>>()
                new SageFs.Server.McpTools.SageFsTools(mcpContext, logger)
            ) |> ignore
            
            // Create notification tracker
            let serverTracker = McpServerTracker()
            builder.Services.AddSingleton<McpServerTracker>(serverTracker) |> ignore

            // SSE broadcast for typed test events (clients subscribe via /events)
            let testEventBroadcast = Event<string>()
            // SSE serialization uses default PascalCase (NOT camelCase) + JsonFSharpConverter
            // for Case/Fields DU encoding. This differs from WorkerProtocol.Serialization.jsonOptions
            // which uses type/value format. Both VS Code and VS parsers expect PascalCase + Case/Fields.
            let sseJsonOpts = JsonSerializerOptions()
            sseJsonOpts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())

            builder.Services
                .AddMcpServer(fun options ->
                  options.ServerInstructions <- String.concat " " [
                    "SageFs is an affordance-driven F# Interactive (FSI) REPL with MCP integration."
                    "ALWAYS use SageFs MCP tools for ALL F# work — never shell out to dotnet build, dotnet run, or PowerShell commands."
                    "PowerShell is ONLY for process management: starting/stopping SageFs, dotnet pack, dotnet tool install/uninstall."
                    "SageFs runs as a VISIBLE terminal window — the user watches it."
                    "When starting or restarting SageFs, ALWAYS use Start-Process to launch in a visible console window, NEVER detach or run in background."
                    "You OWN the full development cycle: pack, stop, reinstall, restart, test. Never ask the user to do these steps."
                    "The MCP connection is SSE (push-based) — do not poll or sleep. Tools become available when SageFs is ready."
                    "SageFs pushes structured notifications (notifications/message) for important events: session faults, warmup completion, file reloads, eval failures."
                    "Tool responses return only Result: or Error: with diagnostics — no code echo (you already know what you sent)."
                    "SageFs is affordance-driven: get_fsi_status shows available tools for the current session state. Only invoke listed tools."
                    "If a tool returns an error about session state, check get_fsi_status for available alternatives."
                    "Use send_fsharp_code for incremental, small code blocks. End statements with ';;' for evaluation."
                    "FILE WATCHING: SageFs automatically watches .fs/.fsx source files and reloads changes via #load (~100ms). You do NOT need hard_reset to pick up source file edits."
                    "hard_reset_fsi_session with rebuild=true is ONLY needed when .fsproj changes (new files, packages) or warm-up fails. The file watcher handles .fs/.fsx changes automatically."
                    "Use cancel_eval to stop a running evaluation. Use reset_fsi_session only if warm-up failed."
                  ]
                )
                .WithHttpTransport(fun opts ->
                    // SSE connections are long-lived — only cull if we hit thousands
                    opts.IdleTimeout <- System.TimeSpan.FromHours(24.0)
                    opts.MaxIdleSessionCount <- 1000
                )
                .WithTools<SageFs.Server.McpTools.SageFsTools>()
                .WithRequestFilters(fun filters ->
                    filters.AddCallToolFilter(createServerCaptureFilter serverTracker) |> ignore
                )
            |> ignore
            
            let app = builder.Build()

            // Map MCP endpoints
            app.MapMcp() |> ignore

            // Shared context — constructed once
            let mcpContext = mkContext persistence diagnosticsChanged stateChanged sessionOps port dispatch getElmModel getElmRegions getWarmupContext
            
            // POST /exec — send F# code to the session
            app.MapPost("/exec", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    use reader = new System.IO.StreamReader(ctx.Request.Body)
                    let! body = reader.ReadToEndAsync()
                    let json = System.Text.Json.JsonDocument.Parse(body)
                    let code = json.RootElement.GetProperty("code").GetString()
                    let wd =
                      if json.RootElement.TryGetProperty("working_directory") |> fst then
                        Some (json.RootElement.GetProperty("working_directory").GetString())
                      else None
                    let! result = SageFs.McpTools.sendFSharpCode mcpContext "cli-integrated" code SageFs.McpTools.OutputFormat.Text None wd
                    do! jsonResponse ctx 200 {| success = true; result = result |}
                }) :> Task
            ) |> ignore

            // POST /reset — reset the FSI session
            app.MapPost("/reset", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! result = SageFs.McpTools.resetSession mcpContext "http" None None
                    do! jsonResponse ctx 200 {| success = not (result.Contains("Error")); message = result |}
                }) :> Task
            ) |> ignore

            // POST /hard-reset — hard reset with optional rebuild
            app.MapPost("/hard-reset", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    use reader = new System.IO.StreamReader(ctx.Request.Body)
                    let! body = reader.ReadToEndAsync()
                    let rebuild =
                        try
                            let json = System.Text.Json.JsonDocument.Parse(body)
                            if json.RootElement.TryGetProperty("rebuild") |> fst then
                                json.RootElement.GetProperty("rebuild").GetBoolean()
                            else false
                        with _ -> false
                    let! result = SageFs.McpTools.hardResetSession mcpContext "http" rebuild None None
                    do! jsonResponse ctx 200 {| success = not (result.Contains("Error")); message = result |}
                }) :> Task
            ) |> ignore

            // GET /health — session health check
            app.MapGet("/health", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! status = SageFs.McpTools.getStatus mcpContext "http" None None
                    do! jsonResponse ctx 200 {| healthy = true; status = status |}
                }) :> Task
            ) |> ignore

            // GET /version — protocol version and server info
            app.MapGet("/version", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    let asm = typeof<SageFs.SageFsModel>.Assembly
                    let v = asm.GetName().Version
                    let infoVersion =
                        asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
                        |> Array.tryHead
                        |> Option.map (fun a -> (a :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion)
                        |> Option.defaultValue (string v)
                    do! jsonResponse ctx 200
                          {| version = infoVersion
                             protocolVersion = 1
                             server = "sagefs"
                             mcp = true
                             sse = true |}
                } :> Task
            ) |> ignore

            // POST /diagnostics — fire-and-forget diagnostics check via proxy
            app.MapPost("/diagnostics", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! code = readJsonProp ctx "code"
                    let! _ = SageFs.McpTools.checkFSharpCode mcpContext "http" code None None
                    do! jsonResponse ctx 202 {| accepted = true |}
                }) :> Task
            ) |> ignore

            // GET /diagnostics — SSE stream of diagnostics updates
            app.MapGet("/diagnostics", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    SageFs.Instrumentation.sseConnectionsActive.Add(1L)
                    ctx.Response.ContentType <- "text/event-stream"
                    ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues("no-cache")
                    ctx.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues("keep-alive")

                    // Send initial empty diagnostics
                    let initialEvent = sprintf "event: diagnostics\ndata: []\n\n"
                    do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(initialEvent))
                    do! ctx.Response.Body.FlushAsync()

                    let tcs = System.Threading.Tasks.TaskCompletionSource()
                    use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
                    use _sub = diagnosticsChanged.Subscribe(fun store ->
                        let json = SageFs.McpAdapter.formatDiagnosticsStoreAsJson store
                        let sseEvent = sprintf "event: diagnostics\ndata: %s\n\n" json
                        let bytes = System.Text.Encoding.UTF8.GetBytes(sseEvent)
                        ctx.Response.Body.WriteAsync(bytes).AsTask()
                        |> fun t -> t.ContinueWith(fun (_: Task) -> ctx.Response.Body.FlushAsync()) |> ignore
                    )
                    do! tcs.Task
                    SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
                } :> Task
            ) |> ignore

            let replayCachedTestState (body: System.IO.Stream) =
              match getElmModel with
              | Some getModel ->
                try
                  let model = getModel()
                  let lt = model.LiveTesting.TestState
                  let activeId =
                    SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
                    |> Option.defaultValue ""
                  let sessionEntries =
                    LiveTestState.statusEntriesForSession activeId lt
                  if sessionEntries.Length > 0 then
                    let s = TestSummary.fromStatuses
                              lt.Activation (sessionEntries |> Array.map (fun e -> e.Status))
                    let summaryBytes = System.Text.Encoding.UTF8.GetBytes(
                      SageFs.SseWriter.formatTestSummaryEvent sseJsonOpts (Some activeId) s)
                    body.WriteAsync(summaryBytes).AsTask()
                    |> fun t -> t.ContinueWith(fun (_: Task) -> body.FlushAsync()) |> ignore
                    let freshness =
                      if lt.RunPhases |> Map.exists (fun _ p -> match p with TestRunPhase.RunningButEdited _ -> true | _ -> false) then
                        ResultFreshness.StaleCodeEdited
                      else ResultFreshness.Fresh
                    let payload =
                      let completion =
                        TestResultsBatchPayload.deriveCompletion
                          freshness lt.DiscoveredTests.Length sessionEntries.Length
                      TestResultsBatchPayload.create
                        lt.LastGeneration freshness completion lt.Activation sessionEntries
                    let batchBytes = System.Text.Encoding.UTF8.GetBytes(
                      SageFs.SseWriter.formatTestResultsBatchEvent sseJsonOpts (Some activeId) payload)
                    body.WriteAsync(batchBytes).AsTask()
                    |> fun t -> t.ContinueWith(fun (_: Task) -> body.FlushAsync()) |> ignore
                    let files =
                      sessionEntries
                      |> Array.choose (fun e ->
                        match e.Origin with
                        | TestOrigin.SourceMapped (f, _) -> Some f
                        | _ -> None)
                      |> Array.distinct
                    let pipeline = model.LiveTesting
                    for file in files do
                      let fa = FileAnnotations.projectWithCoverage file pipeline
                      if fa.TestAnnotations.Length > 0 || fa.CodeLenses.Length > 0 || fa.CoverageAnnotations.Length > 0 then
                        let faBytes = System.Text.Encoding.UTF8.GetBytes(
                          SageFs.SseWriter.formatFileAnnotationsEvent sseJsonOpts (Some activeId) fa)
                        body.WriteAsync(faBytes).AsTask()
                        |> fun t -> t.ContinueWith(fun (_: Task) -> body.FlushAsync()) |> ignore
                with ex ->
                  eprintfn "SSE replay error: %s" ex.Message
              | None -> ()

            // GET /events — SSE stream of Elm state changes
            app.MapGet("/events", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    SageFs.Instrumentation.sseConnectionsActive.Add(1L)
                    ctx.Response.ContentType <- "text/event-stream"
                    ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues("no-cache")
                    ctx.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues("keep-alive")

                    match stateChanged with
                    | Some evt ->
                        let tcs = System.Threading.Tasks.TaskCompletionSource()
                        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
                        // Heartbeat keeps connection alive through proxies
                        let heartbeat = new System.Threading.Timer((fun _ ->
                            try
                                let bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n\n")
                                ctx.Response.Body.WriteAsync(bytes).AsTask()
                                |> fun t -> t.ContinueWith(fun (_: Task) -> ctx.Response.Body.FlushAsync()) |> ignore
                            with _ -> ()), null, 15000, 15000)
                        use _heartbeat = heartbeat
                        use _sub = evt.Subscribe(fun json ->
                            try
                                let sseEvent = SageFs.SseWriter.formatSseEvent "state" json
                                let bytes = System.Text.Encoding.UTF8.GetBytes(sseEvent)
                                ctx.Response.Body.WriteAsync(bytes).AsTask()
                                |> fun t -> t.ContinueWith(fun (_: Task) -> ctx.Response.Body.FlushAsync()) |> ignore
                            with _ -> ())
                        use _testSub = testEventBroadcast.Publish.Subscribe(fun sseString ->
                            try
                                let bytes = System.Text.Encoding.UTF8.GetBytes(sseString)
                                ctx.Response.Body.WriteAsync(bytes).AsTask()
                                |> fun t -> t.ContinueWith(fun (_: Task) -> ctx.Response.Body.FlushAsync()) |> ignore
                            with _ -> ())
                        // Replay current test state on new SSE connection
                        replayCachedTestState ctx.Response.Body
                        do! tcs.Task
                        SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
                    | None ->
                        ctx.Response.StatusCode <- 501
                        let msg = System.Text.Encoding.UTF8.GetBytes("event: error\ndata: {\"error\":\"No Elm loop available\"}\n\n")
                        do! ctx.Response.Body.WriteAsync(msg)
                } :> Task
            ) |> ignore

            // GET /api/status — rich JSON status via proxy
            // Accepts ?sessionId=X to query a specific session
            app.MapGet("/api/status", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! sid = task {
                      match ctx.Request.Query.TryGetValue("sessionId") with
                      | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace(v.[0])) -> return v.[0]
                      | _ ->
                        let! sessions = sessionOps.GetAllSessions()
                        return sessions |> List.tryHead |> Option.map (fun s -> s.Id) |> Option.defaultValue ""
                    }
                    let! info = sessionOps.GetSessionInfo sid
                    let! statusResult =
                      task {
                        let! proxy = sessionOps.GetProxy sid
                        match proxy with
                        | Some send ->
                          let! resp = send (SageFs.WorkerProtocol.WorkerMessage.GetStatus "api") |> Async.StartAsTask
                          return Some resp
                        | None -> return None
                      }
                    let elmRegions =
                      match getElmRegions with
                      | Some getRegions -> getRegions ()
                      | None -> []
                    let version =
                      System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                      |> Option.ofObj
                      |> Option.map (fun v -> v.ToString())
                      |> Option.defaultValue "unknown"
                    let regionData =
                      elmRegions |> List.map (fun (r: SageFs.RenderRegion) ->
                        {| id = r.Id
                           content = r.Content |> fun s -> if s.Length > 2000 then s.[..1999] else s
                           affordances = r.Affordances |> List.map (fun a -> a.ToString()) |})
                    let sessionState, evalCount, avgMs, minMs, maxMs =
                      match statusResult with
                      | Some (SageFs.WorkerProtocol.WorkerResponse.StatusResult(_, snap)) ->
                        SageFs.WorkerProtocol.SessionStatus.label snap.Status,
                        snap.EvalCount,
                        (if snap.EvalCount > 0 then float snap.AvgDurationMs else 0.0),
                        float snap.MinDurationMs,
                        float snap.MaxDurationMs
                      | _ -> "Unknown", 0, 0.0, 0.0, 0.0
                    let workingDir =
                      info |> Option.map (fun i -> i.WorkingDirectory) |> Option.defaultValue ""
                    let projects =
                      info |> Option.map (fun i -> i.Projects) |> Option.defaultValue []
                    let data =
                      {| version = version
                         sessionId = sid
                         sessionState = sessionState
                         evalCount = evalCount
                         totalDurationMs = avgMs * float evalCount
                         avgDurationMs = avgMs
                         minDurationMs = minMs
                         maxDurationMs = maxMs
                         workingDirectory = workingDir
                         projectCount = projects.Length
                         projects = projects
                         warmupFailures = ([] : {| name: string; error: string |} list)
                         regions = regionData
                         pid = Environment.ProcessId
                         uptime = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds |}
                    do! jsonResponse ctx 200 data
                }) :> Task
            ) |> ignore

            // GET /api/system/status — system-level info including watchdog state
            app.MapGet("/api/system/status", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let supervised =
                      Environment.GetEnvironmentVariable("SAGEFS_SUPERVISED")
                      |> Option.ofObj |> Option.map (fun s -> s = "1") |> Option.defaultValue false
                    let restartCount =
                      Environment.GetEnvironmentVariable("SAGEFS_RESTART_COUNT")
                      |> Option.ofObj |> Option.bind (fun s -> match Int32.TryParse s with true, n -> Some n | _ -> None)
                      |> Option.defaultValue 0
                    let proc = System.Diagnostics.Process.GetCurrentProcess()
                    let uptime = (DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds
                    let version =
                      System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                      |> Option.ofObj |> Option.map (fun v -> v.ToString()) |> Option.defaultValue "unknown"
                    let! allSessions = sessionOps.GetAllSessions()
                    let data =
                      {| version = version
                         pid = Environment.ProcessId
                         uptimeSeconds = uptime
                         supervised = supervised
                         restartCount = restartCount
                         sessionCount = allSessions.Length
                         mcpPort = port
                         dashboardPort = port + 1 |}
                    do! jsonResponse ctx 200 data
                }) :> Task
            ) |> ignore
            
            // GET /api/sessions — list all sessions with details
            app.MapGet("/api/sessions", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! allSessions = sessionOps.GetAllSessions()
                    let results = System.Collections.Generic.List<obj>()
                    for sess in allSessions do
                      let! proxy = sessionOps.GetProxy sess.Id
                      let! evalCount, avgMs, status = task {
                        match proxy with
                        | Some send ->
                          try
                            let! resp = send (SageFs.WorkerProtocol.WorkerMessage.GetStatus "api") |> Async.StartAsTask
                            match resp with
                            | SageFs.WorkerProtocol.WorkerResponse.StatusResult(_, snap) ->
                              return snap.EvalCount, float snap.AvgDurationMs, SageFs.WorkerProtocol.SessionStatus.label snap.Status
                            | _ -> return 0, 0.0, "Unknown"
                          with _ -> return 0, 0.0, "Error"
                        | None -> return 0, 0.0, "Disconnected"
                      }
                      results.Add(
                        {| id = sess.Id
                           status = status
                           projects = sess.Projects
                           workingDirectory = sess.WorkingDirectory
                           evalCount = evalCount
                           avgDurationMs = avgMs |} :> obj)
                    do! jsonResponse ctx 200 {| sessions = results |}
                }) :> Task
            ) |> ignore

            // POST /api/sessions/switch — switch session for the requesting client
            app.MapPost("/api/sessions/switch", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! sid = readJsonProp ctx "sessionId"
                    // Verify session exists
                    let! info = sessionOps.GetSessionInfo sid
                    match info with
                    | Some _ ->
                      match dispatch with
                      | Some d ->
                        d (SageFs.SageFsMsg.Event (SageFs.SageFsEvent.SessionSwitched (None, sid)))
                        d (SageFs.SageFsMsg.Editor SageFs.EditorAction.ListSessions)
                      | None -> ()
                      let! _ = mcpContext.Persistence.AppendEvents "daemon-sessions" [
                        SageFs.Features.Events.SageFsEvent.DaemonSessionSwitched
                          {| FromId = None; ToId = sid; SwitchedAt = System.DateTimeOffset.UtcNow |}
                      ]
                      do! jsonResponse ctx 200 {| success = true; sessionId = sid |}
                    | None ->
                      do! jsonResponse ctx 404 {| success = false; error = sprintf "Session '%s' not found" sid |}
                }) :> Task
            ) |> ignore

            // POST /api/sessions/create — create a new session
            app.MapPost("/api/sessions/create", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    use reader = new System.IO.StreamReader(ctx.Request.Body)
                    let! body = reader.ReadToEndAsync()
                    let doc = System.Text.Json.JsonDocument.Parse(body)
                    let root = doc.RootElement
                    let workingDir =
                      let tryProp (name: string) =
                        let mutable value = Unchecked.defaultof<System.Text.Json.JsonElement>
                        if root.TryGetProperty(name, &value) then Some (value.GetString())
                        else None
                      tryProp "workingDirectory"
                      |> Option.orElseWith (fun () -> tryProp "working_directory")
                      |> Option.defaultValue Environment.CurrentDirectory
                    let projects =
                      let mutable projProp = Unchecked.defaultof<System.Text.Json.JsonElement>
                      if root.TryGetProperty("projects", &projProp) then
                        match projProp.ValueKind with
                        | System.Text.Json.JsonValueKind.Array ->
                          projProp.EnumerateArray()
                          |> Seq.map (fun e -> e.GetString())
                          |> Seq.toList
                        | System.Text.Json.JsonValueKind.String ->
                          [ projProp.GetString() ]
                        | _ -> []
                      else []
                    let! result = sessionOps.CreateSession projects workingDir
                    match result with
                    | Ok msg ->
                      match dispatch with
                      | Some d -> d (SageFs.SageFsMsg.Editor SageFs.EditorAction.ListSessions)
                      | None -> ()
                      do! jsonResponse ctx 200 {| success = true; message = msg |}
                    | Error err ->
                      do! jsonResponse ctx 400 {| success = false; error = SageFs.SageFsError.describe err |}
                }) :> Task
            ) |> ignore

            // POST /api/sessions/stop — stop a session
            app.MapPost("/api/sessions/stop", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! sid = readJsonProp ctx "sessionId"
                    let! result = sessionOps.StopSession sid
                    match dispatch with
                    | Some d -> d (SageFs.SageFsMsg.Editor SageFs.EditorAction.ListSessions)
                    | None -> ()
                    match result with
                    | Ok msg -> do! jsonResponse ctx 200 {| success = true; message = msg |}
                    | Error err -> do! jsonResponse ctx 400 {| success = false; error = SageFs.SageFsError.describe err |}
                }) :> Task
            ) |> ignore

            // POST /api/live-testing/enable — enable live testing
            app.MapPost("/api/live-testing/enable", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! result = SageFs.McpTools.setLiveTesting mcpContext true
                    do! jsonResponse ctx 200 {| success = true; message = result; activation = "active" |}
                }) :> Task
            ) |> ignore

            // POST /api/live-testing/disable — disable live testing
            app.MapPost("/api/live-testing/disable", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! result = SageFs.McpTools.setLiveTesting mcpContext false
                    do! jsonResponse ctx 200 {| success = true; message = result; activation = "inactive" |}
                }) :> Task
            ) |> ignore

            // POST /api/live-testing/policy — set run policy for a test category
            app.MapPost("/api/live-testing/policy", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    use reader = new System.IO.StreamReader(ctx.Request.Body)
                    let! body = reader.ReadToEndAsync()
                    let json = System.Text.Json.JsonDocument.Parse(body)
                    let category = json.RootElement.GetProperty("category").GetString()
                    let policy = json.RootElement.GetProperty("policy").GetString()
                    let! result = SageFs.McpTools.setRunPolicy mcpContext category policy
                    do! jsonResponse ctx 200 {| success = true; message = result |}
                }) :> Task
            ) |> ignore

            // GET /api/live-testing/file-annotations?file=X — get annotations for a file
            app.MapGet("/api/live-testing/file-annotations", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let fileParam = ctx.Request.Query.["file"].ToString()
                    match getElmModel with
                    | None -> do! jsonResponse ctx 503 {| error = "Elm loop not started" |}
                    | Some getModel ->
                      let model = getModel()
                      let lt = model.LiveTesting.TestState
                      let matchingFile = FileAnnotations.resolveFilePath fileParam lt.StatusEntries model.LiveTesting.InstrumentationMaps
                      match matchingFile with
                      | Some fullPath ->
                        let fa = FileAnnotations.projectWithCoverage fullPath model.LiveTesting
                        let json = System.Text.Json.JsonSerializer.Serialize(fa, sseJsonOpts)
                        do! jsonResponse ctx 200 json
                      | None ->
                        let fa = FileAnnotations.empty fileParam
                        let json = System.Text.Json.JsonSerializer.Serialize(fa, sseJsonOpts)
                        do! jsonResponse ctx 200 json
                }) :> Task
            ) |> ignore

            // GET /api/live-testing/status — get test status with optional file filter
            app.MapGet("/api/live-testing/status", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let fileParam =
                      let fp = ctx.Request.Query.["file"].ToString()
                      if System.String.IsNullOrWhiteSpace fp then None else Some fp
                    let! result = SageFs.McpTools.getLiveTestStatus mcpContext fileParam
                    ctx.Response.ContentType <- "application/json"
                    let bytes = System.Text.Encoding.UTF8.GetBytes(result)
                    do! ctx.Response.Body.WriteAsync(bytes)
                }) :> Task
            ) |> ignore

            // POST /api/live-testing/run — explicitly run tests
            app.MapPost("/api/live-testing/run", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    use reader = new System.IO.StreamReader(ctx.Request.Body)
                    let! body = reader.ReadToEndAsync()
                    let json = System.Text.Json.JsonDocument.Parse(body)
                    let pattern =
                      match json.RootElement.TryGetProperty("pattern") with
                      | true, v -> let s = v.GetString() in if System.String.IsNullOrWhiteSpace s then None else Some s
                      | false, _ -> None
                    let category =
                      match json.RootElement.TryGetProperty("category") with
                      | true, v -> let s = v.GetString() in if System.String.IsNullOrWhiteSpace s then None else Some s
                      | false, _ -> None
                    let timeout =
                      match json.RootElement.TryGetProperty("timeout_seconds") with
                      | true, v -> match v.TryGetInt32() with true, i -> i | _ -> 30
                      | false, _ -> 30
                    let! result = SageFs.McpTools.runTests mcpContext pattern category timeout
                    do! jsonResponse ctx 200 {| success = true; message = result |}
                }) :> Task
            ) |> ignore

            // POST /api/explore— explore a namespace or type
            app.MapPost("/api/explore", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! name = readJsonProp ctx "name"
                    let! result = SageFs.McpTools.exploreNamespace mcpContext "http" name None
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(result))
                }) :> Task
            ) |> ignore

            // POST /api/completions — get code completions
            app.MapPost("/api/completions", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    use reader = new System.IO.StreamReader(ctx.Request.Body)
                    let! body = reader.ReadToEndAsync()
                    let json = System.Text.Json.JsonDocument.Parse(body)
                    let code = json.RootElement.GetProperty("code").GetString()
                    let cursor = json.RootElement.GetProperty("cursorPosition").GetInt32()
                    let! result = SageFs.McpTools.getCompletions mcpContext "http" code cursor None
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(result))
                }) :> Task
            ) |> ignore

            // GET /api/dependency-graph — get test dependency graph for a symbol
            app.MapGet("/api/dependency-graph", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                let symbol =
                  match ctx.Request.Query.TryGetValue("symbol") with
                  | true, v -> Some (string v)
                  | _ -> None
                let json, status =
                  match getElmModel with
                  | Some getModel ->
                    let model = getModel ()
                    let graph = model.LiveTesting.DepGraph
                    let results = model.LiveTesting.TestState.LastResults
                    let body =
                      match symbol with
                      | Some sym ->
                        let tests =
                          Map.tryFind sym graph.SymbolToTests
                          |> Option.defaultValue [||]
                          |> Array.map (fun testId ->
                            let tid = SageFs.Features.LiveTesting.TestId.value testId
                            let status =
                              match Map.tryFind testId results with
                              | Some r ->
                                match r.Result with
                                | SageFs.Features.LiveTesting.TestResult.Passed _ -> "passed"
                                | SageFs.Features.LiveTesting.TestResult.Failed _ -> "failed"
                                | _ -> "other"
                              | None -> "unknown"
                            let testName =
                              match Map.tryFind testId results with
                              | Some r -> r.TestName
                              | None -> tid
                            {| TestId = tid; TestName = testName; Status = status |})
                        System.Text.Json.JsonSerializer.Serialize(
                          {| Symbol = sym; Tests = tests; TotalSymbols = graph.SymbolToTests.Count |})
                      | None ->
                        let symbols =
                          graph.SymbolToTests
                          |> Map.toArray
                          |> Array.map (fun (sym, tids) -> {| Symbol = sym; TestCount = tids.Length |})
                        System.Text.Json.JsonSerializer.Serialize(
                          {| Symbols = symbols; TotalSymbols = symbols.Length |})
                    body, 200
                  | None ->
                    """{"error":"Elm model not available"}""", 503
                task {
                    ctx.Response.StatusCode <- status
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json))
                } :> Task
            ) |> ignore

            // GET /api/recent-events — get recent FSI events
            app.MapGet("/api/recent-events", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    let count =
                      match ctx.Request.Query.TryGetValue("count") with
                      | true, v -> match System.Int32.TryParse(string v) with true, n -> n | _ -> 20
                      | _ -> 20
                    let! result = SageFs.McpTools.getRecentEvents mcpContext "http" count None
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(result))
                } :> Task
            ) |> ignore
            //   1. MCP notifications (for clients that surface them)
            //   2. Event accumulator → appended to next tool response (guaranteed delivery)
            //
            // Note: diagnosticsChanged event from DaemonMode is not wired to workers,
            // so we detect diag changes via stateChanged + Elm model access.

            let mutable lastDiagCount = 0
            let mutable lastTestSsePush = System.Diagnostics.Stopwatch.GetTimestamp()
            let testSseThrottleMs = 250L
            let _stateSub =
              stateChanged |> Option.map (fun evt ->
                evt.Subscribe(fun json ->
                  try
                    let doc = JsonDocument.Parse(json)
                    let root = doc.RootElement
                    let diagCount =
                      match root.TryGetProperty("diagCount") with
                      | true, v -> v.GetInt32()
                      | _ -> 0
                    let outputCount =
                      match root.TryGetProperty("outputCount") with
                      | true, v -> v.GetInt32()
                      | _ -> 0

                    // Always push latest state (Replace dedup keeps only the newest)
                    serverTracker.AccumulateEvent(
                      PushEvent.StateChanged(outputCount, diagCount))

                    // When diagCount changes, extract actual diagnostics from Elm model
                    if diagCount <> lastDiagCount then
                      lastDiagCount <- diagCount
                      match getElmModel with
                      | Some getModel ->
                        let model = getModel()
                        let errors =
                          model.Diagnostics
                          |> Map.toList
                          |> List.collect (fun (_, diags) ->
                            diags
                            |> List.filter (fun d ->
                              d.Severity = DiagnosticSeverity.Error)
                            |> List.map (fun d ->
                              ("fsi", d.Range.StartLine, d.Message)))
                        serverTracker.AccumulateEvent(
                          PushEvent.DiagnosticsChanged errors)
                      | None -> ()

                    // Push live testing summary when test state changes
                    match getElmModel with
                    | Some getModel ->
                      let model = getModel()
                      let lt = model.LiveTesting.TestState
                      let activeId =
                        SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
                        |> Option.defaultValue ""
                      let sessionEntries =
                        LiveTestState.statusEntriesForSession activeId lt
                      if sessionEntries.Length > 0 || TestRunPhase.isAnyRunning lt.RunPhases then
                        let s = SageFs.Features.LiveTesting.TestSummary.fromStatuses
                                  lt.Activation (sessionEntries |> Array.map (fun e -> e.Status))
                        serverTracker.AccumulateEvent(
                          PushEvent.TestSummaryChanged s)
                        // Throttle SSE broadcasts to avoid flooding clients during streaming
                        let now = System.Diagnostics.Stopwatch.GetTimestamp()
                        let elapsedMs = (now - lastTestSsePush) * 1000L / System.Diagnostics.Stopwatch.Frequency
                        let isRunComplete = not (TestRunPhase.isAnyRunning lt.RunPhases)
                        if elapsedMs >= testSseThrottleMs || isRunComplete then
                          lastTestSsePush <- now
                          testEventBroadcast.Trigger(
                            SageFs.SseWriter.formatTestSummaryEvent sseJsonOpts (Some activeId) s)
                          let freshness =
                            if lt.RunPhases |> Map.exists (fun _ p -> match p with SageFs.Features.LiveTesting.TestRunPhase.RunningButEdited _ -> true | _ -> false) then
                              SageFs.Features.LiveTesting.ResultFreshness.StaleCodeEdited
                            else SageFs.Features.LiveTesting.ResultFreshness.Fresh
                          let payload =
                            let completion =
                              SageFs.Features.LiveTesting.TestResultsBatchPayload.deriveCompletion
                                freshness lt.DiscoveredTests.Length sessionEntries.Length
                            SageFs.Features.LiveTesting.TestResultsBatchPayload.create
                              lt.LastGeneration freshness completion lt.Activation sessionEntries
                          serverTracker.AccumulateEvent(
                            PushEvent.TestResultsBatch payload)
                          testEventBroadcast.Trigger(
                            SageFs.SseWriter.formatTestResultsBatchEvent sseJsonOpts (Some activeId) payload)
                          // Compute and push FileAnnotations for each file with source-mapped tests
                          let files =
                            sessionEntries
                            |> Array.choose (fun e ->
                              match e.Origin with
                              | TestOrigin.SourceMapped (f, _) -> Some f
                              | _ -> None)
                            |> Array.distinct
                          // Also push for production code files with coverage from instrumentation maps
                          let instrFiles =
                            model.LiveTesting.InstrumentationMaps
                            |> Map.values |> Seq.collect id
                            |> Seq.collect (fun m -> m.Slots |> Array.map (fun s -> s.File))
                            |> Seq.distinct
                            |> Seq.filter (fun f -> not (Array.contains f files))
                            |> Array.ofSeq
                          let allFiles = Array.append files instrFiles
                          for file in allFiles do
                            let fa = SageFs.Features.LiveTesting.FileAnnotations.projectWithCoverage file model.LiveTesting
                            if fa.TestAnnotations.Length > 0 || fa.CodeLenses.Length > 0 || fa.CoverageAnnotations.Length > 0 then
                              testEventBroadcast.Trigger(
                                SageFs.SseWriter.formatFileAnnotationsEvent sseJsonOpts (Some activeId) fa)
                    | None -> ()

                    if serverTracker.Count > 0 then
                      let data =
                        {| event = "state_changed"
                           diagCount = diagCount
                           outputCount = outputCount |}
                      serverTracker.NotifyLogAsync(
                        LoggingLevel.Info, "sagefs.state", data) |> ignore
                  with _ -> ()))

            // Get logger from DI for structured logging (flows to OTEL)
            let logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SageFs.McpServer")

            // Log startup info — structured so OTEL captures it
            logger.LogInformation("MCP server starting on port {Port}", port)
            logger.LogInformation("SSE endpoint: http://localhost:{Port}/sse", port)
            logger.LogInformation("State events SSE: http://localhost:{Port}/events", port)
            logger.LogInformation("Kestrel max connections: {MaxConnections}", 200)
            logger.LogInformation("Log file: {LogPath}", logPath)
            
            // Log OTEL configuration
            if otelConfigured then
              let endpoint =
                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
              let protocol =
                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")
                |> Option.ofObj |> Option.defaultValue "grpc"
              logger.LogInformation("OpenTelemetry enabled: endpoint={OtelEndpoint}, protocol={OtelProtocol}", endpoint, protocol)
            else
              logger.LogInformation("OpenTelemetry not configured (set OTEL_EXPORTER_OTLP_ENDPOINT)")
            
            do! app.RunAsync()
        with ex ->
            eprintfn "MCP server failed to start: %s" ex.Message
    }
