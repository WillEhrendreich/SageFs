module SageFs.Tests.SageFsAppTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.WarmUp
open SageFs.Features.Diagnostics
open SageFs.Features.LiveTesting
open SageFs.Tests.SharedGenerators

/// Helper to get output buffer for a session from the model.
let outputFor sid (model: SageFsModel) = model.RecentOutput.GetBuffer(sid)

/// Helper to get the active session's output buffer from the model.
let activeOutput (model: SageFsModel) =
  model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)

let mkHotPathSessionContext
  (startedAt: DateTimeOffset)
  (lastLoadedAt: DateTimeOffset option)
  (readiness: FileReadiness)
  (isWatched: bool)
  : SessionContext =
  {
    SessionId = "00000001"
    ProjectNames = ["Proj.fsproj"]
    WorkingDir = @"C:\Code\Repos\SageFs"
    Status = "Ready"
    Warmup = {
      SourceFilesScanned = 2
      AssembliesLoaded = [
        { Name = "Proj"; Path = "Proj.dll"; NamespaceCount = 2; ModuleCount = 1 }
      ]
      NamespacesOpened = [
        { Name = "System"; Kind = OpenableKind.Namespace; Source = "warmup"; DurationMs = 12.0 }
      ]
      FailedOpens = []
      PhaseTiming = { ScanSourceFilesMs = 10L; ScanAssembliesMs = 5L; OpenNamespacesMs = 7L; TotalMs = 22L }
      StartedAt = startedAt
    }
    FileStatuses = [
      { Path = "Domain.fs"; Readiness = readiness; LastLoadedAt = lastLoadedAt; IsWatched = isWatched }
    ]
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
  }

let mkLiveTestCase
  (id: string)
  (fullName: string)
  (displayName: string)
  : TestCase =
  {
    Id = TestId.TestId id
    FullName = fullName
    DisplayName = displayName
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
  }

let mkSourceTestLocation
  (attributeName: string)
  (functionName: string)
  (filePath: string)
  (line: int)
  : SourceTestLocation =
  {
    AttributeName = attributeName
    FunctionName = functionName
    FilePath = filePath
    Line = line
    Column = 1
  }

let mkPassedRunResult
  (testId: string)
  (testName: string)
  (durationMs: float)
  : SageFs.Features.LiveTesting.TestRunResult =
  {
    TestId = TestId.TestId testId
    TestName = testName
    Result = SageFs.Features.LiveTesting.TestResult.Passed (TimeSpan.FromMilliseconds durationMs)
    Timestamp = DateTimeOffset.UtcNow
    Output = None
  }

let mkFailedRunResult
  (testId: string)
  (testName: string)
  (message: string)
  (durationMs: float)
  : SageFs.Features.LiveTesting.TestRunResult =
  {
    TestId = TestId.TestId testId
    TestName = testName
    Result =
      SageFs.Features.LiveTesting.TestResult.Failed(
        SageFs.Features.LiveTesting.TestFailure.AssertionFailed message,
        TimeSpan.FromMilliseconds durationMs
      )
    Timestamp = DateTimeOffset.UtcNow
    Output = None
  }

let mkSkippedRunResult
  (testId: string)
  (testName: string)
  (reason: string)
  : SageFs.Features.LiveTesting.TestRunResult =
  {
    TestId = TestId.TestId testId
    TestName = testName
    Result = SageFs.Features.LiveTesting.TestResult.Skipped reason
    Timestamp = DateTimeOffset.UtcNow
    Output = None
  }

