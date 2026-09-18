/// ResetThrash + HardResetAfterCreate detector — STUB, owned by Brief B2
/// (observed-friction-plan.md §c). Returns no signals until B2 implements
/// it. Registered as a no-op in `ObservedFriction.detectors`
/// (ObservedFriction.fs) so every Wave-1 brief stays file-disjoint: B2
/// edits ONLY this file (and its paired ObservedFrictionResetThrashTests.fs).
///
/// Target behavior (B2): ResetThrash(resets, window) when >=
/// ResetThrashCount of reset_fsi_session/hard_reset_fsi_session occur
/// within ResetThrashWindow; HardResetAfterCreate(gap) when a
/// hard_reset_fsi_session follows a create_session within
/// HardResetAfterCreateWindow. Both Confidence = Heuristic.
module SageFs.Features.ObservedFrictionResetThrash

open SageFs.Features.ObservedFrictionTypes

let detect : Detector = fun _ _ -> []
