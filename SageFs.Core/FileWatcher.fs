module SageFs.FileWatcher

open System
open System.IO
open SageFs.Utils

/// The kind of file change detected.
[<RequireQualifiedAccess>]
type FileChangeKind =
  | Changed
  | Created
  | Deleted
  | Renamed
  /// Synthetic: the OS-level watch buffer overflowed and events were lost.
  /// Not a real file change — `FilePath` carries the watched directory that
  /// overflowed, not a file. Its own kind, rather than a fabricated .fsproj
  /// path pretending to be an ordinary change, so a consumer can tell "we
  /// don't know what changed" apart from "the project file changed" and say
  /// so instead of going quiet about it.
  | Overflow

/// A detected file change.
type FileChange = {
  FilePath: string
  Kind: FileChangeKind
  Timestamp: DateTimeOffset
}

/// Configuration for what to watch.
type WatchConfig = {
  Directories: string list
  Extensions: string list
  ExcludePatterns: string list
  DebounceMs: int
}

/// Pure: check if a file path matches any exclusion glob pattern.
/// Supports ** (any path segments), * (any chars in segment).
let shouldExcludeFile (patterns: string list) (filePath: string) : bool =
  let normalize (p: string) = p.Replace('\\', '/')
  let normalizedPath = normalize filePath
  patterns
  |> List.exists (fun pattern ->
    let normalizedPattern = normalize pattern
    let regex =
      normalizedPattern
        .Replace(".", "\\.")
        .Replace("**/", "(.+/)?")
        .Replace("**", ".*")
        .Replace("*", "[^/]*")
    System.Text.RegularExpressions.Regex.IsMatch(normalizedPath, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))

/// Pure: whether `path` sits inside a different checkout nested under the
/// watched `root` — some directory strictly between the root and the file holds
/// a checkout marker. A nested repository, a git worktree (agent worktrees under
/// .claude/worktrees, a vendored repo) is another project: its files must never
/// be attributed to the root's session. The root's own marker does not count —
/// a session's repository root is where its files live.
let isInNestedCheckout (root: string) (path: string) (hasCheckoutMarker: string -> bool) : bool =
  let rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
  let rec walk (dir: string) =
    match dir with
    | null | "" -> false
    | d when String.Equals(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase) -> false
    | d when not (d.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) -> false
    | d when hasCheckoutMarker d -> true
    | d -> walk (Path.GetDirectoryName d)
  walk (Path.GetDirectoryName(Path.GetFullPath path))

/// A checkout marker on disk: `.git` is a directory in a repository and a
/// file in a git worktree.
let hasCheckoutMarker (dir: string) : bool =
  let marker = Path.Combine(dir, ".git")
  Directory.Exists marker || File.Exists marker

/// Directory names that never need a live watch: build output, VCS
/// internals, package/editor caches, and generated test-run artifacts. A
/// recursive `FileSystemWatcher` has no way to skip a subtree — on Linux it
/// adds one inotify watch per directory it finds, no matter what's in it —
/// so these have to be pruned from the walk itself, not filtered out of the
/// events afterward. This is the whole story behind one daemon holding
/// 148,077 inotify watches (over a quarter of the default 524,288 system
/// limit) after every session watching it had already stopped: the watch
/// root was a checkout with per-project bin/obj trees and SageFs's own
/// .runs test-artifact directories underneath it.
///
/// `artifacts` was missing from this list and it matters far more than any
/// entry already in it: measured live on the F# compiler repo
/// (github.com/dotnet/fsharp, checked out at ~11GB), the Arcade-SDK build
/// root `artifacts/` held 74,582 of the tree's 76,596 directories —
/// `artifacts/Temp` alone was 73,377 — none of it source. Without this
/// entry, `watchableDirs` on that repo hits its own `MaxWatchableEntries`
/// cap with 49,081 of the 50,000 slots burned on `artifacts/Temp` scratch
/// dirs, silently (see that function's doc comment) — before this fix,
/// opening that repo could watch some or none of its real source
/// depending on directory enumeration order, and nothing said so. With
/// `artifacts` excluded the same repo needs 1,957 watchable directories,
/// two orders of magnitude under the cap. `artifacts` is Arcade's own
/// bin+obj+toolset root (dotnet/runtime, dotnet/aspnetcore, dotnet/fsharp,
/// and everything else built on Arcade uses it) — exactly the same shape
/// as `bin`/`obj`, just a convention this list didn't know about yet.
let excludedDirNames = set [ "bin"; "obj"; ".git"; ".vs"; ".idea"; "node_modules"; ".runs"; "artifacts" ]

