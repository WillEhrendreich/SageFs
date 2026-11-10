namespace SageFs

open SageFs.Features

/// How much of a value the live-bindings pane may run to show it. A config.fsx writes it (`ValueWalk = WalkEverything`);
/// the walk itself takes a `LiveValueTree.WalkMode`, and `ValueWalk.toWalkMode` is the one place the two meet.
type ValueWalk =
  /// Read fields, and run a getter only when its compiled body provably does nothing. Everything else is listed, with a click.
  | WalkSafe
  /// Run every readable public property, as the walk always did, under a deadline.
  | WalkEverything
  /// Do not open class instances at all.
  | WalkOff

/// A name that is not one of the choices, as an editor or a person typed it.
type UnknownValueWalk = UnknownValueWalk of given: string

module ValueWalk =
  /// Every choice, in the order the pane offers them.
  let all : ValueWalk list = [ WalkSafe; WalkEverything; WalkOff ]

  /// What a new session does when its config says nothing.
  let standard : ValueWalk = WalkSafe

  let name (choice: ValueWalk) : string =
    match choice with
    | WalkSafe -> "Safe"
    | WalkEverything -> "Everything"
    | WalkOff -> "Off"

  /// What choosing it means, for a tooltip: said plainly, including that Everything runs the user's getters.
  let consequence (choice: ValueWalk) : string =
    match choice with
    | WalkSafe -> "Reads fields and runs only getters that provably do nothing. Every other getter is listed, and runs only when you click it."
    | WalkEverything -> "Runs your getters: every public property of every class value, after every eval. That is your code running, and it can take time or change things."
    | WalkOff -> "Does not open class instances. Records, unions, tuples, lists and maps still show."

  /// Read a choice from its name, ignoring case. The names are exactly `name`'s, so config, the API and the pane spell it one way.
  let parse (text: string) : Result<ValueWalk, UnknownValueWalk> =
    match all |> List.tryFind (fun choice -> System.String.Equals(name choice, text, System.StringComparison.OrdinalIgnoreCase)) with
    | Some choice -> Result.Ok choice
    | None -> Result.Error(UnknownValueWalk text)

  let describeUnknown (UnknownValueWalk given: UnknownValueWalk) : string =
    sprintf "'%s' is not a way to walk values. The choices are: %s." given (all |> List.map name |> String.concat ", ")

  let toWalkMode (choice: ValueWalk) : LiveValueTree.WalkMode =
    match choice with
    | WalkSafe -> LiveValueTree.WalkMode.Safe
    | WalkEverything -> LiveValueTree.WalkMode.Everything
    | WalkOff -> LiveValueTree.WalkMode.Off

  let ofWalkMode (mode: LiveValueTree.WalkMode) : ValueWalk =
    match mode with
    | LiveValueTree.WalkMode.Safe -> WalkSafe
    | LiveValueTree.WalkMode.Everything -> WalkEverything
    | LiveValueTree.WalkMode.Off -> WalkOff

/// Which garbage collector a session's FSI host runs under. A config.fsx writes it (`Gc = ServerGc`). The cases are not called `Server`: a case with that name breaks the resolution of the namespace `SageFs.Server` in patterns.
/// Server GC commits a heap per core and is what allocation-heavy code runs under in production, so timing code
/// in the REPL under it shows the real number. WorkstationGc is what a session gets when the config says nothing.
type GcMode =
  | WorkstationGc
  | ServerGc

/// A name that is not one of the collectors, as an editor or a person typed it.
type UnknownGcMode = UnknownGcMode of given: string

module GcMode =
  /// Every choice, in the order a row offers them.
  let all : GcMode list = [ WorkstationGc; ServerGc ]

  /// What a session gets when nothing says otherwise.
  let standard : GcMode = WorkstationGc

  /// The one spelling of a choice, shared by config, the settings file and the page.
  let name (choice: GcMode) : string =
    match choice with
    | WorkstationGc -> "Workstation"
    | ServerGc -> "Server"

  /// What choosing it means, for a row's help line.
  let consequence (choice: GcMode) : string =
    match choice with
    | WorkstationGc -> "The runtime's default collector. Uses the least memory."
    | ServerGc -> "One heap and one collector thread per core, which is what allocation-heavy code runs under in production. It commits much more memory."

  /// Read a choice from its name, ignoring case.
  let parse (text: string) : Result<GcMode, UnknownGcMode> =
    match all |> List.tryFind (fun choice -> System.String.Equals(name choice, text, System.StringComparison.OrdinalIgnoreCase)) with
    | Some choice -> Result.Ok choice
    | None -> Result.Error(UnknownGcMode text)

  let describeUnknown (UnknownGcMode given: UnknownGcMode) : string =
    sprintf "'%s' is not a garbage collector. The choices are: %s." given (all |> List.map name |> String.concat ", ")

/// One environment variable for a session's FSI host, written `EnvEntry("NAME", "value")`. A union and not a tuple, because
/// a config is carried back from the host that evaluated it over a wire that has no tuples.
type EnvEntry = EnvEntry of name: string * value: string

/// Where a session's own builds write `bin` and `obj`. A config.fsx writes it (`Artifacts = ArtifactsIn "/abs/path"`). The
/// default leaves the project to say, as it always did.
type ArtifactsDir =
  | ProjectChooses
  | ArtifactsIn of path: string

/// Specifies how projects/solutions should be loaded for a session.
type LoadStrategy =
  /// Load a specific solution file (.sln/.slnx)
  | Solution of path: string
  /// Load specific project files (.fsproj)
  | Projects of paths: string list
  /// Auto-detect projects/solutions from the directory (default)
  | AutoDetect
  /// Bare FSI session — no project loading
  | NoLoad

/// Per-directory configuration via .SageFs/config.fsx: load strategy, extra FSI arguments, garbage collector and
/// environment for the session's host. Every field has a reader (`SessionOptions.fieldReaders` names it, and a test
/// fails for a field it does not), because a field that is accepted and ignored is worse than one that is absent.
///
/// Dependency-free by design: this file is compiled into SageFs.Core AND into the isolated FSI host, because a
/// user's config.fsx is arbitrary F# and is evaluated in the host (never in the daemon), which hands the value back
/// over the wire protocol. (The old Keybindings/ThemeOverrides fields belonged to the deprecated TUI and were
/// dropped from the config surface.)
type DirectoryConfig =
  { Load: LoadStrategy
    /// Extra arguments for the session's FSI host, after the ones SageFs builds (`--no-warn:1182`).
    DefaultArgs: string list
    AutoOpenNamespaces: bool
    /// The collector the session's FSI host runs under.
    Gc: GcMode
    /// Environment variables for the session's FSI host and nothing else. `SessionOptions.ofConfig` checks them.
    SessionEnv: EnvEntry list
    /// Where the session's builds write `bin` and `obj` (an absolute path).
    Artifacts: ArtifactsDir
    /// How much of a value the live-bindings pane may run to show it. Safe unless the config says otherwise.
    ValueWalk: ValueWalk }

/// The default configuration, independent of any module named DirectoryConfig (the daemon and the host each have
/// their own, and both point at this single definition).
module DirectoryConfigDefaults =
  let empty : DirectoryConfig =
    { Load = AutoDetect
      DefaultArgs = []
      AutoOpenNamespaces = true
      Gc = WorkstationGc
      SessionEnv = []
      Artifacts = ProjectChooses
      ValueWalk = ValueWalk.standard }
