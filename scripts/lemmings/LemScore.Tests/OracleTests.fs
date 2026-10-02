module LemScore.Tests.OracleTests

open System
open System.IO
open Expecto
open Expecto.Flip
open LemRun
open LemRun.Oracles

/// A run directory shaped like the harness's: a git repository in w/ with the tag lem-baseline, and out/.
let private withRun (files: (string * string) list) (body: string -> unit) : unit =
  let run = Path.Combine(Path.GetTempPath(), "lem-oracle-" + Guid.NewGuid().ToString("N").Substring(0, 8))
  let work = Path.Combine(run, "w")
  Directory.CreateDirectory work |> ignore
  Directory.CreateDirectory(Path.Combine(run, "out")) |> ignore
  for path, text in files do
    let full = Path.Combine(work, path)
    Directory.CreateDirectory(Path.GetDirectoryName full |> Option.ofObj |> Option.defaultValue work) |> ignore
    File.WriteAllText(full, text)
  Workspace.initRepository work Workspace.cmdBaseline
  try body run
  finally try Store.removeTree run with _ -> ()

let private ran (code: int) (output: string) : Proc.Captured = { ExitCode = code; Stdout = output; Stderr = ""; TimedOut = false }

let private contextFor (run: string) (suite: Proc.Captured) : Context * Collections.Generic.List<string> =
  let said = Collections.Generic.List<string>()
  { RunDir = run; Say = said.Add; Sandboxed = fun _ _ _ -> suite }, said

let private passingLine = "[12:00:00 INF] EXPECTO! 9 tests run in 00:00:00 for DemoEnv - 9 passed, 0 ignored, 0 failed, 0 errored. Success!"

[<Tests>]
let which =
  testList "which task has which oracle" [
    testCase "every task the harness ships has one, and the editor tasks go to the Neovim oracle" <| fun _ ->
      forTask "parse-seed" |> Expect.equal "parse-seed" (Some ParseSeed)
      forTask "sagefs-small-fix" |> Expect.equal "small fix" (Some SagefsSmallFix)
      forTask "sagefs-repl-eval" |> Expect.equal "repl eval" (Some SagefsReplEval)
      forTask "smoke" |> Expect.equal "smoke" (Some Smoke)
      for t in [ "ui-eval"; "ui-edit-reeval"; "ui-live-tests"; "ui-hot-reload"; "ui-find-help" ] do
        forTask t |> Expect.equal t (Some (NeovimTask t))

    testCase "a task with no oracle has none: it is scored HarnessError, never a pass" <| fun _ ->
      forTask "made-up" |> Expect.isNone "none"
      forTask "" |> Expect.isNone "none"

    testCase "every task file in tasks/ that is not an editor task has an oracle" <| fun _ ->
      let tasks = Directory.GetFiles(Path.Combine(Env.lemDir, "tasks"), "*.md") |> Array.map Path.GetFileNameWithoutExtension
      tasks |> Array.iter (fun t -> forTask t |> Expect.isSome (sprintf "task %s has an oracle" t))
  ]

