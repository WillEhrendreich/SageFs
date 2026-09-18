/// InvalidStateCall detector (Brief B6, observed-friction-plan.md §c).
///
/// Emits InvalidStateCall(tool, blocked) aggregating
/// `EncounteredBlocker AffordanceMismatch` events per tool. Confidence =
/// Strong — an AffordanceMismatch event is structurally certain evidence
/// that the tool-call gate (`enforceToolCallGate`, Mcp.fs:686) rejected the
/// call for that tool; there is no heuristic guesswork involved.
///
/// DEPENDENCY (not a blocker): the pure detector is fully testable now
/// against synthetic AffordanceMismatch events (see
/// ObservedFrictionInvalidStateTests.fs). It only fires on REAL production
/// data once Brief B9 wires `buildGateErrorResult` (McpServer.fs:395-398)
/// to actually record an AffordanceMismatch `FrictionEvent` before
/// returning the gate rejection — today that rejection path is not
/// recorded at all, so this detector's registration in
/// `ObservedFriction.detectors` is a real, always-on detector that is
/// simply starved of matching input until B9 lands.
///
/// Pure and IO-free: only the tool name and outcome already on each event
/// are read.
module SageFs.Features.ObservedFrictionInvalidState

open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes

let private isAffordanceMismatch (e: FrictionEvent) =
  match e.Outcome with
  | FrictionOutcome.EncounteredBlocker BlockerKind.AffordanceMismatch -> true
  | _ -> false

/// Groups AffordanceMismatch events by tool and emits one InvalidStateCall
/// per distinct tool, scoped to the stream it was detected on.
let detect : Detector =
  fun _cfg stream ->
    stream.Events
    |> List.filter isAffordanceMismatch
    |> List.groupBy (fun e -> e.Tool)
    |> List.map (fun (tool, evs) ->
      { Signal = FrictionSignal.InvalidStateCall(tool, List.length evs)
        Scope = stream.Scope
        Confidence = SignalConfidence.Strong })
