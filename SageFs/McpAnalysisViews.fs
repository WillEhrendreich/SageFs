/// The analysis tools' answers as pure functions of the Elm model and the feature push state: `diagnose`,
/// `coverage_intel`, `impact_forecast`, `suggest_next_action`, `suggest_repair`, `explain_test_failure` and
/// `get_cell_dependencies`. `McpTools` (Mcp.fs) keeps the guards that say "the Elm loop is not started" and hands
/// the data here, so none of these reads a `McpContext`.
module SageFs.McpAnalysisViews

open System
open SageFs.Features.CellDependenciesReport

/// The dependency graph of the current FeaturePushState: materialized once per history version from the indexed
/// eval store and shared by all readers.
let cellGraphOf (state: Features.FeatureHooks.FeaturePushState) : Features.CellDependencyGraph.CellGraph =
  Features.FeatureHooks.cellGraph state

/// Convert BindingScopeSnapshot active bindings to Ghostwriter ScopeBinding list.
let toScopeBindings (snapshot: Features.BindingExplorer.BindingScopeSnapshot) : Features.ScopeBinding list =
  snapshot.ActiveBindings
  |> Map.toList
  |> List.map (fun (_key, info) ->
    { Features.ScopeBinding.Name = info.Name
      TypeSig = info.TypeSig
      Value = info.Value })

/// Convert a CoverageVerdict to its JSON string representation.
let private verdictString (v: Features.CoverageIntel.CoverageVerdict) =
  match v with
  | Features.CoverageIntel.WellCovered -> "WellCovered"
  | Features.CoverageIntel.PartialBlindSpot -> "PartialBlindSpot"
  | Features.CoverageIntel.DiagnosticBlindSpot -> "DiagnosticBlindSpot"

/// Convert a CoverageIntelReport to a JSON-serializable anonymous record.
let toCoverageIntelJson (report: Features.CoverageIntel.CoverageIntelReport) =
  {| CoveragePercent = report.CoveragePercent
     CoveredBranches = report.CoveredBranches
     TotalBranches = report.TotalBranches
     Verdict = verdictString report.Verdict
     BlindSpots =
       report.BlindSpots |> List.map (fun g ->
         {| FilePath = g.FilePath
            Line = g.Line
            EndLine = g.EndLine
            BranchId = g.BranchId
            NearestCoveredLine = g.NearestCoveredLine |})
     CorrelatedFailures =
       report.CorrelatedFailures |> List.map Features.LiveTesting.TestId.value
     Summary = Features.CoverageIntel.CoverageIntel.summarize report |}

/// The causal changes of a failure narrative as the wire says them.
let private causalChangeJson (c: Features.LiveTesting.CausalChange) =
  match c with
  | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
  | Features.LiveTesting.CausalChange.FileChanged p -> {| Kind = "file"; Name = p |}
  | Features.LiveTesting.CausalChange.Unknown -> {| Kind = "unknown"; Name = "" |}

/// A property failure as the wire says it.
let private propertyViolationJson (pv: Features.LiveTesting.PropertyViolationDetail) =
  {| PropertyName = pv.PropertyName
     ShrunkCounterexample = pv.ShrunkCounterexample
     AlgebraicCategory = pv.AlgebraicCategory |}

/// Every currently failing test with its narrative; a failure nothing explained yet says so.
let private failuresWithNarratives (testState: Features.LiveTesting.LiveTestState) =
  testState.DiscoveredTests
  |> Array.choose (fun tc ->
    match Map.tryFind tc.Id testState.LastResults with
    | Some r ->
      match r.Result with
      | Features.LiveTesting.TestResult.Failed _ ->
        let narrative =
          match Map.tryFind tc.Id testState.Cached.FailureNarratives with
          | Some n -> n
          | None ->
            { Features.LiveTesting.FailureNarrative.LastPassedAt = None
              TimeSinceLastPass = None
              CausalChanges = []
              PropertyViolation = None
              Summary = "No narrative available" }
        Some (tc.Id, tc.DisplayName, narrative)
      | _ -> None
    | None -> None)
  |> Array.toList

