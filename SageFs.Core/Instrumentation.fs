namespace SageFs

open System.Diagnostics
open System.Diagnostics.Metrics

/// Centralized OTel instrumentation for SageFs.
/// SessionManager: session lifecycle spans and counters.
/// Test cycle: end-to-end file-change → test-result timing.
/// When no collector is attached, StartActivity returns null (~50ns no-op).
module Instrumentation =

  let sessionSource = new ActivitySource("SageFs.SessionManager")
  let testCycleSource = new ActivitySource("SageFs.TestCycle")
  let mcpSource = new ActivitySource("SageFs.Mcp")
  let elmloopSource = new ActivitySource("SageFs.ElmLoop")
  let daemonSource = new ActivitySource("SageFs.Daemon")

  let sessionMeter = new Meter("SageFs.SessionManager")
  let testCycleMeter = new Meter("SageFs.TestCycle")
  let mcpMeter = new Meter("SageFs.Mcp")
  let daemonMeter = new Meter("SageFs.Daemon")
  let renderMeter = new Meter("SageFs.RenderPipeline")

  // Daemon startup timing
  let startupDurationMs =
    daemonMeter.CreateHistogram<float>("sagefs.daemon.startup_duration_ms", "ms", "Daemon startup duration to ready state")

  let sessionsCreated =
    sessionMeter.CreateCounter<int64>("sagefs.sessions.created_total", description = "Total sessions created")
  let sessionsStopped =
    sessionMeter.CreateCounter<int64>("sagefs.sessions.stopped_total", description = "Total sessions stopped")
  let sessionsRestarted =
    sessionMeter.CreateCounter<int64>("sagefs.sessions.restarted_total", description = "Total session restarts")
  let standbySwaps =
    sessionMeter.CreateCounter<int64>("sagefs.sessions.standby_swaps_total", description = "Restarts using standby pool")
  let coldRestarts =
    sessionMeter.CreateCounter<int64>("sagefs.sessions.cold_restarts_total", description = "Restarts without standby")
  let activeSessions =
    sessionMeter.CreateUpDownCounter<int64>("sagefs.sessions.active", description = "Currently active sessions")

  let testCycleEndToEnd =
    testCycleMeter.CreateHistogram<float>("sagefs.test_cycle.end_to_end_ms", unit = "ms", description = "Test cycle end-to-end latency")
  let fcsTypecheckMs =
    testCycleMeter.CreateHistogram<float>("sagefs.test_cycle.fcs_typecheck_ms", unit = "ms", description = "FCS type-check latency")
  let treeSitterParseMs =
    testCycleMeter.CreateHistogram<float>("sagefs.test_cycle.treesitter_parse_ms", unit = "ms", description = "Tree-sitter parse latency")
  let testExecutionMs =
    testCycleMeter.CreateHistogram<float>("sagefs.test_cycle.test_execution_ms", unit = "ms", description = "Test execution latency")

  let mcpToolInvocations =
    mcpMeter.CreateCounter<int64>("sagefs.mcp.tool_invocations_total", description = "Total MCP tool invocations")
  let mcpToolSuccesses =
    mcpMeter.CreateCounter<int64>("sagefs.mcp.tool_successes_total", description = "Total successful MCP tool invocations")
  let mcpToolFailures =
    mcpMeter.CreateCounter<int64>("sagefs.mcp.tool_failures_total", description = "Total failed MCP tool invocations")
  let fsiEvals =
    mcpMeter.CreateCounter<int64>("sagefs.fsi.evals_total", description = "Total FSI eval calls")
  let fsiStatements =
    mcpMeter.CreateCounter<int64>("sagefs.fsi.statements_total", description = "Total FSI statements evaluated")
  let sseConnectionsActive =
    mcpMeter.CreateUpDownCounter<int64>("sagefs.sse.connections_active", description = "Currently active SSE connections")

  // Standby pool metrics
  let standbyPoolSize =
    sessionMeter.CreateUpDownCounter<int64>("sagefs.standby.pool_size", description = "Current standby pool size")
  let standbyWarmupMs =
    sessionMeter.CreateHistogram<float>("sagefs.standby.warmup_ms", "ms", "Standby warmup duration")
  let standbyInvalidations =
    sessionMeter.CreateCounter<int64>("sagefs.standby.invalidations_total", description = "Total standby invalidations")
  let standbyAgeAtSwapMs =
    sessionMeter.CreateHistogram<float>("sagefs.standby.age_at_swap_ms", "ms", "Standby age at time of swap")

  // File watcher counter
  let fileWatcherChanges =
    testCycleMeter.CreateCounter<int64>("sagefs.filewatcher.changes_total", description = "Total file watcher change events")

  // P0: EventStore retry envelope metrics
  let eventstoreAppendRetries =
    sessionMeter.CreateCounter<int64>("sagefs.eventstore.append_retries_total", description = "Total event append retries due to version conflicts")
  let eventstoreAppendDurationMs =
    sessionMeter.CreateHistogram<float>("sagefs.eventstore.append_duration_ms", "ms", "Event append duration including retries")
  let eventstoreAppendFailures =
    sessionMeter.CreateCounter<int64>("sagefs.eventstore.append_failures_total", description = "Total event append failures after retry exhaustion")

  // P0: Daemon startup metrics
  let daemonStartupMs =
    sessionMeter.CreateHistogram<float>("sagefs.daemon.startup_ms", "ms", "Daemon startup duration")
  let daemonReplayEventCount =
    sessionMeter.CreateCounter<int64>("sagefs.daemon.replay_event_count", description = "Event count during daemon startup replay")
  let daemonSessionsResumed =
    sessionMeter.CreateCounter<int64>("sagefs.daemon.sessions_resumed_total", description = "Sessions resumed during daemon startup")
  let daemonDuplicatesPruned =
    sessionMeter.CreateCounter<int64>("sagefs.daemon.duplicates_pruned_total", description = "Duplicate sessions pruned during startup")

  // P1: Elm loop metrics (histograms only — no spans near the lock)
  let elmloopUpdateMs =
    testCycleMeter.CreateHistogram<float>("sagefs.elmloop.update_ms", "ms", "Elm loop Update phase duration")
  let elmloopRenderMs =
    testCycleMeter.CreateHistogram<float>("sagefs.elmloop.render_ms", "ms", "Elm loop Render phase duration")
  let elmloopCallbackMs =
    testCycleMeter.CreateHistogram<float>("sagefs.elmloop.callback_ms", "ms", "Elm loop OnModelChanged callback duration")
  let elmloopEffectsSpawned =
    testCycleMeter.CreateCounter<int64>("sagefs.elmloop.effects_spawned_total", description = "Total effects spawned from Elm loop")
  let elmloopLockWaitMs =
    testCycleMeter.CreateHistogram<float>("sagefs.elmloop.lock_wait_ms", "ms", "Time waiting to acquire Elm loop lock")
  let elmloopTotalDispatchMs =
    testCycleMeter.CreateHistogram<float>("sagefs.elmloop.total_dispatch_ms", "ms", "Total end-to-end dispatch duration")
  let elmloopQueueDepth =
    testCycleMeter.CreateUpDownCounter<int64>("sagefs.elmloop.queue_depth", description = "Number of dispatches waiting for the lock")

  // Render pipeline per-stage instrumentation
  let renderScreenDrawMs =
    renderMeter.CreateHistogram<float>("sagefs.render.screen_draw_ms", "ms", "Screen.drawWith duration (model → CellGrid)")
  let renderEmitMs =
    renderMeter.CreateHistogram<float>("sagefs.render.emit_ms", "ms", "AnsiEmitter emit/emitDiff duration (CellGrid → escape codes)")
  let renderConsoleWriteMs =
    renderMeter.CreateHistogram<float>("sagefs.render.console_write_ms", "ms", "Console.Write duration (escape codes → terminal)")
  let renderFrameTotalMs =
    renderMeter.CreateHistogram<float>("sagefs.render.frame_total_ms", "ms", "Total frame duration end-to-end")
  let renderDiffCellCount =
    renderMeter.CreateHistogram<int64>("sagefs.render.diff_cell_count", description = "Number of cells changed per diff emit frame")
  let renderFullEmitCount =
    renderMeter.CreateCounter<int64>("sagefs.render.full_emit_total", description = "Frames that used full emit (no diff)")
  let renderDiffEmitCount =
    renderMeter.CreateCounter<int64>("sagefs.render.diff_emit_total", description = "Frames that used diff emit")

  // P1: LiveTesting additions
  let liveTestingDiscoveryMs =
    testCycleMeter.CreateHistogram<float>("sagefs.live_testing.discovery_ms", "ms", "Test discovery duration")
  let liveTestingAssemblyLoadErrors =
    testCycleMeter.CreateCounter<int64>("sagefs.live_testing.assembly_load_errors_total", description = "Total assembly load errors during test discovery")

  // Coverage instrumentation observability
  let coverageMapsReceived =
    testCycleMeter.CreateCounter<int64>("sagefs.coverage.maps_received_total", description = "Total instrumentation map batches received from workers")
  let coverageProbesTotal =
    testCycleMeter.CreateCounter<int64>("sagefs.coverage.probes_total", description = "Total IL probes across received instrumentation maps")
  let coverageBitmapsCollected =
    testCycleMeter.CreateCounter<int64>("sagefs.coverage.bitmaps_collected_total", description = "Total coverage bitmap collections from test runs")

  // Daemon blocking diagnostics
  let threadPoolPending =
    testCycleMeter.CreateObservableGauge<int64>(
      "sagefs.threadpool.pending_work_items",
      (fun () -> int64 System.Threading.ThreadPool.PendingWorkItemCount),
      description = "ThreadPool pending work items")
  let threadPoolCount =
    testCycleMeter.CreateObservableGauge<int64>(
      "sagefs.threadpool.thread_count",
      (fun () -> int64 System.Threading.ThreadPool.ThreadCount),
      description = "ThreadPool active thread count")
  let elmDispatchCount =
    testCycleMeter.CreateCounter<int64>("sagefs.elmloop.dispatch_total", description = "Total Elm dispatch calls")
  let elmloopErrors =
    testCycleMeter.CreateCounter<int64>("sagefs.elmloop.errors_total", description = "Total errors in Elm loop phases")
  let testResultBatchSize =
    testCycleMeter.CreateHistogram<int64>("sagefs.live_testing.result_batch_size", description = "Number of results per TestResultsBatch dispatch")
  let testExecutionActiveCount =
    testCycleMeter.CreateUpDownCounter<int64>("sagefs.live_testing.active_executions", description = "Currently executing test runs")

  // Eval actor queue diagnostics (Level 1)
  let evalQueueWaitMs =
    mcpMeter.CreateHistogram<float>(
      "sagefs.evalactor.queue_wait_ms", unit = "ms",
      description = "Time eval requests wait in the actor queue before processing starts")
  let evalQueueDepth =
    mcpMeter.CreateUpDownCounter<int64>(
      "sagefs.evalactor.queue_depth",
      description = "Current eval actor queue depth")
  let evalCategoryCount =
    mcpMeter.CreateCounter<int64>(
      "sagefs.evalactor.eval_by_category_total",
      description = "Eval requests by category (repl/test/hotreload/warmup/check/completion)")

  // P2: DevReload connected clients
  let devReloadConnectedClients =
    mcpMeter.CreateUpDownCounter<int64>("sagefs.devreload.connected_clients", description = "Currently connected SSE reload clients")

  // Binary cache persistence
  let cacheSaveCount =
    sessionMeter.CreateCounter<int64>("sagefs.cache.periodic_saves_total", description = "Total periodic test cache saves")
  let cacheSaveMs =
    sessionMeter.CreateHistogram<float>("sagefs.cache.save_ms", "ms", "Periodic test cache save duration")

  // P2: RED metric gaps — MCP tool duration
  let mcpToolDurationMs =
    mcpMeter.CreateHistogram<float>("sagefs.mcp.tool_duration_ms", "ms", "MCP tool invocation duration")

  // P2: RED metric gaps — Worker proxy
  let workerRequestDurationMs =
    sessionMeter.CreateHistogram<float>("sagefs.worker.request_duration_ms", "ms", "Daemon→worker proxy request duration")
  let workerRequestErrors =
    sessionMeter.CreateCounter<int64>("sagefs.worker.request_errors_total", description = "Total daemon→worker proxy request errors")

  // P2: RED metric gaps — SSE
  let sseWriteErrors =
    mcpMeter.CreateCounter<int64>("sagefs.sse.write_errors_total", description = "Total SSE write errors")
  let sseEventsDropped =
    mcpMeter.CreateCounter<int64>("sagefs.sse.events_dropped_total", description = "Total SSE events dropped due to full buffer")
  let sseConnectionDurationMs =
    mcpMeter.CreateHistogram<float>("sagefs.sse.connection_duration_ms", "ms", "SSE connection lifetime duration")
  let sseFrameBytes =
    mcpMeter.CreateHistogram<int64>("sagefs.sse.frame_bytes", "B", "SSE frame size in bytes")

  // P2: RED metric gaps — EventStore fetch
  let eventstoreFetchDurationMs =
    sessionMeter.CreateHistogram<float>("sagefs.eventstore.fetch_duration_ms", "ms", "EventStore fetch duration")
  let eventstoreStreamEventCount =
    sessionMeter.CreateHistogram<int64>("sagefs.eventstore.stream_event_count", description = "Event count per fetched stream")

  // P2: RED metric gaps — FSI eval
  let fsiEvalDurationMs =
    mcpMeter.CreateHistogram<float>("sagefs.fsi.eval_duration_ms", "ms", "FSI eval duration")
  let fsiEvalErrors =
    mcpMeter.CreateCounter<int64>("sagefs.fsi.eval_errors_total", description = "Total FSI eval errors")

  // P0: Actor loop resilience counters
  let actorErrors =
    sessionMeter.CreateCounter<int64>(
      "sagefs.actor.errors_total",
      description = "Unhandled exceptions caught by ResilientActor wrapLoop")
  let fireAndForgetErrors =
    sessionMeter.CreateCounter<int64>(
      "sagefs.fire_and_forget.errors_total",
      description = "Unhandled exceptions caught by SafeFireAndForget")

  // P5: Background task error visibility (periodic saves, health checks)
  let periodicTaskErrors =
    sessionMeter.CreateCounter<int64>(
      "sagefs.daemon.periodic_task_errors_total",
      description = "Errors in periodic background tasks (cache_save, manifest_save)")

  // P5: GC/memory observable gauges (zero per-event cost)
  let gcHeapBytes =
    sessionMeter.CreateObservableGauge<int64>(
      "sagefs.gc.heap_bytes",
      (fun () -> System.GC.GetTotalMemory(false)),
      "bytes",
      "GC managed heap size (approximate)")
  let gcGen2Collections =
    sessionMeter.CreateObservableGauge<int64>(
      "sagefs.gc.collections_gen2",
      (fun () -> int64 (System.GC.CollectionCount(2))),
      description = "Gen2 GC collection count (cumulative)")

  // P5: Binary persistence error counters
  let persistenceSaveErrors =
    sessionMeter.CreateCounter<int64>(
      "sagefs.persistence.save_errors_total",
      description = "Binary file save failures (atomic write)")
  let persistenceCrcErrors =
    sessionMeter.CreateCounter<int64>(
      "sagefs.persistence.crc_errors_total",
      description = "CRC validation failures on binary file reads")
  let persistenceOrphanedTmpCleanup =
    sessionMeter.CreateCounter<int64>(
      "sagefs.persistence.orphaned_tmp_cleanup_total",
      description = "Orphaned .tmp files cleaned up at startup")

  /// SSE/long-lived paths to suppress in ASP.NET Core HTTP span instrumentation.
  let sseFilterPaths =
    [ "/events"; "/diagnostics"; "/__sagefs__/reload"; "/sse"; "/dashboard/stream"; "/health" ]

  /// Returns true if the HTTP path should be instrumented (not an SSE long-lived path).
  let shouldFilterHttpSpan (path: string) =
    sseFilterPaths |> List.exists (fun p -> path.StartsWith(p)) |> not

  /// W3C TraceContext traceparent header parsed into fields.
  type TraceparentHeader = {
    version: byte
    traceId: string
    spanId: string
    flags: byte
  }

  /// Format a TraceparentHeader as a W3C traceparent string: {version}-{traceId}-{spanId}-{flags}
  let formatTraceparent (h: TraceparentHeader) =
    sprintf "%02x-%s-%s-%02x" h.version h.traceId h.spanId h.flags

  /// Parse a W3C traceparent string into a TraceparentHeader.
  /// Validates: 4 dash-separated segments, traceId=32 hex chars, spanId=16 hex chars, neither all-zero.
  let parseTraceparent (s: string) : Result<TraceparentHeader, string> =
    let parts = s.Split('-')
    match parts.Length >= 4 with
    | false -> Error "expected at least 4 dash-separated segments"
    | true ->
      let ver = parts.[0]
      let tid = parts.[1]
      let sid = parts.[2]
      let fl = parts.[3]
      match tid.Length = 32 with
      | false -> Error (sprintf "traceId must be 32 hex chars, got %d" tid.Length)
      | true ->
        match sid.Length = 16 with
        | false -> Error (sprintf "spanId must be 16 hex chars, got %d" sid.Length)
        | true ->
          match tid = System.String('0', 32) with
          | true -> Error "traceId must not be all-zero"
          | false ->
            match sid = System.String('0', 16) with
            | true -> Error "spanId must not be all-zero"
            | false ->
              Ok {
                version = System.Convert.ToByte(ver, 16)
                traceId = tid
                spanId = sid
                flags = System.Convert.ToByte(fl, 16)
              }

  /// Env vars to propagate to worker processes for OTel.
  /// Always includes service name; includes OTLP endpoint/protocol only if configured.
  /// Propagates current W3C TraceContext as TRACEPARENT so worker spans link to daemon traces.
  let workerOtelEnvVars (sessionId: string) : (string * string) list =
    let base' = [ "OTEL_SERVICE_NAME", sprintf "sagefs-worker-%s" sessionId ]
    let endpoint = System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    let protocol = System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")
    let extras =
      [ match System.String.IsNullOrEmpty endpoint with
        | false -> "OTEL_EXPORTER_OTLP_ENDPOINT", endpoint
        | true -> ()
        match System.String.IsNullOrEmpty protocol with
        | false -> "OTEL_EXPORTER_OTLP_PROTOCOL", protocol
        | true -> ()
        // W3C TraceContext: propagate current Activity as TRACEPARENT
        match Activity.Current with
        | null -> ()
        | a ->
          let header = {
            version = 0uy
            traceId = a.TraceId.ToHexString()
            spanId = a.SpanId.ToHexString()
            flags = byte a.ActivityTraceFlags
          }
          "TRACEPARENT", formatTraceparent header ]
    base' @ extras

  /// All ActivitySource names for OTel registration in McpServer.
  let allSources =
    [ "SageFs.SessionManager"
      "SageFs.TestCycle"
      "SageFs.LiveTesting"
      "SageFs.ElmLoop"
      "SageFs.Mcp"
      "SageFs.Daemon" ]

  /// All Meter names for OTel registration in McpServer.
  let allMeters =
    [ "SageFs.SessionManager"
      "SageFs.TestCycle"
      "SageFs.LiveTesting"
      "SageFs.Mcp" ]

  /// Start an Activity with initial tags. Returns null when no listener attached.
  let startSpan (source: ActivitySource) (name: string) (tags: (string * obj) list) =
    let activity = source.StartActivity(name)
    match isNull activity with
    | false ->
      for (k, v) in tags do
        activity.SetTag(k, v) |> ignore
    | true -> ()
    activity

  /// Start an Activity with a specific ActivityKind and initial tags.
  let startSpanWithKind (source: ActivitySource) (name: string) (kind: ActivityKind) (tags: (string * obj) list) =
    let activity = source.StartActivity(name, kind)
    match isNull activity with
    | false ->
      for (k, v) in tags do
        activity.SetTag(k, v) |> ignore
    | true -> ()
    activity

  /// Stop an activity with success status.
  let succeedSpan (activity: Activity) =
    match isNull activity with
    | false ->
      activity.Stop()
      activity.Dispose()
    | true -> ()

  /// Stop an activity with error status and message.
  let failSpan (activity: Activity) (message: string) =
    match isNull activity with
    | false ->
      activity.SetTag("error", true) |> ignore
      activity.SetTag("error.message", message) |> ignore
      activity.SetStatus(ActivityStatusCode.Error, message) |> ignore
      activity.Stop()
      activity.Dispose()
    | true -> ()

  /// Wrap a synchronous operation with Activity tracing.
  /// Tags are set on the activity before it is stopped.
  /// Uses explicit Stop/Dispose for reliable ActivityStopped callbacks.
  let traced (source: ActivitySource) (name: string) (tags: (string * obj) list) (f: unit -> 'a) =
    let sw = Stopwatch.StartNew()
    let activity = source.StartActivity(name)
    try
      let result = f ()
      sw.Stop()
      match isNull activity with
      | false ->
        for (k, v) in tags do
          activity.SetTag(k, v) |> ignore
        activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
        activity.Stop()
        activity.Dispose()
      | true -> ()
      result
    with ex ->
      sw.Stop()
      match isNull activity with
      | false ->
        activity.SetTag("error", true) |> ignore
        activity.SetTag("error.type", ex.GetType().Name) |> ignore
        activity.SetTag("error.message", ex.Message) |> ignore
        activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
        activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
        activity.Stop()
        activity.Dispose()
      | true -> ()
      raise ex

  /// Wrap an async operation with Activity tracing.
  let tracedAsync (source: ActivitySource) (name: string) (tags: (string * obj) list) (f: unit -> Async<'a>) =
    async {
      let sw = Stopwatch.StartNew()
      let activity = source.StartActivity(name)
      try
        let! result = f ()
        sw.Stop()
        match isNull activity with
        | false ->
          for (k, v) in tags do
            activity.SetTag(k, v) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.Stop()
          activity.Dispose()
        | true -> ()
        return result
      with ex ->
        sw.Stop()
        match isNull activity with
        | false ->
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.type", ex.GetType().Name) |> ignore
          activity.SetTag("error.message", ex.Message) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
          activity.Stop()
          activity.Dispose()
        | true -> ()
        return raise ex
    }

  /// Wrap an MCP tool invocation with tracing, RPC semantic conventions, and counting.
  let tracedMcpTool (toolName: string) (agentName: string) (f: unit -> System.Threading.Tasks.Task<string>) : System.Threading.Tasks.Task<string> =
    task {
      mcpToolInvocations.Add(1L)
      let sw = Stopwatch.StartNew()
      let activity = mcpSource.StartActivity("mcp.tool.invoke", ActivityKind.Server)
      try
        match isNull activity with
        | false ->
          activity.SetTag("mcp.tool.name", toolName) |> ignore
          activity.SetTag("mcp.agent.name", agentName) |> ignore
          activity.SetTag("rpc.system", "mcp") |> ignore
          activity.SetTag("rpc.service", "sagefs") |> ignore
          activity.SetTag("rpc.method", toolName) |> ignore
        | true -> ()
        let! result = f ()
        sw.Stop()
        let tag = System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName)
        mcpToolSuccesses.Add(1L, tag)
        mcpToolDurationMs.Record(sw.Elapsed.TotalMilliseconds, tag)
        match isNull activity with
        | false ->
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.Stop()
          activity.Dispose()
        | true -> ()
        return result
      with ex ->
        sw.Stop()
        let tag = System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName)
        mcpToolFailures.Add(1L, tag)
        mcpToolDurationMs.Record(sw.Elapsed.TotalMilliseconds, tag)
        match isNull activity with
        | false ->
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.message", ex.Message) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
          activity.Stop()
          activity.Dispose()
        | true -> ()
        return raise ex
    }

  /// Category of eval request for queue diagnostics.
  type EvalCategory = Repl | Test | HotReload | Warmup | Check | Completion

  module EvalCategory =
    let label = function
      | Repl -> "repl" | Test -> "test" | HotReload -> "hotreload"
      | Warmup -> "warmup" | Check -> "check" | Completion -> "completion"

  /// Wrap an actor.PostAndAsyncReply call with queue-wait measurement.
  let tracedActorPost (category: EvalCategory) (postAndReply: Async<'a>) : Async<'a> =
    async {
      let catLabel = EvalCategory.label category
      let tag = System.Collections.Generic.KeyValuePair("category", catLabel :> obj)
      let enqueuedAt = Stopwatch.GetTimestamp()
      evalQueueDepth.Add(1L, tag)
      evalCategoryCount.Add(1L, tag)
      let! result = postAndReply
      let dequeuedAt = Stopwatch.GetTimestamp()
      let waitMs = float (dequeuedAt - enqueuedAt) / float Stopwatch.Frequency * 1000.0
      evalQueueWaitMs.Record(waitMs, tag)
      evalQueueDepth.Add(-1L, tag)
      return result
    }

  /// Wrap an FSI eval call with tracing and counting.
  let tracedFsiEval (agentName: string) (statementCount: int) (sessionId: string) (f: unit -> System.Threading.Tasks.Task<string>) : System.Threading.Tasks.Task<string> =
    task {
      fsiEvals.Add(1L)
      fsiStatements.Add(int64 statementCount)
      let sw = Stopwatch.StartNew()
      let activity = mcpSource.StartActivity("fsi.eval", ActivityKind.Server)
      try
        match isNull activity with
        | false ->
          activity.SetTag("fsi.agent.name", agentName) |> ignore
          activity.SetTag("fsi.statement.count", statementCount) |> ignore
          activity.SetTag("fsi.session.id", sessionId) |> ignore
        | true -> ()
        let! result = f ()
        sw.Stop()
        fsiEvalDurationMs.Record(sw.Elapsed.TotalMilliseconds)
        match isNull activity with
        | false ->
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.Stop()
          activity.Dispose()
        | true -> ()
        return result
      with ex ->
        sw.Stop()
        fsiEvalDurationMs.Record(sw.Elapsed.TotalMilliseconds)
        fsiEvalErrors.Add(1L)
        match isNull activity with
        | false ->
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.message", ex.Message) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
          activity.Stop()
          activity.Dispose()
        | true -> ()
        return raise ex
    }

  /// Wrap a Task-returning operation with Activity tracing.
  let tracedTask (source: ActivitySource) (name: string) (tags: (string * obj) list) (f: unit -> System.Threading.Tasks.Task<'a>) =
    task {
      let sw = Stopwatch.StartNew()
      let activity = source.StartActivity(name)
      try
        let! result = f ()
        sw.Stop()
        match isNull activity with
        | false ->
          for (k, v) in tags do
            activity.SetTag(k, v) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.Stop()
          activity.Dispose()
        | true -> ()
        return result
      with ex ->
        sw.Stop()
        match isNull activity with
        | false ->
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.type", ex.GetType().Name) |> ignore
          activity.SetTag("error.message", ex.Message) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
          activity.Stop()
          activity.Dispose()
        | true -> ()
        return raise ex
    }