[<Tests>]
let parseSeedTests =
  testList "the parse-seed oracle" [
    testCase "the suite passes with a passing summary line: exit 0" <| fun _ ->
      withRun [ "DemoEnv/DemoEnv.fs", "x"; "DemoEnv.Tests/T.fs", "t" ] (fun run ->
        let c, said = contextFor run (ran 0 passingLine)
        parseSeed c |> Expect.equal "pass" 0
        said |> Seq.last |> Expect.equal "said so" "the DemoEnv suite passes")

    testCase "a clean exit with no summary line proves nothing: exit 1" <| fun _ ->
      withRun [ "DemoEnv.Tests/T.fs", "t" ] (fun run ->
        let c, said = contextFor run (ran 0 "nothing here")
        parseSeed c |> Expect.equal "fail" 1
        said |> Seq.last |> Expect.stringContains "reason" "nothing is proven")

    testCase "a failing suite: exit 1, and the exit code is named" <| fun _ ->
      withRun [ "DemoEnv.Tests/T.fs", "t" ] (fun run ->
        let c, said = contextFor run (ran 3 "1 failed")
        parseSeed c |> Expect.equal "fail" 1
        said |> Seq.last |> Expect.equal "named" "the test suite exited 3")

    testCase "editing the tests instead of the code fails before anything runs" <| fun _ ->
      withRun [ "DemoEnv.Tests/T.fs", "t" ] (fun run ->
        File.AppendAllText(Path.Combine(run, "w", "DemoEnv.Tests", "T.fs"), "cheat")
        let c, said = contextFor run (ran 0 passingLine)
        parseSeed c |> Expect.equal "fail" 1
        said |> Seq.toList |> Expect.contains "names the file" "DemoEnv.Tests/T.fs")

    testCase "a new file among the tests counts as touching them" <| fun _ ->
      withRun [ "DemoEnv.Tests/T.fs", "t" ] (fun run ->
        File.WriteAllText(Path.Combine(run, "w", "DemoEnv.Tests", "New.fs"), "x")
        let c, _ = contextFor run (ran 0 passingLine)
        parseSeed c |> Expect.equal "fail" 1)

    testCase "colour codes in the summary line do not hide it" <| fun _ ->
      Oracles.stripAnsi "\u001b[32m9 passed, 0 ignored, 0 failed, 0 errored\u001b[0m" |> Expect.equal "stripped" "9 passed, 0 ignored, 0 failed, 0 errored"
      withRun [ "DemoEnv.Tests/T.fs", "t" ] (fun run ->
        let c, _ = contextFor run (ran 0 "\u001b[32m9 passed, 0 ignored, 0 failed, 0 errored\u001b[0m")
        parseSeed c |> Expect.equal "pass" 0)
  ]

[<Tests>]
let answerTests =
  let good = "toList = [60; 50; 40; 30]\ntryGet 2 = Some 40\nevictedCount = 2\nruntimeMajor = 11\n"
  testList "the sagefs-repl-eval oracle" [
    testCase "the four right values and no edited source: exit 0" <| fun _ ->
      withRun [ "SageFs.Core/RingBuffer.fs", "x" ] (fun run ->
        File.WriteAllText(Path.Combine(run, "w", "ANSWER.md"), good)
        let c, _ = contextFor run (ran 0 "")
        sagefsReplEval c |> Expect.equal "pass" 0)

    testCase "a wrong value names the missing line: exit 2" <| fun _ ->
      withRun [ "SageFs.Core/RingBuffer.fs", "x" ] (fun run ->
        File.WriteAllText(Path.Combine(run, "w", "ANSWER.md"), good.Replace("Some 40", "Some 41"))
        let c, said = contextFor run (ran 0 "")
        sagefsReplEval c |> Expect.equal "refused" 2
        said |> Seq.toList |> List.exists (fun l -> l.StartsWith "missing:" && l.Contains "tryGet") |> Expect.isTrue "names the pattern")

    testCase "no ANSWER.md: exit 2" <| fun _ ->
      withRun [ "SageFs.Core/RingBuffer.fs", "x" ] (fun run ->
        let c, said = contextFor run (ran 0 "")
        sagefsReplEval c |> Expect.equal "refused" 2
        said |> Seq.last |> Expect.stringContains "says why" "does not exist")

    testCase "the task said not to change source files: an edit is exit 1 whatever the answer says" <| fun _ ->
      withRun [ "SageFs.Core/RingBuffer.fs", "x" ] (fun run ->
        File.WriteAllText(Path.Combine(run, "w", "ANSWER.md"), good)
        File.AppendAllText(Path.Combine(run, "w", "SageFs.Core", "RingBuffer.fs"), "// edit")
        let c, said = contextFor run (ran 0 "")
        sagefsReplEval c |> Expect.equal "fail" 1
        said |> Seq.toList |> Expect.contains "names the file" "SageFs.Core/RingBuffer.fs")

    testCase "an unquoted spacing and a trailing letter are tolerated the way the patterns say" <| fun _ ->
      withRun [ "a", "x" ] (fun run ->
        File.WriteAllText(Path.Combine(run, "w", "ANSWER.md"), "- toList = [ 60; 50; 40; 30 ]\n- tryGet 2 = 40\n- evictedCount = 2.\n- runtimeMajor = 10\n")
        let c, _ = contextFor run (ran 0 "")
        sagefsReplEval c |> Expect.equal "pass" 0)
  ]

