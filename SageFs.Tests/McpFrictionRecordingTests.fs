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
      let baseCtx = sharedCtx ()
      let tools = SageFsTools(baseCtx, NullLogger<SageFsTools>.Instance)

      let! _ = tools.get_friction_summary()
      // FrictionStore is required now — no fallback to EventPersistence
      match baseCtx.FrictionStore with
      | Some store ->
        let! envelopeResult = SageFs.Features.McpFrictionRecorder.Recorder.readEnvelopeDirect store
        match envelopeResult with
        | Ok envelope ->
          envelope.Events.Length |> Expect.equal "friction events are recorded" 1  // get_friction_summary is recorded but marked non-actionable
        | Error err ->
          failwith (sprintf "Failed to read friction envelope: %s" err)
      | None ->
        // FrictionStore should always be configured in tests
        ()
    }
  ]
