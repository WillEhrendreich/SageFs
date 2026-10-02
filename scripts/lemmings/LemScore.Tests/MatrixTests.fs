module LemScore.Tests.MatrixTests

open System
open System.IO
open Expecto
open Expecto.Flip
open LemScore.Types
open LemMatrix
open LemMatrix.Plan
open LemMatrix.Scheduler
open LemMatrix.Report

let private cell (model: string) (n: int) : Cell =
  { Fixture = "demoenv"; Task = "parse-seed"; Model = model; Rep = n; RunId = sprintf "%s-%d" model n }

let private bunny = "stealth/space-bunny-alpha"
let private laguna = "poolside/laguna-s-2.1-free"

[<Tests>]
let planTests =
  testList "Matrix plan" [
    testCase "lines parse, comments and blanks are ignored, reps default to one" <| fun _ ->
      match Plan.parse "# a comment\n\ndemoenv parse-seed stealth/space-bunny-alpha 3  # trailing\nsagefs-copy:/x sagefs-small-fix poolside/laguna-s-2.1-free\n" with
      | Ok [ a; b ] ->
        a.Reps |> Expect.equal "three reps" 3
        b.Reps |> Expect.equal "one rep" 1
        b.Fixture |> Expect.equal "fixture with a colon survives" "sagefs-copy:/x"
      | other -> failtestf "got %A" other

    testCase "a bad rep count names its line" <| fun _ ->
      match Plan.parse "demoenv parse-seed m 0" with
      | Error why -> why |> Expect.stringContains "line number" "line 1"
      | Ok _ -> failtest "accepted zero reps"

    testCase "an empty plan is refused" <| fun _ ->
      Plan.parse "# nothing\n" |> Result.isError |> Expect.isTrue "refused"

    testCase "a line with too few fields is refused" <| fun _ ->
      Plan.parse "demoenv parse-seed" |> Result.isError |> Expect.isTrue "refused"

    testCase "run ids are unique across the plan and against directories already on disk" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "lemmatrix-test-" + Guid.NewGuid().ToString "N")
      try
        Directory.CreateDirectory(Path.Combine(root, "space-bunny-parse-seed-01")) |> ignore
        let lines =
          [ { Fixture = "demoenv"; Task = "parse-seed"; Model = bunny; Reps = 2 }
            { Fixture = "demoenv"; Task = "parse-seed"; Model = bunny; Reps = 1 } ]
        let ids = Plan.expand root lines |> List.map _.RunId
        ids |> Expect.equal "02, 03, 04" [ "space-bunny-parse-seed-02"; "space-bunny-parse-seed-03"; "space-bunny-parse-seed-04" ]
        (List.distinct ids).Length |> Expect.equal "no duplicates" ids.Length
      finally
        if Directory.Exists root then Directory.Delete(root, true)

    testCase "a task that has no file is a plan error before anything runs" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "lemmatrix-tasks-" + Guid.NewGuid().ToString "N")
      Directory.CreateDirectory dir |> ignore
      try
        File.WriteAllText(Path.Combine(dir, "real.md"), "x")
        Plan.checkTasks dir [ { Fixture = "f"; Task = "real"; Model = "m"; Reps = 1 } ] |> Expect.equal "ok" (Ok ())
        Plan.checkTasks dir [ { Fixture = "f"; Task = "ghost"; Model = "m"; Reps = 1 } ] |> Result.isError |> Expect.isTrue "refused"
      finally
        Directory.Delete(dir, true)
  ]

[<Tests>]
let schedulerTests =
  testList "Matrix scheduler" [
    testCase "never more than the concurrency cap run at once" <| fun _ ->
      let cells = [ 1 .. 5 ] |> List.map (cell bunny)
      let mutable state = Scheduler.start cells
      let mutable started = 0
      let mutable go = true
      while go do
        match Scheduler.next 2 2 state with
        | Dispatch _, s -> state <- s; started <- started + 1
        | _ -> go <- false
      started |> Expect.equal "two slots, two starts" 2
      state.Running |> Expect.equal "running" 2

    testCase "a finished run frees a slot" <| fun _ ->
      let mutable state = Scheduler.start ([ 1 .. 3 ] |> List.map (cell bunny))
      for _ in 1 .. 2 do state <- snd (Scheduler.next 5 2 state)
      state <- Scheduler.finished (Some Pass) bunny state
      match Scheduler.next 5 2 state with
      | Dispatch c, _ -> c.Rep |> Expect.equal "third starts" 3
      | other, _ -> failtestf "got %A" other

    testCase "after two quota outcomes the model's remaining runs are skipped, not dispatched" <| fun _ ->
      let mutable state = Scheduler.start ([ 1 .. 4 ] |> List.map (cell bunny))
      state <- snd (Scheduler.next 2 1 state)
      state <- Scheduler.finished (Some ProviderQuota) bunny state
      state <- snd (Scheduler.next 2 1 state)
      state <- Scheduler.finished (Some ProviderQuota) bunny state
      let skipped = ResizeArray<Cell>()
      let mutable go = true
      while go do
        match Scheduler.next 2 1 state with
        | Skip (c, why), s ->
          why |> Expect.stringContains "says why" "quota"
          skipped.Add c
          state <- s
        | Dispatch _, _ -> failtest "dispatched to an exhausted model"
        | _ -> go <- false
      skipped.Count |> Expect.equal "the other two" 2

    testCase "one model's quota does not stop another model" <| fun _ ->
      let mutable state = Scheduler.start [ cell bunny 1; cell laguna 1 ]
      state <- { state with QuotaHits = Map.ofList [ bunny, 2 ] }
      match Scheduler.next 2 1 state with
      | Skip (c, _), s ->
        c.Model |> Expect.equal "bunny skipped" bunny
        match Scheduler.next 2 1 s with
        | Dispatch c2, _ -> c2.Model |> Expect.equal "laguna runs" laguna
        | other, _ -> failtestf "got %A" other
      | other, _ -> failtestf "got %A" other

    testCase "a single quota hit is below the limit and does not stop the model" <| fun _ ->
      let state = Scheduler.finished (Some ProviderQuota) bunny { Scheduler.start [ cell bunny 2 ] with Running = 1 }
      match Scheduler.next 2 1 state with
      | Dispatch _, _ -> ()
      | other, _ -> failtestf "got %A" other

    testCase "an aborted matrix skips everything that is left" <| fun _ ->
      let state = Scheduler.abort "the daemon refused" (Scheduler.start [ cell bunny 1; cell laguna 1 ])
      match Scheduler.next 2 3 state with
      | Skip (_, why), _ -> why |> Expect.stringContains "reason" "the daemon refused"
      | other, _ -> failtestf "got %A" other

    testCase "an empty queue with nothing running is finished, with something running it waits" <| fun _ ->
      Scheduler.next 2 3 (Scheduler.start []) |> fst |> Expect.equal "finished" Finished
      Scheduler.next 2 3 { Scheduler.start [] with Running = 1 } |> fst |> Expect.equal "wait" Wait
  ]

