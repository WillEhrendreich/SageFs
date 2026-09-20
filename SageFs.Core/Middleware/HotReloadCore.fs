/// The process-local core of hot reload: the registry of methods FSI has emitted, reflection over an assembly,
/// the detour plan, and the Harmony detour itself. It knows nothing of AppState or project loading: it acts on the
/// assemblies of the process it runs in, so it is compiled into the worker (in-process sessions) AND the isolated
/// FSI host (where the user's code actually lives). The middleware glue stays in HotReloading.fs.
module SageFs.Middleware.HotReloadCore

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open SageFs.Utils
open SageFs.DevReload
open SageFs.Features.LiveTesting

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
  /// The path is innermost-first (Method.make reverses it), so the segments are too.
  let seedPath (t: Type) : string list =
    match t.Namespace with
    | null | "" -> []
    | ns ->
      ns.Split('.')
      |> Array.filter (fun seg -> not (seg.Contains "FSI_"))
      |> Array.rev
      |> Array.toList

  topLevelTypes
  |> List.collect (fun t -> getMethods (seedPath t) t)


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

/// Pairs each older method with a same-named, compatible method offered by the
/// evaluated assembly: the older entry point gets detoured onto the newer one.
/// Every distinct older copy is paired, not just the best match: in
/// --multiemit- mode a running app's closure may hold any prior eval's copy.
/// A method is never paired with itself (MonoMod: "Cannot detour a method to
/// itself!"). Targets are only methods first defined by THIS eval: the
/// accumulated assembly re-offers every older copy on each eval, and pairing
/// two older copies both ways made their entry points jump to each other
/// forever (100% CPU, unsuspendable). Older copies only ever point at strictly
/// newer code, so detours cannot form a cycle.
let planDetours
  (isNewThisEval: Method -> bool)
  (compatible: Method -> Method -> bool)
  (newMethods: Method list)
  (existing: Map<string, Method list>)
  : (Method * Method) list =
  newMethods
  |> List.filter isNewThisEval
  |> List.collect (fun newMethod ->
    match Map.tryFind newMethod.MethodInfo.Name existing with
    | None -> []
    | Some candidates ->
      candidates
      |> List.filter (fun old -> old.MethodInfo <> newMethod.MethodInfo && compatible old newMethod)
      |> List.map (fun old -> old, newMethod))

let private compatibleForDetour (logger: ILogger) (existingMethod: Method) (newMethod: Method) =
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

    isDetourable newMethod
    && getParams existingMethod = getParams newMethod
    && existingMethod.MethodInfo.ReturnType = newMethod.MethodInfo.ReturnType
    && existingMethod.FullName.EndsWith(newMethod.FullName, StringComparison.Ordinal)
  with
  | :? TypeLoadException as ex ->
    logger.LogDebug(
      sprintf "Hot-reload param comparison skipped (TypeLoadException): %s — %s"
        newMethod.FullName ex.Message)
    false

let handleNewAsmFromRepl (logger: ILogger) (hotReloadEnabled: bool) (asm: Assembly) (st: State) =
  // Chesterton's fence: the `prev = asm` dedup only applies to NON-dynamic
  // assemblies. In HotReload the FSI session runs with --multiemit- (single
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
        let known =
          Collections.Generic.HashSet<MethodInfo>(st.Methods |> Map.toSeq |> Seq.collect snd |> Seq.map _.MethodInfo)
        planDetours (fun m -> not (known.Contains m.MethodInfo)) (compatibleForDetour logger) newMethods st.Methods

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

