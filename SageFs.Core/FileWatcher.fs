module SageFs.FileWatcher

open System
open System.IO

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
  DebounceMs = 500
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
  | FileChangeKind.Deleted -> FileChangeAction.Ignore
  | _ ->
    match ext with
    | ".fs" | ".fsx" -> FileChangeAction.Reload change.FilePath
    | ".fsproj" -> FileChangeAction.SoftReset
    | _ -> FileChangeAction.Ignore

/// Pure: decide if a file change should trigger a rebuild.
let shouldTriggerRebuild (config: WatchConfig) (filePath: string) : bool =
  let ext = Path.GetExtension(filePath).ToLowerInvariant()
  let fileName = Path.GetFileName(filePath)
  let isTemp =
    fileName.StartsWith("~")
    || fileName.EndsWith(".tmp")
    || filePath.Contains(sprintf "%cobj%c" Path.DirectorySeparatorChar Path.DirectorySeparatorChar)
    || filePath.Contains(sprintf "%cbin%c" Path.DirectorySeparatorChar Path.DirectorySeparatorChar)
  let isExcluded = shouldExcludeFile config.ExcludePatterns filePath
  not isTemp && not isExcluded && List.contains ext config.Extensions

/// Side-effectful: start watching directories for file changes.
/// Returns a dispose function that stops all watchers.
let start
  (config: WatchConfig)
  (onRebuildNeeded: FileChange -> unit)
  : IDisposable =

  let mutable pendingChange : FileChange option = None
  let lockObj = obj()

  let onTimer _ =
    let change =
      lock lockObj (fun () ->
        let c = pendingChange
        pendingChange <- None
        c)
    match change with
    | Some c -> onRebuildNeeded c
    | None -> ()

  let timer = new Threading.Timer(Threading.TimerCallback(onTimer), null, Threading.Timeout.Infinite, Threading.Timeout.Infinite)

  let watchers =
    config.Directories
    |> List.choose (fun dir ->
      if Directory.Exists(dir) then
        let watcher = new FileSystemWatcher(dir)
        watcher.IncludeSubdirectories <- false
        watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
        for ext in config.Extensions do
          watcher.Filters.Add(sprintf "*%s" ext)

        let handler (kind: FileChangeKind) (e: FileSystemEventArgs) =
          if shouldTriggerRebuild config e.FullPath then
            let change = {
              FilePath = e.FullPath
              Kind = kind
              Timestamp = DateTimeOffset.UtcNow
            }
            lock lockObj (fun () ->
              pendingChange <- Some change
              timer.Change(config.DebounceMs, Threading.Timeout.Infinite) |> ignore)

        watcher.Changed.Add(handler FileChangeKind.Changed)
        watcher.Created.Add(handler FileChangeKind.Created)
        watcher.Deleted.Add(handler FileChangeKind.Deleted)
        watcher.Renamed.Add(fun e -> handler FileChangeKind.Renamed e)
        watcher.EnableRaisingEvents <- true
        Some (watcher :> IDisposable)
      else
        None)

  { new IDisposable with
      member _.Dispose() =
        timer.Dispose()
        watchers |> List.iter (fun w -> w.Dispose()) }
