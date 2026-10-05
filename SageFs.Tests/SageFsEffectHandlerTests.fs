module SageFs.Tests.SageFsEffectHandlerTests

open System
open System.Threading.Tasks
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
    mutable SessionCreateCalls: (SessionProjectTarget list * string) list
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
      Status = SessionLifecycleStatus.Ready { Pid = 999; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync
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
      CreateSession = fun targets dir _workflow ->
        async {
          log.SessionCreateCalls <-
            log.SessionCreateCalls @ [targets, dir]
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
      AwaitReady = fun _ -> async { return Result.Ok () }
      ReadyDeadline = Timeouts.rebuildReadyWait
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
      CreateSession = fun targets dir _workflow ->
        async {
          let info : SessionInfo = {
            Id = testSessionId "b2c3d4e5"
            Name = None
            Projects = SessionProjectTarget.projects targets
            WorkingDirectory = dir
            SolutionRoot = None
            CreatedAt = DateTime.UtcNow
            LastActivity = DateTime.UtcNow
            Status = SessionLifecycleStatus.Starting { Pid = 0; Port = None }
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
            ActiveProject = None
            ProjectRoles = []
            App = SageFs.AppRun.AppRunState.NotRunning
            Rebuild = LastRebuild.NeverRebuilt
            Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync
          }
          return Result.Ok info
        }
      StopSession = fun id ->
        async { return Result.Error (SageFsError.SessionNotFound (SessionId.value id)) }
      RestartSession = fun _ _ ->
        async { return Result.Error SageFsError.NoActiveSessions }
      ListSessions = fun () -> async { return [] }
      ConfigureWarmupAutoOpen = ensureAutoOpenNoop
      AwaitReady = fun _ -> async { return Result.Ok () }
      ReadyDeadline = Timeouts.rebuildReadyWait
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
        else do! Task.Delay TestTimeouts.inProcessPoll
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

/// Run `git <args>` and return its trimmed stdout. An INDEPENDENT oracle: a separate,
/// minimal spawn from the one under test, so a bug shared by both cannot hide.
/// `System.Diagnostics` is qualified because `open SageFs.WarmUp` puts its own
/// `Diagnostics` in scope. Arguments go on `ArgumentList`, never a joined string.
let private git (args: string list) : string =
  let psi = System.Diagnostics.ProcessStartInfo("git")
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.UseShellExecute <- false
  for arg in args do psi.ArgumentList.Add arg
  use proc = System.Diagnostics.Process.Start psi
  let output = proc.StandardOutput.ReadToEnd().Trim()
  proc.WaitForExit TestTimeouts.childExit |> ignore
  output

/// `git hash-object` over a temp file holding `text`, so the production hash is
/// checked against GIT's rather than against the author's understanding of git's
/// header. A tree with no `git` has no oracle, and the case says so loudly rather
/// than passing against the very formula it is checking.
let private gitBlobOf (text: string) : string =
  let path = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-blob-oracle-%s.fs" (Guid.NewGuid().ToString "N"))
  IO.File.WriteAllText (path, text)
  try git [ "hash-object"; path ]
  finally
    if IO.File.Exists path then IO.File.Delete path

/// Is a real `git` on PATH?
let private gitAvailable () =
  try
    use proc =
      System.Diagnostics.Process.Start (
        System.Diagnostics.ProcessStartInfo(
          "git", "--version",
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false))
    proc.WaitForExit TestTimeouts.childExit |> ignore
    proc.HasExited && proc.ExitCode = 0
  with _ -> false

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
    | SageFsMsg.Event (TuiEvent.EvalCompleted (sid, output, _)) ->
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
    | SageFsMsg.Event (TuiEvent.EvalFailed (_, err)) ->
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
    | [SageFsMsg.Event (TuiEvent.OutputEmitted line)] ->
      line.Kind |> Expect.equal "should emit system output" OutputKind.System
      line.Text |> Expect.stringContains "should describe the opt-out" "Disabled warmup auto-open"
    | other ->
      failtestf "expected OutputEmitted, got %A" other
  }

  testTask "RequestEval converts worker diagnostics" {
    let log = TestDeps.createLog ()
    let diag : WorkerDiagnostic = {
      Severity = DiagnosticSeverity.Blocking
      Message = "FS0001"
      StartLine = 1; StartColumn = 5
      EndLine = 1; EndColumn = 10
      ErrorNumber = 1
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
    | SageFsMsg.Event (TuiEvent.EvalCompleted (_, _, diags)) ->
      diags |> Expect.hasLength "1 diag" 1
      diags.[0].Message |> Expect.equal "msg" "FS0001"
      diags.[0].Severity |> Expect.equal "sev" DiagnosticSeverity.Blocking
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
    | SageFsMsg.Event (TuiEvent.CompletionReady items) ->
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
      | SageFsMsg.Event (TuiEvent.TestsDiscovered (sid, tests)) ->
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
    | SageFsMsg.Event (TuiEvent.EvalFailed (_, err)) ->
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
    | SageFsMsg.Event (TuiEvent.SessionsRefreshed snaps) ->
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
    | SageFsMsg.Event (TuiEvent.SessionSwitched (_, toId)) ->
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
        | SageFsMsg.Event (TuiEvent.SessionCreated snap) -> Some snap
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
    | SageFsMsg.Event (TuiEvent.SessionStopped sid) ->
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
    | SageFsMsg.Event (TuiEvent.EvalFailed (_, err)) ->
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
            WorkerResponse.TypeCheckWithSymbolsResult(rid, [], [])
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

  testTask "WHY — RequestFcsTypeCheck — a worker that cannot be reached cancels the check instead of raising a system alarm, because a restarting worker is not a daemon fault" {
    let log = TestDeps.createLog ()
    let deps = TestDeps.singleSession log (fun _ ->
      raise (System.Net.Http.HttpRequestException "An error occurred while sending the request."))
    let effect = makeRequestFcsTypeCheckEffect None "/src/Math.fs" "module Math\nlet x = 1" "buffer-v1" TimeSpan.Zero
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute deps
          (fun msg -> dispatched <- msg :: dispatched)
          (SageFsEffect.TestCycle effect)
    dispatched
    |> List.exists (function
      | SageFsMsg.FcsTypeCheckCompleted (_, _, Features.LiveTesting.FcsTypeCheckResult.Cancelled "/src/Math.fs") -> true
      | _ -> false)
    |> Expect.isTrue "the check is reported as cancelled"
  }

  testTask "RequestHistory is a no-op" {
    let mutable dispatched : SageFsMsg list = []
    do! SageFsEffectHandler.execute (TestDeps.noSessions ())
          (fun m -> dispatched <- m :: dispatched)
          (SageFsEffect.Editor
            (EditorEffect.RequestHistory HistoryDirection.Previous))
    dispatched |> Expect.isEmpty "no dispatch"
  }

  // The blob record the landing gate reconciles against (`DaemonMode`'s
  // `integrationSourceBlobs`) is a record of what a session HOLDS, so it must be
  // written by EVERY path that binds real source text into that session — not
  // only by the gate's own `evalIntegrationFiles`. `EvalBufferThenRunAffected` is
  // that other path: the as-you-type pipeline submits real buffer text through
  // the same `WorkerMessage.EvalLiveTestFile` primitive, and it targets whichever
  // session owns the live-testing cycle — including the integration verification
  // session, which the daemon's file watcher feeds (`FileContentChanged` for a
  // path under a watched dir) and the MCP buffer endpoint can name outright.
  //
  // The note is asserted through `SourceBound`'s OBSERVABLE seam — the same
  // single writer the daemon installs over the real record — rather than through
  // `DaemonMode`'s private `ref`, which a unit test cannot see without starting a
  // daemon. That is the honest seam: the effect handler's whole responsibility is
  // to ANNOUNCE, and the announced (session, path, blob) triple is what the gate
  // writes into the record. Before the fix this call did not exist and nothing
  // was announced at all, so the assertion below fails.
  testTask "WHY: the as-you-type eval announces the blob of the text it binds, so the landing gate's record cannot under-count what the session holds" {
    let log = TestDeps.createLog ()
    let mutable submitted : (string * string) list = []
    let deps = TestDeps.singleSession log (fun msg ->
      match msg with
      | WorkerMessage.EvalLiveTestFile (filePath, content, rid) ->
        submitted <- (filePath, content) :: submitted
        WorkerResponse.EvalLiveTestFileResult (rid, Ok ([||], []))
      | _ ->
        WorkerResponse.WorkerError (SageFsError.Unexpected (exn "unexpected worker message")))
    let filePath = "/src/Alice.fs"
    let content = "module Alice\nlet aliceMessage () : string = \"alice:typed\""
    let effect =
      Features.LiveTesting.TestCycleEffect.EvalBufferThenRunAffected {
        FilePath = filePath
        Content = content
        Run = {
          Tests = [||]
          Trigger = Features.LiveTesting.RunTrigger.Keystroke
          TreeSitterElapsed = TimeSpan.Zero
          FcsElapsed = TimeSpan.Zero
          SessionId = Some "a1b2c3d4"
          InstrumentationMaps = [||]
        }
      }
    // The real text is submitted — the record's whole subject is what the session
    // ends up holding, so this is the binding the note has to speak for.
    let announced = ResizeArray ()
    let previous =
      SageFs.SourceBound.setHook (Some (fun sid path blob ->
        announced.Add(sid, path, blob)))
    try
      do! SageFsEffectHandler.execute deps
            (fun _ -> ())
            (SageFsEffect.TestCycle effect)
    finally
      SageFs.SourceBound.setHook previous |> ignore
    submitted
    |> Expect.hasLength "the buffer is bound into the session" 1
    snd submitted.Head
    |> Expect.equal "bound as the exact buffer text submitted" content
    List.ofSeq announced
    |> Expect.equal
      "the note names the session that took it, the path, and the blob of exactly that text"
      [ "a1b2c3d4", filePath, SageFs.SourceBound.gitBlobIdOfText content ]
  }

  // The other half of the contract, and the reason the note is made against the
  // TEXT rather than the file: an unsaved buffer's blob must not be the disk's.
  // If the note were taken from the path instead, a record written for buffer
  // content would name the file's text, and a session holding the buffer would be
  // read as reconciled when the head never had it.
  if not (gitAvailable ()) then
    testCase "git must be available on PATH: the announced blob is of the TEXT, not of the file on disk" <| fun _ ->
      failtest "git is not on PATH, so the announced blob cannot be compared against git's own answer"
  else
    testCase "WHY: the announced blob is git's blob id of the TEXT, so an unsaved buffer is never recorded as the file on disk" <| fun _ ->
      let bufferText = "module Alice\nlet aliceMessage () : string = \"alice:typed-but-unsaved\""
      SageFs.SourceBound.gitBlobIdOfText bufferText
      |> Expect.equal "the blob is git's own blob id of the given text" (gitBlobOf bufferText)
      SageFs.SourceBound.gitBlobIdOfText bufferText
      |> Expect.notEqual
        "and never the disk's, which is the whole reason the text is hashed rather than the file read"
        (gitBlobOf (bufferText.Replace("typed-but-unsaved", "on-disk")))
      // Non-ASCII, where a char count would disagree with git's byte count.
      SageFs.SourceBound.gitBlobIdOfText "let café = 1"
      |> Expect.equal "byte count, not char count, in the git header" (gitBlobOf "let café = 1")
      // Empty text: the header is "blob 0\0", which a length-prefix bug turns into something else.
      SageFs.SourceBound.gitBlobIdOfText ""
      |> Expect.equal "empty text hashes as git hashes it" (gitBlobOf "")
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
    let! doneSignal = TestDeps.awaitTcs (TestTimeouts.asMs TestTimeouts.patience) resultArrived
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
    let! createdSignal = TestDeps.awaitTcs (TestTimeouts.asMs TestTimeouts.patience) created
    createdSignal |> Expect.isTrue "should create the session"
    lastModel.Value.Sessions.Sessions
    |> Expect.hasLength "1 session" 1
    dispatch (SageFsMsg.Editor
      (EditorAction.StopSession "a1b2c3d4"))
    let! stoppedSignal = TestDeps.awaitTcs (TestTimeouts.asMs TestTimeouts.patience) stopped
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
    let! menuSignal = TestDeps.awaitTcs (TestTimeouts.asMs TestTimeouts.patience) menuArrived
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
      Status = SessionLifecycleStatus.Ready { Pid = 42; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      ActiveProject = None
      ProjectRoles = []
      App = SageFs.AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync
    }
    let warmup : WarmupContext = {
      AssembliesLoaded =
        [{ Name = "A"; Path = "A.dll"; NamespaceCount = 3; ModuleCount = 1 }]
      NamespacesOpened =
        [{ Name = "System"; Kind = OpenableKind.Namespace; Source = "warmup"; DurationMs = FixtureDurations.instantOpenMs }]
      FailedOpens = []; PhaseTiming = FixtureDurations.warmupTotalOnly 500L
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
      AwaitReady = fun _ -> async { return Result.Ok () }
      ReadyDeadline = Timeouts.rebuildReadyWait
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
      | SageFsMsg.Event (TuiEvent.WarmupContextUpdated ctx) ->
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
                  Status = SessionLifecycleStatus.Ready { Pid = 1; Port = None }
                  Workflow = WorkflowTypes.SessionWorkflow.Interactive
                  ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning; Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }]
      }
      AwaitReady = fun _ -> async { return Result.Ok () }
      ReadyDeadline = Timeouts.rebuildReadyWait
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
      | SageFsMsg.Event (TuiEvent.WarmupContextUpdated _) -> true
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
                  Status = SessionLifecycleStatus.Starting { Pid = 0; Port = None }
                  Workflow = WorkflowTypes.SessionWorkflow.Interactive
                  ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning; Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }]
      }
      AwaitReady = fun _ -> async { return Result.Ok () }
      ReadyDeadline = Timeouts.rebuildReadyWait
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

