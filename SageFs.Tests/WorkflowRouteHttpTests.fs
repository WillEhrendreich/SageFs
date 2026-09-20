module SageFs.Tests.WorkflowRouteHttpTests

// RED-first HTTP contract tests for the daemon route that lets ANY editor
// client (not just MCP) switch a session's workflow:
//   POST /api/sessions/{sid}/workflow
// Before this route existed, the dashboard and VS Code had no way to change
// a session's workflow at all — every switch path was MCP-only
// (sagefs-ux-roast.md §4.1/§4.2/§11 Island B item 4).
//
// This stands up only SageFs.Server.McpServer.mapSessionRoutes on a bare
// Kestrel host with a fake SessionManagementOps — no real daemon process,
// no real FSI worker — mirroring SessionAppRoutesHttpTests.fs's in-proc
// pattern exactly.

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

let private sid =
  match SessionId.validate "0a0b0c0d" with
  | Ok id -> id
  | Error e -> failwith e

let private unknownSid =
  match SessionId.validate "deadbeef" with
  | Ok id -> id
  | Error e -> failwith e

/// A fake SessionOps whose `SwitchWorkflow` only recognizes `sid` — an
/// unknown session id answers exactly the way the real SessionManager
/// mailbox does when asked to switch a session it does not hold
/// (`SessionCommand.SwitchWorkflow`'s `None` branch, `SessionManager.fs`).
let private fakeOps : SessionManagementOps =
  { SessionManagementOps.stub with
      SwitchWorkflow = fun sidStr target ->
        match sidStr = SessionId.value sid with
        | true -> Task.FromResult(Ok (sprintf "Switching to %s" (WorkflowTypes.SessionWorkflow.label target)))
        | false -> Task.FromResult(Error (SageFsError.SessionNotFound sidStr)) }

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
let workflowRouteHttpTests =
  testList "Workflow switch HTTP route" [
    testTask "POST /api/sessions/{sid}/workflow with a recognized target returns 200 with the new workflow's label" {
      let! (app: WebApplication), baseUrl = startTestServer fakeOps
      try
        let! (status: int), (body: string) =
          postRaw (sprintf "%s/api/sessions/%s/workflow" baseUrl (SessionId.value sid)) (Some """{"workflow":"hotreload"}""")
        status |> Expect.equal "switching to a recognized workflow returns 200" 200
        let doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "success should be true"
        doc.RootElement.GetProperty("workflow").GetString()
        |> Expect.equal "response should carry the target workflow's label" "Hot Reload"
      finally (app :> IDisposable).Dispose()
    }

    testTask "POST /api/sessions/{sid}/workflow accepts every SessionWorkflow.tryOfString alias" {
      let! (app: WebApplication), baseUrl = startTestServer fakeOps
      try
        for alias, expectedLabel in [ "interactive", "REPL"; "livetesting", "Live Testing"; "hotreload", "Hot Reload" ] do
          let! (status: int), (body: string) =
            postRaw (sprintf "%s/api/sessions/%s/workflow" baseUrl (SessionId.value sid)) (Some (sprintf """{"workflow":"%s"}""" alias))
          status |> Expect.equal (sprintf "'%s' should be a recognized workflow" alias) 200
          let doc = JsonDocument.Parse(body)
          doc.RootElement.GetProperty("workflow").GetString()
          |> Expect.equal (sprintf "'%s' should resolve to the %s workflow" alias expectedLabel) expectedLabel
      finally (app :> IDisposable).Dispose()
    }

    testTask "POST /api/sessions/{sid}/workflow with an unrecognized target returns 400, not a silent default" {
      let! (app: WebApplication), baseUrl = startTestServer fakeOps
      try
        let! (status: int), (body: string) =
          postRaw (sprintf "%s/api/sessions/%s/workflow" baseUrl (SessionId.value sid)) (Some """{"workflow":"hotreoad"}""")
        status |> Expect.equal "an unrecognized workflow string returns 400" 400
        let doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isFalse "success should be false"
        doc.RootElement.GetProperty("error").GetString()
        |> Expect.stringContains "the error should name the bad value" "hotreoad"
      finally (app :> IDisposable).Dispose()
    }

    testTask "POST /api/sessions/{sid}/workflow with no body returns 400 (a missing workflow is never a silent Interactive default)" {
      let! (app: WebApplication), baseUrl = startTestServer fakeOps
      try
        let! (status: int), (_body: string) =
          postRaw (sprintf "%s/api/sessions/%s/workflow" baseUrl (SessionId.value sid)) None
        status |> Expect.equal "a missing workflow body returns 400" 400
      finally (app :> IDisposable).Dispose()
    }

    testTask "POST /api/sessions/{sid}/workflow on an unknown session returns 404 via SageFsError.toHttpStatus" {
      let! (app: WebApplication), baseUrl = startTestServer fakeOps
      try
        let! (status: int), (body: string) =
          postRaw (sprintf "%s/api/sessions/%s/workflow" baseUrl (SessionId.value unknownSid)) (Some """{"workflow":"interactive"}""")
        status |> Expect.equal "an unknown session returns 404" 404
        let doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("case").GetString()
        |> Expect.equal "error body should carry the SageFsError case" "SessionNotFound"
      finally (app :> IDisposable).Dispose()
    }

    testTask "POST /api/sessions/{sid}/workflow with a malformed session id returns 400" {
      let! (app: WebApplication), baseUrl = startTestServer fakeOps
      try
        let! (status: int), (_body: string) =
          postRaw (sprintf "%s/api/sessions/not-a-valid-id/workflow" baseUrl) (Some """{"workflow":"interactive"}""")
        status |> Expect.equal "a malformed session id returns 400" 400
      finally (app :> IDisposable).Dispose()
    }
  ]
