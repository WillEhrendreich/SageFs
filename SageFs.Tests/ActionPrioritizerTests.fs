module SageFs.Tests.ActionPrioritizerTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features.CellDependencyGraph
open SageFs.Features.CoverageIntel
open SageFs.Features.ImpactForecast
open SageFs.Features.ActionPrioritizer

// ── Test helpers ──────────────────────────────────────────────

let private mkTestId name = TestId.TestId name
let private mkCellId (n: int) : CellId = n

let private mkCoverageReport verdict blindSpotCount testName =
  { CoverageIntelReport.empty (mkTestId testName) testName with
      Verdict = verdict
      BlindSpots =
        List.init blindSpotCount (fun i ->
          { FilePath = "src.fs"; Line = 10 + i * 10; EndLine = 10 + i * 10
            BranchId = i; NearestCoveredLine = None }) }

let private mkImpactReport recommendation cellId p95 downstream =
  { ImpactForecastReport.empty (mkCellId cellId) with
      Recommendation = recommendation
      P95Ms = p95
      DownstreamCellCount = downstream }

// ── Actions from coverage tests ──────────────────────────────

[<Tests>]
let coverageActionTests =
  testList "ActionPrioritizer.actionsFromCoverage" [

    testCase "DiagnosticBlindSpot → failure + blind spot actions" <| fun _ ->
      let report = mkCoverageReport DiagnosticBlindSpot 2 "bad test"
      let actions = ActionPrioritizer.actionsFromCoverage [ report ]
      // 1 failure action + 2 blind spot actions
      actions |> Expect.hasLength "3 actions" 3

    testCase "WellCovered → failure action only (no blind spots)" <| fun _ ->
      let report = mkCoverageReport WellCovered 0 "good test"
      let actions = ActionPrioritizer.actionsFromCoverage [ report ]
      actions |> Expect.hasLength "1 action" 1
      match actions.[0].Kind with
      | ActionKind.InvestigateFailure _ -> ()
      | _ -> failwith "expected InvestigateFailure"

    testCase "blind spots truncated to 3" <| fun _ ->
      let report = mkCoverageReport PartialBlindSpot 10 "many gaps"
      let actions = ActionPrioritizer.actionsFromCoverage [ report ]
      let blindSpotActions =
        actions |> List.filter (fun a ->
          match a.Kind with
          | ActionKind.WriteTest _ -> true
          | _ -> false)
      blindSpotActions |> Expect.hasLength "max 3 blind spots" 3

    testCase "empty reports → empty actions" <| fun _ ->
      ActionPrioritizer.actionsFromCoverage []
      |> Expect.isEmpty "should be empty"
  ]

// ── Actions from impact tests ────────────────────────────────

[<Tests>]
let impactActionTests =
  testList "ActionPrioritizer.actionsFromImpact" [

    testCase "Refactor → performance action" <| fun _ ->
      let report = mkImpactReport ImpactRecommendation.Refactor 1 3000.0 20
      let actions = ActionPrioritizer.actionsFromImpact [ report ]
      actions |> Expect.hasLength "1 action" 1
      match actions.[0].Kind with
      | ActionKind.InvestigatePerformance _ -> ()
      | _ -> failwith "expected InvestigatePerformance"

    testCase "Acceptable → no actions" <| fun _ ->
      let report = mkImpactReport ImpactRecommendation.Acceptable 1 100.0 2
      ActionPrioritizer.actionsFromImpact [ report ]
      |> Expect.isEmpty "should be empty"

    testCase "Investigate → action with lower priority than Refactor" <| fun _ ->
      let refactorReport = mkImpactReport ImpactRecommendation.Refactor 1 3000.0 20
      let investigateReport = mkImpactReport ImpactRecommendation.Investigate 2 700.0 8
      let actions = ActionPrioritizer.actionsFromImpact [ refactorReport; investigateReport ]
      actions |> Expect.hasLength "2 actions" 2
      (actions.[0].Priority < actions.[1].Priority)
      |> Expect.isTrue "refactor has higher priority"
  ]

// ── Health classification tests ──────────────────────────────

[<Tests>]
let healthTests =
  testList "ActionPrioritizer.classifyHealth" [

    testCase "no issues → Healthy" <| fun _ ->
      ActionPrioritizer.classifyHealth 0 0 0
      |> Expect.equal "Healthy" SessionHealthGrade.Healthy

    testCase "failures → Critical" <| fun _ ->
      match ActionPrioritizer.classifyHealth 3 0 0 with
      | SessionHealthGrade.Critical n ->
        n |> Expect.equal "3 critical" 3
      | other -> failwith (sprintf "expected Critical, got %A" other)

    testCase "regressions → Critical" <| fun _ ->
      match ActionPrioritizer.classifyHealth 0 0 2 with
      | SessionHealthGrade.Critical n ->
        n |> Expect.equal "2 critical" 2
      | other -> failwith (sprintf "expected Critical, got %A" other)

    testCase "only blind spots → NeedsAttention" <| fun _ ->
      match ActionPrioritizer.classifyHealth 0 5 0 with
      | SessionHealthGrade.NeedsAttention n ->
        n |> Expect.equal "5 issues" 5
      | other -> failwith (sprintf "expected NeedsAttention, got %A" other)

    testCase "failures + blind spots → Critical (failures dominate)" <| fun _ ->
      match ActionPrioritizer.classifyHealth 1 3 0 with
      | SessionHealthGrade.Critical n ->
        n |> Expect.equal "1 critical" 1
      | other -> failwith (sprintf "expected Critical, got %A" other)
  ]

