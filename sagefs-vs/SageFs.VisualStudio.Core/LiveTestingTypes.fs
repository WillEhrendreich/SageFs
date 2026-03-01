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

[<RequireQualifiedAccess>]
module TestSummary =
  /// Format for VS tool window header. Returns (text, severity).
  let formatToolWindowLine (s: TestSummary) =
    let parts = [
      if s.Passed > 0 then sprintf "✓ %d passed" s.Passed
      if s.Failed > 0 then sprintf "✗ %d failed" s.Failed
      if s.Stale > 0 then sprintf "◌ %d stale" s.Stale
      if s.Disabled > 0 then sprintf "⊘ %d disabled" s.Disabled
      if s.Running > 0 then sprintf "● %d running" s.Running
    ]
    let text =
      if parts.IsEmpty then sprintf "%d tests" s.Total
      else sprintf "%d tests: %s" s.Total (parts |> String.concat ", ")
    let severity =
      if s.Failed > 0 then "error"
      elif s.Stale > 0 then "warning"
      else "info"
    text, severity

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

/// Coverage health — whether all covering tests pass
[<RequireQualifiedAccess>]
type CoverageHealth =
  | AllPassing
  | SomeFailing

/// Coverage status for a single line
[<RequireQualifiedAccess>]
type CoverageStatus =
  | Covered of testCount: int * health: CoverageHealth
  | NotCovered
  | Pending

/// Line-level coverage detail
[<RequireQualifiedAccess>]
type LineCoverage =
  | FullyCovered
  | PartiallyCovered of covered: int * total: int
  | NotCovered

/// Coverage annotation for a single source line
type CoverageLineAnnotation = {
  Line: int
  EndLine: int
  EndColumn: int
  Detail: CoverageStatus
  CoveringTestIds: string array
  BranchCoverage: LineCoverage option
}

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

/// SSE feature events from the SageFs server
[<RequireQualifiedAccess>]
type FeatureEvent =
  | EvalDiff of EvalDiffInfo
  | CellGraph of CellGraphInfo
  | BindingScope of BindingScopeInfo
  | Timeline of TimelineStatsInfo

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

/// Filter for test status in the tree view
[<RequireQualifiedAccess>]
type TestStatusFilter =
  | All
  | FailedOnly
  | RunningOnly
  | StaleOnly
  | PassedOnly

/// Tests grouped by file
type TestFileGroup = {
  FilePath: string
  Tests: (TestInfo * TestResult option) array
}

