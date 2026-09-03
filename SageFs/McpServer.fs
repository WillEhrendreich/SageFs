module SageFs.Server.McpServer

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Channels
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
open Microsoft.AspNetCore.ResponseCompression
open SageFs.AppState
open SageFs.McpTools
open SageFs.McpPushNotifications
open SageFs.McpStateHandlers
open SageFs.Utils
open SageFs

// ---------------------------------------------------------------------------
// MCP Push Notifications — tracks active connections and broadcasts events
// ---------------------------------------------------------------------------

open SageFs.Features.Diagnostics
open SageFs.Features.LiveTesting

/// Tracks active MCP server connections for push notifications.
type McpServerTracker() =
  let servers = ConcurrentDictionary<string, McpServer>()
  let accumulator = EventAccumulator()

  member _.Register(server: McpServer) =
    servers.[server.SessionId] <- server

  member _.Remove(sessionId: string) =
    servers.TryRemove(sessionId) |> ignore

  /// Broadcast a structured logging notification to all connected MCP clients.
  /// Sends to all clients in parallel with a 500ms per-send timeout.
  member _.NotifyLogAsync(level: LoggingLevel, logger: string, data: obj) =
    task {
      match servers.IsEmpty with
      | true -> return ()
      | false ->
        let jsonElement =
          let json = JsonSerializer.Serialize(data)
          use doc = JsonDocument.Parse(json)
          doc.RootElement.Clone()
        let snapshot = servers |> Seq.map (fun kvp -> kvp.Key, kvp.Value) |> Seq.toArray
        let! results =
          snapshot
          |> Array.map (fun (key, server) -> task {
            use cts = new System.Threading.CancellationTokenSource(500)
            try
              let payload =
                LoggingMessageNotificationParams(
                  Level = level, Logger = logger, Data = jsonElement)
              do! server.SendNotificationAsync(
                NotificationMethods.LoggingMessageNotification, payload,
                cancellationToken = cts.Token)
              return None
            with
            | :? System.IO.IOException | :? ObjectDisposedException -> return Some key
            | :? System.OperationCanceledException -> return Some key
            | ex ->
              Log.error "[MCP] NotifyLog error for %s: %s\n%s" key ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
              return Some key
          })
          |> System.Threading.Tasks.Task.WhenAll
        for deadId in results |> Array.choose id do
          servers.TryRemove(deadId) |> ignore
    }

  /// Accumulate a structured event for delivery on the next tool response.
  member _.AccumulateEvent(evt: PushEvent) = accumulator.Add(evt)

  /// Drain accumulated events, format for LLM, return as string array.
  member _.DrainEvents() =
    accumulator.Drain()
    |> Array.map (fun e -> PushEvent.formatForLlm e.Event)

  member _.Count = servers.Count
  member _.PendingEvents = accumulator.Count

/// Auto-save a friction report when a tool call throws. The user's directive:
/// "no matter what we shouldn't throw exceptions... a really great idea would
/// be to automatically have sagefs save it's own friction report upon hitting
/// any kind of exception." This gives us a durable post-mortem record without
/// requiring the agent (or user) to manually report the failure.
let recordToolFailure (ctx: McpContext) (tracker: McpServerTracker) (ex: exn) =
  // Best-effort tool name extraction. The AIFunctionFactory doesn't surface
  // the called tool's name in the exception path, so we fall back to a
  // hint parsed from the exception message.
  let toolName =
    match ex.Message with
    | m when m.Contains "'code'" -> "unknown (missing 'code' argument)"
    | m when m.Contains "argument" -> "unknown (missing argument)"
    | _ -> "unknown"
  let event : SageFs.Features.FrictionTelemetryTypes.FrictionEvent =
    { OccurredAtUtc = System.DateTimeOffset.UtcNow
      Session = SageFs.Features.FrictionTelemetryTypes.SessionRef.create "mcp" |> McpTools.ok
      Tool = SageFs.Features.FrictionTelemetryTypes.ToolName.create toolName |> McpTools.ok
      Intent = SageFs.Features.FrictionTelemetryTypes.IntentKind.ExploreCode
      Outcome =
        SageFs.Features.FrictionTelemetryTypes.FrictionOutcome.EncounteredBlocker
          SageFs.Features.FrictionTelemetryTypes.BlockerKind.InvalidRequest
      Duration = SageFs.Features.FrictionTelemetryTypes.DurationMs.create 0 |> McpTools.ok
      FollowUp = SageFs.Features.FrictionTelemetryTypes.FollowUp.NoFollowUpYet
      ContextCost = SageFs.Features.FrictionTelemetryTypes.ContextCost.Focused
      SageFsVersion = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current () }
  // Write to the durable SQLite store if available.
  // P0 automatic-capture defect: this previously built
  //   task { ... } |> Async.AwaitTask |> ignore
  // — an Async that was never started, so pre-wrapper MCP failures were never
  // durably captured. The store append is synchronous, so call it directly.
  match ctx.FrictionStore with
  | Some store ->
    try
      SageFs.Features.McpFrictionRecorder.Recorder.appendEventDirect store event
      |> Async.AwaitTask
      |> Async.RunSynchronously
      |> ignore
    with _ -> () // never throw from the failure path
  | None -> ()
  // Note: we deliberately do NOT push this to the in-memory PushEvent tracker.
  // PushEvent is for SSE state broadcasts (file reloads, test results, etc.)
  // not for friction reporting. Friction lives in the durable SQLite store
  // and is queryable via the dashboard.

/// CallToolFilter that captures the McpServer and appends accumulated events
/// to tool responses. This ensures the LLM sees events even if the client
/// doesn't surface MCP notifications directly.
let createServerCaptureFilter (mcpCtx: McpContext) (tracker: McpServerTracker) =
  let mutable logged = 0  // 0 = not logged; use Interlocked to ensure atomicity
  McpRequestFilter<CallToolRequestParams, CallToolResult>(fun next ->
    McpRequestHandler<CallToolRequestParams, CallToolResult>(fun ctx ct ->
      let wasEmpty = tracker.Count = 0
      tracker.Register(ctx.Server)
      match wasEmpty && System.Threading.Interlocked.CompareExchange(&logged, 1, 0) = 0 with
      | true ->
        let logger =
          ctx.Services.GetService(typeof<ILoggerFactory>)
          |> Option.ofObj
          |> Option.map (fun f -> (f :?> ILoggerFactory).CreateLogger("SageFs.McpServer.Filter"))
        match logger with
        | Some l -> l.LogInformation("First MCP client connected")
        | None -> ()
      | false -> ()

      let inline appendEvents (result: CallToolResult) =
        let events = tracker.DrainEvents()
        match events.Length > 0 with
        | true ->
          let eventText =
            events
            |> Array.map (sprintf "  • %s")
            |> String.concat "\n"
          let banner = sprintf "\n\n📡 SageFs events since last call:\n%s" eventText
          result.Content.Add(TextContentBlock(Text = banner))
        | false -> ()
        result

      /// WHY — the caller of an MCP tool is usually a language model whose only
      /// recovery mechanism is reading error text and retrying correctly. A thrown
      /// exception gives it nothing (observed 2026-08: a missing-argument
      /// ArgumentException escaped as an unhandled server exception). Because —
      /// every tool failure is translated into an IsError result the agent can act on.
      let buildErrorResult (ex: exn) =
        let logger =
          ctx.Services.GetService(typeof<ILoggerFactory>)
          |> Option.ofObj
          |> Option.map (fun f -> (f :?> ILoggerFactory).CreateLogger("SageFs.McpServer.Filter"))
        match logger with
        | Some l -> l.LogError(ex, "MCP tool call threw; returning error result to client")
        | None -> ()
        // Per the user directive: never throw — translate every exception into a
        // structured IsError result the agent can read and act on. Parse the
        // exception to provide a helpful hint rather than a raw stack-shaped
        // message.
        let message =
          match box ex with
          | :? System.ArgumentException as argEx ->
            // The most common case: a tool was called without a required parameter.
            // Surface the parameter name in the message so the agent can retry correctly.
            sprintf "Missing or invalid argument: %s. %s"
              (if String.IsNullOrEmpty(argEx.ParamName) then "(unknown parameter)" else argEx.ParamName)
              argEx.Message
          | :? System.Reflection.TargetInvocationException as tie ->
            // The AIFunctionFactory wraps target invocations in this; unwrap.
            match tie.InnerException with
            | null -> sprintf "Tool call failed: %s" ex.Message
            | inner -> sprintf "Tool call failed: %s" inner.Message
          | _ ->
            sprintf "Tool call failed: %s: %s" (ex.GetType().Name) ex.Message
        // Also auto-save a friction report on any exception so we have a record
        // for post-mortem debugging. Never throw from this path — the whole
        // point is to surface errors cleanly.
        try
          recordToolFailure mcpCtx tracker ex
        with _ -> ()
        let result = CallToolResult()
        result.IsError <- Nullable true
        result.Content.Add(TextContentBlock(Text = message))
        result

      /// Structural affordance gate: every `tools/call` reaches the tool body
      /// ONLY through this check. `Affordances.toolGate` classifies each tool;
      /// state-gated tools are checked against the state of the session the call
      /// targets (resolved exactly as the tool body would resolve it — the "mcp"
      /// agent key plus the same working_directory argument), always-available
      /// tools pass in every state, and undeclared tools fail closed.
      /// A rejected call returns a structured IsError result (never throws,
      /// never executes the tool body).
      let enforceToolGate (toolName: string) =
        task {
          // MCP tool handlers all route as agent "mcp" and take no session_id
          // parameter; working_directory is the only routing argument.
          let workingDirectoryArg =
            match ctx.Params.Arguments with
            | null -> None
            | args ->
              match args.TryGetValue("working_directory") with
              | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String ->
                let s = v.GetString()
                match String.IsNullOrWhiteSpace s with
                | true -> None
                | false -> Some s
              | _ -> None
          return!
            SageFs.McpTools.enforceToolCallGate
              mcpCtx "mcp" None workingDirectoryArg toolName
        }

      let buildGateErrorResult (gateError: string) =
        let result = CallToolResult()
        result.IsError <- Nullable true
        result.Content.Add(TextContentBlock(Text = gateError))
        result

      let requestName =
        match box ctx.Params with
        | null -> ""
        | _ -> ctx.Params.Name

      ValueTask<CallToolResult>(
        task {
          try
            // Affordance gate: reject tools that are not available in the
            // current session state BEFORE the tool body runs.
            match String.IsNullOrWhiteSpace requestName with
            | false ->
              let! gateResult = enforceToolGate requestName
              match gateResult with
              | Error gateError ->
                return buildGateErrorResult gateError
              | Ok _ ->
                let! result = next.Invoke(ctx, ct).AsTask()
                return appendEvents result
            | true ->
              let! result = next.Invoke(ctx, ct).AsTask()
              return appendEvents result
          with ex ->
            return buildErrorResult ex
        })))

/// Raised when a request body exceeds the 4 MB hard limit.
/// withErrorHandling catches this and swallows it (413 already committed).
exception RequestTooLarge

let private maxRequestBodyBytes = 4_194_304L

/// Write a JSON response with the given status code.
let jsonResponse (ctx: Microsoft.AspNetCore.Http.HttpContext) (statusCode: int) (data: obj) = task {
  ctx.Response.StatusCode <- statusCode
  ctx.Response.ContentType <- "application/json"
  let json = System.Text.Json.JsonSerializer.Serialize(data)
  do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json))
}

/// Write a pre-serialized JSON string as the response body.
let rawJsonResponse (ctx: Microsoft.AspNetCore.Http.HttpContext) (json: string) = task {
  ctx.Response.ContentType <- "application/json"
  do! ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json))
}

/// Standard error body: `error` stays a client-compatible string (VS Code's
/// parseOutcome reads it via fieldString), `errorDetails` carries the full
/// SageFsError algebra (case/message/suggestedAction) for agents and logs.
let structuredErrorBody (err: SageFsError) =
  let details = SageFsError.toJson err
  box {| success = false
         error = SageFsError.describe err
         errorDetails = details |}

/// Build the structured body for an unexpected exception, logging the full
/// details server-side while the wire carries the algebra-safe description.
let unexpectedErrorBody (ex: exn) =
  structuredErrorBody (SageFsError.Unexpected ex)

let private writeRequestTooLargeResponse (ctx: Microsoft.AspNetCore.Http.HttpContext) = task {
  do! jsonResponse ctx 413 {| success = false; error = "Request body too large" |}
}

