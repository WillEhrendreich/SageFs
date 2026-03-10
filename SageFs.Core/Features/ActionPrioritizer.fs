module SageFs.Features.ActionPrioritizer

open SageFs.Features.LiveTesting
open SageFs.Features.CellDependencyGraph
open SageFs.Features.CoverageIntel
open SageFs.Features.ImpactForecast

/// What kind of action should the developer take?
[<RequireQualifiedAccess>]
type ActionKind =
  /// Re-evaluate a stale cell
  | ReEvaluateCell of CellId
  /// Write tests for a blind spot
  | WriteTest of filePath: string * line: int
  /// Investigate a test failure
  | InvestigateFailure of TestId
  /// Investigate a performance regression
  | InvestigatePerformance of CellId
  /// Run affected tests
  | RunTests

/// A single prioritized developer action.
type PrioritizedAction = {
  Kind: ActionKind
  Priority: int
  Reason: string
}

/// Overall health grade for the session.
[<RequireQualifiedAccess>]
type SessionHealthGrade =
  /// All green — no action needed
  | Healthy
  /// Some issues worth attention
  | NeedsAttention of issueCount: int
  /// Critical issues — fix before continuing
  | Critical of issueCount: int

/// Queue of prioritized actions with session health.
type ActionQueueReport = {
  Actions: PrioritizedAction list
  HealthGrade: SessionHealthGrade
  TotalFailures: int
  TotalBlindSpots: int
  TotalRegressions: int
}

module ActionQueueReport =
  let empty = {
    Actions = []
    HealthGrade = SessionHealthGrade.Healthy
    TotalFailures = 0
    TotalBlindSpots = 0
    TotalRegressions = 0
  }

