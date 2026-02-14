namespace SageFs

open System
open SageFs.WorkerProtocol

/// Pure, deterministic decision logic for session lifecycle events.
/// Used by SessionManager to decide what to do when workers exit.
/// No IO, no side effects — just decisions based on state.
module SessionLifecycle =

  /// What happened when a worker exited, and what to do about it.
  [<RequireQualifiedAccess>]
  type ExitOutcome =
    /// Worker exited cleanly (code 0). Remove it.
    | Graceful
    /// Worker crashed. Restart after delay.
    | RestartAfter of delay: TimeSpan * newRestartState: RestartPolicy.State
    /// Worker crashed too many times. Give up.
    | Abandoned of SageFsError

  /// Determine the outcome when a worker exits.
  let onWorkerExited
    (policy: RestartPolicy.Policy)
    (restartState: RestartPolicy.State)
    (exitCode: int)
    (now: DateTime)
    : ExitOutcome =
    if exitCode = 0 then
      ExitOutcome.Graceful
    else
      let decision, newState = RestartPolicy.decide policy restartState now
      match decision with
      | RestartPolicy.Decision.Restart delay ->
        ExitOutcome.RestartAfter(delay, newState)
      | RestartPolicy.Decision.GiveUp error ->
        ExitOutcome.Abandoned error

  /// Determine the new session status from an exit outcome.
  let statusAfterExit (outcome: ExitOutcome) : SessionStatus =
    match outcome with
    | ExitOutcome.Graceful -> SessionStatus.Stopped
    | ExitOutcome.RestartAfter _ -> SessionStatus.Restarting
    | ExitOutcome.Abandoned _ -> SessionStatus.Faulted
