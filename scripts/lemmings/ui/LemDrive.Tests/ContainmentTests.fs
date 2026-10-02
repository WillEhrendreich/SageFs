module LemDrive.Tests.ContainmentTests

open Expecto
open Expecto.Flip
open LemDrive

let private proc pid ppid sid cwd args : Leftovers.Proc =
  { Pid = pid; Ppid = ppid; Sid = sid; Cwd = cwd; Args = args }

let private run = "/tmp/lem/a-vsc-eval-01"

let private ownership : Leftovers.Ownership =
  { RunDir = run; Pids = [ 500 ]; Sids = [ 600 ]; Excluded = [ 1; 2 ] }

let private session id workdir status : Daemon.DaemonSession =
  { Id = id
    Status = status
    WorkingDirectory = workdir
    ProjectPaths = []
    Workflow = "REPL"
    EvalCount = 0
    Health = ""
    LastReload = "" }

[<Tests>]
let tests =
  testList "containment: what the window and the lemming cannot reach" [

    // ---- the guard past the daemon: shells and developer tools in the window --------------

    testCase "the guard refuses every way into a shell or developer tool by its palette title and id" <| fun _ ->
      for h in Guard.Hatch.all do
        for name in Guard.hatchNames h do
          Guard.check name |> Expect.isError (sprintf "%s refused (%A)" name h)

    testCase "the guard refuses the terminal commands a model would find in the palette" <| fun _ ->
      for text in [ "Terminal: Create New Terminal"; "View: Toggle Terminal"; "Developer: Toggle Developer Tools"
                    "Toggle Developer Tools"; "Developer: Open Process Explorer"; "Tasks: Run Task"
                    "Terminal: Create New Terminal in Editor Area"; "workbench.action.terminal.new"
                    "  Terminal:   Create   New   Terminal  recently used"; "New Terminal (Ctrl+Shift+`)" ] do
        Guard.check text |> Expect.isError (sprintf "'%s' refused" text)

    testCase "the guard does not stop ordinary editing or the SageFs commands" <| fun _ ->
      for text in [ "SageFs: Eval Selection"; "SageFs: Stop Session"; "View: Toggle Output"; "File: Save"
                    "let terminal = 1"; "Output: Focus on Output View"; "Test: Run All Tests"; "terminalServer" ] do
        Guard.check text |> Expect.isOk (sprintf "'%s' allowed" text)

    testCase "the guard refuses the chords that open a terminal or developer tools, and no others" <| fun _ ->
      let chord (text: string) = Chord.parse text |> Result.defaultWith failwith
      for text in [ "ctrl+`"; "ctrl+shift+`"; "ctrl+shift+i"; "ctrl+shift+c" ] do
        Guard.checkChord (chord text) |> Expect.isError (sprintf "%s refused" text)
      for text in [ "alt+enter"; "ctrl+shift+p"; "ctrl+j"; "ctrl+s"; "escape"; "f5"; "ctrl+z" ] do
        Guard.checkChord (chord text) |> Expect.isOk (sprintf "%s allowed" text)

    // ---- leftover processes: only what this run owns ------------------------------------

    testCase "a process is the run's when its pid, its session or its working directory is the run's" <| fun _ ->
      Leftovers.owned ownership (proc 500 1 1 "/" "Xvfb :81") |> Expect.isTrue "an exact pid the runner started"
      Leftovers.owned ownership (proc 701 1 600 "/" "code --type=renderer") |> Expect.isTrue "a child in the window's session"
      Leftovers.owned ownership (proc 702 1 1 (run + "/w/DemoEnv.Tests") "dotnet SageFs.Host.dll abc") |> Expect.isTrue "a session worker working in the run"

    testCase "a bystander whose command line only mentions the run path is not counted" <| fun _ ->
      let waiter = proc 800 1 800 "/home/will/Work" (sprintf "bash -c until [ -f %s/out/summary.json ]; do sleep 1; done" run)
      Leftovers.owned ownership waiter |> Expect.isFalse "a waiter shell is not the run's"

    testCase "a sibling run that shares the prefix is not the run's" <| fun _ ->
      Leftovers.owned ownership (proc 900 1 900 "/tmp/lem/a-vsc-eval-011/w" "dotnet x") |> Expect.isFalse "a-vsc-eval-011 is another run"

    testCase "the runner and its own ancestors are never counted" <| fun _ ->
      Leftovers.owned ownership (proc 1 0 1 (run + "/w") "run-vscode-lemming") |> Expect.isFalse "excluded"

    testCase "find keeps only the run's processes" <| fun _ ->
      let all = [ proc 500 1 1 "/" "Xvfb"; proc 800 1 800 "/x" "sleep"; proc 702 1 1 (run + "/w") "worker" ]
      Leftovers.find ownership all |> List.map (fun p -> p.Pid) |> Expect.equal "the display and the worker" [ 500; 702 ]

    // ---- binding the window to the run's own session ---------------------------------

    testCase "the run's session loads the tests project when the fixture has one" <| fun _ ->
      Binding.projectFor [ "DemoEnv/DemoEnv.fsproj"; "DemoEnv.Tests/DemoEnv.Tests.fsproj" ]
      |> Expect.equal "tests project" (Some "DemoEnv.Tests/DemoEnv.Tests.fsproj")
      Binding.projectFor [ "App/App.fsproj" ] |> Expect.equal "any project" (Some "App/App.fsproj")
      Binding.projectFor [] |> Expect.isNone "none"

    testCase "the create request names the run's workspace and an absolute project path" <| fun _ ->
      let body = Binding.createBody (run + "/w") (Some "DemoEnv.Tests/DemoEnv.Tests.fsproj")
      body |> Expect.stringContains "working directory" (sprintf "\"workingDirectory\":\"%s/w\"" run)
      body |> Expect.stringContains "project" (sprintf "%s/w/DemoEnv.Tests/DemoEnv.Tests.fsproj" run)
      Binding.createBody (run + "/w") None |> Expect.stringContains "no project, an empty list" "\"projects\":[]"

    testCase "the window is bound when the active row of the Sessions view is the run's session" <| fun _ ->
      let sessions = [ session "aaa" "/home/will/x" "Ready"; session "mine" (run + "/w") "Ready"; session "ccc" "/home/will/y" "Ready" ]
      let rows = [ "X — Ready"; "DemoEnv.Tests, DemoEnv — active · Ready"; "Y — Ready · 3 evals" ]
      Binding.boundTo sessions "mine" rows |> Expect.isOk "row 2 is active and is mine"

    testCase "the window is not bound when another session's row is active" <| fun _ ->
      let sessions = [ session "aaa" "/home/will/x" "Ready"; session "mine" (run + "/w") "Ready" ]
      match Binding.boundTo sessions "mine" [ "X — active · Ready"; "DemoEnv — Ready" ] with
      | Result.Error e -> e |> Expect.stringContains "says which row is active" "row 1"
      | Ok() -> failtest "bound to someone else's session"

    testCase "the window is not bound when the view has not caught up with the daemon's list" <| fun _ ->
      let sessions = [ session "aaa" "/home/will/x" "Ready"; session "mine" (run + "/w") "Ready" ]
      Binding.boundTo sessions "mine" [ "X — active · Ready" ] |> Expect.isError "one row for two sessions"
      Binding.boundTo sessions "gone" [ "X — Ready"; "Y — Ready" ] |> Expect.isError "no such session"

    testCase "a session that faulted is a reason to refuse the run, a warming one is a reason to wait" <| fun _ ->
      Binding.readiness (session "s" run "Ready") |> Expect.equal "ready" Binding.Ready
      Binding.readiness (session "s" run "Warming Up") |> Expect.equal "warming" Binding.Waiting
      Binding.readiness (session "s" run "Starting") |> Expect.equal "starting" Binding.Waiting
      (match Binding.readiness (session "s" run "Faulted") with | Binding.Refused _ -> true | _ -> false) |> Expect.isTrue "faulted"
  ]

