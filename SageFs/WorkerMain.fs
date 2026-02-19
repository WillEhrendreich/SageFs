module SageFs.Server.WorkerMain

open System
open System.Threading
open SageFs
open SageFs.WorkerProtocol
open SageFs.AppState

/// Convert internal Diagnostic to WorkerDiagnostic for transport.
let toWorkerDiagnostic (d: Features.Diagnostics.Diagnostic) : WorkerDiagnostic =
  { Severity = d.Severity
    Message = d.Message
    StartLine = d.Range.StartLine
    StartColumn = d.Range.StartColumn
    EndLine = d.Range.EndLine
    EndColumn = d.Range.EndColumn }

/// Convert internal SessionState + EvalStats to WorkerStatusSnapshot.
let toStatusSnapshot
  (state: SessionState)
  (stats: Affordances.EvalStats)
  : WorkerStatusSnapshot =
  let avg =
    if stats.EvalCount > 0 then
      stats.TotalDuration.TotalMilliseconds / float stats.EvalCount |> int64
    else 0L
  let status =
    match state with
    | SessionState.Uninitialized
    | SessionState.WarmingUp -> SessionStatus.Starting
    | SessionState.Ready -> SessionStatus.Ready
    | SessionState.Evaluating -> SessionStatus.Evaluating
    | SessionState.Faulted -> SessionStatus.Faulted
  { Status = status
    EvalCount = stats.EvalCount
    AvgDurationMs = avg
    MinDurationMs = stats.MinDuration.TotalMilliseconds |> int64
    MaxDurationMs = stats.MaxDuration.TotalMilliseconds |> int64 }

/// Handle a single WorkerMessage by dispatching to the actor.
let handleMessage
  (actor: AppActor)
  (getState: unit -> SessionState)
  (getStats: unit -> Affordances.EvalStats)
  (msg: WorkerMessage)
  : Async<WorkerResponse> =
  async {
    match msg with
    | WorkerMessage.EvalCode(code, replyId) ->
      let request = { Code = code; Args = Map.empty }
      use cts = new CancellationTokenSource()
      let! response =
        actor.PostAndAsyncReply(fun rc -> Eval(request, cts.Token, rc))
      let diags = response.Diagnostics |> Array.map toWorkerDiagnostic |> Array.toList
      let result =
        match response.EvaluationResult with
        | Ok output -> Ok output
        | Error ex -> Error (ex.ToString())
      return WorkerResponse.EvalResult(replyId, result |> Result.mapError SageFsError.EvalFailed, diags)

    | WorkerMessage.CheckCode(code, replyId) ->
      let! diags = actor.PostAndAsyncReply(fun rc -> GetDiagnostics(code, rc))
      let workerDiags = diags |> Array.map toWorkerDiagnostic |> Array.toList
      return WorkerResponse.CheckResult(replyId, workerDiags)

    | WorkerMessage.GetCompletions(code, cursorPos, replyId) ->
      let word = ""
      let! completions =
        actor.PostAndAsyncReply(fun rc -> Autocomplete(code, cursorPos, word, rc))
      let names = completions |> List.map (fun c -> c.DisplayText)
      return WorkerResponse.CompletionResult(replyId, names)

    | WorkerMessage.CancelEval ->
      let! cancelled = actor.PostAndAsyncReply(fun rc -> CancelEval rc)
      return WorkerResponse.EvalCancelled cancelled

    | WorkerMessage.LoadScript(filePath, replyId) ->
      let code = sprintf "#load @\"%s\"" filePath
      let request = { Code = code; Args = Map.ofList ["hotReload", box true] }
      use cts = new CancellationTokenSource()
      let! response =
        actor.PostAndAsyncReply(fun rc -> Eval(request, cts.Token, rc))
      let result =
        match response.EvaluationResult with
        | Ok output -> Ok output
        | Error ex -> Error (ex.ToString())
      return WorkerResponse.ScriptLoaded(replyId, result |> Result.mapError SageFsError.ScriptLoadFailed)

    | WorkerMessage.ResetSession replyId ->
      let! result = actor.PostAndAsyncReply(fun rc -> ResetSession rc)
      return WorkerResponse.ResetResult(replyId, result)

    | WorkerMessage.HardResetSession(rebuild, replyId) ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> HardResetSession(rebuild, rc))
      return WorkerResponse.HardResetResult(replyId, result)

    | WorkerMessage.GetStatus replyId ->
      let state = getState ()
      let stats = getStats ()
      return WorkerResponse.StatusResult(replyId, toStatusSnapshot state stats)

    | WorkerMessage.Shutdown ->
      return WorkerResponse.WorkerShuttingDown
  }

