module SageFs.VisualStudio.Core.Tests.TestTreeViewModelTests

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

let private passedResult = mkResult "r1" (TestOutcome.Passed 45.0) (Some 45.0)
let private failedResult = mkResult "r2" (TestOutcome.Failed("expected 1 but got 2", Some 12.0)) (Some 12.0)
let private runningResult = mkResult "r3" TestOutcome.Running None
let private staleResult = mkResult "r4" TestOutcome.Stale None
let private skippedResult = mkResult "r5" (TestOutcome.Skipped "not applicable") None

let private sampleInfo = mkInfo "r1" "my test" (Some "Test.fs") (Some 10)

// -- LiveTestingSubscriber.formatTestLabel ---------------------------------

[<Fact>]
let ``formatTestLabel with passed result returns check icon and duration`` () =
  LiveTestingSubscriber.formatTestLabel(sampleInfo, Some passedResult)
  |> should equal "✓ Passed (45ms)"

[<Fact>]
let ``formatTestLabel with failed result starts with cross icon and message`` () =
  let label = LiveTestingSubscriber.formatTestLabel(sampleInfo, Some failedResult)
  label |> should startWith "✗ Failed: "
  label |> should haveSubstring "expected 1 but got 2"

[<Fact>]
let ``formatTestLabel with no result returns not-run label`` () =
  LiveTestingSubscriber.formatTestLabel(sampleInfo, None)
  |> should equal "● Not Run"

[<Fact>]
let ``formatTestLabel with running result returns running indicator`` () =
  LiveTestingSubscriber.formatTestLabel(sampleInfo, Some runningResult)
  |> should equal "◆ Running…"

[<Fact>]
let ``formatTestLabel with stale result returns stale indicator`` () =
  LiveTestingSubscriber.formatTestLabel(sampleInfo, Some staleResult)
  |> should equal "◌ Stale"

[<Fact>]
let ``formatTestLabel with long failure message truncates at 50 chars`` () =
  let longMsg = System.String('x', 60)
  let longFailed = mkResult "rx" (TestOutcome.Failed(longMsg, None)) None
  let label = LiveTestingSubscriber.formatTestLabel(sampleInfo, Some longFailed)
  // "✗ Failed: " + 50 chars + "…"
  label |> should haveSubstring "…"
  let msgPart = label.Substring("✗ Failed: ".Length)
  msgPart.Length |> should equal 51  // 50 chars + "…"

[<Fact>]
let ``formatTestLabel with failure message of exactly 50 chars is not truncated`` () =
  let exactMsg = System.String('x', 50)
  let exactFailed = mkResult "rx" (TestOutcome.Failed(exactMsg, None)) None
  let label = LiveTestingSubscriber.formatTestLabel(sampleInfo, Some exactFailed)
  label |> should not' (haveSubstring "…")

// -- LiveTestingSubscriber.formatTestTooltip -------------------------------

[<Fact>]
let ``formatTestTooltip with passed result contains test name and Passed status`` () =
  let tooltip = LiveTestingSubscriber.formatTestTooltip(sampleInfo, Some passedResult)
  tooltip |> should haveSubstring sampleInfo.DisplayName
  tooltip |> should haveSubstring "Passed"

[<Fact>]
let ``formatTestTooltip with failed result contains Failed status`` () =
  let tooltip = LiveTestingSubscriber.formatTestTooltip(sampleInfo, Some failedResult)
  tooltip |> should haveSubstring "Failed"

[<Fact>]
let ``formatTestTooltip with no result returns Not Run`` () =
  let tooltip = LiveTestingSubscriber.formatTestTooltip(sampleInfo, None)
  tooltip |> should haveSubstring "Not Run"
  tooltip |> should haveSubstring sampleInfo.DisplayName

[<Fact>]
let ``formatTestTooltip with stale and StaleCodeEdited freshness mentions code edited`` () =
  let tooltip =
    LiveTestingSubscriber.formatTestTooltip(
      sampleInfo,
      Some staleResult,
      ResultFreshness.StaleCodeEdited
    )
  tooltip |> should haveSubstring "code edited"

// -- TestTreeViewModel.filterGroups ----------------------------------------

let private mkGroup filePath tests =
  { FilePath = filePath; Tests = tests |> Array.ofList }

let private groupWithMixed =
  mkGroup "File1.fs" [
    mkInfo "id-p" "passing" (Some "File1.fs") (Some 1),
    Some (mkResult "id-p" (TestOutcome.Passed 5.0) (Some 5.0))
    mkInfo "id-f" "failing" (Some "File1.fs") (Some 2),
    Some (mkResult "id-f" (TestOutcome.Failed("oops", None)) None)
  ]

let private groupAllPassed =
  mkGroup "File2.fs" [
    mkInfo "id-p2" "another pass" (Some "File2.fs") (Some 1),
    Some (mkResult "id-p2" (TestOutcome.Passed 3.0) (Some 3.0))
  ]

