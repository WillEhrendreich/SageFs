module SageFs.Server.DaemonMode

open System
open System.Threading
open SageFs
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
    | :? ObjectDisposedException as ex ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ex.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe closed — %s" ex.Message))
}

// ---------------------------------------------------------------------------
// DaemonInfra — lifetime group 1: one-time daemon infrastructure
// ---------------------------------------------------------------------------

/// Infrastructure created once at daemon startup.
/// Groups logger, HTTP client, persistence, cancellation, and state-change event.
type DaemonInfra = {
  Log: ILogger
  LoggerFactory: ILoggerFactory
  HttpClient: Net.Http.HttpClient
  Persistence: SageFs.EventStore.EventPersistence
  DaemonStreamId: string
  Cts: CancellationTokenSource
  StateChangedEvent: Event<DaemonStateChange>
  /// Timeout for agent-facing worker fetches (MCP tools, SSE).
  McpFetchTimeoutSec: float
  /// Timeout for user-facing worker fetches (dashboard).
  DashboardFetchTimeoutSec: float
}

/// Event append — returns the Task so callers can await on critical paths (e.g. shutdown).
/// Non-critical callers may discard the Task; it logs errors internally.
let appendEventsAsync (infra: DaemonInfra) (events: Features.Events.SageFsEvent list) =
  System.Threading.Tasks.Task.Run(fun () ->
    task {
      try
        match! infra.Persistence.AppendEvents infra.DaemonStreamId events with
        | Ok () -> ()
        | Error err ->
          match err.Contains("duplicate key") || err.Contains("version") with
          | true -> infra.Log.LogDebug("Audit trail append skipped (already exists): {Error}", err)
          | false -> infra.Log.LogWarning("Event append failed: {Error}", err)
      with
      | :? OperationCanceledException -> ()  // shutdown race — safe to swallow
    } :> System.Threading.Tasks.Task)

/// Create one-time daemon infrastructure (logger, HTTP client, persistence, CTS).
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

  let persistence = SageFs.EventStore.EventPersistence.noop

  {
    Log = log
    LoggerFactory = loggerFactory
    HttpClient = httpClient
    Persistence = persistence
    DaemonStreamId = "daemon-sessions"
    Cts = new CancellationTokenSource()
    StateChangedEvent = Event<DaemonStateChange>()
    McpFetchTimeoutSec = 5.0
    DashboardFetchTimeoutSec = 0.5
  }

/// Handle --prune flag: clear the binary manifest and return true if pruned.
let handlePrune (infra: DaemonInfra) (flags: Args.DaemonFlags) = task {
  match flags.Prune with
  | true ->
    match Features.DaemonPersistence.loadManifest DaemonState.SageFsDir with
    | Ok state ->
      let aliveSessions = Features.Replay.DaemonReplayState.aliveSessions state
      match aliveSessions.IsEmpty with
      | true ->
        infra.Log.LogInformation("No alive sessions to prune")
      | false ->
        let now = DateTimeOffset.UtcNow
        let pruned =
          { state with
              Sessions =
                state.Sessions
                |> Map.map (fun _ r ->
                  match r.StoppedAt with
                  | Some _ -> r
                  | None -> { r with StoppedAt = Some now }) }
        match Features.DaemonPersistence.saveManifest DaemonState.SageFsDir pruned with
        | Ok _ -> infra.Log.LogInformation("Pruned {Count} session(s) from binary manifest", aliveSessions.Length)
        | Error msg -> infra.Log.LogWarning("Prune save failed: {Error}", msg)
    | Error _ ->
      infra.Log.LogInformation("No binary manifest found — nothing to prune")
    return true
  | false -> return false
}

/// Build SessionManagementOps record from mailbox + snapshot reader.
let createSessionOps
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (appendEvents: Features.Events.SageFsEvent list -> unit)
  : SessionManagementOps =
  {
    CreateSession = fun projects workingDir ->
      task {
        let autoOpenNamespaces = DirectoryConfig.autoOpenNamespacesForDirectory workingDir
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.CreateSession(projects, workingDir, autoOpenNamespaces, reply))
          |> Async.StartAsTask
        match result with
        | Ok info ->
          appendEvents [
            Features.Events.SageFsEvent.DaemonSessionCreated
              {| SessionId = info.Id; Projects = projects; WorkingDir = workingDir; CreatedAt = DateTimeOffset.UtcNow |}
          ]
          return Ok info.Id
        | Error e -> return Error e
      }
    ListSessions = fun () ->
      task {
        let! sessions =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.ListSessions reply)
          |> Async.StartAsTask
        return SessionOperations.formatSessionList DateTime.UtcNow None sessions
      }
    StopSession = fun sessionId ->
      task {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.StopSession(sessionId, reply))
          |> Async.StartAsTask
        appendEvents [
          Features.Events.SageFsEvent.DaemonSessionStopped
            {| SessionId = sessionId; StoppedAt = DateTimeOffset.UtcNow |}
        ]
        return
          result
          |> Result.map (fun () ->
            sprintf "Session '%s' stopped." sessionId)
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
      task { return HttpWorkerClient.proxyFromUrls sessionId snapshot.WorkerBaseUrls }
    GetSessionInfo = fun sessionId ->
      task { return SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) }
    GetAllSessions = fun () ->
      task { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    GetStandbyInfo = fun () ->
      task { return (readSnapshot()).StandbyInfo }
    NotifyWorkerDied = fun sessionId ->
      // Post WorkerExited with pid=-1 to accelerate Faulted transition.
      // The real proc.Exited event will also fire; the stale-event guard
      // in WorkerExited handler (checks currentPid <> workerPid) prevents double-restart.
      sessionManager.Post(
        SessionManager.SessionCommand.WorkerExited(sessionId, -1, -1))
  }

