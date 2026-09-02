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

type Method = {
  MethodInfo: MethodInfo
  FullName: string
} with

  static member make modulePath (m: MethodInfo) = {
    MethodInfo = m
    FullName = m.Name :: modulePath |> Seq.rev |> String.concat "."
  }

/// Whether initial live-test discovery has been performed.
[<RequireQualifiedAccess>]
type LiveTestInit =
  | Pending
  | Done

type State = {
  Methods: Map<string, Method list>
  LastOpenModules: string list
  LastAssembly: Assembly Option
  ProjectAssemblies: Assembly list
  AssemblyLoadErrors: AssemblyLoadError list
  LiveTestInit: LiveTestInit
}

type Event =
  | NewReplAssemblies of Assembly array
  | ModuleOpened of string

let getAllMethods (asm: Assembly) =
  let rec getMethods currentPath (t: Type) =
    try
      // Chesterton's fence: never add an FSI_* type name to the path — not for
      // the type's own methods AND not when descending into nested types. The
      // old recursion added t.Name to the CHILD path unconditionally
      // (`getMethods (t.Name :: currentPath)`), so a nested module under the
      // FSI root class (FSI_0007+WebAppFixture+Greeting) registered as
      // FSI_0007.WebAppFixture.Greeting.greeting — a name that never
      // EndsWith-matches the #load'd top-level WebAppFixture.Greeting.greeting,
      // so the route's captured method was never detoured.
      let pathForChildren =
        match t.Name.Contains "FSI_" with
        | true -> currentPath
        | false -> t.Name :: currentPath

      let methods =
        t.GetMethods()
        |> Array.filter (fun m -> m.IsStatic && not <| m.IsGenericMethod)
        |> Array.map (Method.make pathForChildren)
        |> Array.toList

      let nestedTypes =
        try
          t.GetNestedTypes() |> Array.toList
        with _ -> []

      let nestedMethods = nestedTypes |> List.collect (getMethods pathForChildren)
      methods @ nestedMethods
    with ex ->
      // Skip this specific type but continue with others
      []

  // Try to get types, handling partial failures
  let types =
    try
      asm.GetTypes() |> Array.toList
    with
    | :? ReflectionTypeLoadException as ex ->
      // Some types failed to load, but we can use the ones that succeeded
      let loadedTypes = ex.Types |> Array.filter (fun t -> not (isNull t)) |> Array.toList

      match loadedTypes.Length > 0 with
      | true ->
        Log.logWarn
          $"Assembly %s{asm.GetName().Name} has types with missing dependencies - loaded %d{loadedTypes.Length} types, skipped %d{ex.Types.Length - loadedTypes.Length}"
      | false ->
        Log.logWarn $"Could not load any types from assembly %s{asm.GetName().Name} - all types have missing dependencies"

      loadedTypes
    | :? System.IO.FileNotFoundException as ex ->
      Log.logWarn $"Assembly %s{asm.GetName().Name} is missing a dependency: %s{ex.Message}"
      []
    | :? System.IO.FileLoadException as ex ->
      Log.logWarn $"Assembly %s{asm.GetName().Name} has a load error: %s{ex.Message}"
      []
    | :? System.BadImageFormatException as ex ->
      Log.logWarn $"Assembly %s{asm.GetName().Name} has a bad format: %s{ex.Message}"
      []
    | ex ->
      Log.logWarn $"Failed to get types from assembly %s{asm.GetName().Name}: %s{ex.Message}"
      []

  // Only process exported/public types. Nested types are handled by their
  // parent's recursion (getMethods walks GetNestedTypes), so skip them in the
  // top-level iteration — processing them twice produced duplicate/spurious
  // FullNames (e.g. `Greeting.greeting` alongside `WebAppFixture.Greeting.greeting`
  // for the same method) that confused the detour matcher.
  let topLevelTypes =
    types
    |> List.filter (fun t -> not t.IsNested && (t.IsPublic || t.IsNestedPublic))

  /// Chesterton's fence: FSI represents a `namespace Foo.Bar` file's types as
  /// top-level types whose FULL name is `FSI_0042.Foo.Bar.Greeting` but whose
  /// t.Name is just `Greeting` and t.Namespace is `FSI_0042.Foo.Bar`. Building
  /// the path from t.Name alone drops the `Foo.Bar` namespace, so a `#load`'d
  /// module registers as `Greeting.greeting` while a hot-reload re-eval (which
  /// wraps in `module Foo.Bar =`) produces `Foo.Bar.Greeting.greeting`. The
  /// longer name never EndsWith-matches the shorter registered name, so no
  /// detour ever fires and the running app keeps the old closure (P0 gap).
  /// Seed the path with the namespace segments (minus any FSI_ prefix) so both
  /// sides register the same qualified name.
  let seedPath (t: Type) : string list =
    match t.Namespace with
    | null | "" -> []
    | ns ->
      ns.Split('.')
      |> Array.filter (fun seg -> not (seg.Contains "FSI_"))
      |> Array.toList

  topLevelTypes
  |> List.collect (fun t -> getMethods (seedPath t) t)

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

