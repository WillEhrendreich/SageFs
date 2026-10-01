namespace SageFs

open System

// ── SourceState: is the build this session runs behind the files on disk? ─────────
//
// Two different facts sit next to each other on every status surface, and a reader must not confuse them:
//
//   * `ReplFreshness` (SessionReload.fs): the REPL is BEHIND THE APP. A save was patched into the running app by a
//     metadata delta, and the FSI host the REPL and the tests run in kept the build from before it.
//   * `SourceState` (this file): the DISK IS AHEAD OF THE BUILD. A file the build was made from was written after that
//     build, so the host (and the tests in it) run code that is not what the files say.
//
// They can be true together, apart, or not at all. Neither implies the other.
//
// The decision is a pure function of what an edge read (when the worker loaded its build, when the build's output was
// written, when each source file was written) and of the session's rebuild record. Every IO failure arrives as a CASE
// (`StampRead.Unreadable`, `LoadedAt.NotReported`, `ProjectEvidence.NotInspectable`), so "could not tell" is its own
// answer and can never be read as "in sync".

/// A file's last-write time as the edge read it. A read that failed is a case, never a missing value.
[<RequireQualifiedAccess>]
type StampRead =
  | Written of at: DateTime
  | Unreadable of reason: string

/// One file and what the edge read of it.
type StampedFile = { Path: string; Stamp: StampRead }

/// What the edge found out about one project the session loaded.
[<RequireQualifiedAccess>]
type ProjectEvidence =
  /// The project's build output, and every file that builds into it (the project file included).
  | Inspected of project: string * output: StampedFile * sources: StampedFile list
  /// What builds into it could not be listed (project file unreadable or unparsable, a wildcard item, no loaded
  /// assembly that the project built).
  | NotInspectable of project: string * reason: string

/// When the worker loaded the build it runs.
[<RequireQualifiedAccess>]
type LoadedAt =
  | Reported of at: DateTime
  | NotReported of reason: string

/// Why a file counts as changed.
[<RequireQualifiedAccess>]
type StaleBecause =
  /// The file was written after the build this session runs was made.
  | EditedAfterBuild of editedAt: DateTime * builtAt: DateTime
  /// A newer build of the project exists on disk than the one this session loaded.
  | RebuiltAfterLoad of builtAt: DateTime * loadedAt: DateTime

type StaleFile = { Path: string; Because: StaleBecause }

/// Why nothing can be said about the build. Each case names what is missing.
[<RequireQualifiedAccess>]
type UnknownReason =
  /// Nobody looked: the receipt was read without a source check.
  | NotAssessed
  /// The session loaded no project, so there is no build to be behind.
  | NoProjectLoaded
  /// The worker did not say when it loaded its build.
  | LoadTimeNotReported of reason: string
  /// A file's write time could not be read.
  | Unreadable of path: string * reason: string
  /// What builds into a project could not be listed.
  | NotInspectable of project: string * reason: string

[<RequireQualifiedAccess>]
type SourceState =
  /// Nothing the build was made from has been written since the build the session runs. `builtAt` is the oldest
  /// build among the session's projects; `filesChecked` is how many files were compared.
  | InSync of builtAt: DateTime * filesChecked: int
  /// These files changed on disk after the build the session runs.
  | Stale of changed: StaleFile list
  /// A rebuild is in progress. The old worker keeps serving the build from before it.
  | Rebuilding of since: DateTime
  /// Could not tell, and why. Never to be read as in sync.
  | Unknown of reason: UnknownReason

[<RequireQualifiedAccess>]
module SourceState =

  /// The decision. Skeleton: it answers nothing yet.
  let decide (rebuild: LastRebuild) (loaded: LoadedAt) (projects: ProjectEvidence list) : SourceState =
    SourceState.Unknown UnknownReason.NotAssessed

  /// The worse of two readings of the same session, for a run that spans both (dispatched at one, finished at the
  /// other). Skeleton.
  let worse (earlier: SourceState) (later: SourceState) : SourceState = earlier

  /// The one wire spelling of each state.
  let token (state: SourceState) : string = ""

  /// What the state says, in a sentence a reader can act on without knowing the history.
  let describe (state: SourceState) : string = ""

  /// The warning a tool result carries, or nothing when the build is current.
  let banner (state: SourceState) : string = ""

  /// How the state appears in every status shape: always a field, never an absence a client has to read as "fine".
  let toWire (state: SourceState) : obj = box {| state = token state |}
