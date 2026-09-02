module SageFs.VisualStudio.Core.Tests.LiveTestingFormatTests

open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

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

// -- TestSummary.formatToolWindowLine -----------------------------------------

[<Fact>]
let ``formatToolWindowLine all passed reports passed count`` () =
  let s = { Total = 3; Passed = 3; Failed = 0; Running = 0; Stale = 0; Disabled = 0; DiscoveryState = "disabled"; DiscoveryGeneration = 0L; LastDecision = None }
  let text, severity = TestSummary.formatToolWindowLine s
  text |> should haveSubstring "3 passed"
  severity |> should equal "info"

[<Fact>]
let ``formatToolWindowLine with failures has error severity`` () =
  let s = { Total = 3; Passed = 2; Failed = 1; Running = 0; Stale = 0; Disabled = 0; DiscoveryState = "disabled"; DiscoveryGeneration = 0L; LastDecision = None }
  let text, severity = TestSummary.formatToolWindowLine s
  text |> should haveSubstring "1 failed"
  severity |> should equal "error"

[<Fact>]
let ``formatToolWindowLine with stale and no failures has warning severity`` () =
  let s = { Total = 2; Passed = 1; Failed = 0; Running = 0; Stale = 1; Disabled = 0; DiscoveryState = "disabled"; DiscoveryGeneration = 0L; LastDecision = None }
  let _, severity = TestSummary.formatToolWindowLine s
  severity |> should equal "warning"

[<Fact>]
let ``formatToolWindowLine with zero totals shows total count`` () =
  let s = { Total = 5; Passed = 0; Failed = 0; Running = 0; Stale = 0; Disabled = 0; DiscoveryState = "disabled"; DiscoveryGeneration = 0L; LastDecision = None }
  let text, _ = TestSummary.formatToolWindowLine s
  text |> should haveSubstring "5 tests"

[<Fact>]
let ``formatToolWindowLine includes running count when nonzero`` () =
  let s = { Total = 4; Passed = 1; Failed = 0; Running = 2; Stale = 0; Disabled = 0; DiscoveryState = "disabled"; DiscoveryGeneration = 0L; LastDecision = None }
  let text, _ = TestSummary.formatToolWindowLine s
  text |> should haveSubstring "2 running"

[<Fact>]
let ``formatToolWindowLine includes disabled count when nonzero`` () =
  let s = { Total = 4; Passed = 2; Failed = 0; Running = 0; Stale = 0; Disabled = 2; DiscoveryState = "disabled"; DiscoveryGeneration = 0L; LastDecision = None }
  let text, _ = TestSummary.formatToolWindowLine s
  text |> should haveSubstring "2 disabled"

[<Fact>]
let ``formatToolWindowLine includes why hint when decision is present`` () =
  let decision =
    { Cause = RerunCause.KeystrokeBuffered
      FilePath = "src/Architecture.fs"
      Precision = SelectionPrecision.SuppressedByPolicy
      Trust = FreshnessTrust.Suppressed
      ChangedSymbols = [| "Architecture.Rule" |]
      SelectedTests = [||]
      DeferredTests = [| "Architecture.Tests.should_hold" |]
      Reason = "suppressed" }
  let s = { Total = 1; Passed = 0; Failed = 0; Running = 0; Stale = 1; Disabled = 0; DiscoveryState = "disabled"; DiscoveryGeneration = 0L; LastDecision = Some decision }
  let text, _ = TestSummary.formatToolWindowLine s
  text |> should haveSubstring "run policy deferred 1 test"

// -- RunPolicy helpers --------------------------------------------------------

[<Fact>]
let ``RunPolicy ToApiString returns every for EveryKeystroke`` () =
  RunPolicy.EveryKeystroke.ToApiString() |> should equal "every"

[<Fact>]
let ``RunPolicy ToApiString returns save for OnSave`` () =
  RunPolicy.OnSave.ToApiString() |> should equal "save"

[<Fact>]
let ``RunPolicy ToApiString returns demand for OnDemand`` () =
  RunPolicy.OnDemand.ToApiString() |> should equal "demand"

[<Fact>]
let ``RunPolicy ToApiString returns disabled for Disabled`` () =
  RunPolicy.Disabled.ToApiString() |> should equal "disabled"

[<Fact>]
let ``RunPolicy OfApiString roundtrips all values`` () =
  for p in RunPolicy.All do
    RunPolicy.OfApiString(p.ToApiString()) |> should equal p

[<Fact>]
let ``RunPolicy OfApiString unknown string returns Disabled`` () =
  RunPolicy.OfApiString "anything_else" |> should equal RunPolicy.Disabled

// -- TestCategory helpers -----------------------------------------------------

[<Fact>]
let ``TestCategory ToApiString returns unit for Unit`` () =
  TestCategory.Unit.ToApiString() |> should equal "unit"

[<Fact>]
let ``TestCategory OfApiString roundtrips all values`` () =
  for c in TestCategory.All do
    TestCategory.OfApiString(c.ToApiString()) |> should equal c

[<Fact>]
let ``TestCategory OfApiString unknown string returns Property`` () =
  TestCategory.OfApiString "unknown_cat" |> should equal TestCategory.Property

// -- TestTreeViewModel.formatTestLine -----------------------------------------

[<Fact>]
let ``formatTestLine with no result shows circle icon`` () =
  let info = mkInfo "t1" "my test" (Some "File.fs") (Some 1)
  let line = TestTreeViewModel.formatTestLine info None
  line |> should startWith "○"
  line |> should haveSubstring "my test"

