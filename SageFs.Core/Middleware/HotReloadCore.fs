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
open SageFs.Middleware.EntryProbes
open SageFs.Features.LiveTesting

type Method = {
  MethodInfo: MethodInfo
  FullName: string
} with

  static member make modulePath (m: MethodInfo) = {
    MethodInfo = m
    FullName = m.Name :: modulePath |> Seq.rev |> String.concat "."
  }

/// What `State.AppHolds` files a method under: its name for a module function, and the type's name and
/// its own for an instance member, so a `Render` of one class and a `Render` of another (or of a module)
/// are not the same entry.
let holdKey (m: MethodInfo) : string =
  match m.IsStatic with
  | true -> m.Name
  | false -> m.DeclaringType.Name + "." + m.Name

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
  /// The MethodInfo each name resolved to the last time a NON-file-save eval
  /// ran (the startup/init script, or an interactive eval) rather than a
  /// file-watcher-triggered save — i.e. the copy whatever code that eval
  /// defined or ran is holding, such as a route closure a handler table
  /// captured. Read at detour time to know whether a redirect reaches the
  /// copy the app ACTUALLY calls, instead of inferring it from assembly kind
  /// (see `handleNewAsmFromRepl`).
  AppHolds: Map<string, MethodInfo>
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
      // `FsiNaming.isDynamicModuleSegment`, not `Contains "FSI_"`.
      //
      // `Contains` is the compiler's rule plus collateral damage: it also
      // swallows any USER type whose name merely contains those four
      // characters — `MyFSI_Helpers`, `Acme.FSI_Adapters` — dropping that
      // segment from the detour path. The re-eval's qualified name then never
      // matches the registered one, no detour fires, and hot reload silently
      // stops working for that module with no diagnostic at all.
      // `isDynamicModuleSegment` mirrors `TryStripPrefixPath`
      // (CheckDeclarations.fs:274-281): the prefix must START the segment and
      // everything after it must be digits.
      let pathForChildren =
        match SageFs.FsiNaming.isDynamicModuleSegment t.Name with
        | true -> currentPath
        | false -> t.Name :: currentPath

      // Generic methods are registered too, though never detoured (`compatibleForDetour` refuses them): a
      // save that edits one has to be able to name it, and it cannot be named if it was never seen.
      let staticMethods =
        t.GetMethods()
        |> Array.filter (fun m -> m.IsStatic)
        |> Array.map (Method.make pathForChildren)
        |> Array.toList

      // The instance members a class declares itself. Not the ones it inherits (every type "has" ToString),
      // not a struct's (`this` is a byref there), not a generic type's, and not the ones the compiler wrote
      // for an F# record or union (Equals, GetHashCode, CompareTo): those are not code the user edits.
      let instanceMethods =
        match t.IsClass && not t.IsGenericTypeDefinition with
        | false -> []
        | true ->
          t.GetMethods(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.DeclaredOnly)
          |> Array.filter (fun m ->
            not m.IsAbstract
            && not (m.IsDefined(typeof<CompilerGeneratedAttribute>, false)))
          |> Array.map (Method.make pathForChildren)
          |> Array.toList

      let methods = staticMethods @ instanceMethods

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
      // Same rule, same reason as `pathForChildren` above: a `Contains` here
      // drops a user namespace segment like `Acme.FSI_Adapters` from the
      // qualified name and silently disables hot reload for everything in it.
      |> Array.filter (fun seg -> not (SageFs.FsiNaming.isDynamicModuleSegment seg))
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

/// What one call to `detourMethod` actually did to the running process.
///
/// It used to return `unit`, so a detour that threw was indistinguishable from
/// one that landed, and the caller reported BOTH as "reloaded". That is the same
/// class of lie `ReloadOutcome` exists to stop, one layer down.
[<RequireQualifiedAccess>]
type DetourApplied =
  /// The entry point now jumps to the new code.
  | Redirected
  /// Harmony accepted the patch but the native code bytes did not change. The
  /// method is still counted as redirected (the canary is a warning signal, not
  /// a verdict) but the reason is carried so a caller can say so.
  | Ineffective of reason: string
  /// The old copy is unreachable anyway — a stale FSI compilation unit whose
  /// type will not load, or one whose initializer already failed. The new
  /// definition supersedes it, so no detour is needed and none is missing.
  | Superseded of reason: string
  /// The detour did not happen and the old code is still live.
  | Failed of reason: string

let detourMethod (logger: ILogger) (method: MethodBase) (replacement: MethodBase) : DetourApplied =
  try
    // Chesterton's fence: rule 2's value-read watches are Harmony patches, and
    // a later Harmony unpatch on this method would rewrite its entry and put
    // the OLD code back over this detour. Take them off first.
    ValueReadTracking.releaseBeforeDetour method
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
        DetourApplied.Redirected
      | BytesUnchanged ->
        let msg =
          sprintf "Canary warning: native code unchanged after detour for %s — patch may be ineffective"
            method.Name
        logger.LogWarning msg
        DevReloadHealthTracker.transition
          (DevReloadHealth.Degraded (sprintf "Canary: bytes unchanged for %s" method.Name))
        DetourApplied.Ineffective (sprintf "native code unchanged after the detour for %s" method.Name)
      | CanaryError ex ->
        logger.LogWarning (sprintf "Canary validation error for %s: %s" method.Name ex.Message)
        DetourApplied.Redirected
    | None ->
      logger.LogDebug (sprintf "Canary skipped: could not snapshot pre-detour bytes for %s" method.Name)
      DetourApplied.Redirected
  with
  | :? TargetInvocationException as ex when
    (ex.InnerException :? PlatformNotSupportedException) ->
    // MonoMod does not yet support .NET 11+ CoreCLR — transition to Degraded
    let msg = sprintf "Hot-reload detour failed: PlatformNotSupportedException for %s. MonoMod may not support this runtime." method.Name
    logger.LogWarning msg
    DevReloadHealthTracker.transition (DevReloadHealth.Degraded "MonoMod PlatformNotSupportedException")
    DetourApplied.Failed (sprintf "MonoMod cannot patch %s on this runtime" method.Name)
  | :? TargetInvocationException as ex when
    (ex.InnerException :? TypeLoadException) ->
    // FSI compilation units can become unloadable when types are redefined across
    // eval boundaries (FSI_0020 etc). This is benign — the new definition supersedes
    // the old one, so the detour is unnecessary. Log and continue.
    logger.LogDebug (sprintf "Hot-reload detour skipped (stale FSI type): %s — %s" method.Name ex.InnerException.Message)
    DetourApplied.Superseded (sprintf "stale FSI type for %s" method.Name)
  | :? TargetInvocationException as ex when
    (ex.InnerException :? TypeInitializationException) ->
    logger.LogDebug (sprintf "Hot-reload detour skipped (type init failure): %s — %s" method.Name ex.InnerException.Message)
    DetourApplied.Superseded (sprintf "type initializer for %s already failed" method.Name)
  | ex ->
    // Chesterton's fence, inverted: there was NO catch-all here, so an unexpected
    // failure propagated out of the per-method loop and took the whole eval's
    // remaining detours with it — leaving the app half-reloaded with no report.
    logger.LogWarning (sprintf "Hot-reload detour failed for %s: %s" method.Name ex.Message)
    DetourApplied.Failed (sprintf "%s: %s" method.Name ex.Message)

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

