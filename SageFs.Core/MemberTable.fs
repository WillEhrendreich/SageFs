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
    /// One MCP connection, named by a fingerprint of its transport session id
    /// (`MemberId.ofConnectionHandle`), never by the handle itself: the handle
    /// is the SDK's bearer credential and must not appear in any output. Bound
    /// by the request filter before a tool body runs, never supplied by the
    /// tool call's arguments. `display` fingerprints a raw handle that was
    /// wrapped by hand, so a legacy ledger row cannot print one either.
    | Mcp of connection: string
    /// No bound connection was available: a direct in-process call (most
    /// unit tests, a stdio bridge, `curl`). Keyed on the caller-supplied
    /// name itself — the same identity a name-keyed lookup used before this
    /// module existed, so unbound callers are unaffected by binding MCP/
    /// Browser identity to their connection.
    | Minted of id: string

  module MemberId =
    /// A connection's public id is a one-way fingerprint of its bearer handle,
    /// never the handle. The MCP SDK binds a request to a session by looking up
    /// `Mcp-Session-Id`, and no authentication is configured, so the handle IS
    /// the credential: whoever reads it can act as that connection. Every cohort
    /// output prints member ids (status, frame, SSE rows, ledger, leases), so the
    /// id must not be the handle. SHA-256 of a 128-bit handle is not invertible,
    /// and a prefix of it is plenty to tell two connections apart.
    let private fingerprintMarker = "m-"
    let private fingerprintBytes = 8

    let private isFingerprint (text: string) =
      text.Length = fingerprintMarker.Length + 2 * fingerprintBytes
      && text.StartsWith(fingerprintMarker, System.StringComparison.Ordinal)
      && text.Substring(fingerprintMarker.Length) |> Seq.forall (fun c -> System.Uri.IsHexDigit c && not (System.Char.IsUpper c))

    let private fingerprintOfHandle (handle: string) : string =
      let digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes handle)
      fingerprintMarker + (digest |> Array.take fingerprintBytes |> Array.map (fun b -> b.ToString "x2") |> String.concat "")

    /// What a connection id prints as: a fingerprint, whatever it holds. A raw
    /// handle wrapped by hand (ledger rows written before the fingerprint
    /// existed replay as `Mcp <handle>`) is fingerprinted on the way out.
    let private publicConnectionId (held: string) : string =
      match isFingerprint held with
      | true -> held
      | false -> fingerprintOfHandle held

    /// The member id of the connection that holds `handle`. The handle stays
    /// inside the connection; the id is what the cohort, the ledger and every
    /// listing carry.
    let ofConnectionHandle (handle: string) : MemberId = MemberId.Mcp (fingerprintOfHandle handle)

    /// Canonical string form used as the routing/presence key. `Minted`
    /// renders VERBATIM (no prefix) so an unbound caller's resolved key is
    /// byte-identical to its plain name — every existing name-keyed
    /// dictionary entry (tests included) keeps working unchanged. `Mcp` and
    /// `Browser` get distinguishing prefixes so two different connections
    /// can never collide with each other OR with a Minted name, even when
    /// their self-declared display names are identical.
    let display = function
      | MemberId.Browser clientId -> sprintf "browser:%s" clientId
      | MemberId.Mcp connection -> sprintf "mcp:%s" (publicConnectionId connection)
      | MemberId.Minted id -> id