[<Tests>]
let sageFsUpdateTests = testList "SageFsUpdate" [
  testCase "editor action routes through EditorUpdate" <| fun _ ->
    let model = (SageFsModel.initial())
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor (EditorAction.InsertChar 'x')) model
    ValidatedBuffer.text newModel.Editor.Buffer
    |> Expect.equal "should have inserted x" "x"
    effects |> Expect.isEmpty "no effects for insert"

  testCase "submit produces eval effect" <| fun _ ->
    let model = {
      (SageFsModel.initial()) with
        Editor = {
          EditorState.initial with
            Buffer = ValidatedBuffer.insertChar 'a' ValidatedBuffer.empty } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.Submit) model
    effects |> Expect.hasLength "should have one effect" 1
    match effects.[0] with
    | SageFsEffect.Editor (EditorEffect.RequestEval code) ->
      code |> Expect.equal "should eval buffer content" "a"
    | _ -> failtest "expected RequestEval effect"

  testCase "ListSessions preserves the outer model when it only asks for a refresh" <| fun _ ->
    let model = SageFsModel.initial()
    let updated, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.ListSessions) model
    obj.ReferenceEquals(updated, model)
    |> Expect.isTrue "should keep the same outer model for a no-op editor refresh"
    obj.ReferenceEquals(updated.Editor, model.Editor)
    |> Expect.isTrue "should keep the same editor instance"
    effects
    |> Expect.equal "should still request a session list"
         [SageFsEffect.Editor EditorEffect.RequestSessionList]

  testCase "EvalCompleted adds output line" <| fun _ ->
    let event = SageFsEvent.EvalCompleted ("s1", "val x = 42", [])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    let out = outputFor "s1" newModel
    out |> Expect.hasLength "should have one output" 1
    out.[0].Kind
    |> Expect.equal "should be Result" OutputKind.Result
    out.[0].Text
    |> Expect.equal "should have output text" "val x = 42"

  testCase "EvalFailed adds error output" <| fun _ ->
    let event = SageFsEvent.EvalFailed ("s1", "type mismatch")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    (outputFor "s1" newModel).[0].Kind
    |> Expect.equal "should be Error" OutputKind.Error

  testCase "EvalCancelled adds info line" <| fun _ ->
    let event = SageFsEvent.EvalCancelled "s1"
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    (outputFor "s1" newModel).[0].Kind
    |> Expect.equal "should be Info" OutputKind.Info

  testCase "OutputEmitted adds output line" <| fun _ ->
    let line = {
      Kind = OutputKind.System
      Text = "Disabled warmup auto-open"
      Timestamp = DateTime.UtcNow
      SessionId = "" }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.OutputEmitted line)) (SageFsModel.initial())
    let out = activeOutput newModel
    out |> Expect.hasLength "should have one output" 1
    out.[0] |> Expect.equal "should add the emitted line" line

  testCase "CompletionReady sets completion menu" <| fun _ ->
    let items = [
      { Label = "toString"; Kind = "method"; Detail = Some "string -> string" }
    ]
    let event = SageFsEvent.CompletionReady items
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    newModel.Editor.CompletionMenu |> Expect.isSome "should have menu"
    newModel.Editor.CompletionMenu.Value.Items
    |> Expect.hasLength "should have 1 item" 1

  testCase "SessionCreated adds to session list" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let event = SageFsEvent.SessionCreated snap
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    newModel.Sessions.Sessions
    |> Expect.hasLength "should have 1 session" 1

  testCase "ConfigureWarmupAutoOpen uses selected session working directory" <| fun _ ->
    let baseModel = SageFsModel.initial()
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true
      WorkingDirectory = @"C:\Code\Repos\TestProject" }
    let model = {
      baseModel with
        Editor = { baseModel.Editor with SelectedSessionIndex = Some 0 }
        Sessions = {
          baseModel.Sessions with
            Sessions = [snap]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.ConfigureWarmupAutoOpen) model
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestConfigureWarmupAutoOpen dir)] ->
      dir |> Expect.equal "should use selected session working directory" @"C:\Code\Repos\TestProject"
    | other ->
      failtestf "expected RequestConfigureWarmupAutoOpen, got %A" other

  testCase "SessionCreated with existing ID should upsert, not duplicate" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let model2, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap)) model
    model2.Sessions.Sessions
    |> Expect.hasLength "should still have exactly 1 session" 1

  testCase "SessionCreated upsert updates session data" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let updated = { snap with EvalCount = 42 }
    let model2, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated updated)) model
    model2.Sessions.Sessions
    |> Expect.hasLength "should have 1 session" 1
    model2.Sessions.Sessions.[0].EvalCount
    |> Expect.equal "should have updated eval count" 42

  testCase "Multiple ListSessions refreshes should not accumulate duplicates" <| fun _ ->
    let snapA = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let snapB = {
      Id = testSessionId "aa000002"; Name = None; Projects = ["B.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snapA; snapB]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snapA)) model
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snapB)) m1
    m2.Sessions.Sessions
    |> Expect.hasLength "should still have exactly 2 sessions" 2
    let snap1 = {
      Id = testSessionId "aa000001"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let snap2 = {
      Id = testSessionId "aa000002"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "" }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap1; snap2] }
    }
    let event = SageFsEvent.SessionSwitched (Some "aa000001", "aa000002")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should be s2" (ActiveSession.Viewing (testSessionId "aa000002"))
    newModel.Sessions.Sessions
    |> List.find (fun s -> s.Id = testSessionId "aa000002")
    |> fun s -> s.IsActive
    |> Expect.isTrue "s2 should be active"

  testCase "SessionsRefreshed replaces all sessions in one update" <| fun _ ->
    let snap1 = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let snap2 = {
      Id = testSessionId "aa000002"; Name = None; Projects = ["B.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let event = SageFsEvent.SessionsRefreshed [snap1; snap2]
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.Sessions
    |> Expect.hasLength "should have 2 sessions" 2
    newModel.Sessions.Sessions
    |> List.find (fun s -> s.Id = testSessionId "aa000001")
    |> fun s -> s.IsActive
    |> Expect.isTrue "s1 should be active (matches ActiveSessionId)"

  testCase "SessionsRefreshed preserves active session" <| fun _ ->
    let snap1 = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let snap2 = {
      Id = testSessionId "aa000002"; Name = None; Projects = ["B.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000002") } }
    let event = SageFsEvent.SessionsRefreshed [snap1; snap2]
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "active should still be s2" (ActiveSession.Viewing (testSessionId "aa000002"))
    newModel.Sessions.Sessions
    |> List.find (fun s -> s.Id = testSessionId "aa000002")
    |> fun s -> s.IsActive
    |> Expect.isTrue "s2 should be marked active"

  testCase "SessionsRefreshed sets first session active when awaiting" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let event = SageFsEvent.SessionsRefreshed [snap]
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should auto-select first" (ActiveSession.Viewing (testSessionId "aa000001"))

  testCase "SessionStopped removes session" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap] }
    }
    let event = SageFsEvent.SessionStopped "aa000001"
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.Sessions
    |> Expect.isEmpty "session should be removed"

  testCase "DiagnosticsUpdated replaces diagnostics" <| fun _ ->
    let diag = {
      Message = "type error"
      Subcategory = "typecheck"
      Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
      Severity = DiagnosticSeverity.Error
    }
    let event = SageFsEvent.DiagnosticsUpdated ("s1", [diag])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    newModel.Diagnostics
    |> Map.tryFind "s1"
    |> Option.defaultValue []
    |> Expect.hasLength "should have 1 diagnostic" 1

  testCase "WarmupCompleted with no failures adds info" <| fun _ ->
    let event = SageFsEvent.WarmupCompleted (TimeSpan.FromSeconds 2.0, [])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    (activeOutput newModel).[0].Text
    |> Expect.equal "should say complete" "Warmup complete"

  testCase "WarmupCompleted with failures adds error lines" <| fun _ ->
    let event =
      SageFsEvent.WarmupCompleted (TimeSpan.FromSeconds 2.0, ["ns1"; "ns2"])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    activeOutput newModel
    |> Expect.hasLength "should have 2 error lines" 2

  testCase "WarmupContextUpdated preserves model identity when only invisible timestamps change" <| fun _ ->
    let initialStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5.0)
    let initialLoadedAt = DateTimeOffset.UtcNow.AddMinutes(-1.0)
    let existingCtx =
      mkHotPathSessionContext initialStartedAt (Some initialLoadedAt) Loaded true
    let existingFile = existingCtx.FileStatuses |> List.head
    let incomingCtx = {
      existingCtx with
        Warmup = { existingCtx.Warmup with StartedAt = initialStartedAt.AddMinutes 2.0 }
        FileStatuses = [{ existingFile with LastLoadedAt = Some (initialLoadedAt.AddMinutes 2.0) }]
    }
    let model = {
      SageFsModel.initial() with
        SessionContext = Some existingCtx
    }
    let updated, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.WarmupContextUpdated incomingCtx))
        model
    obj.ReferenceEquals(updated, model)
    |> Expect.isTrue "should not rebuild the model for non-render timestamp churn"
    updated.LiveTesting.TestState.StateVersion
    |> Expect.equal "should not rerun live-testing remap work" 0L
    effects |> Expect.isEmpty "no effects are produced for a no-op warmup update"

  testCase "WarmupContextUpdated changes model when file readiness changes" <| fun _ ->
    let existingCtx =
      mkHotPathSessionContext (DateTimeOffset.UtcNow.AddMinutes(-5.0)) None Loaded true
    let existingFile = existingCtx.FileStatuses |> List.head
    let incomingCtx = {
      existingCtx with
        FileStatuses = [{ existingFile with Readiness = Stale }]
    }
    let model = {
      SageFsModel.initial() with
        SessionContext = Some existingCtx
    }
    let updated, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.WarmupContextUpdated incomingCtx))
        model
    obj.ReferenceEquals(updated, model)
    |> Expect.isFalse "should rebuild the model when visible file readiness changes"
    updated.SessionContext
    |> Expect.equal "should store the new warmup context" (Some incomingCtx)
    updated.LiveTesting.TestState.StateVersion
    |> Expect.equal "should rerun live-testing remap work for a real change" 1L
    effects |> Expect.isEmpty "warmup updates still do not produce effects"

  testCase "FileReloaded success adds info line" <| fun _ ->
    let event =
      SageFsEvent.FileReloaded ("test.fs", TimeSpan.FromMilliseconds 50.0, Ok "loaded")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    (activeOutput newModel).[0].Kind
    |> Expect.equal "should be Info" OutputKind.Info

  testCase "FileReloaded failure adds error line" <| fun _ ->
    let event =
      SageFsEvent.FileReloaded ("test.fs", TimeSpan.FromMilliseconds 50.0, Error "parse error")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    (activeOutput newModel).[0].Kind
    |> Expect.equal "should be Error" OutputKind.Error

  testCase "EvalStarted adds info output line" <| fun _ ->
    let event = SageFsEvent.EvalStarted ("s1", "let x = 1")
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    let out = outputFor "s1" newModel
    out |> Expect.hasLength "should have 1 output line" 1
    out.[0].Kind |> Expect.equal "should be Info" OutputKind.Info
    out.[0].Text |> Expect.equal "should contain code" "let x = 1"
    out.[0].SessionId |> Expect.equal "should have session id" "s1"
    effects |> Expect.isEmpty "no effects"

  testCase "SessionStale marks session as stale" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap] }
    }
    let event = SageFsEvent.SessionStale ("aa000001", TimeSpan.FromMinutes 15.0)
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.Sessions.[0].Status
    |> Expect.equal "should be Stale" SessionDisplayStatus.Stale

  testCase "SessionCycleNext with 0 sessions is no-op" <| fun _ ->
    let model = (SageFsModel.initial())
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model
    effects |> Expect.isEmpty "no effects for empty sessions"

  testCase "SessionCycleNext with 1 session is no-op" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model
    effects |> Expect.isEmpty "no effects for single session"

  testCase "SessionCycleNext wraps from last to first" <| fun _ ->
    let mkSnap id = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Editor = { EditorState.initial with SelectedSessionIndex = Some 2 }
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000000"); mkSnap (testSessionId "aa000001"); mkSnap (testSessionId "aa000002")]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000002") } }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)] ->
      sid |> Expect.equal "should wrap to s0" "aa000000"
    | _ -> failtest "expected single RequestSessionSwitch effect"
    newModel.Editor.SelectedSessionIndex
    |> Expect.equal "index should wrap to 0" (Some 0)

  testCase "SessionCyclePrev with 0 sessions is no-op" <| fun _ ->
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCyclePrev) (SageFsModel.initial())
    effects |> Expect.isEmpty "no effects for empty sessions"

  testCase "SessionCyclePrev wraps from first to last" <| fun _ ->
    let mkSnap id = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Editor = { EditorState.initial with SelectedSessionIndex = Some 0 }
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000000"); mkSnap (testSessionId "aa000001"); mkSnap (testSessionId "aa000002")]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000000") } }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCyclePrev) model
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)] ->
      sid |> Expect.equal "should wrap to s2" "aa000002"
    | _ -> failtest "expected single RequestSessionSwitch effect"
    newModel.Editor.SelectedSessionIndex
    |> Expect.equal "index should wrap to 2" (Some 2)

  testCase "ClearOutput resets RecentOutput" <| fun _ ->
    let model = {
      (SageFsModel.initial()) with
        RecentOutput = SessionOutputStore.ofLines [
          { Kind = OutputKind.Result; Text = "line1"
            Timestamp = DateTime.UtcNow; SessionId = "s1" }
          { Kind = OutputKind.Error; Text = "line2"
            Timestamp = DateTime.UtcNow; SessionId = "s1" }
        ] }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.ClearOutput) model
    outputFor "s1" newModel |> Expect.isEmpty "output should be cleared"
    effects |> Expect.isEmpty "no effects"

  testCase "CreateSession blocked when already creating" <| fun _ ->
    let model = { (SageFsModel.initial()) with CreatingSession = true }
    let _, effects =
      SageFsUpdate.update
        (SageFsMsg.Editor (EditorAction.CreateSession ["Test.fsproj"]))
        model
    effects |> Expect.isEmpty "should block duplicate create"

  testCase "EvalFailed with Create failed: clears CreatingSession" <| fun _ ->
    let model = { (SageFsModel.initial()) with CreatingSession = true }
    let event = SageFsEvent.EvalFailed ("s1", "Create failed: some reason")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.CreatingSession
    |> Expect.isFalse "should clear CreatingSession"

  testCase "EvalFailed with normal error keeps CreatingSession" <| fun _ ->
    let model = { (SageFsModel.initial()) with CreatingSession = true }
    let event = SageFsEvent.EvalFailed ("s1", "type mismatch")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.CreatingSession
    |> Expect.isTrue "should keep CreatingSession"

  testCase "SessionStopped active falls back to next session" <| fun _ ->
    let snap1 = {
      Id = testSessionId "aa000001"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let snap2 = {
      Id = testSessionId "aa000002"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap1; snap2]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") }
        Diagnostics = Map.ofList ["aa000001", []; "aa000002", []] }
    let event = SageFsEvent.SessionStopped "aa000001"
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.Sessions
    |> Expect.hasLength "should have 1 session" 1
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should fall back to s2" (ActiveSession.Viewing (testSessionId "aa000002"))
    newModel.Sessions.Sessions.[0].IsActive
    |> Expect.isTrue "s2 should be active"
    newModel.Diagnostics |> Map.containsKey "aa000001"
    |> Expect.isFalse "s1 diagnostics removed"

  testCase "SessionStopped non-active doesn't change active" <| fun _ ->
    let snap1 = {
      Id = testSessionId "aa000001"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let snap2 = {
      Id = testSessionId "aa000002"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap1; snap2]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionStopped "aa000002")) model
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should stay s1" (ActiveSession.Viewing (testSessionId "aa000001"))

  testCase "SessionStopped last session → AwaitingSession" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionStopped "aa000001")) model
    newModel.Sessions.Sessions |> Expect.isEmpty "sessions empty"
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should be AwaitingSession" ActiveSession.AwaitingSession

  testCase "SessionStatusChanged updates status" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let event = SageFsEvent.SessionStatusChanged ("aa000001", SessionDisplayStatus.Errored "faulted")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    match newModel.Sessions.Sessions.[0].Status with
    | SessionDisplayStatus.Errored _ -> ()
    | other -> failtestf "expected Errored, got %A" other

  testCase "SessionStatusChanged for unknown session is no-op" <| fun _ ->
    let event = SageFsEvent.SessionStatusChanged ("x", SessionDisplayStatus.Errored "x")
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    effects |> Expect.isEmpty "no effects"
    newModel.Sessions.Sessions |> Expect.isEmpty "still empty"

  testCase "WarmupProgress adds step/total info line" <| fun _ ->
    let event = SageFsEvent.WarmupProgress(2, 4, "Loading namespaces")
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Event event) (SageFsModel.initial())
    effects |> Expect.isEmpty "no effects"
    let out = activeOutput newModel
    out |> Expect.hasLength "1 output line" 1
    out.[0].Kind
    |> Expect.equal "should be Info" OutputKind.Info
    out.[0].Text
    |> Expect.stringContains "should contain [2/4]" "[2/4]"

  testCase "TestResultsBatch keeps visible output quiet while results are still streaming" <| fun _ ->
    let baseModel = SageFsModel.initial()
    let generation = RunGeneration.next RunGeneration.zero
    let model = {
      baseModel with
        LiveTesting = {
          baseModel.LiveTesting with
            TestState = {
              baseModel.LiveTesting.TestState with
                RunPhases = Map.ofList ["s", Running generation]
                LastGeneration = generation
            }
        }
    }
    let results = [|
      mkPassedRunResult "test.batch.a" "test.batch.a" 42.0
      mkPassedRunResult "test.batch.b" "test.batch.b" 7.0
    |]
    let updated, effects =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestResultsBatch results)) model
    updated.RecentOutput.ActiveCount(updated.Sessions.ActiveSessionId)
    |> Expect.equal "streaming batches should not spam the visible output pane" 0
    updated.PendingTestResults
    |> PendingTestResultBuffer.count
    |> Expect.equal "results should still accumulate for the completion summary" 2
    updated.LiveTesting.TestState.LastResults
    |> Map.count
    |> Expect.equal "live testing state should still merge the incoming results" 2
    effects |> Expect.isEmpty "streaming batches do not produce follow-up effects"

  testCase "TestRunCompleted emits one summary for all prior result batches" <| fun _ ->
    let baseModel = SageFsModel.initial()
    let generation = RunGeneration.next RunGeneration.zero
    let model0 = {
      baseModel with
        LiveTesting = {
          baseModel.LiveTesting with
            TestState = {
              baseModel.LiveTesting.TestState with
                RunPhases = Map.ofList ["s", Running generation]
                LastGeneration = generation
            }
        }
    }
    let batch1 = [|
      mkPassedRunResult "test.summary.pass" "test.summary.pass" 10.0
      mkFailedRunResult "test.summary.fail" "test.summary.fail" "boom" 5.0
    |]
    let batch2 = [|
      mkSkippedRunResult "test.summary.skip" "test.summary.skip" "quarantined"
    |]
    let model1, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestResultsBatch batch1)) model0
    let model2, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestResultsBatch batch2)) model1
    let completed, effects =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.TestRunCompleted (Some "s"))) model2
    completed.RecentOutput.ActiveCount(completed.Sessions.ActiveSessionId)
    |> Expect.equal "completion should add exactly one visible summary line" 1
    let summary = completed.RecentOutput.GetActiveBuffer(completed.Sessions.ActiveSessionId)
    summary.[0].Kind
    |> Expect.equal "a failed run summary should be surfaced as an error" OutputKind.Error
    summary.[0].Text
    |> Expect.equal "summary should reflect every prior batch in the run"
         "🧪 Test run complete: 1 passed, 1 failed, 1 skipped (15ms)"
    completed.PendingTestResults
    |> PendingTestResultBuffer.count
    |> Expect.equal "completion should clear the pending result accumulator" 0
    effects |> Expect.isEmpty "run completion only updates model state"

  testCase "TestRunCompleted after RunningButEdited emits RequestRebuild for the latest affected compiled tests" <| fun _ ->
    let sid = "aa000001"
    let session = {
      Id = testSessionId sid
      Name = None
      Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow
      EvalCount = 0
      UpSince = DateTime.UtcNow
      IsActive = true
      WorkingDirectory = "." }
    let discovered = [|
      mkLiveTestCase "test.replay.active" "SageFs.Tests.Replay.active" "replay-active"
    |]
    let generation = RunGeneration.next RunGeneration.zero
    let content = "module Replay\nlet value = 1"
    let identity = AnalysisIdentity.ofContent content
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [ session ]
            ActiveSessionId = ActiveSession.Viewing (testSessionId sid) }
        LiveTesting =
          { LiveTestCycleState.empty with
              TestState =
                { LiveTestState.empty with
                    Activation = LiveTestingActivation.Active
                    DiscoveredTests = discovered
                    RunPhases = Map.ofList [ sid, RunningButEdited generation ]
                    LastGeneration = generation
                    TestSessionMap = Map.ofList [ discovered.[0].Id, sid ] }
              ActiveFile = Some "Replay.fs"
              LatestContent = Some content
              LatestAnalysisIdentity = Some identity
              LastTrigger = RunTrigger.FileSave } }

    let afterAnalysis, analysisEffects =
      SageFsUpdate.update
        (SageFsMsg.FcsTypeCheckCompleted (Some sid, Some identity, FcsTypeCheckResult.Failed ("Replay.fs", [ "syntax error" ])))
        model

    analysisEffects
    |> Expect.isEmpty
        "an edit that arrives during a running compiled test pass should queue rebuild work instead of emitting it immediately"

    afterAnalysis.LiveTesting.PendingRebuild
    |> Expect.isNone
        "queueing owed rebuild work must not pretend a rebuild has already started"

    let afterComplete, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestRunCompleted (Some sid)))
        afterAnalysis

    let pending =
      afterComplete.LiveTesting.PendingRebuild
      |> Expect.wantSome "completing the stale run should promote the queued rebuild into a real pending rebuild"

    pending.SessionId
    |> Expect.equal "replayed rebuild should stay scoped to the owning session" (Some sid)
    pending.Tests
    |> Expect.equal "replayed rebuild should keep the queued affected tests" discovered

    match effects with
    | [ SageFsEffect.TestCycle (TestCycleEffect.RequestRebuild (generation, req)) ] ->
        generation
        |> Expect.equal "replayed rebuild should use the pending rebuild generation it just minted" pending.Generation
        req.Tests
        |> Expect.equal "completion should replay the latest affected compiled tests" discovered
        req.Trigger
        |> Expect.equal "completion should preserve the save trigger that discovered the owed work" RunTrigger.FileSave
        req.SessionId
        |> Expect.equal "completion should replay against the owning session" (Some sid)
    | other ->
        failtestf "expected RequestRebuild after stale run completion, got %A" other

  testCase "background session completion replays only that session's queued compiled rebuild intent" <| fun _ ->
    let mkSnap id active = {
      Id = testSessionId id
      Name = None
      Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow
      EvalCount = 0
      UpSince = DateTime.UtcNow
      IsActive = active
      WorkingDirectory = "." }
    let primarySid = "aa000001"
    let backgroundSid = "aa000002"
    let backgroundTests = [|
      mkLiveTestCase "test.replay.background" "SageFs.Tests.Replay.background" "replay-background"
    |]
    let generation = RunGeneration.next RunGeneration.zero
    let content = "module Replay\nlet value = 2"
    let identity = AnalysisIdentity.ofContent content
    let primaryState =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.Keystroke }
    let backgroundState =
      { LiveTestCycleState.empty with
          TestState =
            { LiveTestState.empty with
                Activation = LiveTestingActivation.Active
                DiscoveredTests = backgroundTests
                RunPhases = Map.ofList [ backgroundSid, RunningButEdited generation ]
                LastGeneration = generation
                TestSessionMap = Map.ofList [ backgroundTests.[0].Id, backgroundSid ] }
          ActiveFile = Some "Background.fs"
          LatestContent = Some content
          LatestAnalysisIdentity = Some identity
          LastTrigger = RunTrigger.FileSave }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [ mkSnap primarySid true; mkSnap backgroundSid false ]
            ActiveSessionId = ActiveSession.Viewing (testSessionId primarySid) }
        LiveTesting = primaryState
        PerSessionLiveTesting = Map.ofList [ backgroundSid, backgroundState ] }

    let afterAnalysis, analysisEffects =
      SageFsUpdate.update
        (SageFsMsg.FcsTypeCheckCompleted (Some backgroundSid, Some identity, FcsTypeCheckResult.Failed ("Background.fs", [ "syntax error" ])))
        model

    analysisEffects
    |> Expect.isEmpty
        "background queued rebuild work should also wait until the stale run finishes"

    let afterComplete, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestRunCompleted (Some backgroundSid)))
        afterAnalysis

    afterComplete.LiveTesting.PendingRebuild
    |> Expect.isNone
        "background completion must not promote queued rebuild work onto the active primary session"

    let backgroundPending =
      afterComplete.PerSessionLiveTesting
      |> Map.find backgroundSid
      |> fun cycle -> cycle.PendingRebuild
      |> Expect.wantSome "background completion should promote only that session's queued rebuild"

    backgroundPending.SessionId
    |> Expect.equal "background replay should keep its own session ownership" (Some backgroundSid)

    match effects with
    | [ SageFsEffect.TestCycle (TestCycleEffect.RequestRebuild (_, req)) ] ->
        req.Tests
        |> Expect.equal "background completion should replay the queued background tests only" backgroundTests
        req.SessionId
        |> Expect.equal "background completion should emit RequestRebuild for the background session only" (Some backgroundSid)
    | other ->
        failtestf "expected one background RequestRebuild after completion, got %A" other

  testCase "EnableLiveTesting preserves status entries while still triggering follow-up work, and DisableLiveTesting stays quiet" <| fun _ ->
    let discovered = [|
      mkLiveTestCase "test.activation.a" "SageFs.Tests.Activation.tests/a" "activation-a"
      mkLiveTestCase "test.activation.b" "SageFs.Tests.Activation.tests/b" "activation-b"
    |]
    let discoveredModel, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s", discovered)))
        (SageFsModel.initial())
    let discoveredEntries = discoveredModel.LiveTesting.TestState.StatusEntries

    let enabled, enableEffects =
      SageFsUpdate.update
        SageFsMsg.EnableLiveTesting
        discoveredModel

    enabled.LiveTesting.TestState.Activation
    |> Expect.equal "enabling live testing should still update the activation flag"
         LiveTestingActivation.Active
    obj.ReferenceEquals(enabled.LiveTesting.TestState.StatusEntries, discoveredEntries)
    |> Expect.isTrue "enabling live testing should not rebuild identical per-test status entries"
    List.isEmpty enableEffects
    |> Expect.isFalse "enabling live testing should still trigger the follow-up discovery or execution work"

    let disabled, disableEffects =
      SageFsUpdate.update
        SageFsMsg.DisableLiveTesting
        enabled

    disabled.LiveTesting.TestState.Activation
    |> Expect.equal "disabling live testing should still update the activation flag"
         LiveTestingActivation.Inactive
    obj.ReferenceEquals(disabled.LiveTesting.TestState.StatusEntries, enabled.LiveTesting.TestState.StatusEntries)
    |> Expect.isTrue "disabling live testing should also preserve identical per-test status entries"
    disableEffects
    |> Expect.isEmpty "disabling live testing should not emit follow-up effects"

  testCase "TestRunStarted and TestRunCompleted only patch the affected status entries" <| fun _ ->
    let discovered = [|
      mkLiveTestCase "test.runstart.a" "SageFs.Tests.RunStart.tests/a" "runstart-a"
      mkLiveTestCase "test.runstart.b" "SageFs.Tests.RunStart.tests/b" "runstart-b"
    |]
    let discoveredModel, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s", discovered)))
        (SageFsModel.initial())
    let beforeEntries = discoveredModel.LiveTesting.TestState.StatusEntries
    let unaffectedBefore =
      beforeEntries |> Array.find (fun entry -> entry.TestId = discovered.[1].Id)

    let started, startEffects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| discovered.[0].Id |], Some "s")))
        discoveredModel
    let startedEntries = started.LiveTesting.TestState.StatusEntries
    let affectedStarted =
      startedEntries |> Array.find (fun entry -> entry.TestId = discovered.[0].Id)
    let unaffectedStarted =
      startedEntries |> Array.find (fun entry -> entry.TestId = discovered.[1].Id)

    obj.ReferenceEquals(unaffectedStarted, unaffectedBefore)
    |> Expect.isTrue "starting a run should preserve unaffected status entry objects"
    affectedStarted.PreviousStatus
    |> Expect.equal "the started test should remember its prior detected status" TestRunStatus.Detected
    affectedStarted.Status
    |> Expect.equal "the started test should transition to running" TestRunStatus.Running
    startEffects
    |> Expect.isEmpty "the run-start event itself should only mutate model state"

    let completed, completeEffects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestRunCompleted (Some "s")))
        started
    let completedEntries = completed.LiveTesting.TestState.StatusEntries
    let affectedCompleted =
      completedEntries |> Array.find (fun entry -> entry.TestId = discovered.[0].Id)
    let unaffectedCompleted =
      completedEntries |> Array.find (fun entry -> entry.TestId = discovered.[1].Id)

    obj.ReferenceEquals(unaffectedCompleted, unaffectedStarted)
    |> Expect.isTrue "completing a run should also preserve unaffected status entry objects"
    affectedCompleted.PreviousStatus
    |> Expect.equal "completion should remember the running status it is leaving" TestRunStatus.Running
    affectedCompleted.Status
    |> Expect.equal "a completed run with no result should return the test to detected" TestRunStatus.Detected
    completeEffects
    |> Expect.isEmpty "run completion should not emit follow-up effects"

  testCase "AffectedTestsComputed only patches the targeted status entries" <| fun _ ->
    let discovered = [|
      mkLiveTestCase "test.affected.a" "SageFs.Tests.Affected.tests/a" "affected-a"
      mkLiveTestCase "test.affected.b" "SageFs.Tests.Affected.tests/b" "affected-b"
    |]
    let discoveredModel, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s", discovered)))
        (SageFsModel.initial())
    let activeModel, _ =
      SageFsUpdate.update
        SageFsMsg.EnableLiveTesting
        discoveredModel
    let beforeEntries = activeModel.LiveTesting.TestState.StatusEntries
    let unaffectedBefore =
      beforeEntries |> Array.find (fun entry -> entry.TestId = discovered.[1].Id)

    let updated, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.AffectedTestsComputed [| discovered.[0].Id |]))
        activeModel
    let updatedEntries = updated.LiveTesting.TestState.StatusEntries
    let affectedUpdated =
      updatedEntries |> Array.find (fun entry -> entry.TestId = discovered.[0].Id)
    let unaffectedUpdated =
      updatedEntries |> Array.find (fun entry -> entry.TestId = discovered.[1].Id)

    obj.ReferenceEquals(unaffectedUpdated, unaffectedBefore)
    |> Expect.isTrue "affected-test computation should preserve unaffected status entry objects"
    affectedUpdated.PreviousStatus
    |> Expect.equal "affected-test computation should remember the prior detected status" TestRunStatus.Detected
    affectedUpdated.Status
    |> Expect.equal "affected-test computation should queue the targeted test" TestRunStatus.Queued
    List.isEmpty effects
    |> Expect.isFalse "affected-test computation should still emit follow-up execution work"

  testCase "RunTestsRequested only patches the targeted status entries" <| fun _ ->
    let discovered = [|
      mkLiveTestCase "test.requested.a" "SageFs.Tests.Requested.tests/a" "requested-a"
      mkLiveTestCase "test.requested.b" "SageFs.Tests.Requested.tests/b" "requested-b"
    |]
    let discoveredModel, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s", discovered)))
        (SageFsModel.initial())
    let activeModel, _ =
      SageFsUpdate.update
        SageFsMsg.EnableLiveTesting
        discoveredModel
    let beforeEntries = activeModel.LiveTesting.TestState.StatusEntries
    let unaffectedBefore =
      beforeEntries |> Array.find (fun entry -> entry.TestId = discovered.[1].Id)

    let updated, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.RunTestsRequested [| discovered.[0] |]))
        activeModel
    let updatedEntries = updated.LiveTesting.TestState.StatusEntries
    let affectedUpdated =
      updatedEntries |> Array.find (fun entry -> entry.TestId = discovered.[0].Id)
    let unaffectedUpdated =
      updatedEntries |> Array.find (fun entry -> entry.TestId = discovered.[1].Id)

    obj.ReferenceEquals(unaffectedUpdated, unaffectedBefore)
    |> Expect.isTrue "explicit run requests should preserve unaffected status entry objects"
    affectedUpdated.PreviousStatus
    |> Expect.equal "explicit run requests should remember the prior detected status" TestRunStatus.Detected
    affectedUpdated.Status
    |> Expect.equal "explicit run requests should transition the targeted test to running" TestRunStatus.Running
    List.isEmpty effects
    |> Expect.isFalse "explicit run requests should still emit the run effect"

  testCase "duplicate TestsDiscovered preserves model identity when nothing new was learned" <| fun _ ->
    let discovered =
      mkLiveTestCase
        "test.duplicate"
        "SageFs.Tests.MyModule.tests/duplicate"
        "duplicate"
    let model1, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s-1", [| discovered |])))
        (SageFsModel.initial())
    let firstDiscoveryTime = model1.LiveTesting.TestState.LastDiscoveryTime
    let model2, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s-1", [| discovered |])))
        model1
    obj.ReferenceEquals(model2, model1)
    |> Expect.isTrue "duplicate discovery should be observationally silent"
    model2.LiveTesting.TestState.LastDiscoveryTime
    |> Expect.equal "duplicate discovery should not churn the discovery clock" firstDiscoveryTime
    effects |> Expect.isEmpty "duplicate discovery should not emit effects"

  testCase "duplicate TestsDiscovered does not retrigger execution when activation is already active" <| fun _ ->
    let discovered =
      mkLiveTestCase
        "test.duplicate.active"
        "SageFs.Tests.MyModule.tests/duplicate-active"
        "duplicate-active"
    let baseModel = {
      SageFsModel.initial() with
        LiveTesting = {
          SageFsModel.initial().LiveTesting with
            TestState = {
              SageFsModel.initial().LiveTesting.TestState with
                Activation = LiveTestingActivation.Active
            }
        }
    }
    let model1, firstEffects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s-1", [| discovered |])))
        baseModel
    firstEffects
    |> Expect.hasLength "initial discovery should still trigger one execution request" 1
    let _, duplicateEffects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s-1", [| discovered |])))
        model1
    duplicateEffects
    |> Expect.isEmpty "duplicate discovery should not retrigger execution"

  testCase "same-session rediscovery replaces prior session-scoped tests when identity changes" <| fun _ ->
    let sessionOneOriginal =
      mkLiveTestCase
        "test.rediscovery.original"
        "SageFs.Tests.MyModule.tests/add"
        "add"
    let sessionOneUpdated =
      mkLiveTestCase
        "test.rediscovery.updated"
        "SageFs.Tests.MyModule.tests/type-inference/add"
        "add"
    let otherSession =
      mkLiveTestCase
        "test.rediscovery.other-session"
        "SageFs.Tests.OtherModule.tests/keep"
        "keep"

    let model1, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s-1", [| sessionOneOriginal |])))
        (SageFsModel.initial())

    let model2, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s-2", [| otherSession |])))
        model1

    let model3, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s-1", [| sessionOneUpdated |])))
        model2

    model3.LiveTesting.TestState.DiscoveredTests
    |> Expect.hasLength "rediscovery should keep one test for each session" 2

    model3.LiveTesting.TestState.DiscoveredTests
    |> Array.exists (fun tc -> tc.Id = sessionOneUpdated.Id)
    |> Expect.isTrue "rediscovery should keep the updated test identity for the session"

    model3.LiveTesting.TestState.DiscoveredTests
    |> Array.exists (fun tc -> tc.Id = sessionOneOriginal.Id)
    |> Expect.isFalse "rediscovery should drop the superseded test identity for the same session"

    model3.LiveTesting.TestState.TestSessionMap
    |> Map.tryFind sessionOneOriginal.Id
    |> Expect.isNone "superseded test identity should be removed from the session map"

  testCase "duplicate TestLocationsDetected preserves model identity when source truth is unchanged" <| fun _ ->
    let discovered =
      mkLiveTestCase
        "test.source-duplicate"
        "SageFs.Tests.MyModule.tests/source-duplicate"
        "source-duplicate"
    let locations = [|
      mkSourceTestLocation "Tests" "tests" "MyModule.fs" 41
    |]
    let model1, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s-1", [| discovered |])))
        (SageFsModel.initial())
    let model2, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestLocationsDetected ("s-1", locations)))
        model1
    let model3, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.TestLocationsDetected ("s-1", locations)))
        model2
    obj.ReferenceEquals(model3, model2)
    |> Expect.isTrue "duplicate source locations should not force another rerender"
    effects |> Expect.isEmpty "duplicate source locations should not emit effects"

  testCase "queue coalescing merges pending test result batches without losing any completed test facts" <| fun _ ->
    let batch1 = [|
      mkPassedRunResult "test.queue.a" "test.queue.a" 10.0
      mkFailedRunResult "test.queue.b" "test.queue.b" "boom" 4.0
    |]
    let batch2 = [|
      mkSkippedRunResult "test.queue.c" "test.queue.c" "quarantined"
      mkPassedRunResult "test.queue.d" "test.queue.d" 2.0
    |]
    let pending = ResizeArray<SageFsMsg>()
    pending.Add(SageFsMsg.Event (SageFsEvent.TestResultsBatch batch1))

    let absorbed =
      SageFsMsgQueueCoalescing.tryAbsorbPending
        pending
        (SageFsMsg.Event (SageFsEvent.TestResultsBatch batch2))

    absorbed
    |> Expect.isTrue "result batches from the same pending run should merge so redraw work collapses without dropping any tests"
    pending
    |> Expect.hasLength "merged result batches should still occupy one pending slot" 1

    match pending[0] with
    | SageFsMsg.BufferedTestResults buffered ->
      buffered.Batches
      |> Seq.collect (fun batch -> batch |> Seq.map (fun result -> result.TestName))
      |> Seq.toArray
      |> Expect.equal "every individual result should survive the buffered merge in arrival order"
           [| "test.queue.a"; "test.queue.b"; "test.queue.c"; "test.queue.d" |]
    | other ->
      failtestf "expected buffered test results, got %A" other

  testCase "queue coalescing does not merge test result batches across a pending run completion boundary" <| fun _ ->
    let batch1 = [|
      mkPassedRunResult "test.queue.boundary.a" "test.queue.boundary.a" 8.0
    |]
    let batch2 = [|
      mkPassedRunResult "test.queue.boundary.b" "test.queue.boundary.b" 6.0
    |]
    let pending = ResizeArray<SageFsMsg>()
    pending.Add(SageFsMsg.Event (SageFsEvent.TestResultsBatch batch1))
    pending.Add(SageFsMsg.Event (SageFsEvent.TestRunCompleted (Some "s-queue")))

    let absorbed =
      SageFsMsgQueueCoalescing.tryAbsorbPending
        pending
        (SageFsMsg.Event (SageFsEvent.TestResultsBatch batch2))

    absorbed
    |> Expect.isFalse "a completion marker closes the pending run segment, so later batches must stay separate"
    pending
    |> Expect.hasLength "the pending queue should remain unchanged when a lifecycle boundary blocks merging" 2

  testCase "queue coalescing keeps two medium test result batches in one pending refresh unit even after the raw flattening cap would have split them" <| fun _ ->
    let batch1 =
      Array.init 60 (fun i ->
        let name = sprintf "test.queue.medium.a.%d" i
        mkPassedRunResult name name 1.0)
    let batch2 =
      Array.init 60 (fun i ->
        let name = sprintf "test.queue.medium.b.%d" i
        mkPassedRunResult name name 1.0)
    let pending = ResizeArray<SageFsMsg>()
    pending.Add(SageFsMsg.Event (SageFsEvent.TestResultsBatch batch1))

    let incoming = SageFsMsg.Event (SageFsEvent.TestResultsBatch batch2)
    let absorbed =
      SageFsMsgQueueCoalescing.tryAbsorbPending pending incoming

    absorbed
    |> Expect.isTrue "derived-state refresh dominates the hot path, so medium result batches should stay buffered together even when flattening them into one raw array would be too large"
    pending
    |> Expect.hasLength "buffered merging should still leave one pending refresh unit" 1

    match pending[0] with
    | SageFsMsg.BufferedTestResults buffered ->
      let expectedNames =
        Array.append
          (batch1 |> Array.map (fun result -> result.TestName))
          (batch2 |> Array.map (fun result -> result.TestName))
      buffered.TotalResultCount
      |> Expect.equal "the buffered payload should remember the total number of raw results it carries" 120
      buffered.Batches
      |> Seq.collect (fun batch -> batch |> Seq.map (fun result -> result.TestName))
      |> Seq.toArray
      |> Expect.equal "every raw result should still survive in arrival order inside the buffered payload"
           expectedNames
    | other ->
      failtestf "expected buffered test results, got %A" other

  testCase "queue coalescing still splits test result batches once the buffered payload would become pathologically large" <| fun _ ->
    let batch1 =
      Array.init 220 (fun i ->
        let name = sprintf "test.queue.large.a.%d" i
        mkPassedRunResult name name 1.0)
    let batch2 =
      Array.init 220 (fun i ->
        let name = sprintf "test.queue.large.b.%d" i
        mkPassedRunResult name name 1.0)
    let pending = ResizeArray<SageFsMsg>()
    pending.Add(SageFsMsg.Event (SageFsEvent.TestResultsBatch batch1))

    let incoming = SageFsMsg.Event (SageFsEvent.TestResultsBatch batch2)
    let absorbed =
      SageFsMsgQueueCoalescing.tryAbsorbPending pending incoming

    absorbed
    |> Expect.isFalse "once the buffered payload itself would become too large, the incoming batch should stay separate so one drain cannot monopolize the Elm loop"
    pending
    |> Expect.hasLength "the existing pending batch should remain untouched when the cap blocks merging" 1

    pending.Add incoming
    pending
    |> Expect.hasLength "the caller can still enqueue the incoming batch separately without losing any result facts" 2

    let allNames =
      pending
      |> Seq.collect (function
        | SageFsMsg.Event (SageFsEvent.TestResultsBatch batch) -> batch |> Seq.map (fun result -> result.TestName)
        | _ -> Seq.empty)
      |> Seq.toArray

    let expectedNames =
      Array.append
        (batch1 |> Array.map (fun result -> result.TestName))
        (batch2 |> Array.map (fun result -> result.TestName))

    allNames
    |> Expect.equal "every completed test fact should still survive when capped batching leaves two pending result messages"
         expectedNames

  testCase "BufferedTestResults applies multiple streamed batches with one derived-state refresh while preserving every result fact" <| fun _ ->
    let discovered = [|
      mkLiveTestCase "test.buffered.a" "SageFs.Tests.Buffered.tests/a" "buffered-a"
      mkLiveTestCase "test.buffered.b" "SageFs.Tests.Buffered.tests/b" "buffered-b"
    |]
    let generation = RunGeneration.next RunGeneration.zero
    let baseModel =
      let discoveredModel, _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s", discovered)))
          (SageFsModel.initial())
      let startedModel, _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestRunStarted (discovered |> Array.map (fun t -> t.Id), Some "s")))
          discoveredModel
      startedModel

    let batch1 = [| mkPassedRunResult "test.buffered.a" "test.buffered.a" 11.0 |]
    let batch2 = [| mkFailedRunResult "test.buffered.b" "test.buffered.b" "boom" 7.0 |]
    let updated, effects =
      SageFsUpdate.update
        (SageFsMsg.BufferedTestResults {
          TotalResultCount = batch1.Length + batch2.Length
          Batches = [ batch1; batch2 ]
        })
        baseModel

    updated.LiveTesting.TestState.StateVersion
    |> Expect.equal "buffering multiple streamed batches into one refresh unit should only bump the cached live-testing state once" (baseModel.LiveTesting.TestState.StateVersion + 1L)
    updated.PendingTestResults
    |> PendingTestResultBuffer.toArray
    |> Array.map (fun result -> TestId.value result.TestId)
    |> Expect.equal "every result fact should still accumulate for the eventual completion summary"
         [| "test.buffered.b"; "test.buffered.a" |]
    Features.LiveTesting.LiveTestState.statusEntriesForSession "" updated.LiveTesting.TestState
    |> Array.map (fun entry -> TestId.value entry.TestId, entry.Status)
    |> Map.ofArray
    |> fun statuses ->
      Map.find "test.buffered.a" statuses
      |> Expect.equal "the buffered refresh should surface the passed test status" (TestRunStatus.Passed (TimeSpan.FromMilliseconds 11.0))
      Map.find "test.buffered.b" statuses
      |> Expect.equal "the buffered refresh should surface the failed test status" (TestRunStatus.Failed (TestFailure.AssertionFailed "boom", TimeSpan.FromMilliseconds 7.0))
    effects |> Expect.isEmpty "buffered streamed results should not emit follow-up effects"

  testCase "queue coalescing keeps only the latest pending session refresh truth" <| fun _ ->
    let snap1 = {
      Id = testSessionId "aa000100"; Name = Some "pending-a"; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Starting
      LastActivity = DateTime.UtcNow.AddMinutes(-5.0)
      EvalCount = 1
      UpSince = DateTime.UtcNow.AddMinutes(-10.0)
      IsActive = false
      WorkingDirectory = @"C:\Code\Repos\SageFs" }
    let snap2 = {
      snap1 with
        Status = SessionDisplayStatus.Running
        EvalCount = 7
        LastActivity = DateTime.UtcNow }
    let pending = ResizeArray<SageFsMsg>()
    pending.Add(SageFsMsg.Event (SageFsEvent.SessionsRefreshed [snap1]))

    let absorbed =
      SageFsMsgQueueCoalescing.tryAbsorbPending
        pending
        (SageFsMsg.Event (SageFsEvent.SessionsRefreshed [snap2]))

    absorbed
    |> Expect.isTrue "stale session snapshots should collapse to the latest truth while the loop is still busy"
    pending
    |> Expect.hasLength "only one pending refresh should remain after coalescing" 1

    match pending[0] with
    | SageFsMsg.Event (SageFsEvent.SessionsRefreshed [merged]) ->
      merged.Status
      |> Expect.equal "the latest refresh should win because older session truth is stale by the time it renders"
           SessionDisplayStatus.Running
      merged.EvalCount
      |> Expect.equal "the latest refresh should preserve the newest counters as well" 7
    | other ->
      failtestf "expected merged SessionsRefreshed, got %A" other

  testCase "queue coalescing keeps only the latest pending warmup context truth" <| fun _ ->
    let context1 =
      mkHotPathSessionContext
        (DateTimeOffset.UtcNow.AddMinutes(-2.0))
        (Some (DateTimeOffset.UtcNow.AddMinutes(-1.0)))
        FileReadiness.Loaded
        true
    let context2 =
      mkHotPathSessionContext
        (DateTimeOffset.UtcNow.AddMinutes(-2.0))
        (Some DateTimeOffset.UtcNow)
        FileReadiness.Stale
        true
    let pending = ResizeArray<SageFsMsg>()
    pending.Add(SageFsMsg.Event (SageFsEvent.WarmupContextUpdated context1))

    let absorbed =
      SageFsMsgQueueCoalescing.tryAbsorbPending
        pending
        (SageFsMsg.Event (SageFsEvent.WarmupContextUpdated context2))

    absorbed
    |> Expect.isTrue "warmup snapshots should collapse to the latest visible truth while the loop is still draining older work"
    pending
    |> Expect.hasLength "only one pending warmup snapshot should remain after coalescing" 1

    match pending[0] with
    | SageFsMsg.Event (SageFsEvent.WarmupContextUpdated merged) ->
      let file = merged.FileStatuses |> List.head
      file.Readiness
      |> Expect.equal "the newest warmup readiness should win over stale pending state"
           FileReadiness.Stale
    | other ->
      failtestf "expected merged WarmupContextUpdated, got %A" other

  testCase "queue coalescing keeps only one pending ListSessions refresh request" <| fun _ ->
    let pending = ResizeArray<SageFsMsg>()
    pending.Add(SageFsMsg.Editor EditorAction.ListSessions)

    let absorbed =
      SageFsMsgQueueCoalescing.tryAbsorbPending
        pending
        (SageFsMsg.Editor EditorAction.ListSessions)

    absorbed
    |> Expect.isTrue "repeating the same list-sessions poll while one is already pending only adds stale redraw pressure"
    pending
    |> Expect.hasLength "repeated list-sessions requests should collapse to one pending refresh" 1

  testCase "dispatch reduction collapses contiguous buffered test result work into one refresh unit but stops at editor barriers" <| fun _ ->
    let batch1 = [| mkPassedRunResult "test.reduce.a" "test.reduce.a" 1.0 |]
    let batch2 = [| mkPassedRunResult "test.reduce.b" "test.reduce.b" 1.0 |]
    let batch3 = [| mkPassedRunResult "test.reduce.c" "test.reduce.c" 1.0 |]
    let batch4 = [| mkPassedRunResult "test.reduce.d" "test.reduce.d" 1.0 |]
    let reduced =
      [|
        SageFsMsg.Event (SageFsEvent.TestResultsBatch batch1)
        SageFsMsg.BufferedTestResults { TotalResultCount = batch2.Length; Batches = [ batch2 ] }
        SageFsMsg.BufferedTestResults { TotalResultCount = batch3.Length; Batches = [ batch3 ] }
        SageFsMsg.Editor EditorAction.ListSessions
        SageFsMsg.Event (SageFsEvent.TestResultsBatch batch4)
      |]
      |> SageFsDispatchReduction.reduceDispatchBatch

    reduced
    |> Expect.hasLength "contiguous result work before the editor barrier should collapse into one buffered refresh unit, while later work stays separate" 3

    match reduced[0], reduced[1], reduced[2] with
    | SageFsMsg.BufferedTestResults combined,
      SageFsMsg.Editor EditorAction.ListSessions,
      SageFsMsg.BufferedTestResults trailing ->
      combined.TotalResultCount
      |> Expect.equal "the reducer should accumulate the contiguous result batches before the barrier" 3
      combined.Batches
      |> Seq.collect (fun batch -> batch |> Seq.map (fun result -> result.TestName))
      |> Seq.toArray
      |> Expect.equal "the reducer should preserve arrival order inside the collapsed buffered payload"
           [| "test.reduce.a"; "test.reduce.b"; "test.reduce.c" |]
      trailing.TotalResultCount
      |> Expect.equal "the post-barrier result batch should remain in its own buffered unit" 1
    | other ->
      failtestf "expected [BufferedTestResults; Editor.ListSessions; BufferedTestResults], got %A" other

  testCase "DiagnosticsUpdated overwrites previous for same session" <| fun _ ->
    let diag1 = {
      Message = "first"; Subcategory = "a"
      Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 1 }
      Severity = DiagnosticSeverity.Error }
    let diag2 = {
      Message = "second"; Subcategory = "b"
      Range = { StartLine = 2; StartColumn = 0; EndLine = 2; EndColumn = 1 }
      Severity = DiagnosticSeverity.Warning }
    let model = {
      (SageFsModel.initial()) with
        Diagnostics = Map.ofList ["s1", [diag1]] }
    let event = SageFsEvent.DiagnosticsUpdated ("s1", [diag2])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    let diags = newModel.Diagnostics |> Map.find "s1"
    diags |> Expect.hasLength "should replace with 1 diag" 1
    diags.[0].Message |> Expect.equal "should be the new diag" "second"

  testCase "SessionCreated auto-selects first session" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["A.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap)) (SageFsModel.initial())
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should auto-select" (ActiveSession.Viewing (testSessionId "aa000001"))
    newModel.Sessions.Sessions.[0].IsActive
    |> Expect.isTrue "should be marked active"
    newModel.CreatingSession
    |> Expect.isFalse "should clear CreatingSession"

  testCase "SessionCreated second session doesn't override active" <| fun _ ->
    let snap1 = {
      Id = testSessionId "aa000001"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap1]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let snap2 = {
      Id = testSessionId "aa000002"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "." }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap2)) model
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should still be s1" (ActiveSession.Viewing (testSessionId "aa000001"))
    newModel.Sessions.Sessions
    |> Expect.hasLength "should have 2 sessions" 2

  testCase "SessionSwitched marks only target active" <| fun _ ->
    let mkSnap id active = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = active; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false; mkSnap (testSessionId "aa000003") false]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") } }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (Some "aa000001", "aa000003"))) model
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should be s3" (ActiveSession.Viewing (testSessionId "aa000003"))
    newModel.Sessions.Sessions
    |> List.filter (fun s -> s.IsActive)
    |> Expect.hasLength "only s3 active" 1

  testCase "SessionSwitched promotes target live-testing state to primary" <| fun _ ->
    let mkSnap id active = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = active; WorkingDirectory = "." }
    let primaryState =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.FileSave }
    let backgroundState =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.Keystroke }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") }
        LiveTesting = primaryState
        PerSessionLiveTesting = Map.ofList [ "aa000002", backgroundState ] }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (Some "aa000001", "aa000002"))) model
    newModel.LiveTesting.LastTrigger
    |> Expect.equal "active live-testing state should now be session aa000002's state" RunTrigger.Keystroke
    newModel.PerSessionLiveTesting
    |> Map.containsKey "aa000002"
    |> Expect.isFalse "promoted target session should no longer also live in the background map"

  testCase "SessionSwitched parks previous primary live-testing state under the previous session id" <| fun _ ->
    let mkSnap id active = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = active; WorkingDirectory = "." }
    let primaryState =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.FileSave }
    let backgroundState =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.Keystroke }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") }
        LiveTesting = primaryState
        PerSessionLiveTesting = Map.ofList [ "aa000002", backgroundState ] }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (Some "aa000001", "aa000002"))) model
    newModel.PerSessionLiveTesting
    |> Map.find "aa000001"
    |> fun cycle -> cycle.LastTrigger
    |> Expect.equal "previous active session state should be preserved under its session id" RunTrigger.FileSave

  testCase "SessionSwitched round-trips live-testing state between sessions" <| fun _ ->
    let mkSnap id active = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = active; WorkingDirectory = "." }
    let stateA =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.FileSave }
    let stateB =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.Keystroke }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") }
        LiveTesting = stateA
        PerSessionLiveTesting = Map.ofList [ "aa000002", stateB ] }
    let afterB, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (Some "aa000001", "aa000002"))) model
    let afterA, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (Some "aa000002", "aa000001"))) afterB
    afterA.LiveTesting.LastTrigger
    |> Expect.equal "switching back should restore session aa000001's live-testing state" RunTrigger.FileSave
    afterA.PerSessionLiveTesting
    |> Map.find "aa000002"
    |> fun cycle -> cycle.LastTrigger
    |> Expect.equal "session aa000002 should be demoted back to the background map with its state intact" RunTrigger.Keystroke

  testCase "SessionSwitched preserves the promoted session's rebuilding banner so users know tests are waiting on compilation" <| fun _ ->
    let mkSnap id active = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = active; WorkingDirectory = "." }
    let mkTestCase name =
      { Id = TestId.create name TestFramework.Expecto
        FullName = name
        DisplayName = name
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = TestFramework.Expecto
        Category = TestCategory.Unit }
    let pending =
      { Generation = 1L
        Tests =
          [| mkTestCase "MyTests.test1"
             mkTestCase "MyTests.test2" |]
        Trigger = RunTrigger.Keystroke
        FilePath = "Background.fs"
        AnalysisIdentity = None
        TreeSitterElapsed = TimeSpan.FromMilliseconds 5.0
        FcsElapsed = TimeSpan.FromMilliseconds 10.0
        SessionId = Some "aa000002"
        InstrumentationMaps = [||] }
    let primaryState =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.FileSave }
    let backgroundState =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.Keystroke
          NextRebuildGeneration = pending.Generation
          PendingRebuild = Some pending }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") }
        LiveTesting = primaryState
        PerSessionLiveTesting = Map.ofList [ "aa000002", backgroundState ] }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (Some "aa000001", "aa000002"))) model
    LiveTestCycleState.liveTestingStatusBarForSession "aa000002" newModel.LiveTesting
    |> Expect.stringContains
      "promoted session should keep the rebuilding banner so the active UI stays truthful"
      "🔨 Rebuilding 2 tests"

  testCase "SessionSwitched preserves both sessions' pending rebuild identities when parking and promoting live-testing state" <| fun _ ->
    let mkSnap id active = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = active; WorkingDirectory = "." }
    let mkTestCase name =
      { Id = TestId.create name TestFramework.Expecto
        FullName = name
        DisplayName = name
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = TestFramework.Expecto
        Category = TestCategory.Unit }
    let pendingA = {
      Generation = 3L
      Tests = [| mkTestCase "SessionA.Tests.pending" |]
      Trigger = RunTrigger.FileSave
      FilePath = "SessionA.fs"
      AnalysisIdentity = None
      TreeSitterElapsed = TimeSpan.FromMilliseconds 2.0
      FcsElapsed = TimeSpan.FromMilliseconds 4.0
      SessionId = Some "aa000001"
      InstrumentationMaps = [||] }
    let pendingB = {
      Generation = 7L
      Tests = [| mkTestCase "SessionB.Tests.pending" |]
      Trigger = RunTrigger.Keystroke
      FilePath = "SessionB.fs"
      AnalysisIdentity = None
      TreeSitterElapsed = TimeSpan.FromMilliseconds 3.0
      FcsElapsed = TimeSpan.FromMilliseconds 6.0
      SessionId = Some "aa000002"
      InstrumentationMaps = [||] }
    let stateA =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.FileSave
          NextRebuildGeneration = pendingA.Generation
          PendingRebuild = Some pendingA }
    let stateB =
      { LiveTestCycleState.empty with
          LastTrigger = RunTrigger.Keystroke
          NextRebuildGeneration = pendingB.Generation
          PendingRebuild = Some pendingB }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") }
        LiveTesting = stateA
        PerSessionLiveTesting = Map.ofList [ "aa000002", stateB ] }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (Some "aa000001", "aa000002"))) model

    let promotedPending =
      newModel.LiveTesting.PendingRebuild
      |> Expect.wantSome "promoted session should keep its pending rebuild"

    promotedPending.Generation
    |> Expect.equal "promoted session should keep its rebuild generation" pendingB.Generation
    promotedPending.SessionId
    |> Expect.equal "promoted session should keep its target session id" pendingB.SessionId

    let parkedPending =
      newModel.PerSessionLiveTesting
      |> Map.find "aa000001"
      |> fun cycle -> cycle.PendingRebuild
      |> Expect.wantSome "parked primary session should keep its pending rebuild"

    parkedPending.Generation
    |> Expect.equal "parked primary session should keep its rebuild generation" pendingA.Generation
    parkedPending.SessionId
    |> Expect.equal "parked primary session should keep its target session id" pendingA.SessionId

  testCase "live testing status bar explains when ambient reruns were conservative instead of exact" <| fun _ ->
    let decision =
      LiveTestingDecision.fromSelection
        (RerunCause.FileSaved "src/Compiled.fs")
        SelectionPrecision.ConservativeFallback
        []
        [| "Compiled.Tests.should_build_a"; "Compiled.Tests.should_build_b" |]
        [||]
        "fallback rebuild"
    let state =
      { LiveTestCycleState.empty with
          TestState =
            { LiveTestState.empty with
                Activation = LiveTestingActivation.Active
                LastDecision = Some decision } }

    LiveTestCycleState.liveTestingStatusBar state
    |> Expect.stringContains
      "status bar should surface fallback explanation so quiet ambient work is interpretable"
      "why: fallback rebuild (2 selected)"

  testCase "live testing status bar explains when policy intentionally deferred ambient work" <| fun _ ->
    let decision =
      LiveTestingDecision.fromSelection
        (RerunCause.KeystrokeBuffered "src/Architecture.fs")
        SelectionPrecision.SuppressedByPolicy
        [ "Architecture.Rule" ]
        [||]
        [| "Architecture.Tests.should_hold" |]
        "suppressed"
    let state =
      { LiveTestCycleState.empty with
          TestState =
            { LiveTestState.empty with
                Activation = LiveTestingActivation.Active
                LastDecision = Some decision } }

    LiveTestCycleState.liveTestingStatusBar state
    |> Expect.stringContains
      "status bar should surface policy deferral so silence reads as intentional"
      "why: deferred by policy (1)"

  testCase "SessionSwitched does not emit live-testing effects" <| fun _ ->
    let mkSnap id active = {
      Id = id; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = active; WorkingDirectory = "." }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
            ActiveSessionId = ActiveSession.Viewing (testSessionId "aa000001") }
        LiveTesting =
          { LiveTestCycleState.empty with LastTrigger = RunTrigger.FileSave }
        PerSessionLiveTesting =
          Map.ofList [ "aa000002", { LiveTestCycleState.empty with LastTrigger = RunTrigger.Keystroke } ] }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (Some "aa000001", "aa000002"))) model
    effects
    |> Expect.isEmpty "switching sessions should only reassociate live-testing state, not start new work"

  testCase "EnableLiveTesting with discovered tests emits RunAffectedTests" <| fun _ ->
    let tc : Features.LiveTesting.TestCase =
      { Id = Features.LiveTesting.TestId.create "MyModule.test1" TestFramework.Expecto
        FullName = "MyModule.test1"; DisplayName = "test1"
        Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
        Labels = []; Framework = TestFramework.Expecto
        Category = Features.LiveTesting.TestCategory.Unit }
    let model =
      { (SageFsModel.initial()) with
          LiveTesting =
            { (SageFsModel.initial()).LiveTesting with
                TestState =
                  { (SageFsModel.initial()).LiveTesting.TestState with
                      Activation = Features.LiveTesting.LiveTestingActivation.Inactive
                      DiscoveredTests = [| tc |] } } }
    let newModel, effects =
      SageFsUpdate.update SageFsMsg.EnableLiveTesting model
    newModel.LiveTesting.TestState.Activation
    |> Expect.equal "should be active" Features.LiveTesting.LiveTestingActivation.Active
    effects |> Expect.isNonEmpty "should emit effects when activating with tests"
    effects
    |> List.exists (fun e ->
      match e with
      | SageFsEffect.TestCycle (Features.LiveTesting.TestCycleEffect.RunAffectedTests _) -> true
      | _ -> false)
    |> Expect.isTrue "should contain RunAffectedTests effect"

  testCase "EnableLiveTesting with no tests emits an initial discovery request" <| fun _ ->
    let sessionId = testSessionId "aa000001"
    let session : SessionSnapshot =
      { Id = sessionId
        Name = None
        Projects = ["Test.fsproj"]
        Status = SessionDisplayStatus.Running
        LastActivity = DateTime.UtcNow
        EvalCount = 0
        UpSince = DateTime.UtcNow
        IsActive = true
        WorkingDirectory = "." }
    let initial = SageFsModel.initial()
    let model =
      { initial with
          Sessions =
            { initial.Sessions with
                Sessions = [session]
                ActiveSessionId = ActiveSession.Viewing sessionId }
          LiveTesting =
            { initial.LiveTesting with
                TestState =
                  { initial.LiveTesting.TestState with
                      Activation = Features.LiveTesting.LiveTestingActivation.Inactive
                      DiscoveredTests = [||] } } }
    let newModel, effects =
      SageFsUpdate.update SageFsMsg.EnableLiveTesting model
    newModel.LiveTesting.TestState.Activation
    |> Expect.equal "should be active" Features.LiveTesting.LiveTestingActivation.Active
    Set.contains (SageFs.WorkerProtocol.SessionId.value sessionId) newModel.LiveTesting.TestState.PendingDiscoverySessions
    |> Expect.isTrue "should mark the running session as pending discovery"
    effects |> Expect.isNonEmpty "should request initial discovery when no tests are discovered yet"
    effects
    |> List.exists (fun effect ->
      match effect with
      | SageFsEffect.TestCycle Features.LiveTesting.TestCycleEffect.RequestInitialDiscovery -> true
      | _ -> false)
    |> Expect.isTrue "should contain the initial discovery effect"

  testCase "DisableLiveTesting emits no effects" <| fun _ ->
    let model =
      { (SageFsModel.initial()) with
          LiveTesting =
            { (SageFsModel.initial()).LiveTesting with
                TestState =
                  { (SageFsModel.initial()).LiveTesting.TestState with
                      Activation = Features.LiveTesting.LiveTestingActivation.Active } } }
    let newModel, effects =
      SageFsUpdate.update SageFsMsg.DisableLiveTesting model
    newModel.LiveTesting.TestState.Activation
    |> Expect.equal "should be inactive" Features.LiveTesting.LiveTestingActivation.Inactive
    effects |> Expect.isEmpty "no effects when deactivating"

  testCase "EnableLiveTesting when already active is no-op" <| fun _ ->
    let model =
      { (SageFsModel.initial()) with
          LiveTesting =
            { (SageFsModel.initial()).LiveTesting with
                TestState =
                  { (SageFsModel.initial()).LiveTesting.TestState with
                      Activation = Features.LiveTesting.LiveTestingActivation.Active } } }
    let newModel, effects =
      SageFsUpdate.update SageFsMsg.EnableLiveTesting model
    newModel.LiveTesting.TestState.Activation
    |> Expect.equal "should stay active" Features.LiveTesting.LiveTestingActivation.Active
    effects |> Expect.isEmpty "no effects for redundant enable"

  testCase "DisableLiveTesting when already inactive is no-op" <| fun _ ->
    let model =
      { (SageFsModel.initial()) with
          LiveTesting =
            { (SageFsModel.initial()).LiveTesting with
                TestState =
                  { (SageFsModel.initial()).LiveTesting.TestState with
                      Activation = Features.LiveTesting.LiveTestingActivation.Inactive } } }
    let newModel, effects =
      SageFsUpdate.update SageFsMsg.DisableLiveTesting model
    newModel.LiveTesting.TestState.Activation
    |> Expect.equal "should stay inactive" Features.LiveTesting.LiveTestingActivation.Inactive
    effects |> Expect.isEmpty "no effects for redundant disable"
]