/// Look up worker HTTP base URL for a session from CQRS snapshot.
let getWorkerBaseUrl (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: string) =
  let snapshot = readSnapshot()
  match Map.tryFind sid snapshot.WorkerBaseUrls with
  | Some url when url.Length > 0 -> Some url
  | _ -> None

/// Fetch JSON from a worker endpoint with timeout, returning None on failure.
let fetchWorkerEndpoint
  (httpClient: Net.Http.HttpClient)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (sessionId: string)
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
      Log.warn "[fetchWorkerEndpoint] Timeout (%.0fs) fetching %s for session %s" timeout path sessionId
      return None
    | :? Net.Http.HttpRequestException as ex ->
      Log.error "[fetchWorkerEndpoint] HTTP error fetching %s for session %s: %s" path sessionId ex.Message
      return None
    | ex ->
      Log.error "[fetchWorkerEndpoint] Unexpected error fetching %s for session %s: %s" path sessionId (ex.GetType().Name)
      return None
  | None -> return None
}

/// Build DaemonReplayState from active sessions (used in periodic save + shutdown).
let buildReplayState (readSnapshot: unit -> SessionManager.QuerySnapshot) =
  let activeSessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
  let toRecord (s: WorkerProtocol.SessionInfo) : Features.Replay.DaemonSessionRecord =
    { SessionId = s.Id; Projects = s.Projects; WorkingDir = s.WorkingDirectory
      CreatedAt = DateTimeOffset.UtcNow; StoppedAt = None }
  { Features.Replay.DaemonReplayState.Sessions =
      activeSessions |> List.map (fun s -> s.Id, toRecord s) |> Map.ofList
    Features.Replay.DaemonReplayState.ActiveSessionId =
      activeSessions |> List.tryHead |> Option.map (fun s -> s.Id) }

/// Get session state from CQRS snapshot.
let getSessionStateFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: string) =
  match String.IsNullOrEmpty(sid) with
  | true -> SessionState.Uninitialized
  | false ->
    let snapshot = readSnapshot()
    match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
    | Some info -> WorkerProtocol.SessionStatus.toSessionState info.Status
    | None -> SessionState.Uninitialized

/// Get working directory for a session from CQRS snapshot.
let getSessionWorkingDirFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: string) =
  let snapshot = readSnapshot()
  match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
  | Some info -> info.WorkingDirectory
  | None -> ""

/// Get warmup status message for a session.
let getStatusMsgFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: string) =
  readSnapshot().WarmupProgress |> Map.tryFind sid

/// Fetch eval stats from worker HTTP endpoint.
let getEvalStatsFromWorker
  (httpClient: Net.Http.HttpClient)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (sid: string) = task {
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
      Log.error "[getEvalStats] JSON parse error for %s: %s" sid ex.Message
      return Affordances.EvalStats.empty
    | ex ->
      Log.error "[getEvalStats] Unexpected error for %s: %s (%s)" sid ex.Message (ex.GetType().Name)
      return Affordances.EvalStats.empty
  | _ -> return Affordances.EvalStats.empty
}

