module SageFs.Tests.McpFrictionSummaryToolTests

open System
open System.Text.Json
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

    testCaseTask "get_friction_report returns ranked actionable local friction as JSON" <| fun () -> task {
      let persistence = inMemoryPersistence ()
      let baseCtx = sharedCtx ()
      let ctx = { baseCtx with Persistence = persistence }
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      let! _ = tools.report_friction("run_tests", "needed_another_tool", "Exact test name was not obvious.", "list_tests")
      let! reportJson = tools.get_friction_report()

      let doc = JsonDocument.Parse(reportJson)
      doc.RootElement.GetProperty("TotalFeedbackItems").GetInt32()
      |> Expect.equal "report should expose feedback count" 1

      let topTools = doc.RootElement.GetProperty("HighestPriorityTools")
      (topTools.GetArrayLength(), 0)
      |> Expect.isGreaterThan "report should expose at least one actionable tool"

      let top = topTools[0]
      top.GetProperty("Tool").GetString()
      |> Expect.equal "feedback-heavy complained-about tool should rise to the top" "run_tests"
      top.GetProperty("SuggestedFixTarget").GetString()
      |> Expect.stringContains "top remediation target should reflect the resolving follow-up" "list_tests"

      let recentFeedback = doc.RootElement.GetProperty("RecentFeedback")
      (recentFeedback.GetArrayLength(), 0)
      |> Expect.isGreaterThan "report should expose recent explicit complaints"

      let firstFeedback = recentFeedback[0]
      firstFeedback.GetProperty("Tool").GetString()
      |> Expect.equal "recent feedback should preserve the complained-about tool" "run_tests"
      firstFeedback.GetProperty("LatestAlternative").GetString()
      |> Expect.equal "recent feedback should preserve the resolving alternative" "list_tests"

      let recommended = doc.RootElement.GetProperty("RecommendedWorkItems")
      (recommended.GetArrayLength(), 0)
      |> Expect.isGreaterThan "report should expose recommended work items"
      let firstWorkItem = recommended[0]
      firstWorkItem.GetProperty("TargetTool").GetString()
      |> Expect.equal "first recommended work item should point at the complained-about tool" "run_tests"
      firstWorkItem.GetProperty("SuggestedAction").GetString()
      |> Expect.stringContains "recommended action should preserve the resolving alternative" "list_tests"
    }
  ]
