module SageFs.Server.DaemonMode

open System
open System.Threading
open SageFs
open SageFs.Server
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open OpenTelemetry.Logs

/// Run SageFs as a headless daemon.
/// MCP server + SessionManager + Dashboard — all frontends are clients.
/// Every session is a worker sub-process managed by SessionManager.
let run (mcpPort: int) (args: Args.Arguments list) = task {
  let version =
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    |> Option.ofObj
    |> Option.map (fun v -> v.ToString())
    |> Option.defaultValue "unknown"

  // Create structured logger for daemon lifecycle (flows to OTEL when configured)
  let otelConfigured =
    Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    |> Option.ofObj |> Option.isSome
  let loggerFactory =
    LoggerFactory.Create(fun builder ->
      builder
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information)
        .AddFilter("Microsoft", LogLevel.Warning)
      |> ignore
      if otelConfigured then
        builder.AddOpenTelemetry(fun otel ->
          otel.AddOtlpExporter() |> ignore
        ) |> ignore
    )
  let log = loggerFactory.CreateLogger("SageFs.Daemon")

  log.LogInformation("SageFs daemon v{Version} starting on port {Port}", version, mcpPort)

  // Set up persistence: InMemory by default, PostgreSQL with --persist or SAGEFS_CONNECTION_STRING
  let hasPersist = args |> List.exists (function Args.Arguments.Persist -> true | _ -> false)
  let envConnStr = System.Environment.GetEnvironmentVariable("SAGEFS_CONNECTION_STRING")
  let persistence =
    if hasPersist || not (System.String.IsNullOrEmpty envConnStr) then
      let connectionString =
        if not (System.String.IsNullOrEmpty envConnStr) then
          log.LogInformation("Using PostgreSQL from SAGEFS_CONNECTION_STRING")
          Some envConnStr
        else
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
    else
      log.LogInformation("Event persistence: InMemory (use --persist for PostgreSQL)")
      SageFs.EventStore.EventPersistence.inMemory ()
  let daemonStreamId = "daemon-sessions"

  // Handle --prune: mark all alive sessions as stopped and exit
  if args |> List.exists (function Args.Arguments.Prune -> true | _ -> false) then
    let! daemonEvents = persistence.FetchStream daemonStreamId
    let daemonState = Features.Replay.DaemonReplayState.replayStream daemonEvents
    let pruneEvents = Features.Replay.DaemonReplayState.pruneAllSessions daemonState
    if pruneEvents.IsEmpty then
      log.LogInformation("No alive sessions to prune")
    else
      let! result = persistence.AppendEvents daemonStreamId pruneEvents
      match result with
      | Ok () -> log.LogInformation("Pruned {Count} session(s)", pruneEvents.Length)
      | Error msg -> log.LogWarning("Prune failed: {Error}", msg)
    return ()

  // Handle shutdown signals
  use cts = new CancellationTokenSource()

  // Create state-changed event for SSE subscribers (created early so SessionManager can trigger it)
  let stateChangedEvent = Event<string>()
  let mutable lastStateJson = ""
  let mutable lastLoggedOutputCount = 0
  let mutable lastLoggedDiagCount = 0
  // Test discovery callback — set after elmRuntime is created
  let mutable onTestDiscoveryCallback : (WorkerProtocol.SessionId -> Features.LiveTesting.TestCase array -> Features.LiveTesting.ProviderDescription list -> unit) =
    fun _ _ _ -> ()

  // Create SessionManager — the single source of truth for all sessions
  // Returns (mailbox, readSnapshot) — CQRS: reads go to snapshot, writes to mailbox
  let sessionManager, readSnapshot =
    SessionManager.create cts.Token
      (fun () -> stateChangedEvent.Trigger """{"standbyProgress":true}""")
      (fun sid tests providers -> onTestDiscoveryCallback sid tests providers)

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
      task {
        let! managed =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.GetSession(sessionId, reply))
          |> Async.StartAsTask
        return managed |> Option.map (fun s -> s.Proxy)
      }
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
    if not (isNull replaySpan) then
      replaySpan.SetTag("event_count", eventCount) |> ignore
    Instrumentation.succeedSpan replaySpan

    let aliveSessions = Features.Replay.DaemonReplayState.aliveSessions daemonState

    if not aliveSessions.IsEmpty then
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
      if prunedCount > 0 then
        Instrumentation.daemonDuplicatesPruned.Add(int64 prunedCount)
      if not (isNull dedupSpan) then
        dedupSpan.SetTag("alive_count", aliveSessions.Length) |> ignore
        dedupSpan.SetTag("dedup_removed", prunedCount) |> ignore
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
      if not (isNull resumeSpan) then
        resumeSpan.SetTag("resumed_count", existing.Length) |> ignore
      Instrumentation.succeedSpan resumeSpan

      // Sessions restored — clients will discover them via listing
      // No global "active session" to restore; each client picks its own
      match daemonState.ActiveSessionId with
      | Some _ -> () // Previously tracked active session — clients resolve on connect
      | None -> ()
    else
      if not initialProjects.IsEmpty then
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
      else
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
          let client = new Net.Http.HttpClient()
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
      with _ -> return None
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
        if json <> lastStateJson then
          lastStateJson <- json
          let outputChanged = outputCount <> lastLoggedOutputCount
          let diagChanged = diagCount <> lastLoggedDiagCount
          if not TerminalUIState.IsActive && (outputChanged || diagChanged) then
            lastLoggedOutputCount <- outputCount
            lastLoggedDiagCount <- diagCount
            let latest =
              model.RecentOutput
              |> List.tryHead
              |> Option.map (fun o -> o.Text)
              |> Option.defaultValue ""
            eprintfn "\x1b[36m[elm]\x1b[0m output=%d diags=%d | %s"
              outputCount diagCount latest
          stateChangedEvent.Trigger json
      with _ -> ())

  // Create a diagnostics-changed event (aggregated from workers)
  let diagnosticsChanged = Event<Features.DiagnosticsStore.T>()

  // Warmup context fetcher for MCP — uses session manager to find worker URL
  let getWarmupContextForMcp (sessionId: string) : System.Threading.Tasks.Task<WarmupContext option> =
    task {
      try
        let! managed =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.GetSession(sessionId, reply))
          |> Async.StartAsTask
        match managed with
        | Some s when s.WorkerBaseUrl.Length > 0 ->
          let client = new Net.Http.HttpClient()
          let! resp = client.GetStringAsync(sprintf "%s/warmup-context" s.WorkerBaseUrl)
          let ctx = WorkerProtocol.Serialization.deserialize<WarmupContext> resp
          return Some ctx
        | _ -> return None
      with _ -> return None
    }

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
          let fullPath = if IO.Path.IsPathRooted proj then proj else IO.Path.Combine(info.WorkingDirectory, proj)
          IO.Path.GetDirectoryName fullPath)
        |> List.distinct
      | None -> [ workingDir ]
    let locations =
      if Features.LiveTesting.TestTreeSitter.isAvailable () then
        projectDirs
        |> List.toArray
        |> Array.collect (fun dir ->
          if IO.Directory.Exists dir then
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
          else Array.empty)
      else Array.empty
    // Dispatch locations BEFORE tests so mergeSourceLocations can enrich them
    if not (Array.isEmpty locations) then
      elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.TestLocationsDetected (sid, locations)))
    if not (Array.isEmpty tests) then
      elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.TestsDiscovered (sid, tests)))
    if not (List.isEmpty providers) then
      elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.ProvidersDetected providers))

  // Start MCP server
  let mcpTask =
    McpServer.startMcpServer
      diagnosticsChanged.Publish
      (Some stateChangedEvent.Publish)
      persistence
      mcpPort
      sessionOps
      (Some elmRuntime)
      (Some getWarmupContextForMcp)

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
        if fi.Exists && fi.Length < 1_048_576L then
          let content = System.IO.File.ReadAllText(path)
          elmRuntime.Dispatch(SageFsMsg.FileContentChanged(path, content))
      with
      | :? System.IO.IOException -> ()
      | :? System.UnauthorizedAccessException -> ()

  let liveTestDebounceTimer = new System.Threading.Timer(
    System.Threading.TimerCallback(liveTestDebounceCallback), null,
    System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)

  let handleFileChanged (e: System.IO.FileSystemEventArgs) =
    let path = e.FullPath
    if SageFs.FileWatcher.shouldTriggerRebuild
        { Directories = [workingDir]; Extensions = [".fs"; ".fsx"]; ExcludePatterns = []; DebounceMs = 200 }
        path then
      lock liveTestWatcherLock (fun () ->
        liveTestPendingPaths <- liveTestPendingPaths |> Set.add path
        liveTestDebounceTimer.Change(200, System.Threading.Timeout.Infinite) |> ignore)

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
    try
      let! managed =
        sessionManager.PostAndAsyncReply(fun reply ->
          SessionManager.SessionCommand.GetSession(sid, reply))
        |> Async.StartAsTask
      match managed with
      | Some s ->
        let! resp =
          s.Proxy (WorkerProtocol.WorkerMessage.GetStatus "dash")
          |> Async.StartAsTask
        match resp with
        | WorkerProtocol.WorkerResponse.StatusResult(_, snap) -> return Some snap
        | _ -> return None
      | None -> return None
    with
    | :? System.IO.IOException -> return None
    | :? System.AggregateException as ae
        when (ae.InnerException :? System.IO.IOException) -> return None
    | :? System.ObjectDisposedException -> return None
    | :? System.AggregateException as ae
        when (ae.InnerException :? System.ObjectDisposedException) -> return None
  }

  let getSessionState (sid: string) =
    if String.IsNullOrEmpty(sid) then SessionState.Uninitialized
    else
      let snapshot = readSnapshot()
      match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
      | Some info -> WorkerProtocol.SessionStatus.toSessionState info.Status
      | None -> SessionState.Uninitialized

  let getEvalStatsAsync (sid: string) = task {
    match! tryGetSessionSnapshotAsync sid with
    | Some snap ->
      return
        { EvalCount = snap.EvalCount
          TotalDuration = TimeSpan.FromMilliseconds(float snap.AvgDurationMs * float snap.EvalCount)
          MinDuration = TimeSpan.FromMilliseconds(float snap.MinDurationMs)
          MaxDuration = TimeSpan.FromMilliseconds(float snap.MaxDurationMs) }
        : Affordances.EvalStats
    | None -> return Affordances.EvalStats.empty
  }

  let getSessionWorkingDir (sid: string) =
    let snapshot = readSnapshot()
    match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
    | Some info -> info.WorkingDirectory
    | None -> ""

  let getAllSessions () = task {
    let! sessions =
      sessionManager.PostAndAsyncReply(fun reply ->
        SessionManager.SessionCommand.ListSessions reply)
      |> Async.StartAsTask
    return sessions
  }

  let getStatusMsg (sid: string) =
    let snapshot = readSnapshot()
    match Map.tryFind sid snapshot.WarmupProgress with
    | Some progress -> Some progress
    | None -> None

  let getWorkerBaseUrl (sid: string) =
    let snapshot = readSnapshot()
    match Map.tryFind sid snapshot.WorkerBaseUrls with
    | Some url when url.Length > 0 -> Some url
    | _ -> None

  let sessionThemes = Dashboard.loadThemes DaemonState.SageFsDir

  let dashboardEndpoints =
    Dashboard.createEndpoints
      version
      getSessionState
      getStatusMsg
      getEvalStatsAsync
      getSessionWorkingDir
      (fun () ->
        let model = elmRuntime.GetModel()
        ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.defaultValue "")
      (fun () -> elmRuntime.GetRegions() |> Some)
      (Some stateChangedEvent.Publish)
      (fun sid code -> task {
        if String.IsNullOrEmpty(sid) then return "Error: No session selected"
        else
        try
          let! proxy = sessionOps.GetProxy sid
          match proxy with
          | Some send ->
            let! resp =
              send (WorkerProtocol.WorkerMessage.EvalCode(code, "dash"))
              |> Async.StartAsTask
            let result =
              match resp with
              | WorkerProtocol.WorkerResponse.EvalResult(_, Ok msg, diags, _) ->
                elmRuntime.Dispatch (SageFsMsg.Event (
                  SageFsEvent.EvalCompleted (sid, msg, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic)))
                msg
              | WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _, _) ->
                let msg = SageFsError.describe err
                elmRuntime.Dispatch (SageFsMsg.Event (
                  SageFsEvent.EvalFailed (sid, msg)))
                sprintf "Error: %s" msg
              | other -> sprintf "Unexpected: %A" other
            return result
          | None -> return "Error: No active session"
        with
        | :? System.IO.IOException as ex ->
          return sprintf "Error: Session pipe broken — %s" ex.Message
        | :? System.AggregateException as ae
            when (ae.InnerException :? System.IO.IOException) ->
          return sprintf "Error: Session pipe broken — %s" ae.InnerException.Message
        | :? System.ObjectDisposedException as ex ->
          return sprintf "Error: Session pipe closed — %s" ex.Message
      })
      (fun sid -> task {
        if String.IsNullOrEmpty(sid) then return "Error: No session selected"
        else
        try
          let! proxy = sessionOps.GetProxy sid
          match proxy with
          | Some send ->
            let! resp =
              send (WorkerProtocol.WorkerMessage.ResetSession "dash")
              |> Async.StartAsTask
            return
              match resp with
              | WorkerProtocol.WorkerResponse.ResetResult(_, Ok ()) -> "Session reset successfully"
              | WorkerProtocol.WorkerResponse.ResetResult(_, Error e) -> sprintf "Reset failed: %A" e
              | other -> sprintf "Unexpected: %A" other
          | None -> return "Error: No active session"
        with
        | :? System.IO.IOException as ex ->
          return sprintf "Error: Session pipe broken — %s" ex.Message
        | :? System.AggregateException as ae
            when (ae.InnerException :? System.IO.IOException) ->
          return sprintf "Error: Session pipe broken — %s" ae.InnerException.Message
        | :? System.ObjectDisposedException as ex ->
          return sprintf "Error: Session pipe closed — %s" ex.Message
      })
      (fun sid -> task {
        if String.IsNullOrEmpty(sid) then return "Error: No session selected"
        else
        let! result = sessionOps.RestartSession sid true
        return
          match result with
          | Ok msg -> sprintf "Hard reset: %s" msg
          | Error e -> sprintf "Hard reset failed: %s" (SageFsError.describe e)
      })
      // Session switch handler — update Elm model's active session
      (Some (fun (sid: string) -> task {
        elmRuntime.Dispatch(SageFsMsg.Event (SageFsEvent.SessionSwitched (None, sid)))
        return sprintf "Switched to session '%s'" sid
      }))
      // Session stop handler
      (Some (fun (sid: string) -> task {
        let! result = sessionOps.StopSession sid
        elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
        return
          match result with
          | Ok msg -> msg
          | Error e -> sprintf "Stop failed: %A" e
      }))
      // Create session handler
      (Some (fun (projects: string list) (workingDir: string) -> task {
        let! result = sessionOps.CreateSession projects workingDir
        elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
        return
          match result with
          | Ok msg -> Ok msg
          | Error e -> Error (sprintf "%A" e)
      }))
      // Connection tracker
      (Some connectionTracker)
      // Dispatch for TUI client API
      (fun msg -> elmRuntime.Dispatch msg)
      // Shutdown callback for /api/shutdown
      (Some (fun () -> cts.Cancel()))
      // Previous sessions for picker — merge active + historical from Marten
      (fun () ->
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
        let historicalSessions =
          try
            let events =
              persistence.FetchStream daemonStreamId
              |> fun t -> t.ConfigureAwait(false).GetAwaiter().GetResult()
            let daemonState = Features.Replay.DaemonReplayState.replayStream events
            daemonState.Sessions
            |> Map.values
            |> Seq.filter (fun r -> r.StoppedAt.IsSome && not (activeIds.Contains r.SessionId))
            |> Seq.map (fun r ->
              { Dashboard.PreviousSession.Id = r.SessionId
                Dashboard.PreviousSession.WorkingDir = r.WorkingDir
                Dashboard.PreviousSession.Projects = r.Projects
                Dashboard.PreviousSession.LastSeen = r.StoppedAt |> Option.map (fun t -> t.DateTime) |> Option.defaultValue r.CreatedAt.DateTime })
            |> Seq.toList
          with _ -> []
        activeSessions @ historicalSessions)
      getAllSessions
      sessionThemes
      sessionOps.GetStandbyInfo
      (fun sessionId -> task {
        match getWorkerBaseUrl sessionId with
        | Some baseUrl ->
          try
            let client = new Net.Http.HttpClient()
            let! resp = client.GetStringAsync(sprintf "%s/hotreload" baseUrl)
            use doc = Text.Json.JsonDocument.Parse(resp)
            let root = doc.RootElement
            let files =
              root.GetProperty("files").EnumerateArray()
              |> Seq.map (fun el ->
                {| path = el.GetProperty("path").GetString()
                   watched = el.GetProperty("watched").GetBoolean() |})
              |> Seq.toList
            let watchedCount = root.GetProperty("watchedCount").GetInt32()
            return Some {| files = files; watchedCount = watchedCount |}
          with _ -> return None
        | None -> return None
      })
      (fun sessionId -> task {
        match getWorkerBaseUrl sessionId with
        | Some baseUrl ->
          try
            let client = new Net.Http.HttpClient()
            let! resp = client.GetStringAsync(sprintf "%s/warmup-context" baseUrl)
            let ctx = WorkerProtocol.Serialization.deserialize<WarmupContext> resp
            return Some ctx
          with _ -> return None
        | None -> return None
      })
      // Completions handler — routes to worker
      (Some (fun (sessionId: string) (code: string) (cursorPos: int) -> task {
        if String.IsNullOrEmpty(sessionId) then return []
        else
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
        with _ -> return []
      }))
      // Live testing status for TUI/Raylib status bar
      (fun () ->
        let model = elmRuntime.GetModel()
        let activeId =
          SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
          |> Option.defaultValue ""
        SageFs.Features.LiveTesting.LiveTestPipelineState.liveTestingStatusBarForSession activeId model.LiveTesting)
      // Warmup progress from CQRS snapshot
      (fun sessionId ->
        let snapshot = readSnapshot()
        match Map.tryFind sessionId snapshot.WarmupProgress with
        | Some progress -> progress
        | None -> "")

  // Hot-reload proxy endpoints — forward to worker HTTP servers
  let hotReloadHttpClient = new Net.Http.HttpClient()
  let hotReloadProxyEndpoints : HttpEndpoint list =
    let proxyGet (sid: string) (workerPath: string) (ctx: Microsoft.AspNetCore.Http.HttpContext) = task {
      match getWorkerBaseUrl sid with
      | Some baseUrl ->
        try
          let url = sprintf "%s%s" baseUrl workerPath
          let! resp = hotReloadHttpClient.GetStringAsync(url)
          ctx.Response.ContentType <- "application/json"
          do! ctx.Response.WriteAsync(resp)
        with ex ->
          ctx.Response.StatusCode <- 502
          do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
      | None ->
        ctx.Response.StatusCode <- 404
        do! ctx.Response.WriteAsJsonAsync({| error = "Session not found or not ready" |})
    }
    let proxyPost (sid: string) (workerPath: string) (ctx: Microsoft.AspNetCore.Http.HttpContext) = task {
      match getWorkerBaseUrl sid with
      | Some baseUrl ->
        try
          let url = sprintf "%s%s" baseUrl workerPath
          use reader = new IO.StreamReader(ctx.Request.Body)
          let! body = reader.ReadToEndAsync()
          use content = new Net.Http.StringContent(body, Text.Encoding.UTF8, "application/json")
          let! resp = hotReloadHttpClient.PostAsync(url, content)
          let! respBody = resp.Content.ReadAsStringAsync()
          ctx.Response.ContentType <- "application/json"
          ctx.Response.StatusCode <- int resp.StatusCode
          do! ctx.Response.WriteAsync(respBody)
          // Trigger SSE push so Dashboard updates hot-reload panel
          if resp.IsSuccessStatusCode then
            stateChangedEvent.Trigger """{"hotReloadChanged":true}"""
        with ex ->
          ctx.Response.StatusCode <- 502
          do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
      | None ->
        ctx.Response.StatusCode <- 404
        do! ctx.Response.WriteAsJsonAsync({| error = "Session not found or not ready" |})
    }
    [
      mapGet "/api/sessions/{sid}/hotreload"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyGet sid "/hotreload" ctx)
      mapPost "/api/sessions/{sid}/hotreload/toggle"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyPost sid "/hotreload/toggle" ctx)
      mapPost "/api/sessions/{sid}/hotreload/watch-all"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyPost sid "/hotreload/watch-all" ctx)
      mapPost "/api/sessions/{sid}/hotreload/unwatch-all"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyPost sid "/hotreload/unwatch-all" ctx)
      mapPost "/api/sessions/{sid}/hotreload/watch-project"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyPost sid "/hotreload/watch-project" ctx)
      mapPost "/api/sessions/{sid}/hotreload/unwatch-project"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyPost sid "/hotreload/unwatch-project" ctx)
      mapPost "/api/sessions/{sid}/hotreload/watch-directory"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyPost sid "/hotreload/watch-directory" ctx)
      mapPost "/api/sessions/{sid}/hotreload/unwatch-directory"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyPost sid "/hotreload/unwatch-directory" ctx)
      mapGet "/api/sessions/{sid}/warmup-context"
        (fun (r: RequestData) -> r.GetString("sid", ""))
        (fun sid -> fun ctx -> proxyGet sid "/warmup-context" ctx)
    ]

  let dashboardTask = task {
    try
      let builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder()
      // Suppress ASP.NET Core info logging (routing, hosting) for dashboard
      builder.Logging
        .AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
        .AddFilter("Microsoft.Hosting", LogLevel.Warning)
      |> ignore
      let app = builder.Build()
      let bindHost =
        match System.Environment.GetEnvironmentVariable("SAGEFS_BIND_HOST") with
        | null | "" -> "localhost"
        | h -> h
      app.Urls.Add(sprintf "http://%s:%d" bindHost dashboardPort)
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
    if noResume then
      log.LogInformation("Session resume skipped (--no-resume)")
      System.Threading.Tasks.Task.CompletedTask
    else
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
    let! activeSessions =
      sessionManager.PostAndAsyncReply(fun reply ->
        SessionManager.SessionCommand.ListSessions reply)
      |> Async.StartAsTask
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
    if not (stopTask.Wait(TimeSpan.FromSeconds(3.0))) then
      log.LogWarning("StopAll timed out — some workers may not have stopped cleanly")
  with ex ->
    log.LogWarning("Shutdown cleanup error: {Error}", ex.Message)
}
