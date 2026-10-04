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

  /// Mint a token. Conductor only, narrower than the minter, and the member is seated in the cohort
  /// as the preset's role. The raw token is drawn here, hashed here, and returned once: the table
  /// and the cohort command carry only the hash and the public id.
  let mintMember
    (ctx: McpContext)
    (store: CapabilityStore)
    (now: DateTime)
    (agentName: string)
    (role: string)
    (scope: string)
    (ttlMinutes: int)
    (workingDirectory: string option)
    : Task<Result<MintedMember, SageFsError>> =
    task {
      match parsePreset role, parseScope scope, lifetimeOf ttlMinutes with
      | Error e, _, _
      | _, Error e, _
      | _, _, Error e -> return Error e
      | Ok preset, Ok prefix, Ok lifetime ->
        // The CALLER's cohort: a token minted in a second repository is seated in THAT
        // repository's cohort, not the daemon's. Reading the daemon's own cohort refused the
        // mint outright with "no cohort owner is configured for this daemon" — a false
        // statement (one IS configured) that hid the real question.
        match cohortOwnerFor ctx workingDirectory with
        | None -> return Error (SageFsError.SessionCreationFailed "no cohort owner is configured for this daemon")
        | Some owner ->
          let minter =
            { Authority = authorityOf owner (memberIdFor agentName)
              // A conductor acting under a token mints under that token's grant; otherwise it holds the conductor's authority.
              Grant =
                match currentCapability.Value with
                | Some capability -> capability.Grant
                | None -> Grant.conductorAuthority }
          let requested = { Preset = preset; Scope = prefix; NotAfter = now + lifetime }
          let rawToken = Token.ofEntropy (RandomNumberGenerator.GetBytes Token.entropyBytes)
          match store.Mint(now, minter, requested, TokenHash.ofToken rawToken) with
          | Error refusal -> return Error(CohortErrorMapping.mintRefusalToSageFsError refusal)
          | Ok record ->
            let who = CapabilityId.memberId record.Id
            // The checkout the minted run works in, only when the conductor names one: resolving "the
            // conductor's own active session" would seat the run in the wrong checkout.
            let! sessionOpt =
              match workingDirectory with
              | Some _ -> McpCohortTools.resolveJoinSession ctx agentName workingDirectory
              | None -> Task.FromResult None
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
  let revokeMember
    (ctx: McpContext)
    (store: CapabilityStore)
    (now: DateTime)
    (agentName: string)
    (memberText: string)
    : Task<Result<string, SageFsError>> =
    task {
      // Resolve the caller's directory FIRST, and use it for BOTH the owner and the scope the
      // command is dispatched on. They were resolved separately — the owner with no directory
      // and the scope with one — so the authority check could read a different cohort from the
      // one the revoke landed in, which is the whole bug this feature is about.
      let! wd = McpCohortTools.callerWorkingDirectory ctx agentName None
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

  /// The tool names `tools/list` should show this caller: what its token's role may call, or, for a
  /// caller with no token, what the identity policy lets it call.
  let visibleToolNames
    (policy: IdentityPolicy)
    (seat: ConductorSeat)
    (authority: Cohort.Authority<MemberTable.MemberId>)
    (presented: ResolvedCapability option)
    (registered: string list)
    : string list =
    match presented with
    | Some capability -> visibleTools capability.Grant registered
    | None -> registered |> List.filter (fun tool -> Result.isOk (admitTokenless policy seat authority tool))

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
      | None -> "not bound to a session (pass working_directory to bind one)"
    String.concat
      "\n"
      [ sprintf "Minted member %s." (MemberTable.MemberId.display (CapabilityId.memberId record.Id))
        sprintf "  role:    %s (cohort role %A)" (RolePreset.toToken grant.Preset) (RolePreset.joinableRole grant.Preset)
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
