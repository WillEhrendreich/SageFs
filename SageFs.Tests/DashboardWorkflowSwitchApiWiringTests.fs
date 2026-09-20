/// Proves `Dashboard.switchWorkflowViaApi` — the dashboard's ONLY network
/// call for the workflow switcher (sagefs-ux-roast.md §4.1/§4.2/§11 Island B
/// item 4) — actually agrees with the real `POST /api/sessions/{sid}/workflow`
/// route it targets. Stands up the SAME kind of bare Kestrel host
/// `WorkflowRouteHttpTests.fs` uses for that route (a fake `SessionManagementOps`,
/// no real daemon, own ephemeral loopback port) and calls the dashboard's
/// glue function against it — the handler-logic tests
/// (`DashboardWorkflowSwitchHandlerTests.fs`) inject a fake network function,
/// so this is the one place that would catch a URL/body/field-name mismatch
/// between the two ends.
module SageFs.Tests.DashboardWorkflowSwitchApiWiringTests

open System
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
open SageFs.Server.Dashboard

let private sid =
  match SessionId.validate "0a0b0c0d" with
  | Ok id -> id
  | Error e -> failwith e

let private unknownSid =
  match SessionId.validate "deadbeef" with
  | Ok id -> id
  | Error e -> failwith e

/// A fake SessionOps whose `SwitchWorkflow` only recognizes `sid` — matches
/// `WorkflowRouteHttpTests.fs`'s fake exactly (the real SessionManager
/// mailbox's `None` branch for an unknown session).
let private fakeOps : SessionManagementOps =
  { SessionManagementOps.stub with
      SwitchWorkflow = fun sidStr target ->
        match sidStr = SessionId.value sid with
        | true -> Task.FromResult(Ok (sprintf "Switching to %s" (WorkflowTypes.SessionWorkflow.label target)))
        | false -> Task.FromResult(Error (SageFsError.SessionNotFound sidStr)) }

/// Stand up only `mapSessionRoutes` on a bare Kestrel host bound to an
/// ephemeral loopback port — this test's own spare port, never
/// 37749/37750.
let private startFakeApiServer (ops: SessionManagementOps) = task {
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
      SseJsonOpts = Text.Json.JsonSerializerOptions()
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
  let port =
    server.Features.Get<IServerAddressesFeature>().Addresses
    |> Seq.head
    |> fun (addr: string) -> Uri(addr).Port
  return app, port
}

[<Tests>]
let apiWiringTests =
  testList "Dashboard.switchWorkflowViaApi — wired against the real route" [
    testTask "WHY — a recognized session + target resolves through the REAL route's success shape, not just a fake stub" {
      let! (app: WebApplication), port = startFakeApiServer fakeOps
      try
        let! result = switchWorkflowViaApi port sid WorkflowTypes.SessionWorkflow.Interactive
        result |> Expect.equal "the real route's message flows through unchanged" (Ok "Switching to REPL")
      finally (app :> IDisposable).Dispose()
    }

    testTask "WHY — an unknown session's REAL 404 SageFsError body parses to an Error naming the real reason, not a swallowed success" {
      let! (app: WebApplication), port = startFakeApiServer fakeOps
      try
        let! result = switchWorkflowViaApi port unknownSid WorkflowTypes.SessionWorkflow.Interactive
        match result with
        | Error msg -> msg |> Expect.stringContains "names the unknown session" (SessionId.value unknownSid)
        | Ok m -> failtestf "an unknown session must not succeed, got Ok %s" m
      finally (app :> IDisposable).Dispose()
    }

    testTask "WHY — every dashboard-offered workflow round-trips through the real route" {
      let! (app: WebApplication), port = startFakeApiServer fakeOps
      try
        for w in SageFs.Server.DashboardTypes.WorkflowSwitch.options do
          let! result = switchWorkflowViaApi port sid w
          match result with
          | Ok _ -> ()
          | Error e -> failtestf "%s should switch successfully via the real route, got: %s" (WorkflowTypes.SessionWorkflow.label w) e
      finally (app :> IDisposable).Dispose()
    }
  ]
