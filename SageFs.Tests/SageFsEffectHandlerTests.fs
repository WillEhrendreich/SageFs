module SageFs.Tests.SageFsEffectHandlerTests

open System
open System.Threading.Tasks
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

  /// Await a condition with a hard ceiling, without sleep-polling.
  /// Yields via Task.Delay so the thread pool is never hogged; returns true
  /// only when the condition was satisfied before the ceiling elapsed.
  let awaitCondition (timeoutMs: int) (condition: unit -> bool) =
    task {
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let mutable ok = false
      while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
        if condition () then ok <- true
        else do! Task.Delay 10
      return ok
    }

  /// Await a TaskCompletionSource with a hard ceiling. Completes the TCS with
  /// false when the timeout elapses, so a timed-out wait fails the test with a
  /// clear signal instead of hanging.
  let awaitTcs (timeoutMs: int) (tcs: TaskCompletionSource<bool>) =
    task {
      let! winner =
        Task.WhenAny(tcs.Task, Task.Delay(timeoutMs))
      let completed = obj.ReferenceEquals(winner, tcs.Task)
      if not completed then tcs.TrySetResult false |> ignore
      return completed
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
  testTask "RequestEval sends code to worker and dispatches result" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.EvalCode (_, rid) ->
        WorkerResponse.EvalResult (rid, Result.Ok "val x = 42", [], Map.empty)
      | _ ->
        WorkerResponse.WorkerError (
          SageFsError.Unexpected (exn "unexpected")))
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestEval "let x = 42"))
    log.EvalCalls
    |> Expect.hasLength "should call eval" 1
    snd log.EvalCalls.[0]
    |> Expect.equal "code" "let x = 42"
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalCompleted (sid, output, _)) ->
      sid |> Expect.equal "session" "a1b2c3d4"
      output |> Expect.equal "output" "val x = 42"
    | other -> failtestf "expected EvalCompleted, got %A" other
  }

  testTask "RequestEval error dispatches EvalFailed" {
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
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestEval "bad"))
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalFailed (_, err)) ->
      err |> Expect.stringContains "err" "type mismatch"
    | other -> failtestf "expected EvalFailed, got %A" other
  }

  testTask "RequestConfigureWarmupAutoOpen dispatches output message" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "unused")))
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestConfigureWarmupAutoOpen @"C:\Code\Repos\TestProject"))
    log.ConfigureAutoOpenCalls
    |> Expect.equal "should call config helper" [@"C:\Code\Repos\TestProject"]
    match dispatched with
    | [SageFsMsg.Event (SageFsEvent.OutputEmitted line)] ->
      line.Kind |> Expect.equal "should emit system output" OutputKind.System
      line.Text |> Expect.stringContains "should describe the opt-out" "Disabled warmup auto-open"
    | other ->
      failtestf "expected OutputEmitted, got %A" other
  }

  testTask "RequestEval converts worker diagnostics" {
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
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestEval "code"))
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalCompleted (_, _, diags)) ->
      diags |> Expect.hasLength "1 diag" 1
      diags.[0].Message |> Expect.equal "msg" "FS0001"
      diags.[0].Severity |> Expect.equal "sev" DiagnosticSeverity.Error
    | other ->
      failtestf "expected EvalCompleted with diags, got %A" other
  }

  testTask "RequestCompletion dispatches items" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.GetCompletions (_, _, rid) ->
        WorkerResponse.CompletionResult (rid, ["ToString"; "GetType"])
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestCompletion ("x.", 2)))
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.CompletionReady items) ->
      items |> Expect.hasLength "2 items" 2
      items.[0].Label |> Expect.equal "first" "ToString"
    | other -> failtestf "expected CompletionReady, got %A" other
  }

  testTask "RequestInitialDiscovery asks the worker for test discovery" {
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
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.TestCycle Features.LiveTesting.TestCycleEffect.RequestInitialDiscovery)
    log.SessionListCalls |> Expect.equal "should enumerate sessions for discovery" 1
    log.TestDiscoveryCalls |> Expect.hasLength "should request discovery once" 1
    dispatched
    |> List.exists (fun msg ->
      match msg with
      | SageFsMsg.Event (SageFsEvent.TestsDiscovered (sid, tests)) ->
        sid = "a1b2c3d4" && tests.Length = 1
      | _ -> false)
    |> Expect.isTrue "should dispatch discovered tests back into the Elm loop"
  }

  testTask "RequestEval with no sessions dispatches error" {
    let deps = TestDeps.noSessions ()
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestEval "x"))
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalFailed (_, err)) ->
      err |> Expect.stringContains "no sessions" "No active"
    | other -> failtestf "expected error, got %A" other
  }

  testTask "RequestSessionList dispatches snapshots" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor EditorEffect.RequestSessionList)
    log.SessionListCalls |> Expect.equal "called" 1
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.SessionsRefreshed snaps) ->
      snaps |> Expect.hasLength "one session" 1
      snaps.[0].Id |> Expect.equal "id" (testSessionId "a1b2c3d4")
    | other -> failtestf "expected SessionsRefreshed, got %A" other
  }

  testTask "RequestSessionSwitch dispatches switch" {
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute (TestDeps.noSessions ())
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestSessionSwitch "s2"))
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.SessionSwitched (_, toId)) ->
      toId |> Expect.equal "to" "s2"
    | other -> failtestf "expected SessionSwitched, got %A" other
  }

  testTask "RequestSessionCreate dispatches created" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor
            (EditorEffect.RequestSessionCreate ["New.fsproj"]))
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
  }

  testTask "RequestSessionStop dispatches stopped" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestSessionStop "00000001"))
    log.SessionStopCalls |> Expect.equal "called" [testSessionId "00000001"]
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.SessionStopped sid) ->
      sid |> Expect.equal "id" "00000001"
    | other -> failtestf "expected SessionStopped, got %A" other
  }

  testTask "RequestSessionStop failure dispatches error" {
    let deps = {
      TestDeps.noSessions () with
        StopSession = fun _ ->
          async {
            return Result.Error (SageFsError.SessionNotFound "00000001")
          }
    }
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor (EditorEffect.RequestSessionStop "00000001"))
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.EvalFailed (_, err)) ->
      err |> Expect.stringContains "fail" "Stop failed"
    | other -> failtestf "expected error, got %A" other
  }

  testTask "RequestFcsTypeCheck uses provided buffer content instead of rereading disk" {
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
      do! SageFsEffectHandler.execute deps
            (fun msg -> dispatched <- msg :: dispatched)
            (SageFsEffect.TestCycle effect)

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
  }

  testTask "RequestRebuild waits for restarted session proxy before reporting success" {
    let sid = testSessionId "a1b2c3d4"
    let mutable dispatched : SageFsMsg list = []
    let completed = TaskCompletionSource<bool>()
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
      SleepMs = fun delay -> async { do! Async.Sleep delay }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    do! SageFsEffectHandler.execute deps
          (fun m ->
            dispatched <- dispatched @ [m]
            match m with
            | SageFsMsg.RebuildCompleted _ -> completed.TrySetResult true |> ignore
            | _ -> ())
          (SageFsEffect.TestCycle (
            Features.LiveTesting.TestCycleEffect.RequestRebuild(
              1L,
              { Tests = [| tc |]
                Trigger = Features.LiveTesting.RunTrigger.FileSave
                TreeSitterElapsed = TimeSpan.Zero
                FcsElapsed = TimeSpan.Zero
                SessionId = Some (SessionId.value sid)
                InstrumentationMaps = [||] })))
    let! doneSignal = TestDeps.awaitTcs 5000 completed
    doneSignal |> Expect.isTrue "rebuild should complete"
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
  }

  testTask "RequestRebuild keeps waiting while restarted session is still starting" {
    let sid = testSessionId "d4c3b2a1"
    let mutable dispatched : SageFsMsg list = []
    let completed = TaskCompletionSource<bool>()
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
      SleepMs = fun delay -> async { do! Async.Sleep delay }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }
    do! SageFsEffectHandler.execute deps
          (fun m ->
            dispatched <- dispatched @ [m]
            match m with
            | SageFsMsg.RebuildCompleted _ -> completed.TrySetResult true |> ignore
            | _ -> ())
          (SageFsEffect.TestCycle (
            Features.LiveTesting.TestCycleEffect.RequestRebuild(
              1L,
              { Tests = [| tc |]
                Trigger = Features.LiveTesting.RunTrigger.FileSave
                TreeSitterElapsed = TimeSpan.Zero
                FcsElapsed = TimeSpan.Zero
                SessionId = Some (SessionId.value sid)
                InstrumentationMaps = [||] })))
    let! doneSignal = TestDeps.awaitTcs 5000 completed
    doneSignal |> Expect.isTrue "rebuild should complete"
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
  }

  testTask "RequestRebuild uses a short poll cadence during the first second of readiness wait" {
    let sid = testSessionId "c0ffee01"
    let mutable dispatched : SageFsMsg list = []
    let completed = TaskCompletionSource<bool>()
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
    do! SageFsEffectHandler.execute deps
          (fun m ->
            dispatched <- dispatched @ [m]
            match m with
            | SageFsMsg.RebuildCompleted _ -> completed.TrySetResult true |> ignore
            | _ -> ())
          (SageFsEffect.TestCycle (
            Features.LiveTesting.TestCycleEffect.RequestRebuild(
              1L,
              { Tests = [| tc |]
                Trigger = Features.LiveTesting.RunTrigger.FileSave
                TreeSitterElapsed = TimeSpan.Zero
                FcsElapsed = TimeSpan.Zero
                SessionId = Some (SessionId.value sid)
                InstrumentationMaps = [||] })))
    let! doneSignal = TestDeps.awaitTcs 5000 completed
    doneSignal |> Expect.isTrue "rebuild should complete"
    sleepCalls |> Seq.toList
    |> Expect.equal "fast startup should stay on the short poll cadence" [50; 50; 50; 50]
    dispatched
    |> Expect.equal "fast startup should still complete successfully" [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 1L, Ok ())]
  }

  testTask "RequestRebuild switches to a slower poll cadence after the first second" {
    let sid = testSessionId "c0ffee02"
    let mutable dispatched : SageFsMsg list = []
    let completed = TaskCompletionSource<bool>()
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
    do! SageFsEffectHandler.execute deps
          (fun m ->
            dispatched <- dispatched @ [m]
            match m with
            | SageFsMsg.RebuildCompleted _ -> completed.TrySetResult true |> ignore
            | _ -> ())
          (SageFsEffect.TestCycle (
            Features.LiveTesting.TestCycleEffect.RequestRebuild(
              1L,
              { Tests = [| tc |]
                Trigger = Features.LiveTesting.RunTrigger.FileSave
                TreeSitterElapsed = TimeSpan.Zero
                FcsElapsed = TimeSpan.Zero
                SessionId = Some (SessionId.value sid)
                InstrumentationMaps = [||] })))
    let! doneSignal = TestDeps.awaitTcs 5000 completed
    doneSignal |> Expect.isTrue "rebuild should complete"
    let recordedSleeps = sleepCalls |> Seq.toList
    recordedSleeps.Length
    |> Expect.equal "long startup should keep polling until the ready/proxy pair finally arrives" 21
    recordedSleeps |> List.take 20
    |> Expect.equal "the first second should use the short cadence" (List.replicate 20 50)
    recordedSleeps |> List.last
    |> Expect.equal "polling should slow down once the first second has elapsed" 250
    dispatched
    |> Expect.equal "long startup should still complete successfully" [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 1L, Ok ())]
  }

  testTask "superseded RequestRebuild suppresses stale completion from the older rebuild" {
    let sid = testSessionId "c0ffee03"
    let mutable dispatched : SageFsMsg list = []
    let completed = TaskCompletionSource<bool>()
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
      SleepMs = fun delay -> async { do! Async.Sleep delay }
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

    let dispatch m =
      dispatched <- dispatched @ [m]
      match m with
      | SageFsMsg.RebuildCompleted _ -> completed.TrySetResult true |> ignore
      | _ -> ()

    do! SageFsEffectHandler.execute deps dispatch firstRequest

    let! firstStarted = TestDeps.awaitCondition 2000 (fun () ->
      restartCalls.Length >= 1)
    firstStarted |> Expect.isTrue "first rebuild should start promptly"
    restartCalls.Length
    |> Expect.equal "first rebuild should start promptly" 1

    do! SageFsEffectHandler.execute deps dispatch secondRequest

    let! secondStarted = TestDeps.awaitCondition 2000 (fun () ->
      restartCalls.Length >= 2)
    secondStarted |> Expect.isTrue "second rebuild should supersede the first"
    restartCalls.Length
    |> Expect.equal "second rebuild should supersede the first" 2

    lock gate (fun () -> readyGeneration <- rebuildGeneration)

    let! doneSignal = TestDeps.awaitTcs 5000 completed
    doneSignal |> Expect.isTrue "second rebuild should complete"

    dispatched
    |> Expect.equal
        "only the latest rebuild should report completion after superseding the older one"
        [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 2L, Ok ())]
  }

  testTask "CancelRebuild cancels an in-flight RequestRebuild without dispatching RebuildCompleted" {
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

    do! SageFsEffectHandler.execute deps
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

    let! restarted = TestDeps.awaitCondition 2000 (fun () ->
      restartCalls >= 1)
    restarted |> Expect.isTrue "rebuild should have started before cancellation"
    restartCalls
    |> Expect.equal "rebuild should have started before cancellation" 1

    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- dispatched @ [m])
          (SageFsEffect.TestCycle (makeCancelRebuildEffect (Some (SessionId.value sid)) 1L))

    lock gate (fun () -> ready <- true)

    // Give the cancelled rebuild's polling loop a bounded chance to run; it
    // must never dispatch a stale completion.
    let! completed = TestDeps.awaitCondition 250 (fun () ->
      dispatched.Length > 0)

    dispatched
    |> Expect.isEmpty
        "explicit cancellation should suppress stale RebuildCompleted dispatch"
  }

  testTask "CancelRebuild for an older generation does not cancel the newer rebuild" {
    let sid = testSessionId "beadfeed"
    let mutable dispatched : SageFsMsg list = []
    let completed = TaskCompletionSource<bool>()
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

    do! SageFsEffectHandler.execute deps
          (fun m ->
            dispatched <- dispatched @ [m]
            match m with
            | SageFsMsg.RebuildCompleted _ -> completed.TrySetResult true |> ignore
            | _ -> ())
          (SageFsEffect.TestCycle (
            Features.LiveTesting.TestCycleEffect.RequestRebuild(
              2L,
              { Tests = [| tc |]
                Trigger = Features.LiveTesting.RunTrigger.FileSave
                TreeSitterElapsed = TimeSpan.Zero
                FcsElapsed = TimeSpan.Zero
                SessionId = Some (SessionId.value sid)
                InstrumentationMaps = [||] })))

    let! restarted = TestDeps.awaitCondition 2000 (fun () ->
      restartCalls >= 1)
    restarted |> Expect.isTrue "newer rebuild should have started"
    restartCalls
    |> Expect.equal "newer rebuild should have started" 1

    do! SageFsEffectHandler.execute deps
          (fun m -> dispatched <- dispatched @ [m])
          (SageFsEffect.TestCycle (makeCancelRebuildEffect (Some (SessionId.value sid)) 1L))

    lock gate (fun () -> ready <- true)

    let! doneSignal = TestDeps.awaitTcs 5000 completed
    doneSignal |> Expect.isTrue "newer rebuild should complete"

    dispatched
    |> Expect.equal
        "a stale cancel should not stop the newer rebuild from completing"
        [SageFsMsg.RebuildCompleted (Some (SessionId.value sid), 2L, Ok ())]
  }

  testTask "RequestHistory is a no-op" {
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute (TestDeps.noSessions ())
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor
            (EditorEffect.RequestHistory HistoryDirection.Previous))
    dispatched |> Expect.isEmpty "no dispatch"
  }
]

