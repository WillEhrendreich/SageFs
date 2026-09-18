module SageFs.Tests.ObservedFrictionResetThrashTests

open Expecto

/// OWNED BY BRIEF B2 (observed-friction-plan.md §c). This is a compiling
/// stub only — B0 registers the file (and the detector stub it tests,
/// ObservedFriction.ResetThrash.fs) so B2 can flip these `ptestCase`s to
/// `testCase` and fill in the detector's RED tests:
///   - 3 resets within 60s => ResetThrash(3, _).
///   - 2 resets spread over 5 min => no signal (window guard). Property:
///     resets outside the window never aggregate.
///   - create_session then hard_reset_fsi_session 10s later =>
///     HardResetAfterCreate; 5 min later => none.
///   - False-positive guard: a single deliberate hard_reset with
///     rebuild=true far from create => no signal.
[<Tests>]
let tests =
  testList "Observed friction — ResetThrash + HardResetAfterCreate (Brief B2, pending)" [
    ptestCase "PENDING (B2) — ResetThrash/HardResetAfterCreate detector is unimplemented" <| fun _ ->
      failtest "ObservedFriction.ResetThrash.detect is a stub (B0); implement in Brief B2"
  ]
