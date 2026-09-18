module SageFs.Tests.ObservedFrictionResetThrashTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes
open SageFs.Features.ObservedFriction
open SageFs.Tests.ObservedFrictionTypesTests

/// Brief B2 (observed-friction-plan.md §c) — ResetThrash + HardResetAfterCreate.
/// All tests run the detector directly (not through the full `detectors`
/// registry) to stay independent of the other Wave-1 briefs' stubs, and
/// build events on the shared `DaemonWide` scope since every harvest event
/// is stamped Session="mcp" pre-Brief-B9.
let private stream events : PreparedStream = { Scope = SignalScope.DaemonWide; Events = events }

let private detect events =
  SageFs.Features.ObservedFrictionResetThrash.detect DetectorConfig.defaults (stream events)

let private resetThrashSignals signals =
  signals
  |> List.choose (fun d ->
    match d.Signal with
    | FrictionSignal.ResetThrash(resets, window) -> Some(resets, window)
    | _ -> None)

let private hardResetAfterCreateSignals signals =
  signals
  |> List.choose (fun d ->
    match d.Signal with
    | FrictionSignal.HardResetAfterCreate gap -> Some gap
    | _ -> None)

[<Tests>]
let tests =
  testList "Observed friction — ResetThrash + HardResetAfterCreate (Brief B2)" [

    testCase "3 resets within 60s yields ResetThrash(3, _)" <| fun _ ->
      let events = [
        event "reset_fsi_session" FrictionOutcome.CompletedCleanly 0.0
        event "reset_fsi_session" FrictionOutcome.CompletedCleanly 10.0
        event "hard_reset_fsi_session" FrictionOutcome.CompletedCleanly 20.0
      ]

      match resetThrashSignals (detect events) with
      | [ (resets, window) ] ->
        resets |> Expect.equal "should count all 3 resets" 3
        window.EventCount |> Expect.equal "window EventCount should match the resets count" 3
      | other -> failtestf "expected exactly one ResetThrash signal, got %A" other

    testCase "2 resets spread over 5 minutes yields no ResetThrash (below threshold count)" <| fun _ ->
      let events = [
        event "reset_fsi_session" FrictionOutcome.CompletedCleanly 0.0
        event "reset_fsi_session" FrictionOutcome.CompletedCleanly 300.0
      ]

      detect events |> resetThrashSignals |> Expect.isEmpty "2 resets, even far apart, are below ResetThrashCount"

    testProperty "resets more than ResetThrashWindow apart never aggregate into a ResetThrash, however many there are"
    <| fun (PositiveInt rawCount) ->
      let cfg = DetectorConfig.defaults
      let count = (rawCount % 10) + 1
      let gapSeconds = cfg.ResetThrashWindow.TotalSeconds + 1.0
      let events =
        List.init count (fun i -> event "reset_fsi_session" FrictionOutcome.CompletedCleanly (float i * gapSeconds))

      SageFs.Features.ObservedFrictionResetThrash.detect cfg (stream events)
      |> resetThrashSignals
      |> List.isEmpty

    testCase "create_session then hard_reset_fsi_session 10s later yields HardResetAfterCreate" <| fun _ ->
      let events = [
        event "create_session" FrictionOutcome.CompletedCleanly 0.0
        event "hard_reset_fsi_session" FrictionOutcome.CompletedCleanly 10.0
      ]

      match hardResetAfterCreateSignals (detect events) with
      | [ gap ] -> DurationMs.value gap |> Expect.equal "gap should be 10s in ms" 10_000
      | other -> failtestf "expected exactly one HardResetAfterCreate signal, got %A" other

    testCase "create_session then hard_reset_fsi_session 5 minutes later yields no HardResetAfterCreate" <| fun _ ->
      let events = [
        event "create_session" FrictionOutcome.CompletedCleanly 0.0
        event "hard_reset_fsi_session" FrictionOutcome.CompletedCleanly 300.0
      ]

      detect events
      |> hardResetAfterCreateSignals
      |> Expect.isEmpty "a hard reset 5 minutes after create should be outside HardResetAfterCreateWindow"

    testCase
      "a single deliberate hard_reset (e.g. rebuild=true) far from create_session yields no signal at all"
    <| fun _ ->
      let events = [
        event "create_session" FrictionOutcome.CompletedCleanly 0.0
        event "hard_reset_fsi_session" FrictionOutcome.CompletedCleanly 600.0
      ]

      detect events |> Expect.isEmpty "a single well-spaced hard reset must never be flagged as friction"
  ]