/// Read JSON body and extract a string property, with fallback to raw body.
let readJsonProp (ctx: Microsoft.AspNetCore.Http.HttpContext) (prop: string) = task {
  match ctx.Request.ContentLength with
  | contentLength when contentLength.HasValue && contentLength.Value > maxRequestBodyBytes ->
    do! writeRequestTooLargeResponse ctx
    raise RequestTooLarge
    return null  // unreachable — satisfies type checker
  | _ ->
  use reader = new System.IO.StreamReader(ctx.Request.Body)
  let! body = reader.ReadToEndAsync()
  match int64 (System.Text.Encoding.UTF8.GetByteCount(body)) > maxRequestBodyBytes with
  | true ->
    do! writeRequestTooLargeResponse ctx
    raise RequestTooLarge
    return null  // unreachable
  | false ->
  try
    use json = System.Text.Json.JsonDocument.Parse(body)
    match json.RootElement.TryGetProperty(prop) with
    | true, v -> return v.GetString()
    | _ -> return body
  with :? System.Text.Json.JsonException -> return body
}

/// Read and validate a sessionId from JSON body. Returns 400 on invalid format.
let readValidatedSessionId (ctx: Microsoft.AspNetCore.Http.HttpContext) = task {
  let! raw = readJsonProp ctx "sessionId"
  match SageFs.WorkerProtocol.SessionId.validate raw with
  | Ok sid -> return Some sid
  | Error msg ->
    do! jsonResponse ctx 400 {| success = false; error = msg |}
    return None
}

/// Convert a known-good string to SessionId, throwing on invalid format.
/// Use only for strings that have already been validated or originate from SessionId.value round-trips.
let private toSessionId (s: string) =
  match SageFs.WorkerProtocol.SessionId.validate s with
  | Ok sid -> sid
  | Error _ -> failwithf "invalid session ID: %s" s

/// Wrap an async handler with try/catch and JSON error response.
/// Kept for backward compatibility — the global errorHandlingMiddleware now provides
/// this protection for all routes, so per-endpoint wrapping is no longer needed.
let withErrorHandling (ctx: Microsoft.AspNetCore.Http.HttpContext) (handler: unit -> Task) = task {
  try do! handler ()
  with
  | RequestTooLarge -> ()  // 413 already committed — do not write a second response
  | :? System.Text.Json.JsonException as je ->
    do! jsonResponse ctx 400 (structuredErrorBody (SageFsError.JsonParseError ("request body", je.Message)))
  | ex ->
    do! jsonResponse ctx 500 (unexpectedErrorBody ex)
}

/// Global error-handling middleware — catches unhandled exceptions from all endpoints.
/// Replaces per-endpoint withErrorHandling wrapping.  For SSE/streaming responses that
/// have already started writing, the middleware skips the JSON error response (can't
/// change Content-Type or status after headers are sent).
let errorHandlingMiddleware (ctx: Microsoft.AspNetCore.Http.HttpContext) (next: Func<Task>) = task {
  try
    do! next.Invoke()
  with
  | RequestTooLarge -> ()  // 413 already committed — do not write a second response
  | :? System.Text.Json.JsonException as je ->
    match ctx.Response.HasStarted with
    | true -> ()  // SSE or streaming response already committed
    | false -> do! jsonResponse ctx 400 (structuredErrorBody (SageFsError.JsonParseError ("request body", je.Message)))
  | ex ->
    match ctx.Response.HasStarted with
    | true -> ()  // SSE or streaming response already committed
    | false -> do! jsonResponse ctx 500 (unexpectedErrorBody ex)
}

/// Browser-origin/CSRF gate (see HttpOriginGuard). Rejects cross-site and
/// non-loopback requests before any route runs; local tooling (curl, MCP,
/// editors, CLI — no browser headers) passes untouched.
let originGuardMiddleware (ctx: Microsoft.AspNetCore.Http.HttpContext) (next: Func<Task>) = task {
  let host =
    match ctx.Request.Host.HasValue with
    | true -> Some (string ctx.Request.Host)
    | false -> None
  let secFetchSite =
    match ctx.Request.Headers.TryGetValue("Sec-Fetch-Site") with
    | true, v when not (System.String.IsNullOrWhiteSpace(string v)) -> Some (string v)
    | _ -> None
  let origin =
    match ctx.Request.Headers.TryGetValue("Origin") with
    | true, v when not (System.String.IsNullOrWhiteSpace(string v)) -> Some (string v)
    | _ -> None
  match SageFs.Server.HttpOriginGuard.decide host secFetchSite origin with
  | SageFs.Server.HttpOriginGuard.Verdict.Allow ->
    do! next.Invoke()
  | SageFs.Server.HttpOriginGuard.Verdict.Reject reason ->
    Log.warn "[origin-guard] rejected %s %s (%s)" ctx.Request.Method (string ctx.Request.Path) reason
    ctx.Response.StatusCode <- 403
    do! jsonResponse ctx 403 {| success = false; error = sprintf "Request rejected: %s" reason |}
}

/// Read and parse the request body as a JSON document.
let readJsonBody (ctx: Microsoft.AspNetCore.Http.HttpContext) = task {
  match ctx.Request.ContentLength with
  | contentLength when contentLength.HasValue && contentLength.Value > maxRequestBodyBytes ->
    do! writeRequestTooLargeResponse ctx
    raise RequestTooLarge
    return System.Text.Json.JsonDocument.Parse("null")  // unreachable
  | _ ->
  use reader = new System.IO.StreamReader(ctx.Request.Body)
  let! body = reader.ReadToEndAsync()
  match int64 (System.Text.Encoding.UTF8.GetByteCount(body)) > maxRequestBodyBytes with
  | true ->
    do! writeRequestTooLargeResponse ctx
    raise RequestTooLarge
    return System.Text.Json.JsonDocument.Parse("null")  // unreachable
  | false ->
  return System.Text.Json.JsonDocument.Parse(body)
}

let tryGetJsonStringAliases (root: System.Text.Json.JsonElement) (names: string list) =
  let normalize value =
    match String.IsNullOrWhiteSpace value with
    | true -> None
    | false -> Some value

  names
  |> List.tryPick (fun name ->
    match root.TryGetProperty(name) with
    | true, prop ->
      match prop.ValueKind with
      | JsonValueKind.Null
      | JsonValueKind.Undefined -> None
      | JsonValueKind.String -> prop.GetString() |> normalize
      | _ -> prop.ToString() |> normalize
    | false, _ -> None)

let tryGetJsonIntAliases (root: System.Text.Json.JsonElement) (names: string list) =
  names
  |> List.tryPick (fun name ->
    match root.TryGetProperty(name) with
    | true, prop ->
      match prop.ValueKind with
      | JsonValueKind.Number ->
        match prop.TryGetInt32() with
        | true, value -> Some value
        | false, _ -> None
      | JsonValueKind.String ->
        match Int32.TryParse(prop.GetString()) with
        | true, value -> Some value
        | false, _ -> None
      | _ -> None
    | false, _ -> None)

/// Write an SSE frame to a stream (awaitable — use in task{} CEs).
let writeSseFrame (body: System.IO.Stream) (frame: string) = task {
  let bytes = System.Text.Encoding.UTF8.GetBytes(frame)
  SageFs.Instrumentation.sseFrameBytes.Record(int64 bytes.Length)
  do! body.WriteAsync(bytes)
  do! body.FlushAsync()
}

/// Run a single-writer SSE loop: all Observable sources and heartbeat
/// funnel through a bounded Channel, one async reader writes to the stream.
/// Fixes: (1) sync IO on Kestrel, (2) concurrent write data race.
let runSseWriteLoop
  (body: System.IO.Stream)
  (ct: CancellationToken)
  (sources: IObservable<string> list)
  (heartbeatMs: int) =
  task {
    let opts = BoundedChannelOptions(512, FullMode = BoundedChannelFullMode.DropOldest)
    let ch = Channel.CreateBounded<string>(opts)
    let subs = ResizeArray<IDisposable>()
    try
      for src in sources do
        src.Subscribe(fun frame ->
          match ch.Writer.TryWrite(frame) with
          | true -> ()
          | false ->
            SageFs.Instrumentation.sseEventsDropped.Add(1L)
            Log.debug "[SSE] Event dropped (buffer full)")
        |> subs.Add
      use _heartbeat =
        new Timer((fun _ -> ch.Writer.TryWrite(": keepalive\n\n") |> ignore), null, heartbeatMs, heartbeatMs)
      // Send retry hint as first frame — tells client reconnection interval per SSE spec
      do! writeSseFrame body (SageFs.SseWriter.formatRetryHint heartbeatMs)
      try
        while not ct.IsCancellationRequested do
          let! frame = ch.Reader.ReadAsync(ct)
          do! writeSseFrame body frame
      with
      | :? OperationCanceledException -> ()
      | :? System.IO.IOException as ex ->
        SageFs.Instrumentation.sseWriteErrors.Add(1L)
        Log.debug "[SSE] Client disconnected (IOException): %s" ex.Message
      | :? ObjectDisposedException as ex ->
        SageFs.Instrumentation.sseWriteErrors.Add(1L)
        Log.debug "[SSE] Client disconnected (ObjectDisposed): %s" ex.Message
    finally
      for sub in subs do sub.Dispose()
  }

/// Set standard SSE response headers.
let setSseHeaders (ctx: Microsoft.AspNetCore.Http.HttpContext) =
  ctx.Response.ContentType <- "text/event-stream"
  ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues("no-cache")
  ctx.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues("keep-alive")

/// Configuration for the MCP server — replaces 8 positional params on startMcpServer.
type McpServerConfig = {
  DiagnosticsChanged: IEvent<SageFs.Features.DiagnosticsStore.T>
  StateChanged: IEvent<DaemonStateChange> option
  FrictionStore: SageFs.Features.FrictionSqlite.FrictionStore option
  Port: int
  SessionOps: SageFs.SessionManagementOps
  ElmRuntime: SageFs.ElmRuntime<SageFs.SageFsModel, SageFs.SageFsMsg, SageFs.RenderRegion> option
  GetWarmupContext: (string -> Task<SageFs.WarmupContext option>) option
  GetHotReloadState: (string -> Task<string list option>) option
  SharedBindingScope: SageFs.Features.BindingExplorer.BindingScopeSnapshot option ref
  /// Shared feature push state ref — exposed so consumers (e.g. Dashboard) can read EvalTimeline.
  /// If None, startMcpServer creates a private ref that is inaccessible externally.
  SharedFeatureState: SageFs.Features.FeatureHooks.FeaturePushState ref option
  /// In-memory agent activity tracker for multi-agent coordination.
  ActivityTracker: SageFs.AgentActivityTracker.Tracker
  /// Receives the live bound-value snapshot after each successful eval.
  LiveSnapshotSink: (string -> SageFs.Features.LiveValueTree.LiveValueSnapshot -> unit) option
}

// Create shared MCP context (private — called only by startMcpServer)
let private mkContext (cfg: McpServerConfig) (stateChangedStr: IEvent<string> option) (featureStateGetter: (unit -> SageFs.Features.FeatureHooks.FeaturePushState) option) : McpContext =
  let dispatch = cfg.ElmRuntime |> Option.map (fun r -> r.Dispatch)
  let getElmModel = cfg.ElmRuntime |> Option.map (fun r -> r.GetModel)
  let getElmRegions = cfg.ElmRuntime |> Option.map (fun r -> r.GetRegions)
  { FrictionStore = cfg.FrictionStore; DiagnosticsChanged = cfg.DiagnosticsChanged; StateChanged = stateChangedStr; SessionOps = cfg.SessionOps; SessionMap = ConcurrentDictionary<string, string>(); McpPort = cfg.Port; Dispatch = dispatch; GetElmModel = getElmModel; GetElmRegions = getElmRegions; GetWarmupContext = cfg.GetWarmupContext; GetFeatureState = featureStateGetter; ActivityTracker = cfg.ActivityTracker; LiveSnapshotSink = cfg.LiveSnapshotSink }

// ── SSE context: groups immutable dependencies for state change handlers ──

/// Immutable context shared by SSE replay and state change handlers.
/// Created once per MCP server start; handlers close over this instead of
/// ad-hoc capturing locals from startMcpServer's scope.
type SseContext = {
  GetElmModel: (unit -> SageFs.SageFsModel) option
  GetWarmupContext: (string -> Task<SageFs.WarmupContext option>) option
  GetHotReloadState: (string -> Task<string list option>) option
  SseJsonOpts: JsonSerializerOptions
  TestEventBroadcast: Event<string>
  SessionEventBroadcast: Event<string>
  ServerTracker: McpServerTracker
}