/// Create hot-reload proxy HTTP endpoints that forward to worker servers.
let createHotReloadProxyEndpoints
  (getWorkerBaseUrl: string -> string option)
  (httpClient: Net.Http.HttpClient)
  (stateChangedEvent: Event<DaemonStateChange>)
  : HttpEndpoint list =
  let proxyToWorker (sid: string) (workerPath: string) (httpCall: string -> Threading.Tasks.Task<string * int * bool>) (ctx: HttpContext) = task {
    match getWorkerBaseUrl sid with
    | Some baseUrl ->
      try
        let url = sprintf "%s%s" baseUrl workerPath
        let! (respBody, statusCode, triggerChange) = httpCall url
        ctx.Response.ContentType <- "application/json"
        ctx.Response.StatusCode <- statusCode
        do! ctx.Response.WriteAsync(respBody)
        match triggerChange with
        | true -> stateChangedEvent.Trigger HotReloadChanged
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
      let! resp = httpClient.GetStringAsync(url)
      return (resp, 200, false)
    }) ctx
  let proxyPost (sid: string) (workerPath: string) (ctx: HttpContext) =
    proxyToWorker sid workerPath (fun url -> task {
      use reader = new IO.StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      use content = new Net.Http.StringContent(body, Text.Encoding.UTF8, "application/json")
      let! resp = httpClient.PostAsync(url, content)
      let! respBody = resp.Content.ReadAsStringAsync()
      return (respBody, int resp.StatusCode, resp.IsSuccessStatusCode)
    }) ctx
  let extractSid = fun (r: RequestData) -> r.GetString("sid", "")
  let proxyGetRoute path = mapGet (sprintf "/api/sessions/{sid}%s" path) extractSid (fun sid -> fun ctx -> proxyGet sid path ctx)
  let proxyPostRoute path = mapPost (sprintf "/api/sessions/{sid}%s" path) extractSid (fun sid -> fun ctx -> proxyPost sid path ctx)
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
  (persistence: SageFs.EventStore.EventPersistence)
  (daemonStreamId: string)
  (appendEventsAsync: Features.Events.SageFsEvent list -> System.Threading.Tasks.Task)
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  =
  // Save test cache for each unique project set
  let activeSessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
  let testState = (getModel()).LiveTesting.TestState
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

  // Persist session manifest for binary-first resume
  let replayState = buildReplayState readSnapshot
  match Features.DaemonPersistence.saveManifest DaemonState.SageFsDir replayState with
  | Ok path -> log.LogInformation("Saved session manifest to {Path}", path)
  | Error err ->
    Instrumentation.persistenceSaveErrors.Add(
      1L, System.Collections.Generic.KeyValuePair("format", box "sfm1"))
    log.LogWarning("Failed to save session manifest: {Error}", err)

  // Append shutdown events and await them before stopping workers.
  // EventStore is currently noop so this completes immediately;
  // when real persistence is re-enabled the await ensures events are flushed.
  let appendTasks =
    activeSessions
    |> List.map (fun info ->
      appendEventsAsync [
        Features.Events.SageFsEvent.DaemonSessionStopped
          {| SessionId = info.Id; StoppedAt = DateTimeOffset.UtcNow |}
      ])
  System.Threading.Tasks.Task.WhenAll(appendTasks).Wait(System.TimeSpan.FromSeconds 5.0) |> ignore
  // Stop all workers with a timeout
  let stopTask =
    sessionManager.PostAndAsyncReply(fun reply ->
      SessionManager.SessionCommand.StopAll reply)
    |> Async.StartAsTask
  match stopTask.Wait(Timeouts.processNormalExit) with
  | false -> log.LogWarning("StopAll timed out — some workers may not have stopped cleanly")
  | true -> ()

/// Handle test discovery from SessionManager → Elm model.
/// Scans project source files with tree-sitter, then dispatches
/// locations and test cases to the Elm loop.
let handleTestDiscovery
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (workingDir: string)
  (log: ILogger)
  (dispatch: SageFsMsg -> unit)
  (sid: string)
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
  | false -> dispatch (SageFsMsg.Event (SageFsEvent.TestLocationsDetected (sid, locations)))
  | true -> ()
  match Array.isEmpty tests with
  | false -> dispatch (SageFsMsg.Event (SageFsEvent.TestsDiscovered (sid, tests)))
  | true -> ()
  match List.isEmpty providers with
  | false -> dispatch (SageFsMsg.Event (SageFsEvent.ProvidersDetected providers))
  | true -> ()

/// Parse warmup progress string ("step/total msg") and dispatch to Elm.
let handleWarmupProgress (dispatch: SageFsMsg -> unit) (_sid: string) (progress: string) =
  match progress.IndexOf('/') with
  | slashIdx when slashIdx > 0 ->
    match progress.IndexOf(' ', slashIdx) with
    | spaceIdx when spaceIdx > slashIdx ->
      match System.Int32.TryParse(progress.[..slashIdx-1]),
            System.Int32.TryParse(progress.[slashIdx+1..spaceIdx-1]) with
      | (true, step), (true, total) ->
        let msg = progress.[spaceIdx+1..]
        dispatch (SageFsMsg.Event (SageFsEvent.WarmupProgress (step, total, msg)))
      | _ -> ()
    | _ -> ()
  | _ -> ()

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
      for projects in uniqueProjectSets do
        match Features.DaemonPersistence.saveTestCache DaemonState.SageFsDir projects model.LiveTesting.TestState with
        | Ok path -> log.LogDebug("Periodic cache save to {Path} (gen {Gen})", path, gen)
        | Error err ->
          Instrumentation.persistenceSaveErrors.Add(
            1L, System.Collections.Generic.KeyValuePair("format", box "stc1"))
          log.LogWarning("Periodic cache save failed: {Error}", err)
      sw.Stop()
      Instrumentation.cacheSaveCount.Add(1L)
      Instrumentation.cacheSaveMs.Record(
        sw.Elapsed.TotalMilliseconds,
        System.Collections.Generic.KeyValuePair("coverage_entries", box (int64 model.LiveTesting.TestState.TestCoverageBitmaps.Count)),
        System.Collections.Generic.KeyValuePair("result_entries", box (int64 model.LiveTesting.TestState.LastResults.Count)))
      lastSavedGeneration.Value <- gen
    | false -> ()
  with ex ->
    Instrumentation.periodicTaskErrors.Add(
      1L, System.Collections.Generic.KeyValuePair("task", box "cache_save"))
    log.LogWarning("Periodic cache save error: {Error}", ex.Message)

/// Periodic manifest save (binary session resume).
let periodicManifestSave (log: ILogger) (readSnapshot: unit -> SessionManager.QuerySnapshot) =
  try
    let replayState = buildReplayState readSnapshot
    match Features.DaemonPersistence.saveManifest DaemonState.SageFsDir replayState with
    | Ok path -> log.LogDebug("Periodic manifest save to {Path}", path)
    | Error err ->
      Instrumentation.persistenceSaveErrors.Add(
        1L, System.Collections.Generic.KeyValuePair("format", box "sfm1"))
      log.LogWarning("Periodic manifest save failed: {Error}", err)
  with ex ->
    Instrumentation.periodicTaskErrors.Add(
      1L, System.Collections.Generic.KeyValuePair("task", box "manifest_save"))
    log.LogWarning("Periodic manifest save error: {Error}", ex.Message)

