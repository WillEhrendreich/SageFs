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

  /// The outcome of a crash, from whichever `decide` variant the caller chose.
  let private outcomeOfDecision
    (decision: RestartPolicy.Decision, newState: RestartPolicy.State)
    : ExitOutcome =
    match decision with
    | RestartPolicy.Decision.Restart delay ->
      ExitOutcome.RestartAfter(delay, newState)
    | RestartPolicy.Decision.GiveUp error ->
      ExitOutcome.Abandoned error

  /// Determine the outcome when a worker exits.
  let onWorkerExited
    (policy: RestartPolicy.Policy)
    (restartState: RestartPolicy.State)
    (exitCode: int)
    (now: DateTime)
    : ExitOutcome =
    match exitCode = 0 with
    | true ->
      ExitOutcome.Graceful
    | false ->
      outcomeOfDecision (RestartPolicy.decide policy restartState now)

  /// Like `onWorkerExited`, but the restart delay is spread by the seed so sessions whose workers
  /// died together (a machine sleep, an OOM kill) do not all retry at the same instant. The state
  /// and the give-up decision are identical to `onWorkerExited`; only the delay moves.
  let onWorkerExitedJittered
    (seed: RestartPolicy.JitterSeed)
    (policy: RestartPolicy.Policy)
    (restartState: RestartPolicy.State)
    (exitCode: int)
    (now: DateTime)
    : ExitOutcome =
    match exitCode = 0 with
    | true ->
      ExitOutcome.Graceful
    | false ->
      outcomeOfDecision (RestartPolicy.decideWithJitter policy seed restartState now)

  /// Determine the new session status from an exit outcome. The exited
  /// worker's own pid is carried into Restarting only so a late event from
  /// it can be recognized as stale (see SessionManager's stale-pid guards).
  let statusAfterExit (exitedWorkerPid: int option) (outcome: ExitOutcome) : SessionLifecycleStatus =
    match outcome with
    | ExitOutcome.Graceful -> SessionLifecycleStatus.Stopped
    | ExitOutcome.RestartAfter _ -> SessionLifecycleStatus.Restarting (PreviousWorker.ofPid exitedWorkerPid)
    | ExitOutcome.Abandoned err -> SessionLifecycleStatus.Faulted (FaultReason.report (SageFsError.describe err))
