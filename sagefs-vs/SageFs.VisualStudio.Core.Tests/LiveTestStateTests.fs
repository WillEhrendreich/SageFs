module SageFs.VisualStudio.Core.Tests.LiveTestStateTests

open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

// -- helpers ---------------------------------------------------------------

let private mkInfo id name file line = {
  Id = TestId.create id
  DisplayName = name
  FullName = "Module." + name
  FilePath = file
  Line = line
}

let private mkResult id outcome durationMs = {
  Id = TestId.create id
  Outcome = outcome
  DurationMs = durationMs
  Output = None
}

// -- LiveTestState.empty ---------------------------------------------------

[<Fact>]
let ``empty state has Enabled Off`` () =
  LiveTestState.empty.Enabled |> should equal LiveTestingEnabled.Off

[<Fact>]
let ``empty state has no tests`` () =
  LiveTestState.empty.Tests |> should be Empty

[<Fact>]
let ``empty state has no results`` () =
  LiveTestState.empty.Results |> should be Empty

[<Fact>]
let ``empty state has no running tests`` () =
  LiveTestState.empty.RunningTests |> should be Empty

[<Fact>]
let ``empty state has Fresh freshness`` () =
  LiveTestState.empty.Freshness |> should equal ResultFreshness.Fresh

[<Fact>]
let ``empty state has no last summary`` () =
  LiveTestState.empty.LastSummary |> should equal None

[<Fact>]
let ``empty state has no policies`` () =
  LiveTestState.empty.Policies |> should be Empty

// -- LiveTestState.update: enable/disable ----------------------------------

[<Fact>]
let ``update LiveTestingEnabled sets Enabled On`` () =
  let state, _ = LiveTestState.update LiveTestEvent.LiveTestingEnabled LiveTestState.empty
  state.Enabled |> should equal LiveTestingEnabled.On

[<Fact>]
let ``update LiveTestingEnabled returns EnabledChanged On change`` () =
  let _, changes = LiveTestState.update LiveTestEvent.LiveTestingEnabled LiveTestState.empty
  changes |> should equal [ LiveTestChange.EnabledChanged LiveTestingEnabled.On ]

[<Fact>]
let ``update LiveTestingDisabled sets Enabled Off`` () =
  let enabled = { LiveTestState.empty with Enabled = LiveTestingEnabled.On }
  let state, _ = LiveTestState.update LiveTestEvent.LiveTestingDisabled enabled
  state.Enabled |> should equal LiveTestingEnabled.Off

[<Fact>]
let ``update LiveTestingDisabled returns EnabledChanged Off change`` () =
  let enabled = { LiveTestState.empty with Enabled = LiveTestingEnabled.On }
  let _, changes = LiveTestState.update LiveTestEvent.LiveTestingDisabled enabled
  changes |> should equal [ LiveTestChange.EnabledChanged LiveTestingEnabled.Off ]

// -- LiveTestState.update: TestsDiscovered ---------------------------------

[<Fact>]
let ``update TestsDiscovered adds test to Tests map`` () =
  let info = mkInfo "id-1" "my test" (Some "Test.fs") (Some 10)
  let state, _ = LiveTestState.update (LiveTestEvent.TestsDiscovered [| info |]) LiveTestState.empty
  state.Tests |> Map.containsKey (TestId.create "id-1") |> should equal true

[<Fact>]
let ``update TestsDiscovered returns TestsDiscovered change with correct count`` () =
  let info = mkInfo "id-1" "my test" (Some "Test.fs") (Some 10)
  let _, changes = LiveTestState.update (LiveTestEvent.TestsDiscovered [| info |]) LiveTestState.empty
  match changes with
  | [ LiveTestChange.TestsDiscovered arr ] -> arr.Length |> should equal 1
  | _ -> failwith "expected single TestsDiscovered change"

