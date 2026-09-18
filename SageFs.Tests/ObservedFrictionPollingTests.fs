module SageFs.Tests.ObservedFrictionPollingTests

open Expecto

/// OWNED BY BRIEF B1 (observed-friction-plan.md §c). This is a compiling
/// stub only — B0 registers the file (and the detector stub it tests,
/// ObservedFriction.Polling.fs) so B1 can flip these `ptestCase`s to
/// `testCase` and fill in the detector's RED tests:
///   - 72x get_fsi_status with a handful of successful send_fsharp_code
///     interspersed => one ExcessivePolling(get_fsi_status, 72, _, _).
///   - False-positive guard (normal warmup): 5x get_fsi_status during a
///     warmup then a success => no signal (below PollingMinCalls).
///     Property: for any call count < PollingMinCalls, never fires.
///   - Property: a tool NOT in PollingTools never triggers ExcessivePolling
///     regardless of count.
///   - Window EventCount equals the number of polling calls counted.
[<Tests>]
let tests =
  testList "Observed friction — ExcessivePolling (Brief B1, pending)" [
    ptestCase "PENDING (B1) — ExcessivePolling detector is unimplemented" <| fun _ ->
      failtest "ObservedFriction.Polling.detect is a stub (B0); implement in Brief B1"
  ]
