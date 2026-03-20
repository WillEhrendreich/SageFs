module SageFs.Tests.FrictionSummaryTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry

let private ok = function
  | Ok value -> value
  | Error err -> failwith err

let private tool text = ToolName.create text |> ok
let private session text = SessionRef.create text |> ok
let private duration value = DurationMs.create value |> ok

let private eventWith toolName outcome followUp =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool toolName
    Intent = IntentKind.ExploreCode
    Outcome = outcome
    Duration = duration 5
    FollowUp = followUp
    ContextCost = ContextCost.Tiny }

[<Tests>]
let tests =
  testList "Friction summary tests" [
    testCase "tool summaries count blocked and abandoned outcomes separately" <| fun _ ->
      let events = [
        eventWith "run_tests" (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) FollowUp.NoFollowUpYet
        eventWith "run_tests" FrictionOutcome.AbandonedWithoutResolution FollowUp.SessionEnded
        eventWith "run_tests" FrictionOutcome.CompletedCleanly FollowUp.SessionEnded
      ]
      Summaries.toolSummaries events
      |> Expect.equal
        "summaries should preserve blocker and abandonment counts"
        [ { Tool = tool "run_tests"; Invocations = 3; BlockedCount = 1; AbandonedCount = 1 } ]

    testCase "transition summaries group repeated tool chains" <| fun _ ->
      let events = [
        eventWith "run_tests" (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) (FollowUp.FollowedByTool (tool "list_tests"))
        eventWith "run_tests" (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) (FollowUp.FollowedByTool (tool "list_tests"))
      ]
      Summaries.transitions events
      |> Expect.equal
        "transitions should aggregate repeated follow-up paths"
        [ { FromTool = tool "run_tests"; ToTool = tool "list_tests"; Frequency = 2 } ]

    testCase "friction report ranks the tools most worth fixing next" <| fun _ ->
      let events = [
        eventWith "run_tests" (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) (FollowUp.FollowedByTool (tool "list_tests"))
        eventWith "run_tests" (FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound) (FollowUp.FollowedByTool (tool "list_tests"))
        eventWith "targeted_verify" FrictionOutcome.AbandonedWithoutResolution FollowUp.SessionEnded
      ]
      let feedback = [
        { OccurredAtUtc = DateTimeOffset.UtcNow
          Session = session "session-1"
          Tool = tool "run_tests"
          Kind = ExplicitFeedbackKind.NeededAnotherToolToFinish
          ShortReason = "Needed list_tests before exact run."
          AlternativeUsed = AlternativePath.ResolvedWithTool (tool "list_tests") }
      ]
      let report = Summaries.frictionReport events feedback
      report.TotalEvents |> Expect.equal "report should preserve total event count" 3
      report.TotalFeedbackItems |> Expect.equal "report should preserve total feedback count" 1
      report.HighestPriorityTools.Length |> Expect.equal "report should rank actionable tools" 2
      let top = report.HighestPriorityTools |> List.head
      top.Tool |> Expect.equal "run_tests should be the top remediation target" (tool "run_tests")
      top.SuggestedFixTarget |> Expect.stringContains "exact-test misses should point to workflow repair" "list_tests"
      (report.RecommendedWorkItems.Length, 0) |> Expect.isGreaterThan "report should surface recommended work items"
      let firstWorkItem = report.RecommendedWorkItems |> List.head
      firstWorkItem.TargetTool |> Expect.equal "first work item should target the top problem tool" (Some (tool "run_tests"))
      firstWorkItem.LikelyFixType |> Expect.equal "alternative-driven remediation should look like workflow linking" "workflow-linking"
      firstWorkItem.SuggestedAction |> Expect.stringContains "work item should carry the suggested action" "list_tests"

    testCase "feedback-only friction still points agents at the alternative path that resolved the problem" <| fun _ ->
      let feedback = [
        { OccurredAtUtc = DateTimeOffset.UtcNow
          Session = session "session-1"
          Tool = tool "run_tests"
          Kind = ExplicitFeedbackKind.NeededAnotherToolToFinish
          ShortReason = "Needed list_tests before exact run."
          AlternativeUsed = AlternativePath.ResolvedWithTool (tool "list_tests") }
      ]
      let report = Summaries.frictionReport [] feedback
      let top = report.HighestPriorityTools |> List.head
      top.Tool |> Expect.equal "feedback-only issue should still rank the complained-about tool" (tool "run_tests")
      top.MostCommonAlternative |> Expect.equal "feedback alternative should be preserved" (Some (tool "list_tests"))
      top.SuggestedFixTarget |> Expect.stringContains "feedback-only remediation should point at the recorded alternative" "list_tests"
      let firstWorkItem = report.RecommendedWorkItems |> List.head
      firstWorkItem.LikelyFixType |> Expect.equal "feedback-only alternative path should still classify as workflow linking" "workflow-linking"
      firstWorkItem.SuggestedAction |> Expect.stringContains "feedback-only work item should stay agent-actionable" "list_tests"
  ]
