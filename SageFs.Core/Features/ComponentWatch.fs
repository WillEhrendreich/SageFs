namespace SageFs.Features

open System.Collections.Concurrent

/// A component the daemon depends on — the MCP server, a session's file
/// watcher — failed to start or degraded in a way its own log line never
/// reaches a client. Recorded here so a component that failed no longer
/// looks like a healthy daemon.
///
/// Concrete incident this exists for: a daemon whose file watcher silently
/// stopped walking a large repo (a build/scratch directory ate its whole
/// directory budget before reaching the repo's real source) kept reporting
/// "healthy" — hot reload was quietly dead for most of the tree and nothing
/// said so. And separately: `startMcpServer`'s own top-level exception
/// handler already logs a failure to a file nobody reads while the daemon
/// process keeps running with a fully dead MCP server. Both are the same
/// shape — a component returning a confident wrong answer instead of
/// reporting it couldn't do its job — so both report here, and `/health`,
/// `/api/daemon-info` and `sagefs status` all read the same list instead of
/// each growing their own ad-hoc flag.
module ComponentWatch =

  /// `Hint` is what the operator should actually run or check next — never
  /// just "something went wrong." `Component` is a short stable name
  /// ("mcp-server", "file-watcher:<root>") so a second failure for the same
  /// component replaces the first instead of piling up stale duplicates.
  type Failure = {
    Component: string
    Reason: string
    Hint: string
  }

  let private failures = ConcurrentDictionary<string, Failure>()

  let reportFailure (failure: Failure) : unit =
    failures[failure.Component] <- failure

  /// A component that was failing has recovered (or is being re-armed for a
  /// fresh attempt) — e.g. a new session's file watcher for a root that
  /// previously failed.
  let clear (componentName: string) : unit =
    failures.TryRemove(componentName) |> ignore

  let current () : Failure list =
    failures.Values |> List.ofSeq |> List.sortBy (fun f -> f.Component)

  /// For tests, and for a daemon that wants to start clean.
  let reset () : unit =
    failures.Clear()