module SseContext =
  let activeSessionId (ctx: SseContext) =
    ctx.GetElmModel
    |> Option.bind (fun gm ->
      SageFs.ActiveSession.sessionId (gm().Sessions.ActiveSessionId)
      |> Option.map SageFs.WorkerProtocol.SessionId.value)

  let withModel (ctx: SseContext) (f: SageFs.SageFsModel -> unit) =
    ctx.GetElmModel |> Option.iter (fun getModel -> f (getModel()))

  let withModelAsync (ctx: SseContext) (f: SageFs.SageFsModel -> Task) =
    match ctx.GetElmModel with
    | Some getModel -> f (getModel())
    | None -> Task.CompletedTask

// ── SSE replay: send cached state on new SSE connection ──

/// Replay warmup context + hotreload state for a new SSE connection.
let replaySessionSnapshot (ctx: SseContext) (body: System.IO.Stream) =
  match ctx.GetElmModel, ctx.GetWarmupContext with
  | Some getModel, Some getCtx ->
    task {
      try
        let activeId =
          let model = getModel()
          SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
          |> Option.map SageFs.WorkerProtocol.SessionId.value
          |> Option.defaultValue ""
        match activeId.Length > 0 with
        | true ->
          let! ctxOpt = getCtx activeId
          match ctxOpt with
          | Some wctx ->
            let evt = SageFs.SessionEvents.WarmupContextSnapshot(activeId, wctx)
            do! evt |> SageFs.SessionEvents.formatSessionSseEvent |> writeSseFrame body
          | None -> ()
          match ctx.GetHotReloadState with
          | Some getHr ->
            let! hrOpt = getHr activeId
            match hrOpt with
            | Some watchedFiles ->
              let hrEvt = SageFs.SessionEvents.HotReloadSnapshot(activeId, watchedFiles)
              do! hrEvt |> SageFs.SessionEvents.formatSessionSseEvent |> writeSseFrame body
            | None -> ()
          | None -> ()
        | false -> ()
      with
      | :? System.IO.IOException | :? ObjectDisposedException -> ()
      | ex -> Log.error "[SSE] Session snapshot replay error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
    }
  | _ -> task { () }

/// Replay cached test results + file annotations for a new SSE connection.
let replayCachedTestState (ctx: SseContext) (body: System.IO.Stream) =
  SseContext.withModelAsync ctx (fun model -> task {
    try
      let lt = model.LiveTesting.TestState
      let activeId = SseContext.activeSessionId ctx |> Option.defaultValue ""
      let sessionEntries =
        LiveTestState.statusEntriesForSession activeId lt
      // Zero-test suppression defect: even with zero entries, an ACTIVE
      // live-testing session that completed discovery must announce its
      // authoritative discovery state to late clients. Discovery state is
      // derived from Activation + DiscoveredTests + LastDiscoveryTime; emit
      // whenever live testing is enabled OR entries exist (nothing to emit
      // for a fully-disabled session).
      let discoveryState = LiveTestState.discoveryState lt
      match lt.Activation = LiveTestingActivation.Active || sessionEntries.Length > 0 with
      | true ->
        let s = TestSummary.fromStatuses
                  lt.Activation (sessionEntries |> Array.map (fun e -> e.Status))
        do! SageFs.SseWriter.formatTestSummaryEventWithDiscovery ctx.SseJsonOpts (Some activeId) s lt.LastDecision discoveryState lt.DiscoveryGeneration
            |> writeSseFrame body
        let freshness =
          match lt.RunPhases |> Map.exists (fun _ p -> match p with TestRunPhase.RunningButEdited _ -> true | _ -> false) with
          | true -> ResultFreshness.StaleCodeEdited
          | false -> ResultFreshness.Fresh
        let payload =
          let completion =
            TestResultsBatchPayload.deriveCompletion
              freshness lt.DiscoveredTests.Length sessionEntries.Length
          TestResultsBatchPayload.create
            lt.LastGeneration freshness completion lt.Activation sessionEntries lt.LastDecision
        do! SageFs.SseWriter.formatTestResultsBatchEvent ctx.SseJsonOpts (Some activeId) payload
            |> writeSseFrame body
        let files =
          sessionEntries
          |> Array.choose (fun e ->
            match e.Origin with
            | TestOrigin.SourceMapped (f, _) -> Some f
            | _ -> None)
          |> Array.distinct
        let ltState = model.LiveTesting
        for file in files do
          let fa = FileAnnotations.projectWithCoverage file ltState
          match fa.TestAnnotations.Length > 0 || fa.CodeLenses.Length > 0 || fa.CoverageAnnotations.Length > 0 || fa.InlineFailures.Length > 0 with
          | true ->
            do! SageFs.SseWriter.formatFileAnnotationsEvent ctx.SseJsonOpts (Some activeId) fa
                |> writeSseFrame body
            // Emit coverage_view events on replay so late-connecting
            // editors get the same views as already-connected ones.
            let views =
              FileAnnotationsInternals.projectViewsForFile
                CoverageViewMode.defaults file ltState.DepGraph ltState.TestState
            let gen = RunGeneration.value ltState.TestState.LastGeneration
            for view in views do
              do! SageFs.SseWriter.formatCoverageViewEvent ctx.SseJsonOpts (Some activeId) gen view
                  |> writeSseFrame body
          | false -> ()
      | false -> ()
    with ex ->
      Log.error "[SSE] replay error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
  })

// ── Session event subscription: push HotReload/SessionReady via SSE ──

