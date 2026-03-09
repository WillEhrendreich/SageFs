namespace SageFs.Tests

open Expecto
open Expecto.Flip
open SageFs

module HotReloadVisibilityTests =

  let private defaultPanes = LayoutConfig.defaults.VisiblePanes

  let statusHintTests = testList "StatusHints hot reload visibility" [
    testCase "build with watchedCount=0 shows watch-all hint" <| fun _ ->
      let result = StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0
      result |> Expect.stringContains "should show watch-all" "watch-all"

    testCase "build with watchedCount>0 shows unwatch hint with count" <| fun _ ->
      let result = StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 5
      result |> Expect.stringContains "should show unwatch" "unwatch"
      result |> Expect.stringContains "should show count" "5"

    testCase "build with watchedCount>0 does NOT show watch-all" <| fun _ ->
      let result = StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 3
      result.Contains("watch-all")
      |> Expect.isFalse "should not show watch-all when files watched"

    testCase "build with watchedCount=0 does NOT show unwatch" <| fun _ ->
      let result = StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0
      result.Contains("unwatch")
      |> Expect.isFalse "should not show unwatch when nothing watched"

    testCase "findShort resolves HotReloadWatchAll keybinding" <| fun _ ->
      StatusHints.findShort KeyMap.defaults UiAction.HotReloadWatchAll
      |> Expect.isSome "HotReloadWatchAll should have keybinding"

    testCase "findShort resolves HotReloadUnwatchAll keybinding" <| fun _ ->
      StatusHints.findShort KeyMap.defaults UiAction.HotReloadUnwatchAll
      |> Expect.isSome "HotReloadUnwatchAll should have keybinding"

    testCase "hot reload hint uses correct keybinding format" <| fun _ ->
      let result = StatusHints.build KeyMap.defaults PaneId.Output defaultPanes 0
      // Ctrl+Alt+W = "^A-W"
      result |> Expect.stringContains "should contain ^A-W" "^A-W"

    testCase "hot reload unwatch hint uses correct keybinding format" <| fun _ ->
      let result = StatusHints.build KeyMap.defaults PaneId.Output defaultPanes 7
      // Ctrl+Alt+U = "^A-U"
      result |> Expect.stringContains "should contain ^A-U" "^A-U"

    testCase "existing hints still present when hot reload added" <| fun _ ->
      let result = StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0
      result |> Expect.stringContains "quit still present" "quit"
      result |> Expect.stringContains "eval still present" "eval"

    testCase "empty keymap still returns empty string" <| fun _ ->
      let result = StatusHints.build Map.empty PaneId.Editor Set.empty 0
      result |> Expect.equal "empty hints" ""
  ]

  [<Tests>]
  let tests = testList "HotReloadVisibility" [statusHintTests]