[<Tests>]
let fullLoopTests = testList "Full ElmLoop + EffectHandler" [
  testTask "submit → eval → worker → result dispatched back" {
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
    let resultArrived = TaskCompletionSource<bool>()
    let program :
      ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = SageFsEffectHandler.execute deps
      OnModelChanged = fun model regions ->
        lastModel <- Some model
        lastRegions <- regions
        let hasResult () =
          let active = model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)
          let testSess = model.RecentOutput.GetBuffer("a1b2c3d4")
          active |> Seq.exists (fun o -> o.Text.Contains "val it = 42")
          || testSess |> Seq.exists (fun o -> o.Text.Contains "val it = 42")
        if hasResult () then resultArrived.TrySetResult true |> ignore
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    dispatch (SageFsMsg.Editor (EditorAction.InsertChar '4'))
    dispatch (SageFsMsg.Editor (EditorAction.InsertChar '2'))
    dispatch (SageFsMsg.Editor EditorAction.Submit)
    let! doneSignal = TestDeps.awaitTcs 5000 resultArrived
    doneSignal |> Expect.isTrue "should have eval result in output"
    log.EvalCalls |> Expect.hasLength "1 eval" 1
    lastRegions
    |> List.exists (fun r -> r.Id = "output")
    |> Expect.isTrue "should have output region"
  }

  testTask "session create → stop full cycle" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable lastModel : SageFsModel option = None
    let created = TaskCompletionSource<bool>()
    let stopped = TaskCompletionSource<bool>()
    let mutable hadSession = false
    let program :
      ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = SageFsEffectHandler.execute deps
      OnModelChanged = fun model _ ->
        lastModel <- Some model
        if model.Sessions.Sessions.Length >= 1 then
          hadSession <- true
          created.TrySetResult true |> ignore
        if hadSession && model.Sessions.Sessions.Length = 0 then
          stopped.TrySetResult true |> ignore
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    dispatch (SageFsMsg.Editor
      (EditorAction.CreateSession ["New.fsproj"]))
    let! createdSignal = TestDeps.awaitTcs 5000 created
    createdSignal |> Expect.isTrue "should create the session"
    lastModel.Value.Sessions.Sessions
    |> Expect.hasLength "1 session" 1
    dispatch (SageFsMsg.Editor
      (EditorAction.StopSession "a1b2c3d4"))
    let! stoppedSignal = TestDeps.awaitTcs 5000 stopped
    stoppedSignal |> Expect.isTrue "should stop the session"
    lastModel.Value.Sessions.Sessions
    |> Expect.isEmpty "0 sessions"
  }

  testTask "completion request flows through full loop" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.GetCompletions (_, _, rid) ->
        WorkerResponse.CompletionResult (
          rid, ["Length"; "Head"; "Tail"])
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "x")))
    let mutable lastModel : SageFsModel option = None
    let menuArrived = TaskCompletionSource<bool>()
    let program :
      ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = SageFsEffectHandler.execute deps
      OnModelChanged = fun model _ ->
        lastModel <- Some model
        if model.Editor.CompletionMenu.IsSome then
          menuArrived.TrySetResult true |> ignore
      OnSystemAlarm = fun _ _ -> ()
    }
    let dispatch = (ElmLoop.start program (SageFsModel.initial()) System.Threading.CancellationToken.None).Dispatch
    dispatch (SageFsMsg.Editor EditorAction.TriggerCompletion)
    let! menuSignal = TestDeps.awaitTcs 5000 menuArrived
    menuSignal |> Expect.isTrue "should have menu"
    lastModel.Value.Editor.CompletionMenu
    |> Expect.isSome "should have menu"
    lastModel.Value.Editor.CompletionMenu.Value.Items
    |> Expect.hasLength "3 items" 3
  }

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
