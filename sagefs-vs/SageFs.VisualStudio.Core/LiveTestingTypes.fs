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
}

/// Whether live testing is enabled
[<RequireQualifiedAccess>]
type LiveTestingEnabled =
  | On
  | Off

/// SSE events from the SageFs server
[<RequireQualifiedAccess>]
type LiveTestEvent =
  | TestsDiscovered of tests: TestInfo array
  | TestRunStarted of testIds: TestId array
  | TestResultBatch of results: TestResult array
  | LiveTestingToggled of enabled: bool
  | SummaryUpdated of summary: TestSummary

/// Aggregate live testing state
type LiveTestState = {
  Tests: Map<TestId, TestInfo>
  Results: Map<TestId, TestResult>
  RunningTests: Set<TestId>
  Enabled: LiveTestingEnabled
  LastSummary: TestSummary option
}

[<RequireQualifiedAccess>]
module LiveTestState =
  let empty: LiveTestState = {
    Tests = Map.empty
    Results = Map.empty
    RunningTests = Set.empty
    Enabled = LiveTestingEnabled.Off
    LastSummary = None
  }

  let update (event: LiveTestEvent) (state: LiveTestState) =
    match event with
    | LiveTestEvent.TestsDiscovered tests ->
      let newTests =
        tests |> Array.fold (fun m t -> Map.add t.Id t m) state.Tests
      { state with Tests = newTests }
    | LiveTestEvent.TestRunStarted ids ->
      let running = ids |> Set.ofArray
      let results =
        ids |> Array.fold (fun m id ->
          Map.add id { Id = id; Outcome = TestOutcome.Running; DurationMs = None; Output = None } m
        ) state.Results
      { state with RunningTests = running; Results = results }
    | LiveTestEvent.TestResultBatch results ->
      let newResults =
        results |> Array.fold (fun m r -> Map.add r.Id r m) state.Results
      let completedIds = results |> Array.map _.Id |> Set.ofArray
      let stillRunning = Set.difference state.RunningTests completedIds
      { state with Results = newResults; RunningTests = stillRunning }
    | LiveTestEvent.LiveTestingToggled enabled ->
      { state with Enabled = if enabled then LiveTestingEnabled.On else LiveTestingEnabled.Off }
    | LiveTestEvent.SummaryUpdated summary ->
      { state with LastSummary = Some summary }

  let summary (state: LiveTestState) =
    match state.LastSummary with
    | Some s -> s
    | None ->
      let mutable passed = 0
      let mutable failed = 0
      state.Results |> Map.iter (fun _ r ->
        match r.Outcome with
        | TestOutcome.Passed _ -> passed <- passed + 1
        | TestOutcome.Failed _ | TestOutcome.Errored _ -> failed <- failed + 1
        | _ -> ())
      { Total = state.Tests.Count; Passed = passed; Failed = failed
        Running = state.RunningTests.Count; Stale = 0 }

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
