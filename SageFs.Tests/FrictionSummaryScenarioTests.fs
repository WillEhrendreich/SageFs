module SageFs.Tests.FrictionSummaryScenarioTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry

let private ok = function
  | Ok value -> value
  | Error err -> failwith err

let private tool text = ToolName.create text |> ok
let private session text = SessionRef.create text |> ok
let private duration value = DurationMs.create value |> ok

let private mkEvent toolName outcome followUp =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool toolName
    Intent = IntentKind.VerifyChangedBehavior
    Outcome = outcome
    Duration = duration 10
    FollowUp = followUp
    ContextCost = ContextCost.Focused
    SageFsVersion = "" }

[<Tests>]
let tests =
  testList "Friction summary scenarios" [
    testCase "top blocker summary reveals recurring exact-test misses" <| fun _ ->
      let events = [
        mkEvent "run_tests" (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) FollowUp.NoFollowUpYet
        mkEvent "run_tests" (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) (FollowUp.FollowedByTool (tool "list_tests"))
      ]
      Summaries.topBlockers events
      |> Expect.equal
        "exact test misses should aggregate into one summary"
        [ { Blocker = BlockerKind.ExactTestNotFound; Count = 2; MostAffectedTools = [ tool "run_tests" ] } ]

    testCase "abandoned tool summary reveals tools that do not get agents to resolution" <| fun _ ->
      let events = [
        mkEvent "targeted_verify" FrictionOutcome.AbandonedWithoutResolution FollowUp.SessionEnded
        mkEvent "targeted_verify" FrictionOutcome.CompletedCleanly FollowUp.SessionEnded
      ]
      Summaries.toolSummaries events
      |> Expect.equal
        "abandonment should remain visible in per-tool summaries"
        [ { Tool = tool "targeted_verify"; Invocations = 2; BlockedCount = 0; AbandonedCount = 1 } ]
  ]
