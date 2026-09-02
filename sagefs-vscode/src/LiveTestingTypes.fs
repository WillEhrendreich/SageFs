module SageFs.Vscode.LiveTestingTypes

/// Test outcome — replaces boolean pass/fail with domain meaning
[<RequireQualifiedAccess>]
type VscTestOutcome =
  | Passed
  | Failed of message: string
  | Skipped of reason: string
  | Running
  | Errored of message: string
  | Stale
  | PolicyDisabled

/// Stable test identity across reloads
[<RequireQualifiedAccess>]
type VscTestId =
  | VscTestId of string

module VscTestId =
  let create (s: string) = VscTestId.VscTestId s
  let value (VscTestId.VscTestId s) = s

/// Discovered test metadata from the server
type VscTestInfo = {
  Id: VscTestId
  DisplayName: string
  FullName: string
  FilePath: string option
  Line: int option
}

/// Result of a single test execution
type VscTestResult = {
  Id: VscTestId
  Outcome: VscTestOutcome
  DurationMs: float option
  Output: string option
}

/// Whether coverage is healthy — replaces bool (per-line granularity)
[<RequireQualifiedAccess>]
type VscLineCoverageHealth =
  | AllPassing
  | SomeFailing

/// Per-line coverage status
[<RequireQualifiedAccess>]
type VscLineCoverage =
  | Covered of testCount: int * health: VscLineCoverageHealth
  | NotCovered
  | Pending

/// Per-file coverage data
type VscFileCoverage = {
  FilePath: string
  LineCoverage: Map<int, VscLineCoverage>
  CoveredCount: int
  TotalCount: int
}

/// Run policy — when tests auto-execute
[<RequireQualifiedAccess>]
type VscRunPolicy =
  | EveryKeystroke
  | OnSave
  | OnDemand
  | Disabled

module VscRunPolicy =
  let fromString (s: string) =
    match s.ToLowerInvariant() with
    | "every" -> Some VscRunPolicy.EveryKeystroke
    | "save" -> Some VscRunPolicy.OnSave
    | "demand" -> Some VscRunPolicy.OnDemand
    | "disabled" -> Some VscRunPolicy.Disabled
    | _ -> None

  let toString (p: VscRunPolicy) =
    match p with
    | VscRunPolicy.EveryKeystroke -> "every"
    | VscRunPolicy.OnSave -> "save"
    | VscRunPolicy.OnDemand -> "demand"
    | VscRunPolicy.Disabled -> "disabled"

/// Test category — determines default run policy
[<RequireQualifiedAccess>]
type VscTestCategory =
  | Unit
  | Integration
  | Browser
  | Benchmark
  | Architecture
  | Property

module VscTestCategory =
  let fromString (s: string) =
    match s.ToLowerInvariant() with
    | "unit" -> Some VscTestCategory.Unit
    | "integration" -> Some VscTestCategory.Integration
    | "browser" -> Some VscTestCategory.Browser
    | "benchmark" -> Some VscTestCategory.Benchmark
    | "architecture" -> Some VscTestCategory.Architecture
    | "property" -> Some VscTestCategory.Property
    | _ -> None

  let toString (c: VscTestCategory) =
    match c with
    | VscTestCategory.Unit -> "unit"
    | VscTestCategory.Integration -> "integration"
    | VscTestCategory.Browser -> "browser"
    | VscTestCategory.Benchmark -> "benchmark"
    | VscTestCategory.Architecture -> "architecture"
    | VscTestCategory.Property -> "property"

/// Whether live testing is enabled — replaces bool
[<RequireQualifiedAccess>]
type VscLiveTestingEnabled =
  | LiveTestingOn
  | LiveTestingOff

/// Result freshness — mirrors daemon's ResultFreshness
[<RequireQualifiedAccess>]
type VscResultFreshness =
  | Fresh
  | StaleCodeEdited
  | StaleWrongGeneration

/// SSE events from the SageFs server
[<RequireQualifiedAccess>]
type VscLiveTestEvent =
  /// tests: the discovered/streamed test infos; isComplete: true when the
  /// server marked this batch Completion=Complete (the entries are the
  /// authoritative full discovery set); discoveryGeneration: the server's
  /// discovery generation (0 = none/unversioned).
  | TestsDiscovered of tests: VscTestInfo array * isComplete: bool * discoveryGeneration: int64
  | TestRunStarted of testIds: VscTestId array
  | TestResultBatch of results: VscTestResult array * freshness: VscResultFreshness
  | LiveTestingEnabled
  | LiveTestingDisabled
  | RunPolicyChanged of category: VscTestCategory * policy: VscRunPolicy
  | TestCycleTimingRecorded of treeSitterMs: float * fcsMs: float * executionMs: float
  | CoverageUpdated of coverage: Map<string, VscFileCoverage>

