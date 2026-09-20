namespace SageFs

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
    SessionName: string option }

/// The default configuration, independent of any module named DirectoryConfig (the daemon and the host each have
/// their own, and both point at this single definition).
module DirectoryConfigDefaults =
  let empty : DirectoryConfig =
    { Load = AutoDetect
      InitScript = None
      DefaultArgs = []
      AutoOpenNamespaces = true
      IsRoot = false
      SessionName = None }
