namespace SageFs.VisualStudio.Core

open System

/// Test outcome with associated data
[<RequireQualifiedAccess>]
type TestOutcome =
  | Passed of durationMs: float
  | Failed of message: string * durationMs: float option
  | Skipped of reason: string
  | Errored of message: string
  | Running
  | Detected
  | Stale
  | PolicyDisabled

/// Stable test identity across reloads
[<RequireQualifiedAccess>]
type TestId =
  | TestId of string

[<RequireQualifiedAccess>]
module TestId =
  let create (s: string) = TestId.TestId s
  let value (TestId.TestId s) = s

/// Discovered test metadata
type TestInfo = {
  Id: TestId
  DisplayName: string
  FullName: string
  FilePath: string option
  Line: int option
}

/// Result of a single test execution
type TestResult = {
  Id: TestId
  Outcome: TestOutcome
  DurationMs: float option
  Output: string option
}

/// Test summary counts
type TestSummary = {
  Total: int
  Passed: int
  Failed: int
  Running: int
  Stale: int
  Disabled: int
}

/// Whether live testing is enabled
[<RequireQualifiedAccess>]
type LiveTestingEnabled =
  | On
  | Off

/// Result freshness — mirrors daemon's ResultFreshness
[<RequireQualifiedAccess>]
type ResultFreshness =
  | Fresh
  | StaleCodeEdited
  | StaleWrongGeneration

/// Run policy — when tests auto-execute
[<RequireQualifiedAccess>]
type RunPolicy =
  | EveryKeystroke
  | OnSave
  | OnDemand
  | Disabled

/// Test category — determines default run policy
[<RequireQualifiedAccess>]
type TestCategory =
  | Unit
  | Integration
  | Browser
  | Benchmark
  | Architecture
  | Property

/// SSE events from the SageFs server
[<RequireQualifiedAccess>]
type LiveTestEvent =
  | TestsDiscovered of tests: TestInfo array
  | TestRunStarted of testIds: TestId array
  | TestResultBatch of results: TestResult array * freshness: ResultFreshness
  | LiveTestingEnabled
  | LiveTestingDisabled
  | SummaryUpdated of summary: TestSummary
  | RunPolicyChanged of category: TestCategory * policy: RunPolicy

/// Change signals — what the tool window adapter needs to update
[<RequireQualifiedAccess>]
type LiveTestChange =
  | TestsDiscovered of TestInfo array
  | TestsUpdated of TestResult array
  | EnabledChanged of LiveTestingEnabled
  | SummaryChanged of TestSummary
  | ResultsStale of ResultFreshness
  | PolicyUpdated of TestCategory * RunPolicy

/// Aggregate live testing state
type LiveTestState = {
  Tests: Map<TestId, TestInfo>
  Results: Map<TestId, TestResult>
  RunningTests: Set<TestId>
  Enabled: LiveTestingEnabled
  LastSummary: TestSummary option
  Freshness: ResultFreshness
  Policies: Map<TestCategory, RunPolicy>
}

[<RequireQualifiedAccess>]
module LiveTestState =
  let empty: LiveTestState = {
    Tests = Map.empty
    Results = Map.empty
    RunningTests = Set.empty
    Enabled = LiveTestingEnabled.Off
    LastSummary = None
    Freshness = ResultFreshness.Fresh
    Policies = Map.empty
  }

  /// Pure fold returning (state, changes) — imperative subscriber becomes thin adapter
  let update (event: LiveTestEvent) (state: LiveTestState) : LiveTestState * LiveTestChange list =
    match event with
    | LiveTestEvent.TestsDiscovered tests ->
      let newTests =
        tests |> Array.fold (fun m t -> Map.add t.Id t m) state.Tests
      { state with Tests = newTests },
      [ LiveTestChange.TestsDiscovered tests ]

    | LiveTestEvent.TestRunStarted ids ->
      let running = ids |> Set.ofArray
      let results =
        ids |> Array.fold (fun m id ->
          Map.add id { Id = id; Outcome = TestOutcome.Running; DurationMs = None; Output = None } m
        ) state.Results
      { state with RunningTests = running; Results = results; Freshness = ResultFreshness.Fresh }, []

    | LiveTestEvent.TestResultBatch (results, freshness) ->
      let newResults =
        results |> Array.fold (fun m r -> Map.add r.Id r m) state.Results
      let completedIds = results |> Array.map _.Id |> Set.ofArray
      let stillRunning = Set.difference state.RunningTests completedIds
      let changes = [
        LiveTestChange.TestsUpdated results
        if freshness <> ResultFreshness.Fresh then
          LiveTestChange.ResultsStale freshness
      ]
      { state with Results = newResults; RunningTests = stillRunning; Freshness = freshness },
      changes

    | LiveTestEvent.LiveTestingEnabled ->
      { state with Enabled = LiveTestingEnabled.On },
      [ LiveTestChange.EnabledChanged LiveTestingEnabled.On ]

    | LiveTestEvent.LiveTestingDisabled ->
      { state with Enabled = LiveTestingEnabled.Off },
      [ LiveTestChange.EnabledChanged LiveTestingEnabled.Off ]

    | LiveTestEvent.SummaryUpdated summary ->
      { state with LastSummary = Some summary },
      [ LiveTestChange.SummaryChanged summary ]

    | LiveTestEvent.RunPolicyChanged (cat, pol) ->
      { state with Policies = Map.add cat pol state.Policies },
      [ LiveTestChange.PolicyUpdated (cat, pol) ]

  let summary (state: LiveTestState) =
    match state.LastSummary with
    | Some s -> s
    | None ->
      let mutable passed = 0
      let mutable failed = 0
      let mutable stale = 0
      let mutable disabled = 0
      state.Results |> Map.iter (fun _ r ->
        match r.Outcome with
        | TestOutcome.Passed _ -> passed <- passed + 1
        | TestOutcome.Failed _ | TestOutcome.Errored _ -> failed <- failed + 1
        | TestOutcome.Stale -> stale <- stale + 1
        | TestOutcome.PolicyDisabled -> disabled <- disabled + 1
        | _ -> ())
      let stale =
        if state.Freshness <> ResultFreshness.Fresh then
          state.Results
          |> Map.filter (fun _ r ->
            match r.Outcome with TestOutcome.Running -> false | _ -> true)
          |> Map.count
        else stale
      { Total = state.Tests.Count; Passed = passed; Failed = failed
        Running = state.RunningTests.Count; Stale = stale; Disabled = disabled }

  let testsForFile (filePath: string) (state: LiveTestState) =
    state.Tests
    |> Map.toList
    |> List.choose (fun (_, t) ->
      match t.FilePath with
      | Some fp when fp = filePath -> Some t
      | _ -> None)

  let resultFor (testId: TestId) (state: LiveTestState) =
    Map.tryFind testId state.Results

  let isEnabled (state: LiveTestState) =
    match state.Enabled with
    | LiveTestingEnabled.On -> true
    | LiveTestingEnabled.Off -> false

  let allTests (state: LiveTestState) =
    state.Tests
    |> Map.toArray
    |> Array.map (fun (_, info) ->
      let result = Map.tryFind info.Id state.Results
      struct (info, result))
