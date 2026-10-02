module SageFs.Tests.DebugTestRoutesHttpTests

// The daemon's "debug this test" routes, over real HTTP, against the real Elm update (which holds the discovered tests) and
// a scripted worker. Every editor reads these two JSON shapes, so the field names, the status codes and the way a session
// and a test are chosen are pinned here:
//   POST /api/live-testing/debug           hold one test, answer the process to attach to
//   POST /api/live-testing/debug/continue  release it once the debugger is attached, and wait for it to finish
// No daemon process, no FSI host, no debugger: the worker answers with what a host would.

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.HostAgent.TestDebug
open SageFs.McpTools
open SageFs.WorkerProtocol

let private sidText = "a1b2c3d4"

let private caseOf (name: string) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private adds = caseOf "Suite.adds"
let private addsMore = caseOf "Suite.adds more"
let private removes = caseOf "Suite.removes"

let private passed = TestResult.Passed FixtureDurations.usualResult

let private target : DebugTarget =
  { Pid = 4242
    Ticket = DebugTicket "debug-4242-1"
    TestId = adds.Id
    TestName = adds.DisplayName
    Symbols = SymbolSupport.DefinedByEval
    Access = AttachAccess.Blocked "ptrace_scope is 2"
    HoldFor = TestTimeouts.patienceInProcess }

/// What the scripted worker answers to each debug message, and what it was last asked.
type private Worker(onBegin: TestCase -> DebugBegin, onContinue: string -> DebugProgress) =
  let asked = System.Collections.Concurrent.ConcurrentQueue<WorkerMessage>()
  member _.Asked = asked.ToArray() |> Array.toList
  member _.Proxy : SessionProxy =
    fun message ->
      async {
        asked.Enqueue message
        match message with
        | WorkerMessage.DebugTestBegin(test, rid) ->
          return WorkerResponse.DebugTestAnswer(rid, WorkerProtocol.Serialization.serialize (onBegin test))
        | WorkerMessage.DebugTestContinue(ticket, _, rid) ->
          return WorkerResponse.DebugTestAnswer(rid, WorkerProtocol.Serialization.serialize (onContinue ticket))
        | other -> return failwithf "the debug routes must only send debug messages, sent %A" other
      }

let private startServer (worker: Worker) (discovered: TestCase list) = task {
  let info : SessionInfo =
    { Id = SageFs.McpSessionRouting.toSessionId sidText
      Name = None
      Projects = []
      WorkingDirectory = "/debug"
      SolutionRoot = None
      Status = SessionLifecycleStatus.Ready { Pid = 42; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }
  let ops : SessionManagementOps =
    { SessionManagementOps.stub with
        GetProxy = fun _ -> Task.FromResult(Some worker.Proxy)
        GetSessionInfo = fun _ -> Task.FromResult(Some info)
        GetAllSessions = fun () -> Task.FromResult [ info ] }
  let model =
    fst (SageFsUpdate.update (SageFsMsg.Event(TuiEvent.TestsDiscovered(sidText, Array.ofList discovered))) (SageFsModel.initial ()))

  let builder = WebApplication.CreateBuilder([||])
  builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
  builder.Logging.ClearProviders() |> ignore

  let config : SageFs.Server.McpServer.McpServerConfig =
    { DiagnosticsChanged = (Event<SageFs.Features.DiagnosticsStore.T>()).Publish
      StateChanged = None
      FrictionStore = None
      Port = 0
      BindHost = SageFs.SageFsConfig.LoopbackHost.Localhost
      OwnOrigins = SageFs.Server.HttpOriginGuard.OwnOrigins.ofPorts [ 0 ]
      SessionOps = ops
      ElmRuntime = None
      GetWarmupContext = None
      GetHotReloadState = None
      SharedBindingScope = ref None
      SharedFeatureState = None
      ActivityTracker = SageFs.AgentActivityTracker.create ()
      LiveBindings = None
      CohortOwner = None
      GetDaemonHealth = fun () -> None }

  let mcpContext : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = config.DiagnosticsChanged
      StateChanged = None
      SessionOps = ops
      SessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
      McpPort = 0
      Dispatch = None
      GetElmModel = Some (fun () -> model)
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None
      RecordEval = None
      ActivityTracker = config.ActivityTracker
      LiveBindings = None
      CohortOwner = None
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }

  let sseContext : SageFs.Server.McpServer.SseContext =
    { GetElmModel = Some (fun () -> model)
      GetWarmupContext = None
      GetHotReloadState = None
      SseJsonOpts = JsonSerializerOptions()
      TestEventBroadcast = Event<string>()
      SessionEventBroadcast = Event<string>()
      ServerTracker = SageFs.Server.McpServer.McpServerTracker()
      CohortOwner = None }

  let rctx : SageFs.Server.McpServer.RouteContext =
    { Config = config
      McpContext = mcpContext
      SseContext = sseContext
      Dispatch = None
      GetElmRegions = None
      FsiBindings = ref Map.empty
      FeaturePushState = ref SageFs.Features.FeatureHooks.FeaturePushState.empty
      LastFeatureOutputCount = ref 0
      LastEvalContext = ref None }

  let app = builder.Build()
  SageFs.Server.McpServer.mapLiveTestingRoutes app rctx
  do! app.StartAsync()
  let server = app.Services.GetRequiredService<IServer>()
  let baseUrl = server.Features.Get<IServerAddressesFeature>().Addresses |> Seq.head
  return app, baseUrl
}