[<Fact>]
let ``formatTestLine with passed result shows check icon and duration`` () =
  let info = mkInfo "t1" "my test" (Some "File.fs") (Some 1)
  let result = mkResult "t1" (TestOutcome.Passed 42.0) (Some 42.0)
  let line = TestTreeViewModel.formatTestLine info (Some result)
  line |> should startWith "✓"
  line |> should haveSubstring "my test"
  line |> should haveSubstring "42ms"

[<Fact>]
let ``formatTestLine with failed result shows cross icon`` () =
  let info = mkInfo "t1" "fail test" (Some "File.fs") (Some 1)
  let result = mkResult "t1" (TestOutcome.Failed("oops", None)) None
  let line = TestTreeViewModel.formatTestLine info (Some result)
  line |> should startWith "✗"

[<Fact>]
let ``formatTestLine with running result shows bullet icon`` () =
  let info = mkInfo "t1" "running test" (Some "File.fs") (Some 1)
  let result = mkResult "t1" TestOutcome.Running None
  let line = TestTreeViewModel.formatTestLine info (Some result)
  line |> should startWith "●"

[<Fact>]
let ``formatTestLine with duration under 1ms shows less-than marker`` () =
  let info = mkInfo "t1" "fast test" (Some "File.fs") (Some 1)
  let result = mkResult "t1" (TestOutcome.Passed 0.5) (Some 0.5)
  let line = TestTreeViewModel.formatTestLine info (Some result)
  line |> should haveSubstring "<1ms"

[<Fact>]
let ``formatTestLine with duration over 1000ms shows seconds`` () =
  let info = mkInfo "t1" "slow test" (Some "File.fs") (Some 1)
  let result = mkResult "t1" (TestOutcome.Passed 2500.0) (Some 2500.0)
  let line = TestTreeViewModel.formatTestLine info (Some result)
  line |> should haveSubstring "2.5s"

// -- TestTreeViewModel.sortTests ----------------------------------------------

[<Fact>]
let ``sortTests places failures before passing`` () =
  let i1 = mkInfo "t1" "alpha" (Some "F.fs") (Some 1)
  let i2 = mkInfo "t2" "beta" (Some "F.fs") (Some 2)
  let r1 = mkResult "t1" (TestOutcome.Passed 1.0) (Some 1.0)
  let r2 = mkResult "t2" (TestOutcome.Failed("x", None)) None
  let sorted = TestTreeViewModel.sortTests [| (i1, Some r1); (i2, Some r2) |]
  let (firstInfo, _) = sorted.[0]
  firstInfo.DisplayName |> should equal "beta"

[<Fact>]
let ``sortTests places running before passed`` () =
  let i1 = mkInfo "t1" "passed" (Some "F.fs") (Some 1)
  let i2 = mkInfo "t2" "running" (Some "F.fs") (Some 2)
  let r1 = mkResult "t1" (TestOutcome.Passed 1.0) (Some 1.0)
  let r2 = mkResult "t2" TestOutcome.Running None
  let sorted = TestTreeViewModel.sortTests [| (i1, Some r1); (i2, Some r2) |]
  let (firstInfo, _) = sorted.[0]
  firstInfo.DisplayName |> should equal "running"

[<Fact>]
let ``sortTests within same outcome sorts alphabetically`` () =
  let i1 = mkInfo "t1" "zebra" (Some "F.fs") (Some 1)
  let i2 = mkInfo "t2" "apple" (Some "F.fs") (Some 2)
  let r1 = mkResult "t1" (TestOutcome.Passed 1.0) (Some 1.0)
  let r2 = mkResult "t2" (TestOutcome.Passed 2.0) (Some 2.0)
  let sorted = TestTreeViewModel.sortTests [| (i1, Some r1); (i2, Some r2) |]
  let (firstInfo, _) = sorted.[0]
  firstInfo.DisplayName |> should equal "apple"

// -- TestTreeViewModel.filterLabel --------------------------------------------

[<Fact>]
let ``filterLabel All returns All`` () =
  TestTreeViewModel.filterLabel TestStatusFilter.All |> should equal "All"

[<Fact>]
let ``filterLabel FailedOnly returns Failed`` () =
  TestTreeViewModel.filterLabel TestStatusFilter.FailedOnly |> should equal "Failed"

[<Fact>]
let ``filterLabel RunningOnly returns Running`` () =
  TestTreeViewModel.filterLabel TestStatusFilter.RunningOnly |> should equal "Running"

[<Fact>]
let ``filterLabel StaleOnly returns Stale`` () =
  TestTreeViewModel.filterLabel TestStatusFilter.StaleOnly |> should equal "Stale"

[<Fact>]
let ``filterLabel PassedOnly returns Passed`` () =
  TestTreeViewModel.filterLabel TestStatusFilter.PassedOnly |> should equal "Passed"

// -- TestId -------------------------------------------------------------------

[<Fact>]
let ``TestId create and value roundtrip`` () =
  let id = TestId.create "my-test-id"
  TestId.value id |> should equal "my-test-id"

[<Fact>]
let ``TestId equality same value`` () =
  let a = TestId.create "abc"
  let b = TestId.create "abc"
  a |> should equal b

[<Fact>]
let ``TestId inequality different values`` () =
  let a = TestId.create "abc"
  let b = TestId.create "xyz"
  a |> should not' (equal b)