/// Subscribe to DaemonStateChange events and push session-level SSE events
/// (warmup context snapshot, hotreload state) to all connected clients.
let wireSessionEventSubscription
  (stateChanged: IEvent<DaemonStateChange>)
  (ctx: SseContext) =
  match ctx.GetElmModel, ctx.GetWarmupContext with
  | Some _getModel, Some getCtx ->
    stateChanged.Subscribe(fun change ->
      match change with
      | DaemonStateChange.HotReloadChanged sid ->
        task {
          try
            // Session-isolation: the event carries the affected session. Never
            // fall back to a global "active session" here — an event for
            // session B must not push session A's hot-reload state just
            // because A is the active tab in some client.
            let activeId = SageFs.WorkerProtocol.SessionId.value sid
            match ctx.GetHotReloadState with
            | Some getHr ->
              let! hrOpt = getHr activeId
              match hrOpt with
              | Some watchedFiles ->
                let evt = SageFs.SessionEvents.HotReloadSnapshot(activeId, watchedFiles)
                ctx.SessionEventBroadcast.Trigger(SageFs.SessionEvents.formatSessionSseEvent evt)
              | None -> ()
            | None -> ()
          with
          | :? System.IO.IOException -> ()
          | ex -> Log.error "[SSE] HotReload push error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        }
        |> fun t -> t.ContinueWith(fun (t: Threading.Tasks.Task) ->
          match t.IsFaulted with
          | true -> Log.error "[SSE] HotReload push fault: %s" t.Exception.InnerException.Message
          | false -> ())
        |> ignore
      | DaemonStateChange.SessionReady sid ->
        ctx.ServerTracker.AccumulateEvent(PushEvent.WarmupCompleted)
        let sidStr = SageFs.WorkerProtocol.SessionId.value sid
        task {
          try
            match sidStr.Length > 0 with
            | true ->
              let! ctxOpt = getCtx sidStr
              match ctxOpt with
              | Some wctx ->
                let evt = SageFs.SessionEvents.WarmupContextSnapshot(sidStr, wctx)
                ctx.SessionEventBroadcast.Trigger(SageFs.SessionEvents.formatSessionSseEvent evt)
              | None -> ()
              match ctx.GetHotReloadState with
              | Some getHr ->
                let! hrOpt = getHr sidStr
                match hrOpt with
                | Some watchedFiles ->
                  let hrEvt = SageFs.SessionEvents.HotReloadSnapshot(sidStr, watchedFiles)
                  ctx.SessionEventBroadcast.Trigger(SageFs.SessionEvents.formatSessionSseEvent hrEvt)
                | None -> ()
              | None -> ()
            | false -> ()
          with
          | :? System.IO.IOException -> ()
          | ex -> Log.error "[SSE] SessionReady push error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        }
        |> fun t -> t.ContinueWith(fun (t: Threading.Tasks.Task) ->
          match t.IsFaulted with
          | true ->
            let msg =
              match t.Exception with
              | null -> "unknown fault"
              | ae ->
                ae.Flatten().InnerExceptions
                |> Seq.map (fun e -> e.Message)
                |> String.concat "; "
            Log.error "[SSE] SessionReady push fault: %s" msg
          | false -> ())
        |> ignore
      | DaemonStateChange.WarmupProgress(sid, step, total, msg) ->
        let sidStr = SageFs.WorkerProtocol.SessionId.value sid
        let sseFrame = SageFs.SseWriter.formatWarmupProgressEvent ctx.SseJsonOpts (Some sidStr) step total msg
        ctx.SessionEventBroadcast.Trigger(sseFrame)
      | DaemonStateChange.FileReloaded (_sid, path) ->
        ctx.ServerTracker.AccumulateEvent(PushEvent.FileReloaded path)
      | DaemonStateChange.SessionFaulted (_sid, error) ->
        ctx.ServerTracker.AccumulateEvent(PushEvent.SessionFaulted error)
      | _ -> ()) |> ignore
  | _ -> ()

// ── Model change handlers: state change → SSE + MCP notifications ──

/// Wire DaemonStateChange.ModelChanged events to the handler pipeline.
/// Creates handler closures and subscribes them to the event.
/// Returns the subscription disposable.
let wireModelChangeHandlers
  (stateChanged: IEvent<DaemonStateChange>)
  (ctx: SseContext)
  (fsiBindings: Map<string, SageFs.SseWriter.FsiBinding> ref)
  (featurePushState: SageFs.Features.FeatureHooks.FeaturePushState ref)
  (lastFeatureOutputCount: int ref)
  (sharedBindingScope: SageFs.Features.BindingExplorer.BindingScopeSnapshot option ref)
  (lastEvalContext: (string * int) option ref) =
  let modelChangeState = ref ModelChangeState.empty

  let handleDiagnosticsChange diagCount =
    SseContext.withModel ctx (fun model ->
      let state', effects =
        processDiagnosticsChange diagCount model.Diagnostics modelChangeState.Value
      modelChangeState.Value <- state'
      for effect in effects do
        match effect with
        | AccumulatePush evt -> ctx.ServerTracker.AccumulateEvent(evt)
        | BroadcastTestSse _ -> ())

  let handleBindingsChange outputCount =
    match outputCount <> modelChangeState.Value.LastOutputCount with
    | true ->
      match outputCount < modelChangeState.Value.LastOutputCount with
      | true -> fsiBindings.Value <- Map.empty
      | false -> ()
      modelChangeState.Value <- { modelChangeState.Value with LastOutputCount = outputCount }
      SseContext.withModel ctx (fun model ->
        let sid = SseContext.activeSessionId ctx |> Option.defaultValue ""
        let rawOutput =
          model.RecentOutput.GetBuffer(sid).FilterToList(fun o ->
            o.Kind = SageFs.OutputKind.Result)
          |> List.rev
          |> List.map (fun o -> o.Text)
          |> String.concat "\n"
        let newBindings =
          rawOutput
          |> SageFs.SseWriter.parseBindingsFromOutput
          |> SageFs.SseWriter.accumulateBindings Map.empty
        let bindingValues =
          SageFs.Features.FsiOutputParser.parseFsiBatch rawOutput
        match newBindings <> fsiBindings.Value with
        | true ->
          fsiBindings.Value <- newBindings
          let (bsl, fp) =
            lastEvalContext.Value
            |> Option.map (fun (fp, bsl) -> bsl, Some fp)
            |> Option.defaultValue (0, None)
          fsiBindings.Value
          |> Map.values |> Array.ofSeq
          |> SageFs.SseWriter.formatBindingsSnapshotEvent ctx.SseJsonOpts (Some sid) bindingValues bsl fp
          |> ctx.TestEventBroadcast.Trigger
        | false -> ())
    | false -> ()

  let handleTestTraceChange () =
    SseContext.withModel ctx (fun model ->
      let sid = SseContext.activeSessionId ctx
      let lt = model.LiveTesting
      let traceJson =
        try
          let sidStr = sid |> Option.defaultValue ""
          let summary =
            SageFs.Features.LiveTesting.TestSummary.fromStatuses
              lt.TestState.Activation
              (LiveTestState.statusEntriesForSession sidStr lt.TestState
               |> Array.map (fun e -> e.Status))
          System.Text.Json.JsonSerializer.Serialize(
            {| Enabled = lt.TestState.Activation = LiveTestingActivation.Active
               IsRunning = TestRunPhase.isAnyRunning lt.TestState.RunPhases
               Summary = {| Total = summary.Total; Passed = summary.Passed; Failed = summary.Failed
                            Running = summary.Running; Stale = summary.Stale |} |}, ctx.SseJsonOpts)
        with
        | :? System.Text.Json.JsonException as ex ->
          Log.error "[MCP] Test trace serialization error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          ""
        | ex ->
          Log.error "[MCP] Test trace unexpected error: %s (%s)\n%s" ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          ""
      let state', effects = processTestTraceChange traceJson modelChangeState.Value
      modelChangeState.Value <- state'
      for effect in effects do
        match effect with
        | BroadcastTestSse json ->
          ctx.TestEventBroadcast.Trigger(
            SageFs.SseWriter.formatTestTraceEvent sid json)
        | AccumulatePush _ -> ())

  let handleTestSummaryChange () =
    SseContext.withModel ctx (fun model ->
      let lt = model.LiveTesting.TestState
      let activeId =
        SseContext.activeSessionId ctx |> Option.defaultValue ""
      let sessionEntries =
        LiveTestState.statusEntriesForSession activeId lt
      // Zero-test suppression defect: a completed zero-test discovery must be
      // observable. Emit whenever live testing is active (which covers
      // ReadyZeroTests) or a run is in flight or entries exist.
      let discoveryState = LiveTestState.discoveryState lt
      match lt.Activation = SageFs.Features.LiveTesting.LiveTestingActivation.Active
            || sessionEntries.Length > 0
            || TestRunPhase.isAnyRunning lt.RunPhases with
      | true ->
        let s = SageFs.Features.LiveTesting.TestSummary.fromStatuses
                  lt.Activation (sessionEntries |> Array.map (fun e -> e.Status))
        ctx.ServerTracker.AccumulateEvent(
          PushEvent.TestSummaryChanged (s, lt.LastDecision))
        let now = System.Diagnostics.Stopwatch.GetTimestamp()
        let isRunComplete = not (TestRunPhase.isAnyRunning lt.RunPhases)
        match shouldPushTestSummary now modelChangeState.Value.LastTestSsePushTicks modelChangeState.Value.TestSseThrottleMs isRunComplete with
        | true ->
          modelChangeState.Value <- { modelChangeState.Value with LastTestSsePushTicks = now }
          ctx.TestEventBroadcast.Trigger(
            SageFs.SseWriter.formatTestSummaryEventWithDiscovery ctx.SseJsonOpts (Some activeId) s lt.LastDecision discoveryState lt.DiscoveryGeneration)
          let freshness =
            match lt.RunPhases |> Map.exists (fun _ p -> match p with SageFs.Features.LiveTesting.TestRunPhase.RunningButEdited _ -> true | _ -> false) with
            | true -> SageFs.Features.LiveTesting.ResultFreshness.StaleCodeEdited
            | false -> SageFs.Features.LiveTesting.ResultFreshness.Fresh
          let payload =
            let completion =
              let sessionDiscoveredCount =
                lt.TestSessionMap |> Map.filter (fun _ sid -> sid = activeId) |> Map.count
              SageFs.Features.LiveTesting.TestResultsBatchPayload.deriveCompletion
                freshness sessionDiscoveredCount sessionEntries.Length
            SageFs.Features.LiveTesting.TestResultsBatchPayload.create
              lt.LastGeneration freshness completion lt.Activation sessionEntries lt.LastDecision
          ctx.ServerTracker.AccumulateEvent(
            PushEvent.TestResultsBatch payload)
          ctx.TestEventBroadcast.Trigger(
            SageFs.SseWriter.formatTestResultsBatchEvent ctx.SseJsonOpts (Some activeId) payload)
          let files =
            sessionEntries
            |> Array.choose (fun e ->
              match e.Origin with
              | TestOrigin.SourceMapped (f, _) -> Some f
              | _ -> None)
            |> Array.distinct
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
            match fa.TestAnnotations.Length > 0 || fa.CodeLenses.Length > 0 || fa.CoverageAnnotations.Length > 0 || fa.InlineFailures.Length > 0 with
            | true ->
              ctx.TestEventBroadcast.Trigger(
                SageFs.SseWriter.formatFileAnnotationsEvent ctx.SseJsonOpts (Some activeId) fa)
              // Emit one coverage_view event per CoverageView so editors
              // can render a single badge per function instead of one per test.
              let views =
                SageFs.Features.LiveTesting.FileAnnotationsInternals.projectViewsForFile
                  SageFs.Features.LiveTesting.CoverageViewMode.defaults
                  file
                  model.LiveTesting.DepGraph
                  model.LiveTesting.TestState
              let gen =
                RunGeneration.value model.LiveTesting.TestState.LastGeneration
              for view in views do
                ctx.TestEventBroadcast.Trigger(
                  SageFs.SseWriter.formatCoverageViewEvent ctx.SseJsonOpts (Some activeId) gen view)
            | false -> ()
        | false -> ()
      | false -> ())

  let handleFeaturePush outputCount =
    match outputCount <> lastFeatureOutputCount.Value with
    | true ->
      lastFeatureOutputCount.Value <- outputCount
      SseContext.withModel ctx (fun model ->
        let sid = SseContext.activeSessionId ctx
        let outputText =
          model.RecentOutput.GetBuffer(sid |> Option.defaultValue "").FilterToList(fun o ->
            o.Kind = SageFs.OutputKind.Result)
          |> List.rev
          |> List.map (fun o -> o.Text)
          |> String.concat "\n"
        let state = featurePushState.Value
        let state, diffSse =
          SageFs.Features.FeatureHooks.computeEvalDiffPush ctx.SseJsonOpts sid outputText state
        let state, depsSse =
          SageFs.Features.FeatureHooks.computeCellDepsPush ctx.SseJsonOpts sid state
        let state, scopeSse =
          SageFs.Features.FeatureHooks.computeBindingScopePush ctx.SseJsonOpts sid state
        // W12(R10): Volatile.Write ensures the MCP-thread write is visible to dashboard HTTP threads.
        System.Threading.Volatile.Write(&sharedBindingScope.contents, Some (SageFs.Features.FeatureHooks.buildScopeFromState state))
        let state, timelineSse =
          SageFs.Features.FeatureHooks.computeEvalTimelinePush ctx.SseJsonOpts sid state
        featurePushState.Value <- state
        [diffSse; depsSse; scopeSse; timelineSse]
        |> List.choose id
        |> List.iter ctx.TestEventBroadcast.Trigger)
    | false -> ()

  /// After test results push, emit failure_narratives SSE when run is complete
  /// and there are Passed→Failed transitions to report.
  let handleFailureNarrativePush () =
    SseContext.withModel ctx (fun model ->
      let lt = model.LiveTesting.TestState
      let isRunComplete = not (TestRunPhase.isAnyRunning lt.RunPhases)
      match isRunComplete && not lt.Cached.FailureNarratives.IsEmpty with
      | true ->
        let activeId = SseContext.activeSessionId ctx |> Option.defaultValue ""
        ctx.ServerTracker.AccumulateEvent(PushEvent.FailureNarrativesUpdated lt.Cached.FailureNarratives)
        match activeId.Length > 0 with
        | true ->
          ctx.TestEventBroadcast.Trigger(
            SageFs.SseWriter.formatFailureNarrativesEvent ctx.SseJsonOpts (Some activeId) lt.Cached.FailureNarratives)
        | false -> ()
      | false -> ())

  /// After test results + features push, check if any test transitions
  /// warrant an auto-diagnosis. Only fires when run is complete and there
  /// are failure narratives (Passed→Failed transitions).
  let handleDiagnosisPush () =
    SseContext.withModel ctx (fun model ->
      let lt = model.LiveTesting.TestState
      let isRunComplete = not (TestRunPhase.isAnyRunning lt.RunPhases)
      match isRunComplete && not lt.Cached.FailureNarratives.IsEmpty with
      | true ->
        let state = featurePushState.Value
        let graph =
          let cells =
            state.EvalHistory
            |> List.map (fun e ->
              SageFs.Features.CellDependencyGraph.analyzeCell state.KnownBindings e.CellIndex e.Code e.Result)
          SageFs.Features.CellDependencyGraph.buildGraph cells
        let failuresWithNarratives =
          lt.DiscoveredTests
          |> Array.choose (fun tc ->
            Map.tryFind tc.Id lt.Cached.FailureNarratives
            |> Option.map (fun n -> (tc.Id, tc.DisplayName, n)))
          |> Array.toList
        let scopeBindings =
          match state.CachedScope with
          | Some snapshot ->
            snapshot.ActiveBindings
            |> Map.toList
            |> List.map (fun (_key, info) ->
              { SageFs.Features.ScopeBinding.Name = info.Name
                SageFs.Features.ScopeBinding.TypeSig = info.TypeSig
                SageFs.Features.ScopeBinding.Value = info.Value })
          | None -> []
        let report =
          SageFs.Features.Diagnostician.Diagnostician.compose
            graph failuresWithNarratives scopeBindings state.CachedTimeline
        ctx.ServerTracker.AccumulateEvent(PushEvent.DiagnosisReady report)
        let activeId = SseContext.activeSessionId ctx |> Option.defaultValue ""
        match activeId.Length > 0 with
        | true ->
          ctx.TestEventBroadcast.Trigger(
            SageFs.SseWriter.formatDiagnosisReadyEvent ctx.SseJsonOpts (Some activeId) report)
        | false -> ()
      | false -> ())

  stateChanged.Subscribe(fun change ->
    match change with
    | DaemonStateChange.ModelChanged (outputCount, diagCount) ->
      try
        ctx.ServerTracker.AccumulateEvent(
          PushEvent.StateChanged(outputCount, diagCount))
        handleDiagnosticsChange diagCount
        handleBindingsChange outputCount
        handleTestTraceChange ()
        handleTestSummaryChange ()
        handleFailureNarrativePush ()
        handleFeaturePush outputCount
        handleDiagnosisPush ()
        // Push resolved test source locations for editor jump-to-source
        SseContext.withModel ctx (fun model ->
          match model.ResolvedSourceLocations with
          | [] -> ()
          | locs ->
            ctx.ServerTracker.AccumulateEvent(PushEvent.TestSourceLocations locs)
            ctx.TestEventBroadcast.Trigger(
              SageFs.SseWriter.formatTestSourceLocationsEvent ctx.SseJsonOpts (SseContext.activeSessionId ctx) locs))
        match ctx.ServerTracker.Count > 0 with
        | true ->
          try
            let data =
              {| event = "state_changed"
                 diagCount = diagCount
                 outputCount = outputCount |}
            ctx.ServerTracker.NotifyLogAsync(
              LoggingLevel.Info, "sagefs.state", data) |> ignore
          with
          | :? System.Text.Json.JsonException as jex ->
            Log.warn "[MCP] State notification JSON error (non-fatal): %s\n%s" jex.Message (jex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        | false -> ()
      with
      | :? System.IO.IOException | :? ObjectDisposedException -> ()
      | :? System.Text.Json.JsonException as jex ->
        Log.warn "[MCP] State change JSON error (non-fatal): %s\n%s" jex.Message (jex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      | ex -> Log.error "[MCP] State change handler error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
    | DaemonStateChange.SystemAlarm (phase, msg) ->
      ctx.ServerTracker.AccumulateEvent(PushEvent.SystemAlarm (phase, msg))
    | _ -> ())


// Start MCP server in background
type RouteContext = {
  Config: McpServerConfig
  McpContext: McpContext
  SseContext: SseContext
  Dispatch: (SageFs.SageFsMsg -> unit) option
  GetElmRegions: (unit -> SageFs.RenderRegion list) option
  FsiBindings: Map<string, SageFs.SseWriter.FsiBinding> ref
  FeaturePushState: SageFs.Features.FeatureHooks.FeaturePushState ref
  LastFeatureOutputCount: int ref
  /// Last (filePath, blockStartLine) from an /exec call; used to stamp bindings_snapshot events.
  LastEvalContext: (string * int) option ref
}

let configureOtel (builder: WebApplicationBuilder) (port: int) (version: string) (otelConfigured: bool) =
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
      match otelConfigured with
      | true -> tracing.AddOtlpExporter() |> ignore
      | false -> ()
    )
    .WithMetrics(fun metrics ->
      let m = metrics
      for meter in SageFs.Instrumentation.allMeters do
        m.AddMeter(meter) |> ignore
      m.AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation() |> ignore
      metrics.SetExemplarFilter(OpenTelemetry.Metrics.ExemplarFilterType.TraceBased) |> ignore
      match otelConfigured with
      | true -> metrics.AddOtlpExporter() |> ignore
      | false -> ()
    )
  |> ignore

let configureLogging (builder: WebApplicationBuilder) (logPath: string) (otelConfigured: bool) =
  builder.WebHost.ConfigureLogging(fun logging ->
    logging.AddConsole() |> ignore
    logging.AddFile(logPath, minimumLevel = LogLevel.Information) |> ignore
    logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning) |> ignore
    logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning) |> ignore
    logging.AddFilter("Microsoft.Hosting", LogLevel.Warning) |> ignore
    logging.AddFilter("ModelContextProtocol.Server.McpServer", fun level -> level > LogLevel.Information) |> ignore
    logging.AddFilter("ModelContextProtocol.AspNetCore.SseHandler", LogLevel.Warning) |> ignore
    logging.AddFilter("SageFs", LogLevel.Information) |> ignore
    match otelConfigured with
    | true ->
      logging.AddOpenTelemetry(fun otel ->
        otel.IncludeFormattedMessage <- true
        otel.IncludeScopes <- true
        otel.AddOtlpExporter() |> ignore
      ) |> ignore
    | false -> ()
  ) |> ignore

let configureCompression (builder: WebApplicationBuilder) =
  builder.Services.AddResponseCompression(fun opts ->
    opts.EnableForHttps <- true
    opts.Providers.Add<BrotliCompressionProvider>()
    opts.Providers.Add<GzipCompressionProvider>()
  ) |> ignore
  builder.Services.Configure<BrotliCompressionProviderOptions>(fun (opts: BrotliCompressionProviderOptions) ->
    opts.Level <- System.IO.Compression.CompressionLevel.Fastest
  ) |> ignore

let configureMcpProtocol (builder: WebApplicationBuilder) (mcpContext: McpContext) (serverTracker: McpServerTracker) =
  builder.Services.AddSingleton<McpContext>(mcpContext) |> ignore
  builder.Services.AddSingleton<SageFs.Server.McpTools.SageFsTools>(fun serviceProvider ->
    let logger = serviceProvider.GetRequiredService<ILogger<SageFs.Server.McpTools.SageFsTools>>()
    new SageFs.Server.McpTools.SageFsTools(mcpContext, logger)
  ) |> ignore
  builder.Services.AddSingleton<McpServerTracker>(serverTracker) |> ignore
  builder.Services
    .AddMcpServer(fun options ->
      options.ServerInstructions <- String.concat " " [
        "SageFs is an affordance-driven F# Interactive (FSI) REPL with MCP integration."
        "ALWAYS use SageFs MCP tools for ALL F# work \u2014 never shell out to dotnet build, dotnet run, or PowerShell commands."
        "PowerShell is ONLY for process management: starting/stopping SageFs, dotnet pack, dotnet tool install/uninstall."
        "SageFs runs as a VISIBLE terminal window \u2014 the user watches it."
        "When starting or restarting SageFs, ALWAYS use Start-Process to launch in a visible console window, NEVER detach or run in background."
        "You OWN the full development cycle: pack, stop, reinstall, restart, test. Never ask the user to do these steps."
        "The MCP connection is SSE (push-based) \u2014 do not poll or sleep. Tools become available when SageFs is ready."
        "SageFs pushes structured notifications (notifications/message) for important events: session faults, warmup completion, eval failures."
        "Tool responses return only Result: or Error: with diagnostics \u2014 no code echo (you already know what you sent)."
        "SageFs is affordance-driven: get_fsi_status shows available tools for the current session state. Only invoke listed tools."
        "If a tool returns an error about session state, check get_fsi_status for available alternatives."
        "Use send_fsharp_code for incremental, small code blocks. End statements with ';;' for evaluation."
        "hard_reset_fsi_session with rebuild=true is ONLY needed when .fsproj changes (new files, packages) or warm-up fails."
        "Use cancel_eval to stop a running evaluation. Use reset_fsi_session only if warm-up failed."
      ]
    )
    .WithHttpTransport(fun opts ->
      opts.IdleTimeout <- SageFs.Timeouts.sseKeepAlive
      opts.MaxIdleSessionCount <- 1000
    )
    .WithTools<SageFs.Server.McpTools.SageFsTools>()
    .WithRequestFilters(fun filters ->
      filters.AddCallToolFilter(createServerCaptureFilter mcpContext serverTracker) |> ignore
    )
  |> ignore

let wireCoreLogs (app: WebApplication) =
  let coreLogger = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("SageFs.Core")
  SageFs.Utils.Log.logInfo <- fun msg -> coreLogger.LogInformation(msg)
  SageFs.Utils.Log.logDebug <- fun msg -> coreLogger.LogDebug(msg)
  SageFs.Utils.Log.logWarn <- fun msg -> coreLogger.LogWarning(msg)
  SageFs.Utils.Log.logError <- fun msg -> coreLogger.LogError(msg)

let logStartup (app: WebApplication) (port: int) (logPath: string) (otelConfigured: bool) =
  let logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SageFs.McpServer")
  logger.LogInformation("MCP server starting on port {Port}", port)
  logger.LogInformation("SSE endpoint: http://localhost:{Port}/sse", port)
  logger.LogInformation("State events SSE: http://localhost:{Port}/events", port)
  logger.LogInformation("Kestrel max connections: {MaxConnections}", 200)
  logger.LogInformation("Log file: {LogPath}", logPath)
  match otelConfigured with
  | true ->
    let endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    let protocol =
      Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")
      |> Option.ofObj |> Option.defaultValue "grpc"
    logger.LogInformation("OpenTelemetry enabled: endpoint={OtelEndpoint}, protocol={OtelProtocol}", endpoint, protocol)
  | false ->
    logger.LogInformation("OpenTelemetry not configured (set OTEL_EXPORTER_OTLP_ENDPOINT)")

let mapExecutionRoutes (app: WebApplication) (rctx: RouteContext) =
  app.MapPost("/exec", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      use! json = readJsonBody ctx
      let code = json.RootElement.GetProperty("code").GetString()
      let wd =
        match json.RootElement.TryGetProperty("working_directory") with
        | true, prop -> Some (prop.GetString())
        | false, _ -> None
      let filePath =
        match json.RootElement.TryGetProperty("file_path") with
        | true, prop -> Some (prop.GetString())
        | false, _ -> None
      let evalMode =
        match json.RootElement.TryGetProperty("eval_mode") with
        | true, prop -> Some (prop.GetString())
        | false, _ -> None
      let blockStartLine =
        match json.RootElement.TryGetProperty("block_start_line") with
        | true, prop -> Some (prop.GetInt32())
        | false, _ -> None
      // Notify editor plugins that eval is starting so they can mark decorations stale
      let sid = SseContext.activeSessionId rctx.SseContext
      let evalFp, evalBsl =
        match filePath, blockStartLine with
        | Some fp, Some bsl when not (System.String.IsNullOrEmpty(fp)) ->
          let startedStr = SageFs.SseWriter.formatEvalStartedEvent rctx.SseContext.SseJsonOpts sid fp bsl
          rctx.SseContext.TestEventBroadcast.Trigger(startedStr)
          // Remember for bindings_snapshot stamping
          rctx.LastEvalContext.Value <- Some (fp, bsl)
          Some fp, Some bsl
        | _ -> None, None
      let sw = System.Diagnostics.Stopwatch.StartNew()
      // Heartbeat timer — independent thread so it survives if eval thread is slow.
      // Reads elapsed time via Stopwatch (thread-safe for reads) and fires every 500ms.
      use heartbeatCts = new System.Threading.CancellationTokenSource()
      let heartbeatTask : System.Threading.Tasks.Task =
        System.Threading.Tasks.Task.Run(System.Func<System.Threading.Tasks.Task>(fun () ->
          task {
            let token = heartbeatCts.Token
            try
              while not token.IsCancellationRequested do
                do! System.Threading.Tasks.Task.Delay(500, token)
                if not token.IsCancellationRequested then
                  let fp = evalFp |> Option.defaultValue ""
                  let bsl = evalBsl |> Option.defaultValue 0
                  let hbStr = SageFs.SseWriter.formatEvalHeartbeatEvent rctx.SseContext.SseJsonOpts sid fp bsl sw.ElapsedMilliseconds
                  rctx.SseContext.TestEventBroadcast.Trigger(hbStr)
            with :? System.OperationCanceledException -> ()
          } :> System.Threading.Tasks.Task))
      let! result, hadError = SageFs.McpTools.evalFSharpCodeWithOutcome rctx.McpContext "cli-integrated" code SageFs.McpTools.OutputFormat.Text None wd filePath evalMode blockStartLine None
      sw.Stop()
      heartbeatCts.Cancel()
      let! _ = heartbeatTask
      rctx.FeaturePushState.Value <- SageFs.Features.FeatureHooks.recordEval code result sw.ElapsedMilliseconds rctx.FeaturePushState.Value
      // Emit eval_result SSE for inline decorations in editor plugins
      match evalFp, evalBsl with
      | Some fp, Some bsl ->
        let sseStr = SageFs.SseWriter.formatEvalResultEvent rctx.SseContext.SseJsonOpts sid fp bsl result true (sw.ElapsedMilliseconds |> float)
        rctx.SseContext.TestEventBroadcast.Trigger(sseStr)
      | _ -> ()
      // Truthful contract: success reflects the typed worker outcome (an eval
      // that failed to compile/run has success=false), never string sniffing.
      // HTTP stays 200 — the request WAS processed and the result text is for
      // the client to display; editors treat non-2xx as transport failure.
      // The failure body also carries `error` so standard { success, error }
      // client parsers surface the diagnostic instead of "Unknown error".
      let body =
        match hadError with
        | false -> {| success = true; result = result |} :> obj
        | true -> {| success = false; result = result; error = result |} :> obj
      do! jsonResponse ctx 200 body
    } :> Task
  ) |> ignore
  app.MapPost("/reset", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! result = SageFs.McpTools.resetSession rctx.McpContext "http" None None
      // resetSession prefixes failures with "Error: " — check the prefix, not
      // a substring, so a success message containing the word Error is not
      // misreported as a failure.
      let failed = result.StartsWith("Error", StringComparison.Ordinal)
      do! jsonResponse ctx (if failed then 500 else 200) {| success = not failed; message = result |}
    } :> Task
  ) |> ignore
  app.MapPost("/hard-reset", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      use! json = readJsonBody ctx
      let rebuild =
        try
          match json.RootElement.TryGetProperty("rebuild") with
          | true, prop -> prop.GetBoolean()
          | false, _ -> false
        with :? System.Text.Json.JsonException -> false
      let! result = SageFs.McpTools.hardResetSession rctx.McpContext "http" rebuild None None
      let failed = result.StartsWith("Error", StringComparison.Ordinal)
      do! jsonResponse ctx (if failed then 500 else 200) {| success = not failed; message = result |}
    } :> Task
  ) |> ignore
  app.MapPost("/cancel", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! result = SageFs.McpTools.cancelEval rctx.McpContext "http" None
      match result.StartsWith("Error") with
      | true -> do! jsonResponse ctx 500 {| received = false; error = result |}
      | false -> do! jsonResponse ctx 200 {| received = true; message = result |}
    } :> Task
  ) |> ignore
  app.MapPost("/api/cancel-eval", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! result = SageFs.McpTools.cancelEval rctx.McpContext "http" None
      match result.StartsWith("Error") with
      | true -> do! jsonResponse ctx 500 {| received = false; error = result |}
      | false -> do! jsonResponse ctx 200 {| received = true; message = result |}
    } :> Task
  ) |> ignore
  app.MapPost("/load-script", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      use! json = readJsonBody ctx
      let filePath = json.RootElement.GetProperty("path").GetString()
      let sessionIdOpt =
        match json.RootElement.TryGetProperty("sessionId") with
        | true, prop -> Option.ofObj (prop.GetString())
        | _ -> None
      // W6: Use the requested session's workdir, not just List.tryHead (wrong in multi-session).
      // W1: resolve both file and directory symlinks via ResolveLinkTarget(returnFinalTarget=true).
      let! workingDir = task {
        match sessionIdOpt with
        | Some sid when sid.Length > 0 ->
          let! infoOpt = rctx.Config.SessionOps.GetSessionInfo(toSessionId sid)
          return infoOpt |> Option.map (fun s -> s.WorkingDirectory) |> Option.defaultValue ""
        | _ ->
          let! sessions = rctx.Config.SessionOps.GetAllSessions()
          return sessions |> List.tryHead |> Option.map (fun s -> s.WorkingDirectory) |> Option.defaultValue ""
      }
      let resolveRealPath (p: string) : string =
        let full = System.IO.Path.GetFullPath p
        let fsi : System.IO.FileSystemInfo =
          match System.IO.Directory.Exists(full) with
          | true -> System.IO.DirectoryInfo(full) :> System.IO.FileSystemInfo
          | false -> System.IO.FileInfo(full) :> System.IO.FileSystemInfo
        match fsi.ResolveLinkTarget(returnFinalTarget = true) with
        | null -> full
        | resolved -> resolved.FullName
      // W1(R8): Hoist canonical before isContained so the SAME value is used for both
      //         the containment check and the loadFSharpScript call (eliminates TOCTOU).
      let canonical = resolveRealPath filePath
      let canonicalDir = resolveRealPath workingDir
      let isContained =
        not (System.String.IsNullOrWhiteSpace filePath || System.String.IsNullOrWhiteSpace workingDir)
        && (canonical.StartsWith(
              canonicalDir + string System.IO.Path.DirectorySeparatorChar,
              System.StringComparison.OrdinalIgnoreCase)
            || canonical.Equals(canonicalDir, System.StringComparison.OrdinalIgnoreCase))
      match isContained with
      | false ->
        do! jsonResponse ctx 403 {| success = false; error = "Path is outside the session working directory" |}
      | true ->
      let! result = SageFs.McpTools.loadFSharpScript rctx.McpContext "http" canonical None None
      match result.StartsWith("Error") with
      | true -> do! jsonResponse ctx 500 {| received = false; error = result |}
      | false -> do! jsonResponse ctx 200 {| received = true; message = result |}
    } :> Task
  ) |> ignore

