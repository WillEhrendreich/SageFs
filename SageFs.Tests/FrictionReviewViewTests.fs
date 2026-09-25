module SageFs.Tests.FrictionReviewViewTests

/// Phase 5 dashboard journey: the friction review drawer's pure view-model.
/// Pins the contract the drawer renders and the send handler POSTs:
/// - build() converts the canonical report to the sanitized outgoing shape
///   with counts + send history;
/// - IsEmpty is true only when there is nothing to review or send;
/// - withEdits re-derives the outgoing payload and the edited reason is
///   sanitized (a user cannot push a raw path/secret out through an edit).

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry
open SageFs.Features.FrictionSanitize
open SageFs.Features.FrictionReviewView
open SageFs.Features.FrictionSqlite
open SageFs.Features.ObservedFrictionTypes
open SageFs.Features.ObservedFriction
open SageFs.Server

let private ok = function
  | Ok value -> value
  | Error err -> failwith err

let private tool text = ToolName.create text |> ok
let private session text = SessionRef.create text |> ok
let private duration value = DurationMs.create value |> ok

/// A sample report that exercises every outbound section.
let private sampleReport () =
  let events = [
    { OccurredAtUtc = DateTimeOffset.UtcNow
      Session = session "session-1"
      Tool = tool "run_tests"
      Intent = IntentKind.ExploreCode
      Outcome = FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound
      Duration = duration 5
      FollowUp = FollowUp.NoFollowUpYet
      ContextCost = ContextCost.Tiny
      SageFsVersion = ""; AgentKey = ""; ErrorSignature = "" }
    { OccurredAtUtc = DateTimeOffset.UtcNow
      Session = session "session-1"
      Tool = tool "run_tests"
      Intent = IntentKind.ExploreCode
      Outcome = FrictionOutcome.AbandonedWithoutResolution
      Duration = duration 8
      FollowUp = FollowUp.SessionEnded
      ContextCost = ContextCost.Focused
      SageFsVersion = ""; AgentKey = ""; ErrorSignature = "" }
  ]
  let feedback = [
    { OccurredAtUtc = DateTimeOffset.UtcNow
      Session = session "session-1"
      Tool = tool "run_tests"
      Kind = ExplicitFeedbackKind.NeededAnotherToolToFinish
      ShortReason = "Needed list_tests before exact run on C:\\Users\\alice\\secret"
      AlternativeUsed = AlternativePath.ResolvedWithTool (tool "list_tests")
      SageFsVersion = "" }
  ]
  Summaries.frictionReport events feedback

/// The workstream's harvest replay pattern (observed-friction-plan.md §e /
/// ObservedFrictionAcceptanceTests.fs / McpFrictionSummaryToolTests.fs),
/// duplicated locally per the same compile-order rationale
/// McpFrictionSummaryToolTests.fs documents: SageFs.Tests.fsproj compiles
/// this file before ObservedFrictionTypesTests.fs, so sharing its `event`
/// builder here is a compile-order landmine. 4x "unknown (missing
/// argument)" unattributed failures + a 72x polling burst under the
/// historical tool name.
let private baseTimeUtc = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

/// The tool the recorded harvest burst was captured under. Duplicated from
/// `ObservedFrictionTypesTests.historicalPollingTool` for the same
/// compile-order reason the `event` builder is: SageFs.Tests.fsproj compiles
/// this file first. Both sides read the value from the product's retired-tool
/// vocabulary, so the two copies cannot name different things.
let private historicalPollingTool =
  SageFs.Affordances.RetiredTool.toToolName SageFs.Affordances.RetiredTool.GetFsiStatus

/// Defaults with the polling watch pinned back to the historical name, so a
/// harvest replay stays a replay of what was actually recorded.
let private harvestReplayConfig : DetectorConfig =
  { DetectorConfig.defaults with PollingTools = Set.ofList [ historicalPollingTool ] }

let private mkEvent (toolName: string) (outcome: FrictionOutcome) (atUtcOffsetSeconds: float) : FrictionEvent =
  { OccurredAtUtc = baseTimeUtc.AddSeconds atUtcOffsetSeconds
    Session = session "mcp"
    Tool = tool toolName
    Intent = IntentKind.ExploreCode
    Outcome = outcome
    Duration = duration 1
    FollowUp = FollowUp.NoFollowUpYet
    ContextCost = ContextCost.Focused
    SageFsVersion = ""; AgentKey = ""; ErrorSignature = "" }

