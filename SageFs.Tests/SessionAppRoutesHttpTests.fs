module SageFs.Tests.SessionAppRoutesHttpTests

// RED-first HTTP contract tests for the daemon routes that let ANY editor
// client (not just the dashboard/MCP) run and stop a session's app:
//   POST /api/sessions/{sid}/run-app
//   POST /api/sessions/{sid}/stop-app
// A second agent (VS Code extension wiring) depends on this exact shape:
// 200 + AppRun.AppStateView JSON on success, or
// SageFsError.toHttpStatus/toJson on failure (e.g. 404 for an unknown session).
//
// This stands up only SageFs.Server.McpServer.mapSessionRoutes on a bare
// Kestrel host with a fake SessionManagementOps — no real daemon process,
// no real FSI worker — mirroring HotReloadEndpointTests.fs's in-proc pattern
// and AppRunOrchestrationTests.fs's fake-ops-over-AppSlot pattern.

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
open SageFs.McpTools
open SageFs.WorkerProtocol
open SageFs.AppRun

let private at = DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc)

let private sid =
  match SessionId.validate "0a0b0c0d" with
  | Ok id -> id
  | Error e -> failwith e

let private unknownSid =
  match SessionId.validate "deadbeef" with
  | Ok id -> id
  | Error e -> failwith e

let private exeProject : ProjectLoading.ClassifiedProject =
  { Path = "/src/Web.fsproj"; Role = ProjectLoading.ProjectRole.Executable; PackageRefs = [] }

let private baseInfo (app: AppRunState) : SessionInfo =
  { Id = sid
    Name = None
    Projects = [ exeProject.Path ]
    WorkingDirectory = "/src"
    SolutionRoot = None
    Status = SessionLifecycleStatus.Ready { Pid = 1; Port = Some 5555 }
    Workflow = WorkflowTypes.SessionWorkflow.WebLive WorkflowTypes.BrowserRefreshConfig.defaults
    CreatedAt = at
    LastActivity = at
    ActiveProject = None
    ProjectRoles = [ exeProject ]
    App = app }

/// A worker proxy that runs the app successfully and never reports a later
/// change (AwaitAppChange never completes — the run-app response does not
/// wait on it; it only drives AppRunOrchestration's fire-and-forget watch).
let private never = TaskCompletionSource<WorkerResponse>()

let private worker (msg: WorkerMessage) : Async<WorkerResponse> =
  match msg with
  | WorkerMessage.RunApp (project, _, replyId) ->
    async {
      return WorkerResponse.AppRunResult (
        replyId,
        Ok (AppRunState.Running
              { RunId = "run1"
                Project = project
                EntryPoint = "Program.main"
                Endpoint = AppEndpoint.Http ("http://127.0.0.1:5123", [])
                StartedAt = at }))
    }
  | WorkerMessage.StopApp (_, replyId) ->
    async { return WorkerResponse.AppRunResult (replyId, Ok AppRunState.NotRunning) }
  | WorkerMessage.AwaitAppChange (_, _) -> Async.AwaitTask never.Task
  | other -> failwithf "unexpected worker message in test: %A" other

/// The session's owner as SessionManager is in production: one AppSlot,
/// changed only through the real AppRun.AppSlot decisions (not hand-rolled
/// state), so this exercises the same transitions AppRunOrchestration relies on.
let private fakeOps (info: SessionInfo) : SessionManagementOps =
  let gate = obj ()
  let slot = ref { AppSlot.initial with State = info.App }
  let change (decide: AppSlot -> 'a * AppSlot) : 'a =
    lock gate (fun () ->
      let answer, next = decide slot.Value
      slot.Value <- next
      answer)
  // Gate every op on session identity, exactly as the real SessionManager
  // mailbox does (SessionManager.fs's ClaimRun/ClaimStop handlers reply
  // SessionNotFound for an id ManagerState doesn't hold) — stopApp relies on
  // ClaimStop itself for the unknown-session 404; it never calls GetSessionInfo.
  let notFound () = Error (SageFsError.SessionNotFound (SessionId.value sid))
  { SessionManagementOps.stub with
      GetSessionInfo = fun id ->
        match id = sid with
        | true -> Task.FromResult(Some { info with App = lock gate (fun () -> slot.Value.State) })
        | false -> Task.FromResult None
      ClaimRun = fun id project ->
        match id = sid with
        | false -> Task.FromResult(notFound ())
        | true ->
          let phase = AppRunOrchestration.startPhaseFor info.Status info.Workflow
          Task.FromResult(Ok (change (AppSlot.claimRun project phase at)))
      ClaimStop = fun id ->
        match id = sid with
        | false -> Task.FromResult(notFound ())
        | true -> Task.FromResult(Ok (change AppSlot.claimStop))
      AdvanceRun = fun _ generation next -> Task.FromResult(change (AppSlot.advance generation next))
      EndAppRun = fun _ generation runId final -> Task.FromResult(change (AppSlot.endRun generation runId final))
      GetProxy = fun _ -> Task.FromResult(Some worker)
      SwitchWorkflow = fun _ _ -> Task.FromResult(Ok "restarting")
      AwaitReady = fun _ _ -> Task.FromResult(Ok ()) }

/// Stand up only mapSessionRoutes on a bare Kestrel host bound to an
/// ephemeral loopback port, backed by the given fake ops.
let private startTestServer (ops: SessionManagementOps) = task {
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
      LiveSnapshotSink = None
      CohortOwner = None }

  let mcpContext : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = config.DiagnosticsChanged
      StateChanged = None
      SessionOps = ops
      SessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
      McpPort = 0
      Dispatch = None
      GetElmModel = None
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None; RecordEval = None
      ActivityTracker = config.ActivityTracker
      LiveSnapshotSink = None
      CohortOwner = None }

  let sseContext : SageFs.Server.McpServer.SseContext =
    { GetElmModel = None
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
  SageFs.Server.McpServer.mapSessionRoutes app rctx
  do! app.StartAsync()
  let server = app.Services.GetRequiredService<IServer>()
  let baseUrl =
    server.Features.Get<IServerAddressesFeature>().Addresses
    |> Seq.head
  return app, baseUrl
}

