module SageFs.Features.ImpactForecast

open SageFs.Features.LiveTesting
open SageFs.Features.CellDependencyGraph

/// Why is performance regressing?
[<RequireQualifiedAccess>]
type RegressionCause =
  /// More cells depend on this cell than before
  | DependencyGrowth of downstreamCount: int
  /// Cell eval latency is trending up (P95 over threshold)
  | LatencySpike of p95Ms: float
  /// Cannot determine a structural cause
  | Unknown

/// How urgent is this performance concern?
[<RequireQualifiedAccess>]
type ImpactRecommendation =
  /// Under thresholds — no action needed
  | Acceptable
  /// Approaching thresholds — worth investigating
  | Investigate
  /// Over thresholds — refactor or split the cell
  | Refactor

/// Performance impact assessment for a single cell.
type ImpactForecastReport = {
  CellId: CellId
  P50Ms: float
  P95Ms: float
  DurationTrendMs: float list
  DownstreamCellCount: int
  RegressionCauses: RegressionCause list
  Recommendation: ImpactRecommendation
}

module ImpactForecastReport =
  let empty cellId = {
    CellId = cellId
    P50Ms = 0.0
    P95Ms = 0.0
    DurationTrendMs = []
    DownstreamCellCount = 0
    RegressionCauses = []
    Recommendation = ImpactRecommendation.Acceptable
  }

/// Pure composition: joins EvalTimeline stats + CellDependencyGraph + PerformanceAnnotation
/// into regression detection and downstream impact analysis.
module ImpactForecast =

  /// Thresholds for recommendation classification
  let [<Literal>] P95AcceptableMs = 500.0
  let [<Literal>] P95InvestigateMs = 2000.0
  let [<Literal>] DownstreamAcceptable = 5
  let [<Literal>] DownstreamInvestigate = 15

  /// Classify recommendation based on P95 latency and downstream count.
  let classifyRecommendation (p95Ms: float) (downstreamCount: int) : ImpactRecommendation =
    match p95Ms > P95InvestigateMs || downstreamCount > DownstreamInvestigate with
    | true -> ImpactRecommendation.Refactor
    | false ->
      match p95Ms > P95AcceptableMs || downstreamCount > DownstreamAcceptable with
      | true -> ImpactRecommendation.Investigate
      | false -> ImpactRecommendation.Acceptable

  /// Detect regression causes from performance data.
  let detectRegressionCauses
    (p95Ms: float)
    (downstreamCount: int)
    : RegressionCause list =
    [
      match p95Ms > P95AcceptableMs with
      | true -> RegressionCause.LatencySpike p95Ms
      | false -> ()
      match downstreamCount > DownstreamAcceptable with
      | true -> RegressionCause.DependencyGrowth downstreamCount
      | false -> ()
    ]

  /// Analyze impact for a single cell given its timeline stats and dependency info.
  let analyzeCell
    (cellId: CellId)
    (p50Ms: float)
    (p95Ms: float)
    (durationTrend: float list)
    (downstreamCellIds: CellId list)
    : ImpactForecastReport =
    let downstreamCount = downstreamCellIds.Length
    let causes = detectRegressionCauses p95Ms downstreamCount
    let recommendation = classifyRecommendation p95Ms downstreamCount
    {
      CellId = cellId
      P50Ms = p50Ms
      P95Ms = p95Ms
      DurationTrendMs = durationTrend
      DownstreamCellCount = downstreamCount
      RegressionCauses = causes
      Recommendation = recommendation
    }

  /// Compute a trend direction from a duration list.
  /// Returns positive if trending up (getting slower), negative if trending down.
  let trendSlope (durations: float list) : float =
    match durations with
    | [] | [_] -> 0.0
    | _ ->
      let n = durations.Length
      let half = n / 2
      let firstHalf = durations |> List.take half
      let secondHalf = durations |> List.skip (n - half)
      let avgFirst = firstHalf |> List.average
      let avgSecond = secondHalf |> List.average
      avgSecond - avgFirst

  /// Summarize an ImpactForecastReport as human-readable text.
  let summarize (report: ImpactForecastReport) : string =
    let icon =
      match report.Recommendation with
      | ImpactRecommendation.Acceptable -> "✅"
      | ImpactRecommendation.Investigate -> "⚠️"
      | ImpactRecommendation.Refactor -> "🔴"
    let header = sprintf "%s Cell %A — P50: %.0fms, P95: %.0fms, %d downstream"
                   icon report.CellId report.P50Ms report.P95Ms report.DownstreamCellCount
    let lines = ResizeArray<string>()
    lines.Add header
    match report.RegressionCauses with
    | [] ->
      lines.Add "  Performance is within acceptable thresholds"
    | causes ->
      causes |> List.iter (fun cause ->
        match cause with
        | RegressionCause.LatencySpike p95 ->
          lines.Add(sprintf "  ⚡ P95 latency spike: %.0fms (threshold: %.0fms)" p95 P95AcceptableMs)
        | RegressionCause.DependencyGrowth count ->
          lines.Add(sprintf "  🔗 High downstream impact: %d cells depend on this" count)
        | RegressionCause.Unknown ->
          lines.Add "  ❓ Unknown regression cause")
    match report.Recommendation with
    | ImpactRecommendation.Refactor ->
      lines.Add "  ⚡ Consider splitting this cell or optimizing hot paths"
    | ImpactRecommendation.Investigate ->
      lines.Add "  🔍 Worth investigating — approaching performance thresholds"
    | ImpactRecommendation.Acceptable -> ()
    lines |> String.concat "\n"
