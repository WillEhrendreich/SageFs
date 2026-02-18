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

  // Handle shutdown signals
  use cts = new CancellationTokenSource()

  // Create SessionManager — the single source of truth for all sessions
  let sessionManager = SessionManager.create cts.Token

  // Active session ID — mutable, shared across MCP and dashboard
  let activeSessionId = ref ""

  let daemonStreamId = "daemon-sessions"

  let sessionOps : SessionManagementOps = {
    CreateSession = fun projects workingDir ->
      task {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.CreateSession(projects, workingDir, reply))
          |> Async.StartAsTask
        match result with
        | Ok info ->
          activeSessionId.Value <- info.Id
          do! SageFs.EventStore.appendEvents eventStore daemonStreamId [
            Features.Events.SageFsEvent.DaemonSessionCreated
              {| SessionId = info.Id; Projects = projects; WorkingDir = workingDir; CreatedAt = DateTimeOffset.UtcNow |}
          ]
          return Ok (SessionOperations.formatSessionInfo DateTime.UtcNow info)
        | Error e -> return Error e
      }
    ListSessions = fun () ->
      task {
        let! sessions =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.ListSessions reply)
          |> Async.StartAsTask
        return SessionOperations.formatSessionList DateTime.UtcNow sessions
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
        // If we stopped the active session, switch to another
        if !activeSessionId = sessionId then
          let! sessions =
            sessionManager.PostAndAsyncReply(fun reply ->
              SessionManager.SessionCommand.ListSessions reply)
            |> Async.StartAsTask
          let next =
            sessions
            |> List.tryFind (fun s -> s.Id <> sessionId)
            |> Option.map (fun s -> s.Id)
            |> Option.defaultValue ""
          activeSessionId.Value <- next
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
  }

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
      for prev in uniqueByDir do
        if IO.Directory.Exists prev.WorkingDir then
          eprintfn "  Resuming session for %s..." prev.WorkingDir
          let! result = sessionOps.CreateSession prev.Projects prev.WorkingDir
          match result with
          | Ok info ->
            eprintfn "  Resumed: %s" info
            onSessionResumed ()
          | Error err -> eprintfn "  [WARN] Failed to resume session for %s: %A" prev.WorkingDir err
        else
          eprintfn "  [SKIP] %s — directory no longer exists" prev.WorkingDir
          do! SageFs.EventStore.appendEvents eventStore daemonStreamId [
            Features.Events.SageFsEvent.DaemonSessionStopped
              {| SessionId = prev.SessionId; StoppedAt = DateTimeOffset.UtcNow |}
          ]
      // Restore active session from last switch event
      match daemonState.ActiveSessionId with
      | Some lastActiveId ->
        let lastRecord = daemonState.Sessions |> Map.tryFind lastActiveId
        match lastRecord with
        | Some r ->
          let! sessions =
            sessionManager.PostAndAsyncReply(fun reply ->
              SessionManager.SessionCommand.ListSessions reply)
            |> Async.StartAsTask
          let matching =
            sessions |> List.tryFind (fun s -> s.WorkingDirectory = r.WorkingDir)
          match matching with
          | Some s -> activeSessionId.Value <- s.Id
          | None -> ()
        | None -> ()
      | None -> ()
    else
      eprintfn "Spawning initial session for %s..." workingDir
      let! initialResult = sessionOps.CreateSession initialProjects workingDir
      match initialResult with
      | Ok info -> eprintfn "Initial session created: %s" info
      | Error err -> eprintfn "[ERROR] Failed to create initial session: %A" err
  }

  // Create state-changed event for SSE subscribers
  let stateChangedEvent = Event<string>()

  // Create EffectDeps from SessionManager + start Elm loop
  let effectDeps = ElmDaemon.createEffectDeps sessionManager
  let elmRuntime =
    ElmDaemon.start effectDeps (fun model _regions ->
      let outputCount = model.RecentOutput.Length
      let diagCount =
        model.Diagnostics |> Map.values |> Seq.sumBy List.length
      if not TerminalUIState.IsActive && (outputCount > 0 || diagCount > 0) then
        let latest =
          model.RecentOutput
          |> List.tryHead
          |> Option.map (fun o -> o.Text)
          |> Option.defaultValue ""
        eprintfn "\x1b[36m[elm]\x1b[0m output=%d diags=%d | %s"
          outputCount diagCount latest
      // Fire SSE event with summary JSON
      try
        let json = System.Text.Json.JsonSerializer.Serialize(
          {| outputCount = outputCount
             diagCount = diagCount
             sessionCount = model.Sessions.Sessions.Length
             activeSession = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.defaultValue ""
             timestamp = DateTime.UtcNow.ToString("o") |})
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
      activeSessionId
      (Some elmRuntime)

  // Start dashboard web server on MCP port + 1
  let dashboardPort = mcpPort + 1
  let connectionTracker = ConnectionTracker()
  // Dashboard status helpers — route through active session proxy
  // These are called from SSE handlers; pipe errors must not crash Kestrel.
  let tryGetSessionSnapshot () =
    let sid = !activeSessionId
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

  let getSessionState () =
    let sid = !activeSessionId
    if String.IsNullOrEmpty(sid) then SessionState.Uninitialized
    else
      // First try the pipe for live status; fall back to session manager's stored status
      match tryGetSessionSnapshot () with
      | Some snap -> WorkerProtocol.SessionStatus.toSessionState snap.Status
      | None ->
        try
          let managed =
            sessionManager.PostAndAsyncReply(fun reply ->
              SessionManager.SessionCommand.GetSession(sid, reply))
            |> Async.RunSynchronously
          match managed with
          | Some s -> WorkerProtocol.SessionStatus.toSessionState s.Info.Status
          | None -> SessionState.Faulted
        with _ -> SessionState.Faulted

  let getEvalStats () =
    match tryGetSessionSnapshot () with
    | Some snap ->
      { EvalCount = snap.EvalCount
        TotalDuration = TimeSpan.FromMilliseconds(float snap.AvgDurationMs * float snap.EvalCount)
        MinDuration = TimeSpan.FromMilliseconds(float snap.MinDurationMs)
        MaxDuration = TimeSpan.FromMilliseconds(float snap.MaxDurationMs) }
      : Affordances.EvalStats
    | None -> Affordances.EvalStats.empty

  let getSessionWorkingDir () =
    let sid = !activeSessionId
    try
      let managed =
        sessionManager.PostAndAsyncReply(fun reply ->
          SessionManager.SessionCommand.GetSession(sid, reply))
        |> Async.RunSynchronously
      match managed with
      | Some s -> s.Info.WorkingDirectory
      | None -> ""
    with _ -> ""

  let dashboardEndpoints =
    Dashboard.createEndpoints
      version
      getSessionState
      getEvalStats
      (fun () -> !activeSessionId)
      getSessionWorkingDir
      (fun () -> elmRuntime.GetRegions() |> Some)
      (Some stateChangedEvent.Publish)
      (fun code -> task {
        let sid = !activeSessionId
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
      (fun () -> task {
        let sid = !activeSessionId
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
      (fun () -> task {
        let sid = !activeSessionId
        let! result = sessionOps.RestartSession sid true
        return
          match result with
          | Ok msg -> sprintf "Hard reset: %s" msg
          | Error e -> sprintf "Hard reset failed: %s" (SageFsError.describe e)
      })
      // Session switch handler — per-browser only, no shared Elm dispatch
      (Some (fun (sid: string) -> task {
        activeSessionId.Value <- sid
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
        // Refresh Elm model's session list so dashboard SSE pushes update
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

  // NOW resume sessions — servers are listening, MCP clients can connect
  // Each resumed session dispatches ListSessions so dashboard sees them incrementally
  do! resumeSessions (fun () ->
    elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions))

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
