module SageFs.Simulation.HolderRewriteInvariants

/// WHY — this is a source-rewrite gate, so the failure that matters is a
/// REWRITE of a binding with an unsafe fact: SageFs would silently change the
/// meaning of a user's program. The invariant is therefore one-sided in
/// spirit, and the battery folds the full cross-product of conditions.

open SageFs
open SageFs.Simulation.HolderRewriteSim

type Violation =
  { Seed: int
    Trace: Trace
    Why: string }

let private fail seed trace why : Violation list = [ { Seed = seed; Trace = trace; Why = why } ]

/// SAFETY — no unsafe binding is ever rewritten.
let neverRewritesAnUnsafeBinding (trace: Trace) : Violation list =
  if trace.Rewritten && trace.Unsafe then
    fail trace.Scenario.Seed trace "an unsafe binding was rewritten, which would silently change the program's meaning"
  else
    []

/// COMPLETENESS — a fully safe binding IS rewritten. Without this, "always
/// refuse" would pass the safety invariant while making the feature inert.
let safeBindingsAreRewritten (trace: Trace) : Violation list =
  if not trace.Unsafe && not trace.Rewritten then
    fail trace.Scenario.Seed trace "a fully safe binding refused, so the rewrite could never actually apply"
  else
    []

let all (trace: Trace) : Violation list =
  neverRewritesAnUnsafeBinding trace @ safeBindingsAreRewritten trace

/// TWIN — a partial check must rewrite an unsafe binding, proving the
/// invariant is not vacuous.
let partialCheckBreaksTheRule (scenario: Scenario) : bool =
  let trace = runPartialCheckTwin scenario
  not (List.isEmpty (neverRewritesAnUnsafeBinding trace))