[<Tests>]
let sageFsRenderTests = testList "SageFsRender" [
  testCase "renders 6 regions from initial model" <| fun _ ->
    let regions = SageFsRender.render (SageFsModel.initial())
    regions |> Expect.hasLength "should have 6 regions" 6

  testCase "editor region is focusable" <| fun _ ->
    let regions = SageFsRender.render (SageFsModel.initial())
    regions |> List.find (fun r -> r.Id = "editor")
    |> fun r -> r.Flags.HasFlag RegionFlags.Focusable
    |> Expect.isTrue "editor should be focusable"

  testCase "output region shows recent output" <| fun _ ->
    let model = {
      (SageFsModel.initial()) with
        RecentOutput = SessionOutputStore.ofLines [
          { Kind = OutputKind.Result
            Text = "val x = 42"
            Timestamp = DateTime.UtcNow
            SessionId = "" }
        ]
    }
    let regions = SageFsRender.render model
    let outputRegion = regions |> List.find (fun r -> r.Id = "output")
    outputRegion.Content
    |> Expect.stringContains "should contain output" "val x = 42"

  testCase "diagnostics region shows diagnostics" <| fun _ ->
    let model = {
      (SageFsModel.initial()) with
        Diagnostics = Map.ofList [
          "", [{
            Message = "type error"
            Subcategory = "typecheck"
            Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
            Severity = DiagnosticSeverity.Error
          }] ]
    }
    let regions = SageFsRender.render model
    let diagRegion = regions |> List.find (fun r -> r.Id = "diagnostics")
    diagRegion.Content
    |> Expect.stringContains "should contain error" "type error"

  testCase "sessions region shows active session" <| fun _ ->
    let snap = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let model = {
      (SageFsModel.initial()) with
        Sessions = {
          (SageFsModel.initial()).Sessions with
            Sessions = [snap] }
    }
    let regions = SageFsRender.render model
    let sessionsRegion = regions |> List.find (fun r -> r.Id = "sessions")
    sessionsRegion.Content
    |> Expect.stringContains "should show session id" "aa000001"
    sessionsRegion.Content
    |> Expect.stringContains "should show active marker" "*"

  testCase "output region tags each line with correct [kind]" <| fun _ ->
    let now = DateTime.UtcNow
    let model =
      { (SageFsModel.initial()) with
          RecentOutput = SessionOutputStore.ofLines [
            { Kind = OutputKind.Result; Text = "val x = 1"; Timestamp = now; SessionId = "" }
            { Kind = OutputKind.Error; Text = "oops"; Timestamp = now; SessionId = "" }
            { Kind = OutputKind.Info; Text = "loaded"; Timestamp = now; SessionId = "" }
            { Kind = OutputKind.System; Text = "sys"; Timestamp = now; SessionId = "" }
          ] }
    let output =
      SageFsRender.render model
      |> List.find (fun r -> r.Id = "output")
    let lines = output.Content.Split('\n')
    // RecentOutput is newest-first; render reverses to oldest-first (FSI style)
    lines |> Array.length |> Expect.equal "4 output lines" 4
    lines.[0] |> Expect.stringContains "first tagged system (oldest)" "[system]"
    lines.[1] |> Expect.stringContains "second tagged info" "[info]"
    lines.[2] |> Expect.stringContains "third tagged error" "[error]"
    lines.[3] |> Expect.stringContains "fourth tagged result (newest)" "[result]"

  testCase "inactive session has no * marker" <| fun _ ->
    let now = DateTime.UtcNow
    let model =
      { (SageFsModel.initial()) with
          Sessions =
            { (SageFsModel.initial()).Sessions with
                Sessions = [
                  { Id = testSessionId "aa000001"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
                    LastActivity = now; EvalCount = 0; UpSince = now; IsActive = true; WorkingDirectory = "" }
                  { Id = testSessionId "aa000002"; Name = None; Projects = []; Status = SessionDisplayStatus.Starting
                    LastActivity = now; EvalCount = 0; UpSince = now; IsActive = false; WorkingDirectory = "" }
                ] } }
    let sessions =
      SageFsRender.render model
      |> List.find (fun r -> r.Id = "sessions")
    let lines = sessions.Content.Split('\n')
    lines |> Array.length |> Expect.equal "2 session lines plus nav hint" 3
    lines.[1].Contains("*")
    |> Expect.isFalse "inactive session has no *"

  testCase "empty model produces empty output and diagnostics" <| fun _ ->
    let regions = SageFsRender.render (SageFsModel.initial())
    let output = regions |> List.find (fun r -> r.Id = "output")
    let diag = regions |> List.find (fun r -> r.Id = "diagnostics")
    output.Content |> Expect.equal "empty output" ""
    diag.Content |> Expect.equal "empty diagnostics" ""

  testCase "region ids are correct" <| fun _ ->
    SageFsRender.render (SageFsModel.initial())
    |> List.map (fun r -> r.Id)
    |> Expect.equal "region ids in order" ["editor"; "output"; "diagnostics"; "sessions"; "context"; "tests"]

  testProperty "sessions render contains id and status for every session"
    <| fun (sessionCount: byte) ->
      let count = int sessionCount % 10
      let now = DateTime.UtcNow
      let statuses = [|
        SessionDisplayStatus.Running
        SessionDisplayStatus.Starting
        SessionDisplayStatus.Suspended
        SessionDisplayStatus.Stale
        SessionDisplayStatus.Restarting |]
      let sessions =
        [ for i in 0..count-1 do
            { SessionSnapshot.Id = testSessionId (sprintf "%08x" i)
              Name = None; Projects = []; Status = statuses.[i % statuses.Length]
              LastActivity = now; EvalCount = i; UpSince = now
              IsActive = (i = 0); WorkingDirectory = "" } ]
      let model =
        { (SageFsModel.initial()) with
            Sessions = { (SageFsModel.initial()).Sessions with Sessions = sessions } }
      let sessRegion =
        SageFsRender.render model
        |> List.find (fun r -> r.Id = "sessions")
      if count = 0 then
        sessRegion.Content |> Expect.stringContains "no-sessions message is shown" "No sessions"
      else
        let lines = sessRegion.Content.Split('\n')
        // count + 1 for the nav hint footer line
        lines |> Array.length |> Expect.equal "one line per session plus nav hint" (count + 1)
        for i in 0..count-1 do
          lines.[i]
          |> Expect.stringContains "contains session id" (sprintf "%08x" i)
          lines.[i]
          |> Expect.stringContains "contains status brackets" "["
]

