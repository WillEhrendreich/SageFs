module SageFs.Tests.ObservedFrictionAcceptanceTests

open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes
open SageFs.Features.ObservedFriction
open SageFs.Tests.ObservedFrictionTypesTests

/// The workstream's explicit definition of "it works"
/// (observed-friction-plan.md §e, "Explicit acceptance test"), replaying
/// the harvest's real pattern (observed-friction-harvest.md):
///   - 4x "unknown (missing argument)" / EncounteredBlocker InvalidRequest
///     (the arg-parse sentinel class, McpServer.fs:175-179).
///   - a 72x get_fsi_status burst, all CompletedCleanly.
///   - baseline create_session x8, diagnose x1 (no friction).
/// through `ObservedFriction.detectAll DetectorConfig.defaults` must
/// produce EXACTLY UnattributedFailure("unknown (missing argument)", 4)
/// and ExcessivePolling(get_fsi_status, 72, _, _), and must NOT emit
/// ResetThrash/RetryLoop/Abandonment (false-positive guard — the pattern
/// has no reset/retry/abandon events at all).
let private harvestReplayEvents : FrictionEvent list =
  let unattributed =
    List.init 4 (fun i ->
      event "unknown (missing argument)" (FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest) (float i))
  let polling =
    List.init 72 (fun i -> event "get_fsi_status" FrictionOutcome.CompletedCleanly (10.0 + float i))
  let baseline =
    List.init 8 (fun i -> event "create_session" FrictionOutcome.CompletedCleanly (200.0 + float i))
    @ [ event "diagnose" FrictionOutcome.CompletedCleanly 300.0 ]
  unattributed @ polling @ baseline

[<Tests>]
let tests =
  testList "Observed friction — harvest replay acceptance" [
    // PENDING until Brief B1 (ExcessivePolling) AND Brief B3
    // (UnattributedFailure) both land — both detector stubs return []
    // today (Brief B0), so this is RED-by-construction if run for real.
    // It is deliberately `ptestCase` (pending, excluded from the default
    // suite run) so B0 leaves the default suite green; B3 (the later of
    // the two dependencies in the Wave-1 leverage order) flips this
    // `ptestCase` -> `testCase` once both assertions hold against the
    // real detectors.
    testCase
      "harvest replay yields UnattributedFailure(4) + ExcessivePolling(72) and no false positives"
    <| fun _ ->
      let signals = detectAll DetectorConfig.defaults harvestReplayEvents

      signals
      |> List.exists (fun d ->
        match d.Signal with
        | FrictionSignal.UnattributedFailure("unknown (missing argument)", 4) -> true
        | _ -> false)
      |> Expect.isTrue "should detect the 4x unattributed arg-parse failure"

      signals
      |> List.exists (fun d ->
        match d.Signal with
        | FrictionSignal.ExcessivePolling(tool, 72, _, _) -> ToolName.value tool = "get_fsi_status"
        | _ -> false)
      |> Expect.isTrue "should detect the 72x get_fsi_status excessive-polling burst"

      signals
      |> List.exists (fun d ->
        match d.Signal with
        | FrictionSignal.ResetThrash _
        | FrictionSignal.RetryLoop _
        | FrictionSignal.Abandonment _ -> true
        | _ -> false)
      |> Expect.isFalse
        "should not false-positive ResetThrash/RetryLoop/Abandonment — the pattern has no reset/retry/abandon events"
  ]
