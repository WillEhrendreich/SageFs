namespace SageFs.Simulation

open SageFs.EvalActorDecision
open SageFs.Simulation.EvalActorSim

/// Named invariants over a drained `EvalActorSim.Trace`. Same stable-id /
/// `Holds`-vs-`Violated` shape as `CohortLandingInvariants`. The oracle here
/// is INDEPENDENT of the reducer under test: it inspects only the final
/// `SimState`'s log and the scenario — it never calls `decide` — so it is a
/// genuine external check, not a tautology.
module EvalActorInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// query-liveness: every `Query` op in the log was decided `ServeQuery`,
  /// regardless of what activity it was scripted against (mid-eval or not).
  /// The single-actor twin rejects a `Query` decided while `Evaluating`, so
  /// this fires there and holds against the real reducer.
  let queryLiveness : Invariant =
    { Id = "query-liveness"
      Description = "A Query is always served immediately (ServeQuery) — never gated on activity."
      Check = fun t ->
        match t.Final.Log |> List.tryFind (fun e -> e.Op = EvalOp.Query && e.Decision <> EvalDecision.ServeQuery) with
        | Some e -> Outcome.Violated(sprintf "Query decided %A instead of ServeQuery (seed=%d, ops=%A)" e.Decision t.Scenario.Seed t.Scenario.Ops)
        | None -> Outcome.Holds }

  /// cancel-idempotence: every `Cancel` op in the log was decided
  /// `AckCancel` (production's "always attempt, always answer, never
  /// gated" contract — the reply is never blocked on the eval actor, see
  /// `decide`'s doc), regardless of how many times it is scripted in a row
  /// or what activity it lands on.
  let cancelIdempotence : Invariant =
    { Id = "cancel-idempotence"
      Description = "Cancel is always acknowledged (AckCancel), any number of times, regardless of activity — never rejected, never desynced by repetition."
      Check = fun t ->
        match t.Final.Log |> List.tryFind (fun e -> e.Op = EvalOp.Cancel && e.Decision <> EvalDecision.AckCancel) with
        | Some e -> Outcome.Violated(sprintf "Cancel decided %A instead of AckCancel (seed=%d, ops=%A)" e.Decision t.Scenario.Seed t.Scenario.Ops)
        | None -> Outcome.Holds }

  /// cancel-blocks-resubmit: once a Cancel has moved the session into
  /// Cancelling (i.e. it landed while Evaluating), no Submit is EVER
  /// decided `RunEval` until a Finished or a Reset has cleared Cancelling
  /// again. This is the state-machine half of the orphaned-loop fix: a
  /// second eval must never be spawned onto a session whose first eval
  /// hasn't confirmed it stopped — that was the live, reproduced bug
  /// (cancel an unbounded loop, then a trivial `1+1` hangs for the
  /// caller's full timeout instead of being told the session can't take a
  /// new eval yet).
  ///
  /// This oracle is independent of the reducer under test: it walks the
  /// log's own (Op, Decision) sequence — never `EvalActorSim.applyDecision`
  /// — reconstructing "was a cancel pending when this Submit was decided"
  /// from the SAME public contract every decision already publishes, the
  /// same way `noResurrection` reconstructs its check from generations.
  let cancelBlocksResubmit : Invariant =
    { Id = "cancel-blocks-resubmit"
      Description = "A Submit decided while a prior Cancel is unresolved is always rejected, never RunEval — a second eval must never share a live session with an orphaned first one."
      Check = fun t ->
        // A tiny independent 3-state tracker (Idle/Evaluating/Cancelling),
        // reconstructed from the log's own decision sequence — NOT by
        // calling EvalActorSim.applyDecision (that's the reducer under
        // test). Only an AckCancel seen while tracked-Evaluating moves to
        // Cancelling; an AckCancel with nothing running is a no-op, exactly
        // like the real apply step.
        let chronological = t.Final.Log |> List.rev
        let rec walk (evaluating: bool) (cancelling: bool) (entries: LogEntry list) : string option =
          match entries with
          | [] -> None
          | e :: rest ->
            match e.Decision with
            | EvalDecision.RunEval when cancelling ->
              Some(sprintf "a Submit was decided RunEval while a Cancel was unresolved (seed=%d, ops=%A)" t.Scenario.Seed t.Scenario.Ops)
            | EvalDecision.RunEval -> walk true false rest
            | EvalDecision.AckCancel when evaluating -> walk evaluating true rest
            | EvalDecision.AckCancel -> walk evaluating cancelling rest
            | EvalDecision.ApplyFinished
            | EvalDecision.AdvanceGenerationAndReset -> walk false false rest
            | _ -> walk evaluating cancelling rest
        match walk false false chronological with
        | Some msg -> Outcome.Violated msg
        | None -> Outcome.Holds }

  /// no-resurrection: a `Finished` whose stamped generation does NOT match
  /// the generation `decide` saw at that point is ALWAYS
  /// `DropSupersededFinished`, never `ApplyFinished` — and, for teeth in the
  /// other direction, a `Finished` for the CURRENT generation is always
  /// `ApplyFinished`, never over-eagerly dropped. The generation-blind twin
  /// violates the first half; a broken "drop everything" reducer would
  /// violate the second (neither the real reducer nor any twin here does
  /// that, but the check still guards against it).
  let noResurrection : Invariant =
    { Id = "no-resurrection"
      Description = "A Finished for a superseded generation is always dropped, never applied; a Finished for the current generation is always applied, never dropped."
      Check = fun t ->
        t.Final.Log
        |> List.tryPick (fun e ->
          match e.Input with
          | EvalInput.Finished forGeneration when forGeneration <> e.GenerationBefore ->
            match e.Decision with
            | EvalDecision.ApplyFinished ->
              Some(sprintf "a Finished for a SUPERSEDED generation was ApplyFinished, not dropped (seed=%d, ops=%A)" t.Scenario.Seed t.Scenario.Ops)
            | _ -> None
          | EvalInput.Finished forGeneration (* forGeneration = e.GenerationBefore *) ->
            match e.Decision with
            | EvalDecision.DropSupersededFinished ->
              Some(sprintf "a Finished for the CURRENT generation was dropped as superseded (seed=%d, ops=%A)" t.Scenario.Seed t.Scenario.Ops)
            | _ -> None
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// loop-survival: every non-`PoisonPill` op in the scenario was logged
  /// (i.e. actually decided and applied) — a poisoned op never stops the
  /// fold from processing what comes after it. The unwrapped twin dies at
  /// the first poison, so any scenario with a non-poison op AFTER a poison
  /// violates this against `runUnwrapped`.
  let loopSurvival : Invariant =
    { Id = "loop-survival"
      Description = "A poisoned op (handler exception) never stops later ops from being processed — the fold survives exactly like ResilientActor.wrapLoop keeps a mailbox alive."
      Check = fun t ->
        let expected = t.Scenario.Ops |> List.filter (fun op -> op <> EvalOp.PoisonPill) |> List.length
        let actual = t.Final.Log |> List.length
        match actual = expected with
        | true -> Outcome.Holds
        | false ->
          Outcome.Violated(
            sprintf "expected %d non-poison ops to be processed, only %d were (died=%b, seed=%d, ops=%A)"
              expected actual t.Died t.Scenario.Seed t.Scenario.Ops) }

  let all : Invariant list = [ queryLiveness; cancelIdempotence; cancelBlocksResubmit; noResurrection; loopSurvival ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