let hotReloadingInitFunction (sln: SageFs.ProjectLoading.Solution) : string * obj =
  try
    "hotReload", box (mkReloadingState sln)
  with ex ->
    Log.logWarn $"HotReloading initialization failed: %s{ex.Message}"

    "hotReload",
    box {
      Methods = Map.empty
      LastOpenModules = []
      LastAssembly = None
      ProjectAssemblies = []
      AssemblyLoadErrors = []
      LiveTestInit = LiveTestInit.Pending
    }

[<Literal>]
let hotReloadKey = "hotReload"

/// Typed read from AppState.Custom — single cast site for hot-reload state.
let getReloadingState (st: AppState) =
  AppStateCustom.tryGetFeature<State> hotReloadKey st
  |> Option.defaultWith (fun () -> mkReloadingState st.Solution)

/// Typed write of hot-reload state into AppState.Custom.
let setReloadingState (value: State) (st: AppState) : AppState =
  AppStateCustom.set hotReloadKey value st

open HarmonyLib

/// Outcome of the post-detour canary check.
type CanaryResult =
  | DetourConfirmed
  | BytesUnchanged
  | CanaryError of exn

/// Number of leading native code bytes to snapshot for canary comparison.
[<Literal>]
let private canarySnapshotBytes = 16

/// On x64 .NET, JIT-compiled methods often have a fixed-address precode stub:
///   FF 25 xx xx xx xx  =  JMP [rip + disp32]
/// The stub jumps through an indirect slot to the actual JIT-compiled code.
/// MonoMod patches the first bytes of the JIT code (writing an E9 JMP trampoline)
/// but leaves the stub, the slot value, and the function pointer unchanged.
/// To detect the detour, we must follow the indirection and read bytes at the
/// actual JIT code address.
let private resolveJitCodeAddress (fnPtr: nativeint) : nativeint =
  try
    let header = Array.zeroCreate<byte> 2
    System.Runtime.InteropServices.Marshal.Copy(fnPtr, header, 0, 2)
    match header.[0], header.[1] with
    | 0xFFuy, 0x25uy ->
      // JMP [rip+disp32]: read the 4-byte displacement, compute slot address,
      // then read the slot to get the actual JIT code address
      let dispBytes = Array.zeroCreate<byte> 4
      System.Runtime.InteropServices.Marshal.Copy(fnPtr + 2n, dispBytes, 0, 4)
      let disp = System.BitConverter.ToInt32(dispBytes, 0)
      let slotAddr = fnPtr + 6n + nativeint disp
      System.Runtime.InteropServices.Marshal.ReadIntPtr(slotAddr)
    | _ ->
      // Not a JMP stub — the function pointer IS the JIT code
      fnPtr
  with _ ->
    fnPtr

