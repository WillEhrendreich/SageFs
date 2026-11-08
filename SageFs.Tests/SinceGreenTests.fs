/// "Since green": what moved after the tests last settled with no failures. The comparison point moves only on a green settle, an edge for
/// each run, so while the loop is red, running or held back it stays where the tests were last known good.
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

/// One of every case of the loop's activity, each with the event it is for run 5.
let private everyActivity : (LiveTestActivity * SettleEvent) list =
  [ LiveTestActivity.Off, SettleEvent.NotSettled
    LiveTestActivity.Discovering, SettleEvent.NotSettled
    LiveTestActivity.DiscoveryFailed "x", SettleEvent.NotSettled
    LiveTestActivity.NoTestsFound [ "Expecto" ], SettleEvent.NotSettled
    LiveTestActivity.BlockedByCompileErrors("Game.fs", 1, green), SettleEvent.NotSettled
    LiveTestActivity.BlockedByFailedRebuild("build failed", green), SettleEvent.NotSettled
    LiveTestActivity.Rebuilding(2, green), SettleEvent.NotSettled
    LiveTestActivity.Running green, SettleEvent.NotSettled
    LiveTestActivity.Settled red, SettleEvent.RedSettled 5L
    LiveTestActivity.Settled TestTally.empty, SettleEvent.RedSettled 5L
    LiveTestActivity.Settled green, SettleEvent.GreenSettled 5L ]

/// The old way, as an oracle for the new one: every node of a walk by its place, compared as two maps.
let private flatten (snapshot: LiveValueSnapshot) : Map<string, string> =
  let rec nodes (binding: string) (labels: string list) (node: LiveValueNode) : (string * string) list =
    (pathKey binding labels, node.Preview) :: (node.Children |> List.collect (fun child -> nodes binding (labels @ [ child.Label ]) child))
  snapshot.Bindings |> List.collect (fun b -> nodes b.Name [] b.Root) |> Map.ofList

