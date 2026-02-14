module SageFs.Server.CliEventLoop

open SageFs.AppState
open SageFs
open System.Threading
open System.Threading.Tasks

open SageFs.Server.Logging
open SageFs.Server.PrettyPromptCallbacks
open SageFs.Features

let runSimpleEval (actor: AppActor) code ct =
  task {
    let request = {
      EvalRequest.Code = code
      Args = Map.empty
    }

    let! response = actor.PostAndAsyncReply(fun r -> Command.Eval(request, ct, r))
    return response
  }

let loadConfiguration actor =
  task {
    cliLogger.LogInfo "Loading configuration..."
    let! config = Configuration.loadGlobalConfig ()
    let! { EvaluationResult = r } = runSimpleEval actor config CancellationToken.None

    match r with
    | Error ex -> cliLogger.LogWarning <| ex.ToString()
    | Ok _ -> ()

    let! promptConfig = actor.PostAndAsyncReply(fun r -> Command.GetBoundValue("promptConfig", r))

    match promptConfig with
    | Some x when (x :? PrettyPrompt.PromptConfiguration) ->
      cliLogger.LogDebug "Done!"
      actor.Post Command.EnableStdout
      return x :?> PrettyPrompt.PromptConfiguration
    | _ ->
      cliLogger.LogError <| "Cannot find prompt configuration!"
      System.Environment.Exit 1
      return failwith "cannot happen"
  }

let runCliEventLoop useAsp mcpPort args () =
  task {
    let args = Args.parser.ParseCommandLine args

    // Set up event store — always available via Testcontainers or explicit connection string
    let connectionString = PostgresInfra.getOrStartPostgres ()
    let eventStore = SageFs.EventStore.configureStore connectionString
    let sessionId = SageFs.EventStore.createSessionId ()
    let onEvent (evt: SageFs.Features.Events.SageFsEvent) =
      SageFs.EventStore.appendEvents eventStore sessionId [evt]
      |> fun t -> t.ConfigureAwait(false).GetAwaiter().GetResult()

    let actorArgs =
      ActorCreation.mkCommonActorArgs cliLogger useAsp onEvent (args.GetAllResults())

    // Phase 1: Create actor immediately — MCP can serve status while warm-up runs
    let result = ActorCreation.createActorImmediate actorArgs
    let appActor = result.Actor

    // Ctrl-C cancels the current eval without killing the process
    System.Console.CancelKeyPress.Add(fun e ->
      e.Cancel <- true
      result.CancelEval() |> ignore
    )

    // Start MCP server BEFORE warm-up completes so get_fsi_status is available immediately
    match mcpPort with
    | Some port ->
        appActor.Post(AppState.UpdateMcpPort port)
        let mcpTask = McpServer.startMcpServer appActor result.DiagnosticsChanged result.CancelEval result.GetSessionState result.GetEvalStats result.GetWarmupFailures result.GetStartupConfig eventStore sessionId port SageFs.SessionMode.Embedded
        System.Threading.Tasks.Task.Run(System.Func<Task>(fun () -> mcpTask)) |> ignore
    | None -> ()

    // Phase 2: Add middleware — blocks until warm-up completes
    do! ActorCreation.addMiddleware result actorArgs.Middleware

    // Start file watcher for incremental reload on source changes
    let noWatch = args.Contains Args.Arguments.No_Watch
    let _fileWatcher =
      if noWatch || result.ProjectDirectories.IsEmpty then None
      else
        let config = SageFs.FileWatcher.defaultWatchConfig result.ProjectDirectories
        let onFileChanged (change: SageFs.FileWatcher.FileChange) =
          match SageFs.FileWatcher.fileChangeAction change with
          | SageFs.FileWatcher.FileChangeAction.Reload filePath ->
            eprintfn "\u001b[90m>> File changed: %s — reloading via #load\u001b[0m" (System.IO.Path.GetFileName filePath)
            let code = sprintf "#load @\"%s\"" filePath
            let request : AppState.EvalRequest = { Code = code; Args = Map.empty }
            appActor.PostAndAsyncReply(fun reply -> AppState.Eval(request, System.Threading.CancellationToken.None, reply))
            |> Async.RunSynchronously
            |> fun resp ->
              match resp.EvaluationResult with
              | Ok _ -> eprintfn "\u001b[90m>> Reloaded %s\u001b[0m" (System.IO.Path.GetFileName filePath)
              | Error ex -> eprintfn "\u001b[33m>> Reload error in %s: %s\u001b[0m" (System.IO.Path.GetFileName filePath) ex.Message
          | SageFs.FileWatcher.FileChangeAction.SoftReset ->
            eprintfn "\u001b[90m>> Project file changed — resetting session\u001b[0m"
            appActor.PostAndAsyncReply(fun reply -> AppState.ResetSession(reply))
            |> Async.RunSynchronously
            |> ignore
          | SageFs.FileWatcher.FileChangeAction.Ignore -> ()
        Some (SageFs.FileWatcher.start config onFileChanged)

    let! config = loadConfiguration appActor

    let version =
      System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
      |> Option.ofObj
      |> Option.map (fun v -> v.ToString())
      |> Option.defaultValue "unknown"
    printfn "%s" (McpAdapter.formatStartupBanner version mcpPort)

    // Interactive mode: Create prompt and read input
    let prompt =
      PrettyPrompt.Prompt(persistentHistoryFilepath = "./.sagefs_history", callbacks = FsiCallBacks appActor, configuration = config)

    while true do
      try
        let! userLine = prompt.ReadLineAsync()

        if userLine.IsSuccess then
          let! response = runSimpleEval appActor userLine.Text userLine.CancellationToken

          for d in response.Diagnostics do
            match d.Severity with
            | Diagnostics.Info -> cliLogger.LogInfo d.Message
            | Diagnostics.Hidden -> cliLogger.LogDebug d.Message
            | Diagnostics.Warning -> cliLogger.LogWarning d.Message
            | Diagnostics.Error -> cliLogger.LogError d.Message

          match response.EvaluationResult with
          | Ok output -> 
              // Print the captured output (includes both stdout and FSI evaluation result)
              if not (System.String.IsNullOrWhiteSpace output) then
                printfn "%s" output
          | Error e -> cliLogger.LogError <| e.ToString()

      with _ ->
        ()

  }
