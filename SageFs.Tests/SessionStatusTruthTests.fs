module SageFs.Tests.SessionStatusTruthTests

/// WHY — a live dogfood session on the real daemon reported this:
///
///   { "state": "Ready", "lifecycle": "Starting", "loadedProjects": [] }
///
/// `state` was hard-coded in the serializer while `lifecycle`,
/// `loadedProjects`, `health` and `available` all came from the reconciled
/// status. An agent is told to gate on `state == Ready` before evaluating, so
/// a warming session looked ready and the agent walked into a session with
/// nothing loaded. The Lemmings roast reported exactly this shape.
///
/// This test drives the REAL `SageFsTools.get_session_status` over a stub
/// McpContext — the same production entry point an MCP client calls — and
/// pins the cross-field invariant. A separate reproduction of the projection
/// would have passed while the product still lied.
open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server.McpTools
open SageFs.WorkerProtocol
open SageFs.ProjectLoading
open SageFs.McpTools

let private proxy (_: WorkerMessage) =
  async {
    return
      WorkerResponse.StatusResult(
        "reply",
        { Status = SessionStatus.Ready
          StatusMessage = None
          EvalCount = 0
          AvgDurationMs = FixtureDurations.unmeasuredMs
          MinDurationMs = FixtureDurations.unmeasuredMs
          MaxDurationMs = FixtureDurations.unmeasuredMs
          Projects = []
          CoreVersion = "0.0.0-test" })
  }

/// A session that is still warming: registry says Starting, no projects loaded.
let private warmingInfo : SessionInfo =
  { Id = SessionId.newId ()
    Name = Some "warming"
    Projects = [ "Thing.fsproj" ]
    WorkingDirectory = "/tmp/warming"
    SolutionRoot = Some "/tmp/warming"
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    Status = SessionLifecycleStatus.Starting({ Pid = 4242; Port = None })
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning; Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet }

let private readyInfo : SessionInfo =
  let readyRole : ClassifiedProject =
    { Path = "Thing.fsproj"
      Role = ProjectRole.Library
      PackageRefs = []
      LoadMode = LoadMode.Evaluated; Build = SageFs.BuildOptimization.Unoptimized }

  { Id = SessionId.newId ()
    Name = Some "ready"
    Projects = [ "Thing.fsproj" ]
    WorkingDirectory = "/tmp/ready"
    SolutionRoot = Some "/tmp/ready"
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    Status = SessionLifecycleStatus.Ready({ Pid = 4242; Port = Some 45000 })
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = [ readyRole ]
    App = SageFs.AppRun.AppRunState.NotRunning; Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet }

let private ctxFor (info: SessionInfo) : McpContext =
  let diagEvent = Event<SageFs.Features.DiagnosticsStore.T>()
  { FrictionStore = None
    DiagnosticsChanged = diagEvent.Publish
    StateChanged = None
    SessionOps =
      { SessionManagementOps.stub with
          GetAllSessions = fun () -> Task.FromResult [ info ]
          GetProxy = fun _ -> Task.FromResult(Some proxy)
          GetSessionInfo = fun sid -> Task.FromResult(if sid = info.Id then Some info else None) }
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None
    CohortOwner = None
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

/// The parsed status fields, copied out while the JsonDocument is still alive.
/// Returning a `JsonElement` past its `use` would hand back a disposed
/// document — a lifetime bug in the test, not the product.
type private StatusFacts = {
  State: string
  Lifecycle: string
  Loaded: int
}

let private callStatus (info: SessionInfo) : StatusFacts =
  let json =
    SageFsTools(ctxFor info, Microsoft.Extensions.Logging.Abstractions.NullLogger<SageFsTools>.Instance)
      .get_session_status(info.WorkingDirectory)
      .GetAwaiter()
      .GetResult()

  use doc = JsonDocument.Parse(json)
  let el = doc.RootElement
  { State = el.GetProperty("state").GetString()
    Lifecycle = el.GetProperty("lifecycle").GetString()
    Loaded = el.GetProperty("loadedProjects").GetArrayLength() }

[<Tests>]
let sessionStatusTruthTests = testList "session status tells the truth" [

  testCase "WHY — a warming session must not report Ready, because agents gate on that field" <| fun _ ->
    let facts = callStatus warmingInfo

    facts.State
    |> Expect.notEqual "a still-warming session must never claim Ready" "Ready"

  testCase "WHY — state and lifecycle must never contradict each other" <| fun _ ->
    let facts = callStatus warmingInfo

    (facts.State = "Ready" && facts.Lifecycle <> "Ready")
    |> Expect.isFalse
      (sprintf "state '%s' contradicts lifecycle '%s' — the exact dogfooded payload" facts.State facts.Lifecycle)

  testCase "WHY — a Ready session reports Ready for both state and lifecycle" <| fun _ ->
    let facts = callStatus readyInfo

    facts.State
    |> Expect.equal "a ready session reports Ready" "Ready"

    facts.Lifecycle
    |> Expect.equal "and its lifecycle agrees" "Ready"

  testCase "WHY — Ready is only reported when the requested projects actually loaded" <| fun _ ->
    let facts = callStatus warmingInfo

    (facts.State = "Ready" && facts.Loaded = 0)
    |> Expect.isFalse
      (sprintf "Ready with %d loaded projects is the dogfooded contradiction" facts.Loaded)

  testCase "WHY — a ready session reports the projects it actually loaded" <| fun _ ->
    let facts = callStatus readyInfo

    (facts.Loaded, 0)
    |> Expect.isGreaterThan "a Ready session must have loaded its projects"
]

let private faultedInfo : SessionInfo =
  { warmingInfo with
      Name = Some "faulted"
      Status = SessionLifecycleStatus.Faulted (FaultReason.Reported "warmup failed") }

/// The `target` entries as kind tokens, read through the real tool.
/// `routable` false means the worker has no proxy yet, so a warming or faulted session
/// resolves to its own status shape instead of the routable one.
let private targetKinds (routable: bool) (info: SessionInfo) : string list =
  let ctx =
    match routable with
    | true -> ctxFor info
    | false ->
      let routed = ctxFor info
      { routed with SessionOps = { routed.SessionOps with GetProxy = fun _ -> Task.FromResult None } }
  let json =
    SageFsTools(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<SageFsTools>.Instance)
      .get_session_status(info.WorkingDirectory)
      .GetAwaiter()
      .GetResult()
  use doc = JsonDocument.Parse json
  doc.RootElement.GetProperty("target").EnumerateArray()
  |> Seq.map (fun entry -> entry.GetProperty("kind").GetString())
  |> Seq.toList

[<Tests>]
let sessionStatusTargetShapeTests = testList "session status target, every shape" [

  testCase "WHY — a routable session's target is plain {kind, path} data (a raw F# union throws on .NET 10)" <| fun _ ->
    targetKinds true readyInfo |> Expect.equal "one Project entry" [ "Project" ]

  testCase "WHY — a warming session's status carries the same target shape, because it is built separately from the routable one and each shape has to get it right" <| fun _ ->
    targetKinds false warmingInfo |> Expect.equal "one Project entry" [ "Project" ]

  testCase "WHY — a faulted session's status carries the same target shape too" <| fun _ ->
    targetKinds false faultedInfo |> Expect.equal "one Project entry" [ "Project" ]
]
