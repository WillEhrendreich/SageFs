module LemScore.Tests.SummaryTests

open System.Text.Json
open Expecto
open Expecto.Flip
open LemScore
open LemScore.Types
open LemScore.CmdcStream
open LemScore.Classify
open LemScore.Summary
open LemScore.Tests.Samples

let private realRun () = sampleStream "parse-seed-real-run.ndjson"

let private factsFor (s: RunStream) (oracle: OracleVerdict) : RunFacts =
  { Stream = s; CmdcExit = 0; Oracle = oracle; OracleOutput = ""; Cleanup = Clean; ResidueSessions = [] }

let private inputFor (f: RunFacts) : SummaryInput =
  { Id = "space-bunny-parse-seed-01"; Model = "stealth/space-bunny-alpha"; Harness = Cmdc
    SagefsVersion = "daemon 0.6.875+d7e11077; bridge dev build"; Task = "parse-seed"; Seconds = 61
    Facts = f; Teardown = ExitedOnItsOwn
    DaemonStart = Some { Pressure = "normal"; AvailableBytes = 54000000000L; ActiveLeases = 0 }
    DaemonEnd = Some { Pressure = "normal"; AvailableBytes = 53000000000L; ActiveLeases = 0 }
    DashboardUrl = "http://localhost:37750/dashboard"; SessionsSeen = [ "f50417a6", "Ready" ]; SandboxProcessesLeft = []; ChangedFiles = [ "DemoEnv/DemoEnv.fs" ]; Extra = [] }

let private render' (f: RunFacts) : JsonElement =
  let json = render (inputFor f) (assess f)
  JsonDocument.Parse(json).RootElement.Clone()

let private requiredKeys =
  [ "id"; "model"; "harness"; "sagefsVersion"; "task"; "outcome"; "seconds"; "turns"; "toolCalls"; "sagefsMcp"
    "oracle"; "residue"; "teardown"; "dashboardUrl"; "daemon"; "fellOver" ]

[<Tests>]
let realRunTests =
  testList "The real parse-seed run (space-bunny, 2026-10-01)" [
    testCase "the stream reads clean: 20 turns, 28 calls, no unreadable lines" <| fun _ ->
      let s = realRun ()
      s.Turns |> Expect.equal "turns" 20
      s.Calls |> List.length |> Expect.equal "calls" 28
      s.Unreadable |> Expect.isEmpty "all lines parsed"

    testCase "the model's own eval failure is not counted as a SageFs error, run_tests refusing is" <| fun _ ->
      let s = realRun ()
      let errors = sagefsCalls s |> List.filter isSagefsError |> List.map sagefsToolName
      errors |> Expect.equal "only the run_tests refusal" [ "run_tests" ]
      evalFailures s |> Expect.equal "one eval failed on its own" 1

    testCase "with the oracle passing the run is PassWithRecovery, because run_tests was refused once" <| fun _ ->
      (assess (factsFor (realRun ()) OraclePassed)).Outcome |> Expect.equal "outcome" PassWithRecovery

    testCase "cmdc's input repair on list_tests is a ToolSurface finding" <| fun _ ->
      let a = assess (factsFor (realRun ()) OraclePassed)
      a.FellOver
      |> List.exists (fun f -> f.Symptom.Contains "repair" && f.Symptom.Contains "list_tests")
      |> Expect.isTrue "repair reported"

    testCase "the real sandbox ps output leaves only the MSBuild node" <| fun _ ->
      leftoverProcesses (readSample "sandbox-ps.txt")
      |> List.map (fun p -> p.Contains "MSBuild")
      |> Expect.equal "one leftover, a build node" [ true ]
  ]

[<Tests>]
let summaryTests =
  testList "summary.json" [
    testCase "carries every key the contract promises" <| fun _ ->
      let root = render' (factsFor (realRun ()) OraclePassed)
      for key in requiredKeys do
        let found, _ = root.TryGetProperty key
        found |> Expect.isTrue (sprintf "key %s is present" key)

    testCase "the outcome is one of the closed set, always" <| fun _ ->
      for oracle in [ OraclePassed; OracleFailed 1; OracleNotRun ] do
        let root = render' (factsFor (realRun ()) oracle)
        Outcome.tryParse (root.GetProperty("outcome").GetString()) |> Result.isOk |> Expect.isTrue "in the set"

    testCase "tool calls are counted by name, SageFs calls and errors are listed with their text" <| fun _ ->
      let root = render' (factsFor (realRun ()) OraclePassed)
      root.GetProperty("toolCalls").GetProperty("mcp__sagefs__send_fsharp_code").GetInt32() |> Expect.equal "evals" 5
      let mcp = root.GetProperty "sagefsMcp"
      mcp.GetProperty("errorCount").GetInt32() |> Expect.equal "one error" 1
      mcp.GetProperty("errors").[0].GetProperty("text").GetString() |> Expect.stringContains "text kept" "no discovered tests"

    testCase "the oracle block says what the harness measured" <| fun _ ->
      let root = render' (factsFor (realRun ()) (OracleFailed 1))
      let oracle = root.GetProperty "oracle"
      oracle.GetProperty("passed").GetBoolean() |> Expect.isFalse "failed"
      oracle.GetProperty("exit").GetInt32() |> Expect.equal "exit" 1

    testCase "daemon pressure at start and end is recorded" <| fun _ ->
      let root = render' (factsFor (realRun ()) OraclePassed)
      root.GetProperty("daemon").GetProperty("start").GetProperty("memoryPressure").GetString() |> Expect.equal "start" "normal"
      root.GetProperty("daemon").GetProperty("end").GetProperty("memoryPressure").GetString() |> Expect.equal "end" "normal"

    testCase "extra fellOver entries from an editor driver are parsed, and an unknown stage is refused" <| fun _ ->
      parseExtraFellOver """[{"stage":"Editor","symptom":"no gutter sign","evidence":"screenshot 3"}]"""
      |> Expect.equal "parsed" (Ok [ { Stage = Editor; Symptom = "no gutter sign"; Evidence = "screenshot 3" } ])
      parseExtraFellOver """[{"stage":"Nope","symptom":"x","evidence":"y"}]"""
      |> Result.isError |> Expect.isTrue "unknown stage refused"
      parseExtraFellOver "not json" |> Result.isError |> Expect.isTrue "not json refused"
  ]

[<Tests>]
let editTests =
  testList "replace-exact and expect" [
    testCase "exactly one match is replaced" <| fun _ ->
      LemScore.Program.replaceExact "a < b" "<" "<=" |> Expect.equal "replaced" (Ok "a <= b")

    testCase "no match is refused and says so" <| fun _ ->
      match LemScore.Program.replaceExact "a < b" ">" "x" with
      | Error why -> why |> Expect.stringContains "no match" "no match"
      | Ok _ -> failtest "replaced nothing silently"

    testCase "several matches are refused, never guessed" <| fun _ ->
      match LemScore.Program.replaceExact "a < b < c" "<" "<=" with
      | Error why -> why |> Expect.stringContains "count" "2 matches"
      | Ok _ -> failtest "guessed which one"

    testCase "answer patterns report the ones that are missing" <| fun _ ->
      let answer = "toList = [60; 50; 40; 30]\ntryGet 2 = Some 40\n"
      LemScore.Program.patternsMissing answer [ "^toList = .*60\\D+50"; "evictedCount\\s*=\\s*2" ]
      |> Expect.equal "one missing" [ "evictedCount\\s*=\\s*2" ]
  ]
