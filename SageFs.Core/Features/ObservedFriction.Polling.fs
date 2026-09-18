/// ExcessivePolling detector (Brief B1, observed-friction-plan.md §c).
///
/// Signals when a stream has >= `PollingMinCalls` calls to a
/// `PollingTools` member AND the calls:successes ratio is >=
/// `PollingMinCallsPerSuccess` (poll-without-progress), emitting
/// `ExcessivePolling(tool, calls, successes, window)` with
/// `Confidence = Heuristic`. "Successes" is progress happening elsewhere in
/// the stream while the polling continues — `CompletedCleanly` calls to any
/// tool OTHER than the one being polled — so a polling tool that is itself
/// always `CompletedCleanly` (as `get_fsi_status` normally is) does not
/// count as its own progress. The harvest's live example: 72x
/// `get_fsi_status` against a baseline of 9 unrelated successes
/// (`create_session`x8 + `diagnose`x1) is exactly 72:9 = 8, matching the
/// default `PollingMinCallsPerSuccess` threshold.
///
/// Pure and IO-free: only the timestamps already on the events are read,
/// never the clock.
module SageFs.Features.ObservedFrictionPolling

open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes

let detect : Detector =
  fun cfg stream ->
    let toolNameOf (e: FrictionEvent) = ToolName.value e.Tool

    let isCompletedCleanly (e: FrictionEvent) =
      match e.Outcome with
      | FrictionOutcome.CompletedCleanly -> true
      | _ -> false

    let pollingToolNames =
      stream.Events
      |> List.map toolNameOf
      |> List.filter cfg.PollingTools.Contains
      |> List.distinct

    pollingToolNames
    |> List.choose (fun name ->
      let pollingEvents = stream.Events |> List.filter (fun e -> toolNameOf e = name)
      let calls = List.length pollingEvents

      if calls < cfg.PollingMinCalls then
        None
      else
        let successes =
          stream.Events
          |> List.filter (fun e -> toolNameOf e <> name && isCompletedCleanly e)
          |> List.length

        // Integer form of "calls:successes ratio >= PollingMinCallsPerSuccess"
        // avoids a float division-by-zero when there are no successes at all
        // (an infinite ratio should fire, and `calls < threshold * 0` is
        // always false once `calls >= PollingMinCalls > 0`).
        if calls < cfg.PollingMinCallsPerSuccess * successes then
          None
        else
          let timestamps = pollingEvents |> List.map (fun e -> e.OccurredAtUtc)
          let window =
            { FirstAtUtc = List.min timestamps
              LastAtUtc = List.max timestamps
              EventCount = calls }
          let tool = (List.head pollingEvents).Tool
          Some
            { Signal = FrictionSignal.ExcessivePolling(tool, calls, successes, window)
              Scope = stream.Scope
              Confidence = SignalConfidence.Heuristic })
