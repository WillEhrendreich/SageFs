namespace SageFs.Features

open System
open System.Reflection

[<RequireQualifiedAccess>]
module ReflectionDiscovery =

  /// Dynamic FSI submissions produce dynamic assemblies; those throw
  /// NotSupportedException from GetExportedTypes. Discovery must skip them so
  /// REPL evaluation remains usable.
  let exportedTypes (asm: Assembly) : Type array =
    try asm.GetExportedTypes()
    with
    | :? ReflectionTypeLoadException -> [||]
    | :? TypeLoadException -> [||]
    | :? NotSupportedException -> [||]