/// Create a debounced file watcher for live testing.
/// Returns (watcher, debounceTimer) — caller must dispose both.
let createLiveTestWatcher
  (workingDir: string)
  (dispatch: SageFsMsg -> unit) =
  let mutable pendingPaths : Set<string> = Set.empty
  let watcherLock = obj()
  let debounceCallback _ =
    let paths =
      lock watcherLock (fun () ->
        let ps = pendingPaths
        pendingPaths <- Set.empty
        ps)
    for path in paths do
      try
        let fi = System.IO.FileInfo(path)
        match fi.Exists && fi.Length < 1_048_576L with
        | true ->
          let content = System.IO.File.ReadAllText(path)
          dispatch (SageFsMsg.FileContentChanged(path, content))
        | false -> ()
      with
      | :? System.IO.IOException -> ()
      | :? System.UnauthorizedAccessException -> ()
  let debounceTimer = new System.Threading.Timer(
    System.Threading.TimerCallback(debounceCallback), null,
    System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
  let handleFileChanged (e: System.IO.FileSystemEventArgs) =
    let path = e.FullPath
    match SageFs.FileWatcher.shouldTriggerRebuild
        { Directories = [workingDir]; Extensions = [".fs"; ".fsx"]; ExcludePatterns = []; DebounceMs = 200 }
        path with
    | true ->
      lock watcherLock (fun () ->
        pendingPaths <- pendingPaths |> Set.add path
        debounceTimer.Change(200, System.Threading.Timeout.Infinite) |> ignore)
    | false -> ()
  let watcher = new System.IO.FileSystemWatcher(workingDir)
  watcher.IncludeSubdirectories <- true
  watcher.NotifyFilter <- System.IO.NotifyFilters.LastWrite
  watcher.Filters.Add("*.fs")
  watcher.Filters.Add("*.fsx")
  watcher.Changed.Add(handleFileChanged)
  watcher.Created.Add(handleFileChanged)
  watcher.EnableRaisingEvents <- true
  watcher, debounceTimer

/// Get previous sessions: active from CQRS snapshot + historical from binary manifest.
let getPreviousSessions
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (persistence: SageFs.EventStore.EventPersistence)
  (daemonStreamId: string) = task {
  let snapshot = readSnapshot()
  let activeSessions =
    SessionManager.QuerySnapshot.allSessions snapshot
    |> List.map (fun (info: WorkerProtocol.SessionInfo) ->
      { PreviousSession.Id = info.Id
        PreviousSession.WorkingDir = info.WorkingDirectory
        PreviousSession.Projects = info.Projects
        PreviousSession.LastSeen = info.LastActivity })
  let activeIds = activeSessions |> List.map (fun s -> s.Id) |> Set.ofList
  let historicalSessions =
    match Features.DaemonPersistence.loadManifest DaemonState.SageFsDir with
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
let startDashboardServer
  (log: ILogger)
  (dashboardPort: int)
  (endpoints: HttpEndpoint list) = task {
  try
    let builder = WebApplication.CreateBuilder()
    builder.Logging
      .AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
      .AddFilter("Microsoft.Hosting", LogLevel.Warning)
    |> ignore
    builder.Services.AddResponseCompression(fun opts ->
      opts.EnableForHttps <- true
      // Do NOT compress text/event-stream — SSE is a long-lived stream.
      // Response compression buffers output and FinishCompressionAsync() throws
      // ArgumentOutOfRangeException on StreamPipeWriter when the client disconnects.
      opts.Providers.Add<BrotliCompressionProvider>()
      opts.Providers.Add<GzipCompressionProvider>()
    ) |> ignore
    builder.Services.Configure<BrotliCompressionProviderOptions>(fun (opts: BrotliCompressionProviderOptions) ->
      opts.Level <- System.IO.Compression.CompressionLevel.Fastest
    ) |> ignore
    let app = builder.Build()
    let bindHost =
      match System.Environment.GetEnvironmentVariable("SAGEFS_BIND_HOST") with
      | null | "" -> "localhost"
      | h -> h
    app.Urls.Add(sprintf "http://%s:%d" bindHost dashboardPort)
    app.UseResponseCompression() |> ignore
    app.UseRouting().UseFalco(endpoints) |> ignore
    log.LogInformation("Dashboard available at http://localhost:{Port}/dashboard", dashboardPort)
    do! app.RunAsync()
  with ex ->
    log.LogWarning("Dashboard failed to start: {Error}", ex.Message)
}

/// Resume previous sessions from binary manifest.
/// Creates new sessions for each alive-but-deduplicated entry, or
/// starts bare if no previous sessions exist.
let resumePreviousSessions
  (infra: DaemonInfra)
  (sessionOps: SessionManagementOps)
  (workingDir: string)
  (onSessionResumed: unit -> unit)
  = task {
  let log = infra.Log
  let appendEventsAsync events = appendEventsAsync infra events
  let startupSw = System.Diagnostics.Stopwatch.StartNew()
  let startupSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.startup" []

  // Load session manifest from binary — the sole source of truth
  let binarySpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.binary_manifest_load" []
  let binarySw = System.Diagnostics.Stopwatch.StartNew()
  let manifestResult = Features.DaemonPersistence.loadManifest DaemonState.SageFsDir
  binarySw.Stop()
  match isNull binarySpan with
  | false -> binarySpan.SetTag("binary_load_ms", binarySw.Elapsed.TotalMilliseconds) |> ignore
  | true -> ()

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
    | Error binaryErr ->
      log.LogInformation("No binary manifest ({Error}) — starting fresh", binaryErr)
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "none") |> ignore
      | true -> ()
      Instrumentation.succeedSpan binarySpan
      Features.Replay.DaemonReplayState.empty

  let aliveSessions = Features.Replay.DaemonReplayState.aliveSessions daemonState

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
    for staleId in Set.difference staleIds keptIds do
      appendEventsAsync [
        Features.Events.SageFsEvent.DaemonSessionStopped
          {| SessionId = staleId; StoppedAt = DateTimeOffset.UtcNow |}
      ] |> ignore
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
    // Skip missing directories first (synchronous, fast)
    let existing, missing =
      uniqueByDir |> List.partition (fun prev -> IO.Directory.Exists prev.WorkingDir)
    for prev in missing do
      log.LogWarning("Skipping session {SessionId} — directory {WorkingDir} no longer exists (will retry next startup)", prev.SessionId, prev.WorkingDir)
    // Filter to sessions relevant to current working directory
    let isSubdirectoryOf (parent: string) (child: string) =
      let comparison =
        match System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) with
        | true -> System.StringComparison.OrdinalIgnoreCase
        | false -> System.StringComparison.Ordinal
      let normalizedParent =
        match parent.EndsWith(string IO.Path.DirectorySeparatorChar) || parent.EndsWith(string IO.Path.AltDirectorySeparatorChar) with
        | true -> parent
        | false -> parent + string IO.Path.DirectorySeparatorChar
      child.StartsWith(normalizedParent, comparison) || child.Equals(parent, comparison)
    let relevant, irrelevant =
      existing |> List.partition (fun prev ->
        isSubdirectoryOf workingDir prev.WorkingDir ||
        isSubdirectoryOf prev.WorkingDir workingDir)
    for prev in irrelevant do
      log.LogInformation("Skipping session {SessionId} for {WorkingDir} — not under daemon directory {DaemonDir}",
        prev.SessionId, prev.WorkingDir, workingDir)
    // Resume all valid sessions in parallel — each is an independent worker process
    let resumeSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.session_resume" []
    let resumeTasks =
      relevant
      |> List.map (fun prev -> task {
        log.LogInformation("Resuming session for {WorkingDir}", prev.WorkingDir)
        let! result = sessionOps.CreateSession prev.Projects prev.WorkingDir
        match result with
        | Ok info ->
          Instrumentation.daemonSessionsResumed.Add(1L)
          // Stop the OLD session ID so it doesn't resurrect on next restart
          do! appendEventsAsync [
            Features.Events.SageFsEvent.DaemonSessionStopped
              {| SessionId = prev.SessionId; StoppedAt = DateTimeOffset.UtcNow |}
          ]
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
  (stateChangedEvent: Event<DaemonStateChange>) =
  let mutable lastStateJson = ""
  let mutable lastLoggedOutputCount = 0
  let mutable lastLoggedDiagCount = 0
  let getWarmupContextForElm (sessionId: string) : Async<SessionContext option> =
    async {
      try
        let! managed =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.GetSession(sessionId, reply))
        match managed with
        | Some s when s.WorkerBaseUrl.Length > 0 ->
          let! resp =
            httpClient.GetStringAsync(sprintf "%s/warmup-context" s.WorkerBaseUrl)
            |> Async.AwaitTask
          let warmup = WorkerProtocol.Serialization.deserialize<WarmupContext> resp
          let! sessions =
            sessionManager.PostAndAsyncReply(fun reply ->
              SessionManager.SessionCommand.ListSessions reply)
          let info = sessions |> List.tryFind (fun si -> si.Id = sessionId)
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
          }
        | _ -> return None
      with
      | :? System.IO.IOException as ex ->
        Log.error "[getWarmupContextForElm] IO error: %s" ex.Message
        return None
      | :? System.Net.Http.HttpRequestException as ex ->
        Log.error "[getWarmupContextForElm] HTTP error: %s" ex.Message
        return None
      | :? System.Threading.Tasks.TaskCanceledException ->
        return None
      | ex ->
        Log.error "[getWarmupContextForElm] Unexpected: %s (%s)" ex.Message (ex.GetType().Name)
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
        GetWarmupContext = Some getWarmupContextForElm
        GetStreamingTestProxy = fun sid ->
          let snapshot = readSnapshot()
          match Map.tryFind sid snapshot.WorkerBaseUrls with
          | Some url when url.Length > 0 ->
            Some (HttpWorkerClient.streamingTestProxyWithCoverage url)
          | _ -> None }
  ElmDaemon.start effectDeps (fun model _regions ->
    let activeBuf = model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)
    let outputCount = activeBuf.Count
    let diagCount =
      model.Diagnostics |> Map.values |> Seq.sumBy List.length
    try
      let json = SseDedupKey.fromModel model
      match json <> lastStateJson with
      | true ->
        lastStateJson <- json
        let significantOutputChange = abs (outputCount - lastLoggedOutputCount) >= 50
        let diagChanged = diagCount <> lastLoggedDiagCount
        match (not TerminalUIState.IsActive) && (significantOutputChange || diagChanged) with
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
        System.Threading.ThreadPool.QueueUserWorkItem(fun _ ->
          stateChangedEvent.Trigger (ModelChanged (outputCount, diagCount))) |> ignore
      | false -> ()
    with ex -> Log.error "[elm] State change propagation error: %s (%s)" ex.Message (ex.GetType().Name))

