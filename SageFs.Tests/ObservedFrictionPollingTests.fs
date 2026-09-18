module SageFs.Tests.ObservedFrictionPollingTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes
open SageFs.Features.ObservedFriction
open SageFs.Tests.ObservedFrictionTypesTests

/// Brief B1 (observed-friction-plan.md §c) — the ExcessivePolling detector,
/// the highest-leverage signal (the harvest's live 72x get_fsi_status
/// burst). All tests run the detector directly (not through the full
/// `detectors` registry) to stay independent of the other Wave-1 briefs'
/// stubs, and build events on the shared `DaemonWide` scope since every
/// harvest event is stamped Session="mcp" pre-Brief-B9.
let private stream events : PreparedStream = { Scope = SignalScope.DaemonWide; Events = events }

let private detect events =
  SageFs.Features.ObservedFrictionPolling.detect DetectorConfig.defaults (stream events)

[<Tests>]
let tests =
  testList "Observed friction — ExcessivePolling (Brief B1)" [

    testCase
      "72x get_fsi_status with a handful of successful send_fsharp_code interspersed yields one ExcessivePolling(get_fsi_status, 72, _, _)"
    <| fun _ ->
      let polling = List.init 72 (fun i -> event "get_fsi_status" FrictionOutcome.CompletedCleanly (float i))
      let progress =
        List.init 3 (fun i -> event "send_fsharp_code" FrictionOutcome.CompletedCleanly (200.0 + float i))
      let signals = detect (polling @ progress)

      signals
      |> List.exists (fun d ->
        match d.Signal with
        | FrictionSignal.ExcessivePolling(tool, 72, _, _) -> ToolName.value tool = "get_fsi_status"
        | _ -> false)
      |> Expect.isTrue "should detect the 72x get_fsi_status excessive-polling burst"

    testCase
      "5x get_fsi_status during a normal warmup then a success yields no signal (below PollingMinCalls)"
    <| fun _ ->
      let events =
        List.init 5 (fun i -> event "get_fsi_status" FrictionOutcome.CompletedCleanly (float i))
        @ [ event "send_fsharp_code" FrictionOutcome.CompletedCleanly 10.0 ]

      detect events |> Expect.isEmpty "a small warmup poll burst below PollingMinCalls should never fire"

    testProperty "never fires for any call count below PollingMinCalls, regardless of the successes ratio"
    <| fun (NonNegativeInt rawCount) ->
      let cfg = DetectorConfig.defaults
      let callCount = rawCount % cfg.PollingMinCalls // 0 .. PollingMinCalls - 1
      let events = List.init callCount (fun i -> event "get_fsi_status" FrictionOutcome.CompletedCleanly (float i))
      SageFs.Features.ObservedFrictionPolling.detect cfg (stream events) |> List.isEmpty

    testProperty "a tool not in PollingTools never triggers ExcessivePolling regardless of call count"
    <| fun (PositiveInt rawCount) ->
      let calls = (rawCount % 500) + 1
      let events =
        List.init calls (fun i -> event "some_other_tool" FrictionOutcome.CompletedCleanly (float i))

      detect events |> List.isEmpty

    testCase "window EventCount equals the number of polling calls counted" <| fun _ ->
      let polling = List.init 25 (fun i -> event "get_fsi_status" FrictionOutcome.CompletedCleanly (float i))

      match detect polling with
      | [ { Signal = FrictionSignal.ExcessivePolling(_, calls, _, window) } ] ->
        window.EventCount |> Expect.equal "window EventCount should equal the counted polling calls" calls
      | other -> failtestf "expected exactly one ExcessivePolling signal, got %A" other
  ]
