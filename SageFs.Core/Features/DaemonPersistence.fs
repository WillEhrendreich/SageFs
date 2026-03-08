namespace SageFs.Features

open SageFs.Features.LiveTesting

/// Daemon-level coordination between domain types and binary file I/O.
/// Pure functions that bridge LiveTestState/SessionReplayState ↔ .sagetc/.sagefs files.
module DaemonPersistence =

  /// Compute a stable hash key from project paths for cache lookup.
  /// Order-independent, case-insensitive, path-separator-normalized.
  let projectHash (projects: string list) =
    let normalized =
      projects
      |> List.sort
      |> List.map (fun p -> p.Replace("\\", "/").ToLowerInvariant())
      |> String.concat "|"
    // FNV-1a hash → 8-char hex string
    let mutable h = 2166136261u
    for c in normalized do
      h <- h ^^^ (uint32 c)
      h <- h * 16777619u
    sprintf "%08x" h

  /// Save LiveTestState to .sagetc binary cache.
  let saveTestCache (sageFsDir: string) (projects: string list) (state: LiveTestState) : Result<string, string> =
    let hash = projectHash projects
    let stcData = TestCacheMapping.fromLiveTestState state
    TestCacheFile.save sageFsDir hash stcData

  /// Load LiveTestState from .sagetc binary cache.
  let loadTestCache (sageFsDir: string) (projects: string list) : Result<LiveTestState, string> =
    let hash = projectHash projects
    TestCacheFile.load sageFsDir hash
    |> Result.map TestCacheMapping.toLiveTestState

  /// Save session replay state to .sagefs binary.
  let saveSession
    (sageFsDir: string) (sessionId: string) (projectPath: string)
    (workingDir: string) (refs: string list) (state: Replay.SessionReplayState)
    : Result<string, string> =
    let sfsData = SessionMapping.fromReplayState sessionId projectPath workingDir refs state
    SessionFile.save sageFsDir sessionId sfsData

  /// Load session replay state from .sagefs binary.
  let loadSession (sageFsDir: string) (sessionId: string) : Result<Replay.SessionReplayState, string> =
    SessionFile.load sageFsDir sessionId
    |> Result.map SessionMapping.toReplayState

  /// Save daemon session manifest to .sagefm binary.
  let saveManifest (sageFsDir: string) (state: Replay.DaemonReplayState) : Result<string, string> =
    let data = ManifestMapping.fromReplayState state
    ManifestFile.save sageFsDir data

  /// Load daemon session manifest from .sagefm binary.
  /// W26(R12): Returns ManifestLoadError DU instead of stringly-typed error string.
  let loadManifest (sageFsDir: string) : Result<Replay.DaemonReplayState, ManifestTypes.ManifestLoadError> =
    ManifestFile.load sageFsDir
    |> Result.map ManifestMapping.toReplayState
