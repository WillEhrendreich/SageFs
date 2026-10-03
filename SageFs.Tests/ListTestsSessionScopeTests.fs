/// A new session's `list_tests` reported another session's file paths.
/// Observed on 0.6.850: a session on /tmp/lem-listtests (a copy of a small
/// fixture) listed all nine tests with FilePath under /tmp/dogfood-gi, the
/// directory of an earlier, stopped, deleted session whose tests had the same
/// names. Three things carried one session's locations into another's answer:
/// `list_tests` read the Primary cycle whoever asked, source mapping used the
/// daemon-global warmup context whoever was discovering, and a stopped
/// session's cycle was never retired. These tests drive the real update fold
/// and the real `listTests`, no daemon.
module SageFs.Tests.ListTestsSessionScopeTests

open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Features.LiveTesting

let private dirA = "/tmp/lt-a"
let private dirB = "/tmp/lt-b"
let private fileA = dirA + "/DemoEnv.Tests/DemoEnvTests.fs"
let private fileB = dirB + "/DemoEnv.Tests/DemoEnvTests.fs"

let private mkSnap (id: WorkerProtocol.SessionId) (dir: string) : SessionSnapshot =
  { Id = id; Name = None; Projects = [ dir + "/DemoEnv.Tests/DemoEnv.Tests.fsproj" ]
    Status = SessionDisplayStatus.Running
    LastActivity = System.DateTime.UtcNow
    EvalCount = 0
    UpSince = System.DateTime.UtcNow
    WorkingDirectory = dir }

