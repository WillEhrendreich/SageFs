module SageFs.Tests.InstrumentationTests

open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs

// Tests use ActivityListeners that share global ActivitySources,
// so they must run sequentially to avoid cross-test interference.
[<Tests>]
let instrumentationTests = testSequenced (testList "Instrumentation" [

  test "SessionManager source name" {
    Instrumentation.sessionSource.Name
    |> Expect.equal "name" "SageFs.SessionManager"
  }
  test "cycle source name" {
    Instrumentation.testCycleSource.Name
    |> Expect.equal "name" "SageFs.TestCycle"
  }
  test "SessionManager meter name" {
    Instrumentation.sessionMeter.Name
    |> Expect.equal "name" "SageFs.SessionManager"
  }
  test "cycle meter name" {
    Instrumentation.testCycleMeter.Name
    |> Expect.equal "name" "SageFs.TestCycle"
  }
  test "counters are not null" {
    Instrumentation.sessionsCreated |> Expect.isNotNull "sessionsCreated"
    Instrumentation.sessionsStopped |> Expect.isNotNull "sessionsStopped"
    Instrumentation.sessionsRestarted |> Expect.isNotNull "sessionsRestarted"
    Instrumentation.standbySwaps |> Expect.isNotNull "standbySwaps"
    Instrumentation.coldRestarts |> Expect.isNotNull "coldRestarts"
    Instrumentation.activeSessions |> Expect.isNotNull "activeSessions"
  }
  test "histogram is not null" {
    Instrumentation.testCycleEndToEnd |> Expect.isNotNull "testCycleEndToEnd"
  }
  test "allSources has expected entries" {
    Instrumentation.allSources |> Expect.hasLength "6 sources" 6
    Instrumentation.allSources |> Expect.contains "has SessionManager" "SageFs.SessionManager"
    Instrumentation.allSources |> Expect.contains "has cycle" "SageFs.TestCycle"
    Instrumentation.allSources |> Expect.contains "has LiveTesting" "SageFs.LiveTesting"
    Instrumentation.allSources |> Expect.contains "has Mcp" "SageFs.Mcp"
    Instrumentation.allSources |> Expect.contains "has ElmLoop" "SageFs.ElmLoop"
    Instrumentation.allSources |> Expect.contains "has Daemon" "SageFs.Daemon"
  }
  test "allMeters has expected entries" {
    Instrumentation.allMeters |> Expect.hasLength "4 meters" 4
  }

  test "traced returns correct value" {
    Instrumentation.traced
      Instrumentation.sessionSource "test.op" []
      (fun () -> 42)
    |> Expect.equal "return value" 42
  }

  test "traced preserves exceptions" {
    Expect.throwsT<System.InvalidOperationException>
      "should rethrow"
      (fun () ->
        Instrumentation.traced
          Instrumentation.sessionSource "test.fail" []
          (fun () -> raise (System.InvalidOperationException "boom"))
        |> ignore)
  }

  test "traced emits activity with tags to listener" {
    let mutable capturedTagKeys : string list = []
    let mutable capturedName = ""
    let listener = new ActivityListener()
    listener.ShouldListenTo <- fun src -> src.Name = "SageFs.SessionManager"
    listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
    listener.ActivityStopped <- fun a ->
      capturedName <- a.DisplayName
      capturedTagKeys <- [ for t in a.TagObjects -> t.Key ]
    ActivitySource.AddActivityListener(listener)

    Instrumentation.traced
      Instrumentation.sessionSource
      "session.create"
      [("session.id", box "abc123")]
      (fun () -> "ok") |> ignore

    listener.Dispose()

    capturedName |> Expect.equal "activity name" "session.create"
    capturedTagKeys |> Expect.contains "has session.id" "session.id"
    capturedTagKeys |> Expect.contains "has duration_ms" "duration_ms"
  }

  test "tracedAsync returns correct value" {
    Instrumentation.tracedAsync
      Instrumentation.testCycleSource "test.async" []
      (fun () -> async { return 99 })
    |> Async.RunSynchronously
    |> Expect.equal "return value" 99
  }

  test "tracedAsync preserves exceptions" {
    Expect.throwsT<System.InvalidOperationException>
      "should rethrow"
      (fun () ->
        Instrumentation.tracedAsync
          Instrumentation.testCycleSource "test.fail" []
          (fun () -> async { return raise (System.InvalidOperationException "boom") })
        |> Async.RunSynchronously
        |> ignore)
  }

  // Span helper tests
  test "startSpan returns null when no listener" {
    // Use a fresh source guaranteed to have no listeners attached
    use freshSource = new ActivitySource("SageFs.Tests.NoListener")
    let span = Instrumentation.startSpan freshSource "test.op" []
    span |> Expect.isNull "should be null with no listener"
  }

  test "succeedSpan handles null activity" {
    Instrumentation.succeedSpan null
  }

  test "failSpan handles null activity" {
    Instrumentation.failSpan null "some error"
  }

  test "startSpan creates activity with tags when listener attached" {
    let mutable captured : Activity option = None
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.SessionManager"),
      Sample = (fun _ -> ActivitySamplingResult.AllData),
      ActivityStopped = (fun a -> captured <- Some a)
    )
    ActivitySource.AddActivityListener(listener)

    let span = Instrumentation.startSpan Instrumentation.sessionSource "test.create" [("session.id", "abc123" :> obj); ("rebuild", true :> obj)]
    span |> Expect.isNotNull "should create activity"
    Instrumentation.succeedSpan span

    captured |> Expect.isSome "should have captured stopped activity"
    let a = captured.Value
    a.OperationName |> Expect.equal "operation name" "test.create"
    let idTag = a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "session.id")
    idTag |> Expect.isSome "should have session.id tag"
    idTag.Value.Value |> Expect.equal "session.id value" ("abc123" :> obj)
  }

  test "failSpan sets error status and tags" {
    let mutable captured : Activity option = None
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.SessionManager"),
      Sample = (fun _ -> ActivitySamplingResult.AllData),
      ActivityStopped = (fun a -> captured <- Some a)
    )
    ActivitySource.AddActivityListener(listener)

    let span = Instrumentation.startSpan Instrumentation.sessionSource "test.fail" []
    Instrumentation.failSpan span "something went wrong"

    captured |> Expect.isSome "should have captured"
    let a = captured.Value
    a.Status |> Expect.equal "should be error status" ActivityStatusCode.Error
    let errTag = a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "error")
    errTag |> Expect.isSome "should have error tag"
    errTag.Value.Value |> Expect.equal "error value" (true :> obj)
    let msgTag = a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "error.message")
    msgTag |> Expect.isSome "should have error.message tag"
    msgTag.Value.Value |> Expect.equal "error.message value" ("something went wrong" :> obj)
  }

  test "tracedAsync emits activity with tags to listener" {
    let mutable capturedTagKeys : string list = []
    let mutable capturedName = ""
    let listener = new ActivityListener()
    listener.ShouldListenTo <- fun src -> src.Name = "SageFs.TestCycle"
    listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
    listener.ActivityStopped <- fun a ->
      capturedName <- a.DisplayName
      capturedTagKeys <- [ for t in a.TagObjects -> t.Key ]
    ActivitySource.AddActivityListener(listener)

    Instrumentation.tracedAsync
      Instrumentation.testCycleSource
      "cycle.run"
      [("trigger", box "file_change")]
      (fun () -> async { return 42 })
    |> Async.RunSynchronously |> ignore

    listener.Dispose()

    capturedName |> Expect.equal "activity name" "cycle.run"
    capturedTagKeys |> Expect.contains "has trigger" "trigger"
    capturedTagKeys |> Expect.contains "has duration_ms" "duration_ms"
  }

  // New Mcp source and meter tests
  test "Mcp source name" {
    Instrumentation.mcpSource.Name
    |> Expect.equal "name" "SageFs.Mcp"
  }
  test "Mcp meter name" {
    Instrumentation.mcpMeter.Name
    |> Expect.equal "name" "SageFs.Mcp"
  }
  test "MCP and FSI counters are not null" {
    Instrumentation.mcpToolInvocations |> Expect.isNotNull "mcpToolInvocations"
    Instrumentation.fsiEvals |> Expect.isNotNull "fsiEvals"
    Instrumentation.fsiStatements |> Expect.isNotNull "fsiStatements"
    Instrumentation.sseConnectionsActive |> Expect.isNotNull "sseConnectionsActive"
  }
  test "cycle histograms are not null" {
    Instrumentation.fcsTypecheckMs |> Expect.isNotNull "fcsTypecheckMs"
    Instrumentation.treeSitterParseMs |> Expect.isNotNull "treeSitterParseMs"
    Instrumentation.testExecutionMs |> Expect.isNotNull "testExecutionMs"
  }
  test "tracedMcpTool creates span with tool name tag" {
    let mutable captured : Activity option = None
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.Mcp"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
      ActivityStopped = (fun a -> if a.OperationName = "mcp.tool.invoke" then captured <- Some a))
    ActivitySource.AddActivityListener(listener)

    let result =
      Instrumentation.tracedMcpTool "send_fsharp_code" "copilot" (fun () ->
        System.Threading.Tasks.Task.FromResult "ok")
      |> fun t -> t.Result

    result |> Expect.equal "should return result" "ok"
    captured |> Expect.isSome "should have captured activity"
    let a = captured.Value
    a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "mcp.tool.name")
    |> Option.map (fun kv -> kv.Value :?> string)
    |> Expect.equal "tool name tag" (Some "send_fsharp_code")
    a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "mcp.agent.name")
    |> Option.map (fun kv -> kv.Value :?> string)
    |> Expect.equal "agent name tag" (Some "copilot")
  }
  test "tracedFsiEval creates span with session and statement tags" {
    let mutable captured : Activity option = None
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.Mcp"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
      ActivityStopped = (fun a -> if a.OperationName = "fsi.eval" then captured <- Some a))
    ActivitySource.AddActivityListener(listener)

    let result =
      Instrumentation.tracedFsiEval "test-agent" 3 "sess-123" (fun () ->
        System.Threading.Tasks.Task.FromResult "evaluated")
      |> fun t -> t.Result

    result |> Expect.equal "should return result" "evaluated"
    captured |> Expect.isSome "should have captured activity"
    let a = captured.Value
    a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "fsi.statement.count")
    |> Option.map (fun kv -> kv.Value :?> int)
    |> Expect.equal "statement count tag" (Some 3)
    a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "fsi.session.id")
    |> Option.map (fun kv -> kv.Value :?> string)
    |> Expect.equal "session id tag" (Some "sess-123")
  }
  test "tracedMcpTool sets error status on exception" {
    let mutable captured : Activity option = None
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.Mcp"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
      ActivityStopped = (fun a -> if a.OperationName = "mcp.tool.invoke" then captured <- Some a))
    ActivitySource.AddActivityListener(listener)

    let threw =
      try
        Instrumentation.tracedMcpTool "bad_tool" "copilot" (fun () ->
          failwith "test error" |> System.Threading.Tasks.Task.FromResult)
        |> fun t -> t.Result |> ignore
        false
      with _ -> true

    threw |> Expect.isTrue "should have thrown"
    captured |> Expect.isSome "should have captured activity"
    captured.Value.Status |> Expect.equal "should be error status" ActivityStatusCode.Error
  }

  // === Tier 1: Worker OTel env var propagation ===
  test "workerOtelEnvVars returns service name with session id" {
    let vars = Instrumentation.workerOtelEnvVars "test-session-42"
    vars |> Expect.isNonEmpty "should have at least service name"
    let keys = vars |> List.map fst
    keys |> Expect.containsAll "should have service name" ["OTEL_SERVICE_NAME"]
    let svcName = vars |> List.find (fun (k,_) -> k = "OTEL_SERVICE_NAME") |> snd
    svcName |> Expect.equal "should include session id" "sagefs-worker-test-session-42"
  }
  test "workerOtelEnvVars includes endpoint when configured" {
    let original = System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    try
      System.Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")
      let vars = Instrumentation.workerOtelEnvVars "sess-1"
      let keys = vars |> List.map fst
      keys |> Expect.containsAll "should have endpoint" ["OTEL_EXPORTER_OTLP_ENDPOINT"; "OTEL_SERVICE_NAME"]
      let ep = vars |> List.find (fun (k,_) -> k = "OTEL_EXPORTER_OTLP_ENDPOINT") |> snd
      ep |> Expect.equal "should propagate endpoint" "http://localhost:4317"
    finally
      System.Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", original)
  }

  // === Tier 1: SSE span filtering ===
  test "sseFilterPaths lists SSE paths to suppress" {
    Instrumentation.sseFilterPaths |> Expect.isNonEmpty "should have paths"
    Instrumentation.sseFilterPaths |> Expect.containsAll "should contain /events, /diagnostics, /health" ["/events"; "/diagnostics"; "/health"]
  }
  test "shouldFilterHttpSpan suppresses SSE paths" {
    Instrumentation.shouldFilterHttpSpan "/events"
    |> Expect.isFalse "should suppress /events"
    Instrumentation.shouldFilterHttpSpan "/diagnostics"
    |> Expect.isFalse "should suppress /diagnostics"
    Instrumentation.shouldFilterHttpSpan "/__sagefs__/reload"
    |> Expect.isFalse "should suppress /__sagefs__/reload"
    Instrumentation.shouldFilterHttpSpan "/health"
    |> Expect.isFalse "should suppress /health"
    Instrumentation.shouldFilterHttpSpan "/api/status"
    |> Expect.isTrue "should NOT suppress /api/status"
    Instrumentation.shouldFilterHttpSpan "/mcp/tool"
    |> Expect.isTrue "should NOT suppress /mcp/tool"
  }

  // === Tier 1: ActivityKind on spans ===
  test "startSpanWithKind creates activity with Server kind" {
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.Mcp"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded))
    ActivitySource.AddActivityListener(listener)
    let act = Instrumentation.startSpanWithKind Instrumentation.mcpSource "test.kind" ActivityKind.Server []
    act |> Expect.isNotNull "should create activity"
    act.Kind |> Expect.equal "should be Server" ActivityKind.Server
    act.Stop(); act.Dispose()
  }
  test "startSpanWithKind supports Producer kind" {
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.Mcp"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded))
    ActivitySource.AddActivityListener(listener)
    let act = Instrumentation.startSpanWithKind Instrumentation.mcpSource "test.producer" ActivityKind.Producer []
    act |> Expect.isNotNull "should create activity"
    act.Kind |> Expect.equal "should be Producer" ActivityKind.Producer
    act.Stop(); act.Dispose()
  }

  // === Tier 2: RPC semantic conventions on MCP spans ===
  test "tracedMcpTool sets RPC semantic convention tags" {
    let mutable captured : Activity option = None
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.Mcp"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
      ActivityStopped = (fun a -> if a.OperationName = "mcp.tool.invoke" then captured <- Some a))
    ActivitySource.AddActivityListener(listener)

    Instrumentation.tracedMcpTool "send_fsharp_code" "copilot" (fun () ->
      System.Threading.Tasks.Task.FromResult "ok")
    |> fun t -> t.Result |> ignore

    captured |> Expect.isSome "should have captured activity"
    let a = captured.Value
    a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "rpc.system")
    |> Option.map (fun kv -> kv.Value :?> string)
    |> Expect.equal "rpc.system = mcp" (Some "mcp")
    a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "rpc.service")
    |> Option.map (fun kv -> kv.Value :?> string)
    |> Expect.equal "rpc.service = sagefs" (Some "sagefs")
    a.TagObjects |> Seq.tryFind (fun kv -> kv.Key = "rpc.method")
    |> Option.map (fun kv -> kv.Value :?> string)
    |> Expect.equal "rpc.method = tool name" (Some "send_fsharp_code")
  }
  test "tracedMcpTool uses ActivityKind.Server" {
    let mutable captured : Activity option = None
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.Mcp"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
      ActivityStopped = (fun a -> if a.OperationName = "mcp.tool.invoke" then captured <- Some a))
    ActivitySource.AddActivityListener(listener)

    Instrumentation.tracedMcpTool "test_tool" "copilot" (fun () ->
      System.Threading.Tasks.Task.FromResult "ok")
    |> fun t -> t.Result |> ignore

    captured |> Expect.isSome "should have captured activity"
    captured.Value.Kind |> Expect.equal "should be Server kind" ActivityKind.Server
  }

  // === Tier 2: Standby pool metrics ===
  test "standbyPoolSize counter exists" {
    Instrumentation.standbyPoolSize |> Expect.isNotNull "standbyPoolSize"
  }
  test "standbyWarmupMs histogram exists" {
    Instrumentation.standbyWarmupMs |> Expect.isNotNull "standbyWarmupMs"
  }
  test "standbyInvalidations counter exists" {
    Instrumentation.standbyInvalidations |> Expect.isNotNull "standbyInvalidations"
  }
  test "standbyAgeAtSwapMs histogram exists" {
    Instrumentation.standbyAgeAtSwapMs |> Expect.isNotNull "standbyAgeAtSwapMs"
  }

  // === Tier 2: File watcher counter ===
  test "fileWatcherChanges counter exists" {
    Instrumentation.fileWatcherChanges |> Expect.isNotNull "fileWatcherChanges"
  }

  // === Tier 3 P0: EventStore retry envelope metrics ===
  test "eventstoreAppendRetries counter exists" {
    Instrumentation.eventstoreAppendRetries |> Expect.isNotNull "eventstoreAppendRetries"
  }
  test "eventstoreAppendDurationMs histogram exists" {
    Instrumentation.eventstoreAppendDurationMs |> Expect.isNotNull "eventstoreAppendDurationMs"
  }
  test "eventstoreAppendFailures counter exists" {
    Instrumentation.eventstoreAppendFailures |> Expect.isNotNull "eventstoreAppendFailures"
  }

  // === Tier 3 P0: Daemon startup metrics ===
  test "daemonStartupMs histogram exists" {
    Instrumentation.daemonStartupMs |> Expect.isNotNull "daemonStartupMs"
  }
  test "daemonReplayEventCount counter exists" {
    Instrumentation.daemonReplayEventCount |> Expect.isNotNull "daemonReplayEventCount"
  }
  test "daemonSessionsResumed counter exists" {
    Instrumentation.daemonSessionsResumed |> Expect.isNotNull "daemonSessionsResumed"
  }
  test "daemonDuplicatesPruned counter exists" {
    Instrumentation.daemonDuplicatesPruned |> Expect.isNotNull "daemonDuplicatesPruned"
  }

  // === Tier 3 P1: Elm loop metrics ===
  test "elmloopUpdateMs histogram exists" {
    Instrumentation.elmloopUpdateMs |> Expect.isNotNull "elmloopUpdateMs"
  }
  test "elmloopRenderMs histogram exists" {
    Instrumentation.elmloopRenderMs |> Expect.isNotNull "elmloopRenderMs"
  }
  test "elmloopCallbackMs histogram exists" {
    Instrumentation.elmloopCallbackMs |> Expect.isNotNull "elmloopCallbackMs"
  }
  test "elmloopEffectsSpawned counter exists" {
    Instrumentation.elmloopEffectsSpawned |> Expect.isNotNull "elmloopEffectsSpawned"
  }

  // === Tier 3 P1: LiveTesting additions ===
  test "liveTestingDiscoveryMs histogram exists" {
    Instrumentation.liveTestingDiscoveryMs |> Expect.isNotNull "liveTestingDiscoveryMs"
  }
  test "liveTestingAssemblyLoadErrors counter exists" {
    Instrumentation.liveTestingAssemblyLoadErrors |> Expect.isNotNull "liveTestingAssemblyLoadErrors"
  }

  // === Tier 3 P2: DevReload connected clients ===
  test "devReloadConnectedClients updown counter exists" {
    Instrumentation.devReloadConnectedClients |> Expect.isNotNull "devReloadConnectedClients"
  }

  // === tracedTask helper ===
  test "tracedTask returns correct value" {
    Instrumentation.tracedTask
      Instrumentation.sessionSource "test.task" []
      (fun () -> System.Threading.Tasks.Task.FromResult 42)
    |> fun t -> t.Result
    |> Expect.equal "return value" 42
  }
  test "tracedTask preserves exceptions" {
    let threw =
      try
        Instrumentation.tracedTask
          Instrumentation.sessionSource "test.fail" []
          (fun () -> failwith "boom" |> System.Threading.Tasks.Task.FromResult)
        |> fun t -> t.Result |> ignore
        false
      with _ -> true
    threw |> Expect.isTrue "should have thrown"
  }
  test "tracedTask emits activity with tags when listener attached" {
    let mutable capturedName = ""
    let mutable capturedTagKeys : string list = []
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.SessionManager"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
      ActivityStopped = (fun a ->
        if a.OperationName = "daemon.startup.test" then
          capturedName <- a.DisplayName
          capturedTagKeys <- [ for t in a.TagObjects -> t.Key ]))
    ActivitySource.AddActivityListener(listener)

    Instrumentation.tracedTask
      Instrumentation.sessionSource "daemon.startup.test"
      [("event_count", box 42)]
      (fun () -> System.Threading.Tasks.Task.FromResult "ok")
    |> fun t -> t.Result |> ignore

    capturedName |> Expect.equal "activity name" "daemon.startup.test"
    capturedTagKeys |> Expect.contains "has event_count" "event_count"
    capturedTagKeys |> Expect.contains "has duration_ms" "duration_ms"
  }

  // === Phase 3.2: W3C TraceContext parsing and formatting ===
  test "formatTraceparent produces W3C format" {
    let header: Instrumentation.TraceparentHeader = {
      version = 0uy
      traceId = "0af7651916cd43dd8448eb211c80319c"
      spanId = "b7ad6b7169203331"
      flags = 1uy
    }
    Instrumentation.formatTraceparent header
    |> Expect.equal "should be W3C format" "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
  }
  test "parseTraceparent roundtrips with formatTraceparent" {
    let original = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
    match Instrumentation.parseTraceparent original with
    | Ok parsed ->
      Instrumentation.formatTraceparent parsed
      |> Expect.equal "should roundtrip" original
    | Error e -> failwith (sprintf "parse failed: %s" e)
  }
  test "parseTraceparent extracts correct fields" {
    match Instrumentation.parseTraceparent "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00" with
    | Ok h ->
      h.version |> Expect.equal "version" 0uy
      h.traceId |> Expect.equal "traceId" "4bf92f3577b34da6a3ce929d0e0e4736"
      h.spanId |> Expect.equal "spanId" "00f067aa0ba902b7"
      h.flags |> Expect.equal "flags" 0uy
    | Error e -> failwith (sprintf "parse failed: %s" e)
  }
  test "parseTraceparent rejects too few segments" {
    match Instrumentation.parseTraceparent "00-abc-def" with
    | Error _ -> ()
    | Ok _ -> failwith "should reject invalid format"
  }
  test "parseTraceparent rejects invalid traceId length" {
    match Instrumentation.parseTraceparent "00-0af765-b7ad6b7169203331-01" with
    | Error e ->
      e |> Expect.stringContains "should mention traceId" "traceId"
    | Ok _ -> failwith "should reject short traceId"
  }
  test "parseTraceparent rejects invalid spanId length" {
    match Instrumentation.parseTraceparent "00-0af7651916cd43dd8448eb211c80319c-b7ad-01" with
    | Error e ->
      e |> Expect.stringContains "should mention spanId" "spanId"
    | Ok _ -> failwith "should reject short spanId"
  }
  test "parseTraceparent rejects all-zero traceId" {
    match Instrumentation.parseTraceparent "00-00000000000000000000000000000000-b7ad6b7169203331-01" with
    | Error e ->
      e |> Expect.stringContains "should mention zero" "zero"
    | Ok _ -> failwith "should reject all-zero traceId"
  }
  test "parseTraceparent rejects all-zero spanId" {
    match Instrumentation.parseTraceparent "00-0af7651916cd43dd8448eb211c80319c-0000000000000000-01" with
    | Error e ->
      e |> Expect.stringContains "should mention zero" "zero"
    | Ok _ -> failwith "should reject all-zero spanId"
  }
  test "workerOtelEnvVars includes valid traceparent when Activity is active" {
    use listener = new ActivityListener(
      ShouldListenTo = (fun s -> s.Name = "SageFs.Mcp"),
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded))
    ActivitySource.AddActivityListener(listener)
    use act = Instrumentation.mcpSource.StartActivity("test.trace.propagation")
    let vars = Instrumentation.workerOtelEnvVars "trace-test"
    let tpOpt = vars |> List.tryFind (fun (k,_) -> k = "TRACEPARENT") |> Option.map snd
    tpOpt |> Expect.isSome "should have TRACEPARENT"
    match Instrumentation.parseTraceparent tpOpt.Value with
    | Ok h ->
      h.version |> Expect.equal "version 00" 0uy
      h.traceId |> Expect.equal "traceId matches activity" (act.TraceId.ToHexString())
      h.spanId |> Expect.equal "spanId matches activity" (act.SpanId.ToHexString())
    | Error e -> failwith (sprintf "invalid traceparent: %s" e)
  }
  testProperty "formatTraceparent >> parseTraceparent roundtrips" <| fun (version: byte) (flags: byte) ->
    // Generate valid hex strings of correct length
    let rng = System.Random(int version + int flags)
    let hexChar () = "0123456789abcdef".[rng.Next(16)]
    let traceId = System.String(Array.init 32 (fun _ -> hexChar()))
    let spanId = System.String(Array.init 16 (fun _ -> hexChar()))
    // Ensure not all-zero
    let traceId = match traceId with t when t = System.String('0', 32) -> "0000000000000000000000000000000a" | t -> t
    let spanId = match spanId with s when s = System.String('0', 16) -> "000000000000000a" | s -> s
    let header: Instrumentation.TraceparentHeader = {
      version = version
      traceId = traceId
      spanId = spanId
      flags = flags
    }
    let formatted = Instrumentation.formatTraceparent header
    match Instrumentation.parseTraceparent formatted with
    | Ok parsed ->
      parsed.traceId = traceId
      && parsed.spanId = spanId
      && parsed.flags = flags
    | Error _ -> false
])
