// WHY — measured with the tour harness on a running Falco app: after a save the window showed
// nothing about the patch. The daemon already says what a save did (the `ReloadReported` event and
// `lastReload` on every session: PatchPending "applied, the new body has not run yet", then Patched or
// NeverEntered; Restarted, RestartRequired and CompileFailed with their cause; the mechanism, metadata
// delta or detour; and whether the REPL is now behind the app). Nothing in the extension read it.
// These pin how the window says it: a status bar item that always shows the latest verdict, and ONE
// non-modal message per save, on the verdict that ends the save.
//
// Runs under plain `dotnet fsi` (no Fable), like the other contract tests here.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/ReloadReportPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.ReloadReportPure

let private wire state outcome patched considered message action mechanism : ReportWire =
  { State = state
    File = ""
    Outcome = outcome
    Patched = patched
    Considered = considered
    Message = message
    SuggestedAction = action
    Mechanism = mechanism }

let private finished outcome patched considered message action mechanism =
  reportOfWire (wire "finished" outcome patched considered message action mechanism)

let private behind = Freshness.BehindApp(2, [ "Program.hello" ], "The REPL has the build from before 2 saves.")

let private shown (item: StatusItem) =
  match item with
  | StatusItem.Shown view -> view
  | StatusItem.Hidden -> failtest "expected the status item to be shown"

let private notice (n: Notice) =
  match n with
  | Notice.Show shown -> shown
  | Notice.Quiet -> failtest "expected a message"