[<Tests>]
let scoreTests =
  testList "scoring: what the summary says about the build and the findings" [

    testCase "a bridge built from another commit than the daemon is reported, with both commits" <| fun _ ->
      match LemScore.SharedDaemon.versionSkew "0.6.875+d7e110776ff30c34db60a19949da26dadd940819" "0.6.875+72b286a92e88b44d974844a060f6438be097a644" with
      | Some warning ->
        warning |> Expect.stringContains "names the daemon's commit" "d7e11077"
        warning |> Expect.stringContains "names the bridge's commit" "72b286a9"
      | None -> failtest "different commits are skew"

    testCase "the same commit, or a build that names none, is not reported as skew" <| fun _ ->
      LemScore.SharedDaemon.versionSkew "0.6.875+d7e110776ff" "0.6.875+d7e110776ff" |> Expect.isNone "same commit"
      LemScore.SharedDaemon.versionSkew "0.6.875" "0.6.875" |> Expect.isNone "no commit on either, same version"

    testCase "a different version without commits is skew too" <| fun _ ->
      LemScore.SharedDaemon.versionSkew "0.6.875" "0.6.880" |> Expect.isSome "versions differ"

    testCase "an editor lemming is not told it never called a SageFs MCP tool" <| fun _ ->
      let finding : LemScore.Types.FellOver =
        { Stage = LemScore.Types.Registration; Symptom = "never called a SageFs MCP tool"; Evidence = "tools used: []" }
      let other : LemScore.Types.FellOver = { Stage = LemScore.Types.Eval; Symptom = "x"; Evidence = "y" }
      let assessment : LemScore.Classify.Assessment =
        { Outcome = LemScore.Types.Fail; Reason = "r"; Provider = None; FellOver = [ finding; other ] }
      (LemScore.Classify.forHarness LemScore.Types.CmdcVscode assessment).FellOver
      |> Expect.equal "only the real finding stays" [ other ]
      (LemScore.Classify.forHarness LemScore.Types.CmdcNvim assessment).FellOver |> List.length |> Expect.equal "nvim too" 1
      (LemScore.Classify.forHarness LemScore.Types.Cmdc assessment).FellOver |> List.length |> Expect.equal "the MCP lemming keeps it" 2
  ]

