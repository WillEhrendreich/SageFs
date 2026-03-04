module SageFs.AppState

open System
open System.IO

open System.Threading
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open System
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

    match value with
    | '\n' ->
      match lastCharWasCR with
      | false -> writerToRecord.Write '\r'
      | true -> ()
      writerToRecord.Write '\n'
      lastCharWasCR <- false
    | _ ->
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

/// A warm-up failure — alias for the rich WarmupOpenFailure type.
type WarmupFailure = WarmupOpenFailure

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
  WarmupContext: WarmupContext
  HotReloadState: HotReloadState.T
}

type EvalResponse = {
  EvaluationResult: Result<string, Exception>
  Diagnostics: Diagnostics.Diagnostic array
  EvaluatedCode: string
  Metadata: Map<string, objnull>
}

type EvalRequest = { Code: string; Args: Map<string, obj> }

/// Whether the active session is idle or currently evaluating code.
/// Only meaningful when the session is Active — not a top-level lifecycle state.
type SessionActivity = Idle | Evaluating

/// Rich session lifecycle phase — the source of truth for QuerySnapshot.
/// Carries domain data only in states where it's meaningful, making
/// impossible states (e.g., "Faulted with a valid AppState") unrepresentable.
/// Replaces the old (AppState option × SessionState) pair which could desync.
type SessionPhase =
  | Initializing of statusMessage: string option
  | Active of AppState * SessionActivity
  | Faulted

module SessionPhase =
  /// Derive the legacy SessionState for external consumers (MCP, dashboard, etc.)
  let toSessionState = function
    | Initializing _ -> SessionState.WarmingUp
    | Active (_, Idle) -> SessionState.Ready
    | Active (_, Evaluating) -> SessionState.Evaluating
    | Faulted -> SessionState.Faulted

  /// Extract the AppState when active, None otherwise.
  /// Narrow convenience for callers that genuinely don't need phase distinction.
  let tryAppState = function
    | Active (st, _) -> Some st
    | Initializing _ | Faulted -> None

type MiddlewareNext = EvalRequest * AppState -> EvalResponse * AppState
type Middleware = MiddlewareNext -> EvalRequest * AppState -> EvalResponse * AppState

type Command =
  | Eval of EvalRequest * CancellationToken * AsyncReplyChannel<EvalResponse>
  | CancelEval of AsyncReplyChannel<bool>
  | Autocomplete of text: string * caret: int * word: string * AsyncReplyChannel<list<AutoCompletion.CompletionItem>>
  | GetBoundValue of name: string * AsyncReplyChannel<obj Option>
  | AddMiddleware of Middleware list * AsyncReplyChannel<unit>
  | GetDiagnostics of text: string * AsyncReplyChannel<Diagnostics.Diagnostic array>
  | GetTypeCheckWithSymbols of text: string * filePath: string * AsyncReplyChannel<Diagnostics.TypeCheckWithSymbolsResult>
  | GetSessionPhase of AsyncReplyChannel<SessionPhase>
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
/// All fields are derivable from Phase; EvalStats is kept separate
/// because it's always meaningful (even as empty during Initializing).
type QuerySnapshot = {
  Phase: SessionPhase
  EvalStats: Affordances.EvalStats
}

/// Internal command for the query actor
type internal QueryCommand =
  | UpdateSnapshot of QuerySnapshot
  | QueryGetSessionPhase of AsyncReplyChannel<SessionPhase>
  | QueryGetSessionState of AsyncReplyChannel<SessionState>
  | QueryGetEvalStats of AsyncReplyChannel<Affordances.EvalStats>
  | QueryGetStartupConfig of AsyncReplyChannel<StartupConfig option>
  | QueryGetWarmupFailures of AsyncReplyChannel<WarmupFailure list>
  | QueryGetWarmupContext of AsyncReplyChannel<WarmupContext>
  | QueryGetStatusMessage of AsyncReplyChannel<string option>
  | QueryAutocomplete of text: string * caret: int * word: string * AsyncReplyChannel<list<AutoCompletion.CompletionItem>>
  | QueryGetDiagnostics of text: string * AsyncReplyChannel<Diagnostics.Diagnostic array>
  | QueryGetTypeCheckWithSymbols of text: string * filePath: string * AsyncReplyChannel<Diagnostics.TypeCheckWithSymbolsResult>
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

open System.Text.RegularExpressions

// Pre-compiled regex patterns for cleanStdout (avoids recompilation per call)
let reAnsiCursorReset = Regex(@"\x1b\[\d+D", RegexOptions.Compiled)
let reAnsiCursorVis = Regex(@"\x1b\[\?25[hl]", RegexOptions.Compiled)
let reAnsiEscape = Regex(@"\x1b\[[0-9;]*[a-zA-Z]|\x1b\].*?\x07", RegexOptions.Compiled)
let reProgressBar = Regex(@"^\d+/\d+\s*\|", RegexOptions.Compiled)
let reExpectoTimestamp = Regex(@"^\[\d{2}:\d{2}:\d{2}\s+\w{3}\]\s*", RegexOptions.Compiled)
let reExpectoSuffix = Regex(@"\s*<Expecto>\s*$", RegexOptions.Compiled)
let reExpectoSummary = Regex(@"EXPECTO!\s+(\d+)\s+tests?\s+run\s+in\s+(\S+)\s+for\s+(.+?)\s+.\s+(\d+)\s+passed,\s+(\d+)\s+ignored,\s+(\d+)\s+failed,\s+(\d+)\s+errored\.\s+(\S+!?)", RegexOptions.Compiled)

