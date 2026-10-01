namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.StartLearningSim

/// Seeded generators for `StartLearningSim`.
module StartLearningGenerators =

  /// The silence allowance a first attempt gets on the `Fast` tier, as in `Timeouts`; the sim states the
  /// baseline it models instead of reading `Timeouts`, whose values depend on the process's tier.
  let private baselineInactivity = TimeSpan.FromSeconds 30.0

  let private baselineAbsolute = TimeSpan.FromMinutes 10.0

  let private scaled (tier: MachineTier) (span: TimeSpan) : TimeSpan =
    TimeSpan.FromTicks(int64 (float span.Ticks * MachineTier.factor tier))

  /// A machine whose every start takes about `typical`, give or take a fifth, for `count` starts.
  let stableMachine (seed: int) (tier: MachineTier) (typical: TimeSpan) (count: int) : Scenario =
    let rnd = Random(seed)
    { Seed = seed
      Tier = tier
      StaticInactivity = scaled tier baselineInactivity
      Absolute = scaled tier baselineAbsolute
      Starts = [ for _ in 1 .. count -> TimeSpan.FromSeconds(typical.TotalSeconds * (0.9 + rnd.NextDouble() * 0.2)) ] }

  /// A scenario for a seed: any tier, a typical start from a few seconds to well past the tier's allowance,
  /// and between two and twenty starts.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let tier = MachineTier.all |> List.item (rnd.Next(0, List.length MachineTier.all))
    let typical = TimeSpan.FromSeconds(2.0 + rnd.NextDouble() * (scaled tier baselineInactivity).TotalSeconds * 2.0)
    stableMachine seed tier typical (rnd.Next(2, 21))

  /// The Phenom II's case: a start needs about 40 s where the allowance is 30, over ten starts.
  let phenom : Scenario = stableMachine 7 MachineTier.Fast (TimeSpan.FromSeconds 40.0) 10
