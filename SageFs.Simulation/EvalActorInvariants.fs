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
  /// `ServeQuery` (production's "always attempt, always answer, never
  /// gated" contract), regardless of how many times it is scripted in a row
  /// or what activity it lands on.
  let cancelIdempotence : Invariant =
    { Id = "cancel-idempotence"
      Description = "Cancel is always served (ServeQuery), any number of times, regardless of activity — never rejected, never desynced by repetition."
      Check = fun t ->
        match t.Final.Log |> List.tryFind (fun e -> e.Op = EvalOp.Cancel && e.Decision <> EvalDecision.ServeQuery) with
        | Some e -> Outcome.Violated(sprintf "Cancel decided %A instead of ServeQuery (seed=%d, ops=%A)" e.Decision t.Scenario.Seed t.Scenario.Ops)
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

  let all : Invariant list = [ queryLiveness; cancelIdempotence; noResurrection; loopSurvival ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