[<Fact>]
let ``update TestsDiscovered preserves existing tests`` () =
  let info1 = mkInfo "id-1" "test 1" (Some "Test.fs") (Some 1)
  let info2 = mkInfo "id-2" "test 2" (Some "Test.fs") (Some 2)
  let state0 = { LiveTestState.empty with Tests = Map.ofList [ (info1.Id, info1) ] }
  let state1, _ = LiveTestState.update (LiveTestEvent.TestsDiscovered [| info2 |]) state0
  state1.Tests.Count |> should equal 2

// -- LiveTestState.update: TestResultBatch ---------------------------------

[<Fact>]
let ``update TestResultBatch adds result to Results map`` () =
  let result = mkResult "id-1" (TestOutcome.Passed 10.0) (Some 10.0)
  let state, _ =
    LiveTestState.update
      (LiveTestEvent.TestResultBatch([| result |], ResultFreshness.Fresh))
      LiveTestState.empty
  state.Results |> Map.containsKey (TestId.create "id-1") |> should equal true

[<Fact>]
let ``update TestResultBatch returns TestsUpdated change`` () =
  let result = mkResult "id-1" (TestOutcome.Passed 10.0) (Some 10.0)
  let _, changes =
    LiveTestState.update
      (LiveTestEvent.TestResultBatch([| result |], ResultFreshness.Fresh))
      LiveTestState.empty
  changes
  |> List.exists (function LiveTestChange.TestsUpdated _ -> true | _ -> false)
  |> should equal true

[<Fact>]
let ``update TestResultBatch with unknown test ID does not throw KeyNotFoundException`` () =
  // CRITICAL: unknown IDs must not cause exceptions — tests that race with discovery
  let result = mkResult "unknown-id" (TestOutcome.Passed 5.0) (Some 5.0)
  let state, _ =
    LiveTestState.update
      (LiveTestEvent.TestResultBatch([| result |], ResultFreshness.Fresh))
      LiveTestState.empty
  state.Results |> Map.containsKey (TestId.create "unknown-id") |> should equal true

[<Fact>]
let ``update TestResultBatch with stale freshness returns ResultsStale change`` () =
  let result = mkResult "id-1" (TestOutcome.Stale) None
  let _, changes =
    LiveTestState.update
      (LiveTestEvent.TestResultBatch([| result |], ResultFreshness.StaleCodeEdited))
      LiveTestState.empty
  changes
  |> List.exists (function LiveTestChange.ResultsStale _ -> true | _ -> false)
  |> should equal true

[<Fact>]
let ``update TestResultBatch removes completed IDs from RunningTests`` () =
  let tid = TestId.create "id-1"
  let running = { LiveTestState.empty with RunningTests = Set.ofList [ tid ] }
  let result = mkResult "id-1" (TestOutcome.Passed 5.0) (Some 5.0)
  let state, _ =
    LiveTestState.update
      (LiveTestEvent.TestResultBatch([| result |], ResultFreshness.Fresh))
      running
  state.RunningTests |> Set.contains tid |> should equal false

// -- LiveTestState.update: SummaryUpdated ----------------------------------

[<Fact>]
let ``update SummaryUpdated sets LastSummary`` () =
  let summary = { Total = 5; Passed = 3; Failed = 1; Running = 0; Stale = 1; Disabled = 0; LastDecision = None }
  let state, _ = LiveTestState.update (LiveTestEvent.SummaryUpdated summary) LiveTestState.empty
  state.LastSummary |> should equal (Some summary)

[<Fact>]
let ``update SummaryUpdated returns SummaryChanged change`` () =
  let summary = { Total = 5; Passed = 3; Failed = 1; Running = 0; Stale = 1; Disabled = 0; LastDecision = None }
  let _, changes = LiveTestState.update (LiveTestEvent.SummaryUpdated summary) LiveTestState.empty
  changes |> should equal [ LiveTestChange.SummaryChanged summary ]

// -- LiveTestState.update: RunPolicyChanged --------------------------------

