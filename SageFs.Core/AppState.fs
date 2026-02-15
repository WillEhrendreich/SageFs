module SageFs.AppState

open System
open System.IO

open System.Threading
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open FSharpPlus
open SageFs.Features
open SageFs.ProjectLoading
open SageFs.Utils
open SageFs.WarmUp

type FilePath = string

open System.Text

type TextWriterRecorder(writerToRecord: TextWriter) =
  inherit TextWriter()

  let mutable recording: StringBuilder option = None
  let mutable lastCharWasCR = false

  override _.Encoding = writerToRecord.Encoding

  override _.Write(value: char) =
    match recording with
    | None -> ()
    | Some recorder -> recorder.Append value |> ignore

    if value = '\n' then
      if not lastCharWasCR then
        writerToRecord.Write '\r'
      writerToRecord.Write '\n'
      lastCharWasCR <- false
    else
      lastCharWasCR <- (value = '\r')
      writerToRecord.Write value

  override _.Write(value: string) =
    match recording with
    | None -> ()
    | Some recorder -> recorder.Append value |> ignore

    let normalized = value.Replace("\r\n", "\n").Replace("\n", "\r\n")
    writerToRecord.Write normalized

  override _.Write(bufferArr: char[], index: int, count: int) =
    match recording with
    | None -> ()
    | Some recorder -> recorder.Append(bufferArr, index, count) |> ignore

    let s = new string(bufferArr, index, count)
    let normalized = s.Replace("\r\n", "\n").Replace("\n", "\r\n")
    writerToRecord.Write normalized

  member _.Enable() = () // No longer needed but kept for compatibility

  member _.StartRecording() =
    recording <- Some <| new StringBuilder()

  member _.StopRecording() =
    match recording with
    | None -> ""
    | Some recorder ->
      recording <- None
      recorder.ToString()

  override _.Flush() = writerToRecord.Flush()

type StartupConfig = {
  CommandLineArgs: string[]
  LoadedProjects: string list
  WorkingDirectory: string
  McpPort: int
  HotReloadEnabled: bool
  AspireDetected: bool
  StartupTimestamp: DateTime
  StartupProfileLoaded: string option
}

/// A warm-up failure: namespace/module name and error message.
type WarmupFailure = { Name: string; Error: string }

type AppState = {
  Solution: Solution
  OriginalSolution: Solution
  ShadowDir: string option
  Logger: ILogger
  Session: FsiEvaluationSession
  OutStream: TextWriterRecorder
  StartupConfig: StartupConfig option
  Custom: Map<string, obj>
  Diagnostics: Features.DiagnosticsStore.T
  WarmupFailures: WarmupFailure list
}

type EvalResponse = {
  EvaluationResult: Result<string, Exception>
  Diagnostics: Diagnostics.Diagnostic array
  EvaluatedCode: string
  Metadata: Map<string, objnull>
}

type EvalRequest = { Code: string; Args: Map<string, obj> }

type MiddlewareNext = EvalRequest * AppState -> EvalResponse * AppState
type Middleware = MiddlewareNext -> EvalRequest * AppState -> EvalResponse * AppState

type Command =
  | Eval of EvalRequest * CancellationToken * AsyncReplyChannel<EvalResponse>
  | CancelEval of AsyncReplyChannel<bool>
  | Autocomplete of text: string * caret: int * word: string * AsyncReplyChannel<list<AutoCompletion.CompletionItem>>
  | GetBoundValue of name: string * AsyncReplyChannel<obj Option>
  | AddMiddleware of Middleware list * AsyncReplyChannel<unit>
  | GetDiagnostics of text: string * AsyncReplyChannel<Diagnostics.Diagnostic array>
  | GetAppState of AsyncReplyChannel<AppState>
  | GetSessionState of AsyncReplyChannel<SessionState>
  | GetStartupConfig of AsyncReplyChannel<StartupConfig option>
  | GetWarmupFailures of AsyncReplyChannel<WarmupFailure list>
  | EnableStdout
  | UpdateMcpPort of int
  | ResetSession of AsyncReplyChannel<Result<unit, SageFsError>>
  | HardResetSession of rebuild: bool * AsyncReplyChannel<Result<string, SageFsError>>

type AppActor = MailboxProcessor<Command>

/// Immutable snapshot published from eval actor to query actor.
/// Query actor serves reads from this — no shared mutable state.
type QuerySnapshot = {
  AppState: AppState
  SessionState: SessionState
  EvalStats: Affordances.EvalStats
  StartupConfig: StartupConfig option
  WarmupFailures: WarmupFailure list
}

