/// RepeatedSameError + RetryLoop detector — Brief B5 (observed-friction-plan.md §c).
///
/// RepeatedSameError(blocker, run): a run of >= RepeatedErrorRun consecutive
/// events with the identical BlockerKind. RetryLoop(tool, attempts): a run
/// of >= RetryLoopAttempts consecutive EncounteredBlocker events on the
/// same tool (any BlockerKind — the run is keyed by tool identity, not by
/// which blocker fired). Both Confidence = Heuristic: these are
/// threshold-based judgments, not structurally certain like
/// UnattributedFailure/InvalidStateCall.
///
/// v1 granularity is BlockerKind only — there is no error-signature field
/// on FrictionEvent yet, so two EncounteredBlocker events that happen to
/// classify to the same BlockerKind but come from completely different
/// underlying error MESSAGES (e.g. two distinct compile errors that both
/// land in OperationFailed) are indistinguishable here and fold into one
/// run. Brief B9 adds a sanitized ErrorSignature field that upgrades
/// RepeatedSameError to same-*message* granularity without changing this
/// detector's shape — not implemented in this brief.
module SageFs.Features.ObservedFrictionRepetition

open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes

/// Fold a stream into maximal consecutive runs keyed by `keyOf`. An event
/// mapping to `None`, or to a key different from the run in progress,
/// closes the current run (`None` never opens a new one); an event whose
/// key matches the in-progress run extends it. Pure, single-pass,
/// order-preserving — the only signal a detector may read is the events'
/// own sequence, never the clock.
let private consecutiveRuns (keyOf: FrictionEvent -> 'k option) (events: FrictionEvent list) : ('k * int) list =
  let closeRun acc current =
    match current with
    | Some(key, run) -> (key, run) :: acc
    | None -> acc

  let acc, lastRun =
    events
    |> List.fold
      (fun (acc, current) evt ->
        match keyOf evt, current with
        | Some k, Some(curKey, curRun) when k = curKey -> acc, Some(curKey, curRun + 1)
        | Some k, current -> closeRun acc current, Some(k, 1)
        | None, current -> closeRun acc current, None)
      ([], None)

  closeRun acc lastRun |> List.rev

let private blockerKindOf (evt: FrictionEvent) =
  match evt.Outcome with
  | FrictionOutcome.EncounteredBlocker kind -> Some kind
  | _ -> None

/// The same "unknown ..." arg-parse sentinel prefix ObservedFriction.Unattributed.fs
/// and ObservedFriction.Resolution.fs key off of (McpServer.fs:175-179). Events in
/// this class are NOT a stable tool identity — each "unknown ..." call can be a
/// different real tool that failed to parse — so they must never form a RetryLoop.
/// That class is UnattributedFailure's job; excluding it here keeps the two signals
/// from double-reporting the same evidence (the harvest-replay acceptance test pins this).
[<Literal>]
let private unattributedPrefix = "unknown"

let private blockedToolOf (evt: FrictionEvent) =
  match evt.Outcome with
  | FrictionOutcome.EncounteredBlocker _ ->
    match (ToolName.value evt.Tool).StartsWith(unattributedPrefix, System.StringComparison.Ordinal) with
    | true -> None
    | false -> Some evt.Tool
  | _ -> None

let detect : Detector =
  fun cfg stream ->
    let repeatedSameErrors =
      consecutiveRuns blockerKindOf stream.Events
      |> List.filter (fun (_, run) -> run >= cfg.RepeatedErrorRun)
      |> List.map (fun (blocker, run) ->
        { Signal = FrictionSignal.RepeatedSameError(blocker, run)
          Scope = stream.Scope
          Confidence = SignalConfidence.Heuristic })

    let retryLoops =
      consecutiveRuns blockedToolOf stream.Events
      |> List.filter (fun (_, attempts) -> attempts >= cfg.RetryLoopAttempts)
      |> List.map (fun (tool, attempts) ->
        { Signal = FrictionSignal.RetryLoop(tool, attempts)
          Scope = stream.Scope
          Confidence = SignalConfidence.Heuristic })

    repeatedSameErrors @ retryLoops
