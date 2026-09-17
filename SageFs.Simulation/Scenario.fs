namespace SageFs.Simulation

open System
open SageFs
open SageFs.WorkerProtocol

/// Deterministic Simulation Testing (DST) for the pure supervision core.
///
/// Inspired by — NOT copied from — Phil Chen's Wonderly calendar-sync
/// validation harness. The core ideas we adapt:
///   * Chaos is DATA: a `Scenario` carries an explicit, ordered, seeded
///     schedule of events/faults, not ambient randomness. Same seed produces
///     the identical run, so any failure replays perfectly.
///   * Time is injected, never `DateTime.Now`: the run threads a simulated
///     clock (`StartTime` + accumulated `ClockAdvance` spans).
///   * The REAL code is the subject: `run` folds events through the actual
///     `RestartPolicy.decide` / `SessionLifecycle.onWorkerExited` functions —
///     there is no reimplemented "candidate". The oracle here is the set of
///     named invariants (Invariants.fs), each a property the real policy is
///     claimed to satisfy.
///
/// Phase 1 targets the two already-pure, already-time-injected modules:
///   RestartPolicy.decide   (SageFs.Core/RestartPolicy.fs)
///   SessionLifecycle.*     (SageFs.Core/SessionLifecycle.fs)
module Scenario =

  /// One simulated event in a scenario's ordered, seeded schedule.
  [<RequireQualifiedAccess>]
  type SimEvent =
    /// The worker process died with a non-zero exit (a crash). Drives a
    /// restart/give-up decision through the real policy.
    | WorkerCrashed
    /// The worker exited cleanly (exit code 0). A graceful stop.
    | WorkerExitedGracefully
    /// Advance the simulated clock by this span. No policy interaction — it
    /// only moves time forward so later crashes fall inside or outside the
    /// StartupCrashWindow / ResetWindow.
    | ClockAdvance of span: TimeSpan

  /// A fully-specified, replayable scenario. Same seed + same events => the
  /// identical trace. This is the unit of "chaos as data".
  type Scenario =
    { /// The seed the scenario was generated from (for replay/reporting).
      Seed: int
      /// The restart policy under test.
      Policy: RestartPolicy.Policy
      /// The simulated clock's starting instant.
      StartTime: DateTime
      /// The ordered schedule of events/faults.
      Events: SimEvent list }

  /// What the policy did in response to a step's event. A single DU so illegal
  /// combinations (a restart with no delay, a give-up that also restarts) are
  /// unrepresentable — the delay exists ONLY inside `Restarted`.
  ///
  /// The harness deliberately does NOT carry a "which backoff regime" label:
  /// `RestartPolicy.decide` does not expose whether it took the startup-crash
  /// branch, and the two regimes can produce the same delay value, so any label
  /// would be inferred and occasionally wrong. The only thing the harness truly
  /// observes is the delay, so that is all it records; the monotonicity
  /// invariant reasons about the delay against the policy's known circuit-breaker
  /// constant instead (see Invariants.fs).
  [<RequireQualifiedAccess>]
  type StepEffect =
    /// A clock advance, or any event delivered to an already-terminal session
    /// (a crash of a session that already gave up / stopped): no policy action.
    | NoEffect
    /// The worker exited cleanly; the session stopped.
    | Stopped
    /// The worker crashed and the policy restarted it after `delay`. The
    /// resulting tracking state is `Step.RestartState`.
    | Restarted of delay: TimeSpan
    /// The worker crashed and the policy gave up (too many restarts).
    | GaveUp of SageFsError

  /// One folded step of the trace: the event, what the policy did about it,
  /// and the resulting state/status AFTER the step.
  type Step =
    { /// 0-based position in the scenario's event list.
      Index: int
      /// The simulated clock at the moment this event was processed.
      At: DateTime
      /// The event that produced this step.
      Event: SimEvent
      /// What the policy did in response.
      Effect: StepEffect
      /// The restart-tracking state AFTER this step.
      RestartState: RestartPolicy.State
      /// The session lifecycle status AFTER this step.
      Status: SessionLifecycleStatus }

  /// A session is terminal — the real supervisor stops feeding it exit events —
  /// exactly when it has faulted (gave up) or stopped (graceful exit). Derived
  /// from the status DU rather than carried as a redundant bool flag. This is a
  /// query over a DU, not stored state.
  let isTerminal (status: SessionLifecycleStatus) : bool =
    match status with
    | SessionLifecycleStatus.Faulted _
    | SessionLifecycleStatus.Stopped -> true
    | SessionLifecycleStatus.Starting _
    | SessionLifecycleStatus.Ready _
    | SessionLifecycleStatus.Evaluating _
    | SessionLifecycleStatus.Building _
    | SessionLifecycleStatus.Restarting _ -> false

  /// The full deterministic result of running a scenario: the scenario plus
  /// the ordered steps. Invariants are checked over this.
  type Trace =
    { Scenario: Scenario
      Steps: Step list }
