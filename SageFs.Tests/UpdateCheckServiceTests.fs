module SageFs.Tests.UpdateCheckServiceTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// A fresh scratch dir per test, standing in for `<SageFsDir>` — never the
/// real `~/.SageFs`. Not deleted afterward (matching the rest of this
/// suite's temp-dir tests); OS temp cleanup handles it.
let private freshDir () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-updatecheck-tests-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  dir

[<Tests>]
let tests =
  testList "UpdateCheckService" [

    testList "dismissVersion / isDismissed — round trip through the on-disk cache" [
      testCase "an undismissed version reads as not dismissed" <| fun _ ->
        UpdateCheckService.isDismissed (freshDir ()) (System.Version "0.6.700")
        |> Expect.isFalse "not dismissed"

      testCase "dismissing a version persists across a fresh read" <| fun _ ->
        let dir = freshDir ()
        UpdateCheckService.dismissVersion dir (System.Version "0.6.700")
        UpdateCheckService.isDismissed dir (System.Version "0.6.700")
        |> Expect.isTrue "dismissed"

      testCase "dismissing one version does not silence a different one (issue #136's own requirement)" <| fun _ ->
        let dir = freshDir ()
        UpdateCheckService.dismissVersion dir (System.Version "0.6.690")
        UpdateCheckService.isDismissed dir (System.Version "0.6.750")
        |> Expect.isFalse "0.6.750 still nags"

      testCase "dismissing the same version twice is idempotent" <| fun _ ->
        let dir = freshDir ()
        UpdateCheckService.dismissVersion dir (System.Version "0.6.700")
        UpdateCheckService.dismissVersion dir (System.Version "0.6.700")
        UpdateCheckService.isDismissed dir (System.Version "0.6.700")
        |> Expect.isTrue "still dismissed, no crash on the duplicate write"

      testCase "a missing cache directory is created rather than throwing" <| fun _ ->
        let dir = Path.Combine(Path.GetTempPath(), "sagefs-updatecheck-tests-" + Guid.NewGuid().ToString("N"))
        // deliberately NOT created — dismissVersion must create it itself
        UpdateCheckService.dismissVersion dir (System.Version "0.6.700")
        UpdateCheckService.isDismissed dir (System.Version "0.6.700")
        |> Expect.isTrue "created and dismissed"
    ]

    testList "evaluateForCli — never a false 'up to date' or a crash" [
      testCase "an unparseable daemon-reported version is CheckFailed, never a crash or a fabricated verdict" <| fun _ ->
        match UpdateCheckService.evaluateForCli (freshDir ()) "not-a-version" with
        | UpdateOutcome.CheckFailed _ -> ()
        | other -> failtestf "expected CheckFailed for an unparseable version, got %A" other

      testCase "running under the test host (not installed under ~/.dotnet/tools) never claims UpdateAvailable, however old the reported version is" <| fun _ ->
        // The test host's own executable path is never under the dotnet
        // tools dir, so InstallKind.classify sees LocalBuild here — this is
        // exactly the false-positive class issue #136 asks to be
        // conservative about: a dev/CI process must never render a
        // staleness warning meant for an installed global tool.
        match UpdateCheckService.evaluateForCli (freshDir ()) "0.0.1" with
        | UpdateOutcome.UpdateAvailable _ -> failtest "a local/dev process must never render UpdateAvailable"
        | _ -> ()
    ]

    testList "checkOnceAsync — self-consistency" [
      testTask "the returned outcome is exactly what currentOutcome() reads back immediately after" {
        let dir = freshDir ()
        let! returned = UpdateCheckService.checkOnceAsync dir
        UpdateCheckService.currentOutcome ()
        |> Expect.equal "matches the return value" returned
      }

      testTask "never returns UpdateAvailable when running under the test host" {
        // Same reasoning as evaluateForCli above — no network call is even
        // reachable from this branch (LocalBuild short-circuits first), so
        // this is hermetic and can't flake on network availability.
        let dir = freshDir ()
        let! outcome = UpdateCheckService.checkOnceAsync dir
        match outcome with
        | UpdateOutcome.UpdateAvailable _ -> failtest "a local/dev process must never render UpdateAvailable"
        | _ -> ()
      }
    ]
  ]
