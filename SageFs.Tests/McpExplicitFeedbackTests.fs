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
      let baseCtx = sharedCtx ()
      let tools = SageFsTools(baseCtx, NullLogger<SageFsTools>.Instance)

      let! result =
        tools.report_friction(
          "run_tests",
          "needed_another_tool",
          "Exact test name was not obvious.",
          "list_tests")

      result |> Expect.equal "tool should acknowledge local persistence" "Recorded local friction feedback."

      // FrictionStore is required now — no fallback to EventPersistence
      match baseCtx.FrictionStore with
      | Some store ->
        let! envelopeResult = SageFs.Features.McpFrictionRecorder.Recorder.readEnvelopeDirect store
        match envelopeResult with
        | Ok envelope ->
          envelope.Feedback.Length |> Expect.equal "one explicit feedback item should be stored" 1
          let feedback = envelope.Feedback |> List.head
          feedback.Tool |> SageFs.Features.FrictionTelemetryTypes.ToolName.value |> Expect.equal "should preserve tool name" "run_tests"
          feedback.ShortReason |> Expect.equal "should preserve reason" "Exact test name was not obvious."
        | Error err ->
          failwith (sprintf "Failed to read friction envelope: %s" err)
      | None -> ()
    }
  ]
