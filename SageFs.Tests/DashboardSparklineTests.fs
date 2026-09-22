module SageFs.Tests.DashboardSparklineTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server

let private makeTimeline (durationsMsAsc: int list) : Features.EvalTimeline.TimelineState =
  durationsMsAsc
  |> List.fold
      (fun st dur ->
        let e: Features.EvalTimeline.TimelineEntry =
          { CellId = 0; StartMs = 0L; DurationMs = int64 dur; Status = Features.EvalTimeline.Succeeded }
        Features.EvalTimeline.TimelineState.record e st)
      Features.EvalTimeline.TimelineState.empty

let private statsOf (durations: int list) =
  Features.EvalTimeline.timelineStats 20 (makeTimeline durations)

[<Tests>]
let evalStatsViewTests =
  testList "EvalStatsView sparkline fields" [
    testCase "has Sparkline field (non-empty with data)" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 5; AvgMs = 100.0; MinMs = 50.0; MaxMs = 200.0
        Sparkline = "▁▂▃▄█"
        P50Ms = Some 100.0; P95Ms = Some 180.0 }
      view.Sparkline |> Expect.equal "sparkline preserved" "▁▂▃▄█"
    testCase "has P50Ms field" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 3; AvgMs = 100.0; MinMs = 80.0; MaxMs = 120.0
        Sparkline = "▄▅▆"
        P50Ms = Some 100.0; P95Ms = None }
      view.P50Ms |> Expect.equal "P50 round-trips" (Some 100.0)
    testCase "has P95Ms field" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 3; AvgMs = 100.0; MinMs = 80.0; MaxMs = 150.0
        Sparkline = "▅▆▇"
        P50Ms = Some 100.0; P95Ms = Some 150.0 }
      view.P95Ms |> Expect.equal "P95 round-trips" (Some 150.0)
    testCase "sparkline is empty string when no evals" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0
        Sparkline = ""; P50Ms = None; P95Ms = None }
      view.Sparkline |> Expect.equal "empty sparkline when no evals" ""
  ]

[<Tests>]
let renderEvalStatsTests =
  testList "renderEvalStats sparkline rendering" [
    testCase "renders sparkline in output when non-empty" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 5; AvgMs = 100.0; MinMs = 50.0; MaxMs = 200.0
        Sparkline = "▁▂▃▄█"
        P50Ms = Some 100.0; P95Ms = Some 180.0 }
      let html = DashboardFragments.renderEvalStats view |> renderNode
      html |> Expect.stringContains "sparkline in rendered HTML" "▁▂▃▄█"
    testCase "renders P50 in output when present" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 5; AvgMs = 100.0; MinMs = 50.0; MaxMs = 200.0
        Sparkline = "▁▂▃▄█"
        P50Ms = Some 95.0; P95Ms = Some 190.0 }
      let html = DashboardFragments.renderEvalStats view |> renderNode
      html |> Expect.stringContains "P50 in rendered HTML" "P50"
    testCase "renders count in output" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 42; AvgMs = 50.0; MinMs = 10.0; MaxMs = 100.0
        Sparkline = "▄"
        P50Ms = Some 50.0; P95Ms = None }
      let html = DashboardFragments.renderEvalStats view |> renderNode
      html |> Expect.stringContains "count in rendered HTML" "42"
    testCase "renders gracefully when sparkline is empty" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0
        Sparkline = ""; P50Ms = None; P95Ms = None }
      let html = DashboardFragments.renderEvalStats view |> renderNode
      html |> Expect.isNotEmpty "renders something even with no data"
  ]

[<Tests>]
let evalStatsViewFromTimelineTests =
  testList "EvalStatsView.fromStats" [
    testCase "populates sparkline from stats" <| fun () ->
      let stats = statsOf [50; 100; 150; 200; 250]
      let evalStats = SageFs.Affordances.EvalStats.empty
      let view = DashboardTypes.EvalStatsView.fromStats evalStats stats
      view.Sparkline |> Expect.isNotEmpty "sparkline populated from stats"
    testCase "populates P50 from stats" <| fun () ->
      let stats = statsOf [100; 100; 100; 100; 100]
      let evalStats = SageFs.Affordances.EvalStats.empty
      let view = DashboardTypes.EvalStatsView.fromStats evalStats stats
      view.P50Ms |> Expect.equal "P50 = 100ms" (Some 100.0)
    testCase "sparkline is empty when no entries" <| fun () ->
      let stats = statsOf []
      let evalStats = SageFs.Affordances.EvalStats.empty
      let view = DashboardTypes.EvalStatsView.fromStats evalStats stats
      view.Sparkline |> Expect.equal "empty sparkline for empty timeline" ""
    testCase "preserves count and avg from EvalStats" <| fun () ->
      let stats = statsOf [100; 200]
      let evalStats = { SageFs.Affordances.EvalStats.empty with EvalCount = 7 }
      let view = DashboardTypes.EvalStatsView.fromStats evalStats stats
      view.Count |> Expect.equal "count from EvalStats" 7
  ]
