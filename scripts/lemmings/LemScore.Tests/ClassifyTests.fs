module LemScore.Tests.ClassifyTests

open Expecto
open Expecto.Flip
open LemScore.Types
open LemScore.CmdcStream
open LemScore.Classify
open LemScore.Tests.Samples

let private facts (s: RunStream) (exitCode: int) (oracle: OracleVerdict) : RunFacts =
  { Stream = s; CmdcExit = exitCode; Oracle = oracle; OracleOutput = "oracle said no"; Cleanup = Clean; ResidueSessions = [] }

let private okSession = "State Ready, loadedProjects [DemoEnv]"

/// A clean run: status check, session, eval, reset, eval.
let private cleanRun : RunStream =
  stream
    [ sagefsCall 1 "get_daemon_status" "{}" """{"state":"Ready"}"""
      sagefsCall 2 "create_project_session" "{}" "session abc12345 created"
      sagefsCall 3 "get_session_status" "{}" okSession
      sagefsCall 4 "send_fsharp_code" "{}" "val it: int = 1" ]
    finishedOk

let private stageOf (a: Assessment) = a.FellOver |> List.map _.Stage

[<Tests>]
let outcomeTests =
  testList "Classify outcome" [
    testCase "oracle passed with nothing to recover from is Pass" <| fun _ ->
      (assess (facts cleanRun 0 OraclePassed)).Outcome |> Expect.equal "outcome" Pass

    testCase "oracle passed after a SageFs error is PassWithRecovery" <| fun _ ->
      let s =
        stream
          [ sagefsCall 1 "create_project_session" "{}" "Failed to create session: bad path → Next: use an absolute path"
            sagefsCall 2 "create_project_session" "{}" "session abc12345 created"
            sagefsCall 3 "send_fsharp_code" "{}" "val it: int = 1" ]
          finishedOk
      let a = assess (facts s 0 OraclePassed)
      a.Outcome |> Expect.equal "outcome" PassWithRecovery
      stageOf a |> Expect.contains "the error is placed at its stage" SessionCreate

    testCase "oracle passed but a session was left behind is PassWithRecovery with a Cleanup finding" <| fun _ ->
      let f = { facts cleanRun 0 OraclePassed with ResidueSessions = [ "abc12345" ]; Cleanup = ResidueStopped }
      let a = assess f
      a.Outcome |> Expect.equal "outcome" PassWithRecovery
      stageOf a |> Expect.contains "residue is reported" Cleanup

    testCase "oracle passed at the turn cap is PassWithRecovery" <| fun _ ->
      (assess (facts cleanRun 8 OraclePassed)).Outcome |> Expect.equal "outcome" PassWithRecovery

    testCase "finished cleanly but the oracle failed is Fail, never the model's claim" <| fun _ ->
      let a = assess (facts cleanRun 0 (OracleFailed 1))
      a.Outcome |> Expect.equal "outcome" Fail
      stageOf a |> Expect.contains "the oracle failure is a finding" Oracle
      a.FellOver |> List.find (fun f -> f.Stage = Oracle) |> _.Evidence |> Expect.equal "evidence is the oracle output" "oracle said no"

    testCase "session setup errors and no eval, oracle failed, is Blocked" <| fun _ ->
      let s =
        stream
          [ sagefsCall 1 "create_project_session" "{}" "Failed to create session: Not all DLLs are found → Next: build"
            sagefsCall 2 "get_session_status" "{}" """{"state":"Faulted"}""" ]
          finishedOk
      let a = assess (facts s 0 (OracleFailed 1))
      a.Outcome |> Expect.equal "outcome" Blocked
      stageOf a |> Expect.contains "faulted warmup is named" SessionWarmup

    testCase "exit 8 with the oracle failing is MaxTurns and the budget finding says when SageFs was first used" <| fun _ ->
      let s = stream [ sagefsCall 1 "get_daemon_status" "{}" "{}"; sagefsCall 2 "list_sessions" "{}" "[]" ] [ runEnd "max_turns" 2; resultLine "max_turns" None "" ]
      let a = assess (facts s 8 (OracleFailed 1))
      a.Outcome |> Expect.equal "outcome" MaxTurns
      let budget = a.FellOver |> List.find (fun f -> f.Stage = Budget)
      budget.Evidence |> Expect.stringContains "first eval never" "first successful eval at turn never"

    testCase "the harness timeout killing the run, oracle failing, is Incomplete" <| fun _ ->
      let s = stream [ sagefsCall 1 "get_daemon_status" "{}" "{}" ] []
      (assess (facts s 124 (OracleFailed 1))).Outcome |> Expect.equal "outcome" Incomplete

    testCase "an oracle that did not run is a HarnessError, because there is no verdict" <| fun _ ->
      (assess (facts cleanRun 0 OracleNotRun)).Outcome |> Expect.equal "outcome" HarnessError

    testCase "no events at all is a HarnessError" <| fun _ ->
      (assess (facts emptyStream 1 (OracleFailed 1))).Outcome |> Expect.equal "outcome" HarnessError

    testCase "an auth failure inside the sandbox is a HarnessError, not a provider problem" <| fun _ ->
      (assess (facts emptyStream 3 (OracleFailed 1))).Outcome |> Expect.equal "outcome" HarnessError
  ]

