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
open SageFs.EvalActorDecision

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
  /// The FSI session behind the port: in this process today, an isolated host process next.
  Session: FsiSession.IFsiSession
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
///   "hotReload"    | HotReloadCore.State         | SageFs.Middleware.HotReloadCore
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

/// Rich session lifecycle phase — the source of truth for QuerySnapshot.
/// Carries domain data only in states where it's meaningful, making
/// impossible states (e.g., "Faulted with a valid AppState") unrepresentable.
/// Replaces the old (AppState option × SessionState) pair which could desync.
type SessionPhase =
  | Initializing of statusMessage: string option
  | Active of AppState * SessionActivity
  | Faulted of reason: string

module SessionPhase =
  /// What the phase has to say about itself: warmup progress, or why it faulted.
  let statusMessage = function
    | Initializing msg -> msg
    | Faulted reason -> Some reason
    | Active _ -> None

  /// Derive the legacy SessionState for external consumers (MCP, dashboard, etc.)
  let toSessionState = function
    | Initializing _ -> SessionState.WarmingUp
    | Active (_, Idle) -> SessionState.Ready
    | Active (_, Evaluating) -> SessionState.Evaluating
    | Faulted _ -> SessionState.Faulted

  /// Extract the AppState when active, None otherwise.
  /// Narrow convenience for callers that genuinely don't need phase distinction.
  let tryAppState = function
    | Active (st, _) -> Some st
    | Initializing _ | Faulted _ -> None

type MiddlewareNext = EvalRequest * AppState -> EvalResponse * AppState
type Middleware = MiddlewareNext -> EvalRequest * AppState -> EvalResponse * AppState

type Command =
  | Eval of EvalRequest * CancellationToken * AsyncReplyChannel<EvalResponse>
  | CancelEval of AsyncReplyChannel<bool>
  | Autocomplete of text: string * caret: int * word: string * AsyncReplyChannel<list<AutoCompletion.CompletionItem>>
  | GetBoundValue of name: string * AsyncReplyChannel<obj Option>
  /// Pulled on demand, after the eval reply — never attached to it (roast-4
  /// #2). Serialized JSON of a Features.LiveValueTree.LiveValueSnapshot.
  | GetLiveValues of AsyncReplyChannel<string>
  | AddMiddleware of Middleware list * AsyncReplyChannel<unit>
  | GetDiagnostics of text: string * AsyncReplyChannel<Diagnostics.Diagnostic array>
  | GetTypeCheckWithSymbols of text: string * filePath: string * AsyncReplyChannel<Diagnostics.TypeCheckWithSymbolsResult>
  | GetSessionPhase of AsyncReplyChannel<SessionPhase>
  | GetSessionState of AsyncReplyChannel<SessionState>
  | GetStartupConfig of AsyncReplyChannel<StartupConfig option>
  | GetWarmupFailures of AsyncReplyChannel<WarmupFailure list>
  | EnableStdout
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

/// Internal command for the eval actor — only mutation/eval operations
type internal EvalCommand =
  | EvalRun of EvalRequest * CancellationTokenSource * AsyncReplyChannel<EvalResponse>
  /// Posted by the eval thread, which can outlive a reset: `generation` is the
  /// session incarnation the eval started on.
  | EvalFinished of result: Result<EvalResponse * AppState, exn> * sw: Diagnostics.Stopwatch * code: string * AsyncReplyChannel<EvalResponse> * generation: SessionGeneration
  | EvalAddMiddleware of Middleware list * AsyncReplyChannel<unit>
  | EvalEnableStdout
  | EvalReset of AsyncReplyChannel<Result<unit, SageFsError>>
  | EvalHardReset of rebuild: bool * AsyncReplyChannel<Result<string, SageFsError>>
  /// Serialized on the eval actor because reading the FSI session's bound
  /// values must not race a concurrent eval/reset — but it is no longer on
  /// the eval reply path, so it costs no eval its latency (roast-4 #2).
  | EvalGetLiveValues of AsyncReplyChannel<string>

/// Test-only fault-injection seam for the eval-actor resilience tests
/// (SageFs.Tests/EvalActorResilienceTests.fs). When set, the eval actor's
/// message-processing function runs it before handling each message; a
/// throw here escapes the handler exactly like an unexpected bug would.
/// Default (None) is a no-op, so production behavior is unchanged.
let mutable internal evalActorFaultInjector : (unit -> unit) option = None

