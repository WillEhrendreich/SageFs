namespace SageFs.Features

open System
open System.Reflection

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
  let exportedTypes (asm: Assembly) : Type array =
    try
      match asm.IsDynamic with
      | true -> asm.GetTypes() |> Array.filter (fun t -> t.IsVisible)
      | false -> asm.GetExportedTypes()
    with
    | :? ReflectionTypeLoadException -> [||]
    | :? TypeLoadException -> [||]
    | :? NotSupportedException -> [||]