[<Tests>]
let elmIntegrationTests = testList "ElmLoop integration" [
  testCase "SageFs program wires update+render correctly" <| fun _ ->
    let mutable lastRegions : RenderRegion list = []
    let mutable lastModel : SageFsModel option = None
    let signal = new System.Threading.ManualResetEventSlim(false)
    let program : ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = fun _ _ -> async { () }
      OnModelChanged = fun model regions ->
        lastModel <- Some model
        lastRegions <- regions
        signal.Set()
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    signal.Wait(1000) |> ignore; signal.Reset()
    lastRegions |> Expect.hasLength "initial render should have 6 regions" 6

    dispatch (SageFsMsg.Editor (EditorAction.InsertChar 'h'))
    signal.Wait(1000) |> ignore; signal.Reset()
    lastModel.Value.Editor.Buffer
    |> ValidatedBuffer.text
    |> Expect.equal "should have h" "h"

    let snap : SessionSnapshot = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running; IsActive = true
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; WorkingDirectory = "" }
    dispatch (SageFsMsg.Event (SageFsEvent.SessionCreated snap))
    signal.Wait(1000) |> ignore; signal.Reset()

    dispatch (SageFsMsg.Event (SageFsEvent.EvalCompleted ("aa000001", "val x = 42", [])))
    signal.Wait(1000) |> ignore; signal.Reset()
    outputFor "aa000001" lastModel.Value
    |> Expect.hasLength "should have output" 1
    let outputRegion = lastRegions |> List.find (fun r -> r.Id = "output")
    outputRegion.Content
    |> Expect.stringContains "should show in render" "val x = 42"

  testCase "unchanged ListSessions polls stay silent but real session refreshes still render" <| fun _ ->
    let signal = new System.Threading.ManualResetEventSlim(false)
    let mutable callbackCount = 0
    let program : ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = fun _ _ -> async { () }
      OnModelChanged = fun _ _ ->
        callbackCount <- callbackCount + 1
        signal.Set()
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    signal.Wait(1000) |> ignore
    signal.Reset()
    let initialCallbackCount = callbackCount

    dispatch (SageFsMsg.Editor EditorAction.ListSessions)
    signal.Wait(250)
    |> Expect.isFalse "an unchanged poll should not trigger another render"
    callbackCount
    |> Expect.equal "callback count should stay the same after the no-op poll" initialCallbackCount

    let snap : SessionSnapshot = {
      Id = testSessionId "aa000001"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running; IsActive = true
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; WorkingDirectory = "" }
    dispatch (SageFsMsg.Event (SageFsEvent.SessionsRefreshed [snap]))
    signal.Wait(1000)
    |> Expect.isTrue "a real session refresh should still render"
    callbackCount
    |> Expect.equal "real session refresh should render exactly once" (initialCallbackCount + 1)

  testCase "effects are dispatched asynchronously" <| fun _ ->
    let mutable effectExecuted = false
    let mutable resultReceived = false
    let program : ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = fun dispatch effect ->
        async {
          effectExecuted <- true
          match effect with
          | SageFsEffect.Editor (EditorEffect.RequestEval code) ->
            dispatch (
              SageFsMsg.Event (
                SageFsEvent.EvalCompleted (
                  "s1", sprintf "val it = %s" code, [])))
          | _ -> ()
        }
      OnModelChanged = fun model _ ->
        if model.RecentOutput.GetBuffer("s1").Count > 0 then resultReceived <- true
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    dispatch (SageFsMsg.Editor (EditorAction.InsertChar '1'))
    dispatch (SageFsMsg.Editor EditorAction.Submit)
    let deadline = DateTime.UtcNow.AddSeconds 5.0
    while not resultReceived && DateTime.UtcNow < deadline do
      System.Threading.Thread.Sleep 10
    effectExecuted |> Expect.isTrue "effect should have been executed"
    resultReceived |> Expect.isTrue "result should have been received"
]