/// Snapshot the leading bytes at the resolved JIT code address of a method.
/// Returns the JIT code address and the byte snapshot.
let snapshotMethodState (method: MethodBase) : (nativeint * byte[]) option =
  try
    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle)
    let fnPtr = method.MethodHandle.GetFunctionPointer()
    let jitAddr = resolveJitCodeAddress fnPtr
    let buf = Array.zeroCreate<byte> canarySnapshotBytes
    System.Runtime.InteropServices.Marshal.Copy(jitAddr, buf, 0, canarySnapshotBytes)
    Some (jitAddr, buf)
  with _ -> None

/// Validate that a detour changed the JIT code bytes at the resolved address.
/// Re-reads bytes at the same JIT code address captured before the detour.
let validateDetourCanary (jitAddr: nativeint) (preBytes: byte[]) : CanaryResult =
  try
    let postBytes = Array.zeroCreate<byte> preBytes.Length
    System.Runtime.InteropServices.Marshal.Copy(jitAddr, postBytes, 0, preBytes.Length)
    match postBytes <> preBytes with
    | true -> DetourConfirmed
    | false -> BytesUnchanged
  with ex ->
    CanaryError ex

let detourMethod (logger: ILogger) (method: MethodBase) (replacement: MethodBase) =
  try
    // Snapshot pre-detour observable state for canary validation
    let preSnapshot = snapshotMethodState method

    typeof<Harmony>.Assembly
    |> _.GetTypes()
    |> Array.find (fun t -> t.Name = "PatchTools")
    |> fun x -> x.GetDeclaredMethods()
    |> Seq.find (fun n -> n.Name = "DetourMethod")
    |> fun x -> x.Invoke(null, [| method; replacement |])
    |> ignore

    // Post-patch canary: verify the detour actually took effect
    match preSnapshot with
    | Some (jitAddr, preBytes) ->
      match validateDetourCanary jitAddr preBytes with
      | DetourConfirmed ->
        logger.LogDebug (sprintf "Canary confirmed: detour for %s is active" method.Name)
      | BytesUnchanged ->
        let msg =
          sprintf "Canary warning: native code unchanged after detour for %s — patch may be ineffective"
            method.Name
        logger.LogWarning msg
        DevReloadHealthTracker.transition
          (DevReloadHealth.Degraded (sprintf "Canary: bytes unchanged for %s" method.Name))
      | CanaryError ex ->
        logger.LogWarning (sprintf "Canary validation error for %s: %s" method.Name ex.Message)
    | None ->
      logger.LogDebug (sprintf "Canary skipped: could not snapshot pre-detour bytes for %s" method.Name)
  with
  | :? TargetInvocationException as ex when
    (ex.InnerException :? PlatformNotSupportedException) ->
    // MonoMod does not yet support .NET 11+ CoreCLR — transition to Degraded
    let msg = sprintf "Hot-reload detour failed: PlatformNotSupportedException for %s. MonoMod may not support this runtime." method.Name
    logger.LogWarning msg
    DevReloadHealthTracker.transition (DevReloadHealth.Degraded "MonoMod PlatformNotSupportedException")
  | :? TargetInvocationException as ex when
    (ex.InnerException :? TypeLoadException) ->
    // FSI compilation units can become unloadable when types are redefined across
    // eval boundaries (FSI_0020 etc). This is benign — the new definition supersedes
    // the old one, so the detour is unnecessary. Log and continue.
    logger.LogDebug (sprintf "Hot-reload detour skipped (stale FSI type): %s — %s" method.Name ex.InnerException.Message)
  | :? TargetInvocationException as ex when
    (ex.InnerException :? TypeInitializationException) ->
    logger.LogDebug (sprintf "Hot-reload detour skipped (type init failure): %s — %s" method.Name ex.InnerException.Message)

