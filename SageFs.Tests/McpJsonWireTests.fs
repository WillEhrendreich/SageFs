/// What the MCP and HTTP surfaces write as JSON, pinned as exact text. These payloads used
/// to be written by `System.Text.Json` directly, with no F# converter, so a union or an
/// option inside one wrote the native form on .NET 11 and threw on .NET 10. They now go
/// through `SageFs.Json`. Each test names the production function that writes the text and
/// asserts the whole string, so a change in key names, key order, layout or nulls fails here.
/// The suite runs in the net11 tier and the net10 tier.
module SageFs.Tests.McpJsonWireTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.WorkerProtocol


let private lf (text: string) : string = text.Replace("\r\n", "\n")

/// The default JSON encoder writes an apostrophe and any non-ASCII character as \uXXXX.
/// The expected text below spells that as \\uXXXX so this file holds no unicode escapes of
/// its own, and `equalJson` turns it back before comparing.
let private equalJson (message: string) (expected: string) (actual: string) : unit =
  Expect.equal message (expected.Replace(@"\\u", @"\u")) actual

let private proxyReturning (response: WorkerResponse) : SessionProxy = fun _ -> async { return response }

let private infoWith (sid: string) (status: SessionLifecycleStatus) (workflow: WorkflowTypes.SessionWorkflow) : SessionInfo =
  { Id = SageFs.McpSessionRouting.toSessionId sid
    Name = None
    Projects = []
    WorkingDirectory = "/mcp-json-wire/" + sid
    SolutionRoot = None
    Status = status
    Workflow = workflow
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }

