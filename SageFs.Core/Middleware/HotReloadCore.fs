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
  match name.StartsWith("get_", StringComparison.Ordinal), name.StartsWith("set_", StringComparison.Ordinal) with
  | true, _ -> AccessorRole.Getter(qualify (name.Substring 4))
  | _, true -> AccessorRole.Setter(qualify (name.Substring 4))
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

/// Everything one eval did, in the vocabulary a user-facing outcome needs.
type DetourReport = {
  /// Full names of the older methods whose entry points now jump to new code.
  Redirected: string list
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
  Bindings: BindingOutcome list
  Declined: DeclinedBinding list
  /// Detours that were planned and did not happen.
  Failures: string list
}

module DetourReport =
  /// The report of an eval that touched nothing — no new assembly, or hot
  /// reload disabled. Named so callers never hand-roll the all-empty record.
  let empty : DetourReport =
    { Redirected = []; Ineffective = []; RedirectedFromCompiled = []; CompiledCandidates = []; Bindings = []; Declined = []; Failures = [] }

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

let applyDetourPlan (logger: ILogger) (plan: DetourPlan) : DetourReport =
  let functionResults =
    plan.Functions
    |> List.map (fun (older, newer) ->
      logger.LogDebug("Updating method " + older.FullName)
      older, detourMethod logger older.MethodInfo newer.MethodInfo)

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

  { Redirected = redirectedFunctions @ redirectedBindings
    Ineffective = ineffectiveFunctions
    RedirectedFromCompiled = redirectedFromCompiled
    CompiledCandidates = compiledCandidates
    Bindings = outcomes
    Declined = plan.Declined
    Failures = functionFailures @ bindingFailures }

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

/// Registers a new REPL-emitted assembly and, when hot reload is on, applies
/// every detour it makes possible. Returns the FULL report — not just the
/// names that landed — because a caller that only sees `Redirected` cannot
/// tell "nothing changed" from "a mutable binding just tore": both look like
/// an empty/short list. `DetourReport.Bindings` and `.Declined` are how a
/// torn or orphaned accessor pair reaches anyone past this function.
let handleNewAsmFromRepl (logger: ILogger) (hotReloadEnabled: bool) (asm: Assembly) (st: State) : State * DetourReport =
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
        let known =
          Collections.Generic.HashSet<MethodInfo>(st.Methods |> Map.toSeq |> Seq.collect snd |> Seq.map _.MethodInfo)

        planDetours (fun m -> not (known.Contains m.MethodInfo)) (compatibleForDetour logger) newMethods st.Methods
        |> planDetourUnits (settableBindingsOf st.Methods)

    // Apply Harmony detours — already gated by an empty plan when disabled. Only
    // the detours that ACTUALLY landed are reported: a method that threw on the
    // way in used to be listed as reloaded, which made `confirmPatch` confirm a
    // patch that never reached the running process.
    let report = applyDetourPlan logger detourPlan

    { st with
        LastAssembly = Some asm
        Methods = mergedMethods },
    report

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

