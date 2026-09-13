module SageFs.Tests.LiveTestActivityWiringTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTestActivity
open SageFs.Tests.LiveTestingTestHelpers

let private sid = "aa000001"
let private otherSid = "bb000002"
let private passed = TestRunStatus.Passed (TimeSpan.FromMilliseconds 5.)
let private failed = TestRunStatus.Failed (TestFailure.AssertionFailed "boom", TimeSpan.FromMilliseconds 5.)

let private sessionIdOf (value: string) =
  match WorkerProtocol.SessionId.validate value with
  | Ok id -> id
  | Error err -> failwithf "bad test session id %s: %A" value err

let private entry (tc: TestCase) (status: TestRunStatus) : TestStatusEntry =
  { TestId = tc.Id
    DisplayName = tc.DisplayName
    FullName = tc.FullName
    Origin = tc.Origin
    Framework = tc.Framework
    Category = tc.Category
    CurrentPolicy = RunPolicy.OnEveryChange
    Status = status
    PreviousStatus = TestRunStatus.Detected }

let private activeModel () =
  let initial = SageFsModel.initial ()
  { initial with
      LiveTesting =
        { initial.LiveTesting with
            TestState = { initial.LiveTesting.TestState with Activation = LiveTestingActivation.Active } } }

let private afterFcs (result: FcsTypeCheckResult) (cycle: LiveTestCycleState) =
  snd (LiveTestCycleState.handleFcsResult result cycle)

let private pendingRebuild (generation: int64) : PendingRebuildState =
  { Generation = generation
    Tests = [||]
    Trigger = RunTrigger.FileSave
    FilePath = "/src/Math.fs"
    AnalysisIdentity = None
    TreeSitterElapsed = TimeSpan.Zero
    FcsElapsed = TimeSpan.Zero
    SessionId = Some sid
    InstrumentationMaps = [||] }

let private withPendingRebuild (compile: CompileBlock) =
  let model = activeModel ()
  { model with LiveTesting = { model.LiveTesting with PendingRebuild = Some (pendingRebuild 7L); Compile = compile } }

[<Tests>]
let compileBlockTests =
  testList "LiveTestActivity compile block" [
    testCase "WHY — handleFcsResult — a failed type-check records the file and its error count because the user must see why tests did not re-run" <| fun _ ->
      (LiveTestCycleState.empty |> afterFcs (FcsTypeCheckResult.Failed ("/src/Math.fs", [ "FS0001 a"; "FS0039 b" ]))).Compile
      |> Expect.equal "blocked on Math.fs" (CompileBlock.CompileErrors ("/src/Math.fs", 2))

    testCase "WHY — handleFcsResult — the blocked file type-checking clean lifts the block because it no longer holds anything back" <| fun _ ->
      (LiveTestCycleState.empty
       |> afterFcs (FcsTypeCheckResult.Failed ("/src/Math.fs", [ "FS0001 a" ]))
       |> afterFcs (FcsTypeCheckResult.Success ("/src/Math.fs", []))).Compile
      |> Expect.equal "unblocked" CompileBlock.NoCompileErrors

    testCase "WHY — handleFcsResult — another file type-checking clean keeps the block because the broken file is still broken" <| fun _ ->
      (LiveTestCycleState.empty
       |> afterFcs (FcsTypeCheckResult.Failed ("/src/Math.fs", [ "FS0001 a" ]))
       |> afterFcs (FcsTypeCheckResult.Success ("/src/Other.fs", []))).Compile
      |> Expect.equal "still blocked" (CompileBlock.CompileErrors ("/src/Math.fs", 1))

    testCase "WHY — RebuildCompleted — a failed rebuild blocks with its reason because the tests did not re-run and the user must see why" <| fun _ ->
      let reason = "Math.fs(3,5): error FS0001: This expression was expected to have type 'int'"
      let model', _ = SageFsUpdate.update (SageFsMsg.RebuildCompleted (Some sid, 7L, Error reason)) (withPendingRebuild CompileBlock.NoCompileErrors)
      model'.LiveTesting.Compile
      |> Expect.equal "blocked by the rebuild" (CompileBlock.RebuildFailed reason)

    testCase "WHY — RebuildCompleted — a successful rebuild lifts the block because the tests now run against fresh code" <| fun _ ->
      let model', _ = SageFsUpdate.update (SageFsMsg.RebuildCompleted (Some sid, 7L, Ok ())) (withPendingRebuild (CompileBlock.CompileErrors ("/src/Math.fs", 1)))
      model'.LiveTesting.Compile
      |> Expect.equal "unblocked" CompileBlock.NoCompileErrors
  ]

