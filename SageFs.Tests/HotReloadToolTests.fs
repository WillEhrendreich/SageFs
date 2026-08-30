module SageFs.Tests.HotReloadToolTests

open System
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.WorkerProtocol
open SageFs.WorkflowTypes

// Build a minimal SageFsTools + McpContext pair for direct invocation.
let private mkTools
  (workerPort: int option)
  : SageFsTools =
  let diagnosticsChanged = Event<SageFs.Features.DiagnosticsStore.T>()
  let sid = SessionId.newId ()
  let sessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- SessionId.value sid
  let stubOps : SessionManagementOps = {
    CreateSession = fun _ _ _ -> Task.FromResult(Ok "stub")
    ListSessions = fun () -> Task.FromResult("[]")
    StopSession = fun _ -> Task.FromResult(Ok "stub")
    DisposeSession = fun _ -> Task.FromResult(Ok "stub")
    PurgeSession = fun _ -> Task.FromResult(Ok "stub")
    RestartSession = fun _ _ -> Task.FromResult(Ok "stub")
    GetProxy = fun _ -> Task.FromResult(Some (fun _ -> failwith "stub proxy called" : Async<SageFs.WorkerProtocol.WorkerResponse>))
    GetSessionInfo = fun _ ->
      Task.FromResult(Some {
        Id = sid
        Name = None
        Projects = ["p.fsproj"]
        WorkingDirectory = "C:\\test"
        SolutionRoot = None
        CreatedAt = System.DateTime.UtcNow
        LastActivity = System.DateTime.UtcNow
        Status = SessionStatus.Ready
        FaultReason = None
        WorkerPid = Some 42
        WorkerPort = workerPort
        Workflow = SessionWorkflow.Interactive
      })
    GetAllSessions = fun () -> Task.FromResult([])
    UpdateSessionStatus = fun _ _ -> Task.FromResult(())
    GetStandbyInfo = fun () -> Task.FromResult(StandbyInfo.NoPool)
    NotifyWorkerDied = fun _ -> ()
  }
  let ctx : McpContext = {
    FrictionStore = None
    DiagnosticsChanged = diagnosticsChanged.Publish
    StateChanged = None
    SessionOps = stubOps
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveSnapshotSink = None
  }
  SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

[<Tests>]
let hotReloadToolTests =
  testList "HotReloadTool" [

    // Ensure a clean env-var state for every test. Earlier tests can leave
    // SAGEFS_DEVRELOAD set in the process, which would break this one.
    let resetEnv () =
      System.Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", null)

    testCase "enable_hot_reload returns PatchFailed gracefully when SageFs.Host is not loaded" <| fun _ ->
      resetEnv ()
      let port = 40000
      let tools = mkTools (Some port)
      let m = tools.GetType().GetMethod("enable_hot_reload")
      let raw = m.Invoke(tools, [| box "" |]) :?> Task<string>
      raw.Result
      |> System.Text.Json.JsonDocument.Parse
      |> fun doc -> doc.RootElement
      |> fun n ->
        let health = n.GetProperty("health").GetString()
        Expect.stringContains "should mention PatchFailed" "PatchFailed" health
        Expect.stringContains "should mention DevReloadInjector or host assembly" "DevReloadInjector" health

    testCase "enable_hot_reload respects SAGEFS_DEVRELOAD=0" <| fun _ ->
      resetEnv ()
      System.Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", "0")
      try
        let tools = mkTools (Some 40000)
        let m = tools.GetType().GetMethod("enable_hot_reload")
        let raw = m.Invoke(tools, [| box "" |]) :?> Task<string>
        let n = System.Text.Json.JsonDocument.Parse(raw.Result).RootElement
        // When the env var is set, the tool short-circuits to Disabled before
        // calling the reflection-based install. patched=false and health mentions
        // the env var. disabledByEnvVar is not set (it's only true when
        // disableForSession was the cause, not the env var).
        Expect.isFalse (n.GetProperty("patched").GetBoolean()) "should not be patched"
        let health = n.GetProperty("health").GetString()
        health |> Expect.stringContains "should mention SAGEFS_DEVRELOAD" "SAGEFS_DEVRELOAD"
        let steps =
          n.GetProperty("nextSteps").EnumerateArray()
          |> Seq.map (fun x -> x.GetString ())
          |> Seq.toList
          |> String.concat " "
        steps |> Expect.stringContains "should mention how to unset the env var" "unset"
      finally
        System.Environment.SetEnvironmentVariable("SAGEFS_DEVRELOAD", null)

    testCase "enable_hot_reload returns PatchFailed when worker port is 0" <| fun _ ->
      let tools = mkTools None
      let m = tools.GetType().GetMethod("enable_hot_reload")
      let raw = m.Invoke(tools, [| box "" |]) :?> Task<string>
      let n = System.Text.Json.JsonDocument.Parse(raw.Result).RootElement
      Expect.isFalse (n.GetProperty("patched").GetBoolean()) "should not be patched"
      Expect.stringContains "should mention PatchFailed" "PatchFailed" (n.GetProperty("health").GetString())

    testCase "disable_hot_reload returns disabled: true with health Disabled" <| fun _ ->
      let tools = mkTools (Some 40000)
      let m = tools.GetType().GetMethod("disable_hot_reload")
      let raw = m.Invoke(tools, [| box "" |]) :?> Task<string>
      let n = System.Text.Json.JsonDocument.Parse(raw.Result).RootElement
      Expect.isTrue (n.GetProperty("disabled").GetBoolean()) "should be disabled"
      Expect.equal (n.GetProperty("health").GetString()) "Disabled" "health should be Disabled"
  ]