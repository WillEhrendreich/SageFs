module SageFs.Tests.SimulationTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Simulation
open SageFs.Simulation.Scenario
open SageFs.Simulation.Runner
open SageFs.Simulation.Invariants

/// DST harness tests: every invariant in Invariants.all is claimed to be a
/// TRUE property of the real supervision core, so a failure here is a genuine
/// bug in RestartPolicy/SessionLifecycle — the failing scenario (seed + events)
/// is printed for replay. See SageFs.Simulation/Scenario.fs for the DST design.

let private pick (gen: Gen<'a>) = (Gen.sample 1 gen).[0]

/// 300 random inputs per property — enough to sweep the seed space.
let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

// ── Generators (FsCheck layer over the pure hand-rolled ones) ──

/// A sane, varied policy: BackoffMax >= BackoffBase, positive spans, and the
/// startup ceiling never exceeds MaxRestarts — the domain in which the
/// invariants are claimed to hold.
let private genPolicy : Gen<RestartPolicy.Policy> =
  gen {
    let! maxRestarts = Gen.choose (1, 8)
    let! backoffBaseMs = Gen.choose (100, 2000)
    let! backoffMaxMs = Gen.choose (backoffBaseMs, 60000)
    let! resetWindowSec = Gen.choose (30, 600)
    let! startupWindowMs = Gen.choose (500, 30000)
    let! startupMax = Gen.choose (1, maxRestarts)
    return
      { RestartPolicy.Policy.MaxRestarts = maxRestarts
        BackoffBase = TimeSpan.FromMilliseconds(float backoffBaseMs)
        BackoffMax = TimeSpan.FromMilliseconds(float backoffMaxMs)
        ResetWindow = TimeSpan.FromSeconds(float resetWindowSec)
        StartupCrashWindow = TimeSpan.FromMilliseconds(float startupWindowMs)
        StartupCrashMaxRestarts = startupMax }
  }

let private genEvent : Gen<SimEvent> =
  Gen.frequency
    [ 6, Gen.constant SimEvent.WorkerCrashed
      1, Gen.constant SimEvent.WorkerExitedGracefully
      3, gen {
            let! ms = Gen.choose (0, 900000) // 0 .. 15 min, straddles both windows
            return SimEvent.ClockAdvance(TimeSpan.FromMilliseconds(float ms)) } ]

/// A richly-varied scenario over a generated policy. Carries a seed for
/// reporting even though the events are FsCheck-generated (the seed is not the
/// sole source of chaos here — the Scenario value itself is fully printable).
let private genScenario : Gen<Scenario> =
  gen {
    let! seed = Gen.choose (0, 1_000_000)
    let! policy = genPolicy
    let! events = Gen.listOf genEvent
    return
      { Seed = seed
        Policy = policy
        StartTime = Generators.epoch
        Events = events }
  }

// ── Helpers ──

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    // Replayable failure: print the whole scenario (seed + policy + events)
    // and every violated invariant with its message.
    failtestf
      "INVARIANT VIOLATION — replay this scenario:\n  Seed=%d\n  Policy=%A\n  Events=%A\n  Violations=%A"
      t.Scenario.Seed t.Scenario.Policy t.Scenario.Events vs

// ── Property tests: each invariant, over generated scenarios ──

[<Tests>]
let tests =
  testList "DST simulation harness" [

    testList "invariants hold over generated scenarios" [

      testPropertyWithConfig simConfig "seeded scenarios (default policy) — all invariants hold" <|
        fun (seed: int) ->
          // fromSeed is a pure function of the seed: this case replays exactly.
          assertHolds (run (Generators.fromSeed seed))

      testPropertyWithConfig simConfig "richly-varied scenarios (varied policy) — all invariants hold" <|
        fun () ->
          assertHolds (run (pick genScenario))

      testPropertyWithConfig simConfig "give-up-terminal holds" <|
        fun () ->
          let t = run (pick genScenario)
          giveUpTerminal.Check t
          |> Expect.equal (sprintf "give-up-terminal for seed %d" t.Scenario.Seed) Outcome.Holds

      testPropertyWithConfig simConfig "backoff-monotonic-in-window holds" <|
        fun () ->
          let t = run (pick genScenario)
          backoffMonotonicInWindow.Check t
          |> Expect.equal (sprintf "backoff-monotonic for seed %d" t.Scenario.Seed) Outcome.Holds

      testPropertyWithConfig simConfig "crash-storm-terminates (liveness) holds" <|
        fun () ->
          let t = run (pick genScenario)
          crashStormTerminates.Check t
          |> Expect.equal (sprintf "crash-storm-terminates for seed %d" t.Scenario.Seed) Outcome.Holds
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical trace (deterministic replay)" <|
        fun (seed: int) ->
          let a = run (Generators.fromSeed seed)
          let b = run (Generators.fromSeed seed)
          a.Steps |> Expect.equal "replaying the same seed yields the identical trace" b.Steps
    ]

    testList "example scenarios (worked properties)" [

      testCase "pure crash storm reaches GiveUp within the startup ceiling" <| fun () ->
        let t = run (Generators.crashStorm 20)
        // With the default policy, a back-to-back storm is all startup crashes:
        // it must give up at StartupCrashMaxRestarts (3) restarts.
        let restarts =
          t.Steps
          |> List.filter (fun s -> match s.Effect with StepEffect.Restarted _ -> true | _ -> false)
          |> List.length
        restarts
        |> Expect.equal "exactly StartupCrashMaxRestarts restarts before GiveUp"
             RestartPolicy.defaultPolicy.StartupCrashMaxRestarts
        t.Steps
        |> List.exists (fun s -> match s.Effect with StepEffect.GaveUp _ -> true | _ -> false)
        |> Expect.isTrue "the storm reaches a GiveUp outcome"
        assertHolds t

      testCase "spaced crashes in one window back off exponentially and give up at MaxRestarts" <| fun () ->
        let t = run (Generators.spacedCrashes 6 (TimeSpan.FromSeconds 20.0))
        // 20s gap > StartupCrashWindow (10s) => never a startup crash; all in
        // one window (< ResetWindow) => clean exponential backoff, cap 30s.
        let delays =
          t.Steps
          |> List.choose (fun s ->
            match s.Effect with StepEffect.Restarted d -> Some d | _ -> None)
        delays
        |> Expect.equal "exponential backoff 1,2,4,8,16 s"
             [ TimeSpan.FromSeconds 1.0; TimeSpan.FromSeconds 2.0; TimeSpan.FromSeconds 4.0
               TimeSpan.FromSeconds 8.0; TimeSpan.FromSeconds 16.0 ]
        t.Steps
        |> List.exists (fun s -> match s.Effect with StepEffect.GaveUp _ -> true | _ -> false)
        |> Expect.isTrue "gives up at MaxRestarts (5) after the 6th crash"
        assertHolds t

      testCase "give-up-terminal: crashes after GiveUp never restart" <| fun () ->
        // 20 back-to-back crashes: 3 restarts, GiveUp, then 16 terminal no-ops.
        let t = run (Generators.crashStorm 20)
        let giveUpIdx =
          t.Steps
          |> List.findIndex (fun s -> match s.Effect with StepEffect.GaveUp _ -> true | _ -> false)
        t.Steps
        |> List.filter (fun s -> s.Index > giveUpIdx)
        |> List.forall (fun s -> match s.Effect with StepEffect.Restarted _ -> false | _ -> true)
        |> Expect.isTrue "no restart is ever issued after the GiveUp step"

      // ── DST FINDING codified (see Invariants.backoffMonotonicInWindow) ──
      // The naive "all delays monotonic in a window" property is FALSE by
      // design of the circuit breaker: under a policy whose startup ceiling is
      // high enough to allow a startup crash at a high count, the fixed 4x-base
      // startup delay UNDERCUTS the exponential delay already reached — a real
      // in-window drop, exposed by the varied-policy generator. It is intended
      // behavior (invisible under defaultPolicy), so this test pins the drop as
      // expected while confirming the refined regime-scoped invariant HOLDS.
      testCase "circuit-breaker regime switch legitimately lowers the delay (intended, not a bug)" <| fun () ->
        let policy = { RestartPolicy.defaultPolicy with MaxRestarts = 8; StartupCrashMaxRestarts = 8 }
        let scn =
          { Seed = 999
            Policy = policy
            StartTime = Generators.epoch
            Events =
              [ SimEvent.WorkerCrashed
                SimEvent.ClockAdvance(TimeSpan.FromSeconds 20.0); SimEvent.WorkerCrashed
                SimEvent.ClockAdvance(TimeSpan.FromSeconds 20.0); SimEvent.WorkerCrashed
                SimEvent.ClockAdvance(TimeSpan.FromSeconds 20.0); SimEvent.WorkerCrashed
                SimEvent.ClockAdvance(TimeSpan.FromSeconds 2.0);  SimEvent.WorkerCrashed ] } // rapid => startup
        let t = run scn
        let delays =
          t.Steps
          |> List.choose (fun s ->
            match s.Effect with StepEffect.Restarted d -> Some d | _ -> None)
        // 1,2,4,8s exponential, then the startup circuit breaker drops to 4s.
        delays
        |> Expect.equal "exponential 1,2,4,8 then a startup-crash drop to 4s within one window"
             [ TimeSpan.FromSeconds 1.0; TimeSpan.FromSeconds 2.0; TimeSpan.FromSeconds 4.0
               TimeSpan.FromSeconds 8.0; TimeSpan.FromSeconds 4.0 ]
        // The dip is exactly the circuit-breaker delay (4x base = 4s), so the
        // exact, regime-free invariant still HOLDS.
        (List.last delays)
        |> Expect.equal "the dip equals the circuit-breaker delay (4x base)" (TimeSpan.FromSeconds 4.0)
        backoffMonotonicInWindow.Check t
        |> Expect.equal "monotonicity-with-circuit-breaker-exception holds" Outcome.Holds
    ]
  ]
