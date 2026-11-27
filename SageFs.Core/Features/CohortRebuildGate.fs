namespace SageFs.Features

/// The rebuild a landing of compiled source needs, with its effects as ports so its order can be proven without a daemon
/// (P16, roast improvements 6 and 7). The daemon supplies the ports; this module owns the order and the verdicts.
module CohortRebuildGate =

  type Ports = {
    /// The commit the integration worktree is at.
    WorktreeHead: unit -> Async<Result<string, string>>
    /// Record that a build of this commit was ATTEMPTED. Read at the next landing: a commit that did not land makes the session's
    /// build off the head, so the next landing rebuilds.
    NoteAttempt: string -> unit
    /// Restore and build the worktree, so a changed package list, props file or feed takes effect. Run only when the landing
    /// carries a build input (`CohortLandingPlan.needsRestore`).
    Restore: unit -> Async<Result<unit, SageFs.SageFsError>>
    /// Rebuild and restart the session on the result.
    Restart: unit -> Async<Result<unit, SageFs.SageFsError>>
    /// Wait for the restarted session to settle.
    AwaitSettled: unit -> Async<Result<unit, string>>
  }

  /// Two commit names for the same commit: git prints a full sha where a caller may hold an abbreviation.
  let sameCommit (a: string) (b: string) : bool =
    let a, b = a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant()
    a = b || (a.Length >= 7 && b.Length >= 7 && (a.StartsWith b || b.StartsWith a))

  /// Rebuild the integration worktree at `headSha` and restart the session on the result. FAIL-CLOSED on what the rebuild
  /// trusts: the worktree must be AT `headSha` (the rebase put it there, and nothing else may have moved it since), or nothing
  /// is built. The attempt is recorded before the build starts: a build that fails still writes the refused commit's output, so
  /// the session's build is off the head from that moment, and the next landing rebuilds.
  let restartAt (ports: Ports) (restore: bool) (headSha: string) : Async<Result<unit, CohortOwner.VerificationFault>> =
    async {
      match! ports.WorktreeHead() with
      | Error reason ->
        return
          Error(
            CohortOwner.VerificationFault.Environment(
              sprintf "could not read the integration worktree's head before the rebuild (%s), so it cannot be trusted to hold the rebased commit. Request the landing again." reason))
      | Ok at when not (sameCommit at headSha) ->
        return
          Error(
            CohortOwner.VerificationFault.Environment(
              sprintf "the integration worktree is at %s, not at the rebased commit %s: something moved it since the rebase. Request the landing again." at headSha))
      | Ok _ ->
        ports.NoteAttempt headSha
        // A package list, props file or feed that changed only takes effect through a restore; the incremental rebuild the session
        // runs does not restore, and would call a build that never saw the new package a green one.
        let! restored =
          match restore with
          | true -> ports.Restore()
          | false -> async { return Ok() }
        match restored with
        | Error error -> return Error(CohortLandingPlan.classifyBuildFailure error)
        | Ok () ->
          match! ports.Restart() with
          | Error error -> return Error(CohortLandingPlan.classifyBuildFailure error)
          | Ok () ->
            match! ports.AwaitSettled() with
            | Error reason -> return Error(CohortOwner.VerificationFault.Environment reason)
            | Ok () -> return Ok()
    }

  /// Which way a verification session was brought to the head.
  [<RequireQualifiedAccess>]
  type Recovery =
    /// The cheap way: respawn the worker, keep the build, rebind the head's sources over it.
    | Rebound
    /// The expensive way, taken only when the cheap one could not work: rebuild and restart on the build.
    | Rebuilt

  /// Bring the verification session to the head cheaply first, then pay for a rebuild once.
  ///
  /// The cheap way respawns the worker but KEEPS the build. That is the right price for the common case, and impossible for a
  /// landing that changed a signature: the sources no longer type-check against the build they are bound over, the eval answers
  /// FSI's "earlier error", and retrying it changes nothing. Without the rebuild the session stays wedged, every later landing
  /// verifies against a session that holds nothing, and the cohort is blocked for good. So one cheap failure escalates exactly
  /// once and no more: a rebuild that also fails is reported with both reasons rather than looped on.
  let rebindOrRebuild
    (rebind: unit -> Async<Result<unit, 'error>>)
    (rebuild: unit -> Async<Result<unit, 'error>>)
    (bothFailed: 'error -> 'error -> 'error)
    : Async<Result<Recovery, 'error>> =
    async {
      match! rebind () with
      | Ok () -> return Ok Recovery.Rebound
      | Error cheap ->
        match! rebuild () with
        | Ok () -> return Ok Recovery.Rebuilt
        | Error missed -> return Error(bothFailed cheap missed)
    }
