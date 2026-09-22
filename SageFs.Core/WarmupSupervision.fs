namespace SageFs

open System

/// Pure, deterministic decision logic for a session's warmup lifecycle:
/// the window between a worker process being spawned and the session
/// reaching Ready, Faulted, or Stopped. No IO, no side effects — just
/// decisions based on state, mirroring SessionLifecycle.fs's doctrine for
/// worker-exit decisions.
///
/// This exists because the real behavior used to live inline in
/// SessionManager.fs's ready-poll `Async.Start` loop, untestable without a
/// real worker process, and because two of the module's OWN config knobs —
/// `Timeouts.warmupAbsoluteMax` and `Timeouts.warmupInactivityLimit` — were
/// defined and unit-tested for "is positive" but never actually read by any
/// warmup code path. Three fcs-onboarding-trial reports (2026-09-22)
/// independently hit the seam those knobs were meant to cover: a session
/// created against a 60+ project repo warmed up for 20+ minutes with no
/// error and no sign anything was still happening.
///
/// `decidePoll` is the poll-tick decision (SessionManager's warmup-ready
/// loop calls it once per second). `step` is the fuller lifecycle model —
/// poll ticks plus a `StopRequested` event racing in at any point — used by
/// the DST scenarios in SageFs.Simulation (WarmupSim.fs).
module WarmupSupervision =

  /// What a single readiness poll observed. Decoupled from the wire
  /// `WorkerResponse`/`SessionStatus` DUs so this module — and its tests —
  /// never need a real worker process or HTTP transport.
  [<RequireQualifiedAccess>]
  type PollObservation<'Loaded> =
    | Ready of loaded: 'Loaded
    | Faulted of reason: string option
    /// Real forward motion (e.g. a WARMUP_PROGRESS= line, or a worker's own
    /// "still warming, N/M projects loaded" self-report). Resets the
    /// inactivity clock — this is what lets a genuinely large repo take
    /// minutes without being killed, while a repo that has gone silent gets
    /// caught fast.
    | Progressed
    /// A tick with nothing new to report — no progress, no fault, no ready.
    | StillWarming
    /// The probe itself failed (timeout, connection refused) — NOT the same
    /// as the worker reporting Faulted. Treated the same as `StillWarming`:
    /// a flaky probe should not immediately kill a session that is, in
    /// truth, still warming up; only sustained silence does that.
    | ProbeFailed of reason: string

  /// The two bounds a warmup is judged against. `Absolute` is the hard
  /// ceiling no warmup may ever exceed, active or not. `Inactivity` is how
  /// long a warmup may go with NO observed progress before it is declared
  /// stuck — this is what turns "no error for 20 minutes" into "an error
  /// within 30 seconds of the moment nothing was happening any more."
  type Bounds =
    { Absolute: TimeSpan
      Inactivity: TimeSpan }

  /// What to do about one poll tick.
  [<RequireQualifiedAccess>]
  type PollDecision<'Loaded> =
    | MarkReady of 'Loaded
    | MarkFaulted of reason: string
    | KeepPolling
    /// Either bound was exceeded — the warmup is declared over FOR the
    /// worker instead of polling forever. The reason text says which bound
    /// tripped, so the fix (raise the bound vs. investigate why nothing is
    /// happening) is obvious from the message alone.
    | TimedOut of reason: string

  /// The default reason given when a worker never self-reports why it
  /// faulted. Shared so production and tests describe the same failure the
  /// same way.
  let defaultFaultReason =
    "The worker failed during warmup. → Check the daemon log, then hard-reset the session with rebuild=true."

  let absoluteTimeoutReason (elapsed: TimeSpan) : string =
    sprintf
      "Session warmup timed out after %.0fs (absolute limit) — worker did not reach Ready state. On a large repo, name one explicit project instead of projects=[]. Use hard_reset_fsi_session with rebuild=true to retry."
      elapsed.TotalSeconds

  let inactivityTimeoutReason (sinceLastActivity: TimeSpan) : string =
    sprintf
      "Session warmup stalled — no progress for %.0fs. The worker is probably stuck, not just slow. Use hard_reset_fsi_session with rebuild=true to retry, or check the daemon log."
      sinceLastActivity.TotalSeconds

  /// INVARIANT: for any `elapsed > bounds.Absolute`, the result is ALWAYS
  /// `TimedOut`, regardless of what the observation says — the absolute
  /// bound cannot be argued past by a stream of `Progressed` observations.
  /// For `elapsed <= bounds.Absolute`, a `StillWarming`/`ProbeFailed`
  /// observation whose `sinceLastActivity` already exceeds
  /// `bounds.Inactivity` ALSO forces `TimedOut` — silence has its own,
  /// tighter bound. This is what makes "create always reaches Ready or a
  /// stated failure within a bound" true by construction, and what makes a
  /// SILENT stall fail fast even while a large-but-progressing warmup is
  /// still within its generous absolute ceiling.
  let decidePoll<'Loaded>
    (bounds: Bounds)
    (elapsed: TimeSpan)
    (sinceLastActivity: TimeSpan)
    (observation: PollObservation<'Loaded>)
    : PollDecision<'Loaded> =
    match elapsed > bounds.Absolute with
    | true -> PollDecision.TimedOut (absoluteTimeoutReason elapsed)
    | false ->
      match observation with
      | PollObservation.Ready loaded -> PollDecision.MarkReady loaded
      | PollObservation.Faulted reason ->
        PollDecision.MarkFaulted (reason |> Option.defaultValue defaultFaultReason)
      | PollObservation.Progressed -> PollDecision.KeepPolling
      | PollObservation.StillWarming | PollObservation.ProbeFailed _ ->
        match sinceLastActivity > bounds.Inactivity with
        | true -> PollDecision.TimedOut (inactivityTimeoutReason sinceLastActivity)
        | false -> PollDecision.KeepPolling

  // ---------------------------------------------------------------------
  // Full lifecycle model — poll ticks plus a Stop racing in at any point.
  // Used by the DST scenarios (SageFs.Simulation/WarmupSim.fs).
  // ---------------------------------------------------------------------

  /// A session's warmup-lifecycle state, as observed by anything asking
  /// "what state is this session in right now" (get_fsi_status, /api/sessions,
  /// the dashboard). Terminal states (`Faulted`, `Stopped`) are absorbing:
  /// once reached, no later event moves the session out of them again —
  /// that is the "one source of truth" invariant. `Ready` is reachable only
  /// from `Starting` and is itself non-terminal (a Ready session can still
  /// be stopped).
  [<RequireQualifiedAccess>]
  type LifecycleState<'Loaded> =
    | Starting
    | Ready of 'Loaded
    | Faulted of reason: string
    | Stopped

  [<RequireQualifiedAccess>]
  type LifecycleEvent<'Loaded> =
    | PollTick of PollObservation<'Loaded>
    | StopRequested
    | ClockAdvance of span: TimeSpan

  type LifecycleModel<'Loaded> =
    { State: LifecycleState<'Loaded>
      Elapsed: TimeSpan
      SinceLastActivity: TimeSpan
      Bounds: Bounds }

  module LifecycleModel =
    let starting (bounds: Bounds) : LifecycleModel<'Loaded> =
      { State = LifecycleState.Starting
        Elapsed = TimeSpan.Zero
        SinceLastActivity = TimeSpan.Zero
        Bounds = bounds }

  /// A session is terminal once it can never again change state on its own —
  /// only whatever mints a brand-new session can revive it. Stop is the one
  /// event that is always accepted, from any state, terminal or not — it
  /// idempotently confirms Stopped rather than being ignored.
  let isTerminal (state: LifecycleState<'Loaded>) : bool =
    match state with
    | LifecycleState.Faulted _ | LifecycleState.Stopped -> true
    | LifecycleState.Starting | LifecycleState.Ready _ -> false

  /// Fold one event through the model.
  ///
  /// INVARIANTS this function is required to uphold (proven by
  /// SageFs.Simulation/WarmupInvariants.fs and SageFs.Tests's property
  /// tests over this module):
  ///  1. StopAlwaysWins — `StopRequested` from ANY state (including
  ///     already-Faulted or mid-Starting) always yields `Stopped` in this
  ///     one step. A stop is never deferred, queued behind warmup, or lost.
  ///  2. NoResurrection — once `Stopped` or `Faulted`, no later `PollTick`
  ///     (even a stale `Ready` or `Faulted` observation racing in late)
  ///     moves the state anywhere else.
  ///  3. BoundedReach — `Starting` can only persist while
  ///     `Elapsed <= Bounds.Absolute` AND `SinceLastActivity <=
  ///     Bounds.Inactivity`; any event processed once either is exceeded
  ///     forces a transition out of `Starting` (to `Faulted`), even if the
  ///     event itself is a `StillWarming`/`ProbeFailed` observation.
  ///  4. ProgressResetsInactivity — a `Progressed` observation resets
  ///     `SinceLastActivity` to zero; a `StillWarming`/`ProbeFailed`
  ///     observation does not.
  let step<'Loaded> (model: LifecycleModel<'Loaded>) (event: LifecycleEvent<'Loaded>) : LifecycleModel<'Loaded> =
    match event with
    | LifecycleEvent.ClockAdvance span ->
      { model with
          Elapsed = model.Elapsed + span
          SinceLastActivity = model.SinceLastActivity + span }
    | LifecycleEvent.StopRequested ->
      // Invariant 1 (StopAlwaysWins): unconditional, regardless of current
      // state. A session mid-warmup, already faulted, or already stopped
      // all converge on Stopped in this one step — "a stop always
      // completes" is true of the DECISION, independent of how long the
      // IO shell around it (process kill, HTTP shutdown) takes.
      { model with State = LifecycleState.Stopped }
    | LifecycleEvent.PollTick observation ->
      match model.State with
      | LifecycleState.Stopped | LifecycleState.Faulted _ ->
        // Invariant 2 (NoResurrection): a late/stale poll tick after the
        // session is already terminal changes nothing.
        model
      | LifecycleState.Ready _ ->
        // Already ready; a stray extra tick (the poll loop should have
        // stopped) is inert rather than reopening warmup.
        model
      | LifecycleState.Starting ->
        match decidePoll model.Bounds model.Elapsed model.SinceLastActivity observation with
        | PollDecision.MarkReady loaded -> { model with State = LifecycleState.Ready loaded }
        | PollDecision.MarkFaulted reason -> { model with State = LifecycleState.Faulted reason }
        | PollDecision.TimedOut reason -> { model with State = LifecycleState.Faulted reason }
        | PollDecision.KeepPolling ->
          // Invariant 4 (ProgressResetsInactivity).
          match observation with
          | PollObservation.Progressed -> { model with SinceLastActivity = TimeSpan.Zero }
          | _ -> model

  /// Fold a whole ordered event list through the model from a fresh
  /// `Starting` state. Used directly by property tests and by the DST
  /// scenario runner.
  let run<'Loaded> (bounds: Bounds) (events: LifecycleEvent<'Loaded> list) : LifecycleModel<'Loaded> =
    events |> List.fold step (LifecycleModel.starting bounds)
