namespace SageFs

open System.Text.Json.Nodes
open System.Threading.Tasks
open SageFs.McpTools
open SageFs.McpSessionRouting
open SageFs.Features.ToolAnswers

/// The session-analysis tools (`impact_forecast`, `get_cell_dependencies`, `plan_ripple`,
/// `preview_what_if`, `suggest_next_cell`, `diagnose`, `coverage_intel`).
///
/// Each one answers for ONE session, resolved the way `run_tests` resolves it, and reads that
/// session's own eval history (the store `send_fsharp_code` records into) and that session's own
/// live-testing cycle. None of them reads "the" global store or "the" primary cycle on its own
/// account: that is how a tool answered about somebody else's session, or said "No issues
/// detected" while a test failed in another session's run.
///
/// Each one answers with a `ToolAnswer`: `Measured` when the data was there (an empty list is then
/// a real zero), `NotAvailable` with a reason and an action when it was not.
module McpAnalysis =

  /// Which cells an impact forecast covers.
  [<RequireQualifiedAccess>]
  type CellScope =
    | AllCells
    | OneCell of cellId: int

  /// The live-testing side of one session, as the tools that read tests see it.
  type TestSide =
    { /// The cycle that owns the session's test data (`SageFsModel.cycleOwnedBySession`).
      Cycle: Features.LiveTesting.LiveTestCycleState
      /// The instrumentation maps recorded for this session alone.
      Maps: Features.LiveTesting.InstrumentationMap array
      Workflow: WorkflowKind }

  module TestSide =
    /// No live testing at all: what a session with no cycle of its own has.
    let none (workflow: WorkflowKind) : TestSide =
      { Cycle = Features.LiveTesting.LiveTestCycleState.empty; Maps = [||]; Workflow = workflow }

    /// The test side of `sessionId`: the cycle that owns its data, and its own maps.
    let ofModel (model: SageFsModel) (sessionId: string) (workflow: WorkflowKind) : TestSide =
      { Cycle = SageFsModel.cycleOwnedBySession sessionId model
        Maps = model.LiveTesting.InstrumentationMaps |> Map.tryFind sessionId |> Option.defaultValue [||]
        Workflow = workflow }

  /// The pure decisions: what each tool says given what its session has recorded.
  module Answer =

    let private json (value: 'payload) : Rendered = Rendered.Json (Json.serialize Json.standard value)

    let private truncated (code: string) =
      match code.Length > 50 with
      | true -> code.[..47] + "..."
      | false -> code

    let private toScopeBindings (snapshot: Features.BindingExplorer.BindingScopeSnapshot) : Features.ScopeBinding list =
      snapshot.ActiveBindings
      |> Map.toList
      |> List.map (fun (_, info) ->
        { Features.ScopeBinding.Name = info.Name
          TypeSig = info.TypeSig
          Value = info.Value })

    /// Every failing test of a cycle with its narrative (an empty one when none was recorded).
    let private failuresOf (cycle: Features.LiveTesting.LiveTestCycleState) =
      let testState = cycle.TestState
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

    /// How many of a cycle's tests failed in their last recorded result.
    let failingTestCount (cycle: Features.LiveTesting.LiveTestCycleState) : int =
      (failuresOf cycle).Length

    let private evalCount (state: Features.FeatureHooks.FeaturePushState) = state.History.Count

    let private resultCount (side: TestSide) = side.Cycle.TestState.LastResults.Count

    /// Run `answer` once the session has at least one recorded eval, else say there are none.
    let private whenEvalsRecorded (state: Features.FeatureHooks.FeaturePushState) (answer: unit -> ToolAnswer<Measurement>) =
      match Readiness.evalsRecorded (evalCount state) with
      | Ok () -> answer ()
      | Error reason -> ToolAnswer.NotAvailable reason

    /// What this measures is REPL cells: how many cells sit downstream of a cell in the eval
    /// dependency graph, and how long the session's evals take. It says nothing about the source
    /// code's blast radius, and nothing about which tests a change affects.
    let impactForecast (state: Features.FeatureHooks.FeaturePushState) (scope: CellScope) : ToolAnswer<Measurement> =
      whenEvalsRecorded state (fun () ->
        let graph = Features.FeatureHooks.cellGraph state
        match scope with
        | CellScope.OneCell cellId when not (Map.containsKey cellId graph.Cells) ->
          ToolAnswer.NotAvailable (NotAvailableReason.CellsNotInHistory [ cellId ])
        | _ ->
          let targetCells =
            match scope with
            | CellScope.OneCell cellId -> [ cellId ]
            | CellScope.AllCells -> graph.Cells |> Map.toList |> List.map fst
          let timelineStats = Features.EvalTimeline.timelineStats 20 state.CachedTimeline
          let p50 = timelineStats.P50Ms |> Option.defaultValue 0.0
          let p95 = timelineStats.P95Ms |> Option.defaultValue 0.0
          let reports =
            targetCells
            |> List.map (fun cellId ->
              let downstream = Features.CellDependencyGraph.transitiveStale graph cellId
              let durations =
                state.CachedTimeline.Entries
                |> List.filter (fun e -> e.CellId = cellId)
                |> List.map (fun e -> float e.DurationMs)
                |> List.rev
                |> List.truncate 10
              Features.ImpactForecast.ImpactForecast.analyzeCell cellId p50 p95 durations downstream)
          reports
          |> List.map (fun r ->
            {| CellId = r.CellId
               P50Ms = r.P50Ms
               P95Ms = r.P95Ms
               DurationTrend = r.DurationTrendMs
               DownstreamCellCount = r.DownstreamCellCount
               Recommendation = r.Recommendation.ToString()
               RegressionCauses = r.RegressionCauses |> List.map (fun c -> c.ToString())
               Summary = Features.ImpactForecast.ImpactForecast.summarize r |})
          |> json
          |> ToolAnswer.measured)

    /// The graph's producers, consumers and edges. Staleness is not measured here, so it is not
    /// reported as zero: `plan_ripple` answers what a change would invalidate.
    let cellDependencies (state: Features.FeatureHooks.FeaturePushState) : ToolAnswer<Measurement> =
      whenEvalsRecorded state (fun () ->
        let graph = Features.FeatureHooks.cellGraph state
        let report = Features.CellDependenciesReport.CellDependenciesReport.compose graph Set.empty
        {| TotalCells = report.TotalCells
           TotalEdges = report.TotalEdges
           Staleness = "NotMeasured"
           Summary = sprintf "%d cell(s), %d edge(s)" report.TotalCells report.TotalEdges
           Nodes =
             report.Nodes
             |> List.map (fun n ->
               {| Id = n.Id
                  Produces = n.Produces
                  Consumes = n.Consumes
                  DownstreamIds = n.DownstreamIds
                  UpstreamIds = n.UpstreamIds |}) |}
        |> json
        |> ToolAnswer.measured)

    let planRipple (state: Features.FeatureHooks.FeaturePushState) (changedCellIds: string) : ToolAnswer<Measurement> =
      whenEvalsRecorded state (fun () ->
        let tokens = changedCellIds.Split([| ','; ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
        let parsed =
          tokens
          |> Array.map (fun s -> match System.Int32.TryParse s with | true, v -> Ok v | _ -> Error s)
        let readable = parsed |> Array.forall (function Ok _ -> true | Error _ -> false)
        match tokens.Length > 0 && readable with
        | false -> ToolAnswer.NotAvailable NotAvailableReason.NoUsableCellIds
        | true ->
          let cellIds = parsed |> Array.choose (function Ok v -> Some v | Error _ -> None) |> Set.ofArray
          let graph = Features.FeatureHooks.cellGraph state
          match cellIds |> Set.filter (fun id -> not (Map.containsKey id graph.Cells)) |> Set.toList with
          | _ :: _ as missing -> ToolAnswer.NotAvailable (NotAvailableReason.CellsNotInHistory missing)
          | [] ->
            let plan = Features.EvalRipple.planRipple graph cellIds
            plan.Steps
            |> List.map (fun step ->
              sprintf "  [%d] %s — %s"
                step.CellId
                (truncated step.Code)
                (match step.Status with
                 | Features.Pending -> "pending"
                 | Features.Evaluating -> "evaluating"
                 | Features.Succeeded o -> sprintf "ok: %s" o
                 | Features.Failed e -> sprintf "FAILED: %s" e
                 | Features.Skipped r -> sprintf "skipped: %s" r))
            |> String.concat "\n"
            |> sprintf "Ripple plan (%d steps, %d changed):\n%s" plan.Steps.Length cellIds.Count
            |> Rendered.Prose
            |> ToolAnswer.measured)

    let previewWhatIf (state: Features.FeatureHooks.FeaturePushState) (bindingName: string) (newCode: string) : ToolAnswer<Measurement> =
      whenEvalsRecorded state (fun () ->
        let scope = Features.FeatureHooks.scope state
        match Map.tryFind bindingName scope.ActiveBindings with
        | None -> ToolAnswer.NotAvailable (NotAvailableReason.BindingNotInScope bindingName)
        | Some existing ->
          let graph = Features.FeatureHooks.cellGraph state
          let original = existing.Value |> Option.defaultValue "?"
          let override' = Features.WhatIf.createOverride bindingName original newCode existing.TypeSig
          let plan = Features.WhatIf.planWhatIf graph override'
          [ sprintf "What-If: %s" (Features.WhatIf.formatOverride override')
            sprintf "Affected cells: %d" plan.AffectedCells.Length
            yield!
              plan.RippleSteps
              |> List.map (fun step -> sprintf "  [%d] %s" step.CellId (truncated step.Code)) ]
          |> String.concat "\n"
          |> Rendered.Prose
          |> ToolAnswer.measured)

    let suggestNextCell (state: Features.FeatureHooks.FeaturePushState) : ToolAnswer<Measurement> =
      whenEvalsRecorded state (fun () ->
        let bindings = toScopeBindings (Features.FeatureHooks.scope state)
        match bindings with
        | [] ->
          sprintf "No bindings in scope: %d eval(s) are recorded and none of them bound a name." (evalCount state)
          |> Rendered.Prose
          |> ToolAnswer.measured
        | _ ->
          match Features.Ghostwriter.suggest bindings with
          | [] -> ToolAnswer.measured (Rendered.Prose "No suggestions available for the current bindings.")
          | suggestions ->
            suggestions
            |> List.map (fun s -> sprintf "  %.0f%% %s — %s" (s.Confidence * 100.0) s.Code s.Explanation)
            |> String.concat "\n"
            |> sprintf "Ghostwriter suggestions (%d):\n%s" suggestions.Length
            |> Rendered.Prose
            |> ToolAnswer.measured)

    let private unmeasuredSentence (unmeasured: UnmeasuredScope list) =
      unmeasured
      |> List.map (function
        | UnmeasuredScope.Tests -> "tests (no test result is recorded for this session; call run_tests)"
        | UnmeasuredScope.Cells -> "cells (no eval is recorded for this session; call send_fsharp_code)")
      |> String.concat ", "

    /// A composed report over the session's cells and its tests. It names what it could not read
    /// instead of reporting a side it never saw as healthy.
    let diagnose (state: Features.FeatureHooks.FeaturePushState) (side: TestSide) : ToolAnswer<Measurement> =
      match Readiness.diagnosable (evalCount state) (resultCount side) with
      | Error reason -> ToolAnswer.NotAvailable reason
      | Ok unmeasured ->
        let graph = Features.FeatureHooks.cellGraph state
        let scopeBindings = toScopeBindings (Features.FeatureHooks.scope state)
        let report =
          Features.Diagnostician.Diagnostician.compose graph (failuresOf side.Cycle) scopeBindings state.CachedTimeline
        let summary =
          match report.Failures, unmeasured with
          | [], _ :: _ -> sprintf "Nothing wrong in what was measured. Not measured: %s." (unmeasuredSentence unmeasured)
          | _ -> report.Summary
        {| FailureCount = report.Failures.Length
           Severity = report.Severity.ToString()
           AffectedCellCount = report.AffectedCells.Length
           RippleStepCount =
             match report.RipplePlan with
             | Some p -> p.Steps.Length
             | None -> 0
           SuggestionCount = report.SuggestedFixes.Length
           Unmeasured = unmeasured |> List.map UnmeasuredScope.token
           Failures =
             report.Failures
             |> List.map (fun f ->
               {| TestName = f.TestName
                  CausalCells = f.CausalCells
                  CausalChanges =
                    f.Narrative.CausalChanges
                    |> List.map (fun c ->
                      match c with
                      | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
                      | Features.LiveTesting.CausalChange.FileChanged p -> {| Kind = "file"; Name = p |}
                      | Features.LiveTesting.CausalChange.Unknown -> {| Kind = "unknown"; Name = "" |})
                  PropertyViolation =
                    f.Narrative.PropertyViolation
                    |> Option.map (fun pv ->
                      {| PropertyName = pv.PropertyName
                         ShrunkCounterexample = pv.ShrunkCounterexample
                         AlgebraicCategory = pv.AlgebraicCategory |}) |})
           Suggestions =
             report.SuggestedFixes
             |> List.truncate 5
             |> List.map (fun s -> {| Code = s.Code; Explanation = s.Explanation; Confidence = s.Confidence |})
           Performance = report.PerformanceContext |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
           Summary = summary |}
        |> json
        |> fun body -> ToolAnswer.Measured { Body = body; Unmeasured = unmeasured }

    /// Per-failing-test coverage. Coverage only comes from live testing's instrumented runs, so
    /// without it a failing test is a question this tool cannot answer, and the reason says which
    /// switch is missing. No failing test is a measured, empty answer.
    let coverageIntel (side: TestSide) : ToolAnswer<Measurement> =
      match Readiness.testsRecorded (resultCount side) with
      | Error reason -> ToolAnswer.NotAvailable reason
      | Ok () ->
        match failuresOf side.Cycle with
        | [] -> ToolAnswer.measured (Rendered.Json "[]")
        | failures ->
          let testState = side.Cycle.TestState
          // Coverage is evidence only when a failing test has its own recorded bitmap. A session can hold
          // instrumentation maps (even empty ones) without any run having recorded a hit, and "0% of 0
          // branches" is not a measurement of that test.
          let evidence =
            match failures |> List.exists (fun (testId, _, _) -> Map.containsKey testId testState.TestCoverageBitmaps) with
            | true -> CoverageEvidence.Recorded
            | false -> CoverageEvidence.NotRecorded
          match Readiness.coverageRecorded testState.Activation side.Workflow evidence with
          | Error reason -> ToolAnswer.NotAvailable reason
          | Ok () ->
            let causalFileResolver (symbols: string list) =
              symbols
              |> List.collect (fun sym ->
                side.Cycle.DepGraph.PerFileIndex
                |> Map.toList
                |> List.choose (fun (file, symMap) ->
                  match Map.containsKey sym symMap with
                  | true -> Some file
                  | false -> None))
              |> List.distinct
            Features.CoverageIntel.CoverageIntel.compose
              failures causalFileResolver side.Maps testState.TestCoverageBitmaps side.Cycle.DepGraph
            |> List.map (fun r ->
              {| TestId = r.TestId
                 TestName = r.TestName
                 Verdict = r.Verdict.ToString()
                 CoveragePercent = r.CoveragePercent
                 CoveredBranches = r.CoveredBranches
                 TotalBranches = r.TotalBranches
                 CausalSymbols = r.CausalSymbols
                 BlindSpots = r.BlindSpots |> List.map (fun g -> {| File = g.FilePath; Line = g.Line; Branch = g.BranchId |})
                 CorrelatedFailures = r.CorrelatedFailures |> List.map string
                 Summary = Features.CoverageIntel.CoverageIntel.summarize r |})
            |> json
            |> ToolAnswer.measured

  // ── The wire ────────────────────────────────────────────────

  /// The text block: the measurement as it always was (JSON, or prose), or one plain sentence
  /// saying what is missing and what to do.
  let textOf (answer: ToolAnswer<Measurement>) : string =
    match answer with
    | ToolAnswer.Measured { Body = Rendered.Json text } -> text
    | ToolAnswer.Measured { Body = Rendered.Prose text } -> text
    | ToolAnswer.NotAvailable reason -> notAvailableText reason

  let private payloadNode (body: Rendered) : JsonNode =
    match body with
    | Rendered.Prose text -> JsonValue.Create text
    | Rendered.Json text ->
      try JsonNode.Parse text
      with _ -> JsonValue.Create text

  /// The structured content: a typed `answer` field, and either the `payload` or the `reason`
  /// with its sentence and action.
  let structuredOf (answer: ToolAnswer<Measurement>) : string =
    let root = JsonObject()
    root["answer"] <- JsonValue.Create(ToolAnswer.token answer)
    match answer with
    | ToolAnswer.Measured measurement ->
      let unmeasured = JsonArray()
      measurement.Unmeasured |> List.iter (fun scope -> unmeasured.Add(JsonValue.Create(UnmeasuredScope.token scope)))
      root["unmeasured"] <- unmeasured
      root["payload"] <- payloadNode measurement.Body
    | ToolAnswer.NotAvailable reason ->
      root["reason"] <- JsonValue.Create(NotAvailableReason.token reason)
      root["message"] <- JsonValue.Create(NotAvailableReason.describe reason)
      root["whatToDo"] <- JsonValue.Create(NotAvailableReason.whatToDo reason)
    root.ToJsonString()

  // ── Resolving the session ───────────────────────────────────

  /// The session this call answers for, resolved the way `run_tests` resolves it.
  let private resolve
    (ctx: McpContext)
    (toolName: string)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<Result<string, NotAvailableReason>> =
    task {
      let! resolution = resolveAdmitted ctx toolName "mcp" sessionId workingDirectory
      return
        match resolution with
        | Routable sid -> Ok sid
        | WarmingUp _ -> Error (NotAvailableReason.SessionNotReady SessionReadiness.WarmingUp)
        | Unroutable _ -> Error (NotAvailableReason.SessionNotReady SessionReadiness.NotRoutable)
        | FaultedSession _ -> Error (NotAvailableReason.SessionNotReady SessionReadiness.Faulted)
        | Gone why -> Error (NotAvailableReason.NoSessionResolved why)
    }

  /// The eval history of one session: what `send_fsharp_code` recorded for it. A call with
  /// nothing recorded gets an empty one, which the answers read as "no evals yet".
  let private historyOf (ctx: McpContext) (sid: string) : Features.FeatureHooks.FeaturePushState =
    featureStateForSession ctx sid |> Option.defaultValue Features.FeatureHooks.FeaturePushState.empty

  /// The live-testing side of one session. With no Elm loop there is no engine, so no test result.
  let private testSideOf (ctx: McpContext) (sid: string) : Task<Result<TestSide, NotAvailableReason>> =
    task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      match info with
      | None -> return Error (NotAvailableReason.NoSessionResolved "The session is no longer registered with the daemon.")
      | Some i ->
        let workflow = WorkflowKind.ofSession i.Workflow
        match ctx.GetElmModel with
        | Some getModel -> return Ok (TestSide.ofModel (getModel ()) sid workflow)
        | None -> return Ok (TestSide.none workflow)
    }

  let private answerFor
    (ctx: McpContext)
    (toolName: string)
    (sessionId: string option)
    (workingDirectory: string option)
    (decide: string -> Task<ToolAnswer<Measurement>>)
    : Task<ToolAnswer<Measurement>> =
    task {
      match! resolve ctx toolName sessionId workingDirectory with
      | Error reason -> return ToolAnswer.NotAvailable reason
      | Ok sid -> return! decide sid
    }

  let private overHistory
    (ctx: McpContext)
    (toolName: string)
    (sessionId: string option)
    (workingDirectory: string option)
    (decide: Features.FeatureHooks.FeaturePushState -> ToolAnswer<Measurement>)
    : Task<ToolAnswer<Measurement>> =
    answerFor ctx toolName sessionId workingDirectory (fun sid -> Task.FromResult (decide (historyOf ctx sid)))

  let impactForecast ctx (scope: CellScope) sessionId workingDirectory =
    overHistory ctx "impact_forecast" sessionId workingDirectory (fun state -> Answer.impactForecast state scope)

  let cellDependencies ctx sessionId workingDirectory =
    overHistory ctx "get_cell_dependencies" sessionId workingDirectory Answer.cellDependencies

  let planRipple ctx (changedCells: string) sessionId workingDirectory =
    overHistory ctx "plan_ripple" sessionId workingDirectory (fun state -> Answer.planRipple state changedCells)

  let previewWhatIf ctx (bindingName: string) (newCode: string) sessionId workingDirectory =
    overHistory ctx "preview_what_if" sessionId workingDirectory (fun state -> Answer.previewWhatIf state bindingName newCode)

  let suggestNextCell ctx sessionId workingDirectory =
    overHistory ctx "suggest_next_cell" sessionId workingDirectory Answer.suggestNextCell

  let diagnose (ctx: McpContext) sessionId workingDirectory =
    answerFor ctx "diagnose" sessionId workingDirectory (fun sid ->
      task {
        match! testSideOf ctx sid with
        | Error reason -> return ToolAnswer.NotAvailable reason
        | Ok side -> return Answer.diagnose (historyOf ctx sid) side
      })

  let coverageIntel (ctx: McpContext) sessionId workingDirectory =
    answerFor ctx "coverage_intel" sessionId workingDirectory (fun sid ->
      task {
        match! testSideOf ctx sid with
        | Error reason -> return ToolAnswer.NotAvailable reason
        | Ok side -> return Answer.coverageIntel side
      })

  // ── discover_features ───────────────────────────────────────

  /// The feature tour for one session. It ranks the REGISTERED tools by what the session's own
  /// evals and tests say, and it says so when it could not resolve a session instead of ranking
  /// as if the session were fresh.
  let discoverFeatures
    (ctx: McpContext)
    (registered: Features.FeatureDiscovery.RegisteredTool list)
    (topic: string option)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<string> =
    task {
      let requested = topic |> Option.filter (fun s -> s.Length > 0)
      let! report =
        task {
          match! resolve ctx "discover_features" sessionId workingDirectory with
          | Error reason ->
            let fresh = { Features.FeatureDiscovery.FeatureDiscovery.emptyContext with RequestedTopic = requested }
            let report = Features.FeatureDiscovery.FeatureDiscovery.discoverOver registered fresh
            return { report with ContextSummary = sprintf "Not ranked by session state. %s" (NotAvailableReason.describe reason) }
          | Ok sid ->
            let state = historyOf ctx sid
            let! side = testSideOf ctx sid
            let cycle =
              match side with
              | Ok s -> s.Cycle
              | Error _ -> Features.LiveTesting.LiveTestCycleState.empty
            let discoveryCtx : Features.FeatureDiscovery.DiscoveryContext =
              { FailingTestCount = Answer.failingTestCount cycle
                StaleCellCount = 0
                TotalEvals = state.History.Count
                TotalTests = cycle.TestState.DiscoveredTests.Length
                RequestedTopic = requested }
            return Features.FeatureDiscovery.FeatureDiscovery.discoverOver registered discoveryCtx
        }
      return
        Json.serialize Json.standard
          {| ContextSummary = report.ContextSummary
             TotalKnownFeatures = report.TotalKnownFeatures
             Returned = report.Suggestions.Length
             Suggestions =
               report.Suggestions
               |> List.map (fun s ->
                 {| ToolName = s.ToolName
                    ShortDescription = s.ShortDescription
                    ExampleUsage = s.ExampleUsage
                    WhyNow = s.WhyNow
                    Relevance = s.Relevance.ToString() |}) |}
    }
