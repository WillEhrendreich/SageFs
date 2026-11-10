namespace SageFs

open System
open System.Collections.Concurrent
open System.IO

/// What the daemon holds of session options, in two books.
///
/// The PENDING book is by directory: what the next session created there will run with. It is written once per create
/// (`DirectoryConfig.prepareForCreate`, the only writer, and a ratchet counts that), and it carries what was refused on the way
/// so a settings page can say so.
///
/// The HELD book is by session: the options a session was started with, fixed the first time its worker is spawned and kept for
/// the life of the session. A later create in the same directory writes the pending book and cannot reach a session that is
/// already running, so its restarts, rebuilds and resets read the options it began with. The daemon's builds find a session's
/// options by its directory and projects, which the manager already makes unique (one session per project set and directory).
/// A build with no session yet (a recovery build before a create) reads the pending book.
module SessionOptionsRegistry =

  type private Held =
    { Directory: string
      Projects: string list
      Options: SessionOptions.Options }

  let private pending = ConcurrentDictionary<string, SessionOptions.Resolution>(StringComparer.Ordinal)

  let private held = ConcurrentDictionary<string, Held>(StringComparer.Ordinal)

  let private keyOf (directory: string) : string = Path.TrimEndingDirectorySeparator(Path.GetFullPath directory)

  let private projectsKey (directory: string) (projects: string list) : string list =
    projects
    |> List.map (fun project -> Path.GetFullPath(project, directory))
    |> List.distinct
    |> List.sort

  /// Record what a session created in this directory will run with, and what was refused to get there. A resolution that asks
  /// for nothing and refused nothing clears the entry.
  let rememberResolution (directory: string) (resolution: SessionOptions.Resolution) : unit =
    match resolution.Options = SessionOptions.none && List.isEmpty resolution.Refusals with
    | true -> pending.TryRemove(keyOf directory) |> ignore
    | false -> pending[keyOf directory] <- resolution

  /// `rememberResolution` for options with no refusals and no source worth naming.
  let remember (directory: string) (options: SessionOptions.Options) : unit =
    rememberResolution directory { SessionOptions.unresolved with Options = options }

  /// What the next session created in this directory will run with.
  let optionsFor (directory: string) : SessionOptions.Options =
    match pending.TryGetValue(keyOf directory) with
    | true, resolution -> resolution.Options
    | false, _ -> SessionOptions.none

  /// How the directory's options were resolved at its last create: where each came from, and what was refused.
  let resolutionFor (directory: string) : SessionOptions.Resolution option =
    match pending.TryGetValue(keyOf directory) with
    | true, resolution -> Some resolution
    | false, _ -> None

  /// The options a session runs with. The first call for a session fixes them, from what is pending for its directory; every
  /// later call, a restart or a rebuild, returns the same value whatever has been created since.
  let optionsOfSession (sessionId: string) (projects: string list) (directory: string) : SessionOptions.Options =
    held.GetOrAdd(sessionId, fun _ -> { Directory = keyOf directory; Projects = projectsKey directory projects; Options = optionsFor directory }).Options

  /// What a build in this directory of these projects runs with: the live session's own options when one has them, else what is
  /// pending for the directory.
  let forBuild (projects: string list) (directory: string) : SessionOptions.Options =
    let directoryKey = keyOf directory
    let projectKey = projectsKey directory projects
    match held.Values |> Seq.tryFind (fun session -> session.Directory = directoryKey && session.Projects = projectKey) with
    | Some session -> session.Options
    | None -> optionsFor directory

  /// A session that is gone no longer holds options.
  let release (sessionId: string) : unit = held.TryRemove sessionId |> ignore

  /// The environment a session's worker is started with: the one carrier of its options.
  let workerEnvironmentOf (sessionId: string) (projects: string list) (directory: string) : (string * string) list =
    SessionOptions.workerEnvironment (optionsOfSession sessionId projects directory)
