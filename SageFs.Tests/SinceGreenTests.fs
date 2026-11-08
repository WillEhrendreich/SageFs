/// "Since green": what moved after the tests last settled with no failures. The comparison point moves only on a green settle, so while the
/// loop is red, running or held back it stays where the tests were last known good.
module SageFs.Tests.SinceGreenTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.LiveTestActivity
open SageFs.Features.LiveValueTree
open SageFs.Features.Tweak.SinceGreen

let private green : TestTally = { TestTally.empty with Passed = 3 }

let private red : TestTally = { TestTally.empty with Passed = 2; Failed = 1 }

let private leaf (label: string) (preview: string) : LiveValueNode =
  { Label = label
    TypeName = "int"
    Preview = preview
    Kind = NodeKind.Leaf
    Children = []
    BestEffort = false
    Assign = Assignment.NotKnown
    Depth = 0 }

let private snapshot (score: string) (health: string) : LiveValueSnapshot =
  let player = { leaf "player" "{ ... }" with Kind = NodeKind.Record; Children = [ leaf "Health" health; leaf "Speed" "4.5" ] }
  { SessionId = "s"
    Generation = 1L
    Bindings =
      [ { Name = "score"; TypeSignature = "int"; Root = leaf "score" score }
        { Name = "player"; TypeSignature = "P"; Root = player } ]
    Truncated = false
    CapturedAt = DateTimeOffset.UnixEpoch }

/// One of every case of the loop's activity, each with whether it is a green settle.
let private everyActivity : (LiveTestActivity * bool) list =
  [ LiveTestActivity.Off, false
    LiveTestActivity.Discovering, false
    LiveTestActivity.DiscoveryFailed "x", false
    LiveTestActivity.NoTestsFound [ "Expecto" ], false
    LiveTestActivity.BlockedByCompileErrors("Game.fs", 1, green), false
    LiveTestActivity.BlockedByFailedRebuild("build failed", green), false
    LiveTestActivity.Rebuilding(2, green), false
    LiveTestActivity.Running green, false
    LiveTestActivity.Settled red, false
    LiveTestActivity.Settled TestTally.empty, false
    LiveTestActivity.Settled green, true ]

[<Tests>]
let tests =
  testList "since green" [

    testCase "only a settle with a test passed and none failed is green: every other activity of the loop is not" <| fun _ ->
      for activity, expected in everyActivity do
        isGreenSettle activity |> Expect.equal (sprintf "%A" activity) expected

    testCase "a green settle takes the values it saw, and every other activity leaves the comparison point where it was" <| fun _ ->
      let first = valuesOf (snapshot "10" "100")
      let later = valuesOf (snapshot "11" "90")
      let atFirst = update Green.NoGreenYet (LiveTestActivity.Settled green) first
      atFirst |> Expect.equal "the first green settle" (Green.GreenAt first)
      for activity, isGreen in everyActivity do
        match isGreen with
        | true -> update atFirst activity later |> Expect.equal "a green settle moves it" (Green.GreenAt later)
        | false -> update atFirst activity later |> Expect.equal (sprintf "%A leaves it" activity) atFirst

    testProperty "WHY — after any run of the loop the comparison point is the values at its last green settle, whatever happened since" <| fun (steps: (bool * byte) list) ->
      let valuesAt (n: byte) = valuesOf (snapshot (string n) "100")
      let activityOf (isGreen: bool) = if isGreen then LiveTestActivity.Settled green else LiveTestActivity.Settled red
      let actual = steps |> List.fold (fun acc (isGreen, n) -> update acc (activityOf isGreen) (valuesAt n)) Green.NoGreenYet
      let expected =
        match steps |> List.filter fst |> List.tryLast with
        | Some(_, n) -> Green.GreenAt(valuesAt n)
        | None -> Green.NoGreenYet
      actual = expected

    testCase "before the first green settle nothing has moved, and right after a green settle nothing has either" <| fun _ ->
      let now = valuesOf (snapshot "10" "100")
      moves Green.NoGreenYet now |> Expect.equal "no comparison point" Map.empty
      moves (update Green.NoGreenYet (LiveTestActivity.Settled green) now) now |> Expect.equal "just settled" Map.empty

    testCase "a node that shows something else differs, a node that was not there appeared, and one that is gone left" <| fun _ ->
      let atGreen = update Green.NoGreenYet (LiveTestActivity.Settled green) (valuesOf (snapshot "10" "100"))
      moves atGreen (valuesOf (snapshot "11" "90"))
      |> Expect.equal "the two that moved, at the places they moved"
        (Map.ofList
          [ pathKey "player" [ "Health" ], Moved.Differs("100", "90")
            pathKey "score" [], Moved.Differs("10", "11") ])
      let without = valuesOf (snapshot "10" "100") |> Map.remove (pathKey "score" []) |> Map.add (pathKey "extra" []) "1"
      moves atGreen without
      |> Expect.equal "one left, one appeared" (Map.ofList [ pathKey "extra" [], Moved.Appeared "1"; pathKey "score" [], Moved.Left "10" ])

    testCase "a walk is read node by node, down to the fields of a record, each at its own place" <| fun _ ->
      valuesOf (snapshot "10" "100")
      |> Expect.equal "every node"
        (Map.ofList
          [ pathKey "score" [], "10"
            pathKey "player" [], "{ ... }"
            pathKey "player" [ "Health" ], "100"
            pathKey "player" [ "Speed" ], "4.5" ])

    testCase "WHY — the binding a place belongs to is read with an ordinal search, because a culture search treats the separator as ignorable and finds it at 0" <| fun _ ->
      bindingOf (pathKey "player" [ "Health" ]) |> Expect.equal "a field's binding" "player"
      bindingOf (pathKey "score" []) |> Expect.equal "a binding's own place" "score"
      let atGreen = update Green.NoGreenYet (LiveTestActivity.Settled green) (valuesOf (snapshot "10" "100"))
      movedBindings (moves atGreen (valuesOf (snapshot "11" "90"))) |> Expect.equal "both bindings moved" (Set.ofList [ "player"; "score" ])
  ]