let private harvestReplayEvents : FrictionEvent list =
  let unattributed =
    List.init 4 (fun i ->
      mkEvent "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
  let polling =
    List.init 72 (fun i -> mkEvent historicalPollingTool FrictionOutcome.CompletedCleanly (10.0 + float i))
  unattributed @ polling

let private sampleSentReports () = [
  { ReportId = "old-report"
    SentAtUtc = DateTimeOffset.UtcNow.AddHours(-2.0)
    SageFsVersion = "0.6.360"
    TotalEvents = 3
    TotalFeedbackItems = 1
    DestinationKind = "cloudflare-worker"
    DestinationUrlHash = "abc" }
  { ReportId = "new-report"
    SentAtUtc = DateTimeOffset.UtcNow.AddHours(-1.0)
    SageFsVersion = "0.6.370"
    TotalEvents = 2
    TotalFeedbackItems = 1
    DestinationKind = "cloudflare-worker"
    DestinationUrlHash = "def" }
]

[<Tests>]
let tests =
  testList "Friction review view" [

    testCase "build exposes sanitized outgoing counts and newest-first history" <| fun _ ->
      let snap = build (sampleReport ()) [] (sampleSentReports ())
      snap.EventCount |> Expect.equal "event count should pass through" 2
      snap.FeedbackCount |> Expect.equal "feedback count should pass through" 1
      snap.IsEmpty |> Expect.isFalse "sample report is not empty"
      snap.Outgoing.TotalEvents |> Expect.equal "outgoing total should match" 2
      snap.Outgoing.TotalFeedbackItems |> Expect.equal "outgoing feedback should match" 1
      snap.Outgoing.RecentFeedback.Length |> Expect.equal "outgoing should carry the feedback" 1
      snap.Outgoing.RecentFeedback.[0].Reason
      |> Expect.stringContains "raw reason is sanitized in the outgoing payload" "<path>"
      snap.Outgoing.RecentFeedback.[0].Reason.Contains "alice"
      |> Expect.isFalse "raw path must not appear in outgoing"
      snap.SentReports
      |> List.map (fun s -> s.ReportId)
      |> Expect.equal "send history should be newest-first" [ "new-report"; "old-report" ]

    testCase "build marks an empty report as empty" <| fun _ ->
      let empty = Summaries.frictionReport [] []
      let snap = build empty [] []
      snap.IsEmpty |> Expect.isTrue "no events and no feedback is empty"
      snap.EventCount |> Expect.equal "zero events" 0
      snap.SentReports |> Expect.isEmpty "no send history"
      snap.ObservedSignals |> Expect.isEmpty "no events means no observed signals either"

    testCase "withEdits re-derives outgoing and sanitizes the edited reason" <| fun _ ->
      let snap = build (sampleReport ()) [] []
      let edits =
        Map.ofList [
          ("run_tests", "NeededAnotherToolToFinish"),
          "User edited: see C:\\Users\\mallory\\evil for the full trace"
        ]
      let edited = withEdits edits snap
      edited.Outgoing.RecentFeedback.[0].Reason
      |> Expect.stringContains "edited reason should appear" "User edited"
      edited.Outgoing.RecentFeedback.[0].Reason
      |> Expect.stringContains "edited reason is sanitized in the payload" "<path>"
      edited.Outgoing.RecentFeedback.[0].Reason.Contains "mallory"
      |> Expect.isFalse "edited raw path must not leak"

    testCase "parseEditsJson accepts pipe-separated keys and drops malformed ones" <| fun _ ->
      let json = """{"run_tests|NeededAnotherToolToFinish":"edited reason","list_tests|ResultDidNotEstablishTrust":"other","badkey":"no separator","":""}"""
      let parsed = parseEditsJson json
      parsed |> Map.toList
      |> Expect.equal
        "only well-formed (tool,kind) keys should survive"
        [ ("list_tests", "ResultDidNotEstablishTrust"), "other"
          ("run_tests", "NeededAnotherToolToFinish"), "edited reason" ]

    testCase "parseEditsJson rejects non-object and invalid JSON" <| fun _ ->
      parseEditsJson "[1,2]"
      |> Expect.isEmpty "array should parse to empty"
      parseEditsJson "not json at all"
      |> Expect.isEmpty "invalid json should parse to empty"
      parseEditsJson ""
      |> Expect.isEmpty "empty string should parse to empty"

    // ── B8: observed friction surfaced on the snapshot ──────────────────

    testCase "build exposes observed signals computed from the harvest replay pattern (B8)" <| fun _ ->
      let report = Summaries.frictionReport harvestReplayEvents []
      let observedSignals = detectAll harvestReplayConfig harvestReplayEvents
      let snap = build report observedSignals []

      snap.ObservedSignals
      |> List.exists (fun d ->
        match d.Signal with
        | FrictionSignal.UnattributedFailure("unknown (missing argument)", 4) -> true
        | _ -> false)
      |> Expect.isTrue "snapshot should carry the UnattributedFailure(4) observed signal"

      snap.ObservedSignals
      |> List.exists (fun d ->
        match d.Signal with
        | FrictionSignal.ExcessivePolling(t, 72, _, _) -> ToolName.value t = "get_fsi_status"
        | _ -> false)
      |> Expect.isTrue "snapshot should carry the ExcessivePolling(72) observed signal"

    testCase "build yields no observed signals for an empty event stream — no phantom signals (B8)" <| fun _ ->
      let report = Summaries.frictionReport [] []
      let observedSignals = detectAll DetectorConfig.defaults []
      let snap = build report observedSignals []
      snap.ObservedSignals |> Expect.isEmpty "an empty event stream must yield zero observed signals"

    // ── B8: renderFrictionPanel markup ───────────────────────────────────

    testCase "renderFrictionPanel renders the observed-friction section with one row per signal" <| fun _ ->
      let report = Summaries.frictionReport harvestReplayEvents []
      let observedSignals = detectAll harvestReplayConfig harvestReplayEvents
      let snap = build report observedSignals []
      let html = DashboardFragments.renderFrictionPanel snap |> renderNode

      html |> Expect.stringContains "should render the observed-friction section header" "Observed friction"
      html
      |> Expect.stringContains
        "should render a row naming the unattributed-failure signal"
        "observed.unattributed-failure"
      html
      |> Expect.stringContains
        "should render a row naming the excessive-polling signal"
        "observed.excessive-polling"

    testCase "renderFrictionPanel renders a quiet line when there are zero observed signals — whole panel still renders" <| fun _ ->
      let empty = Summaries.frictionReport [] []
      let snap = build empty [] []
      let html = DashboardFragments.renderFrictionPanel snap |> renderNode

      html |> Expect.stringContains "should still render the observed-friction section header" "Observed friction"
      html |> Expect.stringContains "zero signals should render the quiet line" "No observed friction yet"
      html |> Expect.stringContains "the rest of the panel should still render" "No local friction recorded yet"
  ]
