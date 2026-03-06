namespace SageFs.Features

open System
open System.Collections.Concurrent

/// Hash-based temporal dedup for eval submissions.
/// If the same code (by content hash) was evaluated within `windowMs`
/// for the same session, return the cached result instead of re-evaluating.
module EvalDedup =
  type DedupEntry = {
    Hash: int
    Result: string
    Timestamp: DateTimeOffset
  }

  type DedupCache = {
    Entries: ConcurrentDictionary<string, DedupEntry>
    WindowMs: int
  }

  module DedupCache =
    let create windowMs =
      { Entries = ConcurrentDictionary<string, DedupEntry>()
        WindowMs = windowMs }

    let defaultCache () = create 2000

    let private codeHash (code: string) = code.GetHashCode()

    let private makeKey (sessionId: string) (hash: int) =
      sprintf "%s:%08x" sessionId hash

    /// Try to get a cached result for the same code within the dedup window.
    let tryGet (cache: DedupCache) (sessionId: string) (code: string) (now: DateTimeOffset) : string option =
      let hash = codeHash code
      let key = makeKey sessionId hash
      match cache.Entries.TryGetValue(key) with
      | true, entry ->
        let age = (now - entry.Timestamp).TotalMilliseconds
        match age < float cache.WindowMs && entry.Hash = hash with
        | true -> Some entry.Result
        | false ->
          cache.Entries.TryRemove(key) |> ignore
          None
      | false, _ -> None

    /// Record a successful eval result.
    let record (cache: DedupCache) (sessionId: string) (code: string) (result: string) (now: DateTimeOffset) =
      let hash = codeHash code
      let key = makeKey sessionId hash
      cache.Entries.[key] <- { Hash = hash; Result = result; Timestamp = now }

    /// Evict stale entries older than 2x the window.
    let evictStale (cache: DedupCache) (now: DateTimeOffset) =
      let cutoff = float (cache.WindowMs * 2)
      for kvp in cache.Entries do
        match (now - kvp.Value.Timestamp).TotalMilliseconds > cutoff with
        | true -> cache.Entries.TryRemove(kvp.Key) |> ignore
        | false -> ()

    /// Clear all entries for a specific session (e.g., on reset).
    let clearSession (cache: DedupCache) (sessionId: string) =
      let prefix = sessionId + ":"
      for kvp in cache.Entries do
        match kvp.Key.StartsWith(prefix, StringComparison.Ordinal) with
        | true -> cache.Entries.TryRemove(kvp.Key) |> ignore
        | false -> ()
