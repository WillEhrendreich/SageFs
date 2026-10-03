namespace SageFs

open SageFs

/// WHICH COHORT A CALLER IS ASKING ABOUT.
///
/// WHY THIS IS ITS OWN MODULE: `Mcp.fs` is the accretion hub and carries a line budget
/// (ArchitectureTests, ratchet down / never raise). This is a pure decision about scope, it
/// has no IO, and it is the rule every cohort read has to share — so it belongs beside the
/// wiring it reads rather than as forty lines in the hub every reader has to find.
///
/// WHY EVERY READ GOES THROUGH ONE FUNCTION: with one daemon serving one cohort per
/// repository, "is this member the conductor" and "which cohort is this" are questions about
/// the CALLER's directory, not the daemon's. Reading the daemon's single owner answered both
/// about the cohort it happened to start in — so an agent in a second repository could be
/// refused by the first repository's conductor seat, and a status read could describe a
/// cohort the caller is not in. That is the UI lying, in the form that matters.
///
/// `workingDirectory` is the caller's own directory, exactly as `join_cohort` takes it;
/// `None` means the caller named none, so it is asking about the daemon's own cohort.
module CohortOwnerResolution =

  let ownerFor
    (support: Features.CohortOwners.Wiring)
    (workingDirectory: string option)
    : Features.CohortOwner.Handle option =
    let scopeOf dir = Scope.ofWorkingDirectory Scope.defaultStrategy dir

    match support with
    | Features.CohortOwners.Wiring.Wired(owners, own) ->
      // The caller's own directory when it named one, and the scope the DAEMON started in
      // when it did not — which is what that caller is asking about.
      let scope =
        match workingDirectory with
        | Some dir -> scopeOf dir
        | None -> own
      Some(owners.OwnerFor scope)
    | Features.CohortOwners.Wiring.Single(owner, _own) ->
      // The caller wired ONE owner and named no registry, so this caller has exactly one
      // cohort and it is this one — whatever directory it phrases the request in. Checking
      // that the request's scope equals `own` (the first version) refused a caller that was
      // legitimately asking about its own cohort from a different working directory, which
      // is the "no cohort owner is configured" refusal three capability suites hit.
      // `own` is still what `commitCohort` dispatches on, so the command lands in the right
      // cohort; this resolver's only job is to FIND the owner.
      Some owner
    | Features.CohortOwners.Wiring.Unwired -> None