let private mkTest (name: string) (origin: TestOrigin) : TestCase =
  { TestCase.Id = TestId.create name TestFramework.Expecto
    FullName = name; DisplayName = name
    Origin = origin; Labels = []; Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private testNames = [ "DemoEnv.Tests.DemoEnvTests.tests/one"; "DemoEnv.Tests.DemoEnvTests.tests/two" ]

let private reflected () = testNames |> List.map (fun n -> mkTest n TestOrigin.ReflectionOnly) |> Array.ofList

let private ctxFor (id: WorkerProtocol.SessionId) (dir: string) (files: string list) : SessionContext =
  { SessionId = WorkerProtocol.SessionId.value id
    ProjectNames = [ "DemoEnv.Tests" ]
    WorkingDir = dir
    Status = "Ready"
    Warmup = WarmupContext.empty
    FileStatuses =
      files |> List.map (fun f -> { Path = f; Readiness = FileReadiness.Loaded; LastLoadedAt = None; IsWatched = true })
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    AutoOpenNamespaces = false }

let private step (ev: TuiEvent) (m: SageFsModel) : SageFsModel =
  SageFsUpdate.update (SageFsMsg.Event ev) m |> fst

let private mcpContextFor (model: SageFsModel) (callerSession: string) : McpContext =
  let sessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- callerSession
  { FrictionStore = None
    DiagnosticsChanged = Event<Features.DiagnosticsStore.T>().Publish
    StateChanged = None
    SessionOps = SessionManagementOps.stub
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = Some (fun () -> model)
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = Some (fun () -> Features.FeatureHooks.FeaturePushState.empty)
    RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

let private listFor (model: SageFsModel) (callerSession: string) : System.Threading.Tasks.Task<string> =
  listTests (mcpContextFor model callerSession) None None

let private originsOf (cycle: LiveTestCycleState) : TestOrigin list =
  cycle.TestState.DiscoveredTests |> Array.map (fun t -> t.Origin) |> List.ofArray

[<Tests>]
let tests =
  testList "list_tests session scope" [

    testTask "list_tests answers from the calling session's cycle, not the active session's" {
      let sidA = WorkerProtocol.SessionId.newId ()
      let sidB = WorkerProtocol.SessionId.newId ()
      let a = WorkerProtocol.SessionId.value sidA
      let b = WorkerProtocol.SessionId.value sidB
      let aTests = testNames |> List.map (fun n -> mkTest n (TestOrigin.SourceMapped (fileA, 7))) |> Array.ofList
      let model =
        SageFsModel.initial ()
        |> step (TuiEvent.SessionCreated (mkSnap sidA dirA))
        |> step (TuiEvent.SessionSwitched (None, a))
        |> step (TuiEvent.TestsDiscovered (a, aTests))
        |> step (TuiEvent.SessionCreated (mkSnap sidB dirB))
        |> step (TuiEvent.TestsDiscovered (b, reflected ()))
      let! (listing: string) = listFor model b
      Expect.isFalse "session B's listing never names session A's directory" (listing.Contains dirA)
      let doc = JsonDocument.Parse listing
      doc.RootElement.GetProperty("TotalCount").GetInt32()
      |> Expect.equal "B's own two tests are listed" 2
    }

    testCase "a background session's discovery is not mapped onto the active session's warmup files" <| fun _ ->
      let sidA = WorkerProtocol.SessionId.newId ()
      let sidB = WorkerProtocol.SessionId.newId ()
      let a = WorkerProtocol.SessionId.value sidA
      let b = WorkerProtocol.SessionId.value sidB
      let model =
        SageFsModel.initial ()
        |> step (TuiEvent.SessionCreated (mkSnap sidA dirA))
        |> step (TuiEvent.SessionSwitched (None, a))
        |> step (TuiEvent.WarmupContextUpdated (ctxFor sidA dirA [ fileA ]))
        |> step (TuiEvent.SessionCreated (mkSnap sidB dirB))
        |> step (TuiEvent.TestsDiscovered (b, reflected ()))
      let foreign =
        originsOf (SageFsModel.cycleForSession b model)
        |> List.filter (function
          | TestOrigin.SourceMapped (f, _) -> f.StartsWith dirA
          | TestOrigin.ReflectionOnly -> false)
      foreign |> Expect.isEmpty "B's tests carry no path under A's directory"

    testCase "a session whose own warmup files do contain the tests still gets file paths" <| fun _ ->
      let sidB = WorkerProtocol.SessionId.newId ()
      let b = WorkerProtocol.SessionId.value sidB
      let model =
        SageFsModel.initial ()
        |> step (TuiEvent.SessionCreated (mkSnap sidB dirB))
        |> step (TuiEvent.SessionSwitched (None, b))
        |> step (TuiEvent.WarmupContextUpdated (ctxFor sidB dirB [ fileB ]))
        |> step (TuiEvent.TestsDiscovered (b, reflected ()))
      originsOf (SageFsModel.cycleForSession b model)
      |> Expect.all "every test is mapped to B's own file" (function
        | TestOrigin.SourceMapped (f, _) -> f = fileB
        | TestOrigin.ReflectionOnly -> false)

    testTask "list_tests never reports a path outside the session's working directory, even if one is in the state" {
      let sidB = WorkerProtocol.SessionId.newId ()
      let b = WorkerProtocol.SessionId.value sidB
      let foreignTests =
        testNames |> List.map (fun n -> mkTest n (TestOrigin.SourceMapped ("/tmp/dogfood-gi/DemoEnv.Tests/DemoEnvTests.fs", 3))) |> Array.ofList
      let m0 =
        SageFsModel.initial ()
        |> step (TuiEvent.SessionCreated (mkSnap sidB dirB))
        |> step (TuiEvent.SessionSwitched (None, b))
      let model =
        { m0 with
            LiveTesting =
              { m0.LiveTesting with
                  TestState = { m0.LiveTesting.TestState with DiscoveredTests = foreignTests } } }
      let! (listing: string) = listFor model b
      Expect.isFalse "the foreign path is not reported" (listing.Contains "dogfood-gi")
      let doc = JsonDocument.Parse listing
      doc.RootElement.GetProperty("TotalCount").GetInt32()
      |> Expect.equal "the tests are still listed, without a location" 2
    }

    testCase "tree-sitter locations land in the session that found them, not the active session's cycle" <| fun _ ->
      let sidA = WorkerProtocol.SessionId.newId ()
      let sidB = WorkerProtocol.SessionId.newId ()
      let a = WorkerProtocol.SessionId.value sidA
      let b = WorkerProtocol.SessionId.value sidB
      let loc : SourceTestLocation =
        { FilePath = fileB; Line = 5; Column = 0; FunctionName = "tests"; AttributeName = "Tests" }
      let model =
        SageFsModel.initial ()
        |> step (TuiEvent.SessionCreated (mkSnap sidA dirA))
        |> step (TuiEvent.SessionSwitched (None, a))
        |> step (TuiEvent.TestsDiscovered (a, reflected ()))
        |> step (TuiEvent.SessionCreated (mkSnap sidB dirB))
        |> step (TuiEvent.TestLocationsDetected (b, [| loc |]))
      model.LiveTesting.TestState.SourceLocations
      |> Expect.isEmpty "A's cycle did not receive B's locations"

    testCase "a stopped session leaves no live-testing state behind when the session list refreshes without it" <| fun _ ->
      // The MCP and daemon stop paths dispatch only a session-list refresh, never
      // SessionStopped, so the refresh is where a vanished session is noticed.
      let sidA = WorkerProtocol.SessionId.newId ()
      let sidB = WorkerProtocol.SessionId.newId ()
      let a = WorkerProtocol.SessionId.value sidA
      let aTests = testNames |> List.map (fun n -> mkTest n (TestOrigin.SourceMapped (fileA, 7))) |> Array.ofList
      let model =
        SageFsModel.initial ()
        |> step (TuiEvent.SessionCreated (mkSnap sidA dirA))
        |> step (TuiEvent.SessionSwitched (None, a))
        |> step (TuiEvent.TestsDiscovered (a, aTests))
        |> step (TuiEvent.SessionsRefreshed [ mkSnap sidB dirB ])
      model.LiveTesting.TestState.DiscoveredTests
      |> Expect.isEmpty "the vanished session's discovered tests are gone"
      model.LiveTesting.TestState.SessionDiscovery
      |> Map.containsKey a
      |> Expect.isFalse "the vanished session no longer owns the primary cycle"
      model.Sessions.ActiveSessionId
      |> Expect.equal "the active pointer moves to a session that exists" (ActiveSession.Viewing sidB)

    testCase "confineToRoots demotes paths outside every root, including a sibling that shares a prefix" <| fun _ ->
      let inside = mkTest "a" (TestOrigin.SourceMapped (dirA + "/x.fs", 1))
      let sibling = mkTest "b" (TestOrigin.SourceMapped (dirA + "-evil/x.fs", 1))
      let elsewhere = mkTest "c" (TestOrigin.SourceMapped ("/tmp/dogfood-gi/x.fs", 1))
      SessionSourceScope.confineToRoots [ dirA ] [| inside; sibling; elsewhere |]
      |> Array.map (fun t -> t.Origin)
      |> Expect.equal "only the path under the root survives"
           [| TestOrigin.SourceMapped (dirA + "/x.fs", 1); TestOrigin.ReflectionOnly; TestOrigin.ReflectionOnly |]

    testCase "confineToRoots with no roots demotes everything (fail closed)" <| fun _ ->
      SessionSourceScope.confineToRoots [] [| mkTest "a" (TestOrigin.SourceMapped (dirA + "/x.fs", 1)) |]
      |> Array.map (fun t -> t.Origin)
      |> Expect.equal "nothing can be claimed without a root" [| TestOrigin.ReflectionOnly |]
  ]
