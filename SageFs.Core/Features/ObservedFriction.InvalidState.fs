/// InvalidStateCall detector — STUB, owned by Brief B6 (observed-friction-
/// plan.md §c). Returns no signals until B6 implements it. Registered as a
/// no-op in `ObservedFriction.detectors` (ObservedFriction.fs) so every
/// Wave-1 brief stays file-disjoint: B6 edits ONLY this file (and its
/// paired ObservedFrictionInvalidStateTests.fs).
///
/// Target behavior (B6): emit InvalidStateCall(tool, blocked) aggregating
/// EncounteredBlocker AffordanceMismatch events per tool. Confidence =
/// Strong. The pure detector is fully testable now against synthetic
/// AffordanceMismatch events; it fires on REAL data only after Brief B9
/// records gate rejections into the friction store (dependency noted, not
/// blocking).
module SageFs.Features.ObservedFrictionInvalidState

open SageFs.Features.ObservedFrictionTypes

let detect : Detector = fun _ _ -> []
