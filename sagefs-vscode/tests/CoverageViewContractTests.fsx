// WHY — this contract test pins the pure rendering logic for the new
// coverage_view event. If this test starts failing, the renderer is
// emitting more than one line per CoverageView and the user is back to
// seeing 100 test names stacked on one function.
//
// Runs under plain `dotnet fsi` (no Fable). Tests the CoverageViewPure
// module which has no Fable dependency. Full test cycle is <2s.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/CoverageViewPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.CoverageViewPure

let tests =
  testList "VS Code coverage_view contract - pure renderer" [

    testCase "WHY - render - formats the inline badge as one short line 'check 97 x 3' so the editor never paints 100 names" <| fun _ ->
      toInlineBadge [ "Pass", 97; "Fail", 3 ]
      |> Expect.equal "compact one line" "✓ 97 ✗ 3"

    testCase "WHY - render - empty badge list produces empty text so the editor omits the badge entirely" <| fun _ ->
      toInlineBadge []
      |> Expect.equal "empty" ""

    testCase "WHY - render - single Pass badge formats as 'check 100' not 'check 100  '" <| fun _ ->
      toInlineBadge [ "Pass", 100 ]
      |> Expect.equal "single kind" "✓ 100"

    testCase "WHY - render - mixed statuses with running produce three badges space-separated" <| fun _ ->
      toInlineBadge [ "Pass", 5; "Fail", 2; "Running", 5 ]
      |> Expect.equal "three kinds" "✓ 5 ✗ 2 ⟳ 5"

    testCase "WHY - render - all five status kinds fit on one line (bounded by DU arity)" <| fun _ ->
      toInlineBadge [ "Pass", 10; "Fail", 2; "Running", 3; "Stale", 1; "Skipped", 4 ]
      |> Expect.equal "five kinds, one line" "✓ 10 ✗ 2 ⟳ 3 ~ 1 ⊘ 4"

    testCase "WHY - render - unknown case produces empty string (defensive default)" <| fun _ ->
      // Inline badges with unknown kind get the prefix but empty count
      toInlineBadge [ "Mystery", 5 ]
      |> Expect.equal "unknown case" ""

    testCase "WHY - health - 'Passing' parses to Passing" <| fun _ ->
      healthFromString "Passing"
      |> Expect.equal "all passing" CoverageHealth.Passing

    testCase "WHY - health - 'Failing' parses to Failing" <| fun _ ->
      healthFromString "Failing"
      |> Expect.equal "some failing" CoverageHealth.Failing

    testCase "WHY - health - unknown string defaults to Absent (defensive default)" <| fun _ ->
      healthFromString "Wat"
      |> Expect.equal "unknown -> absent" CoverageHealth.Absent

    testCase "WHY - perf - rendering 5 badges completes in <1ms because the editor calls this on every visible function and a slow renderer freezes the editor" <| fun _ ->
      let badges = [ "Pass", 10; "Fail", 2; "Running", 3; "Stale", 1; "Skipped", 4 ]
      let sw = System.Diagnostics.Stopwatch.StartNew()
      for _ in 1..10000 do
        toInlineBadge badges |> ignore
      sw.Stop()
      // 10000 calls must complete in <100ms = <10us per call. Generous
      // budget that catches O(n^2) regressions (e.g. accidental Seq
      // allocations on the hot path).
      (sw.Elapsed.TotalMilliseconds, 100.0)
      |> Expect.isLessThan "10000 badge renders must complete in <100ms"
  ]

let _ = Expecto.Tests.runTestsWithCLIArgs [] [||] tests
