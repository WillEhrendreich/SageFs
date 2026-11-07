/// What the live bindings pane says about how current its values are, from what the test loop is doing: each activity of the loop maps
/// to one word and one semantic tone, and a loop held back never reads as current.
module SageFs.Tests.LoopFreshnessTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTestActivity
open SageFs.Features.Tweak.LoopFreshness

let private tally : TestTally = { TestTally.empty with Passed = 3; Failed = 1 }

/// One of every case of the loop's activity.
let private everyActivity : LiveTestActivity list =
  [ LiveTestActivity.Off
    LiveTestActivity.Discovering
    LiveTestActivity.DiscoveryFailed "no tests"
    LiveTestActivity.NoTestsFound [ "Expecto" ]
    LiveTestActivity.BlockedByCompileErrors("/src/Game.fs", 2, tally)
    LiveTestActivity.BlockedByFailedRebuild("build failed", tally)
    LiveTestActivity.Rebuilding(4, tally)
    LiveTestActivity.Running tally
    LiveTestActivity.Settled tally ]

[<Tests>]
let tests =
  testList "the pane's freshness follows the test loop" [

    testCase "each activity of the loop says one thing about the values, and the four that have no say say nothing" <| fun _ ->
      let said = everyActivity |> List.map ofActivity
      said
      |> Expect.equal "the loop's nine activities"
        [ None
          None
          None
          None
          Some(Freshness.HeldByErrors("/src/Game.fs", 2))
          Some(Freshness.HeldByRebuild "build failed")
          Some Freshness.Updating
          Some Freshness.Updating
          Some Freshness.Current ]

    testCase "WHY — Running and Rebuilding are in progress, Settled is current, and a loop held back by compile errors is never current" <| fun _ ->
      ofActivity (LiveTestActivity.Settled tally) |> Expect.equal "settled is current" (Some Freshness.Current)
      ofActivity (LiveTestActivity.Running tally) |> Expect.equal "running is updating" (Some Freshness.Updating)
      ofActivity (LiveTestActivity.Rebuilding(1, tally)) |> Expect.equal "rebuilding is updating" (Some Freshness.Updating)
      match ofActivity (LiveTestActivity.BlockedByCompileErrors("Game.fs", 1, tally)) with
      | Some Freshness.Current -> failtest "compile errors held the loop back and the pane said current"
      | _ -> ()

    testCase "each freshness has its own word and its own tone, and red is only for the rebuild that failed" <| fun _ ->
      let all =
        [ Freshness.Updating
          Freshness.Current
          Freshness.HeldByErrors("Game.fs", 2)
          Freshness.HeldByRebuild "build failed" ]
      (all |> List.map word |> List.distinct).Length |> Expect.equal "one word each" all.Length
      (all |> List.map tone |> List.distinct).Length |> Expect.equal "one tone each" all.Length
      tone Freshness.Current |> Expect.equal "current is pass" FreshnessTone.Current
      tone Freshness.Updating |> Expect.equal "updating is moving" FreshnessTone.Moving
      tone (Freshness.HeldByErrors("Game.fs", 2)) |> Expect.equal "errors hold it back, a warning" FreshnessTone.Held
      tone (Freshness.HeldByRebuild "build failed") |> Expect.equal "a failed rebuild is a failure" FreshnessTone.Failed

    testCase "a loop held back by compile errors names the file and the count, and says the values are from the last good eval" <| fun _ ->
      let said = detail (Freshness.HeldByErrors("/src/Game.fs", 2))
      said |> Expect.stringContains "the file's name, not its path" "Game.fs"
      said |> Expect.stringContains "the count, plural" "2 errors"
      said |> Expect.stringContains "the values' source" "last good eval"
      detail (Freshness.HeldByErrors("Game.fs", 1)) |> Expect.stringContains "one error, singular" "1 error"
      detail (Freshness.HeldByRebuild "build failed") |> Expect.stringContains "the rebuild's reason" "build failed"

    testCase "the chip carries the word, the detail and the tone of the freshness, and nothing for an activity the loop has no say in" <| fun _ ->
      chipOf LiveTestActivity.Off |> Expect.isNone "live testing off has no chip"
      match chipOf (LiveTestActivity.Settled tally) with
      | Some chip ->
        chip.Word |> Expect.equal "the word" (word Freshness.Current)
        chip.Detail |> Expect.equal "the detail" (detail Freshness.Current)
        chip.Tone |> Expect.equal "the tone" (tone Freshness.Current)
      | None -> failtest "a settled loop has a chip"
  ]
