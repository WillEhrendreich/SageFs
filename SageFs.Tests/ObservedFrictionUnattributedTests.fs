module SageFs.Tests.ObservedFrictionUnattributedTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes
open SageFs.Tests.ObservedFrictionTypesTests

/// Brief B3 (observed-friction-plan.md §c). Exercises
/// `ObservedFriction.Unattributed.detect` directly against a synthetic
/// `PreparedStream` — the detector under test owns exactly this file plus
/// `ObservedFriction.Unattributed.fs`.
let private cfg = DetectorConfig.defaults

let private detect events =
  SageFs.Features.ObservedFrictionUnattributed.detect cfg { Scope = SignalScope.DaemonWide; Events = events }

let private isUnattributed rawTool count =
  function
  | FrictionSignal.UnattributedFailure(t, c) -> t = rawTool && c = count
  | _ -> false

let private isAnyUnattributed =
  function
  | FrictionSignal.UnattributedFailure _ -> true
  | _ -> false

[<Tests>]
let tests =
  testList "Observed friction — UnattributedFailure (Brief B3)" [

    testCase "4x \"unknown (missing argument)\" / InvalidRequest aggregates into UnattributedFailure(_, 4)" <| fun _ ->
      let events =
        List.init 4 (fun i ->
          event "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
      detect events
      |> List.exists (fun d -> d.Signal |> isUnattributed "unknown (missing argument)" 4)
      |> Expect.isTrue "should detect exactly one UnattributedFailure(\"unknown (missing argument)\", 4)"

    testCase "the detected signal carries Strong confidence and DaemonWide scope" <| fun _ ->
      let events =
        List.init 4 (fun i ->
          event "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
      let signal = detect events |> List.find (fun d -> d.Signal |> isAnyUnattributed)
      signal.Confidence |> Expect.equal "the tool literally could not be named — this is structurally certain" SignalConfidence.Strong
      signal.Scope |> Expect.equal "scope should carry through from the input stream" SignalScope.DaemonWide

    testCase "a normally-named tool with InvalidRequest is never UnattributedFailure" <| fun _ ->
      let events =
        List.init 4 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyUnattributed)
      |> Expect.isFalse "only the \"unknown...\" raw-tool class qualifies as unattributed"

    testCase "two distinct unknown strings aggregate separately" <| fun _ ->
      let events =
        List.init 3 (fun i ->
          event "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
        @ List.init 2 (fun i -> event "unknown" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (10.0 + float i))
      let signals = detect events
      signals
      |> List.exists (fun d -> d.Signal |> isUnattributed "unknown (missing argument)" 3)
      |> Expect.isTrue "the \"unknown (missing argument)\" class should aggregate to 3, independent of the other class"
      signals
      |> List.exists (fun d -> d.Signal |> isUnattributed "unknown" 2)
      |> Expect.isTrue "the bare \"unknown\" class should aggregate separately to 2"

    testCase "an \"unknown\"-named tool that completed cleanly is not a friction signal at all" <| fun _ ->
      detect [ event "unknown" FrictionOutcome.CompletedCleanly 0.0 ]
      |> List.exists (fun d -> d.Signal |> isAnyUnattributed)
      |> Expect.isFalse "a clean outcome is never friction, regardless of the tool name"

    testCase "a different BlockerKind on an \"unknown\"-named tool is not UnattributedFailure" <| fun _ ->
      detect [ event "unknown" (FrictionOutcome.EncounteredBlocker BlockerKind.SessionMissing) 0.0 ]
      |> List.exists (fun d -> d.Signal |> isAnyUnattributed)
      |> Expect.isFalse "only InvalidRequest blockers qualify — a different blocker is a different signal's job"

    testCase "an empty stream yields no UnattributedFailure" <| fun _ ->
      detect [] |> Expect.isEmpty "no events, no signals"

    testProperty "a run of N \"unknown (missing argument)\" / InvalidRequest events always yields UnattributedFailure(_, N) for N in [1,50]"
    <| fun (PositiveInt raw) ->
      let n = (raw % 50) + 1
      let events =
        List.init n (fun i ->
          event "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
      detect events |> List.exists (fun d -> d.Signal |> isUnattributed "unknown (missing argument)" n)
  ]
