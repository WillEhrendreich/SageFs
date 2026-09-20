module SageFs.Middleware.HotReloading

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices

open SageFs.ProjectLoading
open SageFs.Utils
open SageFs.AppState
open SageFs.DevReload
open SageFs.Features.LiveTesting
open SageFs.Middleware.HotReloadCore

// Chesterton's fence: ConcurrentDictionary instead of ResizeArray because
// assembly resolve callbacks fire on arbitrary CLR threads — a concurrent read
// during mkReloadingState's writes on a plain List<T> would corrupt the array.
// Using Keys as the iterable in resolveAssembly gives snapshot-safe enumeration.
let assemblySearchPaths = Collections.Concurrent.ConcurrentDictionary<string, byte>()

let resolveAssembly (args: ResolveEventArgs) =
  let assemblyName = AssemblyName(args.Name)
  let name = assemblyName.Name

  // Chesterton's fence: check already-loaded assemblies FIRST, before touching disk.
  // Assembly.LoadFrom uses the LoadFrom binding context, which can conflict with
  // assemblies already in the default context — causing FileLoadException even when
  // the file on disk has the correct version. This happens when Harmony's JIT hook
  // triggers assembly resolution for assemblies the host already loaded.
  // Returning the already-loaded instance avoids the context conflict entirely.
  let alreadyLoaded =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.tryFind (fun a ->
      try a.GetName().Name = name with _ -> false)

  match alreadyLoaded with
  | Some asm -> asm
  | None ->

  let dllName = name + ".dll"

  // Chesterton's fence: inspect assembly version metadata from the file BEFORE loading.
  // Assembly.LoadFrom commits the assembly to the AppDomain permanently — loading all
  // candidates then sorting would pollute the AppDomain with N assemblies when only one
  // is needed. AssemblyName.GetAssemblyName reads PE metadata without loading.
  assemblySearchPaths.Keys
  |> Seq.choose (fun searchPath ->
    let fullPath = Path.Combine(searchPath, dllName)
    match File.Exists(fullPath) with
    | true ->
      try
        let candidateName = AssemblyName.GetAssemblyName(fullPath)
        match assemblyName.Version with
        | null -> Some (fullPath, candidateName.Version)
        | requestedVersion ->
          match candidateName.Version >= requestedVersion with
          | true -> Some (fullPath, candidateName.Version)
          | false ->
            Log.debug "Assembly %s version %O < requested %O, skipping %s"
              assemblyName.Name candidateName.Version requestedVersion searchPath
            None
      with ex ->
        Log.warn "Failed to inspect assembly at %s: %s" fullPath ex.Message
        None
    | false -> None)
  |> Seq.sortByDescending snd
  |> Seq.tryHead
  |> Option.map (fun (path, _) ->
    try Assembly.LoadFrom(path)
    with :? FileLoadException ->
      // Chesterton's fence: the native CLR binder rejected LoadFrom because it already
      // tracks this assembly (from the host's deps.json) at a different version. This
      // happens when user projects reference newer/older versions of the same packages
      // as SageFs (e.g., SageFs has OTel 1.14.0, user project has OTel 1.15.0).
      // Fall back to loading by simple name — the CLR will provide whatever version it
      // has from its own probing paths, giving automatic version unification.
      // Reentrancy-safe: the CLR won't re-enter AssemblyResolve for the same assembly.
      try Assembly.Load(name)
      with _ ->
        // Last resort: load from byte array to bypass native binder identity tracking.
        // This avoids the path-based version check entirely. Load PDB sidecar if
        // available so stack traces retain source line numbers.
        try
          let pdbPath = Path.ChangeExtension(path, ".pdb")
          match File.Exists(pdbPath) with
          | true -> Assembly.Load(File.ReadAllBytes(path), File.ReadAllBytes(pdbPath))
          | false -> Assembly.Load(File.ReadAllBytes(path))
        with _ -> null)
  |> Option.defaultValue null

// Chesterton's fence: Interlocked.CompareExchange instead of ref bool.
// Assembly resolve callbacks fire on arbitrary CLR threads. A plain ref read/write
// has a TOCTOU race — two threads could both read 0 before either writes 1,
// causing double-registration. CompareExchange is atomic.
let private resolverRegistered = ref 0

let setupAssemblyResolver () =
  match System.Threading.Interlocked.CompareExchange(resolverRegistered, 1, 0) = 0 with
  | true -> AppDomain.CurrentDomain.add_AssemblyResolve (ResolveEventHandler(fun _ args -> resolveAssembly args))
  | false -> ()