let tests =
  testList "VS Code hot reload report - what a save did, in the window" [

    // ── reading the wire ──

    testCase "WHY - every outcome token the daemon sends is read, and an unknown one is named, not judged" <| fun _ ->
      for token, expected in
        [ "Patched", Outcome.Patched
          "PatchPending", Outcome.PatchPending
          "NeverEntered", Outcome.NeverEntered
          "Restarted", Outcome.Restarted
          "NoEffect", Outcome.NoEffect
          "RestartRequired", Outcome.RestartRequired
          "CompileFailed", Outcome.CompileFailed
          "KeptLiveState", Outcome.KeptLiveState ] do
        Outcome.ofWire token |> Expect.equal token expected
      Outcome.ofWire "Teleported" |> Expect.equal "unknown" (Outcome.Unrecognised "Teleported")

    testCase "WHY - the mechanism is read from its field, never from the words of the message" <| fun _ ->
      Mechanism.ofWire "metadata-delta" |> Expect.equal "delta" Mechanism.MetadataDelta
      Mechanism.ofWire "detour" |> Expect.equal "detour" Mechanism.Detour
      Mechanism.ofWire "" |> Expect.equal "not a patch" Mechanism.NotAPatch
      Mechanism.ofWire "quantum" |> Expect.equal "unknown" (Mechanism.Unrecognised "quantum")

    testCase "WHY - a compiling save and a finished one are different reports" <| fun _ ->
      reportOfWire { wire "compiling" "" 0 0 "" "" "" with File = "/w/Program.fs" }
      |> Expect.equal "compiling" (Report.Compiling "/w/Program.fs")
      (finished "Patched" 2 2 "m" "" "metadata-delta")
      |> function
        | Report.Finished f -> f.Outcome |> Expect.equal "outcome" Outcome.Patched
        | other -> failtestf "expected Finished, got %A" other

    testCase "WHY - freshness reads InSync and BehindApp, and says nothing it was not told" <| fun _ ->
      Freshness.ofWire "InSync" 0 [] "" |> Expect.equal "in sync" Freshness.InSync
      Freshness.ofWire "BehindApp" 3 [ "A.b" ] "late" |> Expect.equal "behind" (Freshness.BehindApp(3, [ "A.b" ], "late"))
      Freshness.ofWire "" 0 [] "" |> Expect.equal "an older daemon sends none" Freshness.NotReported

    // ── the status bar item ──

    testCase "WHY - before any save the item is hidden, because there is nothing to report" <| fun _ ->
      statusItem Report.NoReloadYet Freshness.InSync |> Expect.equal "hidden" StatusItem.Hidden

    testCase "WHY - a patch that has not been seen running says so, because that is the daemon's own caution" <| fun _ ->
      let view = statusItem (finished "PatchPending" 2 2 "Applied. Exercise the changed code." "" "metadata-delta") Freshness.InSync |> shown
      view.Text |> Expect.stringContains "text" "applied"
      view.Text |> Expect.stringContains "not run" "not run yet"
      view.Tone |> Expect.equal "plain while pending" StatusTone.Plain

    testCase "WHY - Patched means the new body ran, and the item says how many and by what mechanism" <| fun _ ->
      let view = statusItem (finished "Patched" 3 3 "Patched." "" "metadata-delta") Freshness.InSync |> shown
      view.Text |> Expect.stringContains "count" "3/3"
      view.Text |> Expect.stringContains "word" "patched"
      view.Tooltip |> Expect.stringContains "mechanism" "metadata delta"
      view.Tone |> Expect.equal "plain" StatusTone.Plain

    testCase "WHY - a detour is named as one" <| fun _ ->
      (statusItem (finished "Patched" 1 1 "Patched." "" "detour") Freshness.InSync |> shown).Tooltip
      |> Expect.stringContains "detour" "detour"

    testCase "WHY - NeverEntered warns, because the patch is in place and nothing has run it" <| fun _ ->
      let view = statusItem (finished "NeverEntered" 1 2 "1 of 2 changed functions have not run." "Exercise that code path." "detour") Freshness.InSync |> shown
      view.Text |> Expect.stringContains "text" "never ran"
      view.Tone |> Expect.equal "warning" StatusTone.Warning
      view.Tooltip |> Expect.stringContains "message" "1 of 2 changed functions have not run."
      view.Tooltip |> Expect.stringContains "action" "Exercise that code path."

    testCase "WHY - a restart, a refused change and a compile failure each get their own word and tone" <| fun _ ->
      let v outcome = statusItem (finished outcome 0 1 "msg" "do this" "") Freshness.InSync |> shown
      (v "Restarted").Text |> Expect.stringContains "restarted" "restarted"
      (v "RestartRequired").Text |> Expect.stringContains "restart required" "restart required"
      (v "RestartRequired").Tone |> Expect.equal "failing" StatusTone.Failing
      (v "CompileFailed").Text |> Expect.stringContains "compile failed" "compile failed"
      (v "CompileFailed").Tone |> Expect.equal "failing" StatusTone.Failing
      (v "NoEffect").Text |> Expect.stringContains "no effect" "no effect"

    testCase "WHY - a save being compiled shows the file, so a slow build is not mistaken for nothing happening" <| fun _ ->
      let view = statusItem (Report.Compiling "/w/src/Program.fs") Freshness.InSync |> shown
      view.Text |> Expect.stringContains "compiling" "compiling"
      view.Text |> Expect.stringContains "file" "Program.fs"

    testCase "WHY - when the REPL is behind the app the tooltip says so and says what fixes it and what that costs" <| fun _ ->
      let view = statusItem (finished "Patched" 1 1 "Patched." "" "metadata-delta") behind |> shown
      view.Tooltip |> Expect.stringContains "behind" "REPL is behind"
      view.Tooltip |> Expect.stringContains "saves" "2 saves"
      view.Tooltip |> Expect.stringContains "fix" "Hard Reset"
      view.Tooltip |> Expect.stringContains "cost" "stops the running app"

    testCase "WHY - no codicon token in the tooltip, because VS Code prints it literally there" <| fun _ ->
      for outcome in [ "Patched"; "PatchPending"; "NeverEntered"; "Restarted"; "NoEffect"; "RestartRequired"; "CompileFailed"; "KeptLiveState" ] do
        (statusItem (finished outcome 1 1 "m" "a" "detour") behind |> shown).Tooltip
        |> fun t -> Expect.isFalse outcome (t.Contains "$(")

    // ── the one message per save ──

    testCase "WHY - a save that is still pending says nothing yet, because Patched or NeverEntered follows it" <| fun _ ->
      noticeFor (finished "PatchPending" 1 1 "Applied." "" "detour") Freshness.InSync |> Expect.equal "quiet" Notice.Quiet
      noticeFor (Report.Compiling "/w/a.fs") Freshness.InSync |> Expect.equal "quiet" Notice.Quiet
      noticeFor Report.NoReloadYet Freshness.InSync |> Expect.equal "quiet" Notice.Quiet

    testCase "WHY - Patched is an information message that names the mechanism and that the new code ran" <| fun _ ->
      let n = noticeFor (finished "Patched" 3 3 "Patched." "" "metadata-delta") Freshness.InSync |> notice
      n.Severity |> Expect.equal "info" Severity.Info
      n.Text |> Expect.stringContains "count" "3 of 3"
      n.Text |> Expect.stringContains "mechanism" "metadata delta"
      n.Text |> Expect.stringContains "ran" "ran"

    testCase "WHY - the message carries the REPL-behind warning when the patch left the REPL behind" <| fun _ ->
      (noticeFor (finished "Patched" 1 1 "Patched." "" "metadata-delta") behind |> notice).Text
      |> Expect.stringContains "behind" "REPL is behind"

    testCase "WHY - NeverEntered is a warning with the remedy in the body" <| fun _ ->
      let n = noticeFor (finished "NeverEntered" 0 1 "Nothing ran." "Exercise that code path, or restart." "detour") Freshness.InSync |> notice
      n.Severity |> Expect.equal "warning" Severity.Warning
      n.Text |> Expect.stringContains "message" "Nothing ran."
      n.Text |> Expect.stringContains "remedy" "Exercise that code path, or restart."

    testCase "WHY - a restart names its cause, which the daemon puts in the message and the action" <| fun _ ->
      let n = noticeFor (finished "RestartRequired" 0 1 "A type's shape changed." "Restart the app." "") Freshness.InSync |> notice
      n.Severity |> Expect.equal "warning" Severity.Warning
      n.Text |> Expect.stringContains "cause" "A type's shape changed."
      n.Text |> Expect.stringContains "remedy" "Restart the app."

    testCase "WHY - a compile failure is an error that keeps the last code serving, and offers the Output" <| fun _ ->
      let n = noticeFor (finished "CompileFailed" 0 0 "Program.fs(7,3): error FS0001" "" "") Freshness.InSync |> notice
      n.Severity |> Expect.equal "error" Severity.Error
      n.Text |> Expect.stringContains "error text" "FS0001"
      n.Actions |> Expect.contains "show output" NoticeAction.ShowOutput

    testCase "WHY - NoEffect and KeptLiveState are information, never alarms" <| fun _ ->
      (noticeFor (finished "NoEffect" 0 0 "Nothing changed." "" "") Freshness.InSync |> notice).Severity |> Expect.equal "info" Severity.Info
      (noticeFor (finished "KeptLiveState" 0 0 "Kept." "" "") Freshness.InSync |> notice).Severity |> Expect.equal "info" Severity.Info

    testCase "WHY - a behind REPL offers the hard reset, since that is the one thing that brings it level" <| fun _ ->
      (noticeFor (finished "Patched" 1 1 "Patched." "" "metadata-delta") behind |> notice).Actions
      |> Expect.contains "hard reset" NoticeAction.HardResetRebuild

    testCase "WHY - the action captions are verbs, never the sentence of remedy" <| fun _ ->
      NoticeAction.caption NoticeAction.ShowOutput |> Expect.equal "show output" "Show Output"
      NoticeAction.caption NoticeAction.HardResetRebuild |> Expect.equal "hard reset" "Hard Reset (Rebuild)"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