/// Compose a full diagnostic report: joins test failures, cell graph, provenance, ripple plan, suggestions, and
/// performance context.
let diagnoseJson (model: SageFsModel) (state: Features.FeatureHooks.FeaturePushState) : string =
  let graph = cellGraphOf state
  let failures = failuresWithNarratives model.LiveTesting.TestState
  // Get scope bindings for Ghostwriter suggestions
  let scopeBindings = toScopeBindings (Features.FeatureHooks.scope state)
  let report =
    Features.Diagnostician.Diagnostician.compose graph failures scopeBindings state.CachedTimeline
  // Format as both structured JSON and human summary
  let jsonData =
    {| FailureCount = report.Failures.Length
       Severity = report.Severity.ToString()
       AffectedCellCount = report.AffectedCells.Length
       RippleStepCount =
         match report.RipplePlan with
         | Some p -> p.Steps.Length
         | None -> 0
       SuggestionCount = report.SuggestedFixes.Length
       Failures =
         report.Failures
         |> List.map (fun f ->
           {| TestName = f.TestName
              CausalCells = f.CausalCells
              CausalChanges = f.Narrative.CausalChanges |> List.map causalChangeJson
              PropertyViolation = f.Narrative.PropertyViolation |> Option.map propertyViolationJson |})
       Suggestions =
         report.SuggestedFixes
         |> List.truncate 5
         |> List.map (fun s ->
           {| Code = s.Code; Explanation = s.Explanation; Confidence = s.Confidence |})
       Performance = report.PerformanceContext |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
       Summary = report.Summary |}
  Json.serialize Json.standard jsonData

/// Coverage intelligence: joins failure narratives + coverage bitmaps + dep graph into blind spot analysis and
/// correlated failure discovery.
let coverageIntelJson (model: SageFsModel) : string =
  let cycleState = model.LiveTesting
  let testState = cycleState.TestState
  let failures = failuresWithNarratives testState
  let allMaps =
    cycleState.InstrumentationMaps
    |> Map.values |> Seq.collect id |> Seq.toArray
  let causalFileResolver (symbols: string list) =
    symbols
    |> List.collect (fun sym ->
      cycleState.DepGraph.PerFileIndex
      |> Map.toList
      |> List.choose (fun (file, symMap) ->
        match Map.containsKey sym symMap with
        | true -> Some file
        | false -> None))
    |> List.distinct
  let reports =
    Features.CoverageIntel.CoverageIntel.compose
      failures causalFileResolver allMaps
      testState.TestCoverageBitmaps cycleState.DepGraph
  let jsonData =
    reports |> List.map (fun r ->
      {| TestId = r.TestId
         TestName = r.TestName
         Verdict = r.Verdict.ToString()
         CoveragePercent = r.CoveragePercent
         CoveredBranches = r.CoveredBranches
         TotalBranches = r.TotalBranches
         CausalSymbols = r.CausalSymbols
         BlindSpots = r.BlindSpots |> List.map (fun g ->
           {| File = g.FilePath; Line = g.Line; Branch = g.BranchId |})
         CorrelatedFailures = r.CorrelatedFailures |> List.map string
         Summary = Features.CoverageIntel.CoverageIntel.summarize r |})
  Json.serialize Json.standard jsonData

/// Impact forecast: joins eval timeline + cell dependency graph + performance data into regression detection and
/// downstream impact analysis.
let impactForecastJson (state: Features.FeatureHooks.FeaturePushState) (cellIdOpt: int option) : string =
  let graph = cellGraphOf state
  let targetCells =
    match cellIdOpt with
    | Some cid -> [ cid ]
    | None -> graph.Cells |> Map.toList |> List.map fst
  let reports =
    targetCells
    |> List.map (fun cellId ->
      let downstream = Features.CellDependencyGraph.transitiveStale graph cellId
      let timeline = state.CachedTimeline
      let timelineStats =
        Features.EvalTimeline.timelineStats 20 state.CachedTimeline
      let durations =
        timeline.Entries
        |> List.filter (fun e -> e.CellId = cellId)
        |> List.map (fun e -> float e.DurationMs)
        |> List.rev |> List.truncate 10
      let p50 = timelineStats.P50Ms |> Option.defaultValue 0.0
      let p95 = timelineStats.P95Ms |> Option.defaultValue 0.0
      Features.ImpactForecast.ImpactForecast.analyzeCell cellId p50 p95 durations downstream)
  let jsonData =
    reports |> List.map (fun r ->
      {| CellId = r.CellId
         P50Ms = r.P50Ms
         P95Ms = r.P95Ms
         DurationTrend = r.DurationTrendMs
         DownstreamCellCount = r.DownstreamCellCount
         Recommendation = r.Recommendation.ToString()
         RegressionCauses = r.RegressionCauses |> List.map (fun c -> c.ToString())
         Summary = Features.ImpactForecast.ImpactForecast.summarize r |})
  Json.serialize Json.standard jsonData

