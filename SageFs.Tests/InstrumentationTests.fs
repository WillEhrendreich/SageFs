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
  test "Pipeline source name" {
    Instrumentation.pipelineSource.Name
    |> Expect.equal "name" "SageFs.Pipeline"
  }
  test "SessionManager meter name" {
    Instrumentation.sessionMeter.Name
    |> Expect.equal "name" "SageFs.SessionManager"
  }
  test "Pipeline meter name" {
    Instrumentation.pipelineMeter.Name
    |> Expect.equal "name" "SageFs.Pipeline"
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
    Instrumentation.pipelineEndToEnd |> Expect.isNotNull "pipelineEndToEnd"
  }
  test "allSources has expected entries" {
    Instrumentation.allSources |> Expect.hasLength "5 sources" 5
    Instrumentation.allSources |> Expect.contains "has SessionManager" "SageFs.SessionManager"
    Instrumentation.allSources |> Expect.contains "has Pipeline" "SageFs.Pipeline"
    Instrumentation.allSources |> Expect.contains "has LiveTesting" "SageFs.LiveTesting"
    Instrumentation.allSources |> Expect.contains "has Mcp" "SageFs.Mcp"
    Instrumentation.allSources |> Expect.contains "has Marten" "Marten"
  }
  test "allMeters has expected entries" {
    Instrumentation.allMeters |> Expect.hasLength "5 meters" 5
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
      Instrumentation.pipelineSource "test.async" []
      (fun () -> async { return 99 })
    |> Async.RunSynchronously
    |> Expect.equal "return value" 99
  }

  test "tracedAsync preserves exceptions" {
    Expect.throwsT<System.InvalidOperationException>
      "should rethrow"
      (fun () ->
        Instrumentation.tracedAsync
          Instrumentation.pipelineSource "test.fail" []
          (fun () -> async { return raise (System.InvalidOperationException "boom") })
        |> Async.RunSynchronously
        |> ignore)
  }

  // Span helper tests
  test "startSpan returns null when no listener" {
    let span = Instrumentation.startSpan Instrumentation.sessionSource "test.op" []
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
    listener.ShouldListenTo <- fun src -> src.Name = "SageFs.Pipeline"
    listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
    listener.ActivityStopped <- fun a ->
      capturedName <- a.DisplayName
      capturedTagKeys <- [ for t in a.TagObjects -> t.Key ]
    ActivitySource.AddActivityListener(listener)

    Instrumentation.tracedAsync
      Instrumentation.pipelineSource
      "pipeline.run"
      [("trigger", box "file_change")]
      (fun () -> async { return 42 })
    |> Async.RunSynchronously |> ignore

    listener.Dispose()

    capturedName |> Expect.equal "activity name" "pipeline.run"
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
  test "pipeline histograms are not null" {
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
    Instrumentation.sseFilterPaths |> Expect.containsAll "should contain /events and /diagnostics" ["/events"; "/diagnostics"]
  }
  test "shouldFilterHttpSpan suppresses SSE paths" {
    Instrumentation.shouldFilterHttpSpan "/events"
    |> Expect.isFalse "should suppress /events"
    Instrumentation.shouldFilterHttpSpan "/diagnostics"
    |> Expect.isFalse "should suppress /diagnostics"
    Instrumentation.shouldFilterHttpSpan "/__sagefs__/reload"
    |> Expect.isFalse "should suppress /__sagefs__/reload"
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
])
