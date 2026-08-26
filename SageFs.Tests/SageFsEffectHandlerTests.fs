module SageFs.Tests.SageFsEffectHandlerTests

open System
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.WarmUp
open SageFs.WorkerProtocol
open SageFs.Features.Diagnostics
open SageFs.Tests.SharedGenerators

module TestDeps =

  type CallLog = {
    mutable EvalCalls: (string * string) list
    mutable CompletionCalls: (string * string * int) list
    mutable TestDiscoveryCalls: string list
    mutable SessionListCalls: int
    mutable SessionCreateCalls: (string list * string) list
    mutable SessionStopCalls: SessionId list
    mutable ConfigureAutoOpenCalls: string list
  }

  let createLog () = {
    EvalCalls = []
    CompletionCalls = []
    TestDiscoveryCalls = []
    SessionListCalls = 0
    SessionCreateCalls = []
    SessionStopCalls = []
    ConfigureAutoOpenCalls = []
  }

  let ensureAutoOpenNoop _ =
    async {
      return Result.Ok {
        Kind = OutputKind.System
        Text = "Disabled warmup auto-open"
        Timestamp = DateTime.UtcNow
        SessionId = "" }
    }

  let singleSession
    (log: CallLog)
    (handler: WorkerMessage -> WorkerResponse) : EffectDeps =
    let sessionInfo : SessionInfo = {
      Id = testSessionId "a1b2c3d4"
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = SessionStatus.Ready
      FaultReason = None
      WorkerPid = Some 999
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let proxy (msg: WorkerMessage) =
      async {
        match msg with
        | WorkerMessage.EvalCode (code, rid) ->
          log.EvalCalls <- log.EvalCalls @ [rid, code]
        | WorkerMessage.GetCompletions (code, pos, rid) ->
          log.CompletionCalls <- log.CompletionCalls @ [rid, code, pos]
        | WorkerMessage.GetTestDiscovery rid ->
          log.TestDiscoveryCalls <- log.TestDiscoveryCalls @ [rid]
        | _ -> ()
        return handler msg
      }
    {
      ResolveSession = fun _ ->
        Result.Ok (
          SessionOperations.SessionResolution.DefaultSingle (testSessionId "a1b2c3d4"))
      GetProxy = fun id ->
        if id = testSessionId "a1b2c3d4" then Some proxy else None
      GetStreamingTestProxy = fun _ -> None
      CreateSession = fun projects dir _workflow ->
        async {
          log.SessionCreateCalls <-
            log.SessionCreateCalls @ [projects, dir]
          return Result.Ok sessionInfo
        }
      ConfigureWarmupAutoOpen = fun dir ->
        async {
          log.ConfigureAutoOpenCalls <-
            log.ConfigureAutoOpenCalls @ [dir]
          return Result.Ok {
            Kind = OutputKind.System
            Text = sprintf "Disabled warmup auto-open for %s" dir
            Timestamp = DateTime.UtcNow
            SessionId = "" }
        }
      StopSession = fun id ->
        async {
          log.SessionStopCalls <- log.SessionStopCalls @ [id]
          return Result.Ok ()
        }
      RestartSession = fun _ _ ->
        async { return Result.Ok "restarted" }
      ListSessions = fun () ->
        async {
          log.SessionListCalls <- log.SessionListCalls + 1
          return [sessionInfo]
        }
      SleepMs = fun _ -> async { return () }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }

  let noSessions () : EffectDeps =
    {
      ResolveSession = fun _ ->
        Result.Error (SageFsError.NoActiveSessions)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ -> None
      CreateSession = fun projects dir _workflow ->
        async {
          let info : SessionInfo = {
            Id = testSessionId "b2c3d4e5"
            Name = None
            Projects = projects
            WorkingDirectory = dir
            SolutionRoot = None
            CreatedAt = DateTime.UtcNow
            LastActivity = DateTime.UtcNow
            Status = SessionStatus.Starting
            FaultReason = None
            WorkerPid = None
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
          }
          return Result.Ok info
        }
      StopSession = fun id ->
        async { return Result.Error (SageFsError.SessionNotFound (SessionId.value id)) }
      RestartSession = fun _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ListSessions = fun () -> async { return [] }
      ConfigureWarmupAutoOpen = ensureAutoOpenNoop
      SleepMs = fun _ -> async { return () }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }

let private makeRequestFcsTypeCheckEffect
  (targetSession: string option)
  (filePath: string)
  (content: string)
  (analysisIdentity: string)
  (treeSitterElapsed: TimeSpan)
  =
  Features.LiveTesting.TestCycleEffect.RequestFcsTypeCheck {
    SessionId = targetSession
    FilePath = filePath
    Content = Some content
    AnalysisIdentity = Some (Features.LiveTesting.AnalysisIdentity.ofContent analysisIdentity)
    TreeSitterElapsed = treeSitterElapsed
  }

let private makeCancelRebuildEffect
  (targetSession: string option)
  (generation: int64)
  =
  let case =
    FSharpType.GetUnionCases(typeof<Features.LiveTesting.TestCycleEffect>)
    |> Array.find (fun uc -> uc.Name = "CancelRebuild")

  let fields = case.GetFields()
  fields.Length
  |> Expect.equal
      "CancelRebuild should carry session and generation"
      2

  FSharpValue.MakeUnion(
    case,
    [|
      box targetSession
      box generation
    |]
  ) :?> Features.LiveTesting.TestCycleEffect

[<Tests>]
let effectHandlerTests = testList "SageFsEffectHandler" [
  testCase "RequestEval sends code to worker and dispatches result"
    <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.EvalCode (_, rid) ->
        WorkerResponse.EvalResult (rid, Result.Ok "val x = 42", [], Map.empty)
      | _ ->
        WorkerResponse.WorkerError (
          SageFsError.Unexpected (exn "unexpected")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestEval "let x = 42"))
    |> Async.RunSynchronously
    log.EvalCalls
    |> Expect.hasLength "should call eval" 1
    snd log.EvalCalls.[0]
    |> Expect.equal "code" "let x = 42"
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalCompleted (sid, output, _)) ->
      sid |> Expect.equal "session" "a1b2c3d4"
      output |> Expect.equal "output" "val x = 42"
    | other -> failtestf "expected EvalCompleted, got %A" other

  testCase "RequestEval error dispatches EvalFailed" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.EvalCode (_, rid) ->
        WorkerResponse.EvalResult (
          rid,
          Result.Error (SageFsError.EvalFailed "type mismatch"),
          [], Map.empty)
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestEval "bad"))
    |> Async.RunSynchronously
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalFailed (_, err)) ->
      err |> Expect.stringContains "err" "type mismatch"
    | other -> failtestf "expected EvalFailed, got %A" other

  testCase "RequestConfigureWarmupAutoOpen dispatches output message" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "unused")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestConfigureWarmupAutoOpen @"C:\Code\Repos\TestProject"))
    |> Async.RunSynchronously
    log.ConfigureAutoOpenCalls
    |> Expect.equal "should call config helper" [@"C:\Code\Repos\TestProject"]
    match dispatched with
    | [SageFsMsg.Event (SageFsEvent.OutputEmitted line)] ->
      line.Kind |> Expect.equal "should emit system output" OutputKind.System
      line.Text |> Expect.stringContains "should describe the opt-out" "Disabled warmup auto-open"
    | other ->
      failtestf "expected OutputEmitted, got %A" other

  testCase "RequestEval converts worker diagnostics" <| fun _ ->
    let log = TestDeps.createLog ()
    let diag : WorkerDiagnostic = {
      Severity = DiagnosticSeverity.Error
      Message = "FS0001"
      StartLine = 1; StartColumn = 5
      EndLine = 1; EndColumn = 10
    }
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.EvalCode (_, rid) ->
        WorkerResponse.EvalResult (rid, Result.Ok "ok", [diag], Map.empty)
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestEval "code"))
    |> Async.RunSynchronously
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalCompleted (_, _, diags)) ->
      diags |> Expect.hasLength "1 diag" 1
      diags.[0].Message |> Expect.equal "msg" "FS0001"
      diags.[0].Severity |> Expect.equal "sev" DiagnosticSeverity.Error
    | other ->
      failtestf "expected EvalCompleted with diags, got %A" other

  testCase "RequestCompletion dispatches items" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.GetCompletions (_, _, rid) ->
        WorkerResponse.CompletionResult (rid, ["ToString"; "GetType"])
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestCompletion ("x.", 2)))
    |> Async.RunSynchronously
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.CompletionReady items) ->
      items |> Expect.hasLength "2 items" 2
      items.[0].Label |> Expect.equal "first" "ToString"
    | other -> failtestf "expected CompletionReady, got %A" other

  testCase "RequestInitialDiscovery asks the worker for test discovery" <| fun _ ->
    let log = TestDeps.createLog ()
    let discovered : Features.LiveTesting.TestCase =
      { Id = Features.LiveTesting.TestId.create "MyModule.test1" Features.LiveTesting.TestFramework.Expecto
        FullName = "MyModule.test1"
        DisplayName = "test1"
        Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
        Labels = []
        Framework = Features.LiveTesting.TestFramework.Expecto
        Category = Features.LiveTesting.TestCategory.Unit }
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.GetTestDiscovery _ ->
        WorkerResponse.InitialTestDiscovery([|discovered|], [])
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "unexpected")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.TestCycle Features.LiveTesting.TestCycleEffect.RequestInitialDiscovery)
    |> Async.RunSynchronously
    log.SessionListCalls |> Expect.equal "should enumerate sessions for discovery" 1
    log.TestDiscoveryCalls |> Expect.hasLength "should request discovery once" 1
    dispatched
    |> List.exists (fun msg ->
      match msg with
      | SageFsMsg.Event (SageFsEvent.TestsDiscovered (sid, tests)) ->
        sid = "a1b2c3d4" && tests.Length = 1
      | _ -> false)
    |> Expect.isTrue "should dispatch discovered tests back into the Elm loop"

  testCase "RequestEval with no sessions dispatches error" <| fun _ ->
    let deps = TestDeps.noSessions ()
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestEval "x"))
    |> Async.RunSynchronously
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalFailed (_, err)) ->
      err |> Expect.stringContains "no sessions" "No active"
    | other -> failtestf "expected error, got %A" other

  testCase "RequestSessionList dispatches snapshots" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor EditorEffect.RequestSessionList)
    |> Async.RunSynchronously
    log.SessionListCalls |> Expect.equal "called" 1
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.SessionsRefreshed snaps) ->
      snaps |> Expect.hasLength "one session" 1
      snaps.[0].Id |> Expect.equal "id" (testSessionId "a1b2c3d4")
    | other -> failtestf "expected SessionsRefreshed, got %A" other

  testCase "RequestSessionSwitch dispatches switch" <| fun _ ->
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute (TestDeps.noSessions ())
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestSessionSwitch "s2"))
    |> Async.RunSynchronously
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.SessionSwitched (_, toId)) ->
      toId |> Expect.equal "to" "s2"
    | other -> failtestf "expected SessionSwitched, got %A" other

  testCase "RequestSessionCreate dispatches created" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor
        (EditorEffect.RequestSessionCreate ["New.fsproj"]))
    |> Async.RunSynchronously
    log.SessionCreateCalls |> Expect.hasLength "called" 1
    // dispatched is prepend-order: [SessionSwitched; SessionCreated]
    let created =
      dispatched |> List.tryPick (function
        | SageFsMsg.Event (SageFsEvent.SessionCreated snap) -> Some snap
        | _ -> None)
    match created with
    | Some snap ->
      snap.Projects |> Expect.equal "projects" ["Test.fsproj"]
    | None -> failtestf "expected SessionCreated in dispatched, got %A" dispatched

  testCase "RequestSessionStop dispatches stopped" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestSessionStop "00000001"))
    |> Async.RunSynchronously
    log.SessionStopCalls |> Expect.equal "called" [testSessionId "00000001"]
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.SessionStopped sid) ->
      sid |> Expect.equal "id" "00000001"
    | other -> failtestf "expected SessionStopped, got %A" other

  testCase "RequestSessionStop failure dispatches error" <| fun _ ->
    let deps = {
      TestDeps.noSessions () with
        StopSession = fun _ ->
          async {
            return Result.Error (SageFsError.SessionNotFound "00000001")
          }
    }
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestSessionStop "00000001"))
    |> Async.RunSynchronously
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalFailed (_, err)) ->
      err |> Expect.stringContains "fail" "Stop failed"
    | other -> failtestf "expected error, got %A" other

  testCase "RequestFcsTypeCheck uses provided buffer content instead of rereading disk" <| fun _ ->
    let log = TestDeps.createLog ()
    let tempFile = IO.Path.GetTempFileName()
    let staleDiskContent = "module Sample\nlet answer = 1"
    let latestBufferContent = "module Sample\nlet answer = 2"
    let mutable observedCode : string option = None
    IO.File.WriteAllText(tempFile, staleDiskContent)
    try
      let deps = TestDeps.singleSession log (fun msg ->
        match msg with
        | WorkerMessage.TypeCheckWithSymbols (code, filePath, rid) ->
            observedCode <- Some code
            filePath |> Expect.equal "should typecheck the requested file" tempFile
            WorkerResponse.TypeCheckWithSymbolsResult(rid, false, [], [])
        | _ ->
            WorkerResponse.WorkerError (SageFsError.Unexpected (exn "unexpected worker message")))

      let effect =
        makeRequestFcsTypeCheckEffect
          None
          tempFile
          latestBufferContent
          "buffer-v2"
          TimeSpan.Zero

      let mutable dispatched : SageFsMsg list = []
      SageFsEffectHandler.execute deps
        (fun msg -> dispatched <- msg :: dispatched)
        (SageFsEffect.TestCycle effect)
      |> Async.RunSynchronously

      observedCode
      |> Expect.equal
          "FCS should analyze the provided buffer content, not stale disk content"
          (Some latestBufferContent)

      dispatched
      |> List.isEmpty
      |> Expect.isFalse "FCS completion should still be dispatched"
    finally
      if IO.File.Exists tempFile then
        IO.File.Delete tempFile

  testCase "RequestRebuild waits for restarted session proxy before reporting success" <| fun _ ->
    let sid = testSessionId "a1b2c3d4"
    let mutable dispatched : SageFsMsg list = []
    let mutable restartCalls : (SessionId * bool) list = []
    let mutable listCalls = 0
    let mutable proxyCalls = 0
    let tc : Features.LiveTesting.TestCase = {
      Id = Features.LiveTesting.TestId.TestId "t1"
      FullName = "Sample.Tests.should fail after rebuild"
      DisplayName = "should fail after rebuild"
      Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
      Labels = []
      Framework = Features.LiveTesting.TestFramework.Expecto
      Category = Features.LiveTesting.TestCategory.Unit
    }
    let sessionInfo status : SessionInfo = {
      Id = sid
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = status
      FaultReason = None
      WorkerPid = Some 999
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ ->
        proxyCalls <- proxyCalls + 1
        match proxyCalls >= 3 with
        | true -> Some (fun _ _ _ _ -> async { return () })
        | false -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      RestartSession = fun sessionId rebuild ->
        async {
          restartCalls <- restartCalls @ [sessionId, rebuild]
          return Result.Ok "restarted"
        }
      ListSessions = fun () ->
        async {
          listCalls <- listCalls + 1
          return [
            sessionInfo
              (match listCalls >= 3 with
               | true -> SessionStatus.Ready
               | false -> SessionStatus.Starting)
          ]
        }
      SleepMs = fun _ -> async { return () }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      (SageFsEffect.TestCycle (
        Features.LiveTesting.TestCycleEffect.RequestRebuild(
          1L,
          { Tests = [| tc |]
            Trigger = Features.LiveTesting.RunTrigger.FileSave
            TreeSitterElapsed = TimeSpan.Zero
            FcsElapsed = TimeSpan.Zero
            SessionId = Some (SessionId.value sid)
            InstrumentationMaps = [||] })))
    |> Async.RunSynchronously
    let sw = Diagnostics.Stopwatch.StartNew()
    while dispatched.IsEmpty && sw.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10
    restartCalls
    |> Expect.hasLength "should restart the targeted session once" 1
    restartCalls.Head
    |> Expect.equal "should request rebuild restart" (sid, true)
    (listCalls > 1)
    |> Expect.isTrue "should poll session readiness before completing rebuild"
    (proxyCalls > 1)
    |> Expect.isTrue "should wait for the streaming proxy before completing rebuild"
    dispatched
    |> Expect.equal "should report rebuild completion only after readiness" [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 1L, Ok ())]

  testCase "RequestRebuild keeps waiting while restarted session is still starting" <| fun _ ->
    let sid = testSessionId "d4c3b2a1"
    let mutable dispatched : SageFsMsg list = []
    let mutable restartCalls : (SessionId * bool) list = []
    let mutable listCalls = 0
    let mutable proxyCalls = 0
    let tc : Features.LiveTesting.TestCase = {
      Id = Features.LiveTesting.TestId.TestId "t2"
      FullName = "Sample.Tests.should wait for ready session"
      DisplayName = "should wait for ready session"
      Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
      Labels = []
      Framework = Features.LiveTesting.TestFramework.Expecto
      Category = Features.LiveTesting.TestCategory.Unit
    }
    let sessionInfo status : SessionInfo = {
      Id = sid
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = status
      FaultReason = None
      WorkerPid = Some 999
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ ->
        proxyCalls <- proxyCalls + 1
        match proxyCalls >= 12 with
        | true -> Some (fun _ _ _ _ -> async { return () })
        | false -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      RestartSession = fun sessionId rebuild ->
        async {
          restartCalls <- restartCalls @ [sessionId, rebuild]
          return Result.Ok "restarted"
        }
      ListSessions = fun () ->
        async {
          listCalls <- listCalls + 1
          return [
            sessionInfo
              (match listCalls >= 12 with
               | true -> SessionStatus.Ready
               | false -> SessionStatus.Starting)
          ]
        }
      SleepMs = fun _ -> async { return () }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      (SageFsEffect.TestCycle (
        Features.LiveTesting.TestCycleEffect.RequestRebuild(
          1L,
          { Tests = [| tc |]
            Trigger = Features.LiveTesting.RunTrigger.FileSave
            TreeSitterElapsed = TimeSpan.Zero
            FcsElapsed = TimeSpan.Zero
            SessionId = Some (SessionId.value sid)
            InstrumentationMaps = [||] })))
    |> Async.RunSynchronously
    let sw = Diagnostics.Stopwatch.StartNew()
    while dispatched.IsEmpty && sw.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10
    restartCalls
    |> Expect.hasLength "should restart the targeted session once" 1
    restartCalls.Head
    |> Expect.equal "should request rebuild restart" (sid, true)
    (listCalls >= 12)
    |> Expect.isTrue "should keep polling until the restarted session is finally ready"
    (proxyCalls >= 12)
    |> Expect.isTrue "should keep polling until the streaming proxy is finally ready"
    dispatched
    |> Expect.equal "should report rebuild success once the long startup finishes" [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 1L, Ok ())]

  testCase "RequestRebuild uses a short poll cadence during the first second of readiness wait" <| fun _ ->
    let sid = testSessionId "c0ffee01"
    let mutable dispatched : SageFsMsg list = []
    let mutable listCalls = 0
    let mutable proxyCalls = 0
    let sleepCalls = ResizeArray<int>()
    let tc : Features.LiveTesting.TestCase = {
      Id = Features.LiveTesting.TestId.TestId "t-fast"
      FullName = "Sample.Tests.should use fast rebuild polling"
      DisplayName = "should use fast rebuild polling"
      Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
      Labels = []
      Framework = Features.LiveTesting.TestFramework.Expecto
      Category = Features.LiveTesting.TestCategory.Unit
    }
    let sessionInfo status : SessionInfo = {
      Id = sid
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = status
      FaultReason = None
      WorkerPid = Some 999
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ ->
        proxyCalls <- proxyCalls + 1
        match proxyCalls >= 5 with
        | true -> Some (fun _ _ _ _ -> async { return () })
        | false -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      RestartSession = fun _ _ ->
        async { return Result.Ok "restarted" }
      ListSessions = fun () ->
        async {
          listCalls <- listCalls + 1
          return [
            sessionInfo
              (match listCalls >= 5 with
               | true -> SessionStatus.Ready
               | false -> SessionStatus.Starting)
          ]
        }
      SleepMs = fun delay ->
        async {
          sleepCalls.Add delay
        }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      (SageFsEffect.TestCycle (
        Features.LiveTesting.TestCycleEffect.RequestRebuild(
          1L,
          { Tests = [| tc |]
            Trigger = Features.LiveTesting.RunTrigger.FileSave
            TreeSitterElapsed = TimeSpan.Zero
            FcsElapsed = TimeSpan.Zero
            SessionId = Some (SessionId.value sid)
            InstrumentationMaps = [||] })))
    |> Async.RunSynchronously
    let sw = Diagnostics.Stopwatch.StartNew()
    while dispatched.IsEmpty && sw.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10
    sleepCalls |> Seq.toList
    |> Expect.equal "fast startup should stay on the short poll cadence" [50; 50; 50; 50]
    dispatched
    |> Expect.equal "fast startup should still complete successfully" [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 1L, Ok ())]

  testCase "RequestRebuild switches to a slower poll cadence after the first second" <| fun _ ->
    let sid = testSessionId "c0ffee02"
    let mutable dispatched : SageFsMsg list = []
    let mutable listCalls = 0
    let mutable proxyCalls = 0
    let sleepCalls = ResizeArray<int>()
    let tc : Features.LiveTesting.TestCase = {
      Id = Features.LiveTesting.TestId.TestId "t-slow"
      FullName = "Sample.Tests.should switch rebuild polling cadence"
      DisplayName = "should switch rebuild polling cadence"
      Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
      Labels = []
      Framework = Features.LiveTesting.TestFramework.Expecto
      Category = Features.LiveTesting.TestCategory.Unit
    }
    let sessionInfo status : SessionInfo = {
      Id = sid
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = status
      FaultReason = None
      WorkerPid = Some 999
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ ->
        proxyCalls <- proxyCalls + 1
        match proxyCalls >= 22 with
        | true -> Some (fun _ _ _ _ -> async { return () })
        | false -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      RestartSession = fun _ _ ->
        async { return Result.Ok "restarted" }
      ListSessions = fun () ->
        async {
          listCalls <- listCalls + 1
          return [
            sessionInfo
              (match listCalls >= 22 with
               | true -> SessionStatus.Ready
               | false -> SessionStatus.Starting)
          ]
        }
      SleepMs = fun delay ->
        async {
          sleepCalls.Add delay
        }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      (SageFsEffect.TestCycle (
        Features.LiveTesting.TestCycleEffect.RequestRebuild(
          1L,
          { Tests = [| tc |]
            Trigger = Features.LiveTesting.RunTrigger.FileSave
            TreeSitterElapsed = TimeSpan.Zero
            FcsElapsed = TimeSpan.Zero
            SessionId = Some (SessionId.value sid)
            InstrumentationMaps = [||] })))
    |> Async.RunSynchronously
    let sw = Diagnostics.Stopwatch.StartNew()
    while dispatched.IsEmpty && sw.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10
    let recordedSleeps = sleepCalls |> Seq.toList
    recordedSleeps.Length
    |> Expect.equal "long startup should keep polling until the ready/proxy pair finally arrives" 21
    recordedSleeps |> List.take 20
    |> Expect.equal "the first second should use the short cadence" (List.replicate 20 50)
    recordedSleeps |> List.last
    |> Expect.equal "polling should slow down once the first second has elapsed" 250
    dispatched
    |> Expect.equal "long startup should still complete successfully" [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 1L, Ok ())]

  testCase "superseded RequestRebuild suppresses stale completion from the older rebuild" <| fun _ ->
    let sid = testSessionId "c0ffee03"
    let mutable dispatched : SageFsMsg list = []
    let mutable restartCalls : (SessionId * bool) list = []
    let gate = obj()
    let mutable rebuildGeneration = 0
    let mutable readyGeneration = 0
    let tc : Features.LiveTesting.TestCase = {
      Id = Features.LiveTesting.TestId.TestId "t-cancelled"
      FullName = "Sample.Tests.should cancel stale rebuild completion"
      DisplayName = "should cancel stale rebuild completion"
      Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
      Labels = []
      Framework = Features.LiveTesting.TestFramework.Expecto
      Category = Features.LiveTesting.TestCategory.Unit
    }
    let sessionInfo status : SessionInfo = {
      Id = sid
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = status
      FaultReason = None
      WorkerPid = Some 999
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ ->
        let ready =
          lock gate (fun () -> rebuildGeneration > 0 && readyGeneration >= rebuildGeneration)
        match ready with
        | true -> Some (fun _ _ _ _ -> async { return () })
        | false -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      RestartSession = fun sessionId rebuild ->
        async {
          lock gate (fun () ->
            restartCalls <- restartCalls @ [sessionId, rebuild]
            rebuildGeneration <- rebuildGeneration + 1)
          return Result.Ok "restarted"
        }
      ListSessions = fun () ->
        async {
          let status =
            lock gate (fun () ->
              match readyGeneration >= rebuildGeneration && rebuildGeneration > 0 with
              | true -> SessionStatus.Ready
              | false -> SessionStatus.Starting)
          return [sessionInfo status]
        }
      SleepMs = fun _ -> async { return () }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    let firstRequest =
      SageFsEffect.TestCycle (
        Features.LiveTesting.TestCycleEffect.RequestRebuild(
          1L,
          { Tests = [| tc |]
            Trigger = Features.LiveTesting.RunTrigger.FileSave
            TreeSitterElapsed = TimeSpan.Zero
            FcsElapsed = TimeSpan.Zero
            SessionId = Some (SessionId.value sid)
            InstrumentationMaps = [||] }))
    let secondRequest =
      SageFsEffect.TestCycle (
        Features.LiveTesting.TestCycleEffect.RequestRebuild(
          2L,
          { Tests = [| tc |]
            Trigger = Features.LiveTesting.RunTrigger.FileSave
            TreeSitterElapsed = TimeSpan.Zero
            FcsElapsed = TimeSpan.Zero
            SessionId = Some (SessionId.value sid)
            InstrumentationMaps = [||] }))

    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      firstRequest
    |> Async.RunSynchronously

    let firstStart = Diagnostics.Stopwatch.StartNew()
    while restartCalls.Length < 1 && firstStart.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10
    restartCalls.Length
    |> Expect.equal "first rebuild should start promptly" 1

    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      secondRequest
    |> Async.RunSynchronously

    let secondStart = Diagnostics.Stopwatch.StartNew()
    while restartCalls.Length < 2 && secondStart.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10
    restartCalls.Length
    |> Expect.equal "second rebuild should supersede the first" 2

    lock gate (fun () -> readyGeneration <- rebuildGeneration)

    let completionWindow = Diagnostics.Stopwatch.StartNew()
    while dispatched.Length < 2 && completionWindow.ElapsedMilliseconds < 250L do
      Threading.Thread.Sleep 10

    dispatched
    |> Expect.equal
        "only the latest rebuild should report completion after superseding the older one"
        [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 2L, Ok ())]

  testCase "CancelRebuild cancels an in-flight RequestRebuild without dispatching RebuildCompleted" <| fun _ ->
    let sid = testSessionId "fadecafe"
    let mutable dispatched : SageFsMsg list = []
    let gate = obj()
    let mutable restartCalls = 0
    let mutable ready = false
    let tc : Features.LiveTesting.TestCase = {
      Id = Features.LiveTesting.TestId.TestId "t-explicit-cancel"
      FullName = "Sample.Tests.should cancel rebuild by generation"
      DisplayName = "should cancel rebuild by generation"
      Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
      Labels = []
      Framework = Features.LiveTesting.TestFramework.Expecto
      Category = Features.LiveTesting.TestCategory.Unit
    }
    let sessionInfo status : SessionInfo = {
      Id = sid
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = status
      FaultReason = None
      WorkerPid = Some 999
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ ->
        match lock gate (fun () -> ready) with
        | true -> Some (fun _ _ _ _ -> async { return () })
        | false -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      RestartSession = fun _ _ ->
        async {
          lock gate (fun () -> restartCalls <- restartCalls + 1)
          return Result.Ok "restarted"
        }
      ListSessions = fun () ->
        async {
          let status =
            lock gate (fun () ->
              match ready with
              | true -> SessionStatus.Ready
              | false -> SessionStatus.Starting)
          return [sessionInfo status]
        }
      SleepMs = fun _ -> async { do! Async.Sleep 10 }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }

    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      (SageFsEffect.TestCycle (
        Features.LiveTesting.TestCycleEffect.RequestRebuild(
          1L,
          { Tests = [| tc |]
            Trigger = Features.LiveTesting.RunTrigger.FileSave
            TreeSitterElapsed = TimeSpan.Zero
            FcsElapsed = TimeSpan.Zero
            SessionId = Some (SessionId.value sid)
            InstrumentationMaps = [||] })))
    |> Async.RunSynchronously

    let restartWindow = Diagnostics.Stopwatch.StartNew()
    while restartCalls < 1 && restartWindow.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10
    restartCalls
    |> Expect.equal "rebuild should have started before cancellation" 1

    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      (SageFsEffect.TestCycle (makeCancelRebuildEffect (Some (SessionId.value sid)) 1L))
    |> Async.RunSynchronously

    lock gate (fun () -> ready <- true)

    let completionWindow = Diagnostics.Stopwatch.StartNew()
    while dispatched.IsEmpty && completionWindow.ElapsedMilliseconds < 250L do
      Threading.Thread.Sleep 10

    dispatched
    |> Expect.isEmpty
        "explicit cancellation should suppress stale RebuildCompleted dispatch"

  testCase "CancelRebuild for an older generation does not cancel the newer rebuild" <| fun _ ->
    let sid = testSessionId "beadfeed"
    let mutable dispatched : SageFsMsg list = []
    let gate = obj()
    let mutable restartCalls = 0
    let mutable ready = false
    let tc : Features.LiveTesting.TestCase = {
      Id = Features.LiveTesting.TestId.TestId "t-stale-cancel"
      FullName = "Sample.Tests.should ignore stale cancel"
      DisplayName = "should ignore stale cancel"
      Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
      Labels = []
      Framework = Features.LiveTesting.TestFramework.Expecto
      Category = Features.LiveTesting.TestCategory.Unit
    }
    let sessionInfo status : SessionInfo = {
      Id = sid
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = status
      FaultReason = None
      WorkerPid = Some 999
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ ->
        match lock gate (fun () -> ready) with
        | true -> Some (fun _ _ _ _ -> async { return () })
        | false -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      RestartSession = fun _ _ ->
        async {
          lock gate (fun () -> restartCalls <- restartCalls + 1)
          return Result.Ok "restarted"
        }
      ListSessions = fun () ->
        async {
          let status =
            lock gate (fun () ->
              match ready with
              | true -> SessionStatus.Ready
              | false -> SessionStatus.Starting)
          return [sessionInfo status]
        }
      SleepMs = fun _ -> async { do! Async.Sleep 10 }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }

    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      (SageFsEffect.TestCycle (
        Features.LiveTesting.TestCycleEffect.RequestRebuild(
          2L,
          { Tests = [| tc |]
            Trigger = Features.LiveTesting.RunTrigger.FileSave
            TreeSitterElapsed = TimeSpan.Zero
            FcsElapsed = TimeSpan.Zero
            SessionId = Some (SessionId.value sid)
            InstrumentationMaps = [||] })))
    |> Async.RunSynchronously

    let restartWindow = Diagnostics.Stopwatch.StartNew()
    while restartCalls < 1 && restartWindow.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10
    restartCalls
    |> Expect.equal "newer rebuild should have started" 1

    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- dispatched @ [m])
      (SageFsEffect.TestCycle (makeCancelRebuildEffect (Some (SessionId.value sid)) 1L))
    |> Async.RunSynchronously

    lock gate (fun () -> ready <- true)

    let completionWindow = Diagnostics.Stopwatch.StartNew()
    while dispatched.IsEmpty && completionWindow.ElapsedMilliseconds < 2000L do
      Threading.Thread.Sleep 10

    dispatched
    |> Expect.equal
        "a stale cancel should not stop the newer rebuild from completing"
        [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 2L, Ok ())]

  testCase "RequestHistory is a no-op" <| fun _ ->
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute (TestDeps.noSessions ())
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor
        (EditorEffect.RequestHistory HistoryDirection.Previous))
    |> Async.RunSynchronously
    dispatched |> Expect.isEmpty "no dispatch"
]