/// Internal command for the query actor
type internal QueryCommand =
  | UpdateSnapshot of QuerySnapshot
  | QueryGetAppState of AsyncReplyChannel<AppState>
  | QueryGetSessionState of AsyncReplyChannel<SessionState>
  | QueryGetEvalStats of AsyncReplyChannel<Affordances.EvalStats>
  | QueryGetStartupConfig of AsyncReplyChannel<StartupConfig option>
  | QueryGetWarmupFailures of AsyncReplyChannel<WarmupFailure list>
  | QueryAutocomplete of text: string * caret: int * word: string * AsyncReplyChannel<list<AutoCompletion.CompletionItem>>
  | QueryGetDiagnostics of text: string * AsyncReplyChannel<Diagnostics.Diagnostic array>
  | QueryGetBoundValue of name: string * AsyncReplyChannel<obj Option>
  | QueryUpdateMcpPort of int

/// Internal command for the eval actor — only mutation/eval operations
type internal EvalCommand =
  | EvalRun of EvalRequest * CancellationTokenSource * AsyncReplyChannel<EvalResponse>
  | EvalFinished of result: Result<EvalResponse * AppState, exn> * sw: Diagnostics.Stopwatch * code: string * AsyncReplyChannel<EvalResponse>
  | EvalAddMiddleware of Middleware list * AsyncReplyChannel<unit>
  | EvalEnableStdout
  | EvalReset of AsyncReplyChannel<Result<unit, SageFsError>>
  | EvalHardReset of rebuild: bool * AsyncReplyChannel<Result<string, SageFsError>>

let wrapErrorMiddleware next (request, st) =
  try
    next (request, st)
  with e ->
    let errResponse = {
      EvaluationResult = Error <| new Exception("SageFsInternal error occured", e)
      Diagnostics = [||]
      EvaluatedCode = ""
      Metadata = Map.empty
    }

    errResponse, st

//fold - first m in list would be the closest to eval
//foldBack - last m in list would be the closest to eval
//better to use foldBack as we can simply push new m's and it's more intuitive that
//the last m would evaluate the latest
let buildPipeline (middleware: Middleware list) evalFn =
  List.foldBack (fun m next -> m next) middleware evalFn

let evalFn (token: CancellationToken) =
  fun ({ Code = code }, st) ->
    st.OutStream.StartRecording()
    let thread = Thread.CurrentThread
    token.Register(fun () -> thread.Interrupt()) |> ignore
    let evalRes, diagnostics = st.Session.EvalInteractionNonThrowing(code, token)
    let diagnostics = diagnostics |> Array.map Diagnostics.Diagnostic.mkDiagnostic

    let evalRes =
      match evalRes with
      | Choice1Of2 _ -> Ok <| st.OutStream.StopRecording()
      | Choice2Of2 ex -> Error <| ex

    st.OutStream.StopRecording() |> ignore

    {
      EvaluationResult = evalRes
      Diagnostics = diagnostics
      Metadata = Map.empty
      EvaluatedCode = code
    },
    st

open System.Threading.Tasks
open System.Threading

