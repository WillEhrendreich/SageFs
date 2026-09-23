namespace SageFs

open System
open System.IO

/// A bounded, symlink-cycle-proof directory walk — the primitive
/// `.fsproj`/`.fs` discovery should have been built on from the start.
///
/// The incident this exists for: the daemon's own RSS climbing ~2GB/minute
/// with zero sessions, project-independent, worst when started from $HOME.
/// The live process dump found the cause — a `System.String` 3,000+
/// characters long and growing, its `gcroot` a live thread inside
/// `List.collect`, walking `~/.local/share/Steam/steamapps/compatdata/.../
/// dosdevices/z:` — Wine maps that `z:` entry to `/`, so the walk goes
/// Steam -> dosdevices/z: -> / -> home -> .local -> share -> Steam ->
/// forever, manufacturing an ever-longer path string on every lap.
/// `Directory.EnumerateFiles(_, _, SearchOption.AllDirectories)` follows
/// directory symlinks with no cycle detection at all, and the three call
/// sites that used it (`DashboardTypes.discoverProjects`,
/// `Mcp.fs`'s private `discoverProjects`, `DaemonMode.fs`'s tree-sitter
/// file scan) all filtered noise (bin/obj/.git/...) AFTER the walk
/// finished, which discards unwanted results but never stops the walk
/// itself from reaching them.
module SafeDirectoryWalk =

  /// Caps for one walk: this repo's own worktree-laden checkout is ~1,300
  /// entries, 15 levels deep, so these are generous for any real project
  /// tree while still bounding a walk that starts somewhere unbounded (a
  /// user's $HOME is not a project tree) or that somehow still reaches a
  /// cycle these defenses didn't catch.
  type Bounds = {
    /// Refuse to descend more than this many directory levels below the root.
    MaxDepth: int
    /// Refuse to examine more than this many directory entries in total
    /// (directories and files together, summed across the whole walk).
    MaxEntries: int
  }

  module Bounds =
    let standard : Bounds = { MaxDepth = 64; MaxEntries = 50_000 }

  /// What a walk actually saw. `Truncated` must be surfaced to the caller,
  /// never swallowed — trusting a truncated `Files` list as if it were
  /// complete is the same failure mode as having no bound at all, just a
  /// quieter one. A caller that cares should say so (a log line, a status
  /// field), not silently ship a partial answer as gospel.
  type Result = {
    Files: string list
    Truncated: bool
  }

  /// Whether `dir` is itself a symlink (a directory reparse point) — never
  /// descended into, full stop. This alone stops the Steam/Wine case: `z:`
  /// is a symlink to `/`, so the walk simply never follows it. Checked
  /// against `DirectoryInfo.LinkTarget`, which is `null` for an ordinary
  /// directory and non-null for a symlink regardless of whether its target
  /// exists or is itself a loop. Public: `FileWatcher.watchableDirs` walks
  /// directories (not files) for a different reason (seeding a watch, not
  /// finding projects) but has the exact same symlink-cycle exposure, so it
  /// reuses this same one-line defense rather than a second copy of it.
  let isSymlinkDir (dir: string) : bool =
    try
      not (isNull (DirectoryInfo(dir).LinkTarget))
    with _ ->
      // Can't tell — refuse to descend rather than risk it. A directory we
      // can't even stat is not one we can safely walk into.
      true

  /// Walk `root` for files matching `isWanted` (given the file's full
  /// path), pruning a subtree BEFORE descending into it via `prune` (given
  /// a candidate subdirectory's full path) rather than filtering results
  /// after a full recursive enumeration — the whole point: an excluded
  /// subtree costs one `Directory.GetDirectories` call, not however many
  /// files live under it, and a symlink cycle a caller would otherwise
  /// wander into is never entered at all.
  ///
  /// Two independent defenses against cycles, because either one alone has
  /// a gap the other closes: `isSymlinkDir` refuses to follow ANY
  /// directory symlink (covers Wine's `z:` and every ordinary symlink
  /// loop), and `visited` tracks every directory's own canonical
  /// (`Path.GetFullPath`, case-insensitive) path across the WHOLE walk —
  /// so two different logical paths that resolve to the same real
  /// directory (a bind mount, `..`-shaped traversal) are only ever entered
  /// once, even if neither one is itself a symlink.
  ///
  /// `bounds` caps depth and total entries examined so a walk that starts
  /// somewhere genuinely unbounded, or reaches a cycle neither defense
  /// above catches, terminates on its own and says so via `Truncated`
  /// rather than running until something else (the process's own memory)
  /// stops it.
  let walkFiles
    (root: string)
    (isWanted: string -> bool)
    (prune: string -> bool)
    (bounds: Bounds)
    : Result =
    let visited = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let matched = ResizeArray<string>()
    let mutable examined = 0
    let mutable truncated = false

    let rec walk (dir: string) (depth: int) =
      if truncated then
        ()
      elif depth > bounds.MaxDepth then
        truncated <- true
      elif examined >= bounds.MaxEntries then
        truncated <- true
      else
        let full = try Path.GetFullPath dir with _ -> dir
        match visited.Add full with
        | false ->
          // Already walked this real directory via some other path —
          // a cycle, or two logical paths landing on the same place.
          // Either way, walking it again cannot find anything new.
          ()
        | true ->
          let entries =
            try Directory.GetFileSystemEntries dir
            with _ -> [||]
          let mutable i = 0
          while not truncated && i < entries.Length do
            let entry = entries.[i]
            i <- i + 1
            examined <- examined + 1
            if examined > bounds.MaxEntries then
              truncated <- true
            else
              let isDir = try Directory.Exists entry with _ -> false
              match isDir with
              | true ->
                match isSymlinkDir entry || prune entry with
                | true -> ()
                | false -> walk entry (depth + 1)
              | false ->
                if isWanted entry then matched.Add entry

    walk root 0
    { Files = matched |> List.ofSeq
      Truncated = truncated }