[<Tests>]
let reportTests =
  testList "Matrix report" [
    let row id status : Row = { emptyRow id bunny "demoenv" "parse-seed" status with Seconds = 61; Turns = 20; SageCalls = 18; SageErrors = 1; FellOverCount = 2 }

    yield testCase "the table has a header, one line per run, and notes for skipped and unscored runs" <| fun _ ->
      let table =
        renderTable
          [ row "space-bunny-parse-seed-01" (Scored PassWithRecovery)
            row "space-bunny-parse-seed-02" (Skipped "quota")
            row "space-bunny-parse-seed-03" (Unscored "runner exit 3") ]
      table |> Expect.stringContains "header" "outcome"
      table |> Expect.stringContains "outcome" "PassWithRecovery"
      table |> Expect.stringContains "skip note" "space-bunny-parse-seed-02 skipped: quota"
      table |> Expect.stringContains "unscored note" "no summary: runner exit 3"

    yield testCase "the tally counts outcomes in the closed set's order" <| fun _ ->
      tally [ row "a" (Scored Fail); row "b" (Scored Pass); row "c" (Scored Pass); row "d" (Skipped "x") ]
      |> Expect.equal "tally" "Pass 2, Fail 1, Skipped 1"

    yield testCase "a real summary.json written by LemScore is read back into a row" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "lemmatrix-sum-" + Guid.NewGuid().ToString "N")
      let outDir = Path.Combine(root, "r1", "out")
      Directory.CreateDirectory outDir |> ignore
      try
        File.WriteAllText(
          Path.Combine(outDir, "summary.json"),
          """{"id":"r1","outcome":"Blocked","seconds":42,"turns":9,"sagefsMcp":{"calls":7,"errorCount":3},"fellOver":[{},{}]}""")
        match readSummary root "r1" bunny "demoenv" "parse-seed" with
        | Ok r ->
          r.Status |> Expect.equal "status" (Scored Blocked)
          r.SageErrors |> Expect.equal "errors" 3
          r.FellOverCount |> Expect.equal "fellOver" 2
        | Error e -> failtest e
      finally
        Directory.Delete(root, true)

    yield testCase "a missing summary is an error with the path, never a made-up row" <| fun _ ->
      readSummary "/tmp/definitely-not-here" "r1" bunny "demoenv" "parse-seed" |> Result.isError |> Expect.isTrue "error"

    yield testCase "options parse with defaults, and unknown flags are refused" <| fun _ ->
      match Program.parseOptions [ "plan.txt"; "--concurrency"; "2" ] with
      | Ok o ->
        o.Concurrency |> Expect.equal "concurrency" 2
        o.QuotaLimit |> Expect.equal "default quota limit" Scheduler.defaultQuotaLimit
      | Error e -> failtest e
      (Program.parseOptions [ "plan.txt" ] |> Result.map _.Concurrency) |> Expect.equal "default cap is three" (Ok 3)
      Program.parseOptions [ "plan.txt"; "--wat" ] |> Result.isError |> Expect.isTrue "unknown flag refused"
  ]

[<Tests>]
let runnerTests =
  testList "Matrix runner" [
    testCase "by default each run is LemRun's run-cmd, which sits beside the binary, and tasks come from the harness" <| fun _ ->
      match Program.findRunner None with
      | Ok runner ->
        runner.File |> Expect.equal "dotnet" "dotnet"
        runner.LeadingArgs |> List.last |> Expect.equal "the command" "run-cmd"
        File.Exists(runner.LeadingArgs.Head) |> Expect.isTrue "LemRun.dll is there"
        Directory.Exists runner.TasksDir |> Expect.isTrue "tasks directory"
      | Error e -> failtest e

    testCase "a runner given with --runner is run as it is, with its tasks beside it" <| fun _ ->
      match Program.findRunner (Some "/opt/x/run") with
      | Ok runner ->
        runner.File |> Expect.equal "file" "/opt/x/run"
        runner.LeadingArgs |> Expect.isEmpty "no leading arguments"
        runner.TasksDir |> Expect.equal "tasks" "/opt/x/tasks"
      | Error e -> failtest e
  ]
