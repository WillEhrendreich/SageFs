namespace SageFs

open System

/// The tool-admission gate over the WHOLE tool surface, and the reasoning behind it.
///
/// WHY THIS IS ITS OWN FILE. It is the decision, and it was accreted into `Mcp.fs`, which is
/// already over its line budget: the prose explaining WHY a role is not a sandbox is worth keeping,
/// and it does not belong in the middle of an adapter file where a reader looking for a route
/// finds it instead. `Mcp.fs` calls in; the decision lives here.
///
/// WHAT CHANGED. Authority gating used to speak only about the ten cohort verbs:
/// `checkCohortAuthorityGate` returned `None` for every other tool name, the caller fell through to
/// the session-state gate, and a caller whose role was `Observer` — a name that reads like a
/// boundary — could call `send_fsharp_code`, `hard_reset_fsi_session` and `run_app` in perfect
/// health. A role name that does not mean anything is worse than no role at all, because it is
/// read as a guarantee.
///
/// The gate is now `Authority -> ToolName -> Result<unit, AuthorityRefusal>` over the closed
/// `Affordances.ToolName` DU, so it is TYPED end to end and an undeclared tool name cannot even be
/// expressed. It intersects the cohort verbs with the old `checkCohortToolAllowed` verdict, so it
/// is strictly STRICTER than the cohort gate was: it can never admit anything that gate refused.
///
/// WHAT THIS IS NOT. A role here is a well-mannered-agent convention plus a refusal a careless
/// agent will hit. IT IS NOT A SANDBOX. `send_fsharp_code` runs arbitrary F# as the same OS user as
/// the daemon — it can read the daemon's data dir, call the daemon's loopback with any session id,
/// and read `/proc` — and `run_tests` runs the project's own test binary, which is user code by
/// another door. Any allow-list containing either of those is ADVISORY AGAINST A HOSTILE AGENT,
/// NOT A BOUNDARY. Containment is a process or credential boundary, never a row in a table. Read
/// `Affordances.fs`'s section header for the long form.
[<RequireQualifiedAccess>]
module ToolAuthorityGate =

  /// What the gate decided, with everything a caller needs to act on it.
  type Decision =
    /// The call goes ahead.
    | Admitted
    /// The call is refused. `reason` says what happened and `nextAction` says what to do about it;
    /// an agent cannot proceed correctly with only one of those.
    | Refused of reason: string * nextAction: string

  /// Fail closed on a tool name the daemon does not register. An undeclared name has no authority
  /// policy, and this arm is a SECOND, earlier refusal of the same thing the session-state gate
  /// would give — it is here so the log says "unknown tool" rather than "wrong role", which are
  /// different faults with different fixes.
  let private refuseUnknownTool (toolName: string) : Decision =
    let refusal = Affordances.AuthorityRefusal.CapabilityRequired toolName
    Refused(
      SageFsError.describeForAgent (
        SageFsError.CohortActionFailed(
          Affordances.AuthorityRefusal.describe refusal,
          Affordances.AuthorityRefusal.nextAction refusal)),
      Affordances.AuthorityRefusal.nextAction refusal)

  /// Decide one tool call.
  ///
  /// `authority` is the caller's, resolved by the CALLER from the owner's published frame using the
  /// BOUND identity — never from a self-declared role argument, which a caller controls. Passing
  /// `Anonymous` (no cohort owner wired, e.g. pre-Slice-2 unit tests) maps to `Observer`, which is
  /// deliberately not "no authority at all": that would make `join_cohort` unreachable and leave
  /// nobody able to form a cohort in the first place.
  let decide (who: MemberTable.MemberId) (authority: Cohort.Authority<MemberTable.MemberId>) (toolName: string) : Decision =
    match Affordances.ToolName.tryParse toolName with
    | None -> refuseUnknownTool toolName
    | Some tool ->
      Affordances.checkAuthorityAllowed authority tool
      |> Result.map (fun () -> Admitted)
      |> Result.defaultWith (fun refusal ->
        let reason =
          sprintf
            "%s cannot call %s (your role is %s): %s"
            (MemberTable.MemberId.display who)
            toolName
            (Affordances.ToolRole.toToken (Affordances.authorityRoleOf authority))
            (Affordances.AuthorityRefusal.describe refusal)
        Refused(
          SageFsError.describeForAgent (SageFsError.CohortActionFailed(reason, Affordances.AuthorityRefusal.nextAction refusal)),
          Affordances.AuthorityRefusal.nextAction refusal))

  /// The caller's authority, read from the owner's published frame — wait-free, never a mailbox
  /// round trip. `None` owner means `Anonymous`, which is `Observer`'s authority.
  let authorityOf
    (owner: Features.CohortOwner.Handle option)
    (who: MemberTable.MemberId)
    : Cohort.Authority<MemberTable.MemberId> =
    match owner with
    | Some o -> Affordances.authorityOfMember who (o.ReadFrame())
    | None -> Cohort.Authority.Anonymous