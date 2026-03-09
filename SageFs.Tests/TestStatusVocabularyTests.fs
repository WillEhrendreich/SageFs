namespace SageFs.Tests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

module TestStatusVocabularyTests =

  let private allIcons = [
    GutterIcon.TestDiscovered; GutterIcon.TestPassed; GutterIcon.TestFailed
    GutterIcon.TestRunning; GutterIcon.TestSkipped; GutterIcon.TestFlaky
    GutterIcon.Covered; GutterIcon.NotCovered
  ]

  let existingBehaviorTests = testList "GutterIcon existing behavior" [
    testCase "toChar returns distinct chars for all icons" <| fun _ ->
      let chars = allIcons |> List.map GutterIcon.toChar
      chars |> List.distinct |> List.length
      |> Expect.equal "all chars distinct" (List.length allIcons)

    testCase "toColorIndex returns valid 256-color byte indices" <| fun _ ->
      allIcons |> List.iter (fun icon ->
        let idx = GutterIcon.toColorIndex icon
        (idx, 255uy) |> Expect.isLessThanOrEqual "color <= 255")

    testCase "toLabel round-trips through parseLabel for all icons" <| fun _ ->
      allIcons |> List.iter (fun icon ->
        let label = GutterIcon.toLabel icon
        let parsed = GutterIcon.parseLabel label
        parsed |> Expect.equal (sprintf "round-trip for %A" icon) (Some icon))

    testCase "parseLabel returns None for unknown strings" <| fun _ ->
      GutterIcon.parseLabel "Bogus"
      |> Expect.equal "unknown label" None

    testCase "StatusToGutter.fromTestStatus maps Passed" <| fun _ ->
      StatusToGutter.fromTestStatus (TestRunStatus.Passed System.TimeSpan.Zero)
      |> Expect.equal "Passed -> TestPassed" GutterIcon.TestPassed

    testCase "StatusToGutter.fromTestStatus maps Failed" <| fun _ ->
      StatusToGutter.fromTestStatus (TestRunStatus.Failed (TestFailure.AssertionFailed "err", System.TimeSpan.Zero))
      |> Expect.equal "Failed -> TestFailed" GutterIcon.TestFailed

    testCase "StatusToGutter.fromCoverageStatus maps Covered" <| fun _ ->
      StatusToGutter.fromCoverageStatus (CoverageStatus.Covered (3, CoverageHealth.AllPassing))
      |> Expect.equal "Covered -> Covered icon" GutterIcon.Covered
  ]

  let newFunctionTests = testList "GutterIcon vocabulary extensions" [
    testCase "toEmoji returns distinct emoji for all icons" <| fun _ ->
      let emojis = allIcons |> List.map GutterIcon.toEmoji
      emojis |> List.distinct |> List.length
      |> Expect.equal "all emojis distinct" (List.length allIcons)

    testCase "toEmoji returns non-empty strings" <| fun _ ->
      allIcons |> List.iter (fun icon ->
        let emoji = GutterIcon.toEmoji icon
        (emoji.Length, 0) |> Expect.isGreaterThan "emoji non-empty")

    testCase "toStatusText returns distinct text for all icons" <| fun _ ->
      let texts = allIcons |> List.map GutterIcon.toStatusText
      texts |> List.distinct |> List.length
      |> Expect.equal "all status texts distinct" (List.length allIcons)

    testCase "toStatusText returns lowercase strings" <| fun _ ->
      allIcons |> List.iter (fun icon ->
        let text = GutterIcon.toStatusText icon
        text |> Expect.equal "lowercase" (text.ToLowerInvariant()))

    testCase "toAnsiColor returns strings starting with ESC" <| fun _ ->
      allIcons |> List.iter (fun icon ->
        let color = GutterIcon.toAnsiColor icon
        color.[0] |> Expect.equal "starts with ESC" '\x1b')

    testCase "toAnsiColor contains bracket for ANSI sequence" <| fun _ ->
      allIcons |> List.iter (fun icon ->
        let color = GutterIcon.toAnsiColor icon
        color.Contains("[") |> Expect.isTrue "contains [")
  ]

  [<Tests>]
  let tests = testList "TestStatusVocabulary" [existingBehaviorTests; newFunctionTests]
