namespace SageFs

open SageFs.WorkerProtocol

/// What the session manager does when a starting worker ran out of patience. Pure: the manager's pid guard
/// (`WorkerEventGuard`, shared with ready and spawn-failed events), the tier, and what the machine has
/// taught, in; one decision out. The manager only carries it out.
module StartTimeoutDecision =

  [<RequireQualifiedAccess>]
  type Decision =
    /// The event is from a worker the session has already replaced: it decides nothing.
    | Ignore
    /// A replacement worker (a spawn-first restart) ran out of patience while the old one still serves:
    /// put the old one back, because the session is not down.
    | RevertSwap
    /// Start the worker again with MORE patience, and say so in the session's progress text.
    | Retry of budget: StartBudget * progress: string
    /// Every attempt ran out of patience: fault the session with the whole story.
    | GiveUp of reason: FaultReason * message: string

  /// The patience a FIRST attempt at starting a worker is given: what the tier allows, or what this machine
  /// has shown a start needs, whichever is longer, never past the tier's absolute bound.
  let firstStartBudget (ledger: Ledger) : StartBudget =
    StartEscalation.firstBudget (ledger.History StartStage.WorkerPort) Timeouts.warmupInactivityLimit Timeouts.warmupAbsoluteMax

  /// The progress line that says a slow start was started again. The step and total are the attempt and
  /// the attempts allowed, so it reads in the same "n/m text" shape the worker's own progress does.
  let retryProgress (budget: StartBudget) (timeout: StartTimeout) : string =
    sprintf "%d/%d Slow start: the worker was silent for %.0f s (%.0f s in all), so it was started again (attempt %d of %d). This attempt may be silent for up to %.0f s while it waits for %s."
      budget.Attempt StartEscalation.MaxAttempts timeout.Budget.Inactivity.TotalSeconds timeout.Waited.TotalSeconds budget.Attempt StartEscalation.MaxAttempts
      budget.Inactivity.TotalSeconds (StartStage.describe timeout.Stage)

  /// The progress line that tells a person a slow machine's first start is expected to be slow; "" on a
  /// machine where a start takes seconds.
  let noticeProgress (attempt: int) (notice: StartEscalation.SlowStartNotice) : string =
    match notice with
    | StartEscalation.SlowStartNotice.NotSlow -> ""
    | StartEscalation.SlowStartNotice.Expected _ ->
      sprintf "%d/%d %s" attempt StartEscalation.MaxAttempts (StartEscalation.describeNotice notice)

  /// The fault message for a give-up: the explanation, and the worker's last words when it left any.
  let giveUpMessage (reason: FaultReason) (stderrTail: string) : string =
    match System.String.IsNullOrWhiteSpace stderrTail with
    | true -> FaultReason.describe reason
    | false -> sprintf "%s\nstderr:\n%s" (FaultReason.describe reason) stderrTail

  let decide
    (tier: MachineTier)
    (history: StageHistory)
    (guard: WorkerEventGuard.SpawnFailedDecision)
    (timeout: StartTimeout)
    (stderrTail: string)
    : Decision =
    match guard with
    | WorkerEventGuard.SpawnFailedDecision.IgnoreStale -> Decision.Ignore
    | WorkerEventGuard.SpawnFailedDecision.RevertSwap -> Decision.RevertSwap
    | WorkerEventGuard.SpawnFailedDecision.Fault ->
      match StartEscalation.next tier history timeout with
      | EscalationStep.RetryWith budget -> Decision.Retry (budget, retryProgress budget timeout)
      | EscalationStep.GiveUp failure ->
        let reason = FaultReason.StartTimedOut failure
        Decision.GiveUp (reason, giveUpMessage reason stderrTail)