let registerSearchPath (path: string) =
  let dir = Path.GetDirectoryName(path)
  assemblySearchPaths.TryAdd(dir, 0uy) |> ignore

let mkReloadingState (sln: SageFs.ProjectLoading.Solution) =
  // Setup assembly resolver once
  setupAssemblyResolver ()

  // Register all project output directories for dependency resolution
  sln.Projects |> List.iter (fun p -> registerSearchPath p.TargetPath)

  // Register NuGet package directories so transitive dependencies resolve at runtime
  sln.Projects
  |> List.iter (fun p ->
    p.PackageReferences |> List.iter (fun pr -> registerSearchPath pr.FullPath)
    // Also register framework/SDK DLL directories from OtherOptions -r: args
    p.OtherOptions
    |> List.filter (fun s ->
      s.StartsWith("-r:", System.StringComparison.Ordinal)
      && s.EndsWith(".dll", System.StringComparison.Ordinal))
    |> List.iter (fun s -> registerSearchPath (s.Substring(3))))

  let results =
    sln.Projects
    |> List.map (fun p -> AssemblyLoadError.loadAssembly p.TargetPath)

  let assemblies =
    results |> List.choose (fun r -> match r with Ok a -> Some a | _ -> None)

  let loadErrors =
    results |> List.choose (fun r -> match r with Error e -> Some e | _ -> None)

  match List.isEmpty loadErrors with
  | false ->
    loadErrors |> List.iter (fun e -> Log.logWarn $"%s{AssemblyLoadError.describe e}")
  | true -> ()

  // getAllMethods now handles all reflection errors internally
  let allMethods = assemblies |> List.collect getAllMethods

  let methods =
    allMethods
    |> List.groupBy (fun m -> m.MethodInfo.Name)
    |> List.map (fun (methodName, methods) -> methodName, methods)
    |> Map.ofList

  {
    Methods = methods
    LastOpenModules = []
    LastAssembly = None
    ProjectAssemblies = assemblies
    AssemblyLoadErrors = loadErrors
    LiveTestInit = LiveTestInit.Pending
  }

/// The hot-reload state of a session that has loaded nothing.
let emptyReloadingState : State =
  { Methods = Map.empty
    LastOpenModules = []
    LastAssembly = None
    ProjectAssemblies = []
    AssemblyLoadErrors = []
    LiveTestInit = LiveTestInit.Pending }

let hotReloadingInitFunction (sln: SageFs.ProjectLoading.Solution) : string * obj =
  try
    "hotReload", box (mkReloadingState sln)
  with ex ->
    Log.logWarn $"HotReloading initialization failed: %s{ex.Message}"
    "hotReload", box emptyReloadingState

/// The init for an ISOLATED session. The user's code runs in the FSI host, so the worker must load nothing of the
/// user's: no project assemblies (mkReloadingState would load every project's output into this process) and no
/// AssemblyResolve handler over the user's package directories. Loading those here is precisely the conflict
/// surface isolation exists to remove. Hot reload and live testing get their state from the host agent instead.
let isolatedInitFunction (_solution: SageFs.ProjectLoading.Solution) : string * obj =
  "hotReload", box emptyReloadingState

[<Literal>]
let hotReloadKey = "hotReload"

/// Typed read from AppState.Custom — single cast site for hot-reload state.
let getReloadingState (st: AppState) =
  AppStateCustom.tryGetFeature<State> hotReloadKey st
  |> Option.defaultWith (fun () -> mkReloadingState st.Solution)

/// Typed write of hot-reload state into AppState.Custom.
let setReloadingState (value: State) (st: AppState) : AppState =
  AppStateCustom.set hotReloadKey value st

/// Detect top-level function bindings (not value bindings).
/// Function bindings have parameters between the name and '=':
///   let f () = ...      → function (unit param)
///   let f x y = ...     → function (named params)
///   let f (x: int) = .. → function (typed params)
///   let x = 42          → value (no params)
///   let h : Type = ...  → value (type annotation, no params)
///
/// Chesterton's fence: accepts INDENTED function bindings too (module-member
/// functions like `  let greeting () = ...` inside `module Greeting =`).
/// The hot-reload pipeline transforms module-declared files by indenting the
/// module body, so the detour target `greeting` is indented. Requiring column-0
/// meant module-nested functions never got NoInlining, the JIT inlined them
/// into the route closure, and Harmony had nothing to detour — the running app
/// kept serving the old value (P0 hot-reload gap). Local `let f x =` inside a
/// function body is also a method and getting NoInlining is harmless.
let isTopLevelFunctionBinding (line: string) =
  let trimmed = line.TrimStart()
  match not (trimmed.StartsWith("let ", System.StringComparison.Ordinal)) || trimmed.StartsWith("let!", System.StringComparison.Ordinal) with
  | true -> false
  | false ->
    let mutable s = trimmed.Substring(4).TrimStart()
    for m in ["private "; "internal "; "public "; "inline "; "rec "; "mutable "] do
      match s.StartsWith(m, System.StringComparison.Ordinal) with
      | true -> s <- s.Substring(m.Length).TrimStart()
      | false -> ()
    match s.IndexOf('=') with
    | -1 -> false
    | eqIdx ->
      let beforeEq = s.Substring(0, eqIdx).Trim()
      beforeEq.Contains("(") || (beforeEq.Contains(" ") && not (beforeEq.Contains(":")))