/// Pure ViewModel functions for the test tree panel
[<RequireQualifiedAccess>]
module TestTreeViewModel =

  let private outcomeIcon (outcome: TestOutcome) =
    match outcome with
    | TestOutcome.Passed _ -> "✓"
    | TestOutcome.Failed _ | TestOutcome.Errored _ -> "✗"
    | TestOutcome.Running -> "●"
    | TestOutcome.Stale -> "◌"
    | TestOutcome.Detected -> "○"
    | TestOutcome.Skipped _ -> "⊘"
    | TestOutcome.PolicyDisabled -> "⊘"

  let private outcomeOrder (outcome: TestOutcome) =
    match outcome with
    | TestOutcome.Failed _ | TestOutcome.Errored _ -> 0
    | TestOutcome.Running -> 1
    | TestOutcome.Stale -> 2
    | TestOutcome.Detected -> 3
    | TestOutcome.Passed _ -> 4
    | TestOutcome.Skipped _ -> 5
    | TestOutcome.PolicyDisabled -> 6

  let private formatDuration (ms: float option) =
    match ms with
    | None -> ""
    | Some d when d < 1.0 -> "(<1ms)"
    | Some d when d < 1000.0 -> sprintf "(%.0fms)" d
    | Some d -> sprintf "(%.1fs)" (d / 1000.0)

  /// Group tests by file path, sorted by filename
  let groupByFile (state: LiveTestState) : TestFileGroup array =
    state.Tests
    |> Map.toArray
    |> Array.map (fun (_, info) ->
      let result = Map.tryFind info.Id state.Results
      let path = info.FilePath |> Option.defaultValue "(no file)"
      path, (info, result))
    |> Array.groupBy fst
    |> Array.map (fun (path, items) ->
      { FilePath = path; Tests = items |> Array.map snd })
    |> Array.sortBy (fun g -> IO.Path.GetFileName g.FilePath)

  /// Filter groups by status
  let filterGroups (filter: TestStatusFilter) (groups: TestFileGroup array) : TestFileGroup array =
    match filter with
    | TestStatusFilter.All -> groups
    | _ ->
      let predicate (_, result: TestResult option) =
        match filter, result with
        | TestStatusFilter.FailedOnly, Some r ->
          match r.Outcome with TestOutcome.Failed _ | TestOutcome.Errored _ -> true | _ -> false
        | TestStatusFilter.RunningOnly, Some r ->
          match r.Outcome with TestOutcome.Running -> true | _ -> false
        | TestStatusFilter.StaleOnly, Some r ->
          match r.Outcome with TestOutcome.Stale -> true | _ -> false
        | TestStatusFilter.PassedOnly, Some r ->
          match r.Outcome with TestOutcome.Passed _ -> true | _ -> false
        | _ -> false
      groups
      |> Array.choose (fun g ->
        let filtered = g.Tests |> Array.filter predicate
        if filtered.Length > 0 then Some { g with Tests = filtered }
        else None)

  /// Search groups by text (case-insensitive on DisplayName and FullName)
  let searchGroups (query: string) (groups: TestFileGroup array) : TestFileGroup array =
    if String.IsNullOrWhiteSpace query then groups
    else
      let q = query.ToLowerInvariant()
      groups
      |> Array.choose (fun g ->
        let filtered =
          g.Tests |> Array.filter (fun (info, _) ->
            info.DisplayName.ToLowerInvariant().Contains(q) ||
            info.FullName.ToLowerInvariant().Contains(q))
        if filtered.Length > 0 then Some { g with Tests = filtered }
        else None)

  /// Sort tests within a group: failures first, then running, then passed
  let sortTests (tests: (TestInfo * TestResult option) array) =
    tests |> Array.sortBy (fun (info, result) ->
      let order =
        match result with
        | Some r -> outcomeOrder r.Outcome
        | None -> 3
      order, info.DisplayName)

  /// Format a file group header line
  let formatGroupHeader (group: TestFileGroup) =
    let fileName = IO.Path.GetFileName group.FilePath
    let total = group.Tests.Length
    let passed =
      group.Tests
      |> Array.filter (fun (_, r) ->
        match r with Some { Outcome = TestOutcome.Passed _ } -> true | _ -> false)
      |> Array.length
    let failed =
      group.Tests
      |> Array.filter (fun (_, r) ->
        match r with
        | Some { Outcome = TestOutcome.Failed _ }
        | Some { Outcome = TestOutcome.Errored _ } -> true
        | _ -> false)
      |> Array.length
    if failed > 0 then
      sprintf "✗ %s (%d/%d passed, %d failed)" fileName passed total failed
    else
      sprintf "✓ %s (%d/%d passed)" fileName passed total

  /// Format a single test line
  let formatTestLine (info: TestInfo) (result: TestResult option) =
    let icon =
      match result with
      | Some r -> outcomeIcon r.Outcome
      | None -> "○"
    let duration =
      match result with
      | Some r -> formatDuration r.DurationMs
      | None -> ""
    if duration = "" then sprintf "%s %s" icon info.DisplayName
    else sprintf "%s %s %s" icon info.DisplayName duration

  /// Filter label for button text
  let filterLabel (filter: TestStatusFilter) =
    match filter with
    | TestStatusFilter.All -> "All"
    | TestStatusFilter.FailedOnly -> "Failed"
    | TestStatusFilter.RunningOnly -> "Running"
    | TestStatusFilter.StaleOnly -> "Stale"
    | TestStatusFilter.PassedOnly -> "Passed"

  /// Cycle to next filter
  let nextFilter (filter: TestStatusFilter) =
    match filter with
    | TestStatusFilter.All -> TestStatusFilter.FailedOnly
    | TestStatusFilter.FailedOnly -> TestStatusFilter.RunningOnly
    | TestStatusFilter.RunningOnly -> TestStatusFilter.StaleOnly
    | TestStatusFilter.StaleOnly -> TestStatusFilter.PassedOnly
    | TestStatusFilter.PassedOnly -> TestStatusFilter.All

  /// Produce the complete formatted output for the text-based tool window
  let formatGroupedOutput (filter: TestStatusFilter) (search: string) (state: LiveTestState) =
    if state.Tests.Count = 0 then "No tests discovered yet."
    else
      let groups =
        groupByFile state
        |> filterGroups filter
        |> searchGroups search
      if groups.Length = 0 then
        sprintf "No tests match filter '%s'%s"
          (filterLabel filter)
          (if String.IsNullOrWhiteSpace search then ""
           else sprintf " and search '%s'" search)
      else
        let sb = Text.StringBuilder()
        for group in groups do
          sb.AppendLine(formatGroupHeader group) |> ignore
          let sorted = sortTests group.Tests
          for (info, result) in sorted do
            sb.Append("  ").AppendLine(formatTestLine info result) |> ignore
          sb.AppendLine() |> ignore
        sb.ToString().TrimEnd()
