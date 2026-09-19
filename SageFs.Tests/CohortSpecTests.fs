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
/// HONESTY NOTE (armfix, cmd-handoff.md item B — read `CohortSpec.fs`'s own
/// COVERAGE comment before repeating either claim below): the bound covers 13
/// of `CohortCommand`'s 20 cases — REACHING, and proving safe, the arms that
/// the previous alphabet could not reach at all (`VetoLanding`, `ResolveVeto`,
/// `SetIntegrationHead`'s HeadMoved race, `ReleaseClaim`'s StaleClaimFence
/// race, one `FastForwardFailed` retry cycle). It does NOT cover `Depart`,
/// `RenewLease`, `Tick`, `ReassignClaim`, `DelegateConductor`, `ObserveSave`,
/// `WithdrawLanding`, or FastForwardFailed's full retry-exhaustion cascade
/// (verified separately by `CohortFastForwardFailedTests.fs`, not this BFS).
/// Say "proven over the modeled subspace" — naming what's modeled — never
/// "the whole cohort core is proven": that phrase describes 8/20 commands'
/// worth of coverage, not 20/20, and was itself the roasts' central finding
/// (roast-2day §1, roast-2day-cmd §1: a proof whose alphabet excludes the
/// counterexamples is proof of the wrong thing).
///
/// The payoff is twofold:
///   * All five rules (CLAIM-EXCLUSIVE, FENCE-MONOTONE, CONDUCTOR-BOUND,
///     NO-TERMINAL-IN-QUEUE, QUEUE-SERIAL) HOLD over the ENTIRE bounded space of
///     the real Cohort.decide, for the modeled 13/20-command subspace — proven,
///     not sampled, for that subspace.
///   * The FAULTS-mode twin (the pre-fix RebaseConflict queue-jam) is CAUGHT by
///     NO-TERMINAL-IN-QUEUE across the same space — proof the rule has teeth.

[<Tests>]
let tests =
  testList "DST cohort spec (exhaustive proof)" [

    testCase "the modeled 13/20-command subspace is PROVEN — every rule holds at every reachable state, including the arms the old alphabet could not reach (VetoLanding/ResolveVeto/SetIntegrationHead/ReleaseClaim/FastForwardFailed)" <| fun _ ->
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
