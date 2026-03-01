module SageFs.Tests.EvalDiffTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.EvalDiff

[<Tests>]
let evalDiffTests = testList "EvalDiff" [

  testList "Property-based" [
    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "applyDiff round-trips: applying diff to old produces new"
      (fun (oldStr: string) (newStr: string) ->
        let oldClean = if isNull oldStr then "" else oldStr.Replace('\r', ' ')
        let newClean = if isNull newStr then "" else newStr.Replace('\r', ' ')
        let diff = diffLines (Some oldClean) (Some newClean)
        let applied = applyDiff (splitLines oldClean) diff
        let expected = splitLines newClean
        applied |> Expect.equal "round-trip should produce new output" expected)

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "identical outputs produce all Unchanged"
      (fun (s: string) ->
        let sClean = if isNull s then "" else s.Replace('\r', ' ')
        let diff = diffLines (Some sClean) (Some sClean)
        diff |> List.forall (function Unchanged _ -> true | _ -> false)
        |> Expect.isTrue "all lines should be Unchanged")

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "None→Some produces all Added"
      (fun (s: string) ->
        let sClean = if isNull s then "" else s.Replace('\r', ' ')
        let diff = diffLines None (Some sClean)
        diff |> List.forall (function Added _ -> true | _ -> false)
        |> Expect.isTrue "all lines should be Added")

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "Some→None produces all Removed"
      (fun (s: string) ->
        let sClean = if isNull s then "" else s.Replace('\r', ' ')
        let diff = diffLines (Some sClean) None
        diff |> List.forall (function Removed _ -> true | _ -> false)
        |> Expect.isTrue "all lines should be Removed")

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "summary counts match line kinds"
      (fun (oldStr: string) (newStr: string) ->
        let oldClean = if isNull oldStr then "" else oldStr.Replace('\r', ' ')
        let newClean = if isNull newStr then "" else newStr.Replace('\r', ' ')
        let diff = diffLines (Some oldClean) (Some newClean)
        let summary = summarize diff
        summary.AddedCount + summary.RemovedCount + summary.ModifiedCount + summary.UnchangedCount
        |> Expect.equal "counts should sum to total lines" diff.Length)

    testCase "None/None produces empty diff" <| fun () ->
      diffLines None None
      |> Expect.isEmpty "should be empty"
  ]

  testList "Examples" [
    testCase "single line modified" <| fun () ->
      let diff = diffLines (Some "hello") (Some "world")
      diff |> Expect.equal "should be one Modified" [Modified("hello", "world")]

    testCase "single line unchanged" <| fun () ->
      let diff = diffLines (Some "hello") (Some "hello")
      diff |> Expect.equal "should be one Unchanged" [Unchanged "hello"]

    testCase "single line added" <| fun () ->
      let diff = diffLines None (Some "hello")
      diff |> Expect.equal "should be one Added" [Added "hello"]

    testCase "single line removed" <| fun () ->
      let diff = diffLines (Some "hello") None
      diff |> Expect.equal "should be one Removed" [Removed "hello"]

    testCase "multi-line mixed changes" <| fun () ->
      let diff = diffLines (Some "a\nb\nc") (Some "a\nB\nc\nd")
      diff |> Expect.equal "should have mixed diff"
        [Unchanged "a"; Modified("b", "B"); Unchanged "c"; Added "d"]

    testCase "summary formatting with changes" <| fun () ->
      let diff = diffLines (Some "a\nb") (Some "a\nB\nc")
      let summary = summarize diff
      formatSummary summary
      |> Expect.equal "should format correctly" "Δ 1 added, 1 modified"

    testCase "summary formatting no changes" <| fun () ->
      let diff = diffLines (Some "a") (Some "a")
      let summary = summarize diff
      formatSummary summary
      |> Expect.equal "should say no changes" "no changes"

    testCase "empty string edge case" <| fun () ->
      splitLines "" |> Expect.isEmpty "empty string should give empty list"
  ]
]
