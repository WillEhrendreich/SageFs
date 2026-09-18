module SageFs.Tests.ActorSplitTests

open Expecto

/// Query-liveness (GetSessionPhase/Autocomplete/GetDiagnostics responding
/// during a long eval) previously required a real FSI-warmup Integration
/// session (~15-30s) to prove. It is now the `query-liveness` invariant in
/// SageFs.Simulation/EvalActorSim.fs + EvalActorInvariants.fs, folding the
/// REAL SageFs.EvalActorDecision.decide, and asserted in
/// SageFs.Tests/EvalActorSimTests.fs — proven in milliseconds, with a
/// single-actor twin proving the invariant has teeth (it fires the twin,
/// holds the real reducer). See EvalActorSimTests.fs "queryDuringEval" and
/// "REPRODUCED — single-actor twin blocks a Query mid-eval".
[<Tests>]
let actorSplitTests = testList "Actor split" []
