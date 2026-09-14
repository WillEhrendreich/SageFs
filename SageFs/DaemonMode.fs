module SageFs.Server.DaemonMode

open System
open System.Threading
open System.Threading.Tasks
open SageFs
open SageFs.WarmUp
open SageFs.Utils
open SageFs.Server
open SageFs.Server.DashboardTypes
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.ResponseCompression
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open OpenTelemetry.Logs

/// Send a message through the session proxy with railway error handling.
/// Centralizes error recovery for IO, pipe, and disposed exceptions.
/// Wrapped in a daemon.proxy_to_worker span for trace propagation to workers.
///
/// onWorkerDied: called synchronously when a pipe break reveals the worker is dead.
/// Posts SessionCommand to accelerate Faulted transition — closes race window between
/// pipe failure and process.Exited event firing.
let proxyToSession
  (getProxy: string -> Threading.Tasks.Task<(WorkerProtocol.WorkerMessage -> Async<WorkerProtocol.WorkerResponse>) option>)
  (onWorkerDied: string -> unit)
  (sid: string)
  (msg: WorkerProtocol.WorkerMessage)
  : Threading.Tasks.Task<Result<WorkerProtocol.WorkerResponse, SageFsError>> = task {
  let sw = System.Diagnostics.Stopwatch.StartNew()
  let activity =
    Instrumentation.startSpanWithKind
      Instrumentation.daemonSource "daemon.proxy_to_worker"
      System.Diagnostics.ActivityKind.Client
      [("session.id", box sid); ("worker.message_type", box (msg.GetType().Name))]
  match sid with
  | null | "" ->
    sw.Stop()
    Instrumentation.workerRequestErrors.Add(1L)
    Instrumentation.failSpan activity "empty session id"
    return Error (SageFsError.SessionNotFound (sid |> Option.ofObj |> Option.defaultValue ""))
  | _ ->
    try
      let! proxy = getProxy sid
      match proxy with
      | Some send ->
        let! resp = send msg |> Async.StartAsTask
        sw.Stop()
        Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
        Instrumentation.succeedSpan activity
        return Ok resp
      | None ->
        sw.Stop()
        Instrumentation.workerRequestErrors.Add(1L)
        Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
        Instrumentation.failSpan activity "No proxy available for session"
        return Error (SageFsError.WorkerCommunicationFailed(sid, "No proxy available for session"))
    with
    | :? IO.IOException as ex ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ex.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe broken — %s" ex.Message))
    | :? AggregateException as ae when (ae.InnerException :? IO.IOException) ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ae.InnerException.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe broken — %s" ae.InnerException.Message))
    | :? AggregateException as ae when (ae.InnerException :? ObjectDisposedException) ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ae.InnerException.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe closed — %s" ae.InnerException.Message))
    | :? ObjectDisposedException as ex ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ex.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe closed — %s" ex.Message))
}

/// Convert a known-good session ID string to SessionId.
/// Only use for strings that originated from a valid SessionId.
let private toSessionId (s: string) =
  match WorkerProtocol.SessionId.validate s with
  | Ok sid -> sid
  | Error msg -> failwithf "Invalid session ID '%s': %s" s msg

// ---------------------------------------------------------------------------
// DaemonInfra — lifetime group 1: one-time daemon infrastructure
// ---------------------------------------------------------------------------

/// Infrastructure created once at daemon startup.
/// Groups logger, HTTP client, friction store, cancellation, and state-change event.
type DaemonInfra = {
  Log: ILogger
  LoggerFactory: ILoggerFactory
  HttpClient: Net.Http.HttpClient
  FrictionStore: SageFs.Features.FrictionSqlite.FrictionStore option
  DaemonStreamId: string
  Cts: CancellationTokenSource
  StateChangedEvent: Event<SseEvent>
  /// Timeout for agent-facing worker fetches (MCP tools, SSE).
  McpFetchTimeoutSec: float
  /// Timeout for user-facing worker fetches (dashboard).
  DashboardFetchTimeoutSec: float
}

/// Create one-time daemon infrastructure (logger, HTTP client, friction store, CTS).
let createDaemonInfrastructure () : DaemonInfra =
  let otelConfigured = DaemonInfo.otelConfigured
  let loggerFactory =
    LoggerFactory.Create(fun builder ->
      builder
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information)
        .AddFilter("Microsoft", LogLevel.Warning)
      |> ignore
      match otelConfigured with
      | true ->
        builder.AddOpenTelemetry(fun otel ->
          otel.IncludeFormattedMessage <- true
          otel.IncludeScopes <- true
          otel.AddOtlpExporter() |> ignore
        ) |> ignore
      | false -> ()
    )
  let log = loggerFactory.CreateLogger("SageFs.Daemon")
  let httpClient = new Net.Http.HttpClient()

  log.LogInformation("SageFs daemon v{Version} starting", DaemonInfo.version)

  // Ensure adequate thread pool for concurrent SSE/MCP/effects
  let minWorker, minIO = System.Threading.ThreadPool.GetMinThreads()
  let desiredMin = max 32 (System.Environment.ProcessorCount * 4)
  match minWorker < desiredMin with
  | true ->
    System.Threading.ThreadPool.SetMinThreads(desiredMin, max minIO desiredMin) |> ignore
    log.LogInformation("ThreadPool min threads: {Old} → {New}", minWorker, desiredMin)
  | false -> ()

  // Create durable SQLite friction store at ~/.SageFs/friction.db
  let frictionStore =
    try
      let dir = DaemonState.SageFsDir
      match System.IO.Directory.Exists dir with
      | false -> System.IO.Directory.CreateDirectory dir |> ignore
      | true -> ()
      let dbPath = System.IO.Path.Combine(dir, "friction.db")
      let connStr = sprintf "Data Source=%s" dbPath
      let store = SageFs.Features.FrictionSqlite.Store.create connStr
      match store.Initialize() with
      | Ok () ->
        log.LogInformation("Friction store initialized at {Path}", dbPath)
        Some store
      | Error err ->
        log.LogWarning("Friction store initialization failed: {Error}. Friction telemetry will not persist.", err)
        None
    with ex ->
      log.LogWarning("Friction store creation failed: {Error}. Friction telemetry will not persist.", ex.Message)
      None

  {
    Log = log
    LoggerFactory = loggerFactory
    HttpClient = httpClient
    FrictionStore = frictionStore
    DaemonStreamId = "daemon-sessions"
    Cts = new CancellationTokenSource()
    StateChangedEvent = Event<SseEvent>()
    McpFetchTimeoutSec = 5.0
    DashboardFetchTimeoutSec = 0.5
  }

/// Interpret the manifest owner's answer to a --prune request. Synchronous and
/// separate so handlePrune's task{} stays shallow (avoids FS3511).
let private pruneOutcome (log: ILogger) (result: Features.ManifestOwner.CommitResult) : Result<bool, string> =
  match result with
  | Ok committed ->
    match committed.Persisted with
    | Features.ManifestOwner.Persisted.WrittenTo _ ->
      let pruned = Features.DaemonManifest.DaemonManifestState.aliveSessions committed.Previous
      log.LogInformation("Pruned {Count} session(s) from binary manifest", pruned.Length)
    | Features.ManifestOwner.Persisted.Unchanged ->
      log.LogInformation("No alive sessions to prune")
    Result.Ok true
  // W31(R13)/W36(R14): an unreadable manifest is an error, never "nothing to prune".
  | Error (Features.ManifestOwner.CommitError.BaseUnreadable (Features.ManifestTypes.ManifestLoadError.IoError err)) ->
    log.LogWarning("Cannot read manifest for prune — leaving untouched: {Error}", err)
    Result.Error (sprintf "Cannot prune: manifest read failed: %s" err)
  | Error (Features.ManifestOwner.CommitError.BaseUnreadable (Features.ManifestTypes.ManifestLoadError.CorruptData err)) ->
    log.LogWarning("Manifest corrupt — prune skipped, manual recovery needed: {Error}", err)
    Result.Error (sprintf "Cannot prune: manifest corrupt: %s" err)
  | Error (Features.ManifestOwner.CommitError.BaseUnreadable Features.ManifestTypes.ManifestLoadError.NotFound) ->
    log.LogInformation("No binary manifest found — nothing to prune")
    Result.Ok true
  | Error (Features.ManifestOwner.CommitError.WriteFailed err) ->
    log.LogWarning("Prune save failed: {Error}", err)
    Result.Error (sprintf "Cannot prune: manifest write failed: %s" err)
  | Error Features.ManifestOwner.CommitError.AfterShutdown ->
    Result.Error (sprintf "Cannot prune: %s" (Features.ManifestOwner.CommitError.describe Features.ManifestOwner.CommitError.AfterShutdown))

/// Handle --prune flag: clear the binary manifest and return a Result.
/// W28+W31(R13): Parametrized dir/log/checkDaemonRunning for testability.
/// W36(R14): Returns Result<bool, string> — Ok true=pruned/exit, Ok false=not-requested/continue,
///           Error msg=prune-was-requested-but-failed → caller exits with error.
/// W42(R14): checkDaemonRunning: unit -> Task<DaemonInfo option> to avoid Async.RunSynchronously
///           inside task{} (thread pool starvation risk).
let handlePrune (dir: string) (log: ILogger) (checkDaemonRunning: unit -> System.Threading.Tasks.Task<DaemonInfo option>) (flags: Args.DaemonFlags) = task {
  match flags.Prune with
  | true ->
    // W28(R13): Refuse to prune if daemon is running — cross-process TOCTOU guard.
    // W42(R14): await Task directly instead of Async.RunSynchronously in task{}.
    let! daemonInfo = checkDaemonRunning()
    match daemonInfo with
    | Some info ->
      log.LogWarning("Cannot prune while daemon is running (PID {Pid}) — stop the daemon first", info.Pid)
      return Result.Error (sprintf "Cannot prune: daemon running at PID %d — stop it first" info.Pid)
    | None ->
      // Even a one-shot prune writes through the single manifest owner.
      use owner = Features.ManifestOwner.start (Log.asILogger ()) dir
      let! committed = owner.Commit (Features.DaemonManifest.ManifestMutation.StampAllStopped DateTimeOffset.UtcNow)
      return pruneOutcome log committed
  | false -> return Result.Ok false
}

/// Remove adaptive-store snapshots for sessions that no longer exist and
/// reset the shared feature push state when the last session is gone.
/// Returns the ids that were swept (roast queue item 2 — session state must
/// die with the session instead of accumulating for the daemon's lifetime).
let sweepStaleSessionState
  (liveIds: Set<string>)
  (adaptive: SageFs.Features.LiveBindingsAdaptive.State)
  (featureState: SageFs.Features.FeatureHooks.FeaturePushState ref)
  : string list =
  let stale =
    adaptive.SessionSnapshots.Keys
    |> Seq.filter (fun k -> not (liveIds.Contains k))
    |> Seq.toList
  stale |> List.iter (fun k -> SageFs.Features.LiveBindingsAdaptive.remove adaptive k)
  if liveIds.IsEmpty then
    System.Threading.Volatile.Write(&featureState.contents, SageFs.Features.FeatureHooks.FeaturePushState.empty)
  stale

/// Build SessionManagementOps record from mailbox + snapshot reader.
/// Session lifecycle events are recorded directly in the daemon.sagefm binary
/// manifest (the sole source of truth for session resume) — there is no
/// separate event-append step or per-session event stream.
let createSessionOps
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (manifestOwner: Features.ManifestOwner.Handle)
  : SessionManagementOps =
  {
    CreateSession = fun projects workingDir workflow ->
      task {
        let autoOpenNamespaces = DirectoryConfig.autoOpenNamespacesForDirectory workingDir
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.CreateSession(projects, workingDir, autoOpenNamespaces, workflow, reply))
          |> Async.StartAsTask
        return
          result
          |> Result.map (fun info -> WorkerProtocol.SessionId.value info.Id)
      }
    ListSessions = fun () ->
      task {
        let sessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
        return SessionOperations.formatSessionList DateTime.UtcNow None sessions
      }
    StopSession = fun sessionId ->
      task {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.StopSession(toSessionId sessionId, reply))
          |> Async.StartAsTask
        return
          result
          |> Result.map (fun () ->
            sprintf "Session '%s' stopped." sessionId)
      }
    PurgeSession = fun sessionId ->
      task {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.StopSession(toSessionId sessionId, reply))
          |> Async.StartAsTask
        match result with
        | Ok () ->
          // Remove the manifest entry entirely — the session is gone from the
          // resume picker too (purge = the corrupted-state escape hatch).
          let! committed = manifestOwner.Commit (Features.DaemonManifest.ManifestMutation.Remove sessionId)
          match committed with
          | Ok _ -> ()
          | Error err ->
            Log.warn "[DaemonMode] Purge session %s (remove manifest entry): %s" sessionId (Features.ManifestOwner.CommitError.describe err)
        | Error _ -> ()
        return
          result
          |> Result.map (fun () ->
            sprintf "Session '%s' purged — manifest entry removed." sessionId)
      }
    RestartSession = fun sessionId rebuild ->
      task {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.RestartSession(sessionId, rebuild, reply))
          |> Async.StartAsTask
        return result
      }
    GetProxy = fun sessionId ->
      let snapshot = readSnapshot()
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let urlMap = snapshot.WorkerBaseUrls |> Map.fold (fun acc k v -> Map.add (WorkerProtocol.SessionId.value k) v acc) Map.empty
      task { return HttpWorkerClient.proxyFromUrls sidStr urlMap }
    GetSessionInfo = fun sessionId ->
      task { return SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) }
    GetAllSessions = fun () ->
      task { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    UpdateSessionStatus = fun sessionId (status: WorkerProtocol.SessionLifecycleStatus) ->
      task {
        sessionManager.Post(
          SessionManager.SessionCommand.UpdateSessionStatus(sessionId, status))
      }
    NotifyWorkerDied = fun sessionId ->
      // Post WorkerExited with pid=-1 to accelerate Faulted transition.
      // The real proc.Exited event will also fire; the stale-event guard
      // in WorkerExited handler (checks currentPid <> workerPid) prevents double-restart.
      sessionManager.Post(
        SessionManager.SessionCommand.WorkerExited(sessionId, -1, -1))
    ClaimRun = fun sessionId project ->
      sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.ClaimRun(sessionId, project, reply))
      |> Async.StartAsTask
    ClaimStop = fun sessionId ->
      sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.ClaimStop(sessionId, reply))
      |> Async.StartAsTask
    AdvanceRun = fun sessionId generation next ->
      sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.AdvanceRun(sessionId, generation, next, reply))
      |> Async.StartAsTask
    EndAppRun = fun sessionId generation runId final ->
      sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.EndAppRun(sessionId, generation, runId, final, reply))
      |> Async.StartAsTask
    AwaitReady = fun sessionId timeout ->
      task {
        try
          return!
            sessionManager.PostAndAsyncReply(
              (fun reply -> SessionManager.SessionCommand.AwaitReady(sessionId, reply)),
              int timeout.TotalMilliseconds)
            |> Async.StartAsTask
        with :? TimeoutException ->
          return Error (SageFsError.WorkerTimeout (WorkerProtocol.SessionId.value sessionId, "restart", timeout.TotalSeconds))
      }
    SwitchWorkflow = fun sessionIdStr workflow ->
      task {
        let sessionId = toSessionId sessionIdStr
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.SwitchWorkflow(sessionId, workflow, reply))
          |> Async.StartAsTask
        return result
      }
  }

/// Look up worker HTTP base URL for a session from CQRS snapshot.
let getWorkerBaseUrl (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: WorkerProtocol.SessionId) =
  let snapshot = readSnapshot()
  match Map.tryFind sid snapshot.WorkerBaseUrls with
  | Some url when url.Length > 0 -> Some url
  | _ -> None

/// Fetch JSON from a worker endpoint with timeout, returning None on failure.
let fetchWorkerEndpoint
  (httpClient: Net.Http.HttpClient)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (sessionId: WorkerProtocol.SessionId)
  (path: string)
  (timeout: float)
  (parse: string -> 'T)
  : Threading.Tasks.Task<'T option> = task {
  match getWorkerBaseUrl readSnapshot sessionId with
  | Some baseUrl ->
    try
      use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout))
      let! resp = httpClient.GetStringAsync(sprintf "%s%s" baseUrl path, cts.Token)
      return Some (parse resp)
    with
    | :? Threading.Tasks.TaskCanceledException ->
      Log.warn "[fetchWorkerEndpoint] Timeout (%.0fs) fetching %s for session %s" timeout path (WorkerProtocol.SessionId.value sessionId)
      return None
    | :? Net.Http.HttpRequestException as ex ->
      Log.error "[fetchWorkerEndpoint] HTTP error fetching %s for session %s: %s\n%s" path (WorkerProtocol.SessionId.value sessionId) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      return None
    | ex ->
      Log.error "[fetchWorkerEndpoint] Unexpected error fetching %s for session %s: %s" path (WorkerProtocol.SessionId.value sessionId) (ex.GetType().Name)
      return None
  | None -> return None
}

