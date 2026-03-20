module SageFs.Tests.McpFrictionScenarioTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry

let private tool text =
  match ToolName.create text with
  | Ok value -> value
  | Error err -> failtestf "expected tool name, got error: %s" err

let private session text =
  match SessionRef.create text with
  | Ok value -> value
  | Error err -> failtestf "expected session ref, got error: %s" err

let private duration value =
  match DurationMs.create value with
  | Ok measured -> measured
  | Error err -> failtestf "expected duration, got error: %s" err

let private frictionEvent outcome followUp =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool "run_tests"
    Intent = IntentKind.RunExactTest
    Outcome = outcome
    Duration = duration 12
    FollowUp = followUp
    ContextCost = ContextCost.Focused }

[<Tests>]
let tests =
  testList "MCP friction scenarios" [
    testCase "when exact test lookup fails, SageFs records exact-test friction instead of vague confusion" <| fun _ ->
      let event = frictionEvent (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) FollowUp.NoFollowUpYet
      event
      |> FrictionEvent.outcomeKind
      |> Expect.equal "exact test misses should be a blocked outcome" OutcomeKind.Blocked

    testCase "when a blocked tool requires another tool, SageFs records the transition so overlap can be reduced" <| fun _ ->
      let first = frictionEvent (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) (FollowUp.FollowedByTool (tool "list_tests"))
      let second = frictionEvent FrictionOutcome.CompletedCleanly FollowUp.SessionEnded
      let transitions = Summaries.transitions [ first; second ]
      transitions
      |> Expect.equal
        "the blocker should record the follow-up tool"
        [ { FromTool = tool "run_tests"; ToTool = tool "list_tests"; Frequency = 1 } ]

    testCase "when a tool succeeds cleanly, SageFs does not invent friction" <| fun _ ->
      let event = frictionEvent FrictionOutcome.CompletedCleanly FollowUp.SessionEnded
      let blockers = Summaries.topBlockers [ event ]
      blockers |> Expect.isEmpty "clean success should not create blocker summaries"
  ]
