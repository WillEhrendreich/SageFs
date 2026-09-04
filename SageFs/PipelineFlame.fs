namespace SageFs

open SageFs.EvalPipeline
open SageFs.Measures

/// Density-gated pipeline visualization.
/// Minimal: nothing. Normal: railway string. Full: proportional flame bars.
module PipelineFlame =

  /// A rendered bar for one pipeline stage.
  type FlameBar = {
    Name: string
    Width: int
    Outcome: StageOutcome
    ElapsedMs: float<ms>
  }

  /// Build proportional bars for each stage given a total available width.
  let buildBars (totalWidth: int) (stages: CompletedStage list) : FlameBar list =
    match stages with
    | [] -> []
    | _ ->
      let total = stages |> List.sumBy (fun s -> rawMsf s.ElapsedMs) |> max 0.001
      stages
      |> List.map (fun s ->
        let proportion = rawMsf s.ElapsedMs / total
        let width = proportion * float totalWidth |> int |> max 1
        { Name = s.Name
          Width = width
          Outcome = s.Outcome
          ElapsedMs = s.ElapsedMs })

  /// Render a single flame bar as a string: "██████ Parse ✓ [1.2ms]"
  let private renderBar (bar: FlameBar) : string =
    let icon =
      match bar.Outcome with
      | StageOutcome.Succeeded -> "✓"
      | StageOutcome.Failed _ -> "✗"
    let barChars = System.String('█', bar.Width)
    sprintf "%s %s %s [%.1fms]" barChars bar.Name icon (rawMsf bar.ElapsedMs)

  /// One-line summary: "✓ 4.0ms (2 stages)" or "✗ 1.5ms (1 stage failed)"
  let summary (trace: PipelineTrace<'T>) : string =
    let total = totalMs trace |> rawMsf
    let icon =
      match succeeded trace with
      | true -> "✓"
      | false -> "✗"
    let stageCount = trace.Stages.Length
    let failedCount =
      trace.Stages
      |> List.filter (fun s ->
        match s.Outcome with
        | StageOutcome.Failed _ -> true
        | StageOutcome.Succeeded -> false)
      |> List.length
    match failedCount with
    | 0 -> sprintf "%s %.1fms (%d stages)" icon total stageCount
    | n -> sprintf "%s %.1fms (%d stage%s failed)" icon total n (match n with 1 -> "" | _ -> "s")

  /// Density-gated rendering of a pipeline trace.
  let render (density: UiDensity) (trace: PipelineTrace<'T>) : string =
    match density with
    | UiDensity.Minimal -> ""
    | UiDensity.Normal -> formatRailway trace
    | UiDensity.Full ->
      match trace.Stages with
      | [] -> "(empty pipeline)"
      | stages ->
        let bars = buildBars 30 stages
        bars |> List.map renderBar |> String.concat "\n"
