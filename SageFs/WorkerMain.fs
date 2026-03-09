module SageFs.Server.WorkerMain

open System
open System.Threading
open SageFs
open SageFs.Utils
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
    match stats.EvalCount > 0 with
    | true -> stats.TotalDuration.TotalMilliseconds / float stats.EvalCount |> int64
    | false -> 0L
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
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Repl
      let diags = response.Diagnostics |> Array.map toWorkerDiagnostic |> Array.toList
      let result = response.EvaluationResult |> Result.mapError (fun ex -> ex.ToString())
      let metadata =
        response.Metadata
        |> Map.fold (fun acc k v ->
          match v with
          | :? SageFs.Features.LiveTesting.LiveTestHookResultDto as dto ->
            acc |> Map.add k (WorkerProtocol.Serialization.serialize dto)
          | _ -> acc) Map.empty
      // Capture RunTest closure from the latest discovery
      let metaKeys = response.Metadata |> Map.toList |> List.map fst |> String.concat ", "
      Log.debug "[WorkerMain] Metadata keys after eval: [%s]" metaKeys
      match response.Metadata |> Map.tryFind "liveTestRunTest" with
      | Some (:? (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) as runTest) ->
        Log.debug "[WorkerMain] RunTest captured from eval metadata"
        setRunTest runTest
      | Some v ->
        Log.warn "[WorkerMain] liveTestRunTest found but wrong type: %s" (v.GetType().FullName)
      | None ->
        Log.debug "[WorkerMain] liveTestRunTest NOT found in metadata"
      return WorkerResponse.EvalResult(replyId, result |> Result.mapError SageFsError.EvalFailed, diags, metadata)

    | WorkerMessage.CheckCode(code, replyId) ->
      let! diags =
        actor.PostAndAsyncReply(fun rc -> GetDiagnostics(code, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Check
      let workerDiags = diags |> Array.map toWorkerDiagnostic |> Array.toList
      return WorkerResponse.CheckResult(replyId, workerDiags)

    | WorkerMessage.TypeCheckWithSymbols(code, filePath, replyId) ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> GetTypeCheckWithSymbols(code, filePath, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Check
      let workerDiags = result.Diagnostics |> Array.map toWorkerDiagnostic |> Array.toList
      let workerSymRefs = result.SymbolRefs |> List.map WorkerProtocol.WorkerSymbolRef.fromDomain
      return WorkerResponse.TypeCheckWithSymbolsResult(replyId, result.HasErrors, workerDiags, workerSymRefs)

    | WorkerMessage.GetCompletions(code, cursorPos, replyId) ->
      let word = ""
      let! completions =
        actor.PostAndAsyncReply(fun rc -> Autocomplete(code, cursorPos, word, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Completion
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
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.HotReload
      let result = response.EvaluationResult |> Result.mapError (fun ex -> ex.ToString())
      return WorkerResponse.ScriptLoaded(replyId, result |> Result.mapError SageFsError.ScriptLoadFailed)

    | WorkerMessage.ResetSession replyId ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> ResetSession rc)
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Warmup
      return WorkerResponse.ResetResult(replyId, result)

    | WorkerMessage.HardResetSession(rebuild, replyId) ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> HardResetSession(rebuild, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Warmup
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
let run (sessionId: string) (port: int) = async {
  let workerConfig = Args.WorkerConfig.fromEnvironment sessionId port
  let loadConfig = Args.ProjectLoadConfig.fromWorkerConfig workerConfig
  let logger =
    { new Utils.ILogger with
        member _.LogInfo msg = Log.info "%s" msg
        member _.LogDebug msg = Log.debug "%s" msg
        member _.LogWarning msg = Log.warn "%s" msg
        member _.LogError msg = Log.error "%s" msg }
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
    LoadConfig = loadConfig
    IsBare = workerConfig.IsBare
    AutoOpenNamespaces = workerConfig.AutoOpenNamespaces
    OnEvent = onEvent
    HotReloadEnabled = workerConfig.HotReloadEnabled
  }

  let! result =
    ActorCreation.createActor actorArgs |> Async.AwaitTask
  let actor = result.Actor

  // Install DevReload Harmony patches only when hot-reload is explicitly enabled.
  // WARNING: this installs a process-wide JIT hook that requires FSI single-assembly
  // mode (--multiemit-), which disables type redefinition in the REPL.
  // Enable with SAGEFS_HOT_RELOAD=1.
  if workerConfig.HotReloadEnabled then
    DevReloadInjector.install()

  // Two-layer RunTest: project assemblies (stable) + dynamic FSI assemblies (updated per eval).
  // Warm-up evals go through the middleware (which discovers tests and builds a RunTest closure),
  // but the response metadata is consumed internally by the actor — handleMessage never sees it.
  // We discover tests directly from loaded assemblies after actor creation.
  let testFrameworkMarkers = [| "Expecto"; "xunit.core"; "xunit.v3.core"; "nunit.framework"; "Microsoft.VisualStudio.TestPlatform.TestFramework"; "TUnit.Core" |]
  let testAssemblies =
    System.AppDomain.CurrentDomain.GetAssemblies()
    |> Array.filter (fun a ->
      try
        a.GetReferencedAssemblies()
        |> Array.exists (fun r -> testFrameworkMarkers |> Array.contains r.Name)
      with ex ->
        Log.warn "[WorkerMain] Assembly framework check failed for %s: %s" a.FullName ex.Message
        false)
  let projectDiscoveryResults =
    testAssemblies
    |> Array.choose (fun asm ->
      try
        let hr =
          Features.LiveTesting.LiveTestingHook.afterReload
            Features.LiveTesting.BuiltInExecutors.builtIn asm []
        match hr.DiscoveredTests.Length > 0 with
        | true -> Some hr
        | false -> None
      with ex ->
        Log.error "[WorkerMain] LiveTestingHook.afterReload failed for %s: %s" asm.FullName ex.Message
        None)

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
    match runTests.Length with
    | 0 ->
      Features.LiveTesting.LiveTestHookResult.noOp
    | 1 ->
      runTests.[0]
    | _ ->
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

  // Dynamic RunTest from FSI evals (updated on each eval via handleMessage.EvalCode).
  // Chesterton's fence: ref + Volatile.Read/Interlocked.Exchange instead of mutable.
  // Writer (file watcher async on ThreadPool) and reader (HTTP handler thread) are
  // on different threads. Plain mutable has no memory barrier — on ARM64 .NET the
  // store may not be visible to the reader without volatile semantics.
  let latestDynamicRunTest : (Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult>) option ref =
    ref None

  // Composed RunTest: try dynamic first (for interactively defined tests), fall back to project
  let getRunTest () =
    match System.Threading.Volatile.Read(&latestDynamicRunTest.contents) with
    | Some dynamicRt ->
      fun (tc: Features.LiveTesting.TestCase) -> async {
        let! result = dynamicRt tc
        match result with
        | Features.LiveTesting.TestResult.NotRun -> return! projectRunTest tc
        | found -> return found }
    | None -> projectRunTest
  let setDynamicRunTest v = System.Threading.Interlocked.Exchange(latestDynamicRunTest, Some v) |> ignore

  // Start file watcher unless no-watch was set
  let fileWatcher =
    match workerConfig.NoWatch || List.isEmpty result.ProjectDirectories with
    | true ->
      match workerConfig.NoWatch with
      | true -> Log.info "File watcher disabled (SAGEFS_NO_WATCH=1)"
      | false -> Log.warn "File watcher skipped: no project directories found"
      None
    | false ->
      Log.info "File watcher starting for %d directories: %s"
        result.ProjectDirectories.Length
        (String.Join(", ", result.ProjectDirectories))
      let config = FileWatcher.defaultWatchConfig result.ProjectDirectories
      // Chesterton's fence: per-watcher CompilationState tracks module context
      // across hot-reload cycles. Without this, each reload is context-free —
      // preprocessForFsi can't determine which modules are already `open`'d,
      // leading to duplicate module errors or missing opens.
      let mutable compilationState = Middleware.CompilationContext.CompilationState.empty
      // Chesterton's fence: SemaphoreSlim(1,1) serializes file change processing.
      // Without this, two different files changing within the debounce window spawn
      // two async workflows that race on `compilationState` — the mutable
      // EvaluatedModules set could lose an entry from a concurrent read-modify-write.
      let compilationLock = new Threading.SemaphoreSlim(1, 1)
      // Chesterton's fence: per-file CancellationTokenSource enables cancel-and-restart.
      // When a user rapid-saves, the new change cancels the previous eval for the same
      // file (which may be compiling an intermediate broken state), so only the latest
      // content is evaluated. Without this, rapid saves queue up multiple evals that
      // flash red errors before the final green.
      let perFileCts = System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>()
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
        // Cancel any in-flight eval for this exact file — only latest save matters.
        // Chesterton's fence: AddOrUpdate is atomic — eliminates the TOCTOU race where
        // TryGetValue + manual cancel + indexer assignment could interleave with another
        // thread's update for the same file path.
        let filePath = change.FilePath
        let newCts = new CancellationTokenSource()
        perFileCts.AddOrUpdate(
          filePath,
          newCts,
          fun _key oldCts ->
            try oldCts.Cancel()
            with
            | :? ObjectDisposedException -> ()
            | ex -> Log.warn "[WorkerMain] CTS cancel failed for %s: %s" filePath ex.Message
            oldCts.Dispose()
            newCts)
        |> ignore
        let ct = newCts.Token
        Async.Start(async {
          do! compilationLock.WaitAsync(ct) |> Async.AwaitTask
          try
            try
              ct.ThrowIfCancellationRequested()
              match FileWatcher.fileChangeAction change with
              | FileWatcher.FileChangeAction.Reload filePath ->
                match HotReloadState.isWatched filePath !result.HotReloadStateRef with
                | false ->
                  Log.debug "File changed but not in hot-reload watch set: %s (watched: %d files)"
                    (IO.Path.GetFileName filePath) (HotReloadState.watchedCount !result.HotReloadStateRef)
                | true ->
                Log.debug "[DevReload] Reloading watched file: %s" (IO.Path.GetFileName filePath)
                DevReload.broadcastCompiling (Some (IO.Path.GetFileName filePath))
                // Chesterton's fence: read file and preprocess through CompilationContext
                // instead of using `#load`. `#load` re-executes the entire file including
                // module-level side effects (server startup, DB connections), causing type
                // errors ("unit doesn't match Task") in files with effectful top-level code.
                // CompilationContext strips the module declaration and wraps definitions
                // properly for FSI, preserving only type/function definitions. The existing
                // HotReloading middleware then applies NoInlining + Harmony detours.
                let fileContent = IO.File.ReadAllText(filePath)
                let! fileStructure, updatedCache = async {
                  try
                    let! fs, cache =
                      Middleware.CompilationContext.parseFileStructureCached
                        filePath fileContent compilationState.FileCache
                      |> Async.AwaitTask
                    return Some fs, cache
                  with exn ->
                    // Chesterton's fence: do NOT fall back to #load here. #load re-executes
                    // the entire file including module-level side effects (app.RunAsync(),
                    // DB connections), which is the exact bug CompilationContext was built
                    // to fix. Instead, broadcast the parse failure to the browser and skip
                    // the reload. The user sees the error, fixes the file, saves again.
                    Log.warn "CompilationContext parse failed for %s — file not reloaded: %s"
                      filePath exn.Message
                    DevReload.broadcastCompilationFailed
                      (sprintf "Parse failed for %s: %s" (IO.Path.GetFileName filePath) exn.Message) []
                    return None, compilationState.FileCache
                }
                match fileStructure with
                | None -> () // parse failed — error already broadcast, skip reload
                | Some _ ->
                Log.debug "[DevReload] Parse succeeded for %s — preprocessing" (IO.Path.GetFileName filePath)
                let preprocessed, updatedModules =
                  Middleware.CompilationContext.preprocessForFsi
                    fileStructure
                    Middleware.CompilationContext.EvalMode.File
                    None
                    compilationState.EvaluatedModules
                    fileContent
                compilationState <-
                  { compilationState with
                      EvaluatedModules = updatedModules
                      FileCache = updatedCache }
                let code = preprocessed.Code
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
                  match List.isEmpty reloaded with
                  | false ->
                    Log.info "Hot reloaded %s: %s" fileName (String.Join(", ", reloaded))
                  | true ->
                    // Chesterton's fence: broadcastReload even when no methods were detouring.
                    // When a file adds NEW types/functions (not modifying existing ones),
                    // Harmony finds no methods to detour, so triggerReload() in HotReloading.fs
                    // is never called. Without this, the browser stays stuck on "⟳ Recompiling..."
                    // forever — violating the Compiling→(Reload|CompilationFailed) contract.
                    DevReload.broadcastReload ()
                    Log.info "Reloaded %s (new types/functions, no methods detouring)" fileName
                | Error ex ->
                  // Chesterton's fence: broadcastCompilationFailed ensures the browser
                  // overlay transitions from "Recompiling..." to the error message.
                  // Without this, compilation errors leave the overlay stuck on blue
                  // "Recompiling..." forever — the #1 reported DX issue.
                  let fileName = IO.Path.GetFileName filePath
                  let summary = sprintf "%s: %s" fileName ex.Message
                  // Extract structured diagnostics with source-mapped line numbers.
                  // Chesterton's fence: preprocessed.LineOffset compensates for lines
                  // added/removed by CompilationContext preprocessing (module wrapper,
                  // #load directives). Without applying this offset, browser error
                  // overlay shows FSI-internal line numbers that don't match the user's
                  // source file — the #1 DX complaint from the expert panel.
                  let diagnostics =
                    response.Diagnostics
                    |> Array.filter (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Error || d.Severity = Features.Diagnostics.DiagnosticSeverity.Warning)
                    |> Array.map (fun d ->
                      ({ File = fileName
                         Line = Middleware.CompilationContext.mapDiagnosticLine preprocessed.LineOffset d.Range.StartLine
                         EndLine = Middleware.CompilationContext.mapDiagnosticLine preprocessed.LineOffset d.Range.EndLine
                         Column = d.Range.StartColumn
                         EndColumn = d.Range.EndColumn
                         Severity = Features.Diagnostics.DiagnosticSeverity.label d.Severity
                         DiagCode =
                           match d.Subcategory with
                           | s when String.IsNullOrWhiteSpace s -> None
                           | s -> Some s
                         Message = d.Message
                         SourceContext = None
                         SourceContextStartLine = None } : DevReload.DevReloadDiagnostic)
                      |> DevReload.DevReloadDiagnostic.addSourceContext)
                    |> Array.toList
                  DevReload.broadcastCompilationFailed summary diagnostics
                  Log.warn "Reload failed for %s: %s" fileName (ex.Message)
              | FileWatcher.FileChangeAction.SoftReset ->
                Log.info "Project file changed — soft reset needed"
                let! _ = actor.PostAndAsyncReply(fun rc -> ResetSession rc)
                ()
              | FileWatcher.FileChangeAction.Ignore -> ()
            with
            | :? OperationCanceledException ->
              Log.debug "File change cancelled (superseded by newer save): %s"
                (IO.Path.GetFileName change.FilePath)
            | ex ->
              // Chesterton's fence: if the actor mailbox crashes or PostAndAsyncReply
              // throws, we must still close the Compiling→(Reload|CompilationFailed)
              // lifecycle. Without this catch-all, an unhandled exception leaves the
              // browser stuck on "⟳ Recompiling..." with no recovery path.
              DevReload.broadcastCompilationFailed (sprintf "Internal error: %s" ex.Message) []
              Log.error "File watcher async failed: %s" (ex.ToString())
          finally
            compilationLock.Release() |> ignore
        })
      Some (FileWatcher.start config DevReload.DevReloadConfig.defaults onFileChanged)

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
        match IO.Directory.Exists(dir) with
        | true ->
          IO.Directory.GetFiles(dir, "*.fs", IO.SearchOption.AllDirectories)
          |> Array.append (IO.Directory.GetFiles(dir, "*.fsx", IO.SearchOption.AllDirectories))
          |> Array.toList
          |> List.filter (fun f ->
            let n = f.Replace('\\', '/')
            not (n.Contains("/obj/") || n.Contains("/bin/")))
        | false -> [])

    // Chesterton's fence: watch all project files by default. Without this,
    // hot-reload is silently OFF until users discover the toggle UI and manually
    // opt files in — the #1 onboarding friction point. Users who want the old
    // opt-in behavior can set SAGEFS_HOT_RELOAD=opt-in.
    match Environment.GetEnvironmentVariable("SAGEFS_HOT_RELOAD") with
    | "opt-in" -> ()
    | _ ->
      result.HotReloadStateRef.Value <-
        HotReloadState.watchAll projectFiles HotReloadState.empty
      Log.info "Hot reload: watching %d project files by default" projectFiles.Length
    let! server =
      WorkerHttpTransport.startServer readyHandler result.HotReloadStateRef projectFiles result.GetWarmupContext getRunTest port
      |> Async.AwaitTask
    // Print actual port to stdout so daemon can discover it
    printfn "WORKER_PORT=%s" server.BaseUrl
    Console.Out.Flush()

    // Tell DevReload Harmony patches which port to inject into user scripts
    let uri = Uri(server.BaseUrl)
    DevReloadInjector.setWorkerPort uri.Port

    // Block until cancellation
    let tcs = Threading.Tasks.TaskCompletionSource<unit>()
    use _reg = cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)
    do! tcs.Task |> Async.AwaitTask

    // Graceful shutdown
    (server :> IDisposable).Dispose()
  with
  | :? OperationCanceledException -> ()
  | ex ->
    Log.error "Worker %s error: %s" sessionId (ex.ToString())

  // Clean up file watcher
  fileWatcher |> Option.iter (fun w -> w.Dispose())
}
