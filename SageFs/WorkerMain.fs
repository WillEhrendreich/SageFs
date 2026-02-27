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
  (statusMsg: string option)
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
    StatusMessage = statusMsg
    EvalCount = stats.EvalCount
    AvgDurationMs = avg
    MinDurationMs = stats.MinDuration.TotalMilliseconds |> int64
    MaxDurationMs = stats.MaxDuration.TotalMilliseconds |> int64 }

/// Handle a single WorkerMessage by dispatching to the actor.
let handleMessage
  (actor: AppActor)
  (getState: unit -> SessionState)
  (getStats: unit -> Affordances.EvalStats)
  (getStatusMessage: unit -> string option)
  (getRunTest: unit -> (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>))
  (setRunTest: (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) -> unit)
  (getInitialDiscovery: unit -> Features.LiveTesting.TestCase array * Features.LiveTesting.ProviderDescription list)
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
      let metadata =
        response.Metadata
        |> Map.fold (fun acc k v ->
          match v with
          | :? SageFs.Features.LiveTesting.LiveTestHookResultDto as dto ->
            acc |> Map.add k (WorkerProtocol.Serialization.serialize dto)
          | _ -> acc) Map.empty
      // Capture RunTest closure from the latest discovery
      let metaKeys = response.Metadata |> Map.toList |> List.map fst |> String.concat ", "
      eprintfn "[WorkerMain] Metadata keys after eval: [%s]" metaKeys
      match response.Metadata |> Map.tryFind "liveTestRunTest" with
      | Some (:? (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) as runTest) ->
        eprintfn "[WorkerMain] ✅ RunTest captured from eval metadata"
        setRunTest runTest
      | Some v ->
        eprintfn "[WorkerMain] ⚠️ liveTestRunTest found but wrong type: %s" (v.GetType().FullName)
      | None ->
        eprintfn "[WorkerMain] ❌ liveTestRunTest NOT found in metadata"
      return WorkerResponse.EvalResult(replyId, result |> Result.mapError SageFsError.EvalFailed, diags, metadata)

    | WorkerMessage.CheckCode(code, replyId) ->
      let! diags = actor.PostAndAsyncReply(fun rc -> GetDiagnostics(code, rc))
      let workerDiags = diags |> Array.map toWorkerDiagnostic |> Array.toList
      return WorkerResponse.CheckResult(replyId, workerDiags)

    | WorkerMessage.TypeCheckWithSymbols(code, filePath, replyId) ->
      let! result = actor.PostAndAsyncReply(fun rc -> GetTypeCheckWithSymbols(code, filePath, rc))
      let workerDiags = result.Diagnostics |> Array.map toWorkerDiagnostic |> Array.toList
      let workerSymRefs = result.SymbolRefs |> List.map WorkerProtocol.WorkerSymbolRef.fromDomain
      return WorkerResponse.TypeCheckWithSymbolsResult(replyId, result.HasErrors, workerDiags, workerSymRefs)

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
      return WorkerResponse.StatusResult(replyId, toStatusSnapshot state stats (getStatusMessage()))

    | WorkerMessage.RunTests(tests, maxParallelism, replyId) ->
      let runTest = getRunTest()
      let results = System.Collections.Concurrent.ConcurrentBag<Features.LiveTesting.TestRunResult>()
      use cts = new CancellationTokenSource(TimeSpan.FromSeconds(float (30 + tests.Length / 10)))
      do!
        Features.LiveTesting.TestOrchestrator.executeFiltered
          runTest (fun r -> results.Add r) maxParallelism tests cts.Token
      return WorkerResponse.TestRunResults(replyId, results.ToArray())

    | WorkerMessage.GetTestDiscovery(replyId) ->
      let tests, providers = getInitialDiscovery()
      return WorkerResponse.InitialTestDiscovery(tests, providers)

    | WorkerMessage.GetInstrumentationMaps _ ->
      return WorkerResponse.InstrumentationMapsResult("", [||])

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
  let onEvent (evt: Features.Events.SageFsEvent) =
    match evt with
    | Features.Events.SageFsEvent.SessionWarmUpProgress p ->
      printfn "WARMUP_PROGRESS=%d/%d %s" p.Step p.Total p.Message
      Console.Out.Flush()
    | _ -> ()

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

  // Two-layer RunTest: project assemblies (stable) + dynamic FSI assemblies (updated per eval).
  // Warm-up evals go through the middleware (which discovers tests and builds a RunTest closure),
  // but the response metadata is consumed internally by the actor — handleMessage never sees it.
  // We discover tests directly from loaded assemblies after actor creation.
  let testFrameworkMarkers = [| "Expecto"; "xunit.core"; "nunit.framework"; "Microsoft.VisualStudio.TestPlatform.TestFramework"; "TUnit.Core" |]
  let testAssemblies =
    System.AppDomain.CurrentDomain.GetAssemblies()
    |> Array.filter (fun a ->
      try
        a.GetReferencedAssemblies()
        |> Array.exists (fun r -> testFrameworkMarkers |> Array.contains r.Name)
      with _ -> false)
  let projectDiscoveryResults =
    testAssemblies
    |> Array.choose (fun asm ->
      try
        let hr =
          Features.LiveTesting.LiveTestingHook.afterReload
            Features.LiveTesting.BuiltInExecutors.builtIn asm []
        if hr.DiscoveredTests.Length > 0 then Some hr
        else None
      with _ -> None)

  let initialDiscoveredTests =
    projectDiscoveryResults |> Array.collect (fun r -> r.DiscoveredTests)
  let initialProviders =
    projectDiscoveryResults
    |> Array.collect (fun r -> r.DetectedProviders |> List.toArray)
    |> Array.distinctBy (fun p ->
      match p with
      | Features.LiveTesting.ProviderDescription.AttributeBased a -> a.Name
      | Features.LiveTesting.ProviderDescription.Custom c -> c.Name)
    |> Array.toList

  let projectRunTest =
    let runTests = projectDiscoveryResults |> Array.map (fun r -> r.RunTest)
    if runTests.Length = 0 then
      Features.LiveTesting.LiveTestHookResult.noOp
    elif runTests.Length = 1 then
      runTests.[0]
    else
      fun (tc: Features.LiveTesting.TestCase) ->
        let rec tryRunners (idx: int) remaining = async {
          match remaining with
          | [] -> return Features.LiveTesting.TestResult.NotRun
          | rt :: rest ->
            let! result = rt tc
            match result with
            | Features.LiveTesting.TestResult.NotRun -> return! tryRunners (idx + 1) rest
            | found -> return found }
        tryRunners 0 (runTests |> Array.toList)

  // Dynamic RunTest from FSI evals (updated on each eval via handleMessage.EvalCode)
  let mutable latestDynamicRunTest : (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) option =
    None

  // Composed RunTest: try dynamic first (for interactively defined tests), fall back to project
  let getRunTest () =
    match latestDynamicRunTest with
    | Some dynamicRt ->
      fun (tc: Features.LiveTesting.TestCase) -> async {
        let! result = dynamicRt tc
        match result with
        | Features.LiveTesting.TestResult.NotRun -> return! projectRunTest tc
        | found -> return found }
    | None -> projectRunTest
  let setDynamicRunTest v = latestDynamicRunTest <- Some v

  // Start file watcher unless --no-watch was passed
  let noWatch = args |> List.exists (function Args.No_Watch -> true | _ -> false)
  let fileWatcher =
    if noWatch || List.isEmpty result.ProjectDirectories then
      None
    else
      let config = FileWatcher.defaultWatchConfig result.ProjectDirectories
      let onFileChanged (change: FileWatcher.FileChange) =
        let ext = IO.Path.GetExtension(change.FilePath)
        let kind = match change.Kind with
                   | FileWatcher.FileChangeKind.Changed -> "Modified"
                   | FileWatcher.FileChangeKind.Created -> "Created"
                   | FileWatcher.FileChangeKind.Deleted -> "Deleted"
                   | FileWatcher.FileChangeKind.Renamed -> "Renamed"
        Instrumentation.fileWatcherChanges.Add(
          1L,
          System.Collections.Generic.KeyValuePair("file.extension", ext :> obj),
          System.Collections.Generic.KeyValuePair("change.kind", kind :> obj))
        Async.Start(async {
          match FileWatcher.fileChangeAction change with
          | FileWatcher.FileChangeAction.Reload filePath ->
            if not (HotReloadState.isWatched filePath !result.HotReloadStateRef) then
              () // File not opted-in for hot-reload
            else
            let code = sprintf "#load @\"%s\"" filePath
            let request = { Code = code; Args = Map.ofList ["hotReload", box true] }
            use localCts = new CancellationTokenSource()
            let! response =
              actor.PostAndAsyncReply(fun rc -> Eval(request, localCts.Token, rc))
            match response.EvaluationResult with
            | Ok _ ->
              // Capture RunTest from hot-reload discovery
              match response.Metadata |> Map.tryFind "liveTestRunTest" with
              | Some (:? (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) as runTest) ->
                setDynamicRunTest runTest
              | _ -> ()
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
            let! _ = actor.PostAndAsyncReply(fun rc -> ResetSession rc)
            ()
          | FileWatcher.FileChangeAction.Ignore -> ()
        })
      Some (FileWatcher.start config onFileChanged)

  // Signal readiness over the pipe
  let handler =
    handleMessage actor result.GetSessionState result.GetEvalStats result.GetStatusMessage
      getRunTest setDynamicRunTest (fun () -> initialDiscoveredTests, initialProviders)

  let readyHandler (msg: WorkerMessage) = async {
    match msg with
    | WorkerMessage.GetInstrumentationMaps(replyId) ->
      return WorkerResponse.InstrumentationMapsResult(replyId, result.InstrumentationMaps)
    | _ -> return! handler msg
  }

  use cts = new CancellationTokenSource()

  // Handle process signals — guard against ObjectDisposedException
  // if the CTS is disposed before the event fires (e.g. daemon kills worker)
  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    try cts.Cancel() with :? ObjectDisposedException -> ())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    try cts.Cancel() with :? ObjectDisposedException -> ())

  try
    // Start HTTP server on requested port (0 = OS-assigned)
    // Collect all .fs/.fsx files from project directories for hot-reload UI
    let projectFiles =
      result.ProjectDirectories
      |> List.collect (fun dir ->
        if IO.Directory.Exists(dir) then
          IO.Directory.GetFiles(dir, "*.fs", IO.SearchOption.AllDirectories)
          |> Array.append (IO.Directory.GetFiles(dir, "*.fsx", IO.SearchOption.AllDirectories))
          |> Array.toList
          |> List.filter (fun f ->
            let n = f.Replace('\\', '/')
            not (n.Contains("/obj/") || n.Contains("/bin/")))
        else [])
    let! server =
      WorkerHttpTransport.startServer readyHandler result.HotReloadStateRef projectFiles result.GetWarmupContext getRunTest port
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
