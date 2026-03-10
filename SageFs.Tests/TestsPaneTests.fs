module SageFs.Tests.TestsPaneTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting

// ── Helpers ───────────────────────────────────────────────────────

let private makeEntry (name: string) (status: TestRunStatus) : TestStatusEntry =
  { TestId = TestId.TestId name
    DisplayName = name
    FullName = name
    Origin = TestOrigin.ReflectionOnly
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
    CurrentPolicy = RunPolicy.OnEveryChange
    Status = status
    PreviousStatus = TestRunStatus.Stale }

let private passEntry n  = makeEntry n (TestRunStatus.Passed (System.TimeSpan.FromMilliseconds 42.0))
let private failEntry n  = makeEntry n (TestRunStatus.Failed (TestFailure.AssertionFailed "boom", System.TimeSpan.FromMilliseconds 7.0))
let private runEntry  n  = makeEntry n TestRunStatus.Running
let private staleEntry n = makeEntry n TestRunStatus.Stale

// ── iconChar ──────────────────────────────────────────────────────

[<Tests>]
let iconCharTests = testList "TestsPane.iconChar" [
  test "Passed gives filled circle" {
    TestsPane.iconChar (TestRunStatus.Passed System.TimeSpan.Zero)
    |> Expect.equal "should be bullet" '\u25CF'
  }
  test "Failed gives cross" {
    TestsPane.iconChar (TestRunStatus.Failed (TestFailure.AssertionFailed "x", System.TimeSpan.Zero))
    |> Expect.equal "should be cross" '\u2717'
  }
  test "Running gives rotating arrow" {
    TestsPane.iconChar TestRunStatus.Running
    |> Expect.equal "should be rotating" '\u27F3'
  }
  test "Stale gives dashed circle" {
    TestsPane.iconChar TestRunStatus.Stale
    |> Expect.equal "should be dashed" '\u25CC'
  }
  test "Queued gives dashed circle" {
    TestsPane.iconChar TestRunStatus.Queued
    |> Expect.equal "queued is dashed" '\u25CC'
  }
  test "Skipped gives empty circle" {
    TestsPane.iconChar (TestRunStatus.Skipped "reason")
    |> Expect.equal "should be empty circle" '\u25CB'
  }
]

// ── truncate ──────────────────────────────────────────────────────

[<Tests>]
let truncateTests = testList "TestsPane.truncate" [
  test "short string is unchanged" {
    TestsPane.truncate 20 "hello"
    |> Expect.equal "unchanged" "hello"
  }
  test "string exactly at max is unchanged" {
    let s = System.String.Concat(Array.replicate 10 "x")
    TestsPane.truncate 10 s
    |> Expect.equal "exact length unchanged" s
  }
  test "long string is truncated with ellipsis" {
    TestsPane.truncate 5 "hello world"
    |> Expect.equal "truncated to 5" "hell\u2026"
  }
  test "truncated result is exactly maxLen chars" {
    let result = TestsPane.truncate 8 "abcdefghijk"
    result.Length |> Expect.equal "length is maxLen" 8
    result.[result.Length - 1] |> Expect.equal "last char is ellipsis" '\u2026'
  }
]

// ── buildContent ──────────────────────────────────────────────────

