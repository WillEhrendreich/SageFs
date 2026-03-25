module SageFs.Tests.FrictionOtelProjectionPropertyTests

open System
open Expecto
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionOtelProjection

let private ok = function
  | Ok value -> value
  | Error err -> failwith err

[<Tests>]
let tests =
  testList "Friction OTEL projection properties" [
    testProperty "blocked events always project a blocker attribute" <| fun () ->
      let event =
        { OccurredAtUtc = DateTimeOffset.UtcNow
          Session = SessionRef.create "session-1" |> ok
          Tool = ToolName.create "run_tests" |> ok
          Intent = IntentKind.RunExactTest
          Outcome = FrictionOutcome.EncounteredBlocker BlockerKind.LoadedStateStale
          Duration = DurationMs.create 5 |> ok
          FollowUp = FollowUp.NoFollowUpYet
          ContextCost = ContextCost.Focused
          SageFsVersion = "" }
      Projection.tags event
      |> List.exists (fun (key, value) -> key = "sagefs.mcp.blocker_kind" && unbox<string> value = "LoadedStateStale")
  ]
