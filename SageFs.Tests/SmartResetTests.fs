module SageFs.Tests.SmartResetTests

open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let smartResetOutcomeTests =
  testList "SmartReset outcome" [

    testCase "describe SoftResetSucceeded" <| fun _ ->
      SmartReset.Outcome.SoftResetSucceeded
      |> SmartReset.describe
      |> Expect.stringContains "should mention cleared" "cleared"

    testCase "describe EscalatedToHardReset" <| fun _ ->
      SmartReset.Outcome.EscalatedToHardReset "rebuilt OK"
      |> SmartReset.describe
      |> Expect.stringContains "should mention escalated" "escalated"

    testCase "describe AllResetsFailed" <| fun _ ->
      SmartReset.Outcome.AllResetsFailed("soft boom", "hard boom")
      |> SmartReset.describe
      |> Expect.stringContains "should mention both errors" "soft boom"
  ]

[<Tests>]
let smartResetExecuteTests =
  testList "SmartReset execute" [

    testTask "soft reset succeeds → no escalation" {
      let mutable hardCalled = false
      let soft () = task { return Ok () }
      let hard () = task { hardCalled <- true; return Ok "hard done" }

      let! outcome = SmartReset.execute soft hard
      outcome
      |> Expect.equal "should be SoftResetSucceeded" SmartReset.Outcome.SoftResetSucceeded

      hardCalled
      |> Expect.isFalse "hard reset should NOT have been called"
    }

    testTask "soft reset fails → escalates to hard reset" {
      let soft () = task { return Error "FSI stuck" }
      let hard () = task { return Ok "rebuilt and reloaded" }

      let! outcome = SmartReset.execute soft hard
      outcome
      |> Expect.equal
        "should be EscalatedToHardReset"
        (SmartReset.Outcome.EscalatedToHardReset "rebuilt and reloaded")
    }

    testTask "both fail → AllResetsFailed with both errors" {
      let soft () = task { return Error "soft fail" }
      let hard () = task { return Error "hard fail" }

      let! outcome = SmartReset.execute soft hard
      outcome
      |> Expect.equal
        "should be AllResetsFailed"
        (SmartReset.Outcome.AllResetsFailed("soft fail", "hard fail"))
    }

    testTask "hard reset receives rebuild=false by default" {
      let mutable receivedRebuild = None
      let soft () = task { return Error "nope" }
      let hard () = task { receivedRebuild <- Some false; return Ok "done" }

      let! _ = SmartReset.execute soft hard

      receivedRebuild
      |> Expect.equal "should have called hard reset" (Some false)
    }

  ]

[<Tests>]
let smartResetKeybindingTests =
  testList "SmartReset keybinding" [

    testCase "SmartReset exists in EditorAction DU" <| fun _ ->
      let action = EditorAction.SmartReset
      match action with
      | EditorAction.SmartReset -> ()
      | _ -> failtest "SmartReset should be an EditorAction case"

    testCase "SmartReset has a keybinding in defaults" <| fun _ ->
      let hasBinding =
        KeyMap.defaults
        |> Map.exists (fun _ action ->
          match action with
          | UiAction.Editor EditorAction.SmartReset -> true
          | _ -> false)
      hasBinding
      |> Expect.isTrue "SmartReset should have a default keybinding"

    testCase "SmartReset keybinding is Ctrl+Shift+R" <| fun _ ->
      let key = KeyCombo.ctrlShift System.ConsoleKey.R
      KeyMap.defaults
      |> Map.tryFind key
      |> Expect.equal "Ctrl+Shift+R should map to SmartReset"
        (Some (UiAction.Editor EditorAction.SmartReset))
  ]
