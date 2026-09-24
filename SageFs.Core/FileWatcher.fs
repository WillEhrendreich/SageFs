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

/// Pure: would `path` be dropped by a recursive watch rooted at `root`
/// because SOME ancestor directory strictly between them (or `path`'s own
/// parent) is itself excluded (`shouldPruneDir`) — a build/VCS/cache name,
/// or a nested checkout root? Reuses `shouldPruneDir`'s exact rule so
/// event-time filtering and the old directory-walk's pruning agree on
/// exactly the same set of paths; unlike `isInNestedCheckout` (which only
/// checks checkout markers) this also catches the name-based exclusions.
let isUnderPrunedPath (root: string) (path: string) (hasCheckoutMarker: string -> bool) : bool =
  let rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
  let rec walk (dir: string) =
    match dir with
    | null | "" -> false
    | d when String.Equals(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase) -> false
    | d when not (d.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) -> false
    | d when shouldPruneDir rootFull d hasCheckoutMarker -> true
    | d -> walk (Path.GetDirectoryName d)
  walk (Path.GetDirectoryName(Path.GetFullPath path))

/// Pure: classify a `FileSystemWatcher.Error` exception into a short label
/// and an actionable hint. Before this, EVERY `Error` event — a genuine OS
/// buffer overflow, a permission-denied hit walking into a restricted
/// subdirectory, anything else — was logged under the single label "Buffer
/// overflow" and never reported to `ComponentWatch`, so `/health` stayed
/// "Healthy" while a watcher was silently losing events (reproduced live:
/// "[FileWatcher] Buffer overflow watching /tmp — events may have been
/// lost. Cause: Access to the path '/tmp/systemd-private-.../upower.service-
/// ...' is denied." repeated 7+ times with `/health` reporting `healthy:
/// true` throughout). A permission error is not a transient overflow: it
/// will keep recurring for as long as the watched root contains
/// permission-restricted subtrees (systemd's `PrivateTmp=` leaves ephemeral
/// 0700 `systemd-private-*` directories under `/tmp` that come and go as
/// services restart) — worth naming distinctly so an operator reads
/// "wrong watch root", not "flaky buffer".
let classifyWatcherError (ex: exn) : string * string =
  match ex with
  | :? InternalBufferOverflowException ->
    "buffer overflow",
    "The OS-level watch buffer overflowed under heavy file-change volume — raise DevReload.FileWatcherBufferSizeBytes if this recurs."
  | :? UnauthorizedAccessException
  | _ when ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) ->
    "permission denied",
    "A subdirectory under this root denies read access to the daemon's user (e.g. another user's or systemd's private temp directories) — a recursive watch will keep losing events under it. Point the session/fallback watch at a real project root, not a shared scratch directory like /tmp."
  | _ ->
    "watcher error",
    "The OS-level file watcher reported an error and may have lost events under this root."

/// The full handling of one `FileSystemWatcher.Error` event: log it, report
/// it into `ComponentWatch` (so `/health`, `/api/daemon-info` and `sagefs
/// status` all see a watcher that is losing events — this used to be the gap:
/// the log line fired every time but nothing outside this process's own log
/// file ever learned about it), and invoke the caller's `onOverflow` recovery
/// callback. Extracted from `startPrunedWatcher` so the wiring itself — not
/// just the pure classification above — is unit-testable without spinning up
/// a real OS-level `FileSystemWatcher`.
let handleWatcherError (rootFull: string) (onOverflow: string -> unit) (ex: exn) : unit =
  let kind, hint = classifyWatcherError ex
  Log.warn "[FileWatcher] %s watching %s — events may have been lost. Cause: %s\n%s" kind rootFull ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
  SageFs.Features.ComponentWatch.reportFailure
    { Component = sprintf "file-watcher:%s" rootFull
      Reason = sprintf "%s watching %s: %s" kind rootFull ex.Message
      Hint = hint }
  onOverflow rootFull