// ── Accessor pairs ────────────────────────────────────────────────────────────
//
// A module-level `let mutable x` does NOT compile to a field load at the use
// site: F# emits a `get_x`/`set_x` pair of static methods over a backing field
// on a separate synthetic type, and every ordinary read and write in user code —
// including inside a closure captured into a startup route table — goes through
// them. That is why a detour reaches mutable module state at all.
//
// It is also why half a redirect is worse than none. MEASURED on this machine
// (net10.0 linux-x64, Debug, 400k warming iterations, the same
// PatchTools.DetourMethod used below), re-pointing one leg at a re-evaluated
// copy of the module:
//
//   both legs     read NEW field, write NEW field   — coherent
//   getter only   read NEW field, write OLD field   — every write is LOST
//   setter only   read OLD field, write NEW field   — every write is LOST
//
// Nothing throws, nothing logs, and the value simply refuses to change. So the
// plan is built so a half-moved binding cannot be expressed: the getter and the
// setter of one binding live in ONE record that cannot hold a getter without a
// setter, and a binding that could not form a complete pair is declined by name.

/// Which leg of a binding's accessor pair a method is, named by the QUALIFIED
/// binding (`Demo.App.Config.port`) so two modules' same-named bindings are
/// never confused for each other.
[<RequireQualifiedAccess>]
type AccessorRole =
  | Getter of binding: string
  | Setter of binding: string
  | Plain

let accessorRole (m: Method) : AccessorRole =
  let name = m.MethodInfo.Name
  let qualify (bare: string) =
    match m.FullName.LastIndexOf '.' with
    | -1 -> bare
    | dot -> m.FullName.Substring(0, dot + 1) + bare
  // Only a MODULE's accessors are the pair of one mutable binding. An instance property's accessors are
  // members of an object, which a patch re-points like any other member.
  match m.MethodInfo.IsStatic, name.StartsWith("get_", StringComparison.Ordinal), name.StartsWith("set_", StringComparison.Ordinal) with
  | false, _, _ -> AccessorRole.Plain
  | true, true, _ -> AccessorRole.Getter(qualify (name.Substring 4))
  | true, _, true -> AccessorRole.Setter(qualify (name.Substring 4))
  | _ -> AccessorRole.Plain

/// The qualified bindings the RUNNING code can write to. It is the running
/// code's setters that matter: those are the writes that would start landing in
/// a field nobody reads.
let settableBindingsOf (existing: Map<string, Method list>) : Set<string> =
  existing
  |> Map.toSeq
  |> Seq.collect snd
  |> Seq.choose (fun m ->
    match accessorRole m with
    | AccessorRole.Setter binding -> Some binding
    | AccessorRole.Getter _
    | AccessorRole.Plain -> None)
  |> Set.ofSeq

/// Which leg was left without a partner. Both directions tear identically; the
/// distinction is kept because it tells the user what changed about their code
/// (a `let mutable` that became a `let`, or the reverse).
[<RequireQualifiedAccess>]
type OrphanedLeg =
  | GetterWithoutSetter
  | SetterWithoutGetter

type DeclinedBinding = {
  Binding: string
  Orphan: OrphanedLeg
}

module OrphanedLeg =
  let describe =
    function
    | OrphanedLeg.GetterWithoutSetter ->
      "only its getter could be re-pointed; its setter had no counterpart in the new code"
    | OrphanedLeg.SetterWithoutGetter ->
      "only its setter could be re-pointed; its getter had no counterpart in the new code"

/// Both legs of one mutable binding, and never fewer. The head-and-rest shape is
/// the point: there is no way to construct this record with an empty getter list
/// or an empty setter list, so "redirected the getter but not the setter" is not
/// a state the planner can hand to the applier.
type AccessorPairDetour = {
  Binding: string
  FirstGetter: Method * Method
  MoreGetters: (Method * Method) list
  FirstSetter: Method * Method
  MoreSetters: (Method * Method) list
} with

  member this.Getters = this.FirstGetter :: this.MoreGetters
  member this.Setters = this.FirstSetter :: this.MoreSetters
  /// Every (older, newer) pair this binding must move as one.
  member this.Legs = this.Getters @ this.Setters

/// Everything one eval will do to the running process, separated so the applier
/// cannot treat an accessor as an ordinary method by accident.
type DetourPlan = {
  Functions: (Method * Method) list
  MutableBindings: AccessorPairDetour list
  Declined: DeclinedBinding list
}

[<RequireQualifiedAccess>]
type private PairRole =
  | MutableGetter of binding: string
  | MutableSetter of binding: string
  | PlainFunction

/// Groups raw `planDetours` pairs so that every accessor of a settable binding
/// is either part of a complete pair or declined — never applied alone.
///
/// A getter whose binding has no setter in the running code is an ordinary
/// value accessor (`let x = ...`): there is no writer anywhere, so there is
/// nothing to tear, and it is redirected like any other method.
let planDetourUnits (settable: Set<string>) (pairs: (Method * Method) list) : DetourPlan =
  let roleOf (older: Method, _) =
    match accessorRole older with
    | AccessorRole.Getter binding when Set.contains binding settable -> PairRole.MutableGetter binding
    | AccessorRole.Setter binding when Set.contains binding settable -> PairRole.MutableSetter binding
    | AccessorRole.Getter _
    | AccessorRole.Setter _
    | AccessorRole.Plain -> PairRole.PlainFunction

  let tagged = pairs |> List.map (fun pair -> roleOf pair, pair)

  let functions =
    tagged
    |> List.choose (function
      | PairRole.PlainFunction, pair -> Some pair
      | _ -> None)

  let legs role =
    tagged
    |> List.choose (fun (r, pair) ->
      match role r with
      | Some binding -> Some(binding, pair)
      | None -> None)

  let getters =
    legs (function
      | PairRole.MutableGetter b -> Some b
      | _ -> None)

  let setters =
    legs (function
      | PairRole.MutableSetter b -> Some b
      | _ -> None)

  let forBinding (xs: (string * (Method * Method)) list) binding =
    xs |> List.filter (fst >> (=) binding) |> List.map snd

  let units, declined =
    (getters @ setters)
    |> List.map fst
    |> List.distinct
    |> List.fold
      (fun (units, declined) binding ->
        match forBinding getters binding, forBinding setters binding with
        | g :: gs, s :: ss ->
          units
          @ [ { Binding = binding
                FirstGetter = g
                MoreGetters = gs
                FirstSetter = s
                MoreSetters = ss } ],
          declined
        | _ :: _, [] -> units, declined @ [ { Binding = binding; Orphan = OrphanedLeg.GetterWithoutSetter } ]
        | [], _ :: _ -> units, declined @ [ { Binding = binding; Orphan = OrphanedLeg.SetterWithoutGetter } ]
        | [], [] -> units, declined)
      ([], [])

  { Functions = functions
    MutableBindings = units
    Declined = declined }