[<Fact>]
let ``update RunPolicyChanged adds policy to Policies map`` () =
  let state, _ =
    LiveTestState.update
      (LiveTestEvent.RunPolicyChanged(TestCategory.Unit, RunPolicy.EveryKeystroke))
      LiveTestState.empty
  state.Policies |> Map.containsKey TestCategory.Unit |> should equal true
  state.Policies.[TestCategory.Unit] |> should equal RunPolicy.EveryKeystroke

[<Fact>]
let ``update RunPolicyChanged returns PolicyUpdated change`` () =
  let _, changes =
    LiveTestState.update
      (LiveTestEvent.RunPolicyChanged(TestCategory.Unit, RunPolicy.EveryKeystroke))
      LiveTestState.empty
  changes |> should equal [ LiveTestChange.PolicyUpdated(TestCategory.Unit, RunPolicy.EveryKeystroke) ]

// -- sequential updates ----------------------------------------------------

[<Fact>]
let ``multiple events applied sequentially produce correct cumulative state`` () =
  let info = mkInfo "id-1" "my test" (Some "Test.fs") (Some 10)
  let result = mkResult "id-1" (TestOutcome.Passed 20.0) (Some 20.0)
  let state0 = LiveTestState.empty
  let state1, _ = LiveTestState.update LiveTestEvent.LiveTestingEnabled state0
  let state2, _ = LiveTestState.update (LiveTestEvent.TestsDiscovered [| info |]) state1
  let state3, _ =
    LiveTestState.update
      (LiveTestEvent.TestResultBatch([| result |], ResultFreshness.Fresh))
      state2
  state3.Enabled |> should equal LiveTestingEnabled.On
  state3.Tests.Count |> should equal 1
  state3.Results.Count |> should equal 1

// -- LiveTestState.testsForFile --------------------------------------------

[<Fact>]
let ``testsForFile returns only tests matching the given file path`` () =
  let info1 = mkInfo "id-1" "test 1" (Some "File1.fs") (Some 1)
  let info2 = mkInfo "id-2" "test 2" (Some "File2.fs") (Some 2)
  let info3 = mkInfo "id-3" "test 3" (Some "File1.fs") (Some 3)
  let state = {
    LiveTestState.empty with
      Tests = Map.ofList [ (info1.Id, info1); (info2.Id, info2); (info3.Id, info3) ]
  }
  let result = LiveTestState.testsForFile "File1.fs" state
  result.Length |> should equal 2
  result |> List.forall (fun t -> t.FilePath = Some "File1.fs") |> should equal true

[<Fact>]
let ``testsForFile returns empty list when no tests match the file`` () =
  let info = mkInfo "id-1" "test 1" (Some "File1.fs") (Some 1)
  let state = { LiveTestState.empty with Tests = Map.ofList [ (info.Id, info) ] }
  LiveTestState.testsForFile "File2.fs" state |> should be Empty

// -- LiveTestState.resultFor -----------------------------------------------

[<Fact>]
let ``resultFor returns None for unknown test ID without throwing`` () =
  LiveTestState.resultFor (TestId.create "no-such-id") LiveTestState.empty
  |> should equal None

[<Fact>]
let ``resultFor returns Some result when test ID exists`` () =
  let r = mkResult "id-1" (TestOutcome.Passed 5.0) (Some 5.0)
  let state = { LiveTestState.empty with Results = Map.ofList [ (TestId.create "id-1", r) ] }
  LiveTestState.resultFor (TestId.create "id-1") state |> should equal (Some r)

// -- TestSummary.formatToolWindowLine --------------------------------------

[<Fact>]
let ``formatToolWindowLine with all passed returns info severity`` () =
  let s = { Total = 5; Passed = 5; Failed = 0; Running = 0; Stale = 0; Disabled = 0; LastDecision = None }
  let _, severity = TestSummary.formatToolWindowLine s
  severity |> should equal "info"