/// Side-effectful: watch `root` recursively for file changes, with excluded
/// subtrees (`shouldPruneDir` — bin/obj/.git/node_modules/.runs/artifacts,
/// nested checkouts) filtered out at EVENT time instead of never watched.
///
/// This used to be one non-recursive `FileSystemWatcher` per surviving
/// directory instead of .NET's own `IncludeSubdirectories = true`, reasoned
/// from "a recursive watch can't skip a subtree, so it wastes inotify
/// *watches* on bin/obj". True, but watches were never the resource that
/// mattered: `fs.inotify.max_user_watches` defaults to 524,288 on this
/// machine, and this design spent the OTHER, genuinely scarce one —
/// `fs.inotify.max_user_instances`, default 1024 — one FileSystemWatcher
/// at a time, which turned out to cost a real instance each in the actual
/// compiled runtime this daemon ships as (measured: 50 non-recursive
/// watchers = 50 inotify fds in a Release build; `dotnet fsi` was
/// independently checked first and misleadingly shares one fd across
/// thousands of watchers — that difference is real and is why this had to
/// be re-verified against a compiled binary before trusting it). Live
/// reproduction on this machine: a freshly built daemon opening a session
/// on the F# compiler repo (975 directories after pruning — itself down
/// from 75,402 before `artifacts` was added to `excludedDirNames`) still
/// hit "The configured user limit (1024) on the number of inotify
/// instances has been reached" partway through, because ordinary desktop
/// use (this editor, a compositor, a handful of other tools) already holds
/// a few dozen instances against the same per-real-UID budget.
///
/// One recursive `FileSystemWatcher` costs exactly ONE inotify instance no
/// matter how large the tree is (measured: 76,000 real directories, 1
/// inotify fd, 76,000 inotify watches — 14.6% of the watch budget, 0.1% of
/// nothing since it is one instance). Watching bin/obj/.git/artifacts too
/// (no subtree can be excluded from a single recursive watch) spends watches,
/// which are abundant, to save instances, which are not — and the events
/// those excluded subtrees raise are filtered right here, before `onChange`
/// ever sees them, so a build churning through `artifacts/` or `obj/` costs
/// this a cheap path-prefix check per event, never a reload.
let startPrunedWatcher
  (root: string)
  (extensions: string list)
  (bufferSizeBytes: int)
  (onChange: FileChangeKind -> FileSystemEventArgs -> unit)
  (onOverflow: string -> unit)
  : IDisposable =
  let rootFull = Path.GetFullPath root
  let noop = { new IDisposable with member _.Dispose() = () }
  match Directory.Exists rootFull with
  | false -> noop
  | true ->
    // Diagnostic-only: how many directories this watch conceptually covers,
    // and whether that walk itself hit MaxWatchableEntries (still worth
    // knowing — a tree with a legitimate, non-excluded subtree bigger than
    // 50,000 directories is a real oddity worth a loud report even though
    // the recursive watcher below needs no per-directory setup to work).
    // Pure directory enumeration: costs no inotify resources of its own.
    let watchableCount = (watchableDirs rootFull hasCheckoutMarker).Length
    try
      let watcher = new FileSystemWatcher(rootFull)
      watcher.IncludeSubdirectories <- true
      watcher.InternalBufferSize <- bufferSizeBytes
      watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
      for ext in extensions do
        watcher.Filters.Add(sprintf "*%s" ext)

      let guarded (kind: FileChangeKind) (e: FileSystemEventArgs) =
        match isUnderPrunedPath rootFull e.FullPath hasCheckoutMarker with
        | true -> ()
        | false -> onChange kind e

      watcher.Changed.Add(guarded FileChangeKind.Changed)
      watcher.Created.Add(guarded FileChangeKind.Created)
      watcher.Deleted.Add(guarded FileChangeKind.Deleted)
      watcher.Renamed.Add(guarded FileChangeKind.Renamed)
      watcher.Error.Add(fun e -> handleWatcherError rootFull onOverflow (e.GetException()))
      watcher.EnableRaisingEvents <- true
      Log.info "FileWatcher started for %s: one recursive watch (~%d directories in scope; bin/obj/.git/node_modules/.runs/artifacts/nested-checkouts filtered at event time, not walked separately)" rootFull watchableCount
      watcher :> IDisposable
    // Even one recursive watcher can fail if the per-real-UID inotify
    // instance budget is ALREADY exhausted by other processes when this
    // session starts — rare now that this needs exactly one instance
    // instead of one per directory, but still reported loudly rather than
    // silently leaving this session's hot reload dead. See ComponentWatch's
    // doc comment for why this and the MaxWatchableEntries cap share one
    // registry.
    with
    | :? IOException as ex when ex.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase) ->
      Log.error
        "[FileWatcher] Out of inotify instances watching %s: %s. Raise fs.inotify.max_user_instances (e.g. `sudo sysctl fs.inotify.max_user_instances=8192`, or persist it in /etc/sysctl.d/) and restart affected sessions. Hot reload is unavailable for this session until then."
        rootFull ex.Message
      SageFs.Features.ComponentWatch.reportFailure
        { Component = sprintf "file-watcher:%s" rootFull
          Reason = sprintf "inotify instance limit reached watching %s: %s" rootFull ex.Message
          Hint = "Raise fs.inotify.max_user_instances (sudo sysctl fs.inotify.max_user_instances=8192) and restart affected sessions." }
      noop
    | ex ->
      Log.warn "[FileWatcher] Cannot watch %s: %s — hot reload disabled for this session\n%s" rootFull ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      noop

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
