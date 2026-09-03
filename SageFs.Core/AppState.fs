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
open SageFs.WarmupReplayCache

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
  Workflow: WorkflowTypes.SessionWorkflow
  AutoOpenNamespaces: bool
  AspireDetected: bool
  StartupTimestamp: DateTime
  StartupProfileLoaded: string option
}
  with
    /// Backward-compatible accessor for code that still checks the bool.
    member this.HotReloadEnabled = WorkflowTypes.SessionWorkflow.isHotReloadActive this.Workflow

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

/// Contract documentation for AppState.Custom.
/// This map is an escape hatch for features that cannot be added to AppState directly
/// due to circular compilation dependencies. Each feature module owns its key and accessors.
///
/// REGISTERED KEYS (update this list when adding a key):
///   "openedFiles"  | OpenDirective.OpenedFiles  | SageFs.Middleware.Directives.OpenDirective
///   "hotReload"    | HotReloading.State          | SageFs.Middleware.HotReloading
///
/// CONVENTION FOR NEW KEYS:
///   1. Define [<Literal>] key constant in the owning module.
///   2. Define the state as a plain record.
///   3. Write typed getCustom / setCustom functions using AppStateCustom.tryGetFeature/set.
///   4. Add entry to this doc comment.
///   5. Never write to another module's key.
module AppStateCustom =

  /// Read a typed feature value from Custom.
  /// Returns None if absent or if the stored value is a different type.
  let inline tryGetFeature<'T> (key: string) (state: AppState) : 'T option =
    match state.Custom |> Map.tryFind key with
    | Some (:? 'T as v) -> Some v
    | _ -> None

  /// Read a typed value from Custom.
  /// Returns None if absent; raises InvalidCastException if type is wrong.
  let inline tryGet<'T> (key: string) (state: AppState) : 'T option =
    state.Custom
    |> Map.tryFind key
    |> Option.map (fun o -> o :?> 'T)

  /// Write a typed value into Custom.
  let inline set<'T> (key: string) (value: 'T) (state: AppState) : AppState =
    { state with Custom = Map.add key (box value) state.Custom }

  /// Remove a key from Custom.
  let remove (key: string) (state: AppState) : AppState =
    { state with Custom = Map.remove key state.Custom }

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
// Full CSI coverage: params are 0x20-0x3F, final byte is 0x40-0x7E.
// This handles standard AND private CSI sequences (e.g. ESC[?25h, ESC[!p, ESC[>4m)
// plus OSC sequences (ESC]...BEL) and bare 2-char ESC sequences (e.g. ESC=, ESC>).
let reAnsiEscape =
  Regex(@"\x1b\[[\x20-\x3f]*[\x40-\x7e]|\x1b\].*?\x07|\x1b[^\[]", RegexOptions.Compiled)
let reProgressBar = Regex(@"^\d+/\d+\s*\|", RegexOptions.Compiled)
let reExpectoTimestamp = Regex(@"^\[\d{2}:\d{2}:\d{2}\s+\w{3}\]\s*", RegexOptions.Compiled)
let reExpectoSuffix = Regex(@"\s*<Expecto>\s*$", RegexOptions.Compiled)
let reExpectoSummary = Regex(@"EXPECTO!\s+(\d+)\s+tests?\s+run\s+in\s+(\S+)\s+for\s+(.+?)\s+.\s+(\d+)\s+passed,\s+(\d+)\s+ignored,\s+(\d+)\s+failed,\s+(\d+)\s+errored\.\s+(\S+!?)", RegexOptions.Compiled)

/// Strip ANSI escape sequences and terminal control codes from a string.
/// Cursor-reset sequences (move to column 0) become newlines to preserve logical line breaks.
let stripAnsi (s: string) =
  let s = reAnsiCursorReset.Replace(s, "\n")
  let s = reAnsiCursorVis.Replace(s, "")
  let s = reAnsiEscape.Replace(s, "")
  // Safety pass: remove any residual ESC chars from truncated or non-standard sequences.
  if s.IndexOf('\x1b') >= 0 then s.Replace("\x1b", "") else s

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

/// The eval gate: can code be evaluated right now? Phase-based — no null
/// checks, because a live Session/OutStream exist iff phase is Active.
/// Initializing covers two windows: initial warm-up (eval never arrives —
/// init() runs before the loop processes messages) and the in-flight reset
/// window (EvalRun IS queued behind EvalReset). Gating the latter is a
/// deliberate fail-closed improvement over the old parallel SessionState
/// loop variable, which reported WarmingUp (gate passed) while the reset was
/// mid-flight — a queued eval could have run against a session being torn
/// down. Faulted carries no AppState at all, so recovery is via reset.
let tryGetEvalAvailabilityError (phase: SessionPhase) =
  match phase with
  | Faulted ->
    Some "Session is faulted. Run hard_reset_fsi_session to recover."
  | Initializing _ ->
    Some "Session is resetting. Wait for reset to complete before evaluating."
  | Active (_, _) -> None

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

/// Extract all `open` namespace/module names from a source file's lines.
/// Returns distinct names preserving first-occurrence order.
/// Ignores commented-out lines and any non-`open` lines.
let extractOpensFromLines (lines: string[]) : string[] =
  lines
  |> Array.choose (fun line ->
    let trimmed = line.Trim()
    match trimmed.StartsWith("open ", System.StringComparison.Ordinal) && not (trimmed.StartsWith("//", System.StringComparison.Ordinal)) with
    | false -> None
    | true ->
      let parts = trimmed.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
      match parts.Length >= 2 with
       | true -> Some (parts.[1].TrimEnd(';'))
       | false -> None)
  |> Array.distinct

let internal resolveWarmupReplayPlan
  (logger: ILogger)
  (cachePath: string option)
  (fingerprint: Fingerprint)
  (discoverPlan: unit -> Async<ReplayPlan>) =
  async {
    match cachePath with
    | Some path ->
      match tryLoadValidPlan path fingerprint with
      | Some plan ->
        logger.LogInfo (sprintf "  Warmup replay cache hit: %s" path)
        return plan
      | None ->
        logger.LogInfo "  Warmup replay cache miss — discovering warmup plan."
        let! plan = discoverPlan()

        match trySave path plan with
        | Ok () ->
          logger.LogDebug (sprintf "  Warmup replay cache updated: %s" path)
        | Error message ->
          logger.LogWarning (sprintf "  Could not save warmup replay cache: %s" message)

        return plan
    | None ->
      return! discoverPlan()
  }

let private discoverWarmupReplayPlan
  (logger: ILogger)
  (originalSln: Solution)
  (sln: Solution)
  (autoOpenNamespaces: bool)
  (ct: CancellationToken)
  (fingerprint: Fingerprint) =
  async {
    let openedNamespaces = System.Collections.Generic.HashSet<string>()
    let namesToOpen = System.Collections.Generic.List<string>()
    let moduleNames = System.Collections.Generic.HashSet<string>()
    let loadedAssemblies = System.Collections.Generic.List<LoadedAssembly>()
    // Problems discovered during warmup planning that the user must see
    // (missing project DLLs, zero namespaces found despite auto-open ON).
    // Surfaced through ReplayPlan.DiscoveryWarnings → WarmupContext.FailedOpens
    // so the dashboard always explains why nothing was opened.
    let discoveryWarnings = System.Collections.Generic.List<string>()
    let stableAssemblyPaths =
      originalSln.Projects
      |> Seq.map (fun project -> project.ProjectFileName, project.TargetPath)
      |> Map.ofSeq

    let allFsFilesArr =
      sourceFilesForSolution originalSln
      |> List.toArray

    let mutable fileCount = 0

    let! fileResults =
      allFsFilesArr
      |> Array.map (fun fsFile -> async {
        ct.ThrowIfCancellationRequested()

        try
          match File.Exists(fsFile) with
          | true ->
            let! sourceLines = File.ReadAllLinesAsync fsFile |> Async.AwaitTask
            return Some (extractOpensFromLines sourceLines)
          | false -> return None
        with ex ->
          logger.LogWarning (sprintf "Could not parse opens from %s: %s" fsFile ex.Message)
          return None
      })
      |> fun tasks ->
        let sem = new System.Threading.SemaphoreSlim(8)

        tasks
        |> Array.map (fun task -> async {
          do! sem.WaitAsync() |> Async.AwaitTask

          try
            return! task
          finally
            sem.Release() |> ignore
        })
      |> Async.Parallel

    for result in fileResults do
      match result with
      | Some opens ->
        fileCount <- fileCount + 1

        for nsName in opens do
          match openedNamespaces.Add(nsName) with
          | true ->
            match autoOpenNamespaces with
            | true -> namesToOpen.Add(nsName)
            | false -> ()
          | false -> ()
      | None -> ()

    logger.LogInfo "  Scanning assemblies for namespaces..."

    let reflectionAlc =
      new System.Runtime.Loader.AssemblyLoadContext(
        "sagefs-reflection", isCollectible = true)

    for project in sln.Projects do
      ct.ThrowIfCancellationRequested()

      try
        match System.IO.File.Exists(project.TargetPath) with
        | false ->
          let msg = sprintf "Project assembly not found: %s — run 'dotnet build' first. Namespaces/modules from this project could not be auto-opened." project.TargetPath
          Log.warn "[Warmup] %s" msg
          discoveryWarnings.Add(msg)
        | true ->
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

                match parts.Length > 0 with
                | true -> Some parts.[0]
                | false -> None
              | true ->
                None)
            |> Array.distinct
            |> Array.filter (fun ns ->
              not (
                ns.StartsWith("<", System.StringComparison.Ordinal)
                || ns.StartsWith("$", System.StringComparison.Ordinal)
              ))

          let topLevelModules =
            types
            |> Array.filter (fun t ->
              t.Namespace |> isNull
              && (t.GetCustomAttributes(typeof<Microsoft.FSharp.Core.CompilationMappingAttribute>, false)
                  |> Array.exists (fun attr ->
                    let cma = attr :?> Microsoft.FSharp.Core.CompilationMappingAttribute
                    cma.SourceConstructFlags = Microsoft.FSharp.Core.SourceConstructFlags.Module))
              && not (
                t.Name.StartsWith("<", System.StringComparison.Ordinal)
                || t.Name.StartsWith("$", System.StringComparison.Ordinal)
                || t.Name.Contains("@")
                || t.Name.Contains("+")
              )
              && t.IsPublic
              && t.GetCustomAttributes(typeof<Microsoft.FSharp.Core.RequireQualifiedAccessAttribute>, false).Length = 0)
            |> Array.map (fun t ->
              match t.Name.EndsWith("Module", System.StringComparison.Ordinal) with
              | true -> t.Name.Substring(0, t.Name.Length - 6)
              | false -> t.Name)
            |> Array.distinct

          for ns in rootNamespaces do
            match openedNamespaces.Add(ns) with
            | true ->
              match autoOpenNamespaces with
              | true -> namesToOpen.Add(ns)
              | false -> ()
            | false -> ()

          for m in topLevelModules do
            match openedNamespaces.Add(m) with
            | true ->
              match autoOpenNamespaces with
              | true ->
                namesToOpen.Add(m)
                moduleNames.Add(m) |> ignore
              | false -> ()
            | false -> ()

          let stableAssemblyPath =
            stableAssemblyPaths
            |> Map.tryFind project.ProjectFileName
            |> Option.defaultValue project.TargetPath

          loadedAssemblies.Add({
            Name = asm.GetName().Name
            Path = stableAssemblyPath
            NamespaceCount = rootNamespaces.Length
            ModuleCount = topLevelModules.Length
          } : LoadedAssembly)
      with ex ->
        logger.LogWarning (sprintf "Could not analyze %s: %s" project.TargetPath ex.Message)

    reflectionAlc.Unload()

    // Auto-open is ON but nothing was discovered to open. This is either a
    // project with genuinely no namespaces/modules (bare/empty) or a discovery
    // problem. The user must see WHICH, so surface it as a warning instead of
    // silently reporting a "successful" warmup that opened nothing.
    match autoOpenNamespaces, namesToOpen.Count, fileCount with
    | true, 0, 0 ->
      discoveryWarnings.Add(
        "Auto-open was enabled but no source files were found for this project. " +
        "Nothing could be auto-opened — check that the project path is correct and the .fs/.fsx files exist.")
    | true, 0, n when n > 0 ->
      discoveryWarnings.Add(
        sprintf "Auto-open was enabled and %d source file(s) were scanned, but no namespaces/modules were found to open. If the project defines modules, ensure they are compiled into the project assembly (dotnet build) and are not hidden behind RequireQualifiedAccess." n)
    | _ -> ()

    let namePairs =
      namesToOpen
      |> Seq.map (fun name ->
        name,
        match moduleNames.Contains(name) with
        | true -> OpenableKind.Module
        | false -> OpenableKind.Namespace)
      |> Seq.toList

    return
      createPlan
        fingerprint
        fileCount
        (Seq.toList loadedAssemblies)
        namePairs
        (Seq.toList discoveryWarnings)
  }

