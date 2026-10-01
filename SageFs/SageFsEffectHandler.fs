namespace SageFs

open System
open SageFs.WorkerProtocol
open SageFs.WarmUp
open SageFs.Features.Diagnostics
open SageFs.Features.LiveTesting

// The effect handler: the impure edge of the Elm loop. It interprets each `SageFsEffect` against the
// dependencies it is given (`EffectDeps`), and dispatches what comes back as messages.

/// Dependencies the effect handler needs — injected, not hard-coded.
/// This is the seam between pure Elm and impure infrastructure.
type EffectDeps = {
  /// Resolve which session to target
  ResolveSession: SessionId option -> Result<SessionOperations.SessionResolution, SageFsError>
  /// Get the proxy for a session
  GetProxy: SessionId -> SessionProxy option
  /// Get a streaming test execution proxy for a session.
  /// The proxy streams test results and IL coverage hits. A coverage reading names the test it
  /// was taken for: the worker takes it right after that test ran.
  GetStreamingTestProxy: SessionId -> (Features.LiveTesting.TestCase array -> int -> (Features.LiveTesting.TestRunResult -> unit) -> (Features.LiveTesting.TestId -> bool array -> unit) -> System.Threading.CancellationToken -> Async<HttpWorkerClient.StreamOutcome>) option
  /// Create a new session
  CreateSession: SessionProjectTarget list -> string -> WorkflowTypes.SessionWorkflow -> Async<Result<SessionInfo, SageFsError>>
  /// Ensure the working directory has warmup auto-open disabled.
  ConfigureWarmupAutoOpen: string -> Async<Result<OutputLine, string>>
  /// Stop a session
  StopSession: SessionId -> Async<Result<unit, SageFsError>>
  /// Restart a session: what to cover, and whether to rebuild. See
  /// `SageFs.RestartPlan` — this was a bare bool carrying both decisions.
  RestartSession: SessionId -> RestartPlan -> Async<Result<string, SageFsError>>
  /// List all sessions
  ListSessions: unit -> Async<SessionInfo list>
  /// Answers when the session is Ready, or with the reason it can no longer
  /// become Ready. Parked in the session manager, so it costs nothing while it waits.
  AwaitReady: SessionId -> Async<Result<unit, SageFsError>>
  /// How long a rebuild waits for AwaitReady before it fails the rebuild.
  ReadyDeadline: System.TimeSpan
  /// Fetch warmup context for a session (optional — None disables warmup dispatch)
  GetWarmupContext: (SessionId -> Async<SessionContext option>) option
  /// Test cycle cancellation for stale work
  TestCycleCancellation: Features.LiveTesting.TestCycleCancellation
  /// Register an OS file watcher for the given session's project directory
  RegisterFileWatcher: string -> string -> unit
  /// Dispose the OS file watcher for the given session's project directory
  DisposeFileWatcher: string -> string -> unit
}

/// Routes SageFsEffect to real infrastructure via injected deps.
/// Converts WorkerResponses back into SageFsMsg for the Elm loop.
/// Who reports a live-test run's completion once its stream has ended.
[<RequireQualifiedAccess>]
type RunHandoff =
  /// The run ended on its own — cleanly, with gaps, stalled, or failed — and
  /// reports its own completion.
  | ReportsCompletion
  /// The run was cancelled: the run that replaced it owns the session's run
  /// phase and reports completion, so this one must not end that phase early.
  | LeavesCompletionToSuccessor

