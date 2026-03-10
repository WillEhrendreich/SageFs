module SageFs.Features.FeatureDiscovery

/// How relevant a suggested feature is given the current session context.
[<RequireQualifiedAccess>]
type FeatureRelevance =
  | Essential   // Need this right now
  | High        // Very useful given current state
  | Medium      // Worth knowing about
  | Contextual  // Relevant when you're ready

/// A single feature suggestion with actionable context.
type FeatureSuggestion = {
  /// The MCP tool name (e.g., "run_tests").
  ToolName: string
  /// One-sentence description of what the tool does.
  ShortDescription: string
  /// Concrete, copy-paste-ready example usage.
  ExampleUsage: string
  /// Why this feature is specifically relevant right now.
  WhyNow: string
  /// Relevance ranking for the current context.
  Relevance: FeatureRelevance
}

/// Snapshot of current session state used to personalise feature ranking.
type DiscoveryContext = {
  /// True if there are currently failing tests.
  HasFailingTests: bool
  /// Number of failing tests.
  FailingTestCount: int
  /// True if there are cells that need re-evaluation.
  HasStaleCells: bool
  /// Number of stale cells.
  StaleCellCount: int
  /// Total cells evaluated so far this session.
  TotalEvals: int
  /// True if any tests have been discovered.
  HasTests: bool
  /// Total tests discovered.
  TotalTests: int
  /// Optional topic the user asked about (used to filter suggestions).
  RequestedTopic: string option
}

/// Ranked list of feature suggestions with context summary.
type DiscoveryReport = {
  /// Suggestions sorted by relevance (most relevant first).
  Suggestions: FeatureSuggestion list
  /// Human-readable summary of the context used for ranking.
  ContextSummary: string
  /// Total features in the catalogue.
  TotalKnownFeatures: int
}