let fallbackSessionStatusLabel (status: SageFs.WorkerProtocol.SessionStatus) =
  match status with
  | SageFs.WorkerProtocol.SessionStatus.Starting -> "Starting"
  | SageFs.WorkerProtocol.SessionStatus.Restarting -> "Restarting"
  | SageFs.WorkerProtocol.SessionStatus.Faulted -> "Faulted"
  | SageFs.WorkerProtocol.SessionStatus.Stopped -> "Stopped"
  | _ -> "Disconnected"

let resolveSessionStatusLabel
  (sessionOps: SageFs.SessionManagementOps)
  (routeName: string)
  (session: SageFs.WorkerProtocol.SessionInfo) =
  task {
    let! proxy = sessionOps.GetProxy session.Id
    match proxy with
    | Some send ->
      try
        let! resp = send (SageFs.WorkerProtocol.WorkerMessage.GetStatus routeName) |> Async.StartAsTask
        match resp with
        | SageFs.WorkerProtocol.WorkerResponse.StatusResult(_, snap) ->
          return SageFs.WorkerProtocol.SessionStatus.label snap.Status
        | SageFs.WorkerProtocol.WorkerResponse.WorkerError _ ->
          return fallbackSessionStatusLabel session.Status
        | _ ->
          return fallbackSessionStatusLabel session.Status
      with
      | :? System.Net.Http.HttpRequestException ->
        return fallbackSessionStatusLabel session.Status
      | :? System.Threading.Tasks.TaskCanceledException ->
        return fallbackSessionStatusLabel session.Status
      | _ ->
        return fallbackSessionStatusLabel session.Status
    | None ->
      return fallbackSessionStatusLabel session.Status
  }

