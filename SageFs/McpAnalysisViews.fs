/// The views of `suggest_repair` and `explain_test_failure` as pure functions of the Elm model and the feature
/// push state. `McpTools` (Mcp.fs) keeps the guards that say "the Elm loop is not started" and hands the data
/// here, so none of these reads a `McpContext`. The other analysis tools (`diagnose`, `coverage_intel`,
/// `impact_forecast`, `suggest_next_action`, `get_cell_dependencies` and the rest) answer for one session through
/// `McpAnalysis`.
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
