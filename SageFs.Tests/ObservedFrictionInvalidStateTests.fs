module SageFs.Tests.ObservedFrictionInvalidStateTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes
open SageFs.Tests.ObservedFrictionTypesTests

/// Brief B6 (observed-friction-plan.md §c). Exercises
/// `ObservedFriction.InvalidState.detect` directly against a synthetic
/// `PreparedStream` — the detector under test owns exactly this file plus
/// `ObservedFriction.InvalidState.fs`.
///
/// DEPENDENCY: this detector is fully testable now against synthetic
/// AffordanceMismatch events (below), but it fires on REAL data only once
/// Brief B9 records gate rejections (the `enforceToolCallGate` ->
/// `buildGateErrorResult` branch) into the friction store. B6 does not
/// block on B9.
let private cfg = DetectorConfig.defaults

let private detect events =
  SageFs.Features.ObservedFrictionInvalidState.detect cfg { Scope = SignalScope.DaemonWide; Events = events }

let private isInvalidState tool blocked =
  function
  | FrictionSignal.InvalidStateCall(t, b) -> ToolName.value t = tool && b = blocked
  | _ -> false

let private isAnyInvalidState =
  function
  | FrictionSignal.InvalidStateCall _ -> true
  | _ -> false

[<Tests>]
let tests =
  testList "Observed friction — InvalidStateCall (Brief B6)" [

    testCase "2x AffordanceMismatch on send_fsharp_code aggregates into InvalidStateCall(send_fsharp_code, 2)" <| fun _ ->
      let events =
        List.init 2 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.AffordanceMismatch) (float i))
      detect events
      |> List.exists (fun d -> d.Signal |> isInvalidState "send_fsharp_code" 2)
      |> Expect.isTrue "should detect exactly one InvalidStateCall(\"send_fsharp_code\", 2)"

    testCase "the detected signal carries Strong confidence and the stream's scope" <| fun _ ->
      let events =
        List.init 2 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.AffordanceMismatch) (float i))
      let signal = detect events |> List.find (fun d -> d.Signal |> isAnyInvalidState)
      signal.Confidence |> Expect.equal "an AffordanceMismatch event is structurally certain — the gate rejected the call" SignalConfidence.Strong
      signal.Scope |> Expect.equal "scope should carry through from the input stream" SignalScope.DaemonWide

    testCase "non-affordance blockers never produce an InvalidStateCall" <| fun _ ->
      let events =
        [ event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.SessionMissing) 0.0
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.SessionWarming) 1.0
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 2.0
          event "send_fsharp_code" FrictionOutcome.CompletedCleanly 3.0 ]
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyInvalidState)
      |> Expect.isFalse "only AffordanceMismatch blockers qualify as InvalidStateCall"

    testCase "two distinct tools' AffordanceMismatch events aggregate separately" <| fun _ ->
      let events =
        List.init 3 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.AffordanceMismatch) (float i))
        @ List.init 2 (fun i ->
          event "hard_reset_fsi_session" (FrictionOutcome.EncounteredBlocker BlockerKind.AffordanceMismatch) (10.0 + float i))
      let signals = detect events
      signals
      |> List.exists (fun d -> d.Signal |> isInvalidState "send_fsharp_code" 3)
      |> Expect.isTrue "the send_fsharp_code class should aggregate to 3, independent of the other tool"
      signals
      |> List.exists (fun d -> d.Signal |> isInvalidState "hard_reset_fsi_session" 2)
      |> Expect.isTrue "the hard_reset_fsi_session class should aggregate separately to 2"

    testCase "an empty stream yields no InvalidStateCall" <| fun _ ->
      detect [] |> Expect.isEmpty "no events, no signals"

    testProperty "a run of N AffordanceMismatch events on one tool always yields InvalidStateCall(_, N) for N in [1,50]"
    <| fun (PositiveInt raw) ->
      let n = (raw % 50) + 1
      let events =
        List.init n (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.AffordanceMismatch) (float i))
      detect events |> List.exists (fun d -> d.Signal |> isInvalidState "send_fsharp_code" n)
  ]
