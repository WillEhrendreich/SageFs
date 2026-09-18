module SageFs.Tests.CohortSpecTests

open Expecto
open Expecto.Flip
open SageFs.Simulation

/// Phase 1 DST — the EXHAUSTIVE specification proof for the cohort coordination
/// core. See SageFs.Simulation/CohortSpec.fs and sagefs-learn-from-cscheck.md.
///
/// Unlike the sampled CohortLandingSim, these tests PROVE the named rules by
/// enumerating the entire reachable state space of a bounded model (2 members,
/// 2 scopes, the full landing lifecycle with every outcome branch explored) and
/// checking every rule at every reachable state. A completed BFS with no
/// violation is a proof for that bound.
///
/// The payoff is twofold:
///   * All five rules (CLAIM-EXCLUSIVE, FENCE-MONOTONE, CONDUCTOR-BOUND,
///     NO-TERMINAL-IN-QUEUE, QUEUE-SERIAL) HOLD over the ENTIRE bounded space of
///     the real Cohort.decide — proven, not sampled.
///   * The FAULTS-mode twin (the pre-fix RebaseConflict queue-jam) is CAUGHT by
///     NO-TERMINAL-IN-QUEUE across the same space — proof the rule has teeth.

[<Tests>]
let tests =
  testList "DST cohort spec (exhaustive proof)" [

    testCase "the whole cohort core is PROVEN over the bounded model — every rule holds at every reachable state" <| fun _ ->
      let r = CohortSpec.proof ()
      // The BFS must actually close (empty frontier within the cap), or an empty
      // Violations list would be a sample, not a proof.
      r.Complete |> Expect.isTrue "the bounded state space must be fully enumerated (frontier emptied, not capped)"
      r.Capped |> Expect.isFalse "the cap must not be hit — raise it if the alphabet grew"
      // Non-vacuous: the model must reach a substantial space, not close trivially.
      Expect.isGreaterThan "the bounded model must reach a non-trivial state space" (r.Nodes, 1000)
      // The proof itself: no reachable state violates any rule.
      r.Violations |> Expect.equal "no reachable state violates any named rule" []

    testCase "FAULTS — the RebaseConflict-jam twin is caught by NO-TERMINAL-IN-QUEUE across the whole space" <| fun _ ->
      let r = CohortSpec.faults ()
      r.Complete |> Expect.isTrue "the twin's bounded space must also fully enumerate"
      r.Violations
      |> Expect.contains "the reintroduced queue-jam must violate the no-deadlock rule" "NO-TERMINAL-IN-QUEUE"

    testCase "the exploration is deterministic — the same bound yields the same node count" <| fun _ ->
      let a = CohortSpec.proof ()
      let b = CohortSpec.proof ()
      a.Nodes |> Expect.equal "re-running the exhaustive proof enumerates the identical space" b.Nodes
  ]
