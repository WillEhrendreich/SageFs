namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.Scenario

/// Independent, from-first-principles reference model of the expected
/// RestartPolicy + SessionLifecycle decision.
///
/// This module deliberately does NOT call `RestartPolicy.decide` or
/// `SessionLifecycle.onWorkerExited` anywhere — every rule below is a fresh
/// recomputation from the policy's documented contract (see the doc comments
/// on `RestartPolicy.fs`). That independence is what makes `Oracle.check` a
/// genuine external cross-check of the real supervision core rather than a
/// tautology (a reference model that called the subject would agree with it
/// by construction and catch nothing).
///
/// Mirrors the documented rules:
///   1. If the reset window has expired since `WindowStart`, the restart
///      count resets — but only TRANSIENTLY, for the current decision (see
///      the GiveUp note below).
///   2. Circuit breaker: a crash within `StartupCrashWindow` of the previous
///      restart is a STARTUP crash — the ceiling drops to
///      `StartupCrashMaxRestarts` and the delay becomes a fixed 4x-base
///      (capped at `BackoffMax`) instead of exponential backoff.
///   3. If the (possibly window-reset) count has reached the effective
///      ceiling, give up.
///   4. Otherwise restart with exponential backoff `base * 2^(count-1)`
///      (capped), or the fixed startup delay for a startup crash.
///
/// One faithful subtlety carried over from the real composition
/// (`Runner.fs` folding `SessionLifecycle.onWorkerExited`): on a GiveUp, the
/// tracked restart-count/window state is NOT advanced to the
/// transiently-computed window-reset value — `ExitOutcome.Abandoned` carries
/// no state at all, so the real caller keeps whatever state preceded the
/// decision. The reference model reproduces this exactly; otherwise every
/// give-up-after-a-window-reset scenario would show a spurious "state
/// mismatch" that is not a real behavioral divergence.
module ReferenceModel =

  /// The kind of effect the reference model expects for a step — mirrors
  /// `Scenario.StepEffect` in shape but is an independently-named type so it
  /// is never accidentally treated as the same value.
  [<RequireQualifiedAccess>]
  type RefEffectKind =
    | NoEffect
    | Stopped
    | Restarted
    | GaveUp

  /// One independently-computed expected step.
  type ReferenceStep =
    { /// 0-based position in the scenario's event list — lines up with the
      /// real `Step.Index` for `Oracle.check`'s zip.
      Index: int
      /// The simulated clock at the moment this event is processed.
      At: DateTime
      /// The expected effect kind.
      EffectKind: RefEffectKind
      /// The expected restart delay — `Some` only when `EffectKind =
      /// Restarted`.
      Delay: TimeSpan option
      /// The expected restart-tracking count AFTER this step.
      RestartCount: int
      /// The expected terminality AFTER this step (Faulted or Stopped).
      Terminal: bool }

  /// The full independently-computed expected trace.
  type ReferenceTrace =
    { Scenario: Scenario
      Steps: ReferenceStep list }

  /// Outcome of an independently-recomputed restart decision. Kept private —
  /// only `compute`/`computeWith` observe it.
  [<RequireQualifiedAccess>]
  type private RefDecision =
    | Restart of
      delay: TimeSpan *
      newCount: int *
      newLastRestartAt: DateTime option *
      newWindowStart: DateTime option
    | GiveUp

  type private FoldState =
    { Steps: ReferenceStep list // reversed while folding
      Now: DateTime
      RestartCount: int
      LastRestartAt: DateTime option
      WindowStart: DateTime option
      Terminal: bool }

  /// Independent recompute of the exponential-backoff formula: base *
  /// 2^(count-1), capped at BackoffMax. Written fresh from the documented
  /// rule, not delegated to `RestartPolicy.nextBackoff`.
  let private exponentialDelay (policy: RestartPolicy.Policy) (count: int) : TimeSpan =
    if count <= 0 then
      policy.BackoffBase
    else
      let exponent = min count 20
      let multiplier = Math.Pow(2.0, float (exponent - 1))
      let ms = min (policy.BackoffBase.TotalMilliseconds * multiplier) policy.BackoffMax.TotalMilliseconds
      TimeSpan.FromMilliseconds ms

  /// Independent recompute of the circuit breaker's fixed startup-crash
  /// delay: 4x base, capped at BackoffMax.
  let private startupCrashDelay (policy: RestartPolicy.Policy) : TimeSpan =
    let ms = min (policy.BackoffBase.TotalMilliseconds * 4.0) policy.BackoffMax.TotalMilliseconds
    TimeSpan.FromMilliseconds ms

  /// Independently recompute the expected decision for one crash.
  /// `giveUpDelta` is 0 for the faithful reference; a non-zero value builds
  /// a deliberately-wrong variant used only to prove the oracle has teeth
  /// (see `computeWith`/`wrongGiveUpOffByOne`).
  let private decide
    (giveUpDelta: int)
    (policy: RestartPolicy.Policy)
    (count: int)
    (lastRestartAt: DateTime option)
    (windowStart: DateTime option)
    (now: DateTime)
    : RefDecision =
    // Rule 1: a crash after the reset window expired starts a fresh window —
    // transiently, for this decision only (see the GiveUp note above).
    let effCount, effLastRestartAt, effWindowStart =
      match windowStart with
      | Some start when (now - start) > policy.ResetWindow -> 0, None, None
      | _ -> count, lastRestartAt, windowStart

    // Rule 2: startup-crash circuit breaker.
    let isStartupCrash =
      match effLastRestartAt with
      | Some last when (now - last) <= policy.StartupCrashWindow -> true
      | _ -> false

    let ceiling =
      match isStartupCrash with
      | true -> min policy.StartupCrashMaxRestarts policy.MaxRestarts
      | false -> policy.MaxRestarts
    // Deliberately-wrong variants shift the ceiling; the faithful reference
    // uses giveUpDelta = 0 (ceiling unchanged).
    let effectiveCeiling = ceiling + giveUpDelta

    // Rule 3 / 4.
    match effCount >= effectiveCeiling with
    | true -> RefDecision.GiveUp
    | false ->
      let newCount = effCount + 1
      let delay =
        match isStartupCrash with
        | true -> startupCrashDelay policy
        | false -> exponentialDelay policy newCount
      let newWindowStart =
        match effWindowStart with
        | None -> Some now
        | Some _ as ws -> ws
      RefDecision.Restart(delay, newCount, Some now, newWindowStart)

  /// Independently compute the expected trace for a scenario, optionally
  /// using a deliberately-wrong give-up ceiling (`giveUpDelta`). The faithful
  /// reference is `computeWith 0`.
  let private computeWith (giveUpDelta: int) (scenario: Scenario) : ReferenceTrace =
    let policy = scenario.Policy

    let fold (s: FoldState) (idx: int, ev: SimEvent) : FoldState =
      match ev with
      | SimEvent.ClockAdvance span ->
        let now' = s.Now + span
        let step =
          { Index = idx
            At = now'
            EffectKind = RefEffectKind.NoEffect
            Delay = None
            RestartCount = s.RestartCount
            Terminal = s.Terminal }
        { s with Steps = step :: s.Steps; Now = now' }

      | _ when s.Terminal ->
        // A crash/graceful for an already-terminal session: no-op, mirrors
        // Runner.fs's terminal short-circuit.
        let step =
          { Index = idx
            At = s.Now
            EffectKind = RefEffectKind.NoEffect
            Delay = None
            RestartCount = s.RestartCount
            Terminal = s.Terminal }
        { s with Steps = step :: s.Steps }

      | SimEvent.WorkerExitedGracefully ->
        let step =
          { Index = idx
            At = s.Now
            EffectKind = RefEffectKind.Stopped
            Delay = None
            RestartCount = s.RestartCount
            Terminal = true }
        { s with Steps = step :: s.Steps; Terminal = true }

      | SimEvent.WorkerCrashed ->
        match decide giveUpDelta policy s.RestartCount s.LastRestartAt s.WindowStart s.Now with
        | RefDecision.Restart(delay, newCount, newLastRestartAt, newWindowStart) ->
          let step =
            { Index = idx
              At = s.Now
              EffectKind = RefEffectKind.Restarted
              Delay = Some delay
              RestartCount = newCount
              Terminal = false }
          { s with
              Steps = step :: s.Steps
              RestartCount = newCount
              LastRestartAt = newLastRestartAt
              WindowStart = newWindowStart }
        | RefDecision.GiveUp ->
          // Faithful to the real composition: the restart-tracking state is
          // NOT advanced here — Abandoned carries no state (see module doc).
          let step =
            { Index = idx
              At = s.Now
              EffectKind = RefEffectKind.GaveUp
              Delay = None
              RestartCount = s.RestartCount
              Terminal = true }
          { s with Steps = step :: s.Steps; Terminal = true }

    let init =
      { Steps = []
        Now = scenario.StartTime
        RestartCount = 0
        LastRestartAt = None
        WindowStart = None
        Terminal = false }

    let final =
      scenario.Events
      |> List.mapi (fun i e -> (i, e))
      |> List.fold fold init

    { Scenario = scenario; Steps = List.rev final.Steps }

  /// The faithful reference model — the source of truth for `Oracle.check`.
  let compute (scenario: Scenario) : ReferenceTrace = computeWith 0 scenario

  /// A DELIBERATELY WRONG reference: gives up one crash later than the real
  /// policy (the effective ceiling is one higher). Exists only so
  /// `Oracle.check` can be proven to have teeth — a real bug this wrong would
  /// be caught, not silently passed. Never used as the actual oracle.
  let wrongGiveUpOffByOne (scenario: Scenario) : ReferenceTrace = computeWith 1 scenario