/// What applying one mutable binding's pair did.
[<RequireQualifiedAccess>]
type BindingOutcome =
  /// Reads and writes both moved. The running process is coherent.
  | BothLegsRedirected of binding: string
  /// Neither leg was touched, so the running process still reads and writes the
  /// SAME field it always did. Nothing is lost; the edit just did not land.
  | NeitherLegRedirected of binding: string * reason: string
  /// One leg moved and another did not. This is the state the whole design
  /// exists to prevent, so it is reported as loudly as it can be rather than
  /// swallowed: from here on, writes to this binding disappear.
  | Torn of binding: string * reason: string

/// Why a change the planner thought it could patch cannot be, known only at the
/// moment the detours are planned: the compiled shapes are in front of the host
/// then, and nowhere before.
[<RequireQualifiedAccess>]
type DetourRefusal =
  /// A lambda changed in a way the closures already built cannot take.
  | ClosureShapeChanged of declaration: string * detail: string
  /// An instance member's type has different fields than the one the app built its
  /// objects from.
  | InstanceLayoutChanged of typeName: string * detail: string
  /// A generic function: only the instantiations that already ran could be reached.
  | GenericFunction of declaration: string

/// Everything one eval did, in the vocabulary a user-facing outcome needs.
type DetourReport = {
  /// Full names of the older methods whose entry points now jump to new code.
  Redirected: string list
  /// The TRUE evidence, not a proxy: the subset of `Redirected` whose
  /// re-pointed OLD entry point is the exact `MethodInfo` `State.AppHolds`
  /// recorded for that name — the copy the running app is actually known to
  /// call, tracked at the point the last non-file-save eval (the startup/init
  /// script, or an interactive eval) defined it. `RedirectedFromCompiled`/
  /// `CompiledCandidates` below were the old proxy for this ("assembly kind
  /// stands in for what the app captured") and both broke a working `#load`ed
  /// reload in practice, because an app can hold an FSI copy even when a
  /// compiled copy of the same name also exists. This field replaces them as
  /// the one `confirmPatchAsOutcome` should be given; when a name has no
  /// `AppHolds` entry at all (the app has not been observed to capture
  /// anything under that name yet), it is never included here, so a save can
  /// never be reported as `Patched` without positive evidence — it falls back
  /// to `RestartRequired`/`NoEffect` by construction.
  ReachedRunningProcess: string list
  /// Full names of methods Harmony accepted a patch for and whose JIT-compiled
  /// bytes the canary then found UNCHANGED. Deliberately NOT in `Redirected`:
  /// the running process demonstrably still executes the old body, so counting
  /// these as landed is how a save became "Hot reloaded 1 of 1" while the app
  /// served stale code. Kept as its own list rather than folded into
  /// `Failures` so a caller can name the declaration the user edited.
  Ineffective: string list
  /// The subset of `Redirected` whose re-pointed OLD entry point lived in a
  /// COMPILED assembly. An app running from the project's own build output
  /// calls those; re-pointing only a previous FSI copy leaves it untouched, and
  /// by name alone the two are indistinguishable.
  RedirectedFromCompiled: string list
  /// Names for which a COMPILED copy existed among the detour candidates at
  /// all.
  ///
  /// Needed because "a compiled entry point was re-pointed" is the right test
  /// ONLY when there is a compiled copy to re-point. A file brought in by
  /// `#load` has no compiled copy anywhere — every copy is an FSI one, so the
  /// running app necessarily holds an FSI copy and an FSI-to-FSI redirect IS
  /// what reaches it. Without this, that entirely legitimate reload gets
  /// reported as no effect.
  CompiledCandidates: string list
  /// One probe per new body a detour was pointed at (through a stub) and that
  /// landed. The redirect is not the evidence that the app runs the new code:
  /// the new body running is, and these are how the host sees it. A caller
  /// that had the old function inlined never enters it, so its probe never
  /// fires.
  Probes: EntryProbe list
  Bindings: BindingOutcome list
  Declined: DeclinedBinding list
  /// Detours that were planned and did not happen.
  Failures: string list
  /// Changes that cannot be patched, named, so the save restarts with the reason
  /// instead of reporting a patch that did nothing.
  Refusals: DetourRefusal list
}

module DetourReport =
  /// The report of an eval that touched nothing — no new assembly, or hot
  /// reload disabled. Named so callers never hand-roll the all-empty record.
  let empty : DetourReport =
    { Redirected = []; ReachedRunningProcess = []; Ineffective = []; RedirectedFromCompiled = []; CompiledCandidates = []; Probes = []; Bindings = []; Declined = []; Failures = []; Refusals = [] }

/// Forces everything a detour will touch to resolve BEFORE any leg is written:
/// the parameter and return types (which throw `TypeLoadException` for a stale
/// FSI compilation unit) and the JIT-compiled body. A binding that is going to
/// fail should fail here, where declining is still free — once one leg is
/// written there is no way back.
let private preflight (m: Method) : Result<unit, string> =
  try
    m.MethodInfo.GetParameters() |> Array.iter (fun p -> p.ParameterType |> ignore)
    m.MethodInfo.ReturnType |> ignore
    RuntimeHelpers.PrepareMethod m.MethodInfo.MethodHandle
    Ok()
  with ex ->
    Error(sprintf "%s is not patchable (%s: %s)" m.FullName (ex.GetType().Name) ex.Message)

