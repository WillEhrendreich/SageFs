module SageFs.Tests.FrictionOtelProjectionTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionOtelProjection

let private ok = function
  | Ok value -> value
  | Error err -> failtestf "expected success, got error: %s" err

[<Tests>]
let tests =
  testList "Friction OTEL projection tests" [
    testCase "recovered-via-different-tool records resolution tool attribute" <| fun _ ->
      let event =
        { OccurredAtUtc = DateTimeOffset.UtcNow
          Session = SessionRef.create "session-1" |> ok
          Tool = ToolName.create "targeted_verify" |> ok
          Intent = IntentKind.VerifyChangedBehavior
          Outcome = FrictionOutcome.RecoveredVia (ResolutionKind.SolvedWithDifferentTool (ToolName.create "get_fsi_status" |> ok))
          Duration = DurationMs.create 9 |> ok
          FollowUp = FollowUp.FollowedByTool (ToolName.create "get_fsi_status" |> ok)
          ContextCost = ContextCost.Focused
          SageFsVersion = "" }
      let tags = Projection.tags event
      tags
      |> List.exists (fun (key, value) -> key = "sagefs.mcp.resolution_tool" && unbox<string> value = "get_fsi_status")
      |> Expect.isTrue "resolution tool should be projected"
  ]
