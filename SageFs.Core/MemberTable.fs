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
    /// A run the conductor minted a capability token for (Capability.fs). The
    /// payload is the token's public fingerprint, a prefix of the SHA-256 of
    /// the token, never the token. It outranks the connection the call arrived
    /// on: sub-agents sharing one connection are one `Mcp` member but as many
    /// `Capability` members as there are tokens.
    | Capability of fingerprint: string
    /// The person who runs the daemon, acting through the operator credential (EdgeCredential.fs). There is
    /// one per daemon, so the case has no payload. It names who minted a token when no agent did.
    | Operator

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

    /// What the key is hashed under, so a key and a session id that happen to be the same text still name two members.
    let private clientKeyDomain = "sagefs-client-key:"

    /// The member id of the client that holds `key` (a stdio bridge's `ClientKey`): the same one-way fingerprint shape
    /// as a connection's, taken over the key under its own domain. The key stays with the bridge and the HTTP edge.
    let ofClientKey (key: string) : MemberId = MemberId.Mcp (fingerprintOfHandle (clientKeyDomain + key))

    /// What the operator prints as. The colon makes it the prefix form of the other kinds, with nothing after it.
    let operatorDisplay = "operator:"

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
      | MemberId.Capability fingerprint -> sprintf "cap:%s" fingerprint
      | MemberId.Operator -> operatorDisplay
      | MemberId.Minted id -> id

    /// The exact inverse of `display`. `Minted` renders verbatim, so anything without a recognized prefix
    /// reads back as `Minted`: total, no parse failure. The operator is the whole text `operator:` and
    /// nothing after it, so a name that merely starts with it is a name.
    let ofDisplay (raw: string) : MemberId =
      let after (prefix: string) = raw.Substring prefix.Length
      match raw with
      | _ when raw = operatorDisplay -> MemberId.Operator
      | _ when raw.StartsWith("browser:", System.StringComparison.Ordinal) -> MemberId.Browser(after "browser:")
      | _ when raw.StartsWith("mcp:", System.StringComparison.Ordinal) -> MemberId.Mcp(after "mcp:")
      | _ when raw.StartsWith("cap:", System.StringComparison.Ordinal) -> MemberId.Capability(after "cap:")
      | _ -> MemberId.Minted raw

  /// Why a client key the edge was given cannot be one.
  [<RequireQualifiedAccess>]
  type ClientKeyRefusal =
    | Blank
    | WrongLength of actual: int
    | NotBase64Url

  /// The key a stdio bridge holds for the life of its process and presents on every request it forwards, so the
  /// daemon can tell that the same client came back after a reconnect or a daemon restart. The MCP session id cannot
  /// say so: the daemon mints a new one each time the bridge re-initializes, and the member id used to be a
  /// fingerprint of that id, so a restart made the conductor a stranger to its own seat.
  ///
  /// The key is a bearer secret held by the bridge process, as the session id was. The daemon turns it into a member
  /// id at its HTTP edge and carries only the id from there on, so the key itself is in no claim, status text,
  /// ledger row or log line.
  module ClientKey =
    /// The HTTP header the bridge sends it in, on every request, including the ones after it re-initializes.
    let headerName = "X-SageFs-Client-Key"
    /// The claim the daemon's edge puts the derived member id in, for the MCP request filter to read.
    let claimType = "urn:sagefs:client-key-member"
    /// 256 bits.
    let private randomBytes = 32
    /// 32 bytes in base64url without padding.
    let length = 43

    let generate () : string =
      System.Security.Cryptography.RandomNumberGenerator.GetBytes randomBytes
      |> System.Convert.ToBase64String
      |> fun text -> text.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    /// The member a key names, or why it names none. A key that is present and wrong is refused at the edge: the
    /// request is not quietly treated as the session's own member, because that would split one client in two.
    let toMemberId (raw: string) : Result<MemberId, ClientKeyRefusal> =
      match raw with
      | null -> Result.Error ClientKeyRefusal.Blank
      | text when System.String.IsNullOrWhiteSpace text -> Result.Error ClientKeyRefusal.Blank
      | text when text.Length <> length -> Result.Error(ClientKeyRefusal.WrongLength text.Length)
      | text when not (text |> Seq.forall (fun c -> System.Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')) ->
        Result.Error ClientKeyRefusal.NotBase64Url
      | text -> Result.Ok(MemberId.ofClientKey text)

    let describe (refusal: ClientKeyRefusal) : string =
      match refusal with
      | ClientKeyRefusal.Blank -> "the client key is blank"
      | ClientKeyRefusal.WrongLength actual -> sprintf "the client key is %d characters, not the %d a bridge generates" actual length
      | ClientKeyRefusal.NotBase64Url -> "the client key holds a character outside A-Z, a-z, 0-9, '-' and '_'"

  /// Which name a call's member id comes from, strongest first. A member token IS the member, so sub-agents sharing
  /// one connection are as many members as they have tokens. Without one, a bridge's client key names the client
  /// across reconnects. Without that, the connection's own session id does, as it did before the key existed (HTTP
  /// clients, `sagefs call`). With none of them the caller-supplied name is all there is.
  module ConnectionIdentity =
    let resolve (capability: MemberId option) (clientKey: MemberId option) (transportHandle: string option) (agentName: string) : MemberId =
      match capability, clientKey, transportHandle with
      | Some who, _, _ -> who
      | None, Some who, _ -> who
      | None, None, Some handle when not (System.String.IsNullOrWhiteSpace handle) -> MemberId.ofConnectionHandle handle
      | None, None, _ -> MemberId.Minted agentName