[<Tests>]
let buildContentTests = testList "TestsPane.buildContent" [
  test "empty entries shows placeholder" {
    let content = TestsPane.buildContent 80 [||]
    content |> Expect.stringContains "No tests discovered" "placeholder present"
  }
  test "passed entry contains pass icon" {
    let content = TestsPane.buildContent 80 [| passEntry "MyTest" |]
    content |> Expect.stringContains (string '\u25CF') "pass icon present"
  }
  test "failed entry contains fail icon" {
    let content = TestsPane.buildContent 80 [| failEntry "MyTest" |]
    content |> Expect.stringContains (string '\u2717') "fail icon present"
  }
  test "running entry contains running icon" {
    let content = TestsPane.buildContent 80 [| runEntry "MyTest" |]
    content |> Expect.stringContains (string '\u27F3') "running icon present"
  }
  test "stale entry contains stale icon" {
    let content = TestsPane.buildContent 80 [| staleEntry "MyTest" |]
    content |> Expect.stringContains (string '\u25CC') "stale icon present"
  }
  test "multiple entries produce multiple lines" {
    let entries = [| passEntry "A"; failEntry "B"; staleEntry "C" |]
    let content = TestsPane.buildContent 80 entries
    let lines = content.Split('\n')
    lines.Length |> Expect.equal "three lines" 3
  }
  test "test name appears in content" {
    let content = TestsPane.buildContent 80 [| passEntry "MyUnique/Test" |]
    content |> Expect.stringContains "MyUnique" "name present"
  }
  test "duration appears for passed test" {
    let content = TestsPane.buildContent 80 [| passEntry "T" |]
    content |> Expect.stringContains "42ms" "duration present"
  }
  test "long test name is truncated to fit pane" {
    let longName = System.String.Concat(Array.replicate 200 "x")
    let content = TestsPane.buildContent 40 [| passEntry longName |]
    let line = content.Split('\n').[0]
    (line.Length <= 40) |> Expect.isTrue "line width bounded"
    line |> Expect.stringContains (string '\u2026') "ellipsis present"
  }
]

// ── renderContent ─────────────────────────────────────────────────

[<Tests>]
let renderContentTests = testList "TestsPane.renderContent" [
  test "renders without error with empty lines" {
    let grid = CellGrid.create 10 40
    let inner = DrawTarget.create grid (Rect.create 0 0 40 10)
    TestsPane.renderContent inner [||] -1 0 Theme.defaults
    CellGrid.toText grid |> Expect.isNonEmpty "grid has content"
  }

  test "renders pass and fail icons into grid" {
    let grid = CellGrid.create 5 40
    let inner = DrawTarget.create grid (Rect.create 0 0 40 5)
    let entries = [| passEntry "TestA"; failEntry "TestB" |]
    let lines = TestsPane.buildContent 40 entries |> fun s -> s.Split('\n')
    TestsPane.renderContent inner lines -1 0 Theme.defaults
    let text = CellGrid.toText grid
    text |> Expect.stringContains (string '\u25CF') "pass icon in grid"
    text |> Expect.stringContains (string '\u2717') "fail icon in grid"
  }

  test "cursor row has selection background" {
    let grid = CellGrid.create 5 40
    let inner = DrawTarget.create grid (Rect.create 0 0 40 5)
    let entries = [| passEntry "TestA" |]
    let lines = TestsPane.buildContent 40 entries |> fun s -> s.Split('\n')
    TestsPane.renderContent inner lines 0 0 Theme.defaults
    let cell = CellGrid.get grid 0 0
    let selBg = Theme.hexToRgb Theme.defaults.BgSelection
    cell.Bg |> Expect.equal "cursor row has selection bg" selBg
  }

  test "non-cursor row has panel background" {
    let grid = CellGrid.create 5 40
    let inner = DrawTarget.create grid (Rect.create 0 0 40 5)
    let entries = [| passEntry "TestA"; failEntry "TestB" |]
    let lines = TestsPane.buildContent 40 entries |> fun s -> s.Split('\n')
    TestsPane.renderContent inner lines 0 0 Theme.defaults
    let cell = CellGrid.get grid 1 0
    let panelBg = Theme.hexToRgb Theme.defaults.BgPanel
    cell.Bg |> Expect.equal "non-cursor row has panel bg" panelBg
  }

  test "scrolled view shows fail icon from second line" {
    let grid = CellGrid.create 3 40
    let inner = DrawTarget.create grid (Rect.create 0 0 40 3)
    let entries = [| passEntry "TestA"; failEntry "TestB"; staleEntry "TestC" |]
    let lines = TestsPane.buildContent 40 entries |> fun s -> s.Split('\n')
    let visibleLines = lines |> Array.skip 1 |> Array.truncate 3
    TestsPane.renderContent inner visibleLines -1 1 Theme.defaults
    let text = CellGrid.toText grid
    text |> Expect.stringContains (string '\u2717') "fail icon visible after scroll"
  }
]