/// Creates a fresh FSI session with warm-up: loads startup files and opens namespaces.
/// The CancellationToken is passed through to FSI EvalInteraction calls so that
/// warm-up can be cancelled if it takes too long (e.g. a stuck module initializer).
let createFsiSession (logger: ILogger) (outStream: TextWriter) (useAsp: bool) (originalSln: Solution) (sln: Solution) (autoOpenNamespaces: bool) (hotReload: bool) (ct: CancellationToken) (onProgress: (int * int * string) -> unit) =
  async {
    let warmupStartedAt = System.DateTimeOffset.UtcNow
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()
    let args = solutionToFsiArgs logger useAsp hotReload sln
    let replayArgs = solutionToFsiArgs logger useAsp hotReload originalSln
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

    // Chesterton's fence: evaluate the embedded base.fsx FIRST so the
    // feature-gate flags (_SageFsHotReload, _SageFsCompExpr) are bound before
    // any user code runs. The middleware gates read these via
    // Session.TryFindBoundValue; if they are never bound, hot-reload detouring
    // and computation-expression rewriting silently no-op (the P0 hot-reload
    // gap: WebLive sessions never detoured because _SageFsHotReload was
    // unbound). getBaseConfigString() was dead code — wire it here.
    let baseConfig =
      try
        SageFs.Utils.Configuration.getBaseConfigString()
        |> Async.AwaitTask
        |> Async.RunSynchronously
      with ex ->
        logger.LogWarning (sprintf "  Failed to load embedded base.fsx: %s" ex.Message)
        ""
    match baseConfig.Trim() with
    | "" -> ()
    | _ ->
      logger.LogInfo "  Loading embedded base.fsx (feature gates)"
      try
        fsiSession.EvalInteraction(baseConfig, ct)
      with ex ->
        logger.LogWarning (sprintf "  base.fsx eval failed (continuing): %s" ex.Message)

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

    let replayFingerprint =
      buildFingerprintForSolution autoOpenNamespaces replayArgs originalSln

    let! replayPlan =
      resolveWarmupReplayPlan
        logger
        (tryGetCachePath originalSln)
        replayFingerprint
        (fun () ->
          discoverWarmupReplayPlan
            logger
            originalSln
            sln
            autoOpenNamespaces
            ct
            replayFingerprint)

    let fileCount = replayPlan.SourceFilesScanned
    logger.LogInfo (sprintf "  Scanned %d source files for opens in %dms" fileCount sw.ElapsedMilliseconds)
    let scanPhaseMs = sw.ElapsedMilliseconds
    onProgress(2, 4, sprintf "Scanned %d source files" fileCount)
    logger.LogInfo (sprintf "  Assembly scan complete in %dms" sw.ElapsedMilliseconds)
    let assemblyPhaseMs = sw.ElapsedMilliseconds
    let loadedAssemblies = replayPlan.AssembliesLoaded
    let replayNamePairs = namePairs replayPlan
    let totalNames = replayNamePairs.Length
    match autoOpenNamespaces with
    | true -> onProgress(3, 4, sprintf "Scanned assemblies, opening %d namespaces" totalNames)
    | false -> onProgress(3, 4, "Scanned assemblies, auto-open disabled")
    // Phase 3: Open all collected names with rich diagnostics via iterative retry
    let mutable openCount = 0
    let toWarmupDiagnostics (diagnostics: FSharpDiagnostic array) : WarmupFcsDiagnostic list =
      diagnostics
      |> Array.map (fun d ->
        { Message = d.Message
          Severity =
            match d.Severity with
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error -> "error"
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning -> "warning"
            | _ -> "info"
          ErrorNumber = d.ErrorNumber
          FileName =
            match d.FileName with
            | null
            | "" -> None
            | fileName -> Some fileName
          StartLine = d.StartLine
          EndLine = d.EndLine
          StartColumn = d.StartColumn
          EndColumn = d.EndColumn })
      |> Array.toList
    let reportOpenSuccess name elapsed =
      openCount <- openCount + 1
      onProgress(openCount, totalNames, sprintf "✅ open %s (%.0fms)" name elapsed)
    let reportOpenSkipped name elapsed =
      openCount <- openCount + 1
      onProgress(openCount, totalNames, sprintf "⏭️ open %s (skipped, %.0fms)" name elapsed)
    let reportOpenFailure name elapsed =
      openCount <- openCount + 1
      onProgress(openCount, totalNames, sprintf "✖ open %s — failed (%.0fms)" name elapsed)
    let singleOpener name kind =
      ct.ThrowIfCancellationRequested()
      let label = OpenableKind.label kind
      logger.LogDebug (sprintf "Opening %s: %s" label name)
      let openSw = System.Diagnostics.Stopwatch.StartNew()
      let result, diagnostics = fsiSession.EvalInteractionNonThrowing(sprintf "open %s;;" name, ct)
      let elapsed = openSw.Elapsed.TotalMilliseconds
      match result with
      | Choice1Of2 _ ->
        reportOpenSuccess name elapsed
        match kind with
        | OpenableKind.Module -> logger.LogInfo (sprintf "✅ Opened module: %s (%.1fms)" name elapsed)
        | OpenableKind.Namespace -> ()
        WarmUp.OpenSuccess elapsed
      | Choice2Of2 ex ->
        let allText = sprintf "%s %s" ex.Message (diagnostics |> Array.map (fun d -> d.Message) |> String.concat " ")
        match isBenignOpenError allText with
        | true ->
          reportOpenSkipped name elapsed
          logger.LogDebug (sprintf "⏭️ Skipped %s (RequireQualifiedAccess — types accessible via qualified paths)" name)
          WarmUp.OpenSuccess elapsed
        | false ->
          reportOpenFailure name elapsed
          WarmUp.OpenFailed (ex.Message, toWarmupDiagnostics diagnostics, elapsed)
    let batchOpener batch =
      ct.ThrowIfCancellationRequested()
      match batch with
      | [] -> WarmUp.OpenSuccess 0.0
      | [ name, kind ] -> singleOpener name kind
      | _ ->
        logger.LogDebug (sprintf "Opening batch of %d namespaces/modules" batch.Length)
        let script =
          batch
          |> List.map (fun (name, _) -> sprintf "open %s" name)
          |> String.concat Environment.NewLine
          |> fun body -> body + Environment.NewLine + ";;"
        let openSw = System.Diagnostics.Stopwatch.StartNew()
        let result, diagnostics = fsiSession.EvalInteractionNonThrowing(script, ct)
        let elapsed = openSw.Elapsed.TotalMilliseconds
        match result with
        | Choice1Of2 _ ->
          let durationPerName = elapsed / float batch.Length
          for name, kind in batch do
            reportOpenSuccess name durationPerName
            match kind with
            | OpenableKind.Module -> logger.LogInfo (sprintf "✅ Opened module: %s (%.1fms, batched)" name durationPerName)
            | OpenableKind.Namespace -> ()
          logger.LogDebug (sprintf "✅ Opened batch of %d namespaces/modules in %.1fms" batch.Length elapsed)
          WarmUp.OpenSuccess elapsed
        | Choice2Of2 ex ->
          logger.LogDebug (sprintf "Batch open failed for %d namespaces/modules in %.1fms: %s" batch.Length elapsed ex.Message)
          WarmUp.OpenFailed (ex.Message, toWarmupDiagnostics diagnostics, elapsed)

    let succeeded, failed =
      match autoOpenNamespaces with
      | true ->
        logger.LogInfo (sprintf "Opening %d namespaces/modules (batched in chunks of %d with dependency retry)..." totalNames WarmUp.DefaultOpenBatchSize)
        WarmUp.openWithRetryRichBatched 5 WarmUp.DefaultOpenBatchSize batchOpener singleOpener replayNamePairs
      | false ->
        logger.LogInfo "Auto-open disabled — skipping namespace/module opens."
        [], []
    let openPhaseMs = sw.ElapsedMilliseconds
    match autoOpenNamespaces with
    | true ->
      logger.LogInfo (sprintf "✅ Opened %d/%d namespaces/modules in %dms" (List.length succeeded) totalNames sw.ElapsedMilliseconds)
    | false ->
      logger.LogInfo (sprintf "✅ Warm-up skipped namespace/module opens in %dms" sw.ElapsedMilliseconds)
    match List.isEmpty failed with
    | false ->
      logger.LogWarning (sprintf "⚠️  %d could not be opened:" (List.length failed))
      for f in failed do
        let kind = OpenableKind.label f.Kind
        logger.LogWarning (sprintf "  ✗ %s (%s): %s" f.Name kind f.ErrorMessage)
        for d in f.Diagnostics do
          let loc =
            match d.FileName with
            | Some fn -> sprintf "%s:%d:%d" fn d.StartLine d.StartColumn
            | None -> "unknown location"
          logger.LogWarning (sprintf "    FS%04d %s — %s" d.ErrorNumber loc d.Message)
    | true -> ()

    // WHY — verify project references actually loaded into the AppDomain. FSI
    // surfaces -r load failures only as init stderr warnings, which previously
    // produced "Ready" sessions where every project open failed with 'not
    // defined' while get_fsi_status claimed warmup was complete (friction
    // report 2026-08). Because — a session with zero project assemblies is dead;
    // reporting it Ready destroys agent trust in every downstream signal.
    let expectedAssemblies =
      sln.Projects
      |> List.map (fun p -> Path.GetFileNameWithoutExtension p.TargetPath)
      |> List.distinct
    let loadedAssemblyNames =
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.map (fun a -> a.GetName().Name)
      |> Array.toList
    match WarmUp.classifyAssemblyLoad expectedAssemblies loadedAssemblyNames with
    | WarmUp.AllExpectedLoaded -> ()
    | WarmUp.PartiallyLoaded missing ->
      logger.LogWarning
        (sprintf "  ⚠️ Assembly verification: %d/%d project assemblies loaded; MISSING: %s — code touching these will fail with 'not defined'"
          (expectedAssemblies.Length - missing.Length)
          expectedAssemblies.Length
          (String.concat ", " missing))
    | WarmUp.NothingLoaded ->
      let fsiErrors = fsiErrorWriter.ToString()
      let msg =
        sprintf "Warmup verification failed: NONE of %d project assemblies loaded into FSI (expected: %s).%s"
          expectedAssemblies.Length
          (String.concat ", " expectedAssemblies)
          (match fsiErrors.Length > 0 with | true -> sprintf " FSI init errors: %s" fsiErrors | false -> "")
      logger.LogError (sprintf "  ❌ %s" msg)
      failwith msg

    // Surface discovery warnings (missing project DLLs, zero namespaces found
    // despite auto-open ON) through the same "Failed Opens" channel the
    // dashboard already renders — warmup must never fail silently.
    let failedWithWarnings =
      let warningFailures =
        replayPlan.DiscoveryWarnings
        |> List.map (fun msg -> {
          Name = "(auto-open discovery)"
          Kind = OpenableKind.Namespace
          ErrorMessage = msg
          Diagnostics = []
          RetryCount = 1
          DurationMs = 0.0
        })
      failed @ warningFailures

    let warmupCtx =
      WarmupContext.completeWarmup
        warmupStartedAt
        fileCount
        loadedAssemblies
        succeeded
        failedWithWarnings
        scanPhaseMs
        assemblyPhaseMs
        openPhaseMs

    logger.LogInfo (sprintf "  Warm-up complete in %dms (scan=%dms, asm=%dms, open=%dms)"
      warmupCtx.PhaseTiming.TotalMs
      warmupCtx.PhaseTiming.ScanSourceFilesMs
      warmupCtx.PhaseTiming.ScanAssembliesMs
      warmupCtx.PhaseTiming.OpenNamespacesMs)
    onProgress(4, 4, sprintf "Warm-up complete in %dms" warmupCtx.PhaseTiming.TotalMs)

    match autoOpenNamespaces with
    | true ->
      logger.LogDebug "Restoring core F# operators after warm-up boundary."
      // Restore core F# after warm-up opens. User project libraries like FSharpPlus shadow
      // min/max with SRTP-generic versions and replace the async CE builder.
      fsiSession.EvalInteractionNonThrowing("open Microsoft.FSharp.Core.Operators;;", ct) |> ignore
      fsiSession.EvalInteractionNonThrowing("open Microsoft.FSharp.Core.ExtraTopLevelOperators;;", ct) |> ignore
    | false -> ()

    return fsiSession, recorder, args, failed, warmupCtx
  }

