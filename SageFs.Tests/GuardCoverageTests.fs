/// What a click says about its guards. The row reads these words, so each case has its own text, the counts are real,
/// and a getter that reaches fifty framework methods says so once.
module SageFs.Tests.GuardCoverageTests

open Expecto
open Expecto.Flip
open SageFs.Features

let private allSkipReasons : SkipReason list =
  [ SkipReason.NotOurCode "System.Linq"
    SkipReason.AsyncStateMachine
    SkipReason.DynamicMethod
    SkipReason.Generic
    SkipReason.NoBody
    SkipReason.DetouredByHotReload
    SkipReason.PatchRefused "refused"
    SkipReason.UnreadableBody "unreadable"
    SkipReason.BudgetReached ]

let private skipped (name: string) (reason: SkipReason) : SkippedMethod = { Method = name; Reason = reason }

let private guarded (methods: string list) (skips: SkippedMethod list) : GuardCoverage =
  GuardCoverage.Guarded { Methods = methods; Skipped = skips }

[<Tests>]
let guardCoverageTests =
  testList "guard coverage (what the row says)" [

    testCase "WHY — a click guarded everywhere says how many methods had their stack and loops checked" <| fun _ ->
      GuardCoverage.describe (guarded [ "A.get_Total"; "A.helper" ] [])
      |> Expect.equal "plural" "guarded: stack and loops checked in 2 methods"

    testCase "WHY — one method is not '1 methods'" <| fun _ ->
      GuardCoverage.describe (guarded [ "A.get_Total" ] [])
      |> Expect.equal "singular" "guarded: stack and loops checked in 1 method"

    testCase "WHY — what was reachable and not guarded is said beside what was, grouped by kind, biggest first" <| fun _ ->
      let skips =
        [ skipped "Enumerable.Select" (SkipReason.NotOurCode "System.Linq")
          skipped "Enumerable.Where" (SkipReason.NotOurCode "System.Linq")
          skipped "Enumerable.Sum" (SkipReason.NotOurCode "System.Linq")
          skipped "A.MoveNext" SkipReason.AsyncStateMachine ]
      let line = GuardCoverage.describe (guarded [ "A.get_Total" ] skips)
      line |> Expect.stringStarts "leads with what is guarded" "guarded: stack and loops checked in 1 method; not guarded: "
      line |> Expect.stringContains "counts the library calls together" "3 x code in System.Linq, which SageFs does not own"
      line |> Expect.stringContains "names the async machine" "1 x an async or task state machine"
      (line.IndexOf "3 x", line.IndexOf "1 x") |> Expect.isLessThan "the bigger group comes first"

    testCase "WHY — a click with no guards says it is not guarded and why, for every reason" <| fun _ ->
      let reasons =
        [ NotGuardedReason.SwitchedOff
          NotGuardedReason.NothingRan
          NotGuardedReason.PatchingUnavailable "no patching here"
          NotGuardedReason.GetterSkipped SkipReason.AsyncStateMachine
          NotGuardedReason.PreparationFailed "it threw" ]
      let lines = reasons |> List.map (fun reason -> GuardCoverage.describe (GuardCoverage.NotGuarded reason))
      for line in lines do
        line |> Expect.stringStarts "starts the same way every time" "not guarded: "
        (String.length "not guarded: ", line.Length) |> Expect.isLessThan "carries its reason"
      lines |> List.distinct |> List.length |> Expect.equal "no two reasons read the same" (List.length lines)

    testCase "WHY — every skip reason has its own words" <| fun _ ->
      let texts = allSkipReasons |> List.map SkipReason.describe
      texts |> List.distinct |> List.length |> Expect.equal "all different" (List.length texts)
      texts |> List.iter (fun text -> text |> Expect.isNotEmpty "and none is empty")

    testCase "WHY — the detail a reason carries is on the row, so a refused patch says what the patcher said" <| fun _ ->
      SkipReason.describe (SkipReason.PatchRefused "method has no body")
      |> Expect.stringContains "the patcher's words" "method has no body"

    testCase "WHY — a guard that fired is said, and one that did not is silent" <| fun _ ->
      GuardCoverage.describeTrip GuardTrip.NotTripped |> Expect.equal "silent" ""
      GuardCoverage.describeTrip GuardTrip.LoopStopped |> Expect.stringContains "loop" "looping"
      GuardCoverage.describeTrip GuardTrip.StackLimitReached |> Expect.stringContains "stack" "stack"

    testCase "WHY — the guarded count is the methods that got guards, and zero when nothing was guarded" <| fun _ ->
      GuardCoverage.guardedCount (guarded [ "a"; "b"; "c" ] []) |> Expect.equal "three" 3
      GuardCoverage.guardedCount (GuardCoverage.NotGuarded NotGuardedReason.SwitchedOff) |> Expect.equal "none" 0
  ]
