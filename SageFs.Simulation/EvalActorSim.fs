namespace SageFs.Simulation

open SageFs
open SageFs.AppState
open SageFs.EvalActorDecision

/// Deterministic Simulation Testing (DST) for the eval actor's mailbox
/// routing — folds the REAL `SageFs.EvalActorDecision.decide`
/// (never a reimplemented candidate) to replace four slow FSI-warmup
/// Integration suites: `ActorSplitTests` (query-liveness),
/// `EvalCancellationTests` (cancel-idempotence), `EvalActorResilienceTests`
/// (loop-survival), and `EvalActorStragglerTests` (no-resurrection /
/// generation supersession — `supersededAtWorkerBoundaryTests` there stays,
/// it is the one real process-boundary smoke worth keeping).
///
/// Same design principles as Phase 3's CohortLandingSim:
///   * Chaos is DATA: a `Scenario` is a seed plus an ordered list of
///     `EvalOp`s. Same seed + same ops => the identical trace, forever. No
///     clock, no IO, no waits, no actor, no bools/Options as fold state.
///   * The REAL decision is the subject: `EvalActorDecision.decide` is
///     folded through directly — never a reimplemented candidate.
///   * Two TWIN reducers reintroduce historical bugs so the invariants can
///     prove they have teeth: `runGenerationBlind` (resurrects a stale
///     straggler instead of dropping it) and `runSingleActor` (blocks Query
///     while Evaluating instead of serving it, as a single shared mailbox
///     would). A THIRD twin, `runUnwrapped`, reintroduces the shape
///     `ResilientActor.wrapLoop` exists to prevent: no per-op guard, so one
///     poisoned op (an unhandled handler exception) permanently kills the
///     fold — exactly what `EvalActorResilienceTests` proved doesn't happen
///     in production.
module EvalActorSim =

  /// One scripted message the eval actor's mailbox receives.
  /// `StragglerFinished generationsAgo` scripts a `Finished` stamped
  /// `generationsAgo` resets in the past (0 = the current generation, i.e.
  /// NOT stale — included so generators can also prove a fresh result is
  /// applied, not just that a stale one is dropped). `PoisonPill` models a
  /// handler exception escaping mid-processing — the exact fault
  /// `EvalActorResilienceTests` injects via `evalActorFaultInjector`.
  [<RequireQualifiedAccess>]
  type EvalOp =
    | Submit
    | Query
    | Cancel
    | Reset
    | StragglerFinished of generationsAgo: int
    | PoisonPill

  /// A fully-specified, replayable scenario.
  type Scenario = { Seed: int; Ops: EvalOp list }

  /// The eval actor's own two pieces of fold state — phase-shape and the
  /// generation counter — tracked SEPARATELY, exactly as production does
  /// (`phase` and the `sessionGeneration` ref are two distinct values
  /// threaded through the real mailbox loop, not one merged field).
  /// `Reset` is momentary in production (the mailbox awaits EvalReset's
  /// whole async body before dequeuing anything else — see `evalPhaseOf`),
  /// but it is kept as an explicit fold-state case so the shape survives a
  /// straggler's generation lookup.
  [<RequireQualifiedAccess>]
  type Activity =
    | Idle
    | Evaluating of forGeneration: SessionGeneration
    | Reset of forGeneration: SessionGeneration

  /// One entry in the fold's decision log: which op, which `EvalInput` it
  /// resolved to, the generation `decide` saw at that point, and what it
  /// decided. Recording this (rather than only counters) is what lets the
  /// invariants distinguish "a Query was rejected" from "a Submit was
  /// rejected" — both would otherwise collapse into one opaque tally.
  type LogEntry =
    { Op: EvalOp
      Input: EvalInput
      GenerationBefore: SessionGeneration
      Decision: EvalDecision }

  /// The pure fold state. `Log` is most-recent-first (cons is O(1); order
  /// does not matter to any invariant below, which all search the whole log).
  type SimState =
    { Activity: Activity
      Generation: SessionGeneration
      /// Every generation this fold has held, most-recent-first (head =
      /// Generation) — lets `StragglerFinished` resolve "N resets ago"
      /// without needing `SessionGeneration`'s private constructor.
      History: SessionGeneration list
      Log: LogEntry list }

  let initial : SimState =
    { Activity = Activity.Idle
      Generation = SessionGeneration.initial
      History = [ SessionGeneration.initial ]
      Log = [] }

  /// Project fold-time `Activity` down to the `EvalPhase` the real `decide`
  /// consumes. `Reset` is observably `Active Idle` to the NEXT op — mirroring
  /// exactly what a message dequeued right after a completed reset sees,
  /// since production's mailbox processes `EvalReset`'s entire async body
  /// before dequeuing anything else (no other op can observe a genuine
  /// mid-reset window on the SAME mailbox).
  let evalPhaseOf (activity: Activity) : EvalPhase =
    match activity with
    | Activity.Idle -> EvalPhase.Active SessionActivity.Idle
    | Activity.Evaluating _ -> EvalPhase.Active SessionActivity.Evaluating
    | Activity.Reset _ -> EvalPhase.Active SessionActivity.Idle

  /// A reducer under test: the shape of `EvalActorDecision.decide`.
  type Decide = SessionGeneration -> EvalPhase -> EvalInput -> EvalDecision

  /// Resolve one scripted op to the `EvalInput` `decide` understands.
  /// `PoisonPill` has no `EvalInput` — it is not a real message, it models
  /// an exception escaping the handler — so it resolves to `None` and the
  /// step function raises instead.
  let private inputOf (state: SimState) (op: EvalOp) : EvalInput option =
    match op with
    | EvalOp.Submit -> Some EvalInput.Submit
    | EvalOp.Query -> Some EvalInput.Query
    | EvalOp.Cancel -> Some EvalInput.Cancel
    | EvalOp.Reset -> Some EvalInput.Reset
    | EvalOp.StragglerFinished generationsAgo ->
      let target = state.History |> List.tryItem (max 0 generationsAgo) |> Option.defaultValue state.Generation
      Some(EvalInput.Finished target)
    | EvalOp.PoisonPill -> None

  /// Apply one decision to fold state — activity/generation transitions
  /// only, no side effects, no IO. Mirrors the real mailbox handlers' state
  /// updates for each `EvalDecision` case.
  let private applyDecision (state: SimState) (decision: EvalDecision) : SimState =
    match decision with
    | EvalDecision.RunEval -> { state with Activity = Activity.Evaluating state.Generation }
    | EvalDecision.RejectEval _ -> state
    | EvalDecision.ServeQuery -> state
    | EvalDecision.ApplyFinished -> { state with Activity = Activity.Idle }
    | EvalDecision.DropSupersededFinished -> state
    | EvalDecision.AdvanceGenerationAndReset ->
      let g' = SessionGeneration.next state.Generation
      { state with Generation = g'; History = g' :: state.History; Activity = Activity.Idle }

  /// Decide + apply one op, recording it in the log. Raises for `PoisonPill`
  /// — the ONE place "a handler throws" is modeled.
  let private stepOnce (decideFn: Decide) (state: SimState) (op: EvalOp) : SimState =
    match inputOf state op with
    | None -> failwith "poison: simulated handler exception (mirrors EvalActorResilienceTests' evalActorFaultInjector)"
    | Some input ->
      let generationBefore = state.Generation
      let decision = decideFn generationBefore (evalPhaseOf state.Activity) input
      let logged = { state with Log = { Op = op; Input = input; GenerationBefore = generationBefore; Decision = decision } :: state.Log }
      applyDecision logged decision

  /// GUARDED fold: each op is wrapped exactly like `ResilientActor.wrapLoop`
  /// wraps a mailbox handler — an exception (`PoisonPill`) is caught and the
  /// fold continues with the PREVIOUS state, so every later op still runs.
  /// This is production's actual shape (the eval actor IS wrapped).
  let rec private goGuarded (decideFn: Decide) (state: SimState) (ops: EvalOp list) : SimState =
    match ops with
    | [] -> state
    | op :: rest ->
      let state' = try stepOnce decideFn state op with _ -> state
      goGuarded decideFn state' rest

  /// UNGUARDED fold: no per-op guard — an exception propagates out and stops
  /// the fold entirely, exactly like an unwrapped `MailboxProcessor` loop
  /// dying permanently on an unhandled exception. Models the shape
  /// `ResilientActor.wrapLoop` exists to prevent.
  let rec private goUnguarded (decideFn: Decide) (state: SimState) (ops: EvalOp list) : SimState * bool =
    match ops with
    | [] -> state, false
    | op :: rest ->
      match (try Some(stepOnce decideFn state op) with _ -> None) with
      | Some state' -> goUnguarded decideFn state' rest
      | None -> state, true

  /// The result of folding a scenario to completion (or to a death, for the
  /// unguarded twin): the final state, whether the fold died mid-way, and
  /// which reducer produced it (for reporting).
  type Trace =
    { Scenario: Scenario
      Final: SimState
      /// true only for `runUnwrapped` when a `PoisonPill` killed the fold.
      Died: bool
      Reducer: string }

  let private runGuardedWith (name: string) (decideFn: Decide) (scenario: Scenario) : Trace =
    { Scenario = scenario; Final = goGuarded decideFn initial scenario.Ops; Died = false; Reducer = name }

  let private runUnguardedWith (name: string) (decideFn: Decide) (scenario: Scenario) : Trace =
    let final, died = goUnguarded decideFn initial scenario.Ops
    { Scenario = scenario; Final = final; Died = died; Reducer = name }

  /// TWIN 1: generation-blind — a `Finished` is ALWAYS applied, never
  /// dropped as superseded. Reintroduces the resurrection bug
  /// `EvalActorStragglerTests` existed to catch.
  let private decideGenerationBlind : Decide =
    fun generation phase input ->
      match input with
      | EvalInput.Finished _ -> EvalDecision.ApplyFinished
      | other -> decide generation phase other

  /// TWIN 2: single-actor — `Query` is BLOCKED (rejected) while Evaluating,
  /// modeling a design where Query/Cancel share the SAME mailbox as
  /// Submit/Finished/Reset instead of the separate query actor production
  /// uses. Reintroduces the query-liveness bug `ActorSplitTests` existed to
  /// catch.
  let private decideSingleActor : Decide =
    fun generation phase input ->
      match input, phase with
      | EvalInput.Query, EvalPhase.Active SessionActivity.Evaluating ->
        EvalDecision.RejectEval(SageFsError.EvalFailed "busy: single-actor mailbox blocked on the in-flight eval")
      | other, p -> decide generation p other

  /// Run through the REAL `EvalActorDecision.decide`, guarded — the subject
  /// under test, in production's own shape.
  let run (scenario: Scenario) : Trace =
    runGuardedWith "real-EvalActorDecision.decide (guarded)" decide scenario

  /// Run through the generation-blind twin — used to prove `no-resurrection`
  /// has teeth.
  let runGenerationBlind (scenario: Scenario) : Trace =
    runGuardedWith "twin-generation-blind" decideGenerationBlind scenario

  /// Run through the single-actor twin — used to prove `query-liveness` has
  /// teeth.
  let runSingleActor (scenario: Scenario) : Trace =
    runGuardedWith "twin-single-actor" decideSingleActor scenario

  /// Run the REAL decide through the UNGUARDED fold — used to prove
  /// `loop-survival` has teeth (it is the fold guard, not the decision
  /// logic, that differs here).
  let runUnwrapped (scenario: Scenario) : Trace =
    runUnguardedWith "twin-unwrapped (no wrapLoop)" decide scenario