[<Tests>]
let sessionNavAppTests = testList "SageFsUpdate session navigation" [
  let mkSnap id isActive = {
    Id = id; Name = None; Projects = []; Status = SessionDisplayStatus.Running
    LastActivity = System.DateTime.UtcNow; EvalCount = 0
    UpSince = System.DateTime.UtcNow; IsActive = isActive; WorkingDirectory = "" }

  let withSessions (snaps: SessionSnapshot list) (model: SageFsModel) : SageFsModel =
    { model with
        Sessions = {
          model.Sessions with Sessions = snaps } }

  testCase "SessionNavDown clamps to session count" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 1 } }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionNavDown) model'
    newModel.Editor.SelectedSessionIndex
    |> Expect.equal "should clamp to last index" (Some 1)

  testCase "SessionSelect emits RequestSessionSwitch with correct id" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 1 } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model'
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)] ->
      sid |> Expect.equal "should switch to s2" "aa000002"
    | _ -> failtest (sprintf "expected RequestSessionSwitch, got %A" effects)

  testCase "SessionDelete emits RequestSessionStop with correct id" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 0 } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionDelete) model'
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionStop sid)] ->
      sid |> Expect.equal "should stop s1" "aa000001"
    | _ -> failtest (sprintf "expected RequestSessionStop, got %A" effects)

  testCase "SessionSelect with no selection does nothing" <| fun _ ->
    let model = (SageFsModel.initial()) |> withSessions [mkSnap (testSessionId "aa000001") true]
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model
    effects |> Expect.isEmpty "no effects when no selection"

  testCase "SessionSelect with out-of-range index does nothing" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 5 } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model'
    effects |> Expect.isEmpty "no effects for OOB index"

  testCase "ClearOutput clears recent output" <| fun _ ->
    let model = {
      (SageFsModel.initial()) with
        RecentOutput = SessionOutputStore.ofLines [
          { Kind = OutputKind.Result; Text = "hello"
            Timestamp = System.DateTime.UtcNow; SessionId = "" }
        ] }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.ClearOutput) model
    newModel.RecentOutput.GetActiveBuffer(newModel.Sessions.ActiveSessionId) |> Expect.isEmpty "output should be cleared"

  testCase "InsertChar remapped to PromptChar when prompt active" <| fun _ ->
    let model = {
      (SageFsModel.initial()) with
        Editor = { EditorState.initial with
                     Prompt = Some { Label = "Dir"; Input = "ab"; Purpose = PromptPurpose.CreateSessionDir } } }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Editor (EditorAction.InsertChar 'c')) model
    newModel.Editor.Prompt.Value.Input |> Expect.equal "should append via remap" "abc"

  testCase "NewLine remapped to PromptConfirm when prompt active" <| fun _ ->
    let model = {
      (SageFsModel.initial()) with
        Editor = { EditorState.initial with
                     Prompt = Some { Label = "Dir"; Input = "C:\\Code"; Purpose = PromptPurpose.CreateSessionDir } } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.NewLine) model
    effects |> List.exists (fun e ->
      match e with
      | SageFsEffect.Editor (EditorEffect.RequestSessionCreate _) -> true
      | _ -> false)
    |> Expect.isTrue "should produce session create effect"

  testCase "Cancel remapped to PromptCancel when prompt active" <| fun _ ->
    let model = {
      (SageFsModel.initial()) with
        Editor = { EditorState.initial with
                     Prompt = Some { Label = "Dir"; Input = "test"; Purpose = PromptPurpose.CreateSessionDir } } }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.Cancel) model
    newModel.Editor.Prompt |> Expect.isNone "prompt should close"
    effects |> Expect.isEmpty "no effects on cancel"

  testCase "SessionCycleNext moves to next session and switches" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false; mkSnap (testSessionId "aa000003") false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 0 } }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model'
    newModel.Editor.SelectedSessionIndex
    |> Expect.equal "should move to index 1" (Some 1)
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)] ->
      sid |> Expect.equal "should switch to s2" "aa000002"
    | _ -> failtest (sprintf "expected RequestSessionSwitch, got %A" effects)

  testCase "SessionCycleNext wraps around" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 1 } }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model'
    newModel.Editor.SelectedSessionIndex
    |> Expect.equal "should wrap to index 0" (Some 0)
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)] ->
      sid |> Expect.equal "should switch to s1" "aa000001"
    | _ -> failtest (sprintf "expected RequestSessionSwitch, got %A" effects)

  testCase "SessionCyclePrev moves to previous session and switches" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false; mkSnap (testSessionId "aa000003") false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 2 } }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCyclePrev) model'
    newModel.Editor.SelectedSessionIndex
    |> Expect.equal "should move to index 1" (Some 1)
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)] ->
      sid |> Expect.equal "should switch to s2" "aa000002"
    | _ -> failtest (sprintf "expected RequestSessionSwitch, got %A" effects)

  testCase "SessionCyclePrev wraps around" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true; mkSnap (testSessionId "aa000002") false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 0 } }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCyclePrev) model'
    newModel.Editor.SelectedSessionIndex
    |> Expect.equal "should wrap to index 1" (Some 1)
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)] ->
      sid |> Expect.equal "should switch to s2" "aa000002"
    | _ -> failtest (sprintf "expected RequestSessionSwitch, got %A" effects)

  testCase "SessionCycleNext with single session does nothing" <| fun _ ->
    let model =
      (SageFsModel.initial())
      |> withSessions [mkSnap (testSessionId "aa000001") true]
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model
    effects |> Expect.isEmpty "no effects with single session"

  testCase "SessionCyclePrev with no sessions does nothing" <| fun _ ->
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCyclePrev) (SageFsModel.initial())
    effects |> Expect.isEmpty "no effects with no sessions"
]