let handleNewAsmFromRepl (logger: ILogger) (hotReloadEnabled: bool) (asm: Assembly) (st: State) =
  // Chesterton's fence: the `prev = asm` dedup only applies to NON-dynamic
  // assemblies. In WebLive the FSI session runs with --multiemit- (single
  // assembly mode): EVERY eval lands in the SAME persistent FSI-ASSEMBLY, so
  // object identity is constant and the old check made the middleware
  // early-return after the first eval — no hot-reload re-eval was ever
  // processed, no detour ever fired (P0 hot-reload gap). Dynamic assemblies
  // must always be processed; the method merge is idempotent (Map.add
  // overwrites) and the detour matcher pairs old->new per eval.
  match st.LastAssembly with
  | Some prev when prev = asm && not asm.IsDynamic -> st, []
  | _ ->
    // Compute getAllMethods once — used for both method merge and hot-reload matching.
    // getAllMethods has internal try/catch for ReflectionTypeLoadException so this is safe.
    let newMethods = getAllMethods asm

    // Merge new assembly's methods into Methods so future evals can patch functions
    // defined in FSI (not in project DLLs). Without this, only project-DLL methods
    // are ever patchable; FSI-first-defined functions are invisible to Harmony.
    // This merge is safe (no .ParameterType access) and needed for live testing
    // discovery regardless of hot-reload state.
    let newMethodsByName =
      newMethods
      |> List.groupBy (fun m -> m.MethodInfo.Name)
      |> List.map (fun (name, methods) -> name, methods)
      |> Map.ofList

    let mergedMethods =
      newMethodsByName |> Map.fold (fun acc k v -> Map.add k v acc) st.Methods

    // Chesterton's fence: replacementPairs computation accesses .ParameterType and
    // .ReturnType on MethodInfo — these trigger TypeLoadException when parameters
    // reference types from older FSI compilation units that were redefined.
    // This MUST be gated behind hotReloadEnabled. Without this gate, every normal
    // REPL eval (define type in block 1, use it in block 2) triggers the exception.
    let replacementPairs =
      match hotReloadEnabled with
      | false -> []
      | true ->
        newMethods
        |> Seq.collect (fun newMethod ->
          Map.tryFind newMethod.MethodInfo.Name st.Methods
          |> Option.map (fun existing ->
            existing
            |> Seq.filter (fun existingMethod ->
              // Chesterton's fence: .ParameterType/.ReturnType can throw
              // TypeLoadException when FSI redefines a type across compilation
              // units. Even with the hotReloadEnabled gate, this can happen for
              // hot-reload workflows that redefine types. Catch and skip gracefully.
              try
                let getParams m =
                  m.MethodInfo.GetParameters() |> Array.map _.ParameterType

                // Chesterton's fence: only detour USER module functions, not
                // FSI bookkeeping. In single-assembly mode the accumulated
                // assembly contains REPL temp accessors (get_it, set_it,
                // get_asm, ...) and the framework's own methods; detouring
                // those is collateral damage — patching FSI's `it` accessor
                // corrupted the running session. A detourable method must have
                // a dotted module path (WebAppFixture.Greeting.greeting) and a
                // name that is not an FSI temp-value accessor.
                let isDetourable (m: Method) =
                  let name = m.MethodInfo.Name
                  let full = m.FullName
                  full.Contains(".")
                  && not (name.StartsWith("get_", StringComparison.Ordinal) && full.StartsWith("get_", StringComparison.Ordinal))
                  && not (name = "get_it" || name = "set_it" || name = "get_asm")

                // Chesterton's fence: never detour a method onto itself. In
                // WebLive's --multiemit- single-assembly mode the FSI assembly
                // ACCUMULATES every eval's methods, and st.Methods (merged
                // from prior evals) contains the very method being considered
                // as "new" — pairing it with itself makes MonoMod throw
                // "Cannot detour a method to itself!" and the eval crashes.
                existingMethod.MethodInfo <> newMethod.MethodInfo
                && isDetourable newMethod
                && getParams existingMethod = getParams newMethod
                && existingMethod.MethodInfo.ReturnType = newMethod.MethodInfo.ReturnType
                && existingMethod.FullName.EndsWith(newMethod.FullName, StringComparison.Ordinal)
              with
              | :? TypeLoadException as ex ->
                logger.LogDebug(
                  sprintf "Hot-reload param comparison skipped (TypeLoadException): %s — %s"
                    newMethod.FullName ex.Message)
                false)
            // Chesterton's fence: detour EVERY distinct older method with a
            // matching signature onto the newest, not just the best-scoring
            // one. In single-assembly FSI mode each re-eval leaves the prior
            // eval's method in the accumulated assembly AND in st.Methods, and
            // the running app's route closure may have captured ANY of them
            // (the #load's FSI_0005, a prior re-eval's FSI_0007, ...). Picking
            // only the "best" left the actually-captured method undetoured and
            // the app kept serving the old value. Detouring all of them is
            // safe (Harmony just rewrites each entry point to the newest).
            |> Seq.map (fun oldMethod -> oldMethod, newMethod))
          |> Option.defaultValue Seq.empty)
        |> Seq.toList

    // Apply Harmony detours — already gated by replacementPairs being [] when disabled.
    for methodToReplace, newMethod in replacementPairs do
      logger.LogDebug <| "Updating method " + methodToReplace.FullName
      detourMethod logger methodToReplace.MethodInfo newMethod.MethodInfo

    { st with LastAssembly = Some asm; Methods = mergedMethods },
    List.map (fst >> _.FullName) replacementPairs