/// Strip ANSI escape sequences and terminal control codes from a string.
/// Cursor-reset sequences (move to column 0) become newlines to preserve logical line breaks.
let stripAnsi (s: string) =
  let s = reAnsiCursorReset.Replace(s, "\n")
  let s = reAnsiCursorVis.Replace(s, "")
  reAnsiEscape.Replace(s, "")

/// Reformat Expecto summary line into readable multi-line output.
let reformatExpectoSummary (line: string) =
  let m = reExpectoSummary.Match(line)
  match m.Success with
  | true ->
    sprintf "%s: %s tests in %s\n  %s passed\n  %s ignored\n  %s failed\n  %s errored\n  %s"
      m.Groups.[3].Value m.Groups.[1].Value m.Groups.[2].Value
      m.Groups.[4].Value m.Groups.[5].Value
      m.Groups.[6].Value m.Groups.[7].Value m.Groups.[8].Value
  | false -> line

/// Clean captured stdout: strip ANSI, remove progress noise, reformat Expecto.
/// Uses pre-compiled regex and single-pass line processing for 1.7× speedup.
let cleanStdout (raw: string) =
  let sb = StringBuilder(raw.Length)
  let s = raw |> stripAnsi
  let mutable first = true
  for line in s.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
    let l = line.Trim()
    match l.Length > 0
          && not (l.StartsWith("Expecto Running", System.StringComparison.Ordinal))
          && not (reProgressBar.IsMatch(l)) with
    | true ->
      let l = reExpectoTimestamp.Replace(l, "")
      let l = reExpectoSuffix.Replace(l, "")
      let l = l.Trim()
      match l.Length > 0 with
      | true ->
        let l =
          match l.Contains "EXPECTO!" with
          | true -> reformatExpectoSummary l
          | false -> l
        match first with
        | false -> sb.Append('\n') |> ignore
        | true -> ()
        sb.Append(l) |> ignore
        first <- false
      | false -> ()
    | false -> ()
  sb.ToString()