/// Pure: should `dir` — and therefore everything under it — be pruned from
/// a recursive watch? Either its own name is a build/VCS/cache artifact, or
/// (when `dir` isn't the watch root itself) it is the root of a nested
/// checkout: a git worktree or vendored repo is another project, and gets
/// its own watch when IT needs one rather than inflating this one's count.
/// Mirrors `isInNestedCheckout`'s "the root's own marker doesn't count"
/// rule, but decides about the DIRECTORY ITSELF (for pruning a walk) rather
/// than about a file strictly beneath a marker (for filtering an event).
let shouldPruneDir (root: string) (dir: string) (hasCheckoutMarker: string -> bool) : bool =
  let trim (p: string) = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
  let rootFull = trim (Path.GetFullPath root)
  let dirFull = trim (Path.GetFullPath dir)
  let name = Path.GetFileName dirFull
  let isNestedCheckoutRoot =
    not (String.Equals(dirFull, rootFull, StringComparison.OrdinalIgnoreCase)) && hasCheckoutMarker dir
  Set.contains name excludedDirNames || isNestedCheckoutRoot

/// Impure (directory enumeration only, no watches): every directory under
/// `root` — including `root` itself — that a pruned recursive watch would
/// give its own watch to. A pruned directory's children are never visited,
/// so an excluded subtree costs one `Directory.GetDirectories` call, not
/// one inotify watch per file inside it. Used both to seed a pruned watch
/// and, on its own, as a pure-enough-to-test decision anyone can call
/// against a real directory tree.
///
/// Same defenses as `SafeDirectoryWalk.walkFiles`, and the same reason: a
/// live process dump caught a directory symlink cycle (Wine's
/// `dosdevices/z:` -> `/`, found under `~/.local/share/Steam/...`) sending
/// a walk into unbounded recursion when the daemon was started from
/// $HOME — this function is the daemon's OWN fallback-watcher seed
/// (`startPrunedWatcher`, rooted at the daemon's CWD), so it has the exact
/// same exposure `SafeDirectoryWalk` was built to close. A candidate
/// subdirectory that is itself a symlink is never descended into, and
/// every directory's canonical path is tracked across the whole walk so a
/// non-symlink cycle (two logical paths landing on the same real
/// directory) can't loop either. `MaxWatchableEntries` bounds the total
/// directories examined — loudly (`watchableDirs` below reports it), not
/// silently as this used to: on the un-pruned F# compiler repo this cap
/// was hit with 49,081 of its 50,000 slots burned on a single scratch
/// directory (`artifacts/Temp`) before ever finishing the real source tree
/// — a session opened there could silently get hot reload on some, or
/// none, of its files depending on directory-enumeration order, and
/// nothing said so. Bounding still beats growing forever the way
/// `Directory.EnumerateFiles(_, _, AllDirectories)` would; it just has to
/// say when it actually bites.
[<Literal>]
let MaxWatchableEntries = 50_000

/// The walk `watchableDirs` runs, parameterized on the cap so it can be
/// proven against a small tree in a test without waiting to grow one to
/// 50,000 real directories. Returns the survivors AND whether the cap was
/// reached before the frontier was exhausted — `true` means at least one
/// undiscovered directory (and everything under it) was silently dropped.
let watchableDirsCapped (cap: int) (root: string) (hasCheckoutMarker: string -> bool) : string list * bool =
  let rootFull = Path.GetFullPath root
  let visited = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
  let mutable examined = 0
  let mutable truncated = false
  let rec walk (dir: string) : string list =
    let full = try Path.GetFullPath dir with _ -> dir
    match examined >= cap, visited.Add full with
    | true, _ ->
      truncated <- true
      []
    | _, false -> []
    | false, true ->
      examined <- examined + 1
      let children =
        try
          Directory.GetDirectories dir
          |> Array.filter (fun sub ->
            not (SafeDirectoryWalk.isSymlinkDir sub) && not (shouldPruneDir rootFull sub hasCheckoutMarker))
        with ex ->
          Log.debug "[FileWatcher] Cannot enumerate %s: %s" dir ex.Message
          [||]
      dir :: (children |> Array.toList |> List.collect walk)
  match Directory.Exists rootFull with
  | true -> walk rootFull, truncated
  | false -> [], false