/// Run the worker process: create actor, start HTTP server, handle messages.
let run (sessionId: string) (port: int) (args: Args.Arguments list) = async {
  let logger =
    { new Utils.ILogger with
        member _.LogInfo msg = eprintfn "[worker] %s" msg
        member _.LogDebug _ = ()
        member _.LogWarning msg = eprintfn "[worker] ⚠️ %s" msg
        member _.LogError msg = eprintfn "[worker] ❌ %s" msg }
  let onEvent _ = ()

  let actorArgs : ActorCreation.ActorArgs = {
    Middleware = ActorCreation.commonMiddleware
    InitFunctions = ActorCreation.commonInitFunctions
    Logger = logger
    OutStream = IO.TextWriter.Null
    UseAsp = false
    ParsedArgs = args
    OnEvent = onEvent
  }

  let! result =
    ActorCreation.createActor actorArgs |> Async.AwaitTask
  let actor = result.Actor

  // Start file watcher unless --no-watch was passed
  let noWatch = args |> List.exists (function Args.No_Watch -> true | _ -> false)
  let fileWatcher =
    if noWatch || List.isEmpty result.ProjectDirectories then
      None
    else
      let config = FileWatcher.defaultWatchConfig result.ProjectDirectories
      let onFileChanged (change: FileWatcher.FileChange) =
        match FileWatcher.fileChangeAction change with
        | FileWatcher.FileChangeAction.Reload filePath ->
          let code = sprintf "#load @\"%s\"" filePath
          let request = { Code = code; Args = Map.ofList ["hotReload", box true] }
          use localCts = new CancellationTokenSource()
          let response =
            actor.PostAndAsyncReply(fun rc -> Eval(request, localCts.Token, rc))
            |> Async.RunSynchronously
          match response.EvaluationResult with
          | Ok _ ->
            let reloaded =
              response.Metadata
              |> Map.tryFind "reloadedMethods"
              |> Option.bind (fun v ->
                match v with
                | :? (string list) as methods -> Some methods
                | _ -> None)
              |> Option.defaultValue []
            let fileName = IO.Path.GetFileName filePath
            if not (List.isEmpty reloaded) then
              eprintfn "🔥 Hot reloaded %s: %s" fileName (String.Join(", ", reloaded))
            else
              eprintfn "📄 Reloaded %s" fileName
          | Error ex ->
            eprintfn "⚠️ Reload failed for %s: %s" (IO.Path.GetFileName filePath) (ex.Message)
        | FileWatcher.FileChangeAction.SoftReset ->
          eprintfn "📦 Project file changed — soft reset needed"
          actor.PostAndAsyncReply(fun rc -> ResetSession rc)
          |> Async.RunSynchronously
          |> ignore
        | FileWatcher.FileChangeAction.Ignore -> ()
      Some (FileWatcher.start config onFileChanged)

  // Signal readiness over the pipe
  let handler = handleMessage actor result.GetSessionState result.GetEvalStats

  let readyHandler (msg: WorkerMessage) = async {
    match msg with
    | WorkerMessage.GetStatus _ ->
      // First message — respond with ready, then delegate
      return! handler msg
    | _ -> return! handler msg
  }

  use cts = new CancellationTokenSource()

  // Handle process signals
  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    cts.Cancel())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    cts.Cancel())

  try
    // Start HTTP server on requested port (0 = OS-assigned)
    let! server =
      WorkerHttpTransport.startServer handler port
      |> Async.AwaitTask
    // Print actual port to stdout so daemon can discover it
    printfn "WORKER_PORT=%s" server.BaseUrl
    Console.Out.Flush()

    // Block until cancellation
    let tcs = Threading.Tasks.TaskCompletionSource<unit>()
    use _reg = cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)
    do! tcs.Task |> Async.AwaitTask

    // Graceful shutdown
    (server :> IDisposable).Dispose()
  with
  | :? OperationCanceledException -> ()
  | ex ->
    eprintfn "Worker %s error: %s" sessionId (ex.ToString())

  // Clean up file watcher
  fileWatcher |> Option.iter (fun w -> w.Dispose())
}