let getOpenModules (replCode: string) st =
  let modules =
    replCode.Split([| " "; "\n" |], System.StringSplitOptions.None)
    |> Seq.filter ((<>) "")
    |> Seq.chunkBySize 2
    |> Seq.filter (fun arr -> arr.Length >= 2)
    |> Seq.filter (Array.tryHead >> Option.map ((=) "open") >> Option.defaultValue false)
    |> Seq.map (fun arr -> arr[1])
    |> Seq.toList

  {
    st with
        LastOpenModules = (modules @ st.LastOpenModules) |> List.distinct
  }

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
    let hasExistingAttribute (idx: int) =
      let mutable j = idx - 1
      let mutable found = false
      while j >= 0 && not found do
        let t = lines.[j].Trim()
        match t with
        | "" -> j <- j - 1
        | _ ->
          if t.StartsWith("[<MethodImpl", StringComparison.Ordinal)
             || t.StartsWith("[<System.Runtime.CompilerServices.MethodImpl", StringComparison.Ordinal) then
            found <- true
          else
            j <- -1 // stop at the first non-blank, non-attribute line
      found
    let sb = System.Text.StringBuilder()
    sb.Append("open System.Runtime.CompilerServices\n") |> ignore
    for i in 0 .. lines.Length - 1 do
      match Set.contains i injectionLines && not (hasExistingAttribute i) with
      | true ->
        let line = lines.[i]
        let indent = line.Length - line.TrimStart().Length
        let prefix = System.String(' ', indent)
        sb.Append(prefix + "[<MethodImpl(MethodImplOptions.NoInlining)>]\n") |> ignore
      | false -> ()
      sb.Append(lines.[i] + "\n") |> ignore
    sb.ToString()

let hotReloadingMiddleware next (request, st: AppState) =
  let sessionAvailable = not (isNull (box st.Session))
  let hotReloadFlagEnabled =
    match sessionAvailable with
    | true ->
      match st.Session.TryFindBoundValue "_SageFsHotReload" with
      | Some fsiBoundValue when fsiBoundValue.Value.ReflectionValue = true -> true
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
        // unless this is the first eval where we need initial test discovery.
        let needsInitialScan = reloadingSt.LiveTestInit = LiveTestInit.Pending && not (List.isEmpty reloadingSt.ProjectAssemblies)
        let hookResult, reloadingSt =
          match not (List.isEmpty updatedMethods) || needsInitialScan with
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
