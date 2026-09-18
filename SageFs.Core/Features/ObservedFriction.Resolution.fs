/// Abandonment + SlowTimeToFirstSuccess detector — STUB, owned by Brief B4
/// (observed-friction-plan.md §c). Returns no signals until B4 implements
/// it. Registered as a no-op in `ObservedFriction.detectors`
/// (ObservedFriction.fs) so every Wave-1 brief stays file-disjoint: B4
/// edits ONLY this file (and its paired ObservedFrictionResolutionTests.fs).
///
/// Target behavior (B4): Abandonment(tool, lastBlocker) when a tool's last
/// event in the stream is EncounteredBlocker with no later CompletedCleanly
/// for that tool AND the stream continues with other tools (agent moved
/// on). SlowTimeToFirstSuccess(elapsed, failedBefore) when the gap from the
/// first create_session to the first CompletedCleanly exceeds
/// SlowFirstSuccess, counting failures in between. Both Confidence =
/// Heuristic.
module SageFs.Features.ObservedFrictionResolution

open SageFs.Features.ObservedFrictionTypes

let detect : Detector = fun _ _ -> []
