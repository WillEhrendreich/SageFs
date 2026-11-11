// WHY — one daemon start had two timers and two messages. `ensureRunning` gave up after 30 s
// with "SageFs didn't start in time." while `startDaemon`'s own poll ran to 120 s and then said
// "failed to start after 120s." The Retry button on both ran `sagefs.restart`, which stops the
// daemon first, so a daemon that was about to answer got killed (roast-5 finding 2).
//
// These pin the single give-up time and what Retry does: probe first, restart only when
// nothing answers.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring StatusBarContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/StartupPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.StartupPure

let tests =
  testList "VS Code daemon startup - one timer, Retry probes first" [

    testCase "WHY - the give-up time is the longest wait the old code had, so no start is cut short" <| fun _ ->
      Expect.isGreaterThanOrEqual "at least the old 120 s poll" (giveUpSeconds, 120)

    testCase "WHY - the one message names the same number the timer uses" <| fun _ ->
      giveUpMessage ""
      |> Expect.stringContains "message carries the timer's seconds" (string giveUpSeconds)

    testCase "WHY - daemon output rides along when there is some, and nothing is appended when there is none" <| fun _ ->
      giveUpMessage "boom" |> Expect.stringContains "snippet" "boom"
      giveUpMessage "" |> Expect.stringEnds "no trailing blank block" "s."

    testCase "WHY - a long snippet is cut so the toast stays readable" <| fun _ ->
      let msg = giveUpMessage (String.replicate 2000 "x")
      Expect.isLessThan "bounded" (msg.Length, 700)

    testCase "WHY - Retry connects when the daemon answers, because restarting would kill the start that just finished" <| fun _ ->
      retryStep DaemonProbe.Answering
      |> Expect.equal "connect" RetryStep.ConnectToRunningDaemon

    testCase "WHY - Retry restarts only when nothing answers" <| fun _ ->
      retryStep DaemonProbe.Silent
      |> Expect.equal "restart" RetryStep.RestartDaemon
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
