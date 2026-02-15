module SageFs.Server.DaemonMode

open System
open System.Threading
open SageFs
open SageFs.Server
open Falco
open Microsoft.Extensions.Logging

/// Run SageFs as a headless daemon.
/// MCP server + SessionManager + Dashboard — all frontends are clients.
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
  let sessionId = SageFs.EventStore.createSessionId ()
  let onEvent (evt: SageFs.Features.Events.SageFsEvent) =
    SageFs.EventStore.appendEvents eventStore sessionId [evt]
    |> fun t -> t.ConfigureAwait(false).GetAwaiter().GetResult()

  let logger =
    { new Utils.ILogger with
        member _.LogInfo msg = eprintfn "[INFO] %s" msg
        member _.LogDebug msg = eprintfn "[DEBUG] %s" msg
        member _.LogWarning msg = eprintfn "[WARN] %s" msg
        member _.LogError msg = eprintfn "[ERROR] %s" msg }

  let actorArgs =
    ActorCreation.mkCommonActorArgs logger false onEvent (args |> List.map id)

  // Phase 1: Create actor immediately — MCP can serve status while warm-up runs
  let result = ActorCreation.createActorImmediate actorArgs
  let appActor = result.Actor

  // Handle shutdown signals
  use cts = new CancellationTokenSource()

  // Create SessionManager for multi-session support
  let sessionManager = SessionManager.create cts.Token
  let sessionOps : SessionManagementOps = {
    CreateSession = fun projects workingDir ->
      task {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.CreateSession(projects, workingDir, reply))
          |> Async.StartAsTask
        return
          result
          |> Result.map (fun info ->
            SessionOperations.formatSessionInfo DateTime.UtcNow info)
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
        return
          result
          |> Result.map (fun () ->
            sprintf "Session '%s' stopped." sessionId)
      }
    GetProxy = fun sessionId ->
      task {
        let! managed =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.GetSession(sessionId, reply))
          |> Async.StartAsTask
        return managed |> Option.map (fun s -> s.Proxy)
      }
  }

  // Write daemon state
  let daemonInfo : DaemonInfo = {
    Pid = Environment.ProcessId
    Port = mcpPort
    StartedAt = DateTime.UtcNow
    WorkingDirectory = Environment.CurrentDirectory
    Version = version
  }
  DaemonState.write daemonInfo

  // Create state-changed event for SSE subscribers
  let stateChangedEvent = Event<string>()

  // Create EffectDeps from SessionManager + start Elm loop
  let effectDeps = ElmDaemon.createEffectDeps sessionManager
  let elmRuntime =
    ElmDaemon.start effectDeps (fun model _regions ->
      let outputCount = model.RecentOutput.Length
      let diagCount = model.Diagnostics.Length
      if outputCount > 0 || diagCount > 0 then
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
             activeSession = model.Sessions.ActiveSessionId |> Option.defaultValue ""
             timestamp = DateTime.UtcNow.ToString("o") |})
        stateChangedEvent.Trigger json
      with _ -> ())

  // Start MCP server BEFORE warm-up completes
  appActor.Post(AppState.UpdateMcpPort mcpPort)
  let mcpTask =
    McpServer.startMcpServer
      appActor
      result.DiagnosticsChanged
      (Some stateChangedEvent.Publish)
      result.CancelEval
      result.GetSessionState
      result.GetEvalStats
      result.GetWarmupFailures
      result.GetStartupConfig
      eventStore
      sessionId
      mcpPort
      sessionOps
      (Some elmRuntime)

  // Start dashboard web server on MCP port + 1
  let dashboardPort = mcpPort + 1
  let dashboardEndpoints =
    Dashboard.createEndpoints
      version
      result.GetSessionState
      result.GetEvalStats
      sessionId
      (result.ProjectDirectories.Length)
      (fun () -> elmRuntime.GetRegions() |> Some)
      (Some stateChangedEvent.Publish)
      (fun code -> task {
        let request : AppState.EvalRequest = { Code = code; Args = Map.empty }
        let! resp =
          appActor.PostAndAsyncReply(fun reply ->
            AppState.Eval(request, CancellationToken.None, reply))
          |> Async.StartAsTask
        return
          match resp.EvaluationResult with
          | Ok msg -> msg
          | Error ex -> sprintf "Error: %s" ex.Message
      })
      (fun () -> task {
        let! resp =
          appActor.PostAndAsyncReply(fun reply ->
            AppState.ResetSession reply)
          |> Async.StartAsTask
        return
          match resp with
          | Ok () -> "Session reset successfully"
          | Error e -> sprintf "Reset failed: %A" e
      })
      (fun () -> task {
        let! resp =
          appActor.PostAndAsyncReply(fun reply ->
            AppState.HardResetSession(true, reply))
          |> Async.StartAsTask
        return
          match resp with
          | Ok msg -> sprintf "Hard reset: %s" msg
          | Error e -> sprintf "Hard reset failed: %A" e
      })
      // Session switch handler
      (Some (fun (sid: string) -> task {
        elmRuntime.Dispatch (SageFsMsg.Event (
          SageFsEvent.SessionSwitched (None, sid)))
        return sprintf "Switched to session '%s'" sid
      }))
      // Session stop handler
      (Some (fun (sid: string) -> task {
        let! result = sessionOps.StopSession sid
        return
          match result with
          | Ok msg -> msg
          | Error e -> sprintf "Stop failed: %A" e
      }))
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

  // Phase 2: Add middleware — blocks until warm-up completes
  do! ActorCreation.addMiddleware result actorArgs.Middleware

  // Notify Elm loop that warmup is done
  let warmupFailures = result.GetWarmupFailures()
  elmRuntime.Dispatch (SageFsMsg.Event (
    SageFsEvent.WarmupCompleted (
      TimeSpan.Zero, warmupFailures |> List.map (fun f -> sprintf "%s: %s" f.Name f.Error))))
  elmRuntime.Dispatch (SageFsMsg.Event (
    SageFsEvent.SessionStatusChanged (sessionId, SessionDisplayStatus.Running)))

  // Start file watcher for incremental reload on source changes
  let noWatch = args |> List.exists (function Args.Arguments.No_Watch -> true | _ -> false)
  let _fileWatcher =
    if noWatch || result.ProjectDirectories.IsEmpty then None
    else
      let config = SageFs.FileWatcher.defaultWatchConfig result.ProjectDirectories
      let onFileChanged (change: SageFs.FileWatcher.FileChange) =
        match SageFs.FileWatcher.fileChangeAction change with
        | SageFs.FileWatcher.FileChangeAction.Reload filePath ->
          let fileName = IO.Path.GetFileName filePath
          eprintfn "[INFO] File changed: %s -- reloading via #load" fileName
          elmRuntime.Dispatch (SageFsMsg.Event (
            SageFsEvent.FileChanged (filePath, FileWatchAction.Changed)))
          let code = sprintf "#load @\"%s\"" filePath
          let request : AppState.EvalRequest = { Code = code; Args = Map.empty }
          let sw = System.Diagnostics.Stopwatch.StartNew()
          appActor.PostAndAsyncReply(fun reply -> AppState.Eval(request, System.Threading.CancellationToken.None, reply))
          |> Async.RunSynchronously
          |> fun resp ->
            sw.Stop()
            match resp.EvaluationResult with
            | Ok _ ->
              eprintfn "[INFO] Reloaded %s" fileName
              elmRuntime.Dispatch (SageFsMsg.Event (
                SageFsEvent.FileReloaded (fileName, sw.Elapsed, Ok "reloaded")))
            | Error ex ->
              eprintfn "[WARN] Reload error in %s: %s" fileName ex.Message
              elmRuntime.Dispatch (SageFsMsg.Event (
                SageFsEvent.FileReloaded (fileName, sw.Elapsed, Error ex.Message)))
        | SageFs.FileWatcher.FileChangeAction.SoftReset ->
          eprintfn "[INFO] Project file changed -- resetting session"
          elmRuntime.Dispatch (SageFsMsg.Event (
            SageFsEvent.SessionStatusChanged (sessionId, SessionDisplayStatus.Restarting)))
          appActor.PostAndAsyncReply(fun reply -> AppState.ResetSession(reply))
          |> Async.RunSynchronously
          |> ignore
        | SageFs.FileWatcher.FileChangeAction.Ignore -> ()
      Some (SageFs.FileWatcher.start config onFileChanged)

  eprintfn "SageFs daemon ready (PID %d, MCP port %d, dashboard port %d)" Environment.ProcessId mcpPort dashboardPort

  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    eprintfn "Shutting down..."
    cts.Cancel())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    DaemonState.clear ()
    eprintfn "Daemon stopped.")

  try
    // Run MCP server and dashboard until cancelled
    let! _ = System.Threading.Tasks.Task.WhenAny(
      System.Threading.Tasks.Task.Run(
        System.Func<System.Threading.Tasks.Task>(fun () -> mcpTask),
        cts.Token),
      System.Threading.Tasks.Task.Run(
        System.Func<System.Threading.Tasks.Task>(fun () -> dashboardTask),
        cts.Token))
    ()
  with
  | :? OperationCanceledException -> ()

  // Graceful shutdown: stop all sessions
  sessionManager.PostAndAsyncReply(fun reply ->
    SessionManager.SessionCommand.StopAll reply)
  |> Async.RunSynchronously

  DaemonState.clear ()
}