/// Action prioritizer: merges all intelligence into a ranked "what to do next" queue.
let nextActionJson
  (model: SageFsModel)
  (state: Features.FeatureHooks.FeaturePushState)
  (frictionStore: Features.FrictionSqlite.FrictionStore option)
  : string =
  let cycleState = model.LiveTesting
  let testState = cycleState.TestState
  // Build coverage intel reports
  let failures = failuresWithNarratives testState
  let allMaps =
    cycleState.InstrumentationMaps
    |> Map.values |> Seq.collect id |> Seq.toArray
  let coverageReports =
    Features.CoverageIntel.CoverageIntel.compose
      failures
      (fun _ -> [])
      allMaps testState.TestCoverageBitmaps cycleState.DepGraph
  // Real per-cell durations from EvalTimeline (was `[]` for every cell).
  let graph = cellGraphOf state
  let stats = Features.EvalTimeline.timelineStats 20 state.CachedTimeline
  let p50, p95 = stats.P50Ms |> Option.defaultValue 0.0, stats.P95Ms |> Option.defaultValue 0.0
  let impactReports =
    graph.Cells |> Map.toList |> List.map (fun (cellId, _) ->
      let ds = state.CachedTimeline.Entries |> List.filter (fun e -> e.CellId = cellId)
      let durations = ds |> List.truncate 10 |> List.map (fun e -> float e.DurationMs)
      Features.ImpactForecast.ImpactForecast.analyzeCell cellId p50 p95 durations
        (Features.CellDependencyGraph.transitiveStale graph cellId))
  // Changed cells = the most recently evaluated cell (roast: was EVERY cell, always stale).
  let changedCellIds =
    state.CachedTimeline.Entries |> List.tryHead
    |> Option.map (fun e -> e.CellId) |> Option.toList |> Set.ofList
  let frictionSignalReports = Features.McpFrictionRecorder.Recorder.computeFrictionSignalReports frictionStore
  let report =
    Features.ActionPrioritizer.ActionPrioritizer.compose
      graph coverageReports impactReports changedCellIds frictionSignalReports
  let jsonData =
    {| HealthGrade = report.HealthGrade.ToString()
       TotalFailures = report.TotalFailures
       TotalBlindSpots = report.TotalBlindSpots
       TotalRegressions = report.TotalRegressions
       Actions = report.Actions |> List.truncate 10 |> List.map (fun a ->
         {| Kind = a.Kind.ToString()
            Priority = a.Priority
            Reason = a.Reason |})
       Summary = Features.ActionPrioritizer.ActionPrioritizer.summarize report |}
  Json.serialize Json.standard jsonData

/// Expose the cell dependency graph with staleness annotations.
let cellDependenciesJson (state: Features.FeatureHooks.FeaturePushState) : string =
  let graph = cellGraphOf state
  // Pass empty changed set: graph structure and wiring is always useful.
  // Callers can use plan_ripple with a specific cell to see staleness impact.
  let report = Features.CellDependenciesReport.CellDependenciesReport.compose graph Set.empty
  let jsonData =
    {| TotalCells    = report.TotalCells
       TotalStale    = report.TotalStale
       TotalEdges    = report.TotalEdges
       StaleCellIds  = report.StaleCellIds
       Summary       = report.Summary
       Nodes         = report.Nodes |> List.map (fun n ->
         {| Id            = n.Id
            Produces      = n.Produces
            Consumes      = n.Consumes
            DownstreamIds = n.DownstreamIds
            UpstreamIds   = n.UpstreamIds
            IsStale       = CellFreshness.isStale n.Staleness
            StaleCauses   = CellFreshness.causes n.Staleness |}) |}
  Json.serialize Json.standard jsonData

