namespace SageFs.Simulation

open System
open SageFs
open SageFs.WarmupSupervision

/// Deterministic Simulation Testing (DST) for a session's warmup lifecycle
/// (`SageFs.Core/WarmupSupervision.fs`) — the fix for three defects three
/// independent onboarding trials hit on the FSharp.Compiler.Service
/// checkout (fcs-trial-a/b/c, 2026-09-22): warmup with no bound and no
/// visible progress, `stop_session` racing a warming or already-faulted
/// session, and a session's reported state disagreeing with itself
/// depending on which surface asked and when.
///
/// THE HISTORICAL BUG (the twin below reproduces its SHAPE): before this
/// fix, the pre-port phase's only guard was a single `CancelAfter` set ONCE
/// at spawn — a flat elapsed bound with no notion of "still making
/// progress." A worker resolving many projects on a large repo and a
/// worker that had genuinely hung looked identical to that guard: both were
/// just "no port yet." Set the flat bound generous enough to tolerate a big
/// repo, and a truly stuck worker inside that same window goes uncaught —
/// exactly fcs-trial-a's 20+ minutes with no error.
///
/// WHY THE REAL CODE IS THE SUBJECT: `run` calls
/// `SageFs.WarmupSupervision.run`/`.step` directly — there is no
/// reimplemented candidate for the fix itself. `runFlatBoundOnly` is a
/// SEPARATE, deliberately-regressed reducer standing in for the pre-fix
/// shape, so the invariants in `WarmupInvariants.fs` can be proven to
/// actually discriminate between the fix and the bug it replaced, not just
/// hold vacuously.
module WarmupSim =

  /// Stand-in for the worker's resolved project list (`ClassifiedProject
  /// list` in production) — opaque to the sim, which only needs equality.
  type Loaded = string

  type Scenario =
    { Seed: int
      Bounds: Bounds
      Events: LifecycleEvent<Loaded> list }

  type Trace =
    { Scenario: Scenario
      Final: LifecycleModel<Loaded>
      Reducer: string }

  /// Run through the REAL, fixed lifecycle model.
  let run (scenario: Scenario) : Trace =
    { Scenario = scenario
      Final = WarmupSupervision.run scenario.Bounds scenario.Events
      Reducer = "real (WarmupSupervision.step — inactivity + absolute bounds)" }

  // ---------------------------------------------------------------------
  // TWIN — a single flat elapsed bound, no inactivity check at all.
  // ---------------------------------------------------------------------

  let private flatBoundDecide (bound: TimeSpan) (elapsed: TimeSpan) (observation: PollObservation<Loaded>) : PollDecision<Loaded> =
    match elapsed > bound with
    | true -> PollDecision.TimedOut (sprintf "flat bound of %.0fs exceeded" bound.TotalSeconds)
    | false ->
      match observation with
      | PollObservation.Ready loaded -> PollDecision.MarkReady loaded
      | PollObservation.Faulted reason -> PollDecision.MarkFaulted (reason |> Option.defaultValue defaultFaultReason)
      // The historical bug: Progressed, StillWarming and ProbeFailed are all
      // indistinguishable to a flat bound — none of them reset anything,
      // because there is nothing to reset.
      | PollObservation.Progressed | PollObservation.StillWarming | PollObservation.ProbeFailed _ ->
        PollDecision.KeepPolling

  let private flatBoundStep (bound: TimeSpan) (model: LifecycleModel<Loaded>) (event: LifecycleEvent<Loaded>) : LifecycleModel<Loaded> =
    match event with
    | LifecycleEvent.ClockAdvance span ->
      { model with Elapsed = model.Elapsed + span; SinceLastActivity = model.SinceLastActivity + span }
    | LifecycleEvent.StopRequested ->
      { model with State = LifecycleState.Stopped }
    | LifecycleEvent.PollTick observation ->
      match model.State with
      | LifecycleState.Stopped | LifecycleState.Faulted _ -> model
      | LifecycleState.Ready _ -> model
      | LifecycleState.Starting ->
        match flatBoundDecide bound model.Elapsed observation with
        | PollDecision.MarkReady l -> { model with State = LifecycleState.Ready l }
        | PollDecision.MarkFaulted r -> { model with State = LifecycleState.Faulted r }
        | PollDecision.TimedOut r -> { model with State = LifecycleState.Faulted r }
        | PollDecision.KeepPolling -> model

  /// Run through the twin: one flat bound, no inactivity check. Pass a
  /// bound generous enough to tolerate a large repo (e.g. the real fix's
  /// own `Bounds.Absolute`) to reproduce the exact historical bind: a
  /// session silent for well under that generous bound, but well OVER what
  /// the real fix's inactivity bound would tolerate, is never caught.
  let runFlatBoundOnly (flatBound: TimeSpan) (scenario: Scenario) : Trace =
    { Scenario = scenario
      Final = scenario.Events |> List.fold (flatBoundStep flatBound) (LifecycleModel.starting scenario.Bounds)
      Reducer = sprintf "twin-flat-bound-only (%.0fs, no inactivity check)" flatBound.TotalSeconds }
