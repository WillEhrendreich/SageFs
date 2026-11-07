namespace SageFs

/// Which MCP connection (or member token) created each session. `stop_session` refuses a session recorded to another
/// connection (`McpTools.sessionOwnership`, #184). Nothing here decides whether a person is asked: no tool that asks acts
/// on one session.
module SessionOwners =

  /// Sessions created by MCP, keyed by session id and owned by the bound transport MemberId. This is separate from
  /// SessionMap: switching a connection to an existing session must not transfer ownership. The value is the identity
  /// itself, never its display text. It lives in memory, so a session the daemon resumed after a restart has no owner.
  let owners = System.Collections.Concurrent.ConcurrentDictionary<string, MemberTable.MemberId>()

  let claim (owner: MemberTable.MemberId) (sessionId: string) =
    owners.TryAdd(sessionId, owner) |> ignore

  /// The identity the session was created under. None for a session made from the dashboard, the CLI or an editor,
  /// and for one the daemon resumed after a restart.
  let ownerOf (sessionId: string) : MemberTable.MemberId option =
    match owners.TryGetValue sessionId with
    | true, owner -> Some owner
    | false, _ -> None

  /// Drop a removed session's owner. The daemon calls this from `SessionManagerCallbacks.OnSessionRemoved`, so every
  /// route that removes a session releases it.
  let release (sessionId: string) =
    owners.TryRemove sessionId |> ignore
