module SageFs.Tests.EvalCancellationTests

open Expecto

/// Cancel semantics (idempotently served with/without an eval running)
/// previously required a real FSI-warmup Integration session to prove. It is
/// now the `cancel-idempotence` invariant in
/// SageFs.Simulation/EvalActorSim.fs + EvalActorInvariants.fs, folding the
/// REAL SageFs.EvalActorDecision.decide, and asserted in
/// SageFs.Tests/EvalActorSimTests.fs — proven in milliseconds. See
/// EvalActorSimTests.fs "doubleCancel: both cancels are served".
[<Tests>]
let evalCancellationTests = testList "Eval cancellation" []
