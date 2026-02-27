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

/// Whether coverage is healthy — replaces bool
[<RequireQualifiedAccess>]
type VscCoverageHealth =
  | AllPassing
  | SomeFailing

/// Per-line coverage status
[<RequireQualifiedAccess>]
type VscLineCoverage =
  | Covered of testCount: int * health: VscCoverageHealth
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
  | TestsDiscovered of tests: VscTestInfo array
  | TestRunStarted of testIds: VscTestId array
  | TestResultBatch of results: VscTestResult array * freshness: VscResultFreshness
  | LiveTestingEnabled
  | LiveTestingDisabled
  | RunPolicyChanged of category: VscTestCategory * policy: VscRunPolicy
  | PipelineTimingRecorded of treeSitterMs: float * fcsMs: float * executionMs: float
  | CoverageUpdated of coverage: Map<string, VscFileCoverage>

/// Test summary counts
type VscTestSummary = {
  Total: int
  Passed: int
  Failed: int
  Running: int
  Stale: int
  Disabled: int
}

/// UI change signals — what the TestController adapter needs to update
[<RequireQualifiedAccess>]
type VscStateChange =
  | TestsAdded of VscTestInfo array
  | TestsStarted of VscTestId array
  | TestsCompleted of VscTestResult array
  | ResultsStale of VscResultFreshness
  | EnabledChanged of VscLiveTestingEnabled
  | PolicyUpdated of VscTestCategory * VscRunPolicy
  | TimingUpdated of treeSitterMs: float * fcsMs: float * executionMs: float
  | CoverageRefreshed of Map<string, VscFileCoverage>
  | SummaryChanged of VscTestSummary

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
  }

  /// Pure fold: event → state → (new state * changes for UI)
  let update (event: VscLiveTestEvent) (state: VscLiveTestState) : VscLiveTestState * VscStateChange list =
    match event with
    | VscLiveTestEvent.TestsDiscovered tests ->
      let newTests =
        tests |> Array.fold (fun m t -> Map.add t.Id t m) state.Tests
      { state with Tests = newTests }, [ VscStateChange.TestsAdded tests ]

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
        if freshness <> VscResultFreshness.Fresh then
          VscStateChange.ResultsStale freshness
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

    | VscLiveTestEvent.PipelineTimingRecorded (ts, fcs, exec) ->
      { state with LastTiming = Some (ts, fcs, exec) },
      [ VscStateChange.TimingUpdated (ts, fcs, exec) ]

    | VscLiveTestEvent.CoverageUpdated cov ->
      { state with Coverage = cov }, [ VscStateChange.CoverageRefreshed cov ]

  /// Compute test summary from current state
  let summary (state: VscLiveTestState) : VscTestSummary =
    let total = state.Tests.Count
    let mutable passed = 0
    let mutable failed = 0
    let mutable stale = 0
    let mutable disabled = 0
    state.Results |> Map.iter (fun _ r ->
      match r.Outcome with
      | VscTestOutcome.Passed -> passed <- passed + 1
      | VscTestOutcome.Failed _ | VscTestOutcome.Errored _ -> failed <- failed + 1
      | VscTestOutcome.Stale -> stale <- stale + 1
      | VscTestOutcome.PolicyDisabled -> disabled <- disabled + 1
      | _ -> ())
    let stale =
      if state.Freshness <> VscResultFreshness.Fresh then
        state.Results
        |> Map.filter (fun _ r ->
          match r.Outcome with
          | VscTestOutcome.Running -> false
          | _ -> true)
        |> Map.count
      else stale
    { Total = total; Passed = passed; Failed = failed
      Running = state.RunningTests.Count; Stale = stale; Disabled = disabled }

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