[<RequireQualifiedAccess>]
type VscSelectionPrecision =
  | ExactDependencyMatch
  | CoverageApproximation
  | ConservativeFallback
  | NoImpactedTests
  | SuppressedByPolicy

[<RequireQualifiedAccess>]
type VscFreshnessTrust =
  | FreshExact
  | FreshApproximate
  | StaleAwaitingRerun
  | Suppressed

[<RequireQualifiedAccess>]
type VscRerunCause =
  | KeystrokeBuffered
  | FileSaved
  | ExplicitRunRequested

type VscLiveTestingDecision = {
  Cause: VscRerunCause
  FilePath: string
  Precision: VscSelectionPrecision
  Trust: VscFreshnessTrust
  ChangedSymbols: string array
  SelectedTests: string array
  DeferredTests: string array
  Reason: string
}

/// Test summary counts
type VscTestSummary = {
  Total: int
  Passed: int
  Failed: int
  Running: int
  Stale: int
  Disabled: int
  /// Server discovery state wire value: disabled | discovering |
  /// ready_zero_tests | ready_with_tests. Makes a completed zero-test
  /// discovery observable (previously Total=0 was ambiguous between
  /// "discovering", "disabled", and "discovery completed with no tests").
  DiscoveryState: string
  /// Server discovery generation — clients can reject stale summaries.
  DiscoveryGeneration: int64
  LastDecision: VscLiveTestingDecision option
}

module VscLiveTestingDecision =
  let formatHint (decision: VscLiveTestingDecision) =
    match decision.Precision with
    | VscSelectionPrecision.ExactDependencyMatch ->
      sprintf "Why: exact dependency match (%d selected)" decision.SelectedTests.Length
    | VscSelectionPrecision.CoverageApproximation ->
      sprintf "Why: coverage widened the rerun (%d selected)" decision.SelectedTests.Length
    | VscSelectionPrecision.ConservativeFallback ->
      sprintf "Why: conservative fallback rebuild (%d selected)" decision.SelectedTests.Length
    | VscSelectionPrecision.NoImpactedTests ->
      "Why: no impacted tests were identified"
    | VscSelectionPrecision.SuppressedByPolicy ->
      sprintf "Why: run policy deferred %d test(s)" decision.DeferredTests.Length

/// UI change signals — what the TestController adapter needs to update
[<RequireQualifiedAccess>]
type VscStateChange =
  | TestsAdded of VscTestInfo array
  /// A complete rediscovery from a newer generation removed these tests —
  /// the adapter must drop their TestItems and decorations.
  | TestsRemoved of VscTestId array
  | TestsStarted of VscTestId array
  | TestsCompleted of VscTestResult array
  | ResultsStale of VscResultFreshness
  | EnabledChanged of VscLiveTestingEnabled
  | PolicyUpdated of VscTestCategory * VscRunPolicy
  | TimingUpdated of treeSitterMs: float * fcsMs: float * executionMs: float
  | CoverageRefreshed of Map<string, VscFileCoverage>
  | SummaryChanged of VscTestSummary

/// A causal change that may have caused a test failure
type VscCausalChange = {
  Kind: string
  Name: string
}

/// Enriched failure narrative for a test that transitioned Passed→Failed
type VscFailureNarrative = {
  TestId: string
  Summary: string
  TimeSinceLastPass: string
  CausalChanges: VscCausalChange array
}

/// Per-failure diagnosis from the diagnosis_ready SSE event.
/// Carries the test name and the symbols that caused this failure,
/// so clients can render repair CodeLens without additional round-trips.
type VscDiagnosisFailure = {
  TestName: string
  CausalSymbols: string array
}

/// Aggregate live testing state — pure data, no functions
type VscLiveTestState = {
  Tests: Map<VscTestId, VscTestInfo>
  Results: Map<VscTestId, VscTestResult>
  Coverage: Map<string, VscFileCoverage>
  RunningTests: Set<VscTestId>
  Policies: Map<VscTestCategory, VscRunPolicy>
  Enabled: VscLiveTestingEnabled
  LastTiming: (float * float * float) option
  Freshness: VscResultFreshness
  FailureNarratives: Map<string, VscFailureNarrative>
  /// The newest discovery generation applied to this state. A complete
  /// discovery carrying a HIGHER generation replaces Tests/Results; older
  /// or equal generations are ignored (the server already applied them).
  DiscoveryGeneration: int64
}

