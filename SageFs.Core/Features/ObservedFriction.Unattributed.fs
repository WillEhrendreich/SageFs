/// UnattributedFailure detector — STUB, owned by Brief B3 (observed-
/// friction-plan.md §c). Returns no signals until B3 implements it.
/// Registered as a no-op in `ObservedFriction.detectors`
/// (ObservedFriction.fs) so every Wave-1 brief stays file-disjoint: B3
/// edits ONLY this file (and its paired ObservedFrictionUnattributedTests.fs).
///
/// Target behavior (B3): emit UnattributedFailure(rawTool, count) for
/// every event whose Tool name starts with "unknown" (the
/// McpServer.fs:175-179 sentinel class) and whose outcome is
/// EncounteredBlocker InvalidRequest, aggregated by raw name. Confidence =
/// Strong (structurally certain — the tool literally could not be named).
/// This names the harvest's one real observed blocker: 4x
/// "unknown (missing argument)".
module SageFs.Features.ObservedFrictionUnattributed

open SageFs.Features.ObservedFrictionTypes

let detect : Detector = fun _ _ -> []
