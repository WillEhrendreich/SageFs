module SageFs.Tests.FrictionOtelProjectionScenarioTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionOtelProjection

let private ok = function
  | Ok value -> value
  | Error err -> failtestf "expected success, got error: %s" err

let private event =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = SessionRef.create "session-1" |> ok
    Tool = ToolName.create "run_tests" |> ok
    Intent = IntentKind.RunExactTest
    Outcome = FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound
    Duration = DurationMs.create 12 |> ok
    FollowUp = FollowUp.FollowedByTool (ToolName.create "list_tests" |> ok)
    ContextCost = ContextCost.Focused
    SageFsVersion = "" }

[<Tests>]
let tests =
  testList "Friction OTEL projection scenarios" [
    testCase "when a blocker is projected, OTEL preserves the blocker semantics needed for correlation" <| fun _ ->
      let tags = Projection.tags event
      tags
      |> List.exists (fun (key, value) -> key = "sagefs.mcp.blocker_kind" && unbox<string> value = "ExactTestNotFound")
      |> Expect.isTrue "blocker tag should be preserved"

    testCase "OTEL projection stays compact and does not leak raw code payloads" <| fun _ ->
      let tags = Projection.tags event
      tags
      |> List.exists (fun (key, _) -> key.Contains("code") || key.Contains("prompt"))
      |> Expect.isFalse "projection should not include raw code or prompts"
  ]