// ── Compose tests ────────────────────────────────────────────

[<Tests>]
let composeTests =
  testList "ActionPrioritizer.compose" [

    testCase "mixed inputs → sorted by priority" <| fun _ ->
      let coverageReports = [ mkCoverageReport DiagnosticBlindSpot 1 "t1" ]
      let impactReports = [ mkImpactReport ImpactRecommendation.Refactor 1 3000.0 20 ]
      let staleCells = [ mkCellId 5 ]

      let report = ActionPrioritizer.compose coverageReports impactReports staleCells

      // Should have: 1 failure + 1 blind spot + 1 perf + 1 stale = 4
      report.Actions |> Expect.hasLength "4 actions" 4
      // First action should be highest priority (failure investigation)
      (report.Actions.[0].Priority <= report.Actions.[1].Priority)
      |> Expect.isTrue "sorted by priority"

    testCase "empty inputs → empty queue, Healthy" <| fun _ ->
      let report = ActionPrioritizer.compose [] [] []
      report.Actions |> Expect.isEmpty "no actions"
      report.HealthGrade |> Expect.equal "Healthy" SessionHealthGrade.Healthy

    testCase "counts are correct" <| fun _ ->
      let coverageReports = [
        mkCoverageReport DiagnosticBlindSpot 3 "t1"
        mkCoverageReport WellCovered 0 "t2"
      ]
      let impactReports = [ mkImpactReport ImpactRecommendation.Refactor 1 5000.0 30 ]

      let report = ActionPrioritizer.compose coverageReports impactReports []

      report.TotalFailures |> Expect.equal "2 failures" 2
      report.TotalBlindSpots |> Expect.equal "3 blind spots" 3
      report.TotalRegressions |> Expect.equal "1 regression" 1
  ]

// ── Summarize tests ──────────────────────────────────────────

[<Tests>]
let summarizeTests =
  testList "ActionPrioritizer.summarize" [

    testCase "Healthy → nothing to do" <| fun _ ->
      let text = ActionPrioritizer.summarize ActionQueueReport.empty
      text |> Expect.stringContains "has icon" "✅"
      text |> Expect.stringContains "says keep coding" "keep coding"

    testCase "Critical → immediate action" <| fun _ ->
      let report = {
        ActionQueueReport.empty with
          HealthGrade = SessionHealthGrade.Critical 2
          Actions = [
            { Kind = ActionKind.RunTests; Priority = 10; Reason = "Fix failures" }
          ]
      }
      let text = ActionPrioritizer.summarize report
      text |> Expect.stringContains "has icon" "🔴"
      text |> Expect.stringContains "says immediate" "immediate"

    testCase "more than 5 actions → shows truncation" <| fun _ ->
      let actions = List.init 8 (fun i ->
        { Kind = ActionKind.RunTests; Priority = i * 10
          Reason = sprintf "Action %d" (i + 1) })
      let report = {
        ActionQueueReport.empty with
          HealthGrade = SessionHealthGrade.NeedsAttention 8
          Actions = actions
      }
      let text = ActionPrioritizer.summarize report
      text |> Expect.stringContains "truncated" "and 3 more"
  ]

// ── Property tests ───────────────────────────────────────────

[<Tests>]
let propertyTests =
  testList "ActionPrioritizer properties" [

    testCase "compose output is sorted by priority (ascending)" <| fun _ ->
      let coverageReports = [
        mkCoverageReport DiagnosticBlindSpot 2 "t1"
        mkCoverageReport WellCovered 0 "t2"
      ]
      let impactReports = [ mkImpactReport ImpactRecommendation.Investigate 1 700.0 8 ]
      let staleCells = [ mkCellId 1; mkCellId 2 ]

      let report = ActionPrioritizer.compose coverageReports impactReports staleCells
      let priorities = report.Actions |> List.map (fun a -> a.Priority)
      let sorted = priorities |> List.sort
      priorities |> Expect.equal "should be sorted" sorted

    testCase "health grade severity is monotonic" <| fun _ ->
      let h1 = ActionPrioritizer.classifyHealth 0 0 0
      let h2 = ActionPrioritizer.classifyHealth 0 3 0
      let h3 = ActionPrioritizer.classifyHealth 2 0 0
      let toInt h =
        match h with
        | SessionHealthGrade.Healthy -> 0
        | SessionHealthGrade.NeedsAttention _ -> 1
        | SessionHealthGrade.Critical _ -> 2
      (toInt h1 <= toInt h2)
      |> Expect.isTrue "healthy ≤ attention"
      (toInt h2 <= toInt h3)
      |> Expect.isTrue "attention ≤ critical"
  ]
