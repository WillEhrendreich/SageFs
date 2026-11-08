namespace SageFs

open System
open System.Collections.Concurrent
open System.IO

/// What the daemon knows of each directory's session options, by directory. The daemon fills it every time it reads a
/// directory's `.SageFs/config.fsx` (`DirectoryConfig.load`), and two places that run before any worker exists read it: the
/// builds the daemon starts for a session (`SessionBuild.buildStartInfo`) and the spawn of the worker
/// (`WorkerSpawn.startWorkerProcess`). A directory the daemon never read, or whose config stopped asking for anything,
/// has no entry and the project builds where it always did. It is a cache of a pure function of a file, like
/// `ConfigHost`'s own.
module SessionOptionsRegistry =

  let private entries = ConcurrentDictionary<string, SessionOptions.Options>(StringComparer.Ordinal)

  let private keyOf (directory: string) : string = Path.TrimEndingDirectorySeparator(Path.GetFullPath directory)

  /// Record what a directory's config asks for. `SessionOptions.none` clears the entry.
  let remember (directory: string) (options: SessionOptions.Options) : unit =
    match options = SessionOptions.none with
    | true -> entries.TryRemove(keyOf directory) |> ignore
    | false -> entries[keyOf directory] <- options

  let artifactsFor (directory: string) : ArtifactsDir =
    match entries.TryGetValue(keyOf directory) with
    | true, options -> options.Artifacts
    | false, _ -> ProjectChooses

  /// The environment a directory's worker is started with, for what the daemon knows of it.
  let workerEnvironment (directory: string) : (string * string) list =
    match entries.TryGetValue(keyOf directory) with
    | true, options -> SessionOptions.workerEnvironment options
    | false, _ -> []
