module SageFs.Tests.DstOracleTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Simulation
open SageFs.Simulation.Scenario
open SageFs.Simulation.ReferenceModel
open SageFs.Simulation.Oracle

/// Brief B1 — reference-vs-candidate oracle for the supervision core.
///
/// Stronger than the invariant suite (SimulationTests.fs): invariants assert
/// that some PROPERTY holds of the real trace; this oracle asserts exact
/// behavioral EQUIVALENCE between the real `Runner.Trace` and an independent
/// `ReferenceModel` recomputation that never calls `RestartPolicy.decide` /
/// `SessionLifecycle.onWorkerExited`. An empty `Divergence list` means the
/// real supervision core and the from-first-principles spec agree.

let private pick (gen: Gen<'a>) = (Gen.sample 1 gen).[0]

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

// ── Generators (mirrors SimulationTests.fs's genPolicy/genScenario shape —
//    duplicated here per the brief so this test file has no dependency on
//    another test file's private generators). ──

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
      3,
      gen {
        let! ms = Gen.choose (0, 900000) // 0 .. 15 min, straddles both windows
        return SimEvent.ClockAdvance(TimeSpan.FromMilliseconds(float ms))
      } ]

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

let private assertAgrees (scn: Scenario) =
  match checkScenario scn with
  | [] -> ()
  | divergences ->
    failtestf
      "ORACLE DIVERGENCE — real trace disagrees with the reference model, replay this scenario:\n  Seed=%d\n  Policy=%A\n  Events=%A\n  Divergences=%A"
      scn.Seed scn.Policy scn.Events divergences

[<Tests>]
let tests =
  testList "DST reference-vs-candidate oracle" [

    testList "agreement — the real supervision core matches the independent reference model" [

      testPropertyWithConfig simConfig "seeded scenarios (default policy) — oracle reports no divergence" <|
        fun (seed: int) -> assertAgrees (Generators.fromSeed seed)

      testPropertyWithConfig simConfig "richly-varied scenarios (varied policy) — oracle reports no divergence" <|
        fun () -> assertAgrees (pick genScenario)

      testCase "pure crash storm agrees with the reference model" <| fun () ->
        assertAgrees (Generators.crashStorm 20)

      testCase "spaced crashes in one window agree with the reference model" <| fun () ->
        assertAgrees (Generators.spacedCrashes 6 (TimeSpan.FromSeconds 20.0))

      testCase "circuit-breaker regime switch (dip case) agrees with the reference model" <| fun () ->
        // The scenario from SimulationTests.fs that pins the intended
        // in-window delay dip — the oracle must agree with it exactly, not
        // just satisfy the looser monotonicity-with-exception invariant.
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
        assertAgrees scn
    ]

    testList "teeth — a deliberately-wrong reference model is caught, not vacuously agreed with" [

      testCase "wrongGiveUpOffByOne diverges on a crash storm (real gives up one crash earlier)" <| fun () ->
        let scn = Generators.crashStorm 20
        let real = Runner.run scn
        let wrongReference = ReferenceModel.wrongGiveUpOffByOne scn
        let divergences = Oracle.check real wrongReference
        divergences
        |> List.isEmpty
        |> Expect.isFalse "a reference model that gives up one crash late must diverge from the real trace"
        // Pin the exact first disagreement: real gives up at step 3 (after 3
        // startup-crash restarts, ceiling 3); the wrong reference still
        // expects a 4th restart at that step.
        let firstEffectDivergence =
          divergences
          |> List.tryFind (fun d -> d.Field = DivergedField.Effect)
        match firstEffectDivergence with
        | Some d ->
          d.StepIndex |> Expect.equal "the divergence is pinned at the real give-up step" 3
          d.Actual |> Expect.equal "the real trace actually gave up here" "GaveUp"
          d.Expected |> Expect.equal "the wrong reference expected one more restart" "Restarted"
        | None -> failtestf "expected an Effect-field divergence, got: %A" divergences

      testPropertyWithConfig simConfig "the oracle has teeth across the seed sweep: some scenario diverges under wrongGiveUpOffByOne" <|
        fun () ->
          // Not every generated scenario reaches the give-up ceiling, so
          // sweep a battery of crash storms — deterministic, always exercise
          // the give-up path — and require at least one real divergence.
          let anyDivergence =
            [ 3 .. 20 ]
            |> List.exists (fun n ->
              let scn = Generators.crashStorm n
              Oracle.check (Runner.run scn) (ReferenceModel.wrongGiveUpOffByOne scn)
              |> List.isEmpty
              |> not)
          anyDivergence
          |> Expect.isTrue "at least one crash-storm scenario must expose the wrong reference's off-by-one give-up"
    ]

    testList "determinism" [

      testProperty "same scenario => identical divergence report" <|
        fun (seed: int) ->
          let scn = Generators.fromSeed seed
          let a = checkScenario scn
          let b = checkScenario scn
          a |> Expect.equal "replaying the same scenario yields the identical oracle report" b
    ]
  ]
