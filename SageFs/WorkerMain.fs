module SageFs.Server.WorkerMain

open System
open System.Threading
open SageFs
open SageFs.WorkerProtocol
open SageFs.AppState

/// Convert internal Diagnostic to WorkerDiagnostic for transport.
let private toWorkerDiagnostic (d: Features.Diagnostics.Diagnostic) : WorkerDiagnostic =
  { Severity = d.Severity
    Message = d.Message
    StartLine = d.Range.StartLine
    StartColumn = d.Range.StartColumn
    EndLine = d.Range.EndLine
    EndColumn = d.Range.EndColumn }

/// Convert internal SessionState + EvalStats to WorkerStatusSnapshot.
let private toStatusSnapshot
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
      let request = { Code = code; Args = Map.empty }
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

/// Run the worker process: create actor, listen on pipe, handle messages.
let run (sessionId: string) (pipeName: string) (args: Args.Arguments list) = async {
  let logger =
    { new Utils.ILogger with
        member _.LogInfo _ = ()
        member _.LogDebug _ = ()
        member _.LogWarning _ = ()
        member _.LogError _ = () }
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
    do! NamedPipeTransport.listen pipeName handler cts.Token
  with
  | :? OperationCanceledException -> ()
  | ex ->
    eprintfn "Worker %s error: %s" sessionId (ex.ToString())
}