/// Impure (directory enumeration only, no watches): every directory under
/// `root` — including `root` itself — that a pruned recursive watch would
/// give its own watch to, capped at `MaxWatchableEntries`. If the cap is
/// actually hit, this is not a quiet best-effort result — it means real
/// directories under `root` will never be watched, so it is reported as
/// loudly as an inotify exhaustion (see `startPrunedWatcher`): one
/// `Log.error` naming the exact cap and root, plus a `ComponentWatch`
/// entry so `/health`, `/api/daemon-info` and `sagefs status` all see it
/// too, not just whoever happens to be reading this process's own log file.
let watchableDirs (root: string) (hasCheckoutMarker: string -> bool) : string list =
  let dirs, truncated = watchableDirsCapped MaxWatchableEntries root hasCheckoutMarker
  match truncated with
  | false -> ()
  | true ->
    let rootFull = try Path.GetFullPath root with _ -> root
    Log.error
      "[FileWatcher] Stopped walking %s after %d directories — the cap was reached before every directory was examined. Some directories under %s will NOT be watched (hot reload may silently miss them). This usually means a large generated/vendor/build-scratch directory needs its own entry in FileWatcher.excludedDirNames."
      rootFull MaxWatchableEntries rootFull
    SageFs.Features.ComponentWatch.reportFailure
      { Component = sprintf "file-watcher:%s" rootFull
        Reason = sprintf "directory walk stopped at the %d-directory cap before finishing %s" MaxWatchableEntries rootFull
        Hint = "Add the large directory's name to FileWatcher.excludedDirNames (e.g. a build/scratch root like `artifacts`), then restart the session." }
  dirs

/// Create a default watch config for the given directories.
let defaultWatchConfig dirs : WatchConfig = {
  Directories = dirs
  Extensions = [".fs"; ".fsx"; ".fsproj"]
  ExcludePatterns = []
  DebounceMs = 200
}

/// What action to take when a file changes.
/// Escalation: Reload (cheapest) → SoftReset → Ignore (no action).
[<RequireQualifiedAccess>]
type FileChangeAction =
  /// Re-eval the file via #load — FSI compiles it, shadows old definitions atomically.
  | Reload of filePath: string
  /// Project structure changed — soft reset to pick up new references.
  | SoftReset
  /// The watch buffer overflowed under `directory` — SageFs cannot know
  /// which files changed, so it resets the session as a precaution AND
  /// must say why (see Features.ReloadBroadcast.watcherOverflow). Kept
  /// distinct from `SoftReset` so a consumer never reports "project file
  /// changed" for a save it never actually saw.
  | RecoverFromOverflow of directory: string
  /// No action needed (e.g. file deleted, unrecognized extension).
  | Ignore

/// Pure: decide what action to take for a file change.
/// Deleted files are ignored — old definitions remain valid in FSI.
/// Source files (.fs/.fsx) are reloaded via #load (cheapest path).
/// Project files (.fsproj) trigger a soft reset (need new assembly refs).
let fileChangeAction (change: FileChange) : FileChangeAction =
  match change.Kind with
  | FileChangeKind.Overflow -> FileChangeAction.RecoverFromOverflow change.FilePath
  | FileChangeKind.Deleted ->
    Log.debug "[FileWatcher] Ignoring deleted file: %s" (Path.GetFileName change.FilePath)
    FileChangeAction.Ignore
  | FileChangeKind.Changed
  | FileChangeKind.Created
  | FileChangeKind.Renamed ->
    let ext = Path.GetExtension(change.FilePath).ToLowerInvariant()
    match ext with
    | ".fs" | ".fsx" -> FileChangeAction.Reload change.FilePath
    | ".fsproj" -> FileChangeAction.SoftReset
    | _ ->
      Log.debug "[FileWatcher] Ignoring %s — extension %s not watched" (Path.GetFileName change.FilePath) ext
      FileChangeAction.Ignore

/// Pure: decide if a file change should trigger a rebuild.
let shouldTriggerRebuild (config: WatchConfig) (filePath: string) : bool =
  let normalized = filePath.Replace('\\', '/')
  let ext = Path.GetExtension(normalized).ToLowerInvariant()
  let fileName = Path.GetFileName(normalized)
  let isTemp =
    fileName.StartsWith("~", System.StringComparison.Ordinal)
    || fileName.EndsWith(".tmp", System.StringComparison.Ordinal)
    || normalized.Contains("/obj/")
    || normalized.Contains("/bin/")
  let isExcluded = shouldExcludeFile config.ExcludePatterns filePath
  not isTemp && not isExcluded && List.contains ext config.Extensions

