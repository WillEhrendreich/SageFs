module SageFs.Tests.ObservedFrictionRepetitionTests

open Expecto

/// OWNED BY BRIEF B5 (observed-friction-plan.md §c). This is a compiling
/// stub only — B0 registers the file (and the detector stub it tests,
/// ObservedFriction.Repetition.fs) so B5 can flip these `ptestCase`s to
/// `testCase` and fill in the detector's RED tests:
///   - 3 consecutive OperationFailed => RepeatedSameError(OperationFailed, 3).
///   - 3 consecutive send_fsharp_code failures =>
///     RetryLoop(send_fsharp_code, 3).
///   - Alternating success/failure => no RepeatedSameError (run broken by
///     success — property: any CompletedCleanly resets the run counter).
///   - 2 consecutive failures (below threshold) => none.
/// v1 granularity is BlockerKind (Brief B9 adds a sanitized ErrorSignature
/// that upgrades this to same-*message* granularity — call that out, do
/// not implement it here).
[<Tests>]
let tests =
  testList "Observed friction — RepeatedSameError + RetryLoop (Brief B5, pending)" [
    ptestCase "PENDING (B5) — RepeatedSameError/RetryLoop detector is unimplemented" <| fun _ ->
      failtest "ObservedFriction.Repetition.detect is a stub (B0); implement in Brief B5"
  ]
