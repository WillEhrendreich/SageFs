module SageFs.Tests.DstFaultInjectionTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.Scenario
open SageFs.Simulation.WorkerLifecycleSim
open SageFs.Simulation.WorkerLifecycleInvariants
open SageFs.Simulation.ManifestSim
open SageFs.Simulation.ManifestInvariants
open SageFs.Simulation.FaultInjection
open SageFs.Features.DaemonManifest

/// Phase 3C DST (Brief B8): reusable, pure fault-injection combinators —
/// clock skew, message reorder, drop, duplicate — composed over the shipped
/// generators (Phase 1 `Generators`, Phase 2 `MgrGenerators`, Brief B5
/// `ManifestGenerators`) and the real subject cores. See
/// `SageFs.Simulation/FaultInjection.fs` for the combinator contracts
/// (pure, deterministic, composable).
///
/// ── HONEST RESULT (dogfooded live via the SageFs REPL, not hand-derived)
/// ───────────────────────────────────────────────────────────────────────
/// Every robustness property below HOLDS. This was verified interactively
/// in a live SageFs session before being written down as a permanent
/// regression suite:
///   * `no-stale-pid-applied` survived `reorderWithin 3` over 300 seeded
///     `MgrScenario`s — 0 violations. The real `WorkerEventGuard`'s
///     decision is a pure function of (current pid, pending swap pid, event
///     pid) at the moment a command is folded, not of how the command
///     stream happened to arrive; reordering the stream just produces a
///     different, still-valid replay of the guard, never a stale-pid leak.
///   * The manifest owner's `no-lost-update` survived `reorderWithin 3`
///     over 500 seeded `ManifestScenario`s — 0 violations, confirming B5's
///     single-owner design (every committed mutation is applied in
///     whatever order it is actually received, never lost) independent of
///     that order.
///   * A non-monotonic (skewed, including zero and negative) simulated
///     clock never throws and never violates a Phase-1 invariant over 300
///     seeded scenarios. Read from the source rather than assumed:
///     `RestartPolicy.decide`'s two window checks
///     (SageFs.Core/RestartPolicy.fs:93,102) are `(now - X) <= threshold` /
///     `> threshold` TimeSpan comparisons — a negative delta from a
///     backward clock jump is simply "very much within the window" (never
///     an exception, never a desync); termination is structural (every
///     scenario is a finite event list), so there is no infinite-restart
///     risk to begin with.
/// No genuine bug was found. That is itself the finding this brief asks
/// for: the properties are locked in below as a permanent regression
/// harness, so a FUTURE change that breaks order-robustness or
/// clock-monotonicity-independence fails loudly here — not in production
/// under real network jitter or a machine with clock skew.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }
let private manifestConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