/// Build DaemonManifestState from active sessions (used in periodic save + shutdown).
/// `activeSessionId` must come from the live Elm model (not the snapshot) since the
/// snapshot has no concept of "which session is currently active".
let buildManifestState (snapshot: SessionManager.QuerySnapshot) (activeSessionId: string option) =
  let activeSessions = SessionManager.QuerySnapshot.allSessions snapshot
  let toRecord (s: WorkerProtocol.SessionInfo) : Features.DaemonManifest.DaemonSessionRecord =
    { SessionId = WorkerProtocol.SessionId.value s.Id; Projects = s.Projects; WorkingDir = s.WorkingDirectory
      CreatedAt = DateTimeOffset(s.CreatedAt, TimeSpan.Zero); StoppedAt = None }
  { Features.DaemonManifest.DaemonManifestState.Sessions =
      activeSessions |> List.map (fun s -> WorkerProtocol.SessionId.value s.Id, toRecord s) |> Map.ofList
    Features.DaemonManifest.DaemonManifestState.ActiveSessionId = activeSessionId }

/// The sessions running right now, as manifest records (StoppedAt = None).
let liveSessionRecords (snapshot: SessionManager.QuerySnapshot) : Features.DaemonManifest.DaemonSessionRecord list =
  (buildManifestState snapshot None).Sessions |> Map.values |> List.ofSeq

/// The live sync the manifest owner commits: a periodic save (`stampActive = None`,
/// live sessions stay alive) or the shutdown save (`stampActive = Some now`, every
/// live session is stamped stopped). Takes the snapshot as a value (W25) so one
/// consistent read feeds the whole sync.
let liveSyncMutation
  (snapshot: SessionManager.QuerySnapshot)
  (activeSessionId: string option)
  (stampActive: DateTimeOffset option)
  : Features.DaemonManifest.ManifestMutation =
  let at, mode =
    match stampActive with
    | Some ts -> ts, Features.DaemonManifest.LiveSync.ShuttingDown
    | None -> DateTimeOffset.UtcNow, Features.DaemonManifest.LiveSync.Running
  Features.DaemonManifest.ManifestMutation.SyncLive (liveSessionRecords snapshot, activeSessionId, at, mode)

/// What a live sync would make of the manifest currently in `dir` — read only,
/// never writes. Production writes go through Features.ManifestOwner, which
/// applies the same pure ManifestMutation.apply; this keeps the merge rules
/// (W10/W20/W23/W34/W38) checkable against a directory.
/// Returns Error on an unreadable manifest: a write based on it would erase history.
let mergeManifestWithExisting
  (dir: string)
  (log: Microsoft.Extensions.Logging.ILogger)
  (snapshot: SessionManager.QuerySnapshot)
  (activeSessionId: string option)
  (stampActive: DateTimeOffset option)
  : Result<Features.DaemonManifest.DaemonManifestState, Features.ManifestTypes.ManifestLoadError> =
  let sync = liveSyncMutation snapshot activeSessionId stampActive
  match Features.DaemonPersistence.loadManifest dir with
  | Ok existing -> Ok (Features.DaemonManifest.ManifestMutation.apply sync existing)
  | Error Features.ManifestTypes.ManifestLoadError.NotFound ->
    Ok (Features.DaemonManifest.ManifestMutation.apply sync Features.DaemonManifest.DaemonManifestState.empty)
  | Error (Features.ManifestTypes.ManifestLoadError.IoError err) ->
    log.LogWarning("Cannot read manifest for merge — skipping write to preserve history: {Error}", err)
    Error (Features.ManifestTypes.ManifestLoadError.IoError err)
  | Error (Features.ManifestTypes.ManifestLoadError.CorruptData err) ->
    log.LogWarning("Manifest data corrupt — skipping write to preserve history: {Error}", err)
    Error (Features.ManifestTypes.ManifestLoadError.CorruptData err)

/// Log the manifest owner's answer to a live sync.
let logManifestCommit (log: ILogger) (level: LogLevel) (what: string) (result: Features.ManifestOwner.CommitResult) =
  match result with
  | Ok committed ->
    match committed.Persisted with
    | Features.ManifestOwner.Persisted.WrittenTo path -> log.Log(level, "{What}: saved session manifest to {Path}", what, path)
    | Features.ManifestOwner.Persisted.Unchanged -> log.LogDebug("{What}: session manifest unchanged", what)
  | Error (Features.ManifestOwner.CommitError.BaseUnreadable _ as err) ->
    log.LogWarning("{What} skipped — {Error}; writing now would erase the manifest's history", what, Features.ManifestOwner.CommitError.describe err)
  | Error err ->
    log.LogWarning("{What} failed: {Error}", what, Features.ManifestOwner.CommitError.describe err)

/// Get session state from CQRS snapshot.
let getSessionStateFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: WorkerProtocol.SessionId) =
  let snapshot = readSnapshot()
  match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
  | Some info -> WorkerProtocol.SessionLifecycleStatus.toSessionState info.Status
  | None -> SessionState.Uninitialized

/// Get working directory for a session from CQRS snapshot.
let getSessionWorkingDirFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: WorkerProtocol.SessionId) =
  let snapshot = readSnapshot()
  match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
  | Some info -> info.WorkingDirectory
  | None -> ""

/// Get warmup status message for a session.
let getStatusMsgFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: WorkerProtocol.SessionId) =
  readSnapshot().WarmupProgress |> Map.tryFind sid

/// Fetch eval stats from worker HTTP endpoint.
let getEvalStatsFromWorker
  (httpClient: Net.Http.HttpClient)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (sid: WorkerProtocol.SessionId) = task {
  let snapshot = readSnapshot()
  match Map.tryFind sid snapshot.WorkerBaseUrls with
  | Some baseUrl when baseUrl.Length > 0 ->
    try
      use cts = new Threading.CancellationTokenSource(Timeouts.healthCheck)
      let! resp = httpClient.GetStringAsync(sprintf "%s/status?replyId=dash-stats" baseUrl, cts.Token)
      use doc = Text.Json.JsonDocument.Parse(resp)
      let root = doc.RootElement
      let getInt (name: string) def =
        match root.TryGetProperty(name) with
        | true, v -> v.GetInt32()
        | false, _ -> def
      let getLong (name: string) def =
        match root.TryGetProperty(name) with
        | true, v -> v.GetInt64()
        | false, _ -> def
      let evalCount = getInt "evalCount" 0
      let avgMs = getLong "avgDurationMs" 0L
      let minMs = getLong "minDurationMs" 0L
      let maxMs = getLong "maxDurationMs" 0L
      return
        { EvalCount = evalCount
          TotalDuration = TimeSpan.FromMilliseconds(float avgMs * float evalCount)
          MinDuration = TimeSpan.FromMilliseconds(float minMs)
          MaxDuration = TimeSpan.FromMilliseconds(float maxMs) }
        : Affordances.EvalStats
    with
    | :? Net.Http.HttpRequestException | :? Threading.Tasks.TaskCanceledException -> return Affordances.EvalStats.empty
    | :? Text.Json.JsonException as ex ->
      Log.error "[getEvalStats] JSON parse error for %s: %s\n%s" (WorkerProtocol.SessionId.value sid) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      return Affordances.EvalStats.empty
    | ex ->
      Log.error "[getEvalStats] Unexpected error for %s: %s (%s)\n%s" (WorkerProtocol.SessionId.value sid) ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      return Affordances.EvalStats.empty
  | _ -> return Affordances.EvalStats.empty
}

/// Create hot-reload proxy HTTP endpoints that forward to worker servers.
let createHotReloadProxyEndpoints
  (getWorkerBaseUrl: WorkerProtocol.SessionId -> string option)
  (httpClient: Net.Http.HttpClient)
  (stateChangedEvent: Event<SseEvent>)
  : HttpEndpoint list =
  let proxyToWorker (sidStr: string) (workerPath: string) (httpCall: string -> Threading.Tasks.Task<string * int * bool>) (ctx: HttpContext) = task {
    match WorkerProtocol.SessionId.validate sidStr with
    | Error _ ->
      ctx.Response.StatusCode <- 400
      do! ctx.Response.WriteAsJsonAsync({| error = "Invalid session ID" |})
    | Ok sid ->
    match getWorkerBaseUrl sid with
    | Some baseUrl ->
      try
        let url = sprintf "%s%s" baseUrl workerPath
        let! (respBody, statusCode, triggerChange) = httpCall url
        ctx.Response.ContentType <- "application/json"
        ctx.Response.StatusCode <- statusCode
        do! ctx.Response.WriteAsync(respBody)
        match triggerChange with
        | true -> stateChangedEvent.Trigger (HotReloadChanged sid)
        | false -> ()
      with ex ->
        ctx.Response.StatusCode <- 502
        do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
    | None ->
      ctx.Response.StatusCode <- 404
      do! ctx.Response.WriteAsJsonAsync({| error = "Session not found or not ready" |})
  }
  let proxyGet (sid: string) (workerPath: string) (ctx: HttpContext) =
    proxyToWorker sid workerPath (fun url -> task {
      use timeoutCts = new System.Threading.CancellationTokenSource(5000)
      use linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token)
      let! resp = httpClient.GetStringAsync(url, linked.Token)
      return (resp, 200, false)
    }) ctx
  let proxyPost (sid: string) (workerPath: string) (ctx: HttpContext) =
    proxyToWorker sid workerPath (fun url -> task {
      use timeoutCts = new System.Threading.CancellationTokenSource(5000)
      use linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token)
      // Guard against oversized payloads (hot-reload control messages are always < 1 KB)
      let maxBodyBytes = 1_048_576L  // 1 MB hard limit
      match ctx.Request.ContentLength with
      | contentLength when contentLength.HasValue && contentLength.Value > maxBodyBytes ->
        return (sprintf """{"error":"Request body too large (%d bytes, max 1 MB)"}""" contentLength.Value, 413, false)
      | _ ->
      use reader = new IO.StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync(linked.Token)
      match int64 (System.Text.Encoding.UTF8.GetByteCount(body)) > maxBodyBytes with
      | true -> return ("""{"error":"Request body too large (max 1 MB)"}""", 413, false)
      | false ->
      use content = new Net.Http.StringContent(body, Text.Encoding.UTF8, "application/json")
      let! resp = httpClient.PostAsync(url, content, linked.Token)
      let! respBody = resp.Content.ReadAsStringAsync(linked.Token)
      return (respBody, int resp.StatusCode, resp.IsSuccessStatusCode)
    }) ctx
  let extractSid = Dashboard.routeValue "sid"
  let proxyGetRoute path = Dashboard.mapGetRaw (sprintf "/api/sessions/{sid}%s" path) extractSid (fun sid -> fun ctx -> proxyGet sid path ctx)
  let proxyPostRoute path = Dashboard.mapPostRaw (sprintf "/api/sessions/{sid}%s" path) extractSid (fun sid -> fun ctx -> proxyPost sid path ctx)
  [
    proxyGetRoute "/hotreload"
    proxyPostRoute "/hotreload/toggle"
    proxyPostRoute "/hotreload/watch-all"
    proxyPostRoute "/hotreload/unwatch-all"
    proxyPostRoute "/hotreload/watch-project"
    proxyPostRoute "/hotreload/unwatch-project"
    proxyPostRoute "/hotreload/watch-directory"
    proxyPostRoute "/hotreload/unwatch-directory"
    proxyGetRoute "/warmup-context"
  ]

/// Graceful shutdown: save caches, persist manifest, stop all workers.
let performGracefulShutdown
  (log: ILogger)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (getModel: unit -> SageFsModel)
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  (manifestOwner: Features.ManifestOwner.Handle)
  = task {
  // W25(R12): Read snapshot ONCE — pass as value throughout to ensure a consistent view
  // across test-cache saves, event appends, and manifest merge. Multiple readSnapshot()
  // calls during shutdown can observe different state if sessions exit between calls.
  let snapshot = readSnapshot()
  // W40(R14): Read model ONCE before any async operations to prevent divergence.
  // The old code called getModel() at line 545 (for testState) and again at line 577
  // (for activeSessionId) — after the 5s event-append await. A session starting between
  // those two calls would cause activeSessionId to reference a session absent from snapshot.
  let model = getModel()
  let activeSessions = SessionManager.QuerySnapshot.allSessions snapshot
  // Save test cache for each unique project set
  let testState = model.LiveTesting.TestState
  let uniqueProjectSets =
    activeSessions
    |> List.map (fun s -> s.Projects)
    |> List.distinctBy (fun ps ->
      ps |> List.sort |> List.map (fun p -> p.Replace("\\", "/").ToLowerInvariant()) |> String.concat "|")
  for projects in uniqueProjectSets do
    match Features.DaemonPersistence.saveTestCache DaemonState.SageFsDir projects testState with
    | Ok path -> log.LogInformation("Saved test cache to {Path}", path)
    | Error err ->
      Instrumentation.persistenceSaveErrors.Add(
        1L, System.Collections.Generic.KeyValuePair("format", box "stc1"))
      log.LogWarning("Failed to save test cache: {Error}", err)

  // W29(R12): Event append to daemon stream removed — using binary manifest only for persistence.
  // Session stop events are recorded in the manifest instead.

  // Persist session manifest for binary-first resume
  // W4(R9) + W10(R10): Use mergeManifestWithExisting shared helper — loads existing manifest,
  // preserves stopped sessions' original StoppedAt, stamps active sessions as stopped now.
  // W23+W25(R12): Skip write on Error — returning active-only state would erase history.
  // W40(R14): activeSessionId derived from model read at function entry (not a second getModel call).
  let activeSessionId = model.Sessions.ActiveSessionId |> ActiveSession.sessionId |> Option.map WorkerProtocol.SessionId.value
  let now = DateTimeOffset.UtcNow
  // The owner applies this after every manifest change queued before it and
  // replies only once it is on disk, so shutdown awaits a durable manifest.
  // Once applied, the owner refuses live syncs: a late periodic save cannot
  // mark these sessions alive again.
  let shutdownSync = manifestOwner.Commit (liveSyncMutation snapshot activeSessionId (Some now))
  try
    let! committed = shutdownSync.WaitAsync(TimeSpan.FromSeconds 10.0)
    logManifestCommit log LogLevel.Information "Shutdown manifest save" committed
  with
  | :? TimeoutException ->
    log.LogWarning("Shutdown manifest save did not finish within 10s — this shutdown may not be recorded")
  | ex ->
    log.LogWarning("Shutdown manifest save failed: {Error}", ex.Message)

  // Stop all workers with a timeout
  let stopTask =
    sessionManager.PostAndAsyncReply(fun reply ->
      SessionManager.SessionCommand.StopAll reply)
    |> Async.StartAsTask
  let! stop_winner = System.Threading.Tasks.Task.WhenAny(stopTask, System.Threading.Tasks.Task.Delay(Timeouts.processNormalExit))
  match System.Object.ReferenceEquals(stop_winner, stopTask) with
  | false -> log.LogWarning("StopAll timed out — some workers may not have stopped cleanly")
  | true -> ()
}

/// Scans project source files with tree-sitter, then dispatches
/// locations and test cases to the Elm loop.
let private dispatchDiscoveredTests
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (workingDir: string)
  (log: ILogger)
  (dispatch: SageFsMsg -> unit)
  (sid: WorkerProtocol.SessionId)
  (tests: Features.LiveTesting.TestCase array)
  (providers: Features.LiveTesting.ProviderDescription list) =
  let sessionInfo = SessionManager.QuerySnapshot.tryGetSession sid (readSnapshot())
  let projectDirs =
    match sessionInfo with
    | Some info ->
      info.Projects
      |> List.map (fun proj ->
        let fullPath =
          match IO.Path.IsPathRooted proj with
          | true -> proj
          | false -> IO.Path.Combine(info.WorkingDirectory, proj)
        IO.Path.GetDirectoryName fullPath)
      |> List.distinct
    | None -> [ workingDir ]
  let locations =
    match Features.LiveTesting.TestTreeSitter.isAvailable () with
    | true ->
      projectDirs
      |> List.toArray
      |> Array.collect (fun dir ->
        match IO.Directory.Exists dir with
        | true ->
          IO.Directory.GetFiles(dir, "*.fs", IO.SearchOption.AllDirectories)
          |> Array.filter (fun f ->
            let rel = f.Substring(dir.Length)
            let sep = string IO.Path.DirectorySeparatorChar
            not (rel.Contains(sep + "bin" + sep))
            && not (rel.Contains(sep + "obj" + sep)))
          |> Array.collect (fun f ->
            try
              let code = IO.File.ReadAllText f
              Features.LiveTesting.TestTreeSitter.discover f code
            with ex ->
              log.LogWarning("[Daemon] Tree-sitter discovery failed for {File}: {Error}", f, ex.Message)
              Array.empty)
        | false -> Array.empty)
    | false -> Array.empty
  match Array.isEmpty locations with
  | false -> dispatch (SageFsMsg.Event (TuiEvent.TestLocationsDetected (WorkerProtocol.SessionId.value sid, locations)))
  | true -> ()
  // Zero tests is still an answer: it completes this session's discovery.
  dispatch (SageFsMsg.Event (TuiEvent.TestsDiscovered (WorkerProtocol.SessionId.value sid, tests)))
  match List.isEmpty providers with
  | false -> dispatch (SageFsMsg.Event (TuiEvent.ProvidersDetected providers))
  | true -> ()

/// Handle a worker's test discovery report from SessionManager → Elm model.
/// A failure reaches the model with its reason instead of only the log.
let handleTestDiscovery
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (workingDir: string)
  (log: ILogger)
  (dispatch: SageFsMsg -> unit)
  (sid: WorkerProtocol.SessionId)
  (report: SessionManager.TestDiscoveryReport) =
  match report with
  | SessionManager.TestDiscoveryReport.Discovered (tests, providers) ->
    dispatchDiscoveredTests readSnapshot workingDir log dispatch sid tests providers
  | SessionManager.TestDiscoveryReport.DiscoveryFailed reason ->
    let id = WorkerProtocol.SessionId.value sid
    log.LogWarning("[Daemon] Test discovery failed for {SessionId}: {Reason}", id, reason)
    dispatch (SageFsMsg.Event (TuiEvent.TestDiscoveryFailed (id, reason)))