[<Tests>]
let smallFixTests =
  testList "the sagefs-small-fix oracle" [
    testCase "the diff must touch exactly the named file; anything else is exit 1 before a build" <| fun _ ->
      withRun [ "SageFs.Core/RingBuffer.fs", "x"; "SageFs.Core/Other.fs", "y" ] (fun run ->
        File.AppendAllText(Path.Combine(run, "w", "SageFs.Core", "Other.fs"), "z")
        let c, said = contextFor run (ran 0 passingLine)
        sagefsSmallFix c |> Expect.equal "fail" 1
        said |> Seq.toList |> Expect.contains "names the stray file" "SageFs.Core/Other.fs")

    testCase "an empty diff is said as (nothing)" <| fun _ ->
      withRun [ "SageFs.Core/RingBuffer.fs", "x" ] (fun run ->
        let c, said = contextFor run (ran 0 passingLine)
        sagefsSmallFix c |> Expect.equal "fail" 1
        said |> Seq.toList |> Expect.contains "nothing" "(nothing)")

    testCase "the named file plus a stray untracked file is still a fail" <| fun _ ->
      withRun [ "SageFs.Core/RingBuffer.fs", "x" ] (fun run ->
        File.AppendAllText(Path.Combine(run, "w", "SageFs.Core", "RingBuffer.fs"), "fix")
        File.WriteAllText(Path.Combine(run, "w", "NOTES.md"), "n")
        let c, _ = contextFor run (ran 0 passingLine)
        sagefsSmallFix c |> Expect.equal "fail" 1)
  ]

[<Tests>]
let smokeTests =
  let streamWith (line: string) (body: string -> unit) =
    withRun [ "a", "x" ] (fun run ->
      File.WriteAllText(Path.Combine(run, "out", "events.ndjson"), line + "\n")
      body run)
  testList "the smoke oracle" [
    testCase "a completed get_daemon_status call in the stream passes" <| fun _ ->
      streamWith """{"type":"tool_completed","toolCallId":"1","toolName":"mcp__sagefs__get_daemon_status"}""" (fun run ->
        let c, _ = contextFor run (ran 0 "")
        smoke c |> Expect.equal "pass" 0)

    testCase "a call that was only queued, or another tool, or no stream at all, fails" <| fun _ ->
      streamWith """{"type":"tool_queued","toolName":"mcp__sagefs__get_daemon_status"}""" (fun run ->
        let c, _ = contextFor run (ran 0 "")
        smoke c |> Expect.equal "queued only" 1)
      streamWith """{"type":"tool_completed","toolName":"mcp__sagefs__other"}""" (fun run ->
        let c, _ = contextFor run (ran 0 "")
        smoke c |> Expect.equal "other tool" 1)
      withRun [ "a", "x" ] (fun run ->
        let c, _ = contextFor run (ran 0 "")
        smoke c |> Expect.equal "no stream" 1)
  ]

[<Tests>]
let tailTests =
  testList "the end of an output" [
    testCase "the last N lines, and fewer when there are fewer" <| fun _ ->
      tailLines 2 "a\nb\nc\n" |> Expect.equal "last two" [ "b"; "c" ]
      tailLines 10 "a\nb" |> Expect.equal "all" [ "a"; "b" ]
  ]