/// Pure: decide if a file change should be suppressed because the same
/// file was compiled too recently. Prevents double-compilation when
/// FileSystemWatcher fires duplicate events for a single save.
let shouldSuppressRecompile
  (guardMs: int)
  (lastCompiled: (string * DateTimeOffset) option)
  (current: FileChange) : bool =
  match lastCompiled with
  | None -> false
  | Some (path, ts) ->
    let sameFile = String.Equals(path, current.FilePath, StringComparison.OrdinalIgnoreCase)
    let elapsed = (current.Timestamp - ts).TotalMilliseconds
    sameFile && elapsed < float guardMs

/// Side-effectful: watch `root` recursively while pruning excluded subtrees
/// (see `shouldPruneDir`) — one non-recursive `FileSystemWatcher` per
/// surviving directory, added as new subdirectories appear and disposed
/// when their directory disappears, instead of .NET's own
/// `IncludeSubdirectories = true` (which cannot skip a subtree: on Linux it
/// adds one inotify watch per directory it finds under the root, no matter
/// what's in it). `onChange` and `onOverflow` see exactly the same events a
/// single recursive watcher would have raised — this only changes how many
/// OS-level watches it costs to raise them.
let startPrunedWatcher
  (root: string)
  (extensions: string list)
  (bufferSizeBytes: int)
  (onChange: FileChangeKind -> FileSystemEventArgs -> unit)
  (onOverflow: string -> unit)
  : IDisposable =
  let rootFull = Path.GetFullPath root
  let watchers = Collections.Generic.Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase)
  let treeLock = obj ()

  let rec addOne (dir: string) : unit =
    match lock treeLock (fun () -> watchers.ContainsKey dir) with
    | true -> ()
    | false ->
      try
        let watcher = new FileSystemWatcher(dir)
        watcher.IncludeSubdirectories <- false
        watcher.InternalBufferSize <- bufferSizeBytes
        watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
        for ext in extensions do
          watcher.Filters.Add(sprintf "*%s" ext)

        watcher.Changed.Add(onChange FileChangeKind.Changed)
        watcher.Created.Add(fun e ->
          onChange FileChangeKind.Created e
          // A newly created directory (or one moved in whole) can itself
          // contain a subtree — seed watchers for it exactly as the initial
          // walk would have, pruning the same way.
          match Directory.Exists e.FullPath with
          | true -> for sub in watchableDirs e.FullPath hasCheckoutMarker do addOne sub
          | false -> ())
        watcher.Deleted.Add(fun e ->
          onChange FileChangeKind.Deleted e
          removeSubtree e.FullPath)
        watcher.Renamed.Add(fun e ->
          onChange FileChangeKind.Renamed e
          // A renamed directory shows up here too (the entry lives in its
          // parent's watcher): drop whatever was watching the old name and
          // pick the new one up like a fresh subtree.
          removeSubtree e.OldFullPath
          match Directory.Exists e.FullPath with
          | true -> for sub in watchableDirs e.FullPath hasCheckoutMarker do addOne sub
          | false -> ())
        watcher.Error.Add(fun e ->
          let ex = e.GetException()
          Log.warn "[FileWatcher] Buffer overflow watching %s — events may have been lost. Cause: %s\n%s" dir ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          onOverflow rootFull)
        watcher.EnableRaisingEvents <- true
        lock treeLock (fun () -> watchers.[dir] <- watcher)
        Log.debug "[FileWatcher] Watching %s (pruned recursive watch of %s)" dir rootFull
      // A watcher failing to start for ONE directory (permission denied, a
      // race where the directory vanished between the walk and here) is a
      // per-directory concern — warn and move on, the rest of the tree is
      // unaffected. Running out of the OS's inotify budget is not that: it
      // will fail identically for every remaining directory in this walk
      // (and the next one), so logging the same "hot-reload disabled for
      // this directory" line hundreds of times is exactly the confident,
      // silent-ish wrong answer this exists to stop. Reported once, loudly,
      // naming the actual sysctl to raise — see ComponentWatch's own doc
      // comment for why this and the file-watcher's other failure mode
      // (the MaxWatchableEntries cap) share one registry.
      with
      | :? IOException as ex when ex.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase) ->
        Log.error
          "[FileWatcher] Out of inotify instances watching %s under %s: %s. Raise fs.inotify.max_user_instances (e.g. `sudo sysctl fs.inotify.max_user_instances=8192`, or persist it in /etc/sysctl.d/) and restart affected sessions. Hot reload is degraded for the rest of this tree until then."
          dir rootFull ex.Message
        SageFs.Features.ComponentWatch.reportFailure
          { Component = sprintf "file-watcher:%s" rootFull
            Reason = sprintf "inotify instance limit reached watching %s: %s" dir ex.Message
            Hint = "Raise fs.inotify.max_user_instances (sudo sysctl fs.inotify.max_user_instances=8192) and restart affected sessions." }
      | ex ->
        Log.warn "[FileWatcher] Cannot watch %s: %s — hot-reload disabled for this directory\n%s" dir ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")

  and removeSubtree (dir: string) : unit =
    lock treeLock (fun () ->
      let toRemove =
        watchers.Keys
        |> Seq.filter (fun k ->
          String.Equals(k, dir, StringComparison.OrdinalIgnoreCase)
          || k.StartsWith(dir + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        |> Seq.toList
      for k in toRemove do
        match watchers.TryGetValue k with
        | true, w ->
          w.EnableRaisingEvents <- false
          w.Dispose()
          watchers.Remove k |> ignore
        | false, _ -> ())

  match Directory.Exists rootFull with
  | true ->
    for d in watchableDirs rootFull hasCheckoutMarker do addOne d
    Log.info "FileWatcher started for %s: %d directory watch(es) after pruning bin/obj/.git/nested checkouts" rootFull watchers.Count
  | false -> ()

  { new IDisposable with
      member _.Dispose() =
        lock treeLock (fun () ->
          for KeyValue(_, w) in watchers do
            w.EnableRaisingEvents <- false
            w.Dispose()
          watchers.Clear()) }

/// Side-effectful: start watching directories for file changes.
/// Returns a dispose function that stops all watchers.
let start
  (config: WatchConfig)
  (devConfig: DevReload.DevReloadConfig)
  (onRebuildNeeded: FileChange -> unit)
  : IDisposable =

  let mutable pendingChanges : FileChange list = []
  let mutable lastCompiled : (string * DateTimeOffset) option = None
  let lockObj = obj()

  let onTimer _ =
    let changes =
      lock lockObj (fun () ->
        let cs = pendingChanges |> List.rev
        pendingChanges <- []
        cs)
    Log.info "FileWatcher debounce fired: %d pending changes" changes.Length
    for c in changes do
      match shouldSuppressRecompile devConfig.DoubleCompileGuardMs lastCompiled c with
      | true ->
        Log.info "FileWatcher suppressed duplicate compile for %s (within %dms guard)" c.FilePath devConfig.DoubleCompileGuardMs
      | false ->
        Instrumentation.fileWatcherChanges.Add(1L)
        let activity =
          Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle"
            [ ("trigger_type", box "file_change")
              ("file.path", box c.FilePath)
              ("file.change_kind", box (string c.Kind))
              ("file.change_at", box (c.Timestamp.ToString("o"))) ]
        try
          onRebuildNeeded c
          lastCompiled <- Some (c.FilePath, c.Timestamp)
        finally
          Instrumentation.succeedSpan activity

  let timer = new Threading.Timer(Threading.TimerCallback(onTimer), null, Threading.Timeout.Infinite, Threading.Timeout.Infinite)

  let enqueue (change: FileChange) =
    lock lockObj (fun () ->
      pendingChanges <- change :: (pendingChanges |> List.filter (fun c -> c.FilePath <> change.FilePath))
      timer.Change(config.DebounceMs, Threading.Timeout.Infinite) |> ignore)

  let watchers =
    config.Directories
    |> List.choose (fun dir ->
      match Directory.Exists(dir) with
      | true ->
        let handler (kind: FileChangeKind) (e: FileSystemEventArgs) =
          Log.info "FileWatcher raw event: %s %s" (string kind) e.FullPath
          match shouldTriggerRebuild config e.FullPath && not (isInNestedCheckout dir e.FullPath hasCheckoutMarker) with
          | true -> enqueue { FilePath = e.FullPath; Kind = kind; Timestamp = DateTimeOffset.UtcNow }
          | false -> ()
        let onOverflow (overflowDir: string) =
          // We cannot know which files changed, so we cannot reload just
          // one — the change carries the watched directory and its own
          // Overflow kind, so `fileChangeAction` routes it to a recovery
          // reset rather than pretending it was an ordinary edit.
          enqueue { FilePath = overflowDir; Kind = FileChangeKind.Overflow; Timestamp = DateTimeOffset.UtcNow }
        Some (startPrunedWatcher dir config.Extensions devConfig.FileWatcherBufferSizeBytes handler onOverflow)
      | false ->
        Log.debug "[FileWatcher] Skipping %s — directory does not exist" dir
        None)

  { new IDisposable with
      member _.Dispose() =
        timer.Dispose()
        watchers |> List.iter (fun w -> w.Dispose()) }