[<Tests>]
let discoveryProgressTests =
  testList "LiveTestActivity discovery progress" [
    testCase "WHY — EnableLiveTesting — marks each running session's discovery in progress because the user is now waiting on it" <| fun _ ->
      let session : SessionSnapshot =
        { Id = sessionIdOf sid
          Name = None
          Projects = [ "Test.fsproj" ]
          Status = SessionDisplayStatus.Running
          LastActivity = DateTime.UtcNow
          EvalCount = 0
          UpSince = DateTime.UtcNow
          WorkingDirectory = "." }
      let initial = SageFsModel.initial ()
      let model = { initial with Sessions = { initial.Sessions with Sessions = [ session ] } }
      let model', _ = SageFsUpdate.update SageFsMsg.EnableLiveTesting model
      model'.LiveTesting.TestState.SessionDiscovery |> Map.tryFind sid
      |> Expect.equal "in progress" (Some DiscoveryProgress.InProgress)

    testCase "WHY — TestsDiscovered — a discovery that found nothing is Completed because it is not still looking" <| fun _ ->
      let model', _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestsDiscovered (sid, [||]))) (activeModel ())
      model'.LiveTesting.TestState.SessionDiscovery |> Map.tryFind sid
      |> Expect.equal "completed" (Some DiscoveryProgress.Completed)

    testCase "WHY — TestDiscoveryFailed — records the reason for that session because a spinner that never ends explains nothing" <| fun _ ->
      let model', _ = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestDiscoveryFailed (sid, "could not load Tests.dll"))) (activeModel ())
      model'.LiveTesting.TestState.SessionDiscovery |> Map.tryFind sid
      |> Expect.equal "failed" (Some (DiscoveryProgress.Failed "could not load Tests.dll"))
  ]

[<Tests>]
let activityInputTests =
  let mine = mkTestCase "Math.Tests.adds" TestFramework.Expecto TestCategory.Unit
  let theirs = mkTestCase "Other.Tests.fails" TestFramework.Expecto TestCategory.Unit
  let expecto = ProviderDescription.AttributeBased { Name = TestFramework.Expecto; TestAttributes = []; AssemblyMarker = "Expecto" }
  let cycle =
    { LiveTestCycleState.empty with
        Compile = CompileBlock.CompileErrors ("/src/Math.fs", 2)
        TestState =
          { LiveTestState.empty with
              Activation = LiveTestingActivation.Active
              DiscoveredTests = [| mine; theirs |]
              TestSessionMap = Map.ofList [ mine.Id, sid; theirs.Id, otherSid ]
              StatusIndex = TestStatusIndex.fromEntries [| entry mine passed; entry theirs failed |]
              DetectedProviders = [ expecto ]
              SessionDiscovery = Map.ofList [ sid, DiscoveryProgress.Completed ] } }
  testList "LiveTestActivity activity input" [
    testCase "WHY — activityInput — takes only this session's tests with its own discovery and compile state because each session's card describes that session" <| fun _ ->
      LiveTestActivity.activityInput sid cycle
      |> Expect.equal "this session's input"
        { Activation = LiveTestingActivation.Active
          Discovery = DiscoveryProgress.Completed
          Frameworks = [ "Expecto" ]
          Compile = CompileBlock.CompileErrors ("/src/Math.fs", 2)
          Rebuild = RebuildProgress.NotRebuilding
          Statuses = [| passed |] }

    testCase "WHY — activityInput — a session never asked to discover is NotRequested because nothing is known about it yet" <| fun _ ->
      (LiveTestActivity.activityInput "cc000003" cycle).Discovery
      |> Expect.equal "not requested" DiscoveryProgress.NotRequested

    testCase "WHY — activityInput — this session's pending rebuild counts its waiting tests because they are about to re-run" <| fun _ ->
      let rebuilding = { cycle with PendingRebuild = Some { pendingRebuild 7L with Tests = [| mine; theirs |] } }
      (LiveTestActivity.activityInput sid rebuilding).Rebuild
      |> Expect.equal "rebuilding two" (RebuildProgress.Rebuilding 2)

    testCase "WHY — activityInput — another session's rebuild is not this session's because only that worker restarts" <| fun _ ->
      let rebuilding = { cycle with PendingRebuild = Some { pendingRebuild 7L with Tests = [| mine |] } }
      (LiveTestActivity.activityInput otherSid rebuilding).Rebuild
      |> Expect.equal "not rebuilding" RebuildProgress.NotRebuilding

    testCase "WHY — liveTestActivityFor — a background session's block comes from its own cycle because each session builds separately" <| fun _ ->
      let model = activeModel ()
      let withTest =
        { model with
            LiveTesting =
              { cycle with Compile = CompileBlock.NoCompileErrors }
            PerSessionLiveTesting =
              Map.ofList [ sid, { LiveTestCycleState.empty with Compile = CompileBlock.RebuildFailed "error FS0001: x" } ] }
      SageFsModel.liveTestActivityFor sid withTest
      |> Expect.equal "blocked by its own rebuild" (LiveTestActivity.BlockedByFailedRebuild ("error FS0001: x", { TestTally.empty with Passed = 1 }))
  ]