/// Creates a fresh FSI session with warm-up: loads startup files and opens namespaces.
let createFsiSession (logger: ILogger) (outStream: TextWriter) (useAsp: bool) (sln: Solution) =
  async {
    let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()
    let args = solutionToFsiArgs logger useAsp sln
    let recorder = new TextWriterRecorder(outStream)

    let fsiSession =
      FsiEvaluationSession.Create(fsiConfig, args, new StreamReader(Stream.Null), recorder, TextWriter.Null, collectible = true)

    for fileName in sln.StartupFiles do
      logger.LogInfo $"Loading {fileName}"
      let! fileContents = File.ReadAllTextAsync fileName |> Async.AwaitTask
      let compatibleContents = FsiRewrite.rewriteInlineUseStatements fileContents
      if compatibleContents <> fileContents then
        logger.LogInfo $"⚡ Applied FSI compatibility transforms to {fileName}"
        let beforeCount = (fileContents.Split('\n') |> Array.filter (fun line -> line.TrimStart().StartsWith("use "))).Length
        let afterCount = (compatibleContents.Split('\n') |> Array.filter (fun line -> line.TrimStart().StartsWith("use "))).Length  
        logger.LogInfo $"   Rewrote {beforeCount - afterCount} 'use' statements to 'let'"
      fsiSession.EvalInteraction(compatibleContents, CancellationToken.None)

    let openedNamespaces = System.Collections.Generic.HashSet<string>()
    let namesToOpen = System.Collections.Generic.List<string>()
    let moduleNames = System.Collections.Generic.HashSet<string>()

    // Phase 1: Collect namespaces from source files
    let allFsFiles =
      sln.FsProjects
      |> Seq.collect (fun proj -> proj.SourceFiles)
      |> Seq.filter (fun f -> f.EndsWith(".fs") || f.EndsWith(".fsx"))
      |> Seq.distinct

    for fsFile in allFsFiles do
      try
        if File.Exists(fsFile) then
          let! sourceLines = File.ReadAllLinesAsync fsFile |> Async.AwaitTask
          for line in sourceLines do
            let trimmed = line.Trim()
            if trimmed.StartsWith("open ") && not (trimmed.StartsWith("//")) then
              let parts = trimmed.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
              if parts.Length >= 2 then
                let nsName = parts.[1].TrimEnd(';')
                if openedNamespaces.Add(nsName) then
                  namesToOpen.Add(nsName)
      with ex ->
        logger.LogDebug (sprintf "Could not parse opens from %s: %s" fsFile ex.Message)

    // Phase 2: Collect namespaces/modules via reflection
    // Use a collectible AssemblyLoadContext to avoid the default context's identity cache.
    // Assembly.LoadFrom caches by identity — after hard reset + rebuild, it returns the
    // OLD assembly even though the shadow-copied DLL on disk has new types.
    let reflectionAlc =
      new System.Runtime.Loader.AssemblyLoadContext(
        "sagefs-reflection", isCollectible = true)
    for project in sln.Projects do
      try
        let asm = reflectionAlc.LoadFromAssemblyPath(project.TargetPath)
        let types =
          try
            asm.GetTypes()
          with
          | :? System.Reflection.ReflectionTypeLoadException as ex ->
            ex.Types |> Array.filter (fun t -> t <> null)

        let rootNamespaces =
          types
          |> Array.choose (fun t ->
            if not (t.Namespace |> isNull) then
              let parts = t.Namespace.Split('.')
              if parts.Length > 0 then Some parts.[0] else None
            else
              None)
          |> Array.distinct
          |> Array.filter (fun ns -> not (ns.StartsWith("<") || ns.StartsWith("$")))

        let topLevelModules =
          types
          |> Array.filter (fun t -> 
            t.Namespace |> isNull && 
            (t.GetCustomAttributes(typeof<Microsoft.FSharp.Core.CompilationMappingAttribute>, false)
             |> Array.exists (fun attr ->
               let cma = attr :?> Microsoft.FSharp.Core.CompilationMappingAttribute
               cma.SourceConstructFlags = Microsoft.FSharp.Core.SourceConstructFlags.Module)) &&
            not (t.Name.StartsWith("<") || t.Name.StartsWith("$") || t.Name.Contains("@") || t.Name.Contains("+")))
          |> Array.map (fun t ->
            // F# compiler adds "Module" suffix when there's a name collision
            // (type abbreviations are erased in IL, so we can't detect all collisions).
            // Always strip it — F# source uses the unsuffixed name.
            if t.Name.EndsWith("Module") then
              t.Name.Substring(0, t.Name.Length - 6)
            else
              t.Name)
          |> Array.distinct

        for ns in rootNamespaces do
          if openedNamespaces.Add(ns) then
            namesToOpen.Add(ns)

        for m in topLevelModules do
          if openedNamespaces.Add(m) then
            namesToOpen.Add(m)
            moduleNames.Add(m) |> ignore
      with ex ->
        logger.LogDebug (sprintf "Could not analyze %s: %s" project.TargetPath ex.Message)
    reflectionAlc.Unload()
    // Phase 3: Open all collected names with iterative retry
    let opener name =
      let label = if moduleNames.Contains(name) then "module" else "namespace"
      logger.LogDebug (sprintf "Opening %s: %s" label name)
      let result, diagnostics = fsiSession.EvalInteractionNonThrowing(sprintf "open %s;;" name)
      match result with
      | Choice1Of2 _ ->
        if moduleNames.Contains(name) then
          logger.LogInfo (sprintf "✅ Opened module: %s" name)
        Ok ()
      | Choice2Of2 ex ->
        let allText = sprintf "%s %s" ex.Message (diagnostics |> Array.map (fun d -> d.Message) |> String.concat " ")
        if isBenignOpenError allText then
          logger.LogDebug (sprintf "⏭️ Skipped %s (RequireQualifiedAccess — types accessible via qualified paths)" name)
          Ok ()
        else
          Error ex.Message

    let totalNames = namesToOpen.Count
    logger.LogInfo (sprintf "Opening %d namespaces/modules (with dependency retry)..." totalNames)
    let succeeded, failed = openWithRetry 5 opener (Seq.toList namesToOpen)
    logger.LogInfo (sprintf "✅ Opened %d/%d namespaces/modules" (List.length succeeded) totalNames)
    if not (List.isEmpty failed) then
      logger.LogWarning (sprintf "⚠️  %d could not be opened (missing dependencies or type errors)" (List.length failed))
      for name, err in failed do
        let shortErr =
          if err.Contains("earlier error") then "cascade from earlier failure"
          elif err.Contains("not defined") then "not defined in scope"
          else
            let lines = err.Split('\n')
            if lines.Length > 0 then lines.[0].Trim() else err
        logger.LogDebug (sprintf "  ✗ %s — %s" name shortErr)

    // Restore core F# after warm-up opens. Libraries like FSharpPlus shadow
    // min/max with SRTP-generic versions and replace the async CE builder.
    fsiSession.EvalInteractionNonThrowing("open Microsoft.FSharp.Core.Operators;;") |> ignore
    fsiSession.EvalInteractionNonThrowing("open Microsoft.FSharp.Core.ExtraTopLevelOperators;;") |> ignore

    let warmupFailures =
      failed |> List.map (fun (name, err) -> { Name = name; Error = err })
    return fsiSession, recorder, args, warmupFailures
  }

