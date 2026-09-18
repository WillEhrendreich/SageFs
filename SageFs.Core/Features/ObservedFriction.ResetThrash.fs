/// ResetThrash + HardResetAfterCreate detector — Brief B2
/// (observed-friction-plan.md §c).
///
/// ResetThrash(resets, window): fires when the stream contains a cluster of
/// >= ResetThrashCount calls to reset_fsi_session/hard_reset_fsi_session
/// whose span never exceeds ResetThrashWindow — repeatedly resetting the
/// session in a short burst, the "I gave up and am mashing reset" pattern.
///
/// HardResetAfterCreate(gap): fires per hard_reset_fsi_session call whose
/// nearest preceding create_session is within HardResetAfterCreateWindow —
/// creating a session and immediately needing a hard reset suggests the
/// fresh session never came up cleanly.
///
/// Both Confidence = Heuristic (threshold-based; a deliberate, well-spaced
/// reset or a hard reset long after session creation must never fire).
/// Pure and IO-free: only the timestamps already on the events are read,
/// never the clock.
module SageFs.Features.ObservedFrictionResetThrash

open System
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes

[<Literal>]
let private resetSessionTool = "reset_fsi_session"

[<Literal>]
let private hardResetSessionTool = "hard_reset_fsi_session"

[<Literal>]
let private createSessionTool = "create_session"

let private isResetEvent (e: FrictionEvent) =
  let name = ToolName.value e.Tool
  name = resetSessionTool || name = hardResetSessionTool

let private isHardResetEvent (e: FrictionEvent) =
  ToolName.value e.Tool = hardResetSessionTool

let private isCreateSessionEvent (e: FrictionEvent) =
  ToolName.value e.Tool = createSessionTool

/// Extends the cluster starting at index `i` (already at `baseline`'s
/// timestamp), returning the largest index `j` such that every event in
/// `events.[i..j]` falls within `window` of `baseline`. Events are assumed
/// sorted ascending by `OccurredAtUtc`.
let rec private extendCluster (window: TimeSpan) (events: FrictionEvent[]) (baseline: DateTimeOffset) j =
  if j + 1 < events.Length && events.[j + 1].OccurredAtUtc - baseline <= window then
    extendCluster window events baseline (j + 1)
  else
    j

/// The earliest cluster of >= ResetThrashCount reset/hard-reset calls whose
/// span never exceeds ResetThrashWindow, if one exists. Events outside any
/// such window-bounded cluster never aggregate toward the count — a run of
/// resets each more than ResetThrashWindow apart never fires, no matter how
/// many accumulate over the full stream.
let private findResetThrashCluster (cfg: DetectorConfig) (events: FrictionEvent[]) =
  [ 0 .. events.Length - 1 ]
  |> List.tryPick (fun i ->
    let j = extendCluster cfg.ResetThrashWindow events events.[i].OccurredAtUtc i
    let count = j - i + 1
    if count >= cfg.ResetThrashCount then Some events.[i .. j] else None)

let private resetThrashSignal (cfg: DetectorConfig) (stream: PreparedStream) : DetectedSignal option =
  stream.Events
  |> List.filter isResetEvent
  |> List.sortBy (fun e -> e.OccurredAtUtc)
  |> List.toArray
  |> findResetThrashCluster cfg
  |> Option.map (fun cluster ->
    let window =
      { FirstAtUtc = cluster.[0].OccurredAtUtc
        LastAtUtc = cluster.[cluster.Length - 1].OccurredAtUtc
        EventCount = cluster.Length }
    { Signal = FrictionSignal.ResetThrash(cluster.Length, window)
      Scope = stream.Scope
      Confidence = SignalConfidence.Heuristic })

/// The gap from a hard_reset event back to its nearest PRECEDING
/// create_session event, if one exists before it.
let private gapToNearestPrecedingCreate (sortedCreates: FrictionEvent[]) (hardReset: FrictionEvent) =
  sortedCreates
  |> Array.filter (fun c -> c.OccurredAtUtc <= hardReset.OccurredAtUtc)
  |> Array.tryLast
  |> Option.map (fun create -> hardReset.OccurredAtUtc - create.OccurredAtUtc)

let private hardResetAfterCreateSignals (cfg: DetectorConfig) (stream: PreparedStream) : DetectedSignal list =
  let sortedCreates =
    stream.Events
    |> List.filter isCreateSessionEvent
    |> List.sortBy (fun e -> e.OccurredAtUtc)
    |> List.toArray

  stream.Events
  |> List.filter isHardResetEvent
  |> List.choose (fun hardReset ->
    gapToNearestPrecedingCreate sortedCreates hardReset
    |> Option.filter (fun gap -> gap >= TimeSpan.Zero && gap <= cfg.HardResetAfterCreateWindow)
    |> Option.bind (fun gap ->
      match DurationMs.create (int gap.TotalMilliseconds) with
      | Ok duration -> Some duration
      | Error _ -> None)
    |> Option.map (fun duration ->
      { Signal = FrictionSignal.HardResetAfterCreate duration
        Scope = stream.Scope
        Confidence = SignalConfidence.Heuristic }))

let detect : Detector =
  fun cfg stream ->
    (resetThrashSignal cfg stream |> Option.toList) @ hardResetAfterCreateSignals cfg stream
