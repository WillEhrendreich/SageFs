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
    /// Rebuild and restart the session on the result.
    Restart: unit -> Async<Result<unit, SageFs.SageFsError>>
    /// Wait for the restarted session to settle.
    AwaitSettled: unit -> Async<Result<unit, string>>
  }

  let restartAt (ports: Ports) (headSha: string) : Async<Result<unit, CohortOwner.VerificationFault>> =
    async {
      match! ports.Restart() with
      | Error error -> return Error(CohortLandingPlan.classifyBuildFailure error)
      | Ok () ->
        match! ports.AwaitSettled() with
        | Error reason -> return Error(CohortOwner.VerificationFault.Environment reason)
        | Ok () ->
          ports.NoteAttempt headSha
          return Ok()
    }
