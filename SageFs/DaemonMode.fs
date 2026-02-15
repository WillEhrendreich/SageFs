module SageFs.Server.DaemonMode

open System
open System.Threading
open SageFs
open SageFs.Server

/// Run SageFs as a headless daemon.
/// No PrettyPrompt, no console UI — just MCP server + SessionManager.
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
          outputCount diagCount latest)

  // Start MCP server BEFORE warm-up completes
  appActor.Post(AppState.UpdateMcpPort mcpPort)
  let mcpTask =
    McpServer.startMcpServer
      appActor
      result.DiagnosticsChanged
      result.CancelEval
      result.GetSessionState
      result.GetEvalStats
      result.GetWarmupFailures
      result.GetStartupConfig
      eventStore
      sessionId
      mcpPort
      (SageFs.SessionMode.Daemon sessionOps)
      (Some elmRuntime)

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

  eprintfn "SageFs daemon ready (PID %d, MCP port %d)" Environment.ProcessId mcpPort

  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    eprintfn "Shutting down..."
    cts.Cancel())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    DaemonState.clear ()
    eprintfn "Daemon stopped.")

  try
    // Run MCP server until cancelled
    do! System.Threading.Tasks.Task.Run(
      System.Func<System.Threading.Tasks.Task>(fun () -> mcpTask),
      cts.Token)
  with
  | :? OperationCanceledException -> ()

  // Graceful shutdown: stop all sessions
  sessionManager.PostAndAsyncReply(fun reply ->
    SessionManager.SessionCommand.StopAll reply)
  |> Async.RunSynchronously

  DaemonState.clear ()
}