/// The tests a name (full or display, any case) picks out.
let private testsMatching (testState: Features.LiveTesting.LiveTestState) (testName: string) =
  testState.DiscoveredTests
  |> Array.filter (fun tc ->
    tc.FullName.Contains(testName, StringComparison.OrdinalIgnoreCase)
    || tc.DisplayName.Contains(testName, StringComparison.OrdinalIgnoreCase))

/// suggest_repair: compose explain_test_failure, extract the causal symbol, then preview_what_if.
/// V1: surfaces the causal symbol + current binding + ripple plan without suggesting a new value.
let suggestRepairJson
  (model: SageFsModel)
  (state: Features.FeatureHooks.FeaturePushState)
  (testName: string)
  : string =
  let testState = model.LiveTesting.TestState
  match testsMatching testState testName with
  | [||] ->
    sprintf "No test found matching '%s'. Use list_tests to see available tests." testName
  | tests ->
    let narrativeOpt =
      tests |> Array.tryPick (fun tc -> Map.tryFind tc.Id testState.Cached.FailureNarratives)
    match narrativeOpt with
    | None ->
      let testNames = tests |> Array.map (fun tc -> tc.DisplayName) |> String.concat ", "
      sprintf
        "No failure narrative for '%s' (%s). The test may not have transitioned Passed→Failed recently, or live testing may not be running. Call run_tests to trigger a run, then retry."
        testName testNames
    | Some narrative ->
      let allChanges = narrative.CausalChanges |> List.map causalChangeJson
      let primarySymbol =
        narrative.CausalChanges
        |> List.tryPick (fun c ->
          match c with
          | Features.LiveTesting.CausalChange.SymbolChanged s -> Some s
          | _ -> None)
      // Build ripple plan for primary symbol if it's in session bindings
      let ripplePlanOpt =
        match primarySymbol, state.EvalHistory with
        | None, _ | _, [] -> None
        | Some sym, _ ->
          let graph = cellGraphOf state
          let scope =
            Features.FeatureHooks.scope state
          match scope.ActiveBindings |> Map.tryFind sym with
          | None -> None
          | Some binding ->
            let currentCode = binding.Value |> Option.defaultValue "?"
            let typeSig = binding.TypeSig
            let override' = Features.WhatIf.createOverride sym currentCode "<your-fix>" typeSig
            let plan = Features.WhatIf.planWhatIf graph override'
            let steps =
              plan.RippleSteps
              |> List.map (fun step ->
                {| CellId = step.CellId
                   Code = step.Code |> fun c -> if c.Length > 60 then c.[..57] + "..." else c
                   Status =
                     match step.Status with
                     | Features.Pending     -> "pending"
                     | Features.Evaluating  -> "evaluating"
                     | Features.Succeeded _ -> "succeeded"
                     | Features.Failed _    -> "failed"
                     | Features.Skipped _   -> "skipped" |})
            Some {| Symbol = sym; CurrentCode = currentCode; TypeSig = typeSig; AffectedCellCount = plan.AffectedCells.Length; RippleSteps = steps |}
      let timeSince =
        narrative.TimeSinceLastPass
        |> Option.map (fun ts -> sprintf "%.0fs" ts.TotalSeconds)
        |> Option.defaultValue "unknown"
      let suggestion =
        match primarySymbol, ripplePlanOpt with
        | None, _ ->
          sprintf
            "This test broke ~%s ago. No symbol-level causal changes were detected — review the file changes above and check recent edits manually."
            timeSince
        | Some sym, None ->
          sprintf
            "'%s' is the likely cause, but it's not in the current session bindings. Re-evaluate the cell that defines '%s', then retry suggest_repair."
            sym sym
        | Some sym, Some plan ->
          sprintf
            "'%s' (%s) is the likely cause. Call `preview_what_if \"%s\" \"<new-value>\"` to preview the ripple before applying. %d cells downstream will re-evaluate."
            sym plan.TypeSig sym plan.AffectedCellCount
      let jsonData =
        {| TestName      = testName
           Summary       = narrative.Summary
           TimeSinceLastPass = timeSince
           CausalChanges = allChanges
           PrimarySymbol = primarySymbol |> Option.toObj
           RipplePlan    = ripplePlanOpt |> Option.toObj
           Suggestion    = suggestion |}
      Json.serialize Json.standard jsonData

