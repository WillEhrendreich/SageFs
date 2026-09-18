/// RepeatedSameError + RetryLoop detector — STUB, owned by Brief B5
/// (observed-friction-plan.md §c). Returns no signals until B5 implements
/// it. Registered as a no-op in `ObservedFriction.detectors`
/// (ObservedFriction.fs) so every Wave-1 brief stays file-disjoint: B5
/// edits ONLY this file (and its paired ObservedFrictionRepetitionTests.fs).
///
/// Target behavior (B5): RepeatedSameError(blocker, run) for a run of >=
/// RepeatedErrorRun consecutive events with the identical BlockerKind.
/// RetryLoop(tool, attempts) for >= RetryLoopAttempts consecutive
/// EncounteredBlocker events on the same tool. Both Confidence = Heuristic.
/// v1 granularity is BlockerKind (no error-signature field yet — Brief B9
/// adds a sanitized ErrorSignature that upgrades this to same-*message*
/// granularity).
module SageFs.Features.ObservedFrictionRepetition

open SageFs.Features.ObservedFrictionTypes

let detect : Detector = fun _ _ -> []
