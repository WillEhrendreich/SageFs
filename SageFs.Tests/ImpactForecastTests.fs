module SageFs.Tests.ImpactForecastTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features.CellDependencyGraph
open SageFs.Features.ImpactForecast

// ── Test helpers ──────────────────────────────────────────────

let private mkCellId (n: int) : CellId = n

// ── Recommendation classification tests ──────────────────────

[<Tests>]
let recommendationTests =
  testList "ImpactForecast.classifyRecommendation" [

    testCase "low P95, low downstream → Acceptable" <| fun _ ->
      ImpactForecast.classifyRecommendation 100.0 3
      |> Expect.equal "should be Acceptable" ImpactRecommendation.Acceptable

    testCase "P95 over 500ms → Investigate" <| fun _ ->
      ImpactForecast.classifyRecommendation 600.0 2
      |> Expect.equal "should be Investigate" ImpactRecommendation.Investigate

    testCase "downstream over 5 → Investigate" <| fun _ ->
      ImpactForecast.classifyRecommendation 100.0 8
      |> Expect.equal "should be Investigate" ImpactRecommendation.Investigate

    testCase "P95 over 2000ms → Refactor" <| fun _ ->
      ImpactForecast.classifyRecommendation 2500.0 1
      |> Expect.equal "should be Refactor" ImpactRecommendation.Refactor

    testCase "downstream over 15 → Refactor" <| fun _ ->
      ImpactForecast.classifyRecommendation 100.0 20
      |> Expect.equal "should be Refactor" ImpactRecommendation.Refactor

    testCase "exactly at boundary 500ms → Investigate" <| fun _ ->
      ImpactForecast.classifyRecommendation 500.1 0
      |> Expect.equal "should be Investigate" ImpactRecommendation.Investigate

    testCase "exactly at boundary 2000ms → Refactor" <| fun _ ->
      ImpactForecast.classifyRecommendation 2000.1 0
      |> Expect.equal "should be Refactor" ImpactRecommendation.Refactor
  ]

// ── Regression cause detection tests ─────────────────────────

[<Tests>]
let regressionCauseTests =
  testList "ImpactForecast.detectRegressionCauses" [

    testCase "no issues → empty causes" <| fun _ ->
      ImpactForecast.detectRegressionCauses 200.0 3
      |> Expect.isEmpty "should be empty"

    testCase "latency spike detected" <| fun _ ->
      let causes = ImpactForecast.detectRegressionCauses 800.0 2
      causes |> Expect.hasLength "should have 1 cause" 1
      match causes.[0] with
      | RegressionCause.LatencySpike p95 ->
        p95 |> Expect.equal "should be 800" 800.0
      | _ -> failwith "expected LatencySpike"

    testCase "dependency growth detected" <| fun _ ->
      let causes = ImpactForecast.detectRegressionCauses 100.0 10
      causes |> Expect.hasLength "should have 1 cause" 1
      match causes.[0] with
      | RegressionCause.DependencyGrowth count ->
        count |> Expect.equal "should be 10" 10
      | _ -> failwith "expected DependencyGrowth"

    testCase "both causes detected" <| fun _ ->
      let causes = ImpactForecast.detectRegressionCauses 900.0 12
      causes |> Expect.hasLength "should have 2 causes" 2
  ]

// ── Cell analysis tests ──────────────────────────────────────

[<Tests>]
let analyzeCellTests =
  testList "ImpactForecast.analyzeCell" [

    testCase "healthy cell → Acceptable with no causes" <| fun _ ->
      let report = ImpactForecast.analyzeCell
                     (mkCellId 1) 50.0 100.0 [ 50.0; 55.0; 48.0 ]
                     [ mkCellId 2; mkCellId 3 ]
      report.Recommendation |> Expect.equal "Acceptable" ImpactRecommendation.Acceptable
      report.RegressionCauses |> Expect.isEmpty "no causes"
      report.DownstreamCellCount |> Expect.equal "2 downstream" 2

    testCase "slow cell with many dependents → Refactor" <| fun _ ->
      let downstream = List.init 20 (fun i -> mkCellId (i + 10))
      let report = ImpactForecast.analyzeCell
                     (mkCellId 5) 1500.0 3000.0 [ 2000.0; 2500.0; 3000.0 ]
                     downstream
      report.Recommendation |> Expect.equal "Refactor" ImpactRecommendation.Refactor
      report.P95Ms |> Expect.equal "P95" 3000.0
      report.DownstreamCellCount |> Expect.equal "20 downstream" 20

    testCase "preserves duration trend" <| fun _ ->
      let trend = [ 100.0; 200.0; 300.0; 400.0 ]
      let report = ImpactForecast.analyzeCell
                     (mkCellId 1) 250.0 400.0 trend []
      report.DurationTrendMs |> Expect.equal "trend preserved" trend
  ]

