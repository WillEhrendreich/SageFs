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
  /// Number of failing tests.
  FailingTestCount: int
  /// Number of stale cells.
  StaleCellCount: int
  /// Total cells evaluated so far this session.
  TotalEvals: int
  /// Total tests discovered.
  TotalTests: int
  /// Optional topic the user asked about (used to filter suggestions).
  RequestedTopic: string option
}

module DiscoveryContext =
  let hasFailingTests (ctx: DiscoveryContext) = ctx.FailingTestCount > 0
  let hasStaleCells (ctx: DiscoveryContext) = ctx.StaleCellCount > 0
  let hasTests (ctx: DiscoveryContext) = ctx.TotalTests > 0

/// Ranked list of feature suggestions with context summary.
type DiscoveryReport = {
  /// Suggestions sorted by relevance (most relevant first).
  Suggestions: FeatureSuggestion list
  /// Human-readable summary of the context used for ranking.
  ContextSummary: string
  /// Total features in the catalogue.
  TotalKnownFeatures: int
}

/// A tool the daemon registers, as its registration reads: the name it is called by, the first
/// paragraph of its description, and the parameters a call must pass.
type RegisteredTool = {
  Name: string
  Summary: string
  RequiredParameters: string list
}

module FeatureDiscovery =

  /// Whether the session's state raises a tool's rank, and why.
  [<RequireQualifiedAccess>]
  type private Boost =
    | NoBoost
    | Boosted of relevance: FeatureRelevance * whyNow: string

  /// What discovery adds to a registered tool: where it ranks with nothing to go on, and when
  /// the session's state raises it. A tool with no entry here is still advertised (it is
  /// registered), ranked last.
  type private Ranking = {
    Relevance: FeatureRelevance
    WhyNow: string
    Boost: DiscoveryContext -> Boost
  }

  let private rank relevance whyNow : Ranking =
    { Relevance = relevance; WhyNow = whyNow; Boost = fun _ -> Boost.NoBoost }

  /// The most characters of a tool's description discovery repeats.
  [<Literal>]
  let private MaxSummaryChars = 200

  /// The tools discovery knows how to rank. Every name here must be a registered tool: a test
  /// reads the registered set by reflection and fails when one is not, so discovery can never
  /// advertise a tool the daemon does not have.
  let private rankings : (string * Ranking) list = [
    "diagnose",
      rank FeatureRelevance.Essential "When something feels wrong, start here for the complete picture"
    "run_tests",
      { Relevance = FeatureRelevance.High
        WhyNow = "Re-validate your work after code changes"
        Boost = fun ctx ->
          match DiscoveryContext.hasTests ctx with
          | true -> Boost.Boosted (FeatureRelevance.Essential, $"🧪 {ctx.TotalTests} test(s) discovered — run them to verify your work")
          | false -> Boost.NoBoost }
    "explain_test_failure",
      { Relevance = FeatureRelevance.High
        WhyNow = "Get root-cause analysis when a test breaks"
        Boost = fun ctx ->
          match DiscoveryContext.hasFailingTests ctx with
          | true -> Boost.Boosted (FeatureRelevance.Essential, $"⚠️ You have {ctx.FailingTestCount} failing test(s) — find out why")
          | false -> Boost.NoBoost }
    "suggest_next_action",
      { Relevance = FeatureRelevance.High
        WhyNow = "Not sure where to start? Let the intelligence layer prioritise"
        Boost = fun ctx ->
          match DiscoveryContext.hasFailingTests ctx with
          | true -> Boost.Boosted (FeatureRelevance.Essential, "Failing tests detected — let the prioritiser guide your next move")
          | false -> Boost.NoBoost }
    "coverage_intel",
      { Relevance = FeatureRelevance.High
        WhyNow = "Identify which code paths are unprotected by tests"
        Boost = fun ctx ->
          match DiscoveryContext.hasFailingTests ctx with
          | true -> Boost.Boosted (FeatureRelevance.High, "Find which code paths are unprotected while tests are failing")
          | false -> Boost.NoBoost }
    "list_tests",
      { Relevance = FeatureRelevance.High
        WhyNow = "Discover what tests exist before running them"
        Boost = fun ctx ->
          match DiscoveryContext.hasTests ctx with
          | true -> Boost.Boosted (FeatureRelevance.High, "Discover what tests exist before running them")
          | false -> Boost.NoBoost }
    "get_cell_dependencies",
      { Relevance = FeatureRelevance.High
        WhyNow = "See exactly how bindings flow between cells"
        Boost = fun ctx ->
          match DiscoveryContext.hasStaleCells ctx with
          | true -> Boost.Boosted (FeatureRelevance.Essential, "Stale cells detected — inspect the dependency graph")
          | false -> Boost.NoBoost }
    "plan_ripple",
      { Relevance = FeatureRelevance.Medium
        WhyNow = "Understand which cells a change reaches before editing a binding"
        Boost = fun ctx ->
          match DiscoveryContext.hasStaleCells ctx with
          | true -> Boost.Boosted (FeatureRelevance.Essential, $"⚡ {ctx.StaleCellCount} stale cell(s) — see the re-evaluation plan")
          | false -> Boost.NoBoost }
    "preview_what_if",
      rank FeatureRelevance.Medium "Explore hypothetical changes safely before committing"
    "impact_forecast",
      rank FeatureRelevance.Medium "Check how many cells sit downstream of a cell and how long evals take"
    "suggest_next_cell",
      { Relevance = FeatureRelevance.Medium
        WhyNow = "Blank page? Let the type system suggest the next useful operation"
        Boost = fun ctx ->
          match ctx.TotalEvals = 0 with
          | true -> Boost.Boosted (FeatureRelevance.Essential, "Nothing evaluated yet — start here for guided first steps")
          | false -> Boost.NoBoost }
    "get_eval_timeline",
      rank FeatureRelevance.Medium "Track eval performance trends to catch regressions early"
    "check_fsharp_code",
      rank FeatureRelevance.Medium "Validate syntax and types before submitting to FSI"
    "get_session_filmstrip",
      rank FeatureRelevance.Medium "Review the chronological story of your session"
    "export_notebook",
      rank FeatureRelevance.Contextual "Save your interactive work to share or revisit later"
    "get_message_journal",
      rank FeatureRelevance.Contextual "Review what happened during a session for observability"
    "manage_scratch_pad",
      rank FeatureRelevance.Contextual "Organise ad-hoc code you've been experimenting with"
  ]

  /// Why a registered tool that discovery has no rule for is still advertised, and last.
  let private unrankedWhyNow =
    "Part of the tool surface. Ranked last because nothing in the session's state points at it."

  /// The names discovery has a ranking rule for.
  let rankedToolNames : string list = rankings |> List.map fst

  /// The first paragraph of a tool's description, on one line and no longer than the budget.
  let summarize (description: string) : string =
    let firstParagraph =
      description.Replace("\r\n", "\n").Split([| "\n\n" |], System.StringSplitOptions.RemoveEmptyEntries)
      |> Array.tryHead
      |> Option.defaultValue ""
    let oneLine = System.String.Join(" ", firstParagraph.Split([| '\n'; '\r'; ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries))
    match oneLine.Length > MaxSummaryChars with
    | false -> oneLine
    | true ->
      let cut = oneLine.Substring(0, MaxSummaryChars)
      let atWord = match cut.LastIndexOf ' ' with | i when i > 0 -> cut.Substring(0, i) | _ -> cut
      atWord + "..."

  /// A call shape for a tool, built from the parameters it requires: `plan_ripple(changed_cells=...)`.
  let exampleOf (tool: RegisteredTool) : string =
    tool.RequiredParameters
    |> List.map (fun p -> p + "=...")
    |> String.concat ", "
    |> sprintf "%s(%s)" tool.Name

  let private rankingOf (name: string) : Ranking option =
    rankings |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd

  let private relevanceScore = function
    | FeatureRelevance.Essential  -> 0
    | FeatureRelevance.High       -> 1
    | FeatureRelevance.Medium     -> 2
    | FeatureRelevance.Contextual -> 3

  let private suggestionFor (ctx: DiscoveryContext) (tool: RegisteredTool) : FeatureSuggestion =
    let baseline =
      match rankingOf tool.Name with
      | Some r -> r
      | None -> rank FeatureRelevance.Contextual unrankedWhyNow
    let relevance, whyNow =
      match baseline.Boost ctx with
      | Boost.Boosted (relevance, whyNow) -> relevance, whyNow
      | Boost.NoBoost -> baseline.Relevance, baseline.WhyNow
    { ToolName = tool.Name
      ShortDescription = tool.Summary
      ExampleUsage = exampleOf tool
      WhyNow = whyNow
      Relevance = relevance }

  let private caseInsensitiveContains (needle: string) (haystack: string) =
    haystack.Contains(needle, System.StringComparison.OrdinalIgnoreCase)

  let private matchesTopic (topic: string) (s: FeatureSuggestion) =
    caseInsensitiveContains topic s.ToolName
    || caseInsensitiveContains topic s.ShortDescription
    || caseInsensitiveContains topic s.WhyNow

  /// Discover and rank the REGISTERED tools given the current session context. The catalogue is
  /// the registered set: a tool the daemon does not register cannot appear, and a registered
  /// tool always does.
  let discoverOver (registered: RegisteredTool list) (ctx: DiscoveryContext) : DiscoveryReport =
    let suggestions = registered |> List.map (suggestionFor ctx)
    let ranked =
      suggestions
      |> (match ctx.RequestedTopic with
          | Some topic -> List.filter (matchesTopic topic)
          | None -> id)
      |> List.sortBy (fun s -> relevanceScore s.Relevance, s.ToolName)
    let contextSummary =
      [ if DiscoveryContext.hasFailingTests ctx then yield $"⚠️ {ctx.FailingTestCount} failing"
        if DiscoveryContext.hasStaleCells ctx then yield $"⚡ {ctx.StaleCellCount} stale"
        if ctx.TotalEvals > 0 then yield $"📋 {ctx.TotalEvals} evals"
        if ctx.TotalTests > 0 then yield $"🧪 {ctx.TotalTests} tests" ]
      |> function
         | [] -> "Fresh session — nothing evaluated yet"
         | parts -> System.String.Join(" · ", parts)
    { Suggestions = ranked
      ContextSummary = contextSummary
      TotalKnownFeatures = registered.Length }

  /// Discovery with no registered set to read, so nothing to advertise. It exists because
  /// `Mcp.discoverFeatures` still calls it; the tool itself calls `discoverOver` with the
  /// registered set, and `Mcp.discoverFeatures` can be deleted.
  let discover (ctx: DiscoveryContext) : DiscoveryReport =
    discoverOver [] ctx

  /// An empty (fresh session) context for use in tests and defaults.
  let emptyContext = {
    FailingTestCount = 0
    StaleCellCount = 0
    TotalEvals = 0
    TotalTests = 0
    RequestedTopic = None
  }
