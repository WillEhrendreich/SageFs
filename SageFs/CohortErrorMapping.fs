namespace SageFs

/// Exhaustive, compiler-checked mapping from `Cohort.CohortError<MemberId>` onto
/// `SageFsError.CohortActionFailed(reason, suggestion)` — the one boundary
/// (roast §10) where the pure cohort module's error algebra crosses into the
/// product's. Each case builds an accurate `reason` (what went wrong, naming the
/// member/claim/landing) and an actionable `suggestion` (the next step), so an
/// agent reading the error gets cohort-specific guidance rather than the
/// mismatched session/worker advice a reused case would attach.
///
/// Split out of Mcp.fs, which is over its line budget: this is a pure function
/// of one DU and needs no `McpContext`.
module CohortErrorMapping =

  let toSageFsError (err: Cohort.CohortError<MemberTable.MemberId>) : SageFsError =
    let mid = MemberTable.MemberId.display
    let scopeStr (scope: Cohort.ClaimScope) =
      match scope with
      | Cohort.ClaimScope.File f -> sprintf "file:%s" f
      | Cohort.ClaimScope.Project p -> sprintf "project:%s" p
    let failed reason suggestion = SageFsError.CohortActionFailed(reason, suggestion)
    match err with
    | Cohort.CohortError.DuplicateJoin who ->
      failed
        (sprintf "%s is already a member of this cohort." (mid who))
        "Run get_cohort_status to see your current role; there is no need to join again."
    | Cohort.CohortError.MemberNotPresent who ->
      failed
        (sprintf "%s is not a present member of this cohort." (mid who))
        "Run join_cohort before acting in the cohort."
    | Cohort.CohortError.ClaimConflict(scope, holder) ->
      failed
        (sprintf "The scope %s is already claimed by %s." (scopeStr scope) (mid holder))
        "Coordinate with the current holder, or acquire a different, non-overlapping scope."
    | Cohort.CohortError.NotClaimHolder(Cohort.ClaimId cid, requester) ->
      failed
        (sprintf "%s does not hold claim %s." (mid requester) cid)
        "Acquire it with acquire_claim, or ask the current holder to release it."
    | Cohort.CohortError.UnknownClaim(Cohort.ClaimId cid) ->
      failed
        (sprintf "No claim %s exists in this cohort." cid)
        "Run get_cohort_status to list the current claims and their ids."
    | Cohort.CohortError.ClaimNotOrphaned(Cohort.ClaimId cid) ->
      failed
        (sprintf "Claim %s is not orphaned, so it cannot be reassigned." cid)
        "Only a claim whose holder has departed can be reassigned; check get_cohort_status."
    | Cohort.CohortError.DuplicateClaimId(Cohort.ClaimId cid) ->
      failed
        (sprintf "A claim with id %s already exists." cid)
        "Retry the acquire; claim ids are minted per acquire_claim."
    | Cohort.CohortError.StaleClaimFence(Cohort.ClaimId cid, presented, current) ->
      failed
        (sprintf "Your fence for claim %s is stale — you presented %d but the current fence is %d." cid (int64 presented) (int64 current))
        "Re-read get_cohort_status and retry with the claim's current fence."
    | Cohort.CohortError.InvalidPurpose reason ->
      failed
        (sprintf "The claim purpose is invalid: %s" reason)
        "Provide a non-empty purpose describing why you are claiming the scope."
    | Cohort.CohortError.InvalidStatement reason ->
      failed
        (sprintf "The landing statement is invalid: %s" reason)
        "Provide a non-empty statement describing what this landing changes."
    | Cohort.CohortError.UnknownLanding(Cohort.LandingId lid) ->
      failed
        (sprintf "No landing %s exists in this cohort." lid)
        "Run get_cohort_status to list the current landings."
    | Cohort.CohortError.DuplicateLandingId(Cohort.LandingId lid) ->
      failed
        (sprintf "A landing with id %s already exists." lid)
        "Retry the request; landing ids are minted per request_landing."
    | Cohort.CohortError.NotLandingRequester(Cohort.LandingId lid, who) ->
      failed
        (sprintf "%s did not request landing %s." (mid who) lid)
        "Only the landing's requester can act on it; check get_cohort_status."
    | Cohort.CohortError.LandingNotAtFrontOfQueue(Cohort.LandingId lid) ->
      failed
        (sprintf "Landing %s is not at the front of the landing queue." lid)
        "Landings are strictly serial; wait until the earlier landings ahead of it complete."
    | Cohort.CohortError.LandingNotInExpectedState(Cohort.LandingId lid, expected) ->
      failed
        (sprintf "Landing %s is not in the expected state (%s)." lid expected)
        "Run get_cohort_status to see the landing's current state before acting on it."
    | Cohort.CohortError.NotConductor who ->
      failed
        (sprintf "This is a conductor-only action, and %s is not the cohort conductor." (mid who))
        // No tool hands the conductor role to another member (DelegateConductor has no MCP verb), so
        // the old advice, "have the conductor delegate the role to you", sent an agent to nothing.
        "Ask the cohort conductor to perform it; get_cohort_status names the conductor."
    // A VACANT seat is a different refusal from NotConductor and gets different
    // advice: there is no conductor to ask. The honest next step is a human one —
    // no MCP tool can fill a vacancy, and nothing is auto-promoted into one, so
    // telling an agent to "ask the conductor" here would send it to nobody.
    | Cohort.CohortError.ConductorVacant(former, since, why) ->
      let seat =
        match former with
        | Some who -> sprintf "since %s (%A held it: %A)" (since.ToString "u") (mid who) why
        | None -> "no conductor has ever been bound"
      failed
        (sprintf "This is a conductor-only action, and the conductor seat is VACANT %s." seat)
        (match former with
         | Some _ ->
           "No MCP tool can fill a conductor seat and nothing is auto-promoted into one: a person must restore a conductor before this can run. Meanwhile do the work that needs no conductor — acquire_claim, release_claim, request_landing."
         | None ->
           "No member has ever been conductor in this cohort. The first member to join binds the seat, so join_cohort (if you have not) is what fills it.")

  // ── Member tokens (Capability.fs) ───────────────────────────────────────

  let private failed reason suggestion = SageFsError.CohortActionFailed(reason, suggestion)

  let private scopeText (prefix: Capability.ScopePrefix) =
    match Capability.ScopePrefix.value prefix with
    | "" -> "the whole repo"
    | dir -> sprintf "'%s'" dir

  /// What a tool class does, in the words a refusal needs. Exhaustive: a new class cannot be added without a phrase.
  let private classPhrase (toolClass: Capability.ToolClass) =
    match toolClass with
    | Capability.ToolClass.CohortRead -> "reading the cohort"
    | Capability.ToolClass.CohortMembership -> "joining or leaving the cohort"
    | Capability.ToolClass.CohortWork -> "claiming scopes and queueing landings"
    | Capability.ToolClass.CohortAdmin -> "administering the cohort (minting, revoking, reassigning, configuring the integration)"
    | Capability.ToolClass.SessionRead -> "reading status and sessions"
    | Capability.ToolClass.Feedback -> "reporting friction"
    | Capability.ToolClass.CodeAnalysis -> "analysing code and history"
    | Capability.ToolClass.TestRun -> "running the project's tests"
    | Capability.ToolClass.Leases -> "taking build, test and run leases"
    | Capability.ToolClass.Eval -> "evaluating F#"
    | Capability.ToolClass.SessionLifecycle -> "creating, resetting and stopping sessions"
    | Capability.ToolClass.AppControl -> "starting and stopping the app"
    | Capability.ToolClass.Maintenance -> "clearing local data and tidying the workspace"

  let pathRefusalToSageFsError (refusal: Cohort.PathRefusal) : SageFsError =
    match refusal with
    | Cohort.PathRefusal.Absolute path ->
      failed
        (sprintf "'%s' is an absolute path, and a claim path is relative to the repo root." path)
        "Write the path relative to the repo root, for example 'file:src/Foo/Bar.fs'."
    | Cohort.PathRefusal.EscapesRoot path ->
      failed
        (sprintf "'%s' climbs out of the repo: a '..' goes above the root." path)
        "Write the path relative to the repo root, for example 'file:src/Foo/Bar.fs', without '..' that leave it."

  let toolRefusalToSageFsError (refusal: Capability.ToolRefusal) : SageFsError =
    match refusal with
    | Capability.ToolRefusal.NotInRole(tool, toolClass, preset) ->
      let allowedBy =
        Capability.RolePreset.all
        |> List.filter (fun p -> Set.contains toolClass (Capability.RolePreset.toolClasses p))
        |> List.map Capability.RolePreset.toToken
      let allowedText =
        match allowedBy with
        | [] -> "no member token role (it is conductor-only)"
        | roles -> sprintf "the %s role" (String.concat " or " roles)
      failed
        (sprintf "This member token has the %s role, which cannot call %s: that is %s." (Capability.RolePreset.toToken preset) tool (classPhrase toolClass))
        (sprintf "%s needs %s. Ask the conductor to mint a token with it (mint_member), or use a tool your role allows (tools/list shows them)." tool allowedText)
    | Capability.ToolRefusal.Unclassified tool ->
      failed
        (sprintf "SageFs has no role for the tool %s, so no member token may call it." tool)
        "Use the conductor's own connection, or report this: every registered tool must have a tool class."

  let scopeRefusalToSageFsError (refusal: Capability.ScopeRefusal) : SageFsError =
    match refusal with
    | Capability.ScopeRefusal.OutsideScope(requested, granted) ->
      let requestedText =
        match requested with
        | Cohort.ClaimScope.File f -> sprintf "file:%s" f
        | Cohort.ClaimScope.Project p -> sprintf "project:%s" p
      failed
        (sprintf "This member token is confined to %s and cannot claim %s." (scopeText granted) requestedText)
        (sprintf "Claim a path inside %s, or ask the conductor to mint a token for a wider scope." (scopeText granted))
    | Capability.ScopeRefusal.MalformedPath path -> pathRefusalToSageFsError path

  let policyRefusalToSageFsError (refusal: Capability.PolicyRefusal) : SageFsError =
    match refusal with
    | Capability.PolicyRefusal.TokenRequired tool ->
      failed
        (sprintf "This daemon requires a member token (identity policy %s), and this call presented none, so %s is refused." (Capability.IdentityPolicy.toToken Capability.IdentityPolicy.TokenRequired) tool)
        (sprintf "Ask the conductor to run mint_member and pass the token in the %s header or in _meta[\"%s\"], never as a tool argument. get_cohort_status stays readable without one." Capability.CapabilityTransport.headerName Capability.CapabilityTransport.metaKey)

  let presentRefusalToSageFsError (refusal: Capability.PresentRefusal) : SageFsError =
    match refusal with
    | Capability.PresentRefusal.Unknown ->
      failed
        "The member token you presented is not known to this daemon: it was never minted here, or the daemon restarted (tokens do not survive a restart)."
        "Ask the conductor to mint a new one with mint_member."
    | Capability.PresentRefusal.Expired notAfter ->
      failed
        (sprintf "The member token you presented expired at %s." (notAfter.ToString "u"))
        "Ask the conductor to mint a new one with mint_member."
    | Capability.PresentRefusal.LeaseLapsed lastSeen ->
      failed
        (sprintf "The member token you presented was last used at %s and lapsed after %d minutes of silence, the way a silent member's seat does." (lastSeen.ToString "u") (int Cohort.leaseWindow.TotalMinutes))
        "Ask the conductor to mint a new one with mint_member."
    | Capability.PresentRefusal.Revoked at ->
      failed
        (sprintf "The member token you presented was revoked at %s." (at.ToString "u"))
        "Ask the conductor to mint a new one with mint_member, if the run should continue."

  let mintRefusalToSageFsError (refusal: Capability.MintRefusal) : SageFsError =
    match refusal with
    | Capability.MintRefusal.NotConductor ->
      failed
        "Only the cohort conductor can mint member tokens, and this caller is not the conductor."
        "Ask the cohort conductor to mint it; get_cohort_status names the conductor."
    | Capability.MintRefusal.DuplicateToken ->
      failed
        "A member token with that hash already exists."
        "Retry; every mint draws fresh randomness."
    | Capability.MintRefusal.NotInTheFuture notAfter ->
      failed
        (sprintf "The token would already be expired: its expiry %s is not in the future." (notAfter.ToString "u"))
        "Ask for a lifetime of at least one minute."
    | Capability.MintRefusal.WouldWiden widenings ->
      let describeWidening (widening: Capability.Widening) =
        match widening with
        | Capability.Widening.Role(requested, minter) ->
          sprintf "role %s is wider than %s" (Capability.RolePreset.toToken requested) (Capability.RolePreset.toToken minter)
        | Capability.Widening.Scope(requested, minter) ->
          sprintf "scope %s is wider than %s" (scopeText requested) (scopeText minter)
        | Capability.Widening.Expiry(requested, minter) ->
          sprintf "expiry %s is later than %s" (requested.ToString "u") (minter.ToString "u")
      failed
        (sprintf "A member token can only be narrower than whoever mints it, and this request is wider: %s." (widenings |> List.map describeWidening |> String.concat "; "))
        "Ask for a narrower role, scope or lifetime. Nothing is adjusted for you."
    | Capability.MintRefusal.LifetimeTooLong(requested, max) ->
      failed
        (sprintf "A member token may live at most %d minutes (the maximum), and this one asked for %d." (int max.TotalMinutes) (int requested.TotalMinutes))
        "Ask for a shorter lifetime and mint a new token for the next run."

  let revokeRefusalToSageFsError (refusal: Capability.RevokeRefusal) : SageFsError =
    match refusal with
    | Capability.RevokeRefusal.NotConductor ->
      failed
        "Only the cohort conductor can revoke member tokens, and this caller is not the conductor."
        "Ask the cohort conductor to revoke it; get_cohort_status names the conductor."
    | Capability.RevokeRefusal.UnknownCapability id ->
      failed
        (sprintf "There is no member token with the id %s." (Capability.idText id))
        "Pass the member id as get_cohort_status prints it (cap:<id>)."
    | Capability.RevokeRefusal.AlreadyRevoked at ->
      failed
        (sprintf "That member token was already revoked at %s." (at.ToString "u"))
        "Nothing more to do: a revoked token stays revoked."
