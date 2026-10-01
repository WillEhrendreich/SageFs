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

/// Per-directory configuration via .SageFs/config.fsx: load strategy, init script, default args.
///
/// Dependency-free by design: this file is compiled into SageFs.Core AND into the isolated FSI host, because a
/// user's config.fsx is arbitrary F# and is evaluated in the host (never in the daemon), which hands the value back
/// over the wire protocol. (The old Keybindings/ThemeOverrides fields belonged to the deprecated TUI and were
/// dropped from the config surface.)
type DirectoryConfig =
  { Load: LoadStrategy
    InitScript: string option
    DefaultArgs: string list
    AutoOpenNamespaces: bool
    /// When true, treat this directory as a session root — don't walk up to git/solution root.
    /// Use for monorepos where each subdirectory is an independent project.
    IsRoot: bool
    /// Optional friendly name for auto-created sessions. Defaults to the directory name.
    SessionName: string option
    /// How much of a value the live-bindings pane may run to show it. Safe unless the config says otherwise.
    ValueWalk: ValueWalk }

/// The default configuration, independent of any module named DirectoryConfig (the daemon and the host each have
/// their own, and both point at this single definition).
module DirectoryConfigDefaults =
  let empty : DirectoryConfig =
    { Load = AutoDetect
      InitScript = None
      DefaultArgs = []
      AutoOpenNamespaces = true
      IsRoot = false
      SessionName = None
      ValueWalk = ValueWalk.standard }