module SageFsEffectHandler =

  let newReplyId () =
    Guid.NewGuid().ToString("N").[..7]

  /// Resolve a value that may not be ready yet, retrying with short backoff
  /// (50/100/200/400ms; ~750ms total over 4 retries). A worker's HTTP base-URL
  /// is registered a beat AFTER its session goes Ready, so a just-opened
  /// session's proxy can be None for the first few hundred ms. This is the same
  /// race `RunAffectedTests` already handles inline; `RequestInitialDiscovery`
  /// did not, so it silently dropped such sessions — no discovery, no baseline
  /// run, and the live-testing panel never reached "N✓" (the lt-* demos and the
  /// --integration-lt journey both hung on exactly this).
  let resolveWithBackoff (resolve: unit -> 'a option) : Async<'a option> =
    async {
      let mutable value = resolve ()
      let mutable retries = 0
      let mutable delay = 50
      while value.IsNone && retries < 4 do
        retries <- retries + 1
        do! Async.Sleep delay
        delay <- delay * 2
        value <- resolve ()
      return value
    }

  let evalResponseToMsg
    (sessionId: SessionId)
    (response: WorkerResponse) : SageFsMsg =
    match response with
    | WorkerResponse.EvalResult (_, Ok output, diags, _) ->
      let diagnostics =
        diags |> List.map (fun d -> {
          Message = d.Message
          Subcategory = "typecheck"
          Range = {
            StartLine = d.StartLine
            StartColumn = d.StartColumn
            EndLine = d.EndLine
            EndColumn = d.EndColumn
          }
          Severity = d.Severity
          ErrorNumber = d.ErrorNumber
        })
      SageFsMsg.Event (
        TuiEvent.EvalCompleted (SessionId.value sessionId, output, diagnostics))
    | WorkerResponse.EvalResult (_, Error err, _, _) ->
      SageFsMsg.Event (
        TuiEvent.EvalFailed (SessionId.value sessionId, SageFsError.describe err))
    | WorkerResponse.EvalCancelled _ ->
      SageFsMsg.Event (TuiEvent.EvalCancelled (SessionId.value sessionId))
    | other ->
      SageFsMsg.Event (
        TuiEvent.EvalFailed (
          SessionId.value sessionId, sprintf "Unexpected response: %A" other))

  let completionResponseToMsg
    (response: WorkerResponse) : SageFsMsg =
    match response with
    | WorkerResponse.CompletionResult (_, items) ->
      let completionItems =
        items |> List.map (fun label ->
          { Label = label; Kind = "member"; Detail = None })
      SageFsMsg.Event (TuiEvent.CompletionReady completionItems)
    | _ ->
      SageFsMsg.Event (TuiEvent.CompletionReady [])

  let withSession
    (deps: EffectDeps)
    (dispatch: SageFsMsg -> unit)
    (sessionId: SessionId option)
    (action: SessionId -> SessionProxy -> Async<unit>) =
    async {
      match deps.ResolveSession sessionId with
      | Ok resolution ->
        let id = SessionOperations.sessionId resolution
        match deps.GetProxy id with
        | Some proxy -> do! action id proxy
        | None ->
          dispatch (SageFsMsg.Event (
            TuiEvent.EvalFailed (
              SessionId.value id, sprintf "No proxy for session %s" (SessionId.value id))))
      | Error err ->
        dispatch (SageFsMsg.Event (
          TuiEvent.EvalFailed ("", SageFsError.describe err)))
    }

  let sessionInfoToSnapshot (info: SessionInfo) : SessionSnapshot =
    { Id = info.Id
      Name = info.Name
      Projects = info.Projects
      Status =
        match info.Status with
        | SessionLifecycleStatus.Ready _ -> SessionDisplayStatus.Running
        | SessionLifecycleStatus.Starting _ -> SessionDisplayStatus.Starting
        | SessionLifecycleStatus.Evaluating _ -> SessionDisplayStatus.Running
        | SessionLifecycleStatus.Building _ -> SessionDisplayStatus.Running
        | SessionLifecycleStatus.Faulted reason -> SessionDisplayStatus.Faulted (FaultReason.describe reason)
        | SessionLifecycleStatus.HostCrashed(_, crash) -> SessionDisplayStatus.Faulted (HostCrash.describe crash)
        | SessionLifecycleStatus.Restarting _ -> SessionDisplayStatus.Restarting
        | SessionLifecycleStatus.Stopped -> SessionDisplayStatus.Stopped
      LastActivity = info.LastActivity
      EvalCount = 0
      UpSince = info.CreatedAt
      WorkingDirectory = info.WorkingDirectory }

  /// The main effect handler — plug into ElmProgram.ExecuteEffect
  let execute
    (deps: EffectDeps)
    (dispatch: SageFsMsg -> unit)
    (effect: SageFsEffect) : Async<unit> =
    match effect with
    | SageFsEffect.Editor editorEffect ->
      match editorEffect with
      | EditorEffect.RequestEval code ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! response =
              proxy (WorkerMessage.EvalCode (code, replyId))
            dispatch (evalResponseToMsg sid response)
          })

      | EditorEffect.RequestCompletion (text, cursor) ->
        withSession deps dispatch None (fun _ proxy ->
          async {
            let replyId = newReplyId ()
            let! response =
              proxy (
                WorkerMessage.GetCompletions (text, cursor, replyId))
            dispatch (completionResponseToMsg response)
          })

      | EditorEffect.RequestHistory _ ->
        async { () }

      | EditorEffect.RequestSessionList ->
        async {
          let! sessions = deps.ListSessions ()
          let snaps = sessions |> List.map sessionInfoToSnapshot
          dispatch (SageFsMsg.Event (TuiEvent.SessionsRefreshed snaps))
          // Fetch warmup context for the active Ready session
          match deps.GetWarmupContext with
          | Some getCtx ->
            let readySession =
              sessions |> List.tryFind (fun s -> match s.Status with SessionLifecycleStatus.Ready _ -> true | _ -> false)
            match readySession with
            | Some info ->
              let! ctx = getCtx info.Id
              match ctx with
              | Some sessionCtx ->
                dispatch (SageFsMsg.Event (TuiEvent.WarmupContextUpdated sessionCtx))
              | None -> ()
            | None -> ()
          | None -> ()
        }

      | EditorEffect.RequestSessionSwitch sessionId ->
        async {
          dispatch (SageFsMsg.Event (
            TuiEvent.SessionSwitched (None, sessionId)))
        }

      | EditorEffect.RequestSessionCreate projects ->
        async {
          let workingDir =
            match projects with
            | [dir] when System.IO.Directory.Exists(dir) -> dir
            | _ -> "."
          let targetResult =
            match projects with
            | [dir] when System.IO.Directory.Exists(dir) -> Ok [ SessionProjectTarget.Bare ]
            | other -> SessionProjectTarget.tryCreateMany other
          match targetResult with
          | Error reason ->
            dispatch (SageFsMsg.Event (
              TuiEvent.EvalFailed ("", sprintf "Create failed: %s" reason)))
          | Ok targets ->
            let! result = deps.CreateSession targets workingDir WorkflowTypes.SessionWorkflow.Interactive
            match result with
            | Ok info ->
              dispatch (SageFsMsg.Event (
                TuiEvent.SessionCreated (sessionInfoToSnapshot info)))
              dispatch (SageFsMsg.Event (
                TuiEvent.SessionSwitched (None, SessionId.value info.Id)))
            | Error err ->
              dispatch (SageFsMsg.Event (
                TuiEvent.EvalFailed (
                  "", sprintf "Create failed: %s" (SageFsError.describe err))))
        }

      | EditorEffect.RequestConfigureWarmupAutoOpen workingDir ->
        async {
          let! result = deps.ConfigureWarmupAutoOpen workingDir
          match result with
          | Ok line ->
            dispatch (SageFsMsg.Event (TuiEvent.OutputEmitted line))
          | Error err ->
            dispatch (SageFsMsg.Event (TuiEvent.EvalFailed ("", err)))
        }

      | EditorEffect.RequestSessionStop sessionIdStr ->
        async {
          match SessionId.validate sessionIdStr with
          | Error _ -> ()
          | Ok sessionId ->
            let! result = deps.StopSession sessionId
            match result with
            | Ok () ->
              dispatch (SageFsMsg.Event (
                TuiEvent.SessionStopped sessionIdStr))
            | Error err ->
              dispatch (SageFsMsg.Event (
                TuiEvent.EvalFailed (
                  sessionIdStr,
                  sprintf "Stop failed: %s" (SageFsError.describe err))))
        }

      | EditorEffect.RequestReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! _ = proxy (WorkerMessage.ResetSession replyId)
            dispatch (SageFsMsg.Event (
              TuiEvent.SessionStatusChanged (SessionId.value sid, SessionDisplayStatus.Starting)))
          })

      | EditorEffect.RequestHardReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! _ = proxy (WorkerMessage.HardResetSession (false, replyId))
            dispatch (SageFsMsg.Event (
              TuiEvent.SessionStatusChanged (SessionId.value sid, SessionDisplayStatus.Restarting)))
          })

      | EditorEffect.RequestSmartReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let soft () = task {
              let replyId = newReplyId ()
              let! resp = proxy (WorkerMessage.ResetSession replyId) |> Async.StartAsTask
              match resp with
              | WorkerResponse.ResetResult (_, Ok ()) -> return Ok ()
              | WorkerResponse.ResetResult (_, Error e) -> return Error (SageFsError.describe e)
              | _ -> return Error "unexpected response"
            }
            let hard () = task {
              let replyId = newReplyId ()
              let! resp = proxy (WorkerMessage.HardResetSession (false, replyId)) |> Async.StartAsTask
              match resp with
              | WorkerResponse.HardResetResult (_, Ok msg) -> return Ok msg
              | WorkerResponse.HardResetResult (_, Error e) -> return Error (SageFsError.describe e)
              | _ -> return Error "unexpected response"
            }
            let! outcome = SmartReset.execute soft hard |> Async.AwaitTask
            let status =
              match outcome with
              | SmartReset.Outcome.SoftResetSucceeded ->
                SessionDisplayStatus.Starting
              | SmartReset.Outcome.EscalatedToHardReset _ ->
                SessionDisplayStatus.Restarting
              | SmartReset.Outcome.AllResetsFailed _ ->
                SessionDisplayStatus.Faulted "all resets failed"
            dispatch (SageFsMsg.Event (
              TuiEvent.SessionStatusChanged (SessionId.value sid, status)))
          })

    | SageFsEffect.TestCycle testCycleEffect ->
      async {
        match testCycleEffect with
        | Features.LiveTesting.TestCycleEffect.RequestInitialDiscovery ->
          let! sessions = deps.ListSessions ()
          // Resolve each session's proxy with backoff: a session opened moments
          // before live testing is enabled has no registered worker URL yet, and
          // a one-shot lookup here used to drop it — killing discovery and the
          // baseline run (see resolveWithBackoff). Retry so discovery still fires.
          let! discoveryTargets =
            sessions
            |> List.map (fun session ->
              async {
                match! resolveWithBackoff (fun () -> deps.GetProxy session.Id) with
                | Some proxy -> return Some (session.Id, proxy)
                | None ->
                  Utils.Log.warn "[SageFsApp] Initial test discovery: no worker proxy for %s after retries" (SessionId.value session.Id)
                  return None
              })
            |> Async.Sequential
          let discoveryTargets = discoveryTargets |> Array.choose id
          for sid, proxy in discoveryTargets do
            let replyId = newReplyId ()
            let! report =
              async {
                try
                  let! resp = proxy (WorkerMessage.GetTestDiscovery replyId)
                  return SessionManager.TestDiscoveryReport.ofResponse resp
                with ex ->
                  return SessionManager.TestDiscoveryReport.DiscoveryFailed ex.Message
              }
            match report with
            | SessionManager.TestDiscoveryReport.Discovered (tests, providers) ->
              match List.isEmpty providers with
              | true -> ()
              | false -> dispatch (SageFsMsg.Event (TuiEvent.ProvidersDetected providers))
              dispatch (SageFsMsg.Event (TuiEvent.TestsDiscovered (SessionId.value sid, tests)))
            | SessionManager.TestDiscoveryReport.DiscoveryFailed reason ->
              Utils.Log.warn "[SageFsApp] Initial test discovery failed for %s: %s" (SessionId.value sid) reason
              dispatch (SageFsMsg.Event (TuiEvent.TestDiscoveryFailed (SessionId.value sid, reason)))
        | Features.LiveTesting.TestCycleEffect.ParseTreeSitter (content, filePath) ->
          let span = Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle.treesitter.parse" ["file", box filePath]
          let (locations, elapsed) =
            Features.LiveTesting.LiveTestingInstrumentation.traced
              "SageFs.LiveTesting.TreeSitterParse"
              ["file", box filePath]
              (fun () ->
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let locs = Features.LiveTesting.TestTreeSitter.discover filePath content
                sw.Stop()
                (locs, sw.Elapsed))
          Instrumentation.treeSitterParseMs.Record(elapsed.TotalMilliseconds)
          Features.LiveTesting.LiveTestingInstrumentation.treeSitterHistogram.Record(elapsed.TotalMilliseconds)
          Instrumentation.succeedSpan span
          dispatch (SageFsMsg.Event (TuiEvent.TestLocationsDetected ("", locations)))
          let timing : Features.LiveTesting.TestCycleTiming = {
            Depth = Features.LiveTesting.TestCycleDepth.TreeSitterOnly elapsed
            TotalTests = 0; AffectedTests = 0
            Trigger = Features.LiveTesting.RunTrigger.Keystroke
            Timestamp = System.DateTimeOffset.UtcNow
          }
          dispatch (SageFsMsg.Event (TuiEvent.TestCycleTimingRecorded timing))
        | Features.LiveTesting.TestCycleEffect.RequestFcsTypeCheck req ->
          let span = Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle.fcs.typecheck" ["file", box req.FilePath]
          let fcsStopwatch = System.Diagnostics.Stopwatch.StartNew()
          let targetSid =
            req.SessionId
            |> Option.bind (fun s ->
              match SessionId.validate s with Ok sid -> Some sid | Error _ -> None)
          do! withSession deps dispatch targetSid (fun _sid proxy ->
            async {
              let code =
                match req.Content with
                | Some buffered -> buffered
                | None ->
                    try System.IO.File.ReadAllText(req.FilePath)
                    with ex ->
                      Utils.Log.warn "[SageFsApp] File read failed: %s" ex.Message
                      ""
              match code <> "" with
              | true ->
                let effectiveAnalysisIdentity =
                  req.AnalysisIdentity
                  |> Option.defaultWith (fun () ->
                    Features.LiveTesting.AnalysisIdentity.ofContent code)
                let replyId = newReplyId ()
                // The call runs inside the async so even a synchronous transport
                // throw is caught here.
                let! outcome =
                  async { return! proxy (WorkerMessage.TypeCheckWithSymbols(code, req.FilePath, replyId)) }
                  |> Async.Catch
                fcsStopwatch.Stop()
                Instrumentation.fcsTypecheckMs.Record(fcsStopwatch.Elapsed.TotalMilliseconds)
                Features.LiveTesting.LiveTestingInstrumentation.fcsHistogram.Record(fcsStopwatch.Elapsed.TotalMilliseconds)
                let result =
                  match outcome with
                  | Choice2Of2 ex ->
                    // A worker mid-restart cannot answer: that is a cancelled
                    // check, not a daemon fault for the Elm loop to alarm on.
                    Utils.Log.warn "[SageFsApp] Type-check could not reach the worker for %s: %s" req.FilePath ex.Message
                    Features.LiveTesting.FcsTypeCheckResult.Cancelled req.FilePath
                  | Choice1Of2 resp ->
                  match resp with
                  | WorkerResponse.TypeCheckWithSymbolsResult(_rid, diags, symRefs) ->
                    let hasErrors =
                      diags |> List.exists (fun d -> d.Severity = DiagnosticSeverity.Blocking)
                    match hasErrors with
                    | true ->
                      let errors =
                        diags
                        |> List.filter (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Blocking)
                        |> List.map (fun d -> d.Message)
                      Features.LiveTesting.FcsTypeCheckResult.Failed(req.FilePath, errors)
                    | false ->
                      let refs = symRefs |> List.map WorkerProtocol.WorkerSymbolRef.toDomain
                      Features.LiveTesting.FcsTypeCheckResult.Success(req.FilePath, refs)
                  | _ ->
                    Features.LiveTesting.FcsTypeCheckResult.Cancelled req.FilePath
                dispatch (SageFsMsg.FcsTypeCheckCompleted (req.SessionId, Some effectiveAnalysisIdentity, result))
                let timing : Features.LiveTesting.TestCycleTiming = {
                  Depth = Features.LiveTesting.TestCycleDepth.ThroughFcs(req.TreeSitterElapsed, fcsStopwatch.Elapsed)
                  TotalTests = 0; AffectedTests = 0
                  Trigger = Features.LiveTesting.RunTrigger.Keystroke
                  Timestamp = System.DateTimeOffset.UtcNow
                }
                dispatch (SageFsMsg.Event (TuiEvent.TestCycleTimingRecorded timing))
                Instrumentation.succeedSpan span
              | false -> ()
            })
        | Features.LiveTesting.TestCycleEffect.EvalBufferThenRunAffected req ->
          // Brief 4 keystone: identity-preserving eval of the edited buffer,
          // then run exactly the tests already selected as affected — never
          // the compiled DLL, never the whole suite (see
          // live-testing-asyoutype-plan.md §2, Invariants 1/2/6).
          let targetSid =
            req.Run.SessionId
            |> Option.bind (fun s -> match SessionId.validate s with Ok sid -> Some sid | Error _ -> None)
          do! withSession deps dispatch targetSid (fun sid proxy ->
            async {
              let replyId = newReplyId ()
              let! outcome =
                async { return! proxy (WorkerMessage.EvalLiveTestFile(req.FilePath, req.Content, replyId)) }
                |> Async.Catch
              match outcome with
              | Choice2Of2 ex ->
                // Worker mid-restart etc.: surface it, but LiveTesting state
                // (discovery + results) is untouched — fail-closed.
                Utils.Log.warn "[SageFsApp] EvalLiveTestFile could not reach the worker for %s: %s" req.FilePath ex.Message
                dispatch (SageFsMsg.Event (
                  TuiEvent.EvalFailed (
                    SessionId.value sid,
                    sprintf "Live-test eval could not reach the worker: %s" ex.Message)))
              | Choice1Of2 (WorkerResponse.EvalLiveTestFileResult (_, Error err)) ->
                // Buffer doesn't compile / eval failed. Fail-closed (Invariant
                // 4): do NOT dispatch a discovery merge or a run — the prior
                // dynamic discovery + last-good results stay exactly as they
                // were. Only the error is surfaced. (Brief 5 owns a dedicated
                // stale/rollback DU; this is the honest interim signal.)
                Utils.Log.warn "[SageFsApp] EvalLiveTestFile failed for %s: %s" req.FilePath (SageFsError.describe err)
                dispatch (SageFsMsg.Event (
                  TuiEvent.EvalFailed (SessionId.value sid, SageFsError.describe err)))
              | Choice1Of2 (WorkerResponse.EvalLiveTestFileResult (_, Ok (tests, providers))) ->
                match List.isEmpty providers with
                | true -> ()
                | false -> dispatch (SageFsMsg.Event (TuiEvent.ProvidersDetected providers))
                // Merge the live (compiled ∪ dynamic) discovery WITHOUT
                // auto-running every discovered test (see
                // TuiEvent.LiveDiscoveryMerged), then run exactly the
                // coverage/graph-selected tests `req.Run` already carries —
                // re-resolved against the fresh merge so a re-run reflects
                // the just-eval'd metadata rather than the pre-eval snapshot.
                dispatch (SageFsMsg.Event (TuiEvent.LiveDiscoveryMerged (SessionId.value sid, tests)))
                let runIds = req.Run.Tests |> Array.map (fun tc -> tc.Id) |> Set.ofArray
                let freshTests = tests |> Array.filter (fun tc -> Set.contains tc.Id runIds)
                let toRun = match Array.isEmpty freshTests with true -> req.Run.Tests | false -> freshTests
                match Array.isEmpty toRun with
                | true -> ()
                | false ->
                  dispatch (SageFsMsg.Event (
                    TuiEvent.RunTestsRequested (Some (SessionId.value sid), toRun, None)))
              | Choice1Of2 _ -> ()
            })
        | Features.LiveTesting.TestCycleEffect.CancelRebuild (targetSession, generation) ->
          match deps.TestCycleCancellation.Rebuild.cancel(targetSession, generation) with
          | true ->
              Utils.Log.info "[rebuild] CancelRebuild cancelled generation %d for %A" generation targetSession
          | false ->
              Utils.Log.info "[rebuild] CancelRebuild ignored for stale generation %d for %A" generation targetSession
        | Features.LiveTesting.TestCycleEffect.RequestRebuild (generation, req) ->
          let targetSession = req.SessionId
          let ct = deps.TestCycleCancellation.Rebuild.start(targetSession, generation)
          Async.Start((async {
            let rebuildStopwatch = System.Diagnostics.Stopwatch.StartNew()
            try
              try
                ct.ThrowIfCancellationRequested()
                let targetSid =
                  targetSession
                  |> Option.bind (fun s ->
                    match SessionId.validate s with Ok sid -> Some sid | Error _ -> None)
                match deps.ResolveSession targetSid with
                | Error err ->
                  match ct.IsCancellationRequested with
                  | true ->
                    rebuildStopwatch.Stop()
                    Utils.Log.info "[rebuild] RequestRebuild cancelled before resolve completed for %A" targetSession
                  | false ->
                    rebuildStopwatch.Stop()
                    dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Error (SageFsError.describe err)))
                | Ok resolution ->
                  ct.ThrowIfCancellationRequested()
                  let sid = SessionOperations.sessionId resolution
                  let sidStr = SessionId.value sid
                  let restartStopwatch = System.Diagnostics.Stopwatch.StartNew()
                  match! deps.RestartSession sid (SageFs.RestartPlan.Rebuild SageFs.GranularRestart.RestartSubject.Worker) with
                  | Error err ->
                    match ct.IsCancellationRequested with
                    | true ->
                      restartStopwatch.Stop()
                      rebuildStopwatch.Stop()
                      Utils.Log.info "[rebuild] RequestRebuild cancelled after restart attempt for %s" sidStr
                    | false ->
                      restartStopwatch.Stop()
                      rebuildStopwatch.Stop()
                      Instrumentation.liveTestingRebuildRestartMs.Record(restartStopwatch.Elapsed.TotalMilliseconds)
                      Instrumentation.liveTestingRebuildPipelineMs.Record(rebuildStopwatch.Elapsed.TotalMilliseconds)
                      let msg = SageFsError.describe err
                      Utils.Log.warn "[rebuild] RestartSession(rebuild=true) failed for %s: %s" sidStr msg
                      dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Error msg))
                  | Ok msg ->
                    ct.ThrowIfCancellationRequested()
                    restartStopwatch.Stop()
                    Instrumentation.liveTestingRebuildRestartMs.Record(restartStopwatch.Elapsed.TotalMilliseconds)
                    Utils.Log.info
                      "[rebuild] RestartSession(rebuild=true) started for %s in %.1fms: %s"
                      sidStr
                      restartStopwatch.Elapsed.TotalMilliseconds
                      msg
                    let waitTimeoutMs = int deps.ReadyDeadline.TotalMilliseconds
                    let waitStopwatch = System.Diagnostics.Stopwatch.StartNew()
                    // One await on the session manager, bounded by the deadline and
                    // abandoned on cancel. A session is Ready only after its worker URL
                    // is installed, so Ready is also when the proxy exists (RebuildReadyWait).
                    let! outcome = RebuildReadyWait.await (deps.AwaitReady sid) deps.ReadyDeadline ct
                    ct.ThrowIfCancellationRequested()
                    waitStopwatch.Stop()
                    rebuildStopwatch.Stop()
                    Instrumentation.liveTestingRebuildPipelineMs.Record(rebuildStopwatch.Elapsed.TotalMilliseconds)
                    let failRebuild (err: string) =
                      Utils.Log.warn "[rebuild] %s" err
                      dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Error err))
                    match outcome with
                    | RebuildReadyWait.Outcome.Ready ->
                      let readyMs = waitStopwatch.Elapsed.TotalMilliseconds
                      Instrumentation.liveTestingRebuildReadyWaitMs.Record(readyMs)
                      Utils.Log.info "[rebuild] Session %s reached Ready %.1fms after rebuild restart" sidStr readyMs
                      match deps.GetStreamingTestProxy sid with
                      | Some _ ->
                        Instrumentation.liveTestingRebuildProxyWaitMs.Record(readyMs)
                        Utils.Log.info "[rebuild] Session %s streaming proxy available %.1fms after rebuild restart" sidStr readyMs
                        Utils.Log.info
                          "[rebuild] Session %s ready with streaming proxy after rebuild (restart=%.1fms ready=%.1fms proxy=%.1fms total=%.1fms)"
                          sidStr
                          restartStopwatch.Elapsed.TotalMilliseconds
                          readyMs
                          readyMs
                          rebuildStopwatch.Elapsed.TotalMilliseconds
                        dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Ok ()))
                      | None ->
                        failRebuild
                          (sprintf
                            "Rebuild succeeded and session %s is Ready, but no streaming proxy is registered for it (restart=%.1fms, ready=%.1fms, proxy=missing, total=%.1fms)."
                            sidStr
                            restartStopwatch.Elapsed.TotalMilliseconds
                            readyMs
                            rebuildStopwatch.Elapsed.TotalMilliseconds)
                    | RebuildReadyWait.Outcome.Failed err ->
                      failRebuild
                        (sprintf
                          "Rebuild succeeded but session %s did not become ready for test execution: %s (restart=%.1fms, total=%.1fms)."
                          sidStr
                          (SageFsError.describe err)
                          restartStopwatch.Elapsed.TotalMilliseconds
                          rebuildStopwatch.Elapsed.TotalMilliseconds)
                    | RebuildReadyWait.Outcome.DeadlineReached ->
                      // One look, for the message only. It decides nothing.
                      let! sessions = deps.ListSessions()
                      ct.ThrowIfCancellationRequested()
                      let statusText =
                        sessions
                        |> List.tryFind (fun si -> si.Id = sid)
                        |> Option.map (fun si -> string si.Status)
                        |> Option.defaultValue "missing"
                      let proxyText =
                        match deps.GetStreamingTestProxy sid with
                        | Some _ -> "registered"
                        | None -> "not-observed"
                      failRebuild
                        (sprintf
                          "Rebuild succeeded but session %s never became ready for test execution within %dms (status=%s, restart=%.1fms, ready=not-observed, proxy=%s, total=%.1fms)."
                          sidStr
                          waitTimeoutMs
                          statusText
                          restartStopwatch.Elapsed.TotalMilliseconds
                          proxyText
                          rebuildStopwatch.Elapsed.TotalMilliseconds)
                    | RebuildReadyWait.Outcome.Cancelled ->
                      // Only a cancelled token produces this, and it threw above.
                      Utils.Log.info "[rebuild] RequestRebuild cancelled for %A" targetSession
              with
              | :? OperationCanceledException ->
                rebuildStopwatch.Stop()
                Utils.Log.info "[rebuild] RequestRebuild cancelled for %A" targetSession
              | ex ->
                rebuildStopwatch.Stop()
                Instrumentation.liveTestingRebuildPipelineMs.Record(rebuildStopwatch.Elapsed.TotalMilliseconds)
                Utils.Log.error "[rebuild] Exception: %s" ex.Message
                dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Error ex.Message))
            finally
              deps.TestCycleCancellation.Rebuild.complete(targetSession, generation)
            }), ct)
        | Features.LiveTesting.TestCycleEffect.RegisterFileWatcher (_sessionId, directory) ->
          deps.RegisterFileWatcher _sessionId directory
        | Features.LiveTesting.TestCycleEffect.DisposeFileWatcher (_sessionId, directory) ->
          deps.DisposeFileWatcher _sessionId directory
        | Features.LiveTesting.TestCycleEffect.RunAffectedTests req
        | Features.LiveTesting.TestCycleEffect.RunRequestedTests (req, _) ->
          let tests = req.Tests
          let trigger = req.Trigger
          let tsElapsed = req.TreeSitterElapsed
          let fcsElapsed = req.FcsElapsed
          let targetSession = req.SessionId
          let instrumentationMaps = req.InstrumentationMaps
          match Array.isEmpty tests with
          | true -> ()
          | false ->
            let testIds = tests |> Array.map (fun tc -> tc.Id)
            // A requested run starts WITH the generation allocated when it was
            // requested; anything else bumps a new one as it starts.
            let startMessage =
              match testCycleEffect, targetSession with
              | Features.LiveTesting.TestCycleEffect.RunRequestedTests (_, generation), Some sid ->
                TuiEvent.TestRunStartedAt (testIds, sid, generation)
              | _ -> TuiEvent.TestRunStarted (testIds, targetSession)
            dispatch (SageFsMsg.Event startMessage)
            let ct = deps.TestCycleCancellation.TestRun.next()
            let hasInstrMaps = not (Array.isEmpty instrumentationMaps)
            let testCycleSpan = Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle.test.execution" ["test.count", box tests.Length; "trigger", box (sprintf "%A" trigger); "coverage.has_maps", box hasInstrMaps; "coverage.probe_count", box (instrumentationMaps |> Array.sumBy (fun m -> m.TotalProbes))]
            Async.Start(async {
              Instrumentation.testExecutionActiveCount.Add(1L)
              use activity =
                Features.LiveTesting.LiveTestingInstrumentation.activitySource.StartActivity(
                  "SageFs.LiveTesting.TestExecution")
              let sw = System.Diagnostics.Stopwatch.StartNew()
              let receivedIds = System.Collections.Generic.HashSet<Features.LiveTesting.TestId>()
              let mutable handoff = RunHandoff.ReportsCompletion
              try
                let targetSid = targetSession |> Option.bind (fun s -> match SessionId.validate s with Ok sid -> Some sid | Error _ -> None)
                match deps.ResolveSession targetSid with
                | Ok resolution ->
                  let sid = SessionOperations.sessionId resolution
                  // Bounded by a DEADLINE, not an attempt count: four fixed
                  // attempts (750ms) stranded every run under load. See
                  // WorkerProxyWait.fs.
                  let mutable proxy = deps.GetStreamingTestProxy sid
                  let mutable delays = WorkerProxyWait.delaysByDefault
                  while proxy.IsNone && not (List.isEmpty delays) do
                    let delay = List.head delays
                    delays <- List.tail delays
                    do! Async.Sleep delay
                    proxy <- deps.GetStreamingTestProxy sid
                  match proxy with
                  | Some streamProxy ->
                    use resultFlusher =
                      new BatchFlusher<Features.LiveTesting.TestRunResult>(25, 200, fun batch ->
                        Instrumentation.testResultBatchSize.Record(int64 batch.Length)
                        dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, batch)))
                      )
                    let onResult (result: Features.LiveTesting.TestRunResult) =
                      receivedIds.Add(result.TestId) |> ignore
                      resultFlusher.Add(result)
                    let onCoverage (_reportedFor: Features.LiveTesting.TestId) (hits: bool array) =
                      let mergedMap = Features.LiveTesting.InstrumentationMap.merge instrumentationMaps
                      match mergedMap.TotalProbes > 0 && hits.Length = mergedMap.TotalProbes with
                      | true ->
                        let coverage = Features.LiveTesting.InstrumentationMap.toCoverageState hits mergedMap
                        dispatch (SageFsMsg.Event (TuiEvent.CoverageUpdated coverage))
                        let bitmap = Features.LiveTesting.CoverageBitmap.ofBoolArray hits
                        dispatch (SageFsMsg.Event (TuiEvent.CoverageBitmapCollected (targetSession, testIds, bitmap)))
                        match activity <> null with
                        | true ->
                          activity.SetTag("coverage.total_probes", hits.Length) |> ignore
                          activity.SetTag("coverage.hit_probes", Features.LiveTesting.CoverageBitmap.popCount bitmap) |> ignore
                          activity.SetTag("coverage.tests_in_batch", testIds.Length) |> ignore
                        | false -> ()
                      | false -> ()
                    let parallelism = max 4 (Environment.ProcessorCount / 2)
                    let! outcome = streamProxy tests parallelism onResult onCoverage ct // ct explicit, not ambient — see HttpWorkerClient.fs's safeAwait
                    // Whichever way the stream ended — a clean end with gaps, a
                    // stall, a cancellation — every requested test that never
                    // reported gets a truthful NoResult saying why, so none is
                    // left spinning and none gets a fabricated failure. Tests that
                    // did report are excluded: their outcome is never replaced.
                    // BatchFlusher's Dispose (via 'use') flushes the reported ones.
                    let reason = HttpWorkerClient.noResultReason outcome
                    let missing =
                      Features.LiveTesting.TestRunResult.neverReported
                        reason System.DateTimeOffset.UtcNow tests (receivedIds |> Set.ofSeq)
                    match missing.Length with
                    | 0 -> ()
                    | _ ->
                      dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, missing)))
                      Utils.Log.warn
                        "[LiveTesting] %d of %d tests never reported: %s"
                        missing.Length tests.Length (Features.LiveTesting.NoResultReason.describe reason)
                    match outcome with
                    | HttpWorkerClient.StreamOutcome.Cancelled ->
                      handoff <- RunHandoff.LeavesCompletionToSuccessor
                    | HttpWorkerClient.StreamOutcome.Completed
                    | HttpWorkerClient.StreamOutcome.TimedOut _ -> ()
                  | None ->
                    let notRunResults =
                      tests |> Array.map (fun tc ->
                        { TestId = tc.Id
                          TestName = tc.FullName
                          Result = Features.LiveTesting.TestResult.NotRun
                          Timestamp = System.DateTimeOffset.UtcNow
                          Output = None }
                        : Features.LiveTesting.TestRunResult)
                    dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, notRunResults)))
                | Error _ ->
                  let notRunResults =
                    tests |> Array.map (fun tc ->
                      { TestId = tc.Id
                        TestName = tc.FullName
                        Result = Features.LiveTesting.TestResult.NotRun
                        Timestamp = System.DateTimeOffset.UtcNow
                        Output = None }
                      : Features.LiveTesting.TestRunResult)
                  dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, notRunResults)))
                match handoff with
                | RunHandoff.LeavesCompletionToSuccessor ->
                  // Superseded: the replacing run owns this session's run phase,
                  // so reporting completion here would end its phase early.
                  sw.Stop()
                  Instrumentation.succeedSpan testCycleSpan
                  Instrumentation.testExecutionActiveCount.Add(-1L)
                | RunHandoff.ReportsCompletion ->
                  sw.Stop()
                  Instrumentation.testExecutionMs.Record(sw.Elapsed.TotalMilliseconds)
                  let endToEndMs = tsElapsed.TotalMilliseconds + fcsElapsed.TotalMilliseconds + sw.Elapsed.TotalMilliseconds
                  Instrumentation.testCycleEndToEnd.Record(endToEndMs)
                  Features.LiveTesting.LiveTestingInstrumentation.executionHistogram.Record(sw.Elapsed.TotalMilliseconds)
                  match activity <> null with
                  | true ->
                    activity.SetTag("test_count", tests.Length) |> ignore
                    activity.SetTag("trigger", sprintf "%A" trigger) |> ignore
                    activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
                  | false -> ()
                  dispatch (SageFsMsg.Event (TuiEvent.TestRunCompleted targetSession))
                  let timing : Features.LiveTesting.TestCycleTiming = {
                    Depth = Features.LiveTesting.TestCycleDepth.ThroughExecution(
                              tsElapsed, fcsElapsed, sw.Elapsed)
                    TotalTests = tests.Length
                    AffectedTests = tests.Length
                    Trigger = trigger
                    Timestamp = System.DateTimeOffset.UtcNow
                  }
                  dispatch (SageFsMsg.Event (TuiEvent.TestCycleTimingRecorded timing))
                  Instrumentation.succeedSpan testCycleSpan
                  Instrumentation.testExecutionActiveCount.Add(-1L)
              with ex ->
                sw.Stop()
                Instrumentation.failSpan testCycleSpan ex.Message
                // Transport failure: mark ONLY the tests that never reported, and
                // as never-reported — the connection failed, not the tests.
                // Tests that already streamed a result keep it (no double report).
                let errResults =
                  Features.LiveTesting.TestRunResult.neverReported
                    (Features.LiveTesting.NoResultReason.TransportFailed ex.Message)
                    System.DateTimeOffset.UtcNow
                    tests
                    (receivedIds |> Set.ofSeq)
                match errResults.Length with
                | 0 ->
                  Utils.Log.warn
                    "[LiveTesting] Transport failure after all tests reported: %s" ex.Message
                | _ ->
                  dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, errResults)))
                  Utils.Log.warn
                    "[LiveTesting] Transport failure — %d of %d tests never reported: %s"
                    errResults.Length tests.Length ex.Message
                dispatch (SageFsMsg.Event (TuiEvent.TestRunCompleted targetSession))
                Instrumentation.testExecutionActiveCount.Add(-1L)
            }, ct)
      }

    | SageFsEffect.SwitchWorkflow _targetWorkflow ->
      // Phase 4 will implement the actual switch logic (create new session, migrate)
      // For now, this is a placeholder that satisfies exhaustive pattern matching
      async { () }

    | SageFsEffect.Confirm (_sessionId, _effect) ->
      async { () }
