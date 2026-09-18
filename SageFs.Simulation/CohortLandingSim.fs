namespace SageFs.Simulation

open System
open SageFs
open SageFs.Cohort

/// Phase 3 DST: simulate the cohort's serial landing pipeline deterministically,
/// to REPRODUCE the queue-jam the fix at 396ee1c3 removed (§5.4) — a failing
/// landing left at the head of the strict-FIFO queue forever, dead-locking every
/// OTHER member's unrelated landing behind it — as a replayable regression test.
///
/// Same design principles as Phase 1/2 (see Scenario.fs, WorkerLifecycleSim.fs):
///   * Chaos is DATA: a `LandingScenario` is a seed plus an ordered list of
///     landing requests, each scripted with the `Verdict` its verification will
///     produce (passes / fails tests / rebase conflict / inconclusive). Same seed
///     + same scripts => the identical trace, forever.
///   * The REAL decision is the subject: `Cohort.decide` is the pure core folded
///     through — never a reimplemented candidate. Because `decide` returns its
///     effects as DATA (`CohortEffect`), a tiny PURE performer (`completionFor`)
///     resolves each effect to the completion command the shell would post back,
///     driven only by the scripted verdict. No git, no test runner, no clock, no
///     actor, no waits — the whole rebase -> verify -> fast-forward lifecycle
///     folds to a fixpoint.
///   * The oracle is a set of named invariants (see CohortLandingInvariants):
///     a passing landing always lands; the queue never retains a terminal
///     landing; every landing terminates.
///
/// A second, `jammed`, reducer models the PRE-FIX behavior — a `TestsCompleted`
/// with failures blocks the landing but leaves it at the queue head (no pop, no
/// advance). Everything else delegates to the real `decide`. The invariants HOLD
/// against the real reducer and are VIOLATED against the jammed one — which is
/// exactly what proves they have teeth and pins the fix.
module CohortLandingSim =

  /// Member identity in the sim is an opaque string (the cohort core is generic
  /// over `'m`; instantiating `'m = string` is all a deterministic driver needs).
  type Member = string

  /// What a landing's verification will produce, scripted up front. Modeled as a
  /// DU (not a bool "passes"/nullable failure list) so each terminal route is
  /// distinct and the performer is total.
  [<RequireQualifiedAccess>]
  type Verdict =
    /// Tests pass — the landing fast-forwards and lands.
    | Passes
    /// The affected tests fail — `Blocked(FailingTests)`, popped, `FixTests`.
    | FailsTests
    /// The rebase hits a merge conflict — `Blocked(RebaseConflict)`, popped.
    | Conflict
    /// The verifier could not reach a verdict — `Blocked(Inconclusive)`, popped,
    /// `RebaseAndResubmit` (transient, not a code failure).
    | Inconclusive

  /// One scripted landing request: who asks, and what its verification yields.
  type LandingScript =
    { Requester: Member
      Verdict: Verdict }

  /// A fully-specified, replayable scenario. The seed only shapes the minted
  /// landing/claim ids (via entropy) so distinct requests get distinct ids; the
  /// landing behavior is entirely in the scripts.
  type LandingScenario =
    { Seed: int
      Landings: LandingScript list }

  /// The result of folding a scenario to a fixpoint: the final cohort state, the
  /// scripted verdict of each landing (keyed by the id `decide` actually minted,
  /// so the invariants can reason about "this landing was supposed to pass"), and
  /// which reducer produced it (real vs the pre-fix jammed model), for reporting.
  type LandingTrace =
    { Scenario: LandingScenario
      Final: CohortState<Member>
      Verdicts: Map<LandingId, Verdict>
      Reducer: string }

  /// A reducer under test: the shape of `Cohort.decide` (and the jammed twin).
  type Decide =
    Clock
      -> Entropy
      -> CohortState<Member>
      -> CohortCommand<Member>
      -> Result<CohortState<Member> * CohortEvent<Member> list * CohortEffect<Member> list, CohortError<Member>>

  /// Every command in the sim carries the same fixed clock — the landing pipeline
  /// has no time-dependent transition (leases/reaping are a different island), so
  /// a constant keeps the trace purely a function of the scripts.
  let private clock : Clock = DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)

  /// Deterministic shas the performer hands back — a rebase produces a fresh
  /// commit distinct from the base it sat on (mirroring a real rebase; conflating
  /// the two was itself a historical bug, see LandingState.Verifying's comment).
  let private rebasedShaOf (LandingId id) = "rebased-" + id
  /// The affected-test set a `ComputeAffected` reports — one test per landing is
  /// enough to drive the `RunTests` -> `TestsCompleted` leg.
  let private affectedOf (LandingId id) = [ TestId ("t-" + id) ]

  /// The PURE performer stand-in: resolve one effect to the completion command the
  /// real shell (`CohortOwner`) would post back, driven only by the landing's
  /// scripted verdict. This is where "chaos" enters — never inside `decide`.
  let completionFor (verdictOf: LandingId -> Verdict) (eff: CohortEffect<Member>) : CohortCommand<Member> option =
    match eff with
    | CohortEffect.Rebase(id, _onto, _commits) ->
      match verdictOf id with
      | Verdict.Conflict -> Some(CohortCommand.RebaseCompleted(id, Result.Error [ "conflict.fs" ]))
      | Verdict.Passes
      | Verdict.FailsTests
      | Verdict.Inconclusive -> Some(CohortCommand.RebaseCompleted(id, Result.Ok(rebasedShaOf id)))
    | CohortEffect.ComputeAffected(id, _baseSha, _headSha) ->
      Some(CohortCommand.AffectedComputed(id, affectedOf id))
    | CohortEffect.RunTests(id, tests) ->
      match verdictOf id with
      | Verdict.Passes -> Some(CohortCommand.TestsCompleted(id, []))
      | Verdict.FailsTests -> Some(CohortCommand.TestsCompleted(id, tests))
      | Verdict.Inconclusive -> Some(CohortCommand.VerificationInconclusive(id, "scripted-inconclusive"))
      // Conflict stops the pipeline at Rebase, so RunTests is never reached for it;
      // treat it as a pass to keep the performer total.
      | Verdict.Conflict -> Some(CohortCommand.TestsCompleted(id, []))
    | CohortEffect.FastForward(id, toSha) -> Some(CohortCommand.FastForwardCompleted(id, toSha))
    // A notification drives no state transition in the pure core.
    | CohortEffect.Notify _ -> None

  /// Fold a work-list of pending effects to a fixpoint through the chosen reducer:
  /// resolve each effect to a completion, feed it back through `decide`, append any
  /// effects that completion produced, and continue until no effects remain. This
  /// is the pure analogue of `CohortOwner`'s dispatch loop — a completion that the
  /// reducer rejects (an out-of-order straggler) is simply dropped, exactly as a
  /// stale completion would be a no-op in production.
  let private drain (decide: Decide) (verdictOf: LandingId -> Verdict) (state0: CohortState<Member>) (effects0: CohortEffect<Member> list) : CohortState<Member> =
    let rec loop state effects =
      match effects with
      | [] -> state
      | eff :: rest ->
        match completionFor verdictOf eff with
        | None -> loop state rest
        | Some cmd ->
          match decide clock [||] state cmd with
          | Result.Ok(state', _events, newEffects) -> loop state' (rest @ newEffects)
          | Result.Error _ -> loop state rest
    loop state0 effects0

  /// The landing id `decide` minted for a request — read off the authoritative
  /// `LandingQueued` event rather than reproducing the private id-minting formula.
  let private landingIdOf (events: CohortEvent<Member> list) : LandingId option =
    events
    |> List.tryPick (function
      | CohortEvent.LandingQueued(id, _) -> Some id
      | _ -> None)

  /// Distinct entropy per landing index, so distinct requests mint distinct ids.
  let private entropyFor (seed: int) (index: int) : Entropy =
    Array.append (BitConverter.GetBytes seed) (BitConverter.GetBytes index)

  // ── The run ─────────────────────────────────────────────────────────────

  /// Fold a scenario through a chosen reducer:
  ///   1. every distinct member joins in first-seen order (the first is the
  ///      conductor — v1 has no separate create_cohort command);
  ///   2. every landing is requested in order — the strict-FIFO queue means only
  ///      the head goes in flight now, the rest sit `Queued`;
  ///   3. the kicked-off effects are drained to a fixpoint, so each landing runs
  ///      its full rebase -> verify -> fast-forward pipeline serially.
  /// Member joins always use the real `decide` (setup is not the subject); the
  /// requests and the drain use the reducer under test.
  let private runWith (name: string) (decide: Decide) (scenario: LandingScenario) : LandingTrace =
    let realStep (state: CohortState<Member>) (cmd: CohortCommand<Member>) : CohortState<Member> =
      match Cohort.decide clock [||] state cmd with
      | Result.Ok(state', _, _) -> state'
      | Result.Error e -> failwithf "cohort setup command %A failed unexpectedly: %A" cmd e

    // 1. distinct members join, first-seen order (the first becomes conductor).
    let members = scenario.Landings |> List.map (fun l -> l.Requester) |> List.distinct
    let joined =
      members
      |> List.fold
        (fun state who -> realStep state (CohortCommand.Join(who, JoinableRole.Implementer, Some(who + "-session"))))
        (CohortState.empty ())

    // 2. request every landing through the reducer under test, recording the id
    //    decide minted for each so verdicts key off the authoritative id.
    let afterRequests, kickoffEffects, verdicts =
      scenario.Landings
      |> List.indexed
      |> List.fold
        (fun (state, effs, verds) (i, script) ->
          let command =
            CohortCommand.RequestLanding(
              script.Requester,
              [],
              [ sprintf "commit-%s-%d" script.Requester i ],
              sprintf "landing %s #%d" script.Requester i)
          match decide clock (entropyFor scenario.Seed i) state command with
          | Result.Ok(state', events, newEffects) ->
            match landingIdOf events with
            | Some id -> state', effs @ newEffects, Map.add id script.Verdict verds
            | None -> failwithf "RequestLanding for %s produced no LandingQueued event" script.Requester
          | Result.Error e -> failwithf "RequestLanding for %s failed unexpectedly: %A" script.Requester e)
        (joined, [], Map.empty)

    // 3. drain the pipeline to a fixpoint through the reducer under test.
    let verdictOf id =
      match Map.tryFind id verdicts with
      | Some v -> v
      | None -> Verdict.Passes
    let final = drain decide verdictOf afterRequests kickoffEffects
    { Scenario = scenario; Final = final; Verdicts = verdicts; Reducer = name }

  /// The PRE-FIX reducer: models the historical queue-jam. A `TestsCompleted` that
  /// reports failures blocks the front-of-queue landing but leaves it at the head
  /// (no pop, no advance) — so the serial queue never moves past it and every
  /// following landing is dead-locked. Everything else delegates to the REAL
  /// `decide`, so the ONLY difference is the missing pop — the minimal twin that
  /// reintroduces exactly the bug the fix removed.
  let private decideJammed : Decide =
    fun clk entropy state command ->
      match command with
      | CohortCommand.TestsCompleted(id, (_ :: _ as fails)) when (state.Queue |> List.tryHead = Some id) ->
        match Map.tryFind id state.Landings with
        | Some req ->
          match req.State with
          | LandingState.Verifying _ ->
            let blocked = { req with State = LandingState.Blocked(LandingBlocker.FailingTests fails, NextAction.FixTests fails) }
            // The bug: the queue is NOT popped and advanceQueue is NOT called.
            Result.Ok(
              { state with Landings = Map.add id blocked state.Landings },
              [ CohortEvent.LandingStateChanged(id, blocked.State) ],
              ([]: CohortEffect<Member> list))
          | _ -> Cohort.decide clk entropy state command
        | None -> Cohort.decide clk entropy state command
      | _ -> Cohort.decide clk entropy state command

  /// Run through the REAL `Cohort.decide` — the subject under test.
  let run (scenario: LandingScenario) : LandingTrace = runWith "real-Cohort.decide" Cohort.decide scenario

  /// Run through the pre-fix jammed model — used to prove the invariants have
  /// teeth (they must FAIL here whenever a failing landing sits ahead of another).
  let runJammed (scenario: LandingScenario) : LandingTrace = runWith "jammed-prefix" decideJammed scenario
