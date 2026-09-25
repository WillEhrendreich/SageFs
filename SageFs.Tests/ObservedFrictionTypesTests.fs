module SageFs.Tests.ObservedFrictionTypesTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes
open SageFs.Features.ObservedFriction

let private ok =
  function
  | Ok value -> value
  | Error err -> failwith err

/// A fixed reference instant so every detector test's "atUtc offset" reads
/// as a small, comparable number instead of `DateTimeOffset.UtcNow` noise.
let baseTimeUtc = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

/// Shared synthetic-event builder reused by every ObservedFriction detector
/// test file (Briefs B1-B6 consume this READ-ONLY; do not duplicate it —
/// this is the ONE shared test helper per the plan). `atUtcOffsetSeconds`
/// is relative to `baseTimeUtc` so streams can be built with simple,
/// readable, monotonic offsets.
let event (tool: string) (outcome: FrictionOutcome) (atUtcOffsetSeconds: float) : FrictionEvent =
  { OccurredAtUtc = baseTimeUtc.AddSeconds atUtcOffsetSeconds
    Session = SessionRef.create "mcp" |> ok
    Tool = ToolName.create tool |> ok
    Intent = IntentKind.ExploreCode
    Outcome = outcome
    Duration = DurationMs.create 1 |> ok
    FollowUp = FollowUp.NoFollowUpYet
    ContextCost = ContextCost.Focused
    SageFsVersion = ""; AgentKey = ""; ErrorSignature = "" }

let private sampleWindow = { FirstAtUtc = baseTimeUtc; LastAtUtc = baseTimeUtc; EventCount = 1 }
let private sampleTool = ToolName.create "send_fsharp_code" |> ok
let private sampleDuration = DurationMs.create 1 |> ok

/// The tool the ORIGINAL harvest burst was recorded against. That harvest
/// predates the `get_fsi_status` → `get_session_status` rename, so a test that
/// replays it must ask for the historical name explicitly rather than inherit
/// whatever the live default happens to be. Data, not a literal at each site.
let historicalPollingTool = SageFs.Affordances.RetiredTool.toToolName SageFs.Affordances.RetiredTool.GetFsiStatus

/// `DetectorConfig.defaults` with the polling watch set back to the historical
/// name, for tests that deliberately replay the recorded harvest. The live
/// default watches the current status tool; without this, a correct rename
/// silently disables the replay (and, worse, would make a real regression in
/// the current name invisible to these tests).
let harvestReplayConfig : DetectorConfig =
  { DetectorConfig.defaults with PollingTools = Set.ofList [ historicalPollingTool ] }

/// Golden set: every `FrictionSignal` case paired with its documented
/// stable id. A case rename or a new unmapped case breaks this test —
/// that is the point (repo convention: no magic strings anywhere).
let private goldenIds : (FrictionSignal * string) list = [
  FrictionSignal.ExcessivePolling(sampleTool, 1, 1, sampleWindow), "observed.excessive-polling"
  FrictionSignal.ResetThrash(1, sampleWindow), "observed.reset-thrash"
  FrictionSignal.HardResetAfterCreate sampleDuration, "observed.hard-reset-after-create"
  FrictionSignal.UnattributedFailure("unknown (missing argument)", 1), "observed.unattributed-failure"
  FrictionSignal.InvalidStateCall(sampleTool, 1), "observed.invalid-state-call"
  FrictionSignal.RepeatedSameError(BlockerKind.OperationFailed, 3), "observed.repeated-same-error"
  FrictionSignal.RetryLoop(sampleTool, 3), "observed.retry-loop"
  FrictionSignal.Abandonment(sampleTool, BlockerKind.OperationFailed), "observed.abandonment"
  FrictionSignal.SlowTimeToFirstSuccess(sampleDuration, 1), "observed.slow-first-success"
]

[<Tests>]
let tests =
  testList "Observed friction — foundation types" [
    testCase "every FrictionSignal case maps to its documented stable id" <| fun _ ->
      for signal, expected in goldenIds do
        signal |> FrictionSignal.id |> Expect.equal "stable id should match the golden set" expected

    testCase "stable ids are pairwise distinct because a collision would blend two signals into one" <| fun _ ->
      let ids = goldenIds |> List.map snd
      ids |> List.distinct |> Expect.equal "no two signal cases should share an id" ids

    testProperty "prepare preserves order for a uniform-session stream" <| fun (offsets: int list) ->
      let events =
        offsets
        |> List.truncate 200
        |> List.mapi (fun i offset -> event "send_fsharp_code" FrictionOutcome.CompletedCleanly (float (i + offset)))
      prepare events |> List.collect (fun s -> s.Events) = events

    testCase "prepare groups an all-\"mcp\" stream into exactly one DaemonWide scope" <| fun _ ->
      let events = [
        event "create_session" FrictionOutcome.CompletedCleanly 0.0
        event "get_fsi_status" FrictionOutcome.CompletedCleanly 1.0
        event "diagnose" FrictionOutcome.CompletedCleanly 2.0
      ]
      let streams = prepare events
      streams |> List.length |> Expect.equal "a uniform-session stream should yield exactly one PreparedStream" 1
      streams.[0].Scope |> Expect.equal "the sentinel \"mcp\" session should map to DaemonWide" SignalScope.DaemonWide
      streams.[0].Events |> Expect.equal "the DaemonWide stream should carry every event, in order" events

    testCase "detectAll on an empty event log yields no signals" <| fun _ ->
      detectAll DetectorConfig.defaults [] |> Expect.equal "an empty stream must never produce phantom signals" []
  ]
