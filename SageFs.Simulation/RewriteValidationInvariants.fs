module SageFs.Simulation.RewriteValidationInvariants

/// WHY — the invariants are TWO-SIDED on purpose. "Never license an unsafe
/// rewrite" alone is satisfied by an implementation that always refuses, which
/// would make the feature inert while looking perfectly safe. Pairing it with
/// "must license an all-safe rewrite" means a validator that refuses everything
/// fails here too.

open SageFs
open SageFs.Simulation.RewriteValidationSim

type Violation =
  { Seed: int
    Why: string }

let private fail seed why = [ { Seed = seed; Why = why } ]

/// SAFETY — a rewrite whose sites are not ALL plain reads is never licensed.
let neverLicensesAnUnsafeRewrite (trace: Trace) : Violation list =
  if trace.Licensed && not trace.Scenario.AllPlainReads then
    fail
      trace.Scenario.Seed
      "a rewrite with an unsafe use site was licensed, which would silently change the program's meaning"
  else
    []

/// COMPLETENESS — an all-plain-read rewrite IS licensed, so the gate can pass.
let licensesASafeRewrite (trace: Trace) : Violation list =
  if trace.Scenario.AllPlainReads && not trace.Licensed then
    fail trace.Scenario.Seed "an all-safe rewrite refused, so the feature could never apply"
  else
    []

let all (trace: Trace) = neverLicensesAnUnsafeRewrite trace @ licensesASafeRewrite trace

/// TWIN — the first-site-only, unknown-is-a-pass validator must break the
/// safety invariant, or the battery proves nothing.
let firstSiteOnlyTwinBreaksTheRule (scenario: Scenario) : bool =
  let trace = runFirstSiteOnlyTwin scenario
  not (List.isEmpty (neverLicensesAnUnsafeRewrite trace))
