module SageFs.Tests.ObservedFrictionResolutionTests

open Expecto

/// OWNED BY BRIEF B4 (observed-friction-plan.md §c). This is a compiling
/// stub only — B0 registers the file (and the detector stub it tests,
/// ObservedFriction.Resolution.fs) so B4 can flip these `ptestCase`s to
/// `testCase` and fill in the detector's RED tests:
///   - Error on targeted_verify, never resolved, followed by other tools
///     => Abandonment.
///   - Error then a later success on the SAME tool => no Abandonment
///     (resolution guard).
///   - create -> 60s of failed evals -> first success =>
///     SlowTimeToFirstSuccess(_, n); create -> success in 2s => none.
///   - False-positive guard: a stream that ends on a failure that is the
///     LAST event overall (agent may still be working) => no Abandonment
///     (needs a later different-tool event).
[<Tests>]
let tests =
  testList "Observed friction — Abandonment + SlowTimeToFirstSuccess (Brief B4, pending)" [
    ptestCase "PENDING (B4) — Abandonment/SlowTimeToFirstSuccess detector is unimplemented" <| fun _ ->
      failtest "ObservedFriction.Resolution.detect is a stub (B0); implement in Brief B4"
  ]
