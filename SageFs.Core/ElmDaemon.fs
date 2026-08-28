module SageFs.ElmDaemon

open System.Threading

/// Create EffectDeps from a SessionManager MailboxProcessor.
/// This bridges the Elm domain to real infrastructure.
/// readSnapshot provides lock-free CQRS reads for session lists (non-blocking).
let createEffectDeps
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (autoOpenNamespacesForDirectory: string -> bool)
  (configureWarmupAutoOpen: string -> Result<OutputLine, string>)
  : EffectDeps =
  {
    ResolveSession = fun sessionIdOpt ->
      // Non-blocking: read from CQRS snapshot instead of mailbox
      let sessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
      SessionOperations.resolveSession sessionIdOpt sessions
    GetProxy = fun sessionId ->
      // CQRS read path — lock-free snapshot, no mailbox blocking
      let snap = readSnapshot()
      let urls = snap.WorkerBaseUrls |> Map.toSeq |> Seq.map (fun (k, v) -> WorkerProtocol.SessionId.value k, v) |> Map.ofSeq
      HttpWorkerClient.proxyFromUrls (WorkerProtocol.SessionId.value sessionId) urls
    CreateSession = fun projects workingDir workflow ->
      async {
        let autoOpenNamespaces = autoOpenNamespacesForDirectory workingDir
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.CreateSession(
              projects, workingDir, autoOpenNamespaces, workflow, reply))
        return result
      }
    ConfigureWarmupAutoOpen = fun workingDir ->
      async { return configureWarmupAutoOpen workingDir }
    StopSession = fun sessionId ->
      async {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.StopSession(
              sessionId, reply))
        return result
      }
    RestartSession = fun sessionId rebuild ->
      async {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.RestartSession(
              sessionId, rebuild, reply))
        return result
      }
    ListSessions = fun () ->
      // CQRS read path — lock-free snapshot, no mailbox blocking
      async { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    SleepMs = Async.Sleep
    GetStreamingTestProxy = fun _sessionId -> None
    GetWarmupContext = None
    TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    RegisterFileWatcher = fun _ _ -> ()
    DisposeFileWatcher = fun _ _ -> ()
  }

/// Create an ElmProgram wired to real SageFs components.
/// The OnModelChanged callback is injected to allow different frontends.
/// The onSystemAlarm callback surfaces Elm loop exceptions to the caller.
let createProgram
  (deps: EffectDeps)
  (onModelChanged: SageFsModel -> RenderRegion list -> unit)
  (onSystemAlarm: string -> string -> unit)
  : ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> =
  {
    Update = SageFsUpdate.updateWithInvariant
    Render = SageFsRender.render
    ExecuteEffect = SageFsEffectHandler.execute deps
    OnModelChanged = onModelChanged
    OnSystemAlarm = onSystemAlarm
  }

/// Headless daemon mode does not need eagerly materialized RenderRegions on every
/// model change. Regions are rendered lazily only when a client explicitly asks.
let createHeadlessProgram
  (deps: EffectDeps)
  (onModelChanged: SageFsModel -> RenderRegion list -> unit)
  (onSystemAlarm: string -> string -> unit)
  : ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> =
  {
    Update = SageFsUpdate.updateWithInvariant
    Render = fun _ -> []
    ExecuteEffect = SageFsEffectHandler.execute deps
    OnModelChanged = onModelChanged
    OnSystemAlarm = onSystemAlarm
  }

/// Start the Elm loop with initial model and return the runtime.
let start
  (deps: EffectDeps)
  (onModelChanged: SageFsModel -> RenderRegion list -> unit)
  (onSystemAlarm: string -> string -> unit)
  (ct: System.Threading.CancellationToken)
  : ElmRuntime<SageFsModel, SageFsMsg, RenderRegion> =
  let program = createProgram deps onModelChanged onSystemAlarm
  ElmLoop.startWithCoalescerAndReducer
    SageFsMsgQueueCoalescing.tryAbsorbPending
    SageFsDispatchReduction.reduceDispatchBatch
    program
    (SageFsModel.initial())
    ct

/// Start the Elm loop for a headless daemon. This keeps update batches cheap by
/// deferring full RenderRegion materialization until a client requests it.
let startHeadless
  (deps: EffectDeps)
  (onModelChanged: SageFsModel -> RenderRegion list -> unit)
  (onSystemAlarm: string -> string -> unit)
  (ct: System.Threading.CancellationToken)
  : ElmRuntime<SageFsModel, SageFsMsg, RenderRegion> =
  let program = createHeadlessProgram deps onModelChanged onSystemAlarm
  ElmLoop.startWithCoalescerAndReducer
    SageFsMsgQueueCoalescing.tryAbsorbPending
    SageFsDispatchReduction.reduceDispatchBatch
    program
    (SageFsModel.initial())
    ct

let renderRegionsOnDemand
  (runtime: ElmRuntime<SageFsModel, SageFsMsg, RenderRegion>)
  : RenderRegion list =
  runtime.GetModel()
  |> SageFsRender.render

/// Render regions for a SPECIFIC session, not the Elm runtime's
/// currently-active session. This is the per-client path: the
/// dashboard's viewing session drives the output buffer that gets
/// rendered, so the TUI switching sessions doesn't change what a
/// dashboard tab is displaying. The other regions (sessions list,
/// diagnostics for the session, etc.) are filtered/selected for the
/// given session where applicable.
let renderRegionsForSession
  (runtime: ElmRuntime<SageFsModel, SageFsMsg, RenderRegion>)
  (sessionId: string)
  : RenderRegion list =
  let model = runtime.GetModel()
  let all = SageFsRender.render model
  // Override the output region with the buffer for the requested session.
  // The default render uses model.Sessions.ActiveSessionId; we replace
  // it with the caller's session so the dashboard's output matches
  // the session it's viewing, regardless of what the TUI has selected.
  all
  |> List.map (fun region ->
    match region.Id with
    | "output" ->
      let buf = model.RecentOutput.GetBuffer sessionId
      { region with Content = buf.RenderAllCached() }
    | "diagnostics" ->
      let diags =
        model.Diagnostics
        |> Map.tryFind sessionId
        |> Option.defaultValue []
        |> List.map (fun d ->
          sprintf "[%s] (%d,%d) %s"
            (Features.Diagnostics.DiagnosticSeverity.label d.Severity)
            d.Range.StartLine d.Range.StartColumn d.Message)
        |> String.concat "\n"
      { region with Content = diags }
    | _ -> region)

/// Dispatch a message and wait for the model to update.
/// Returns the model state after the dispatch has been processed.
let dispatchAndWait
  (dispatch: SageFsMsg -> unit)
  (getLatest: unit -> SageFsModel option)
  (waitForUpdate: int -> unit)
  (msg: SageFsMsg)
  (timeoutMs: int)
  : SageFsModel =
  dispatch msg
  waitForUpdate timeoutMs
  getLatest ()
  |> Option.defaultValue (SageFsModel.initial())