[<Tests>]
let providerTests =
  testList "Classify provider errors" [
    testCase "exit 5 is ProviderQuota, not a SageFs failure" <| fun _ ->
      let s = stream [] [ resultLine "error" (Some "You've reached today's limit on Ling 3.0 Flash Sante.") "" ]
      let a = assess (facts s 5 (OracleFailed 1))
      a.Outcome |> Expect.equal "outcome" ProviderQuota
      a.Provider |> Expect.equal "kind" (Some RateLimited)

    testCase "a quota run records no SageFs fellOver beyond the oracle" <| fun _ ->
      let s = stream [] [ resultLine "error" (Some "You've reached today's limit on X.") "" ]
      let a = assess (facts s 5 (OracleFailed 1))
      a.FellOver |> List.filter (fun f -> f.Stage <> Oracle && f.Stage <> Registration) |> Expect.isEmpty "nothing else"

    testCase "exit 10 is ProviderQuota (insufficient credits)" <| fun _ ->
      let a = assess (facts emptyStream 10 (OracleFailed 1))
      a.Outcome |> Expect.equal "outcome" ProviderQuota
      a.Provider |> Expect.equal "kind" (Some InsufficientCredits)

    testCase "connection and server errors are provider outcomes too" <| fun _ ->
      (assess (facts emptyStream 6 (OracleFailed 1))).Provider |> Expect.equal "connection" (Some ConnectionFailed)
      (assess (facts emptyStream 7 (OracleFailed 1))).Provider |> Expect.equal "server" (Some ServerFailed)

    testCase "quota wording in a generic exit-1 error is still a quota" <| fun _ ->
      let s = stream [] [ resultLine "error" (Some "Rate limit exceeded. Please wait.") "" ]
      (assess (facts s 1 (OracleFailed 1))).Outcome |> Expect.equal "outcome" ProviderQuota

    testCase "a provider error after the oracle already passed does not erase the pass" <| fun _ ->
      let a = assess (facts cleanRun 5 OraclePassed)
      (a.Outcome = ProviderQuota) |> Expect.isFalse "the work was done"
  ]

[<Tests>]
let fellOverTests =
  testList "Classify fellOver" [
    testCase "reaching for dotnet build before any eval is an Adoption finding with the command" <| fun _ ->
      let s =
        stream
          [ sagefsCall 1 "get_daemon_status" "{}" "{}"
            shellCall 2 "dotnet build" "Build succeeded" ]
          finishedOk
      let a = assess (facts s 0 (OracleFailed 1))
      let f = a.FellOver |> List.find (fun f -> f.Stage = Adoption)
      f.Evidence |> Expect.stringContains "the command" "dotnet build"

    testCase "dotnet after a successful eval is the final gate, not a finding" <| fun _ ->
      let s =
        stream
          [ sagefsCall 1 "send_fsharp_code" "{}" "val it: int = 1"
            shellCall 2 "dotnet run --project Tests" "passed" ]
          finishedOk
      stageOf (assess (facts s 0 OraclePassed)) |> Expect.isEmpty "no findings"

    testCase "a lemming that never called SageFs is a Registration finding" <| fun _ ->
      let s = stream [ shellCall 1 "ls" "x" ] finishedOk
      stageOf (assess (facts s 0 (OracleFailed 1))) |> Expect.contains "registration" Registration

    testCase "the same call three times in a row is a loop" <| fun _ ->
      let one n = sagefsCall n "get_session_status" """{"x":1}""" "State Starting"
      let s = stream [ one 1 @ one 2 @ one 3 ] finishedOk
      let a = assess (facts s 0 (OracleFailed 1))
      a.FellOver |> List.exists (fun f -> f.Symptom.Contains "repeated") |> Expect.isTrue "loop reported"

    testCase "identical errors collapse to one finding with a count" <| fun _ ->
      let bad n = sagefsCall n "send_fsharp_code" "{}" "Error: session is busy → Next: wait"
      let s = stream [ bad 1 @ bad 2 ] finishedOk
      let findings = (assess (facts s 0 OraclePassed)).FellOver |> List.filter (fun f -> f.Stage = Eval)
      findings |> List.length |> Expect.equal "one finding" 1
      (List.head findings).Symptom |> Expect.stringContains "counted" "(x2)"

    testCase "a tool Command Code refused as non-existent is a ToolSurface finding" <| fun _ ->
      let s = stream [ [ toolQueued "c1" "sagefs_eval" "{}"; toolDenied "c1" "sagefs_eval" ] ] finishedOk
      stageOf (assess (facts s 0 OraclePassed)) |> Expect.contains "tool surface" ToolSurface

    testCase "a plain F# compile error inside an eval is the lemming's own, not a SageFs error" <| fun _ ->
      let s = stream [ sagefsCall 1 "send_fsharp_code" "{}" "stdin(1,1): error FS0039: The value 'x' is not defined" ] finishedOk
      (assess (facts s 0 OraclePassed)).Outcome |> Expect.equal "no recovery needed" Pass

    testCase "stop_session refusing another connection's session is placed at Cleanup" <| fun _ ->
      let s = sampleStream "sagefs-error-and-shell.ndjson"
      let a = assess (facts s 0 (OracleFailed 1))
      stageOf a |> Expect.contains "cleanup stage" Cleanup
  ]
