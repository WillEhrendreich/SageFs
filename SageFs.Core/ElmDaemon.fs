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
    ListSessions = fun () ->
      // CQRS read path — lock-free snapshot, no mailbox blocking
      async { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    GetStreamingTestProxy = fun _sessionId -> None
    GetWarmupContext = None
    TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
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

/// Start the Elm loop with initial model and return the runtime.
let start
  (deps: EffectDeps)
  (onModelChanged: SageFsModel -> RenderRegion list -> unit)
  (onSystemAlarm: string -> string -> unit)
  (ct: System.Threading.CancellationToken)
  : ElmRuntime<SageFsModel, SageFsMsg, RenderRegion> =
  let program = createProgram deps onModelChanged onSystemAlarm
  ElmLoop.start program (SageFsModel.initial()) ct

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
