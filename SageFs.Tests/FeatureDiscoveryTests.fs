module SageFs.Tests.FeatureDiscoveryTests

open Expecto
open Expecto.Flip
open SageFs.Features.FeatureDiscovery

// ── Test helpers ──────────────────────────────────────────────

let private freshCtx = FeatureDiscovery.emptyContext

let private withFailingTests n =
  { freshCtx with HasFailingTests = true; FailingTestCount = n }

let private withStaleCells n =
  { freshCtx with HasStaleCells = true; StaleCellCount = n }

let private withTests n =
  { freshCtx with HasTests = true; TotalTests = n }

let private withEvals n =
  { freshCtx with TotalEvals = n }

let private withTopic t =
  { freshCtx with RequestedTopic = Some t }

let private findTool name (r: DiscoveryReport) =
  r.Suggestions |> List.tryFind (fun s -> s.ToolName = name)

let private isEssential (s: FeatureSuggestion) =
  match s.Relevance with FeatureRelevance.Essential -> true | _ -> false

// ── Fresh session defaults ────────────────────────────────────

[<Tests>]
let freshSessionTests =
  testList "FeatureDiscovery.discover fresh session" [

    testCase "returns non-empty suggestions for empty context" <| fun _ ->
      let r = FeatureDiscovery.discover freshCtx
      r.Suggestions |> Expect.isNonEmpty "has suggestions"

    testCase "TotalKnownFeatures > 10" <| fun _ ->
      let r = FeatureDiscovery.discover freshCtx
      (r.TotalKnownFeatures > 10) |> Expect.isTrue "many features"

    testCase "context summary mentions no evals for fresh session" <| fun _ ->
      let r = FeatureDiscovery.discover freshCtx
      r.ContextSummary |> Expect.stringContains "fresh or nothing" "Fresh"

    testCase "suggest_next_cell boosted to Essential for 0 evals" <| fun _ ->
      let r = FeatureDiscovery.discover freshCtx
      match findTool "suggest_next_cell" r with
      | Some s -> s |> isEssential |> Expect.isTrue "essential for fresh"
      | None   -> failtest "suggest_next_cell should be present"

    testCase "diagnose always appears in results" <| fun _ ->
      let r = FeatureDiscovery.discover freshCtx
      findTool "diagnose" r |> Expect.isSome "diagnose present"
  ]

// ── Failing tests context ─────────────────────────────────────

[<Tests>]
let failingTestsContextTests =
  testList "FeatureDiscovery.discover failing tests" [

    testCase "explain_test_failure boosted to Essential" <| fun _ ->
      let ctx = withFailingTests 3
      let r = FeatureDiscovery.discover ctx
      match findTool "explain_test_failure" r with
      | Some s -> s |> isEssential |> Expect.isTrue "essential when failing"
      | None   -> failtest "explain_test_failure should be present"

    testCase "explain_test_failure WhyNow mentions failure count" <| fun _ ->
      let ctx = withFailingTests 5
      let r = FeatureDiscovery.discover ctx
      match findTool "explain_test_failure" r with
      | Some s -> s.WhyNow |> Expect.stringContains "count in message" "5"
      | None   -> failtest "missing tool"

    testCase "suggest_next_action boosted to Essential with failures" <| fun _ ->
      let ctx = withFailingTests 2
      let r = FeatureDiscovery.discover ctx
      match findTool "suggest_next_action" r with
      | Some s -> s |> isEssential |> Expect.isTrue "essential"
      | None   -> failtest "suggest_next_action should be present"

    testCase "coverage_intel boosted to High with failures" <| fun _ ->
      let ctx = withFailingTests 1
      let r = FeatureDiscovery.discover ctx
      match findTool "coverage_intel" r with
      | Some s ->
        let isHigh = match s.Relevance with FeatureRelevance.High -> true | _ -> false
        isHigh |> Expect.isTrue "high with failures"
      | None -> failtest "coverage_intel should be present"

    testCase "context summary includes failing count" <| fun _ ->
      let ctx = withFailingTests 4
      let r = FeatureDiscovery.discover ctx
      r.ContextSummary |> Expect.stringContains "failure count" "4"
  ]

// ── Stale cells context ───────────────────────────────────────

[<Tests>]
let staleCellsContextTests =
  testList "FeatureDiscovery.discover stale cells" [

    testCase "plan_ripple boosted to Essential with stale cells" <| fun _ ->
      let ctx = withStaleCells 3
      let r = FeatureDiscovery.discover ctx
      match findTool "plan_ripple" r with
      | Some s -> s |> isEssential |> Expect.isTrue "essential with stale"
      | None   -> failtest "plan_ripple should be present"

    testCase "get_cell_dependencies boosted to Essential with stale cells" <| fun _ ->
      let ctx = withStaleCells 2
      let r = FeatureDiscovery.discover ctx
      match findTool "get_cell_dependencies" r with
      | Some s -> s |> isEssential |> Expect.isTrue "essential with stale"
      | None   -> failtest "get_cell_dependencies should be present"

    testCase "plan_ripple WhyNow mentions stale count" <| fun _ ->
      let ctx = withStaleCells 7
      let r = FeatureDiscovery.discover ctx
      match findTool "plan_ripple" r with
      | Some s -> s.WhyNow |> Expect.stringContains "count in message" "7"
      | None   -> failtest "missing tool"

    testCase "context summary includes stale count" <| fun _ ->
      let ctx = withStaleCells 2
      let r = FeatureDiscovery.discover ctx
      r.ContextSummary |> Expect.stringContains "stale count" "2"
  ]