/// Detect static member method definitions (not properties).
/// The F# compiler inlines simple static member bodies at the IL level,
/// eliminating the call instruction entirely and making Harmony detours invisible.
let isStaticMemberFunction (line: string) =
  let trimmed = line.TrimStart()
  trimmed.StartsWith("static member ", System.StringComparison.Ordinal) &&
    let afterKw = trimmed.Substring("static member ".Length).TrimStart()
    match afterKw.IndexOf('('), afterKw.IndexOf('=') with
    | parenIdx, eqIdx when parenIdx >= 0 && (eqIdx < 0 || parenIdx < eqIdx) -> true
    | _, eqIdx when eqIdx > 0 ->
      let beforeEq = afterKw.Substring(0, eqIdx).Trim()
      beforeEq.Contains(" ") && not (beforeEq.Contains(":"))
    | _ -> false

/// Strip `let` keyword + modifiers, returning the remaining text after the name.
/// Used by multi-line binding detection to identify function signatures that span lines.
/// Accepts indented bindings (module-member functions) — see
/// isTopLevelFunctionBinding for why column-0 is not required.
let private startsLetBinding (line: string) =
  let trimmed = line.TrimStart()
  match trimmed.StartsWith("let ", System.StringComparison.Ordinal)
        && not (trimmed.StartsWith("let!", System.StringComparison.Ordinal)) with
  | false -> None
  | true ->
    let mutable s = trimmed.Substring(4).TrimStart()
    for m in ["private "; "internal "; "public "; "inline "; "rec "; "mutable "] do
      match s.StartsWith(m, System.StringComparison.Ordinal) with
      | true -> s <- s.Substring(m.Length).TrimStart()
      | false -> ()
    Some s

/// Detect multi-line function bindings where params span multiple lines:
///   let handler
///       (ctx: HttpContext)
///       (next: RequestDelegate) =
///       task { ... }
/// Scans up to 5 lines forward from a `let` line to find the `=`.
let isMultiLineFunctionBinding (lines: string[]) (idx: int) : bool =
  match startsLetBinding lines.[idx] with
  | None -> false
  | Some afterName ->
    match afterName.Contains("=") with
    | true -> false // single-line — handled by isTopLevelFunctionBinding
    | false ->
      let maxLookahead = 5
      let mutable found = false
      let mutable combined = afterName
      let mutable i = idx + 1
      while i < lines.Length && i <= idx + maxLookahead && not found do
        let nextLine = lines.[i].TrimStart()
        combined <- combined + " " + nextLine
        match nextLine.Contains("=") with
        | true -> found <- true
        | false -> ()
        i <- i + 1
      match found with
      | false -> false
      | true ->
        match combined.IndexOf('=') with
        | -1 -> false
        | eqIdx ->
          let beforeEq = combined.Substring(0, eqIdx).Trim()
          beforeEq.Contains("(") || (beforeEq.Contains(" ") && not (beforeEq.Contains(":")))

/// Classification of a binding for hot-reload purposes.
type BindingKind =
  | FunctionBinding     // let f x = ...
  | ValueBinding        // let x = 42
  | StaticMemberMethod  // static member F x = ...
  | MultiLineFunction   // let f\n  (x: int)\n  (y: int) = ...
  | Unknown             // not a binding line

/// Classify a source line (in context of surrounding lines) into a BindingKind.
let classifyBinding (lines: string[]) (idx: int) : BindingKind =
  let line = lines.[idx]
  match isStaticMemberFunction line with
  | true -> StaticMemberMethod
  | false ->
    match isTopLevelFunctionBinding line with
    | true -> FunctionBinding
    | false ->
      match isMultiLineFunctionBinding lines idx with
      | true -> MultiLineFunction
      | false ->
        let trimmed = line.TrimStart()
        match trimmed.StartsWith("let ", System.StringComparison.Ordinal)
              && not (trimmed.StartsWith("let!", System.StringComparison.Ordinal))
              && line = line.TrimStart() with
        | true -> ValueBinding
        | false -> Unknown

