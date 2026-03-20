module SageFs.Tests.McpExplicitFeedbackTests

open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure

[<Tests>]
let tests =
  testList "MCP explicit feedback" [
    testCaseTask "report_friction stores structured explicit feedback locally" <| fun () -> task {
      let persistence = inMemoryPersistence ()
      let baseCtx = sharedCtx ()
      let ctx = { baseCtx with Persistence = persistence }
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      let! result =
        tools.report_friction(
          "run_tests",
          "needed_another_tool",
          "Exact test name was not obvious.",
          "list_tests")

      result |> Expect.equal "tool should acknowledge local persistence" "Recorded local friction feedback."

      let! envelope = SageFs.Features.McpFrictionRecorder.Recorder.readEnvelope persistence
      envelope.Feedback.Length |> Expect.equal "one explicit feedback item should be stored" 1
      let feedback = envelope.Feedback |> List.head
      feedback.Tool |> SageFs.Features.FrictionTelemetryTypes.ToolName.value |> Expect.equal "should preserve tool name" "run_tests"
      feedback.ShortReason |> Expect.equal "should preserve reason" "Exact test name was not obvious."
    }
  ]
