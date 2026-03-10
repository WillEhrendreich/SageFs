module SageFs.Tests.DashboardSparklineTests

open Expecto
open Falco.Markup
open SageFs
open SageFs.Server

let private makeTimeline (durationsMsAsc: int list) : Features.EvalTimeline.TimelineState =
  durationsMsAsc
  |> List.fold
      (fun st dur ->
        let e: Features.EvalTimeline.TimelineEntry =
          { CellId = 0; StartMs = 0L; DurationMs = int64 dur; Status = Features.EvalTimeline.Success }
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
      Expect.equal view.Sparkline "▁▂▃▄█" "sparkline preserved"
    testCase "has P50Ms field" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 3; AvgMs = 100.0; MinMs = 80.0; MaxMs = 120.0
        Sparkline = "▄▅▆"
        P50Ms = Some 100.0; P95Ms = None }
      Expect.equal view.P50Ms (Some 100.0) "P50 round-trips"
    testCase "has P95Ms field" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 3; AvgMs = 100.0; MinMs = 80.0; MaxMs = 150.0
        Sparkline = "▅▆▇"
        P50Ms = Some 100.0; P95Ms = Some 150.0 }
      Expect.equal view.P95Ms (Some 150.0) "P95 round-trips"
    testCase "sparkline is empty string when no evals" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0
        Sparkline = ""; P50Ms = None; P95Ms = None }
      Expect.equal view.Sparkline "" "empty sparkline when no evals"
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
      Expect.stringContains html "▁▂▃▄█" "sparkline in rendered HTML"
    testCase "renders P50 in output when present" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 5; AvgMs = 100.0; MinMs = 50.0; MaxMs = 200.0
        Sparkline = "▁▂▃▄█"
        P50Ms = Some 95.0; P95Ms = Some 190.0 }
      let html = DashboardFragments.renderEvalStats view |> renderNode
      Expect.stringContains html "P50" "P50 in rendered HTML"
    testCase "renders count in output" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 42; AvgMs = 50.0; MinMs = 10.0; MaxMs = 100.0
        Sparkline = "▄"
        P50Ms = Some 50.0; P95Ms = None }
      let html = DashboardFragments.renderEvalStats view |> renderNode
      Expect.stringContains html "42" "count in rendered HTML"
    testCase "renders gracefully when sparkline is empty" <| fun () ->
      let view : DashboardTypes.EvalStatsView = {
        Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0
        Sparkline = ""; P50Ms = None; P95Ms = None }
      let html = DashboardFragments.renderEvalStats view |> renderNode
      Expect.isNotEmpty html "renders something even with no data"
  ]

[<Tests>]
let evalStatsViewFromTimelineTests =
  testList "EvalStatsView.fromStats" [
    testCase "populates sparkline from stats" <| fun () ->
      let stats = statsOf [50; 100; 150; 200; 250]
      let evalStats = SageFs.Affordances.EvalStats.empty
      let view = DashboardTypes.EvalStatsView.fromStats evalStats stats
      Expect.isNotEmpty view.Sparkline "sparkline populated from stats"
    testCase "populates P50 from stats" <| fun () ->
      let stats = statsOf [100; 100; 100; 100; 100]
      let evalStats = SageFs.Affordances.EvalStats.empty
      let view = DashboardTypes.EvalStatsView.fromStats evalStats stats
      Expect.equal view.P50Ms (Some 100.0) "P50 = 100ms"
    testCase "sparkline is empty when no entries" <| fun () ->
      let stats = statsOf []
      let evalStats = SageFs.Affordances.EvalStats.empty
      let view = DashboardTypes.EvalStatsView.fromStats evalStats stats
      Expect.equal view.Sparkline "" "empty sparkline for empty timeline"
    testCase "preserves count and avg from EvalStats" <| fun () ->
      let stats = statsOf [100; 200]
      let evalStats = { SageFs.Affordances.EvalStats.empty with EvalCount = 7 }
      let view = DashboardTypes.EvalStatsView.fromStats evalStats stats
      Expect.equal view.Count 7 "count from EvalStats"
  ]
