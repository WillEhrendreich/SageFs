module SageFs.Tests.DstCoverageTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.Scenario
open SageFs.Simulation.Invariants
open SageFs.Simulation.Coverage

/// Coverage-matrix tests (Brief B3): turns "the suite is green" into "the
/// suite is green AND it actually exercised every branch it claims to."
/// Answers the roast's §9 "is this vacuously green?" concern by computing,
/// over a battery of traces, which EffectKind shapes actually occurred and
/// which invariants' ANTECEDENTS were actually hit (not merely "held" —
/// an invariant whose antecedent never fires trivially Holds).
///
/// See SageFs.Simulation/Coverage.fs for the design.

/// The varied-policy circuit-breaker scenario, reproduced from
/// SimulationTests.fs:181-191 — the one worked example known to produce a
/// CircuitBreakerDip (an in-window delay drop to the fixed 4x-base delay).
let private varyPolicyCircuitBreakerScenario : Scenario =
  let policy = { RestartPolicy.defaultPolicy with MaxRestarts = 8; StartupCrashMaxRestarts = 8 }
  { Seed = 999
    Policy = policy
    StartTime = Generators.epoch
    Events =
      [ SimEvent.WorkerCrashed
        SimEvent.ClockAdvance(TimeSpan.FromSeconds 20.0); SimEvent.WorkerCrashed
        SimEvent.ClockAdvance(TimeSpan.FromSeconds 20.0); SimEvent.WorkerCrashed
        SimEvent.ClockAdvance(TimeSpan.FromSeconds 20.0); SimEvent.WorkerCrashed
        SimEvent.ClockAdvance(TimeSpan.FromSeconds 2.0);  SimEvent.WorkerCrashed ] }

/// A restart, then a clock advance past the ResetWindow (5 min), then another
/// crash: deterministically produces a WindowReset (the second restart's
/// WindowStart differs from the first's — the count started over). Added
/// alongside the seeded sweep so this effect kind's coverage does not depend
/// on randomness happening to produce a >5min gap between two crashes.
let private windowResetScenario : Scenario =
  { Seed = -100
    Policy = RestartPolicy.defaultPolicy
    StartTime = Generators.epoch
    Events =
      [ SimEvent.WorkerCrashed
        SimEvent.ClockAdvance(TimeSpan.FromSeconds 400.0)
        SimEvent.WorkerCrashed ] }

/// A single graceful exit from the initial Ready state: deterministically
/// produces a Stopped effect. Added for the same determinism reason as
/// windowResetScenario — the seeded sweep is *likely* to hit an early
/// graceful exit but should not be the only source of this effect kind.
let private gracefulStopScenario : Scenario =
  { Seed = -101
    Policy = RestartPolicy.defaultPolicy
    StartTime = Generators.epoch
    Events = [ SimEvent.WorkerExitedGracefully ] }

/// The default coverage battery: 300 seeded scenarios (breadth) plus the
/// canonical worked examples that guarantee every effect kind and invariant
/// antecedent is hit deterministically, independent of RNG luck.
let private defaultBattery : Trace list =
  [ for seed in 1 .. 300 -> Runner.run (Generators.fromSeed seed) ]
  @ [ Runner.run (Generators.crashStorm 20)
      Runner.run (Generators.spacedCrashes 6 (TimeSpan.FromSeconds 20.0))
      Runner.run varyPolicyCircuitBreakerScenario
      Runner.run windowResetScenario
      Runner.run gracefulStopScenario ]

[<Tests>]
let tests =
  testList "DST coverage matrix" [

    testCase "coverage floor: the default battery covers every EffectKind and exercises every invariant" <| fun () ->
      let report = Coverage.over defaultBattery

      report.UncoveredEffects
      |> Expect.equal "the default battery leaves no EffectKind uncovered" Set.empty

      report.Effects
      |> Expect.equal
           "every EffectKind — including the hard-to-reach CircuitBreakerDip and WindowReset — is covered"
           (Set.ofList
             [ EffectKind.Restarted; EffectKind.GaveUp; EffectKind.Stopped
               EffectKind.NoEffectTerminal; EffectKind.ClockOnly
               EffectKind.CircuitBreakerDip; EffectKind.WindowReset ])

      report.Invariants
      |> List.forall (fun h -> h.Exercised)
      |> Expect.isTrue "every named invariant's antecedent is exercised at least once (not vacuously green)"

      report.ScenarioCount
      |> Expect.equal "the battery size is reported accurately" (List.length defaultBattery)

    testCase "non-vacuity guard: a clock-only battery covers only ClockOnly and exercises no invariant" <| fun () ->
      // Proves the report genuinely distinguishes exercised from unexercised —
      // without this, the coverage-floor test above would be meaningless: a
      // report that always claims full coverage regardless of input would
      // pass it trivially.
      let clockOnlyScenario : Scenario =
        { Seed = 0
          Policy = RestartPolicy.defaultPolicy
          StartTime = Generators.epoch
          Events = [ SimEvent.ClockAdvance(TimeSpan.FromSeconds 1.0) ] }

      let report = Coverage.over [ Runner.run clockOnlyScenario ]

      report.Effects
      |> Expect.equal "a clock-only trace covers exactly ClockOnly" (Set.ofList [ EffectKind.ClockOnly ])

      report.Invariants
      |> List.forall (fun h -> not h.Exercised)
      |> Expect.isTrue "no invariant's antecedent is exercised by a clock-only battery"

      report.UncoveredEffects
      |> Expect.equal
           "every other EffectKind is reported uncovered"
           (Set.ofList
             [ EffectKind.Restarted; EffectKind.GaveUp; EffectKind.Stopped
               EffectKind.NoEffectTerminal; EffectKind.CircuitBreakerDip; EffectKind.WindowReset ])

    testCase "render produces a printable matrix naming every effect and invariant" <| fun () ->
      let report = Coverage.over [ Runner.run (Generators.crashStorm 20) ]
      let text = Coverage.render report

      [ "Restarted"; "GaveUp"; "Stopped"; "NoEffectTerminal"; "ClockOnly"
        "CircuitBreakerDip"; "WindowReset"; "give-up-terminal"
        "backoff-monotonic-in-window"; "crash-storm-terminates" ]
      |> List.forall text.Contains
      |> Expect.isTrue "the rendered report names every EffectKind and every invariant Id"

    testCase "empty battery: no scenario count, no effects, no invariant exercised" <| fun () ->
      let report = Coverage.over ([] : Trace list)

      report.ScenarioCount |> Expect.equal "zero scenarios" 0
      report.Effects |> Expect.equal "no effects covered" Set.empty
      report.Invariants
      |> List.forall (fun h -> not h.Exercised)
      |> Expect.isTrue "no invariant is exercised by an empty battery"
  ]
