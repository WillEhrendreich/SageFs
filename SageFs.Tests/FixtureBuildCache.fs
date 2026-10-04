/// One `dotnet build` per hot-reload fixture per runtime, then copies. (The stub: the cases in FixtureBuildCacheTests
/// are red against it.)
module SageFs.Tests.FixtureBuildCache

open System.Threading.Tasks

/// Everything that decides the bytes a fixture build produces.
type BuildInputs =
  { Tree: (string * string) list
    Shared: (string * string) list
    Sdk: string
    Command: string list }

[<RequireQualifiedAccess>]
type Served =
  | FromCache
  | BuiltInPlace

type Cache = { Root: string }

module Cache =
  let create (root: string) : Cache = { Root = root }
  let clear (_cache: Cache) : unit = failwith "FixtureBuildCache.Cache.clear: not built yet"

let keyOf (_inputs: BuildInputs) : string = failwith "FixtureBuildCache.keyOf: not built yet"

let treeOf (_runDir: string) : (string * string) list = failwith "FixtureBuildCache.treeOf: not built yet"

let provide (_cache: Cache) (_inputs: BuildInputs) (_runDir: string) (_buildIn: string -> Task<Result<unit, string>>) : Task<Result<Served, string>> =
  failwith "FixtureBuildCache.provide: not built yet"
