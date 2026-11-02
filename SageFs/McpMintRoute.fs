namespace SageFs

open SageFs.Capability
open SageFs.EdgeCredential

/// `POST /api/members/mint`: mint a member token without an MCP connection, for the person who runs the daemon or
/// the launcher they trust (slice 0 of docs/plans/a-token-for-every-daemon-call.md). The route needs the operator
/// credential, so the token goes from the daemon into an environment variable and never through a model. This
/// module holds what is decided without a server: who may call, what a refusal says, how the body is read, and
/// what a minted token's reply holds. McpServer.fs registers the route and runs it.
module McpMintRoute =

  [<Literal>]
  let Path = "/api/members/mint"

  /// The name the mint is made under. It reaches the cohort as the member's `agentName` only for sessions and
  /// activity, never as an identity: the identity that minted is `MemberId.Operator`.
  [<Literal>]
  let AgentName = "operator"

  /// Who mints: the operator, holding everything a conductor holds.
  let operatorMinter : Minter =
    { Authority = Cohort.Authority.Conductor MemberTable.MemberId.Operator
      Grant = Grant.conductorAuthority }

  /// What a request asks for, with the defaults `mint_member` takes: a blank string and a zero are "not given".
  /// `role` is required.
  type MintRequest =
    { Role: string
      Scope: string
      TtlMinutes: int
      WorkingDirectory: string
      SessionId: string }

  /// A request that names nothing but a role.
  let requestFor (role: string) : MintRequest = { Role = role; Scope = ""; TtlMinutes = 0; WorkingDirectory = ""; SessionId = "" }

  /// `""` for a field that is absent, null or not text.
  let textField (root: System.Text.Json.JsonElement) (name: string) : string =
    match root.ValueKind with
    | System.Text.Json.JsonValueKind.Object ->
      match root.TryGetProperty name with
      | true, value when value.ValueKind = System.Text.Json.JsonValueKind.String -> value.GetString()
      | _ -> ""
    | _ -> ""

  let numberField (root: System.Text.Json.JsonElement) (name: string) : int =
    match root.ValueKind with
    | System.Text.Json.JsonValueKind.Object ->
      match root.TryGetProperty name with
      | true, value when value.ValueKind = System.Text.Json.JsonValueKind.Number ->
        match value.TryGetInt32() with
        | true, n -> n
        | false, _ -> 0
      | _ -> 0
    | _ -> 0

  let parseRequest (body: string) : Result<MintRequest, SageFsError> =
    try
      use document = System.Text.Json.JsonDocument.Parse body
      let root = document.RootElement
      match root.ValueKind, textField root "role" with
      | System.Text.Json.JsonValueKind.Object, role when not (System.String.IsNullOrWhiteSpace role) ->
        Ok
          { Role = role
            Scope = textField root "scope"
            TtlMinutes = numberField root "ttl_minutes"
            WorkingDirectory = textField root "working_directory"
            SessionId = textField root "session_id" }
      | System.Text.Json.JsonValueKind.Object, _ -> Error(SageFsError.JsonParseError("request body", "'role' is required: Observer, Analysis, Verifier or Implementer."))
      | _ -> Error(SageFsError.JsonParseError("request body", "expected a JSON object."))
    with :? System.Text.Json.JsonException as ex -> Error(SageFsError.JsonParseError("request body", ex.Message))

  /// The request's optional text, as the `string option` `mintMember` takes.
  let given (text: string) : string option =
    match System.String.IsNullOrWhiteSpace text with
    | true -> None
    | false -> Some text

  /// A refusal the edge makes before any mint is tried. The body is the shape the editors already parse.
  type EdgeBody =
    { Error: string
      Message: string
      Case: string
      SuggestedAction: string }

  /// Whether the daemon could make the operator credential. A daemon that could not still answers the route,
  /// with the fix, instead of leaving it unregistered and the caller guessing.
  [<RequireQualifiedAccess>]
  type Verdict =
    | Admitted
    | Refused of status: int * body: EdgeBody

  let statusNumber (status: EdgeStatus) : int =
    match status with
    | EdgeStatus.Unauthorized -> 401
    | EdgeStatus.Forbidden -> 403

  /// The status the route answers when the credential file could not be made.
  [<Literal>]
  let UnavailableStatus = 503

  let unavailableBody (refusal: OperatorFile.Refusal) : EdgeBody =
    { Error = "operator_credential_unavailable"
      Message = OperatorFile.describe refusal
      Case = "operator_credential_unavailable"
      SuggestedAction = "Fix operator.token as the message says, then restart the daemon. Until then this route refuses every caller." }

  let refusalBody (refusal: EdgeRefusal) : EdgeBody =
    { Error = EdgeRefusal.errorCode refusal
      Message = EdgeRefusal.message refusal
      Case = EdgeRefusal.caseName refusal
      SuggestedAction = EdgeRefusal.suggestedAction refusal }

  /// May a request that carries `authorization` (the Authorization header's value, if any) mint?
  let verdictOf (access: OperatorStore.OperatorAccess) (authorization: string option) : Verdict =
    match access with
    | OperatorStore.OperatorAccess.Unavailable refusal -> Verdict.Refused(UnavailableStatus, unavailableBody refusal)
    | OperatorStore.OperatorAccess.Ready expected ->
      match decide expected (Presented.ofAuthorization authorization) EdgeAccess.OperatorOnly with
      | Ok _ -> Verdict.Admitted
      | Error refusal -> Verdict.Refused(statusNumber (EdgeRefusal.statusOf refusal), refusalBody refusal)

  /// What the caller gets for a mint. The token is here once and nowhere else.
  type MintReply =
    { Token: string
      MemberId: string
      Role: string
      Route: string
      Scope: string
      ExpiresAt: string
      /// The session the run is seated on, or "" when none is open in its checkout yet.
      Session: string }

  let replyOf (minted: McpCapability.MintedMember) : MintReply =
    let grant = minted.Record.Grant
    { Token = minted.Token
      MemberId = MemberTable.MemberId.display (CapabilityId.memberId minted.Record.Id)
      Role = RolePreset.toToken grant.Preset
      Route = RouteBinding.describe grant.Route
      Scope = ScopePrefix.value grant.Scope
      ExpiresAt = grant.NotAfter.ToString "o"
      Session = Option.defaultValue "" minted.Session }