let mkAppStateActor (logger: ILogger) (initCustomData: Map<string, obj>) outStream useAsp (originalSln: Solution) (shadowDir: string option) (onEvent: Events.SageFsEvent -> unit) (sln: Solution) =
  let diagnosticsChangedEvent = Event<Features.DiagnosticsStore.T>()
  let emit evt = try onEvent evt with _ -> ()

  // Query actor: serves all reads from an immutable snapshot.
  // No mutable state — receives snapshots via UpdateSnapshot message.
  let queryActor = MailboxProcessor<QueryCommand>.Start(fun inbox ->
    let rec loop (snapshot: QuerySnapshot) = async {
      let! cmd = inbox.Receive()
      match cmd with
      | UpdateSnapshot newSnapshot ->
        return! loop newSnapshot
      | QueryGetAppState reply ->
        reply.Reply snapshot.AppState
        return! loop snapshot
      | QueryGetSessionState reply ->
        reply.Reply snapshot.SessionState
        return! loop snapshot
      | QueryGetEvalStats reply ->
        reply.Reply snapshot.EvalStats
        return! loop snapshot
      | QueryGetStartupConfig reply ->
        reply.Reply snapshot.StartupConfig
        return! loop snapshot
      | QueryGetWarmupFailures reply ->
        reply.Reply snapshot.WarmupFailures
        return! loop snapshot
      | QueryAutocomplete(text, caret, word, reply) ->
        let res = AutoCompletion.getCompletions snapshot.AppState.Session text caret word
        reply.Reply res
        return! loop snapshot
      | QueryGetDiagnostics(text, reply) ->
        let res = Diagnostics.getDiagnostics snapshot.AppState.Session text
        reply.Reply res
        let newAppState = { snapshot.AppState with Diagnostics = Features.DiagnosticsStore.add text res snapshot.AppState.Diagnostics }
        diagnosticsChangedEvent.Trigger(newAppState.Diagnostics)
        emit (Events.DiagnosticsChecked {|
          Code = text
          Diagnostics = res |> Array.toList |> List.map Events.DiagnosticEvent.fromDiagnostic
          Source = Events.System
        |})
        return! loop { snapshot with AppState = newAppState }
      | QueryGetBoundValue(name, reply) ->
        snapshot.AppState.Session.GetBoundValues()
        |> List.tryFind (fun x -> x.Name = name)
        |>> (fun v -> v.Value.ReflectionValue)
        >>= Option.ofObj
        |> reply.Reply
        return! loop snapshot
      | QueryUpdateMcpPort port ->
        let updatedConfig =
          match snapshot.StartupConfig with
          | Some config -> Some { config with McpPort = port }
          | None -> None
        return! loop { snapshot with StartupConfig = updatedConfig }
    }
    let emptySnapshot = {
      AppState = Unchecked.defaultof<AppState>
      SessionState = SessionState.WarmingUp
      EvalStats = Affordances.EvalStats.empty
      StartupConfig = None
      WarmupFailures = []
    }
    loop emptySnapshot
  )

  let publishSnapshot st sessionState evalStats =
    queryActor.Post(UpdateSnapshot {
      AppState = st
      SessionState = sessionState
      EvalStats = evalStats
      StartupConfig = st.StartupConfig
      WarmupFailures = st.WarmupFailures
    })

  // Shared refs for cancellation + thread interruption.
  // Readable by both the eval actor (to set) and router actor (to cancel/interrupt).
  let currentEvalCts = ref Option<CancellationTokenSource>.None
  let currentEvalThread = ref Option<Thread>.None

  // Eval actor: owns AppState, serializes evals and session mutations.
  // Publishes immutable snapshots to query actor after each state change.
  let evalActor = MailboxProcessor<EvalCommand>.Start(fun mailbox ->
    let rec loop st middleware sessionState evalStats =
      async {
        let! cmd = mailbox.Receive()

        match cmd with
        | EvalEnableStdout ->
          st.OutStream.Enable()
          return! loop st middleware sessionState evalStats
        | EvalRun(request, cts, reply) ->
          let sessionState' = SessionState.Evaluating
          publishSnapshot st sessionState' evalStats
          let sw = System.Diagnostics.Stopwatch.StartNew()
          emit (Events.EvalRequested {| Code = request.Code; Source = Events.System |})
          let pipeline = buildPipeline (wrapErrorMiddleware :: middleware) (evalFn cts.Token)
          // Run eval on a dedicated thread so the actor stays responsive
          // to CancelEval, HardReset, etc. while the eval is in progress.
          let evalThread = Thread(fun () ->
            try
              let res, newSt = pipeline (request, st)
              mailbox.Post(EvalFinished(Ok(res, newSt), sw, request.Code, reply))
            with ex ->
              mailbox.Post(EvalFinished(Error ex, sw, request.Code, reply))
          )
          evalThread.IsBackground <- true
          evalThread.Name <- sprintf "sagefs-eval-%d" (evalStats.EvalCount + 1)
          currentEvalThread.Value <- Some evalThread
          evalThread.Start()
          return! loop st middleware sessionState' evalStats
        | EvalFinished(result, sw, code, reply) ->
          sw.Stop()
          currentEvalThread.Value <- None
          match result with
          | Ok(res, newSt) ->
            let sessionState'' = SessionState.Ready
            let evalStats' = Affordances.EvalStats.record sw.Elapsed evalStats
            publishSnapshot newSt sessionState'' evalStats'
            match res.EvaluationResult with
            | Ok result ->
              emit (Events.EvalCompleted {|
                Code = code
                Result = result
                TypeSignature = None
                Duration = sw.Elapsed
              |})
            | Error ex ->
              emit (Events.EvalFailed {|
                Code = code
                Error = ex.Message
                Diagnostics = res.Diagnostics |> Array.toList |> List.map Events.DiagnosticEvent.fromDiagnostic
              |})
            reply.Reply res
            return! loop newSt middleware sessionState'' evalStats'
          | Error ex ->
            let errResponse = {
              EvaluationResult = Error ex
              Diagnostics = [||]
              EvaluatedCode = code
              Metadata = Map.empty
            }
            let sessionState'' = SessionState.Ready
            publishSnapshot st sessionState'' evalStats
            emit (Events.EvalFailed {|
              Code = code
              Error = ex.Message
              Diagnostics = []
            |})
            reply.Reply errResponse
            return! loop st middleware sessionState'' evalStats
        | EvalAddMiddleware(additionalMiddleware, r) ->
          r.Reply(())
          return! loop st (additionalMiddleware @ middleware) sessionState evalStats
        | EvalReset reply ->
          try
            let sessionState' = SessionState.WarmingUp
            publishSnapshot st sessionState' evalStats
            logger.LogInfo "🔄 Resetting FSI session..."
            // Wait briefly for any in-flight eval thread to finish
            match currentEvalThread.Value with
            | Some thread ->
              if not (thread.Join(2000)) then
                logger.LogWarning "⚠️ Eval thread did not exit in time, proceeding with reset"
              currentEvalThread.Value <- None
            | None -> ()
            (st.Session :> System.IDisposable).Dispose()
            let! newSession, newRecorder, _, warmupFailures = createFsiSession logger outStream useAsp st.Solution
            let newSt = { st with Session = newSession; OutStream = newRecorder; Diagnostics = Features.DiagnosticsStore.empty; WarmupFailures = warmupFailures }
            logger.LogInfo "✅ FSI session reset complete"
            let sessionState'' = SessionState.Ready
            publishSnapshot newSt sessionState'' evalStats
            emit Events.SessionReset
            reply.Reply(Ok ())
            return! loop newSt middleware sessionState'' evalStats
          with ex ->
            logger.LogError $"❌ FSI session reset failed: {ex.Message}"
            let sessionState' = SessionState.Faulted
            publishSnapshot st sessionState' evalStats
            reply.Reply(Error (SageFsError.ResetFailed ex.Message))
            return! loop st middleware sessionState' evalStats
        | EvalHardReset (rebuild, reply) ->
          try
            let sessionState' = SessionState.WarmingUp
            publishSnapshot st sessionState' evalStats
            logger.LogInfo "🔨 Hard resetting FSI session..."
            // Wait briefly for any in-flight eval thread to finish
            match currentEvalThread.Value with
            | Some thread ->
              if not (thread.Join(2000)) then
                logger.LogWarning "⚠️ Eval thread did not exit in time, proceeding with hard reset"
              currentEvalThread.Value <- None
            | None -> ()

            (st.Session :> System.IDisposable).Dispose()
            // Force GC to unload the collectible AssemblyLoadContext and release DLL file locks
            // Required before dotnet build can overwrite assemblies on Windows
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()

            match st.ShadowDir with
            | Some dir -> ShadowCopy.cleanupShadowDir dir
            | None -> ()

            if rebuild then
              // Build only the primary project — dotnet build resolves dependencies transitively.
              // Building each project separately is redundant and slow for multi-project solutions.
              let primaryProject =
                st.OriginalSolution.Projects
                |> List.tryHead
                |> Option.map (fun p -> p.ProjectFileName)
              match primaryProject with
              | Some projFile ->
                logger.LogInfo (sprintf "  Building %s..." (System.IO.Path.GetFileName projFile))
                let runBuild () =
                  let psi =
                    System.Diagnostics.ProcessStartInfo(
                      "dotnet", sprintf "build \"%s\" --no-restore" projFile,
                      RedirectStandardOutput = true,
                      RedirectStandardError = true,
                      UseShellExecute = false)
                  use proc = System.Diagnostics.Process.Start(psi)
                  // Activity-based timeout: restart clock on each output line.
                  // Only kills truly hanging builds, not long-but-active ones.
                  let inactivityLimitMs = 30_000  // 30s with no output = stuck
                  let maxTotalMs = 600_000        // 10 min absolute max
                  let mutable lastActivity = DateTime.UtcNow
                  let startedAt = lastActivity
                  let stderrLines = System.Collections.Generic.List<string>()
                  // Stream stderr line-by-line, updating activity clock
                  let stderrTask = System.Threading.Tasks.Task.Run(fun () ->
                    let mutable line = proc.StandardError.ReadLine()
                    while not (isNull line) do
                      stderrLines.Add(line)
                      lastActivity <- DateTime.UtcNow
                      line <- proc.StandardError.ReadLine())
                  // Drain stdout, updating activity clock
                  let _stdoutTask = System.Threading.Tasks.Task.Run(fun () ->
                    let mutable line = proc.StandardOutput.ReadLine()
                    while not (isNull line) do
                      lastActivity <- DateTime.UtcNow
                      line <- proc.StandardOutput.ReadLine())
                  // Poll for completion or inactivity timeout
                  let mutable finished = false
                  let mutable timedOut = false
                  while not finished do
                    if proc.WaitForExit(1000) then
                      finished <- true
                    else
                      let now = DateTime.UtcNow
                      let totalMs = (now - startedAt).TotalMilliseconds
                      let inactiveMs = (now - lastActivity).TotalMilliseconds
                      if totalMs > float maxTotalMs then
                        logger.LogWarning (sprintf "  ⚠️ Build exceeded %d min limit" (maxTotalMs / 60_000))
                        timedOut <- true
                        finished <- true
                      elif inactiveMs > float inactivityLimitMs then
                        logger.LogWarning (sprintf "  ⚠️ Build inactive for %ds (no output)" (inactivityLimitMs / 1000))
                        timedOut <- true
                        finished <- true
                  if timedOut then
                    try proc.Kill(entireProcessTree = true) with _ -> ()
                    -1, sprintf "Build timed out (inactive for %ds or exceeded %d min limit)" (inactivityLimitMs / 1000) (maxTotalMs / 60_000)
                  else
                    try stderrTask.Wait(5000) |> ignore with _ -> ()
                    proc.ExitCode, String.concat "\n" stderrLines
                let exitCode, stderr = runBuild ()
                if exitCode <> 0 then
                  if stderr.Contains("denied") || stderr.Contains("locked") then
                    logger.LogWarning "  ⚠️ DLL lock detected, retrying after GC..."
                    GC.Collect()
                    GC.WaitForPendingFinalizers()
                    GC.Collect()
                    Thread.Sleep(500)
                    let retryCode, retryErr = runBuild ()
                    if retryCode <> 0 then
                      let msg = sprintf "Build failed on retry (exit code %d): %s" retryCode retryErr
                      logger.LogError (sprintf "  ❌ %s" msg)
                      let failedState = SessionState.Faulted
                      publishSnapshot st failedState evalStats
                      reply.Reply(Error (SageFsError.HardResetFailed msg))
                      return! loop st middleware failedState evalStats
                  else
                    let msg = sprintf "Build failed (exit code %d): %s" exitCode stderr
                    logger.LogError (sprintf "  ❌ %s" msg)
                    let failedState = SessionState.Faulted
                    publishSnapshot st failedState evalStats
                    reply.Reply(Error (SageFsError.HardResetFailed msg))
                    return! loop st middleware failedState evalStats
              | None ->
                logger.LogWarning "  ⚠️ No project to build"

            let newShadowDir = ShadowCopy.createShadowDir ()
            logger.LogInfo "  Creating shadow copies..."
            let newSln = ShadowCopy.shadowCopySolution newShadowDir st.OriginalSolution

            let! newSession, newRecorder, _, warmupFailures = createFsiSession logger outStream useAsp newSln
            let newSt =
              { st with
                  Session = newSession
                  OutStream = newRecorder
                  Solution = newSln
                  ShadowDir = Some newShadowDir
                  Diagnostics = Features.DiagnosticsStore.empty
                  WarmupFailures = warmupFailures }
            logger.LogInfo "✅ Hard reset complete"
            let sessionState'' = SessionState.Ready
            publishSnapshot newSt sessionState'' evalStats
            emit (Events.SessionHardReset {| Rebuild = rebuild |})
            reply.Reply(Ok "Hard reset complete. Fresh session with re-copied assemblies.")
            return! loop newSt middleware sessionState'' evalStats
          with ex ->
            logger.LogError (sprintf "❌ Hard reset failed: %s" ex.Message)
            let sessionState' = SessionState.Faulted
            publishSnapshot st sessionState' evalStats
            reply.Reply(Error (SageFsError.HardResetFailed ex.Message))
            return! loop st middleware sessionState' evalStats
      }

    and init () =
      async {
        logger.LogInfo "Welcome to SageFs!"
        emit (Events.SessionStarted {|
          Config = Map.ofList [
            "projects", (sln.Projects |> List.map (fun p -> p.ProjectFileName) |> String.concat ";")
          ]
          StartedAt = DateTimeOffset.UtcNow
        |})

        if not (List.isEmpty sln.Projects) then
          logger.LogInfo "Loading these projects: "
          for project in sln.Projects do
            logger.LogInfo project.ProjectFileName

        match sln.Projects |> List.tryHead with
        | Some primaryProject ->
          let projectDir = System.IO.Path.GetDirectoryName(primaryProject.ProjectFileName)
          logger.LogInfo $"Setting working directory to: {projectDir}"
          System.Environment.CurrentDirectory <- projectDir
        | None -> ()

        let! fsiSession, recorder, args, warmupFailures = createFsiSession logger outStream useAsp sln
        
        // Evaluate startup profile if found
        let startupProfileResult =
          let workingDir = System.Environment.CurrentDirectory
          match StartupProfile.discoverInitScript workingDir with
          | None -> None
          | Some scriptPath ->
            let evalFn code =
              fsiSession.EvalInteraction(code, CancellationToken.None)
            let logFn msg = logger.LogInfo msg
            match StartupProfile.evalInitScript evalFn logFn scriptPath with
            | Result.Ok path -> Some path
            | Result.Error msg ->
              logger.LogWarning msg
              None
        
        emit Events.SessionReady
        let sessionState = SessionState.Ready

        let st = {
          Solution = sln
          OriginalSolution = originalSln
          ShadowDir = shadowDir
          Session = fsiSession
          Logger = logger
          OutStream = recorder
          Custom = initCustomData
          Diagnostics = Features.DiagnosticsStore.empty
          WarmupFailures = warmupFailures
          StartupConfig = Some {
            CommandLineArgs = args
            LoadedProjects = sln.Projects |> List.map (fun p -> p.ProjectFileName)
            WorkingDirectory = System.Environment.CurrentDirectory
            McpPort = 0
            HotReloadEnabled = true
            AspireDetected = useAsp
            StartupTimestamp = DateTime.UtcNow
            StartupProfileLoaded = startupProfileResult
          }
        }

        let evalStats = Affordances.EvalStats.empty
        publishSnapshot st sessionState evalStats
        return! loop st [] sessionState evalStats
      }

    init ()
  )

  // Router actor: dispatches instantly, never blocks.
  // Query commands go to queryActor, eval commands go to evalActor.
  let actor = MailboxProcessor.Start(fun mailbox ->
    let rec loop () =
      async {
        let! cmd = mailbox.Receive()
        match cmd with
        // Query commands — forward to query actor (responds even during eval)
        | GetAppState reply ->
          queryActor.Post(QueryGetAppState reply)
        | GetSessionState reply ->
          queryActor.Post(QueryGetSessionState reply)
        | GetStartupConfig reply ->
          queryActor.Post(QueryGetStartupConfig reply)
        | GetWarmupFailures reply ->
          queryActor.Post(QueryGetWarmupFailures reply)
        | Autocomplete(text, caret, word, reply) ->
          queryActor.Post(QueryAutocomplete(text, caret, word, reply))
        | GetDiagnostics(text, reply) ->
          queryActor.Post(QueryGetDiagnostics(text, reply))
        | GetBoundValue(name, reply) ->
          queryActor.Post(QueryGetBoundValue(name, reply))
        | UpdateMcpPort port ->
          queryActor.Post(QueryUpdateMcpPort port)

        // Cancel — cooperative via CTS + thread interrupt for blocked evals
        | CancelEval reply ->
          let cancelled =
            match currentEvalCts.Value with
            | Some cts ->
              try
                cts.Cancel()
                // Also interrupt the eval thread in case it's blocked
                // on I/O (ReadLine, pipe read, etc.) where tokens aren't checked
                match currentEvalThread.Value with
                | Some thread ->
                  try thread.Interrupt() with _ -> ()
                | None -> ()
                true
              with _ -> false
            | None -> false
          reply.Reply cancelled

        // Eval commands — forward to eval actor (serialized)
        | Eval(request, token, reply) ->
          let cts = CancellationTokenSource.CreateLinkedTokenSource(token)
          currentEvalCts.Value <- Some cts
          evalActor.Post(EvalRun(request, cts, reply))
        | AddMiddleware(mw, reply) ->
          evalActor.Post(EvalAddMiddleware(mw, reply))
        | EnableStdout ->
          evalActor.Post(EvalEnableStdout)
        | ResetSession reply ->
          // Cancel any running eval before resetting
          match currentEvalCts.Value with
          | Some cts -> try cts.Cancel() with _ -> ()
          | None -> ()
          match currentEvalThread.Value with
          | Some thread -> try thread.Interrupt() with _ -> ()
          | None -> ()
          evalActor.Post(EvalReset reply)
        | HardResetSession(rebuild, reply) ->
          // Cancel any running eval before hard resetting
          match currentEvalCts.Value with
          | Some cts -> try cts.Cancel() with _ -> ()
          | None -> ()
          match currentEvalThread.Value with
          | Some thread -> try thread.Interrupt() with _ -> ()
          | None -> ()
          evalActor.Post(EvalHardReset(rebuild, reply))

        return! loop ()
      }
    loop ()
  )

  // Bypass closures read from query actor — no mutable state
  let getSessionState () =
    queryActor.PostAndAsyncReply(fun reply -> QueryGetSessionState reply)
    |> Async.RunSynchronously
  let getEvalStats () =
    queryActor.PostAndAsyncReply(fun reply -> QueryGetEvalStats reply)
    |> Async.RunSynchronously
  let getWarmupFailures () =
    queryActor.PostAndAsyncReply(fun reply -> QueryGetWarmupFailures reply)
    |> Async.RunSynchronously
  let getStartupConfig () =
    queryActor.PostAndAsyncReply(fun reply -> QueryGetStartupConfig reply)
    |> Async.RunSynchronously
  let cancelCurrentEval () =
    actor.PostAndAsyncReply(fun reply -> CancelEval reply)
    |> Async.RunSynchronously

  actor, diagnosticsChangedEvent.Publish, cancelCurrentEval, getSessionState, getEvalStats, getWarmupFailures, getStartupConfig