[<Tests>]
let cleanupTests =
  testList "cleanup: the run's own session is not residue" [
    let info id : LemScore.SharedDaemon.SessionInfo =
      { Id = id; Status = "Ready"; WorkingDirectory = "/tmp/lem/a-vsc-eval-01/w"; ProjectPaths = []; Workflow = "REPL" }

    testCase "only the session the harness made: clean, and it is still stopped" <| fun _ ->
      let verdict, residue = LemScore.SharedDaemon.cleanupVerdict [ info "mine" ] [ "mine" ] []
      verdict |> Expect.equal "clean" LemScore.Types.Clean
      residue |> Expect.isEmpty "nothing the lemming left"

    testCase "a session the lemming made as well is residue" <| fun _ ->
      let verdict, residue = LemScore.SharedDaemon.cleanupVerdict [ info "mine"; info "extra" ] [ "mine" ] []
      verdict |> Expect.equal "residue" LemScore.Types.ResidueStopped
      residue |> List.map (fun s -> s.Id) |> Expect.equal "only the extra one" [ "extra" ]

    testCase "nothing under the run is clean" <| fun _ ->
      LemScore.SharedDaemon.cleanupVerdict [] [ "mine" ] [] |> fst |> Expect.equal "clean" LemScore.Types.Clean

    testCase "anything still listed after the stop is a failed cleanup, the run's own or not" <| fun _ ->
      LemScore.SharedDaemon.cleanupVerdict [ info "mine" ] [ "mine" ] [ info "mine" ] |> fst
      |> Expect.equal "failed" LemScore.Types.CleanupFailed
  ]

[<Tests>]
let outputTests =
  testList "the Output channel as the editor draws it" [
    testCase "a no-break space in a drawn line reads as the plain space a pattern is written with" <| fun _ ->
      let drawn = "Result: val it: int option = Some 7"
      OutputChannel.normalizeLine drawn |> Expect.equal "plain spaces" "Result: val it: int option = Some 7"
      System.Text.RegularExpressions.Regex.IsMatch(OutputChannel.normalizeLine drawn, "int option = Some 7") |> Expect.isTrue "the oracle's pattern matches"
  ]