/// What one binding's already-applied legs (`detourMethod`'s own verdicts,
/// one per accessor) add up to. Pulled out of `applyBindingDetour` as its own
/// pure function — no Harmony call in it — so the three-way verdict
/// (coherent / untouched / TORN) is testable directly from a constructed
/// `DetourApplied list`, independent of whatever makes a real detour actually
/// fail on a given runtime or Harmony build. `detourMethod` already owns and
/// is tested for WHEN a leg fails for real (a stale FSI type, an unsupported
/// runtime, a native failure); this owns what a MIX of those verdicts means
/// for the binding as a whole.
let classifyBindingApplication (binding: string) (applied: DetourApplied list) : BindingOutcome =
  let failures =
    applied
    |> List.choose (function
      | DetourApplied.Failed reason -> Some reason
      | DetourApplied.Redirected
      | DetourApplied.Ineffective _
      | DetourApplied.Superseded _ -> None)

  let anyLanded =
    applied
    |> List.exists (function
      | DetourApplied.Failed _ -> false
      | DetourApplied.Redirected
      | DetourApplied.Ineffective _
      | DetourApplied.Superseded _ -> true)

  match failures, anyLanded with
  | [], _ -> BindingOutcome.BothLegsRedirected binding
  | reason :: _, false -> BindingOutcome.NeitherLegRedirected(binding, reason)
  | reason :: _, true -> BindingOutcome.Torn(binding, reason)

let private applyBindingDetour (logger: ILogger) (unit: AccessorPairDetour) : BindingOutcome =
  let preflightFailures =
    unit.Legs
    |> List.collect (fun (older, newer) -> [ preflight older; preflight newer ])
    |> List.choose (function
      | Error reason -> Some reason
      | Ok() -> None)

  match preflightFailures with
  | reason :: _ -> BindingOutcome.NeitherLegRedirected(unit.Binding, reason)
  | [] ->
    unit.Legs
    |> List.map (fun (older, newer) ->
      logger.LogDebug("Updating accessor " + older.FullName)
      detourMethod logger older.MethodInfo newer.MethodInfo)
    |> classifyBindingApplication unit.Binding

/// The probe for one new body, and what a detour should point at in place of
/// it: a stub that records an entry and then calls the body. When no stub can be
/// built the detour points straight at the body and the probe can never fire,
/// so that function ends as never-entered rather than as a claim nobody can check.
let private probeTarget (logger: ILogger) (newer: Method) : EntryProbe * MethodBase =
  let probe = ProbeRegistry.Shared.Allocate newer.FullName
  match stubFor probe newer.MethodInfo with
  | Result.Ok stub -> probe, stub :> MethodBase
  | Result.Error failure ->
    logger.LogWarning(sprintf "Hot reload cannot watch %s run, so its patch cannot be confirmed: %s" newer.FullName (StubFailure.describe failure))
    probe, newer.MethodInfo :> MethodBase

let applyDetourPlan (logger: ILogger) (appHolds: Map<string, MethodInfo>) (plan: DetourPlan) : DetourReport =
  // One stub per new body, shared by every older copy that is pointed at it.
  let targets = Collections.Generic.Dictionary<MethodInfo, EntryProbe * MethodBase>()
  let targetFor (newer: Method) : EntryProbe * MethodBase =
    match targets.TryGetValue newer.MethodInfo with
    | true, found -> found
    | false, _ ->
      let made = probeTarget logger newer
      targets.[newer.MethodInfo] <- made
      made
  let functionResults =
    plan.Functions
    |> List.map (fun (older, newer) ->
      logger.LogDebug("Updating method " + older.FullName)
      let _, target = targetFor newer
      older, detourMethod logger older.MethodInfo target)
  // A probe counts once its body is what some old entry point now reaches. It
  // supersedes the earlier probe of the same function that never ran.
  let landedProbes =
    List.zip plan.Functions functionResults
    |> List.choose (fun ((_, newer), (_, applied)) ->
      match applied with
      | DetourApplied.Redirected
      | DetourApplied.Ineffective _ -> Some(fst (targetFor newer))
      | DetourApplied.Superseded _
      | DetourApplied.Failed _ -> None)
    |> List.distinctBy _.Id
  for probe in landedProbes do
    ProbeRegistry.Shared.Commit probe
  // `Ineffective` IS still counted as redirected, and the comment on
  // `DetourApplied.Ineffective` calling the canary "a warning signal, not a
  // verdict" is load-bearing — MEASURED, after trying the opposite.
  //
  // Excluding it looks obviously right: the canary compares the method's
  // JIT-compiled bytes either side of the patch, so "unchanged" reads as proof
  // the running process did not move. It is not. Against a real host, the
  // `real file save hot-reloads a running module-declared app` and
  // `compile-error save keeps last valid behavior` suites both went red with
  // "'greeting' was re-pointed but the running code did not change" for reloads
  // that demonstrably DO change what the app serves. The canary reads the
  // method's own entry point; a reload can reach the app through a copy it does
  // not sample, so BytesUnchanged is a false negative often enough to be
  // useless as a verdict.
  //
  // It still travels, as `Ineffective`, for anyone who wants the signal — it
  // just may not decide the count on its own.
  let redirectedFunctions =
    functionResults
    |> List.choose (fun (older, applied) ->
      match applied with
      | DetourApplied.Redirected
      | DetourApplied.Ineffective _
      | DetourApplied.Superseded _ -> Some older.FullName
      | DetourApplied.Failed _ -> None)

  /// Re-pointed, and proven by the canary not to have changed the running code.
  /// Carried separately from `Failures` so a caller can name the DECLARATION
  /// the user edited rather than only log a sentence about it.
  let ineffectiveFunctions =
    functionResults
    |> List.choose (fun (older, applied) ->
      match applied with
      | DetourApplied.Ineffective _ -> Some older.FullName
      | _ -> None)

  /// Every candidate name for which a COMPILED copy was among the older methods
  /// considered. When this does NOT contain a name, there is no compiled copy to
  /// reach and an FSI-to-FSI redirect is what the running app calls.
  let compiledCandidates =
    functionResults
    |> List.choose (fun (older, _) ->
      let isDynamic =
        try older.MethodInfo.DeclaringType.Assembly.IsDynamic with _ -> true
      match isDynamic with
      | true -> None
      | false -> Some older.FullName)
    |> List.distinct

  /// Of the redirects that landed, those whose re-pointed OLD entry point lived
  /// in a COMPILED assembly rather than in FSI's own dynamic assembly.
  ///
  /// This distinction is invisible by name. FSI wraps every submission in an
  /// `FSI_NNNN` type (see FsiDynamicModulePrefix in the compiler), and
  /// `getAllMethods` strips that prefix so both sides register the same
  /// qualified name — which is what makes the detour matcher work at all, and
  /// also what makes "a method with this name was re-pointed" unable to tell
  /// the compiled entry point apart from a previous eval's copy. In
  /// `--multiemit-` mode the single FSI assembly ACCUMULATES every eval, so
  /// there is a growing pile of same-named older copies to pair with, and
  /// re-pointing one of those changes nothing an app running from the compiled
  /// project assembly will ever call.
  let redirectedFromCompiled =
    functionResults
    |> List.choose (fun (older, applied) ->
      match applied with
      | DetourApplied.Redirected
      | DetourApplied.Superseded _ ->
        let declaringAsmIsDynamic =
          try older.MethodInfo.DeclaringType.Assembly.IsDynamic with _ -> true
        match declaringAsmIsDynamic with
        | true -> None
        | false -> Some older.FullName
      | _ -> None)

  let functionFailures =
    functionResults
    |> List.choose (fun (_, applied) ->
      match applied with
      | DetourApplied.Failed reason -> Some reason
      | _ -> None)

  let outcomes = plan.MutableBindings |> List.map (applyBindingDetour logger)

  let redirectedBindings =
    List.zip plan.MutableBindings outcomes
    |> List.collect (fun (unit, outcome) ->
      match outcome with
      | BindingOutcome.BothLegsRedirected _ -> unit.Legs |> List.map (fun (older, _) -> older.FullName)
      | BindingOutcome.NeitherLegRedirected _
      | BindingOutcome.Torn _ -> [])

  for outcome in outcomes do
    match outcome with
    | BindingOutcome.BothLegsRedirected binding ->
      logger.LogDebug(sprintf "Hot reload re-pointed both accessors of %s" binding)
    | BindingOutcome.NeitherLegRedirected(binding, reason) ->
      logger.LogWarning(
        sprintf
          "Hot reload left '%s' alone: %s. Re-pointing one accessor of a mutable binding without the other silently discards every later write, so neither was re-pointed. Restart the app to pick this change up."
          binding
          reason)
    | BindingOutcome.Torn(binding, reason) ->
      logger.LogError(
        sprintf
          "Hot reload TORE '%s': %s. One accessor was re-pointed and another was not, so writes to '%s' now land in a field nothing reads. Restart the app."
          binding
          reason
          binding)
      DevReloadHealthTracker.transition (DevReloadHealth.Degraded(sprintf "Torn accessor pair for %s" binding))

  for declined in plan.Declined do
    logger.LogWarning(
      sprintf
        "Hot reload declined '%s': %s. Re-pointing one accessor of a mutable binding without the other silently discards every later write, so neither was re-pointed. Restart the app to pick this change up."
        declined.Binding
        (OrphanedLeg.describe declined.Orphan))

  let bindingFailures =
    outcomes
    |> List.choose (function
      | BindingOutcome.BothLegsRedirected _ -> None
      | BindingOutcome.NeitherLegRedirected(binding, reason)
      | BindingOutcome.Torn(binding, reason) -> Some(sprintf "%s: %s" binding reason))

  // The evidence `confirmPatchAsOutcome` actually needs: of the function
  // redirects that landed (Redirected/Ineffective/Superseded — a canary
  // false negative must not un-count a real redirect), the ones whose
  // OLDER, re-pointed entry point is the EXACT MethodInfo `appHolds` recorded
  // for that name. `functionResults` (not the flattened name lists above) is
  // the only place that still has both the per-pair MethodInfo and its own
  // per-pair outcome, so this reads it directly instead of re-deriving
  // "landed" from a name that could belong to a different, unrelated pair.
  let reachedRunningProcess =
    functionResults
    |> List.choose (fun (older, applied) ->
      let landed =
        match applied with
        | DetourApplied.Redirected
        | DetourApplied.Ineffective _
        | DetourApplied.Superseded _ -> true
        | DetourApplied.Failed _ -> false
      match landed, Map.tryFind (holdKey older.MethodInfo) appHolds with
      | true, Some held when held = older.MethodInfo -> Some older.FullName
      | _ -> None)
    |> List.distinct

  { Redirected = redirectedFunctions @ redirectedBindings
    ReachedRunningProcess = reachedRunningProcess
    Ineffective = ineffectiveFunctions
    RedirectedFromCompiled = redirectedFromCompiled
    CompiledCandidates = compiledCandidates
    Probes = landedProbes
    Bindings = outcomes
    Declined = plan.Declined
    Failures = functionFailures @ bindingFailures
    Refusals = [] }

