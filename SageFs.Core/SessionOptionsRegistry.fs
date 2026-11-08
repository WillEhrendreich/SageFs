namespace SageFs

open System
open System.Collections.Concurrent
open System.IO

/// What the daemon holds of each directory's session options, by directory. It is written once per session, when the session is
/// created (`DirectoryConfig.prepareForCreate`, the only writer, and a ratchet counts that). Two places that run before any
/// worker exists read it: the builds the daemon starts for a session (`SessionBuild.buildStartInfo`) and the spawn of the worker
/// (`WorkerSpawn.startWorkerProcess`, which hands the worker the options in one environment variable,
/// `SessionOptions.carrierVariable`). A directory with no entry builds where its project says.
///
/// An entry belongs to a directory and not to a session, because a session has no id until it is spawned. Two sessions in one
/// directory share the options of the later create.
module SessionOptionsRegistry =

  let private entries = ConcurrentDictionary<string, SessionOptions.Options>(StringComparer.Ordinal)

  let private keyOf (directory: string) : string = Path.TrimEndingDirectorySeparator(Path.GetFullPath directory)

  /// Record what a session created in this directory runs with. `SessionOptions.none` clears the entry.
  let remember (directory: string) (options: SessionOptions.Options) : unit =
    match options = SessionOptions.none with
    | true -> entries.TryRemove(keyOf directory) |> ignore
    | false -> entries[keyOf directory] <- options

  let optionsFor (directory: string) : SessionOptions.Options =
    match entries.TryGetValue(keyOf directory) with
    | true, options -> options
    | false, _ -> SessionOptions.none

  /// The environment a directory's worker is started with: the one carrier of its options.
  let workerEnvironment (directory: string) : (string * string) list =
    SessionOptions.workerEnvironment (optionsFor directory)
