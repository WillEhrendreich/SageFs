module SageFs.Tests.SageFsEffectHandlerTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.Diagnostics

module TestDeps =

  type CallLog = {
    mutable EvalCalls: (string * string) list
    mutable CompletionCalls: (string * string * int) list
    mutable SessionListCalls: int
    mutable SessionCreateCalls: (string list * string) list
    mutable SessionStopCalls: string list
  }

  let createLog () = {
    EvalCalls = []
    CompletionCalls = []
    SessionListCalls = 0
    SessionCreateCalls = []
    SessionStopCalls = []
  }

  let singleSession
    (log: CallLog)
    (handler: WorkerMessage -> WorkerResponse) : EffectDeps =
    let sessionInfo : SessionInfo = {
      Id = "test-session"
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
        | _ -> ()
        return handler msg
      }
    {
      ResolveSession = fun _ ->
        Result.Ok (
          SessionOperations.SessionResolution.DefaultSingle "test-session")
      GetProxy = fun id ->
        if id = "test-session" then Some proxy else None
      CreateSession = fun projects dir ->
        async {
          log.SessionCreateCalls <-
            log.SessionCreateCalls @ [projects, dir]
          return Result.Ok sessionInfo
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
    }

  let noSessions () : EffectDeps =
    {
      ResolveSession = fun _ ->
        Result.Error (SageFsError.NoActiveSessions)
      GetProxy = fun _ -> None
      CreateSession = fun projects dir ->
        async {
          let info : SessionInfo = {
            Id = "new-session"
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
        async { return Result.Error (SageFsError.SessionNotFound id) }
      ListSessions = fun () -> async { return [] }
    }

[<Tests>]
let effectHandlerTests = testList "SageFsEffectHandler" [
  testCase "RequestEval sends code to worker and dispatches result"
    <| fun _ ->
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.EvalCode (_, rid) ->
        WorkerResponse.EvalResult (rid, Result.Ok "val x = 42", [])
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
      sid |> Expect.equal "session" "test-session"
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
          [])
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
        WorkerResponse.EvalResult (rid, Result.Ok "ok", [diag])
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
      snaps.[0].Id |> Expect.equal "id" "test-session"
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
      (SageFsEffect.Editor (EditorEffect.RequestSessionStop "s1"))
    |> Async.RunSynchronously
    log.SessionStopCalls |> Expect.equal "called" ["s1"]
    match dispatched.[0] with
    | SageFsMsg.Event (SageFsEvent.SessionStopped sid) ->
      sid |> Expect.equal "id" "s1"
    | other -> failtestf "expected SessionStopped, got %A" other

  testCase "RequestSessionStop failure dispatches error" <| fun _ ->
    let deps = {
      TestDeps.noSessions () with
        StopSession = fun _ ->
          async {
            return Result.Error (SageFsError.SessionNotFound "s1")
          }
    }
    let mutable dispatched : SageFsMsg list = []
    SageFsEffectHandler.execute deps
      (fun m -> dispatched <- m :: dispatched)
      (SageFsEffect.Editor (EditorEffect.RequestSessionStop "s1"))
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
          rid, Result.Ok (sprintf "val it = %s" code), [])
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
    }
    let dispatch = (ElmLoop.start program SageFsModel.initial).Dispatch
    dispatch (SageFsMsg.Editor (EditorAction.InsertChar '4'))
    dispatch (SageFsMsg.Editor (EditorAction.InsertChar '2'))
    dispatch (SageFsMsg.Editor EditorAction.Submit)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    while log.EvalCalls.Length < 1 && sw.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    log.EvalCalls |> Expect.hasLength "1 eval" 1
    let sw2 = System.Diagnostics.Stopwatch.StartNew()
    while (lastModel.IsNone || lastModel.Value.RecentOutput.IsEmpty)
          && sw2.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    lastModel.Value.RecentOutput
    |> List.exists (fun o -> o.Text.Contains "val it = 42")
    |> Expect.isTrue "should have eval result in output"
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
    }
    let dispatch = (ElmLoop.start program SageFsModel.initial).Dispatch
    dispatch (SageFsMsg.Editor
      (EditorAction.CreateSession ["New.fsproj"]))
    let sw = System.Diagnostics.Stopwatch.StartNew()
    while (lastModel.IsNone || lastModel.Value.Sessions.Sessions.Length < 1)
          && sw.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    lastModel.Value.Sessions.Sessions
    |> Expect.hasLength "1 session" 1
    dispatch (SageFsMsg.Editor
      (EditorAction.StopSession "test-session"))
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
    }
    let dispatch = (ElmLoop.start program SageFsModel.initial).Dispatch
    dispatch (SageFsMsg.Editor EditorAction.TriggerCompletion)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    while (lastModel.IsNone || lastModel.Value.Editor.CompletionMenu.IsNone)
          && sw.ElapsedMilliseconds < 2000L do
      System.Threading.Thread.Sleep 10
    lastModel.Value.Editor.CompletionMenu
    |> Expect.isSome "should have menu"
    lastModel.Value.Editor.CompletionMenu.Value.Items
    |> Expect.hasLength "3 items" 3
]
