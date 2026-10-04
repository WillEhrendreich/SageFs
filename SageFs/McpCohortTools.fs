namespace SageFs

open System
open System.Threading.Tasks
open SageFs.McpTools
open SageFs.McpSessionRouting

/// The cohort tools' bodies (join, leave, claim, release, reassign, land): what `join_cohort`,
/// `acquire_claim` and the others call. Split out of Mcp.fs, which was over its line budget; the
/// plumbing they share (`commitCohort`, `cohortOwnerFor`, `memberIdFor`) stays there.
module McpCohortTools =

  /// The cohort a caller working in `workingDirectory` belongs to.
  ///
  /// Per repository by default, which is the unit two agents actually contend over: two agents in
  /// one repo must see each other's claims and share a conductor, and two agents in unrelated
  /// repos must not. The strategy is `Scope.defaultStrategy` and is a VALUE rather than a
  /// hardcoded rule, so per-solution, per-worktree or machine-wide are selectable by whoever
  /// configures the daemon — adding one is a new case, not a new mechanism.
  ///
  /// A caller that names no directory falls back to the process's own, so an unbound caller is
  /// scoped to wherever the daemon was started rather than being given the machine-wide cohort
  /// unconditionally.
  let cohortScopeOf (workingDirectory: string option) : SageFs.CohortScope =
    SageFs.Scope.ofWorkingDirectory SageFs.Scope.defaultStrategy (Option.defaultValue Environment.CurrentDirectory workingDirectory)

  /// The SAME scope resolution the daemon binds its cohort owner to, resolved for a caller's
  /// directory. The daemon owns ONE cohort, so a caller's command must name that cohort or it is
  /// refused as a scope collision — which is correct, and useless if the two sides compute the
  /// scope by different rules.
  ///
  /// A plain walk-up is not that rule. A worktree's own `.git` is a POINTER FILE, so stopping at the
  /// first checkout marker resolves a worktree to ITSELF while the daemon, asking git for the shared
  /// directory, resolves it to its repository. The integration tree, the trunk checkout and the main
  /// checkout are then one repository under one rule and three under another, and every command the
  /// trunk gate issues is refused. Asking git — `git rev-parse --git-common-dir`, which is what the
  /// helper resolves — is the one answer that holds from every checkout of a repository.
  let internal scopeForDirectory (workingDirectory: string) : SageFs.CohortScope =
    match Features.CohortGit.commonRepositoryRoot workingDirectory with
    | Some root -> SageFs.Scope.ofWorkingDirectory SageFs.Scope.defaultStrategy root
    | None -> cohortScopeOf (Some workingDirectory)

  /// The scope a command issued from `workingDirectory` belongs to: the same rule the daemon bound
  /// its cohort owner to, so a caller's command names THAT cohort instead of colliding with it.
  let internal scopeOf (workingDirectory: string option) : SageFs.CohortScope =
    // THE ONE RULE, shared with the owner resolver. When these were two rules the join and the
    // status read about it resolved differently — `ScopeOf` asked git for the common root, a
    // plain walk-up did not — so a join landed in one cohort and the read came back from
    // another, with both sides individually correct.
    ScopeOf.ofWorkingDirectory workingDirectory

  let private parseJoinableRole (raw: string) : Result<Cohort.JoinableRole, SageFsError> =
    match (if isNull raw then "" else raw.Trim().ToLowerInvariant()) with
    | "implementer" -> Ok Cohort.JoinableRole.Implementer
    | "verifier" -> Ok Cohort.JoinableRole.Verifier
    | "observer" -> Ok Cohort.JoinableRole.Observer
    | other -> Error (SageFsError.SessionCreationFailed (sprintf "unknown cohort role '%s' — expected Implementer, Verifier, or Observer" other))

  /// v1's scope wire format: "file:<repo-relative-path>" or
  /// "project:<repo-relative-.fsproj-path>" — matches `Cohort.ClaimScope`'s
  /// two v1 cases (Module/Symbol/Contract are Phase 3, not constructible yet).
  let private parseClaimScope (raw: string) : Result<Cohort.ClaimScope, SageFsError> =
    let raw = if isNull raw then "" else raw.Trim()
    match raw.IndexOf ':' with
    | -1 -> Error (SageFsError.SessionCreationFailed (sprintf "claim scope '%s' must be 'file:<path>' or 'project:<path>'" raw))
    | i ->
      let kind = raw.Substring(0, i).Trim().ToLowerInvariant()
      let path = raw.Substring(i + 1).Trim()
      if path = "" then Error (SageFsError.SessionCreationFailed "claim scope path is empty")
      else
        let scope =
          match kind with
          | "file" -> Ok (Cohort.ClaimScope.File path)
          | "project" -> Ok (Cohort.ClaimScope.Project path)
          | other -> Error (SageFsError.SessionCreationFailed (sprintf "unknown claim scope kind '%s' — expected 'file' or 'project'" other))
        // Canonical at the boundary: `src/Foo/../Bar/x.fs` is `src/Bar/x.fs`, so exclusivity and a
        // token's scope prefix both compare what the path means, and a path that leaves the repo is refused.
        scope
        |> Result.bind (fun s -> Cohort.ClaimScope.tryCanonical s |> Result.mapError CohortErrorMapping.pathRefusalToSageFsError)

  /// Resolve a landing's presented "claimId:fence" pairs (comma-separated —
  /// v1 has no structured multi-value MCP argument type worth adding for this
  /// small surface, see the Slice 2 report's deviation note).
  let private parseClaimFenceList (raw: string) : Result<(Cohort.ClaimId * int64<Measures.fence>) list, SageFsError> =
    let raw = if isNull raw then "" else raw.Trim()
    if raw = "" then Ok []
    else
      raw.Split(',')
      |> Array.toList
      |> List.map (fun pair ->
        match pair.Trim().Split(':') with
        | [| cid; fenceStr |] when cid.Trim() <> "" ->
          match Int64.TryParse(fenceStr.Trim()) with
          | true, f -> Ok (Cohort.ClaimId (cid.Trim()), LanguagePrimitives.Int64WithMeasure<Measures.fence> f)
          | false, _ -> Error (SageFsError.SessionCreationFailed (sprintf "claim fence '%s' is not an integer in '%s'" fenceStr pair))
        | _ -> Error (SageFsError.SessionCreationFailed (sprintf "expected 'claimId:fence', got '%s'" pair)))
      |> List.fold (fun acc item ->
        match acc, item with
        | Error e, _ -> Error e
        | Ok _, Error e -> Error e
        | Ok xs, Ok x -> Ok (xs @ [ x ]))
        (Ok [])

  /// Resolve a target member named by its OWN display string (as
  /// `get_cohort_status` prints it) back to a `MemberId` — used only for
  /// naming the RECIPIENT of a conductor-only action (`reassign_claim`),
  /// never for the acting caller's own identity (that is always
  /// `memberIdFor agentName`, never a self-declared argument). Looked up
  /// against the live frame first so a real bound connection (Mcp/Browser) is
  /// named exactly as presented; falls back to `Minted` so direct/unbound
  /// callers (most tests) can still name each other by plain agent name.
  ///
  /// `workingDirectory` is the CALLER's, so the frame read is about the cohort the
  /// caller is actually in. Resolving against the daemon's own cohort here would
  /// look the display up in the wrong frame and fall through to `Minted` for a
  /// member that does have a real id in its own cohort.
  let private resolveMemberByDisplay (ctx: McpContext) (display: string) (workingDirectory: string option) : MemberTable.MemberId =
    match SageFs.McpTools.cohortOwnerFor ctx workingDirectory with
    | None -> MemberTable.MemberId.Minted display
    | Some owner ->
      owner.ReadFrame().MemberIds
      |> Array.tryFind (fun m -> MemberTable.MemberId.display m = display)
      |> Option.defaultValue (MemberTable.MemberId.Minted display)

  /// Resolve the caller's SESSION (checkout) for `join_cohort` (item 13c of
  /// sagefs-multiagent-vision.md), via the SAME routing every other tool
  /// uses (`resolveSessionId`): an explicit `workingDirectory` wins, then the
  /// agent's active-session mapping, then the daemon's own working directory
  /// or its one-and-only session. Any resolution that names a real,
  /// registered session (Routable/WarmingUp/Unroutable/FaultedSession) is
  /// bound — the member doesn't need a currently-ROUTABLE worker, just an
  /// existing session id to attribute test outcomes to later. Only `Gone`
  /// (no matching/ambiguous/no session at all) resolves to `None`: the
  /// member still joins, it just contributes no row to the cohort's test
  /// matrix (`CohortOwner.frameOf`) until it joins again with a resolvable
  /// session.
  let internal resolveJoinSession (ctx: McpContext) (agentName: string) (workingDirectory: string option) : Task<string option> =
    task {
      let! resolution = resolveSessionId ctx agentName None workingDirectory
      return
        match resolution with
        | Routable sid
        | WarmingUp(sid, _)
        | Unroutable(sid, _)
        | FaultedSession(sid, _) -> Some sid
        | Gone _ -> None
    }

  /// The working directory a cohort command should be scoped by: the caller's own if it named
  /// one, else the directory of the session it is bound to, else nothing (and then the process's).
  ///
  /// A claim, a release and a landing all act on FILES, so "which cohort" has to be answered from
  /// where the caller is working, not from an ambient default. Resolving through the session is
  /// what makes an agent that never passes `working_directory` still land in its own
  /// repository's cohort rather than the one the daemon happened to start in — which is the whole
  /// reason a cohort has a scope at all.
  let internal callerWorkingDirectory (ctx: McpContext) (agentName: string) (workingDirectory: string option) : Task<string option> =
    task {
      match workingDirectory with
      | Some wd -> return Some wd
      | None ->
        let! sid = resolveJoinSession ctx agentName None
        match sid with
        | None -> return None
        | Some sid ->
          let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
          return info |> Option.map (fun i -> i.WorkingDirectory)
    }

  /// The same, from a wire string where "" means "I did not name one". The MCP tools take
  /// `workingDirectory` as an optional STRING because an SDK tool parameter cannot be an F#
  /// option, so this is where that encoding is undone — in one place, rather than at each of the
  /// five call sites that would otherwise each have to remember it.
  let internal callerWorkingDirectoryOf (ctx: McpContext) (agentName: string) (workingDirectory: string) : Task<string option> =
    callerWorkingDirectory ctx agentName (if String.IsNullOrWhiteSpace workingDirectory then None else Some workingDirectory)

  /// Join the implicit per-daemon cohort as `role` (Implementer/Verifier/
  /// Observer). v1 has no separate `create_cohort` command — the first
  /// member to join an empty cohort becomes its conductor automatically
  /// (`Cohort.decide`'s own semantics for `Join`), so this tool doubles as
  /// create_cohort for the first caller. `workingDirectory` (item 13c) is
  /// resolved to a session id via `resolveJoinSession` and stored on the
  /// member (`MemberRecord.Session`) so the cohort frame can later attribute
  /// this member's checkout's test outcomes to it (`CohortOwner.frameOf`).
  let joinCohort (ctx: McpContext) (agentName: string) (role: string) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    task {
      // Under a member token the role is the token's. The argument is not consulted: a role a caller
      // names for itself is exactly what a minted grant exists to replace.
      let roleResult =
        match currentCapability.Value with
        | Some capability -> Ok (Capability.RolePreset.joinableRole capability.Grant.Preset)
        | None -> parseJoinableRole role
      let tokenHolderWithNoConductor =
        // The CALLER's cohort, not the daemon's: whether the conductor seat is empty is
        // a question about the cohort this joiner is joining. Reading the daemon's own
        // cohort answered it about a cohort they are not joining, so a token holder
        // joining their own repo could be told a seat elsewhere was taken.
        match currentCapability.Value, SageFs.McpTools.cohortOwnerFor ctx workingDirectory with
        // A minted token may only claim the seat when it is actually EMPTY. A
        // VACANT seat counts as empty too — nobody holds conductor authority
        // until a person appoints one — which is why this tests
        // `NeverBound` specifically rather than "not Bound".
        | Some _, Some owner ->
          match (owner.ReadFrame()).Conductor with
          | Cohort.ConductorBinding.NeverBound -> true
          | Cohort.ConductorBinding.Bound _
          | Cohort.ConductorBinding.Vacant _ -> false
        | Some _, None
        | None, _ -> false
      match roleResult with
      | Error e -> return Error e
      | Ok _ when tokenHolderWithNoConductor ->
        return
          Error (SageFsError.CohortActionFailed(
                   "A member token cannot become the conductor, and this cohort has none yet: the first member to join is the conductor.",
                   "Ask the conductor to join_cohort first (it is the one that mints member tokens), then join with the token."))
      | Ok r ->
        let who = memberIdFor agentName
        let! sessionOpt = resolveJoinSession ctx agentName workingDirectory
        // The cohort this join lands in. Derived from the caller's own working directory, so
        // two agents in two repositories get two cohorts with two conductor seats instead of
        // contending for one — which is what made an agent in an unrelated repo report a phantom
        // "already a conductor" conflict.
        let cohortScope = scopeOf workingDirectory
        let! result = commitCohort ctx (Cohort.CohortCommand.Join(who, r, sessionOpt, cohortScope))
        return
          result
          |> Result.map (fun (events, _) ->
            let becameConductor = events |> List.exists (function Cohort.CohortEvent.ConductorBound _ -> true | _ -> false)
            let sessionNote =
              match sessionOpt with
              | Some sid -> sprintf " Bound to session %s." sid
              | None -> " No session was resolved — you won't appear in the per-session test matrix until you join again from a working directory that matches a session."
            let tokenNote =
              match currentCapability.Value with
              | Some _ -> " Your role is the one your member token was minted with; the role argument is ignored."
              | None -> ""
            sprintf
              "Joined cohort as %s (%s) in %s.%s%s%s"
              (MemberTable.MemberId.display who)
              (string r)
              (SageFs.Scope.label cohortScope)
              (if becameConductor then " You are the conductor (first to join in this scope)." else "")
              tokenNote
              sessionNote)
    }

  let leaveCohort (ctx: McpContext) (agentName: string) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    task {
      let who = memberIdFor agentName
      let! result = commitCohort ctx (Cohort.CohortCommand.Depart(who, scopeOf workingDirectory))
      return result |> Result.map (fun _ -> sprintf "%s left the cohort." (MemberTable.MemberId.display who))
    }

  let acquireClaim (ctx: McpContext) (agentName: string) (scope: string) (purpose: string) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    task {
      match parseClaimScope scope with
      | Error e -> return Error e
      | Ok claimScope ->
        // A call under a member token may only claim inside the token's scope prefix.
        let withinGrant =
          match currentCapability.Value with
          | Some capability -> Capability.admitClaim capability.Grant claimScope |> Result.mapError CohortErrorMapping.scopeRefusalToSageFsError
          | None -> Ok ()
        match withinGrant with
        | Error e -> return Error e
        | Ok () ->
        let who = memberIdFor agentName
        let! result =
          commitCohort ctx (Cohort.CohortCommand.AcquireClaim(who, claimScope, purpose, scopeOf workingDirectory))
        return
          result
          |> Result.bind (fun (events, _) ->
            match events |> List.tryPick (function Cohort.CohortEvent.ClaimAcquired(cid, _, _, fence) -> Some(cid, fence) | _ -> None) with
            | Some(Cohort.ClaimId cid, fence) -> Ok (sprintf "Acquired claim %s over %s (fence=%d)." cid scope (int64 fence))
            | None -> Error (SageFsError.Unexpected (exn "acquire_claim committed with no ClaimAcquired event")))
    }

  let releaseClaim (ctx: McpContext) (agentName: string) (claimId: string) (fence: int64) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    task {
      let who = memberIdFor agentName
      let fenceMeasure = LanguagePrimitives.Int64WithMeasure<Measures.fence> fence
      let! result =
        commitCohort ctx (
          Cohort.CohortCommand.ReleaseClaim(who, Cohort.ClaimId claimId, fenceMeasure, scopeOf workingDirectory))
      return result |> Result.map (fun _ -> sprintf "Released claim %s." claimId)
    }

  /// Conductor-only (`Cohort.decide` gates `ReassignClaim` on
  /// `Authority.present by state = Authority.Conductor _`, refusing
  /// `NotConductor` otherwise — surfaced here via `CohortErrorMapping.toSageFsError`).
  /// `toMember` names the recipient by ITS OWN display string, resolved via
  /// `resolveMemberByDisplay` — never trusted as the caller's own identity.
  let reassignClaim (ctx: McpContext) (agentName: string) (claimId: string) (toMember: string) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    task {
      let by = memberIdFor agentName
      let target = resolveMemberByDisplay ctx toMember workingDirectory
      let! result = commitCohort ctx (Cohort.CohortCommand.ReassignClaim(by, Cohort.ClaimId claimId, target, scopeOf workingDirectory))
      return result |> Result.map (fun _ -> sprintf "Reassigned claim %s to %s." claimId (MemberTable.MemberId.display target))
    }

  /// `claims` is "claimId:fence,claimId:fence,..." (empty string = no
  /// backing claims); `commits` is a comma-separated list of shas. See
  /// `parseClaimFenceList`'s doc for why v1 uses this flat wire format
  /// instead of a structured argument type.
  let requestLanding (ctx: McpContext) (agentName: string) (claims: string) (commits: string) (statement: string) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    task {
      match parseClaimFenceList claims with
      | Error e -> return Error e
      | Ok claimList ->
        let commitList =
          (if isNull commits then "" else commits).Split(',')
          |> Array.map (fun s -> s.Trim())
          |> Array.filter (fun s -> s <> "")
          |> Array.toList
        let requester = memberIdFor agentName
        let! result = commitCohort ctx (Cohort.CohortCommand.RequestLanding(requester, claimList, commitList, statement, scopeOf workingDirectory))
        return
          result
          |> Result.bind (fun (events, _) ->
            match events |> List.tryPick (function Cohort.CohortEvent.LandingQueued(lid, _) -> Some lid | _ -> None) with
            | Some(Cohort.LandingId lid) -> Ok (sprintf "Landing %s queued." lid)
            | None -> Error (SageFsError.Unexpected (exn "request_landing committed with no LandingQueued event")))
    }