[<Tests>]
let fullLoopTests = testList "Full ElmLoop + EffectHandler" [
  testCase "submit → eval → worker → result dispatched back" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.EvalCode (code, rid) ->
        WorkerResponse.EvalResult (
          rid, Result.Ok (sprintf "val it = %s" code), [], Map.empty)
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable lastModel : SageFsModel option = None
    let mutable lastRegions : RenderRegion list = []
    let program :
      ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = SageFsEffectHandler.execute deps
      OnModelChanged = fun model regions ->
        lastModel <- Some model
        lastRegions <- regions
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    dispatch (SageFsMsg.Editor (EditorAction.InsertChar '4'))
    dispatch (SageFsMsg.Editor (EditorAction.InsertChar '2'))
    dispatch (SageFsMsg.Editor EditorAction.Submit)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    while log.EvalCalls.Length < 1 && sw.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    log.EvalCalls |> Expect.hasLength "1 eval" 1
    let sw2 = System.Diagnostics.Stopwatch.StartNew()
    let hasResult () =
      match lastModel with
      | None -> false
      | Some m ->
        let active = m.RecentOutput.GetActiveBuffer(m.Sessions.ActiveSessionId)
        let testSess = m.RecentOutput.GetBuffer("a1b2c3d4")
        active |> Seq.exists (fun o -> o.Text.Contains "val it = 42")
        || testSess |> Seq.exists (fun o -> o.Text.Contains "val it = 42")
    while not (hasResult()) && sw2.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    hasResult() |> Expect.isTrue "should have eval result in output"
    lastRegions
    |> List.exists (fun r -> r.Id = "output")
    |> Expect.isTrue "should have output region"

  testCase "session create → stop full cycle" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable lastModel : SageFsModel option = None
    let program :
      ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = SageFsEffectHandler.execute deps
      OnModelChanged = fun model _ -> lastModel <- Some model
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    dispatch (SageFsMsg.Editor
      (EditorAction.CreateSession ["New.fsproj"]))
    let sw = System.Diagnostics.Stopwatch.StartNew()
    while (lastModel.IsNone || lastModel.Value.Sessions.Sessions.Length < 1)
          && sw.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    lastModel.Value.Sessions.Sessions
    |> Expect.hasLength "1 session" 1
    dispatch (SageFsMsg.Editor
      (EditorAction.StopSession "a1b2c3d4"))
    let sw2 = System.Diagnostics.Stopwatch.StartNew()
    while lastModel.Value.Sessions.Sessions.Length > 0
          && sw2.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    lastModel.Value.Sessions.Sessions
    |> Expect.isEmpty "0 sessions"

  testCase "completion request flows through full loop" <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.GetCompletions (_, _, rid) ->
        WorkerResponse.CompletionResult (
          rid, ["Length"; "Head"; "Tail"])
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable lastModel : SageFsModel option = None
    let program :
      ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = SageFsEffectHandler.execute deps
      OnModelChanged = fun model _ -> lastModel <- Some model
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    dispatch (SageFsMsg.Editor EditorAction.TriggerCompletion)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    while (lastModel.IsNone || lastModel.Value.Editor.CompletionMenu.IsNone)
          && sw.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    lastModel.Value.Editor.CompletionMenu
    |> Expect.isSome "should have menu"
    lastModel.Value.Editor.CompletionMenu.Value.Items
    |> Expect.hasLength "3 items" 3

  testAsync "RequestSessionList dispatches WarmupContextUpdated for Ready session" {
    let mutable dispatched : SageFsMsg list = []
    let dispatch msg = dispatched <- dispatched @ [msg]
    let readySession : SessionInfo = {
      Id = testSessionId "00000001"; Name = None; Projects = ["Proj.fsproj"]
      WorkingDirectory = "/code"; SolutionRoot = None
      CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
      Status = SessionStatus.Ready; WorkerPid = Some 42
      FaultReason = None
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let warmup : WarmupContext = {
      AssembliesLoaded =
        [{ Name = "A"; Path = "A.dll"; NamespaceCount = 3; ModuleCount = 1 }]
      NamespacesOpened =
        [{ Name = "System"; Kind = OpenableKind.Namespace; Source = "warmup"; DurationMs = 0.0 }]
      FailedOpens = []; PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 500L }
      SourceFilesScanned = 2; StartedAt = DateTimeOffset.UtcNow
    }
    let getWarmupCtx (sid: SessionId) = async {
      return Some {
        SessionId = SessionId.value sid; ProjectNames = ["Proj.fsproj"]
        WorkingDir = "/code"; Status = "Ready"
        Warmup = warmup; FileStatuses = []
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        AutoOpenNamespaces = true
      }
    }
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Error (SageFsError.NoActiveSessions)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error (SageFsError.NoActiveSessions) }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error (SageFsError.NoActiveSessions) }
      RestartSession = fun _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ListSessions = fun () -> async { return [readySession] }
      SleepMs = fun _ -> async { return () }
      GetWarmupContext = Some getWarmupCtx
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    do! SageFsEffectHandler.execute deps dispatch
          (SageFsEffect.Editor EditorEffect.RequestSessionList)
    dispatched
    |> List.exists (fun m ->
      match m with
      | SageFsMsg.Event (SageFsEvent.WarmupContextUpdated ctx) ->
        ctx.SessionId = "00000001"
      | _ -> false)
    |> Expect.isTrue "Should dispatch WarmupContextUpdated for Ready session"
  }

  testAsync "RequestSessionList skips warmup when GetWarmupContext is None" {
    let mutable dispatched : SageFsMsg list = []
    let dispatch msg = dispatched <- dispatched @ [msg]
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Error (SageFsError.NoActiveSessions)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error (SageFsError.NoActiveSessions) }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error (SageFsError.NoActiveSessions) }
      RestartSession = fun _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ListSessions = fun () -> async {
        return [{ Id = testSessionId "00000002"; Name = None; Projects = ["T.fsproj"]
                  WorkingDirectory = "."; SolutionRoot = None
                  CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
                  Status = SessionStatus.Ready; WorkerPid = Some 1
                  FaultReason = None
                  Workflow = WorkflowTypes.SessionWorkflow.Interactive }]
      }
      SleepMs = fun _ -> async { return () }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    do! SageFsEffectHandler.execute deps dispatch
          (SageFsEffect.Editor EditorEffect.RequestSessionList)
    dispatched
    |> List.exists (fun m ->
      match m with
      | SageFsMsg.Event (SageFsEvent.WarmupContextUpdated _) -> true
      | _ -> false)
    |> Expect.isFalse
          "Should NOT dispatch WarmupContextUpdated when GetWarmupContext is None"
  }

  testAsync "RequestSessionList skips warmup when no Ready session" {
    let mutable dispatched : SageFsMsg list = []
    let dispatch msg = dispatched <- dispatched @ [msg]
    let mutable ctxCalled = false
    let deps : EffectDeps = {
      ResolveSession = fun _ ->
        Result.Error (SageFsError.NoActiveSessions)
      GetProxy = fun _ -> None
      GetStreamingTestProxy = fun _ -> None
      CreateSession = fun _ _ _ ->
        async { return Result.Error (SageFsError.NoActiveSessions) }
      ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
      StopSession = fun _ ->
        async { return Result.Error (SageFsError.NoActiveSessions) }
      RestartSession = fun _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ListSessions = fun () -> async {
        return [{ Id = testSessionId "00000003"; Name = None; Projects = ["T.fsproj"]
                  WorkingDirectory = "."; SolutionRoot = None
                  CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
                  Status = SessionStatus.Starting; WorkerPid = None
                  FaultReason = None
                  Workflow = WorkflowTypes.SessionWorkflow.Interactive }]
      }
      SleepMs = fun _ -> async { return () }
      GetWarmupContext =
        Some (fun _ -> async { ctxCalled <- true; return None })
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    do! SageFsEffectHandler.execute deps dispatch
          (SageFsEffect.Editor EditorEffect.RequestSessionList)
    Expect.isFalse
      "Should not call GetWarmupContext when no Ready session" ctxCalled
  }
]
