module SageFs.Tests.ObservedFrictionResolutionTests

open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes
open SageFs.Tests.ObservedFrictionTypesTests

/// Brief B4 (observed-friction-plan.md §c). Exercises
/// `ObservedFriction.Resolution.detect` directly against a synthetic
/// `PreparedStream` — the detector under test owns exactly this file plus
/// `ObservedFriction.Resolution.fs`.
let private cfg = DetectorConfig.defaults

let private detect events =
  SageFs.Features.ObservedFrictionResolution.detect cfg { Scope = SignalScope.DaemonWide; Events = events }

let private isAbandonment tool blocker =
  function
  | FrictionSignal.Abandonment(t, b) -> ToolName.value t = tool && b = blocker
  | _ -> false

let private isAnyAbandonment =
  function
  | FrictionSignal.Abandonment _ -> true
  | _ -> false

let private isSlowFirstSuccess =
  function
  | FrictionSignal.SlowTimeToFirstSuccess _ -> true
  | _ -> false

[<Tests>]
let tests =
  testList "Observed friction — Abandonment + SlowTimeToFirstSuccess (Brief B4)" [

    testCase "an error on targeted_verify, never resolved, followed by other tools yields Abandonment" <| fun _ ->
      let events = [
        event "targeted_verify" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 0.0
        event "send_fsharp_code" FrictionOutcome.CompletedCleanly 1.0
        event "get_fsi_status" FrictionOutcome.CompletedCleanly 2.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isAbandonment "targeted_verify" BlockerKind.OperationFailed)
      |> Expect.isTrue "the last targeted_verify call was a blocker, and the agent moved on to other tools"

    testCase "an error then a later success on the SAME tool is not Abandonment (resolution guard)" <| fun _ ->
      let events = [
        event "targeted_verify" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 0.0
        event "targeted_verify" FrictionOutcome.CompletedCleanly 1.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyAbandonment)
      |> Expect.isFalse "the tool's LAST occurrence is a success, so the earlier blocker was resolved"

    testCase "a stream that ends on the failure (no later event at all) is not Abandonment" <| fun _ ->
      let events = [
        event "create_session" FrictionOutcome.CompletedCleanly 0.0
        event "targeted_verify" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 1.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyAbandonment)
      |> Expect.isFalse "the agent may still be working — abandonment needs a later, different-tool event"

    testCase "create -> 60s of failed evals -> first success yields SlowTimeToFirstSuccess" <| fun _ ->
      let failures =
        List.init 5 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) (10.0 + float i * 10.0))
      let events =
        [ event "create_session" FrictionOutcome.CompletedCleanly 0.0 ]
        @ failures
        @ [ event "send_fsharp_code" FrictionOutcome.CompletedCleanly 60.0 ]
      match detect events |> List.filter (fun d -> d.Signal |> isSlowFirstSuccess) with
      | [ { Signal = FrictionSignal.SlowTimeToFirstSuccess(elapsed, failedBefore) } ] ->
        elapsed |> DurationMs.value |> Expect.equal "elapsed should be the 60s gap in milliseconds" 60_000
        failedBefore |> Expect.equal "should count the 5 failed evals between create and first success" 5
      | other -> failtestf "expected exactly one SlowTimeToFirstSuccess signal, got %A" other

    testCase "create -> success in 2s yields no SlowTimeToFirstSuccess (under the warmup budget)" <| fun _ ->
      let events = [
        event "create_session" FrictionOutcome.CompletedCleanly 0.0
        event "send_fsharp_code" FrictionOutcome.CompletedCleanly 2.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isSlowFirstSuccess)
      |> Expect.isFalse "2s is well under the default 45s SlowFirstSuccess budget"

    testCase "no create_session event at all yields no SlowTimeToFirstSuccess" <| fun _ ->
      let events = [ event "send_fsharp_code" FrictionOutcome.CompletedCleanly 0.0 ]
      detect events
      |> List.exists (fun d -> d.Signal |> isSlowFirstSuccess)
      |> Expect.isFalse "there is no anchor to measure a gap from"

    testCase
      "the \"unknown ...\" unattributed sentinel class never counts as an Abandonment tool"
    <| fun _ ->
      let events = [
        event "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) 0.0
        event "get_fsi_status" FrictionOutcome.CompletedCleanly 1.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyAbandonment)
      |> Expect.isFalse "the sentinel class is UnattributedFailure's job, not a real tool identity to abandon"

    testCase "detected signals carry Heuristic confidence and the stream's scope" <| fun _ ->
      let events = [
        event "targeted_verify" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 0.0
        event "send_fsharp_code" FrictionOutcome.CompletedCleanly 1.0
      ]
      let signal = detect events |> List.find (fun d -> d.Signal |> isAnyAbandonment)
      signal.Confidence |> Expect.equal "abandonment is threshold-based, never structurally certain" SignalConfidence.Heuristic
      signal.Scope |> Expect.equal "scope should carry through from the input stream" SignalScope.DaemonWide

    testCase "an empty stream yields no signals at all" <| fun _ ->
      detect [] |> Expect.isEmpty "no events, no signals"
  ]
