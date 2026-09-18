module SageFs.Tests.ObservedFrictionUnattributedTests

open Expecto

/// OWNED BY BRIEF B3 (observed-friction-plan.md §c). This is a compiling
/// stub only — B0 registers the file (and the detector stub it tests,
/// ObservedFriction.Unattributed.fs) so B3 can flip these `ptestCase`s to
/// `testCase` and fill in the detector's RED tests:
///   - 4x "unknown (missing argument)" / InvalidRequest =>
///     UnattributedFailure("unknown (missing argument)", 4).
///   - A normally-named tool with InvalidRequest => no UnattributedFailure
///     (only the "unknown..." class qualifies).
///   - Two distinct unknown strings aggregate separately.
[<Tests>]
let tests =
  testList "Observed friction — UnattributedFailure (Brief B3, pending)" [
    ptestCase "PENDING (B3) — UnattributedFailure detector is unimplemented" <| fun _ ->
      failtest "ObservedFriction.Unattributed.detect is a stub (B0); implement in Brief B3"
  ]