let mapHealthRoutes (app: WebApplication) (rctx: RouteContext) =
  app.MapGet("/health", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! allSessions = rctx.Config.SessionOps.GetAllSessions()
      let toSessionHealthStatus = function
        | "Ready" -> SageFs.Features.SessionHealthStatus.Ready
        | "Evaluating" -> SageFs.Features.SessionHealthStatus.Evaluating
        | status when status.StartsWith("Building") -> SageFs.Features.SessionHealthStatus.Evaluating
        | "Starting"
        | "Restarting"
        | "Disconnected" -> SageFs.Features.SessionHealthStatus.WarmingUp
        | "Faulted"
        | "Error" -> SageFs.Features.SessionHealthStatus.Faulted
        | "Stopped" -> SageFs.Features.SessionHealthStatus.Stopped
        | _ -> SageFs.Features.SessionHealthStatus.WarmingUp
      let asm = System.Reflection.Assembly.GetExecutingAssembly()
      let version =
        asm.GetName().Version
        |> Option.ofObj
        |> Option.map (fun v -> v.ToString())
        |> Option.defaultValue "unknown"
      let daemonProcess = System.Diagnostics.Process.GetCurrentProcess()
      let! sessionPairs =
        allSessions
        |> Seq.map (fun sess ->
          task {
            let! proxy = rctx.Config.SessionOps.GetProxy sess.Id
            let! statusLabel = task {
              match proxy with
              | Some _ -> return! resolveSessionStatusLabel rctx.Config.SessionOps "health" sess
              | None -> return fallbackSessionStatusLabel sess.Status
            }
            let projectName =
              sess.Projects
              |> List.tryHead
              |> Option.map System.IO.Path.GetFileName
              |> Option.defaultValue (System.IO.Path.GetFileName sess.WorkingDirectory)
            let lastActivity = System.DateTimeOffset(sess.LastActivity.ToUniversalTime())
            let summary : SageFs.Features.SessionHealthSummary =
              { SessionId = SageFs.WorkerProtocol.SessionId.value sess.Id
                ProjectName = projectName
                Status = toSessionHealthStatus statusLabel
                EvalCount = 0
                LastActivity = lastActivity }
            let payload =
              {| id = SageFs.WorkerProtocol.SessionId.value sess.Id
                 projectName = projectName
                 status = statusLabel
                 faultReason = sess.FaultReason
                 workingDirectory = sess.WorkingDirectory
                 workerPid = sess.WorkerPid
                 lastActivity = lastActivity
                 workflowLabel = SageFs.WorkflowTypes.SessionWorkflow.label sess.Workflow |}
            return summary, payload
          })
        |> System.Threading.Tasks.Task.WhenAll
      let sessionSummaries = sessionPairs |> Array.map fst |> Array.toList
      let sessionStates = sessionPairs |> Array.map snd
      let healthSnapshot : SageFs.Features.HealthSnapshot =
        { DaemonPid = Environment.ProcessId
          DaemonPort = 0
          Uptime = DateTime.UtcNow - daemonProcess.StartTime.ToUniversalTime()
          Version = version
          SessionSummaries = sessionSummaries
          LiveTestingSummary = None
          MemoryMB = int (daemonProcess.WorkingSet64 / 1024L / 1024L) }
      let sessionStatus =
        SageFs.Features.DaemonHealth.primarySessionStatusLabel healthSnapshot.SessionSummaries
      let healthy =
        match SageFs.Features.DaemonHealth.primarySessionStatus healthSnapshot.SessionSummaries with
        | Some SageFs.Features.SessionHealthStatus.Ready
        | Some SageFs.Features.SessionHealthStatus.Evaluating -> true
        | _ -> false
      let diagnosticSummary = SageFs.Features.DaemonHealth.diagnosticSummary healthSnapshot
      // Structured error for the Faulted/Stopped case: the VS Code client
      // branches on `error` and shows { message, suggestedAction } with an
      // action button — a null error leaves it rendering a bare icon with
      // no message. Populate it from the faulted session's reason when one
      // exists (sessionStates carry the faulted session's faultReason).
      let sessionError =
        sessionStates
        |> Array.tryPick (fun s -> SageFs.Features.DaemonHealth.structuredErrorForFault s.status s.faultReason)
        |> Option.defaultValue (null :> obj)
      do! jsonResponse ctx 200
            {| healthy = healthy
               status = sessionStatus
               error = sessionError
               version = version
               apiVersion = SageFs.EndpointContracts.apiVersion
               features = [ "live-testing"; "coverage-intel"; "impact-forecast"; "action-prioritizer"; "mark-all-stale"; "time-travel" ]
               sessionCount = sessionStates.Length
               sessionStates = sessionStates
               diagnosticSummary = diagnosticSummary |}
    } :> Task
  ) |> ignore
  app.MapGet("/diag/threadpool", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let workerThreads = ref 0
      let completionPortThreads = ref 0
      let maxWorkerThreads = ref 0
      let maxCompletionPortThreads = ref 0
      let minWorkerThreads = ref 0
      let minCompletionPortThreads = ref 0
      System.Threading.ThreadPool.GetAvailableThreads(workerThreads, completionPortThreads)
      System.Threading.ThreadPool.GetMaxThreads(maxWorkerThreads, maxCompletionPortThreads)
      System.Threading.ThreadPool.GetMinThreads(minWorkerThreads, minCompletionPortThreads)
      let pending = System.Threading.ThreadPool.PendingWorkItemCount
      let threadCount = System.Threading.ThreadPool.ThreadCount
      do! jsonResponse ctx 200
            {| available = workerThreads.Value
               max = maxWorkerThreads.Value
               min = minWorkerThreads.Value
               pending = pending
               threadCount = threadCount
               completionPort =
                 {| available = completionPortThreads.Value
                    max = maxCompletionPortThreads.Value
                    min = minCompletionPortThreads.Value |} |}
    } :> Task
  ) |> ignore
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
               apiVersion = SageFs.EndpointContracts.apiVersion
               server = "sagefs"
               mcp = true
               sse = true |}
    } :> Task
  ) |> ignore

let mapDiagnosticsRoutes (app: WebApplication) (rctx: RouteContext) =
  app.MapPost("/diagnostics", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! code = readJsonProp ctx "code"
      let! _ = SageFs.McpTools.checkFSharpCode rctx.McpContext "http" code None None
      do! jsonResponse ctx 202 {| accepted = true |}
    } :> Task
  ) |> ignore
  app.MapGet("/diagnostics", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      SageFs.Instrumentation.sseConnectionsActive.Add(1L)
      setSseHeaders ctx
      let initialEvent = sprintf "event: diagnostics\ndata: []\n\n"
      do! writeSseFrame ctx.Response.Body initialEvent
      let diagSource =
        rctx.Config.DiagnosticsChanged |> Observable.map (fun store ->
          let json = SageFs.McpAdapter.formatDiagnosticsStoreAsJson store
          sprintf "event: diagnostics\ndata: %s\n\n" json)
      do! runSseWriteLoop ctx.Response.Body ctx.RequestAborted [diagSource] 30000
      SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
    } :> Task
  ) |> ignore

let mapEventsRoute (app: WebApplication) (rctx: RouteContext) =
  app.MapGet("/events", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      SageFs.Instrumentation.sseConnectionsActive.Add(1L)
      let connSw = System.Diagnostics.Stopwatch.StartNew()
      let connActivity =
        SageFs.Instrumentation.startSpanWithKind
          SageFs.Instrumentation.daemonSource "sse.connection"
          System.Diagnostics.ActivityKind.Server
          [("sse.endpoint", box "/events")]
      setSseHeaders ctx
      match rctx.Config.StateChanged with
      | Some evt ->
        do! replaySessionSnapshot rctx.SseContext ctx.Response.Body
        do! replayCachedTestState rctx.SseContext ctx.Response.Body
        match rctx.FsiBindings.Value.Count, SseContext.activeSessionId rctx.SseContext with
        | count, Some sid when count > 0 ->
          let frame =
            rctx.FsiBindings.Value |> Map.values |> Array.ofSeq
            |> SageFs.SseWriter.formatBindingsSnapshotEvent rctx.SseContext.SseJsonOpts (Some sid) [] 0 None
          do! writeSseFrame ctx.Response.Body frame
        | _ -> ()
        for sse in
          [rctx.FeaturePushState.Value.LastEvalDiffSse
           rctx.FeaturePushState.Value.LastCellDepsSse
           rctx.FeaturePushState.Value.LastBindingScopeSse
           rctx.FeaturePushState.Value.LastEvalTimelineSse]
          |> List.choose id do
          do! writeSseFrame ctx.Response.Body sse
        let stateSource =
          evt |> Observable.map (fun change ->
            change
            |> DaemonStateChange.toJson
            |> SageFs.SseWriter.formatSseEvent "state")
        do! runSseWriteLoop
              ctx.Response.Body
              ctx.RequestAborted
              [ stateSource; rctx.SseContext.TestEventBroadcast.Publish; rctx.SseContext.SessionEventBroadcast.Publish ]
              15000
        connSw.Stop()
        SageFs.Instrumentation.sseConnectionDurationMs.Record(connSw.Elapsed.TotalMilliseconds)
        SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
        SageFs.Instrumentation.succeedSpan connActivity
      | None ->
        ctx.Response.StatusCode <- 501
        do! writeSseFrame ctx.Response.Body "event: error\ndata: {\"error\":\"No Elm loop available\"}\n\n"
        connSw.Stop()
        SageFs.Instrumentation.sseConnectionDurationMs.Record(connSw.Elapsed.TotalMilliseconds)
        SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
        SageFs.Instrumentation.failSpan connActivity "No Elm loop available"
    } :> Task
  ) |> ignore