/// Whether a binding kind needs [<MethodImpl(NoInlining)>] for Harmony detours.
let needsNoInlining (kind: BindingKind) =
  match kind with
  | FunctionBinding | StaticMemberMethod | MultiLineFunction -> true
  | ValueBinding | Unknown -> false

/// Inject [<MethodImpl(MethodImplOptions.NoInlining)>] on top-level function bindings
/// (including multi-line signatures) and static member methods so Harmony detours work.
/// Without this, the F# compiler inlines simple static member bodies at the IL level,
/// and the JIT may inline short let-binding functions — both make Harmony's
/// entry-point detour invisible to callers.
let injectNoInlining (code: string) =
  let lines = code.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
  let injectionLines =
    match CompilationContext.noInliningTargets code with
    | CompilationContext.SyntaxTargets targets -> targets
    | CompilationContext.UnparsedFragment ->
      lines
      |> Array.mapi (fun idx _ -> idx, classifyBinding lines idx)
      |> Array.choose (fun (idx, kind) ->
        match needsNoInlining kind with
        | true -> Some idx
        | false -> None)
      |> Set.ofArray
  match injectionLines.IsEmpty with
  | true -> code
  | false ->
    // Chesterton's fence: skip injecting when the binding ALREADY carries a
    // [<MethodImpl(NoInlining)>] attribute on the line(s) directly above it.
    // User source files (like the hot-reload verification fixture) may declare
    // NoInlining themselves so the startup #load is not inlined into the route
    // closure; injecting a SECOND attribute on the watcher's re-eval makes FSI
    // fail with "MethodImplAttribute has AllowMultiple=false" — the save then
    // errors instead of hot-reloading (P0 gap).
    let isLineDirective (line: string) =
      let t = line.TrimStart()
      (t.Length > 2 && t.StartsWith("# ", StringComparison.Ordinal) && Char.IsDigit t.[2])
      || t.StartsWith("#line ", StringComparison.Ordinal)
    // Blank lines and line directives may sit between a binding and its attributes.
    let hasExistingAttribute (idx: int) =
      let mutable j = idx - 1
      let mutable found = false
      while j >= 0 && not found do
        let t = lines.[j].Trim()
        match t with
        | "" -> j <- j - 1
        | _ when isLineDirective lines.[j] -> j <- j - 1
        | _ ->
          if t.StartsWith("[<MethodImpl", StringComparison.Ordinal)
             || t.StartsWith("[<System.Runtime.CompilerServices.MethodImpl", StringComparison.Ordinal) then
            found <- true
          else
            j <- -1 // stop at the first non-blank, non-attribute line
      found
    // A line directive numbers the next physical line, so an attribute goes above
    // the directive; otherwise every line after it would be reported one too late.
    let insertionPoint (idx: int) =
      match idx > 0 && isLineDirective lines.[idx - 1] with
      | true -> idx - 1
      | false -> idx
    let attributeAt =
      injectionLines
      |> Seq.filter (hasExistingAttribute >> not)
      |> Seq.map (fun idx -> insertionPoint idx, lines.[idx].Length - lines.[idx].TrimStart().Length)
      |> Map.ofSeq
    let sb = System.Text.StringBuilder()
    sb.Append("open System.Runtime.CompilerServices\n") |> ignore
    for i in 0 .. lines.Length - 1 do
      match Map.tryFind i attributeAt with
      | Some indent ->
        sb.Append(System.String(' ', indent) + "[<MethodImpl(MethodImplOptions.NoInlining)>]\n") |> ignore
      | None -> ()
      sb.Append(lines.[i] + "\n") |> ignore
    sb.ToString()

