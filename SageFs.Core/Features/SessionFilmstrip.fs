namespace SageFs.Features

open System

/// Input event for building a filmstrip frame.
type FilmstripEvent = {
  Timestamp: DateTimeOffset
  Label: string
  BindingCount: int
  TestSummary: string option
  EvalDurationMs: float option
}

/// A single frame in the session filmstrip.
type FilmstripFrame = {
  Index: int
  Timestamp: DateTimeOffset
  Label: string
  BindingCount: int
  TestSummary: string option
  EvalDurationMs: float option
}

module SessionFilmstrip =

  /// Build a filmstrip from a sequence of events.
  let buildFilmstrip (events: FilmstripEvent list) : FilmstripFrame list =
    events
    |> List.mapi (fun i ev ->
      { Index = i
        Timestamp = ev.Timestamp
        Label = ev.Label
        BindingCount = ev.BindingCount
        TestSummary = ev.TestSummary
        EvalDurationMs = ev.EvalDurationMs })

  /// Filter frames by label substring (case-insensitive).
  let filterFrames (query: string) (frames: FilmstripFrame list) : FilmstripFrame list =
    match query with
    | "" -> frames
    | q ->
      let lower = q.ToLowerInvariant()
      frames |> List.filter (fun f -> f.Label.ToLowerInvariant().Contains(lower))

  /// Render a single frame as a compact card.
  let renderFrame (frame: FilmstripFrame) : string =
    let duration =
      match frame.EvalDurationMs with
      | Some ms -> sprintf " (%.0fms)" ms
      | None -> ""
    let tests =
      match frame.TestSummary with
      | Some s -> sprintf " [%s]" s
      | None -> ""
    sprintf "[%d] %s — %d bindings%s%s" frame.Index frame.Label frame.BindingCount duration tests

  /// Render an overview of the filmstrip.
  let renderOverview (frames: FilmstripFrame list) : string =
    let count = frames.Length
    let totalDuration =
      frames
      |> List.choose (fun f -> f.EvalDurationMs)
      |> List.sum
    sprintf "🎞 %d frames, %.0fms total eval time" count totalDuration

  /// Sparkline characters for eval durations.
  let private sparkChars = [| '▁'; '▂'; '▃'; '▄'; '▅'; '▆'; '▇'; '█' |]

  /// Generate a sparkline string from frame eval durations.
  let sparkline (frames: FilmstripFrame list) : string =
    let durations = frames |> List.map (fun f -> f.EvalDurationMs |> Option.defaultValue 0.0)
    match durations with
    | [] -> ""
    | _ ->
      let maxDur = durations |> List.max |> max 1.0
      durations
      |> List.map (fun d ->
        let idx = int (d / maxDur * 7.0) |> min 7 |> max 0
        sparkChars.[idx])
      |> List.toArray
      |> System.String