// ── closures ─────────────────────────────────────────────────────────────────
//
// A lambda written inline compiles to a closure CLASS, and the running app holds
// instances of it (a route list built at startup is a list of them). There is no
// named method to re-point, but each class has an `Invoke`, and detouring the old
// class's `Invoke` to the new class's reaches every instance already built.
//
// That is only sound when the new class is laid out like the old one, because the
// new `Invoke` is handed an OLD instance and reads its captured values by field
// offset. So a pair is matched only when the fields are the same, in the same order,
// of the same types. Anything else is a refusal that names what moved.
//
// Which new class belongs to which old one is read off the compiler's own names.
// F# names a closure `<binding>@<line>[-<n>]` after the binding it is in and the
// line of the lambda, and numbers them in the order it makes them. The planner says
// which lambdas changed and where they sit inside their declaration, in lines
// counted from the declaration's first line; here those lines are looked up in the
// compiled assembly (as written in the file) and in the code FSI just compiled
// (found by the `# n "file"` line that precedes the declaration).

/// A lambda whose text changed, as lines counted from its declaration's first line, in the source
/// the app was built from (`Was`) and in the source just saved (`Now`).
type ClosureLambda = {
  WasFirst: int
  WasLast: int
  NowFirst: int
  NowLast: int
}

[<RequireQualifiedAccess>]
type ClosureStrictness =
  /// The closures are the only way the edit can reach the app (the lambdas of a module value, which
  /// is not re-run), so closures that cannot be matched are a refusal.
  | Required
  /// The declaration is patched as a function whatever the closures do (an edited function that also
  /// holds a closure the app kept). Closures that cannot be matched are left as they are.
  | BestEffort

/// One declaration whose lambdas changed, and where to find its closures.
type ClosureRepoint = {
  /// The module path the declaration lives in, `["ParityFixture"; "Parity"]`.
  Container: string list
  /// The binding the compiler numbers its closures after.
  Binding: string
  /// The exact `# n "file"` line the evaluated code has just before the declaration, which is how
  /// the declaration is found in what FSI compiled whatever else the pipeline added to the code.
  Directive: string
  /// The declaration's first line in the source the app was built from.
  WasStartLine: int
  Lambdas: ClosureLambda list
  Strictness: ClosureStrictness
}

/// The name a repointed declaration goes by, the same shape as a function's qualified name.
let closureDeclarationName (request: ClosureRepoint) : string =
  String.concat "." (request.Container @ [ request.Binding ])

/// What re-pointing the closures of the saved declarations did.
type ClosureReport = {
  /// The declarations at least one closure of which was re-pointed.
  Landed: string list
  Probes: EntryProbe list
  Refusals: DetourRefusal list
  Failures: string list
}

module ClosureReport =
  let empty : ClosureReport = { Landed = []; Probes = []; Refusals = []; Failures = [] }

