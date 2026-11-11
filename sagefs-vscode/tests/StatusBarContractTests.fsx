// WHY — measured on 0.6.670: the status bar named no project at all
// ("SageFs: session [REPL]") even when the session had loaded one, and nothing
// pinned the rule that its tooltip must never carry a `$(...)` codicon token
// (the one place VS Code renders it literally instead of as a glyph). These
// pin both: the project name comes through untouched, and every view this
// module builds has a codicon-free tooltip.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/BufferBridge.fs"
#load "../src/SessionScopePure.fs"
#load "../src/SessionsTreePure.fs"
#load "../src/StatusBarPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.SessionsTreePure
open SageFs.Vscode.StatusBarPure

let private input project workflow evals supervised restarts sessions : SessionStatusBarInput =
  { ProjectLabel = project
    SessionId = "c090e3e7"
    WorkflowLabel = workflow
    EvalCount = evals
    Supervised = supervised
    RestartCount = restarts
    SessionCount = sessions
    Health = SessionHealth.Healthy }

let tests =
  testList "VS Code status bar - pure text/tooltip contract" [

    testCase "WHY - the project name reaches both Text and Tooltip, because that is what was missing" <| fun _ ->
      let view = sessionView (input "SageTech" "REPL" 0 false 0 1)
      view.Text |> Expect.stringContains "project name in text" "SageTech"
      view.Tooltip |> Expect.stringContains "project name in tooltip" "SageTech"

    testCase "WHY - a session with no evals, no supervision, no restarts renders the bare form" <| fun _ ->
      (sessionView (input "SageTech" "REPL" 0 false 0 1)).Text
      |> Expect.equal "bare" "$(zap) SageFs: SageTech [REPL]"

    testCase "WHY - the eval count, supervision, and restart count all append when present" <| fun _ ->
      (sessionView (input "SageTech" "WebLive" 12 true 3 2)).Text
      |> Expect.equal "full" "$(zap) SageFs: SageTech [WebLive] [12] $(shield) 3↻"

    testCase "WHY - no codicon token ever reaches the tooltip, because VS Code prints it literally there" <| fun _ ->
      let view = sessionView (input "SageTech" "REPL" 12 true 3 2)
      Expect.isFalse "tooltip carries no $( token" (containsCodiconToken view.Tooltip)
      Expect.isTrue "text legitimately carries the icon" (containsCodiconToken view.Text)

    testCase "WHY - the session count reaches the tooltip, because that is what explains 'click for session menu'" <| fun _ ->
      (sessionView (input "SageTech" "REPL" 0 false 0 3)).Tooltip
      |> Expect.stringContains "count" "3 session(s)"

    testCase "WHY - the no-session view still names itself and stays codicon-free in the tooltip" <| fun _ ->
      let view = noSessionView false 0
      view.Text |> Expect.equal "no session text" "$(zap) SageFs: ready (no session)"
      Expect.isFalse "no session tooltip carries no $( token" (containsCodiconToken view.Tooltip)

    testCase "WHY - the no-session view still carries supervision/restart suffixes" <| fun _ ->
      (noSessionView true 2).Text
      |> Expect.equal "no session with suffixes" "$(zap) SageFs: ready (no session) $(shield) 2↻"

    // ── The daemon's verdict in the one control a user glances at constantly ──

    testCase "WHY - a Degraded session's icon stops being a green bolt, because the bolt was the lie" <| fun _ ->
      let view = sessionView { input "SageTech" "REPL" 4 false 0 1 with Health = SessionHealth.Degraded "nothing loaded" }
      view.Text |> Expect.stringContains "warning glyph" "$(warning)"
      Expect.isFalse "no bolt on a degraded session" (view.Text.Contains "$(zap)")

    testCase "WHY - the Degraded reason reaches the tooltip, so the remedy is one hover away" <| fun _ ->
      let view =
        sessionView
          { input "SageTech" "REPL" 4 false 0 1 with
              Health = SessionHealth.Degraded "run `dotnet build`, then hard_reset_fsi_session" }
      view.Tooltip |> Expect.stringContains "verdict" "Degraded"
      view.Tooltip |> Expect.stringContains "remedy" "then hard_reset_fsi_session"
      Expect.isFalse "the reason must not smuggle a codicon token into the tooltip" (containsCodiconToken view.Tooltip)

    testCase "WHY - Failed and Starting each get their own glyph, because the icon is total over the verdict" <| fun _ ->
      (sessionView { input "S" "REPL" 0 false 0 1 with Health = SessionHealth.Failed "worker exited" }).Text
      |> Expect.stringContains "error glyph" "$(error)"
      (sessionView { input "S" "REPL" 0 false 0 1 with Health = SessionHealth.Starting }).Text
      |> Expect.stringContains "spinner" "$(loading~spin)"

    // The tooltip is also the item's accessible name, and the only status bar text a screen reader
    // or a tour's text snapshot sees (the visible Text is read through it). The workflow used to be
    // in Text alone, so a switch to Hot Reload was invisible to both, and with several sessions on
    // one daemon nothing on the item said WHICH session it was talking about.
    testCase "WHY - the tooltip names the workflow, because it is the accessible name and the text is not read" <| fun _ ->
      (sessionView (input "SageTech" "Hot Reload" 0 false 0 1)).Tooltip
      |> Expect.stringContains "workflow" "[Hot Reload]"

    testCase "WHY - the tooltip names the session id, because several sessions share one daemon" <| fun _ ->
      (sessionView (input "SageTech" "REPL" 0 false 0 5)).Tooltip
      |> Expect.stringContains "session id" "session c090e3e7"

    testCase "WHY - the tooltip still reads in order: project, workflow, session, count, menu hint" <| fun _ ->
      (sessionView (input "SageTech" "Live Testing" 0 false 0 5)).Tooltip
      |> Expect.equal "order" "SageFs: SageTech [Live Testing] — session c090e3e7 — 5 session(s) — click for session menu"

    // WHY — the eval-performance item turns warning/error coloured above 100/500 ms and did nothing when
    // clicked: it had a tooltip and no command (roast-5 finding 9). It now opens the dashboard, where the
    // eval timeline lives, and the tooltip says so.
    testCase "WHY - the eval-performance item's click command is a command package.json contributes" <| fun _ ->
      let contributed =
        System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "package.json")))
          .RootElement.GetProperty("contributes").GetProperty("commands").EnumerateArray()
        |> Seq.map (fun c -> c.GetProperty("command").GetString())
        |> Set.ofSeq
      contributed |> Expect.contains "contributed" evalPerfCommand

    testCase "WHY - the eval-performance tooltip tells the person the item is clickable, codicon-free" <| fun _ ->
      evalPerfClickHint.ToLowerInvariant() |> Expect.stringContains "says click" "click"
      Expect.isFalse "no $( token" (containsCodiconToken evalPerfClickHint)

    testCase "WHY - the eval-performance command is the one Extension.fs actually assigns" <| fun _ ->
      System.IO.File.ReadAllText(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "src", "Extension.fs"))
      |> Expect.stringContains "assigned from the pure constant" "esb.command <- Some StatusBarPure.evalPerfCommand"

    // WHY — roast-5 finding 8: status text written outside refreshStatus set `text` with no accessible
    // label, so a screen reader said "error SageFs offline" (the codicon name leaks) or nothing useful.
    // Every status now builds (text, accessible name, tooltip) in one place; these pin all three for each.
    testCase "WHY - every status has text, an accessible name and a tooltip, because a site used to set only the text" <| fun _ ->
      for status in daemonStatusSamples do
        let view = statusView status
        Expect.isNotEmpty (sprintf "text of %A" status) view.Text
        Expect.isNotEmpty (sprintf "accessible name of %A" status) view.AccessibleName
        Expect.isNotEmpty (sprintf "tooltip of %A" status) view.Tooltip

    testCase "WHY - an accessible name is a sentence: starts with SageFs, three or more words, ends with a full stop, no codicon token" <| fun _ ->
      let views =
        [ yield! daemonStatusSamples |> List.map statusView
          yield sessionView (input "SageTech" "REPL" 12 true 3 2)
          yield sessionView { input "S" "REPL" 0 false 0 1 with Health = SessionHealth.Degraded "nothing loaded" }
          yield noSessionView false 0 ]
      for view in views do
        let name = view.AccessibleName
        Expect.isTrue (sprintf "starts with SageFs: %s" name) (name.StartsWith "SageFs")
        Expect.isTrue (sprintf "ends with a full stop: %s" name) (name.EndsWith ".")
        Expect.isFalse (sprintf "no double stop: %s" name) (name.Contains "..")
        Expect.isGreaterThanOrEqual (sprintf "three words: %s" name) (name.Split(' ').Length, 3)
        Expect.isFalse (sprintf "no codicon token: %s" name) (containsCodiconToken name)
        Expect.isFalse (sprintf "no tooltip token: %s" view.Tooltip) (containsCodiconToken view.Tooltip)

    testCase "WHY - the text of an errored or spinning status carries its icon and the accessible name does not name it" <| fun _ ->
      let view = statusView DaemonStatus.StartFailed
      view.Text |> Expect.stringContains "icon in text" "$(error)"
      Expect.isFalse "icon name not read aloud" (view.AccessibleName.Contains "error SageFs")

    testCase "WHY - the warm-up phase wording is one function, so the status bar and its label cannot disagree" <| fun _ ->
      warmupPhaseLabel "opening_namespaces" (Some "Foo") |> Expect.equal "detail" "Opening namespaces (Foo)"
      warmupPhaseLabel "opening_namespaces" None |> Expect.equal "no detail" "Opening namespaces..."
      warmupPhaseLabel "mystery" None |> Expect.equal "unknown phase" "Warming up..."
      (statusView (DaemonStatus.WarmingUp (Some "Scanning sources..."))).AccessibleName
      |> Expect.equal "sentence" "SageFs is warming up the session. Scanning sources."

    testCase "WHY - the eval-performance item has an accessible name, because its text is sparkline blocks read glyph by glyph" <| fun _ ->
      evalPerfAccessibleName 12 (Some 140.4)
      |> Expect.equal "with median" "SageFs eval performance, 12 evaluation(s), median 140 milliseconds. Click to open the dashboard."
      evalPerfAccessibleName 3 None
      |> Expect.equal "without median" "SageFs eval performance, 3 evaluation(s). Click to open the dashboard."

    testCase "WHY - no status bar text is set from a literal in Extension.fs, because each literal skipped the accessible name" <| fun _ ->
      let source = System.IO.File.ReadAllText(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "src", "Extension.fs"))
      let literalTexts =
        source.Split('\n')
        |> Array.filter (fun line -> System.Text.RegularExpressions.Regex.IsMatch(line, "(^|[^a-z])sb\\.text <- (sprintf )?\"\\$\\("))
      literalTexts |> Expect.isEmpty "no literal status text"

    testCase "WHY - no verdict on the wire renders the historical bolt, not an invented warning" <| fun _ ->
      (sessionView { input "SageTech" "REPL" 0 false 0 1 with Health = SessionHealth.Unknown }).Text
      |> Expect.equal "unchanged for an older daemon" "$(zap) SageFs: SageTech [REPL]"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
