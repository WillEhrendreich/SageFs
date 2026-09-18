module SageFs.Tests.DstShrinkTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.Scenario
open SageFs.Simulation.WorkerLifecycleSim
open SageFs.Simulation.WorkerLifecycleInvariants
open SageFs.Simulation.Shrink

/// Brief B2: shrinking failing DST scenarios to a minimal reproducer
/// (`Shrink.ddmin` / `shrinkScenario` / `shrinkMgr`). See
/// `SageFs.Simulation/Shrink.fs` for the contract (deterministic,
/// terminating, sound, minimal, length-monotonic).
///
/// ── HONEST DEVIATION FROM THE PLAN (dogfooded live via the SageFs REPL,
/// not hand-simulated) ──────────────────────────────────────────────────
/// The plan (`dst-phase3-plan.md`, Brief B2, RED test #4) predicted that
/// shrinking `MgrGenerators.staleReadyAfterCommit`
/// (`[Create 1000; HardReset 1001; Ready 1001; Ready 1000]`) through
/// `shrinkMgr` against the pid-blind no-stale-pid-applied predicate would
/// return "the minimal 3-command reproducer (Create, HardReset,
/// Ready-straggler)". Running the actual `ddmin` against the actual
/// pid-blind reducer (in a live SageFs REPL session over this exact
/// predicate, not by hand) instead finds a genuinely SMALLER 2-command
/// witness: `[Create 1000; Ready 1001]`. This is not a bug in the
/// shrinker — it is `ddmin` doing exactly its job. The pid-blind reducer's
/// defect is broader than the named scenario's own "stale straggler after
/// a swap" narrative: `stepPidBlind`'s `Ready` case
/// (`WorkerLifecycleSim.fs:227`, "any Ready commits, whatever pid it
/// carries") blindly commits ANY Ready event regardless of pid — it does
/// not even require a `HardReset` to be in flight. `Create pid0` followed
/// by `Ready pid1` (`pid1 <> pid0`, no swap ever started) already violates
/// `no-stale-pid-applied`, because pid1 is not a legitimate mutator of a
/// plain `Ready pid0` session. `ddmin` correctly found that smaller,
/// truer-to-the-bug reproducer instead of the swap-shaped one the
/// generator happened to be authored around. The same thing happens for
/// `staleSpawnFailedAfterCommit`, which also shrinks to
/// `[Create 1000; Ready 1001]` (a different named scenario, same
/// underlying minimal witness) — while `staleReadyDuringSwap` and
/// `staleSpawnFailedDuringSwap` are ALREADY minimal at 3 commands (the
/// mid-swap bug genuinely requires Create+HardReset+the stale event, so
/// `ddmin` cannot shrink them further). All of this is asserted below
/// exactly as observed.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// The predicate under which `MgrGenerators`' canonical scenarios are known
/// to fail: the pre-fix pid-blind reducer violates `no-stale-pid-applied`.
let private stillFailsPidBlind (s: MgrScenario) : bool =
  violations (runPidBlind s) <> []