type private ClosureClass = {
  Class: Type
  Line: int
  /// The compiler's own counter, which is the order it made the closures in.
  Order: int
}

let private closureNamePattern =
  System.Text.RegularExpressions.Regex(@"^(?<binding>.+)@(?<line>\d+)(?:-(?<n>\d+))?$", System.Text.RegularExpressions.RegexOptions.Compiled)

/// The types of an assembly, as many as will load.
let private typesOf (asm: Assembly) : Type list =
  try
    asm.GetTypes() |> Array.toList
  with
  | :? ReflectionTypeLoadException as ex -> ex.Types |> Array.filter (fun t -> not (isNull t)) |> Array.toList
  | _ -> []

/// The path a type sits at the way a source file spells it, namespace and modules, with
/// FSI's own `FSI_nnnn` wrapper left out.
let private pathOf (t: Type) : string list =
  let rec chain (x: Type) : Type list =
    match x.DeclaringType with
    | null -> [ x ]
    | declaring -> chain declaring @ [ x ]
  let types = chain t
  let ns =
    match (List.head types).Namespace with
    | null
    | "" -> []
    | n -> n.Split('.') |> Array.toList
  ns @ (types |> List.map _.Name) |> List.filter (fun segment -> not (SageFs.FsiNaming.isDynamicModuleSegment segment))

/// The closure classes the compiler made for a binding of a module.
let private closureClassesOf (types: Type list) (container: string list) (binding: string) : ClosureClass list =
  types
  |> List.choose (fun t ->
    match t.DeclaringType with
    | null -> None
    | declaring when pathOf declaring = container ->
      let named = closureNamePattern.Match t.Name
      match named.Success && named.Groups.["binding"].Value = binding with
      | false -> None
      | true ->
        let order =
          match named.Groups.["n"].Success with
          | true -> int named.Groups.["n"].Value
          | false -> 0
        Some { Class = t; Line = int named.Groups.["line"].Value; Order = order }
    | _ -> None)

/// The FSI submission a type was compiled in: the number in its `FSI_nnnn` wrapper.
let private submissionOf (t: Type) : int =
  let rec outermost (x: Type) =
    match x.DeclaringType with
    | null -> x
    | declaring -> outermost declaring
  let name = (outermost t).Name
  let digits = name.Substring(min name.Length SageFs.FsiNaming.Rules.dynamicModulePrefix.Length)
  match Int32.TryParse digits with
  | true, n -> n
  | false, _ -> -1

/// 1-based line of the first line of text after `directive` in `code`, when the directive is
/// there exactly once.
let private firstLineAfter (code: string) (directive: string) : int option =
  let lines = code.Replace("\r\n", "\n").Split('\n')
  match lines |> Array.indexed |> Array.filter (fun (_, line) -> line.Trim() = directive.Trim()) with
  | [| index, _ |] -> Some(index + 2)
  | _ -> None

let private instanceFields (t: Type) : FieldInfo list =
  t.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
  |> Array.toList

let private describeField (f: FieldInfo) = sprintf "%s: %s" f.Name f.FieldType.Name

/// None when code compiled for `newer` can run on an object of `older`; otherwise what is different.
/// Code compiled for one class reads an object of another by field offset, so the two have to be
/// laid out the same: the same base, and the same fields, in the same order, of the same types.
let layoutDifference (older: Type) (newer: Type) : string option =
  let shape (t: Type) = instanceFields t |> List.map (fun f -> f.Name, f.FieldType)
  match older.IsGenericTypeDefinition || newer.IsGenericTypeDefinition, older.BaseType = newer.BaseType with
  | true, _ -> Some "it is generic"
  | _, false -> Some(sprintf "its base type is %s, not %s" (string newer.BaseType) (string older.BaseType))
  | false, true when shape older = shape newer -> None
  | false, true ->
    let wasFields = instanceFields older |> List.map describeField
    let nowFields = instanceFields newer |> List.map describeField
    let added = nowFields |> List.filter (fun f -> not (List.contains f wasFields))
    let lost = wasFields |> List.filter (fun f -> not (List.contains f nowFields))
    match added, lost with
    | _ :: _, [] -> Some(sprintf "it now holds %s" (String.concat ", " added))
    | [], _ :: _ -> Some(sprintf "it no longer holds %s" (String.concat ", " lost))
    | _ -> Some "its fields are different"

