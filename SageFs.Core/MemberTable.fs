namespace SageFs

/// Bound connection identity — Phase 0 item 4 of sagefs-multiagent-vision.md
/// §4.1: "identity is bound to the connection, not declared." Today an MCP
/// tool call's only identity is a caller-supplied `agentName` string
/// (McpTools.fs's `send_fsharp_code` etc.), so two sub-agents that both
/// forget to name themselves are one agent, and one agent that names itself
/// as another *is* that agent. `MemberId` is the correction: it names the
/// CONNECTION the call arrived on, which a caller cannot choose or forge.
module MemberTable =

  [<RequireQualifiedAccess>]
  type MemberId =
    /// A dashboard tab's SSE stream `clientId` (Dashboard.fs registers one
    /// per connection today via ConnectionTracker; this is the same id).
    | Browser of clientId: string
    /// The MCP SDK's own per-connection transport session id
    /// (`IMcpServer.SessionId`, McpServer.fs's `McpServerTracker` already
    /// keys on it) — bound by the request filter before a tool body runs,
    /// never supplied by the tool call's arguments.
    | Mcp of transportSessionId: string
    /// No bound connection was available: a direct in-process call (most
    /// unit tests, a stdio bridge, `curl`). Keyed on the caller-supplied
    /// name itself — the same identity a name-keyed lookup used before this
    /// module existed, so unbound callers are unaffected by binding MCP/
    /// Browser identity to their connection.
    | Minted of id: string

  module MemberId =
    /// Canonical string form used as the routing/presence key. `Minted`
    /// renders VERBATIM (no prefix) so an unbound caller's resolved key is
    /// byte-identical to its plain name — every existing name-keyed
    /// dictionary entry (tests included) keeps working unchanged. `Mcp` and
    /// `Browser` get distinguishing prefixes so two different connections
    /// can never collide with each other OR with a Minted name, even when
    /// their self-declared display names are identical.
    let display = function
      | MemberId.Browser clientId -> sprintf "browser:%s" clientId
      | MemberId.Mcp transportSessionId -> sprintf "mcp:%s" transportSessionId
      | MemberId.Minted id -> id
