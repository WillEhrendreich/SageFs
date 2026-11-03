namespace SageFs

/// Who has run code in each session. The prompt before a stop or a reset is skipped only when every eval a session has
/// had came from the agent that created it (`ConfirmationPolicy.SessionActivity`), so each way in that runs code records
/// itself here: the MCP and `/exec` eval in Mcp.fs, the dashboard's REPL box in DaemonMode.fs, and the deprecated TUI's
/// editor in SageFsEffectHandler.fs.
module SessionEvaluators =

  open SageFs.ConfirmationPolicy

  /// Every source that has run code in each session, for the life of the session. It only grows: a reset clears the
  /// definitions, and the person who made them may still be working in the session.
  let known = System.Collections.Concurrent.ConcurrentDictionary<string, Set<EvalSource>>()

  /// Record that `source` ran code in the session. Called before the eval goes out, so one that fails still counts.
  /// Every eval calls this, and after the first from a source the set is already right, so that case allocates nothing.
  let record (sessionId: string) (source: EvalSource) =
    match known.TryGetValue sessionId with
    | true, sources when Set.contains source sources -> ()
    | _ -> known.AddOrUpdate(sessionId, Set.singleton source, fun _ sources -> Set.add source sources) |> ignore

  let ofSession (sessionId: string) : Set<EvalSource> =
    match known.TryGetValue sessionId with
    | true, sources -> sources
    | false, _ -> Set.empty

  /// Drop a stopped session's record. `SessionOwners.release` calls this with the creator record, from
  /// `SessionManagerCallbacks.OnSessionRemoved`, so every route that removes a session releases both.
  let forget (sessionId: string) =
    known.TryRemove sessionId |> ignore

/// Which MCP connection (or member token) created each session. `stop_session` refuses a session recorded to another
/// connection (`McpTools.sessionOwnership`), and the confirmation gate skips its prompt only for a session recorded to
/// the caller (`McpServer.creatorOfSession`).
module SessionOwners =

  /// Sessions created by MCP, keyed by session id and owned by the bound transport MemberId. This is separate from
  /// SessionMap: switching a connection to an existing session must not transfer ownership. The value is the identity
  /// itself, never its display text: the confirmation gate compares it with the caller's identity. It lives in memory,
  /// so a session the daemon resumed after a restart has no owner.
  let owners = System.Collections.Concurrent.ConcurrentDictionary<string, MemberTable.MemberId>()

  let claim (owner: MemberTable.MemberId) (sessionId: string) =
    owners.TryAdd(sessionId, owner) |> ignore

  /// The identity the session was created under. None for a session made from the dashboard, the CLI or an editor,
  /// and for one the daemon resumed after a restart.
  let ownerOf (sessionId: string) : MemberTable.MemberId option =
    match owners.TryGetValue sessionId with
    | true, owner -> Some owner
    | false, _ -> None

  /// Drop a removed session's owner and its record of who ran code in it. The daemon calls this from
  /// `SessionManagerCallbacks.OnSessionRemoved`, so every route that removes a session releases it.
  let release (sessionId: string) =
    owners.TryRemove sessionId |> ignore
    SessionEvaluators.forget sessionId
