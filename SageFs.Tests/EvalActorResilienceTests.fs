module SageFs.Tests.EvalActorResilienceTests

open Expecto

/// "The eval actor survives a handler exception and keeps processing
/// commands" previously required a real FSI-warmup Integration session plus
/// the `evalActorFaultInjector` test-only seam to prove. It is now the
/// `loop-survival` invariant in SageFs.Simulation/EvalActorSim.fs +
/// EvalActorInvariants.fs, folding the REAL
/// SageFs.EvalActorDecision.decide through a guarded fold that
/// mirrors ResilientActor.wrapLoop exactly (catch, keep previous state,
/// continue), with an unwrapped twin proving the invariant has teeth (it
/// dies at the poison; the guarded/real reducer does not). Asserted in
/// SageFs.Tests/EvalActorSimTests.fs — proven in milliseconds. See
/// EvalActorSimTests.fs "poisonMidScenario: every op after the poison still
/// runs (guarded)" and "REPRODUCED — unwrapped twin dies on the poison".
[<Tests>]
let evalActorResilienceTests = testList "Eval actor resilience" []
