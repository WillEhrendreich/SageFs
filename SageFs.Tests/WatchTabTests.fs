/// The built-in tabs of the live bindings pane: All, Pinned and Changes. A tab is a question put to each top-level binding, and the answer is
/// a pure function of whether it is pinned, whether it holds a change, and which tab is open. The page evaluates the same function as an
/// expression, so a test holds the expression to the function.
module SageFs.Tests.WatchTabTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.Tweak
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.ChangesStrip
open SageFs.Features.Tweak.WatchTabs
open SageFs.Tests.BindingTweakTests

[<Tests>]
let tests =
  testList "the tabs of the live bindings pane" [

    testCase "every tab has its own token and label, and the token reads back; anything else is refused with the set" <| fun _ ->
      (WatchTab.all |> List.map WatchTab.token |> List.distinct).Length |> Expect.equal "distinct tokens" WatchTab.all.Length
      (WatchTab.all |> List.map WatchTab.label |> List.distinct).Length |> Expect.equal "distinct labels" WatchTab.all.Length
      for tab in WatchTab.all do
        WatchTab.ofToken (WatchTab.token tab) |> Expect.equal "round trip" (Ok tab)
      match WatchTab.ofToken "starred" with
      | Error why -> ConfigError.describe why |> Expect.stringContains "names the word" "starred"
      | Ok tab -> failtestf "'starred' read as %A" tab

    testCase "WHY — All lists every binding, Pinned lists exactly the pinned ones, Changes the ones that hold a change, Since green the ones that moved" <| fun _ ->
      for pinned in [ false; true ] do
        for changed in [ false; true ] do
          for moved in [ false; true ] do
            lists WatchTab.All pinned changed moved |> Expect.isTrue "All lists everything"
            lists WatchTab.Pinned pinned changed moved |> Expect.equal "Pinned follows the pin" pinned
            lists WatchTab.Changes pinned changed moved |> Expect.equal "Changes follows the change" changed
            lists WatchTab.SinceGreen pinned changed moved |> Expect.equal "Since green follows the move" moved

    testCase "WHY — the expression the page evaluates is the function: with true and false put where the page's own expressions go, it answers what `lists` answers" <| fun _ ->
      let literal (value: bool) = match value with | true -> "true" | false -> "false"
      for tab in WatchTab.all do
        for pinned in [ false; true ] do
          for changed in [ false; true ] do
            for moved in [ false; true ] do
              listsJs tab (literal pinned) (literal changed) (literal moved)
              |> Expect.equal (sprintf "%s pinned=%b changed=%b moved=%b" (WatchTab.token tab) pinned changed moved) (literal (lists tab pinned changed moved))

    testCase "an empty tab has words, so it is never a blank pane" <| fun _ ->
      for tab in WatchTab.all do
        ((WatchTab.emptyWords tab).Length, 10) |> Expect.isGreaterThan (sprintf "%s has words" (WatchTab.token tab))

    testCase "the Changes tab lists the binding of every tweak and every value the program changed, whether the row is the binding or one of its fields" <| fun _ ->
      let place = real "a.fs" "gravity" "9.8"
      let tweak : Tweak = { Row = { Binding = "gravity"; Labels = [] }; Place = place; Before = "9.8"; After = "9.5"; Via = PushVia.RebindBinding }
      let evolved : Evolution = { Row = { Binding = "player"; Labels = [ "Health" ] }; Place = place; Initial = "100"; Now = "62" }
      changedBindings { Tweaks = [ tweak ]; Evolved = [ evolved ] } |> Expect.equal "both bindings" (Set.ofList [ "gravity"; "player" ])
      changedBindings Changes.empty |> Expect.isEmpty "nothing changed, nothing listed"
  ]
