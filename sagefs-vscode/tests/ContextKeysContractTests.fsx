// WHY — the command palette showed `Enable Live Testing` and
// `Disable Live Testing` simultaneously in every state, and every
// daemon-only command in every state, because `sagefs:daemonRunning` was
// referenced by package.json's keybindings but never SET, and no key
// existed at all for live-testing/session/project presence. These pin the
// total, exhaustive mappings behind every context key package.json now
// gates on, so a regression (a new DU case, an off-by-one on session count)
// fails here under `dotnet fsi` instead of silently leaving a stale palette
// entry.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring
// SessionsTreeContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/LiveTestingTypes.fs"
#load "../src/ContextKeysPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.ContextKeysPure

let tests =
  testList "VS Code context keys - pure decisions" [

    testCase "WHY - liveTestingEnabledContext - On means the Disable action becomes available" <| fun _ ->
      liveTestingEnabledContext VscLiveTestingEnabled.LiveTestingOn
      |> Expect.isTrue "on -> true"

    testCase "WHY - liveTestingEnabledContext - Off means the Enable action becomes available" <| fun _ ->
      liveTestingEnabledContext VscLiveTestingEnabled.LiveTestingOff
      |> Expect.isFalse "off -> false"

    testCase "WHY - liveTestingEnabledFromDiscoveryState - disabled is authoritative off, regardless of any stale total" <| fun _ ->
      liveTestingEnabledFromDiscoveryState "disabled"
      |> Expect.isFalse "disabled -> false"

    testCase "WHY - liveTestingEnabledFromDiscoveryState - an old daemon sending no state fails closed to off" <| fun _ ->
      liveTestingEnabledFromDiscoveryState ""
      |> Expect.isFalse "empty -> false (fail closed)"

    testCase "WHY - liveTestingEnabledFromDiscoveryState - discovering/ready states all mean on" <| fun _ ->
      liveTestingEnabledFromDiscoveryState "discovering" |> Expect.isTrue "discovering -> true"
      liveTestingEnabledFromDiscoveryState "ready_zero_tests" |> Expect.isTrue "ready_zero_tests -> true"
      liveTestingEnabledFromDiscoveryState "ready_with_tests" |> Expect.isTrue "ready_with_tests -> true"

    testCase "WHY - hasSessionContext - zero sessions means the Sessions view offers Create Session" <| fun _ ->
      hasSessionContext 0 |> Expect.isFalse "zero -> false"

    testCase "WHY - hasSessionContext - any session at all, active or not, counts" <| fun _ ->
      hasSessionContext 1 |> Expect.isTrue "one -> true"
      hasSessionContext 5 |> Expect.isTrue "many -> true"

    testCase "WHY - hasFsharpProjectContext - an empty scan means the welcome content says so and offers Browse" <| fun _ ->
      hasFsharpProjectContext 0 |> Expect.isFalse "zero -> false"

    testCase "WHY - hasFsharpProjectContext - any candidate at all is enough" <| fun _ ->
      hasFsharpProjectContext 1 |> Expect.isTrue "one -> true"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
