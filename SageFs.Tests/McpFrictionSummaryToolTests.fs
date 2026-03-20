module SageFs.Tests.McpFrictionSummaryToolTests

open System
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure

[<Tests>]
let tests =
  testList "MCP friction summary tool" [
    testCaseTask "get_friction_summary reports compact local counts" <| fun () -> task {
      let persistence = inMemoryPersistence ()
      let baseCtx = sharedCtx ()
      let ctx = { baseCtx with Persistence = persistence }
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      let! _ = tools.report_friction("run_tests", "output_too_large", "Too much output to inspect.", "")
      let! summary = tools.get_friction_summary()

      summary |> Expect.stringContains "should count blocker families" "Top blockers:"
      summary |> Expect.stringContains "should count tracked tools" "Tracked tools:"
      summary |> Expect.stringContains "should count explicit feedback" "Explicit feedback items: 1"
    }
  ]
