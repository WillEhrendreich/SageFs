module SageFs.Tests.ObservedFrictionInvalidStateTests

open Expecto

/// OWNED BY BRIEF B6 (observed-friction-plan.md §c). This is a compiling
/// stub only — B0 registers the file (and the detector stub it tests,
/// ObservedFriction.InvalidState.fs) so B6 can flip these `ptestCase`s to
/// `testCase` and fill in the detector's RED tests:
///   - 2 AffordanceMismatch events on send_fsharp_code =>
///     InvalidStateCall(send_fsharp_code, 2).
///   - Non-affordance blockers => no InvalidStateCall.
/// The pure detector is fully testable now against synthetic
/// AffordanceMismatch events; it fires on REAL data only after Brief B9
/// records gate rejections into the friction store (dependency noted in
/// the brief, not a blocker for B6's own tests).
[<Tests>]
let tests =
  testList "Observed friction — InvalidStateCall (Brief B6, pending)" [
    ptestCase "PENDING (B6) — InvalidStateCall detector is unimplemented" <| fun _ ->
      failtest "ObservedFriction.InvalidState.detect is a stub (B0); implement in Brief B6"
  ]