/// The instance methods two classes both declare with the same signature, older paired with newer.
let private sharedInstanceMethods (older: Type) (newer: Type) : (MethodInfo * MethodInfo) list =
  let declared (t: Type) =
    t.GetMethods(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
    |> Array.filter (fun m -> not m.IsGenericMethod && not m.IsAbstract)
  let signature (m: MethodInfo) = m.Name, m.ReturnType, m.GetParameters() |> Array.map _.ParameterType |> Array.toList
  let newest = declared newer
  declared older
  |> Array.choose (fun o ->
    newest |> Array.tryFind (fun n -> signature o = signature n) |> Option.map (fun n -> o, n))
  |> Array.toList

/// One lambda's closures, old and new, ready to be paired.
type private LambdaClosures = {
  Lambda: ClosureLambda
  Older: ClosureClass list
  Newer: ClosureClass list
}

/// Everything one request needs, decided before any detour is made.
[<RequireQualifiedAccess>]
type private ClosurePlan =
  | Ready of (MethodInfo * MethodInfo) list
  | Unmatched of detail: string

let private planClosures (request: ClosureRepoint) (oldTypes: Type list) (newTypes: Type list) (evaluatedCode: string) : ClosurePlan =
  match firstLineAfter evaluatedCode request.Directive with
  | None -> ClosurePlan.Unmatched "the declaration could not be found in the code FSI compiled"
  | Some newStart ->
    let oldClasses = closureClassesOf oldTypes request.Container request.Binding
    let newest =
      closureClassesOf newTypes request.Container request.Binding
      |> List.groupBy (fun c -> submissionOf c.Class)
      |> List.sortBy fst
      |> List.tryLast
      |> Option.map snd
      |> Option.defaultValue []
    let within (start: int) (first: int) (last: int) (c: ClosureClass) =
      c.Line - start >= first && c.Line - start <= last
    let perLambda =
      request.Lambdas
      |> List.map (fun l ->
        { Lambda = l
          Older = oldClasses |> List.filter (within request.WasStartLine l.WasFirst l.WasLast) |> List.sortBy _.Order
          Newer = newest |> List.filter (within newStart l.NowFirst l.NowLast) |> List.sortBy _.Order })
    let problems =
      perLambda
      |> List.choose (fun group ->
        match group.Older.Length, group.Newer.Length with
        | 0, _ -> Some(sprintf "no closure was built for the lambda at line %d" (request.WasStartLine + group.Lambda.WasFirst))
        | was, now when was <> now ->
          Some(sprintf "the lambda at line %d held %d closure(s) and now holds %d" (request.WasStartLine + group.Lambda.WasFirst) was now)
        | _ ->
          List.zip group.Older group.Newer
          |> List.tryPick (fun (o, n) ->
            layoutDifference o.Class n.Class
            |> Option.map (fun why -> sprintf "in the lambda at line %d, %s" (request.WasStartLine + group.Lambda.WasFirst) why)))
    match problems with
    | first :: _ -> ClosurePlan.Unmatched first
    | [] ->
      let methods =
        perLambda
        |> List.collect (fun group -> List.zip group.Older group.Newer)
        |> List.collect (fun (o, n) -> sharedInstanceMethods o.Class n.Class)
      match methods with
      | [] -> ClosurePlan.Unmatched "the closures have no method in common"
      | _ -> ClosurePlan.Ready methods

/// The closures of a save, matched and ready to re-point, or the refusals that stop the save.
/// Nothing in it has been detoured: the point of planning first is that one refusal anywhere in
/// the save stops every detour of it, so the running app is never left half moved.
type ClosureWork = {
  Ready: (ClosureRepoint * (MethodInfo * MethodInfo) list) list
  Refusals: DetourRefusal list
}

module ClosureWork =
  let empty : ClosureWork = { Ready = []; Refusals = [] }

/// Match the closures of the declarations a save changed. Reads types and writes nothing.
let planClosureWork
  (projectAssemblies: Assembly list)
  (newAssembly: Assembly)
  (evaluatedCode: string)
  (requests: ClosureRepoint list)
  : ClosureWork =
  match requests with
  | [] -> ClosureWork.empty
  | _ ->
    let oldTypes = projectAssemblies |> List.collect typesOf
    let newTypes = typesOf newAssembly
    requests
    |> List.fold
      (fun (work: ClosureWork) (request: ClosureRepoint) ->
        match planClosures request oldTypes newTypes evaluatedCode, request.Strictness with
        | ClosurePlan.Unmatched _, ClosureStrictness.BestEffort -> work
        | ClosurePlan.Unmatched detail, ClosureStrictness.Required ->
          { work with Refusals = work.Refusals @ [ DetourRefusal.ClosureShapeChanged(closureDeclarationName request, detail) ] }
        | ClosurePlan.Ready methods, _ -> { work with Ready = work.Ready @ [ request, methods ] })
      ClosureWork.empty

/// Re-point the closures `planClosureWork` matched.
let applyClosureWork (logger: ILogger) (work: ClosureWork) : ClosureReport =
  work.Ready
  |> List.map (fun (request, methods) ->
        let name = closureDeclarationName request
        let probed =
          methods
          |> List.map (fun (older, newer) ->
            let probe = ProbeRegistry.Shared.Allocate name
            let target : MethodBase =
              match stubFor probe newer with
              | Result.Ok stub -> stub :> MethodBase
              | Result.Error failure ->
                logger.LogWarning(sprintf "Hot reload cannot watch %s run, so its patch cannot be confirmed: %s" name (StubFailure.describe failure))
                newer :> MethodBase
            let preflighted =
              try
                RuntimeHelpers.PrepareMethod older.MethodHandle
                Ok()
              with ex -> Error(sprintf "%s.%s is not patchable (%s: %s)" older.DeclaringType.Name older.Name (ex.GetType().Name) ex.Message)
            probe, older, target, preflighted)
        let results =
          probed
          |> List.map (fun (probe, older, target, preflighted) ->
            match preflighted with
            | Error reason -> probe, DetourApplied.Failed reason
            | Ok() ->
              logger.LogDebug(sprintf "Updating closure %s.%s of %s" older.DeclaringType.Name older.Name name)
              probe, detourMethod logger older target)
        let landedProbes =
          results
          |> List.choose (fun (probe, applied) ->
            match applied with
            | DetourApplied.Redirected
            | DetourApplied.Ineffective _ -> Some probe
            | DetourApplied.Superseded _
            | DetourApplied.Failed _ -> None)
        // One commit per declaration, of the OLDEST probe it just allocated: a commit supersedes every
        // earlier probe of the declaration that never ran, and the closures of this save must not
        // supersede each other.
        landedProbes
        |> List.sortBy _.Id
        |> List.tryHead
        |> Option.iter ProbeRegistry.Shared.Commit
        let failures =
          results
          |> List.choose (fun (_, applied) ->
            match applied with
            | DetourApplied.Failed reason -> Some reason
            | _ -> None)
        { Landed =
            match landedProbes with
            | [] -> []
            | _ -> [ name ]
          Probes = landedProbes
          Refusals = []
          Failures = failures })
  |> List.fold
    (fun (all: ClosureReport) (next: ClosureReport) ->
      { Landed = all.Landed @ next.Landed
        Probes = all.Probes @ next.Probes
        Refusals = all.Refusals @ next.Refusals
        Failures = all.Failures @ next.Failures })
    ClosureReport.empty

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
    // A generic method is compiled once per instantiation that runs. Detouring the ones that have run
    // leaves every instantiation that runs LATER on the old body (measured: the open definition cannot be
    // detoured at all, and a closed one covers only itself), so a generic function is never detoured.
    && not newMethod.MethodInfo.IsGenericMethod
    && not existingMethod.MethodInfo.IsGenericMethod
    && existingMethod.MethodInfo.IsStatic = newMethod.MethodInfo.IsStatic
    && getParams existingMethod = getParams newMethod
    && existingMethod.MethodInfo.ReturnType = newMethod.MethodInfo.ReturnType
    && existingMethod.FullName.EndsWith(newMethod.FullName, StringComparison.Ordinal)
    // The new member is handed an object of the OLD type, so it is only the same member when the two types
    // are laid out alike. When they are not, the save is refused by name (`refusals` in `handleNewAsmFromRepl`).
    && (existingMethod.MethodInfo.IsStatic
        || (layoutDifference existingMethod.MethodInfo.DeclaringType newMethod.MethodInfo.DeclaringType).IsNone)
  with
  | :? TypeLoadException as ex ->
    logger.LogDebug(
      sprintf "Hot-reload param comparison skipped (TypeLoadException): %s — %s"
        newMethod.FullName ex.Message)
    false

/// Registers a new REPL-emitted assembly and, when hot reload is on, applies
/// every detour it makes possible. Returns the FULL report — not just the
/// names that landed — because a caller that only sees `Redirected` cannot
/// tell "nothing changed" from "a mutable binding just tore": both look like
/// an empty/short list. `DetourReport.Bindings` and `.Declined` are how a
/// torn or orphaned accessor pair reaches anyone past this function.
let handleNewAsmFromRepl (logger: ILogger) (hotReloadEnabled: bool) (isFileSave: bool) (asm: Assembly) (st: State) : State * DetourReport =
  // Chesterton's fence: the `prev = asm` dedup only applies to NON-dynamic
  // assemblies. In HotReload the FSI session runs with --multiemit- (single
  // assembly mode): EVERY eval lands in the SAME persistent FSI-ASSEMBLY, so
  // object identity is constant and the old check made the middleware
  // early-return after the first eval — no hot-reload re-eval was ever
  // processed, no detour ever fired (P0 hot-reload gap). Dynamic assemblies
  // must always be processed; the method merge is idempotent (Map.add
  // overwrites) and the detour matcher pairs old->new per eval.
  match st.LastAssembly with
  | Some prev when prev = asm && not asm.IsDynamic -> st, DetourReport.empty
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

    let known =
      Collections.Generic.HashSet<MethodInfo>(st.Methods |> Map.toSeq |> Seq.collect snd |> Seq.map _.MethodInfo)

    // Chesterton's fence: replacementPairs computation accesses .ParameterType and
    // .ReturnType on MethodInfo — these trigger TypeLoadException when parameters
    // reference types from older FSI compilation units that were redefined.
    // This MUST be gated behind hotReloadEnabled. Without this gate, every normal
    // REPL eval (define type in block 1, use it in block 2) triggers the exception.
    let detourPlan =
      match hotReloadEnabled with
      | false ->
        { Functions = []
          MutableBindings = []
          Declined = [] }
      | true ->
        let existingPairs =
          planDetours (fun m -> not (known.Contains m.MethodInfo)) (compatibleForDetour logger) newMethods st.Methods

        // The fix for the shipped bug: pair the copy `AppHolds` says the app
        // ACTUALLY holds against this eval's newest offering for that name,
        // regardless of whether `st.Methods`'s own snapshot for the name
        // still carries that held copy as a candidate. `st.Methods` is
        // rebuilt per eval (the comment above: "the method merge is
        // idempotent... overwrites"), so a name's older-copy history does not
        // necessarily survive between evals — but the copy the app is
        // holding must always get a chance to be re-pointed, or a save can
        // land everywhere except the one place that matters. Measured against
        // a real host: without this, `existingPairs` above only ever offers
        // the ORIGINAL compiled entry point as the "older" side once a
        // `#load`ed file's app has moved on to an FSI copy, so the app's own
        // copy is silently never a detour target again.
        let appHoldsPairs =
          newMethods
          |> List.filter (fun m -> not (known.Contains m.MethodInfo))
          |> List.choose (fun newest ->
            match Map.tryFind (holdKey newest.MethodInfo) st.AppHolds with
            | Some held when held <> newest.MethodInfo ->
              let heldMethod = { MethodInfo = held; FullName = newest.FullName }
              match compatibleForDetour logger heldMethod newest with
              | true -> Some(heldMethod, newest)
              | false -> None
            | _ -> None)

        (existingPairs @ appHoldsPairs)
        |> List.distinctBy (fun (older, newer) -> older.MethodInfo, newer.MethodInfo)
        |> planDetourUnits (settableBindingsOf st.Methods)

    // Apply Harmony detours — already gated by an empty plan when disabled. Only
    // the detours that ACTUALLY landed are reported: a method that threw on the
    // way in used to be listed as reloaded, which made `confirmPatch` confirm a
    // patch that never reached the running process. `st.AppHolds` (the
    // pre-this-eval value) is what turns "something with this name moved"
    // into "the copy the app actually calls moved" — see
    // `DetourReport.ReachedRunningProcess`.
    //
    // An instance member the app holds a copy of, whose new type is laid out differently: the objects the
    // app already built cannot run the new member, so the save is refused, and refused whole. One refusal
    // stops every detour of the save, so the running app is left exactly as it was.
    //
    // A generic function the app holds a copy of is refused the same way: its saved body would be compiled and
    // reach nothing, because only the instantiations that already ran could be detoured.
    //
    // Only a SAVE is refused. An interactive eval that redefines a generic function or a class is the user
    // running code, not asking for a patch of the running app, and it never was detoured.
    let refusals =
      match hotReloadEnabled && isFileSave with
      | false -> []
      | true ->
        newMethods
        |> List.filter (fun m -> not (known.Contains m.MethodInfo))
        |> List.choose (fun newest ->
          match Map.tryFind (holdKey newest.MethodInfo) st.AppHolds with
          | Some held when held <> newest.MethodInfo ->
            match newest.MethodInfo.IsGenericMethod, newest.MethodInfo.IsStatic with
            | true, _ -> Some(DetourRefusal.GenericFunction newest.FullName)
            | false, true -> None
            | false, false ->
              try
                layoutDifference held.DeclaringType newest.MethodInfo.DeclaringType
                |> Option.map (fun detail -> DetourRefusal.InstanceLayoutChanged(held.DeclaringType.Name, detail))
              with :? TypeLoadException -> None
          | _ -> None)
        |> List.distinct

    let report =
      match refusals with
      | [] -> applyDetourPlan logger st.AppHolds detourPlan
      | _ -> DetourReport.empty

    // A file-save (`isFileSave`) is an ATTEMPT to reach whatever the app
    // already holds — it must never redefine what "held" means, or a failed
    // attempt would silently start passing next time for the wrong reason.
    // Every other eval (the startup/init script, or an interactive
    // evaluation) is exactly the kind of code that builds route tables and
    // handler closures, so whatever it just (re)defined becomes the captured
    // copy from here on. Only names with a genuinely NEW method this eval are
    // touched — a name this eval's assembly-wide rescan re-offers unchanged
    // keeps whatever was already captured, rather than guessing from
    // re-scanned order.
    //
    // One exception, for a method that did not exist at all: a function a save ADDED has no compiled
    // copy, so the first FSI copy is the only one anything calls, and it has to be remembered as
    // the held copy or the next save of it would find nothing to re-point and read as ineffective.
    let freshByKey =
      newMethods
      |> List.filter (fun m -> not (known.Contains m.MethodInfo))
      |> List.groupBy (fun m -> holdKey m.MethodInfo)
    let appHolds =
      freshByKey
      |> List.fold
        (fun (acc: Map<string, MethodInfo>) (key, fresh) ->
          match isFileSave, Map.containsKey key acc with
          | true, true -> acc
          | _ -> Map.add key (List.last fresh).MethodInfo acc)
        st.AppHolds

    { st with
        LastAssembly = Some asm
        Methods = mergedMethods
        AppHolds = appHolds },
    { report with Refusals = refusals }


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


// --- Assembly resolution for the process the user's code runs in ---

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