/// explain_test_failure: the failure narratives of the tests a name picks out, with the diagnostician's report when
/// the feature state is there.
let explainTestFailureJson
  (model: SageFsModel)
  (featureState: Features.FeatureHooks.FeaturePushState option)
  (testName: string)
  : string =
  let testState = model.LiveTesting.TestState
  let allMaps =
    model.LiveTesting.InstrumentationMaps
    |> Map.values
    |> Seq.collect id
    |> Array.ofSeq
  let bitmaps = testState.TestCoverageBitmaps
  let depGraph = model.LiveTesting.DepGraph
  let hasMaps = allMaps.Length > 0
  match testsMatching testState testName with
  | [||] -> sprintf "No test found matching '%s'." testName
  | tests ->
    let narratives =
      tests
      |> Array.choose (fun tc ->
        Map.tryFind tc.Id testState.Cached.FailureNarratives
        |> Option.map (fun (n: Features.LiveTesting.FailureNarrative) ->
          let changes = n.CausalChanges |> List.map causalChangeJson
          let propViolation = n.PropertyViolation |> Option.map propertyViolationJson
          let coverageIntel =
            match hasMaps with
            | false -> None
            | true ->
              let causalFiles =
                n.CausalChanges
                |> List.choose (fun c ->
                  match c with
                  | Features.LiveTesting.CausalChange.FileChanged f -> Some f
                  | _ -> None)
              let report =
                Features.CoverageIntel.CoverageIntel.composeForFailure
                  tc.Id tc.DisplayName n causalFiles allMaps bitmaps depGraph
              Some (toCoverageIntelJson report)
          {| TestId = Features.LiveTesting.TestId.value tc.Id
             DisplayName = tc.DisplayName
             Summary = n.Summary
             LastPassedAt = n.LastPassedAt
             TimeSinceLastPass = n.TimeSinceLastPass |> Option.map (fun ts -> ts.TotalSeconds)
             CausalChanges = changes
             PropertyViolation = propViolation
             CoverageIntel = coverageIntel |}))
    match narratives with
    | [||] ->
      let failingCount =
        tests |> Array.filter (fun tc ->
          match Map.tryFind tc.Id testState.LastResults with
          | Some r ->
            match r.Result with
            | Features.LiveTesting.TestResult.Failed _ -> true
            | _ -> false
          | None -> false) |> Array.length
      match failingCount with
      | 0 -> sprintf "Test(s) matching '%s' are not currently failing — no narrative available." testName
      | _ -> sprintf "Test(s) matching '%s' are failing but no narrative was computed (may not have transitioned from passing)." testName
    | narrs ->
      // Enrich with diagnostic report if feature state is available
      let diagnostics =
        match featureState with
        | Some state ->
          let graph = cellGraphOf state
          let failuresForDiag =
            tests
            |> Array.choose (fun tc ->
              Map.tryFind tc.Id testState.Cached.FailureNarratives
              |> Option.map (fun n -> (tc.Id, tc.DisplayName, n)))
            |> Array.toList
          let scopeBindings =
            toScopeBindings (Features.FeatureHooks.scope state)
          let report =
            Features.Diagnostician.Diagnostician.compose
              graph failuresForDiag scopeBindings state.CachedTimeline
          Some {| Severity = report.Severity.ToString()
                  AffectedCells = report.AffectedCells
                  SuggestionCount = report.SuggestedFixes.Length
                  TopSuggestions =
                    report.SuggestedFixes
                    |> List.truncate 3
                    |> List.map (fun s -> {| Code = s.Code; Explanation = s.Explanation |})
                  Performance =
                    report.PerformanceContext
                    |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
                  Summary = report.Summary |}
        | None -> None
      let resp = {| MatchCount = narrs.Length; Narratives = narrs; Diagnostics = diagnostics |}
      Json.serialize Json.standard resp