/// Drives `RunAffectedTests` against a stub streaming proxy so each way a run
/// can end (clean end with gaps, stall, transport failure, cancellation) is
/// observed through the real effect handler.
module RunEndHarness =
  open SageFs.Features.LiveTesting

  let mkTest (id: string) : TestCase = {
    Id = TestId.TestId id
    FullName = sprintf "Sample.Tests.%s" id
    DisplayName = id
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
  }

  let requested = [| mkTest "t-a"; mkTest "t-b"; mkTest "t-c" |]

  let reported (tc: TestCase) (result: TestResult) : TestRunResult = {
    TestId = tc.Id
    TestName = tc.FullName
    Result = result
    Timestamp = DateTimeOffset.UtcNow
    Output = None
  }

  let passedA = reported requested.[0] (TestResult.Passed TestTimeouts.testElapsed)
  let failedB =
    reported requested.[1] (TestResult.Failed (TestFailure.AssertionFailed "expected 1, got 2", TestTimeouts.testElapsedOther))

  type Completion =
    | ReportedCompletion
    | NoCompletionReported

  type RunEnd = {
    Results: TestRunResult array
    Completion: Completion
  }

  type CompletionExpectation =
    | CompletesItself
    | LeavesCompletionToSupersedingRun

  let run
    (expectation: CompletionExpectation)
    (stub: (TestRunResult -> unit) -> Async<HttpWorkerClient.StreamOutcome>)
    =
    task {
      let sid = testSessionId "a1b2c3d4"
      let deps : EffectDeps = {
        ResolveSession = fun _ ->
          Result.Ok (SessionOperations.SessionResolution.DefaultSingle sid)
        GetProxy = fun _ -> None
        GetStreamingTestProxy = fun _ -> Some (fun _ _ onResult _ _ -> stub onResult)
        CreateSession = fun _ _ _ ->
          async { return Result.Error SageFsError.NoActiveSessions }
        ConfigureWarmupAutoOpen = TestDeps.ensureAutoOpenNoop
        StopSession = fun _ ->
          async { return Result.Error SageFsError.NoActiveSessions }
        RestartSession = fun _ _ ->
          async { return Result.Error SageFsError.NoActiveSessions }
        ListSessions = fun () -> async { return [] }
        AwaitReady = fun _ -> async { return Result.Ok () }
        ReadyDeadline = Timeouts.rebuildReadyWait
        GetWarmupContext = None
        RegisterFileWatcher = fun _ _ -> ()
        DisposeFileWatcher = fun _ _ -> ()
        TestCycleCancellation = TestCycleCancellation.create ()
      }
      let messages = System.Collections.Concurrent.ConcurrentQueue<SageFsMsg>()
      let results () =
        messages
        |> Seq.collect (fun m ->
          match m with
          | SageFsMsg.Event (TuiEvent.TestResultsBatch (_, batch)) -> Seq.ofArray batch
          | _ -> Seq.empty)
        |> Seq.toArray
      let completion () =
        let completed =
          messages
          |> Seq.exists (fun m ->
            match m with
            | SageFsMsg.Event (TuiEvent.TestRunCompleted _) -> true
            | _ -> false)
        match completed with
        | true -> ReportedCompletion
        | false -> NoCompletionReported
      let everyTestHasAResult () =
        let ids = results () |> Array.map (fun r -> r.TestId) |> Set.ofArray
        requested |> Array.forall (fun tc -> ids.Contains tc.Id)
      do! SageFsEffectHandler.execute deps messages.Enqueue
            (SageFsEffect.TestCycle (
              TestCycleEffect.RunAffectedTests
                { TestRunRequest.empty with
                    Tests = requested
                    Trigger = RunTrigger.FileSave
                    SessionId = Some (SessionId.value sid) }))
      let! _settled =
        TestDeps.awaitCondition (TestTimeouts.asMs TestTimeouts.patience) (fun () ->
          match expectation with
          | CompletesItself -> everyTestHasAResult () && completion () = ReportedCompletion
          | LeavesCompletionToSupersedingRun -> everyTestHasAResult ())
      return { Results = results (); Completion = completion () }
    }

  /// The run's end must leave every requested test with exactly one result:
  /// the one it reported, or a never-reported marker that says why.
  let expectEveryTestTerminal
    (received: TestRunResult list)
    (reason: NoResultReason)
    (runEnd: RunEnd)
    =
    runEnd.Results
    |> Array.map (fun r -> r.TestId)
    |> Array.sort
    |> Expect.equal
        "every requested test has exactly one result — none missing, none double-reported"
        (requested |> Array.map (fun t -> t.Id) |> Array.sort)
    for r in received do
      runEnd.Results
      |> Array.find (fun f -> f.TestId = r.TestId)
      |> fun f -> f.Result
      |> Expect.equal (sprintf "%s keeps the outcome it reported" r.TestName) r.Result
    let receivedIds = received |> List.map (fun r -> r.TestId) |> Set.ofList
    for f in runEnd.Results |> Array.filter (fun f -> not (receivedIds.Contains f.TestId)) do
      f.Result
      |> Expect.equal (sprintf "%s is marked never-reported, not failed" f.TestName) (TestResult.NoResult reason)

