/// Where the Since green point is taken. The tab's comparison point is the values at the last settle of the test loop with a pass and no
/// failure, and it must not depend on who was looking: it is taken by the daemon where the loop settles (`observe`), and drawing the tab
/// only reads it (`said`). A twin that takes the point while it draws is the old behaviour, and the scenario below is the one it gets wrong.
module SageFs.Tests.SinceGreenRecorderTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTestActivity
open SageFs.Features.LiveValueTree
open SageFs.Features.Tweak
open SageFs.Features.Tweak.SinceGreen
open SageFs.Server
open SageFs.Server.LiveBindingsSinceGreen

let private leaf (label: string) (preview: string) : LiveValueNode =
  { Label = label
    TypeName = "int"
    Preview = preview
    Kind = NodeKind.Leaf
    Children = []
    BestEffort = false
    Assign = Assignment.NotKnown
    Depth = 0 }

/// The walk with `score` showing `value`.
let private walkOf (value: string) : LiveValueSnapshot =
  { SessionId = "s"
    Generation = 1L
    Bindings = [ { Name = "score"; TypeSignature = "int"; Root = leaf "score" value } ]
    Truncated = false
    CapturedAt = DateTimeOffset.UnixEpoch }

let private green : LiveTestActivity = LiveTestActivity.Settled { TestTally.empty with Passed = 3 }

let private red : LiveTestActivity = LiveTestActivity.Settled { TestTally.empty with Passed = 2; Failed = 1 }

/// What the tab should say for a point taken at a settle that saw `atSettle`, with the walk showing `now`.
let private expectedSaid (atSettle: string) (now: string) : LiveBindingsViewPanel.GreenSaid =
  LiveBindingsViewPanel.GreenSaid.Moved(moves (Green.GreenAt(0L, (walkOf atSettle))) ((walkOf now)))

/// The old behaviour, as a twin: the point is taken while the tab is drawn, from the activity the draw sees.
let private renderThatRecords (recorder: Recorder) (sessionId: string) (generation: int64) (activity: LiveTestActivity) (walk: LiveValueSnapshot) =
  observe recorder sessionId generation activity walk
  said (pointOf recorder sessionId) walk

[<Tests>]
let tests =
  testList "the Since green point is taken where the loop settles" [

    testCase "WHY — the tests go green and then red with nothing drawing the tab in between, and the tab still compares against the green settle" <| fun _ ->
      let recorder = create ()
      observe recorder "s" 1L green (walkOf "10")
      observe recorder "s" 2L red (walkOf "11")
      said (pointOf recorder "s") (walkOf "11") |> Expect.equal "score moved from 10 to 11 since the green settle" (expectedSaid "10" "11")

    testCase "the twin that takes the point while drawing never saw the green settle, and says there is none: this is what the scenario above would read as" <| fun _ ->
      let recorder = create ()
      renderThatRecords recorder "s" 2L red (walkOf "11")
      |> Expect.equal "no point was taken, so the tab says nothing is known" LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet

    testCase "before any green settle the tab says there is nothing to compare with, and a red settle takes nothing" <| fun _ ->
      let recorder = create ()
      said (pointOf recorder "s") (walkOf "10") |> Expect.equal "no settle" LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet
      observe recorder "s" 1L red (walkOf "10")
      said (pointOf recorder "s") (walkOf "10") |> Expect.equal "a red settle is not a point" LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet

    testCase "a second green settle replaces the point exactly once, and the same run read again takes nothing" <| fun _ ->
      let recorder = create ()
      observe recorder "s" 1L green (walkOf "10")
      pointOf recorder "s" |> Expect.equal "the first settle's values" (Green.GreenAt(1L, (walkOf "10")))
      observe recorder "s" 1L green (walkOf "11")
      pointOf recorder "s" |> Expect.equal "the same run again, with the values moved: nothing" (Green.GreenAt(1L, (walkOf "10")))
      observe recorder "s" 2L green (walkOf "12")
      pointOf recorder "s" |> Expect.equal "the next run's green settle replaces it" (Green.GreenAt(2L, (walkOf "12")))
      observe recorder "s" 2L red (walkOf "13")
      pointOf recorder "s" |> Expect.equal "and a red read of that run takes nothing" (Green.GreenAt(2L, (walkOf "12")))

    testCase "each session has its own point, and a session that is gone has none" <| fun _ ->
      let recorder = create ()
      observe recorder "a" 1L green (walkOf "10")
      observe recorder "b" 1L green (walkOf "20")
      pointOf recorder "a" |> Expect.equal "a" (Green.GreenAt(1L, (walkOf "10")))
      pointOf recorder "b" |> Expect.equal "b" (Green.GreenAt(1L, (walkOf "20")))
      forget recorder "a"
      pointOf recorder "a" |> Expect.equal "forgotten" Green.NoGreenYet
      pointOf recorder "b" |> Expect.equal "the other is kept" (Green.GreenAt(1L, (walkOf "20")))

    testCase "WHY — drawing the tab never changes the point, however often and whatever the loop says at the time" <| fun _ ->
      let recorder = create ()
      observe recorder "s" 1L green (walkOf "10")
      let before = pointOf recorder "s"
      for value in [ "10"; "11"; "12" ] do
        said (pointOf recorder "s") (walkOf value) |> ignore
      pointOf recorder "s" |> Expect.equal "the point is where the loop left it" before

    testProperty "WHY — over any order of settles, repeats and draws, every draw says what the last green settle's values compared with the walk then say, whoever was looking" <| fun (events: (byte * bool * byte) list) ->
      let recorder = create ()
      // the model: the generation of the latest run, and the point as (generation, values) taken at a green settle of a generation not yet taken
      let step (generation: int64, point: (int64 * string) option, ok: bool) (kind: byte, isGreen: bool, value: byte) =
        let activity = if isGreen then green else red
        let text = string value
        match int kind % 3 with
        | 0 ->
          let next = generation + 1L
          observe recorder "s" next activity (walkOf text)
          let taken =
            match isGreen with
            | true -> Some(next, text)
            | false -> point
          next, taken, ok
        | 1 when generation > 0L ->
          observe recorder "s" generation activity (walkOf text)
          let taken =
            match isGreen, point with
            | true, Some(g, _) when g = generation -> point
            | true, _ -> Some(generation, text)
            | false, _ -> point
          generation, taken, ok
        | 1 -> generation, point, ok
        | _ ->
          let actual = said (pointOf recorder "s") (walkOf text)
          let expected =
            match point with
            | None -> LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet
            | Some(g, atSettle) -> LiveBindingsViewPanel.GreenSaid.Moved(moves (Green.GreenAt(g, (walkOf atSettle))) ((walkOf text)))
          generation, point, ok && actual = expected
      let _, _, ok = events |> List.fold step (0L, None, true)
      ok
  ]