let mapStatusRoutes (app: WebApplication) (rctx: RouteContext) =
  app.MapGet("/api/status", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! sid = task {
        match ctx.Request.Query.TryGetValue("sessionId") with
        | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace(v.[0])) -> return v.[0]
        | _ ->
          let! sessions = rctx.Config.SessionOps.GetAllSessions()
          return sessions |> List.tryHead |> Option.map (fun s -> SageFs.WorkerProtocol.SessionId.value s.Id) |> Option.defaultValue ""
      }
      let sidTyped =
        match SageFs.WorkerProtocol.SessionId.validate sid with
        | Ok s -> Some s
        | Error _ -> None
      let! info =
        match sidTyped with
        | Some s -> rctx.Config.SessionOps.GetSessionInfo s
        | None -> Task.FromResult None
      let! statusResult =
        task {
          match sidTyped with
          | Some s ->
            let! proxy = rctx.Config.SessionOps.GetProxy s
            match proxy with
            | Some send ->
              let! resp = send (SageFs.WorkerProtocol.WorkerMessage.GetStatus "api") |> Async.StartAsTask
              return Some resp
            | None -> return None
          | None -> return None
        }
      let elmRegions =
        match rctx.GetElmRegions with
        | Some getRegions -> getRegions ()
        | None -> []
      let version = DaemonInfo.version
      let regionData =
        elmRegions |> List.map (fun (r: SageFs.RenderRegion) ->
          {| id = r.Id
             content = r.Content |> fun s -> match s.Length > 2000 with | true -> s.[..1999] | false -> s
             affordances = r.Affordances |> List.map (fun a -> a.ToString()) |})
      let sessionState, evalCount, avgMs, minMs, maxMs =
        match statusResult with
        | Some (SageFs.WorkerProtocol.WorkerResponse.StatusResult(_, snap)) ->
          SageFs.WorkerProtocol.SessionStatus.label snap.Status,
          snap.EvalCount,
          (match snap.EvalCount > 0 with | true -> float snap.AvgDurationMs | false -> 0.0),
          float snap.MinDurationMs,
          float snap.MaxDurationMs
        | _ ->
          info |> Option.map (fun i -> SageFs.WorkerProtocol.SessionStatus.label i.Status) |> Option.defaultValue "Unknown",
          0, 0.0, 0.0, 0.0
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
           uptime =
             use proc = System.Diagnostics.Process.GetCurrentProcess()
             (DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds |}
      do! jsonResponse ctx 200 data
    } :> Task
  ) |> ignore
  app.MapGet("/api/system/status", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let supervised =
        Environment.GetEnvironmentVariable("SAGEFS_SUPERVISED")
        |> Option.ofObj |> Option.map (fun s -> s = "1") |> Option.defaultValue false
      let restartCount =
        Environment.GetEnvironmentVariable("SAGEFS_RESTART_COUNT")
        |> Option.ofObj |> Option.bind (fun s -> match Int32.TryParse s with true, n -> Some n | _ -> None)
        |> Option.defaultValue 0
      use proc = System.Diagnostics.Process.GetCurrentProcess()
      let uptime = (DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds
      let version = DaemonInfo.version
      let! allSessions = rctx.Config.SessionOps.GetAllSessions()
      let data =
        {| version = version
           apiVersion = SageFs.EndpointContracts.apiVersion
           pid = Environment.ProcessId
           uptimeSeconds = uptime
           supervised = supervised
           restartCount = restartCount
           sessionCount = allSessions.Length
           mcpPort = rctx.Config.Port
           dashboardPort = rctx.Config.Port + 1 |}
      do! jsonResponse ctx 200 data
    } :> Task
  ) |> ignore

let mapSessionRoutes (app: WebApplication) (rctx: RouteContext) =
  app.MapGet("/api/sessions", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! allSessions = rctx.Config.SessionOps.GetAllSessions()
      let results = System.Collections.Generic.List<obj>()
      for sess in allSessions do
        let! proxy = rctx.Config.SessionOps.GetProxy sess.Id
        let! evalCount, avgMs, status = task {
          match proxy with
          | Some send ->
            try
              let! resp = send (SageFs.WorkerProtocol.WorkerMessage.GetStatus "api") |> Async.StartAsTask
              match resp with
              | SageFs.WorkerProtocol.WorkerResponse.StatusResult(_, snap) ->
                return snap.EvalCount, float snap.AvgDurationMs, SageFs.WorkerProtocol.SessionStatus.label snap.Status
              | SageFs.WorkerProtocol.WorkerResponse.WorkerError _ ->
                return 0, 0.0, fallbackSessionStatusLabel sess.Status
              | _ -> return 0, 0.0, fallbackSessionStatusLabel sess.Status
            with
            | :? System.Net.Http.HttpRequestException as ex ->
              Log.error "[MCP] Session status HTTP error for %s: %s\n%s" (SageFs.WorkerProtocol.SessionId.value sess.Id) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
              return 0, 0.0, fallbackSessionStatusLabel sess.Status
            | :? System.Threading.Tasks.TaskCanceledException ->
              return 0, 0.0, fallbackSessionStatusLabel sess.Status
            | ex ->
              Log.error "[MCP] Session status unexpected error for %s: %s (%s)\n%s" (SageFs.WorkerProtocol.SessionId.value sess.Id) ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
              return 0, 0.0, fallbackSessionStatusLabel sess.Status
          | None -> return 0, 0.0, fallbackSessionStatusLabel sess.Status
        }
        results.Add(
          {| id = SageFs.WorkerProtocol.SessionId.value sess.Id
             status = status
             faultReason = sess.FaultReason
             projects = sess.Projects
             workingDirectory = sess.WorkingDirectory
             evalCount = evalCount
             avgDurationMs = avgMs
             workflowLabel = SageFs.WorkflowTypes.SessionWorkflow.label sess.Workflow |} :> obj)
      do! jsonResponse ctx 200 {| sessions = results |}
    } :> Task
  ) |> ignore
  app.MapPost("/api/sessions/switch", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! sidOpt = readValidatedSessionId ctx
      match sidOpt with
      | None -> () // 400 already sent
      | Some sid ->
        let sidStr = SageFs.WorkerProtocol.SessionId.value sid
        let! info = rctx.Config.SessionOps.GetSessionInfo sid
        match info with
        | Some _ ->
          SageFs.McpTools.setActiveSessionId rctx.McpContext "cli-integrated" sidStr
          SageFs.McpTools.setActiveSessionId rctx.McpContext "http" sidStr
          match rctx.Dispatch with
          | Some d ->
            d (SageFs.SageFsMsg.Event (SageFs.SageFsEvent.SessionSwitched (None, sidStr)))
            d (SageFs.SageFsMsg.Editor SageFs.EditorAction.ListSessions)
          | None -> ()
          do! jsonResponse ctx 200 {| success = true; sessionId = sidStr |}
        | None ->
          do! jsonResponse ctx 404 {| success = false; error = sprintf "Session '%s' not found" sidStr |}
    } :> Task
  ) |> ignore
  app.MapPost("/api/sessions/{sid}/buffer-changed", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let raw = ctx.Request.RouteValues.["sid"] |> string
      match SageFs.WorkerProtocol.SessionId.validate raw with
      | Error msg ->
        do! jsonResponse ctx 400 {| success = false; error = msg |}
      | Ok sid ->
        let sidStr = SageFs.WorkerProtocol.SessionId.value sid
        let! info = rctx.Config.SessionOps.GetSessionInfo sid
        match info with
        | None ->
          do! jsonResponse ctx 404 {| success = false; error = sprintf "Session '%s' not found" sidStr |}
        | Some _ ->
          use! json = readJsonBody ctx
          let root = json.RootElement
          let filePath = root.GetProperty("filePath").GetString()
          let content = root.GetProperty("content").GetString()
          match rctx.Dispatch with
          | None ->
            do! jsonResponse ctx 503 {| success = false; error = "Elm loop not started" |}
          | Some dispatch ->
            dispatch (SageFs.SageFsMsg.BufferContentChanged (Some sidStr, filePath, content))
            do! jsonResponse ctx 202 {| success = true; sessionId = sidStr; filePath = filePath |}
    } :> Task
  ) |> ignore
  app.MapPost("/api/sessions/create", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      use! doc = readJsonBody ctx
      let root = doc.RootElement
      let workingDir =
        let tryProp (name: string) =
          let mutable value = Unchecked.defaultof<System.Text.Json.JsonElement>
          match root.TryGetProperty(name, &value) with
          | true -> Some (value.GetString())
          | false -> None
        tryProp "workingDirectory"
        |> Option.orElseWith (fun () -> tryProp "working_directory")
        |> Option.defaultValue Environment.CurrentDirectory
      let projects =
        let mutable projProp = Unchecked.defaultof<System.Text.Json.JsonElement>
        match root.TryGetProperty("projects", &projProp) with
        | true ->
          match projProp.ValueKind with
          | System.Text.Json.JsonValueKind.Array ->
            projProp.EnumerateArray()
            |> Seq.map (fun e -> e.GetString())
            |> Seq.toList
          | System.Text.Json.JsonValueKind.String ->
            [ projProp.GetString() ]
          | _ -> []
        | false -> []
      let workflow =
        let mutable wfProp = Unchecked.defaultof<System.Text.Json.JsonElement>
        match root.TryGetProperty("workflow", &wfProp) with
        | true ->
          match wfProp.GetString() with
          | "WebLive" | "Live" -> SageFs.WorkflowTypes.SessionWorkflow.WebLive SageFs.WorkflowTypes.BrowserRefreshConfig.defaults
          | _ -> SageFs.WorkflowTypes.SessionWorkflow.Interactive
        | false -> SageFs.WorkflowTypes.SessionWorkflow.Interactive
      let! result = rctx.Config.SessionOps.CreateSession projects workingDir workflow
      match result with
      | Ok msg ->
        SageFs.McpTools.setActiveSessionId rctx.McpContext "cli-integrated" msg
        SageFs.McpTools.setActiveSessionId rctx.McpContext "http" msg
        match rctx.Dispatch with
        | Some d -> d (SageFs.SageFsMsg.Editor SageFs.EditorAction.ListSessions)
        | None -> ()
        do! jsonResponse ctx 200 {| success = true; message = msg |}
      | Error err ->
        do! jsonResponse ctx (SageFsError.toHttpStatus err) (structuredErrorBody err)
    } :> Task
  ) |> ignore
  app.MapPost("/api/sessions/stop", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! sidOpt = readValidatedSessionId ctx
      match sidOpt with
      | None -> () // 400 already sent
      | Some sid ->
        let! result = rctx.Config.SessionOps.StopSession (SageFs.WorkerProtocol.SessionId.value sid)
        match rctx.Dispatch with
        | Some d -> d (SageFs.SageFsMsg.Editor SageFs.EditorAction.ListSessions)
        | None -> ()
        match result with
        | Ok msg -> do! jsonResponse ctx 200 {| success = true; message = msg |}
        | Error err -> do! jsonResponse ctx (SageFsError.toHttpStatus err) (structuredErrorBody err)
    } :> Task
  ) |> ignore

