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

// Create shared MCP context
let private mkContext (actor: AppActor) (store: Marten.IDocumentStore) (sessionId: string) (diagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>) (cancelEval: unit -> bool) (getSessionState: unit -> SageFs.SessionState) (getEvalStats: unit -> SageFs.Affordances.EvalStats) (getWarmupFailures: unit -> SageFs.AppState.WarmupFailure list) (getStartupConfig: unit -> SageFs.AppState.StartupConfig option) (mode: SageFs.SessionMode) (dispatch: (SageFs.SageFsMsg -> unit) option) (getElmModel: (unit -> SageFs.SageFsModel) option) (getElmRegions: (unit -> SageFs.RenderRegion list) option) : McpContext =
  { Actor = actor; Store = store; SessionId = sessionId; DiagnosticsChanged = diagnosticsChanged; CancelEval = cancelEval; GetSessionState = getSessionState; GetEvalStats = getEvalStats; GetWarmupFailures = getWarmupFailures; GetStartupConfig = getStartupConfig; Mode = mode; Dispatch = dispatch; GetElmModel = getElmModel; GetElmRegions = getElmRegions }

// Start MCP server in background
let startMcpServer (actor: AppActor) (diagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>) (cancelEval: unit -> bool) (getSessionState: unit -> SageFs.SessionState) (getEvalStats: unit -> SageFs.Affordances.EvalStats) (getWarmupFailures: unit -> SageFs.AppState.WarmupFailure list) (getStartupConfig: unit -> SageFs.AppState.StartupConfig option) (store: Marten.IDocumentStore) (sessionId: string) (port: int) (mode: SageFs.SessionMode) (elmRuntime: SageFs.ElmRuntime<SageFs.SageFsModel, SageFs.SageFsMsg, SageFs.RenderRegion> option) =
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
            let mcpContext = (mkContext actor store sessionId diagnosticsChanged cancelEval getSessionState getEvalStats getWarmupFailures getStartupConfig mode dispatch getElmModel getElmRegions)
            
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
            
            // Add custom /exec endpoint that sends to cli-integrated session directly
            app.MapPost("/exec", fun (context: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    try
                        use reader = new System.IO.StreamReader(context.Request.Body)
                        let! body = reader.ReadToEndAsync()
                        
                        // Parse JSON body
                        let json = System.Text.Json.JsonDocument.Parse(body)
                        let code = 
                            if json.RootElement.TryGetProperty("code") |> fst then
                                json.RootElement.GetProperty("code").GetString()
                            else
                                body // fallback to raw body
                        
                        // Send code to cli-integrated session
                        let mcpContext = (mkContext actor store sessionId diagnosticsChanged cancelEval getSessionState getEvalStats getWarmupFailures getStartupConfig mode dispatch getElmModel getElmRegions)
                        let! result = SageFs.McpTools.sendFSharpCode mcpContext "cli-integrated" code SageFs.McpTools.OutputFormat.Text None
                        
                        // Return result as JSON
                        context.Response.ContentType <- "application/json"
                        let response = 
                            System.Text.Json.JsonSerializer.Serialize({| success = true; result = result |})
                        
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response))
                    with ex ->
                        context.Response.StatusCode <- 500
                        context.Response.ContentType <- "application/json"
                        let errorResponse = System.Text.Json.JsonSerializer.Serialize({| success = false; error = ex.Message |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(errorResponse))
                } :> Task
            ) |> ignore

            // POST /reset — reset the FSI session
            app.MapPost("/reset", fun (context: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    try
                        let mcpContext = (mkContext actor store sessionId diagnosticsChanged cancelEval getSessionState getEvalStats getWarmupFailures getStartupConfig mode dispatch getElmModel getElmRegions)
                        let! result = SageFs.McpTools.resetSession mcpContext None
                        context.Response.ContentType <- "application/json"
                        let isSuccess = not (result.Contains("Error"))
                        let response = System.Text.Json.JsonSerializer.Serialize({| success = isSuccess; message = result |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response))
                    with ex ->
                        context.Response.StatusCode <- 500
                        context.Response.ContentType <- "application/json"
                        let errorResponse = System.Text.Json.JsonSerializer.Serialize({| success = false; error = ex.Message |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(errorResponse))
                } :> Task
            ) |> ignore

            // POST /hard-reset — hard reset: re-shadow-copy, optionally rebuild
            app.MapPost("/hard-reset", fun (context: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    try
                        use reader = new System.IO.StreamReader(context.Request.Body)
                        let! body = reader.ReadToEndAsync()
                        let rebuild =
                            try
                                let json = System.Text.Json.JsonDocument.Parse(body)
                                if json.RootElement.TryGetProperty("rebuild") |> fst then
                                    json.RootElement.GetProperty("rebuild").GetBoolean()
                                else false
                            with _ -> false
                        let mcpContext = (mkContext actor store sessionId diagnosticsChanged cancelEval getSessionState getEvalStats getWarmupFailures getStartupConfig mode dispatch getElmModel getElmRegions)
                        let! result = SageFs.McpTools.hardResetSession mcpContext rebuild None
                        context.Response.ContentType <- "application/json"
                        let isSuccess = not (result.Contains("Error"))
                        let response = System.Text.Json.JsonSerializer.Serialize({| success = isSuccess; message = result |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response))
                    with ex ->
                        context.Response.StatusCode <- 500
                        context.Response.ContentType <- "application/json"
                        let errorResponse = System.Text.Json.JsonSerializer.Serialize({| success = false; error = ex.Message |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(errorResponse))
                } :> Task
            ) |> ignore

            // GET /health — session health check
            app.MapGet("/health", fun (context: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    try
                        let mcpContext = (mkContext actor store sessionId diagnosticsChanged cancelEval getSessionState getEvalStats getWarmupFailures getStartupConfig mode dispatch getElmModel getElmRegions)
                        let! status = SageFs.McpTools.getStatus mcpContext
                        context.Response.ContentType <- "application/json"
                        let response = System.Text.Json.JsonSerializer.Serialize({| healthy = true; status = status |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response))
                    with ex ->
                        context.Response.StatusCode <- 500
                        context.Response.ContentType <- "application/json"
                        let errorResponse = System.Text.Json.JsonSerializer.Serialize({| healthy = false; error = ex.Message |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(errorResponse))
                } :> Task
            ) |> ignore

            // POST /diagnostics — fire-and-forget diagnostics check, results via SSE
            app.MapPost("/diagnostics", fun (context: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    try
                        use reader = new System.IO.StreamReader(context.Request.Body)
                        let! body = reader.ReadToEndAsync()
                        let json = System.Text.Json.JsonDocument.Parse(body)
                        let code =
                            if json.RootElement.TryGetProperty("code") |> fst then
                                json.RootElement.GetProperty("code").GetString()
                            else
                                body
                        // Fire-and-forget: post to actor, don't block on result
                        // The diagnosticsChanged event will notify SSE subscribers
                        Async.Start(async {
                            let! _ = actor.PostAndAsyncReply(fun reply -> GetDiagnostics(code, reply))
                            return ()
                        })
                        context.Response.StatusCode <- 202
                        context.Response.ContentType <- "application/json"
                        let response = System.Text.Json.JsonSerializer.Serialize({| accepted = true |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response))
                    with ex ->
                        context.Response.StatusCode <- 500
                        context.Response.ContentType <- "application/json"
                        let errorResponse = System.Text.Json.JsonSerializer.Serialize({| accepted = false; error = ex.Message |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(errorResponse))
                } :> Task
            ) |> ignore

            // GET /diagnostics — SSE stream of diagnostics updates
            app.MapGet("/diagnostics", fun (context: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    context.Response.ContentType <- "text/event-stream"
                    context.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues("no-cache")
                    context.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues("keep-alive")

                    let mcpContext = (mkContext actor store sessionId diagnosticsChanged cancelEval getSessionState getEvalStats getWarmupFailures getStartupConfig mode dispatch getElmModel getElmRegions)
                    // Send current state on connect
                    let! st = mcpContext.Actor.PostAndAsyncReply(GetAppState) |> Async.StartAsTask
                    let initialJson = SageFs.McpAdapter.formatDiagnosticsStoreAsJson st.Diagnostics
                    let initialEvent = sprintf "event: diagnostics\ndata: %s\n\n" initialJson
                    do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(initialEvent))
                    do! context.Response.Body.FlushAsync()

                    // Subscribe to changes and stream updates
                    let tcs = System.Threading.Tasks.TaskCompletionSource()
                    use _ct = context.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
                    use _sub = diagnosticsChanged.Subscribe(fun store ->
                        let json = SageFs.McpAdapter.formatDiagnosticsStoreAsJson store
                        let sseEvent = sprintf "event: diagnostics\ndata: %s\n\n" json
                        let bytes = System.Text.Encoding.UTF8.GetBytes(sseEvent)
                        context.Response.Body.WriteAsync(bytes).AsTask().Wait()
                        context.Response.Body.FlushAsync().Wait()
                    )
                    do! tcs.Task
                } :> Task
            ) |> ignore

            // GET /api/status — rich JSON status for clients and dashboards
            app.MapGet("/api/status", fun (context: Microsoft.AspNetCore.Http.HttpContext) ->
                task {
                    try
                        let sessionState = getSessionState ()
                        let evalStats = getEvalStats ()
                        let startupConfig = getStartupConfig ()
                        let warmupFailures = getWarmupFailures ()
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
                        let workingDir =
                          startupConfig |> Option.map (fun c -> c.WorkingDirectory) |> Option.defaultValue ""
                        let projects =
                          startupConfig |> Option.map (fun c -> c.LoadedProjects) |> Option.defaultValue []
                        context.Response.ContentType <- "application/json"
                        let response = System.Text.Json.JsonSerializer.Serialize(
                          {| version = version
                             sessionId = sessionId
                             sessionState = SageFs.SessionState.label sessionState
                             evalCount = evalStats.EvalCount
                             totalDurationMs = evalStats.TotalDuration.TotalMilliseconds
                             avgDurationMs =
                               if evalStats.EvalCount > 0 then
                                 evalStats.TotalDuration.TotalMilliseconds / float evalStats.EvalCount
                               else 0.0
                             minDurationMs = evalStats.MinDuration.TotalMilliseconds
                             maxDurationMs = evalStats.MaxDuration.TotalMilliseconds
                             workingDirectory = workingDir
                             projectCount = projects.Length
                             projects = projects
                             warmupFailures = warmupFailures |> List.map (fun f -> {| name = f.Name; error = f.Error |})
                             regions = regionData
                             pid = Environment.ProcessId
                             uptime = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response))
                    with ex ->
                        context.Response.StatusCode <- 500
                        context.Response.ContentType <- "application/json"
                        let errorResponse = System.Text.Json.JsonSerializer.Serialize({| error = ex.Message |})
                        do! context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(errorResponse))
                } :> Task
            ) |> ignore
            
            // Print startup info
            printfn "MCP SSE endpoint: http://localhost:%d/sse" port
            printfn "MCP message endpoint: http://localhost:%d/message" port
            printfn "Direct exec endpoint: http://localhost:%d/exec (no session ID required)" port
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
