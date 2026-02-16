module SageFs.Server.McpServer

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open OpenTelemetry.Logs
open OpenTelemetry.Resources
open SageFs.AppState
open SageFs.McpTools

/// Write a JSON response with the given status code.
let private jsonResponse (ctx: Microsoft.AspNetCore.Http.HttpContext) (statusCode: int) (data: obj) = task {
  ctx.Response.StatusCode <- statusCode
  ctx.Response.ContentType <- "application/json"
  let json = System.Text.Json.JsonSerializer.Serialize(data)
  do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json))
}

/// Read JSON body and extract a string property, with fallback to raw body.
let private readJsonProp (ctx: Microsoft.AspNetCore.Http.HttpContext) (prop: string) = task {
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
let private withErrorHandling (ctx: Microsoft.AspNetCore.Http.HttpContext) (handler: unit -> Task) = task {
  try do! handler ()
  with ex ->
    do! jsonResponse ctx 500 {| success = false; error = ex.Message |}
}

// Create shared MCP context
let private mkContext (store: Marten.IDocumentStore) (diagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>) (stateChanged: IEvent<string> option) (sessionOps: SageFs.SessionManagementOps) (activeSessionId: string ref) (mcpPort: int) (dispatch: (SageFs.SageFsMsg -> unit) option) (getElmModel: (unit -> SageFs.SageFsModel) option) (getElmRegions: (unit -> SageFs.RenderRegion list) option) : McpContext =
  { Store = store; DiagnosticsChanged = diagnosticsChanged; StateChanged = stateChanged; SessionOps = sessionOps; ActiveSessionId = activeSessionId; McpPort = mcpPort; Dispatch = dispatch; GetElmModel = getElmModel; GetElmRegions = getElmRegions }

// Start MCP server in background
let startMcpServer (diagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>) (stateChanged: IEvent<string> option) (store: Marten.IDocumentStore) (port: int) (sessionOps: SageFs.SessionManagementOps) (activeSessionId: string ref) (elmRuntime: SageFs.ElmRuntime<SageFs.SageFsModel, SageFs.SageFsMsg, SageFs.RenderRegion> option) =
    task {
        try
            let dispatch = elmRuntime |> Option.map (fun r -> r.Dispatch)
            let getElmModel = elmRuntime |> Option.map (fun r -> r.GetModel)
            let getElmRegions = elmRuntime |> Option.map (fun r -> r.GetRegions)
            let logPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SageFs", "mcp-server.log")
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)) |> ignore
            
            let builder = WebApplication.CreateBuilder([||])
            builder.WebHost.UseUrls($"http://localhost:{port}") |> ignore

            // Get version from assembly
            let version = 
                System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName()
                    .Version
                    .ToString()

            // Configure OpenTelemetry with resource attributes
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(fun resource ->
                    resource
                        .AddService("sagefs-mcp-server", serviceVersion = version)
                        .AddAttributes([
                            KeyValuePair<string, obj>("mcp.port", port :> obj)
                            KeyValuePair<string, obj>("mcp.session", "cli-integrated" :> obj)
                        ]) |> ignore
                )
                .WithLogging(fun logging ->
                    logging
                        // .AddConsoleExporter()
                        
                        .AddOtlpExporter() // Uses environment variables
                    |> ignore
                )
            |> ignore

            // Configure standard logging (file and console)
            builder.WebHost.ConfigureLogging(fun logging -> 
                logging.AddConsole() |> ignore
                logging.AddFile(logPath, minimumLevel = LogLevel.Debug) |> ignore
                // Silence ASP.NET plumbing on console; file log stays at Debug
                logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore
                logging.AddFilter("Microsoft.Hosting", LogLevel.Warning) |> ignore
                logging.AddFilter("ModelContextProtocol.Server.McpServer", fun level -> level > LogLevel.Information) |> ignore
            ) |> ignore
            
            // Create MCP context
            let mcpContext = (mkContext store diagnosticsChanged stateChanged sessionOps activeSessionId port dispatch getElmModel getElmRegions)
            
            // Register MCP services
            builder.Services.AddSingleton<McpContext>(mcpContext) |> ignore
            builder.Services.AddSingleton<SageFs.Server.McpTools.SageFsTools>(fun serviceProvider ->
                let logger = serviceProvider.GetRequiredService<ILogger<SageFs.Server.McpTools.SageFsTools>>()
                new SageFs.Server.McpTools.SageFsTools(mcpContext, logger)
            ) |> ignore
            
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
                    "Tool responses return only Result: or Error: with diagnostics — no code echo (you already know what you sent)."
                    "SageFs is affordance-driven: get_fsi_status shows available tools for the current session state. Only invoke listed tools."
                    "If a tool returns an error about session state, check get_fsi_status for available alternatives."
                    "Use send_fsharp_code for incremental, small code blocks. End statements with ';;' for evaluation."
                    "FILE WATCHING: SageFs automatically watches .fs/.fsx source files and reloads changes via #load (~100ms). You do NOT need hard_reset to pick up source file edits."
                    "hard_reset_fsi_session with rebuild=true is ONLY needed when .fsproj changes (new files, packages) or warm-up fails. The file watcher handles .fs/.fsx changes automatically."
                    "Use cancel_eval to stop a running evaluation. Use reset_fsi_session only if warm-up failed."
                  ]
                )
                .WithHttpTransport()
                .WithTools<SageFs.Server.McpTools.SageFsTools>()
            |> ignore
            
            let app = builder.Build()
            
            // Map MCP endpoints
            app.MapMcp() |> ignore

            // Shared context — constructed once
            let mcpContext = mkContext store diagnosticsChanged stateChanged sessionOps activeSessionId port dispatch getElmModel getElmRegions
            
            // POST /exec — send F# code to the session
            app.MapPost("/exec", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! code = readJsonProp ctx "code"
                    let! result = SageFs.McpTools.sendFSharpCode mcpContext "cli-integrated" code SageFs.McpTools.OutputFormat.Text None
                    do! jsonResponse ctx 200 {| success = true; result = result |}
                }) :> Task
            ) |> ignore

            // POST /reset — reset the FSI session
            app.MapPost("/reset", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! result = SageFs.McpTools.resetSession mcpContext None
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
                    let! result = SageFs.McpTools.hardResetSession mcpContext rebuild None
                    do! jsonResponse ctx 200 {| success = not (result.Contains("Error")); message = result |}
                }) :> Task
            ) |> ignore

            // GET /health — session health check
            app.MapGet("/health", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! status = SageFs.McpTools.getStatus mcpContext
                    do! jsonResponse ctx 200 {| healthy = true; status = status |}
                }) :> Task
            ) |> ignore

            // POST /diagnostics — fire-and-forget diagnostics check via proxy
            app.MapPost("/diagnostics", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! code = readJsonProp ctx "code"
                    let! _ = SageFs.McpTools.checkFSharpCode mcpContext code None
                    do! jsonResponse ctx 202 {| accepted = true |}
                }) :> Task
            ) |> ignore

            // GET /diagnostics — SSE stream of diagnostics updates
            app.MapGet("/diagnostics", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
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
                        ctx.Response.Body.WriteAsync(bytes).AsTask().Wait()
                        ctx.Response.Body.FlushAsync().Wait()
                    )
                    do! tcs.Task
                } :> Task
            ) |> ignore

            // GET /events — SSE stream of Elm state changes
            // GET /events — SSE stream of Elm state changes
            app.MapGet("/events", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    ctx.Response.ContentType <- "text/event-stream"
                    ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues("no-cache")
                    ctx.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues("keep-alive")

                    match stateChanged with
                    | Some evt ->
                        let tcs = System.Threading.Tasks.TaskCompletionSource()
                        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
                        use _sub = evt.Subscribe(fun json ->
                            try
                                let sseEvent = sprintf "event: state\ndata: %s\n\n" json
                                let bytes = System.Text.Encoding.UTF8.GetBytes(sseEvent)
                                ctx.Response.Body.WriteAsync(bytes).AsTask().Wait()
                                ctx.Response.Body.FlushAsync().Wait()
                            with _ -> ())
                        do! tcs.Task
                    | None ->
                        ctx.Response.StatusCode <- 501
                        let msg = System.Text.Encoding.UTF8.GetBytes("event: error\ndata: {\"error\":\"No Elm loop available\"}\n\n")
                        do! ctx.Response.Body.WriteAsync(msg)
                } :> Task
            ) |> ignore

            // GET /api/status — rich JSON status via proxy
            app.MapGet("/api/status", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let sid = !activeSessionId
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
            
            // Print startup info
            printfn "MCP SSE endpoint: http://localhost:%d/sse" port
            printfn "MCP message endpoint: http://localhost:%d/message" port
            printfn "Direct exec endpoint: http://localhost:%d/exec (no session ID required)" port
            printfn "State events SSE: http://localhost:%d/events" port
            printfn "Logs: %s" logPath
            
            // Print OTEL configuration
            let otelEndpoint = 
                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") 
                |> Option.ofObj 
                |> Option.defaultValue "not configured"
            
            if otelEndpoint <> "not configured" then
                printfn "✓ OpenTelemetry: Enabled → %s" otelEndpoint
            else
                printfn "○ OpenTelemetry: Not configured (set OTEL_EXPORTER_OTLP_ENDPOINT)"
            
            do! app.RunAsync()
        with ex ->
            eprintfn "MCP server failed to start: %s" ex.Message
    }
