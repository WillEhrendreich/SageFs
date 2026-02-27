namespace SageFs

open System.Diagnostics
open System.Diagnostics.Metrics

/// Centralized OTel instrumentation for SageFs.
/// SessionManager: session lifecycle spans and counters.
/// Pipeline: end-to-end file-change → test-result timing.
/// When no collector is attached, StartActivity returns null (~50ns no-op).
module Instrumentation =

  let sessionSource = new ActivitySource("SageFs.SessionManager")
  let pipelineSource = new ActivitySource("SageFs.Pipeline")
  let mcpSource = new ActivitySource("SageFs.Mcp")

  let sessionMeter = new Meter("SageFs.SessionManager")
  let pipelineMeter = new Meter("SageFs.Pipeline")
  let mcpMeter = new Meter("SageFs.Mcp")

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

  let pipelineEndToEnd =
    pipelineMeter.CreateHistogram<float>("sagefs.pipeline.end_to_end_ms", unit = "ms", description = "Pipeline end-to-end latency")
  let fcsTypecheckMs =
    pipelineMeter.CreateHistogram<float>("sagefs.pipeline.fcs_typecheck_ms", unit = "ms", description = "FCS type-check latency")
  let treeSitterParseMs =
    pipelineMeter.CreateHistogram<float>("sagefs.pipeline.treesitter_parse_ms", unit = "ms", description = "Tree-sitter parse latency")
  let testExecutionMs =
    pipelineMeter.CreateHistogram<float>("sagefs.pipeline.test_execution_ms", unit = "ms", description = "Test execution latency")

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
    pipelineMeter.CreateCounter<int64>("sagefs.filewatcher.changes_total", description = "Total file watcher change events")

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
    pipelineMeter.CreateHistogram<float>("sagefs.elmloop.update_ms", "ms", "Elm loop Update phase duration")
  let elmloopRenderMs =
    pipelineMeter.CreateHistogram<float>("sagefs.elmloop.render_ms", "ms", "Elm loop Render phase duration")
  let elmloopCallbackMs =
    pipelineMeter.CreateHistogram<float>("sagefs.elmloop.callback_ms", "ms", "Elm loop OnModelChanged callback duration")
  let elmloopEffectsSpawned =
    pipelineMeter.CreateCounter<int64>("sagefs.elmloop.effects_spawned_total", description = "Total effects spawned from Elm loop")

  // P1: LiveTesting additions
  let liveTestingDiscoveryMs =
    pipelineMeter.CreateHistogram<float>("sagefs.live_testing.discovery_ms", "ms", "Test discovery duration")
  let liveTestingAssemblyLoadErrors =
    pipelineMeter.CreateCounter<int64>("sagefs.live_testing.assembly_load_errors_total", description = "Total assembly load errors during test discovery")

  // P2: DevReload connected clients
  let devReloadConnectedClients =
    mcpMeter.CreateUpDownCounter<int64>("sagefs.devreload.connected_clients", description = "Currently connected SSE reload clients")

  /// SSE/long-lived paths to suppress in ASP.NET Core HTTP span instrumentation.
  let sseFilterPaths =
    [ "/events"; "/diagnostics"; "/__sagefs__/reload"; "/sse"; "/dashboard/stream"; "/health" ]

  /// Returns true if the HTTP path should be instrumented (not an SSE long-lived path).
  let shouldFilterHttpSpan (path: string) =
    sseFilterPaths |> List.exists (fun p -> path.StartsWith(p)) |> not

  /// Env vars to propagate to worker processes for OTel.
  /// Always includes service name; includes OTLP endpoint/protocol only if configured.
  let workerOtelEnvVars (sessionId: string) : (string * string) list =
    let base' = [ "OTEL_SERVICE_NAME", sprintf "sagefs-worker-%s" sessionId ]
    let endpoint = System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    let protocol = System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")
    let extras =
      [ if not (System.String.IsNullOrEmpty endpoint) then
          "OTEL_EXPORTER_OTLP_ENDPOINT", endpoint
        if not (System.String.IsNullOrEmpty protocol) then
          "OTEL_EXPORTER_OTLP_PROTOCOL", protocol ]
    base' @ extras

  /// All ActivitySource names for OTel registration in McpServer.
  let allSources =
    [ "SageFs.SessionManager"
      "SageFs.Pipeline"
      "SageFs.LiveTesting"
      "SageFs.Mcp"
      "Marten" ]

  /// All Meter names for OTel registration in McpServer.
  let allMeters =
    [ "SageFs.SessionManager"
      "SageFs.Pipeline"
      "SageFs.LiveTesting"
      "SageFs.Mcp"
      "Marten" ]

  /// Start an Activity with initial tags. Returns null when no listener attached.
  let startSpan (source: ActivitySource) (name: string) (tags: (string * obj) list) =
    let activity = source.StartActivity(name)
    if not (isNull activity) then
      for (k, v) in tags do
        activity.SetTag(k, v) |> ignore
    activity

  /// Start an Activity with a specific ActivityKind and initial tags.
  let startSpanWithKind (source: ActivitySource) (name: string) (kind: ActivityKind) (tags: (string * obj) list) =
    let activity = source.StartActivity(name, kind)
    if not (isNull activity) then
      for (k, v) in tags do
        activity.SetTag(k, v) |> ignore
    activity

  /// Stop an activity with success status.
  let succeedSpan (activity: Activity) =
    if not (isNull activity) then
      activity.Stop()
      activity.Dispose()

  /// Stop an activity with error status and message.
  let failSpan (activity: Activity) (message: string) =
    if not (isNull activity) then
      activity.SetTag("error", true) |> ignore
      activity.SetTag("error.message", message) |> ignore
      activity.SetStatus(ActivityStatusCode.Error, message) |> ignore
      activity.Stop()
      activity.Dispose()

  /// Wrap a synchronous operation with Activity tracing.
  /// Tags are set on the activity before it is stopped.
  /// Uses explicit Stop/Dispose for reliable ActivityStopped callbacks.
  let traced (source: ActivitySource) (name: string) (tags: (string * obj) list) (f: unit -> 'a) =
    let sw = Stopwatch.StartNew()
    let activity = source.StartActivity(name)
    try
      let result = f ()
      sw.Stop()
      if not (isNull activity) then
        for (k, v) in tags do
          activity.SetTag(k, v) |> ignore
        activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
        activity.Stop()
        activity.Dispose()
      result
    with ex ->
      sw.Stop()
      if not (isNull activity) then
        activity.SetTag("error", true) |> ignore
        activity.SetTag("error.type", ex.GetType().Name) |> ignore
        activity.SetTag("error.message", ex.Message) |> ignore
        activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
        activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
        activity.Stop()
        activity.Dispose()
      raise ex

  /// Wrap an async operation with Activity tracing.
  let tracedAsync (source: ActivitySource) (name: string) (tags: (string * obj) list) (f: unit -> Async<'a>) =
    async {
      let sw = Stopwatch.StartNew()
      let activity = source.StartActivity(name)
      try
        let! result = f ()
        sw.Stop()
        if not (isNull activity) then
          for (k, v) in tags do
            activity.SetTag(k, v) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.Stop()
          activity.Dispose()
        return result
      with ex ->
        sw.Stop()
        if not (isNull activity) then
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.type", ex.GetType().Name) |> ignore
          activity.SetTag("error.message", ex.Message) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
          activity.Stop()
          activity.Dispose()
        return raise ex
    }

  /// Wrap an MCP tool invocation with tracing, RPC semantic conventions, and counting.
  let tracedMcpTool (toolName: string) (agentName: string) (f: unit -> System.Threading.Tasks.Task<string>) : System.Threading.Tasks.Task<string> =
    task {
      mcpToolInvocations.Add(1L)
      let activity = mcpSource.StartActivity("mcp.tool.invoke", ActivityKind.Server)
      try
        if not (isNull activity) then
          activity.SetTag("mcp.tool.name", toolName) |> ignore
          activity.SetTag("mcp.agent.name", agentName) |> ignore
          activity.SetTag("rpc.system", "mcp") |> ignore
          activity.SetTag("rpc.service", "sagefs") |> ignore
          activity.SetTag("rpc.method", toolName) |> ignore
        let! result = f ()
        mcpToolSuccesses.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
        if not (isNull activity) then
          activity.Stop()
          activity.Dispose()
        return result
      with ex ->
        mcpToolFailures.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
        if not (isNull activity) then
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.message", ex.Message) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
          activity.Stop()
          activity.Dispose()
        return raise ex
    }

  /// Wrap an FSI eval call with tracing and counting.
  let tracedFsiEval (agentName: string) (statementCount: int) (sessionId: string) (f: unit -> System.Threading.Tasks.Task<string>) : System.Threading.Tasks.Task<string> =
    task {
      fsiEvals.Add(1L)
      fsiStatements.Add(int64 statementCount)
      let activity = mcpSource.StartActivity("fsi.eval", ActivityKind.Server)
      try
        if not (isNull activity) then
          activity.SetTag("fsi.agent.name", agentName) |> ignore
          activity.SetTag("fsi.statement.count", statementCount) |> ignore
          activity.SetTag("fsi.session.id", sessionId) |> ignore
        let! result = f ()
        if not (isNull activity) then
          activity.Stop()
          activity.Dispose()
        return result
      with ex ->
        if not (isNull activity) then
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.message", ex.Message) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
          activity.Stop()
          activity.Dispose()
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
        if not (isNull activity) then
          for (k, v) in tags do
            activity.SetTag(k, v) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.Stop()
          activity.Dispose()
        return result
      with ex ->
        sw.Stop()
        if not (isNull activity) then
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.type", ex.GetType().Name) |> ignore
          activity.SetTag("error.message", ex.Message) |> ignore
          activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
          activity.Stop()
          activity.Dispose()
        return raise ex
    }