module VscLiveTestState =
  let empty : VscLiveTestState = {
    Tests = Map.empty
    Results = Map.empty
    Coverage = Map.empty
    RunningTests = Set.empty
    Policies = Map.empty
    Enabled = VscLiveTestingEnabled.LiveTestingOff
    LastTiming = None
    Freshness = VscResultFreshness.Fresh
    FailureNarratives = Map.empty
    DiscoveryGeneration = 0L
  }

  /// Pure fold: event → state → (new state * changes for UI)
  let update (event: VscLiveTestEvent) (state: VscLiveTestState) : VscLiveTestState * VscStateChange list =
    match event with
    | VscLiveTestEvent.TestsDiscovered (tests, isComplete, discoveryGeneration) ->
      // Rediscovery sweep: a COMPLETE batch from a NEWER generation is the
      // authoritative discovery set — tests absent from it were renamed or
      // deleted server-side and must be swept (their TestItems, results, and
      // decorations would otherwise linger as stale). Partial batches (live
      // result streaming) keep merge semantics — sweeping on a partial batch
      // would drop tests that merely have no results in it.
      let isAuthoritative =
        isComplete && discoveryGeneration > state.DiscoveryGeneration
      match isAuthoritative with
      | false ->
        let newTests =
          tests |> Array.fold (fun m t -> Map.add t.Id t m) state.Tests
        { state with Tests = newTests }, [ VscStateChange.TestsAdded tests ]
      | true ->
        let newTests = tests |> Array.fold (fun m t -> Map.add t.Id t m) Map.empty
        let removed =
          state.Tests
          |> Map.keys
          |> Seq.filter (fun id -> not (Map.containsKey id newTests))
          |> Seq.toArray
        // Sweep results for removed tests too — stale results drive stale
        // pass/fail decorations on items that no longer exist.
        let sweptResults =
          if Array.isEmpty removed then state.Results
          else
            let removedSet = removed |> Set.ofArray
            state.Results |> Map.filter (fun id _ -> not (Set.contains id removedSet))
        let changes =
          [ yield VscStateChange.TestsAdded tests
            if not (Array.isEmpty removed) then
              yield VscStateChange.TestsRemoved removed ]
        { state with
            Tests = newTests
            Results = sweptResults
            DiscoveryGeneration = discoveryGeneration }, changes

    | VscLiveTestEvent.TestRunStarted ids ->
      let running = ids |> Set.ofArray
      let results =
        ids |> Array.fold (fun m id ->
          Map.add id { Id = id; Outcome = VscTestOutcome.Running; DurationMs = None; Output = None } m
        ) state.Results
      { state with RunningTests = running; Results = results; Freshness = VscResultFreshness.Fresh },
      [ VscStateChange.TestsStarted ids ]

    | VscLiveTestEvent.TestResultBatch (results, freshness) ->
      let newResults =
        results |> Array.fold (fun m r -> Map.add r.Id r m) state.Results
      let completedIds = results |> Array.map (fun r -> r.Id) |> Set.ofArray
      let stillRunning = Set.difference state.RunningTests completedIds
      let changes = [
        VscStateChange.TestsCompleted results
        match freshness with
        | VscResultFreshness.Fresh -> ()
        | _ -> VscStateChange.ResultsStale freshness
      ]
      { state with Results = newResults; RunningTests = stillRunning; Freshness = freshness },
      changes

    | VscLiveTestEvent.LiveTestingEnabled ->
      { state with Enabled = VscLiveTestingEnabled.LiveTestingOn },
      [ VscStateChange.EnabledChanged VscLiveTestingEnabled.LiveTestingOn ]

    | VscLiveTestEvent.LiveTestingDisabled ->
      { state with Enabled = VscLiveTestingEnabled.LiveTestingOff },
      [ VscStateChange.EnabledChanged VscLiveTestingEnabled.LiveTestingOff ]

    | VscLiveTestEvent.RunPolicyChanged (cat, pol) ->
      { state with Policies = Map.add cat pol state.Policies },
      [ VscStateChange.PolicyUpdated (cat, pol) ]

    | VscLiveTestEvent.TestCycleTimingRecorded (ts, fcs, exec) ->
      { state with LastTiming = Some (ts, fcs, exec) },
      [ VscStateChange.TimingUpdated (ts, fcs, exec) ]

    | VscLiveTestEvent.CoverageUpdated cov ->
      { state with Coverage = cov }, [ VscStateChange.CoverageRefreshed cov ]

  /// Compute test summary from current state
  let summary (state: VscLiveTestState) : VscTestSummary =
    let total = state.Tests.Count
    let (passed, failed, stale0, disabled) =
      state.Results
      |> Map.fold (fun (p, f, s, d) _ r ->
        match r.Outcome with
        | VscTestOutcome.Passed -> (p + 1, f, s, d)
        | VscTestOutcome.Failed _ | VscTestOutcome.Errored _ -> (p, f + 1, s, d)
        | VscTestOutcome.Stale -> (p, f, s + 1, d)
        | VscTestOutcome.PolicyDisabled -> (p, f, s, d + 1)
        | _ -> (p, f, s, d)
      ) (0, 0, 0, 0)
    let stale =
      match state.Freshness with
      | VscResultFreshness.Fresh -> stale0
      | _ ->
        state.Results
        |> Map.filter (fun _ r ->
          match r.Outcome with
          | VscTestOutcome.Running -> false
          | _ -> true)
        |> Map.count
    { Total = total; Passed = passed; Failed = failed
      Running = state.RunningTests.Count; Stale = stale; Disabled = disabled
      DiscoveryState =
        match state.Enabled, total with
        | VscLiveTestingEnabled.LiveTestingOff, _ -> "disabled"
        | _, 0 -> "discovering"
        | _ -> "ready_with_tests"
      DiscoveryGeneration = 0L
      LastDecision = None }

  /// Get tests for a specific file
  let testsForFile (filePath: string) (state: VscLiveTestState) : VscTestInfo list =
    state.Tests
    |> Map.toList
    |> List.choose (fun (_, t) ->
      match t.FilePath with
      | Some fp when fp = filePath -> Some t
      | _ -> None)

  /// Look up a specific test result
  let resultFor (testId: VscTestId) (state: VscLiveTestState) : VscTestResult option =
    Map.tryFind testId state.Results

  /// Look up a failure narrative by test ID string
  let narrativeFor (testIdStr: string) (state: VscLiveTestState) : VscFailureNarrative option =
    Map.tryFind testIdStr state.FailureNarratives

