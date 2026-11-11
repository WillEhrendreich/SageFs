namespace SageFs

/// What the MCP tool layer keeps for each session while the session lives, and the one function that
/// frees it when the session is gone. Out of `Mcp.fs`, which is over its line budget.
module McpSessionState =

  /// Per-session eval history: an `EvalStore` of up to 10,000 cells and 64 MiB each.
  let featureStates = System.Collections.Concurrent.ConcurrentDictionary<string, Features.FeatureHooks.FeaturePushState ref>()

  /// Per-session compilation context state (evaluated modules, file cache).
  let compilationStates =
    System.Collections.Concurrent.ConcurrentDictionary<string, Middleware.CompilationContext.CompilationState>()

  /// Per-session TypeLoadException diagnostic.
  /// When a TypeLoadException poisons an FSI session, the diagnostic string is stored here
  /// so that targeted_verify and get_fsi_status can report the compromised type identity.
  /// Cleared on hard_reset_fsi_session (which creates a fresh FSI session).
  let typeIdentityDiagnostics =
    System.Collections.Concurrent.ConcurrentDictionary<string, string>()

  /// Frees what is kept for one session once the session is gone. Without this a daemon that lives for
  /// days keeps the eval history of every session id it ever served. A hard reset keeps the eval history
  /// on purpose, so a reset does not call it.
  let releaseSessionState (sessionId: string) : unit =
    featureStates.TryRemove sessionId |> ignore
    compilationStates.TryRemove sessionId |> ignore
    typeIdentityDiagnostics.TryRemove sessionId |> ignore