/// Pipeline builder: takes middleware list + core eval function, returns composed pipeline.
/// Default is `buildPipeline`. Tracing module provides an instrumented alternative.
type PipelineBuildFn = Middleware list -> MiddlewareNext -> MiddlewareNext

let mkAppStateActor (logger: ILogger) (initCustomData: Map<string, obj>) outStream useAsp (originalSln: Solution) (shadowDir: string option) (autoOpenNamespaces: bool) (hotReload: bool) (onEvent: Events.SageFsEvent -> unit) (pipelineBuildFn: PipelineBuildFn) (sln: Solution) =
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
    // Monotonic generation for live binding snapshots — lets consumers ignore
    // stale/out-of-order snapshots.
    let liveValueGeneration = ref 0L
    let rec loop (phase: SessionPhase) middleware evalStats =
      async {
        let! cmd = mailbox.Receive()

        match cmd with
        | EvalEnableStdout ->
          match phase with
          | Faulted ->
            logger.LogWarning "EnableStdout requested on faulted session; ignoring"
          | Initializing _ ->
            logger.LogWarning "EnableStdout requested during warmup; ignoring"
          | Active (st, _) ->
            st.OutStream.Enable()
          return! loop phase middleware evalStats
        | EvalRun(request, cts, reply) ->
          match tryGetEvalAvailabilityError phase with
          | Some message ->
            currentEvalCts.Value <- None
            let errResponse = {
              EvaluationResult = Error (InvalidOperationException message)
              Diagnostics = [||]
              EvaluatedCode = request.Code
              Metadata = Map.empty
            }
            emit (Events.EvalFailed {| Code = request.Code; Error = message; Diagnostics = [] |})
            reply.Reply errResponse
            return! loop phase middleware evalStats
          | None ->
            match phase with
            | Active (st, _) ->
              publishSnapshot st Evaluating evalStats
              let sw = System.Diagnostics.Stopwatch.StartNew()
              emit (Events.EvalRequested {| Code = request.Code; Source = Events.System |})
              let pipeline = pipelineBuildFn (wrapErrorMiddleware :: middleware) (evalFn cts.Token)
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
              return! loop (Active (st, Evaluating)) middleware evalStats
            | Initializing _ | Faulted ->
              // Unreachable: tryGetEvalAvailabilityError gates these phases with
              // Some above. Kept exhaustive so the phase match is total.
              return! loop phase middleware evalStats
        | EvalFinished(result, sw, code, reply) ->
          sw.Stop()
          currentEvalCts.Value <- None
          currentEvalThread.Value <- None
          match result with
          | Ok(res, newSt) ->
            let evalStats' = Affordances.EvalStats.record sw.Elapsed evalStats
            publishSnapshot newSt Idle evalStats'
            // Live binding watch window: capture the REAL bound values from the
            // FSI session (not the printed text) and attach a serialized tree to
            // the response metadata so the daemon can update its adaptive store.
            let resWithLiveValues =
              try
                // Session id is stamped by the worker/daemon boundary — the
                // daemon keys its adaptive store by the session id it knows.
                let boundValues =
                  newSt.Session.GetBoundValues()
                  |> List.map (fun bv ->
                    let value =
                      try bv.Value.ReflectionValue
                      with _ -> null
                    let typeSig =
                      try
                        match bv.Value.ReflectionType with
                        | null -> ""
                        | t -> t.Name
                      with _ -> ""
                    (bv.Name, typeSig, value))
                let generation = System.Threading.Interlocked.Increment(&liveValueGeneration.contents)
                let snap = Features.LiveValueTree.buildSnapshot "" generation boundValues
                // Use WorkerProtocol.Serialization (FSharp.SystemTextJson) so the
                // NodeKind DU and other F# types serialize correctly.
                let json = WorkerProtocol.Serialization.serialize snap
                { res with Metadata = res.Metadata |> Map.add "liveValueSnapshot" (box json) }
              with ex ->
                Log.warn "[AppState] Live value snapshot capture failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
                { res with Metadata = res.Metadata |> Map.add "liveValueSnapshotError" (box ex.Message) }
            match resWithLiveValues.EvaluationResult with
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
            // Emit trace if pipeline instrumentation produced one
            match res.Metadata |> Map.tryFind "pipelineTrace" with
            | Some traceObj ->
              match traceObj with
              | :? EvalPipeline.PipelineTrace<string> as trace ->
                let stages = trace.Stages |> List.map (fun s -> s.Name, float s.ElapsedMs)
                let totalMs = stages |> List.sumBy snd
                emit (Events.EvalTraced {| Code = code; Stages = stages; TotalMs = totalMs |})
              | _ -> ()
            | None -> ()
            reply.Reply resWithLiveValues
            return! loop (Active (newSt, Idle)) middleware evalStats'
          | Error ex ->
            let errResponse = {
              EvaluationResult = Error ex
              Diagnostics = [||]
              EvaluatedCode = code
              Metadata = Map.empty
            }
            match phase with
            | Active (st, _) ->
              publishSnapshot st Idle evalStats
              emit (Events.EvalFailed {|
                Code = code
                Error = ex.Message
                Diagnostics = []
              |})
              reply.Reply errResponse
              return! loop (Active (st, Idle)) middleware evalStats
            | Initializing _ | Faulted ->
              // Straggler: the eval thread was still running when a reset tore
              // the session down (or the reset failed). Publishing the eval's
              // stale AppState would resurrect a snapshot of a disposed/null
              // session — reply with the error and stay in the current phase.
              logger.LogWarning (sprintf "EvalFinished(Error) arrived outside Active phase (session state: %s); not republishing state: %s" (SessionPhase.toSessionState phase |> SessionState.label) ex.Message)
              reply.Reply errResponse
              return! loop phase middleware evalStats
        | EvalAddMiddleware(additionalMiddleware, r) ->
          r.Reply(())
          return! loop phase (additionalMiddleware @ middleware) evalStats
        | EvalReset reply ->
          try
            publishPhase (Initializing None) evalStats
            logger.LogInfo "🔄 Resetting FSI session..."
            // The reset needs a live session's context only if one exists.
            // A Faulted phase carries no AppState: the closure captures
            // (sln/originalSln/shadowDir/initCustomData) are exactly what the
            // old faulted tombstone held, so recovery needs no state at all.
            let activeSt = SessionPhase.tryAppState phase
            // Wait briefly for any in-flight eval thread to finish
            match currentEvalThread.Value with
            | Some thread ->
              match thread.Join(2000) with
              | false -> logger.LogWarning "⚠️ Eval thread did not exit in time, proceeding with reset"
              | true -> ()
              currentEvalThread.Value <- None
            | None -> ()
            match phase with
            | Active (st, _) when not (isNull (box st.Session)) ->
              (st.Session :> System.IDisposable).Dispose()
            | _ -> ()
            let softResetCts = new CancellationTokenSource(Timeouts.softResetCancellation)
            let onProgress (s,t,msg) =
              emit (Events.SageFsEvent.SessionWarmUpProgress {| Step = s; Total = t; Message = msg |})
              publishPhase (Initializing (Some (sprintf "[%d/%d] %s" s t msg))) evalStats
            // StartupConfig is NOT a closure capture: it is built in init()
            // after warmup and mutated by QueryUpdateMcpPort. When Active,
            // read the live config so a reset preserves McpPort and any
            // profile-loaded flags; when Faulted (StartupConfig was None in
            // the tombstone) fall back to the same defaults the old code used.
            let startupConfig =
              match phase with
              | Active (st, _) -> st.StartupConfig
              | Initializing _ | Faulted -> None
            let autoOpenNamespaces =
              startupConfig
              |> Option.map (fun cfg -> cfg.AutoOpenNamespaces)
              |> Option.defaultValue true
            let hotReload =
              startupConfig
              |> Option.map (fun cfg -> cfg.HotReloadEnabled)
              |> Option.defaultValue false
            // Immutable reset context: on the live path these come off the
            // current st (they may have evolved through prior hard resets);
            // on the Faulted path the closure captures ARE today's tombstone
            // values (Solution = sln, OriginalSolution = originalSln,
            // ShadowDir = shadowDir).
            let resetSolution, resetOriginalSolution =
              match activeSt with
              | Some st -> st.Solution, st.OriginalSolution
              | None -> sln, originalSln
            let! newSession, newRecorder, _, warmupFailures, warmupCtx =
              createFsiSession
                logger
                outStream
                useAsp
                resetOriginalSolution
                resetSolution
                autoOpenNamespaces
                hotReload
                softResetCts.Token
                onProgress
            softResetCts.Dispose()
            let baseSt =
              match activeSt with
              | Some st -> st
              | None ->
                // Recovery from Faulted: start fresh — same field values the
                // deleted faulted tombstone held (Custom = initCustomData,
                // Diagnostics empty, WarmupFailures [], WarmupContext empty,
                // HotReloadState empty, StartupConfig None, plus the closure
                // solution/shadow context).
                { Solution = sln
                  OriginalSolution = originalSln
                  ShadowDir = shadowDir
                  Logger = logger
                  Session = newSession
                  OutStream = newRecorder
                  Custom = initCustomData
                  Diagnostics = Features.DiagnosticsStore.empty
                  WarmupFailures = warmupFailures
                  WarmupContext = warmupCtx
                  HotReloadState = HotReloadState.empty
                  StartupConfig = None }
            let newSt =
              match activeSt with
              | Some st ->
                { st with Session = newSession; OutStream = newRecorder; Diagnostics = Features.DiagnosticsStore.empty; WarmupFailures = warmupFailures; WarmupContext = warmupCtx }
              | None -> baseSt
            logger.LogInfo "✅ FSI session reset complete"
            publishSnapshot newSt Idle evalStats
            emit Events.SessionReset
            reply.Reply(Ok ())
            return! loop (Active (newSt, Idle)) middleware evalStats
          with ex ->
            logger.LogError $"❌ FSI session reset failed: {ex.Message}"
            publishPhase Faulted evalStats
            reply.Reply(Error (SageFsError.ResetFailed ex.Message))
            return! loop Faulted middleware evalStats
        | EvalHardReset (rebuild, reply) ->
          try
            publishPhase (Initializing None) evalStats
            logger.LogInfo "🔨 Hard resetting FSI session..."
            let activeSt = SessionPhase.tryAppState phase
            // Context audit (same policy as EvalReset): StartupConfig lives on
            // the live st when Active (mutated by QueryUpdateMcpPort) and is
            // None-equivalent when Faulted; OriginalSolution/ShadowDir are the
            // closure captures exactly when Faulted (they matched the tombstone).
            let startupConfig =
              match phase with
              | Active (st, _) -> st.StartupConfig
              | Initializing _ | Faulted -> None
            let autoOpenNamespaces =
              startupConfig
              |> Option.map (fun cfg -> cfg.AutoOpenNamespaces)
              |> Option.defaultValue true
            let hotReload =
              startupConfig
              |> Option.map (fun cfg -> cfg.HotReloadEnabled)
              |> Option.defaultValue false
            let resetOriginalSolution =
              match activeSt with
              | Some st -> st.OriginalSolution
              | None -> originalSln
            let shadowDirToClean =
              match activeSt with
              | Some st -> st.ShadowDir
              | None -> shadowDir
            // Wait briefly for any in-flight eval thread to finish
            match currentEvalThread.Value with
            | Some thread ->
              let! joined = System.Threading.Tasks.Task.Run(fun () -> thread.Join(2000)) |> Async.AwaitTask
              match joined with
              | false -> logger.LogWarning "⚠️ Eval thread did not exit in time, proceeding with hard reset"
              | true -> ()
              currentEvalThread.Value <- None
            | None -> ()

            match phase with
            | Active (st, _) when not (isNull (box st.Session)) ->
              let disposeTask = System.Threading.Tasks.Task.Run(fun () ->
                (st.Session :> System.IDisposable).Dispose())
              let timeoutTask = System.Threading.Tasks.Task.Delay(Timeouts.sessionDispose)
              let! completed = System.Threading.Tasks.Task.WhenAny(disposeTask, timeoutTask) |> Async.AwaitTask
              match System.Object.ReferenceEquals(completed, disposeTask) with
              | false -> logger.LogWarning $"⚠️ Session dispose timed out after {Timeouts.sessionDispose.TotalSeconds}s, continuing..."
              | true -> ()
            | _ -> ()
            // Required before dotnet build can overwrite assemblies on Windows
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()

            match shadowDirToClean with
            | Some dir -> ShadowCopy.cleanupShadowDir dir
            | None -> ()

            match rebuild with
            | true ->
              // Build only the primary project — dotnet build resolves dependencies transitively.
              // Building each project separately is redundant and slow for multi-project solutions.
              let primaryProject =
                resetOriginalSolution.Projects
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
                let! exitCode, stderr = System.Threading.Tasks.Task.Run(fun () -> runBuild()) |> Async.AwaitTask
                match exitCode <> 0 with
                | true ->
                  match stderr.Contains("denied") || stderr.Contains("locked") with
                  | true ->
                    logger.LogWarning "  ⚠️ DLL lock detected, retrying after GC..."
                    GC.Collect()
                    GC.WaitForPendingFinalizers()
                    GC.Collect()
                    do! Async.Sleep 500
                    let! retryCode, retryErr = System.Threading.Tasks.Task.Run(fun () -> runBuild()) |> Async.AwaitTask
                    match retryCode <> 0 with
                    | true ->
                      let msg = sprintf "Build failed on retry (exit code %d): %s" retryCode retryErr
                      logger.LogError (sprintf "  ❌ %s" msg)
                      publishPhase Faulted evalStats
                      reply.Reply(Error (SageFsError.HardResetFailed msg))
                      return! loop Faulted middleware evalStats
                    | false ->
                      logger.LogInfo "  ✅ Build succeeded on retry"
                  | false ->
                    let msg = sprintf "Build failed (exit code %d): %s" exitCode stderr
                    logger.LogError (sprintf "  ❌ %s" msg)
                    publishPhase Faulted evalStats
                    reply.Reply(Error (SageFsError.HardResetFailed msg))
                    return! loop Faulted middleware evalStats
                | false ->
                  logger.LogInfo "  ✅ Build succeeded"
              | None ->
                logger.LogWarning "  ⚠️ No project to build"
            | false -> ()

            let newShadowDir = ShadowCopy.createShadowDir ()
            logger.LogInfo "  Creating shadow copies..."
            let newSln = ShadowCopy.shadowCopySolution newShadowDir resetOriginalSolution
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
                    createFsiSession
                      logger
                      outStream
                      useAsp
                      resetOriginalSolution
                      newSln
                      autoOpenNamespaces
                      hotReload
                      warmupCts.Token
                      onProgress)
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
              publishPhase Faulted evalStats
              reply.Reply(Error (SageFsError.HardResetFailed msg))
              return! loop Faulted middleware evalStats
            | Ok (newSession, newRecorder, _, warmupFailures, warmupCtx) ->
            warmupCts.Dispose()
            let newSt =
              match activeSt with
              | Some st ->
                // Live path: preserve Custom/HotReloadState/StartupConfig (incl.
                // McpPort) and swap the session-bearing fields.
                { st with
                    Session = newSession
                    OutStream = newRecorder
                    Solution = newSln
                    ShadowDir = Some newShadowDir
                    Diagnostics = Features.DiagnosticsStore.empty
                    WarmupFailures = warmupFailures
                    WarmupContext = warmupCtx }
              | None ->
                // Recovery from Faulted/Initializing: fresh state carrying the
                // closure context — mirrors the deleted faulted tombstone fields.
                { Solution = newSln
                  OriginalSolution = originalSln
                  ShadowDir = Some newShadowDir
                  Logger = logger
                  Session = newSession
                  OutStream = newRecorder
                  Custom = initCustomData
                  Diagnostics = Features.DiagnosticsStore.empty
                  WarmupFailures = warmupFailures
                  WarmupContext = warmupCtx
                  HotReloadState = HotReloadState.empty
                  StartupConfig = None }
            logger.LogInfo "✅ Hard reset complete"
            publishSnapshot newSt Idle evalStats
            emit (Events.SessionHardReset {| Rebuild = rebuild |})
            reply.Reply(Ok "Hard reset complete. Fresh session with re-copied assemblies.")
            return! loop (Active (newSt, Idle)) middleware evalStats
          with ex ->
            logger.LogError (sprintf "❌ Hard reset failed: %s" ex.Message)
            publishPhase Faulted evalStats
            reply.Reply(Error (SageFsError.HardResetFailed ex.Message))
            return! loop Faulted middleware evalStats
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
          let! fsiSession, recorder, args, warmupFailures, warmupCtx =
            createFsiSession
              logger
              outStream
              useAsp
              originalSln
              sln
              autoOpenNamespaces
              hotReload
              initCts.Token
              onProgress
          initCts.Dispose()
          
          let warmupErrors =
            warmupFailures
            |> List.map (fun f ->
              let kind = OpenableKind.label f.Kind
              sprintf "%s (%s): %s" f.Name kind f.ErrorMessage)
          let warmupDuration =
            WarmupContext.completionDuration warmupCtx
          emit (Events.SessionWarmUpCompleted {| Duration = warmupDuration; Errors = warmupErrors |})
          
          // Evaluate startup profile if found
          let startupProfileResult =
            let workingDir = System.Environment.CurrentDirectory
            let evalFn code =
              fsiSession.EvalInteraction(code, CancellationToken.None)
            let logFn msg = logger.LogInfo msg
            let outcome = StartupProfile.applyIfPresent workingDir evalFn logFn

            match outcome with
            | StartupProfile.Failed (_, message) ->
              logger.LogWarning message
            | StartupProfile.NotFound
            | StartupProfile.Loaded _ -> ()

            StartupProfile.loadedPath outcome
          
          emit Events.SessionReady

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
              Workflow = WorkflowTypes.SessionWorkflow.fromHotReloadBool hotReload
              AutoOpenNamespaces = autoOpenNamespaces
              AspireDetected = useAsp
              StartupTimestamp = DateTime.UtcNow
              StartupProfileLoaded = startupProfileResult
            }
          }

          let evalStats = Affordances.EvalStats.empty
          publishSnapshot st Idle evalStats
          return! loop (Active (st, Idle)) [] evalStats
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
          // Faulted carries NO AppState: Session/OutStream are unrepresentable,
          // and the eval loop stays alive in the Faulted phase to accept
          // hard_reset_fsi_session commands (the reset handlers rebuild state
          // from the actor closure captures). This replaces the old tombstone
          // that held Unchecked.defaultof Session/OutStream.
          publishPhase Faulted Affordances.EvalStats.empty
          return! loop Faulted [] Affordances.EvalStats.empty
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
    |> Async.StartAsTask

  actor, diagnosticsChangedEvent.Publish, cancelCurrentEval, getSessionState, getEvalStats, getWarmupFailures, getWarmupContext, getStartupConfig, getStatusMessage