[<Fact>]
let ``formatToolWindowLine with failures returns error severity`` () =
  let s = { Total = 5; Passed = 4; Failed = 1; Running = 0; Stale = 0; Disabled = 0; LastDecision = None }
  let _, severity = TestSummary.formatToolWindowLine s
  severity |> should equal "error"

[<Fact>]
let ``formatToolWindowLine with stale but no failures returns warning severity`` () =
  let s = { Total = 5; Passed = 4; Failed = 0; Running = 0; Stale = 1; Disabled = 0; LastDecision = None }
  let _, severity = TestSummary.formatToolWindowLine s
  severity |> should equal "warning"

[<Fact>]
let ``formatToolWindowLine with all passed shows passed count in text`` () =
  let s = { Total = 5; Passed = 5; Failed = 0; Running = 0; Stale = 0; Disabled = 0; LastDecision = None }
  let text, _ = TestSummary.formatToolWindowLine s
  text |> should haveSubstring "5 passed"

[<Fact>]
let ``formatToolWindowLine with no results shows just total`` () =
  let s = { Total = 3; Passed = 0; Failed = 0; Running = 0; Stale = 0; Disabled = 0; LastDecision = None }
  let text, _ = TestSummary.formatToolWindowLine s
  text |> should equal "3 tests"

[<Fact>]
let ``formatToolWindowLine mixed summary shows both failed and stale in text`` () =
  let s = { Total = 5; Passed = 2; Failed = 2; Running = 0; Stale = 1; Disabled = 0; LastDecision = None }
  let text, _ = TestSummary.formatToolWindowLine s
  text |> should haveSubstring "failed"
  text |> should haveSubstring "stale"

// -- LiveTestState.summary -------------------------------------------------

[<Fact>]
let ``summary returns LastSummary when it is set`` () =
  let expected = { Total = 10; Passed = 8; Failed = 2; Running = 0; Stale = 0; Disabled = 0; LastDecision = None }
  let state = { LiveTestState.empty with LastSummary = Some expected }
  LiveTestState.summary state |> should equal expected

[<Fact>]
let ``formatToolWindowLine includes last decision explanation when available`` () =
  let decision =
    { Cause = RerunCause.FileSaved
      FilePath = "src/Compiled.fs"
      Precision = SelectionPrecision.ConservativeFallback
      Trust = FreshnessTrust.FreshApproximate
      ChangedSymbols = [||]
      SelectedTests = [| "Compiled.Tests.should_build_a" |]
      DeferredTests = [||]
      Reason = "fallback rebuild" }
  let summary = { Total = 1; Passed = 0; Failed = 0; Running = 0; Stale = 1; Disabled = 0; LastDecision = Some decision }
  let text, _ = TestSummary.formatToolWindowLine summary
  text |> should haveSubstring "conservative fallback rebuild"

[<Fact>]
let ``summary computes passed count from Results when LastSummary is None`` () =
  let info = mkInfo "id-1" "test" (Some "File.fs") (Some 1)
  let result = mkResult "id-1" (TestOutcome.Passed 5.0) (Some 5.0)
  let state = {
    LiveTestState.empty with
      Tests = Map.ofList [ (info.Id, info) ]
      Results = Map.ofList [ (result.Id, result) ]
  }
  let s = LiveTestState.summary state
  s.Passed |> should equal 1
  s.Failed |> should equal 0

[<Fact>]
let ``summary computes failed count from Results when LastSummary is None`` () =
  let info = mkInfo "id-1" "test" (Some "File.fs") (Some 1)
  let result = mkResult "id-1" (TestOutcome.Failed("assertion failed", Some 2.0)) (Some 2.0)
  let state = {
    LiveTestState.empty with
      Tests = Map.ofList [ (info.Id, info) ]
      Results = Map.ofList [ (result.Id, result) ]
  }
  let s = LiveTestState.summary state
  s.Failed |> should equal 1
  s.Passed |> should equal 0
