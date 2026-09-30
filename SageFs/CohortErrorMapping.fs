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
        "Ask the cohort conductor to perform it, or have the conductor delegate the role to you."
