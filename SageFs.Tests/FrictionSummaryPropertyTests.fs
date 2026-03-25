module SageFs.Tests.FrictionSummaryPropertyTests

open System
open Expecto
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry

let private ok = function
  | Ok value -> value
  | Error err -> failwith err

let private tool text = ToolName.create text |> ok
let private session text = SessionRef.create text |> ok
let private duration value = DurationMs.create value |> ok

let private baseEvent outcome =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool "run_tests"
    Intent = IntentKind.RunExactTest
    Outcome = outcome
    Duration = duration 7
    FollowUp = FollowUp.NoFollowUpYet
    ContextCost = ContextCost.Focused
    SageFsVersion = "" }

[<Tests>]
let tests =
  testList "Friction summary properties" [
    testProperty "blocker summary never invents blockers absent from source events" <| fun () ->
      let events = [
        baseEvent (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound)
        baseEvent FrictionOutcome.CompletedCleanly
      ]
      Summaries.topBlockers events
      |> List.forall (fun summary -> summary.Blocker = BlockerKind.ExactTestNotFound)

    testProperty "abandoned count never exceeds invocation count" <| fun () ->
      let events = [
        baseEvent FrictionOutcome.AbandonedWithoutResolution
        baseEvent FrictionOutcome.CompletedCleanly
      ]
      Summaries.toolSummaries events
      |> List.forall (fun summary -> summary.AbandonedCount <= summary.Invocations)
  ]
