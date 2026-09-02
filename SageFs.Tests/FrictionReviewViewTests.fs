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
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry
open SageFs.Features.FrictionSanitize
open SageFs.Features.FrictionReviewView
open SageFs.Features.FrictionSqlite

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
      SageFsVersion = "" }
    { OccurredAtUtc = DateTimeOffset.UtcNow
      Session = session "session-1"
      Tool = tool "run_tests"
      Intent = IntentKind.ExploreCode
      Outcome = FrictionOutcome.AbandonedWithoutResolution
      Duration = duration 8
      FollowUp = FollowUp.SessionEnded
      ContextCost = ContextCost.Focused
      SageFsVersion = "" }
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
      let snap = build (sampleReport ()) (sampleSentReports ())
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
      let snap = build empty []
      snap.IsEmpty |> Expect.isTrue "no events and no feedback is empty"
      snap.EventCount |> Expect.equal "zero events" 0
      snap.SentReports |> Expect.isEmpty "no send history"

    testCase "withEdits re-derives outgoing and sanitizes the edited reason" <| fun _ ->
      let snap = build (sampleReport ()) []
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
  ]
