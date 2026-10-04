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

  /// The owner for a SCOPE a caller already knows — a `CohortCommand` carries one, so a
  /// command dispatch needs no directory.
  ///
  /// Separate from `ownerFor` because a scope and a directory are different inputs: a command
  /// knows its scope exactly, while a caller names a directory that must first be resolved to
  /// one. `commitCohort` read `.Owners` inline instead, which SKIPS `Single` entirely — so
  /// every command under a single-owner wiring failed with "no cohort owner is configured for
  /// this daemon", false (one is configured) and naming no scope.
  let ownerForScope
    (support: Features.CohortOwners.Wiring)
    (scope: CohortScope)
    : Features.CohortOwner.Handle option =
    match support with
    | Features.CohortOwners.Wiring.Wired(owners, _) -> Some(owners.OwnerFor scope)
    | Features.CohortOwners.Wiring.Single(owner, _own) -> Some owner
    | Features.CohortOwners.Wiring.Unwired -> None

  let ownerFor
    (support: Features.CohortOwners.Wiring)
    (workingDirectory: string option)
    : Features.CohortOwner.Handle option =
    let scopeOf dir = Scope.ofWorkingDirectory Scope.defaultStrategy dir

    match support with
    | Features.CohortOwners.Wiring.Wired(_, own) ->
      // The caller's own directory when it named one, and the scope the DAEMON started in
      // when it did not — which is what that caller is asking about.
      let scope =
        match workingDirectory with
        | Some dir -> scopeOf dir
        | None -> own
      ownerForScope support scope
    | Features.CohortOwners.Wiring.Single(owner, _own) ->
      // The caller wired ONE owner and named no registry, so this caller has exactly one
      // cohort and it is this one — whatever directory it phrases the request in. Checking
      // that the request's scope equals `own` (the first version) refused a caller that was
      // legitimately asking about its own cohort from a different working directory, which
      // is the "no cohort owner is configured" refusal three capability suites hit.
      Some owner
    | Features.CohortOwners.Wiring.Unwired -> None