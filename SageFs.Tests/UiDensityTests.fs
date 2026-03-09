module SageFs.Tests.UiDensityTests

open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let uiDensityTests =
  let defaultPanes = Set.ofList [ PaneId.Output; PaneId.Editor; PaneId.Sessions ]

  testList "UiDensity" [
    testList "cycle" [
      test "Minimal cycles to Normal" {
        UiDensity.cycle UiDensity.Minimal
        |> Expect.equal "should become Normal" UiDensity.Normal
      }

      test "Normal cycles to Full" {
        UiDensity.cycle UiDensity.Normal
        |> Expect.equal "should become Full" UiDensity.Full
      }

      test "Full cycles to Minimal" {
        UiDensity.cycle UiDensity.Full
        |> Expect.equal "should become Minimal" UiDensity.Minimal
      }

      test "three cycles returns to start" {
        UiDensity.Minimal
        |> UiDensity.cycle
        |> UiDensity.cycle
        |> UiDensity.cycle
        |> Expect.equal "should round-trip" UiDensity.Minimal
      }
    ]

    testList "label" [
      test "Minimal label" {
        UiDensity.label UiDensity.Minimal
        |> Expect.equal "should be minimal" "minimal"
      }

      test "Normal label" {
        UiDensity.label UiDensity.Normal
        |> Expect.equal "should be normal" "normal"
      }

      test "Full label" {
        UiDensity.label UiDensity.Full
        |> Expect.equal "should be full" "full"
      }
    ]

    testList "StatusHints density filtering" [
      test "Normal density shows context-sensitive hints" {
        let result =
          StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0 UiDensity.Normal
        result |> Expect.stringContains "should have quit" "quit"
        result |> Expect.stringContains "should have eval" "eval"
        result |> Expect.stringContains "should have focus" "focus"
      }

      test "Minimal density shows only quit and primary action" {
        let result =
          StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0 UiDensity.Minimal
        result |> Expect.stringContains "should have quit" "quit"
        result |> Expect.stringContains "should have eval" "eval"
        // Minimal should NOT have scroll, hot reload, focus, etc.
        Expect.isFalse "should not have scroll" (result.Contains "scroll")
        Expect.isFalse "should not have watch" (result.Contains "watch")
      }

      test "Full density shows all available hints" {
        let result =
          StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0 UiDensity.Full
        result |> Expect.stringContains "should have quit" "quit"
        result |> Expect.stringContains "should have eval" "eval"
        result |> Expect.stringContains "should have focus" "focus"
        result |> Expect.stringContains "should have scroll" "scroll"
        result |> Expect.stringContains "should have complete" "complete"
      }

      test "Minimal density has fewer hints than Normal" {
        let minimal =
          StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0 UiDensity.Minimal
        let normal =
          StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0 UiDensity.Normal
        let countPipes s = s |> Seq.filter ((=) '|') |> Seq.length
        (countPipes minimal, countPipes normal)
        |> Expect.isLessThan "minimal should have fewer separators than normal"
      }

      test "Full density has more hints than Normal" {
        let full =
          StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0 UiDensity.Full
        let normal =
          StatusHints.build KeyMap.defaults PaneId.Editor defaultPanes 0 UiDensity.Normal
        let countPipes s = s |> Seq.filter ((=) '|') |> Seq.length
        (countPipes normal, countPipes full)
        |> Expect.isLessThan "normal should have fewer separators than full"
      }

      test "Minimal in sessions pane still shows new-session" {
        let result =
          StatusHints.build KeyMap.defaults PaneId.Sessions defaultPanes 0 UiDensity.Minimal
        result |> Expect.stringContains "should show primary session action" "new-session"
      }

      test "CycleDensity action exists in UiAction" {
        // Verify the DU case exists by constructing it
        let action = UiAction.CycleDensity
        match action with
        | UiAction.CycleDensity -> ()
        | _ -> failtest "should match CycleDensity"
      }

      test "KeyMap.defaults has CycleDensity binding" {
        let hasDensity =
          KeyMap.defaults
          |> Map.exists (fun _ action -> action = UiAction.CycleDensity)
        hasDensity
        |> Expect.isTrue "should have a density cycling keybinding"
      }
    ]
  ]