let evalFn (token: CancellationToken) =
  fun ({ Code = code }, st) ->
    // Capture Console.Out separately so we can reorder: val bindings first, stdout last
    let originalOut = Console.Out
    let stdoutCapture = new StringWriter()
    Console.SetOut(stdoutCapture)
    st.OutStream.StartRecording()
    let thread = Thread.CurrentThread
    token.Register(fun () -> thread.Interrupt()) |> ignore
    let evalRes, diagnostics = st.Session.EvalInteractionNonThrowing(code, token)
    let diagnostics = diagnostics |> Array.map Diagnostics.Diagnostic.mkDiagnostic

    let evalRes =
      match evalRes with
      | Choice1Of2 _ ->
        let fsiOutput = st.OutStream.StopRecording()
        let stdout = stdoutCapture.ToString() |> cleanStdout
        let combined =
          match String.IsNullOrWhiteSpace stdout with
          | true -> fsiOutput
          | false -> sprintf "%s\n%s" fsiOutput stdout
        Ok combined
      | Choice2Of2 ex -> Error <| ex

    st.OutStream.StopRecording() |> ignore
    Console.SetOut(originalOut)

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
/// The CancellationToken is passed through to FSI EvalInteraction calls so that
/// warm-up can be cancelled if it takes too long (e.g. a stuck module initializer).
let createFsiSession (logger: ILogger) (outStream: TextWriter) (useAsp: bool) (sln: Solution) (ct: CancellationToken) (onProgress: (int * int * string) -> unit) =
  async {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()
    let args = solutionToFsiArgs logger useAsp sln
    let recorder = new TextWriterRecorder(outStream)

    logger.LogInfo (sprintf "  Creating FSI session with %d args..." (Array.length args))
    let fsiErrorWriter = new System.IO.StringWriter()
    let fsiSession =
      try
        FsiEvaluationSession.Create(fsiConfig, args, new StreamReader(Stream.Null), recorder, fsiErrorWriter, collectible = true)
      with ex ->
        let fsiErrors = fsiErrorWriter.ToString()
        match fsiErrors.Length > 0 with
        | true -> logger.LogError (sprintf "  FSI stderr: %s" fsiErrors)
        | false -> ()
        logger.LogError (sprintf "  ❌ FsiEvaluationSession.Create failed: %s" ex.Message)
        match isNull ex.InnerException with
        | false -> logger.LogError (sprintf "    Inner: %s" ex.InnerException.Message)
        | true -> ()
        raise ex
    let fsiInitErrors = fsiErrorWriter.ToString()
    match fsiInitErrors.Length > 0 with
    | true -> logger.LogWarning (sprintf "  FSI init warnings: %s" fsiInitErrors)
    | false -> ()
    logger.LogInfo (sprintf "  FSI session created in %dms, loading startup files..." sw.ElapsedMilliseconds)
    onProgress(1, 4, "FSI session created")

    for fileName in sln.StartupFiles do
      ct.ThrowIfCancellationRequested()
      logger.LogInfo $"Loading %s{fileName}"
      let! fileContents = File.ReadAllTextAsync fileName |> Async.AwaitTask
      let compatibleContents = FsiRewrite.rewriteInlineUseStatements fileContents
      match compatibleContents <> fileContents with
      | true ->
        logger.LogInfo $"⚡ Applied FSI compatibility transforms to {fileName}"
        let beforeCount = (fileContents.Split('\n') |> Array.filter (fun line -> line.TrimStart().StartsWith("use ", System.StringComparison.Ordinal))).Length
        let afterCount = (compatibleContents.Split('\n') |> Array.filter (fun line -> line.TrimStart().StartsWith("use ", System.StringComparison.Ordinal))).Length  
        logger.LogInfo $"   Rewrote {beforeCount - afterCount} 'use' statements to 'let'"
      | false -> ()
      try
        fsiSession.EvalInteraction(compatibleContents, ct)
      with ex ->
        logger.LogError (sprintf "  ❌ Startup file %s failed: %s" fileName ex.Message)
        raise ex

    let openedNamespaces = System.Collections.Generic.HashSet<string>()
    let namesToOpen = System.Collections.Generic.List<string>()
    let moduleNames = System.Collections.Generic.HashSet<string>()
    let loadedAssemblies = System.Collections.Generic.List<LoadedAssembly>()

    // Phase 1: Collect namespaces from source files
    let allFsFiles =
      sln.FsProjects
      |> Seq.collect (fun proj -> proj.SourceFiles)
      |> Seq.filter (fun f -> f.EndsWith(".fs", System.StringComparison.Ordinal) || f.EndsWith(".fsx", System.StringComparison.Ordinal))
      |> Seq.distinct

    let mutable fileCount = 0
    for fsFile in allFsFiles do
      ct.ThrowIfCancellationRequested()
      try
        match File.Exists(fsFile) with
        | true ->
          let! sourceLines = File.ReadAllLinesAsync fsFile |> Async.AwaitTask
          fileCount <- fileCount + 1
          for line in sourceLines do
            let trimmed = line.Trim()
            match trimmed.StartsWith("open ", System.StringComparison.Ordinal) && not (trimmed.StartsWith("//", System.StringComparison.Ordinal)) with
            | true ->
              let parts = trimmed.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
              match parts.Length >= 2 with
              | true ->
                let nsName = parts.[1].TrimEnd(';')
                match openedNamespaces.Add(nsName) with
                | true ->
                  namesToOpen.Add(nsName)
                | false -> ()
              | false -> ()
            | false -> ()
        | false -> ()
      with ex ->
        logger.LogDebug (sprintf "Could not parse opens from %s: %s" fsFile ex.Message)
    logger.LogInfo (sprintf "  Scanned %d source files for opens in %dms" fileCount sw.ElapsedMilliseconds)
    let scanPhaseMs = sw.ElapsedMilliseconds
    onProgress(2, 4, sprintf "Scanned %d source files" fileCount)

    // Phase 2: Collect namespaces/modules via reflection
    logger.LogInfo "  Scanning assemblies for namespaces..."
    // Use a collectible AssemblyLoadContext to avoid the default context's identity cache.
    // Assembly.LoadFrom caches by identity — after hard reset + rebuild, it returns the
    // OLD assembly even though the shadow-copied DLL on disk has new types.
    let reflectionAlc =
      new System.Runtime.Loader.AssemblyLoadContext(
        "sagefs-reflection", isCollectible = true)
    for project in sln.Projects do
      ct.ThrowIfCancellationRequested()
      try
        let asm = reflectionAlc.LoadFromAssemblyPath(project.TargetPath)
        let types =
          try
            asm.GetTypes()
          with
          | :? System.Reflection.ReflectionTypeLoadException as ex ->
            ex.Types |> Array.filter (fun t -> not (isNull t))

        let rootNamespaces =
          types
          |> Array.choose (fun t ->
            match isNull t.Namespace with
            | false ->
              let parts = t.Namespace.Split('.')
              match parts.Length > 0 with | true -> Some parts.[0] | false -> None
            | true ->
              None)
          |> Array.distinct
          |> Array.filter (fun ns -> not (ns.StartsWith("<", System.StringComparison.Ordinal) || ns.StartsWith("$", System.StringComparison.Ordinal)))

        let topLevelModules =
          types
          |> Array.filter (fun t -> 
            t.Namespace |> isNull && 
            (t.GetCustomAttributes(typeof<Microsoft.FSharp.Core.CompilationMappingAttribute>, false)
             |> Array.exists (fun attr ->
               let cma = attr :?> Microsoft.FSharp.Core.CompilationMappingAttribute
               cma.SourceConstructFlags = Microsoft.FSharp.Core.SourceConstructFlags.Module)) &&
            not (t.Name.StartsWith("<", System.StringComparison.Ordinal) || t.Name.StartsWith("$", System.StringComparison.Ordinal) || t.Name.Contains("@") || t.Name.Contains("+")))
          |> Array.map (fun t ->
            match t.Name.EndsWith("Module", System.StringComparison.Ordinal) with
            | true -> t.Name.Substring(0, t.Name.Length - 6)
            | false -> t.Name)
          |> Array.distinct

        for ns in rootNamespaces do
          match openedNamespaces.Add(ns) with
          | true -> namesToOpen.Add(ns)
          | false -> ()

        for m in topLevelModules do
          match openedNamespaces.Add(m) with
          | true ->
            namesToOpen.Add(m)
            moduleNames.Add(m) |> ignore
          | false -> ()

        loadedAssemblies.Add({
          Name = asm.GetName().Name
          Path = project.TargetPath
          NamespaceCount = rootNamespaces.Length
          ModuleCount = topLevelModules.Length
        } : LoadedAssembly)
      with ex ->
        logger.LogDebug (sprintf "Could not analyze %s: %s" project.TargetPath ex.Message)
    reflectionAlc.Unload()
    logger.LogInfo (sprintf "  Assembly scan complete in %dms" sw.ElapsedMilliseconds)
    let assemblyPhaseMs = sw.ElapsedMilliseconds
    let totalNames = namesToOpen.Count
    onProgress(3, 4, sprintf "Scanned assemblies, opening %d namespaces" totalNames)
    // Phase 3: Open all collected names with rich diagnostics via iterative retry
    let mutable openCount = 0
    let opener name isMod =
      ct.ThrowIfCancellationRequested()
      let label = match isMod with | true -> "module" | false -> "namespace"
      logger.LogDebug (sprintf "Opening %s: %s" label name)
      let openSw = System.Diagnostics.Stopwatch.StartNew()
      let result, diagnostics = fsiSession.EvalInteractionNonThrowing(sprintf "open %s;;" name, ct)
      let elapsed = openSw.Elapsed.TotalMilliseconds
      openCount <- openCount + 1
      match result with
      | Choice1Of2 _ ->
        onProgress(openCount, totalNames, sprintf "✅ open %s (%.0fms)" name elapsed)
        match isMod with
        | true -> logger.LogInfo (sprintf "✅ Opened module: %s (%.1fms)" name elapsed)
        | false -> ()
        WarmUp.OpenSuccess elapsed
      | Choice2Of2 ex ->
        let allText = sprintf "%s %s" ex.Message (diagnostics |> Array.map (fun d -> d.Message) |> String.concat " ")
        match isBenignOpenError allText with
        | true ->
          onProgress(openCount, totalNames, sprintf "⏭️ open %s (skipped, %.0fms)" name elapsed)
          logger.LogDebug (sprintf "⏭️ Skipped %s (RequireQualifiedAccess — types accessible via qualified paths)" name)
          WarmUp.OpenSuccess elapsed
        | false ->
          onProgress(openCount, totalNames, sprintf "✖ open %s — failed (%.0fms)" name elapsed)
          let fcsDiags =
            diagnostics
            |> Array.map (fun d ->
              { Message = d.Message
                Severity =
                  match d.Severity with
                  | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error -> "error"
                  | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning -> "warning"
                  | _ -> "info"
                ErrorNumber = d.ErrorNumber
                FileName = match d.FileName with | null | "" -> None | f -> Some f
                StartLine = d.StartLine
                EndLine = d.EndLine
                StartColumn = d.StartColumn
                EndColumn = d.EndColumn })
            |> Array.toList
          WarmUp.OpenFailed (ex.Message, fcsDiags, elapsed)

    let namePairs =
      namesToOpen
      |> Seq.map (fun name -> name, moduleNames.Contains(name))
      |> Seq.toList
    logger.LogInfo (sprintf "Opening %d namespaces/modules (with dependency retry)..." totalNames)
    let succeeded, failed = WarmUp.openWithRetryRich 5 opener namePairs
    let openPhaseMs = sw.ElapsedMilliseconds
    logger.LogInfo (sprintf "✅ Opened %d/%d namespaces/modules in %dms" (List.length succeeded) totalNames sw.ElapsedMilliseconds)
    match List.isEmpty failed with
    | false ->
      logger.LogWarning (sprintf "⚠️  %d could not be opened:" (List.length failed))
      for f in failed do
        let kind = match f.IsModule with | true -> "module" | false -> "namespace"
        logger.LogWarning (sprintf "  ✗ %s (%s): %s" f.Name kind f.ErrorMessage)
        for d in f.Diagnostics do
          let loc =
            match d.FileName with
            | Some fn -> sprintf "%s:%d:%d" fn d.StartLine d.StartColumn
            | None -> "unknown location"
          logger.LogWarning (sprintf "    FS%04d %s — %s" d.ErrorNumber loc d.Message)
    | true -> ()

    // Restore core F# after warm-up opens. User project libraries like FSharpPlus shadow
    // min/max with SRTP-generic versions and replace the async CE builder.
    fsiSession.EvalInteractionNonThrowing("open Microsoft.FSharp.Core.Operators;;", ct) |> ignore
    fsiSession.EvalInteractionNonThrowing("open Microsoft.FSharp.Core.ExtraTopLevelOperators;;", ct) |> ignore

    let warmupCtx = {
      SourceFilesScanned = fileCount
      AssembliesLoaded = Seq.toList loadedAssemblies
      NamespacesOpened = succeeded
      FailedOpens = failed
      PhaseTiming = {
        ScanSourceFilesMs = scanPhaseMs
        ScanAssembliesMs = assemblyPhaseMs - scanPhaseMs
        OpenNamespacesMs = openPhaseMs - assemblyPhaseMs
        TotalMs = sw.ElapsedMilliseconds
      }
      StartedAt = System.DateTimeOffset.UtcNow
    }

    logger.LogInfo (sprintf "  Warm-up complete in %dms (scan=%dms, asm=%dms, open=%dms)"
      sw.ElapsedMilliseconds scanPhaseMs (assemblyPhaseMs - scanPhaseMs) (openPhaseMs - assemblyPhaseMs))
    onProgress(4, 4, sprintf "Warm-up complete in %dms" sw.ElapsedMilliseconds)
    return fsiSession, recorder, args, failed, warmupCtx
  }

