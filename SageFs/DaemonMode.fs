module SageFs.Server.DaemonMode

open System
open System.Threading
open SageFs
open SageFs.Server
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
let proxyToSession
  (getProxy: string -> Threading.Tasks.Task<(WorkerProtocol.WorkerMessage -> Async<WorkerProtocol.WorkerResponse>) option>)
  (sid: string)
  (msg: WorkerProtocol.WorkerMessage)
  : Threading.Tasks.Task<Result<WorkerProtocol.WorkerResponse, SageFsError>> = task {
  match sid with
  | null | "" -> return Error (SageFsError.SessionNotFound (sid |> Option.ofObj |> Option.defaultValue ""))
  | _ ->
    try
      let! proxy = getProxy sid
      match proxy with
      | Some send ->
        let! resp = send msg |> Async.StartAsTask
        return Ok resp
      | None -> return Error (SageFsError.WorkerCommunicationFailed(sid, "No proxy available for session"))
    with
    | :? IO.IOException as ex ->
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe broken — %s" ex.Message))
    | :? AggregateException as ae when (ae.InnerException :? IO.IOException) ->
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe broken — %s" ae.InnerException.Message))
    | :? ObjectDisposedException as ex ->
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe closed — %s" ex.Message))
}

/// Run SageFs as a headless daemon.
/// MCP server + SessionManager + Dashboard — all frontends are clients.
/// Every session is a worker sub-process managed by SessionManager.
let run (mcpPort: int) (args: Args.Arguments list) = task {
  let version = DaemonInfo.version

  // Create structured logger for daemon lifecycle (flows to OTEL when configured)
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
          otel.AddOtlpExporter() |> ignore
        ) |> ignore
      | false -> ()
    )
  let log = loggerFactory.CreateLogger("SageFs.Daemon")
  // Shared HttpClient for all daemon→worker HTTP calls (avoids socket exhaustion)
  let httpClient = new Net.Http.HttpClient()

  /// Timeout for agent-facing worker fetches (MCP tools, SSE session events).
  let mcpFetchTimeoutSec = 5.0
  /// Timeout for user-facing worker fetches (dashboard responses).
  let dashboardFetchTimeoutSec = 2.0

  log.LogInformation("SageFs daemon v{Version} starting on port {Port}", version, mcpPort)

  // Set up persistence: InMemory by default, PostgreSQL with --persist or SAGEFS_CONNECTION_STRING
  let hasPersist = args |> List.exists (function Args.Arguments.Persist -> true | _ -> false)
  let envConnStr = System.Environment.GetEnvironmentVariable("SAGEFS_CONNECTION_STRING")
  let persistence =
    match hasPersist || not (System.String.IsNullOrEmpty envConnStr) with
    | true ->
      let connectionString =
        match System.String.IsNullOrEmpty envConnStr with
        | false ->
          log.LogInformation("Using PostgreSQL from SAGEFS_CONNECTION_STRING")
          Some envConnStr
        | true ->
          log.LogInformation("--persist: starting PostgreSQL via Docker...")
          PostgresInfra.getOrStartPostgres ()
      match connectionString with
      | Some connStr ->
        let store = SageFs.EventStore.configureStore connStr
        log.LogInformation("Event persistence: PostgreSQL")
        SageFs.EventStore.EventPersistence.postgres store
      | None ->
        log.LogWarning("PostgreSQL unavailable, falling back to InMemory mode")
        SageFs.EventStore.EventPersistence.inMemory ()
    | false ->
      log.LogInformation("Event persistence: InMemory (use --persist for PostgreSQL)")
      SageFs.EventStore.EventPersistence.inMemory ()
  let daemonStreamId = "daemon-sessions"

  // Handle --prune: mark all alive sessions as stopped and exit
  match args |> List.exists (function Args.Arguments.Prune -> true | _ -> false) with
  | true ->
    let! daemonEvents = persistence.FetchStream daemonStreamId
    let daemonState = Features.Replay.DaemonReplayState.replayStream daemonEvents
    let pruneEvents = Features.Replay.DaemonReplayState.pruneAllSessions daemonState
    match pruneEvents.IsEmpty with
    | true ->
      log.LogInformation("No alive sessions to prune")
    | false ->
      let! result = persistence.AppendEvents daemonStreamId pruneEvents
      match result with
      | Ok () -> log.LogInformation("Pruned {Count} session(s)", pruneEvents.Length)
      | Error msg -> log.LogWarning("Prune failed: {Error}", msg)
    return ()
  | false -> ()

  // Handle shutdown signals
  use cts = new CancellationTokenSource()

  // Create state-changed event for SSE subscribers (created early so SessionManager can trigger it)
  let stateChangedEvent = Event<DaemonStateChange>()
  let mutable lastStateJson = ""
  let mutable lastLoggedOutputCount = 0
  let mutable lastLoggedDiagCount = 0
  // Test discovery callback — set after elmRuntime is created
  let mutable onTestDiscoveryCallback : (WorkerProtocol.SessionId -> Features.LiveTesting.TestCase array -> Features.LiveTesting.ProviderDescription list -> unit) =
    fun _ _ _ -> ()
  let mutable onInstrumentationMapsCallback : (WorkerProtocol.SessionId -> Features.LiveTesting.InstrumentationMap array -> unit) =
    fun _ _ -> ()

  // Create SessionManager — the single source of truth for all sessions
  // Returns (mailbox, readSnapshot) — CQRS: reads go to snapshot, writes to mailbox
  let sessionManager, readSnapshot =
    SessionManager.create cts.Token
      (fun () -> stateChangedEvent.Trigger StandbyProgress)
      (fun sid tests providers -> onTestDiscoveryCallback sid tests providers)
      (fun sid maps -> onInstrumentationMapsCallback sid maps)
      (fun sid -> stateChangedEvent.Trigger (SessionReady sid))

  // Active session ID — REMOVED: No global shared session.
  // Each client (MCP, TUI, dashboard) tracks its own session independently.
  // MCP uses McpContext.SessionMap. UIs pass ?sessionId= in SSE URL.

  let sessionOps : SessionManagementOps = {
    CreateSession = fun projects workingDir ->
      task {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.CreateSession(projects, workingDir, reply))
          |> Async.StartAsTask
        match result with
        | Ok info ->
          let! _ = persistence.AppendEvents daemonStreamId [
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
        // Persist stop event
        let! _ = persistence.AppendEvents daemonStreamId [
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
      // CQRS read path — lock-free snapshot, no mailbox blocking
      task { return HttpWorkerClient.proxyFromUrls sessionId (readSnapshot()).WorkerBaseUrls }
    GetSessionInfo = fun sessionId ->
      // CQRS read path — lock-free snapshot, no mailbox blocking
      task { return SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) }
    GetAllSessions = fun () ->
      // CQRS read path — lock-free snapshot, no mailbox blocking
      task { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    GetStandbyInfo = fun () ->
      // CQRS read path — lock-free snapshot, no mailbox blocking
      task { return (readSnapshot()).StandbyInfo }
  }

  let noResume = args |> List.exists (function Args.Arguments.No_Resume -> true | _ -> false)

  // Parse initial projects from CLI args (used if no previous sessions)
  let initialProjects =
    args
    |> List.choose (function
      | Args.Arguments.Proj p -> Some p
      | _ -> None)
  let workingDir = Environment.CurrentDirectory

  // Session resume runs AFTER servers start (deferred below).
  // This ensures MCP + dashboard are listening before workers spawn.
  let resumeSessions (onSessionResumed: unit -> unit) = task {
    let startupSw = System.Diagnostics.Stopwatch.StartNew()
    let startupSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.startup" []

    // Replay phase
    let replaySpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.event_replay" []
    let! daemonEvents = persistence.FetchStream daemonStreamId
    let daemonState = Features.Replay.DaemonReplayState.replayStream daemonEvents
    let eventCount = daemonEvents.Length
    Instrumentation.daemonReplayEventCount.Add(int64 eventCount)
    match isNull replaySpan with
    | false -> replaySpan.SetTag("event_count", eventCount) |> ignore
    | true -> ()
    Instrumentation.succeedSpan replaySpan

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
        let! _ = persistence.AppendEvents daemonStreamId [
          Features.Events.SageFsEvent.DaemonSessionStopped
            {| SessionId = staleId; StoppedAt = DateTimeOffset.UtcNow |}
        ]
        ()
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
        log.LogWarning("Skipping session {SessionId} — directory {WorkingDir} no longer exists", prev.SessionId, prev.WorkingDir)
        let! _ = persistence.AppendEvents daemonStreamId [
          Features.Events.SageFsEvent.DaemonSessionStopped
            {| SessionId = prev.SessionId; StoppedAt = DateTimeOffset.UtcNow |}
        ]
        ()
      // Resume all valid sessions in parallel — each is an independent worker process
      let resumeSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.session_resume" []
      let resumeTasks =
        existing
        |> List.map (fun prev -> task {
          log.LogInformation("Resuming session for {WorkingDir}", prev.WorkingDir)
          let! result = sessionOps.CreateSession prev.Projects prev.WorkingDir
          match result with
          | Ok info ->
            Instrumentation.daemonSessionsResumed.Add(1L)
            // Stop the OLD session ID so it doesn't resurrect on next restart
            let! _ = persistence.AppendEvents daemonStreamId [
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
      | false -> resumeSpan.SetTag("resumed_count", existing.Length) |> ignore
      | true -> ()
      Instrumentation.succeedSpan resumeSpan

      // Sessions restored — clients will discover them via listing
      // No global "active session" to restore; each client picks its own
      match daemonState.ActiveSessionId with
      | Some _ -> () // Previously tracked active session — clients resolve on connect
      | None -> ()
    | true ->
      match initialProjects.IsEmpty with
      | false ->
        // Multi-project: create one session per project for independent workers
        log.LogInformation("No previous sessions. Creating {Count} session(s) from --proj args", initialProjects.Length)
        let createTasks =
          initialProjects
          |> List.map (fun proj -> task {
            let! result = sessionOps.CreateSession [ proj ] workingDir
            match result with
            | Ok info ->
              Instrumentation.daemonSessionsResumed.Add(1L)
              log.LogInformation("Created session for {Project}: {Info}", proj, info)
              onSessionResumed ()
            | Error err ->
              log.LogWarning("Failed to create session for {Project}: {Error}", proj, err)
          })
        do! System.Threading.Tasks.Task.WhenAll(createTasks) :> System.Threading.Tasks.Task
      | true ->
        log.LogInformation("No previous sessions to resume. Waiting for clients to create sessions")

    startupSw.Stop()
    Instrumentation.daemonStartupMs.Record(startupSw.Elapsed.TotalMilliseconds)
    Instrumentation.succeedSpan startupSpan
  }

  // Create EffectDeps from SessionManager + start Elm loop
  let getWarmupContextForElm (sessionId: string) : Async<SessionContext option> =
    async {
      try
        let! managed =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.GetSession(sessionId, reply))
        match managed with
        | Some s when s.WorkerBaseUrl.Length > 0 ->
          let client = httpClient
          let! resp =
            client.GetStringAsync(sprintf "%s/warmup-context" s.WorkerBaseUrl)
            |> Async.AwaitTask
          let warmup = WorkerProtocol.Serialization.deserialize<WarmupContext> resp
          let! sessions =
            sessionManager.PostAndAsyncReply(fun reply ->
              SessionManager.SessionCommand.ListSessions reply)
          let info = sessions |> List.tryFind (fun si -> si.Id = sessionId)
          let ctx : SessionContext = {
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
          return Some ctx
        | _ -> return None
      with
      | :? System.IO.IOException as ex ->
        eprintfn "[getWarmupContextForElm] IO error: %s" ex.Message
        return None
      | :? System.Net.Http.HttpRequestException as ex ->
        eprintfn "[getWarmupContextForElm] HTTP error: %s" ex.Message
        return None
      | :? System.Threading.Tasks.TaskCanceledException ->
        return None
      | ex ->
        eprintfn "[getWarmupContextForElm] Unexpected: %s (%s)" ex.Message (ex.GetType().Name)
        return None
    }
  let effectDeps =
    { ElmDaemon.createEffectDeps sessionManager readSnapshot with
        GetWarmupContext = Some getWarmupContextForElm
        GetStreamingTestProxy = fun sid ->
          let snapshot = readSnapshot()
          match Map.tryFind sid snapshot.WorkerBaseUrls with
          | Some url when url.Length > 0 ->
            Some (HttpWorkerClient.streamingTestProxyWithCoverage url)
          | _ -> None }
  let elmRuntime =
    ElmDaemon.start effectDeps (fun model _regions ->
      let outputCount = model.RecentOutput.Length
      let diagCount =
        model.Diagnostics |> Map.values |> Seq.sumBy List.length
      // Fire SSE event with summary JSON — deduplicated
      try
        let json = SseDedupKey.fromModel model
        match json <> lastStateJson with
        | true ->
          lastStateJson <- json
          let outputChanged = outputCount <> lastLoggedOutputCount
          let diagChanged = diagCount <> lastLoggedDiagCount
          match (not TerminalUIState.IsActive) && (outputChanged || diagChanged) with
          | true ->
            lastLoggedOutputCount <- outputCount
            lastLoggedDiagCount <- diagCount
            let latest =
              model.RecentOutput
              |> List.tryHead
              |> Option.map (fun o -> o.Text)
              |> Option.defaultValue ""
            eprintfn "\x1b[36m[elm]\x1b[0m output=%d diags=%d | %s"
              outputCount diagCount latest
          | false -> ()
          stateChangedEvent.Trigger (ModelChanged json)
        | false -> ()
      with ex -> eprintfn "[elm] State change propagation error: %s (%s)" ex.Message (ex.GetType().Name))

  // Create a diagnostics-changed event (aggregated from workers)
  let diagnosticsChanged = Event<Features.DiagnosticsStore.T>()

  let getWorkerBaseUrl (sid: string) =
    let snapshot = readSnapshot()
    match Map.tryFind sid snapshot.WorkerBaseUrls with
    | Some url when url.Length > 0 -> Some url
    | _ -> None

  /// Fetch JSON from a worker endpoint with timeout, returning None on failure.
  let fetchWorkerEndpoint (sessionId: string) (path: string) (timeout: float) (parse: string -> 'T) : Threading.Tasks.Task<'T option> = task {
    match getWorkerBaseUrl sessionId with
    | Some baseUrl ->
      try
        use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout))
        let! resp = httpClient.GetStringAsync(sprintf "%s%s" baseUrl path, cts.Token)
        return Some (parse resp)
      with
      | :? Threading.Tasks.TaskCanceledException ->
        eprintfn "[fetchWorkerEndpoint] Timeout (%.0fs) fetching %s for session %s" timeout path sessionId
        return None
      | :? Net.Http.HttpRequestException as ex ->
        eprintfn "[fetchWorkerEndpoint] HTTP error fetching %s for session %s: %s" path sessionId ex.Message
        return None
      | ex ->
        eprintfn "[fetchWorkerEndpoint] Unexpected error fetching %s for session %s: %s" path sessionId (ex.GetType().Name)
        return None
    | None -> return None
  }

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
  // After discovery, scan project source files with tree-sitter to produce
  // SourceTestLocations, enabling source-mapped origins for gutter signs etc.
  onTestDiscoveryCallback <- fun sid tests providers ->
    // Scan project directories for test source locations via tree-sitter
    let snapshot = readSnapshot()
    let sessionInfo = SessionManager.QuerySnapshot.tryGetSession sid snapshot
    let projectDirs =
      match sessionInfo with
      | Some info ->
        info.Projects
        |> List.map (fun proj ->
          let fullPath = match IO.Path.IsPathRooted proj with | true -> proj | false -> IO.Path.Combine(info.WorkingDirectory, proj)
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
              with _ -> Array.empty)
          | false -> Array.empty)
      | false -> Array.empty
    // Dispatch locations BEFORE tests so mergeSourceLocations can enrich them
    match Array.isEmpty locations with
    | false -> elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.TestLocationsDetected (sid, locations)))
    | true -> ()
    match Array.isEmpty tests with
    | false -> elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.TestsDiscovered (sid, tests)))
    | true -> ()
    match List.isEmpty providers with
    | false -> elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.ProvidersDetected providers))
    | true -> ()

  // Wire instrumentation maps from SessionManager → Elm model
  onInstrumentationMapsCallback <- fun sid maps ->
    elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.InstrumentationMapsReady (sid, maps)))

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
    }

  // Pipeline tick timer — drives debounce channels for live testing (50ms fixed interval)
  let pipelineTimer = new System.Threading.Timer(
    System.Threading.TimerCallback(fun _ ->
      elmRuntime.Dispatch(SageFsMsg.PipelineTick DateTimeOffset.UtcNow)),
    null, 50, 50)

  // Live testing file watcher — monitors *.fs and *.fsx changes, dispatches FileContentChanged.
  // Uses timer-based debounce with per-path deduplication (same pattern as FileWatcher.start).
  // File content is read in the debounced callback, NOT in the raw FSW handler.
  let mutable liveTestPendingPaths : Set<string> = Set.empty
  let liveTestWatcherLock = obj()

  let liveTestDebounceCallback _ =
    let paths =
      lock liveTestWatcherLock (fun () ->
        let ps = liveTestPendingPaths
        liveTestPendingPaths <- Set.empty
        ps)
    for path in paths do
      try
        let fi = System.IO.FileInfo(path)
        match fi.Exists && fi.Length < 1_048_576L with
        | true ->
          let content = System.IO.File.ReadAllText(path)
          elmRuntime.Dispatch(SageFsMsg.FileContentChanged(path, content))
        | false -> ()
      with
      | :? System.IO.IOException -> ()
      | :? System.UnauthorizedAccessException -> ()

  let liveTestDebounceTimer = new System.Threading.Timer(
    System.Threading.TimerCallback(liveTestDebounceCallback), null,
    System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)

  let handleFileChanged (e: System.IO.FileSystemEventArgs) =
    let path = e.FullPath
    match SageFs.FileWatcher.shouldTriggerRebuild
        { Directories = [workingDir]; Extensions = [".fs"; ".fsx"]; ExcludePatterns = []; DebounceMs = 200 }
        path with
    | true ->
      lock liveTestWatcherLock (fun () ->
        liveTestPendingPaths <- liveTestPendingPaths |> Set.add path
        liveTestDebounceTimer.Change(200, System.Threading.Timeout.Infinite) |> ignore)
    | false -> ()

  let liveTestWatcher = new System.IO.FileSystemWatcher(workingDir)
  liveTestWatcher.IncludeSubdirectories <- true
  liveTestWatcher.NotifyFilter <- System.IO.NotifyFilters.LastWrite
  liveTestWatcher.Filters.Add("*.fs")
  liveTestWatcher.Filters.Add("*.fsx")
  liveTestWatcher.Changed.Add(handleFileChanged)
  liveTestWatcher.Created.Add(handleFileChanged)
  liveTestWatcher.EnableRaisingEvents <- true

  // Start dashboard web server on MCP port + 1
  let dashboardPort = mcpPort + 1
  let connectionTracker = ConnectionTracker()
  // Dashboard status helpers — read from CQRS snapshot (non-blocking, lock-free).
  // Only proxy calls (eval stats) go async; state/workingDir/statusMsg/workerBaseUrl use snapshot.
  let tryGetSessionSnapshotAsync (sid: string) = task {
    // CQRS: bypass MailboxProcessor — use snapshot + direct HTTP to worker
    let snapshot = readSnapshot()
    match SessionManager.QuerySnapshot.tryGetSession sid snapshot, Map.tryFind sid snapshot.WorkerBaseUrls with
    | Some _, Some baseUrl when baseUrl.Length > 0 ->
      try
        use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(2.0))
        let! resp = httpClient.GetStringAsync(sprintf "%s/status?replyId=dash" baseUrl, cts.Token)
        use doc = Text.Json.JsonDocument.Parse(resp)
        let root = doc.RootElement
        let snap : WorkerProtocol.WorkerStatusSnapshot = {
          Status =
            match root.GetProperty("status").GetString() with
            | "Ready" -> WorkerProtocol.SessionStatus.Ready
            | "Evaluating" -> WorkerProtocol.SessionStatus.Evaluating
            | _ -> WorkerProtocol.SessionStatus.Starting
          StatusMessage =
            match root.TryGetProperty("statusMessage") with
            | true, v when v.ValueKind <> Text.Json.JsonValueKind.Null -> Some (v.GetString())
            | _ -> None
          EvalCount =
            match root.TryGetProperty("evalCount") with
            | true, v -> v.GetInt32()
            | false, _ -> 0
          AvgDurationMs =
            match root.TryGetProperty("avgDurationMs") with
            | true, v -> v.GetInt64()
            | false, _ -> 0L
          MinDurationMs =
            match root.TryGetProperty("minDurationMs") with
            | true, v -> v.GetInt64()
            | false, _ -> 0L
          MaxDurationMs =
            match root.TryGetProperty("maxDurationMs") with
            | true, v -> v.GetInt64()
            | false, _ -> 0L
        }
        return Some snap
      with
      | :? System.Net.Http.HttpRequestException -> return None
      | :? Threading.Tasks.TaskCanceledException -> return None
      | :? Text.Json.JsonException as ex ->
        eprintfn "[tryGetSessionSnapshot] JSON parse error for %s: %s" sid ex.Message
        return None
      | ex ->
        eprintfn "[tryGetSessionSnapshot] Unexpected error for %s: %s (%s)" sid ex.Message (ex.GetType().Name)
        return None
    | _ -> return None
  }

  let getSessionState (sid: string) =
    match String.IsNullOrEmpty(sid) with
    | true -> SessionState.Uninitialized
    | false ->
      let snapshot = readSnapshot()
      match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
      | Some info -> WorkerProtocol.SessionStatus.toSessionState info.Status
      | None -> SessionState.Uninitialized

  let getEvalStatsAsync (sid: string) = task {
    // CQRS: bypass MailboxProcessor — call worker HTTP directly
    let snapshot = readSnapshot()
    match Map.tryFind sid snapshot.WorkerBaseUrls with
    | Some baseUrl when baseUrl.Length > 0 ->
      try
        use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(2.0))
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
      | :? System.Net.Http.HttpRequestException | :? Threading.Tasks.TaskCanceledException -> return Affordances.EvalStats.empty
      | :? Text.Json.JsonException as ex ->
        eprintfn "[getEvalStats] JSON parse error for %s: %s" sid ex.Message
        return Affordances.EvalStats.empty
      | ex ->
        eprintfn "[getEvalStats] Unexpected error for %s: %s (%s)" sid ex.Message (ex.GetType().Name)
        return Affordances.EvalStats.empty
    | _ -> return Affordances.EvalStats.empty
  }

  let getSessionWorkingDir (sid: string) =
    let snapshot = readSnapshot()
    match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
    | Some info -> info.WorkingDirectory
    | None -> ""

  let getAllSessions () = task {
    return SessionManager.QuerySnapshot.allSessions (readSnapshot())
  }

  let getStatusMsg (sid: string) =
    readSnapshot().WarmupProgress |> Map.tryFind sid


  let sessionThemes = Dashboard.loadThemes DaemonState.SageFsDir

  let dashboardQueries : Dashboard.DashboardQueries = {
    GetSessionState = getSessionState
    GetStatusMsg = getStatusMsg
    GetEvalStats = getEvalStatsAsync
    GetSessionWorkingDir = getSessionWorkingDir
    GetActiveSessionId = fun () ->
      let model = elmRuntime.GetModel()
      ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.defaultValue ""
    GetElmRegions = fun () -> elmRuntime.GetRegions() |> Some
    GetPreviousSessions = fun () -> task {
      // Active sessions from CQRS snapshot (non-blocking)
      let snapshot = readSnapshot()
      let activeSessions =
        SessionManager.QuerySnapshot.allSessions snapshot
        |> List.map (fun (info: WorkerProtocol.SessionInfo) ->
          { Dashboard.PreviousSession.Id = info.Id
            Dashboard.PreviousSession.WorkingDir = info.WorkingDirectory
            Dashboard.PreviousSession.Projects = info.Projects
            Dashboard.PreviousSession.LastSeen = info.LastActivity })
      let activeIds = activeSessions |> List.map (fun s -> s.Id) |> Set.ofList
      // Historical sessions from Marten (stopped ones not currently active)
      let! historicalSessions = task {
        try
          let! events = persistence.FetchStream daemonStreamId
          let daemonState = Features.Replay.DaemonReplayState.replayStream events
          return
            daemonState.Sessions
            |> Map.values
            |> Seq.filter (fun r -> r.StoppedAt.IsSome && not (activeIds.Contains r.SessionId))
            |> Seq.map (fun r ->
              { Dashboard.PreviousSession.Id = r.SessionId
                Dashboard.PreviousSession.WorkingDir = r.WorkingDir
                Dashboard.PreviousSession.Projects = r.Projects
                Dashboard.PreviousSession.LastSeen = r.StoppedAt |> Option.map (fun t -> t.DateTime) |> Option.defaultValue r.CreatedAt.DateTime })
            |> Seq.toList
        with
        | :? Marten.Exceptions.MartenException as ex ->
          eprintfn "[getPreviousSessions] Marten error: %s" ex.Message
          return []
        | ex ->
          eprintfn "[getPreviousSessions] Unexpected error: %s (%s)" ex.Message (ex.GetType().Name)
          return []
      }
      return activeSessions @ historicalSessions
    }
    GetAllSessions = getAllSessions
    GetStandbyInfo = sessionOps.GetStandbyInfo
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
    GetPipelineTrace = fun () ->
      let model = elmRuntime.GetModel()
      let activeId =
        SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
        |> Option.defaultValue ""
      let state = model.LiveTesting.TestState
      let sessionEntries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession activeId state
      let summary =
        Features.LiveTesting.TestSummary.fromStatuses
          state.Activation (sessionEntries |> Array.map (fun e -> e.Status))
      Some {| Timing = model.LiveTesting.LastTiming
              IsRunning = Features.LiveTesting.TestRunPhase.isAnyRunning state.RunPhases
              Summary = summary |}
    GetLiveTestingStatus = fun () ->
      let model = elmRuntime.GetModel()
      let activeId =
        SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
        |> Option.defaultValue ""
      SageFs.Features.LiveTesting.LiveTestPipelineState.liveTestingStatusBarForSession activeId model.LiveTesting
  }

  let dashboardActions : Dashboard.DashboardActions = {
    EvalCode = fun sid code -> task {
      let! result = proxyToSession sessionOps.GetProxy sid (WorkerProtocol.WorkerMessage.EvalCode(code, "dash"))
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
      let! result = proxyToSession sessionOps.GetProxy sid (WorkerProtocol.WorkerMessage.ResetSession "dash")
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
          match result with
          | Ok msg -> Ok (sprintf "Hard reset: %s" msg)
          | Error e -> Error (sprintf "Hard reset failed: %s" (SageFsError.describe e))
    }
    Dispatch = fun msg -> elmRuntime.Dispatch msg
    SwitchSession = Some (fun (sid: string) -> task {
      elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.SessionSwitched (None, sid)))
      return Ok (sprintf "Switched to session '%s'" sid)
    })
    StopSession = Some (fun (sid: string) -> task {
      let! result = sessionOps.StopSession sid
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return
        match result with
        | Ok msg -> Ok msg
        | Error e -> Error (SageFsError.describe e)
    })
    CreateSession = Some (fun (projects: string list) (workingDir: string) -> task {
      let! result = sessionOps.CreateSession projects workingDir
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return
        match result with
        | Ok msg -> Ok msg
        | Error e -> Error (SageFsError.describe e)
    })
    ShutdownCallback = Some (fun () -> cts.Cancel())
  }

  let dashboardInfra : Dashboard.DashboardInfra = {
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
        eprintfn "[getCompletions] Error for session: %s (%s)" ex.Message (ex.GetType().Name)
        return []
    })
  }

  let dashboardEndpoints =
    Dashboard.createEndpoints dashboardQueries dashboardActions dashboardInfra

  // Hot-reload proxy endpoints — forward to worker HTTP servers
  let hotReloadProxyEndpoints : HttpEndpoint list =
    let proxyToWorker (sid: string) (workerPath: string) (httpCall: string -> Threading.Tasks.Task<string * int * bool>) (ctx: Microsoft.AspNetCore.Http.HttpContext) = task {
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
    let proxyGet (sid: string) (workerPath: string) (ctx: Microsoft.AspNetCore.Http.HttpContext) =
      proxyToWorker sid workerPath (fun url -> task {
        let! resp = httpClient.GetStringAsync(url)
        return (resp, 200, false)
      }) ctx
    let proxyPost (sid: string) (workerPath: string) (ctx: Microsoft.AspNetCore.Http.HttpContext) =
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

  let dashboardTask = task {
    try
      let builder = WebApplication.CreateBuilder()
      // Suppress ASP.NET Core info logging (routing, hosting) for dashboard
      builder.Logging
        .AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
        .AddFilter("Microsoft.Hosting", LogLevel.Warning)
      |> ignore
      // Response compression: Brotli at fastest level for dashboard SSE + JSON
      builder.Services.AddResponseCompression(fun opts ->
        opts.EnableForHttps <- true
        opts.MimeTypes <- ResponseCompressionDefaults.MimeTypes |> Seq.append ["text/event-stream"]
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
      app.UseRouting().UseFalco(dashboardEndpoints @ hotReloadProxyEndpoints) |> ignore
      log.LogInformation("Dashboard available at http://localhost:{Port}/dashboard", dashboardPort)
      do! app.RunAsync()
    with ex ->
      log.LogWarning("Dashboard failed to start: {Error}", ex.Message)
  }

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
  log.LogInformation("SageFs daemon ready (PID {Pid}, MCP port {McpPort}, dashboard port {DashboardPort})",
    Environment.ProcessId, mcpPort, dashboardPort)
  log.LogInformation("Dashboard: http://localhost:{Port}/dashboard", dashboardPort)
  log.LogInformation("SSE events: http://localhost:{Port}/events", mcpPort)
  log.LogInformation("Health: http://localhost:{Port}/health", mcpPort)

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

  // Graceful shutdown: stop pipeline timer, file watcher, and all sessions
  pipelineTimer.Dispose()
  liveTestDebounceTimer.Dispose()
  liveTestWatcher.EnableRaisingEvents <- false
  liveTestWatcher.Dispose()
  try
    // CQRS: read from snapshot (non-blocking), then command to stop
    let activeSessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
    for info in activeSessions do
      let! _ = persistence.AppendEvents daemonStreamId [
        Features.Events.SageFsEvent.DaemonSessionStopped
          {| SessionId = info.Id; StoppedAt = DateTimeOffset.UtcNow |}
      ]
      ()
    // Stop all workers with a timeout — don't block forever if a worker is hung
    let stopTask =
      sessionManager.PostAndAsyncReply(fun reply ->
        SessionManager.SessionCommand.StopAll reply)
      |> Async.StartAsTask
    match stopTask.Wait(TimeSpan.FromSeconds(3.0)) with
    | false -> log.LogWarning("StopAll timed out — some workers may not have stopped cleanly")
    | true -> ()
  with ex ->
    log.LogWarning("Shutdown cleanup error: {Error}", ex.Message)
}
