module SageFs.LiveTestWatcherStaleGuard

/// Pure stale-event decision for debounced file watchers (dispose/recreate
/// lifecycle). An event queued by dir `d` at `queuedEpoch` is stale when the
/// dir no longer resolves (None) or its current epoch advanced past the
/// queue-time epoch — meaning the watcher was stopped/recreated while the
/// event was debouncing, so the reload belongs to a dead generation and must
/// be dropped rather than dispatched to a fresh session claim.
///
/// Top-level module (not nested under FileWatcher) so FSI / REPL consumers
/// can resolve `LiveTestWatcherStaleGuard.isStaleEvent` by name — nested
/// modules inside a compiled module class are not reliably reachable.
let isStaleEvent
  (queuedDir: string option)
  (queuedEpoch: int64 option)
  (currentEpoch: string -> int64)
  : bool =
  match queuedDir, queuedEpoch with
  | Some d, Some queued -> currentEpoch d <> queued
  | Some _, None -> false
  | None, _ -> true