/// Format causal context from a failure narrative into a short inline suffix string.
/// Returns "" when narrative has nothing worth displaying.
/// Examples:
///   "— because validateToken changed (12m ago)"
///   "— validateToken signature changed (unknown)"
///   "— Test started failing after module reload"
let renderNarrativeText (n: VscFailureNarrative) : string =
  let symbols =
    n.CausalChanges
    |> Array.filter (fun c -> c.Kind = "symbol")
    |> Array.map (fun c -> c.Name)
    |> String.concat ", "
  match symbols, n.TimeSinceLastPass with
  | "", "" -> sprintf " — %s" n.Summary
  | "", t  -> sprintf " — %s (%s ago)" n.Summary t
  | s, ""  -> sprintf " — because %s changed" s
  | s, t   -> sprintf " — because %s changed (%s ago)" s t

// --- CoverageView (Fable-side mirror of server-side type) ---
// WHY — kept in the Fable-side LiveTestingTypes rather than in the
// separate CoverageView.fs so the listener state record can carry
// the map directly. The parser (parseCoverageView) lives in
// CoverageView.fs and returns this same shape.

/// Overflow indicator. DU not bool — the renderer needs the exact
/// "hidden" count to render "▾ +N more".
[<RequireQualifiedAccess>]
type VscCoverageOverflow =
  | VscOverflowWithin
  | VscOverflowOf of hidden: int

/// Honest health indicator. Preserves the 5 status kinds so the
/// renderer can show the exact problem (a Stale test is not the same
/// as a Passing test).
[<RequireQualifiedAccess>]
type VscCoverageHealth =
  | VscCoveragePassing
  | VscCoverageFailing
  | VscCoverageRunning
  | VscCoverageStale
  | VscCoverageSkipped
  | VscCoverageAbsent

/// Per-function aggregate. Pre-rendered to one line of text so the
/// editor never iterates per test.
type VscCoverageView = {
  Symbol: string
  FilePath: string
  DefinitionLine: int
  TotalCount: int
  Overflow: VscCoverageOverflow
  /// Pre-formatted single-line badge text (e.g. "✓ 97 ✗ 3").
  /// Editor renders this as one line of virtual text or one CodeLens.
  InlineBadgeText: string
  Health: VscCoverageHealth
}