/// Parse warmup progress string ("step/total msg") into structured fields.
let tryParseWarmupProgress (progress: string) =
  WarmupProgressLine.tryParsePayload progress

/// Parse warmup progress string ("step/total msg") and dispatch to Elm.
let handleWarmupProgress (dispatch: SageFsMsg -> unit) (_sid: string) (progress: string) =
  match tryParseWarmupProgress progress with
  | Some (step, total, msg) ->
    dispatch (SageFsMsg.Event (TuiEvent.WarmupProgress (step, total, msg)))
  | None -> ()

/// Periodic cache + manifest save callback.
/// Only writes when RunGeneration has advanced since last save.
let periodicCacheSave
  (log: ILogger)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (getModel: unit -> SageFsModel)
  (lastSavedGeneration: int ref) =
  try
    let model = getModel()
    let (Features.LiveTesting.RunGeneration gen) = model.LiveTesting.TestState.LastGeneration
    match gen > lastSavedGeneration.Value with
    | true ->
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let activeSessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
      let uniqueProjectSets =
        activeSessions
        |> List.map (fun s -> s.Projects)
        |> List.distinctBy (fun ps ->
          ps |> List.sort |> List.map (fun p -> p.Replace("\\", "/").ToLowerInvariant()) |> String.concat "|")
      // W22(R11): Track per-project-set success. Only advance lastSavedGeneration if ALL saves
      // succeed — prevents suppressing a retry when one project set fails.
      let mutable allSavesSucceeded = true
      for projects in uniqueProjectSets do
        match Features.DaemonPersistence.saveTestCache DaemonState.SageFsDir projects model.LiveTesting.TestState with
        | Ok path -> log.LogDebug("Periodic cache save to {Path} (gen {Gen})", path, gen)
        | Error err ->
          allSavesSucceeded <- false
          Instrumentation.persistenceSaveErrors.Add(
            1L, System.Collections.Generic.KeyValuePair("format", box "stc1"))
          log.LogWarning("Periodic cache save failed: {Error}", err)
      sw.Stop()
      Instrumentation.cacheSaveCount.Add(1L)
      Instrumentation.cacheSaveMs.Record(
        sw.Elapsed.TotalMilliseconds,
        System.Collections.Generic.KeyValuePair("coverage_entries", box (int64 model.LiveTesting.TestState.TestCoverageBitmaps.Count)),
        System.Collections.Generic.KeyValuePair("result_entries", box (int64 model.LiveTesting.TestState.LastResults.Count)))
      // W11(R10): Volatile.Write ensures the store is visible across threads.
      // W22(R11): Only advance if all project-set saves succeeded — enables retry on next tick.
      if allSavesSucceeded then
        System.Threading.Volatile.Write(&lastSavedGeneration.contents, gen)
    | false -> ()
  with ex ->
    Instrumentation.periodicTaskErrors.Add(
      1L, System.Collections.Generic.KeyValuePair("task", box "cache_save"))
    log.LogWarning("Periodic cache save error: {Error}", ex.Message)

/// Periodic manifest save (binary session resume): queue a live sync on the
/// manifest owner. The timer never writes the file itself — the owner applies
/// the sync in order with every purge and forget, so neither can lose the other.
let periodicManifestSave
  (log: ILogger)
  (manifestOwner: Features.ManifestOwner.Handle)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (getModel: unit -> SageFsModel) =
  try
    // W40(R14): Read model ONCE before snapshot to prevent divergence.
    // If getModel() were called after readSnapshot(), a session starting/stopping between
    // the two calls could cause activeSessionId to reference a session absent from snapshot.
    let model = getModel()
    let activeSessionId = model.Sessions.ActiveSessionId |> ActiveSession.sessionId |> Option.map WorkerProtocol.SessionId.value
    // W10(R10): the sync keeps stopped sessions (live ones stay StoppedAt = None).
    // W23+W25(R12): one snapshot read; the owner skips the write on an unreadable manifest.
    let snapshot = readSnapshot()
    manifestOwner.Post(liveSyncMutation snapshot activeSessionId None, fun result ->
      match result with
      | Error (Features.ManifestOwner.CommitError.BaseUnreadable _) ->
        Instrumentation.periodicTaskErrors.Add(
          1L, System.Collections.Generic.KeyValuePair("task", box "manifest_read_error"))
      | _ -> ()
      logManifestCommit log LogLevel.Debug "Periodic manifest save" result)
  with ex ->
    Instrumentation.periodicTaskErrors.Add(
      1L, System.Collections.Generic.KeyValuePair("task", box "manifest_save"))
    log.LogWarning("Periodic manifest save error: {Error}", ex.Message)

/// Create a debounced file watcher for live testing.
/// Returns (watcher, debounceTimer) — caller must dispose both.
/// Manages per-session file watchers for live testing.
/// Each session directory gets its own FileSystemWatcher. Watchers are created
/// when sessions are discovered and disposed when sessions are removed.
type LiveTestWatcherManager
  ( dispatch: SageFsMsg -> unit,
    onFileReloaded: WorkerProtocol.SessionId -> string -> unit,
    fallbackDir: string option ) =
  // onFileReloaded: sessionId -> path -> unit. Carries the owning session so
  // downstream FileReloaded events are session-attributed (the isolation
  // blocker: two sessions can share one working dir; a path alone cannot
  // disambiguate which session's live-test state changed).
  //
  // fallbackDir: the daemon-CWD fallback watcher. It claims no session — files
  // under it fire FileContentChanged but never FileReloaded (a path with no
  // owning session must not be attributed to a fabricated one).

  let liveTestWatcherDebounceMs = 75

  let watchers = System.Collections.Concurrent.ConcurrentDictionary<string, System.IO.FileSystemWatcher * System.Threading.Timer>()
  // dir -> owning session IDs (only real sessions; the fallback dir is tracked
  // separately via fallbackDir).
  let dirSessions = System.Collections.Concurrent.ConcurrentDictionary<string, WorkerProtocol.SessionId list>()
  // dir -> generation counter. Incremented every time a watcher for the dir is
  // stopped (dispose/recreate lifecycle). Events queued before a stop carry the
  // dir's epoch at queue time; a queued path whose epoch no longer matches is a
  // stale event from a dead watcher generation and is dropped at fire time.
  let dirEpochs = System.Collections.Concurrent.ConcurrentDictionary<string, int64>()
  let pendingPaths = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()
  // path -> epoch of the dir that queued it (the dir prefix that matched at
  // queue time). Kept in lock-step with pendingPaths.
  let pendingEpochs = System.Collections.Concurrent.ConcurrentDictionary<string, int64>()
  let pendingLock = obj()
  let debounceMs = liveTestWatcherDebounceMs

  /// The watched directory that contains `path` (longest-prefix wins), if any.
  /// Fallback dir participates only when no session dir claims the path.
  let dirForPath (path: string) =
    let normalizedPath = System.IO.Path.GetFullPath(path)
    dirSessions
    |> Seq.filter (fun kvp -> normalizedPath.StartsWith(System.IO.Path.GetFullPath(kvp.Key), System.StringComparison.OrdinalIgnoreCase))
    |> Seq.sortByDescending (fun kvp -> kvp.Key.Length)
    |> Seq.tryHead
    |> Option.map (fun kvp -> kvp.Key)
    |> Option.orElseWith (fun () ->
      match fallbackDir with
      | Some f when normalizedPath.StartsWith(System.IO.Path.GetFullPath(f), System.StringComparison.OrdinalIgnoreCase) -> Some f
      | _ -> None)

  /// Current epoch for a watched dir (0 if never stopped).
  let epochOf (dir: string) =
    match dirEpochs.TryGetValue(dir) with
    | true, e -> e
    | false, _ -> 0L

  /// Session ID(s) owning the directory that contains `path` (longest-prefix
  /// dir wins — a file under a session's dir must not be attributed to the
  /// daemon-CWD fallback).
  let sessionsForPath (path: string) =
    let normalizedPath = System.IO.Path.GetFullPath(path)
    dirSessions
    |> Seq.filter (fun kvp -> normalizedPath.StartsWith(System.IO.Path.GetFullPath(kvp.Key), System.StringComparison.OrdinalIgnoreCase))
    |> Seq.sortByDescending (fun kvp -> kvp.Key.Length)
    |> Seq.tryHead
    |> Option.map (fun kvp -> kvp.Value)
    |> Option.defaultValue []

  let debounceCallback _ =
    let paths =
      lock pendingLock (fun () ->
        let ps = pendingPaths.Keys |> Seq.toArray
        let epochs = ps |> Array.map (fun p -> match pendingEpochs.TryGetValue(p) with | true, e -> Some e | false, _ -> None)
        pendingPaths.Clear()
        pendingEpochs.Clear()
        Array.zip ps epochs)
    for (path, queuedEpoch) in paths do
      try
        // Stale-event guard: if the dir that queued this path was stopped and
        // recreated (or dropped entirely) while the debounce was pending, the
        // queued event belongs to a dead watcher generation. Drop it — a stale
        // reload must never reach a fresh session claim.
        let dir = dirForPath path
        let isStale = SageFs.LiveTestWatcherStaleGuard.isStaleEvent dir queuedEpoch epochOf
        match isStale with
        | true -> ()
        | false ->
          let fi = System.IO.FileInfo(path)
          match fi.Exists && fi.Length < 1_048_576L with
          | true ->
            let content = System.IO.File.ReadAllText(path)
            dispatch (SageFsMsg.FileContentChanged(path, content))
            for sessionId in sessionsForPath path do
              onFileReloaded sessionId path
          | false -> ()
      with
      | :? System.IO.IOException -> ()
      | :? System.UnauthorizedAccessException -> ()

  let sharedDebounceTimer =
    new System.Threading.Timer(
      System.Threading.TimerCallback(debounceCallback), null,
      System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)

  let handleFileChanged (directories: string list) (e: System.IO.FileSystemEventArgs) =
    let path = e.FullPath
    // A file inside another checkout nested under the watched directory (a git
    // worktree such as .claude/worktrees/*, a vendored repo) belongs to that
    // project — feeding it to this session's live testing type-checked foreign
    // copies of the session's own files.
    let inNestedCheckout =
      directories |> List.exists (fun root -> SageFs.FileWatcher.isInNestedCheckout root path SageFs.FileWatcher.hasCheckoutMarker)
    let watchedSource =
      SageFs.FileWatcher.shouldTriggerRebuild
        { Directories = directories; Extensions = [".fs"; ".fsx"]; ExcludePatterns = []; DebounceMs = debounceMs }
        path
    match watchedSource && not inNestedCheckout with
    | true ->
      lock pendingLock (fun () ->
        // Snapshot the dir's epoch now; the fire-time guard compares against
        // it to drop events queued by a watcher generation that was stopped.
        let queuedEpoch =
          match dirForPath path with
          | Some d -> epochOf d
          | None -> 0L
        pendingPaths.TryAdd(path, true) |> ignore
        pendingEpochs.[path] <- queuedEpoch
        sharedDebounceTimer.Change(debounceMs, System.Threading.Timeout.Infinite) |> ignore)
    | false -> ()

  let startWatcher (dir: string) =
    watchers.GetOrAdd(dir, fun d ->
      let watcher = new System.IO.FileSystemWatcher(d)
      watcher.IncludeSubdirectories <- true
      // FileName too: editors that save safely (vim, JetBrains, sed -i) write a
      // temp file and rename it over the source, which is a rename, not a write.
      watcher.NotifyFilter <- System.IO.NotifyFilters.LastWrite ||| System.IO.NotifyFilters.FileName
      watcher.Filters.Add("*.fs")
      watcher.Filters.Add("*.fsx")
      let handler = handleFileChanged [d]
      watcher.Changed.Add(handler)
      watcher.Created.Add(handler)
      // A rename's FullPath is the new name — the source file that was saved.
      watcher.Renamed.Add(fun e -> handler e)
      watcher.EnableRaisingEvents <- true
      Log.info "[watcher] Registered file watcher for %s" d
      watcher, sharedDebounceTimer) |> ignore

  let stopWatcher (dir: string) =
    match watchers.TryRemove(dir) with
    | true, (watcher, _) ->
      watcher.EnableRaisingEvents <- false
      watcher.Dispose()
      // Bump the dir's epoch so any event queued by this watcher generation
      // is recognized as stale and dropped at debounce-fire time.
      dirEpochs.AddOrUpdate(dir, 1L, fun _ e -> e + 1L) |> ignore
      Log.info "[watcher] Disposed file watcher for %s" dir
    | false, _ -> ()

  /// Whether `dir` must stay watched: it is the fallback dir, or a session
  /// claims it. (The fallback watcher itself covers nested session dirs via
  /// IncludeSubdirectories, so no nesting special-case is needed here.)
  let isNeeded (dir: string) =
    let isFallback =
      match fallbackDir with
      | Some f -> System.String.Equals(System.IO.Path.GetFullPath f, System.IO.Path.GetFullPath dir, System.StringComparison.OrdinalIgnoreCase)
      | None -> false
    let hasSession =
      match dirSessions.TryGetValue(dir) with
      | true, claims -> not claims.IsEmpty
      | false, _ -> false
    isFallback || hasSession

  /// Register a watcher for a directory, attributed to a session (idempotent).
  member _.AddDirectory(dir: string, sessionId: WorkerProtocol.SessionId) =
    match System.IO.Directory.Exists(dir) with
    | false -> ()
    | true ->
      dirSessions.AddOrUpdate(
        dir,
        [sessionId],
        fun _ existing ->
          if List.contains sessionId existing then existing
          else sessionId :: existing)
      |> ignore
      startWatcher dir

  /// Remove one session's claim on a directory. The watcher is disposed only
  /// when nothing needs it anymore — a dir shared by two sessions keeps its
  /// watcher when one session stops.
  member _.RemoveDirectory(dir: string, sessionId: WorkerProtocol.SessionId) =
    let remaining =
      dirSessions.AddOrUpdate(
        dir,
        [],
        fun _ existing -> existing |> List.filter (fun s -> s <> sessionId))
    match remaining with
    | [] ->
      dirSessions.TryRemove(dir) |> ignore
      if not (isNeeded dir) then stopWatcher dir
    | _ -> ()

  /// Sync watchers to match the current sessions. Each entry is
  /// (sessionId, workingDir). One dir may host several sessions; the watcher
  /// is created once and attributes reloads to every owning session.
  member this.SyncToSessions(sessions: (WorkerProtocol.SessionId * string) list) =
    let desiredDirs = sessions |> List.map snd |> Set.ofList
    for (sessionId, dir) in sessions do
      this.AddDirectory(dir, sessionId)
    // Remove session claims for dirs that no session references anymore, then
    // dispose watchers nothing needs (isNeeded covers the fallback).
    let claimedDirs = dirSessions.Keys |> Seq.toList
    for dir in claimedDirs do
      if not (desiredDirs.Contains dir) then
        // Drop all session claims for this dir; the fallback may still keep it.
        dirSessions.TryRemove(dir) |> ignore
        if not (isNeeded dir) then stopWatcher dir
    // Watch the fallback dir (session-less) if it is not already covered by a
    // session claim or nested session dir.
    match fallbackDir with
    | Some f when System.IO.Directory.Exists(f) ->
      if not (dirSessions.ContainsKey f) then startWatcher f
    | _ -> ()

  member _.WatchedDirectories = watchers.Keys |> Seq.toList

  interface System.IDisposable with
    member _.Dispose() =
      for KeyValue(_, (w, _)) in watchers do
        w.EnableRaisingEvents <- false
        w.Dispose()
      watchers.Clear()
      dirSessions.Clear()
      sharedDebounceTimer.Dispose()

/// Cohort claim early-warning (multi-agent vision §5.1): resolve which
/// cohort member (if any) should receive an `ObserveSave` advisory for a
/// file the watcher just saw saved inside `sessionId`'s working directory,
/// plus the path made repo-relative to that session's checkout root — the
/// same "overlap is path arithmetic" rule Phase 0's
/// `CrossCheckoutOverlap.repoRelative` already applies for the cross-checkout
/// advisory. Pure: no IO, no mailbox post — the caller (the watcher's
/// `onFileReloaded` callback) decides what to do with the result and must
/// never block on it. `None` when `sessionId` has no matching `SessionInfo`
/// (shouldn't happen — the caller already routed via `sessionsForPath`) or
/// when that session isn't bound to any cohort member (`MemberRecord.Session`
/// only ever matches when `join_cohort` recorded this session) — a solo,
/// non-cohort save is unchanged behavior either way.
let resolveSaveObserver
  (sessions: WorkerProtocol.SessionInfo list)
  (members: Map<MemberTable.MemberId, Cohort.MemberRecord>)
  (sessionId: WorkerProtocol.SessionId)
  (path: string)
  : (MemberTable.MemberId * string) option =
  let sidStr = WorkerProtocol.SessionId.value sessionId
  sessions
  |> List.tryFind (fun s -> WorkerProtocol.SessionId.value s.Id = sidStr)
  |> Option.bind (fun s -> CrossCheckoutOverlap.repoRelative s.WorkingDirectory path)
  |> Option.bind (fun relPath ->
    members
    |> Map.toList
    |> List.tryFind (fun (_, record) -> record.Session = Some sidStr)
    |> Option.map (fun (m, _) -> m, relPath))

