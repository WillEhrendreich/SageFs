module SageFs.Tests.McpFrictionRecordingTests

open System
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure

[<Tests>]
let tests =
  testList "MCP friction recording" [
    testCaseTask "successful MCP tool calls are recorded as clean friction events" <| fun () -> task {
      let persistence = inMemoryPersistence ()
      let baseCtx = sharedCtx ()
      let ctx = { baseCtx with Persistence = persistence }
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      let! _ = tools.get_friction_summary()
      let! envelope = SageFs.Features.McpFrictionRecorder.Recorder.readEnvelope persistence

      envelope.Events.Length |> Expect.equal "one tool call should create one friction event" 1
      let recorded = envelope.Events |> List.head
      recorded.Tool |> SageFs.Features.FrictionTelemetryTypes.ToolName.value |> Expect.equal "should record tool name" "get_friction_summary"
      recorded.Outcome |> Expect.equal "successful tool calls should record clean completion" SageFs.Features.FrictionTelemetryTypes.FrictionOutcome.CompletedCleanly
    }
  ]
