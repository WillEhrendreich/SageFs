module SageFs.Server.McpServer

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open OpenTelemetry.Logs
open OpenTelemetry.Resources
open SageFs.AppState
open SageFs.McpTools

// ---------------------------------------------------------------------------
// MCP Push Notifications — tracks active connections and broadcasts events
// ---------------------------------------------------------------------------

open SageFs.Features.Diagnostics

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

  /// Discriminator tag used for Replace dedup.
  let tag = function
    | PushEvent.DiagnosticsChanged _ -> 0
    | PushEvent.StateChanged _ -> 1
    | PushEvent.FileReloaded _ -> 2
    | PushEvent.SessionFaulted _ -> 3
    | PushEvent.WarmupCompleted -> 4

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
          Nullable(doc.RootElement.Clone())
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
        eprintfn "✓ McpServerTracker: captured first MCP client connection"

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
let mkContext (store: Marten.IDocumentStore) (diagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>) (stateChanged: IEvent<string> option) (sessionOps: SageFs.SessionManagementOps) (mcpPort: int) (dispatch: (SageFs.SageFsMsg -> unit) option) (getElmModel: (unit -> SageFs.SageFsModel) option) (getElmRegions: (unit -> SageFs.RenderRegion list) option) : McpContext =
  { Store = store; DiagnosticsChanged = diagnosticsChanged; StateChanged = stateChanged; SessionOps = sessionOps; SessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>(); McpPort = mcpPort; Dispatch = dispatch; GetElmModel = getElmModel; GetElmRegions = getElmRegions }

// Start MCP server in background
let startMcpServer (diagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>) (stateChanged: IEvent<string> option) (store: Marten.IDocumentStore) (port: int) (sessionOps: SageFs.SessionManagementOps) (elmRuntime: SageFs.ElmRuntime<SageFs.SageFsModel, SageFs.SageFsMsg, SageFs.RenderRegion> option) =
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
            let mcpContext = (mkContext store diagnosticsChanged stateChanged sessionOps port dispatch getElmModel getElmRegions)
            
            // Register MCP services
            builder.Services.AddSingleton<McpContext>(mcpContext) |> ignore
            builder.Services.AddSingleton<SageFs.Server.McpTools.SageFsTools>(fun serviceProvider ->
                let logger = serviceProvider.GetRequiredService<ILogger<SageFs.Server.McpTools.SageFsTools>>()
                new SageFs.Server.McpTools.SageFsTools(mcpContext, logger)
            ) |> ignore
            
            // Create notification tracker
            let serverTracker = McpServerTracker()
            builder.Services.AddSingleton<McpServerTracker>(serverTracker) |> ignore

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
                .WithHttpTransport()
                .WithTools<SageFs.Server.McpTools.SageFsTools>()
                .AddCallToolFilter(createServerCaptureFilter serverTracker)
            |> ignore
            
            let app = builder.Build()
            
            // Map MCP endpoints
            app.MapMcp() |> ignore

            // Shared context — constructed once
            let mcpContext = mkContext store diagnosticsChanged stateChanged sessionOps port dispatch getElmModel getElmRegions
            
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
                                ctx.Response.Body.WriteAsync(bytes).AsTask()
                                |> fun t -> t.ContinueWith(fun (_: Task) -> ctx.Response.Body.FlushAsync()) |> ignore
                            with _ -> ())
                        do! tcs.Task
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
            
            // GET /api/sessions — list all sessions with details
            app.MapGet("/api/sessions", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
                withErrorHandling ctx (fun () -> task {
                    let! listing = sessionOps.ListSessions ()
                    // Also get per-session snapshots for richer info
                    let sessions = System.Collections.Generic.List<obj>()
                    // Parse listing to extract IDs, then get snapshots
                    // ListSessions returns formatted text; we need structured data
                    // Get all session info from manager
                    let! allInfo = task {
                      let results = System.Collections.Generic.List<{| id: string; status: string; projects: string list; workingDirectory: string; evalCount: int; avgDurationMs: float |}>()
                      // Attempt to get proxy for each known session ID by parsing the listing
                      // The listing is text-based, so we'll try each line
                      let lines = listing.Split('\n') |> Array.filter (fun l -> l.Contains("│") || l.Contains("["))
                      for line in lines do
                        // Try to extract session ID from the line
                        let parts = line.Split([|'['; ']'; '│'; ' '|], StringSplitOptions.RemoveEmptyEntries)
                        if parts.Length > 0 then
                          let candidateId = parts.[0].Trim()
                          if candidateId.Length >= 6 && not (candidateId.Contains("/")) then
                            let! info = sessionOps.GetSessionInfo candidateId
                            match info with
                            | Some i ->
                              let! proxy = sessionOps.GetProxy candidateId
                              let! evalCount, avgMs, status = task {
                                match proxy with
                                | Some send ->
                                  let! resp = send (SageFs.WorkerProtocol.WorkerMessage.GetStatus "api") |> Async.StartAsTask
                                  match resp with
                                  | SageFs.WorkerProtocol.WorkerResponse.StatusResult(_, snap) ->
                                    return snap.EvalCount, float snap.AvgDurationMs, SageFs.WorkerProtocol.SessionStatus.label snap.Status
                                  | _ -> return 0, 0.0, "Unknown"
                                | None -> return 0, 0.0, "Disconnected"
                              }
                              results.Add({| id = candidateId; status = status; projects = i.Projects; workingDirectory = i.WorkingDirectory; evalCount = evalCount; avgDurationMs = avgMs |})
                            | None -> ()
                      return results |> Seq.toList
                    }
                    do! jsonResponse ctx 200 {| sessions = allInfo |}
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
                      let! _ = SageFs.EventStore.appendEvents store "daemon-sessions" [
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
                      if root.TryGetProperty("workingDirectory") |> fst then
                        root.GetProperty("workingDirectory").GetString()
                      else Environment.CurrentDirectory
                    let projects =
                      if root.TryGetProperty("projects") |> fst then
                        root.GetProperty("projects").EnumerateArray()
                        |> Seq.map (fun e -> e.GetString())
                        |> Seq.toList
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

            // Wire push notifications: subscribe to events → broadcast to MCP clients
            // Two delivery paths:
            //   1. MCP notifications (for clients that surface them)
            //   2. Event accumulator → appended to next tool response (guaranteed delivery)
            //
            // Note: diagnosticsChanged event from DaemonMode is not wired to workers,
            // so we detect diag changes via stateChanged + Elm model access.

            let mutable lastDiagCount = 0
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

                    if serverTracker.Count > 0 then
                      let data =
                        {| event = "state_changed"
                           diagCount = diagCount
                           outputCount = outputCount |}
                      serverTracker.NotifyLogAsync(
                        LoggingLevel.Info, "sagefs.state", data) |> ignore
                  with _ -> ()))

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