let mapLiveTestingRoutes (app: WebApplication) (rctx: RouteContext) =
  // Truthful command failure: enable/disable/policy used to report HTTP 200
  // success even when the internal operation failed (e.g. Elm loop not
  // started, unknown category/policy). Distinguish error strings from
  // success messages so clients can surface real failures.
  let isFailureMessage (message: string) =
    message.StartsWith("Cannot ", System.StringComparison.Ordinal)
    || message.StartsWith("Unknown ", System.StringComparison.Ordinal)
  let respond (ctx: Microsoft.AspNetCore.Http.HttpContext) (result: string) (activation: string option) =
    task {
      match isFailureMessage result with
      | true -> do! jsonResponse ctx 503 {| success = false; error = result |}
      | false ->
        match activation with
        | Some a -> do! jsonResponse ctx 200 {| success = true; message = result; activation = a |}
        | None -> do! jsonResponse ctx 200 {| success = true; message = result |}
    }
  app.MapPost("/api/live-testing/enable", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! result = SageFs.McpTools.setLiveTesting rctx.McpContext true
      do! respond ctx result (Some "active")
    } :> Task
  ) |> ignore
  app.MapPost("/api/live-testing/disable", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! result = SageFs.McpTools.setLiveTesting rctx.McpContext false
      do! respond ctx result (Some "inactive")
    } :> Task
  ) |> ignore
  app.MapPost("/api/live-testing/policy", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      use! json = readJsonBody ctx
      let category = json.RootElement.GetProperty("category").GetString()
      let policy = json.RootElement.GetProperty("policy").GetString()
      let! result = SageFs.McpTools.setRunPolicy rctx.McpContext category policy
      do! respond ctx result None
    } :> Task
  ) |> ignore
  app.MapPost("/api/live-testing/run", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      use! json = readJsonBody ctx
      let root = json.RootElement
      let patternFilter = tryGetJsonStringAliases root [ "pattern" ]
      let fileFilter = tryGetJsonStringAliases root [ "file"; "filePath"; "file_path" ]
      let categoryFilter =
        tryGetJsonStringAliases root [ "category" ]
        |> Option.bind (fun category ->
          match category.Trim().ToLowerInvariant() with
          | "" -> None
          | "unit" -> Some TestCategory.Unit
          | "integration" -> Some TestCategory.Integration
          | "browser" -> Some TestCategory.Browser
          | "benchmark" -> Some TestCategory.Benchmark
          | "architecture" -> Some TestCategory.Architecture
          | "property" -> Some TestCategory.Property
          | other -> Some (TestCategory.Custom other))

      match rctx.Dispatch, rctx.SseContext.GetElmModel with
      | None, _ ->
          do! jsonResponse ctx 503 {| success = false; error = "Cannot run tests — Elm loop not started." |}
      | _, None ->
          do! jsonResponse ctx 503 {| success = false; error = "Cannot run tests — Elm model unavailable." |}
      | Some dispatch, Some getModel ->
          let model = getModel()
          let discoveredTests = model.LiveTesting.TestState.DiscoveredTests

          match Array.isEmpty discoveredTests with
          | true ->
              do! jsonResponse ctx 409 {|
                success = false
                error = "No tests discovered yet. Enable live testing and wait for DiscoveryState=ready_with_tests."
              |}
          | false ->
              let tests =
                LiveTestCycleState.filterTestsForExplicitRun
                  discoveredTests
                  fileFilter
                  patternFilter
                  categoryFilter

              match Array.isEmpty tests with
              | true ->
                  let filterSummary =
                    [ match patternFilter with
                      | Some pattern -> yield sprintf "pattern=%s" pattern
                      | None -> ()
                      match fileFilter with
                      | Some file -> yield sprintf "file=%s" file
                      | None -> ()
                      match categoryFilter with
                      | Some category -> yield sprintf "category=%A" category
                      | None -> () ]
                    |> function
                      | [] -> "no filters"
                      | parts -> String.concat ", " parts

                  do! jsonResponse ctx 404 {|
                    success = false
                    error = sprintf "No discovered tests matched the explicit run filters (%s)." filterSummary
                  |}
              | false ->
                  dispatch (SageFs.SageFsMsg.Event (SageFs.SageFsEvent.RunTestsRequested tests))
                  do! jsonResponse ctx 200 {|
                    success = true
                    queued = tests.Length
                    message = sprintf "Queued %d test(s) for explicit run." tests.Length
                  |}
    } :> Task
  ) |> ignore
  app.MapGet("/api/live-testing/file-annotations", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let fileParam = ctx.Request.Query.["file"].ToString()
      match rctx.SseContext.GetElmModel with
      | None -> do! jsonResponse ctx 503 {| error = "Elm loop not started" |}
      | Some getModel ->
        let model = getModel()
        let lt = model.LiveTesting.TestState
        let entries =
          LiveTestState.statusEntriesForSession "" lt
        let matchingFile = FileAnnotations.resolveFilePath fileParam entries model.LiveTesting.InstrumentationMaps
        match matchingFile with
        | Some fullPath ->
          let fa = FileAnnotations.projectWithCoverage fullPath model.LiveTesting
          let json = System.Text.Json.JsonSerializer.Serialize(fa, rctx.SseContext.SseJsonOpts)
          do! jsonResponse ctx 200 json
        | None ->
          let fa = FileAnnotations.empty fileParam
          let json = System.Text.Json.JsonSerializer.Serialize(fa, rctx.SseContext.SseJsonOpts)
          do! jsonResponse ctx 200 json
    } :> Task
  ) |> ignore
  app.MapGet("/api/live-testing/status", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let fileParam =
        let fp = ctx.Request.Query.["file"].ToString()
        match System.String.IsNullOrWhiteSpace fp with
        | true -> None
        | false -> Some fp
      let! result = SageFs.McpTools.getLiveTestStatus rctx.McpContext "http" fileParam
      do! rawJsonResponse ctx result
    } :> Task
  ) |> ignore
  app.MapGet("/api/live-testing/test-trace", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! result = SageFs.McpTools.getTestTrace rctx.McpContext
      do! rawJsonResponse ctx result
    } :> Task
  ) |> ignore
  app.MapPost("/api/live-testing/mark-all-stale", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! result = SageFs.McpTools.markAllTestsStale rctx.McpContext
      do! jsonResponse ctx 202 {| message = result |}
    } :> Task
  ) |> ignore

let mapAnalysisRoutes (app: WebApplication) (rctx: RouteContext) =
  app.MapPost("/api/explore", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let! name = readJsonProp ctx "name"
      let! result = SageFs.McpTools.exploreNamespace rctx.McpContext "http" name None
      do! rawJsonResponse ctx result
    } :> Task
  ) |> ignore
  app.MapPost("/api/completions", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      use! json = readJsonBody ctx
      let root = json.RootElement
      let code = root.GetProperty("code").GetString()
      let cursor =
        match tryGetJsonIntAliases root [ "cursorPosition"; "cursor_position" ] with
        | Some value -> value
        | None -> raise (System.Text.Json.JsonException("Missing required cursorPosition/cursor_position"))
      // WHY — editors and agents routinely send a cursor one past the end of the
      // code string (or negative on malformed input). An out-of-range cursor made
      // completions silently return zero items (smoke-test failure 2026-08).
      // Because — clamping to [0, code.Length] keeps the completion request total.
      let code = match code with | null -> "" | c -> c
      let cursor = System.Math.Max(0, System.Math.Min(cursor, code.Length))
      let workingDirectory =
        tryGetJsonStringAliases root [ "workingDirectory"; "working_directory" ]
      let! items = SageFs.McpTools.getCompletionsItems rctx.McpContext "http" code cursor workingDirectory
      do! rawJsonResponse ctx (SageFs.McpAdapter.formatCompletionsJson items)
    } :> Task
  ) |> ignore
  app.MapGet("/api/dependency-graph", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    let symbol =
      match ctx.Request.Query.TryGetValue("symbol") with
      | true, v -> Some (string v)
      | _ -> None
    let json, status =
      match rctx.SseContext.GetElmModel with
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
      do! rawJsonResponse ctx json
    } :> Task
  ) |> ignore
  app.MapGet("/api/recent-events", fun (ctx: Microsoft.AspNetCore.Http.HttpContext) ->
    task {
      let count =
        match ctx.Request.Query.TryGetValue("count") with
        | true, v -> match System.Int32.TryParse(string v) with true, n -> n | _ -> 20
        | _ -> 20
      let! result = SageFs.McpTools.getRecentEvents rctx.McpContext "http" count None
      do! rawJsonResponse ctx result
    } :> Task
  ) |> ignore

let startMcpServer (cfg: McpServerConfig) =
  task {
    try
      let dispatch = cfg.ElmRuntime |> Option.map (fun r -> r.Dispatch)
      let getElmRegions = cfg.ElmRuntime |> Option.map (fun r -> r.GetRegions)
      let logPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SageFs", "mcp-server.log")
      System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)) |> ignore
      let version = DaemonInfo.version
      let otelConfigured = DaemonInfo.otelConfigured

      let builder = WebApplication.CreateBuilder([||])
      let bindHost =
        match System.Environment.GetEnvironmentVariable("SAGEFS_BIND_HOST") with
        | null | "" -> "localhost"
        | h -> h
      builder.WebHost.UseUrls(sprintf "http://%s:%d" bindHost cfg.Port) |> ignore

      // Phase 1: Infrastructure
      configureOtel builder cfg.Port version otelConfigured
      configureLogging builder logPath otelConfigured
      configureCompression builder

      // Phase 2: Services + MCP protocol
      let stateChangedStr : IEvent<string> option =
        cfg.StateChanged |> Option.map (fun evt ->
          let bridge = Event<string>()
          evt.Add(DaemonStateChange.toJson >> bridge.Trigger)
          bridge.Publish)
      let featurePushState =
        cfg.SharedFeatureState |> Option.defaultWith (fun () -> ref SageFs.Features.FeatureHooks.FeaturePushState.empty)
      let mcpContext = mkContext cfg stateChangedStr (Some (fun () -> featurePushState.Value))
      let serverTracker = McpServerTracker()
      let sseJsonOpts = JsonSerializerOptions()
      sseJsonOpts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
      configureMcpProtocol builder mcpContext serverTracker

      let app = builder.Build()
      wireCoreLogs app
      app.UseResponseCompression() |> ignore
      app.Use(Func<Microsoft.AspNetCore.Http.HttpContext, Func<Task>, Task>(fun ctx next ->
        errorHandlingMiddleware ctx next :> Task)) |> ignore
      app.Use(Func<Microsoft.AspNetCore.Http.HttpContext, Func<Task>, Task>(fun ctx next ->
        originGuardMiddleware ctx next :> Task)) |> ignore
      app.MapMcp() |> ignore

      // Phase 3: Route context + routes
      let fsiBindings = ref (Map.empty: Map<string, SageFs.SseWriter.FsiBinding>)
      let lastFeatureOutputCount = ref 0
      let lastEvalContext = ref (None: (string * int) option)
      let testEventBroadcast = Event<string>()
      let sessionEventBroadcast = Event<string>()
      let sseCtx: SseContext = {
        GetElmModel = cfg.ElmRuntime |> Option.map (fun r -> r.GetModel)
        GetWarmupContext = cfg.GetWarmupContext
        GetHotReloadState = cfg.GetHotReloadState
        SseJsonOpts = sseJsonOpts
        TestEventBroadcast = testEventBroadcast
        SessionEventBroadcast = sessionEventBroadcast
        ServerTracker = serverTracker
      }
      let rctx: RouteContext = {
        Config = cfg
        McpContext = mcpContext
        SseContext = sseCtx
        Dispatch = dispatch
        GetElmRegions = getElmRegions
        FsiBindings = fsiBindings
        FeaturePushState = featurePushState
        LastFeatureOutputCount = lastFeatureOutputCount
        LastEvalContext = lastEvalContext
      }

      match cfg.StateChanged with
      | Some evt -> wireSessionEventSubscription evt sseCtx
      | None -> ()

      mapExecutionRoutes app rctx
      mapHealthRoutes app rctx
      mapDiagnosticsRoutes app rctx
      mapEventsRoute app rctx
      mapStatusRoutes app rctx
      mapSessionRoutes app rctx
      mapLiveTestingRoutes app rctx
      mapAnalysisRoutes app rctx

      let _stateSub =
        cfg.StateChanged |> Option.map (fun evt ->
          wireModelChangeHandlers evt sseCtx fsiBindings featurePushState lastFeatureOutputCount cfg.SharedBindingScope lastEvalContext)

      logStartup app cfg.Port logPath otelConfigured
      do! app.RunAsync()
    with
    | :? System.IO.IOException as ex when ex.Message.Contains("address") || ex.Message.Contains("already") ->
      Log.error "Port %d is already in use. Another SageFs instance may be running — try 'sagefs status' or use --mcp-port to pick a different port." cfg.Port
    | ex ->
      Log.error "MCP server failed to start (%s): %s\n%s" (ex.GetType().Name) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
  }
