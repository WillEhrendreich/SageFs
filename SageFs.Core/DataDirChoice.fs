namespace SageFs

open System
open System.IO

// Its own file, and the file before DaemonState.fs, so that finding the data directory does not run DaemonState's
// static initialiser. That one builds an HttpClient from `Timeouts`, and `Timeouts` fixes every scaled wait the
// moment it is first read, which has to be after the machine tier is settled from the profile in this directory.

/// Whose state a daemon owns: the user's own, or an isolated directory named by
/// SAGEFS_DATA_DIR (tests, throwaway daemons). The manifest dir and the log dir
/// both read this one decision, so a daemon that is isolated for one is isolated
/// for the other.
[<RequireQualifiedAccess>]
type DataDirChoice =
  | Isolated of dir: string
  | UserDefault

module DataDirChoice =
  /// The environment variable that requests isolation.
  [<Literal>]
  let envVar = "SAGEFS_DATA_DIR"

  /// Unset, empty and blank all mean "not isolated".
  let ofEnvValue (value: string | null) : DataDirChoice =
    match value with
    | null -> DataDirChoice.UserDefault
    | v when String.IsNullOrWhiteSpace v -> DataDirChoice.UserDefault
    | v -> DataDirChoice.Isolated (Path.GetFullPath v)

  let current () : DataDirChoice =
    ofEnvValue (Environment.GetEnvironmentVariable envVar)

  /// The data directory this process uses: the isolated one, else `~/.SageFs`.
  let dataDirectory () : string =
    match current () with
    | DataDirChoice.Isolated dir -> dir
    | DataDirChoice.UserDefault ->
      let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
      Path.Combine(home, ".SageFs")