/// Pure composition: merges CoverageIntelReports + ImpactForecastReports
/// into a ranked action queue with session health grading.
module ActionPrioritizer =

  /// Priority weights (lower number = higher priority)
  let [<Literal>] FailurePriority = 10
  let [<Literal>] BlindSpotPriority = 30
  let [<Literal>] PerformancePriority = 50
  let [<Literal>] StaleReevalPriority = 70
  let [<Literal>] RunTestsPriority = 90

  /// Generate actions from coverage intelligence reports.
  let actionsFromCoverage (reports: CoverageIntelReport list) : PrioritizedAction list =
    reports
    |> List.collect (fun report ->
      let failureAction =
        match report.Verdict with
        | DiagnosticBlindSpot | PartialBlindSpot ->
          [ { Kind = ActionKind.InvestigateFailure report.TestId
              Priority = FailurePriority
              Reason = sprintf "Test '%s' failing with %s coverage" report.TestName
                         (match report.Verdict with
                          | DiagnosticBlindSpot -> "poor"
                          | PartialBlindSpot -> "partial"
                          | WellCovered -> "good") } ]
        | WellCovered ->
          [ { Kind = ActionKind.InvestigateFailure report.TestId
              Priority = FailurePriority + 5
              Reason = sprintf "Test '%s' failing despite good coverage — logic bug likely" report.TestName } ]
      let blindSpotActions =
        report.BlindSpots
        |> List.truncate 3
        |> List.map (fun gap ->
          { Kind = ActionKind.WriteTest (gap.FilePath, gap.Line)
            Priority = BlindSpotPriority
            Reason = sprintf "Uncovered branch at %s:%d (branch %d)" gap.FilePath gap.Line gap.BranchId })
      failureAction @ blindSpotActions)

  /// Generate actions from impact forecast reports.
  let actionsFromImpact (reports: ImpactForecastReport list) : PrioritizedAction list =
    reports
    |> List.choose (fun report ->
      match report.Recommendation with
      | ImpactRecommendation.Refactor ->
        Some { Kind = ActionKind.InvestigatePerformance report.CellId
               Priority = PerformancePriority
               Reason = sprintf "Cell %A: P95=%.0fms, %d downstream — refactor recommended"
                          report.CellId report.P95Ms report.DownstreamCellCount }
      | ImpactRecommendation.Investigate ->
        Some { Kind = ActionKind.InvestigatePerformance report.CellId
               Priority = PerformancePriority + 10
               Reason = sprintf "Cell %A: P95=%.0fms — worth investigating"
                          report.CellId report.P95Ms }
      | ImpactRecommendation.Acceptable -> None)

  /// Generate actions from stale cell IDs.
  let actionsFromStaleCells (staleCellIds: CellId list) : PrioritizedAction list =
    staleCellIds
    |> List.map (fun cellId ->
      { Kind = ActionKind.ReEvaluateCell cellId
        Priority = StaleReevalPriority
        Reason = sprintf "Cell %A is stale — re-evaluate to propagate changes" cellId })

  /// Classify session health based on issue counts.
  let classifyHealth (failureCount: int) (blindSpotCount: int) (regressionCount: int) : SessionHealthGrade =
    let critical = failureCount + regressionCount
    let total = critical + blindSpotCount
    match critical > 0 with
    | true -> SessionHealthGrade.Critical critical
    | false ->
      match total > 0 with
      | true -> SessionHealthGrade.NeedsAttention total
      | false -> SessionHealthGrade.Healthy

  /// Compose all inputs into a ranked action queue.
  let compose
    (coverageReports: CoverageIntelReport list)
    (impactReports: ImpactForecastReport list)
    (staleCellIds: CellId list)
    : ActionQueueReport =
    let coverageActions = actionsFromCoverage coverageReports
    let impactActions = actionsFromImpact impactReports
    let staleActions = actionsFromStaleCells staleCellIds

    let allActions =
      coverageActions @ impactActions @ staleActions
      |> List.sortBy (fun a -> a.Priority)

    let totalFailures = coverageReports.Length
    let totalBlindSpots =
      coverageReports |> List.sumBy (fun r -> r.BlindSpots.Length)
    let totalRegressions =
      impactReports
      |> List.filter (fun r ->
        match r.Recommendation with
        | ImpactRecommendation.Refactor -> true
        | _ -> false)
      |> List.length

    {
      Actions = allActions
      HealthGrade = classifyHealth totalFailures totalBlindSpots totalRegressions
      TotalFailures = totalFailures
      TotalBlindSpots = totalBlindSpots
      TotalRegressions = totalRegressions
    }

  /// Summarize the action queue as human-readable text.
  let summarize (report: ActionQueueReport) : string =
    let icon =
      match report.HealthGrade with
      | SessionHealthGrade.Healthy -> "✅"
      | SessionHealthGrade.NeedsAttention _ -> "⚠️"
      | SessionHealthGrade.Critical _ -> "🔴"
    let header =
      match report.HealthGrade with
      | SessionHealthGrade.Healthy ->
        sprintf "%s Session healthy — no actions needed" icon
      | SessionHealthGrade.NeedsAttention n ->
        sprintf "%s Session needs attention — %d issue%s" icon n
          (match n with 1 -> "" | _ -> "s")
      | SessionHealthGrade.Critical n ->
        sprintf "%s Session critical — %d issue%s require immediate action" icon n
          (match n with 1 -> "" | _ -> "s")
    let lines = ResizeArray<string>()
    lines.Add header
    match report.Actions with
    | [] ->
      lines.Add "  Nothing to do — keep coding! 🎉"
    | actions ->
      lines.Add(sprintf "  📋 %d action%s:" actions.Length
        (match actions.Length with 1 -> "" | _ -> "s"))
      actions
      |> List.truncate 5
      |> List.iteri (fun i action ->
        lines.Add(sprintf "  %d. %s" (i + 1) action.Reason))
      match actions.Length > 5 with
      | true ->
        lines.Add(sprintf "  ... and %d more" (actions.Length - 5))
      | false -> ()
    lines |> String.concat "\n"