let private httpClient = new HttpClient()

let private post (url: string) (json: string) : Task<int * JsonElement> = task {
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  let! response = httpClient.PostAsync(url, content)
  let! body = response.Content.ReadAsStringAsync()
  use doc = JsonDocument.Parse body
  return int response.StatusCode, doc.RootElement.Clone()
}

let private text (name: string) (root: JsonElement) = root.GetProperty(name).GetString()

let private withServer (worker: Worker) (discovered: TestCase list) (body: string -> Task<unit>) : Task<unit> = task {
  let! (app: WebApplication), baseUrl = startServer worker discovered
  try do! body baseUrl
  finally (app :> IDisposable).Dispose()
}

let private holds = Worker((fun _ -> DebugBegin.Held target), (fun _ -> DebugProgress.Ended(DebugEnd.Attached passed)))

[<Tests>]
let tests =
  testList "Debug test HTTP routes" [

    testTask "WHY: debug by test id holds that test and answers the process, the ticket and the warnings, in camelCase fields" {
      let worker = Worker((fun _ -> DebugBegin.Held target), (fun _ -> DebugProgress.StillRunning))
      do!
        withServer worker [ adds; removes ] (fun baseUrl -> task {
          let! status, body = post (baseUrl + "/api/live-testing/debug") (JsonSerializer.Serialize {| testId = TestId.value adds.Id; sessionId = sidText |})
          status |> Expect.equal "held is a 200" 200
          text "status" body |> Expect.equal "status" "held"
          body.GetProperty("pid").GetInt32() |> Expect.equal "the process to attach to" 4242
          text "ticket" body |> Expect.equal "the ticket" "debug-4242-1"
          text "testName" body |> Expect.equal "the test" "Suite.adds"
          text "symbols" body |> Expect.equal "eval-defined code has no symbols" "eval"
          text "symbolsNote" body |> Expect.stringContains "says what to do" "rebuild"
          text "access" body |> Expect.equal "blocked" "blocked"
          text "accessNote" body |> Expect.equal "the reason" "ptrace_scope is 2"
          body.GetProperty("holdMs").GetInt32() |> Expect.equal "how long it is held" (int TestTimeouts.patienceInProcess.TotalMilliseconds)
          match worker.Asked with
          | [ WorkerMessage.DebugTestBegin(sent, _) ] -> sent |> Expect.equal "the worker was asked to hold exactly that test" adds
          | other -> failtestf "expected one DebugTestBegin, got %A" other
        })
    }

    testTask "WHY: with no session named, the daemon picks the one session, the way run_tests does" {
      do!
        withServer holds [ adds ] (fun baseUrl -> task {
          let! status, body = post (baseUrl + "/api/live-testing/debug") (JsonSerializer.Serialize {| testId = TestId.value adds.Id |})
          status |> Expect.equal "routed" 200
          text "status" body |> Expect.equal "held" "held"
        })
    }

    testTask "WHY: debug by name resolves a unique name, and refuses an ambiguous one with every candidate and a 409" {
      do!
        withServer holds [ adds; addsMore; removes ] (fun baseUrl -> task {
          let! uniqueStatus, unique = post (baseUrl + "/api/live-testing/debug") (JsonSerializer.Serialize {| pattern = "removes"; sessionId = sidText |})
          uniqueStatus |> Expect.equal "unique" 200
          text "status" unique |> Expect.equal "held" "held"
          let! ambiguousStatus, ambiguous = post (baseUrl + "/api/live-testing/debug") (JsonSerializer.Serialize {| pattern = "adds"; sessionId = sidText |})
          ambiguousStatus |> Expect.equal "ambiguous is a conflict" 409
          text "status" ambiguous |> Expect.equal "status" "ambiguous_test"
          text "message" ambiguous |> Expect.stringContains "lists the matches" "Suite.adds more"
        })
    }

    testTask "WHY: an unknown test is a 404 that says so, and no test discovered is a 409 that says why" {
      do!
        withServer holds [ adds ] (fun baseUrl -> task {
          let! status, body = post (baseUrl + "/api/live-testing/debug") (JsonSerializer.Serialize {| testId = "NOPE"; sessionId = sidText |})
          status |> Expect.equal "not found" 404
          text "status" body |> Expect.equal "status" "no_test_matched"
        })
      do!
        withServer holds [] (fun baseUrl -> task {
          let! status, body = post (baseUrl + "/api/live-testing/debug") (JsonSerializer.Serialize {| testId = "x"; sessionId = sidText |})
          status |> Expect.equal "conflict" 409
          text "status" body |> Expect.equal "status" "not_discovered"
        })
    }

    testTask "WHY: a request that names no test is a 400 that says what to pass, and the worker is never asked" {
      let worker = Worker((fun _ -> DebugBegin.Held target), (fun _ -> DebugProgress.StillRunning))
      do!
        withServer worker [ adds ] (fun baseUrl -> task {
          let! status, body = post (baseUrl + "/api/live-testing/debug") "{}"
          status |> Expect.equal "bad request" 400
          text "message" body |> Expect.stringContains "says what to pass" "testId"
          worker.Asked |> Expect.isEmpty "nothing was held"
        })
    }

    testTask "WHY: a host that holds another test is a 409 naming the holder" {
      let worker = Worker((fun _ -> DebugBegin.Unsupported(UnsupportedReason.HoldAlreadyOpen(DebugTicket "debug-1-1"))), (fun _ -> DebugProgress.StillRunning))
      do!
        withServer worker [ adds ] (fun baseUrl -> task {
          let! status, body = post (baseUrl + "/api/live-testing/debug") (JsonSerializer.Serialize {| testId = TestId.value adds.Id; sessionId = sidText |})
          status |> Expect.equal "conflict" 409
          text "status" body |> Expect.equal "status" "hold_already_open"
          text "message" body |> Expect.stringContains "names the holder" "debug-1-1"
        })
    }

    testTask "WHY: continue carries the ticket to the worker and answers how the test ended" {
      let seen = ref ""
      let worker = Worker((fun _ -> DebugBegin.Held target), (fun ticket -> seen.Value <- ticket; DebugProgress.Ended(DebugEnd.Attached(TestResult.Failed(TestFailure.AssertionFailed "expected 3 but got 4", FixtureDurations.usualResult)))))
      do!
        withServer worker [ adds ] (fun baseUrl -> task {
          let! status, body = post (baseUrl + "/api/live-testing/debug/continue") (JsonSerializer.Serialize {| ticket = "debug-4242-1"; sessionId = sidText |})
          status |> Expect.equal "finished is a 200" 200
          text "status" body |> Expect.equal "status" "attached"
          text "outcome" body |> Expect.equal "failed" "failed"
          text "detail" body |> Expect.equal "the assertion" "expected 3 but got 4"
          seen.Value |> Expect.equal "the worker got the ticket" "debug-4242-1"
          match worker.Asked with
          | [ WorkerMessage.DebugTestContinue(_, park, _) ] -> park |> Expect.equal "the daemon's own park bound" Timeouts.debugContinuePark
          | other -> failtestf "expected one DebugTestContinue, got %A" other
        })
    }

    testTask "WHY: continue answers still_running as a 200 so the editor asks again, and the dead ends as conflicts" {
      for progress, expectedStatus, expectedWire in
        [ DebugProgress.StillRunning, 200, "still_running"
          DebugProgress.Ended(DebugEnd.NoDebuggerWithin TestTimeouts.patienceInProcess), 409, "no_debugger_within"
          DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger, 409, "released_without_debugger"
          DebugProgress.Ended DebugEnd.NoSuchHold, 409, "no_such_hold"
          DebugProgress.Ended(DebugEnd.HostLost "gone"), 503, "host_lost" ] do
        let worker = Worker((fun _ -> DebugBegin.Held target), (fun _ -> progress))
        do!
          withServer worker [ adds ] (fun baseUrl -> task {
            let! status, body = post (baseUrl + "/api/live-testing/debug/continue") (JsonSerializer.Serialize {| ticket = "t"; sessionId = sidText |})
            status |> Expect.equal (sprintf "%s: http status" expectedWire) expectedStatus
            text "status" body |> Expect.equal "wire status" expectedWire
          })
    }

    testTask "WHY: continue with no ticket is a 400" {
      do!
        withServer holds [ adds ] (fun baseUrl -> task {
          let! status, body = post (baseUrl + "/api/live-testing/debug/continue") "{}"
          status |> Expect.equal "bad request" 400
          text "status" body |> Expect.equal "status" "bad_request"
        })
    }
  ]
