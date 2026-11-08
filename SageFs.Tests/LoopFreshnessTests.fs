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

module private PaneDrawing =
  open System
  open Falco.Markup
  open SageFs
  open SageFs.Features
  open SageFs.Features.LiveValueTree
  open SageFs.Features.LiveBindingsPane
  open SageFs.Server
  open SageFs.Server.LiveBindingsDock

  let walked : BindingsSource =
    let leaf : LiveValueNode =
      { Label = "x"; TypeName = "int"; Preview = "1"; Kind = NodeKind.Leaf; Children = []; BestEffort = false; Assign = Assignment.NotKnown; Depth = 0 }
    let view : PaneView =
      { Snapshot =
          { SessionId = "abcd1234"
            Generation = 1L
            Bindings = [ { Name = "x"; TypeSignature = "int"; Root = leaf } ]
            Truncated = false
            CapturedAt = DateTimeOffset.UnixEpoch }
        Notes = { Mode = ValueWalk.standard; Click = NoClickYet } }
    WalkedBindings view

  /// The dock with the chip the loop gave, as the page decodes it.
  let dockWith (chip: Chip option) : string =
    let look = SageFs.View.recommended SageFs.ViewWorkflow.Repl
    let controls = { LiveBindingsViewPanel.render "abcd1234" SageFs.ViewWorkflow.Repl { View = look; Sources = Map.empty } with Freshness = (match chip with | Some c -> LiveBindingsViewPanel.LoopSaid.Said c | None -> LiveBindingsViewPanel.LoopSaid.NothingSaid) }
    renderDockWith look (Some controls) SageFs.Features.Tweak.BindingTweak.TweakView.none DockPanes.SessionInView "abcd1234" walked None
    |> renderNode
    |> Net.WebUtility.HtmlDecode

[<Tests>]
let drawingTests =
  testList "the pane draws its freshness" [

    testCase "a loop with no say draws no chip, and each freshness draws one chip with its word, its sentence and its tone's class" <| fun _ ->
      (PaneDrawing.dockWith None).Contains "live-freshness" |> Expect.isFalse "no chip without a loop"
      for freshness in
        [ Freshness.Updating
          Freshness.Current
          Freshness.HeldByErrors("Game.fs", 2)
          Freshness.HeldByRebuild "build failed" ] do
        let chip = { Word = word freshness; Detail = detail freshness; Tone = tone freshness }
        let html = PaneDrawing.dockWith (Some chip)
        html |> Expect.stringContains "the chip" "data-testid=\"live-freshness\""
        html |> Expect.stringContains "the word is markup text" (sprintf ">%s<" chip.Word)
        html |> Expect.stringContains "the sentence is the tooltip" (sprintf "title=\"%s\"" chip.Detail)
        html |> Expect.stringContains "the tone's class" (SageFs.Server.LiveBindingsDock.freshnessClass chip.Tone)
        (html.Split([| "data-testid=\"live-freshness\"" |], System.StringSplitOptions.None).Length - 1) |> Expect.equal "one chip" 1

    testCase "WHY — each tone has its own non-empty class, so a state is never told apart by colour alone and never shares a colour with another" <| fun _ ->
      let classes =
        [ FreshnessTone.Current; FreshnessTone.Moving; FreshnessTone.Held; FreshnessTone.Failed ]
        |> List.map SageFs.Server.LiveBindingsDock.freshnessClass
      classes |> List.forall (fun c -> c <> "") |> Expect.isTrue "every tone has a class"
      (List.distinct classes).Length |> Expect.equal "one class each" classes.Length
  ]
