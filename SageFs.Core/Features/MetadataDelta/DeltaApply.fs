/// Hands a delta to the runtime, and says what the runtime made of it.
///
/// `MetadataUpdater.ApplyUpdate` is process-wide and cannot be taken back, so it is asked as little as
/// possible: `check` reads everything that is known BEFORE the call (is the runtime able, did the process
/// start with the environment variable, is a debugger attached, is this module one the runtime can edit,
/// can the runtime do what this delta needs) and `apply` turns whatever the runtime then does into a closed
/// outcome. Neither throws.
///
/// What makes a module editable, from the runtime's source (dotnet/runtime `ceeload.cpp`
/// `IsEditAndContinueCapable`, `assemblynative.cpp` `AssemblyNative_ApplyUpdate`): the process was started
/// with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, the module's DebuggableAttribute disables optimization, and it
/// is not reflection-emit. The first is a property of the process and cannot be set once it is running.
namespace SageFs.Features.MetadataDelta

open System
open System.Diagnostics
open System.Reflection
open System.Reflection.Metadata
open System.Runtime.Loader

[<RequireQualifiedAccess>]
type UpdateSupport =
  | Supported
  | Unsupported

[<RequireQualifiedAccess>]
type ModifiableEnvironment =
  /// `DOTNET_MODIFIABLE_ASSEMBLIES=debug`.
  | Debug
  | Unset
  | OtherValue of value: string

[<RequireQualifiedAccess>]
type DebuggerState =
  | Detached
  | Attached

/// What this process can do for a delta, read once.
type RuntimeCapability =
  { Support: UpdateSupport
    Environment: ModifiableEnvironment
    Debugger: DebuggerState
    /// The runtime's capability names (`MetadataUpdater.GetCapabilities()`). Empty when they could not be read.
    Features: string list }

/// A reason a delta should not be handed to the runtime.
[<RequireQualifiedAccess>]
type CapabilityGap =
  | RuntimeUpdateUnsupported
  | EnvironmentNotModifiable of current: string
  | DebuggerAttached
  | MissingFeature of RequiredFeature
  /// The module was built with optimizations, so the runtime will not edit it.
  | ModuleOptimized of assembly: string
  /// A reflection-emit module, which is what FSI's own assemblies are.
  | ModuleIsDynamic of assembly: string

[<RequireQualifiedAccess>]
type CapabilityCheck =
  | Capable
  | Incapable of CapabilityGap list

/// What the runtime did with an apply.
[<RequireQualifiedAccess>]
type ApplyOutcome =
  | Applied
  /// The runtime took the delta, and a metadata-update handler threw while telling the process. The code is
  /// patched; some cache may still describe the old one.
  | AppliedHandlersFailed of failures: string list
  /// The runtime said the assembly cannot be edited (no environment variable, or an optimized module).
  | RuntimeNotModifiable of reason: string
  | DebuggerAttached
  /// The runtime refused for a reason of its own, carried as it said it.
  | NotSupported of reason: string
  /// The delta was malformed, or something else threw.
  | Rejected of reason: string

