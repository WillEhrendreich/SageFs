namespace SageFs

/// The one definition of "this path is inside that directory".
///
/// A prefix test on the text (`StartsWith "/a/repo"`) says `/a/repo2/x.fs` is inside `/a/repo`,
/// because the sibling's name begins with the same characters. Containment is a question about
/// whole path segments: the root's segments must be the first segments of the path, so a segment
/// that merely starts with the root's last segment does not count.
///
/// Three callers decide this and used to decide it three ways:
///   * the repo-relative, `/`-separated, already-canonical paths of a cohort claim
///     (`Cohort.ClaimScope`) and of a capability grant (`Capability.ScopePrefix`), through
///     `isWithinCanonical`;
///   * the absolute filesystem paths of the live-test watcher and file router
///     (`LiveTestWatcherCore`), through `isWithin`;
///   * the absolute filesystem paths a session owns (`SessionSourceScope`), through
///     `isStrictlyUnder`.
///
/// Pure. It reads no file and follows no symlink: `Path.GetFullPath` resolves `.`, `..` and a
/// trailing separator on the text, and that is as far as it goes, as it always did here.
module PathContainment =

  open System
  open System.IO

  /// Whether two spellings that differ only in letter case name the same path segment.
  [<RequireQualifiedAccess>]
  type PathCase =
    | Sensitive
    | Insensitive

  module PathCase =
    /// Windows paths ignore case; the other platforms this ships on do not. Same choice
    /// `SessionSourceScope` made before this module existed.
    let ofCurrentOs () : PathCase =
      match OperatingSystem.IsWindows() with
      | true -> PathCase.Insensitive
      | false -> PathCase.Sensitive

    let comparison (pathCase: PathCase) : StringComparison =
      match pathCase with
      | PathCase.Sensitive -> StringComparison.Ordinal
      | PathCase.Insensitive -> StringComparison.OrdinalIgnoreCase

  /// `root` is `path`, or a leading run of its segments.
  let segmentsWithin (pathCase: PathCase) (root: string list) (path: string list) : bool =
    let comparison = PathCase.comparison pathCase
    let rec go (r: string list) (p: string list) =
      match r, p with
      | [], _ -> true
      | _ :: _, [] -> false
      | rs :: rest, ps :: more -> String.Equals(rs, ps, comparison) && go rest more
    go root path

  /// The segments of a `/`-separated repo-relative path. The canonical spelling has no empty
  /// segment, and the repo root (the empty string) has none at all.
  let canonicalSegments (canonical: string) : string list =
    canonical.Split('/', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

  /// `path` is `prefix` or under it, for paths already canonical (`Cohort.ClaimPath`): `/`
  /// separated, no `.` or `..`. The empty prefix is the whole repo. Case matters, as it
  /// always did for claims.
  let isWithinCanonical (prefix: string) (path: string) : bool =
    segmentsWithin PathCase.Sensitive (canonicalSegments prefix) (canonicalSegments path)

  /// The segments of an absolute filesystem path, after the platform resolves `.`, `..` and a
  /// trailing separator. A relative path is read against the current directory.
  let fullSegments (path: string) : string list =
    Path.GetFullPath(path).Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList

  /// `path` is `root` or lies under it. `/a/repo/` is `/a/repo`, `/a/repo/x.fs` is inside it and
  /// `/a/repo2/x.fs` is not.
  let isWithin (pathCase: PathCase) (root: string) (path: string) : bool =
    segmentsWithin pathCase (fullSegments root) (fullSegments path)

  /// `path` lies under `root` and is not `root` itself.
  let isStrictlyUnder (pathCase: PathCase) (root: string) (path: string) : bool =
    let rootSegments = fullSegments root
    let pathSegments = fullSegments path
    segmentsWithin pathCase rootSegments pathSegments
    && List.length pathSegments > List.length rootSegments

  /// How deep a directory sits. The more specific of two roots that both contain a path is the
  /// deeper one; text length is not depth (`/a/b/` is longer than `/a/b` and the same directory).
  let depth (path: string) : int = List.length (fullSegments path)