/// Run SageFs as a headless daemon.
/// MCP server + SessionManager + Dashboard — all frontends are clients.
/// Every session is a worker sub-process managed by SessionManager.
let run (mcpPort: int) (flags: Args.DaemonFlags) = task {
  let startupSw = System.Diagnostics.Stopwatch.StartNew()
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
  let persistence = infra.Persistence
  let daemonStreamId = infra.DaemonStreamId
  let mcpFetchTimeoutSec = infra.McpFetchTimeoutSec
  let dashboardFetchTimeoutSec = infra.DashboardFetchTimeoutSec
  let stateChangedEvent = infra.StateChangedEvent

  log.LogInformation("SageFs daemon v{Version} starting on port {Port}", version, mcpPort)

  let appendEventsAsync events = appendEventsAsync infra events

  // Handle --prune: mark all alive sessions as stopped and exit
  let! pruned = handlePrune infra flags
  match pruned with
  | true -> return ()
  | false -> ()

  use cts = infra.Cts
  // Test discovery callback — set after elmRuntime is created
  let mutable onTestDiscoveryCallback : (WorkerProtocol.SessionId -> Features.LiveTesting.TestCase array -> Features.LiveTesting.ProviderDescription list -> unit) =
    fun _ _ _ -> ()
  let mutable onInstrumentationMapsCallback : (WorkerProtocol.SessionId -> Features.LiveTesting.InstrumentationMap array -> unit) =
    fun _ _ -> ()
  let mutable onWarmupProgressCallback : (string -> string -> unit) =
    fun _ _ -> ()

  // Create SessionManager — the single source of truth for all sessions
  // Returns (mailbox, readSnapshot) — CQRS: reads go to snapshot, writes to mailbox
  let sessionManager, readSnapshot =
    SessionManager.create cts.Token
      (fun () -> stateChangedEvent.Trigger StandbyProgress)
      (fun sid tests providers -> onTestDiscoveryCallback sid tests providers)
      (fun sid maps -> onInstrumentationMapsCallback sid maps)
      (fun sid -> stateChangedEvent.Trigger (SessionReady sid))
      (fun sid progress -> onWarmupProgressCallback sid progress)

  let sessionOps = createSessionOps sessionManager readSnapshot (fun events -> appendEventsAsync events |> ignore)

  let noResume = flags.NoResume

  let workingDir = Environment.CurrentDirectory

  // resumeSessions delegates to module-level function with captured infra
  let resumeSessions onSessionResumed =
    resumePreviousSessions infra sessionOps workingDir onSessionResumed

  // Create EffectDeps from SessionManager + start Elm loop
  let elmRuntime = createElmRuntime sessionManager readSnapshot httpClient stateChangedEvent

  // Create a diagnostics-changed event (aggregated from workers)
  let diagnosticsChanged = Event<Features.DiagnosticsStore.T>()

  // Partially applied worker helpers (capture httpClient + readSnapshot)
  let getWorkerBaseUrl = getWorkerBaseUrl readSnapshot
  let fetchWorkerEndpoint sessionId path timeout parse =
    fetchWorkerEndpoint httpClient readSnapshot sessionId path timeout parse

  // Warmup context fetcher for MCP — uses session manager to find worker URL
  let getWarmupContextForMcp (sessionId: string) : System.Threading.Tasks.Task<WarmupContext option> =
    fetchWorkerEndpoint sessionId "/warmup-context" mcpFetchTimeoutSec
      (WorkerProtocol.Serialization.deserialize<WarmupContext>)

  // Hotreload state fetcher for MCP — returns watched file paths
  let getHotReloadStateForMcp (sessionId: string) : System.Threading.Tasks.Task<string list option> =
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
    elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.InstrumentationMapsReady (sid, maps)))

  // Wire warmup progress from SessionManager → Elm model (per-namespace granularity)
  onWarmupProgressCallback <- handleWarmupProgress elmRuntime.Dispatch

  // Shared binding scope snapshot — written by MCP server, read by dashboard
  let sharedBindingScope : SageFs.Features.BindingExplorer.BindingScopeSnapshot option ref = ref None

  // Start MCP server
  let mcpTask =
    McpServer.startMcpServer {
      DiagnosticsChanged = diagnosticsChanged.Publish
      StateChanged = Some stateChangedEvent.Publish
      Persistence = persistence
      Port = mcpPort
      SessionOps = sessionOps
      ElmRuntime = Some elmRuntime
      GetWarmupContext = Some getWarmupContextForMcp
      GetHotReloadState = Some getHotReloadStateForMcp
      SharedBindingScope = sharedBindingScope
    }

  // Test cycle tick timer — drives debounce channels for live testing (200ms interval)
  // Elmish-style batching means rapid ticks coalesce: N ticks → N updates → 1 render
  let testCycleTimer = new System.Threading.Timer(
    System.Threading.TimerCallback(fun _ ->
      elmRuntime.Dispatch(SageFsMsg.TestCycleTick DateTimeOffset.UtcNow)),
    null, 200, 200)

  // Periodic test cache save — crash recovery for test results.
  // Fires every 60s, only writes when RunGeneration has advanced since last save.
  let lastSavedGeneration = ref 0
  let cacheSaveTimer = new System.Threading.Timer(
    System.Threading.TimerCallback(fun _ ->
      periodicCacheSave log readSnapshot elmRuntime.GetModel lastSavedGeneration
      periodicManifestSave log readSnapshot),
    null, 60_000, 60_000)

  // Live testing file watcher — monitors *.fs and *.fsx changes
  let liveTestWatcher, liveTestDebounceTimer = createLiveTestWatcher workingDir elmRuntime.Dispatch

  // Start dashboard web server on MCP port + 1
  let dashboardPort = mcpPort + 1
  let connectionTracker = ConnectionTracker()

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
    GetSessionWorkingDir = getSessionWorkingDir
    GetActiveSessionId = fun () ->
      let model = elmRuntime.GetModel()
      ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.defaultValue ""
    GetElmRegions = fun () -> elmRuntime.GetRegions() |> Some
    GetPreviousSessions = fun () ->
      getPreviousSessions readSnapshot persistence daemonStreamId
    GetAllSessions = fun () -> task { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    GetStandbyInfo = sessionOps.GetStandbyInfo
    GetSessionStandbyInfo = fun sessionId ->
      (readSnapshot()).PerSessionStandby |> Map.tryFind sessionId |> Option.defaultValue StandbyInfo.NoPool
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
      let model = elmRuntime.GetModel()
      let state = model.LiveTesting.TestState
      let entries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession sessionId state
      match entries.Length with
      | 0 -> None
      | _ ->
        Features.LiveTesting.TestSummary.fromStatuses
          state.Activation (entries |> Array.map (fun e -> e.Status))
        |> Some
    GetSessionCoverageSummary = fun sessionId ->
      let model = elmRuntime.GetModel()
      let state = model.LiveTesting.TestState
      let sessionTestIds =
        match System.String.IsNullOrEmpty sessionId || Map.isEmpty state.TestSessionMap with
        | true -> state.TestCoverageBitmaps |> Map.keys |> Set.ofSeq
        | false ->
          state.TestSessionMap
          |> Map.toSeq
          |> Seq.choose (fun (tid, sid) ->
            match sid = sessionId with
            | true -> Some tid
            | false -> None)
          |> Set.ofSeq
      let bitmaps =
        sessionTestIds
        |> Seq.choose (fun tid -> Map.tryFind tid state.TestCoverageBitmaps)
        |> Seq.toArray
      match bitmaps.Length with
      | 0 -> None
      | _ ->
        Features.LiveTesting.CoverageSummary.fromBitmaps 16 (bitmaps |> Seq.ofArray)
        |> Some
    GetSessionTestTreemap = fun sessionId ->
      let model = elmRuntime.GetModel()
      let state = model.LiveTesting.TestState
      let entries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession sessionId state
      Features.LiveTesting.TestTreemap.fromStatusEntries entries
    GetSessionBindings = fun sessionId ->
      match sharedBindingScope.Value with
      | Some scope ->
        let activeId =
          SageFs.ActiveSession.sessionId (elmRuntime.GetModel().Sessions.ActiveSessionId)
          |> Option.defaultValue ""
        match activeId = sessionId with
        | true ->
          scope.ActiveBindings
          |> Map.values |> Array.ofSeq
        | false -> [||]
      | None -> [||]
    GetBindingScopeSnapshot = fun () -> sharedBindingScope.Value
    GetLiveTestingStatus = fun () ->
      let model = elmRuntime.GetModel()
      let activeId =
        SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
        |> Option.defaultValue ""
      SageFs.Features.LiveTesting.LiveTestCycleState.liveTestingStatusBarForSession activeId model.LiveTesting
  }

  let dashboardActions : DashboardActions = {
    EvalCode = fun sid code -> task {
      let! result = proxyToSession sessionOps.GetProxy sessionOps.NotifyWorkerDied sid (WorkerProtocol.WorkerMessage.EvalCode(code, "dash"))
      return
        match result with
        | Ok (WorkerProtocol.WorkerResponse.EvalResult(_, Ok msg, diags, _)) ->
          elmRuntime.Dispatch (SageFsMsg.Event (
            SageFsEvent.EvalCompleted (sid, msg, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic)))
          Ok msg
        | Ok (WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _, _)) ->
          let msg = SageFsError.describe err
          elmRuntime.Dispatch (SageFsMsg.Event (SageFsEvent.EvalFailed (sid, msg)))
          Error msg
        | Ok other -> Error (sprintf "Unexpected: %A" other)
        | Error e -> Error (SageFsError.describe e)
    }
    ResetSession = fun sid -> task {
      let! result = proxyToSession sessionOps.GetProxy sessionOps.NotifyWorkerDied sid (WorkerProtocol.WorkerMessage.ResetSession "dash")
      return
        match result with
        | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Ok ())) -> Ok "Session reset successfully"
        | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Error e)) -> Error (sprintf "Reset failed: %A" e)
        | Ok other -> Error (sprintf "Unexpected: %A" other)
        | Error e -> Error (SageFsError.describe e)
    }
    HardResetSession = fun sid -> task {
      match sid with
      | null | "" -> return Error (SageFsError.describe (SageFsError.SessionNotFound ""))
      | _ ->
        let! result = sessionOps.RestartSession sid true
        return
          result
          |> Result.map (sprintf "Hard reset: %s")
          |> Result.mapError (fun e -> sprintf "Hard reset failed: %s" (SageFsError.describe e))
    }
    Dispatch = fun msg -> elmRuntime.Dispatch msg
    SwitchSession = Some (fun (sid: string) -> task {
      elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.SessionSwitched (None, sid)))
      return Ok (sprintf "Switched to session '%s'" sid)
    })
    StopSession = Some (fun (sid: string) -> task {
      let! result = sessionOps.StopSession sid
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.mapError SageFsError.describe
    })
    CreateSession = Some (fun (projects: string list) (workingDir: string) -> task {
      let! result = sessionOps.CreateSession projects workingDir
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.mapError SageFsError.describe
    })
    ShutdownCallback = Some (fun () -> cts.Cancel())
  }

  let dashboardInfra : DashboardInfra = {
    Version = version
    StateChanged = Some stateChangedEvent.Publish
    ConnectionTracker = Some connectionTracker
    SessionThemes = sessionThemes
    GetCompletions = Some (fun (sessionId: string) (code: string) (cursorPos: int) -> task {
      match String.IsNullOrEmpty(sessionId) with
      | true -> return []
      | false ->
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
        Log.error "[getCompletions] Error for session: %s (%s)" ex.Message (ex.GetType().Name)
        return []
    })
  }

  let dashboardEndpoints =
    Dashboard.createEndpoints dashboardQueries dashboardActions dashboardInfra

  let hotReloadProxyEndpoints = createHotReloadProxyEndpoints getWorkerBaseUrl httpClient stateChangedEvent

  let dashboardTask =
    startDashboardServer log dashboardPort (dashboardEndpoints @ hotReloadProxyEndpoints)

  // Workers handle their own warmup, middleware, and file watching.
  // The daemon just needs to wait for the MCP and dashboard servers.

  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    log.LogInformation("Shutting down...")
    // Start a watchdog — if graceful shutdown takes too long, force exit
    System.Threading.Tasks.Task.Delay(5000).ContinueWith(fun (_: System.Threading.Tasks.Task) ->
      log.LogWarning("Graceful shutdown timed out — forcing exit")
      Environment.Exit(1)) |> ignore
    try cts.Cancel() with :? ObjectDisposedException -> ())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    log.LogInformation("Daemon stopped"))

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

  // Cleanup orphaned .tmp files from interrupted writes
  let stcOrphans = Features.TestCacheFile.cleanupOrphanedTmpFiles DaemonState.SageFsDir
  let sfsOrphans = Features.SessionFile.cleanupOrphanedTmpFiles DaemonState.SageFsDir
  match stcOrphans + sfsOrphans > 0 with
  | true ->
    Instrumentation.persistenceOrphanedTmpCleanup.Add(int64 stcOrphans, System.Collections.Generic.KeyValuePair("format", box "stc1"))
    Instrumentation.persistenceOrphanedTmpCleanup.Add(int64 sfsOrphans, System.Collections.Generic.KeyValuePair("format", box "sfs3"))
    log.LogInformation("Cleaned up {Count} orphaned .tmp files ({Stc} .sagetc, {Sfs} .sagefs)",
      stcOrphans + sfsOrphans, stcOrphans, sfsOrphans)
  | false -> ()

  // Test cache pre-loading happens per-session after resume, not eagerly

  // Resume sessions in background — don't block the daemon main task.
  // Each resumed session dispatches ListSessions so dashboard sees them incrementally.
  let _resumeTask =
    match noResume with
    | true ->
      log.LogInformation("Session resume skipped (--no-resume)")
      System.Threading.Tasks.Task.CompletedTask
    | false ->
      System.Threading.Tasks.Task.Run(fun () ->
        task {
          try
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
                match gen > lastSavedGeneration.Value with
                | true -> lastSavedGeneration.Value <- gen
                | false -> ()
              | Error msg -> log.LogDebug("No test cache available: {Reason}", msg)
          with ex ->
            log.LogWarning("Session resume failed: {Error}", ex.Message)
        } :> System.Threading.Tasks.Task)

  // Periodic status polling — refreshes session status (Starting → Ready)
  // so SSE subscribers see warmup progress in real time.
  let _statusPollTask =
    System.Threading.Tasks.Task.Run(fun () ->
      task {
        try
          while not cts.Token.IsCancellationRequested do
            do! System.Threading.Tasks.Task.Delay(2000, cts.Token)
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
  testCycleTimer.Dispose()
  cacheSaveTimer.Dispose()
  liveTestDebounceTimer.Dispose()
  liveTestWatcher.EnableRaisingEvents <- false
  liveTestWatcher.Dispose()
  try
    performGracefulShutdown log readSnapshot elmRuntime.GetModel persistence daemonStreamId appendEventsAsync sessionManager
  with ex ->
    log.LogWarning("Shutdown cleanup error: {Error}", ex.Message)
}