let mkAppStateActor (logger: ILogger) (initCustomData: Map<string, obj>) outStream useAsp (originalSln: Solution) (shadowDir: string option) (onEvent: Events.SageFsEvent -> unit) (sln: Solution) =
  let diagnosticsChangedEvent = Event<Features.DiagnosticsStore.T>()
  let emit evt = try onEvent evt with ex -> logger.LogWarning (sprintf "Event emission failed: %s" ex.Message)

  // Query actor: serves all reads from an immutable snapshot.
  // No mutable state — receives snapshots via UpdateSnapshot message.
  // Wrapped with ResilientActor.wrapLoop so unhandled exceptions in
  // diagnostics/completions don't silently kill the query actor.
  let queryActor = MailboxProcessor<QueryCommand>.Start(fun inbox ->
    let processQuery (snapshot: QuerySnapshot) (cmd: QueryCommand) =
      async {
        match cmd with
        | UpdateSnapshot newSnapshot ->
          return newSnapshot
        | QueryGetSessionPhase reply ->
          reply.Reply snapshot.Phase
          return snapshot
        | QueryGetSessionState reply ->
          reply.Reply (SessionPhase.toSessionState snapshot.Phase)
          return snapshot
        | QueryGetEvalStats reply ->
          reply.Reply snapshot.EvalStats
          return snapshot
        | QueryGetStartupConfig reply ->
          let config =
            match snapshot.Phase with
            | Active (st, _) -> st.StartupConfig
            | _ -> None
          reply.Reply config
          return snapshot
        | QueryGetWarmupFailures reply ->
          let failures =
            match snapshot.Phase with
            | Active (st, _) -> st.WarmupFailures
            | _ -> []
          reply.Reply failures
          return snapshot
        | QueryGetWarmupContext reply ->
          let ctx =
            match snapshot.Phase with
            | Active (st, _) -> st.WarmupContext
            | _ -> WarmupContext.empty
          reply.Reply ctx
          return snapshot
        | QueryGetStatusMessage reply ->
          let msg =
            match snapshot.Phase with
            | Initializing msg -> msg
            | _ -> None
          reply.Reply msg
          return snapshot
        | QueryAutocomplete(text, caret, word, reply) ->
          match snapshot.Phase with
          | Active (st, _) ->
            let res = AutoCompletion.getCompletions st.Session text caret word
            reply.Reply res
            return snapshot
          | _ ->
            reply.Reply []
            return snapshot
        | QueryGetDiagnostics(text, reply) ->
          match snapshot.Phase with
          | Active (st, activity) ->
            let res = Diagnostics.getDiagnostics st.Session text
            reply.Reply res
            let newSt = { st with Diagnostics = Features.DiagnosticsStore.add text res st.Diagnostics }
            diagnosticsChangedEvent.Trigger(newSt.Diagnostics)
            emit (Events.DiagnosticsChecked {|
              Code = text
              Diagnostics = res |> Array.toList |> List.map Events.DiagnosticEvent.fromDiagnostic
              Source = Events.System
            |})
            return { snapshot with Phase = Active (newSt, activity) }
          | _ ->
            reply.Reply [||]
            return snapshot
        | QueryGetTypeCheckWithSymbols(text, filePath, reply) ->
          match snapshot.Phase with
          | Active (st, _) ->
            let res = Diagnostics.getTypeCheckWithSymbols st.Session filePath text
            reply.Reply res
            return snapshot
          | _ ->
            reply.Reply { Diagnostics.TypeCheckWithSymbolsResult.Diagnostics = [||]; HasErrors = false; SymbolRefs = [] }
            return snapshot
        | QueryGetBoundValue(name, reply) ->
          match snapshot.Phase with
          | Active (st, _) ->
            st.Session.GetBoundValues()
            |> List.tryFind (fun x -> x.Name = name)
            |> Option.map (fun v -> v.Value.ReflectionValue)
            |> Option.bind Option.ofObj
            |> reply.Reply
            return snapshot
          | _ ->
            reply.Reply None
            return snapshot
        | QueryUpdateMcpPort port ->
          match snapshot.Phase with
          | Active (st, activity) ->
            let updatedConfig =
              match st.StartupConfig with
              | Some config -> Some { config with McpPort = port }
              | None -> None
            return { snapshot with Phase = Active ({ st with StartupConfig = updatedConfig }, activity) }
          | _ -> return snapshot
      }
    let safeProcessQuery = ResilientActor.wrapLoop logger "query-actor" processQuery
    let rec loop (snapshot: QuerySnapshot) = async {
      let! cmd = inbox.Receive()
      let! snapshot' = safeProcessQuery snapshot cmd
      return! loop snapshot'
    }
    let emptySnapshot = {
      Phase = Initializing None
      EvalStats = Affordances.EvalStats.empty
    }
    loop emptySnapshot
  )

  // CQRS snapshot: volatile ref for lock-free reads of query state.
  // Writers: publishSnapshot (called by main actor on every state change).
  // Readers: getSessionState, getEvalStats, etc. — zero mailbox round-trip.
  let mutable latestSnapshot : QuerySnapshot = {
    Phase = Initializing None
    EvalStats = Affordances.EvalStats.empty
  }

  let publishSnapshot st activity evalStats =
    let snap = {
      Phase = Active (st, activity)
      EvalStats = evalStats
    }
    System.Threading.Volatile.Write(&latestSnapshot, snap)
    queryActor.Post(UpdateSnapshot snap)

  let publishPhase phase evalStats =
    let snap = {
      Phase = phase
      EvalStats = evalStats
    }
    System.Threading.Volatile.Write(&latestSnapshot, snap)
    queryActor.Post(UpdateSnapshot snap)

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
          publishSnapshot st Evaluating evalStats
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
            publishSnapshot newSt Idle evalStats'
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
            publishSnapshot st Idle evalStats
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
            publishPhase (Initializing None) evalStats
            logger.LogInfo "🔄 Resetting FSI session..."
            // Wait briefly for any in-flight eval thread to finish
            match currentEvalThread.Value with
            | Some thread ->
              match thread.Join(2000) with
              | false -> logger.LogWarning "⚠️ Eval thread did not exit in time, proceeding with reset"
              | true -> ()
              currentEvalThread.Value <- None
            | None -> ()
            match isNull (box st.Session) with
            | false -> (st.Session :> System.IDisposable).Dispose()
            | true -> ()
            let softResetCts = new CancellationTokenSource(Timeouts.softResetCancellation)
            let onProgress (s,t,msg) =
              emit (Events.SageFsEvent.SessionWarmUpProgress {| Step = s; Total = t; Message = msg |})
              publishPhase (Initializing (Some (sprintf "[%d/%d] %s" s t msg))) evalStats
            let! newSession, newRecorder, _, warmupFailures, warmupCtx = createFsiSession logger outStream useAsp st.Solution softResetCts.Token onProgress
            softResetCts.Dispose()
            let newSt = { st with Session = newSession; OutStream = newRecorder; Diagnostics = Features.DiagnosticsStore.empty; WarmupFailures = warmupFailures; WarmupContext = warmupCtx }
            logger.LogInfo "✅ FSI session reset complete"
            let sessionState'' = SessionState.Ready
            publishSnapshot newSt Idle evalStats
            emit Events.SessionReset
            reply.Reply(Ok ())
            return! loop newSt middleware sessionState'' evalStats
          with ex ->
            logger.LogError $"❌ FSI session reset failed: {ex.Message}"
            let sessionState' = SessionState.Faulted
            publishPhase Faulted evalStats
            reply.Reply(Error (SageFsError.ResetFailed ex.Message))
            return! loop st middleware sessionState' evalStats
        | EvalHardReset (rebuild, reply) ->
          try
            let sessionState' = SessionState.WarmingUp
            publishPhase (Initializing None) evalStats
            logger.LogInfo "🔨 Hard resetting FSI session..."
            // Wait briefly for any in-flight eval thread to finish
            match currentEvalThread.Value with
            | Some thread ->
              match thread.Join(2000) with
              | false -> logger.LogWarning "⚠️ Eval thread did not exit in time, proceeding with hard reset"
              | true -> ()
              currentEvalThread.Value <- None
            | None -> ()

            match isNull (box st.Session) with
            | false ->
              let disposeTask = System.Threading.Tasks.Task.Run(fun () ->
                (st.Session :> System.IDisposable).Dispose())
              match disposeTask.Wait(Timeouts.sessionDispose) with
              | false -> logger.LogWarning $"⚠️ Session dispose timed out after {Timeouts.sessionDispose.TotalSeconds}s, continuing..."
              | true -> ()
            | true -> ()
            // Required before dotnet build can overwrite assemblies on Windows
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()

            match st.ShadowDir with
            | Some dir -> ShadowCopy.cleanupShadowDir dir
            | None -> ()

            match rebuild with
            | true ->
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
                      "dotnet",
                      sprintf "build \"%s\" --no-restore" projFile,
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
                    match proc.WaitForExit(1000) with
                    | true ->
                      finished <- true
                    | false ->
                      let now = DateTime.UtcNow
                      let totalMs = (now - startedAt).TotalMilliseconds
                      let inactiveMs = (now - lastActivity).TotalMilliseconds
                      match totalMs > float maxTotalMs with
                      | true ->
                        logger.LogWarning (sprintf "  ⚠️ Build exceeded %d min limit" (maxTotalMs / 60_000))
                        timedOut <- true
                        finished <- true
                      | false ->
                        match inactiveMs > float inactivityLimitMs with
                        | true ->
                          logger.LogWarning (sprintf "  ⚠️ Build inactive for %ds (no output)" (inactivityLimitMs / 1000))
                          timedOut <- true
                          finished <- true
                        | false -> ()
                  match timedOut with
                  | true ->
                    try proc.Kill(entireProcessTree = true) with ex -> logger.LogDebug (sprintf "Build kill failed: %s" ex.Message)
                    -1, sprintf "Build timed out (inactive for %ds or exceeded %d min limit)" (inactivityLimitMs / 1000) (maxTotalMs / 60_000)
                  | false ->
                    try stderrTask.Wait(5000) |> ignore with ex -> logger.LogDebug (sprintf "Build stderr wait failed: %s" ex.Message)
                    proc.ExitCode, String.concat "\n" stderrLines
                let exitCode, stderr = runBuild ()
                match exitCode <> 0 with
                | true ->
                  match stderr.Contains("denied") || stderr.Contains("locked") with
                  | true ->
                    logger.LogWarning "  ⚠️ DLL lock detected, retrying after GC..."
                    GC.Collect()
                    GC.WaitForPendingFinalizers()
                    GC.Collect()
                    Thread.Sleep(500)
                    let retryCode, retryErr = runBuild ()
                    match retryCode <> 0 with
                    | true ->
                      let msg = sprintf "Build failed on retry (exit code %d): %s" retryCode retryErr
                      logger.LogError (sprintf "  ❌ %s" msg)
                      let failedState = SessionState.Faulted
                      publishPhase Faulted evalStats
                      reply.Reply(Error (SageFsError.HardResetFailed msg))
                      return! loop st middleware failedState evalStats
                    | false ->
                      logger.LogInfo "  ✅ Build succeeded on retry"
                  | false ->
                    let msg = sprintf "Build failed (exit code %d): %s" exitCode stderr
                    logger.LogError (sprintf "  ❌ %s" msg)
                    let failedState = SessionState.Faulted
                    publishPhase Faulted evalStats
                    reply.Reply(Error (SageFsError.HardResetFailed msg))
                    return! loop st middleware failedState evalStats
                | false ->
                  logger.LogInfo "  ✅ Build succeeded"
              | None ->
                logger.LogWarning "  ⚠️ No project to build"
            | false -> ()

            let newShadowDir = ShadowCopy.createShadowDir ()
            logger.LogInfo "  Creating shadow copies..."
            let newSln = ShadowCopy.shadowCopySolution newShadowDir st.OriginalSolution
            logger.LogInfo "  Instrumenting assemblies for IL coverage..."
            let instrSw = System.Diagnostics.Stopwatch.StartNew()
            let targetPaths = newSln.Projects |> List.map (fun po -> po.TargetPath)
            let instrMaps = Features.LiveTesting.CoverageInstrumenter.instrumentShadowSolution targetPaths
            instrSw.Stop()
            let totalProbes = instrMaps |> Array.sumBy (fun (m: Features.LiveTesting.InstrumentationMap) -> m.TotalProbes)
            logger.LogInfo (sprintf "  IL coverage: %d probes across %d assemblies in %.0fms" totalProbes instrMaps.Length instrSw.Elapsed.TotalMilliseconds)
            ShadowCopy.cleanupStaleDirs ()

            logger.LogInfo "  Creating new FSI session..."
            let warmupTimeout = Timeouts.initSessionCancellation
            let warmupCts = new CancellationTokenSource()
            // Run warmup on a ThreadPool thread so the mailbox isn't blocked
            // if EvalInteractionNonThrowing hangs during namespace opening.
            // Task.Delay races against the warmup: if the timeout fires first,
            // we cancel and unblock the mailbox even if FSI is stuck.
            let warmupTask =
              System.Threading.Tasks.Task.Run<Result<_, exn>>(fun () ->
                let onProgress (s,t,msg) =
                  emit (Events.SageFsEvent.SessionWarmUpProgress {| Step = s; Total = t; Message = msg |})
                  publishPhase (Initializing (Some (sprintf "[%d/%d] %s" s t msg))) evalStats
                try
                  Async.RunSynchronously(
                    createFsiSession logger outStream useAsp newSln warmupCts.Token onProgress)
                  |> Ok
                with
                | :? OperationCanceledException as ex -> Error (ex :> exn)
                | ex -> Error ex)
            let timeoutTask = System.Threading.Tasks.Task.Delay(warmupTimeout)
            let! winner = System.Threading.Tasks.Task.WhenAny(warmupTask, timeoutTask) |> Async.AwaitTask
            let! warmupResult =
              async {
                match Object.ReferenceEquals(winner, warmupTask) with
                | true ->
                  let! r = warmupTask |> Async.AwaitTask
                  return r
                | false ->
                  logger.LogWarning "  ⚠️ Warmup timed out, cancelling..."
                  warmupCts.Cancel()
                  return Error (System.TimeoutException(sprintf "Warmup timed out after %.0f minutes" warmupTimeout.TotalMinutes) :> exn)
              }
            match warmupResult with
            | Error ex ->
              warmupCts.Dispose()
              ShadowCopy.cleanupShadowDir newShadowDir
              let msg = sprintf "Session warmup failed: %s" ex.Message
              logger.LogError (sprintf "  ❌ %s" msg)
              let failedState = SessionState.Faulted
              publishPhase Faulted evalStats
              reply.Reply(Error (SageFsError.HardResetFailed msg))
              return! loop st middleware failedState evalStats
            | Ok (newSession, newRecorder, _, warmupFailures, warmupCtx) ->
            warmupCts.Dispose()
            let newSt =
              { st with
                  Session = newSession
                  OutStream = newRecorder
                  Solution = newSln
                  ShadowDir = Some newShadowDir
                  Diagnostics = Features.DiagnosticsStore.empty
                  WarmupFailures = warmupFailures
                  WarmupContext = warmupCtx }
            logger.LogInfo "✅ Hard reset complete"
            let sessionState'' = SessionState.Ready
            publishSnapshot newSt Idle evalStats
            emit (Events.SessionHardReset {| Rebuild = rebuild |})
            reply.Reply(Ok "Hard reset complete. Fresh session with re-copied assemblies.")
            return! loop newSt middleware sessionState'' evalStats
          with ex ->
            logger.LogError (sprintf "❌ Hard reset failed: %s" ex.Message)
            let sessionState' = SessionState.Faulted
            publishPhase Faulted evalStats
            reply.Reply(Error (SageFsError.HardResetFailed ex.Message))
            return! loop st middleware sessionState' evalStats
      }

    and init () =
      async {
        try
          logger.LogInfo "Welcome to SageFs!"
          emit (Events.SessionStarted {|
            Config = Map.ofList [
              "projects", (sln.Projects |> List.map (fun p -> p.ProjectFileName) |> String.concat ";")
            ]
            StartedAt = DateTimeOffset.UtcNow
          |})

          match List.isEmpty sln.Projects with
          | false ->
            logger.LogInfo "Loading these projects: "
            for project in sln.Projects do
              logger.LogInfo project.ProjectFileName
          | true -> ()

          match sln.Projects |> List.tryHead with
          | Some primaryProject ->
            let projectDir = System.IO.Path.GetDirectoryName(primaryProject.ProjectFileName)
            logger.LogInfo $"Setting working directory to: %s{projectDir}"
            System.Environment.CurrentDirectory <- projectDir
          | None -> ()

          let initCts = new CancellationTokenSource(Timeouts.initSessionCancellation)
          let onProgress (s,t,msg) =
            emit (Events.SageFsEvent.SessionWarmUpProgress {| Step = s; Total = t; Message = msg |})
            publishPhase (Initializing (Some (sprintf "[%d/%d] %s" s t msg))) Affordances.EvalStats.empty
          let! fsiSession, recorder, args, warmupFailures, warmupCtx = createFsiSession logger outStream useAsp sln initCts.Token onProgress
          initCts.Dispose()
          
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
            WarmupContext = warmupCtx
            HotReloadState = HotReloadState.empty
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
          publishSnapshot st Idle evalStats
          return! loop st [] sessionState evalStats
        with ex ->
          let msg =
            match ex with
            | :? OperationCanceledException -> "Initial warm-up timed out after 5 minutes"
            | _ -> sprintf "Initial warm-up failed: %s" ex.Message
          logger.LogError (sprintf "❌ %s" msg)
          match isNull ex.InnerException with
          | false -> logger.LogError (sprintf "  Inner: %s" ex.InnerException.Message)
          | true -> ()
          logger.LogError (sprintf "  Stack: %s" ex.StackTrace)
          // Publish Faulted so MCP clients know the session is dead, not warming up.
          // AppState is None because the Session/OutStream are unusable.
          publishPhase Faulted Affordances.EvalStats.empty
          // Actor stays alive to accept hard_reset_fsi_session commands.
          // faultedSt keeps the loop alive but is NOT exposed via QuerySnapshot.
          let faultedSt = {
            Solution = sln
            OriginalSolution = originalSln
            ShadowDir = shadowDir
            Session = Unchecked.defaultof<_>
            Logger = logger
            OutStream = Unchecked.defaultof<_>
            Custom = initCustomData
            Diagnostics = Features.DiagnosticsStore.empty
            WarmupFailures = []
            WarmupContext = WarmupContext.empty
            StartupConfig = None
            HotReloadState = HotReloadState.empty
          }
          return! loop faultedSt [] SessionState.Faulted Affordances.EvalStats.empty
      }

    init ()
  )

  // Router actor: dispatches instantly, never blocks.
  // Query commands go to queryActor, eval commands go to evalActor.
  // Wrapped with ResilientActor.wrapLoop for safety (low risk but cheap insurance).
  let actor = MailboxProcessor.Start(fun mailbox ->
    let processRoute () (cmd: Command) =
      async {
        match cmd with
        // Query commands — forward to query actor (responds even during eval)
        | GetSessionPhase reply ->
          queryActor.Post(QueryGetSessionPhase reply)
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
        | GetTypeCheckWithSymbols(text, filePath, reply) ->
          queryActor.Post(QueryGetTypeCheckWithSymbols(text, filePath, reply))
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
                  try thread.Interrupt() with ex -> logger.LogWarning (sprintf "Thread interrupt during cancel failed: %s" ex.Message)
                | None -> ()
                true
              with ex ->
                logger.LogWarning (sprintf "Eval cancellation failed: %s" ex.Message)
                false
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
          | Some cts -> try cts.Cancel() with ex -> logger.LogDebug (sprintf "Reset cancel failed: %s" ex.Message)
          | None -> ()
          match currentEvalThread.Value with
          | Some thread -> try thread.Interrupt() with ex -> logger.LogDebug (sprintf "Reset interrupt failed: %s" ex.Message)
          | None -> ()
          evalActor.Post(EvalReset reply)
        | HardResetSession(rebuild, reply) ->
          // Cancel any running eval before hard resetting
          match currentEvalCts.Value with
          | Some cts -> try cts.Cancel() with ex -> logger.LogDebug (sprintf "Hard reset cancel failed: %s" ex.Message)
          | None -> ()
          match currentEvalThread.Value with
          | Some thread -> try thread.Interrupt() with ex -> logger.LogDebug (sprintf "Hard reset interrupt failed: %s" ex.Message)
          | None -> ()
          evalActor.Post(EvalHardReset(rebuild, reply))
      }
    let safeProcessRoute = ResilientActor.wrapLoop logger "router" processRoute
    let rec loop () =
      async {
        let! cmd = mailbox.Receive()
        let! () = safeProcessRoute () cmd
        return! loop ()
      }
    loop ()
  )

  // CQRS reads: volatile snapshot — zero blocking, zero mailbox round-trip
  // All fields derived from SessionPhase — impossible to desync.
  let getSessionState () =
    let snap = System.Threading.Volatile.Read(&latestSnapshot)
    SessionPhase.toSessionState snap.Phase
  let getEvalStats () =
    let snap = System.Threading.Volatile.Read(&latestSnapshot)
    snap.EvalStats
  let getWarmupFailures () =
    let snap = System.Threading.Volatile.Read(&latestSnapshot)
    match snap.Phase with
    | Active (st, _) -> st.WarmupFailures
    | _ -> []
  let getWarmupContext () =
    let snap = System.Threading.Volatile.Read(&latestSnapshot)
    match snap.Phase with
    | Active (st, _) -> st.WarmupContext
    | _ -> WarmupContext.empty
  let getStartupConfig () =
    let snap = System.Threading.Volatile.Read(&latestSnapshot)
    match snap.Phase with
    | Active (st, _) -> st.StartupConfig
    | _ -> None
  let getStatusMessage () =
    let snap = System.Threading.Volatile.Read(&latestSnapshot)
    match snap.Phase with
    | Initializing msg -> msg
    | _ -> None
  let cancelCurrentEval () =
    actor.PostAndAsyncReply(fun reply -> CancelEval reply)
    |> Async.RunSynchronously

  actor, diagnosticsChangedEvent.Publish, cancelCurrentEval, getSessionState, getEvalStats, getWarmupFailures, getWarmupContext, getStartupConfig, getStatusMessage