[<Tests>]
let discoveryReportTests =
  let test = mkTestCase "Math.Tests.adds" TestFramework.Expecto TestCategory.Unit
  let dispatchedFor (report: SessionManager.TestDiscoveryReport) =
    let dispatched = ResizeArray<SageFsMsg>()
    let dir = Directory.CreateTempSubdirectory "sagefs-discovery-"
    try
      SageFs.Server.DaemonMode.handleTestDiscovery
        (fun () -> SessionManager.QuerySnapshot.empty)
        dir.FullName
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
        dispatched.Add
        (sessionIdOf sid)
        report
    finally
      dir.Delete true
    List.ofSeq dispatched
  testList "LiveTestActivity discovery report" [
    testCase "WHY — TestDiscoveryReport.ofResponse — a discovery reply is Discovered" <| fun _ ->
      SessionManager.TestDiscoveryReport.ofResponse (WorkerProtocol.WorkerResponse.InitialTestDiscovery ([| test |], []))
      |> Expect.equal "discovered" (SessionManager.TestDiscoveryReport.Discovered ([| test |], []))

    testCase "WHY — TestDiscoveryReport.ofResponse — a worker error is DiscoveryFailed with its description because the user must see why" <| fun _ ->
      let err = SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "Tests.dll is missing" ])
      SessionManager.TestDiscoveryReport.ofResponse (WorkerProtocol.WorkerResponse.WorkerError err)
      |> Expect.equal "failed" (SessionManager.TestDiscoveryReport.DiscoveryFailed (SageFsError.describe err))

    testCase "WHY — TestDiscoveryReport.ofResponse — any other reply is DiscoveryFailed because silently dropping it leaves discovery pending forever" <| fun _ ->
      match SessionManager.TestDiscoveryReport.ofResponse (WorkerProtocol.WorkerResponse.EvalCancelled false) with
      | SessionManager.TestDiscoveryReport.DiscoveryFailed reason -> reason.StartsWith "Unexpected reply" |> Expect.isTrue "names the unexpected reply"
      | other -> failwithf "expected DiscoveryFailed, got %A" other

    testCase "WHY — handleTestDiscovery — a failed discovery reaches the Elm model because it is otherwise only logged" <| fun _ ->
      dispatchedFor (SessionManager.TestDiscoveryReport.DiscoveryFailed "could not load Tests.dll")
      |> List.exists (function
        | SageFsMsg.Event (TuiEvent.TestDiscoveryFailed (s, reason)) -> s = sid && reason = "could not load Tests.dll"
        | _ -> false)
      |> Expect.isTrue "dispatches TestDiscoveryFailed"

    testCase "WHY — handleTestDiscovery — a discovery with zero tests is still dispatched because finding nothing is an answer" <| fun _ ->
      dispatchedFor (SessionManager.TestDiscoveryReport.Discovered ([||], []))
      |> List.exists (function
        | SageFsMsg.Event (TuiEvent.TestsDiscovered (s, tests)) -> s = sid && Array.isEmpty tests
        | _ -> false)
      |> Expect.isTrue "dispatches TestsDiscovered with no tests"
  ]