// ── Tests discovered context ──────────────────────────────────

[<Tests>]
let testsContextTests =
  testList "FeatureDiscovery.discover with tests" [

    testCase "run_tests boosted to Essential when tests discovered" <| fun _ ->
      let ctx = withTests 10
      let r = FeatureDiscovery.discover ctx
      match findTool "run_tests" r with
      | Some s -> s |> isEssential |> Expect.isTrue "essential with tests"
      | None   -> failtest "run_tests should be present"

    testCase "run_tests WhyNow mentions test count" <| fun _ ->
      let ctx = withTests 42
      let r = FeatureDiscovery.discover ctx
      match findTool "run_tests" r with
      | Some s -> s.WhyNow |> Expect.stringContains "count in message" "42"
      | None   -> failtest "missing tool"

    testCase "context summary includes test count" <| fun _ ->
      let ctx = withTests 8
      let r = FeatureDiscovery.discover ctx
      r.ContextSummary |> Expect.stringContains "test count" "8"
  ]

// ── Topic filtering ───────────────────────────────────────────

[<Tests>]
let topicFilterTests =
  testList "FeatureDiscovery.discover topic filter" [

    testCase "filtering by 'coverage' returns coverage-related tools" <| fun _ ->
      let ctx = withTopic "coverage"
      let r = FeatureDiscovery.discover ctx
      r.Suggestions |> Expect.isNonEmpty "has coverage tools"
      r.Suggestions
      |> List.forall (fun s ->
        s.ToolName.Contains("coverage", System.StringComparison.OrdinalIgnoreCase)
        || s.ShortDescription.Contains("coverage", System.StringComparison.OrdinalIgnoreCase)
        || s.WhyNow.Contains("coverage", System.StringComparison.OrdinalIgnoreCase))
      |> Expect.isTrue "all results related to coverage"

    testCase "filtering by 'test' returns test-related tools" <| fun _ ->
      let ctx = withTopic "test"
      let r = FeatureDiscovery.discover ctx
      r.Suggestions |> Expect.isNonEmpty "has test tools"

    testCase "filtering by unknown topic returns empty" <| fun _ ->
      let ctx = withTopic "zzz_nonexistent_xyz"
      let r = FeatureDiscovery.discover ctx
      r.Suggestions |> Expect.isEmpty "no matching tools"

    testCase "no topic returns full catalogue" <| fun _ ->
      let r = FeatureDiscovery.discover freshCtx
      (r.Suggestions.Length >= 15) |> Expect.isTrue "full catalogue"
  ]

// ── Sort order ────────────────────────────────────────────────

[<Tests>]
let sortOrderTests =
  testList "FeatureDiscovery.discover sort order" [

    testCase "Essential features appear before High" <| fun _ ->
      let ctx = withFailingTests 3
      let r = FeatureDiscovery.discover ctx
      let essentialIdx =
        r.Suggestions |> List.findIndex (fun s -> s.Relevance = FeatureRelevance.Essential)
      let highIdx =
        r.Suggestions |> List.findIndex (fun s -> s.Relevance = FeatureRelevance.High)
      (essentialIdx < highIdx) |> Expect.isTrue "essential before high"

    testCase "suggestions are in stable alphabetical order within same relevance tier" <| fun _ ->
      let r = FeatureDiscovery.discover freshCtx
      let highGroup =
        r.Suggestions
        |> List.filter (fun s -> s.Relevance = FeatureRelevance.High)
        |> List.map (fun s -> s.ToolName)
      let sorted = highGroup |> List.sort
      highGroup |> Expect.equal "alpha within tier" sorted
  ]

// ── Context summary ───────────────────────────────────────────

[<Tests>]
let contextSummaryTests =
  testList "FeatureDiscovery.discover context summary" [

    testCase "all metrics appear in full context summary" <| fun _ ->
      let ctx = {
        HasFailingTests = true; FailingTestCount = 2
        HasStaleCells = true; StaleCellCount = 3
        TotalEvals = 10; HasTests = true; TotalTests = 15
        RequestedTopic = None
      }
      let r = FeatureDiscovery.discover ctx
      r.ContextSummary |> Expect.stringContains "failing count" "2"
      r.ContextSummary |> Expect.stringContains "stale count" "3"
      r.ContextSummary |> Expect.stringContains "eval count" "10"
      r.ContextSummary |> Expect.stringContains "test count" "15"
  ]

[<Tests>]
let allTests =
  testList "FeatureDiscovery" [
    freshSessionTests
    failingTestsContextTests
    staleCellsContextTests
    testsContextTests
    topicFilterTests
    sortOrderTests
    contextSummaryTests
  ]