[<Tests>]
let renderConsistencyTests = testList "Render consistency" [
  let mkModel () =
    let snap : SessionSnapshot = {
      Id = testSessionId "aa100001"; Name = None; Status = SessionDisplayStatus.Running
      IsActive = true; Projects = ["Test.fsproj"]; EvalCount = 5
      UpSince = DateTime.UtcNow.AddHours(-1.0)
      LastActivity = DateTime.UtcNow; WorkingDirectory = "C:\\Code" }
    { (SageFsModel.initial()) with
        Sessions = { Sessions = [snap]; ActiveSessionId = ActiveSession.Viewing (testSessionId "aa100001")
                     TotalEvals = 5; WatchStatus = None; Standby = StandbyInfo.NoPool }
        RecentOutput = SessionOutputStore.ofLines [
          { Kind = OutputKind.Result; Text = "val x = 42"
            Timestamp = DateTime.UtcNow; SessionId = "aa100001" }
        ]
        Diagnostics = Map.ofList [
          "aa100001", [
            { Severity = DiagnosticSeverity.Warning; Message = "unused var"
              Subcategory = ""
              Range = { StartLine = 1; StartColumn = 1; EndLine = 1; EndColumn = 5 } }
          ] ] }

  testCase "render is deterministic — same model produces same regions" <| fun _ ->
    let model = mkModel ()
    let r1 = SageFsRender.render model
    let r2 = SageFsRender.render model
    r1 |> List.map (fun r -> r.Id, r.Content)
    |> Expect.equal "regions should be identical" (r2 |> List.map (fun r -> r.Id, r.Content))

  testCase "render produces expected region IDs" <| fun _ ->
    let regions = SageFsRender.render (mkModel ())
    let ids = regions |> List.map (fun r -> r.Id)
    ids |> Expect.contains "should have editor" "editor"
    ids |> Expect.contains "should have output" "output"
    ids |> Expect.contains "should have diagnostics" "diagnostics"
    ids |> Expect.contains "should have sessions" "sessions"

  testCase "prompt appears in editor content when active" <| fun _ ->
    let model = {
      mkModel () with
        Editor = { EditorState.initial with
                     Prompt = Some { Label = "Dir"; Input = "C:\\Foo"; Purpose = PromptPurpose.CreateSessionDir } } }
    let regions = SageFsRender.render model
    let editor = regions |> List.find (fun r -> r.Id = "editor")
    editor.Content |> Expect.stringContains "should show label" "Dir"
    editor.Content |> Expect.stringContains "should show input" "C:\\Foo"

  testCase "no prompt means clean editor content" <| fun _ ->
    let model = mkModel ()
    let regions = SageFsRender.render model
    let editor = regions |> List.find (fun r -> r.Id = "editor")
    editor.Content |> fun c ->
      c.Contains("───") |> Expect.isFalse "should not have prompt separator"

  testCase "selected session gets > marker" <| fun _ ->
    let model = {
      mkModel () with
        Editor = { EditorState.initial with SelectedSessionIndex = Some 0 } }
    let regions = SageFsRender.render model
    let sessions = regions |> List.find (fun r -> r.Id = "sessions")
    sessions.Content |> Expect.stringContains "should have > marker" ">"

  testCase "output filters by active session" <| fun _ ->
    let model = {
      mkModel () with
        RecentOutput = SessionOutputStore.ofLines [
          { Kind = OutputKind.Result; Text = "active output"
            Timestamp = DateTime.UtcNow; SessionId = "aa100001" }
          { Kind = OutputKind.Result; Text = "other output"
            Timestamp = DateTime.UtcNow; SessionId = "aa100002" }
        ] }
    let regions = SageFsRender.render model
    let output = regions |> List.find (fun r -> r.Id = "output")
    output.Content |> Expect.stringContains "should show active" "active output"
    output.Content |> fun c ->
      c.Contains("other output") |> Expect.isFalse "should not show other session"
]