let wrapErrorMiddleware next (request, st) =
  try
    next (request, st)
  with e ->
    // Carry a structured, serializable SageFsError through the exception-typed
    // channel (roast-5 §10) so agents get describe/suggestedAction instead of a
    // flattened ex.ToString(). Preserve an already-structured inner error rather
    // than double-wrapping it.
    let structuredError =
      match e with
      | :? SageFsErrorException -> e
      | _ -> SageFsErrorException(SageFsError.EvalFailed (sprintf "internal error: %s" e.Message)) :> exn
    let errResponse = {
      EvaluationResult = Error structuredError
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

/// The one seam between the real, IO-backed `SessionPhase` and
/// `EvalActorDecision.EvalPhase` (see SageFs.Core/EvalActorDecision.fs for
/// the pure decision core this narrows into — `decide`, `EvalInput`,
/// `EvalDecision`, and why that module is a standalone file rather than
/// nested here). The only place a live `AppState` is looked at is to read
/// the `SessionActivity` sitting next to it; it is never touched otherwise.
let phaseOf (phase: SessionPhase) : EvalActorDecision.EvalPhase =
  match phase with
  | Initializing _ -> EvalActorDecision.EvalPhase.Initializing
  | Active(_, activity) -> EvalActorDecision.EvalPhase.Active activity
  | Faulted _ -> EvalActorDecision.EvalPhase.Faulted

let evalFn (token: CancellationToken) =
  fun ({ Code = code }, st) ->
    // Capture Console.Out separately so we can reorder: val bindings first, stdout last
    let originalOut = Console.Out
    let stdoutCapture = new StringWriter()
    Console.SetOut(stdoutCapture)
    st.OutStream.StartRecording()
    let thread = Thread.CurrentThread
    token.Register(fun () -> thread.Interrupt()) |> ignore
    let evaluation = st.Session.Eval(code, token)
    let diagnostics = evaluation.Diagnostics

    let evalRes =
      match evaluation.Outcome with
      | FsiSession.FsiSucceeded ->
        let fsiOutput = st.OutStream.StopRecording()
        let stdout = stdoutCapture.ToString() |> cleanStdout
        let combined =
          match String.IsNullOrWhiteSpace stdout with
          | true -> fsiOutput
          | false -> sprintf "%s\n%s" fsiOutput stdout
        Ok combined
      | FsiSession.FsiFailed ex -> Error <| ex
      | FsiSession.FsiInterrupted -> Error <| (OperationCanceledException("the evaluation was interrupted") :> exn)

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
open SageFs.OpenReplay

/// Re-exported for backward compatibility — the tested surface used to live
/// here; the implementation now lives in `SageFs.OpenReplay` alongside the
/// rest of the pure open-replay decision core (see that file's header).
let internalTopLevelModuleFullNames = OpenReplay.internalTopLevelModuleFullNames

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
    // Every F# module reflected out of the solution's own project assemblies
    // (internal or public, nested or not). The source-scan
    // (extractOpensFromLines) collects `open X` lines verbatim from each .fs
    // file — including a file's own legal `open` of a module that is only
    // legal exactly where it's written (an internal top-level module, or a
    // nested module opened by its bare name) — and warmup must not replay
    // those into the FSI session, a separately loaded assembly where they
    // cannot resolve ("namespace not defined": non-fatal but user-visible
    // warmup noise, roast-7 F7 and roast-8). `resolveWarmupOpens` decides,
    // from these facts, which scraped names are safe to replay.
    let moduleFacts = System.Collections.Generic.List<ReflectedModuleFact>()
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
              // The assembly loaded, but one or more of its types could not
              // (a missing dependency, most commonly) — GetTypes() only
              // gives back the types that DID load; LoaderExceptions names
              // WHY the rest didn't, and the user needs that name, not a
              // silent partial namespace/module scan.
              let missingDependencies =
                ex.LoaderExceptions
                |> Array.choose (fun e -> if isNull e then None else Some e.Message)
                |> Array.distinct
              let msg =
                sprintf "Project assembly loaded only partially: %s — %d of its type(s) could not be loaded (%s). Run 'dotnet build' to restore any missing dependency; namespaces/modules from the unloaded types could not be auto-opened."
                  project.TargetPath
                  (ex.Types |> Array.filter isNull |> Array.length)
                  (match missingDependencies with
                   | [||] -> "reason unknown — check the SageFs log"
                   | names -> String.concat "; " names)
              Log.warn "[Warmup] %s" msg
              discoveryWarnings.Add(msg)
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

          // Record this assembly's module facts so source-scanned `open`s
          // that cannot resolve from the FSI session (internal top-level
          // modules — F7 — and nested modules, public or not) can be dropped
          // before replay via `resolveWarmupOpens`.
          moduleFacts.AddRange(reflectedModuleFacts types)

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

    // Drop opens that cannot possibly resolve from the FSI session — internal
    // top-level modules (roast-7 F7) and nested modules, public or not
    // (roast-8) — one decision instead of two parallel filters. This can only
    // ever DROP a name the reflection scan positively proved unresolvable;
    // anything it has no evidence against (a BCL/NuGet namespace, say) is
    // kept exactly as before. Dropped names are not warmup failures — they
    // were never attempted — so they get a debug trace, never a warning.
    let openResolution = resolveWarmupOpens (Seq.toList namesToOpen) (Seq.toList moduleFacts)

    for droppedName, reason in openResolution.Dropped do
      logger.LogDebug
        (sprintf "  Dropped source-scanned open '%s' before replay: %s" droppedName (DroppedOpenReason.describe reason))

    let namePairs =
      openResolution.Replayable
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
        (projectFilesForSolution originalSln)
        namePairs
        (Seq.toList discoveryWarnings)
  }

