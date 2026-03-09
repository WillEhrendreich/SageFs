module SageFs.VisualStudio.Core.Tests.GlyphProjectionTests

open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

// -- helpers ------------------------------------------------------------------

let private mkInfo id name (file: string option) (line: int option) = {
  Id = TestId.create id
  DisplayName = name
  FullName = "Module." + name
  FilePath = file
  Line = line
}

let private mkResult id outcome = {
  Id = TestId.create id
  Outcome = outcome
  DurationMs = None
  Output = None
}

let private addTest (state: LiveTestState) (info: TestInfo) =
  { state with Tests = state.Tests |> Map.add info.Id info }

let private addResult (state: LiveTestState) (result: TestResult) =
  { state with Results = state.Results |> Map.add result.Id result }

let private stateWith tests results =
  tests |> List.fold addTest LiveTestState.empty
  |> fun s -> results |> List.fold addResult s

// -- GlyphProjection.classify -------------------------------------------------

[<Fact>]
let ``classify Passed returns GlyphPassed`` () =
  GlyphProjection.classify (TestOutcome.Passed 42.0) |> should equal GlyphPassed

[<Fact>]
let ``classify Failed returns GlyphFailed`` () =
  GlyphProjection.classify (TestOutcome.Failed("oops", None)) |> should equal GlyphFailed

[<Fact>]
let ``classify Errored returns GlyphFailed`` () =
  GlyphProjection.classify (TestOutcome.Errored "crash") |> should equal GlyphFailed

[<Fact>]
let ``classify Running returns GlyphRunning`` () =
  GlyphProjection.classify TestOutcome.Running |> should equal GlyphRunning

[<Fact>]
let ``classify Stale returns GlyphStale`` () =
  GlyphProjection.classify TestOutcome.Stale |> should equal GlyphStale

[<Fact>]
let ``classify Detected returns GlyphNotRun`` () =
  GlyphProjection.classify TestOutcome.Detected |> should equal GlyphNotRun

[<Fact>]
let ``classify Skipped returns GlyphNotRun`` () =
  GlyphProjection.classify (TestOutcome.Skipped "too slow") |> should equal GlyphNotRun

[<Fact>]
let ``classify PolicyDisabled returns GlyphNotRun`` () =
  GlyphProjection.classify TestOutcome.PolicyDisabled |> should equal GlyphNotRun

// -- GlyphProjection.forFile --------------------------------------------------

[<Fact>]
let ``forFile returns empty for empty state`` () =
  GlyphProjection.forFile LiveTestState.empty "C:\\Tests.fs"
  |> should be Empty

[<Fact>]
let ``forFile returns entry for matching file`` () =
  let info = mkInfo "t1" "myTest" (Some "C:\\Tests.fs") (Some 10)
  let state = stateWith [info] []
  let result = GlyphProjection.forFile state "C:\\Tests.fs"
  result |> should haveLength 1
  result.[0].Line |> should equal 10
  result.[0].DisplayName |> should equal "myTest"

[<Fact>]
let ``forFile is case-insensitive`` () =
  let info = mkInfo "t1" "myTest" (Some "C:\\Tests.fs") (Some 5)
  let state = stateWith [info] []
  let result = GlyphProjection.forFile state "c:\\tests.fs"
  result |> should haveLength 1

[<Fact>]
let ``forFile excludes tests from other files`` () =
  let info1 = mkInfo "t1" "testA" (Some "C:\\A.fs") (Some 10)
  let info2 = mkInfo "t2" "testB" (Some "C:\\B.fs") (Some 20)
  let state = stateWith [info1; info2] []
  GlyphProjection.forFile state "C:\\A.fs" |> should haveLength 1
  GlyphProjection.forFile state "C:\\B.fs" |> should haveLength 1

[<Fact>]
let ``forFile excludes tests with no FilePath`` () =
  let info = mkInfo "t1" "noPath" None (Some 5)
  let state = stateWith [info] []
  GlyphProjection.forFile state "C:\\Tests.fs" |> should be Empty

[<Fact>]
let ``forFile excludes tests with no Line`` () =
  let info = mkInfo "t1" "noLine" (Some "C:\\Tests.fs") None
  let state = stateWith [info] []
  GlyphProjection.forFile state "C:\\Tests.fs" |> should be Empty

[<Fact>]
let ``forFile uses Detected outcome when no result exists`` () =
  let info = mkInfo "t1" "myTest" (Some "C:\\Tests.fs") (Some 5)
  let state = stateWith [info] []  // no results
  let entries = GlyphProjection.forFile state "C:\\Tests.fs"
  entries.[0].Outcome |> should equal TestOutcome.Detected
  entries.[0].Status |> should equal GlyphNotRun

[<Fact>]
let ``forFile uses actual result outcome when present`` () =
  let info = mkInfo "t1" "myTest" (Some "C:\\Tests.fs") (Some 5)
  let result = mkResult "t1" (TestOutcome.Passed 12.5)
  let state = stateWith [info] [result]
  let entries = GlyphProjection.forFile state "C:\\Tests.fs"
  entries.[0].Outcome |> should equal (TestOutcome.Passed 12.5)
  entries.[0].Status |> should equal GlyphPassed

[<Fact>]
let ``forFile sorts entries by line ascending`` () =
  let info1 = mkInfo "t1" "first" (Some "C:\\Tests.fs") (Some 30)
  let info2 = mkInfo "t2" "second" (Some "C:\\Tests.fs") (Some 10)
  let info3 = mkInfo "t3" "third" (Some "C:\\Tests.fs") (Some 20)
  let state = stateWith [info1; info2; info3] []
  let entries = GlyphProjection.forFile state "C:\\Tests.fs"
  entries |> List.map (fun e -> e.Line) |> should equal [10; 20; 30]

// -- GlyphProjection.allEntries -----------------------------------------------

[<Fact>]
let ``allEntries returns empty for empty state`` () =
  GlyphProjection.allEntries LiveTestState.empty |> should be Empty

[<Fact>]
let ``allEntries includes tests from multiple files`` () =
  let info1 = mkInfo "t1" "testA" (Some "C:\\A.fs") (Some 5)
  let info2 = mkInfo "t2" "testB" (Some "C:\\B.fs") (Some 3)
  let state = stateWith [info1; info2] []
  GlyphProjection.allEntries state |> should haveLength 2

[<Fact>]
let ``allEntries sorts by filePath then line`` () =
  let info1 = mkInfo "t1" "a" (Some "C:\\B.fs") (Some 10)
  let info2 = mkInfo "t2" "b" (Some "C:\\A.fs") (Some 20)
  let info3 = mkInfo "t3" "c" (Some "C:\\A.fs") (Some 5)
  let state = stateWith [info1; info2; info3] []
  let entries = GlyphProjection.allEntries state
  entries |> List.map (fun e -> e.FilePath, e.Line)
  |> should equal [("C:\\A.fs", 5); ("C:\\A.fs", 20); ("C:\\B.fs", 10)]

[<Fact>]
let ``allEntries excludes tests with no location`` () =
  let withPath = mkInfo "t1" "located" (Some "C:\\Tests.fs") (Some 5)
  let noPath   = mkInfo "t2" "nopath"  None (Some 5)
  let noLine   = mkInfo "t3" "noline"  (Some "C:\\Tests.fs") None
  let state = stateWith [withPath; noPath; noLine] []
  GlyphProjection.allEntries state |> should haveLength 1
