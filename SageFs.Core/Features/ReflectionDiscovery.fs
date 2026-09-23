namespace SageFs.Features

open System
open System.Reflection
open SageFs.Utils

[<RequireQualifiedAccess>]
module ReflectionDiscovery =

  /// Dynamic FSI submissions produce dynamic assemblies (Assembly.IsDynamic),
  /// and .NET's GetExportedTypes() reports no types at all for them — even
  /// when the submission's own top-level bindings ARE public (measured: an
  /// FSI interaction's own generated type is IsPublic/IsVisible, but
  /// GetExportedTypes() still returns an empty array for the whole dynamic
  /// assembly). A freshly FSI-defined `[<Tests>]` value would therefore never
  /// be discoverable no matter how eagerly the caller re-scans — so for a
  /// dynamic assembly, reflect with GetTypes() (which HotReloading.fs's own
  /// Harmony method-diffing already relies on for dynamic FSI assemblies)
  /// filtered to externally-visible types, instead of skipping discovery on
  /// dynamic assemblies outright. Non-dynamic assemblies (real compiled
  /// project DLLs) are completely unaffected — this only WIDENS what a
  /// dynamic assembly can report, it can never narrow a compiled assembly's
  /// result.
  ///
  /// A load failure here used to come back as a bare `[||]` — indistinguishable from "this assembly
  /// genuinely has no tests". That is a wrong answer dressed as a legitimate one: a caller sees "0 tests
  /// discovered" and has no way to tell "nothing here" from "couldn't load something". Every failure path
  /// now logs BEFORE returning the (still-empty) result, `ReflectionTypeLoadException` with its
  /// `LoaderExceptions` — the one piece of information that actually names which assembly/type failed to
  /// resolve, instead of forcing the next person to re-derive it from a zero.
  ///
  /// `FileNotFoundException`/`FileLoadException`/`BadImageFormatException` are caught here too, alongside
  /// the three this used to handle — live-verified on this runtime: when a type's BASE type (not merely a
  /// method signature) fails to resolve, `GetExportedTypes()` propagates the RAW resolution exception
  /// directly rather than wrapping it in `ReflectionTypeLoadException` (that wrapper is only observed for a
  /// PARTIAL failure — some types load, some don't; a single unresolvable type gives the bare exception).
  /// Before this fix that case wasn't silently swallowed to `[||]` — it wasn't caught AT ALL, so it could
  /// propagate out of discovery uncaught. See `SageFs.Tests.ReflectionDiscoveryTests` for the synthetic
  /// repro (a type whose base type lives in an assembly reference nothing can resolve) that proves both:
  /// discovery now reports the failure instead of returning an empty list, and the process never crashes.
  let exportedTypes (asm: Assembly) : Type array =
    let logAndCount (kind: string) (detail: string) =
      Log.warn "[ReflectionDiscovery] %s: could not enumerate exported types (%s): %s" asm.FullName kind detail
      SageFs.Instrumentation.liveTestingAssemblyLoadErrors.Add(1L)
    try
      match asm.IsDynamic with
      | true -> asm.GetTypes() |> Array.filter (fun t -> t.IsVisible)
      | false -> asm.GetExportedTypes()
    with
    | :? ReflectionTypeLoadException as ex ->
      let loaderDetail =
        ex.LoaderExceptions
        |> Array.choose (fun e -> if isNull e then None else Some e.Message)
        |> Array.distinct
        |> function
           | [||] -> "reason unknown — no LoaderExceptions reported"
           | messages -> String.concat "; " messages
      logAndCount (sprintf "ReflectionTypeLoadException, %d of %d types failed to load" (ex.Types |> Array.filter isNull |> Array.length) ex.Types.Length) loaderDetail
      [||]
    | :? TypeLoadException as ex ->
      logAndCount "TypeLoadException" ex.Message
      [||]
    | :? NotSupportedException as ex ->
      logAndCount "NotSupportedException" ex.Message
      [||]
    | :? IO.FileNotFoundException as ex ->
      logAndCount "FileNotFoundException — an assembly this one depends on could not be resolved" ex.Message
      [||]
    | :? IO.FileLoadException as ex ->
      logAndCount "FileLoadException — an assembly this one depends on could not be loaded" ex.Message
      [||]
    | :? BadImageFormatException as ex ->
      logAndCount "BadImageFormatException" ex.Message
      [||]
