module LemDrive.Tests.RunnerTests

open Expecto
open Expecto.Flip
open LemDrive

let private session id workdir evals : Daemon.DaemonSession =
  { Id = id
    Status = "Ready"
    WorkingDirectory = workdir
    ProjectPaths = []
    Workflow = "REPL"
    EvalCount = evals
    Health = ""
    LastReload = "" }

[<Tests>]
let tests =
  testList "the runner side" [
    testCase "the sessions list is read: working directory, eval count, workflow" <| fun _ ->
      let body = """{"sessions":[{"id":"ab12","status":"Ready","workingDirectory":"/tmp/lem/x-vsc-eval-01/w","evalCount":3,"workflowLabel":"Live Testing","loadedProjects":["/tmp/lem/x-vsc-eval-01/w/DemoEnv/DemoEnv.fsproj"],"health":{"status":"Healthy"},"lastReload":null}]}"""
      match Daemon.parseSessions body with
      | Ok [ s ] ->
        s.Id |> Expect.equal "id" "ab12"
        s.EvalCount |> Expect.equal "evals" 3
        s.Workflow |> Expect.equal "workflow" "Live Testing"
        Daemon.belongsTo "/tmp/lem/x-vsc-eval-01" s |> Expect.isTrue "under the run directory"
        Daemon.belongsTo "/tmp/lem/x-vsc-eval-02" s |> Expect.isFalse "not under a sibling run"
      | other -> failtestf "expected one session, got %A" other

    testCase "a run directory does not claim a sibling that shares its prefix" <| fun _ ->
      Daemon.belongsTo "/tmp/lem/a-vsc-eval-01" (session "s" "/tmp/lem/a-vsc-eval-011/w" 0)
      |> Expect.isFalse "a-vsc-eval-011 is another run"

    testCase "evals that rose outside the run directory are found, the run's own are not" <| fun _ ->
      let run = "/tmp/lem/a-vsc-eval-01"
      let before = [ session "mine" (run + "/w") 0; session "other" "/home/will/Work/x" 10 ]
      let after = [ session "mine" (run + "/w") 4; session "other" "/home/will/Work/x" 13; session "idle" "/home/will/Work/y" 5 ]
      Daemon.foreignEvalDeltas run before after
      |> List.map (fun (s, d) -> s.Id, d)
      |> Expect.equal "only other and idle, by how much they rose" [ "other", 3; "idle", 5 ]

    testCase "fellOver: evaluating while only another session evaluated is a finding" <| fun _ ->
      let call n cmd : FellOver.Call = { Number = n; Command = cmd; Result = FellOver.CallOk; Body = "" }
      let facts : FellOver.EvalFacts = { OwnEvals = 0; Foreign = [ "fc2e2254", "/tmp/lem/other/w", 2 ] }
      let found = FellOver.entriesWithEvals [ call "001" "vsc key alt+return"; call "002" "vsc snapshot" ] true 1 facts
      found |> List.map (fun e -> e.Stage) |> Expect.equal "an Eval entry" [ "Eval" ]
      (found |> List.head).Evidence |> Expect.stringContains "names the session" "fc2e2254"

    testCase "fellOver: no finding when the run's own session evaluated" <| fun _ ->
      let call n cmd : FellOver.Call = { Number = n; Command = cmd; Result = FellOver.CallOk; Body = "" }
      let facts : FellOver.EvalFacts = { OwnEvals = 2; Foreign = [ "fc2e2254", "/tmp/lem/other/w", 2 ] }
      FellOver.entriesWithEvals [ call "001" "vsc key alt+enter" ] true 1 facts
      |> Expect.isEmpty "nothing to report"

    testCase "fellOver: no finding when the lemming never tried to evaluate" <| fun _ ->
      let call n cmd : FellOver.Call = { Number = n; Command = cmd; Result = FellOver.CallOk; Body = "" }
      let facts : FellOver.EvalFacts = { OwnEvals = 0; Foreign = [ "fc2e2254", "/tmp/lem/other/w", 2 ] }
      FellOver.entriesWithEvals [ call "001" "vsc snapshot" ] true 1 facts
      |> Expect.isEmpty "reading the window is not evaluating"

    testCase "the actions clock writes one JSON line with start, end, command and arguments" <| fun _ ->
      let line =
        Timeline.toJson { StartMs = 1000L; EndMs = 1500L; Editor = "vsc"; Command = "palette"; Args = [ "SageFs:"; "Create" ]; Outcome = "ok" }
      line |> Expect.equal "the line" """{"startMs":1000,"endMs":1500,"editor":"vsc","command":"palette","args":["SageFs:","Create"],"outcome":"ok"}"""

    testCase "eval output is taken from the recorded event stream, for the run's sessions only" <| fun _ ->
      let sse =
        String.concat "\n"
          [ "event: session"
            """data: {"sessionId":"mine","type":"session_health_changed"}"""
            ""
            "event: eval_diff"
            """data: {"SessionId":"mine","Added":1,"Lines":[{"Kind":"added","OldText":"","Text":"Result: val it: int option = Some 7"}]}"""
            ""
            "event: eval_diff"
            """data: {"SessionId":"theirs","Added":1,"Lines":[{"Kind":"added","OldText":"","Text":"Result: val it: int option = None"}]}"""
            "" ]
      let outputs = Daemon.evalOutputsIn sse [ "mine" ]
      outputs |> List.length |> Expect.equal "one event is the run's" 1
      System.Text.RegularExpressions.Regex.IsMatch(outputs.Head, Oracle.EvalOfParseSeedSeven) |> Expect.isTrue "seven"
      System.Text.RegularExpressions.Regex.IsMatch(outputs.Head, Oracle.EvalOfParseSeedNegative) |> Expect.isFalse "not the negative case"
      Daemon.evalOutputsIn sse [] |> Expect.isEmpty "no sessions, no output"
  ]