[<Tests>]
let tests =
  testList "since green" [

    testCase "only a settle with a test passed and none failed is green, a settle with a failure is red, and every other activity is no settle" <| fun _ ->
      for activity, expected in everyActivity do
        settleEvent 5L activity |> Expect.equal (sprintf "%A" activity) expected

    testCase "a green settle takes the walk it saw, and every other event leaves the comparison point where it was" <| fun _ ->
      let first = snapshot "10" "100"
      let later = snapshot "11" "90"
      let atFirst = settle Green.NoGreenYet (SettleEvent.GreenSettled 1L) first
      atFirst |> Expect.equal "the first green settle" (Green.GreenAt(1L, first))
      settle atFirst (SettleEvent.GreenSettled 2L) later |> Expect.equal "a green settle of the next run moves it" (Green.GreenAt(2L, later))
      settle atFirst (SettleEvent.RedSettled 2L) later |> Expect.equal "a red settle leaves it" atFirst
      settle atFirst SettleEvent.NotSettled later |> Expect.equal "no settle leaves it" atFirst

    testCase "WHY — taking the point is an edge: the same run settling green again, with the values moved since, takes nothing, and the next run takes it once" <| fun _ ->
      let atSettle = snapshot "10" "100"
      let moved = snapshot "11" "90"
      let settledOnce = settle Green.NoGreenYet (SettleEvent.GreenSettled 7L) atSettle
      let again = settle settledOnce (SettleEvent.GreenSettled 7L) moved
      again |> Expect.equal "the same generation again is not a new settle" settledOnce
      moves again moved |> Map.isEmpty |> Expect.isFalse "so what moved since the settle is still there to be seen"
      let next = settle again (SettleEvent.GreenSettled 8L) moved
      next |> Expect.equal "the next run's green settle takes the point once" (Green.GreenAt(8L, moved))
      moves next moved |> Expect.equal "and nothing has moved since it" Map.empty

    testProperty "WHY — after any run of the loop the comparison point is the walk at its last green settle, whatever happened since" <| fun (steps: (bool * byte) list) ->
      let walkAt (n: byte) = snapshot (string n) "100"
      let eventOf (generation: int64) (isGreen: bool) = if isGreen then SettleEvent.GreenSettled generation else SettleEvent.RedSettled generation
      // each step is a new run, so each has its own generation
      let actual = steps |> List.indexed |> List.fold (fun acc (i, (isGreen, n)) -> settle acc (eventOf (int64 i + 1L) isGreen) (walkAt n)) Green.NoGreenYet
      let expected =
        match steps |> List.indexed |> List.filter (fun (_, (isGreen, _)) -> isGreen) |> List.tryLast with
        | Some(i, (_, n)) -> Green.GreenAt(int64 i + 1L, walkAt n)
        | None -> Green.NoGreenYet
      actual = expected

    testCase "before the first green settle nothing has moved, and right after a green settle nothing has either" <| fun _ ->
      let now = snapshot "10" "100"
      moves Green.NoGreenYet now |> Expect.equal "no comparison point" Map.empty
      moves (settle Green.NoGreenYet (SettleEvent.GreenSettled 1L) now) now |> Expect.equal "just settled" Map.empty

    testCase "a node that shows something else differs, a node that was not there appeared, and one that is gone left" <| fun _ ->
      let atGreen = settle Green.NoGreenYet (SettleEvent.GreenSettled 1L) (snapshot "10" "100")
      moves atGreen (snapshot "11" "90")
      |> Expect.equal "the two that moved, at the places they moved"
        (Map.ofList
          [ pathKey "player" [ "Health" ], Moved.Differs("100", "90")
            pathKey "score" [], Moved.Differs("10", "11") ])
      let without =
        let now = snapshot "10" "100"
        { now with Bindings = (now.Bindings |> List.filter (fun b -> b.Name <> "score")) @ [ { Name = "extra"; TypeSignature = "int"; Root = leaf "extra" "1" } ] }
      moves atGreen without
      |> Expect.equal "one left, one appeared" (Map.ofList [ pathKey "extra" [], Moved.Appeared "1"; pathKey "score" [], Moved.Left "10" ])

    testCase "a field that is new on a record appears, and a field that is gone left, each at its own place" <| fun _ ->
      let atGreen = settle Green.NoGreenYet (SettleEvent.GreenSettled 1L) (snapshot "10" "100")
      let now =
        let walk = snapshot "10" "100"
        let player = walk.Bindings |> List.find (fun b -> b.Name = "player")
        let changed = { player with Root = { player.Root with Children = [ leaf "Health" "100"; leaf "Mana" "5" ] } }
        { walk with Bindings = [ walk.Bindings.[0]; changed ] }
      moves atGreen now
      |> Expect.equal "Mana appeared and Speed left"
        (Map.ofList [ pathKey "player" [ "Mana" ], Moved.Appeared "5"; pathKey "player" [ "Speed" ], Moved.Left "4.5" ])

    testProperty "WHY — the side-by-side comparison finds exactly the nodes the flattened comparison found: the same places that show something else" <| fun (a: byte) (b: byte) (c: byte) (d: byte) ->
      let before = snapshot (string a) (string b)
      let after = snapshot (string c) (string d)
      let flatBefore = flatten before
      let oracle =
        flatten after
        |> Map.toList
        |> List.choose (fun (key, now) ->
          match Map.tryFind key flatBefore with
          | Some was when was <> now -> Some(key, Moved.Differs(was, now))
          | _ -> None)
        |> Map.ofList
      moves (Green.GreenAt(1L, before)) after = oracle

    testCase "WHY — the binding a place belongs to is read with an ordinal search, because a culture search treats the separator as ignorable and finds it at 0" <| fun _ ->
      bindingOf (pathKey "player" [ "Health" ]) |> Expect.equal "a field's binding" "player"
      bindingOf (pathKey "score" []) |> Expect.equal "a binding's own place" "score"
      let atGreen = settle Green.NoGreenYet (SettleEvent.GreenSettled 1L) (snapshot "10" "100")
      movedBindings (moves atGreen (snapshot "11" "90")) |> Expect.equal "both bindings moved" (Set.ofList [ "player"; "score" ])
  ]
