/// The chrome of the live bindings pane gives way to the bindings as the dock gets shorter. Which chrome shows at which height is a pure
/// function, and the stylesheet draws it with container queries on the dock's height: the thresholds in the stylesheet are held to the
/// function's, so a change to one without the other fails here.
module SageFs.Tests.DockCompactionTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features.DockCompaction

let private rank (step: Compaction) : int = all |> List.findIndex (fun s -> s = step)

[<Tests>]
let tests =
  testList "the pane's chrome by dock height" [

    testCase "the steps are named by the height they start at, which only falls, and the roomiest has none" <| fun _ ->
      belowRem Compaction.Roomy |> Expect.isNone "the roomiest step applies at any taller height"
      let limits = all |> List.choose belowRem
      limits |> Expect.equal "limits fall as the steps get more compact" (limits |> List.sortDescending)
      limits |> List.distinct |> List.length |> Expect.equal "no two steps share a limit" limits.Length
      limits |> List.forall (fun l -> l > 5.0) |> Expect.isTrue "every limit is above the smallest dock the divider allows (5rem), so the smallest dock is the most compact step"

    testCase "a table of heights: the step, and a height exactly at a limit is still the roomier step" <| fun _ ->
      for rem, expected in
        [ 50.0, Compaction.Roomy; 26.0, Compaction.Roomy; 25.9, Compaction.NoSentence; 20.0, Compaction.NoSentence
          19.9, Compaction.OneLine; 14.0, Compaction.OneLine; 13.9, Compaction.Compact; 9.0, Compaction.Compact
          8.9, Compaction.IconsOnly; 5.0, Compaction.IconsOnly; 0.0, Compaction.IconsOnly ] do
        levelFor rem |> Expect.equal (sprintf "%g rem" rem) expected

    testProperty "WHY — a shorter dock never shows more chrome: the step only moves toward compact as the height falls" <| fun (a: uint16) (b: uint16) ->
      let low, high = (float (min a b)) / 10.0, (float (max a b)) / 10.0
      rank (levelFor low) >= rank (levelFor high)

    testCase "every step is reached by some height, and each step shows no more than the one above it" <| fun _ ->
      for step in all do
        let height =
          match belowRem step with
          | Some limit -> limit - 0.05
          | None -> 100.0
        // a step is reached just under its own limit unless a more compact step's limit is also above that height, which the limits' order rules out
        levelFor height |> Expect.equal (sprintf "%A is reached" step) step
      let shown (chrome: Chrome) = (match chrome.Sentence with | SentenceLine.Shown -> 1 | SentenceLine.InTooltip -> 0), (match chrome.Counts with | CountsPlace.OwnRow -> 1 | CountsPlace.OnTitleRow -> 0), (match chrome.Buttons with | ButtonLook.IconAndWord -> 1 | ButtonLook.IconWithName -> 0), (match chrome.Panel with | PanelPlacement.AboveTree -> 1 | PanelPlacement.ReplacesTree -> 0), (match chrome.Spacing with | Spacing.Roomy -> 2 | Spacing.Tight -> 1 | Spacing.Tightest -> 0)
      for upper, lower in List.pairwise all do
        let (s1, c1, b1, p1, g1), (s2, c2, b2, p2, g2) = shown (chromeFor upper), shown (chromeFor lower)
        (s2 <= s1 && c2 <= c1 && b2 <= b1 && p2 <= p1 && g2 <= g1) |> Expect.isTrue (sprintf "%A shows no more than %A" lower upper)

    testCase "the sentence is shown only at the roomiest step, the counts join the title below the first compaction, and the panel replaces the tree there too" <| fun _ ->
      for step in all do
        let chrome = chromeFor step
        (chrome.Sentence = SentenceLine.Shown) |> Expect.equal (sprintf "%A sentence" step) (step = Compaction.Roomy)
        (chrome.Counts = CountsPlace.OnTitleRow) |> Expect.equal (sprintf "%A counts" step) (rank step >= rank Compaction.OneLine)
        (chrome.Panel = PanelPlacement.ReplacesTree) |> Expect.equal (sprintf "%A panel" step) (rank step >= rank Compaction.OneLine)
      (chromeFor Compaction.IconsOnly).Buttons |> Expect.equal "icons with names at the smallest" ButtonLook.IconWithName
      (chromeFor Compaction.Compact).Buttons |> Expect.equal "icon and word until the smallest" ButtonLook.IconAndWord

    testCase "the stylesheet draws the same thresholds: one container query on the dock's height for each step, and the chrome's share of the dock" <| fun _ ->
      let css = File.ReadAllText(RepoPaths.repoPathFull [| "SageFs"; "dashboard.css" |])
      let found =
        Regex.Matches(css, @"@container\s+dock\s*\(height\s*<\s*([0-9.]+)rem\)")
        |> Seq.map (fun m -> float m.Groups.[1].Value)
        |> Set.ofSeq
      let wanted = all |> List.choose belowRem |> Set.ofList
      found |> Expect.equal "the stylesheet's container queries on the dock are the function's limits" wanted
      css |> Expect.stringContains "the chrome is capped at its share of the dock" (sprintf "max-height: %dcqh" ChromeSharePercent)
  ]