let hotReloadingMiddleware next (request, st: AppState) =
  let sessionAvailable = not (isNull (box st.Session))
  let hotReloadFlagEnabled =
    match sessionAvailable with
    | true ->
      match st.Session.ReadFlag "_SageFsHotReload" with
      | SageFs.FsiSession.FlagBound true -> true
      | _ -> false
    | false -> false

  let shouldTriggerReload (m: Map<string, obj>) =
    match hotReloadFlagEnabled, Map.tryFind "hotReload" m with
    | _, Some v when v = true -> true
    | true, None -> true
    | _ -> false

  // Only inject NoInlining attributes when hot-reload is enabled.
  // Without this gate, every eval gets unnecessary IL modifications.
  let request =
    match hotReloadFlagEnabled with
    | true -> { request with Code = injectNoInlining request.Code }
    | false -> request

  let response, st = next (request, st)

  // Always accumulate method registrations so live testing can discover tests.
  // Only apply Harmony detours when hot-reload is explicitly enabled.
  match response.EvaluationResult with
  | Error _ -> response, st
  | Ok _ ->
    match isNull (box st.Session) with
    | true -> response, st
    | false ->
      match st.Session.DynamicAssemblies |> Array.tryLast with
      | None -> response, st
      | Some asm ->
        let reloadingSt, updatedMethods =
          getReloadingState st
          |> getOpenModules response.EvaluatedCode
          |> handleNewAsmFromRepl st.Logger hotReloadFlagEnabled asm

        match shouldTriggerReload request.Args && not (List.isEmpty updatedMethods) with
        | true -> triggerReload()
        | false -> ()

        // Live testing hook: discover tests and detect providers.
        // Skip discovery when no methods were updated (expression-only evals)
        // unless this is the first eval where we need initial test discovery,
        // or the caller explicitly forces a rediscovery scan (e.g. the daemon
        // asking eval-time discovery to catch a brand-new [<Tests>] value that
        // detoured no existing method — see live-testing-asyoutype-plan.md
        // Brief 2). The force flag only WIDENS scanning; it never changes the
        // result for a submission with no [<Tests>] value in it.
        let needsInitialScan = reloadingSt.LiveTestInit = LiveTestInit.Pending && not (List.isEmpty reloadingSt.ProjectAssemblies)
        let forceRediscover =
          match Map.tryFind "liveTestRediscover" request.Args with
          | Some v when v = box true -> true
          | _ -> false
        let hookResult, reloadingSt =
          match not (List.isEmpty updatedMethods) || needsInitialScan || forceRediscover with
          | true ->
            let fsiHookResult =
              SageFs.Features.LiveTesting.LiveTestingHook.afterReload
                SageFs.Features.LiveTesting.BuiltInExecutors.builtIn
                asm
                updatedMethods

            // On first eval, also scan pre-built project assemblies for tests.
            match needsInitialScan with
            | true ->
              let projectResults =
                reloadingSt.ProjectAssemblies
                |> List.map (fun projAsm ->
                  try
                    SageFs.Features.LiveTesting.LiveTestingHook.afterReload
                      SageFs.Features.LiveTesting.BuiltInExecutors.builtIn
                      projAsm
                      []
                  with _ -> SageFs.Features.LiveTesting.LiveTestHookResult.empty)
              let allResults = fsiHookResult :: projectResults
              let composedRunTest =
                let runTests = allResults |> List.map (fun r -> r.RunTest)
                fun (tc: SageFs.Features.LiveTesting.TestCase) ->
                  let rec tryRunners remaining =
                    async {
                      match remaining with
                      | [] -> return SageFs.Features.LiveTesting.TestResult.NotRun
                      | rt :: rest ->
                        let! result = rt tc
                        match result with
                        | SageFs.Features.LiveTesting.TestResult.NotRun -> return! tryRunners rest
                        | found -> return found
                    }
                  tryRunners runTests
              let merged =
                { SageFs.Features.LiveTesting.LiveTestHookResult.empty with
                    DetectedProviders =
                      allResults
                      |> List.collect (fun r -> r.DetectedProviders)
                      |> List.distinctBy (fun p ->
                        match p with
                        | SageFs.Features.LiveTesting.ProviderDescription.AttributeBased a -> a.Name
                        | SageFs.Features.LiveTesting.ProviderDescription.Custom c -> c.Name)
                    DiscoveredTests =
                      allResults
                      |> List.map (fun r -> r.DiscoveredTests)
                      |> Array.concat
                    AffectedTestIds = fsiHookResult.AffectedTestIds
                    RunTest = composedRunTest }
              merged, { reloadingSt with LiveTestInit = LiveTestInit.Done }
            | false ->
              fsiHookResult, reloadingSt
          | false ->
            SageFs.Features.LiveTesting.LiveTestHookResult.empty, reloadingSt

        let metadata =
          match shouldTriggerReload request.Args with
          | true -> response.Metadata.Add("reloadedMethods", updatedMethods)
          | false -> response.Metadata
        let metadata = metadata.Add("liveTestHookResult", SageFs.Features.LiveTesting.LiveTestHookResultDto.fromResult hookResult)
        let metadata = metadata.Add("liveTestRunTest", hookResult.RunTest)
        let metadata =
          match List.isEmpty reloadingSt.AssemblyLoadErrors with
          | false -> metadata.Add("assemblyLoadErrors", reloadingSt.AssemblyLoadErrors)
          | true -> metadata

        { response with Metadata = metadata },
        setReloadingState reloadingSt st
