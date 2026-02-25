module SageFs.Vscode.LiveTestingTypes

/// Test outcome — replaces boolean pass/fail with domain meaning
[<RequireQualifiedAccess>]
type VscTestOutcome =
  | Passed
  | Failed of message: string
  | Skipped of reason: string
  | Running
  | Errored of message: string

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

/// SSE events from the SageFs server
[<RequireQualifiedAccess>]
type VscLiveTestEvent =
  | TestsDiscovered of tests: VscTestInfo array
  | TestRunStarted of testIds: VscTestId array
  | TestResultBatch of results: VscTestResult array
  | LiveTestingToggled of enabled: bool
  | RunPolicyChanged of category: VscTestCategory * policy: VscRunPolicy
  | PipelineTimingRecorded of treeSitterMs: float * fcsMs: float * executionMs: float
  | CoverageUpdated of coverage: Map<string, VscFileCoverage>

/// UI change signals — what the TestController adapter needs to update
[<RequireQualifiedAccess>]
type VscStateChange =
  | TestsAdded of VscTestInfo array
  | TestsStarted of VscTestId array
  | TestsCompleted of VscTestResult array
  | EnabledChanged of VscLiveTestingEnabled
  | PolicyUpdated of VscTestCategory * VscRunPolicy
  | TimingUpdated of treeSitterMs: float * fcsMs: float * executionMs: float
  | CoverageRefreshed of Map<string, VscFileCoverage>

/// Test summary counts
type VscTestSummary = {
  Total: int
  Passed: int
  Failed: int
  Running: int
  Stale: int
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
      { state with RunningTests = running; Results = results },
      [ VscStateChange.TestsStarted ids ]

    | VscLiveTestEvent.TestResultBatch results ->
      let newResults =
        results |> Array.fold (fun m r -> Map.add r.Id r m) state.Results
      let completedIds = results |> Array.map (fun r -> r.Id) |> Set.ofArray
      let stillRunning = Set.difference state.RunningTests completedIds
      { state with Results = newResults; RunningTests = stillRunning },
      [ VscStateChange.TestsCompleted results ]

    | VscLiveTestEvent.LiveTestingToggled enabled ->
      let flag =
        if enabled then VscLiveTestingEnabled.LiveTestingOn
        else VscLiveTestingEnabled.LiveTestingOff
      { state with Enabled = flag }, [ VscStateChange.EnabledChanged flag ]

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
    let running = state.RunningTests.Count
    state.Results |> Map.iter (fun _ r ->
      match r.Outcome with
      | VscTestOutcome.Passed -> passed <- passed + 1
      | VscTestOutcome.Failed _ | VscTestOutcome.Errored _ -> failed <- failed + 1
      | _ -> ())
    { Total = total; Passed = passed; Failed = failed; Running = running; Stale = 0 }

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