let private ctxFor (infos: SessionInfo list) (proxy: SessionProxy option) (switchResult: Result<string, SageFsError>) : McpContext =
  let ops : SessionManagementOps =
    { SessionManagementOps.stub with
        GetProxy = fun _ -> Task.FromResult proxy
        GetSessionInfo = fun id -> Task.FromResult (infos |> List.tryFind (fun i -> i.Id = id))
        GetAllSessions = fun () -> Task.FromResult infos
        SwitchWorkflow = fun _ _ -> Task.FromResult switchResult }
  { FrictionStore = None
    DiagnosticsChanged = (Event<SageFs.Features.DiagnosticsStore.T>()).Publish
    StateChanged = None
    SessionOps = ops
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortOwner = None
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

/// A payload with its per-run fields removed, so what is left can be compared as text.
let private without (volatileKeys: string list) (text: string) : string =
  let node = JsonNode.Parse text :?> JsonObject
  for key in volatileKeys do
    node.Remove key |> ignore
  node.ToJsonString()

let private readyHandle : WorkerHandle = { Pid = 4242; Port = Some 45000 }
let private noProxy : SessionProxy option = None

[<Tests>]
let tests =
  testList "MCP and HTTP JSON wire" [

    // ---- SageFs/Mcp.fs: session status shapes that are not the routable one ----

    testTask "WHY — get_session_status with no session writes the NoSession shape, keys as written" {
      let ctx = ctxFor [] noProxy (Ok "")
      let! text = getSessionStatus ctx "agent" None (Some "/mcp-json-wire/none")
      text
      |> equalJson "NoSession payload"
           """{"message":"No sessions match workingDirectory \\u0027/mcp-json-wire/none\\u0027. Running sessions: (none running). Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session for that directory, or switch_session to an existing matching session.","scope":"Session","state":"NoSession","wait":{"outcome":"NotNeeded","waitedMs":0}}"""
    }

    testTask "WHY — a warming session writes the WarmingUp shape with its target list and null reload fields" {
      let info = infoWith "aa11bb22" (SessionLifecycleStatus.Starting readyHandle) WorkflowTypes.SessionWorkflow.Interactive
      let ctx = ctxFor [ info ] noProxy (Ok "")
      let! text = getSessionStatus ctx "agent" (Some "aa11bb22") None
      text
      |> Expect.equal "WarmingUp payload"
           """{"available":["get_session_status","get_recent_fsi_events","get_friction_report","get_available_projects","list_sessions","switch_session","create_project_session","create_solution_session","create_bare_session","acquire_full_build_lease","acquire_test_suite_lease","acquire_run_app_lease","release_work_lease","decompose_pipeline"],"lastReload":null,"lastRestart":null,"lifecycle":"Starting","loadedProjects":[],"replFreshness":{"state":"InSync"},"scope":"Session","sessionId":"aa11bb22","state":"WarmingUp","target":[{"kind":"Bare","path":null}],"workerPid":4242,"workerPort":45000,"wait":{"outcome":"NotNeeded","waitedMs":0}}"""
    }

    testTask "WHY — a faulted session writes the Faulted shape with the reason" {
      let info = infoWith "cc33dd44" (SessionLifecycleStatus.Faulted (FaultReason.Reported "warmup failed")) WorkflowTypes.SessionWorkflow.Interactive
      let ctx = ctxFor [ info ] noProxy (Ok "")
      let! text = getSessionStatus ctx "agent" (Some "cc33dd44") None
      text
      |> Expect.equal "Faulted payload"
           """{"available":["get_session_status","get_recent_fsi_events","get_friction_report","get_available_projects","list_sessions","switch_session","create_project_session","create_solution_session","create_bare_session","acquire_full_build_lease","acquire_test_suite_lease","acquire_run_app_lease","release_work_lease","reset_fsi_session","hard_reset_fsi_session","decompose_pipeline"],"faultReason":"warmup failed","lastReload":null,"lastRestart":null,"loadedProjects":[],"replFreshness":{"state":"InSync"},"scope":"Session","sessionId":"cc33dd44","state":"Faulted","target":[{"kind":"Bare","path":null}],"wait":{"outcome":"NotNeeded","waitedMs":0}}"""
    }

    testTask "WHY — a ready session with no worker proxy is reported as WarmingUp, lifecycle as it is" {
      let info = infoWith "ee55ff66" (SessionLifecycleStatus.Ready readyHandle) WorkflowTypes.SessionWorkflow.Interactive
      let ctx = ctxFor [ info ] noProxy (Ok "")
      let! text = getSessionStatus ctx "agent" (Some "ee55ff66") None
      text
      |> Expect.equal "unroutable payload"
           """{"available":["get_session_status","get_recent_fsi_events","get_friction_report","get_available_projects","list_sessions","switch_session","create_project_session","create_solution_session","create_bare_session","acquire_full_build_lease","acquire_test_suite_lease","acquire_run_app_lease","release_work_lease","decompose_pipeline"],"lastReload":null,"lastRestart":null,"lifecycle":"Ready","loadedProjects":[],"replFreshness":{"state":"InSync"},"scope":"Session","sessionId":"ee55ff66","state":"WarmingUp","target":[{"kind":"Bare","path":null}],"workerPid":4242,"workerPort":45000,"wait":{"outcome":"NotNeeded","waitedMs":0}}"""
    }

    testTask "WHY — renderWarmingOrFaulted writes each of its four shapes" {
      let warming = infoWith "a0a0a0a0" (SessionLifecycleStatus.Starting readyHandle) WorkflowTypes.SessionWorkflow.Interactive
      let ctx = ctxFor [ warming ] noProxy (Ok "")
      let! warmingText =
        renderWarmingOrFaulted ctx (SessionResolution.WarmingUp ("a0a0a0a0", warming.Status))
      let! faultedText =
        renderWarmingOrFaulted ctx (SessionResolution.FaultedSession ("a0a0a0a0", McpSessionRouting.FaultCause.Recorded "warmup failed"))
      let! defensiveText = renderWarmingOrFaulted ctx (SessionResolution.Routable "a0a0a0a0")
      let! goneText = renderWarmingOrFaulted ctx (SessionResolution.Gone "no such session")
      [ warmingText |> without [ "elapsedSeconds" ]; faultedText; defensiveText; goneText ]
      |> String.concat "\n"
      |> equalJson "the four shapes"
           ("""{"available":["get_session_status","get_recent_fsi_events","get_friction_report","get_available_projects","list_sessions","switch_session","create_project_session","create_solution_session","create_bare_session","acquire_full_build_lease","acquire_test_suite_lease","acquire_run_app_lease","release_work_lease","decompose_pipeline"],"boundSeconds":720,"inactivityBoundSeconds":30,"machineTier":"Fast","message":"Session \\u0027a0a0a0a0\\u0027 is still warming up (Starting). This typically takes 15-30s for test projects. Call get_session_status with wait_seconds=60 to wait for readiness; do not sleep or poll. Do NOT create a new session \\u2014 it will compete for resources and make warmup slower.","progress":null,"sessionId":"a0a0a0a0","state":"Rebuilding","status":"Starting"}""" + "\n"
            + """{"available":["get_session_status","get_recent_fsi_events","get_friction_report","get_available_projects","list_sessions","switch_session","create_project_session","create_solution_session","create_bare_session","acquire_full_build_lease","acquire_test_suite_lease","acquire_run_app_lease","release_work_lease","reset_fsi_session","hard_reset_fsi_session","decompose_pipeline"],"faultReason":"warmup failed","machineTier":"Fast","message":"Session \\u0027a0a0a0a0\\u0027 is faulted. Why: warmup failed\nRun reset_fsi_session or hard_reset_fsi_session to recover.","sessionId":"a0a0a0a0","state":"Faulted"}""" + "\n"
            + """{"message":"","sessionId":"a0a0a0a0","state":"Rebuilding"}""" + "\n"
            + """{"message":"no such session","state":"NoSession"}""")
    }

    // ---- SageFs/Mcp.fs: daemon status, leases, workflow switch, domain model, test trace ----

    testTask "WHY — get_daemon_status writes the same keys in the same order with its per-run values taken out" {
      let ctx = ctxFor [] noProxy (Ok "")
      let! text = getDaemonStatus ctx
      text
      |> without [ "daemonVersion"; "coreVersion"; "daemonPid"; "telemetrySampledAt"; "machineMemory"; "leases" ]
      |> Expect.equal "daemon status"
           """{"aggregateCpuPercent":0,"aggregateResidentBytes":0,"anomalies":[],"available":["get_daemon_status","get_session_status","get_friction_report","get_available_projects","list_sessions","switch_session","create_project_session","create_solution_session","create_bare_session","hard_reset_fsi_session","acquire_full_build_lease","acquire_test_suite_lease","acquire_run_app_lease","release_work_lease","decompose_pipeline"],"daemonResidentBytes":0,"mcpPort":0,"memoryPressure":"normal","memoryPressureNote":"","overall":"Unknown","processes":[],"scope":"Daemon","sessions":{"evaluating":0,"faulted":0,"ready":0,"stopped":0,"total":0,"warmingUp":0},"state":"Ready","uptimeSeconds":0}"""
    }

    testCase "WHY — a lease decision writes kind and decision, then its detail" <| fun _ ->
      let holder = "mcp-json-wire-holder"
      let granted = acquireWorkLease holder SageFs.ExpensiveWorkLease.Kind.TestSuiteRun
      let grantedNode = JsonNode.Parse granted :?> JsonObject
      let leaseId = grantedNode["leaseId"].GetValue<string>()
      try
        let second = acquireWorkLease holder SageFs.ExpensiveWorkLease.Kind.TestSuiteRun
        [ granted |> without [ "leaseId"; "expiresAt" ]; second |> without [ "retryAfterSeconds" ] ]
        |> String.concat "\n"
        |> equalJson "granted, then the second ask"
             ("""{"decision":"granted","kind":"test_suite_run"}""" + "\n"
              + """{"decision":"refused","kind":"test_suite_run","reason":"you already hold 1/1 leases \\u2014 release one before requesting another"}""")
      finally
        releaseWorkLease holder leaseId |> ignore

    testTask "WHY — a dry-run workflow switch writes the indented outcome with a null NewSessionId" {
      let info = infoWith "11223344" (SessionLifecycleStatus.Ready readyHandle) WorkflowTypes.SessionWorkflow.Interactive
      let ctx = ctxFor [ info ] (Some (proxyReturning (WorkerResponse.CheckResult ("r", [])))) (Ok "restarting")
      let! text = switchWorkflow ctx "agent" (Some info.WorkingDirectory) "livetesting" true
      lf text
      |> Expect.equal "dry run"
           ("{\n"
            + "  \"Cost\": {\n"
            + "    \"DefinitionsLost\": 0,\n"
            + "    \"CellsLost\": 0,\n"
            + "    \"EstimatedRestart\": \"00:00:15\"\n"
            + "  },\n"
            + "  \"Message\": \"Preview: switching from REPL to Live Testing would lose 0 definitions and 0 cells\",\n"
            + "  \"NewSessionId\": null,\n"
            + "  \"Outcome\": \"dryRunPreview\",\n"
            + "  \"PreviousWorkflow\": \"REPL\",\n"
            + "  \"Switched\": false,\n"
            + "  \"TargetWorkflow\": \"Live Testing\"\n"
            + "}")
    }

    testTask "WHY — an executed workflow switch writes the new session id" {
      let info = infoWith "55667788" (SessionLifecycleStatus.Ready readyHandle) WorkflowTypes.SessionWorkflow.Interactive
      let ctx = ctxFor [ info ] (Some (proxyReturning (WorkerResponse.CheckResult ("r", [])))) (Ok "restarting")
      let! text = switchWorkflow ctx "agent" (Some info.WorkingDirectory) "livetesting" false
      lf text
      |> Expect.equal "executed"
           ("{\n"
            + "  \"Cost\": {\n"
            + "    \"DefinitionsLost\": 0,\n"
            + "    \"CellsLost\": 0,\n"
            + "    \"EstimatedRestart\": \"00:00:15\"\n"
            + "  },\n"
            + "  \"Message\": \"Switched from REPL to Live Testing (new session: 55667788)\",\n"
            + "  \"NewSessionId\": \"55667788\",\n"
            + "  \"Outcome\": \"executed\",\n"
            + "  \"PreviousWorkflow\": \"REPL\",\n"
            + "  \"Switched\": true,\n"
            + "  \"TargetWorkflow\": \"Live Testing\"\n"
            + "}")
    }

    testTask "WHY — visualize_domain_model writes the indented state machine, field pairs included" {
      let info = infoWith "99aabbcc" (SessionLifecycleStatus.Ready readyHandle) WorkflowTypes.SessionWorkflow.Interactive
      let evalReply = WorkerResponse.EvalResult ("r", Ok "DUCASES:Idle|;Busy|count:Int32,label:String", [], Map.empty)
      let ctx = ctxFor [ info ] (Some (proxyReturning evalReply)) (Ok "")
      let! text = visualizeDomainModel ctx "agent" "Thing" (Some info.WorkingDirectory)
      lf text
      |> Expect.equal "state machine data"
           ("{\n"
            + "  \"AsciiDiagram\": \"                \\u250C\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2510    \\u250C\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2510\\n                \\u2502  Idle  \\u2502    \\u2502  Busy  \\u2502\\n                \\u2514\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2518    \\u2514\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2500\\u2518\",\n"
            + "  \"States\": [\n"
            + "    {\n"
            + "      \"Fields\": [],\n"
            + "      \"IsEntry\": true,\n"
            + "      \"IsTerminal\": true,\n"
            + "      \"Name\": \"Idle\"\n"
            + "    },\n"
            + "    {\n"
            + "      \"Fields\": [\n"
            + "        [\n"
            + "          \"count\",\n"
            + "          \"Int32\"\n"
            + "        ],\n"
            + "        [\n"
            + "          \"label\",\n"
            + "          \"String\"\n"
            + "        ]\n"
            + "      ],\n"
            + "      \"IsEntry\": true,\n"
            + "      \"IsTerminal\": true,\n"
            + "      \"Name\": \"Busy\"\n"
            + "    }\n"
            + "  ],\n"
            + "  \"Transitions\": [],\n"
            + "  \"TypeName\": \"Thing\"\n"
            + "}")
    }

    testTask "WHY — get_test_trace writes the live-testing trace for an empty model, keys as written" {
      let ctx = { ctxFor [] noProxy (Ok "") with GetElmModel = Some (fun () -> SageFsModel.initial ()) }
      let! text = getTestTrace ctx
      text
      |> Expect.equal "trace"
           """{"DiscoveryHint":"Live testing is not active. Call enable_live_testing to start discovery.","DiscoveryRequiresEval":false,"DiscoveryState":"disabled","Enabled":false,"Hint":"Live testing is not active. Call enable_live_testing to start test discovery and automatic re-runs.","History":{"Case":"NeverRun"},"IsRunning":false,"LastDecision":null,"LastDiscoveryTime":null,"Policies":["Unit: OnEveryChange","Integration: OnDemand","Browser: OnDemand","Benchmark: OnDemand","Architecture: OnSaveOnly","Property: OnEveryChange"],"Providers":[],"Summary":{"Total":0,"Passed":0,"Failed":0,"Stale":0,"Running":0,"Disabled":0,"Enabled":false},"Timing":"no timing yet"}"""
    }

    // ---- SageFs/McpAdapter.fs ----

    testCase "WHY — formatEvalResultJson writes lower-case keys, omits null result, error and stdout" <| fun _ ->
      let ok : AppState.EvalResponse =
        { EvaluationResult = Ok "val x: int = 42"
          Diagnostics = [||]
          EvaluatedCode = "let x = 42;;"
          Metadata = Map.empty }
      let failed : AppState.EvalResponse =
        { EvaluationResult = Error (Exception "boom")
          Diagnostics =
            [| { Message = "Undefined value"
                 Subcategory = "typecheck"
                 Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 3 }
                 Severity = Features.Diagnostics.DiagnosticSeverity.Blocking
                 ErrorNumber = 39 } |]
          EvaluatedCode = "foo;;"
          Metadata = Map.ofList [ "stdout", box "hello" ] }
      [ McpAdapter.formatEvalResultJson ok; McpAdapter.formatEvalResultJson failed ]
      |> String.concat "\n"
      |> Expect.equal "success, then failure with stdout and a diagnostic"
           ("""{"success":true,"result":"val x: int = 42","diagnostics":[],"code":"let x = 42;;"}""" + "\n"
            + """{"success":false,"error":"boom","stdout":"hello","diagnostics":[{"severity":"error","message":"Undefined value","startLine":1,"startColumn":0,"endLine":1,"endColumn":3}],"code":"foo;;"}""")

    testCase "WHY — formatStartupInfoJson writes the indented startup record, a missing profile as null" <| fun _ ->
      let config : AppState.StartupConfig =
        { CommandLineArgs = [| "--mcp-port"; "8080" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = "/w"
          Workflow = WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults
          AutoOpenNamespaces = true
          AspireDetected = false
          StartupTimestamp = DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
          StartupProfileLoaded = None }
      McpAdapter.formatStartupInfoJson config
      |> lf
      |> Expect.equal "startup info"
           ("{\n"
            + "  \"aspireDetected\": false,\n"
            + "  \"commandLineArgs\": [\n"
            + "    \"--mcp-port\",\n"
            + "    \"8080\"\n"
            + "  ],\n"
            + "  \"hotReloadEnabled\": true,\n"
            + "  \"loadedProjects\": [\n"
            + "    \"Test.fsproj\"\n"
            + "  ],\n"
            + "  \"startupProfileLoaded\": null,\n"
            + "  \"startupTimestamp\": \"2026-01-02T03:04:05.0000000Z\",\n"
            + "  \"workingDirectory\": \"/w\"\n"
            + "}")

    testCase "WHY — formatDiagnosticsStoreAsJson writes one entry per code hash, keys as written in camel case" <| fun _ ->
      let store =
        Features.DiagnosticsStore.empty
        |> Features.DiagnosticsStore.add "let x = 1"
             [| { Message = "test warning"
                  Subcategory = "typecheck"
                  Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
                  Severity = Features.Diagnostics.DiagnosticSeverity.Warning
                  ErrorNumber = 0 } |]
      // The code hash is string.GetHashCode, which is different in every process.
      System.Text.RegularExpressions.Regex.Replace(McpAdapter.formatDiagnosticsStoreAsJson store, "\"codeHash\":\"[0-9a-f]+\"", "\"codeHash\":\"HASH\"")
      |> Expect.equal "diagnostics store"
           """[{"codeHash":"HASH","diagnostics":[{"message":"test warning","range":{"endColumn":5,"endLine":1,"startColumn":0,"startLine":1},"severity":"warning"}]}]"""

    testCase "WHY — formatEvalStructuredError writes case, fields, message and suggestedAction" <| fun _ ->
      McpAdapter.formatEvalStructuredError (SageFsError.EvalFailed "type mismatch")
      |> Expect.equal "structured error"
           """{"case":"EvalFailed","fields":{"reason":"type mismatch"},"message":"Evaluation failed: type mismatch","suggestedAction":"Fix the code and resubmit"}"""

    // ---- SageFs/McpServer.fs ----

    testCase "WHY — structuredToolErrorResult carries the error triple as structured content" <| fun _ ->
      let result = SageFs.Server.McpServer.structuredToolErrorResult (SageFsError.SessionNotFound "abc12345")
      result.StructuredContent.Value.GetRawText()
      |> equalJson "structured content"
           """{"case":"SessionNotFound","fields":{"sessionId":"abc12345"},"message":"Session \\u0027abc12345\\u0027 not found. Use list_sessions to see available sessions.","suggestedAction":"Run list_sessions to see available sessions"}"""

    testTask "WHY — jsonResponse writes an anonymous record compactly, keys as written, options as value or null" {
      let http = Microsoft.AspNetCore.Http.DefaultHttpContext()
      use body = new MemoryStream()
      http.Response.Body <- body
      do!
        SageFs.Server.McpServer.jsonResponse http 200
          {| success = true
             message = "done"
             count = 3
             ratio = 0.5
             faultReason = Some "why"
             nothing = (None: string option)
             tags = [ "a"; "b" ]
             nested = {| ok = false |}
             at = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) |}
      System.Text.Encoding.UTF8.GetString(body.ToArray())
      |> Expect.equal "response body"
           """{"at":"2026-01-02T03:04:05+00:00","count":3,"faultReason":"why","message":"done","nested":{"ok":false},"nothing":null,"ratio":0.5,"success":true,"tags":["a","b"]}"""
    }

    testTask "WHY — the file-annotations route answers with the annotations as a JSON object, not as a string holding JSON" {
      let http = Microsoft.AspNetCore.Http.DefaultHttpContext()
      use body = new MemoryStream()
      http.Response.Body <- body
      let cycle = SageFs.Features.LiveTesting.LiveTestCycleState.empty
      do! SageFs.Server.McpServer.writeFileAnnotations http cycle "NoSuch.fs"
      let written = System.Text.Encoding.UTF8.GetString(body.ToArray())
      let rootKind =
        use parsed = System.Text.Json.JsonDocument.Parse written
        parsed.RootElement.ValueKind
      rootKind |> Expect.equal "a client reads an object straight off the wire" System.Text.Json.JsonValueKind.Object
      written
      |> Expect.equal "the body is exactly what the annotations serialize to"
           (Json.serialize Json.standard (SageFs.Features.LiveTesting.FileAnnotations.empty "NoSuch.fs"))
      http.Response.ContentType |> Expect.equal "it says it is JSON" "application/json"
    }

    // ---- SageFs/McpStdioBridge.fs ----

    testCase "WHY — a rejected request is answered with a JSON-RPC error whose id and message are JSON strings" <| fun _ ->
      let rejection (id: McpBridge.RpcId) (reason: string) : string =
        use stream = new MemoryStream()
        SageFs.Server.McpStdioBridge.writeRejection
          (SageFs.Server.McpStdioBridge.StdoutWriter stream)
          (McpBridge.RpcMessage.Request (id, "tools/call", "{}"))
          reason
        System.Text.Encoding.UTF8.GetString(stream.ToArray())
      [ rejection (McpBridge.RpcId.S "id \"1\"") "daemon said \"no\" é"; rejection (McpBridge.RpcId.N 7L) "no" ]
      |> String.concat ""
      |> Expect.equal "a string id, then a numeric id"
           ("{\"jsonrpc\":\"2.0\",\"id\":\"id \\u00221\\u0022\",\"error\":{\"code\":-32000,\"message\":\"daemon said \\u0022no\\u0022 \\u00E9\"}}\n"
            + """{"jsonrpc":"2.0","id":7,"error":{"code":-32000,"message":"no"}}""" + "\n")

    // ---- SageFs/McpTools.fs: the bodies the tools post to a session's worker ----

    testTask "WHY — set_reflection_read_mode and reset_hot_reload_state post a one-key JSON object to the worker" {
      let listener = new System.Net.HttpListener()
      let port =
        let probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        probe.Start()
        let free = (probe.LocalEndpoint :?> System.Net.IPEndPoint).Port
        probe.Stop()
        free
      listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
      listener.Start()
      let seen = ConcurrentQueue<string>()
      let serve =
        task {
          for _ in 1 .. 2 do
            let! context = listener.GetContextAsync()
            use reader = new StreamReader(context.Request.InputStream)
            let! requestBody = reader.ReadToEndAsync()
            seen.Enqueue (context.Request.Url.AbsolutePath + " " + requestBody)
            let reply = System.Text.Encoding.UTF8.GetBytes """{"message":"no"}"""
            context.Response.StatusCode <- 400
            do! context.Response.OutputStream.WriteAsync(reply, 0, reply.Length)
            context.Response.Close()
        }
      try
        let info =
          infoWith "77889900" (SessionLifecycleStatus.Ready { Pid = 4242; Port = Some port }) WorkflowTypes.SessionWorkflow.Interactive
        let ctx = ctxFor [ info ] (Some (proxyReturning (WorkerResponse.CheckResult ("r", [])))) (Ok "")
        let tools =
          SageFs.Server.McpTools.SageFsTools(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<SageFs.Server.McpTools.SageFsTools>.Instance)
        let! _ = tools.reset_hot_reload_state ("App.State.count", info.WorkingDirectory)
        let! _ = tools.set_reflection_read_mode ("probe-callers", info.WorkingDirectory)
        let! finished = Task.WhenAny(serve, Task.Delay TestTimeouts.patience)
        obj.ReferenceEquals(finished, serve) |> Expect.isTrue "the worker was asked twice"
        seen
        |> Seq.toList
        |> String.concat "\n"
        |> Expect.equal
             "what the worker received"
             ("/hotreload/reset-state {\"binding\":\"App.State.count\"}\n"
              + "/hotreload/reflection-mode {\"mode\":\"probe-callers\"}")
      finally
        listener.Stop()
    }

    // ---- SageFs/McpResources.fs ----

    testCase "WHY — the cohort_status resource writes the frame with camelCase keys" <| fun _ ->
      let resources = SageFs.Server.McpResources.SageFsResources (ctxFor [] noProxy (Ok ""))
      resources.CohortStatus()
      |> Expect.equal
           "cohort status"
           """{"claims":[],"integrationHead":"0000000000000000000000000000000000000000","landings":[],"members":[],"rows":[],"tests":[],"version":0}"""
  ]