[<RequireQualifiedAccess>]
module DeltaApply =

  let private modifiableVariable = "DOTNET_MODIFIABLE_ASSEMBLIES"

  /// The runtime's own list. It is an internal method, so a runtime that renamed it answers with nothing and
  /// every delta then fails its feature check, which is the safe direction.
  let private runtimeFeatures () : string list =
    try
      match typeof<MetadataUpdater>.GetMethod("GetCapabilities", BindingFlags.NonPublic ||| BindingFlags.Static, null, Type.EmptyTypes, null) with
      | null -> []
      | method ->
        match method.Invoke(null, [||]) with
        | :? string as text -> text.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
        | _ -> []
    with _ -> []

  /// Read what this process can do.
  let capability () : RuntimeCapability =
    { Support =
        (match MetadataUpdater.IsSupported with
         | true -> UpdateSupport.Supported
         | false -> UpdateSupport.Unsupported)
      Environment =
        (match Environment.GetEnvironmentVariable modifiableVariable with
         | null -> ModifiableEnvironment.Unset
         | value when String.Equals(value, "debug", StringComparison.OrdinalIgnoreCase) -> ModifiableEnvironment.Debug
         | value -> ModifiableEnvironment.OtherValue value)
      Debugger =
        (match Debugger.IsAttached with
         | true -> DebuggerState.Attached
         | false -> DebuggerState.Detached)
      Features = runtimeFeatures () }

  let describeGap (gap: CapabilityGap) : string =
    match gap with
    | CapabilityGap.RuntimeUpdateUnsupported -> "this runtime does not support metadata updates"
    | CapabilityGap.EnvironmentNotModifiable current ->
      sprintf "the process was not started with %s=debug (it is %s)" modifiableVariable current
    | CapabilityGap.DebuggerAttached -> "a debugger is attached, and the runtime does not apply an update under one"
    | CapabilityGap.MissingFeature feature ->
      sprintf "the runtime does not report the %s capability" (RequiredFeature.capabilityName feature)
    | CapabilityGap.ModuleOptimized name -> sprintf "%s was built with optimizations, so the runtime will not edit it" name
    | CapabilityGap.ModuleIsDynamic name -> sprintf "%s is a reflection-emit module, which cannot take an update" name

  let private nameOf (assembly: Assembly) : string =
    match assembly.GetName().Name with
    | null -> "(unnamed assembly)"
    | name -> name

  /// Everything known before the call. A delta is handed to the runtime only when this says `Capable`.
  let check (capability: RuntimeCapability) (assembly: Assembly) (payload: DeltaPayload) : CapabilityCheck =
    let gaps =
      [ match capability.Support with
        | UpdateSupport.Supported -> ()
        | UpdateSupport.Unsupported -> yield CapabilityGap.RuntimeUpdateUnsupported
        match capability.Environment with
        | ModifiableEnvironment.Debug -> ()
        | ModifiableEnvironment.Unset -> yield CapabilityGap.EnvironmentNotModifiable "unset"
        | ModifiableEnvironment.OtherValue value -> yield CapabilityGap.EnvironmentNotModifiable value
        match capability.Debugger with
        | DebuggerState.Detached -> ()
        | DebuggerState.Attached -> yield CapabilityGap.DebuggerAttached
        match assembly.IsDynamic with
        | true -> yield CapabilityGap.ModuleIsDynamic (nameOf assembly)
        | false ->
          let optimized =
            match assembly.GetCustomAttribute<DebuggableAttribute>() with
            | null -> true
            | attribute -> not (attribute.DebuggingFlags.HasFlag DebuggableAttribute.DebuggingModes.DisableOptimizations)
          match optimized with
          | true -> yield CapabilityGap.ModuleOptimized (nameOf assembly)
          | false -> ()
        for feature in payload.Requires do
          match capability.Features |> List.contains (RequiredFeature.capabilityName feature) with
          | true -> ()
          | false -> yield CapabilityGap.MissingFeature feature ]
    match gaps with
    | [] -> CapabilityCheck.Capable
    | _ -> CapabilityCheck.Incapable gaps

  /// What every `[assembly: MetadataUpdateHandler(typeof<X>)]` in the process is told after an update: the
  /// runtime's own (it drops the member lists reflection cached, so a method the delta added shows up in
  /// `GetMethods`) and the app's (a framework that cached a route table or a reflection result). This is the
  /// contract `dotnet watch` keeps; an update that skips it leaves caches that still describe the old code.
  /// Returns what failed, by handler.
  let private runUpdateHandlers () : string list =
    let flags = BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic
    let handlers =
      AppDomain.CurrentDomain.GetAssemblies()
      |> Array.collect (fun a ->
        try
          a.GetCustomAttributes<MetadataUpdateHandlerAttribute>()
          |> Seq.map (fun attribute -> attribute.HandlerType)
          |> Seq.toArray
        with _ -> [||])
      |> Array.distinct
    [ for handler in handlers do
        for name in [ "ClearCache"; "UpdateApplication" ] do
          match handler.GetMethod(name, flags, null, [| typeof<Type[]> |], null) with
          | null -> ()
          | method ->
            try method.Invoke(null, [| null |]) |> ignore
            with e -> yield sprintf "%s.%s: %s" handler.FullName name (match e.InnerException with | null -> e.Message | inner -> inner.Message) ]

  /// Hand the delta to the runtime. Irreversible when it answers `Applied`.
  let apply (assembly: Assembly) (payload: DeltaPayload) : ApplyOutcome =
    match Debugger.IsAttached with
    | true -> ApplyOutcome.DebuggerAttached
    | false ->
      try
        MetadataUpdater.ApplyUpdate(assembly, ReadOnlySpan<byte>(payload.Metadata), ReadOnlySpan<byte>(payload.Il), ReadOnlySpan<byte>.Empty)
        match runUpdateHandlers () with
        | [] -> ApplyOutcome.Applied
        | failures -> ApplyOutcome.AppliedHandlersFailed failures
      with
      | :? InvalidOperationException as e -> ApplyOutcome.RuntimeNotModifiable e.Message
      | :? NotSupportedException as e -> ApplyOutcome.NotSupported e.Message
      | e -> ApplyOutcome.Rejected (sprintf "%s: %s" (e.GetType().Name) e.Message)

  /// The assembly a delta is for, found by name among the ones loaded in the default context.
  let loadedAssembly (name: string) : Assembly voption =
    AssemblyLoadContext.Default.Assemblies
    |> Seq.tryFind (fun a -> String.Equals(a.GetName().Name, name, StringComparison.Ordinal))
    |> function
      | Some a -> ValueSome a
      | None -> ValueNone
