module SageFs.Server.DaemonMode

open System
open System.Threading
open SageFs
open SageFs.Server
open Falco
open Microsoft.Extensions.Logging

/// Run SageFs as a headless daemon.
/// MCP server + SessionManager + Dashboard — all frontends are clients.
/// Every session is a worker sub-process managed by SessionManager.
let run (mcpPort: int) (args: Args.Arguments list) = task {
  let version =
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    |> Option.ofObj
    |> Option.map (fun v -> v.ToString())
    |> Option.defaultValue "unknown"

  eprintfn "SageFs daemon v%s starting on port %d..." version mcpPort

  // Set up event store
  let connectionString = PostgresInfra.getOrStartPostgres ()
  let eventStore = SageFs.EventStore.configureStore connectionString
  let daemonStreamId = "daemon-sessions"

  // Handle --prune: mark all alive sessions as stopped and exit
  if args |> List.exists (function Args.Arguments.Prune -> true | _ -> false) then
    let! daemonEvents = SageFs.EventStore.fetchStream eventStore daemonStreamId
    let daemonState = Features.Replay.DaemonReplayState.replayStream daemonEvents
    let pruneEvents = Features.Replay.DaemonReplayState.pruneAllSessions daemonState
    if pruneEvents.IsEmpty then
      eprintfn "No alive sessions to prune."
    else
      do! SageFs.EventStore.appendEvents eventStore daemonStreamId pruneEvents
      eprintfn "Pruned %d session(s)." pruneEvents.Length
    return ()

  // Handle shutdown signals
  use cts = new CancellationTokenSource()

  // Create SessionManager — the single source of truth for all sessions
  let sessionManager = SessionManager.create cts.Token

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
          do! SageFs.EventStore.appendEvents eventStore daemonStreamId [
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
        do! SageFs.EventStore.appendEvents eventStore daemonStreamId [
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
      task {
        let! managed =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.GetSession(sessionId, reply))
          |> Async.StartAsTask
        return managed |> Option.map (fun s -> s.Info)
      }
    GetAllSessions = fun () ->
      task {
        let! sessions =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.ListSessions reply)
          |> Async.StartAsTask
        return sessions
      }
    GetStandbyInfo = fun () ->
      sessionManager.PostAndAsyncReply(fun reply ->
        SessionManager.SessionCommand.GetStandbyInfo reply)
      |> Async.StartAsTask
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
    let! daemonEvents = SageFs.EventStore.fetchStream eventStore daemonStreamId
    let daemonState = Features.Replay.DaemonReplayState.replayStream daemonEvents
    let aliveSessions = Features.Replay.DaemonReplayState.aliveSessions daemonState

    if not aliveSessions.IsEmpty then
      // Deduplicate by working directory — only resume one session per dir
      let uniqueByDir =
        aliveSessions
        |> List.groupBy (fun r -> r.WorkingDir)
        |> List.map (fun (_, group) ->
          // Pick the most recently created session for each dir
          group |> List.maxBy (fun r -> r.CreatedAt))
      // Mark all stale duplicates as stopped
      let staleIds =
        aliveSessions
        |> List.map (fun r -> r.SessionId)
        |> Set.ofList
      let keptIds =
        uniqueByDir |> List.map (fun r -> r.SessionId) |> Set.ofList
      for staleId in Set.difference staleIds keptIds do
        do! SageFs.EventStore.appendEvents eventStore daemonStreamId [
          Features.Events.SageFsEvent.DaemonSessionStopped
            {| SessionId = staleId; StoppedAt = DateTimeOffset.UtcNow |}
        ]
      eprintfn "Resuming %d previous session(s) (%d stale duplicates cleaned)..."
        uniqueByDir.Length (aliveSessions.Length - uniqueByDir.Length)
      // Skip missing directories first (synchronous, fast)
      let existing, missing =
        uniqueByDir |> List.partition (fun prev -> IO.Directory.Exists prev.WorkingDir)
      for prev in missing do
        eprintfn "  [SKIP] %s — directory no longer exists" prev.WorkingDir
        do! SageFs.EventStore.appendEvents eventStore daemonStreamId [
          Features.Events.SageFsEvent.DaemonSessionStopped
            {| SessionId = prev.SessionId; StoppedAt = DateTimeOffset.UtcNow |}
        ]
      // Resume all valid sessions in parallel — each is an independent worker process
      let resumeTasks =
        existing
        |> List.map (fun prev -> task {
          eprintfn "  Resuming session for %s..." prev.WorkingDir
          let! result = sessionOps.CreateSession prev.Projects prev.WorkingDir
          match result with
          | Ok info ->
            // Stop the OLD session ID so it doesn't resurrect on next restart
            do! SageFs.EventStore.appendEvents eventStore daemonStreamId [
              Features.Events.SageFsEvent.DaemonSessionStopped
                {| SessionId = prev.SessionId; StoppedAt = DateTimeOffset.UtcNow |}
            ]
            eprintfn "  Resumed: %s (retired old id %s)" info prev.SessionId
            onSessionResumed ()
          | Error err ->
            eprintfn "  [WARN] Failed to resume session for %s: %A" prev.WorkingDir err
        })
      do! System.Threading.Tasks.Task.WhenAll(resumeTasks) :> System.Threading.Tasks.Task
      // Sessions restored — clients will discover them via listing
      // No global "active session" to restore; each client picks its own
      match daemonState.ActiveSessionId with
      | Some _ -> () // Previously tracked active session — clients resolve on connect
      | None -> ()
    else
      eprintfn "No previous sessions to resume. Waiting for clients to create sessions."
  }

  // Create state-changed event for SSE subscribers
  let stateChangedEvent = Event<string>()
  let mutable lastStateJson = ""

  // Create EffectDeps from SessionManager + start Elm loop
  let effectDeps = ElmDaemon.createEffectDeps sessionManager
  let elmRuntime =
    ElmDaemon.start effectDeps (fun model _regions ->
      let outputCount = model.RecentOutput.Length
      let diagCount =
        model.Diagnostics |> Map.values |> Seq.sumBy List.length
      // Fire SSE event with summary JSON — deduplicated
      try
        let json = System.Text.Json.JsonSerializer.Serialize(
          {| outputCount = outputCount
             diagCount = diagCount
             sessionCount = model.Sessions.Sessions.Length
             activeSession = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.defaultValue ""
             sessionStatuses = model.Sessions.Sessions |> List.map (fun s -> sprintf "%s:%A" s.Id s.Status) |})
        if json <> lastStateJson then
          lastStateJson <- json
          if not TerminalUIState.IsActive && (outputCount > 0 || diagCount > 0) then
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

  // Start MCP server
  let mcpTask =
    McpServer.startMcpServer
      diagnosticsChanged.Publish
      (Some stateChangedEvent.Publish)
      eventStore
      mcpPort
      sessionOps
      (Some elmRuntime)

  // Start dashboard web server on MCP port + 1
  let dashboardPort = mcpPort + 1
  let connectionTracker = ConnectionTracker()
  // Dashboard status helpers — route through session proxy by explicit session ID
  // These are called from SSE handlers; pipe errors must not crash Kestrel.
  let tryGetSessionSnapshot (sid: string) =
    try
      let proxy =
        sessionManager.PostAndAsyncReply(fun reply ->
          SessionManager.SessionCommand.GetSession(sid, reply))
        |> Async.RunSynchronously
      match proxy with
      | Some s ->
        let resp =
          s.Proxy (WorkerProtocol.WorkerMessage.GetStatus "dash")
          |> Async.RunSynchronously
        match resp with
        | WorkerProtocol.WorkerResponse.StatusResult(_, snap) -> Some snap
        | _ -> None
      | None -> None
    with
    | :? System.IO.IOException -> None
    | :? System.AggregateException as ae
        when (ae.InnerException :? System.IO.IOException) -> None
    | :? System.ObjectDisposedException -> None
    | :? System.AggregateException as ae
        when (ae.InnerException :? System.ObjectDisposedException) -> None

  let getSessionState (sid: string) =
    if String.IsNullOrEmpty(sid) then SessionState.Uninitialized
    else
      // First try the pipe for live status; fall back to session manager's stored status
      match tryGetSessionSnapshot sid with
      | Some snap -> WorkerProtocol.SessionStatus.toSessionState snap.Status
      | None ->
        try
          let managed =
            sessionManager.PostAndAsyncReply(fun reply ->
              SessionManager.SessionCommand.GetSession(sid, reply))
            |> Async.RunSynchronously
          match managed with
          | Some s -> WorkerProtocol.SessionStatus.toSessionState s.Info.Status
          | None -> SessionState.Uninitialized // session removed; Dashboard filters as "stopped"
        with _ -> SessionState.Faulted

  let getEvalStats (sid: string) =
    match tryGetSessionSnapshot sid with
    | Some snap ->
      { EvalCount = snap.EvalCount
        TotalDuration = TimeSpan.FromMilliseconds(float snap.AvgDurationMs * float snap.EvalCount)
        MinDuration = TimeSpan.FromMilliseconds(float snap.MinDurationMs)
        MaxDuration = TimeSpan.FromMilliseconds(float snap.MaxDurationMs) }
      : Affordances.EvalStats
    | None -> Affordances.EvalStats.empty

  let getSessionWorkingDir (sid: string) =
    try
      let managed =
        sessionManager.PostAndAsyncReply(fun reply ->
          SessionManager.SessionCommand.GetSession(sid, reply))
        |> Async.RunSynchronously
      match managed with
      | Some s -> s.Info.WorkingDirectory
      | None -> ""
    with _ -> ""

  let getAllSessions () = task {
    let! sessions =
      sessionManager.PostAndAsyncReply(fun reply ->
        SessionManager.SessionCommand.ListSessions reply)
      |> Async.StartAsTask
    return sessions
  }

  let getStatusMsg (sid: string) =
    match tryGetSessionSnapshot sid with
    | Some snap -> snap.StatusMessage
    | None -> None

  let sessionThemes = Collections.Concurrent.ConcurrentDictionary<string, string>()

  let dashboardEndpoints =
    Dashboard.createEndpoints
      version
      getSessionState
      getStatusMsg
      getEvalStats
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
              | WorkerProtocol.WorkerResponse.EvalResult(_, Ok msg, diags) ->
                elmRuntime.Dispatch (SageFsMsg.Event (
                  SageFsEvent.EvalCompleted (sid, msg, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic)))
                msg
              | WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _) ->
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
        // Active sessions from SessionManager
        let activeInfos =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.ListSessions reply)
          |> Async.RunSynchronously
        let activeSessions =
          activeInfos
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
              SageFs.EventStore.fetchStream eventStore daemonStreamId
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
  let dashboardTask = task {
    try
      let builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder()
      // Suppress ASP.NET Core info logging (routing, hosting) for dashboard
      builder.Logging
        .AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
        .AddFilter("Microsoft.Hosting", LogLevel.Warning)
      |> ignore
      let app = builder.Build()
      app.Urls.Add(sprintf "http://localhost:%d" dashboardPort)
      app.UseRouting().UseFalco(dashboardEndpoints) |> ignore
      eprintfn "Dashboard available at http://localhost:%d/dashboard" dashboardPort
      do! app.RunAsync()
    with ex ->
      eprintfn "[WARN] Dashboard failed to start: %s" ex.Message
  }

  // Workers handle their own warmup, middleware, and file watching.
  // The daemon just needs to wait for the MCP and dashboard servers.

  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    eprintfn "Shutting down..."
    cts.Cancel())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    eprintfn "Daemon stopped.")

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
  eprintfn "SageFs daemon ready (PID %d, MCP port %d, dashboard port %d)" Environment.ProcessId mcpPort dashboardPort

  // Resume sessions in background — don't block the daemon main task.
  // Each resumed session dispatches ListSessions so dashboard sees them incrementally.
  let _resumeTask =
    if noResume then
      eprintfn "Session resume skipped (--no-resume)."
      System.Threading.Tasks.Task.CompletedTask
    else
      System.Threading.Tasks.Task.Run(fun () ->
        task {
          try
            do! resumeSessions (fun () ->
              elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions))
          with ex ->
            eprintfn "[WARN] Session resume failed: %s" ex.Message
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
        | ex -> eprintfn "[WARN] Status poll failed: %s" ex.Message
      } :> System.Threading.Tasks.Task)

  try
    let! _ = System.Threading.Tasks.Task.WhenAny(mcpRunning, dashboardRunning)
    ()
  with
  | :? OperationCanceledException -> ()

  // Graceful shutdown: stop all sessions and persist stop events
  let! activeSessions =
    sessionManager.PostAndAsyncReply(fun reply ->
      SessionManager.SessionCommand.ListSessions reply)
    |> Async.StartAsTask
  for info in activeSessions do
    do! SageFs.EventStore.appendEvents eventStore daemonStreamId [
      Features.Events.SageFsEvent.DaemonSessionStopped
        {| SessionId = info.Id; StoppedAt = DateTimeOffset.UtcNow |}
    ]
  sessionManager.PostAndAsyncReply(fun reply ->
    SessionManager.SessionCommand.StopAll reply)
  |> Async.RunSynchronously
}