/// Get previous sessions: active from CQRS snapshot + historical from binary manifest.
let getPreviousSessions
  (manifestOwner: Features.ManifestOwner.Handle)
  (readSnapshot: unit -> SessionManager.QuerySnapshot) = task {
  let snapshot = readSnapshot()
  let activeSessions =
    SessionManager.QuerySnapshot.allSessions snapshot
    |> List.map (fun (info: WorkerProtocol.SessionInfo) ->
      { PreviousSession.Id = WorkerProtocol.SessionId.value info.Id
        PreviousSession.WorkingDir = info.WorkingDirectory
        PreviousSession.Projects = info.Projects
        PreviousSession.LastSeen = info.LastActivity })
  let activeIds = activeSessions |> List.map (fun s -> s.Id) |> Set.ofList
  let! manifest = manifestOwner.Read()
  let historicalSessions =
    match manifest with
    | Ok daemonState ->
      daemonState.Sessions
      |> Map.values
      |> Seq.filter (fun r -> r.StoppedAt.IsSome && not (activeIds.Contains r.SessionId))
      |> Seq.map (fun r ->
        { PreviousSession.Id = r.SessionId
          PreviousSession.WorkingDir = r.WorkingDir
          PreviousSession.Projects = r.Projects
          PreviousSession.LastSeen = r.StoppedAt |> Option.map (fun t -> t.DateTime) |> Option.defaultValue r.CreatedAt.DateTime })
      |> Seq.toList
    | Error _ -> []
  return activeSessions @ historicalSessions
}

/// Start dashboard web server with Brotli compression.
/// How a timer's disposal ended: its in-flight callback finished, or it did not in time.
[<RequireQualifiedAccess>]
type TimerStop =
  | Joined
  | StillRunning

/// Disposes a timer and waits for any in-flight callback to finish.
let disposeTimerAndWait (timer: System.Threading.Timer) (timeout: TimeSpan) : Task<TimerStop> =
  task {
    // Timer.Dispose(WaitHandle) signals the kernel handle directly; a
    // ManualResetEventSlim's own Wait never observes that, so wait on a kernel event.
    let finished = new System.Threading.ManualResetEvent(false)
    match timer.Dispose(finished) with
    | false ->
      finished.Dispose()
      return TimerStop.Joined
    | true ->
      let! joined = Task.Run(fun () -> finished.WaitOne(timeout))
      match joined with
      | true ->
        finished.Dispose()
        return TimerStop.Joined
      | false ->
        // The timer may still signal the event later, so it stays undisposed.
        return TimerStop.StillRunning
  }

let startDashboardServer
  (log: ILogger)
  (bindHost: SageFs.SageFsConfig.LoopbackHost)
  (ownOrigins: SageFs.Server.HttpOriginGuard.OwnOrigins)
  (dashboardPort: int)
  (endpoints: HttpEndpoint list)
  (stopping: System.Threading.CancellationToken) = task {
  try
    let builder = WebApplication.CreateBuilder()
    builder.Logging
      .AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
      .AddFilter("Microsoft.Hosting", LogLevel.Warning)
    |> ignore
    builder.Services.AddResponseCompression(fun opts ->
      opts.EnableForHttps <- true
      // The dashboard's repeated full-#main morph compresses extremely well
      // (highly repetitive markup) — payload economy belongs to the
      // transport, not to fragmenting the render into per-panel patches.
      // text/event-stream is NOT in ResponseCompressionDefaults.MimeTypes,
      // so it must be added explicitly for the SSE stream to compress at all.
      // A client that disconnects mid-stream can make the compression
      // middleware's own teardown log a warning for that one connection
      // (Kestrel isolates the failure to that connection; it does not affect
      // other sessions or the daemon process) — an acceptable, already-guarded
      // cost for compressing the dominant per-tick payload.
      opts.MimeTypes <- Seq.append ResponseCompressionDefaults.MimeTypes [ "text/event-stream" ]
      opts.Providers.Add<BrotliCompressionProvider>()
      opts.Providers.Add<GzipCompressionProvider>()
    ) |> ignore
    builder.Services.Configure<BrotliCompressionProviderOptions>(fun (opts: BrotliCompressionProviderOptions) ->
      opts.Level <- System.IO.Compression.CompressionLevel.Fastest
    ) |> ignore
    let app = builder.Build()
    app.Urls.Add(SageFs.SageFsConfig.LoopbackHost.listenUrl bindHost dashboardPort)
    app.UseResponseCompression() |> ignore
    // Origin/CSRF gate (see HttpOriginGuard): only this daemon's own pages and
    // local tooling reach the dashboard's mutating endpoints (eval, session
    // create/stop, shutdown).
    SageFs.Server.McpServer.useOriginGuard ownOrigins app
    app.UseRouting().UseFalco(endpoints) |> ignore
    log.LogInformation("Dashboard available at http://localhost:{Port}/dashboard", dashboardPort)
    do! McpServer.runUntilCancelled app stopping
  with ex ->
    log.LogWarning("Dashboard failed to start: {Error}", ex.Message)
}

/// Resume previous sessions from binary manifest.
/// Creates new sessions for each alive-but-deduplicated entry, or
/// starts bare if no previous sessions exist.
let resumePreviousSessions
  (infra: DaemonInfra)
  (sessionOps: SessionManagementOps)
  (manifestOwner: Features.ManifestOwner.Handle)
  (workingDir: string)
  (onSessionResumed: unit -> unit)
  = task {
  let log = infra.Log
  let startupSw = System.Diagnostics.Stopwatch.StartNew()
  let startupSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.startup" []

  // Load session manifest from binary — the sole source of truth
  let binarySpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.binary_manifest_load" []
  let binarySw = System.Diagnostics.Stopwatch.StartNew()
  let! manifestResult = manifestOwner.Read()
  binarySw.Stop()
  match isNull binarySpan with
  | false -> binarySpan.SetTag("binary_load_ms", binarySw.Elapsed.TotalMilliseconds) |> ignore
  | true -> ()
  // W35(R14): a corrupt manifest is renamed aside — by its owner, the only
  // component that touches the file — so saves are not blocked for this run.
  let! quarantined =
    match manifestResult with
    | Error (Features.ManifestTypes.ManifestLoadError.CorruptData _) -> manifestOwner.QuarantineCorrupt()
    | _ -> System.Threading.Tasks.Task.FromResult false

  let daemonState =
    match manifestResult with
    | Ok state ->
      log.LogInformation("Loaded session manifest from binary ({Count} sessions, {Ms:F1}ms)",
        state.Sessions.Count, binarySw.Elapsed.TotalMilliseconds)
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "binary") |> ignore
      | true -> ()
      Instrumentation.succeedSpan binarySpan
      state
    | Error Features.ManifestTypes.ManifestLoadError.NotFound ->
      // W32(R13): NotFound is an expected first-run condition → Info + succeedSpan.
      log.LogInformation("No binary manifest found — starting fresh")
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "none") |> ignore
      | true -> ()
      Instrumentation.succeedSpan binarySpan
      Features.DaemonManifest.DaemonManifestState.empty
    | Error (Features.ManifestTypes.ManifestLoadError.IoError err) ->
      // W32(R13): File EXISTS but can't be read (lock/permissions) → Warning + failSpan.
      // Old code used LogInformation+succeedSpan for ALL error cases — wrong severity.
      log.LogWarning("Binary manifest unreadable — starting fresh (HISTORY NOT RESTORED): {Error}", err)
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "error_io") |> ignore
      | true -> ()
      Instrumentation.failSpan binarySpan err
      Features.DaemonManifest.DaemonManifestState.empty
    | Error (Features.ManifestTypes.ManifestLoadError.CorruptData err) ->
      // W32(R13): File is permanently corrupt → Error + failSpan.
      // W35(R14): Rename the corrupt file so periodic saves are unblocked for this run.
      // Without rename: mergeManifestWithExisting reads daemon.sagefm → CorruptData → Error →
      // skips write → ALL new sessions lost for the daemon's entire lifetime.
      // IoError is NOT renamed (transient lock; file may recover on its own).
      log.LogError("Binary manifest corrupt — starting fresh (HISTORY NOT RESTORED): {Error}", err)
      match quarantined with
      | true -> log.LogWarning("Corrupt manifest renamed — periodic saves unblocked for this run")
      | false -> log.LogWarning("Could not rename corrupt manifest — periodic saves may be blocked")
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "error_corrupt") |> ignore
      | true -> ()
      Instrumentation.failSpan binarySpan err
      Features.DaemonManifest.DaemonManifestState.empty

  let aliveSessions = Features.DaemonManifest.DaemonManifestState.aliveSessions daemonState

  match aliveSessions.IsEmpty with
  | false ->
    // Dedup phase
    let dedupSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.session_dedup" []
    // Deduplicate by working directory + projects — resume one session per (dir, projects) pair
    let uniqueByDir =
      aliveSessions
      |> List.groupBy (fun r -> r.WorkingDir, r.Projects |> List.sort)
      |> List.map (fun (_, group) ->
        // Pick the most recently created session for each (dir, projects) pair
        group |> List.maxBy (fun r -> r.CreatedAt))
    // Mark all stale duplicates as stopped
    let staleIds =
      aliveSessions
      |> List.map (fun r -> r.SessionId)
      |> Set.ofList
    let keptIds =
      uniqueByDir |> List.map (fun r -> r.SessionId) |> Set.ofList
    let prunedCount = (Set.difference staleIds keptIds).Count
    // W14(R10): Session dedup stop-event persistence removed — binary manifest is sole source of truth.
    // Pruned sessions are no longer recorded as events.
    match prunedCount > 0 with
    | true -> Instrumentation.daemonDuplicatesPruned.Add(int64 prunedCount)
    | false -> ()
    match isNull dedupSpan with
    | false ->
      dedupSpan.SetTag("alive_count", aliveSessions.Length) |> ignore
      dedupSpan.SetTag("dedup_removed", prunedCount) |> ignore
    | true -> ()
    Instrumentation.succeedSpan dedupSpan

    log.LogInformation("Resuming {Count} previous session(s) ({Stale} stale duplicates cleaned)",
      uniqueByDir.Length, (aliveSessions.Length - uniqueByDir.Length))
    // A session whose directory or every project is gone can never start again:
    // forget it once rather than retrying (and warning) on every start. One
    // deleted project among several drops just that project.
    let decisions =
      uniqueByDir
      |> List.map (fun prev ->
        prev, Features.DaemonManifest.ResumeDecision.decide IO.Directory.Exists IO.File.Exists prev)
    for prev, decision in decisions do
      match decision with
      | Features.DaemonManifest.ResumeDecision.Forget reason ->
        log.LogWarning("Forgetting session {SessionId} for {WorkingDir}: {Reason}", prev.SessionId, prev.WorkingDir, reason)
      | Features.DaemonManifest.ResumeDecision.Resume projects when projects.Length < prev.Projects.Length ->
        let dropped = prev.Projects |> List.filter (fun p -> not (List.contains p projects))
        log.LogWarning("Resuming session for {WorkingDir} without deleted project(s): {Dropped}", prev.WorkingDir, String.concat ", " dropped)
      | Features.DaemonManifest.ResumeDecision.Resume _ -> ()
    let! forgotten =
      decisions
      |> List.choose (fun (prev, decision) ->
        match decision with
        | Features.DaemonManifest.ResumeDecision.Forget _ -> Some prev.SessionId
        | Features.DaemonManifest.ResumeDecision.Resume _ -> None)
      |> List.map (fun sessionId -> task {
        let! committed = manifestOwner.Commit (Features.DaemonManifest.ManifestMutation.Remove sessionId)
        return sessionId, committed })
      |> System.Threading.Tasks.Task.WhenAll
    for sessionId, committed in forgotten do
      match committed with
      | Ok _ -> ()
      | Error err ->
        log.LogWarning("Could not remove session {SessionId} from the manifest: {Error}", sessionId, Features.ManifestOwner.CommitError.describe err)
    let relevant =
      decisions
      |> List.choose (fun (prev, decision) ->
        match decision with
        | Features.DaemonManifest.ResumeDecision.Resume projects -> Some { prev with Projects = projects }
        | Features.DaemonManifest.ResumeDecision.Forget _ -> None)
    // Resume all valid sessions in parallel — each is an independent worker process
    let resumeSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.session_resume" []
    let resumeTasks =
      relevant
      |> List.map (fun prev -> task {
        log.LogInformation("Resuming session for {WorkingDir}", prev.WorkingDir)
        let! result = sessionOps.CreateSession prev.Projects prev.WorkingDir WorkflowTypes.SessionWorkflow.Interactive
        match result with
        | Ok info ->
          Instrumentation.daemonSessionsResumed.Add(1L)
          // W32(R13): Stop-event persistence removed — no longer tracking session stop in events.
          // Binary manifest is the sole source of truth for session state.
          log.LogInformation("Resumed session {Info} (retired old id {OldSessionId})", info, prev.SessionId)
          onSessionResumed ()
        | Error err ->
          log.LogWarning("Failed to resume session for {WorkingDir}: {Error}", prev.WorkingDir, err)
      })
    do! System.Threading.Tasks.Task.WhenAll(resumeTasks) :> System.Threading.Tasks.Task
    match isNull resumeSpan with
    | false -> resumeSpan.SetTag("resumed_count", relevant.Length) |> ignore
    | true -> ()
    Instrumentation.succeedSpan resumeSpan

    // Sessions restored — clients will discover them via listing
    // No global "active session" to restore; each client picks its own
    match daemonState.ActiveSessionId with
    | Some _ -> () // Previously tracked active session — clients resolve on connect
    | None -> ()
  | true ->
    log.LogInformation("No previous sessions to resume. Waiting for clients to create sessions")

  startupSw.Stop()
  Instrumentation.daemonStartupMs.Record(startupSw.Elapsed.TotalMilliseconds)
  Instrumentation.succeedSpan startupSpan
}

/// Create the Elm runtime with warmup context, streaming test proxy, and SSE dedup.
let createElmRuntime
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (httpClient: System.Net.Http.HttpClient)
  (stateChangedEvent: Event<SseEvent>)
  (watcherManagerRef: LiveTestWatcherManager option ref)
  (onModel: SageFsModel -> unit)
  (ct: System.Threading.CancellationToken) =
  let mutable lastStateJson = ""
  let mutable lastLoggedOutputCount = 0
  let mutable lastLoggedDiagCount = 0
  let getWarmupContextForElm (sessionId: string) : Async<SessionContext option> =
    async {
      try
        let snapshot = readSnapshot()
        match WorkerProtocol.SessionId.validate sessionId with
        | Error _ -> return None
        | Ok sidTyped ->
        match Map.tryFind sidTyped snapshot.WorkerBaseUrls with
        | Some url when url.Length > 0 ->
          use timeoutCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(5.0))
          use linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
          let! resp =
            httpClient.GetStringAsync(System.Uri(sprintf "%s/warmup-context" url), linkedCts.Token)
            |> Async.AwaitTask
          let warmup = WorkerProtocol.Serialization.deserialize<WarmupContext> resp
          let sessions = SessionManager.QuerySnapshot.allSessions snapshot
          let info = sessions |> List.tryFind (fun si -> WorkerProtocol.SessionId.value si.Id = sessionId)
          return Some {
            SessionId = sessionId
            ProjectNames =
              info |> Option.map (fun i -> i.Projects) |> Option.defaultValue []
            WorkingDir =
              info |> Option.map (fun i -> i.WorkingDirectory)
              |> Option.defaultValue ""
            Status =
              info |> Option.map (fun i -> sprintf "%A" i.Status)
              |> Option.defaultValue "Unknown"
            Warmup = warmup
            FileStatuses = []
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
            AutoOpenNamespaces = DirectoryConfig.autoOpenNamespacesForDirectory (info |> Option.map (fun i -> i.WorkingDirectory) |> Option.defaultValue "")
          }
        | _ -> return None
      with
      | :? System.IO.IOException as ex ->
        Log.error "[getWarmupContextForElm] IO error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        return None
      | :? System.Net.Http.HttpRequestException as ex ->
        Log.error "[getWarmupContextForElm] HTTP error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        return None
      | :? System.Threading.Tasks.TaskCanceledException ->
        return None
      | ex ->
        Log.error "[getWarmupContextForElm] Unexpected: %s (%s)\n%s" ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        return None
    }
  let configureWarmupAutoOpen workingDir =
    let now = DateTime.UtcNow
    let mkLine kind text = {
      Kind = kind
      Text = text
      Timestamp = now
      SessionId = ""
    }

    match DirectoryConfig.ensureAutoOpenNamespacesOptOut workingDir with
    | Ok (AutoOpenNamespacesOptOutResult.Created path) ->
      Ok (mkLine OutputKind.System (sprintf "Disabled warmup auto-open for %s (%s)" workingDir path))
    | Ok (AutoOpenNamespacesOptOutResult.AlreadyDisabled path) ->
      Ok (mkLine OutputKind.System (sprintf "Warmup auto-open is already disabled for %s (%s)" workingDir path))
    | Ok (AutoOpenNamespacesOptOutResult.RequiresManualEdit path) ->
      Ok (mkLine OutputKind.System (sprintf "Existing config needs manual edit to disable warmup auto-open: %s" path))
    | Error err ->
      Error err

  let effectDeps =
    { ElmDaemon.createEffectDeps sessionManager readSnapshot DirectoryConfig.autoOpenNamespacesForDirectory configureWarmupAutoOpen with
        GetWarmupContext = Some (fun sid -> getWarmupContextForElm (WorkerProtocol.SessionId.value sid))
        GetStreamingTestProxy = fun sid ->
          let snapshot = readSnapshot()
          match Map.tryFind sid snapshot.WorkerBaseUrls with
          | Some url when url.Length > 0 ->
            Some (HttpWorkerClient.streamingTestProxyWithCoverage Timeouts.workerHttpRead url)
          | _ -> None
        RegisterFileWatcher = fun sessionIdStr directory ->
          match !watcherManagerRef with
          | Some mgr ->
            // The effect carries a raw session-ID string (Elm model type);
            // the watcher manager attributes by typed SessionId. Session IDs
            // originate from validated snapshot state, so validate() is
            // expected to succeed.
            match WorkerProtocol.SessionId.validate sessionIdStr with
            | Ok sid -> mgr.AddDirectory(directory, sid)
            | Error _ -> Log.warn "[watcher] RegisterFileWatcher with invalid session id '%s'" sessionIdStr
          | None -> ()
        DisposeFileWatcher = fun sessionIdStr directory ->
          match !watcherManagerRef with
          | Some mgr ->
            match WorkerProtocol.SessionId.validate sessionIdStr with
            | Ok sid -> mgr.RemoveDirectory(directory, sid)
            | Error _ -> Log.warn "[watcher] DisposeFileWatcher with invalid session id '%s'" sessionIdStr
          | None -> () }
  let runtime =
    ElmDaemon.startHeadless
      effectDeps
      (fun model _regions ->
        (try onModel model
         with ex -> Log.warn "[elm] model hook threw: %s" ex.Message)
        let activeBuf = model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)
        let outputCount = activeBuf.Count
        let diagCount =
          model.Diagnostics |> Map.values |> Seq.sumBy List.length
        try
          let json = SseDedupKey.fromModel model
          match json <> lastStateJson with
          | true ->
            lastStateJson <- json
            // DIAGNOSTIC: log every state change propagation
            Log.info "[elm-OnModelChanged] output=%d diags=%d json.Length=%d" outputCount diagCount json.Length
            let significantOutputChange = abs (outputCount - lastLoggedOutputCount) >= 50
            let diagChanged = diagCount <> lastLoggedDiagCount
            match significantOutputChange || diagChanged with
            | true ->
              lastLoggedOutputCount <- outputCount
              lastLoggedDiagCount <- diagCount
              let latest =
                match activeBuf.IsEmpty with
                | true -> ""
                | false -> activeBuf.[0].Text
              Log.info "[elm] output=%d diags=%d | %s"
                outputCount diagCount latest
            | false -> ()
            // Eval-to-pixel latency chain, stage 3/5 (vision §3.4, §7.4):
            // the Elm model changed and is about to notify subscribers
            // (dashboard push agents, MCP push). Stamped here rather than
            // inside the thread-pool work item so it reflects when the
            // change was decided, not when the work item happened to run.
            EvalLatencyTrace.shared.StampModelChanged()
            System.Threading.ThreadPool.QueueUserWorkItem(fun _ ->
              stateChangedEvent.Trigger (ModelChanged (outputCount, diagCount))) |> ignore
          | false -> ()
        with ex -> Log.error "[elm] State change propagation error: %s (%s)\n%s" ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue ""))
      (fun phase msg ->
        Log.warn "[elm] 🚨 System alarm [%s]: %s" phase msg
        stateChangedEvent.Trigger (SystemAlarm (phase, msg)))
      ct
  runtime

