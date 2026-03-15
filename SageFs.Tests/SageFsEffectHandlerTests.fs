module SageFs.Tests.SageFsEffectHandlerTests

open System
open Expecto
open Expecto.Flip
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
      WorkerPid = Some 999
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
      ListSessions = fun () ->
        async {
          log.SessionListCalls <- log.SessionListCalls + 1
          return [sessionInfo]
        }
      GetWarmupContext = None
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
            WorkerPid = None
          }
          return Result.Ok info
        }
      StopSession = fun id ->
        async { return Result.Error (SageFsError.SessionNotFound (SessionId.value id)) }
      ListSessions = fun () -> async { return [] }
      ConfigureWarmupAutoOpen = ensureAutoOpenNoop
      GetWarmupContext = None
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }

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
      ListSessions = fun () -> async { return [readySession] }
      GetWarmupContext = Some getWarmupCtx
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
      ListSessions = fun () -> async {
        return [{ Id = testSessionId "00000002"; Name = None; Projects = ["T.fsproj"]
                  WorkingDirectory = "."; SolutionRoot = None
                  CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
                  Status = SessionStatus.Ready; WorkerPid = Some 1 }]
      }
      GetWarmupContext = None
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
      ListSessions = fun () -> async {
        return [{ Id = testSessionId "00000003"; Name = None; Projects = ["T.fsproj"]
                  WorkingDirectory = "."; SolutionRoot = None
                  CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
                  Status = SessionStatus.Starting; WorkerPid = None }]
      }
      GetWarmupContext =
        Some (fun _ -> async { ctxCalled <- true; return None })
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    do! SageFsEffectHandler.execute deps dispatch
          (SageFsEffect.Editor EditorEffect.RequestSessionList)
    Expect.isFalse
      "Should not call GetWarmupContext when no Ready session" ctxCalled
  }
]