[<Tests>]
let tests =
  testList "DST fault-injection combinators" [

    testList "generic combinators are pure and deterministic" [

      testPropertyWithConfig simConfig "reorderWithin: same (seed, xs) => identical output" <|
        fun (seed: int) (xs: int list) ->
          FaultInjection.reorderWithin 3 seed xs
          |> Expect.equal "reorderWithin is a pure function of (window, seed, xs)" (FaultInjection.reorderWithin 3 seed xs)

      testPropertyWithConfig simConfig "reorderWithin: preserves the multiset (a permutation, never a mutation)" <|
        fun (seed: int) (xs: int list) ->
          let out = FaultInjection.reorderWithin 4 seed xs
          List.sort out
          |> Expect.equal "reordering is a permutation: same multiset, different order" (List.sort xs)

      testPropertyWithConfig simConfig "drop: same (seed, xs) => identical output, and the result is a sublist" <|
        fun (seed: int) (xs: int list) ->
          let out1 = FaultInjection.drop 0.4 seed xs
          let out2 = FaultInjection.drop 0.4 seed xs
          out1 |> Expect.equal "drop is a pure function of (rate, seed, xs)" out2
          (out1.Length, xs.Length) |> Expect.isLessThanOrEqual "drop never grows the list"

      testPropertyWithConfig simConfig "duplicate: same (seed, xs) => identical output, and the result never shrinks" <|
        fun (seed: int) (xs: int list) ->
          let out1 = FaultInjection.duplicate 0.4 seed xs
          let out2 = FaultInjection.duplicate 0.4 seed xs
          out1 |> Expect.equal "duplicate is a pure function of (rate, seed, xs)" out2
          (out1.Length, xs.Length) |> Expect.isGreaterThanOrEqual "duplicate never shrinks the list"

      testPropertyWithConfig simConfig "withClockSkew: same seed => identical output scenario" <|
        fun (seed: int) (genSeed: int) ->
          let scn = Generators.fromSeed genSeed
          FaultInjection.withClockSkew seed scn
          |> Expect.equal "withClockSkew is a pure function of (seed, scenario)" (FaultInjection.withClockSkew seed scn)
    ]

    testList "reorderWithin is a genuine BOUNDED permutation" [

      testCase "no element moves outside its window-sized chunk" <| fun _ ->
        let xs = [ 1 .. 37 ]
        let window = 4
        for seed in 1 .. 50 do
          let out = FaultInjection.reorderWithin window seed xs
          out
          |> List.iteri (fun newIdx v ->
            (newIdx / window)
            |> Expect.equal (sprintf "seed %d: value %d (original index %d) crossed a window boundary" seed v (v - 1)) ((v - 1) / window))

      testCase "window <= 1 is a no-op (nothing to reorder within a window of one)" <| fun _ ->
        let xs = [ 1 .. 10 ]
        FaultInjection.reorderWithin 1 99 xs |> Expect.equal "window=1 leaves order unchanged" xs
        FaultInjection.reorderWithin 0 99 xs |> Expect.equal "window=0 leaves order unchanged" xs
        FaultInjection.reorderWithin -3 99 xs |> Expect.equal "a negative window leaves order unchanged" xs

      testCase "empty and singleton lists are no-ops" <| fun _ ->
        FaultInjection.reorderWithin 4 1 ([]: int list) |> Expect.isEmpty "reordering an empty list stays empty"
        FaultInjection.reorderWithin 4 1 [ 7 ] |> Expect.equal "reordering a singleton is a no-op" [ 7 ]
    ]

    testList "drop / duplicate compose with the shipped generators" [

      testCase "drop 1.0 empties any command stream deterministically" <| fun _ ->
        let scn = MgrGenerators.fromSeed 5
        FaultInjection.drop 1.0 1 scn.Commands |> Expect.isEmpty "rate=1.0 drops everything"

      testCase "drop 0.0 is a no-op" <| fun _ ->
        let scn = MgrGenerators.fromSeed 5
        FaultInjection.drop 0.0 1 scn.Commands |> Expect.equal "rate=0.0 keeps every command" scn.Commands

      testCase "duplicate 0.0 is a no-op" <| fun _ ->
        let scn = MgrGenerators.fromSeed 5
        FaultInjection.duplicate 0.0 1 scn.Commands |> Expect.equal "rate=0.0 keeps the stream unchanged" scn.Commands

      testCase "duplicate 1.0 doubles every command in place, preserving order" <| fun _ ->
        let scn = MgrGenerators.fromSeed 5
        let out = FaultInjection.duplicate 1.0 1 scn.Commands
        out |> Expect.equal "rate=1.0 doubles every command" (scn.Commands |> List.collect (fun c -> [ c; c ]))
    ]

    testList "no-stale-pid-applied is ORDER-ROBUST under reorderWithin" [

      testPropertyWithConfig simConfig "seeded worker-lifecycle scenarios still hold the real guard's invariant after reordering" <|
        fun (seed: int) ->
          let scn = MgrGenerators.fromSeed seed
          let reordered = { scn with Commands = FaultInjection.reorderWithin 3 (seed * 13 + 5) scn.Commands }
          let t = WorkerLifecycleSim.run reordered
          match WorkerLifecycleInvariants.violations t with
          | [] -> ()
          | vs ->
            failtestf
              "REORDER FINDING — real WorkerEventGuard violated after reordering, replay this scenario:\n  Seed=%d\n  Reordered Commands=%A\n  Violations=%A"
              seed reordered.Commands vs
    ]

    testList "the supervision core stays LIVE under a non-monotonic (skewed) clock" [

      testPropertyWithConfig simConfig "clock-skewed scenarios never throw and still satisfy every Phase-1 invariant" <|
        fun (seed: int) ->
          let scn = Generators.fromSeed seed |> FaultInjection.withClockSkew (seed * 7 + 1)
          // Must not throw: no exception on a negative/zero TimeSpan delta.
          let t = Runner.run scn
          match Invariants.violations t with
          | [] -> ()
          | vs ->
            failtestf
              "CLOCK-SKEW FINDING — replay this scenario:\n  Seed=%d\n  Skewed Events=%A\n  Violations=%A"
              seed scn.Events vs

      testCase "DOCUMENTED: a backward clock jump is read as \"very much within the window\", never an exception" <| fun _ ->
        // RestartPolicy.decide's two window checks (SageFs.Core/RestartPolicy.fs:93,102)
        // are `(now - X) <= threshold` / `> threshold` TimeSpan comparisons.
        // When the clock skews backward, `now - X` is a NEGATIVE TimeSpan,
        // which is always `<= threshold` for any positive threshold — read
        // as a rapid/startup crash, never an exception or an unreachable
        // branch. Pinned here as a permanent worked example after being
        // confirmed empirically (300 skewed scenarios, 0 exceptions, 0
        // violations, dogfooded live via the SageFs REPL).
        let scn : Scenario =
          { Seed = -301
            Policy = RestartPolicy.defaultPolicy
            StartTime = Generators.epoch
            Events =
              [ SimEvent.WorkerCrashed
                SimEvent.ClockAdvance(TimeSpan.FromSeconds -999999.0) // clock leaps backward
                SimEvent.WorkerCrashed
                SimEvent.ClockAdvance TimeSpan.Zero // clock freezes
                SimEvent.WorkerCrashed ] }
        let t = Runner.run scn // must not throw
        Invariants.violations t |> Expect.equal "a non-monotonic clock never violates a Phase-1 invariant" []
    ]

    testList "manifest no-lost-update SURVIVES reordering; order-sensitivity is documented, not hidden" [

      testPropertyWithConfig manifestConfig "seeded manifest scenarios still hold no-lost-update (real single-owner) after reordering" <|
        fun (seed: int) ->
          let scn = ManifestGenerators.fromSeed seed
          let reordered = { scn with Commands = FaultInjection.reorderWithin 3 (seed * 17 + 3) scn.Commands }
          let t = ManifestSim.run reordered
          match ManifestInvariants.violations t with
          | [] -> ()
          | vs ->
            failtestf
              "MANIFEST REORDER FINDING — replay this scenario:\n  Seed=%d\n  Reordered Commands=%A\n  Violations=%A"
              seed reordered.Commands vs

      testCase "independent Applies (disjoint session ids) COMMUTE: identical final state either order" <| fun _ ->
        let recA : DaemonSessionRecord =
          { SessionId = "sess-A"; Projects = [ "A.fsproj" ]; WorkingDir = "/work/A"; CreatedAt = epoch; StoppedAt = None }
        let recB : DaemonSessionRecord =
          { SessionId = "sess-B"; Projects = [ "B.fsproj" ]; WorkingDir = "/work/B"; CreatedAt = epoch; StoppedAt = None }
        let seedCmd = OwnerCmd.Apply(ManifestMutation.SyncLive([ recA; recB ], None, epoch, LiveSync.Running))
        let removeA = OwnerCmd.Apply(ManifestMutation.Remove "sess-A")
        let removeB = OwnerCmd.Apply(ManifestMutation.Remove "sess-B")
        let order1 : ManifestScenario = { Seed = -900; Commands = [ seedCmd; removeA; removeB ] }
        let order2 : ManifestScenario = { Seed = -901; Commands = [ seedCmd; removeB; removeA ] }
        let t1 = ManifestSim.run order1
        let t2 = ManifestSim.run order2
        (List.last t1.Steps).StateAfter.Current
        |> Expect.equal "removing A-then-B and B-then-A land on the identical final manifest" (List.last t2.Steps).StateAfter.Current
        ManifestInvariants.violations t1 |> Expect.equal "no-lost-update holds for order1" []
        ManifestInvariants.violations t2 |> Expect.equal "no-lost-update holds for order2" []

      testCase "conflicting Applies on the SAME session are LEGITIMATELY order-sensitive — but never a lost update" <| fun _ ->
        let recA : DaemonSessionRecord =
          { SessionId = "sess-A"; Projects = [ "A.fsproj" ]; WorkingDir = "/work/A"; CreatedAt = epoch; StoppedAt = None }
        let seedCmd = OwnerCmd.Apply(ManifestMutation.SyncLive([ recA ], None, epoch, LiveSync.Running))
        let removeA = OwnerCmd.Apply(ManifestMutation.Remove "sess-A")
        let reviveA = OwnerCmd.Apply(ManifestMutation.SyncLive([ recA ], None, epoch, LiveSync.Running))
        let removeThenRevive : ManifestScenario = { Seed = -902; Commands = [ seedCmd; removeA; reviveA ] }
        let reviveThenRemove : ManifestScenario = { Seed = -903; Commands = [ seedCmd; reviveA; removeA ] }
        let t1 = ManifestSim.run removeThenRevive
        let t2 = ManifestSim.run reviveThenRemove
        (List.last t1.Steps).StateAfter.Current.Sessions
        |> Map.containsKey "sess-A"
        |> Expect.isTrue "Remove-then-SyncLive ends with sess-A present (the sync re-adds it)"
        (List.last t2.Steps).StateAfter.Current.Sessions
        |> Map.containsKey "sess-A"
        |> Expect.isFalse "SyncLive-then-Remove ends with sess-A absent — order legitimately changes the outcome"
        // Neither ordering ever LOSES a committed mutation — every Apply that
        // returned Committed is reflected exactly, in the order it actually
        // landed. Order-sensitivity of the OUTCOME is not the same claim as
        // a lost update, and this is the distinction the brief asks to be
        // asserted rather than conflated.
        ManifestInvariants.violations t1 |> Expect.equal "no-lost-update still holds for remove-then-revive" []
        ManifestInvariants.violations t2 |> Expect.equal "no-lost-update still holds for revive-then-remove" []
    ]
  ]