/// Dispatch an Elm output event and wait until the target session's output
/// buffer has committed. The daemon state event is raised by OnModelChanged
/// after the model update, so this avoids returning a dashboard action while
/// its long-lived SSE stream still sees the previous output snapshot.
let dispatchOutputAndWait
  (elmRuntime: ElmRuntime<SageFsModel, SageFsMsg, RenderRegion>)
  (stateChanged: IEvent<SseEvent>)
  (sessionId: string)
  (message: SageFsMsg)
  = task {
    let beforeVersion = elmRuntime.GetModel().RecentOutput.GetBuffer(sessionId).Version
    let committed = System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
    use _subscription =
      stateChanged.Subscribe(fun _ ->
        let afterVersion = elmRuntime.GetModel().RecentOutput.GetBuffer(sessionId).Version
        match afterVersion > beforeVersion with
        | true -> committed.TrySetResult(true) |> ignore
        | false -> ())
    elmRuntime.Dispatch message
    let! completed = System.Threading.Tasks.Task.WhenAny(committed.Task, System.Threading.Tasks.Task.Delay(2000))
    return obj.ReferenceEquals(completed, committed.Task) && committed.Task.Result
  }

/// Run SageFs as a headless daemon.
/// MCP server + SessionManager + Dashboard — all frontends are clients.
/// Every session is a worker sub-process managed by SessionManager.
///
/// `ownership` (multi-agent vision §3.1, §10 item 2) is this daemon's own
/// ownership: an `--owner-pid` watchdog that exits this daemon when its
/// owner process is gone, and/or a `--ttl` that self-terminates it once
/// idle — closing the "34 orphaned daemons" seam (S1) for daemons an
/// agent, test, or demo runner spawns directly (not via a supervised
/// worker, which already has `ParentMonitor`/`OwnerMonitor`).
let run
  (bindHost: SageFs.SageFsConfig.LoopbackHost)
  (mcpPort: int)
  (flags: Args.DaemonFlags)
  (ownership: DaemonOwnership.EffectiveOwnership)
  = task {
  let startupSw = System.Diagnostics.Stopwatch.StartNew()
  let daemonStartTime = System.DateTimeOffset.UtcNow
  let startupSpan =
    Instrumentation.startSpan Instrumentation.daemonSource "sagefs.daemon.startup" [
      "daemon.port", box mcpPort
    ]
  let version = DaemonInfo.version
  let infra =
    Instrumentation.traced Instrumentation.daemonSource "sagefs.daemon.infra_create" [] (fun () ->
      createDaemonInfrastructure ())
  let log = infra.Log
  let httpClient = infra.HttpClient
  let frictionStore = infra.FrictionStore
  let daemonStreamId = infra.DaemonStreamId
  let mcpFetchTimeoutSec = infra.McpFetchTimeoutSec
  let dashboardFetchTimeoutSec = infra.DashboardFetchTimeoutSec
  let stateChangedEvent = infra.StateChangedEvent

  log.LogInformation("SageFs daemon v{Version} starting on port {Port}", version, mcpPort)

  // Handle --prune: mark all alive sessions as stopped and exit
  // W36+W42(R14): handlePrune now returns Result<bool,string> and takes Task-returning checkFn.
  // Ok true = pruned/exit, Ok false = not-requested/continue, Error msg = failed/exit with error.
  let! pruneResult = handlePrune DaemonState.SageFsDir infra.Log (fun () -> DaemonState.readAsync() |> Async.StartAsTask) flags
  match pruneResult with
  | Result.Ok true -> return ()
  | Result.Ok false -> ()
  | Result.Error msg ->
    infra.Log.LogError("Prune failed: {Error}", msg)
    return ()

  // The single owner of daemon.sagefm: every manifest write in this daemon goes through it.
  use manifestOwner = Features.ManifestOwner.start (Log.asILogger ()) DaemonState.SageFsDir

  use cts = infra.Cts

  // Ownership rule 2 (§3.1): an externally-spawned daemon with --owner-pid
  // exits when that process is gone — fenced on (pid, startTime) when
  // --owner-start was also given, so a recycled pid can never keep this
  // daemon alive forever. Same mechanism as the worker watchdog, one layer up.
  match ownership.OwnerPid with
  | Some ownerPid ->
    let owner : OwnerMonitor.Owner = { Pid = ownerPid; StartTimeTicks = ownership.OwnerStartTicks }
    log.LogInformation("Daemon monitoring owner PID {OwnerPid} (fenced={Fenced})", ownerPid, Option.isSome ownership.OwnerStartTicks)
    Async.Start(
      OwnerMonitor.run OwnerMonitor.getProcessById owner cts (fun msg -> log.LogWarning("{Message}", msg)),
      cts.Token)
  | None -> ()
  // Test discovery callback — set after elmRuntime is created
  let mutable onTestDiscoveryCallback : (WorkerProtocol.SessionId -> SessionManager.TestDiscoveryReport -> unit) =
    fun _ _ -> ()
  let mutable onInstrumentationMapsCallback : (WorkerProtocol.SessionId -> Features.LiveTesting.InstrumentationMap array -> unit) =
    fun _ _ -> ()
  let mutable onWarmupProgressCallback : (string -> string -> unit) =
    fun _ _ -> ()

  // Create SessionManager — the single source of truth for all sessions
  // Returns (mailbox, readSnapshot) — CQRS: reads go to snapshot, writes to mailbox
  let sessionManager, readSnapshot =
    SessionManager.create cts.Token
      (fun () -> stateChangedEvent.Trigger SessionProgress)
      (fun sid report -> onTestDiscoveryCallback sid report)
      (fun sid maps -> onInstrumentationMapsCallback sid maps)
      (fun sid -> stateChangedEvent.Trigger (SessionReady sid))
      (fun sid progress -> onWarmupProgressCallback (WorkerProtocol.SessionId.value sid) progress)
      (fun sid error -> stateChangedEvent.Trigger (SessionFaulted (sid, error)))

  let sessionOps = createSessionOps sessionManager readSnapshot manifestOwner
  // String-to-SessionId adapters for proxyToSession (which takes string callbacks)
  let getProxyStr s = sessionOps.GetProxy (toSessionId s)
  let notifyWorkerDiedStr s = sessionOps.NotifyWorkerDied (toSessionId s)

  let noResume = flags.NoResume

  let workingDir = Environment.CurrentDirectory

  // resumeSessions delegates to module-level function with captured infra
  let resumeSessions onSessionResumed =
    resumePreviousSessions infra sessionOps manifestOwner workingDir onSessionResumed

  // Create EffectDeps from SessionManager + start Elm loop
  let watcherManagerRef = ref (None: LiveTestWatcherManager option)
  // Wakes the idle live-testing tick when a model change queues a debounce;
  // assigned once the tick timer exists (below).
  let wakeLiveTestTick : (SageFsModel -> unit) ref = ref ignore
  let elmRuntime = createElmRuntime sessionManager readSnapshot httpClient stateChangedEvent watcherManagerRef (fun model -> wakeLiveTestTick.Value model) cts.Token

  // The single owner of this daemon's implicit cohort (cohort-integration-plan.md
  // Slice 2, D1/D2/D4/D5): holds the live CohortState, appends every applied
  // command to a SQLite ledger, and publishes the projected CohortFrame
  // wait-free for reads (Claims v1 MCP tools, McpTools.fs). Constructed here,
  // AFTER `elmRuntime` (not alongside `manifestOwner` above), because item
  // 13c's `getSessionTestOutcomes` closure reads `elmRuntime.GetModel()` —
  // there is nothing to close over any earlier in this function.
  //
  // item 13a left this hardcoded to `[||]`/no outcomes because no
  // session->member mapping existed. Item 13c closed that gap: `join_cohort`
  // (Mcp.fs) now resolves and records the caller's session on `MemberRecord`,
  // so `CohortOwner.frameOf` can build its own `SessionSnapshot[]` straight
  // off `head.State.Members` — this closure only needs to answer "what are
  // THIS session's Pass/Fail/Stale tests and generation", the exact question
  // `Features.CohortTestProjection.projectSession` (item 13a) already
  // answers from a `LiveTestState`, filtered the SAME way
  // `GetSessionTestSummary` below filters it
  // (`LiveTestState.statusEntriesForSession`).
  let getCohortSessionTestOutcomes (sessionId: string) : Features.CohortOwner.SessionTestOutcomes =
    let state = (SageFsModel.cycleForSession sessionId (elmRuntime.GetModel())).TestState
    let projection, generation = Features.CohortTestProjection.projectSession sessionId state
    projection.PassingTests, projection.FailingTests, projection.StaleTests, generation

  // The real landing performer (item 14c — this is the slice that makes
  // request_landing actually run git and tests instead of parking forever
  // in Rebasing/Verifying against `LandingPerformer.stub`). Every field
  // reads the daemon-held integration binding
  // (`McpTools.cohortIntegrationRef`, written by the `set_integration_ref`
  // MCP tool) fresh on every call, so a landing dispatched before
  // `set_integration_ref` has ever run — or after a restart, since the
  // binding is process-local, see its own doc comment — fails closed with
  // an explanatory reason instead of silently doing nothing.
  let cohortToLiveTestId (Cohort.TestId t) : Features.LiveTesting.TestId =
    Features.LiveTesting.TestId.TestId t

  let cohortIntegrationNotConfigured () : string list =
    [ "integration not configured — call set_integration_ref first" ]

  // The performer is honest: `Rebase` returns the REAL rebased head and
  // `FastForward` targets it. `Cohort.decide`'s `LandingState.Verifying` now
  // carries `base` and `rebasedHead` separately, so its land-time HeadMoved
  // guard compares the BASE against IntegrationHead (Property 11) while
  // FastForward targets the rebased head — no echo/re-read workaround needed.
  // Daemon-held content-addressed test-result cache for landing verification
  // (§5.4). Accessed only from the RunTests performer below, which the
  // CohortOwner runs serially through its single landing queue — so a plain ref
  // is safe (no concurrent landings). A test is cached only when its coverage
  // gives a trustworthy InputHash (see the RunTests wiring); a same-input test
  // on a later landing is then skipped instead of re-run.
  let cohortLandingCache = ref (Features.LiveTesting.TestResultCache.empty)

  let cohortLandingPerformer : Features.CohortOwner.LandingPerformer<MemberTable.MemberId> =
    { Rebase = fun _landingId onto ->
        async {
          match McpTools.cohortIntegrationRef.Value with
          | None -> return Error(cohortIntegrationNotConfigured ())
          | Some binding ->
            let! result = Features.CohortGit.rebase binding.WorktreePath onto
            match result with
            | Ok realNewHead -> return Ok realNewHead
            | Error files -> return Error files
        }
      // v1 conservative (design decision, sagefs-multiagent-vision.md item
      // 14c): every test discovered in the integration session, never a
      // diff-narrowed subset — running everything is always correct, if not
      // maximally fast. `baseSha`/`headSha` are now genuinely distinct (real
      // rebase base vs rebased head), so a precise coverage-based affected-set
      // via `CohortGit.diffNames baseSha headSha` is a viable later
      // optimization — deferred, not this item.
      ComputeAffected = fun _landingId _baseSha _headSha ->
        async {
          match McpTools.cohortIntegrationRef.Value with
          | None -> return []
          | Some { SessionId = None } -> return []
          | Some { SessionId = Some sessionId } ->
            let state = (SageFsModel.cycleForSession sessionId (elmRuntime.GetModel())).TestState
            let entries = Features.LiveTesting.LiveTestState.statusEntriesForSession sessionId state
            return entries |> Array.map (fun e -> Features.CohortTestProjection.toCohortTestId e.TestId) |> Array.toList
        }
      // Item 14d's documented caveat: `CohortLandingVerify.runTestsInSession`
      // only produces a trustworthy verdict for the session live-testing
      // tracks as Primary (`model.LiveTesting`) — `RunTestsRequested` always
      // mutates that cycle, never `PerSessionLiveTesting`. So the integration
      // session is made Primary (the same `SessionSwitched` event the
      // dashboard/editor clients dispatch on a session switch, DaemonMode.fs
      // elsewhere) immediately before every run this performer makes.
      RunTests = fun _landingId tests ->
        async {
          match McpTools.cohortIntegrationRef.Value with
          | None -> return tests
          | Some { SessionId = None } -> return tests
          | Some { SessionId = Some sessionId } ->
            elmRuntime.Dispatch(SageFsMsg.Event(TuiEvent.SessionSwitched(None, sessionId)))
            let! sessionInfo = sessionOps.GetSessionInfo(toSessionId sessionId) |> Async.AwaitTask
            let observation: Features.Verification.SessionTrust.SessionObservation =
              { MatchingSessionIds = [ sessionId ]
                SessionStatus = sessionInfo |> Option.map (fun s -> s.Status)
                LoadedState = None
                TypeIdentityDiagnostic = None }
            let liveTests = tests |> List.map cohortToLiveTestId
            // Content-addressed cache lookup (§5.4). Compute each test's
            // InputHash from the integration session's OWN coverage: the merged
            // instrumentation map + the test's coverage bitmap. A test is
            // trustworthy-hashable only when it has a bitmap whose size matches
            // the current instrumentation (a stale/absent bitmap → None → the
            // test always runs and is never cached, so a source change can never
            // be masked by a stale skip). LandingCache.verify then runs only the
            // cache misses (+ untrusted tests) and records the trustworthy ones.
            let lt = SageFsModel.cycleForSession sessionId (elmRuntime.GetModel())
            let maps =
              match Map.tryFind sessionId lt.InstrumentationMaps with
              | Some m when m.Length > 0 -> m
              | _ -> lt.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
            let merged = Features.LiveTesting.InstrumentationMap.merge maps
            let fileReader (path: string) =
              try Some(System.IO.File.ReadAllText path) with _ -> None
            let inputHashOf (tid: Features.LiveTesting.TestId) : string option =
              match merged.Slots.Length with
              | 0 -> None
              | _ ->
                match Map.tryFind tid lt.TestState.TestCoverageBitmaps with
                | Some bm when bm.Count = merged.TotalProbes && bm.Count > 0 ->
                  Some(Features.LiveTesting.InputHashCoverage.ofCoverage fileReader merged bm)
                | _ -> None
            let runMisses toRun =
              Features.CohortLandingVerify.runTestsInSession elmRuntime observation sessionId toRun
            let! newCache, result =
              Features.LiveTesting.LandingCache.verify cohortLandingCache.Value inputHashOf sessionId liveTests runMisses
            cohortLandingCache.Value <- newCache
            match result with
            | Ok failing -> return failing |> List.map Features.CohortTestProjection.toCohortTestId
            | Error reason ->
              // Fail-closed (CohortLandingVerify's own doctrine): a session
              // that can't be trusted enough to run tests in must never read
              // as "all passed" — treat every requested test as failing so
              // the landing blocks with FailingTests rather than landing on
              // an unverified session.
              Log.warn "[cohort-landing] RunTests refused for session %s: %s — treating all %d requested test(s) as failing (fail-closed)" sessionId reason tests.Length
              return tests
        }
      // `toSha` is `decide`'s FastForward target = the landing's `rebasedHead`
      // (the real commit `Rebase` returned, now that `Verifying` carries base
      // and rebasedHead separately). Fast-forward the integration branch to it.
      FastForward = fun _landingId toSha ->
        async {
          match McpTools.cohortIntegrationRef.Value with
          | None -> return Error "integration not configured — call set_integration_ref first"
          | Some binding -> return! Features.CohortGit.fastForwardBranch workingDir binding.Branch toSha
        }
      Notify = fun who event ->
        Log.info "[cohort-landing] notify %s: %A" (MemberTable.MemberId.display who) event
    }

  use cohortOwner =
    Features.CohortOwner.startWithPerformer
      (Log.asILogger ())
      (Features.CohortLedgerSqlite.Sqlite.create (System.IO.Path.Combine(DaemonState.SageFsDir, "cohort.ledger.db")))
      (fun () -> System.DateTime.UtcNow)
      Features.CohortOwner.productionEntropy
      getCohortSessionTestOutcomes
      cohortLandingPerformer

  // Create a diagnostics-changed event (aggregated from workers)
  let diagnosticsChanged = Event<Features.DiagnosticsStore.T>()

  // Partially applied worker helpers (capture httpClient + readSnapshot)
  let getWorkerBaseUrl = getWorkerBaseUrl readSnapshot
  let fetchWorkerEndpoint sessionId path timeout parse =
    fetchWorkerEndpoint httpClient readSnapshot sessionId path timeout parse

  // Warmup context fetcher for MCP — uses session manager to find worker URL
  let getWarmupContextForMcp (sessionId: WorkerProtocol.SessionId) : System.Threading.Tasks.Task<WarmupContext option> =
    fetchWorkerEndpoint sessionId "/warmup-context" mcpFetchTimeoutSec
      (WorkerProtocol.Serialization.deserialize<WarmupContext>)

  // Hotreload state fetcher for MCP — returns watched file paths
  let getHotReloadStateForMcp (sessionId: WorkerProtocol.SessionId) : System.Threading.Tasks.Task<string list option> =
    fetchWorkerEndpoint sessionId "/hotreload" mcpFetchTimeoutSec (fun resp ->
      use doc = System.Text.Json.JsonDocument.Parse(resp)
      doc.RootElement.GetProperty("files").EnumerateArray()
      |> Seq.filter (fun f -> f.GetProperty("watched").GetBoolean())
      |> Seq.map (fun f -> f.GetProperty("path").GetString())
      |> Seq.toList)

  // Wire test discovery from SessionManager → Elm model
  onTestDiscoveryCallback <- handleTestDiscovery readSnapshot workingDir log elmRuntime.Dispatch

  // Wire instrumentation maps from SessionManager → Elm model
  onInstrumentationMapsCallback <- fun sid maps ->
    elmRuntime.Dispatch(SageFsMsg.Event (TuiEvent.InstrumentationMapsReady (WorkerProtocol.SessionId.value sid, maps)))

  // Wire warmup progress from SessionManager → Elm model + SSE broadcast
  onWarmupProgressCallback <- fun sid progress ->
    handleWarmupProgress elmRuntime.Dispatch sid progress
    match tryParseWarmupProgress progress with
    | Some (step, total, msg) ->
      match WorkerProtocol.SessionId.validate sid with
      | Ok sidTyped -> stateChangedEvent.Trigger(WarmupProgress(sidTyped, step, total, msg))
      | Error _ -> ()
    | None -> ()

  // W12(R10): Use Volatile.Read/Write to ensure MCP-thread writes are visible to HTTP-thread
  // readers without data races. Plain ref cell field access has no memory barrier on ARM.
  let sharedBindingScope : SageFs.Features.BindingExplorer.BindingScopeSnapshot option ref = ref None
  // Shared feature push state — gives Dashboard access to EvalTimeline sparkline data.
  let sharedFeatureState : SageFs.Features.FeatureHooks.FeaturePushState ref = ref SageFs.Features.FeatureHooks.FeaturePushState.empty
  // Adaptive live-bindings store — per-session cval cells updated after each eval;
  // subscribers fire only when the snapshot actually changed (FSharp.Data.Adaptive).
  let liveBindingsAdaptive = SageFs.Features.LiveBindingsAdaptive.create ()

  // Permanent binding-scope subscriber — updates sharedBindingScope on every eval completion
  // regardless of MCP SSE client connectivity. Fixes the dashboard "0 bindings" problem when
  // no editor client is connected. W12(R10): Volatile.Write for ARM memory barrier.
  let lastBindingOutputCount = ref -1
  let _bindingScopeSubscription =
    stateChangedEvent.Publish.Subscribe(fun change ->
      match change with
      | SseEvent.ModelChanged (outputCount, _) when outputCount <> lastBindingOutputCount.Value ->
        lastBindingOutputCount.Value <- outputCount
        let model = elmRuntime.GetModel()
        // Use GetActiveBuffer (not GetBuffer) to handle AwaitingSession → staging buffer case.
        // GetBuffer(sessionId) returns empty if session not found; GetActiveBuffer uses staging as fallback.
        let activeBuf = model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)
        let rawOutput =
          activeBuf.FilterToList(fun o -> o.Kind = OutputKind.Result)
          |> List.rev
          |> List.map (fun o -> o.Text)
          |> String.concat "\n"
        let newScope = SageFs.Features.BindingExplorer.fromRawOutput rawOutput
        System.Threading.Volatile.Write(&sharedBindingScope.contents, newScope)
      | _ -> ())

  // Create the multi-agent coordination tracker (in-memory, daemon-lifetime)
  let activityTracker = AgentActivityTracker.create ()

  // Both listeners share one origin set: the dashboard page (mcpPort + 1, see
  // dashboardPort below) calls MCP-port endpoints as a same-site own origin.
  let daemonOrigins = SageFs.Server.HttpOriginGuard.OwnOrigins.ofPorts [ mcpPort; mcpPort + 1 ]

  let mcpTask =
    McpServer.startMcpServer {
      DiagnosticsChanged = diagnosticsChanged.Publish
      StateChanged = Some stateChangedEvent.Publish
      FrictionStore = frictionStore
      Port = mcpPort
      BindHost = bindHost
      OwnOrigins = daemonOrigins
      SessionOps = sessionOps
      ElmRuntime = Some elmRuntime
      GetWarmupContext = Some (fun (sidStr: string) ->
        match WorkerProtocol.SessionId.validate sidStr with
        | Ok sid -> getWarmupContextForMcp sid
        | Error _ -> Threading.Tasks.Task.FromResult None)
      GetHotReloadState = Some (fun (sidStr: string) ->
        match WorkerProtocol.SessionId.validate sidStr with
        | Ok sid -> getHotReloadStateForMcp sid
        | Error _ -> Threading.Tasks.Task.FromResult None)
      SharedBindingScope = sharedBindingScope
      SharedFeatureState = Some sharedFeatureState
      ActivityTracker = activityTracker
      LiveSnapshotSink = Some (fun sid snap ->
        SageFs.Features.LiveBindingsAdaptive.update liveBindingsAdaptive sid snap)
      CohortOwner = Some cohortOwner
    } cts.Token

  let liveTestTickMs = 25

  // Test cycle tick timer — drives debounce channels for live testing.
  // Keep this comfortably below the 50ms tree-sitter debounce so as-you-type
  // feedback can land near the intended debounce threshold instead of being
  // quantized up to an outer 200ms polling loop.
  // Elmish-style batching means rapid ticks coalesce: N ticks → N updates → 1 render
  // Idle-down: when NO session is actively live-testing the timer switches to a
  // 1s heartbeat instead of a constant 40Hz scan (roast queue item 6). The
  // one-shot reschedule pattern (same as cacheSaveTimer) prevents reentrancy.
  let mutable testCycleTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  // The tick only fires debounces, so it runs fast exactly while one is pending
  // (needsLiveTestTick) and otherwise idles on a slow heartbeat. It used to
  // run at 40 Hz for as long as any test had ever been discovered. A model
  // change that queues a debounce wakes an idle timer at once, so a save is
  // never held back by the heartbeat. 1 = idling on the heartbeat.
  let testCycleIdle = ref 0
  let rescheduleTestCycle (periodMs: int) =
    if not (isNull testCycleTimerRef) then
      try testCycleTimerRef.Change(periodMs, System.Threading.Timeout.Infinite) |> ignore
      with :? System.ObjectDisposedException -> ()
  let testCycleCallback _ =
    try
      let model = elmRuntime.GetModel()
      let periodMs =
        match SageFsModel.needsLiveTestTick model with
        | true ->
          System.Threading.Volatile.Write(&testCycleIdle.contents, 0)
          elmRuntime.Dispatch(SageFsMsg.TestCycleTick DateTimeOffset.UtcNow)
          liveTestTickMs
        | false ->
          System.Threading.Volatile.Write(&testCycleIdle.contents, 1)
          // Re-check after going idle: a debounce queued since the read above
          // found the timer not yet idle, so its wake was a no-op.
          match SageFsModel.needsLiveTestTick (elmRuntime.GetModel()) with
          | true ->
            System.Threading.Volatile.Write(&testCycleIdle.contents, 0)
            liveTestTickMs
          | false -> 1000
      rescheduleTestCycle periodMs
    with ex ->
      log.LogWarning("TestCycleTimer callback threw unexpectedly: {Error}", ex.Message)
  let testCycleTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(testCycleCallback),
      null, liveTestTickMs, System.Threading.Timeout.Infinite)
    testCycleTimerRef <- t
    t
  // Wake only from idle: re-arming on every model change would keep pushing
  // the fire time back under a steady stream of output events.
  wakeLiveTestTick.Value <- fun model ->
    match SageFsModel.needsLiveTestTick model
          && System.Threading.Interlocked.CompareExchange(&testCycleIdle.contents, 0, 1) = 1 with
    | true -> rescheduleTestCycle liveTestTickMs
    | false -> ()

  // Periodic test cache save — crash recovery for test results.
  // Fires every 60s, only writes when RunGeneration has advanced since last save.
  // W11(R10): Use a one-shot timer that reschedules AFTER completion to prevent reentrancy.
  // A periodic timer with System.Threading.Timer fires on ThreadPool; if the callback takes
  // >60s, two threads both write to the same .tmp file → corrupt save file.
  // One-shot semantics: the next tick is scheduled only after the current tick finishes.
  let lastSavedGeneration = ref 0
  let mutable cacheSaveTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let cacheSaveCallback _ =
    try
      // W27(R12): Inner try/with catches unexpected exceptions from callees and logs them.
      // The outer try/finally ensures rescheduling always runs, even on catastrophic failure.
      // Without this, an unhandled exception would propagate to the ThreadPool and kill the process.
      try
        periodicCacheSave log readSnapshot elmRuntime.GetModel lastSavedGeneration
        periodicManifestSave log manifestOwner readSnapshot elmRuntime.GetModel
      with ex ->
        log.LogWarning("Periodic cache/manifest save threw unexpectedly: {Error}", ex.Message)
    finally
      // Reschedule for the next period only after this run finishes — prevents concurrent runs.
      // W18(R11): Guard ObjectDisposedException — if Dispose() was called during this callback
      // (shutdown race), the Change() call throws ODE. The null check guards the startup window
      // only; the ODE guard handles the shutdown window.
      if not (isNull cacheSaveTimerRef) then
        try cacheSaveTimerRef.Change(60_000, System.Threading.Timeout.Infinite) |> ignore
        with :? System.ObjectDisposedException -> ()
  let cacheSaveTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(cacheSaveCallback),
      null, 60_000, System.Threading.Timeout.Infinite)
    cacheSaveTimerRef <- t
    t

  // Periodic agent activity cleanup — evicts stale entries from the activity tracker.
  // One-shot timer pattern (same as cache save) to prevent reentrancy.
  let mutable activityCleanupTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let activityCleanupCallback _ =
    try
      let outcome = AgentActivityTracker.cleanup activityTracker (TimeSpan.FromMinutes 5.0) DateTime.UtcNow
      match outcome with
      | SessionOperations.OccupancyCleanupOutcome.EvictedStale agents ->
        log.LogInformation("Agent cleanup: evicted stale agents: {Agents}", String.concat ", " agents)
      | _ -> ()
    finally
      if not (isNull activityCleanupTimerRef) then
        try activityCleanupTimerRef.Change(60_000, System.Threading.Timeout.Infinite) |> ignore
        with :? System.ObjectDisposedException -> ()
  let activityCleanupTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(activityCleanupCallback),
      null, 60_000, System.Threading.Timeout.Infinite)
    activityCleanupTimerRef <- t
    t

  // Live testing file watcher manager — per-session directory watchers.
  // onFileReloaded receives only REAL session IDs: the daemon-CWD fallback
  // watcher claims no session, so files it sees never fire FileReloaded (a
  // path with no owning session must not be attributed to a fabricated one).
  let liveTestWatcherManager =
    new LiveTestWatcherManager(
      elmRuntime.Dispatch,
      (fun sessionId path ->
        stateChangedEvent.Trigger (FileReloaded (sessionId, path))
        // Cohort claim early-warning (multi-agent vision §5.1): a save inside
        // another cohort member's claimed scope is advisory, never blocking
        // (`Cohort.decide`'s `ObserveSave` never refuses) — so this is a
        // fire-and-forget `Post`, never awaited, and never on the hot watcher
        // path for a solo/non-cohort session (`resolveSaveObserver` returns
        // `None` immediately for those).
        match
          resolveSaveObserver
            (SessionManager.QuerySnapshot.allSessions (readSnapshot ()))
            (cohortOwner.ReadCohortState().Members)
            sessionId
            path
        with
        | Some(observer, relPath) ->
          cohortOwner.Post(Cohort.CohortCommand.ObserveSave(observer, relPath), ignore)
        | None -> ()),
      Some workingDir)
  watcherManagerRef := Some liveTestWatcherManager
  // Seed with any existing session directories (fallback dir handled by the
  // manager's fallbackDir).
  let seedSessionDirs () =
    let snapshot = readSnapshot()
    let sessions = SessionManager.QuerySnapshot.allSessions snapshot
    let sessionDirPairs =
      sessions
      |> List.map (fun si -> si.Id, si.WorkingDirectory)
    liveTestWatcherManager.SyncToSessions(sessionDirPairs)
  seedSessionDirs ()

  // Periodic session-watcher sync — ensures new sessions get watchers
  let mutable watcherSyncTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let watcherSyncCallback _ =
    try
      seedSessionDirs ()
      // Sweep stale adaptive-store + feature-push state for sessions that no
      // longer exist (roast queue item 2). Idempotent — cheap at idle.
      let liveIds =
        SessionManager.QuerySnapshot.allSessions (readSnapshot())
        |> List.map (fun si -> WorkerProtocol.SessionId.value si.Id)
        |> Set.ofList
      match sweepStaleSessionState liveIds liveBindingsAdaptive sharedFeatureState with
      | [] -> ()
      | stale -> log.LogInformation("Swept {Count} stale session state entries", stale.Length)
    finally
      match isNull watcherSyncTimerRef with
      | true -> ()
      | false ->
        try watcherSyncTimerRef.Change(5_000, System.Threading.Timeout.Infinite) |> ignore
        with :? System.ObjectDisposedException -> ()
  let watcherSyncTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(watcherSyncCallback),
      null, 5_000, System.Threading.Timeout.Infinite)
    watcherSyncTimerRef <- t
    t

  // Start dashboard web server on MCP port + 1
  let dashboardPort = mcpPort + 1
  let connectionTracker = ConnectionTracker()

  // TTL idle-check (ownership rule 2, §3.1): when this daemon carries a
  // --ttl (explicit, or the nested-checkout default), self-terminate once
  // there are no live sessions and no MCP/SSE clients for at least that
  // long. "Clients" is approximated from what the daemon already tracks —
  // connected dashboard tabs (ConnectionTracker) and recent MCP tool
  // activity (AgentActivityTracker) — rather than a new connection count.
  // One-shot timer, same reschedule-after-completion idiom as the other
  // periodic checks above.
  let mutable ttlLastActiveAt = DateTime.UtcNow
  let mutable ttlTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let ttlTimer : System.Threading.Timer option =
    match ownership.Ttl with
    | None -> None
    | Some ttl ->
      let ttlCheckIntervalMs =
        max 1_000 (min 30_000 (int (ttl.TotalMilliseconds / 4.0)))
      let ttlCallback _ =
        try
          try
            let now = DateTime.UtcNow
            let hasLiveSessions =
              SessionManager.QuerySnapshot.allSessions (readSnapshot()) |> List.isEmpty |> not
            let hasClients =
              connectionTracker.GetAllCounts().Browsers > 0
              || not (AgentActivityTracker.getActivePresences activityTracker None ttl now |> List.isEmpty)
            match hasLiveSessions || hasClients with
            | true -> ttlLastActiveAt <- now
            | false -> ()
            match DaemonOwnership.shouldSelfTerminate now ttl ttlLastActiveAt hasLiveSessions hasClients with
            | true ->
              log.LogWarning("Daemon idle for --ttl {Ttl} with no live sessions and no clients — self-terminating", ttl)
              try cts.Cancel() with :? ObjectDisposedException -> ()
            | false -> ()
          with ex ->
            log.LogWarning("TTL idle-check callback threw unexpectedly: {Error}", ex.Message)
        finally
          if not (isNull ttlTimerRef) then
            try ttlTimerRef.Change(ttlCheckIntervalMs, System.Threading.Timeout.Infinite) |> ignore
            with :? System.ObjectDisposedException -> ()
      let t =
        new System.Threading.Timer(
          System.Threading.TimerCallback(ttlCallback),
          null, ttlCheckIntervalMs, System.Threading.Timeout.Infinite)
      ttlTimerRef <- t
      Some t

  // Dashboard status helpers — partially applied module-level functions
  let getSessionState = getSessionStateFromSnapshot readSnapshot
  let getEvalStatsAsync = getEvalStatsFromWorker httpClient readSnapshot
  let getSessionWorkingDir = getSessionWorkingDirFromSnapshot readSnapshot
  let getStatusMsg = getStatusMsgFromSnapshot readSnapshot

  let sessionThemes = DashboardTypes.loadThemes DaemonState.SageFsDir

  let dashboardQueries : DashboardQueries = {
    GetSessionState = getSessionState
    GetStatusMsg = getStatusMsg
    GetEvalStats = getEvalStatsAsync
    GetFrictionStore = fun () -> task { return frictionStore }
    GetSessionWorkingDir = getSessionWorkingDir
    GetElmRegionsForSession = fun sessionId ->
      ElmDaemon.renderRegionsForSession elmRuntime (WorkerProtocol.SessionId.value sessionId) |> Some
    GetPreviousSessions = fun () ->
      getPreviousSessions manifestOwner readSnapshot
    GetAllSessions = fun () -> task { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    GetHotReloadState = fun sessionId ->
      fetchWorkerEndpoint sessionId "/hotreload" dashboardFetchTimeoutSec (fun resp ->
        use doc = Text.Json.JsonDocument.Parse(resp)
        let root = doc.RootElement
        let files =
          root.GetProperty("files").EnumerateArray()
          |> Seq.map (fun el ->
            {| path = el.GetProperty("path").GetString()
               watched = el.GetProperty("watched").GetBoolean() |})
          |> Seq.toList
        let watchedCount = root.GetProperty("watchedCount").GetInt32()
        {| files = files; watchedCount = watchedCount |})
    GetWarmupContext = fun sessionId ->
      fetchWorkerEndpoint sessionId "/warmup-context" dashboardFetchTimeoutSec
        (WorkerProtocol.Serialization.deserialize<WarmupContext>)
    GetWarmupProgress = fun sessionId ->
      let snapshot = readSnapshot()
      match Map.tryFind sessionId snapshot.WarmupProgress with
      | Some progress -> progress
      | None -> ""
    GetSessionTestSummary = fun sessionId ->
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let lt = SageFsModel.cycleForSession sidStr (elmRuntime.GetModel())
      let state = lt.TestState
      match state.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> None
      | _ ->
      let entries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession sidStr state
      match entries.Length with
      | 0 -> None
      | _ ->
        Features.LiveTesting.TestSummary.fromStatuses
          state.Activation (entries |> Array.map (fun e -> e.Status))
        |> Some
    GetSessionCoverageSummary = fun sessionId ->
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let lt = SageFsModel.cycleForSession sidStr (elmRuntime.GetModel())
      let state = lt.TestState
      match state.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> None
      | _ ->
      // `state` belongs wholly to this session (see `SageFsModel.cycleForSession`).
      let bitmaps = state.TestCoverageBitmaps |> Map.values |> Seq.toArray
      match bitmaps.Length with
      | 0 -> None
      | _ ->
        Features.LiveTesting.CoverageSummary.fromBitmaps 16 (bitmaps |> Seq.ofArray)
        |> Some
    GetSessionTestTreemap = fun sessionId ->
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let lt = SageFsModel.cycleForSession sidStr (elmRuntime.GetModel())
      let state = lt.TestState
      match state.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> [||]
      | _ ->
      let entries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession sidStr state
      Features.LiveTesting.TestTreemap.fromStatusEntries entries
    GetSessionCoverageTreemap = fun sessionId ->
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let lt = SageFsModel.cycleForSession sidStr (elmRuntime.GetModel())
      match lt.TestState.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> None
      | _ ->
      let maps =
        match Map.tryFind sidStr lt.InstrumentationMaps with
        | Some m when m.Length > 0 -> m
        | _ -> lt.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
      let merged = Features.LiveTesting.InstrumentationMap.merge maps
      match merged.Slots.Length = 0 with
      | true -> None
      | false ->
      // `lt.TestState` belongs wholly to this session (see `SageFsModel.cycleForSession`).
      let sessionTestIds = lt.TestState.TestCoverageBitmaps |> Map.keys |> Set.ofSeq
      let compatibleBitmap (tid: Features.LiveTesting.TestId) =
        Map.tryFind tid lt.TestState.TestCoverageBitmaps
        |> Option.filter (fun bm -> bm.Count = merged.TotalProbes)
      let sessionBitmaps = sessionTestIds |> Seq.choose compatibleBitmap |> Seq.toArray
      match sessionBitmaps.Length = 0 with
      | true -> None
      | false ->
      let combinedHits =
        sessionBitmaps
        |> Array.reduce (fun acc bm ->
          { acc with Bits = Array.init acc.Bits.Length (fun i -> acc.Bits.[i] ||| bm.Bits.[i]) })
        |> Features.LiveTesting.CoverageBitmap.toBoolArray
      let entries = Features.LiveTesting.LiveTestState.statusEntriesForSession sidStr lt.TestState
      let failedTestIds =
        entries
        |> Array.choose (fun e ->
          match e.Status with
          | Features.LiveTesting.TestRunStatus.Failed _ -> Some e.TestId
          | _ -> None)
        |> Set.ofArray
      let passedTestIds =
        entries
        |> Array.choose (fun e ->
          match e.Status with
          | Features.LiveTesting.TestRunStatus.Passed _ -> Some e.TestId
          | _ -> None)
        |> Set.ofArray
      // Probes hit by any failing test — these taint their file/symbol red
      // even where the merged (all-tests) coverage otherwise looks green.
      let failingSlotIndices =
        sessionTestIds
        |> Seq.filter failedTestIds.Contains
        |> Seq.choose compatibleBitmap
        |> Seq.collect (fun bm ->
          seq { for i in 0 .. merged.Slots.Length - 1 do
                  if Features.LiveTesting.CoverageBitmap.isSet i bm then yield i })
        |> Set.ofSeq
      let projectDirs =
        match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
        | None -> []
        | Some info ->
          info.Projects
          |> List.map (fun p ->
            System.IO.Path.GetFileNameWithoutExtension p, System.IO.Path.GetDirectoryName p)
      let facts =
        merged.Slots
        |> Array.indexed
        |> Array.groupBy (fun (_, sp) -> sp.File)
        |> Array.map (fun (file, idxSps) ->
          let probeCount = idxSps.Length
          let coveredCount = idxSps |> Array.filter (fun (i, _) -> combinedHits.[i]) |> Array.length
          let hasFailing = idxSps |> Array.exists (fun (i, _) -> failingSlotIndices.Contains i)
          let projectName = Features.Treemap.CoverageTreemapNode.projectNameForFile projectDirs file
          let symbols =
            lt.AnalysisCache.FileSymbols
            |> Map.tryFind file
            |> Option.defaultValue []
            |> List.map (fun sr ->
              let testIds =
                lt.DepGraph.SymbolToTests |> Map.tryFind sr.SymbolFullName |> Option.defaultValue [||]
              ({ SymbolName = sr.SymbolFullName
                 Line = sr.Line
                 TestCount = testIds.Length
                 PassingCount = testIds |> Array.filter passedTestIds.Contains |> Array.length
                 FailingCount = testIds |> Array.filter failedTestIds.Contains |> Array.length }
               : Features.Treemap.SymbolCoverageFact))
          ({ FilePath = file
             ProjectName = projectName
             ProbeCount = probeCount
             CoveredCount = coveredCount
             HasFailingTest = hasFailing
             Symbols = symbols }
           : Features.Treemap.FileCoverageFact))
        |> Array.toList
      Some (Features.Treemap.CoverageTreemapNode.build "Solution" facts)
    GetSessionBindings = fun sessionId ->
      match System.Threading.Volatile.Read(&sharedBindingScope.contents) with
      | Some scope ->
        scope.ActiveBindings
        |> Map.values |> Array.ofSeq
      | None -> [||]
    GetBindingScopeSnapshot = fun () -> System.Threading.Volatile.Read(&sharedBindingScope.contents)
    GetLiveBindings = fun sessionId ->
      SageFs.Features.LiveBindingsAdaptive.tryGet liveBindingsAdaptive (WorkerProtocol.SessionId.value sessionId)
    GetLiveTestingStatus = fun () ->
      let model = elmRuntime.GetModel()
      let activeId =
        SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
        |> Option.map WorkerProtocol.SessionId.value |> Option.defaultValue ""
      SageFs.Features.LiveTesting.LiveTestCycleState.liveTestingStatusBarForSession activeId model.LiveTesting
    GetLiveTestActivity = fun sessionId ->
      SageFsModel.liveTestActivityFor sessionId (elmRuntime.GetModel())
    GetLiveTestingActive = fun () ->
      let model = elmRuntime.GetModel()
      match model.LiveTesting.TestState.Activation with
      | SageFs.Features.LiveTesting.LiveTestingActivation.Active -> true
      | SageFs.Features.LiveTesting.LiveTestingActivation.Inactive -> false
    GetEvalTimeline = fun () ->
      let state = System.Threading.Volatile.Read(&sharedFeatureState.contents)
      SageFs.Features.EvalTimeline.timelineStats 20 state.CachedTimeline
    GetDaemonHealth = fun () ->
      let model = elmRuntime.GetModel()
      let sessions =
        model.Sessions.Sessions
        |> List.map (fun s ->
          let healthStatus : SageFs.Features.SessionHealthStatus =
            match s.Status with
            | SageFs.SessionDisplayStatus.Running -> SageFs.Features.SessionHealthStatus.Ready
            | SageFs.SessionDisplayStatus.Starting -> SageFs.Features.SessionHealthStatus.WarmingUp
            | SageFs.SessionDisplayStatus.Restarting -> SageFs.Features.SessionHealthStatus.WarmingUp
            | SageFs.SessionDisplayStatus.Faulted _ -> SageFs.Features.SessionHealthStatus.Faulted
            | SageFs.SessionDisplayStatus.Lost -> SageFs.Features.SessionHealthStatus.Faulted
            | SageFs.SessionDisplayStatus.Stopped -> SageFs.Features.SessionHealthStatus.Stopped
            | SageFs.SessionDisplayStatus.Stale -> SageFs.Features.SessionHealthStatus.Stopped
          let projectName =
            match s.Projects with
            | p :: _ -> System.IO.Path.GetFileName p
            | [] -> s.Name |> Option.defaultValue (WorkerProtocol.SessionId.value s.Id)
          ({ SessionId = WorkerProtocol.SessionId.value s.Id
             ProjectName = projectName
             Status = healthStatus
             EvalCount = s.EvalCount
             LastActivity = System.DateTimeOffset(s.LastActivity, System.TimeSpan.Zero) }
           : SageFs.Features.SessionHealthSummary))
      let testingSummary =
        let ts = model.LiveTesting.TestState
        match ts.Activation with
        | SageFs.Features.LiveTesting.LiveTestingActivation.Inactive -> None
        | SageFs.Features.LiveTesting.LiveTestingActivation.Active ->
          let entries = SageFs.Features.LiveTesting.LiveTestState.statusEntriesForSession "" ts
          match entries.Length with
          | 0 -> None
          | _ ->
            let summary = SageFs.Features.LiveTesting.TestSummary.fromStatuses ts.Activation (entries |> Array.map (fun e -> e.Status))
            Some ({ TotalTests = summary.Total
                    Passed = summary.Passed
                    Failed = summary.Failed
                    Running = summary.Running }
                  : SageFs.Features.LiveTestHealthSummary)
      let memoryMB = int (System.GC.GetTotalMemory(false) / 1_048_576L)
      Some ({ DaemonPid = System.Diagnostics.Process.GetCurrentProcess().Id
              DaemonPort = mcpPort
              Uptime = System.DateTimeOffset.UtcNow - daemonStartTime
              Version = version
              SessionSummaries = sessions
              LiveTestingSummary = testingSummary
              MemoryMB = memoryMB }
            : SageFs.Features.HealthSnapshot)
    GetFailureNarratives = fun () ->
      let model = elmRuntime.GetModel()
      let testState = model.LiveTesting.TestState
      match testState.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> []
      | _ ->
      testState.Cached.FailureNarratives
      |> Map.toList
      |> List.choose (fun (testId, narrative) ->
        testState.DiscoveredTests
        |> Array.tryFind (fun tc -> tc.Id = testId)
        |> Option.map (fun tc -> tc.DisplayName, narrative))
    GetCurrentDiagnostics = fun () ->
      let model = elmRuntime.GetModel()
      model.Diagnostics
      |> Features.DiagnosticsStore.allFlat
      |> List.map Diagnostic.fromFeatureDiag
      |> List.sortBy (fun d -> match d.Severity with DiagError -> 0 | DiagWarning -> 1)
    GetFilmstripEntries = fun () ->
      let state = System.Threading.Volatile.Read(&sharedFeatureState.contents)
      // The 20 most recent evals, oldest first — the filmstrip shows its
      // newest frame last. (Reversing the whole history and taking 20 showed
      // the session's FIRST 20 evals forever, after walking up to 10k cells.)
      SageFs.Features.FeatureHooks.recentEvals 20 state
      |> List.map (fun entry ->
        let outcome =
          match entry.Result with
          | r when r.Contains("Operation was cancelled") -> EvalCancelled
          | r when r.Contains("error FS") || r.Contains("; error") || r.StartsWith("error") -> EvalError
          | _ -> EvalSuccess
        let label =
          let first = entry.Code.Split('\n') |> Array.tryHead |> Option.defaultValue ""
          if first.Length > 60 then first.[..59] else first
        { Index = entry.CellIndex
          Label = label
          DurationMs = entry.DurationMs
          Outcome = outcome
          Timestamp = entry.Timestamp })
    GetTestSourceLocations = fun () ->
      let model = elmRuntime.GetModel()
      model.ResolvedSourceLocations
    GetSessionAgentBadges = fun sessionId ->
      let presences =
        AgentActivityTracker.getActivePresences
          activityTracker (Some (WorkerProtocol.SessionId.value sessionId)) (TimeSpan.FromMinutes 5.0) DateTime.UtcNow
      presences
      |> List.map (fun p ->
        let freshness = SessionOperations.AgentPresence.freshness DateTime.UtcNow (TimeSpan.FromMinutes 2.0) p
        let cssClass =
          match freshness with
          | SessionOperations.AgentFreshness.Fresh -> "badge-agent"
          | SessionOperations.AgentFreshness.Stale -> "badge-agent badge-agent-stale"
        let intentLabel =
          match p.Intent with
          | Some i -> i
          | None -> ""
        let detail =
          match p.RecentFiles with
          | [] -> ""
          | files -> files |> List.truncate 3 |> String.concat ", "
        { Name = p.AgentName; IntentLabel = intentLabel; CssClass = cssClass; DetailLabel = detail })
    GetSessionGuidanceCss = fun sessionId ->
      let presences =
        AgentActivityTracker.getActivePresences
          activityTracker (Some (WorkerProtocol.SessionId.value sessionId)) (TimeSpan.FromMinutes 5.0) DateTime.UtcNow
      let workers =
        presences
        |> List.filter (fun p -> p.Role = SessionOperations.OccupantRole.Worker)
      match workers with
      | [] -> ""
      | _ -> "guidance-contested"
    GetSessionWorkflow = fun sessionId ->
      match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
      | Some info -> info.Workflow
      | None -> WorkflowTypes.SessionWorkflow.Interactive
    GetSessionActiveProject = fun sessionId ->
      match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
      | Some info -> info.ActiveProject
      | None -> None
    GetSessionProjectRoles = fun sessionId ->
      match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
      | Some info -> info.ProjectRoles
      | None -> []
    GetSessionApp = fun sessionId ->
      match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
      | Some info -> info.App
      | None -> AppRun.AppRunState.NotRunning
    GetSessionEvalCounts = fun () ->
      elmRuntime.GetModel().Sessions.Sessions
      |> List.map (fun s -> s.Id, s.EvalCount)
      |> Map.ofList
    IsCreatingSession = fun () -> elmRuntime.GetModel().CreatingSession
  }

  let dashboardActions : DashboardActions = {
    EvalCode = fun sid code -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = proxyToSession getProxyStr notifyWorkerDiedStr sidStr (WorkerProtocol.WorkerMessage.EvalCode(code, "dash"))
      match result with
      | Ok (WorkerProtocol.WorkerResponse.EvalResult(_, Ok msg, diags, _metadata)) ->
        let! _ =
          dispatchOutputAndWait
            elmRuntime
            stateChangedEvent.Publish
            sidStr
            (SageFsMsg.Event (
              TuiEvent.EvalCompleted (sidStr, msg, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic)))
        // Live binding watch window: pulled AFTER the eval reply, never
        // attached to it (roast-4 #2) — fire-and-forget so a slow or failed
        // reflection walk can never delay the caller's eval result. Fed into
        // the adaptive store; subscribers fire only on real change, and the
        // existing EvalCompleted → ModelChanged morph re-renders the panel.
        Async.Start (
          async {
            let! liveResult =
              proxyToSession getProxyStr notifyWorkerDiedStr sidStr
                (WorkerProtocol.WorkerMessage.GetLiveValues (sprintf "live-%s" sidStr))
              |> Async.AwaitTask
            match liveResult with
            | Ok (WorkerProtocol.WorkerResponse.LiveValuesResult(_, json)) ->
              try
                let snap =
                  WorkerProtocol.Serialization.deserialize<SageFs.Features.LiveValueTree.LiveValueSnapshot> json
                SageFs.Features.LiveBindingsAdaptive.update liveBindingsAdaptive sidStr { snap with SessionId = sidStr }
              with ex ->
                Log.warn "[DaemonMode] Failed to parse live value snapshot for %s: %s" sidStr ex.Message
            | Ok other ->
              Log.warn "[DaemonMode] Unexpected live-values response for %s: %A" sidStr other
            | Error e ->
              Log.warn "[DaemonMode] Live value pull failed for %s: %s" sidStr (SageFsError.describe e)
          })
        return Ok msg
      | Ok (WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _, _)) ->
        let msg = SageFsError.describe err
        let! _ =
          dispatchOutputAndWait
            elmRuntime
            stateChangedEvent.Publish
            sidStr
            (SageFsMsg.Event (TuiEvent.EvalFailed (sidStr, msg)))
        return Error msg
      | Ok other -> return Error (sprintf "Unexpected: %A" other)
      | Error e -> return Error (SageFsError.describe e)
    }
    ResetSession = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = proxyToSession getProxyStr notifyWorkerDiedStr sidStr (WorkerProtocol.WorkerMessage.ResetSession "dash")
      return
        match result with
        | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Ok ())) -> Ok "Session reset successfully"
        | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Error e)) -> Error (sprintf "Reset failed: %A" e)
        | Ok other -> Error (sprintf "Unexpected: %A" other)
        | Error e -> Error (SageFsError.describe e)
    }
    HardResetSession = fun sid -> task {
      let! result = sessionOps.RestartSession sid true
      return
        result
        |> Result.map (sprintf "Hard reset: %s")
        |> Result.mapError (fun e -> sprintf "Hard reset failed: %s" (SageFsError.describe e))
    }
    Dispatch = fun msg -> elmRuntime.Dispatch msg
    SwitchSession = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      elmRuntime.Dispatch(SageFsMsg.Event (TuiEvent.SessionSwitched (None, sidStr)))
      stateChangedEvent.Trigger(SessionSwitched sid)
      return Ok (sprintf "Switched to session '%s'" sidStr)
    }
    StopSession = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = sessionOps.StopSession sidStr
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.mapError SageFsError.describe
    }
    PurgeSession = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = sessionOps.PurgeSession sidStr
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.mapError SageFsError.describe
    }
    CreateSession = fun projects workingDir -> task {
      let! result = sessionOps.CreateSession projects workingDir WorkflowTypes.SessionWorkflow.Interactive
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result
        |> Result.map (fun sidStr -> WorkerProtocol.SessionId.validate sidStr |> Result.defaultValue (WorkerProtocol.SessionId.newId ()))
        |> Result.mapError SageFsError.describe
    }
    ShutdownCallback = Some (fun () -> cts.Cancel())
    RunApp = fun sid request -> task {
      let! result =
        AppRunOrchestration.runApp sessionOps (fun () -> DateTime.UtcNow) Timeouts.warmupReadyPollMax sid request
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.map AppRun.describeState |> Result.mapError SageFsError.describe
    }
    StopApp = fun sid -> task {
      let! result = AppRunOrchestration.stopApp sessionOps sid
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.map AppRun.describeState |> Result.mapError SageFsError.describe
    }
  }

  let dashboardInfra : DashboardInfra = {
    Version = version
    McpPort = mcpPort
    StateChanged = stateChangedEvent.Publish
    ConnectionTracker = Some connectionTracker
    SessionThemes = sessionThemes
    GetSessionCount = fun () -> task {
      let! sessions = sessionOps.GetAllSessions()
      return sessions.Length
    }
    SystemAlarmBuffer =
      // Intercept SystemAlarm events and prepend to the buffer (max 3, newest-first).
      let buf : SageFs.Server.DashboardTypes.SystemAlarmEntry list ref = ref []
      stateChangedEvent.Publish.Add(fun change ->
        match change with
        | SystemAlarm (phase, msg) ->
          let entry : SageFs.Server.DashboardTypes.SystemAlarmEntry =
            { Phase = phase; Message = msg; Timestamp = System.DateTimeOffset.UtcNow }
          buf.Value <- (entry :: buf.Value) |> List.truncate 3
        | _ -> ())
      buf
    TriggerStateChange = fun () -> stateChangedEvent.Trigger (ModelChanged (0, 0))
    ActivityTracker = Some activityTracker
    LiveBindingsAdaptive = Some liveBindingsAdaptive
    ConnectionChannels = System.Collections.Concurrent.ConcurrentDictionary<string, MailboxProcessor<DashboardStreamCommand>>()
    // Wait-free (D4): dereferences CohortOwner's published frame pointer
    // directly — no mailbox round-trip, no IO.
    ReadCohortFrame = cohortOwner.ReadFrame
    GetCompletions = fun (sessionId: WorkerProtocol.SessionId) (code: string) (cursorPos: int) -> task {
      try
        let! proxy = sessionOps.GetProxy sessionId
        match proxy with
        | Some send ->
          let replyId = sprintf "dash-comp-%d" (System.Random.Shared.Next())
          let! resp =
            send (WorkerProtocol.WorkerMessage.GetCompletions(code, cursorPos, replyId))
            |> Async.StartAsTask
          return
            match resp with
            | WorkerProtocol.WorkerResponse.CompletionResult(_, items) ->
              items |> List.map (fun label ->
                { SageFs.Features.AutoCompletion.DisplayText = label
                  SageFs.Features.AutoCompletion.ReplacementText = label
                  SageFs.Features.AutoCompletion.Kind = SageFs.Features.AutoCompletion.CompletionKind.Variable
                  SageFs.Features.AutoCompletion.GetDescription = None })
            | _ -> []
        | None -> return []
      with
      | :? System.Net.Http.HttpRequestException | :? Threading.Tasks.TaskCanceledException -> return []
      | ex ->
        Log.error "[getCompletions] Error for session: %s (%s)\n%s" ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        return []
    }
  }

  let dashboardEndpoints =
    Dashboard.createEndpoints dashboardQueries dashboardActions dashboardInfra

  let hotReloadProxyEndpoints = createHotReloadProxyEndpoints getWorkerBaseUrl httpClient stateChangedEvent

  let dashboardTask =
    startDashboardServer log bindHost daemonOrigins dashboardPort (dashboardEndpoints @ hotReloadProxyEndpoints) cts.Token

  // Workers handle their own warmup, middleware, and file watching.
  // The daemon just needs to wait for the MCP and dashboard servers.

  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    log.LogInformation("Shutting down...")
    // Start a watchdog — if graceful shutdown takes too long, force exit
    System.Threading.Tasks.Task.Delay(5000).ContinueWith(fun (_: System.Threading.Tasks.Task) ->
      log.LogWarning("Graceful shutdown timed out — forcing exit")
      // StopAll has its own graceful budget, but timer and watcher cleanup can
      // consume the watchdog window before it runs. Sweep the session PIDs from
      // the lock-free snapshot before exiting so the daemon never leaves its
      // workers behind when graceful shutdown is delayed or wedged.
      readSnapshot()
      |> SessionManager.QuerySnapshot.allSessions
      |> List.choose (fun session -> WorkerProtocol.SessionLifecycleStatus.workerPid session.Status)
      |> SessionManager.killWorkerPids
      Environment.Exit(1)) |> ignore
    try cts.Cancel() with :? ObjectDisposedException -> ())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    log.LogInformation("Daemon stopped")
    // Belt-and-suspenders for issue #126: if the process is exiting through a
    // path that skipped the graceful StopAll (e.g. Environment.Exit elsewhere,
    // taskkill without /F), sweep worker PIDs so they don't become orphans.
    // The worker-side parent-death watchdog (WorkerMain.ParentMonitor) is the
    // primary defense for hard kills that skip ProcessExit entirely.
    readSnapshot()
    |> SessionManager.QuerySnapshot.allSessions
    |> List.choose (fun session -> WorkerProtocol.SessionLifecycleStatus.workerPid session.Status)
    |> SessionManager.killWorkerPids)

  // Start MCP and dashboard servers FIRST so ports are listening
  let mcpRunning =
    System.Threading.Tasks.Task.Run(
      System.Func<System.Threading.Tasks.Task>(fun () -> mcpTask),
      cts.Token)
  let dashboardRunning =
    System.Threading.Tasks.Task.Run(
      System.Func<System.Threading.Tasks.Task>(fun () -> dashboardTask),
      cts.Token)

  // Brief yield to let servers bind their ports
  do! System.Threading.Tasks.Task.Delay(200)
  startupSw.Stop()
  Instrumentation.startupDurationMs.Record(startupSw.Elapsed.TotalMilliseconds)
  match isNull startupSpan |> not with
  | true ->
    startupSpan.SetTag("startup_ms", startupSw.Elapsed.TotalMilliseconds) |> ignore
    Instrumentation.succeedSpan startupSpan
  | false -> ()
  log.LogInformation("SageFs daemon ready in {StartupMs:F0}ms (PID {Pid}, MCP port {McpPort}, dashboard port {DashboardPort})",
    startupSw.Elapsed.TotalMilliseconds, Environment.ProcessId, mcpPort, dashboardPort)
  log.LogInformation("Dashboard: http://localhost:{Port}/dashboard", dashboardPort)
  log.LogInformation("SSE events: http://localhost:{Port}/events", mcpPort)
  log.LogInformation("Health: http://localhost:{Port}/health", mcpPort)

  // Ownership rules 2 & 3 (§3.1): write a daemon-info file into this
  // daemon's own data dir — what `sagefs sweep` reads when the HTTP
  // /api/daemon-info side is wedged — and, when this daemon's data dir
  // isn't the real ~/.SageFs (an isolated SAGEFS_DATA_DIR, e.g. a test or
  // an agent's throwaway daemon), ALSO register a copy under the real
  // ~/.SageFs/spawned/ so sweep can find it without scanning the
  // filesystem for arbitrary data dirs.
  let daemonInfoFile : DaemonOwnership.DaemonInfoFile =
    { Pid = Environment.ProcessId
      StartTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
      OwnerPid = ownership.OwnerPid
      OwnerStart = ownership.OwnerStartTicks
      McpPort = mcpPort
      DashboardPort = dashboardPort
      DataDir = DaemonState.SageFsDir }
  try
    DaemonOwnership.DaemonInfoFile.write DaemonState.SageFsDir daemonInfoFile
    let realDir = DaemonOwnership.realHomeSageFsDir ()
    match String.Equals(System.IO.Path.GetFullPath DaemonState.SageFsDir, System.IO.Path.GetFullPath realDir, StringComparison.OrdinalIgnoreCase) with
    | true -> ()
    | false -> DaemonOwnership.registerSpawned realDir daemonInfoFile
  with ex ->
    log.LogWarning("Could not write daemon-info file: {Error}", ex.Message)

  // Cleanup orphaned .tmp files from interrupted writes
  // (.sagefs per-session replay files no longer exist — only the .sagetc test cache)
  let stcOrphans = Features.TestCacheFile.cleanupOrphanedTmpFiles DaemonState.SageFsDir
  match stcOrphans > 0 with
  | true ->
    Instrumentation.persistenceOrphanedTmpCleanup.Add(int64 stcOrphans, System.Collections.Generic.KeyValuePair("format", box "stc1"))
    log.LogInformation("Cleaned up {Count} orphaned .tmp files ({Stc} .sagetc)", stcOrphans, stcOrphans)
  | false -> ()

  // Test cache pre-loading happens per-session after resume, not eagerly

  // Resume sessions in background — don't block the daemon main task.
  // Each resumed session dispatches ListSessions so dashboard sees them incrementally.
  let _resumeTask =
    System.Threading.Tasks.Task.Run(fun () ->
      task {
        try
          match noResume with
          | true ->
            log.LogInformation("Session resume skipped (--no-resume)")
          | false ->
            do! resumeSessions (fun () ->
              elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions))

          // Load cached test state after sessions are restored
          let activeSessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
          let uniqueProjectSets =
            activeSessions
            |> List.map (fun s -> s.Projects)
            |> List.distinctBy (fun ps ->
              ps |> List.sort |> List.map (fun p -> p.Replace("\\", "/").ToLowerInvariant()) |> String.concat "|")
          for projects in uniqueProjectSets do
            match Features.DaemonPersistence.loadTestCache DaemonState.SageFsDir projects with
            | Ok cachedState ->
              log.LogInformation("Restored test cache ({CoverageCount} coverage, {ResultCount} results)",
                cachedState.TestCoverageBitmaps.Count, cachedState.LastResults.Count)
              elmRuntime.Dispatch(SageFsMsg.RestoreTestCache cachedState)
              let (Features.LiveTesting.RunGeneration gen) = cachedState.LastGeneration
              match gen > System.Threading.Volatile.Read(&lastSavedGeneration.contents) with
              | true -> System.Threading.Volatile.Write(&lastSavedGeneration.contents, gen)
              | false -> ()
            | Error msg -> log.LogDebug("No test cache available: {Reason}", msg)
        with ex ->
          log.LogWarning("Session resume failed: {Error}", ex.Message)
      } :> System.Threading.Tasks.Task)

  // Periodic status polling — refreshes session status (Starting → Ready)
  // so SSE subscribers see warmup progress in real time.
  // 10s interval: steady-state polling is cheap (short-circuited when unchanged)
  // but avoids the 2s cascade that dominated the render budget.
  let _statusPollTask =
    System.Threading.Tasks.Task.Run(fun () ->
      task {
        try
          while not cts.Token.IsCancellationRequested do
            do! System.Threading.Tasks.Task.Delay(10_000, cts.Token)
            elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
        with
        | :? OperationCanceledException -> ()
        | ex -> log.LogWarning("Status poll failed: {Error}", ex.Message)
      } :> System.Threading.Tasks.Task)

  try
    let! _ = System.Threading.Tasks.Task.WhenAny(mcpRunning, dashboardRunning)
    ()
  with
  | :? OperationCanceledException -> ()

  // Graceful shutdown: stop test cycle timer, file watcher, and all sessions
  // W30(R12): Use Dispose(WaitHandle) for testCycleTimer — matches cacheSaveTimer treatment.
  // Bare Dispose() returns immediately; any in-flight 200ms tick callback could still be running
  // and call elmRuntime.Dispatch() after the elm runtime starts shutting down.
  match! disposeTimerAndWait testCycleTimer (TimeSpan.FromSeconds 3.0) with
  | TimerStop.StillRunning -> log.LogWarning("testCycleTimer shutdown wait timed out — callback may still be running")
  | TimerStop.Joined -> ()
  // Wait for an in-flight cacheSaveCallback before performGracefulShutdown writes the
  // manifest, so a late periodic save cannot overwrite its StoppedAt stamps.
  match! disposeTimerAndWait cacheSaveTimer (TimeSpan.FromSeconds 5.0) with
  | TimerStop.StillRunning -> log.LogWarning("cacheSaveTimer shutdown wait timed out — callback may still be running")
  | TimerStop.Joined -> ()
  // Dispose activity cleanup timer (best-effort, no wait needed — cleanup is idempotent)
  try activityCleanupTimer.Dispose()
  with :? System.ObjectDisposedException -> ()
  try watcherSyncTimer.Dispose()
  with :? System.ObjectDisposedException -> ()
  match ttlTimer with
  | Some t ->
    try t.Dispose()
    with :? System.ObjectDisposedException -> ()
  | None -> ()
  (liveTestWatcherManager :> System.IDisposable).Dispose()
  // Ownership rule 3 (§3.1): a daemon that shut down gracefully is no
  // longer a sweep target — remove its own daemon-info file and, if it was
  // registered externally, its spawned/ registry entry.
  try
    DaemonOwnership.DaemonInfoFile.delete DaemonState.SageFsDir
    DaemonOwnership.unregisterSpawned (DaemonOwnership.realHomeSageFsDir ()) Environment.ProcessId
  with ex ->
    log.LogWarning("Could not clean up daemon-info file: {Error}", ex.Message)
  try
    do! performGracefulShutdown log readSnapshot elmRuntime.GetModel sessionManager manifestOwner
  with ex ->
    log.LogWarning("Shutdown cleanup error: {Error}", ex.Message)
}
