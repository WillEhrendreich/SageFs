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
  /// No action needed (e.g. file deleted, unrecognized extension).
  | Ignore

/// Pure: decide what action to take for a file change.
/// Deleted files are ignored — old definitions remain valid in FSI.
/// Source files (.fs/.fsx) are reloaded via #load (cheapest path).
/// Project files (.fsproj) trigger a soft reset (need new assembly refs).
let fileChangeAction (change: FileChange) : FileChangeAction =
  let ext = Path.GetExtension(change.FilePath).ToLowerInvariant()
  match change.Kind with
  | FileChangeKind.Deleted ->
    Log.debug "[FileWatcher] Ignoring deleted file: %s" (Path.GetFileName change.FilePath)
    FileChangeAction.Ignore
  | _ ->
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

  let watchers =
    config.Directories
    |> List.choose (fun dir ->
      match Directory.Exists(dir) with
      | true ->
        try
          let watcher = new FileSystemWatcher(dir)
          watcher.IncludeSubdirectories <- true
          watcher.InternalBufferSize <- devConfig.FileWatcherBufferSizeBytes
          watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
          for ext in config.Extensions do
            watcher.Filters.Add(sprintf "*%s" ext)

          let handler (kind: FileChangeKind) (e: FileSystemEventArgs) =
            Log.info "FileWatcher raw event: %s %s" (string kind) e.FullPath
            match shouldTriggerRebuild config e.FullPath with
            | true ->
              let change = {
                FilePath = e.FullPath
                Kind = kind
                Timestamp = DateTimeOffset.UtcNow
              }
              lock lockObj (fun () ->
                pendingChanges <-
                  change :: (pendingChanges |> List.filter (fun c -> c.FilePath <> change.FilePath))
                timer.Change(config.DebounceMs, Threading.Timeout.Infinite) |> ignore)
            | false -> ()

          watcher.Changed.Add(handler FileChangeKind.Changed)
          watcher.Created.Add(handler FileChangeKind.Created)
          watcher.Deleted.Add(handler FileChangeKind.Deleted)
          watcher.Renamed.Add(fun e -> handler FileChangeKind.Renamed e)
          watcher.Error.Add(fun e ->
            Log.warn "[FileWatcher] Buffer overflow in %s — events may have been lost. Cause: %s" dir (e.GetException().Message))
          watcher.EnableRaisingEvents <- true
          Log.info "FileWatcher started for %s with %d filters, buffer=%dKB: %A" dir watcher.Filters.Count (watcher.InternalBufferSize / 1024) (Seq.toList watcher.Filters)
          Some (watcher :> IDisposable)
        with ex ->
          Log.warn "[FileWatcher] Cannot watch %s: %s — hot-reload disabled for this directory" dir ex.Message
          None
      | false ->
        Log.debug "[FileWatcher] Skipping %s — directory does not exist" dir
        None)

  { new IDisposable with
      member _.Dispose() =
        timer.Dispose()
        watchers |> List.iter (fun w -> w.Dispose()) }
