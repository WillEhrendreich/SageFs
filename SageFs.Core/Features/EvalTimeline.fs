module SageFs.Features.EvalTimeline

type EvalStatus = Success | Error | Cancelled

type TimelineEntry = {
  CellId: int
  StartMs: int64
  DurationMs: int64
  Status: EvalStatus
}

type TimelineState = {
  Entries: TimelineEntry list
}

module TimelineState =
  let empty = { Entries = [] }

  [<Literal>]
  let MaxEntries = 1000

  // W3(R8): prepend is O(1) (append was O(n)); truncate bounds unbounded growth.
  // Entries are stored newest-first after this change.
  let record (entry: TimelineEntry) (state: TimelineState) : TimelineState =
    { state with Entries = entry :: state.Entries |> List.truncate MaxEntries }

let sparkline (width: int) (state: TimelineState) : string =
  if state.Entries |> List.isEmpty then ""
  else
    let bars = [| '▁'; '▂'; '▃'; '▄'; '▅'; '▆'; '▇'; '█' |]
    // Entries are newest-first; truncate to `width` most recent, then reverse for display.
    let durations = state.Entries |> List.map (fun e -> float e.DurationMs)
    // W3(R9): Compute maxDur from the VISIBLE window only (recent), not all 1000 entries.
    // Using the global max compresses all bars to ▁ if one old outlier had high latency.
    let recent = durations |> List.truncate width |> List.rev
    let maxDur = (if recent.IsEmpty then [1.0] else recent) |> List.max |> max 1.0
    recent
    |> List.map (fun d ->
      let idx = int (d / maxDur * 7.0) |> min 7 |> max 0
      bars.[idx])
    |> Array.ofList
    |> System.String

let percentile (pct: float) (state: TimelineState) : float option =
  if state.Entries |> List.isEmpty then None
  else
    let sorted =
      state.Entries
      |> List.map (fun e -> float e.DurationMs)
      |> List.sort
    let idx = int (float (sorted.Length - 1) * pct / 100.0)
    Some sorted.[idx]

type TimelineStats = {
  Count: int
  P50Ms: float option
  P95Ms: float option
  P99Ms: float option
  MeanMs: float option
  Sparkline: string
}

let timelineStats (width: int) (state: TimelineState) : TimelineStats =
  { Count = state.Entries.Length
    P50Ms = percentile 50.0 state
    P95Ms = percentile 95.0 state
    P99Ms = percentile 99.0 state
    MeanMs =
      if state.Entries.IsEmpty then None
      else state.Entries |> List.averageBy (fun e -> float e.DurationMs) |> Some
    Sparkline = sparkline width state }
