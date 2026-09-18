/// ExcessivePolling detector — STUB, owned by Brief B1 (observed-friction-
/// plan.md §c). Returns no signals until B1 implements it. Registered as a
/// no-op in `ObservedFriction.detectors` (ObservedFriction.fs) so every
/// Wave-1 brief stays file-disjoint: B1 edits ONLY this file (and its
/// paired ObservedFrictionPollingTests.fs).
///
/// Target behavior (B1): signal when a stream has >= PollingMinCalls calls
/// to a PollingTools member AND the calls:successes ratio >=
/// PollingMinCallsPerSuccess (poll-without-progress), emitting
/// ExcessivePolling(tool, calls, successes, window) with Confidence =
/// Heuristic. The harvest's live example: 72x get_fsi_status in one run.
module SageFs.Features.ObservedFrictionPolling

open SageFs.Features.ObservedFrictionTypes

let detect : Detector = fun _ _ -> []