[<Fact>]
let ``filterGroups All keeps all groups unchanged`` () =
  let groups = [| groupWithMixed; groupAllPassed |]
  let result = TestTreeViewModel.filterGroups TestStatusFilter.All groups
  result.Length |> should equal 2

[<Fact>]
let ``filterGroups FailedOnly keeps only groups containing failed tests`` () =
  let groups = [| groupWithMixed; groupAllPassed |]
  let result = TestTreeViewModel.filterGroups TestStatusFilter.FailedOnly groups
  result.Length |> should equal 1
  result.[0].FilePath |> should equal "File1.fs"

[<Fact>]
let ``filterGroups FailedOnly removes passing tests from mixed group`` () =
  let groups = [| groupWithMixed |]
  let result = TestTreeViewModel.filterGroups TestStatusFilter.FailedOnly groups
  result.[0].Tests.Length |> should equal 1
  let (_, outcome) = result.[0].Tests.[0]
  match outcome with
  | Some { Outcome = TestOutcome.Failed _ } -> ()
  | other -> failwith (sprintf "expected Failed but got %A" other)

[<Fact>]
let ``filterGroups PassedOnly keeps only groups containing passed tests`` () =
  let groups = [| groupWithMixed; groupAllPassed |]
  let result = TestTreeViewModel.filterGroups TestStatusFilter.PassedOnly groups
  // groupWithMixed has one passed test, groupAllPassed has one passed test
  result.Length |> should equal 2

// -- TestTreeViewModel.searchGroups ----------------------------------------

let private mkGroupWithNames names filePath =
  mkGroup filePath (names |> List.mapi (fun i name ->
    mkInfo (sprintf "id-%d" i) name (Some filePath) (Some i), None))

[<Fact>]
let ``searchGroups with empty query returns all groups unchanged`` () =
  let groups = [| mkGroupWithNames [ "test one"; "test two" ] "File.fs" |]
  let result = TestTreeViewModel.searchGroups "" groups
  result.Length |> should equal 1
  result.[0].Tests.Length |> should equal 2

[<Fact>]
let ``searchGroups with whitespace-only query returns all groups unchanged`` () =
  let groups = [| mkGroupWithNames [ "test one" ] "File.fs" |]
  let result = TestTreeViewModel.searchGroups "   " groups
  result.Length |> should equal 1

[<Fact>]
let ``searchGroups case-insensitive match on DisplayName`` () =
  let groups = [| mkGroupWithNames [ "My Test"; "Other" ] "File.fs" |]
  let result = TestTreeViewModel.searchGroups "my" groups
  result.Length |> should equal 1
  result.[0].Tests.Length |> should equal 1

[<Fact>]
let ``searchGroups returns empty when no tests match query`` () =
  let groups = [| mkGroupWithNames [ "alpha"; "beta" ] "File.fs" |]
  let result = TestTreeViewModel.searchGroups "zzz" groups
  result |> should be Empty

[<Fact>]
let ``searchGroups removes groups where no tests match`` () =
  let g1 = mkGroupWithNames [ "matching test" ] "File1.fs"
  let g2 = mkGroupWithNames [ "other test" ] "File2.fs"
  let result = TestTreeViewModel.searchGroups "matching" [| g1; g2 |]
  result.Length |> should equal 1
  result.[0].FilePath |> should equal "File1.fs"

// -- TestTreeViewModel.nextFilter ------------------------------------------

[<Fact>]
let ``nextFilter All returns FailedOnly`` () =
  TestTreeViewModel.nextFilter TestStatusFilter.All |> should equal TestStatusFilter.FailedOnly

[<Fact>]
let ``nextFilter PassedOnly returns All completing the cycle`` () =
  TestTreeViewModel.nextFilter TestStatusFilter.PassedOnly |> should equal TestStatusFilter.All

[<Fact>]
let ``nextFilter FailedOnly returns RunningOnly`` () =
  TestTreeViewModel.nextFilter TestStatusFilter.FailedOnly |> should equal TestStatusFilter.RunningOnly

[<Fact>]
let ``nextFilter cycles through all filters and returns to All`` () =
  let cycle =
    TestStatusFilter.All
    |> TestTreeViewModel.nextFilter
    |> TestTreeViewModel.nextFilter
    |> TestTreeViewModel.nextFilter
    |> TestTreeViewModel.nextFilter
    |> TestTreeViewModel.nextFilter
  cycle |> should equal TestStatusFilter.All

// -- TestTreeViewModel.groupByFile -----------------------------------------

[<Fact>]
let ``groupByFile groups tests by file path returning one group per file`` () =
  let info1 = mkInfo "id-1" "test 1" (Some "File1.fs") (Some 1)
  let info2 = mkInfo "id-2" "test 2" (Some "File2.fs") (Some 2)
  let info3 = mkInfo "id-3" "test 3" (Some "File1.fs") (Some 3)
  let state = {
    LiveTestState.empty with
      Tests = Map.ofList [ (info1.Id, info1); (info2.Id, info2); (info3.Id, info3) ]
  }
  let groups = TestTreeViewModel.groupByFile state
  groups.Length |> should equal 2