/// An eval as the (Choice, diagnostics) pair the warm-up code was written against.
let private evalAsChoice (session: FsiSession.IFsiSession) (code: string) (ct: CancellationToken) : Choice<unit, exn> * Diagnostics.Diagnostic array =
  let evaluation = session.Eval(code, ct)
  let outcome =
    match evaluation.Outcome with
    | FsiSession.FsiSucceeded -> Choice1Of2()
    | FsiSession.FsiFailed ex -> Choice2Of2 ex
    | FsiSession.FsiInterrupted -> Choice2Of2(OperationCanceledException "the evaluation was interrupted" :> exn)
  outcome, evaluation.Diagnostics

/// Creates a fresh FSI session with warm-up: loads startup files and opens namespaces.
/// The CancellationToken is passed through to FSI EvalInteraction calls so that
/// warm-up can be cancelled if it takes too long (e.g. a stuck module initializer).
let createFsiSession (kind: SessionKinds.FsiSessionKind) (logger: ILogger) (outStream: TextWriter) (useAsp: bool) (originalSln: Solution) (sln: Solution) (autoOpenNamespaces: bool) (hotReload: bool) (ct: CancellationToken) (onProgress: (int * int * string) -> unit) =
  async {
    let warmupStartedAt = System.DateTimeOffset.UtcNow
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let args = solutionToFsiArgs logger useAsp hotReload sln
    let replayArgs = solutionToFsiArgs logger useAsp hotReload originalSln
    let recorder = new TextWriterRecorder(outStream)

    logger.LogInfo (sprintf "  Creating FSI session (%A) with %d args..." kind (Array.length args))
    let fsiErrorWriter = new System.IO.StringWriter()
    // The session is behind the port from here on: everything below (base.fsx, startup files, the namespace
    // warm-up) is identical whether FSI lives in this process or in an isolated host.
    let! fsiSession =
      match kind with
      | SessionKinds.InProcess ->
        async {
          let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()
          let raw =
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
          return (new FsiSession.InProcessFsiSession(raw, SessionAgent.agentInitOf sln hotReload) :> FsiSession.IFsiSession)
        }
      | SessionKinds.Isolated ->
        async {
          let projects = sln.Projects |> List.map (fun p -> p.ProjectFileName)
          match! IsolatedFsiSession.start logger recorder (Array.toList args) System.Environment.CurrentDirectory projects (SessionAgent.agentInitOf sln hotReload) with
          | Ok session -> return session
          | Error reason ->
            let message = IsolatedFsiSession.describeStartError reason
            logger.LogError (sprintf "  ❌ Isolated FSI host failed to start: %s" message)
            return failwith message
        }
    logger.LogInfo (sprintf "  FSI session created in %dms, loading startup files..." sw.ElapsedMilliseconds)
    onProgress(1, 4, "FSI session created")


    // Chesterton's fence: evaluate the embedded base.fsx FIRST so the
    // feature-gate flags (_SageFsHotReload, _SageFsCompExpr) are bound before
    // any user code runs. The middleware gates read these via
    // Session.TryFindBoundValue; if they are never bound, hot-reload detouring
    // and computation-expression rewriting silently no-op (the P0 hot-reload
    // gap: HotReload sessions never detoured because _SageFsHotReload was
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
        FsiSession.evalOrThrow fsiSession baseConfig ct
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
        FsiSession.evalOrThrow fsiSession compatibleContents ct
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
    let toWarmupDiagnostics (diagnostics: Diagnostics.Diagnostic array) : WarmupFcsDiagnostic list =
      diagnostics
      |> Array.map (fun d ->
        { Message = d.Message
          Severity =
            match d.Severity with
            | Diagnostics.DiagnosticSeverity.Blocking -> "error"
            | Diagnostics.DiagnosticSeverity.Warning -> "warning"
            | Diagnostics.DiagnosticSeverity.Info
            | Diagnostics.DiagnosticSeverity.Hidden -> "info"
          ErrorNumber = d.ErrorNumber
          // The session port does not carry a file name; warm-up opens are evaluated from stdin anyway.
          FileName = None
          StartLine = d.Range.StartLine
          EndLine = d.Range.EndLine
          StartColumn = d.Range.StartColumn
          EndColumn = d.Range.EndColumn })
      |> Array.toList
    // The real "why" for a failed open lives in the diagnostics, not in
    // FSI's own generic exception message — see WarmUp.WarmupFcsDiagnostic.pickErrorMessage.
    let describeOpenFailure (ex: exn) (diagnostics: Diagnostics.Diagnostic array) =
      WarmupFcsDiagnostic.pickErrorMessage ex.Message (toWarmupDiagnostics diagnostics)
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
      let result, diagnostics = evalAsChoice fsiSession (sprintf "open %s;;" name) ct
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
          WarmUp.OpenFailed (describeOpenFailure ex diagnostics, toWarmupDiagnostics diagnostics, elapsed)
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
        let result, diagnostics = evalAsChoice fsiSession script ct
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
          let reason = describeOpenFailure ex diagnostics
          logger.LogDebug (sprintf "Batch open failed for %d namespaces/modules in %.1fms: %s" batch.Length elapsed reason)
          WarmUp.OpenFailed (reason, toWarmupDiagnostics diagnostics, elapsed)

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
          logger.LogWarning (sprintf "    %s" (WarmupFcsDiagnostic.formatLine d))
        match WarmupOpenFailure.suggestedAction f with
        | Some action -> logger.LogWarning (sprintf "    → %s" action)
        | None -> ()
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
    // The user's assemblies live in the session's process (the isolated host, normally), not necessarily in this one.
    let loadedAssemblyNames =
      match fsiSession.LoadedAssemblyNames() with
      | HostAgent.AgentAnswered names -> names
      | HostAgent.AgentUnavailable reason ->
        let msg = sprintf "Warmup verification failed: the session could not report what it loaded: %s" reason
        logger.LogError (sprintf "  ❌ %s" msg)
        failwith msg
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
          Name = WarmupOpenFailure.DiscoveryWarningName
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
      evalAsChoice fsiSession "open Microsoft.FSharp.Core.Operators;;" ct |> ignore
      evalAsChoice fsiSession "open Microsoft.FSharp.Core.ExtraTopLevelOperators;;" ct |> ignore
    | false -> ()

    return fsiSession, recorder, args, failed, warmupCtx
  }

