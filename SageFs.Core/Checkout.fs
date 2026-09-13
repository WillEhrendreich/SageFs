namespace SageFs

open System
open System.IO

/// Git checkout identity, computed purely from the filesystem — no `git`
/// subprocess. Phase 0 item 3 of sagefs-multiagent-vision.md §3.2: a checkout
/// marker (`.git`, directory in a normal checkout, FILE in a worktree) is a
/// ROUTING BOUNDARY, and the worktree case needs its own root/branch, not the
/// main checkout's.
module Checkout =

  /// A checkout marker on disk: `.git` is a directory in a repository and a
  /// FILE (a "gitdir:" pointer) in a git worktree. Semantically the same
  /// check as SageFs.FileWatcher.hasCheckoutMarker; kept independent here
  /// because WorkerProtocol.fs (which needs this) compiles before
  /// FileWatcher.fs in SageFs.Core.fsproj — see the module doc on `classify`.
  let hasCheckoutMarker (dir: string) : bool =
    let marker = Path.Combine(dir, ".git")
    Directory.Exists marker || File.Exists marker

  [<RequireQualifiedAccess>]
  type Checkout =
    /// An ordinary repository checkout — `.git` is a directory at `root`.
    | MainCheckout of root: string
    /// A `git worktree`-created checkout — `.git` at `root` is a pointer
    /// file into the main checkout's `.git/worktrees/<name>` admin dir, and
    /// `branch` is that worktree's own checked-out branch (from its admin
    /// dir's own HEAD file, not the main checkout's).
    | Worktree of root: string * branch: string
    | NotAGitCheckout

  /// Walk up from `startDir` to the nearest checkout marker and classify what
  /// was found. FIXED (sagefs-multiagent-vision.md §1.6 #2): the previous
  /// `SessionInfo.findGitRoot` checked `Directory.Exists ".git"` only, so
  /// from inside a worktree it walked straight past the worktree's `.git`
  /// FILE to the main checkout's `.git` DIRECTORY further up and returned
  /// the wrong (main checkout's) root. This walk stops at the FIRST checkout
  /// marker of either shape.
  let classify (startDir: string) : Checkout =
    let rec walk (dir: string) =
      match String.IsNullOrEmpty dir with
      | true -> None
      | false ->
        match hasCheckoutMarker dir with
        | true -> Some dir
        | false ->
          let parent = Path.GetDirectoryName dir
          match isNull parent || parent = dir with
          | true -> None
          | false -> walk parent
    match walk startDir with
    | None -> Checkout.NotAGitCheckout
    | Some root ->
      let gitPath = Path.Combine(root, ".git")
      match Directory.Exists gitPath with
      | true -> Checkout.MainCheckout root
      | false ->
        // `.git` is a FILE: "gitdir: <path-to-this-worktree's-admin-dir>",
        // e.g. "gitdir: /repo/.git/worktrees/agent-x". That admin dir has
        // its OWN HEAD file naming this worktree's checked-out branch —
        // reading the main checkout's HEAD instead (the old bug) would
        // report every worktree as being on the main checkout's branch.
        try
          let contents = (File.ReadAllText gitPath).Trim()
          let prefix = "gitdir:"
          let adminDirRaw =
            match contents.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) with
            | true -> contents.Substring(prefix.Length).Trim()
            | false -> contents
          let adminDir =
            match Path.IsPathRooted adminDirRaw with
            | true -> adminDirRaw
            | false -> Path.GetFullPath(Path.Combine(root, adminDirRaw))
          let headPath = Path.Combine(adminDir, "HEAD")
          let branch =
            match File.Exists headPath with
            | false -> "unknown"
            | true ->
              let head = (File.ReadAllText headPath).Trim()
              let refPrefix = "ref: refs/heads/"
              match head.StartsWith(refPrefix, StringComparison.Ordinal) with
              | true -> head.Substring(refPrefix.Length)
              | false -> head // detached HEAD: the raw commit sha
          Checkout.Worktree(root, branch)
        with _ ->
          Checkout.Worktree(root, "unknown")

  /// The checkout's root directory, when it is a git checkout at all.
  let root = function
    | Checkout.MainCheckout r -> Some r
    | Checkout.Worktree(r, _) -> Some r
    | Checkout.NotAGitCheckout -> None

  let private lastSegment (path: string) =
    Path.GetFileName(path.TrimEnd('/', '\\'))

  /// Short human-readable label — used in list_sessions / the dashboard.
  let describe = function
    | Checkout.MainCheckout r -> lastSegment r
    | Checkout.Worktree(r, branch) -> sprintf "%s (%s)" (lastSegment r) branch
    | Checkout.NotAGitCheckout -> "(not a git checkout)"