// ── Trend slope tests ────────────────────────────────────────

[<Tests>]
let trendSlopeTests =
  testList "ImpactForecast.trendSlope" [

    testCase "empty → 0.0" <| fun _ ->
      ImpactForecast.trendSlope []
      |> Expect.equal "should be 0" 0.0

    testCase "single → 0.0" <| fun _ ->
      ImpactForecast.trendSlope [ 100.0 ]
      |> Expect.equal "should be 0" 0.0

    testCase "increasing → positive slope" <| fun _ ->
      let slope = ImpactForecast.trendSlope [ 100.0; 110.0; 200.0; 210.0 ]
      (slope > 0.0) |> Expect.isTrue "should be positive"

    testCase "decreasing → negative slope" <| fun _ ->
      let slope = ImpactForecast.trendSlope [ 200.0; 210.0; 100.0; 110.0 ]
      (slope < 0.0) |> Expect.isTrue "should be negative"

    testCase "flat → zero slope" <| fun _ ->
      ImpactForecast.trendSlope [ 100.0; 100.0; 100.0; 100.0 ]
      |> Expect.equal "should be 0" 0.0
  ]

// ── Summarize tests ──────────────────────────────────────────

[<Tests>]
let summarizeTests =
  testList "ImpactForecast.summarize" [

    testCase "Acceptable summary has green icon" <| fun _ ->
      let report = {
        ImpactForecastReport.empty (mkCellId 1) with
          Recommendation = ImpactRecommendation.Acceptable
          P50Ms = 50.0
          P95Ms = 100.0
      }
      let text = ImpactForecast.summarize report
      text |> Expect.stringContains "has icon" "✅"
      text |> Expect.stringContains "mentions acceptable" "acceptable"

    testCase "Refactor summary says split cell" <| fun _ ->
      let report = {
        ImpactForecastReport.empty (mkCellId 1) with
          Recommendation = ImpactRecommendation.Refactor
          P95Ms = 3000.0
          RegressionCauses = [ RegressionCause.LatencySpike 3000.0 ]
      }
      let text = ImpactForecast.summarize report
      text |> Expect.stringContains "has icon" "🔴"
      text |> Expect.stringContains "says split" "splitting"

    testCase "Investigate summary says worth investigating" <| fun _ ->
      let report = {
        ImpactForecastReport.empty (mkCellId 1) with
          Recommendation = ImpactRecommendation.Investigate
          RegressionCauses = [ RegressionCause.DependencyGrowth 10 ]
      }
      let text = ImpactForecast.summarize report
      text |> Expect.stringContains "has icon" "⚠️"
      text |> Expect.stringContains "says investigating" "investigating"
  ]

// ── Property tests ───────────────────────────────────────────

[<Tests>]
let propertyTests =
  testList "ImpactForecast properties" [

    testCase "recommendation severity is monotonic with P95" <| fun _ ->
      let rec1 = ImpactForecast.classifyRecommendation 100.0 0
      let rec2 = ImpactForecast.classifyRecommendation 600.0 0
      let rec3 = ImpactForecast.classifyRecommendation 3000.0 0
      // Acceptable ≤ Investigate ≤ Refactor
      let toInt r =
        match r with
        | ImpactRecommendation.Acceptable -> 0
        | ImpactRecommendation.Investigate -> 1
        | ImpactRecommendation.Refactor -> 2
      (toInt rec1 <= toInt rec2)
      |> Expect.isTrue "low ≤ mid"
      (toInt rec2 <= toInt rec3)
      |> Expect.isTrue "mid ≤ high"

    testCase "causes count ≤ 2 (latency + dependency are the only causes)" <| fun _ ->
      let causes = ImpactForecast.detectRegressionCauses 9999.0 9999
      (causes.Length <= 2)
      |> Expect.isTrue "at most 2 causes"
  ]
