module SageFs.Tests.McpFrictionSummaryToolTests

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open Microsoft.Extensions.Logging.Abstractions
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes

let private ok = function
  | Ok value -> value
  | Error err -> failwith err

/// Local event builder mirroring ObservedFrictionTypesTests.event. NOT
/// shared with that module: SageFs.Tests.fsproj compiles this file before
/// ObservedFrictionTypesTests.fs, so `open`ing it here is a compile-order
/// landmine (the same class of hazard as this brief's Core-side finding) —
/// duplicating this tiny builder avoids it without reordering the fsproj.
let private baseTimeUtc = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private mkEvent (tool: string) (outcome: FrictionOutcome) (atUtcOffsetSeconds: float) : FrictionEvent =
  { OccurredAtUtc = baseTimeUtc.AddSeconds atUtcOffsetSeconds
    Session = SessionRef.create "mcp" |> ok
    Tool = ToolName.create tool |> ok
    Intent = IntentKind.ExploreCode
    Outcome = outcome
    Duration = DurationMs.create 1 |> ok
    FollowUp = FollowUp.NoFollowUpYet
    ContextCost = ContextCost.Focused
    SageFsVersion = ""; AgentKey = ""; ErrorSignature = "" }

/// The harvest replay pattern (observed-friction-plan.md §e /
/// ObservedFrictionAcceptanceTests.fs): 4x "unknown (missing argument)"
/// unattributed failures + a 72x get_fsi_status polling burst. Reused here
/// to prove the SAME signals reach the daemon's `reportDirect` read model
/// (Brief B7), not just the pure `detectAll` engine.
let private harvestReplayEvents : FrictionEvent list =
  let unattributed =
    List.init 4 (fun i ->
      mkEvent "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
  let polling =
    List.init 72 (fun i -> mkEvent "get_fsi_status" FrictionOutcome.CompletedCleanly (10.0 + float i))
  unattributed @ polling

let private seedStore (store: SageFs.Features.FrictionSqlite.FrictionStore) (events: FrictionEvent list) = task {
  for e in events do
    let! (result: Result<unit, string>) = SageFs.Features.McpFrictionRecorder.Recorder.appendEventDirect store e
    match result with
    | Ok () -> ()
    | Error err -> failwithf "failed to seed friction store: %s" err
}

[<Tests>]
let tests =
  testList "MCP friction summary tool" [
    testCaseTask "get_friction_summary reports compact local counts" <| fun () -> task {
      let baseCtx = sharedCtx ()
      let tools = SageFsTools(baseCtx, NullLogger<SageFsTools>.Instance)

      let! _ = tools.report_friction("run_tests", "output_too_large", "Too much output to inspect.", "")
      let! summary = tools.get_friction_summary()

      summary |> Expect.stringContains "should count blocker families" "Top blockers:"
      summary |> Expect.stringContains "should count tracked tools" "Tracked tools:"
      summary |> Expect.stringContains "should count explicit feedback" "Explicit feedback items:"
    }

    testCaseTask "get_friction_report returns ranked actionable local friction as JSON" <| fun () -> task {
      let baseCtx = sharedCtx ()
      let tools = SageFsTools(baseCtx, NullLogger<SageFsTools>.Instance)

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
      firstWorkItem.GetProperty("LikelyFixType").GetString()
      |> Expect.equal "recommended work item should classify the fix shape" "workflow-linking"
      firstWorkItem.GetProperty("SuggestedAction").GetString()
      |> Expect.stringContains "recommended action should preserve the resolving alternative" "list_tests"
    }

    // ── B7 — observed signals surfaced through the daemon read model ──

    testCaseTask "reportDirect surfaces observed signals from the harvest replay pattern (B7)" <| fun () -> task {
      let store = tempFrictionStore ()
      do! seedStore store harvestReplayEvents

      let! bundleResult = SageFs.Features.McpFrictionRecorder.Recorder.reportDirect store None
      match bundleResult with
      | Error err -> failwithf "reportDirect failed: %s" err
      | Ok bundle ->
        bundle.ObservedSignals
        |> List.exists (fun d ->
          match d.Signal with
          | FrictionSignal.UnattributedFailure("unknown (missing argument)", 4) -> true
          | _ -> false)
        |> Expect.isTrue "reportDirect's read model should carry the UnattributedFailure(4) observed signal"

        bundle.ObservedSignals
        |> List.exists (fun d ->
          match d.Signal with
          | FrictionSignal.ExcessivePolling(tool, 72, _, _) -> ToolName.value tool = "get_fsi_status"
          | _ -> false)
        |> Expect.isTrue "reportDirect's read model should carry the ExcessivePolling(72) observed signal"
    }

    testCaseTask "frictionReportJson emits an ObservedSignals array with the expected stable ids (B7)" <| fun () -> task {
      let store = tempFrictionStore ()
      do! seedStore store harvestReplayEvents

      let! bundleResult = SageFs.Features.McpFrictionRecorder.Recorder.reportDirect store None
      match bundleResult with
      | Error err -> failwithf "reportDirect failed: %s" err
      | Ok bundle ->
        let json = frictionReportJson bundle.Report bundle.ObservedSignals
        let doc = JsonDocument.Parse(json)
        let observed = doc.RootElement.GetProperty("ObservedSignals")
        let ids =
          [ for i in 0 .. observed.GetArrayLength() - 1 -> observed[i].GetProperty("Id").GetString() ]

        ids
        |> Expect.containsAll
          "report JSON should carry both observed-signal stable ids"
          [ "observed.unattributed-failure"; "observed.excessive-polling" ]
    }

    testCaseTask "reportDirect yields no observed signals for an empty store — no phantom signals (B7)" <| fun () -> task {
      let store = tempFrictionStore ()

      let! bundleResult = SageFs.Features.McpFrictionRecorder.Recorder.reportDirect store None
      match bundleResult with
      | Error err -> failwithf "reportDirect failed: %s" err
      | Ok bundle ->
        bundle.ObservedSignals |> Expect.isEmpty "an empty friction store must yield zero observed signals"
    }

    testProperty "detectAll never fabricates a signal from an empty event stream, for any threshold config (B7)" <|
      fun (pollingMinCalls: PositiveInt) (resetThrashCount: PositiveInt) (repeatedErrorRun: PositiveInt) ->
        let cfg =
          { DetectorConfig.defaults with
              PollingMinCalls = pollingMinCalls.Get
              ResetThrashCount = resetThrashCount.Get
              RepeatedErrorRun = repeatedErrorRun.Get }
        SageFs.Features.ObservedFriction.detectAll cfg [] |> List.isEmpty
  ]