[<Tests>]
let runEndTests = testList "SageFsEffectHandler — every requested test ends terminal" [
  testTask "a stream that ends cleanly with tests unreported marks them never-reported instead of leaving them running" {
    let! runEnd =
      RunEndHarness.run RunEndHarness.CompletesItself (fun onResult ->
        async {
          onResult RunEndHarness.passedA
          return HttpWorkerClient.StreamOutcome.Completed
        })
    runEnd |> RunEndHarness.expectEveryTestTerminal [ RunEndHarness.passedA ] Features.LiveTesting.NoResultReason.StreamEnded
    runEnd.Completion |> Expect.equal "the run reports completion" RunEndHarness.ReportedCompletion
  }

  testTask "a stalled stream keeps the results that arrived and marks the rest never-reported, not failed" {
    let after = TestTimeouts.streamStalledAfter
    let! runEnd =
      RunEndHarness.run RunEndHarness.CompletesItself (fun onResult ->
        async {
          onResult RunEndHarness.passedA
          onResult RunEndHarness.failedB
          return HttpWorkerClient.StreamOutcome.TimedOut after
        })
    runEnd
    |> RunEndHarness.expectEveryTestTerminal
        [ RunEndHarness.passedA; RunEndHarness.failedB ]
        (Features.LiveTesting.NoResultReason.StreamStalled after)
    runEnd.Completion |> Expect.equal "the run reports completion" RunEndHarness.ReportedCompletion
  }

  testTask "a transport failure keeps streamed results and marks the rest never-reported" {
    let! runEnd =
      RunEndHarness.run RunEndHarness.CompletesItself (fun onResult ->
        async {
          onResult RunEndHarness.failedB
          return failwith "connection reset by worker"
        })
    runEnd
    |> RunEndHarness.expectEveryTestTerminal
        [ RunEndHarness.failedB ]
        (Features.LiveTesting.NoResultReason.TransportFailed "connection reset by worker")
    runEnd.Completion |> Expect.equal "the run reports completion" RunEndHarness.ReportedCompletion
  }

  testTask "a cancelled run marks its unreported tests never-reported and leaves completion to the run that replaced it" {
    let! runEnd =
      RunEndHarness.run RunEndHarness.LeavesCompletionToSupersedingRun (fun onResult ->
        async {
          onResult RunEndHarness.passedA
          return HttpWorkerClient.StreamOutcome.Cancelled
        })
    runEnd
    |> RunEndHarness.expectEveryTestTerminal
        [ RunEndHarness.passedA ]
        Features.LiveTesting.NoResultReason.RunCancelled
    runEnd.Completion
    |> Expect.equal
        "a superseded run must not end the phase of the run that replaced it"
        RunEndHarness.NoCompletionReported
  }

  testTask "a run where every test reported synthesizes nothing" {
    let passedC =
      RunEndHarness.reported RunEndHarness.requested.[2] (Features.LiveTesting.TestResult.Passed TimeSpan.Zero)
    let! (runEnd: RunEndHarness.RunEnd) =
      RunEndHarness.run RunEndHarness.CompletesItself (fun onResult ->
        async {
          onResult RunEndHarness.passedA
          onResult RunEndHarness.failedB
          onResult passedC
          return HttpWorkerClient.StreamOutcome.Completed
        })
    runEnd.Results
    |> Array.map (fun r -> r.TestId)
    |> Array.sort
    |> Expect.equal
        "only the reported results, each once"
        (RunEndHarness.requested |> Array.map (fun t -> t.Id) |> Array.sort)
    runEnd.Results
    |> Array.filter (fun r ->
      match r.Result with
      | Features.LiveTesting.TestResult.NoResult _ -> true
      | _ -> false)
    |> Expect.isEmpty "nothing is marked never-reported"
  }

  testCase "the run summary says how many requested tests never reported, and why" <| fun _ ->
    let missing =
      Features.LiveTesting.TestRunResult.neverReported
        (Features.LiveTesting.NoResultReason.StreamStalled TestTimeouts.streamStalledAfter)
        DateTimeOffset.UtcNow
        RunEndHarness.requested
        (Set.ofList [ RunEndHarness.passedA.TestId ])
    let line =
      PendingRunSummary.empty
      |> PendingRunSummary.addBatch (Array.append [| RunEndHarness.passedA |] missing)
      |> PendingRunSummary.toOutputLine
    line.Text |> Expect.stringContains "counts the unreported tests against the run" "2 of 3 never reported"
    line.Text |> Expect.stringContains "says why" "the worker went silent for 30s"
    line.Kind |> Expect.equal "an incomplete run is not reported as a clean one" OutputKind.Failure
]