[<Fact>]
let ``groupByFile puts both tests in same group for same file`` () =
  let info1 = mkInfo "id-1" "test 1" (Some "File1.fs") (Some 1)
  let info2 = mkInfo "id-2" "test 2" (Some "File1.fs") (Some 2)
  let state = {
    LiveTestState.empty with
      Tests = Map.ofList [ (info1.Id, info1); (info2.Id, info2) ]
  }
  let groups = TestTreeViewModel.groupByFile state
  groups.Length |> should equal 1
  groups.[0].Tests.Length |> should equal 2

[<Fact>]
let ``groupByFile includes result alongside test info`` () =
  let info = mkInfo "id-1" "test 1" (Some "File1.fs") (Some 1)
  let result = mkResult "id-1" (TestOutcome.Passed 5.0) (Some 5.0)
  let state = {
    LiveTestState.empty with
      Tests = Map.ofList [ (info.Id, info) ]
      Results = Map.ofList [ (result.Id, result) ]
  }
  let groups = TestTreeViewModel.groupByFile state
  let (_, r) = groups.[0].Tests.[0]
  r |> should not' (equal None)

// -- TestTreeViewModel.formatGroupHeader -----------------------------------

[<Fact>]
let ``formatGroupHeader with all passed shows check icon and 3 of 3 passed`` () =
  let tests = [
    mkInfo "id-1" "t1" (Some "filename.fs") (Some 1), Some (mkResult "id-1" (TestOutcome.Passed 1.0) (Some 1.0))
    mkInfo "id-2" "t2" (Some "filename.fs") (Some 2), Some (mkResult "id-2" (TestOutcome.Passed 2.0) (Some 2.0))
    mkInfo "id-3" "t3" (Some "filename.fs") (Some 3), Some (mkResult "id-3" (TestOutcome.Passed 3.0) (Some 3.0))
  ]
  let group = mkGroup "path/to/filename.fs" tests
  TestTreeViewModel.formatGroupHeader group
  |> should equal "✓ filename.fs (3/3 passed)"

[<Fact>]
let ``formatGroupHeader with some failed shows cross icon and failed count`` () =
  let tests = [
    mkInfo "id-1" "t1" (Some "filename.fs") (Some 1), Some (mkResult "id-1" (TestOutcome.Passed 1.0) (Some 1.0))
    mkInfo "id-2" "t2" (Some "filename.fs") (Some 2), Some (mkResult "id-2" (TestOutcome.Passed 2.0) (Some 2.0))
    mkInfo "id-3" "t3" (Some "filename.fs") (Some 3), Some (mkResult "id-3" (TestOutcome.Failed("oops", None)) None)
  ]
  let group = mkGroup "path/to/filename.fs" tests
  TestTreeViewModel.formatGroupHeader group
  |> should equal "✗ filename.fs (2/3 passed, 1 failed)"

[<Fact>]
let ``formatGroupHeader with no results shows 0 of N passed`` () =
  let tests = [
    mkInfo "id-1" "t1" (Some "filename.fs") (Some 1), None
    mkInfo "id-2" "t2" (Some "filename.fs") (Some 2), None
  ]
  let group = mkGroup "filename.fs" tests
  TestTreeViewModel.formatGroupHeader group
  |> should equal "✓ filename.fs (0/2 passed)"

// -- TestTreeViewModel.formatGroupedOutput ---------------------------------

[<Fact>]
let ``formatGroupedOutput with empty state returns no-tests-discovered message`` () =
  TestTreeViewModel.formatGroupedOutput TestStatusFilter.All "" LiveTestState.empty
  |> should equal "No tests discovered yet."

[<Fact>]
let ``formatGroupedOutput with tests and no filter returns group headers and test lines`` () =
  let info = mkInfo "id-1" "my test" (Some "File.fs") (Some 1)
  let state = { LiveTestState.empty with Tests = Map.ofList [ (info.Id, info) ] }
  let output = TestTreeViewModel.formatGroupedOutput TestStatusFilter.All "" state
  output |> should haveSubstring "File.fs"
  output |> should haveSubstring "my test"

[<Fact>]
let ``formatGroupedOutput with filter that matches nothing returns no-match message`` () =
  let info = mkInfo "id-1" "passing test" (Some "File.fs") (Some 1)
  let result = mkResult "id-1" (TestOutcome.Passed 5.0) (Some 5.0)
  let state = {
    LiveTestState.empty with
      Tests = Map.ofList [ (info.Id, info) ]
      Results = Map.ofList [ (result.Id, result) ]
  }
  let output = TestTreeViewModel.formatGroupedOutput TestStatusFilter.FailedOnly "" state
  output |> should haveSubstring "Failed"
