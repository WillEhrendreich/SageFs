module SageFs.Tests.ObservedFrictionRepetitionTests

open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes
open SageFs.Tests.ObservedFrictionTypesTests

/// Brief B5 (observed-friction-plan.md §c) — the RepeatedSameError +
/// RetryLoop detectors. All tests run the detector directly (not through
/// the full `detectors` registry) to stay independent of the other Wave-1
/// briefs' stubs, and build events on the shared `DaemonWide` scope since
/// every harvest event is stamped Session="mcp" pre-Brief-B9.
///
/// v1 granularity is BlockerKind only (no error-signature field yet):
/// two EncounteredBlocker events that classify to the same BlockerKind but
/// come from different underlying error messages are indistinguishable to
/// this detector and fold into one run. Brief B9 adds a sanitized
/// ErrorSignature that upgrades RepeatedSameError to same-*message*
/// granularity — not implemented here.
let private stream events : PreparedStream = { Scope = SignalScope.DaemonWide; Events = events }

let private detect events =
  SageFs.Features.ObservedFrictionRepetition.detect DetectorConfig.defaults (stream events)

let private isRepeatedSameError blocker run =
  function
  | FrictionSignal.RepeatedSameError(b, r) -> b = blocker && r = run
  | _ -> false

let private isAnyRepeatedSameError =
  function
  | FrictionSignal.RepeatedSameError _ -> true
  | _ -> false

let private isRetryLoop tool attempts =
  function
  | FrictionSignal.RetryLoop(t, a) -> ToolName.value t = tool && a = attempts
  | _ -> false

let private isAnyRetryLoop =
  function
  | FrictionSignal.RetryLoop _ -> true
  | _ -> false

[<Tests>]
let tests =
  testList "Observed friction — RepeatedSameError + RetryLoop (Brief B5)" [

    testCase "3 consecutive OperationFailed events yield RepeatedSameError(OperationFailed, 3)" <| fun _ ->
      let events =
        List.init 3 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) (float i))
      detect events
      |> List.exists (fun d -> d.Signal |> isRepeatedSameError BlockerKind.OperationFailed 3)
      |> Expect.isTrue "should detect exactly one RepeatedSameError(OperationFailed, 3)"

    testCase "3 consecutive send_fsharp_code failures yield RetryLoop(send_fsharp_code, 3)" <| fun _ ->
      let events =
        List.init 3 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) (float i))
      detect events
      |> List.exists (fun d -> d.Signal |> isRetryLoop "send_fsharp_code" 3)
      |> Expect.isTrue "should detect exactly one RetryLoop(send_fsharp_code, 3)"

    testCase "RetryLoop fires on the same tool regardless of which BlockerKind each attempt hit" <| fun _ ->
      let events = [
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 0.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.TransportFailure) 1.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 2.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isRetryLoop "send_fsharp_code" 3)
      |> Expect.isTrue "RetryLoop is keyed by tool, not by blocker kind — mixed kinds on one tool still count"

    testCase "alternating success/failure never yields RepeatedSameError — any CompletedCleanly resets the run" <| fun _ ->
      let events = [
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 0.0
        event "send_fsharp_code" FrictionOutcome.CompletedCleanly 1.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 2.0
        event "send_fsharp_code" FrictionOutcome.CompletedCleanly 3.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 4.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyRepeatedSameError)
      |> Expect.isFalse "each failure is isolated by a success in between — no run ever reaches the threshold"

    testCase "alternating success/failure never yields RetryLoop either" <| fun _ ->
      let events = [
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 0.0
        event "send_fsharp_code" FrictionOutcome.CompletedCleanly 1.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 2.0
        event "send_fsharp_code" FrictionOutcome.CompletedCleanly 3.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 4.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyRetryLoop)
      |> Expect.isFalse "a CompletedCleanly on the same tool also breaks a RetryLoop run"

    testCase "2 consecutive failures (below RepeatedErrorRun/RetryLoopAttempts) yield no signal" <| fun _ ->
      let events =
        List.init 2 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) (float i))
      let signals = detect events
      signals |> List.exists (fun d -> d.Signal |> isAnyRepeatedSameError) |> Expect.isFalse "below threshold — no RepeatedSameError"
      signals |> List.exists (fun d -> d.Signal |> isAnyRetryLoop) |> Expect.isFalse "below threshold — no RetryLoop"

    testCase "a different BlockerKind partway through breaks a RepeatedSameError run" <| fun _ ->
      let events = [
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 0.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 1.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.TransportFailure) 2.0
        event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) 3.0
      ]
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyRepeatedSameError)
      |> Expect.isFalse "only two OperationFailed run consecutively before the TransportFailure interrupts them"

    testCase "the detected signals carry Heuristic confidence and the stream's scope" <| fun _ ->
      let events =
        List.init 3 (fun i ->
          event "send_fsharp_code" (FrictionOutcome.EncounteredBlocker BlockerKind.OperationFailed) (float i))
      let signals = detect events
      signals |> Expect.isNonEmpty "expected at least one detected signal"
      for signal in signals do
        signal.Confidence |> Expect.equal "repetition signals are threshold-based, not structurally certain" SignalConfidence.Heuristic
        signal.Scope |> Expect.equal "scope should carry through from the input stream" SignalScope.DaemonWide

    testCase "the \"unknown ...\" unattributed sentinel class never forms a RetryLoop (cross-detector guard — that class is UnattributedFailure's job)" <| fun _ ->
      // The harvest pattern's arg-parse failures all stamp the tool name
      // "unknown (missing argument)". They are NOT a stable tool identity —
      // each may be a different real tool that failed to parse — so a run of
      // them must never aggregate into a RetryLoop (mirrors the same guard in
      // ObservedFriction.Resolution.fs's Abandonment; pinned by the
      // harvest-replay acceptance test once all detectors run together).
      let events =
        List.init 4 (fun i ->
          event "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
      detect events
      |> List.exists (fun d -> d.Signal |> isAnyRetryLoop)
      |> Expect.isFalse "the unattributed sentinel class must never form a RetryLoop"

    testCase "an empty stream yields no signals" <| fun _ ->
      detect [] |> Expect.isEmpty "no events, no signals"
  ]
