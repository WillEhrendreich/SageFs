module SageFs.Tests.CohortSpecTests

open Expecto
open Expecto.Flip
open SageFs
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

/// MEMBERSHIP gets its own exhaustive proof (`CohortVacancySpec.fs`), and this is
/// where it is driven. It is separate from `CohortSpecTests` above on purpose:
/// that BFS does not model `Depart`/`RenewLease`/`Tick`/`DelegateConductor` at
/// all and runs on ONE fixed clock, so no lease ever expires inside it and the
/// whole membership axis — which is where the departed-conductor defect lived —
/// was structurally unreachable. Folding membership in there would multiply its
/// 351k-node space out of reach, so the second BFS is a SEPARATE, small space.
[<Tests>]
let membershipTests =
  testList "DST cohort membership (separate exhaustive proof — CohortSpec does not model Depart/Tick)" [

    testCase "membership x presence x conductor x claim-holder is PROVEN over its whole bounded space" <| fun _ ->
      let r = CohortVacancySpec.proof ()
      r.Complete |> Expect.isTrue "the bounded membership space must be fully enumerated (frontier emptied, not capped)"
      r.Capped |> Expect.isFalse "the cap must not be hit — raise it if the alphabet grew"
      Expect.isGreaterThan "the model must reach a non-trivial space, not close trivially" (r.Nodes, 40)
      r.Violations |> Expect.equal "no reachable membership state violates any named rule" []

    testCase "FAULTS — the departed-conductor twin VIOLATES BOUND-IS-PRESENT, so the rule has teeth" <| fun _ ->
      // The pre-fix world: `departMember` marked the seat Departed and left the
      // binding alone, so the binding went on naming a member who had left.
      // If no broken world could trip the rule, "no violations" above would
      // mean nothing.
      let r = CohortVacancySpec.faults ()
      r.Complete |> Expect.isTrue "the twin's bounded membership space must also fully enumerate"
      r.Violations
      |> Expect.contains "a departed conductor keeping the seat must violate the bound-implies-present rule"
                    "BOUND-IS-PRESENT"

    testCase "the vacancy states are REACHABLE — the proof is not vacuous about them" <| fun _ ->
      // A rule can hold over a space that never contains the state it is about.
      // Drive the real core to both vacancies and check the DU says what it says.
      let t0 = System.DateTime(2020, 1, 1)
      let run (s: Cohort.CohortState<string>) (c: Cohort.CohortCommand<string>) (t: System.DateTime) =
        match Cohort.decide t [||] s c with
        | Ok(s', _, _) -> s'
        | Error e -> failwithf "expected %A to succeed: %A" c e
      let empty = Cohort.CohortState.empty ()
      let joined = run empty (Cohort.CohortCommand.Join("a", Cohort.JoinableRole.Implementer, None)) t0
      let left = run joined (Cohort.CohortCommand.Depart "a") t0
      left.Conductor
      |> Expect.equal "an explicit departure vacates the seat, naming who and why"
                    (Cohort.ConductorBinding.Vacant("a", t0, Cohort.VacancyReason.ConductorLeft))
      let lapsed = run joined Cohort.CohortCommand.Tick (t0.Add Cohort.leaseWindow)
      lapsed.Conductor
      |> Expect.equal "a lapsed lease vacates the seat too, and records the different reason"
                    (Cohort.ConductorBinding.Vacant("a", t0.Add Cohort.leaseWindow, Cohort.VacancyReason.LeaseLapsed))
      empty.Conductor
      |> Expect.equal "an untouched cohort's seat is NeverBound, which is NOT a vacancy"
                    Cohort.ConductorBinding.NeverBound

    testCase "the old presence-blind lookup is still reproducible, and is WRONG" <| fun _ ->
      // Frozen as a witness: the exact four lines that let a departed conductor
      // keep the highest authority. Exists only so this test can demonstrate
      // the difference rather than assert it.
      let t0 = System.DateTime(2020, 1, 1)
      let run (s: Cohort.CohortState<string>) (c: Cohort.CohortCommand<string>) =
        match Cohort.decide t0 [||] s c with
        | Ok(s', _, _) -> s'
        | Error e -> failwithf "expected %A to succeed: %A" c e
      let departed =
        (Cohort.CohortState.empty ()
         |> fun s -> run s (Cohort.CohortCommand.Join("a", Cohort.JoinableRole.Implementer, None))
         |> fun s -> run s (Cohort.CohortCommand.Depart "a"))
      // CHANGED, deliberately. The witness below only demonstrates anything on
      // a state the OLD code could actually reach — and the old code's defect
      // was that `departMember` left the binding naming the departed member.
      // The FIXED core now moves that binding to `Vacant` in the same step, so
      // driving the real `decide` alone no longer produces the stale binding
      // and the old lookup would (correctly) say `Anonymous`. Re-imposing the
      // stale binding here reproduces exactly the state the pre-fix core left
      // behind: the seat is Departed and the binding still names it.
      |> fun s -> { s with Conductor = Cohort.ConductorBinding.Bound "a" }
      CohortVacancySpec.presentIgnoringPresence "a" departed
      |> Expect.equal "the pre-fix lookup says Conductor for a departed member" (Cohort.Authority.Conductor "a")
      Cohort.Authority.present "a" departed
      |> Expect.equal "and the fixed lookup says Anonymous" Cohort.Authority.Anonymous
  ]