/// Pipeline builder: takes middleware list + core eval function, returns composed pipeline.
/// Default is `buildPipeline`. Tracing module provides an instrumented alternative.
type PipelineBuildFn = Middleware list -> MiddlewareNext -> MiddlewareNext

let mkAppStateActor (sessionKind: SessionKinds.FsiSessionKind) (logger: ILogger) (initCustomData: Map<string, obj>) outStream useAsp (originalSln: Solution) (shadowDir: string option) (autoOpenNamespaces: bool) (hotReload: bool) (onEvent: Events.SageFsEvent -> unit) (pipelineBuildFn: PipelineBuildFn) (sln: Solution) =
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
          reply.Reply (SessionPhase.statusMessage snapshot.Phase)
          return snapshot
        | QueryAutocomplete(text, caret, word, reply) ->
          match snapshot.Phase with
          | Active (st, _) ->
            let res = st.Session.Completions(text, caret, word)
            reply.Reply res
            return snapshot
          | _ ->
            reply.Reply []
            return snapshot
        | QueryGetDiagnostics(text, reply) ->
          match snapshot.Phase with
          | Active (st, activity) ->
            let res = st.Session.Diagnose text
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
            let res = st.Session.TypeCheckWithSymbols(filePath, text)
            reply.Reply res
            return snapshot
          | _ ->
            reply.Reply { Diagnostics.TypeCheckWithSymbolsResult.Diagnostics = [||]; SymbolRefs = [] }
            return snapshot
        | QueryGetBoundValue(name, reply) ->
          match snapshot.Phase with
          | Active (st, _) ->
            st.Session.BoundValue name |> Option.ofObj |> reply.Reply
            return snapshot
          | _ ->
            reply.Reply None
            return snapshot
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
    // The session incarnation. Owned by this actor alone: the reset handlers
    // advance it, EvalRun stamps it on the eval thread's EvalFinished.
    let sessionGeneration = ref SessionGeneration.initial
    let supersededResponse (code: string) =
      let err = SageFsError.EvalSupersededByReset
      emit (Events.EvalFailed {| Code = code; Error = SageFsError.describe err; Diagnostics = [] |})
      { EvaluationResult = Error (SageFsErrorException err :> exn)
        Diagnostics = [||]
        EvaluatedCode = code
        Metadata = Map.empty }
    let processEvalCommand (phase: SessionPhase, middleware: Middleware list, evalStats: Affordances.EvalStats) (cmd: EvalCommand) : Async<SessionPhase * Middleware list * Affordances.EvalStats> =
      async {
        // Test-only fault-injection seam (None in production): a throw here
        // escapes the handler like an unexpected bug — the wrapped loop below
        // logs it, keeps the previous state, and the session stays alive.
        match evalActorFaultInjector with
        | Some fault -> fault ()
        | None -> ()

        match cmd with
        | EvalEnableStdout ->
          match phase with
          | Faulted _ ->
            logger.LogWarning "EnableStdout requested on faulted session; ignoring"
          | Initializing _ ->
            logger.LogWarning "EnableStdout requested during warmup; ignoring"
          | Active (st, _) ->
            st.OutStream.Enable()
          return (phase, middleware, evalStats)
        | EvalRun(request, cts, reply) ->
          match EvalActorDecision.decide sessionGeneration.Value (phaseOf phase) EvalActorDecision.EvalInput.Submit with
          | EvalActorDecision.EvalDecision.RejectEval err ->
            currentEvalCts.Value <- None
            let message =
              match err with
              | SageFsError.EvalFailed reason -> reason
              | other -> SageFsError.describe other
            let errResponse = {
              EvaluationResult = Error (SageFsErrorException err :> exn)
              Diagnostics = [||]
              EvaluatedCode = request.Code
              Metadata = Map.empty
            }
            emit (Events.EvalFailed {| Code = request.Code; Error = message; Diagnostics = [] |})
            reply.Reply errResponse
            return (phase, middleware, evalStats)
          | EvalActorDecision.EvalDecision.RunEval ->
            match phase with
            | Active (st, _) ->
              publishSnapshot st Evaluating evalStats
              let sw = System.Diagnostics.Stopwatch.StartNew()
              // Eval-to-pixel latency chain, stage 1/5 (vision §3.4, §7.4):
              // starts a new in-flight sample in THIS PROCESS's tracker. This
              // actor runs inside the FSI worker subprocess, not the daemon
              // — see EvalLatencyTrace's module doc for why that means this
              // stamp and the daemon-side ModelChanged/PushReceived/
              // MorphWritten stamps land in two separate `shared` instances
              // today, and why ModelChanged starts its own chain rather than
              // waiting for this one to arrive.
              EvalLatencyTrace.shared.StampRequested() |> ignore
              emit (Events.EvalRequested {| Code = request.Code; Source = Events.System |})
              let pipeline = pipelineBuildFn (wrapErrorMiddleware :: middleware) (evalFn cts.Token)
              let generation = sessionGeneration.Value
              // Run eval on a dedicated thread so the actor stays responsive
              // to CancelEval, HardReset, etc. while the eval is in progress.
              let evalThread = Thread(fun () ->
                try
                  let res, newSt = pipeline (request, st)
                  mailbox.Post(EvalFinished(Ok(res, newSt), sw, request.Code, reply, generation))
                with ex ->
                  mailbox.Post(EvalFinished(Error ex, sw, request.Code, reply, generation))
              )
              evalThread.IsBackground <- true
              evalThread.Name <- sprintf "sagefs-eval-%d" (evalStats.EvalCount + 1)
              currentEvalThread.Value <- Some evalThread
              evalThread.Start()
              return (Active (st, Evaluating), middleware, evalStats)
            | Initializing _ | Faulted _ ->
              // Unreachable: EvalActorDecision.decide's Submit arm only
              // returns RunEval for EvalPhase.Active — see `decide` above.
              // Kept exhaustive so the phase match is total.
              return (phase, middleware, evalStats)
          | EvalActorDecision.EvalDecision.ServeQuery
          | EvalActorDecision.EvalDecision.ApplyFinished
          | EvalActorDecision.EvalDecision.DropSupersededFinished
          | EvalActorDecision.EvalDecision.AdvanceGenerationAndReset ->
            // Unreachable: decide only returns these for Query/Cancel/
            // Finished/Reset inputs, never Submit. Kept exhaustive.
            return (phase, middleware, evalStats)
        | EvalFinished(_, sw, code, reply, generation)
            when EvalActorDecision.decide sessionGeneration.Value (phaseOf phase) (EvalActorDecision.EvalInput.Finished generation)
                 = EvalActorDecision.EvalDecision.DropSupersededFinished ->
          // Straggler: the eval thread outlived a reset that disposed the
          // session it ran on and put a fresh one in its place. Its AppState
          // wraps the disposed session — adopting it would bring that session
          // back and leak the fresh one — so the result is dropped and the
          // caller told why. currentEvalCts/Thread are left alone: they belong
          // to whatever eval runs on the fresh session now.
          sw.Stop()
          logger.LogWarning (sprintf "Discarding the result of an eval that outlived a reset (session state: %s)" (SessionPhase.toSessionState phase |> SessionState.label))
          reply.Reply (supersededResponse code)
          return (phase, middleware, evalStats)
        | EvalFinished(result, sw, code, reply, _) ->
          // ApplyFinished: EvalActorDecision.decide's Finished arm returned
          // ApplyFinished here (the generation matched) — see the guard above.
          sw.Stop()
          // Eval-to-pixel latency chain, stage 2/5: the eval actor received
          // the eval thread's result back on its own mailbox.
          EvalLatencyTrace.shared.StampFinished()
          currentEvalCts.Value <- None
          currentEvalThread.Value <- None
          match result with
          | Ok(res, newSt) ->
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
            // Live values (watch window) are no longer attached here — pulled
            // on demand via GetLiveValues, off this reply path (roast-4 #2).
            reply.Reply res
            return (Active (newSt, Idle), middleware, evalStats')
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
              return (Active (st, Idle), middleware, evalStats)
            | Initializing _ | Faulted _ ->
              // Unreachable: only a reset leaves the Active phase, and every
              // reset advances the generation, so an EvalFinished from before
              // it takes the straggler arm above. Kept so the match is total.
              reply.Reply errResponse
              return (phase, middleware, evalStats)
        | EvalAddMiddleware(additionalMiddleware, r) ->
          r.Reply(())
          return (phase, additionalMiddleware @ middleware, evalStats)
        | EvalGetLiveValues reply ->
          // Serialized with evals (it reads the FSI session's live bound
          // values), but off the eval REPLY path — pulled on demand after
          // the caller already has its eval result (roast-4 #2).
          match phase with
          | Active (st, _) ->
            reply.Reply (st.Session.LiveValuesJson liveValueGeneration)
          | Initializing _ | Faulted _ ->
            reply.Reply (WorkerProtocol.Serialization.serialize (Features.LiveValueTree.buildSnapshot "" 0L []))
          return (phase, middleware, evalStats)
        | EvalReset reply ->
          // decide's Reset arm always returns AdvanceGenerationAndReset,
          // regardless of phase — routed through it anyway so this call site
          // stays the single source of truth rather than a hand-inlined copy.
          match EvalActorDecision.decide sessionGeneration.Value (phaseOf phase) EvalActorDecision.EvalInput.Reset with
          | EvalActorDecision.EvalDecision.AdvanceGenerationAndReset ->
            sessionGeneration.Value <- SessionGeneration.next sessionGeneration.Value
          | _ -> () // unreachable: Reset always advances
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
            | Active (st, _) ->
              (st.Session :> System.IDisposable).Dispose()
            | _ -> ()
            let softResetCts = new CancellationTokenSource(Timeouts.softResetCancellation)
            let onProgress (s,t,msg) =
              emit (Events.SageFsEvent.SessionWarmUpProgress {| Step = s; Total = t; Message = msg |})
              publishPhase (Initializing (Some (sprintf "[%d/%d] %s" s t msg))) evalStats
            // StartupConfig is NOT a closure capture: it is built in init()
            // after warmup. When Active, read the live config so a reset
            // preserves the fields the live config genuinely carries — e.g.
            // AutoOpenNamespaces, HotReloadEnabled (via Workflow), and the
            // profile-loaded flag (StartupProfileLoaded); when Faulted
            // (StartupConfig was None in the tombstone) fall back to the same
            // defaults the old code used.
            let startupConfig =
              match phase with
              | Active (st, _) -> st.StartupConfig
              | Initializing _ | Faulted _ -> None
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
                sessionKind
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
            return (Active (newSt, Idle), middleware, evalStats)
          with ex ->
            logger.LogError $"❌ FSI session reset failed: {ex.Message}"
            let reason = sprintf "Session reset failed: %s" ex.Message
            publishPhase (Faulted reason) evalStats
            reply.Reply(Error (SageFsError.ResetFailed ex.Message))
            return (Faulted reason, middleware, evalStats)
        | EvalHardReset (rebuild, reply) ->
          // See EvalReset above: routed through decide for one source of truth.
          match EvalActorDecision.decide sessionGeneration.Value (phaseOf phase) EvalActorDecision.EvalInput.Reset with
          | EvalActorDecision.EvalDecision.AdvanceGenerationAndReset ->
            sessionGeneration.Value <- SessionGeneration.next sessionGeneration.Value
          | _ -> () // unreachable: Reset always advances
          try
            publishPhase (Initializing None) evalStats
            logger.LogInfo "🔨 Hard resetting FSI session..."
            let activeSt = SessionPhase.tryAppState phase
            // Context audit (same policy as EvalReset): StartupConfig lives on
            // the live st when Active and is None-equivalent when Faulted —
            // read it so a hard reset preserves LoadedProjects/Workflow/
            // AutoOpenNamespaces etc.; OriginalSolution/ShadowDir are the
            // closure captures exactly when Faulted (they matched the tombstone).
            let startupConfig =
              match phase with
              | Active (st, _) -> st.StartupConfig
              | Initializing _ | Faulted _ -> None
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
            | Active (st, _) ->
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
                let runBuild (restore: bool) =
                  // `dotnet` prints compile AND NETSDK errors on STDOUT, so both
                  // streams are captured — a stderr-only read drops the actual
                  // diagnostics and leaves a bare "Build failed (exit code 1)".
                  let args =
                    match restore with
                    | false -> sprintf "build \"%s\" --no-restore" projFile
                    | true  -> sprintf "build \"%s\"" projFile
                  let psi =
                    System.Diagnostics.ProcessStartInfo(
                      "dotnet",
                      args,
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
                  let outputLines = System.Collections.Generic.List<string>()
                  let addLine (l: string) = lock outputLines (fun () -> outputLines.Add(l))
                  let stderrTask = System.Threading.Tasks.Task.Run(fun () ->
                    let mutable line = proc.StandardError.ReadLine()
                    while not (isNull line) do
                      addLine line
                      lastActivity <- DateTime.UtcNow
                      line <- proc.StandardError.ReadLine())
                  let stdoutTask = System.Threading.Tasks.Task.Run(fun () ->
                    let mutable line = proc.StandardOutput.ReadLine()
                    while not (isNull line) do
                      addLine line
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
                    -1, [ sprintf "Build timed out (inactive for %ds or exceeded %d min limit)" (inactivityLimitMs / 1000) (maxTotalMs / 60_000) ]
                  | false ->
                    try System.Threading.Tasks.Task.WaitAll([| stderrTask; stdoutTask |], 5000) |> ignore with ex -> logger.LogDebug (sprintf "Build output wait failed: %s" ex.Message)
                    proc.ExitCode, (lock outputLines (fun () -> List.ofSeq outputLines))
                // The failure message a hard-reset surfaces: the actual compiler/
                // MSBuild diagnostics (each carries its own actionable wording),
                // never a bare exit code.
                let describeBuildFailure (exit: int) (output: string list) =
                  let errors =
                    SessionBuild.buildDiagnosticsOf output []
                    |> List.map (fun (d: BuildDiagnostic) -> d.Message)
                    |> String.concat "\n"
                  match errors.Trim() with
                  | "" -> sprintf "Build failed (exit code %d)." exit
                  | e -> sprintf "Build failed (exit code %d):\n%s" exit e
                // Fast path: no restore. Self-heal a fresh (NETSDK1004) or
                // package-changed project that needs a NuGet restore instead of
                // reporting the missing restore as a build failure.
                let! exit0, out0 = System.Threading.Tasks.Task.Run(fun () -> runBuild false) |> Async.AwaitTask
                let! exitCode, output =
                  match exit0 <> 0 && SessionBuild.buildOutputNeedsRestore out0 with
                  | true ->
                    logger.LogInfo "  Restore needed — retrying build with a NuGet restore..."
                    async {
                      let! r = System.Threading.Tasks.Task.Run(fun () -> runBuild true) |> Async.AwaitTask
                      return r }
                  | false -> async { return exit0, out0 }
                match exitCode <> 0 with
                | true ->
                  let joined = String.concat "\n" output
                  match joined.Contains("denied") || joined.Contains("locked") with
                  | true ->
                    logger.LogWarning "  ⚠️ DLL lock detected, retrying after GC..."
                    GC.Collect()
                    GC.WaitForPendingFinalizers()
                    GC.Collect()
                    do! Async.Sleep 500
                    let! retryCode, retryOut = System.Threading.Tasks.Task.Run(fun () -> runBuild false) |> Async.AwaitTask
                    match retryCode <> 0 with
                    | true ->
                      // Mid-function exit to the handler's existing `with ex ->
                      // Hard reset failed` recovery below (identical Faulted
                      // publish + error reply + Faulted-phase continuation).
                      raise (System.Exception (describeBuildFailure retryCode retryOut))
                    | false ->
                      logger.LogInfo "  ✅ Build succeeded on retry"
                  | false ->
                    raise (System.Exception (describeBuildFailure exitCode output))
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
                      sessionKind
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
              publishPhase (Faulted msg) evalStats
              reply.Reply(Error (SageFsError.HardResetFailed msg))
              return (Faulted msg, middleware, evalStats)
            | Ok (newSession, newRecorder, _, warmupFailures, warmupCtx) ->
            warmupCts.Dispose()
            let newSt =
              match activeSt with
              | Some st ->
                // Live path: preserve Custom/HotReloadState/StartupConfig and
                // swap the session-bearing fields.
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
            return (Active (newSt, Idle), middleware, evalStats)
          with ex ->
            logger.LogError (sprintf "❌ Hard reset failed: %s" ex.Message)
            let reason = sprintf "Hard reset failed: %s" ex.Message
            publishPhase (Faulted reason) evalStats
            reply.Reply(Error (SageFsError.HardResetFailed ex.Message))
            return (Faulted reason, middleware, evalStats)
      }

    let init () =
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
              sessionKind
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
            // Registers with the file-watcher's own agent so AppHolds learns
            // what a #load'ed app calls (evalFn used to skip middleware).
            let evalFn code =
              FsiSession.evalOrThrow fsiSession code CancellationToken.None
              let detours = match hotReload with true -> HostAgent.DetourPolicy.ApplyDetours | false -> HostAgent.DetourPolicy.RegisterOnly
              fsiSession.AfterEval
                { EvaluatedCode = code; Detours = detours; Discovery = HostAgent.DiscoveryPolicy.WhenChanged; IsFileSave = false }
              |> ignore
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
              Workflow = WorkflowTypes.SessionWorkflow.fromHotReloadBool hotReload
              AutoOpenNamespaces = autoOpenNamespaces
              AspireDetected = useAsp
              StartupTimestamp = DateTime.UtcNow
              StartupProfileLoaded = startupProfileResult
            }
          }

          let evalStats = Affordances.EvalStats.empty
          publishSnapshot st Idle evalStats
          return (Active (st, Idle), [], evalStats)
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
          publishPhase (Faulted msg) Affordances.EvalStats.empty
          return (Faulted msg, [], Affordances.EvalStats.empty)
      }

    let safeProcessEval = ResilientActor.wrapLoop logger "eval-actor" processEvalCommand
    let rec loop state = async {
      let! cmd = mailbox.Receive()
      let! state' = safeProcessEval state cmd
      return! loop state'
    }
    async {
      let! initialState = init ()
      return! loop initialState
    }
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
        | GetLiveValues reply ->
          // Must serialize with evals (reads the live FSI session), so this
          // goes to the eval actor, not the query actor — but it is a pull
          // the caller issues after its own eval reply, so it never delays one.
          evalActor.Post(EvalGetLiveValues reply)

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
    SessionPhase.statusMessage snap.Phase
  // The session's agent lives where its user's code lives; a session that is not active has none to ask.
  let sessionAgent =
    SessionAgent.ofCurrentSession (fun () ->
      match (System.Threading.Volatile.Read(&latestSnapshot)).Phase with
      | Active (st, _) -> st.Session
      | _ -> null)
  let cancelCurrentEval () =
    actor.PostAndAsyncReply(fun reply -> CancelEval reply)
    |> Async.StartAsTask

  actor, diagnosticsChangedEvent.Publish, cancelCurrentEval, getSessionState, getEvalStats, getWarmupFailures, getWarmupContext, getStartupConfig, getStatusMessage, sessionAgent