[<Tests>]
let tests =
  testList "DST shrinking (ddmin / shrinkScenario / shrinkMgr)" [

    testList "minimality" [

      testCase "synthetic: ddmin collapses a 30-event haystack to a single WorkerCrashed" <| fun _ ->
        // Predicate: "the event list contains at least one WorkerCrashed" —
        // independent of the real invariants, a marker predicate whose
        // TRUE minimal witness is unambiguous: exactly one WorkerCrashed.
        let hasCrash (evs: SimEvent list) =
          evs |> List.exists (function SimEvent.WorkerCrashed -> true | _ -> false)
        let haystack : SimEvent list =
          [ for i in 0 .. 29 ->
              if i = 7 || i = 20 then SimEvent.WorkerCrashed
              elif i % 3 = 0 then SimEvent.WorkerExitedGracefully
              else SimEvent.ClockAdvance(TimeSpan.FromSeconds(float i)) ]
        let scn =
          { Seed = 4242
            Policy = RestartPolicy.defaultPolicy
            StartTime = Generators.epoch
            Events = haystack }
        let hasCrashScn (s: Scenario) = hasCrash s.Events
        hasCrashScn scn |> Expect.isTrue "the 30-event haystack does contain a WorkerCrashed"
        let shrunk = shrinkScenario hasCrashScn scn
        shrunk.Events
        |> Expect.equal "shrinks to exactly one WorkerCrashed" [ SimEvent.WorkerCrashed ]
    ]

    testList "soundness / monotonicity" [

      testPropertyWithConfig simConfig
        "shrinkMgr output is sound (still fails) and never longer than the input, whenever the pid-blind family fails" <|
        fun (seed: int) ->
          let scn = MgrGenerators.fromSeed seed
          if not (stillFailsPidBlind scn) then
            () // vacuous for this seed — non-vacuousness is proven below
          else
            let shrunk = shrinkMgr stillFailsPidBlind scn
            stillFailsPidBlind shrunk
            |> Expect.isTrue (sprintf "shrunk scenario (seed %d) must still fail the predicate" seed)
            (List.length shrunk.Commands <= List.length scn.Commands)
            |> Expect.isTrue (sprintf "shrunk scenario (seed %d) must never be longer than the original" seed)

      testCase "non-vacuous baseline: the pid-blind family genuinely fails for seeds in 1..200" <| fun _ ->
        let failingSeeds =
          [ 1 .. 200 ]
          |> List.map MgrGenerators.fromSeed
          |> List.filter stillFailsPidBlind
        failingSeeds
        |> List.isEmpty
        |> Expect.isFalse "at least one seed in 1..200 must fail under the pid-blind reducer (else the property above is vacuous)"
    ]

    testList "real payoff — canonical pid-blind scenarios shrink to their true minimal witnesses" [

      testCase "staleReadyAfterCommit shrinks to Create + a differently-pid'd Ready (smaller than the plan predicted — see module doc)" <| fun _ ->
        let shrunk = shrinkMgr stillFailsPidBlind MgrGenerators.staleReadyAfterCommit
        shrunk.Commands
        |> Expect.equal "minimal reproducer: Create then a Ready for a different pid"
             [ MgrCommand.Create 1000; MgrCommand.Ready 1001 ]
        stillFailsPidBlind shrunk
        |> Expect.isTrue "the shrunk scenario still fails under the pid-blind reducer"

      testCase "staleSpawnFailedAfterCommit shrinks to the SAME minimal witness (any-pid blind Ready is the broader bug)" <| fun _ ->
        let shrunk = shrinkMgr stillFailsPidBlind MgrGenerators.staleSpawnFailedAfterCommit
        shrunk.Commands
        |> Expect.equal "minimal reproducer: Create then a Ready for a different pid"
             [ MgrCommand.Create 1000; MgrCommand.Ready 1001 ]
        stillFailsPidBlind shrunk
        |> Expect.isTrue "the shrunk scenario still fails under the pid-blind reducer"

      testCase "staleReadyDuringSwap is already minimal at 3 commands (the mid-swap bug needs Create+HardReset+the stale Ready)" <| fun _ ->
        let shrunk = shrinkMgr stillFailsPidBlind MgrGenerators.staleReadyDuringSwap
        shrunk.Commands
        |> Expect.equal "unchanged — already a 1-minimal reproducer"
             MgrGenerators.staleReadyDuringSwap.Commands
        stillFailsPidBlind shrunk
        |> Expect.isTrue "the shrunk scenario still fails under the pid-blind reducer"

      testCase "staleSpawnFailedDuringSwap is already minimal at 3 commands" <| fun _ ->
        let shrunk = shrinkMgr stillFailsPidBlind MgrGenerators.staleSpawnFailedDuringSwap
        shrunk.Commands
        |> Expect.equal "unchanged — already a 1-minimal reproducer"
             MgrGenerators.staleSpawnFailedDuringSwap.Commands
        stillFailsPidBlind shrunk
        |> Expect.isTrue "the shrunk scenario still fails under the pid-blind reducer"
    ]

    testList "no-op on passing" [

      testCase "shrinkScenario returns a non-failing scenario unchanged" <| fun _ ->
        let cleanScn = Generators.fromSeed 7
        failsAnyInvariant cleanScn
        |> Expect.isFalse "seed 7's scenario must not violate any Phase-1 invariant (the real supervision core is sound)"
        let shrunk = shrinkScenario failsAnyInvariant cleanScn
        shrunk |> Expect.equal "unchanged when the predicate never held" cleanScn

      testCase "shrinkMgr returns a non-failing scenario unchanged" <| fun _ ->
        let cleanScn = MgrGenerators.cleanLifecycle
        stillFailsPidBlind cleanScn
        |> Expect.isFalse "cleanLifecycle must not fail even under the pid-blind reducer"
        let shrunk = shrinkMgr stillFailsPidBlind cleanScn
        shrunk |> Expect.equal "unchanged when the predicate never held" cleanScn
    ]

    testList "determinism" [

      testCase "shrinkMgr is a pure function of (predicate, scenario) — same call twice, same result" <| fun _ ->
        let a = shrinkMgr stillFailsPidBlind MgrGenerators.staleReadyAfterCommit
        let b = shrinkMgr stillFailsPidBlind MgrGenerators.staleReadyAfterCommit
        a |> Expect.equal "identical shrink result on repeat" b

      testCase "shrinkScenario is a pure function of (predicate, scenario) — same call twice, same result" <| fun _ ->
        let stormScn = Generators.crashStorm 20
        let syntheticFails (s: Scenario) =
          s.Events |> List.exists (function SimEvent.WorkerCrashed -> true | _ -> false)
        let a = shrinkScenario syntheticFails stormScn
        let b = shrinkScenario syntheticFails stormScn
        a |> Expect.equal "identical shrink result on repeat" b
        a.Events |> Expect.equal "collapses to a single WorkerCrashed" [ SimEvent.WorkerCrashed ]
    ]
  ]