[<Tests>]
let dispatchRoundTripTests = testList "Dispatch round-trip" [
  let actionsWithApi = [
    EditorAction.SessionNavUp, "sessionNavUp"
    EditorAction.SessionNavDown, "sessionNavDown"
    EditorAction.SessionSelect, "sessionSelect"
    EditorAction.SessionDelete, "sessionDelete"
    EditorAction.SessionCycleNext, "sessionCycleNext"
    EditorAction.SessionCyclePrev, "sessionCyclePrev"
    EditorAction.ClearOutput, "clearOutput"
    EditorAction.PromptBackspace, "promptBackspace"
    EditorAction.PromptConfirm, "promptConfirm"
    EditorAction.PromptCancel, "promptCancel"
    EditorAction.ResetSession, "resetSession"
    EditorAction.HardResetSession, "hardResetSession"
  ]

  for action, expectedApi in actionsWithApi do
    testCase (sprintf "actionToApi maps %A to %s" action expectedApi) <| fun _ ->
      let result = DaemonClient.actionToApi action
      result |> Expect.isSome (sprintf "%A should have API mapping" action)
      let apiAction, _ = result.Value
      apiAction |> Expect.equal "api action name" expectedApi

  testCase "PromptChar includes value in api" <| fun _ ->
    let result = DaemonClient.actionToApi (EditorAction.PromptChar 'x')
    result |> Expect.isSome "should have mapping"
    let apiAction, value = result.Value
    apiAction |> Expect.equal "action" "promptChar"
    value |> Expect.equal "value" (Some "x")
]