module FeatureDiscovery =

  // Full feature catalogue — every MCP-exposed capability with baseline relevance.
  let private catalogue : FeatureSuggestion list = [
    { ToolName = "diagnose"
      ShortDescription = "Full diagnostic report: failures, cell graph, performance, and repair suggestions"
      ExampleUsage = "diagnose()"
      WhyNow = "When something feels wrong, start here for the complete picture"
      Relevance = FeatureRelevance.Essential }
    { ToolName = "run_tests"
      ShortDescription = "Run tests and report pass/fail counts with failure messages"
      ExampleUsage = """run_tests(pattern="", category="unit", timeout_seconds=30)"""
      WhyNow = "Re-validate your work after code changes"
      Relevance = FeatureRelevance.High }
    { ToolName = "explain_test_failure"
      ShortDescription = "Explain why a test transitioned Passed→Failed with causal symbol changes"
      ExampleUsage = """explain_test_failure(test_name="my failing test")"""
      WhyNow = "Get root-cause analysis when a test breaks"
      Relevance = FeatureRelevance.High }
    { ToolName = "suggest_next_action"
      ShortDescription = "Ranked queue of developer actions based on failures + performance + stale cells"
      ExampleUsage = "suggest_next_action()"
      WhyNow = "Not sure where to start? Let the intelligence layer prioritise"
      Relevance = FeatureRelevance.High }
    { ToolName = "coverage_intel"
      ShortDescription = "Find coverage blind spots and test-failure correlations"
      ExampleUsage = "coverage_intel()"
      WhyNow = "Identify which code paths are unprotected by tests"
      Relevance = FeatureRelevance.High }
    { ToolName = "list_tests"
      ShortDescription = "List all discovered tests, optionally filtered by name or file"
      ExampleUsage = """list_tests(pattern="auth", file="")"""
      WhyNow = "Discover what tests exist before running them"
      Relevance = FeatureRelevance.High }
    { ToolName = "get_cell_dependencies"
      ShortDescription = "Visualise the cell dependency graph with staleness annotations"
      ExampleUsage = "get_cell_dependencies()"
      WhyNow = "See exactly how bindings flow between cells and which are stale"
      Relevance = FeatureRelevance.High }
    { ToolName = "plan_ripple"
      ShortDescription = "Preview which downstream cells will need re-evaluation after a change"
      ExampleUsage = """plan_ripple(changed_cells="0,2")"""
      WhyNow = "Understand blast radius before editing a binding"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "preview_what_if"
      ShortDescription = "Simulate changing a binding value without executing it"
      ExampleUsage = """preview_what_if(binding_name="threshold", new_code="0.95")"""
      WhyNow = "Explore hypothetical changes safely before committing"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "impact_forecast"
      ShortDescription = "Detect performance regressions and downstream impact of cell changes"
      ExampleUsage = "impact_forecast()"
      WhyNow = "Check whether recent changes caused a performance regression"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "get_file_coverage"
      ShortDescription = "Per-line coverage data for a specific source file"
      ExampleUsage = """get_file_coverage(file="MyModule.fs")"""
      WhyNow = "See exactly which lines are covered, partially covered, or untested"
      Relevance = FeatureRelevance.High }
    { ToolName = "query_test_coverage"
      ShortDescription = "Find every test that transitively covers a given symbol"
      ExampleUsage = """query_test_coverage(symbol="MyModule.myFunction")"""
      WhyNow = "Before refactoring a function, find its test guard"
      Relevance = FeatureRelevance.High }
    { ToolName = "suggest_next_cell"
      ShortDescription = "Type-directed suggestions for what to evaluate next"
      ExampleUsage = "suggest_next_cell()"
      WhyNow = "Blank page? Let the type system suggest the next useful operation"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "get_eval_timeline"
      ShortDescription = "Sparkline + P50/P95/P99 stats for recent eval durations"
      ExampleUsage = "get_eval_timeline(sparkline_width=20)"
      WhyNow = "Track eval performance trends to catch regressions early"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "get_completions"
      ShortDescription = "Code completions at a cursor position"
      ExampleUsage = """get_completions(code="System.IO.Fi", cursor_position=12)"""
      WhyNow = "Discover available APIs without leaving your workflow"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "explore_namespace"
      ShortDescription = "Browse all types and sub-namespaces available in a namespace"
      ExampleUsage = """explore_namespace(namespaceName="System.Collections.Generic")"""
      WhyNow = "Explore APIs before using them"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "explore_type"
      ShortDescription = "See all members, constructors, and properties of a specific type"
      ExampleUsage = """explore_type(typeName="System.String")"""
      WhyNow = "Drill into a type you've already identified"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "check_fsharp_code"
      ShortDescription = "Check F# code for errors without executing it"
      ExampleUsage = """check_fsharp_code(code="let x = 42")"""
      WhyNow = "Validate syntax and types before submitting to FSI"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "visualize_domain_model"
      ShortDescription = "Render a discriminated union as a state machine diagram"
      ExampleUsage = """visualize_domain_model(typeName="MyApp.OrderState")"""
      WhyNow = "Understand complex DU-based domain models visually"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "get_session_filmstrip"
      ShortDescription = "Visual history of all evaluations in the current session"
      ExampleUsage = """get_session_filmstrip(filter="")"""
      WhyNow = "Review the chronological story of your session"
      Relevance = FeatureRelevance.Medium }
    { ToolName = "export_notebook"
      ShortDescription = "Export the current session as a notebook-style .fsx file"
      ExampleUsage = """export_notebook(project_name="MyExploration")"""
      WhyNow = "Save your interactive work to share or revisit later"
      Relevance = FeatureRelevance.Contextual }
    { ToolName = "get_message_journal"
      ShortDescription = "Structured audit log of eval events with severity filtering"
      ExampleUsage = """get_message_journal(min_level="error", source="")"""
      WhyNow = "Review what happened during a session for observability"
      Relevance = FeatureRelevance.Contextual }
    { ToolName = "manage_scratch_pad"
      ShortDescription = "View, export, or promote ephemeral code snippets"
      ExampleUsage = """manage_scratch_pad(action="list")"""
      WhyNow = "Organise ad-hoc code you've been experimenting with"
      Relevance = FeatureRelevance.Contextual }
  ]

  let private relevanceScore = function
    | FeatureRelevance.Essential  -> 0
    | FeatureRelevance.High       -> 1
    | FeatureRelevance.Medium     -> 2
    | FeatureRelevance.Contextual -> 3

  let private boostForContext (ctx: DiscoveryContext) (s: FeatureSuggestion) : FeatureSuggestion =
    match s.ToolName with
    | "explain_test_failure" when ctx.HasFailingTests ->
      { s with Relevance = FeatureRelevance.Essential
               WhyNow = $"⚠️ You have {ctx.FailingTestCount} failing test(s) — find out why" }
    | "suggest_next_action" when ctx.HasFailingTests ->
      { s with Relevance = FeatureRelevance.Essential
               WhyNow = $"Failing tests detected — let the prioritiser guide your next move" }
    | "plan_ripple" when ctx.HasStaleCells ->
      { s with Relevance = FeatureRelevance.Essential
               WhyNow = $"⚡ {ctx.StaleCellCount} stale cell(s) — see the re-evaluation plan" }
    | "get_cell_dependencies" when ctx.HasStaleCells ->
      { s with Relevance = FeatureRelevance.Essential
               WhyNow = $"Stale cells detected — inspect the dependency graph" }
    | "suggest_next_cell" when ctx.TotalEvals = 0 ->
      { s with Relevance = FeatureRelevance.Essential
               WhyNow = "Nothing evaluated yet — start here for guided first steps" }
    | "run_tests" when ctx.HasTests ->
      { s with Relevance = FeatureRelevance.Essential
               WhyNow = $"🧪 {ctx.TotalTests} test(s) discovered — run them to verify your work" }
    | "list_tests" when ctx.HasTests ->
      { s with Relevance = FeatureRelevance.High }
    | "coverage_intel" when ctx.HasFailingTests ->
      { s with Relevance = FeatureRelevance.High
               WhyNow = "Find which code paths are unprotected while tests are failing" }
    | _ -> s

  let private caseInsensitiveContains (needle: string) (haystack: string) =
    haystack.Contains(needle, System.StringComparison.OrdinalIgnoreCase)

  let private matchesTopic (topic: string) (s: FeatureSuggestion) =
    caseInsensitiveContains topic s.ToolName
    || caseInsensitiveContains topic s.ShortDescription
    || caseInsensitiveContains topic s.WhyNow

  /// Discover and rank features given the current session context.
  let discover (ctx: DiscoveryContext) : DiscoveryReport =
    let boosted = catalogue |> List.map (boostForContext ctx)
    let ranked =
      boosted
      |> (match ctx.RequestedTopic with
          | Some topic -> List.filter (matchesTopic topic)
          | None -> id)
      |> List.sortBy (fun s -> relevanceScore s.Relevance, s.ToolName)
    let contextSummary =
      [ if ctx.HasFailingTests then yield $"⚠️ {ctx.FailingTestCount} failing"
        if ctx.HasStaleCells then yield $"⚡ {ctx.StaleCellCount} stale"
        if ctx.TotalEvals > 0 then yield $"📋 {ctx.TotalEvals} evals"
        if ctx.TotalTests > 0 then yield $"🧪 {ctx.TotalTests} tests" ]
      |> function
         | [] -> "Fresh session — nothing evaluated yet"
         | parts -> System.String.Join(" · ", parts)
    { Suggestions = ranked
      ContextSummary = contextSummary
      TotalKnownFeatures = catalogue.Length }

  /// An empty (fresh session) context for use in tests and defaults.
  let emptyContext = {
    HasFailingTests = false
    FailingTestCount = 0
    HasStaleCells = false
    StaleCellCount = 0
    TotalEvals = 0
    HasTests = false
    TotalTests = 0
    RequestedTopic = None
  }