let private httpClient = new HttpClient()

let private postRaw (url: string) (body: string option) : Task<int * string> = task {
  let content =
    body
    |> Option.map (fun (b: string) -> new StringContent(b, Encoding.UTF8, "application/json") :> HttpContent)
    |> Option.toObj
  let! resp = httpClient.PostAsync(url, content)
  let! respBody = resp.Content.ReadAsStringAsync()
  return int resp.StatusCode, respBody
}

[<Tests>]
let sessionAppRoutesHttpTests =
  testList "Session app HTTP routes" [
    testTask "POST /api/sessions/{sid}/run-app on a Ready session returns 200 with a Running AppStateView" {
      let! (app: WebApplication), baseUrl = startTestServer (fakeOps (baseInfo AppRunState.NotRunning))
      try
        let! (status: int), (body: string) = postRaw (sprintf "%s/api/sessions/%s/run-app" baseUrl (SessionId.value sid)) (Some "{}")
        status |> Expect.equal "run-app on a Ready session returns 200" 200
        let doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("State").GetString()
        |> Expect.equal "AppStateView.State should be Running" "Running"
        let urls =
          doc.RootElement.GetProperty("Urls").EnumerateArray()
          |> Seq.map (fun e -> e.GetString())
          |> Seq.toList
        urls
        |> List.contains "http://127.0.0.1:5123"
        |> Expect.isTrue "AppStateView.Urls should carry the running app's URL"
      finally (app :> IDisposable).Dispose()
    }

    testTask "POST /api/sessions/{sid}/run-app on an unknown session returns 404 via SageFsError.toHttpStatus" {
      let! (app: WebApplication), baseUrl = startTestServer (fakeOps (baseInfo AppRunState.NotRunning))
      try
        let! (status: int), (body: string) = postRaw (sprintf "%s/api/sessions/%s/run-app" baseUrl (SessionId.value unknownSid)) (Some "{}")
        status |> Expect.equal "run-app on an unknown session returns 404" 404
        let doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("case").GetString()
        |> Expect.equal "error body should carry the SageFsError case" "SessionNotFound"
      finally (app :> IDisposable).Dispose()
    }

    testTask "POST /api/sessions/{sid}/stop-app on an unknown session returns 404 via SageFsError.toHttpStatus" {
      let! (app: WebApplication), baseUrl = startTestServer (fakeOps (baseInfo AppRunState.NotRunning))
      try
        let! (status: int), (body: string) = postRaw (sprintf "%s/api/sessions/%s/stop-app" baseUrl (SessionId.value unknownSid)) None
        status |> Expect.equal "stop-app on an unknown session returns 404" 404
        let doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("case").GetString()
        |> Expect.equal "error body should carry the SageFsError case" "SessionNotFound"
      finally (app :> IDisposable).Dispose()
    }

    testTask "POST /api/sessions/{sid}/stop-app on a running app returns 200 with NotRunning" {
      let running =
        AppRunState.Running
          { RunId = "run1"
            Project = exeProject.Path
            EntryPoint = "Program.main"
            Endpoint = AppEndpoint.Http ("http://127.0.0.1:5123", [])
            StartedAt = at }
      let! (app: WebApplication), baseUrl = startTestServer (fakeOps (baseInfo running))
      try
        let! (status: int), (body: string) = postRaw (sprintf "%s/api/sessions/%s/stop-app" baseUrl (SessionId.value sid)) None
        status |> Expect.equal "stop-app on a running app returns 200" 200
        let doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("State").GetString()
        |> Expect.equal "AppStateView.State should be NotRunning" "NotRunning"
      finally (app :> IDisposable).Dispose()
    }
  ]
