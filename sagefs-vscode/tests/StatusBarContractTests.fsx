// WHY — measured on 0.6.670: the status bar named no project at all
// ("SageFs: session [REPL]") even when the session had loaded one, and nothing
// pinned the rule that its tooltip must never carry a `$(...)` codicon token
// (the one place VS Code renders it literally instead of as a glyph). These
// pin both: the project name comes through untouched, and every view this
// module builds has a codicon-free tooltip.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/StatusBarPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.StatusBarPure

let private input project workflow evals supervised restarts sessions : SessionStatusBarInput =
  { ProjectLabel = project
    WorkflowLabel = workflow
    EvalCount = evals
    Supervised = supervised
    RestartCount = restarts
    SessionCount = sessions }

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
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
