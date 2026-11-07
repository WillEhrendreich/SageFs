namespace SageFs

open System
open System.Security.Claims
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading.Tasks
open SageFs.McpTools
open SageFs.Capability

/// The conductor-only tools that mint and revoke per-run member tokens, the
/// transport edge that reads a token from a header or `_meta`, and the list
/// filter that hides what a token cannot call. The decisions are all in the
/// pure `Capability` module; this is the shell that draws the randomness, holds
/// the table and commits the member's seat to the cohort.
module McpCapability =

  /// What `mint_member` hands back: the token itself, shown once, and the record behind it.
  type MintedMember = {
    Token: string
    Record: CapabilityRecord
    Session: string option
  }

  let private failed (reason: string) (suggestion: string) = SageFsError.CohortActionFailed(reason, suggestion)

  let private parsePreset (raw: string) : Result<RolePreset, SageFsError> =
    RolePreset.tryParse raw
    |> Result.mapError (fun unknown ->
      failed
        (sprintf "unknown role '%s': expected one of %s." unknown.Given (String.concat ", " unknown.Expected))
        "Pass one of Observer, Analysis, Verifier or Implementer.")

  let private parseScope (raw: string) : Result<ScopePrefix, SageFsError> =
    ScopePrefix.tryParse (if isNull raw then "" else raw)
    |> Result.mapError CohortErrorMapping.pathRefusalToSageFsError

  /// 0 is the default lifetime. A negative lifetime is a mistake, and is refused rather than read as the default.
  let private lifetimeOf (ttlMinutes: int) : Result<TimeSpan, SageFsError> =
    match ttlMinutes with
    | 0 -> Ok Timeouts.capabilityDefaultLifetime
    | minutes when minutes < 0 ->
      Error(
        failed
          (sprintf "ttl_minutes is %d, and a lifetime in minutes cannot be negative." minutes)
          (sprintf "Pass 0 for the default (%d minutes) or a number of minutes up to %d." (int Timeouts.capabilityDefaultLifetime.TotalMinutes) (int Timeouts.capabilityMaxLifetime.TotalMinutes)))
    | minutes -> Ok(TimeSpan.FromMinutes(float minutes))

  /// The authority the caller holds in the cohort, from the owner's published frame.
  let private authorityOf (owner: Features.CohortOwner.Handle) (who: MemberTable.MemberId) =
    Affordances.authorityOfMember who (owner.ReadFrame())

  /// The minter an MCP caller is: the authority it holds in the cohort it mints into, and the grant it holds
  /// (a conductor acting under a token mints under that token's grant; otherwise it holds the conductor's authority).
  let callerMinter (agentName: string) (owner: Features.CohortOwner.Handle) : Minter =
    { Authority = authorityOf owner (memberIdFor agentName)
      Grant =
        match currentCapability.Value with
        | Some capability -> capability.Grant
        | None -> Grant.conductorAuthority }

  /// Mint a token. Conductor only, narrower than the minter, and the member is seated in the cohort
  /// as the preset's role. The raw token is drawn here, hashed here, and returned once: the table
  /// and the cohort command carry only the hash and the public id. `minterFor` says who is minting once the
  /// caller's cohort is known: an MCP call is `callerMinter agentName`, and the operator route is the operator.
  let mintMember
    (ctx: McpContext)
    (store: CapabilityStore)
    (now: DateTime)
    (agentName: string)
    (minterFor: Features.CohortOwner.Handle -> Minter)
    (role: string)
    (scope: string)
    (ttlMinutes: int)
    (requestedDirectory: string option)
    (requestedSession: string option)
    : Task<Result<MintedMember, SageFsError>> =
    task {
      // The binding is parsed first, because it decides the cohort too: a token for one session is seated in
      // the repository that session lives in, and a token for a checkout in that checkout's.
      let binding = RouteBinding.ofRequest requestedSession requestedDirectory |> Result.mapError CohortErrorMapping.bindRefusalToSageFsError
      let! boundDirectory =
        task {
          match binding with
          | Ok(RouteBinding.BoundToSession id) ->
            match WorkerProtocol.SessionId.validate id with
            | Error _ -> return Error(SageFsError.SessionNotFound id)
            | Ok sid ->
              let! info = ctx.SessionOps.GetSessionInfo sid
              match info with
              | Some session -> return Ok session.WorkingDirectory
              | None -> return Error(SageFsError.SessionNotFound id)
          | Ok(RouteBinding.BoundToCheckout root) -> return Ok(CheckoutRoot.value root)
          // `ofRequest` never answers Unbound; if it ever did, a token that routes anywhere is not minted.
          | Ok RouteBinding.Unbound -> return Error(CohortErrorMapping.bindRefusalToSageFsError BindRefusal.NothingNamed)
          | Error e -> return Error e
        }
      match parsePreset role, parseScope scope, lifetimeOf ttlMinutes, binding, boundDirectory with
      | Error e, _, _, _, _
      | _, Error e, _, _, _
      | _, _, Error e, _, _
      | _, _, _, Error e, _
      | _, _, _, _, Error e -> return Error e
      | Ok preset, Ok prefix, Ok lifetime, Ok route, Ok cohortDirectory ->
        let workingDirectory = Some cohortDirectory
        // The CALLER's cohort: a token minted in a second repository is seated in THAT
        // repository's cohort, not the daemon's. Reading the daemon's own cohort refused the
        // mint outright with "no cohort owner is configured for this daemon" — a false
        // statement (one IS configured) that hid the real question.
        match cohortOwnerFor ctx workingDirectory with
        | None -> return Error (SageFsError.SessionCreationFailed "no cohort owner is configured for this daemon")
        | Some owner ->
          let minter = minterFor owner
          let requested = { Preset = preset; Scope = prefix; Route = route; NotAfter = now + lifetime }
          let rawToken = Token.ofEntropy (RandomNumberGenerator.GetBytes Token.entropyBytes)
          match store.Mint(now, minter, requested, TokenHash.ofToken rawToken) with
          | Error refusal -> return Error(CohortErrorMapping.mintRefusalToSageFsError refusal)
          | Ok record ->
            let who = CapabilityId.memberId record.Id
            // The session the minted run is seated on: the one it is bound to, or the one in the checkout it is
            // bound to. Never "the conductor's own active session", which would seat the run in the wrong checkout.
            let! sessionOpt =
              match route with
              | RouteBinding.BoundToSession id -> Task.FromResult(Some id)
              | RouteBinding.BoundToCheckout _
              | RouteBinding.Unbound -> McpCohortTools.resolveJoinSession ctx agentName workingDirectory
            // The scope the minted member joins. Taken from the working directory it is joining
            // FROM, not from the process: two agents in two repositories are two cohorts, so the
            // conductor seat they contend for is the one in their own repository.
            //
            // Through `ScopeOf` — the same rule the status read and every command dispatch use.
            // It was `cohortScopeOf`, which is a plain walk-up, and the two disagree wherever
            // the repository is a linked WORKTREE: the mint seated `cap:…` in one cohort while
            // `get_cohort_status` read the other, so the member the mint had just created was
            // absent from the frame naming it.
            let scope = ScopeOf.ofWorkingDirectory workingDirectory
            let! seated = commitCohort ctx (Cohort.CohortCommand.Join(who, RolePreset.joinableRole preset, sessionOpt, scope))
            match seated with
            | Error e ->
              // The seat could not be made, so the token must not work: revoke it rather than leave a token with no member.
              store.Revoke(now, minter.Authority, record.Id) |> ignore
              return Error e
            | Ok _ ->
              // A token that only reads never evals, and the reaper renews a seat from recorded activity, so record
              // the member as active now; the request filter does the same on every call it admits.
              AgentActivityTracker.recordMemberActivity ctx.ActivityTracker who (Option.defaultValue "" sessionOpt) None None now
              return Ok { Token = rawToken; Record = record; Session = sessionOpt }
    }

  /// Revoke by the member id `get_cohort_status` prints (`cap:<id>`). The token stops working at
  /// once, the seat departs and its claims are orphaned, so the conductor can reassign them.
  let revokeMemberIn
    (ctx: McpContext)
    (store: CapabilityStore)
    (now: DateTime)
    (agentName: string)
    (memberText: string)
    (workingDirectory: string option)
    : Task<Result<string, SageFsError>> =
    task {
      // Resolve the caller's directory FIRST, and use it for BOTH the owner and the scope the
      // command is dispatched on. They were resolved separately — the owner with no directory
      // and the scope with one — so the authority check could read a different cohort from the
      // one the revoke landed in, which is the whole bug this feature is about.
      let! wd = McpCohortTools.callerWorkingDirectory ctx agentName workingDirectory
      match cohortOwnerFor ctx wd with
      | None -> return Error (SageFsError.SessionCreationFailed "no cohort owner is configured for this daemon")
      | Some owner ->
        let text = if isNull memberText then "" else memberText.Trim()
        // Anything that is not `cap:<id>` is a capability id that does not exist, so the refusal names what was asked.
        let target = CapabilityId(if text.StartsWith("cap:", StringComparison.Ordinal) then text.Substring 4 else text)
        let by = authorityOf owner (memberIdFor agentName)
        match store.Revoke(now, by, target) with
        | Error refusal -> return Error(CohortErrorMapping.revokeRefusalToSageFsError refusal)
        | Ok () ->
          // The token is already dead. Departing the seat is housekeeping; a seat that is already
          // gone is not a failure. The seat belongs to the caller's OWN scope, which is the `wd`
          // resolved above: a token minted in one repository is revoked in that repository's cohort.
          let! _ = commitCohort ctx (Cohort.CohortCommand.Depart(CapabilityId.memberId target, SageFs.ScopeOf.ofWorkingDirectory wd))
          return
            Ok(
              sprintf
                "Revoked %s: its token is refused from now on, its seat departed and its claims are orphaned (reassign_claim can hand them to another member)."
                (MemberTable.MemberId.display (CapabilityId.memberId target)))
    }

  /// `revokeMemberIn` for a caller that names no repository: the cohort of the session it works in, else the daemon's own.
  let revokeMember (ctx: McpContext) (store: CapabilityStore) (now: DateTime) (agentName: string) (memberText: string) : Task<Result<string, SageFsError>> =
    revokeMemberIn ctx store now agentName memberText None

  /// What a `tools/list` request presented. A member token that resolved narrows the list to its role, so it outranks
  /// an operator credential on the same request: a launcher that holds both asked to be listed as the member. The
  /// operator credential lists everything, and nothing presented lists what the policy allows. `Refused` operator
  /// credentials never get here: they are answered with `credential_status` instead.
  let listedForOf (operator: EdgeCredential.OperatorClaim option) (presented: ResolvedCapability option) : ListedFor =
    match presented, operator with
    | Some capability, _ -> ListedFor.Member capability.Grant
    | None, Some EdgeCredential.OperatorClaim.Recognized -> ListedFor.Operator
    | None, Some EdgeCredential.OperatorClaim.Refused
    | None, None -> ListedFor.NoCredential

  /// The marker the edge puts in the principal when the header holds something that cannot be a token.
  let private unreadableHeader = "unreadable"

  /// The authentication type of the identity the edge builds. The MCP SDK attaches a request's principal
  /// to the JSON-RPC message only when it is authenticated, so the identity must claim to be.
  let private authenticationType = "SageFsMemberToken"

  /// The principal the HTTP edge builds from the member-token header's value: a claim holding the
  /// token's HASH (never the token). The identity is authenticated, which is what makes the SDK carry
  /// it to the request filters, but it carries no NameIdentifier, `sub` or UPN claim, so it names no
  /// user and the SDK's session-to-user binding is neither engaged nor broken. The raw value goes no further.
  let principalOfHeader (headerValue: string) : ClaimsPrincipal =
    let claimValue =
      match String.IsNullOrWhiteSpace headerValue || headerValue.Length > CapabilityTransport.maxTokenLength with
      | true -> unreadableHeader
      | false -> TokenHash.value (TokenHash.ofToken (headerValue.Trim()))
    let identity = ClaimsIdentity(authenticationType)
    identity.AddClaim(Claim(CapabilityTransport.hashClaimType, claimValue))
    ClaimsPrincipal(identity)

  /// The request's principal with what the edge decided about its `Authorization` header: a second authenticated
  /// identity holding the operator claim. A request that presented nothing for the operator door keeps its principal.
  /// The identities that were not authenticated are dropped, because the SDK carries a principal only when its
  /// primary identity is authenticated, and the default one is not.
  let withOperatorPresentation (presentation: EdgeCredential.OperatorPresentation) (user: ClaimsPrincipal) : ClaimsPrincipal =
    match EdgeCredential.OperatorPresentation.claimOf presentation with
    | None -> user
    | Some claim ->
      let identity = ClaimsIdentity(authenticationType)
      identity.AddClaim(Claim(EdgeCredential.OperatorClaim.claimType, EdgeCredential.OperatorClaim.toValue claim))
      match presentation with
      | EdgeCredential.OperatorPresentation.Refused refusal ->
        identity.AddClaim(Claim(EdgeCredential.OperatorClaim.refusalClaimType, EdgeCredential.EdgeRefusal.message refusal))
      | EdgeCredential.OperatorPresentation.NotPresented
      | EdgeCredential.OperatorPresentation.Recognized -> ()
      let kept =
        match user with
        | null -> []
        | principal -> principal.Identities |> Seq.filter (fun existing -> existing.IsAuthenticated) |> List.ofSeq
      ClaimsPrincipal(kept @ [ identity ])

  /// The request's principal with the member the bridge's client key names: an authenticated identity holding the
  /// derived member id, never the key. RED stub: leaves the principal as it was.
  let withClientKey (who: MemberTable.MemberId) (user: ClaimsPrincipal) : ClaimsPrincipal = user

  /// The member the edge derived from a client key on this request, if the request carried one.
  /// RED stub: reads nothing.
  let clientKeyMemberOf (user: ClaimsPrincipal) : MemberTable.MemberId option = None

  /// What the edge decided about the operator credential on this request, if it was presented.
  let operatorClaimOf (user: ClaimsPrincipal) : EdgeCredential.OperatorClaim option =
    match user with
    | null -> None
    | principal ->
      match principal.FindFirst EdgeCredential.OperatorClaim.claimType with
      | null -> None
      | claim -> EdgeCredential.OperatorClaim.tryOfValue claim.Value

  /// Why the edge refused the operator credential on this request, in the words it chose.
  let operatorRefusalOf (user: ClaimsPrincipal) : string =
    match user with
    | null -> ""
    | principal ->
      match principal.FindFirst EdgeCredential.OperatorClaim.refusalClaimType with
      | null -> ""
      | claim -> claim.Value

  let private metaToken (meta: JsonObject) : string option =
    match meta with
    | null -> None
    | node ->
      match node.ContainsKey CapabilityTransport.metaKey with
      | false -> None
      | true ->
        match node.[CapabilityTransport.metaKey] with
        | null -> None
        | value ->
          match value.GetValueKind() with
          | System.Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
          // A value that is not a string is a token that cannot be one: refused, not ignored.
          | _ -> Some ""

  /// Which token a request presented: `_meta` (per call) before the header claim (per connection).
  let presentationFrom (user: ClaimsPrincipal) (meta: JsonObject) : CapabilityTransport.Presentation =
    let headerHash =
      match user with
      | null -> None
      | principal ->
        match principal.FindFirst CapabilityTransport.hashClaimType with
        | null -> None
        | claim -> Some claim.Value
    CapabilityTransport.presentationOf headerHash (metaToken meta)

  /// Resolve what a call presented. A token that is presented and bad is an error, never a fall-back to the connection.
  let presentToken (store: CapabilityStore) (now: DateTime) (presentation: CapabilityTransport.Presentation) : Result<ResolvedCapability option, SageFsError> =
    match presentation with
    | CapabilityTransport.Presentation.NoToken -> Ok None
    | CapabilityTransport.Presentation.Unreadable reason ->
      Error(
        failed
          (sprintf "The member token this call presented could not be read: %s." reason)
          (sprintf "Pass the token exactly as mint_member showed it, in the %s header or in _meta[\"%s\"]." CapabilityTransport.headerName CapabilityTransport.metaKey))
    | CapabilityTransport.Presentation.Presented hash ->
      store.Present(now, hash)
      |> Result.map Some
      |> Result.mapError CohortErrorMapping.presentRefusalToSageFsError

  /// The body of an HTTP refusal for a token that cannot be used: the OAuth error code a client recognises and the
  /// sentence `describeForAgent` already gives, so what reaches the model is the cause and the fix.
  type InvalidTokenBody = { Error: string; ErrorDescription: string }

  /// What the HTTP edge does with a request whose header names a token.
  [<RequireQualifiedAccess>]
  type EdgeAnswer =
    | Admit
    | Refuse of status: int * body: string

  /// The status for a token the daemon cannot use. A 401 is worse in every client measured (Claude Code starts an OAuth
  /// registration it cannot finish, Copilot CLI and opencode swap the body for a sign-in message), and only a 403 makes
  /// Claude Code re-run its `headersHelper` and retry the call. `docs/agent-identity-research-2026-11-01.md` has the runs.
  /// MCP 2026-07-28 says an invalid token MUST get a 401; SageFs is not an OAuth resource server, so it departs on purpose.
  let unusableTokenStatus = 403

  /// The name of the one tool a caller with an unusable token is shown.
  let credentialStatusToolName = "credential_status"

  /// The one tool `tools/list` holds for a caller whose token is unusable (#182). A host shows the model only what the
  /// list returned, so an empty list hides why. The description carries the refusal, which a model reads without calling
  /// the tool, and a call to it is refused at the edge with the same `invalid_token` body.
  let credentialStatusTool (refusal: SageFsError) : ModelContextProtocol.Protocol.Tool =
    let schema = System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()
    ModelContextProtocol.Protocol.Tool(
      Name = credentialStatusToolName,
      Description =
        sprintf
          "The member token this connection presents cannot be used. No other SageFs tool is listed until it is replaced. %s Calling this tool returns the same refusal."
          (SageFsError.describeForAgent refusal),
      InputSchema = schema
    )

  /// The same one tool for an operator credential the daemon refused (#181). `reason` is the edge's own sentence.
  let operatorStatusTool (reason: string) : ModelContextProtocol.Protocol.Tool =
    let schema = System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()
    ModelContextProtocol.Protocol.Tool(
      Name = credentialStatusToolName,
      Description =
        sprintf
          "The operator credential this connection presents cannot be used. No other SageFs tool is listed until it is replaced. %s Calling this tool returns the same refusal."
          reason,
      InputSchema = schema
    )

  /// What a request asks of the server, as far as the edge cares. A call is a JSON-RPC request that acts. Everything else
  /// is not a call: the messages a client uses to connect and list (`initialize`, `ping`, `tools/list`, notifications) and
  /// any request that is not a JSON-RPC message at all. Copilot CLI and opencode drop a server whose connect is refused and
  /// leave the model with no tool and no cause, so a bad token has to get through the connect and the list and be refused
  /// when it tries to act. The edge only chooses the status; the call filters still judge every call that reaches them.
  [<RequireQualifiedAccess>]
  type RpcPurpose =
    | NotACall
    | Call

  /// The two fields that make a body a JSON-RPC request.
  type RpcEnvelope = { Jsonrpc: string; Method: string }

  let private notificationPrefix = "notifications/"

  let private purposeOfMethod (methodName: string) : RpcPurpose =
    match methodName with
    | name when name = ModelContextProtocol.Protocol.RequestMethods.Initialize -> RpcPurpose.NotACall
    | name when name = ModelContextProtocol.Protocol.RequestMethods.Ping -> RpcPurpose.NotACall
    | name when name = ModelContextProtocol.Protocol.RequestMethods.ToolsList -> RpcPurpose.NotACall
    | name when name.StartsWith(notificationPrefix, StringComparison.Ordinal) -> RpcPurpose.NotACall
    | _ -> RpcPurpose.Call

  /// The purpose of a request body. A JSON-RPC request is judged by its method. A body that is not one (other routes,
  /// a reply, an unreadable text, a batch) is not a call here, because the edge only picks the status for the calls it can
  /// read and the filters refuse any call that gets past it.
  let purposeOfBody (body: string) : RpcPurpose =
    match Json.deserialize<RpcEnvelope> Json.camelCase body with
    | Ok envelope when not (isNull envelope.Jsonrpc) && not (isNull envelope.Method) -> purposeOfMethod envelope.Method
    | _ -> RpcPurpose.NotACall

  /// Decide, before MCP is spoken, whether a request that presented a token may go on. A request with no token is not
  /// decided here: the identity policy answers per tool. A token that is presented and unusable is refused with the status
  /// and body above when the request is a call, so a client sees the cause where it used to see nothing.
  let edgeAnswer (store: CapabilityStore) (now: DateTime) (purpose: RpcPurpose) (presentation: CapabilityTransport.Presentation) : EdgeAnswer =
    match presentation, purpose with
    | CapabilityTransport.Presentation.NoToken, _ -> EdgeAnswer.Admit
    | _, RpcPurpose.NotACall -> EdgeAnswer.Admit
    | (CapabilityTransport.Presentation.Unreadable _ | CapabilityTransport.Presentation.Presented _), RpcPurpose.Call ->
      match presentToken store now presentation with
      | Ok _ -> EdgeAnswer.Admit
      | Error refusal ->
        let body =
          Json.serialize
            Json.snakeCase
            { Error = "invalid_token"; ErrorDescription = SageFsError.describeForAgent refusal }
        EdgeAnswer.Refuse(unusableTokenStatus, body)

  /// The text the conductor reads once: the token, who it is for, what it may do, and how to present it.
  let private describe (minted: MintedMember) (tokenLine: string) : string =
    let record = minted.Record
    let grant = record.Grant
    let scopeText =
      match ScopePrefix.value grant.Scope with
      | "" -> "the whole repo"
      | dir -> dir
    let seatText =
      match minted.Session with
      | Some sid -> sprintf "bound to session %s" sid
      | None -> "no session is open in that checkout yet; the token can create one inside it"
    String.concat
      "\n"
      [ sprintf "Minted member %s." (MemberTable.MemberId.display (CapabilityId.memberId record.Id))
        sprintf "  role:    %s (cohort role %A)" (RolePreset.toToken grant.Preset) (RolePreset.joinableRole grant.Preset)
        sprintf "  route:   %s (a call that names another session or directory is refused)" (RouteBinding.describe grant.Route)
        sprintf "  scope:   %s (claims outside it are refused)" scopeText
        sprintf "  expires: %s, and lapses after %d minutes without a call" (grant.NotAfter.ToString "u") (int Cohort.leaseWindow.TotalMinutes)
        sprintf "  seat:    %s" seatText
        ""
        "TOKEN (shown once; SageFs keeps only its hash, so it cannot be shown again):"
        sprintf "  %s" tokenLine
        ""
        sprintf "Present it on every call in the %s HTTP header, or in the request's _meta[\"%s\"] when one connection speaks for several members; set %s for `sagefs mcp`. Send it in the header or _meta, never as a tool argument: transcripts keep arguments." CapabilityTransport.headerName CapabilityTransport.metaKey CapabilityTransport.bridgeEnvVar
        sprintf "Tokens do not survive a daemon restart. Cut one off with revoke_member member=%s." (MemberTable.MemberId.display (CapabilityId.memberId record.Id)) ]

  /// What the conductor reads once.
  let describeMinted (minted: MintedMember) : string = describe minted minted.Token

  /// The same text with the token replaced, for the daemon's own log.
  let describeMintedForLog (minted: MintedMember) : string = describe minted "(redacted: shown once to the conductor)"
