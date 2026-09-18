/// Pure decision core for the eval actor's message routing — the single
/// place that decides what an EvalCommand does, extracted so a Deterministic
/// Simulation Testing harness (SageFs.Simulation/EvalActorSim.fs) can fold
/// the REAL decision logic instead of a reimplemented candidate, replacing
/// four slow FSI-warmup Integration suites (ActorSplitTests,
/// EvalCancellationTests, EvalActorResilienceTests, EvalActorStragglerTests).
///
/// A standalone file (compiled before AppState.fs, see SageFs.Core.fsproj)
/// rather than nested inside it: `SessionActivity` and `SessionGeneration`
/// have no dependency on the real, FSI-session-backed `AppState` record, so
/// they — and everything pure that's built on them here — can live outside
/// AppState.fs entirely, keeping AppState.fs under its architecture-test
/// line budget (SageFs.Tests/ArchitectureTests.fs) instead of growing it.
/// `SessionPhase` itself CANNOT move here: its `Active` case pairs a
/// `SessionActivity` with a real `AppState`, a companion type of `module
/// SageFs.AppState` (FS0250 forbids splitting a same-named companion module
/// into its own file — see project_fsharp_type_module_same_name_one_file).
/// `EvalPhase` below is the payload-erased shadow of `SessionPhase` that
/// lets this module — and the DST harness — stay independent of a real FSI
/// session; `AppState.phaseOf` is the one seam that narrows a live
/// `SessionPhase` down to it.
module SageFs.EvalActorDecision

/// Whether the active session is idle or currently evaluating code. Only
/// meaningful when the session is Active — not a top-level lifecycle state.
type SessionActivity = Idle | Evaluating

/// Which incarnation of the FSI session an eval ran against. Every reset
/// (soft or hard) replaces the session and advances the generation, so a
/// result stamped with an older one belongs to a session that no longer
/// exists.
[<Struct>]
type SessionGeneration = private SessionGeneration of int64

module SessionGeneration =
  let initial = SessionGeneration 0L
  let next (SessionGeneration g) = SessionGeneration (g + 1L)

/// A pure shadow of `SessionPhase`, payload-erased. `SageFs.AppState.phaseOf`
/// is the one seam between the real, IO-backed `SessionPhase` and this pure
/// type — the only place a live `AppState` is looked at (and only to read
/// the `SessionActivity` sitting next to it, never touched otherwise).
[<RequireQualifiedAccess>]
type EvalPhase =
  | Initializing
  | Active of SessionActivity
  | Faulted

/// Every message shape the eval actor's mailbox currently routes with ad
/// hoc `match` arms on `EvalCommand`, normalized to one DU so `decide` is
/// total and testable without a mailbox, a thread, or an FSI session.
/// RequireQualifiedAccess: `Submit`/`Cancel`/`Query`/`Reset` are common
/// words reused by the DST's own `EvalOp` — qualifying avoids silent
/// shadowing.
[<RequireQualifiedAccess>]
type EvalInput =
  | Submit
  | Cancel
  | Query
  | Finished of forGeneration: SessionGeneration
  | Reset

/// What to do about an `EvalInput`, given the actor's current generation
/// and phase. A DU — not a bool/Option — so every route is a distinct,
/// named outcome, including the two ways a `Finished` can go.
[<RequireQualifiedAccess>]
type EvalDecision =
  | RunEval
  | RejectEval of SageFsError
  | ServeQuery
  | ApplyFinished
  | DropSupersededFinished
  | AdvanceGenerationAndReset

/// The eval gate: can code be evaluated right now? Phase-based — no null
/// checks, because a live Session/OutStream exist iff phase is Active.
/// Initializing covers two windows: initial warm-up (eval never arrives —
/// init() runs before the loop processes messages) and the in-flight reset
/// window (Submit IS queued behind Reset). Gating the latter is a
/// deliberate fail-closed improvement over the old parallel SessionState
/// loop variable, which reported WarmingUp (gate passed) while the reset
/// was mid-flight — a queued eval could have run against a session being
/// torn down. Faulted carries no AppState at all, so recovery is via
/// reset. `Active` gates identically whether Idle or already Evaluating —
/// this mirrors production exactly (a second Submit while Evaluating is
/// accepted today; that quirk is out of scope here and left unchanged).
///
/// Query and Cancel are ALWAYS served immediately, regardless of phase or
/// activity — this is query-liveness. In production this is realized by
/// routing Query/Cancel to SEPARATE mailboxes (queryActor / the router)
/// from Submit/Finished/Reset (the eval actor) — `decide` states the
/// routing rule that makes that split correct rather than incidental, and
/// the DST harness's single-actor twin (which instead blocks Query while
/// Evaluating) proves the invariant has teeth.
///
/// A `Finished` stamped with a superseded generation is dropped rather
/// than applied: the eval thread that produced it can outlive a Reset
/// that disposed the session it ran on, and adopting its result would
/// resurrect a disposed session while leaking the fresh one.
let decide (generation: SessionGeneration) (phase: EvalPhase) (input: EvalInput) : EvalDecision =
  match input with
  | EvalInput.Submit ->
    match phase with
    | EvalPhase.Faulted ->
      EvalDecision.RejectEval(SageFsError.EvalFailed "Session is faulted. Run hard_reset_fsi_session to recover.")
    | EvalPhase.Initializing ->
      EvalDecision.RejectEval(SageFsError.EvalFailed "Session is resetting. Wait for reset to complete before evaluating.")
    | EvalPhase.Active _ -> EvalDecision.RunEval
  | EvalInput.Cancel
  | EvalInput.Query -> EvalDecision.ServeQuery
  | EvalInput.Finished forGeneration ->
    match forGeneration = generation with
    | true -> EvalDecision.ApplyFinished
    | false -> EvalDecision.DropSupersededFinished
  | EvalInput.Reset -> EvalDecision.AdvanceGenerationAndReset
