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
  let fsiEvals =
    mcpMeter.CreateCounter<int64>("sagefs.fsi.evals_total", description = "Total FSI eval calls")
  let fsiStatements =
    mcpMeter.CreateCounter<int64>("sagefs.fsi.statements_total", description = "Total FSI statements evaluated")
  let sseConnectionsActive =
    mcpMeter.CreateUpDownCounter<int64>("sagefs.sse.connections_active", description = "Currently active SSE connections")

  /// SSE/long-lived paths to suppress in ASP.NET Core HTTP span instrumentation.
  let sseFilterPaths =
    [ "/events"; "/diagnostics"; "/__sagefs__/reload"; "/sse"; "/dashboard/stream" ]

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
