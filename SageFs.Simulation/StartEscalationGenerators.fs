namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.StartEscalationSim

/// Seeded, dependency-free generators for `StartEscalationSim` scenarios. Chaos is data:
/// `run (fromSeed n)` replays identically for ever.
module StartEscalationGenerators =

  /// The silence allowance a first attempt gets on the `Fast` tier, as in `Timeouts`. The sim does
  /// not read `Timeouts` (its values depend on the process's tier); it states the baseline it models.
  let private baselineInactivity = TimeSpan.FromSeconds 30.0

  /// The absolute bound on the `Fast` tier, as in `Timeouts`.
  let private baselineAbsolute = TimeSpan.FromMinutes 10.0

  let private scaled (tier: MachineTier) (span: TimeSpan) : TimeSpan =
    TimeSpan.FromTicks(int64 (float span.Ticks * MachineTier.factor tier))

  let private scenarioOf (seed: int) (tier: MachineTier) (history: StageHistory) (machine: Machine) : Scenario =
    { Seed = seed
      Tier = tier
      History = history
      StaticInactivity = scaled tier baselineInactivity
      Absolute = scaled tier baselineAbsolute
      Machine = machine }

  /// A scenario for a seed: any tier, a learned history or none, and a machine that needs anything from
  /// a second to well past the longest patience the schedule reaches, or that hangs, or that is slow on
  /// the first attempt only (load that passes).
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let tier = MachineTier.all |> List.item (rnd.Next(0, List.length MachineTier.all))
    let history =
      match rnd.Next(0, 3) with
      | 0 -> StageHistory.NeverSeen
      | _ ->
        let seen = float (rnd.Next(1, 200))
        StageHistory.Seen
          { Stage = StartStage.WorkerPort
            SmoothedMs = seen * 1000.0
            DeviationMs = float (rnd.Next(0, 40)) * 1000.0
            Samples = rnd.Next(1, 30) }
    let ceiling = (scaled tier baselineAbsolute).TotalSeconds
    let machine =
      match rnd.Next(0, 6) with
      | 0 -> Machine.Hangs
      | 1 ->
        // loaded on the first attempt only
        let slow = TimeSpan.FromSeconds(rnd.NextDouble() * ceiling)
        let quick = TimeSpan.FromSeconds(1.0 + rnd.NextDouble() * 20.0)
        Machine.NeedsPerAttempt [ slow; quick ]
      | _ -> Machine.NeedsPerAttempt [ TimeSpan.FromSeconds(1.0 + rnd.NextDouble() * ceiling * 1.5) ]
    scenarioOf seed tier history machine

  // ── Named minimal scenarios ─────────────────────────────────────────────

  /// The Phenom II: the tier is `Constrained`, the start needs 40 seconds, and the baseline 30 second
  /// allowance (what a `Fast` tier would give) would kill it every time. With the tier's allowance it
  /// starts first time; this scenario is about the case where the tier was wrong and escalation saves it.
  let slowButHealthyOnTheWrongTier : Scenario =
    scenarioOf 1 MachineTier.Fast StageHistory.NeverSeen (Machine.NeedsPerAttempt [ TimeSpan.FromSeconds 40.0 ])

  /// A machine that never finishes a start.
  let hangs : Scenario =
    scenarioOf 2 MachineTier.Standard StageHistory.NeverSeen Machine.Hangs

  /// A busy moment on the first attempt, then a quiet machine: the second attempt starts at once.
  let loadedOnce : Scenario =
    scenarioOf 3 MachineTier.Fast StageHistory.NeverSeen (Machine.NeedsPerAttempt [ TimeSpan.FromSeconds 100.0; TimeSpan.FromSeconds 5.0 ])

  /// A machine whose history says starts take 90 seconds: the first attempt is already patient enough.
  let knownSlowMachine : Scenario =
    scenarioOf 4 MachineTier.Constrained
      (StageHistory.Seen { Stage = StartStage.WorkerPort; SmoothedMs = 90000.0; DeviationMs = 10000.0; Samples = 12 })
      (Machine.NeedsPerAttempt [ TimeSpan.FromSeconds 95.0 ])
