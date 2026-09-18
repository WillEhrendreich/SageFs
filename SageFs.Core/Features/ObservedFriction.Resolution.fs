/// Abandonment + SlowTimeToFirstSuccess detector — Brief B4
/// (observed-friction-plan.md §c).
///
/// Abandonment(tool, lastBlocker): a tool's LAST occurrence in the stream is
/// EncounteredBlocker, with no later CompletedCleanly for that tool
/// (trivially true — it is the last occurrence) AND the stream continues
/// with at least one event on a DIFFERENT tool afterward (the agent moved
/// on rather than stopping). A stream that simply ends on that failure — no
/// event follows it at all — is NOT abandonment: the agent may still be
/// working. Confidence = Heuristic.
///
/// SlowTimeToFirstSuccess(elapsed, failedBefore): the gap from the first
/// create_session event to the first CompletedCleanly event that follows it
/// exceeds cfg.SlowFirstSuccess, counting the blockers encountered strictly
/// in between. Confidence = Heuristic.
///
/// Pure and IO-free: only the timestamps already on the events are read,
/// never the clock.
module SageFs.Features.ObservedFrictionResolution

open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes

/// The tool name that anchors "time to first success" — the point an
/// agent's session actually starts. A private literal, not a scattered
/// string (mirrors ObservedFriction.Unattributed.fs's `unattributedPrefix`).
[<Literal>]
let private createSessionTool = "create_session"

/// The same sentinel prefix ObservedFriction.Unattributed.fs keys off of
/// (McpServer.fs:175-179 — an arg-parse failure whose real tool name never
/// resolved). Events in this class are NOT a stable tool identity: each
/// "unknown ..." call can be a different real tool that failed to parse, so
/// grouping them as "the tool the agent gave up on" would misattribute.
/// That class is UnattributedFailure's job, not Abandonment's — exclude it
/// here so the two signals never double-report the same evidence.
[<Literal>]
let private unattributedPrefix = "unknown"

let private toolNameOf (e: FrictionEvent) = ToolName.value e.Tool

let private isUnattributedSentinel (e: FrictionEvent) =
  (toolNameOf e).StartsWith(unattributedPrefix, System.StringComparison.Ordinal)

let private isCompletedCleanly (e: FrictionEvent) =
  match e.Outcome with
  | FrictionOutcome.CompletedCleanly -> true
  | _ -> false

let private isBlocker (e: FrictionEvent) =
  match e.Outcome with
  | FrictionOutcome.EncounteredBlocker _ -> true
  | _ -> false

/// A tool's last occurrence being a blocker, with something else afterward,
/// is the whole signal — the resolution guard (a later success on the SAME
/// tool means that occurrence is no longer "last") falls out of grouping by
/// tool and taking each group's last element.
let private detectAbandonment (stream: PreparedStream) : DetectedSignal list =
  let events = stream.Events
  let lastIdx = List.length events - 1
  events
  |> List.indexed
  |> List.filter (fun (_, e) -> not (isUnattributedSentinel e))
  |> List.groupBy (fun (_, e) -> toolNameOf e)
  |> List.choose (fun (_, occurrences) ->
    let (idx, lastEvent) = List.last occurrences
    match lastEvent.Outcome with
    | FrictionOutcome.EncounteredBlocker blocker when idx < lastIdx ->
      Some
        { Signal = FrictionSignal.Abandonment(lastEvent.Tool, blocker)
          Scope = stream.Scope
          Confidence = SignalConfidence.Heuristic }
    | _ -> None)

/// The first create_session anchors the clock; the first CompletedCleanly
/// strictly after it (even if create_session itself was CompletedCleanly —
/// a session can start clean and still take a long time to produce its
/// first real success) is the finish line.
let private detectSlowFirstSuccess (cfg: DetectorConfig) (stream: PreparedStream) : DetectedSignal list =
  let events = stream.Events |> List.indexed

  events
  |> List.tryFind (fun (_, e) -> toolNameOf e = createSessionTool)
  |> Option.bind (fun (createIdx, createEvent) ->
    events
    |> List.tryFind (fun (idx, e) -> idx > createIdx && isCompletedCleanly e)
    |> Option.bind (fun (successIdx, successEvent) ->
      let elapsed = successEvent.OccurredAtUtc - createEvent.OccurredAtUtc
      if elapsed <= cfg.SlowFirstSuccess then
        None
      else
        let failedBefore =
          events
          |> List.filter (fun (idx, e) -> idx > createIdx && idx < successIdx && isBlocker e)
          |> List.length
        match DurationMs.create (int elapsed.TotalMilliseconds) with
        | Error _ -> None
        | Ok duration ->
          Some
            { Signal = FrictionSignal.SlowTimeToFirstSuccess(duration, failedBefore)
              Scope = stream.Scope
              Confidence = SignalConfidence.Heuristic }))
  |> Option.toList

let detect : Detector =
  fun cfg stream -> detectAbandonment stream @ detectSlowFirstSuccess cfg stream
