namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.RunStallSim

/// Seeded generators for `RunStallSim` scenarios. Chaos is data: `fromSeed n` replays identically forever.
module RunStallGenerators =

  /// The stall window the worker and the landing gate are configured with. Every claim the sims make is stated in windows, so
  /// its value changes nothing about them, and the time that passes is virtual.
  let window : TimeSpan = Timeouts.testRunStallWindow ()

  let private scaled (fraction: float) : TimeSpan = TimeSpan.FromSeconds (window.TotalSeconds * fraction)

  /// `count` reports, each `gap` after the one before, the first `gap` after `start`.
  let evenly (start: TimeSpan) (gap: TimeSpan) (count: int) : TimeSpan list =
    [ for i in 1 .. count -> start + TimeSpan.FromTicks (gap.Ticks * int64 i) ]

  /// A general scenario: a seeded number of requested tests, a start inside the first window, a seeded number of reports each
  /// strictly less than a window after the one before, and then a completion or a hang.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let requested = rnd.Next(1, 401)
    let startsAt = scaled (0.9 * rnd.NextDouble())
    let reportCount = rnd.Next(0, requested + 1)
    let reports =
      [ 1 .. reportCount ]
      |> List.fold (fun (acc: TimeSpan list) _ ->
        let previous = acc |> List.tryHead |> Option.defaultValue startsAt
        scaled (0.01 + 0.89 * rnd.NextDouble()) + previous :: acc) []
      |> List.rev
    let ending =
      match rnd.Next 5 with
      | 0 | 1 -> Ending.Hangs
      | _ -> Ending.Finishes (scaled (0.9 * rnd.NextDouble()))
    { Seed = seed; Window = window; Requested = requested; StartsAt = startsAt; Reports = reports; Ending = ending }

  /// The run from the cohort: 19067 tests requested, each reporting as it finishes at an even pace. Timed directly, a full run
  /// of that size took 4 minutes 24 seconds against a two-minute cap, so 2.2 windows, and the cap cut a run of that size with
  /// 538 reported. The old fixed deadline cuts this one at one window.
  let cohortFullRun : Scenario =
    let total = scaled 2.2
    let requested = 19067
    { Seed = 1
      Window = window
      Requested = requested
      StartsAt = TimeSpan.Zero
      Reports = evenly TimeSpan.Zero (TimeSpan.FromTicks (total.Ticks / int64 requested)) requested
      Ending = Ending.Finishes (scaled 0.01) }

  /// A run that reports at nine tenths of a window per test for a hundred tests, so over an hour of virtual time, then finishes.
  /// The old cap would have ended it at the second report.
  let slowButReporting : Scenario =
    { Seed = 2
      Window = window
      Requested = 100
      StartsAt = TimeSpan.Zero
      Reports = evenly TimeSpan.Zero (scaled 0.9) 100
      Ending = Ending.Finishes (scaled 0.5) }

  /// A run that reports `reporting` tests and then stops saying anything, with `requested` asked for.
  let hangsAfter (requested: int) (reporting: int) : Scenario =
    { Seed = 100 + reporting
      Window = window
      Requested = requested
      StartsAt = TimeSpan.Zero
      Reports = evenly TimeSpan.Zero (scaled 0.5) (min reporting requested)
      Ending = Ending.Hangs }

  /// A run that was asked for and never starts: it would start after twenty windows, and nothing is said before that.
  let neverStarts : Scenario =
    { Seed = 3
      Window = window
      Requested = 50
      StartsAt = scaled 20.0
      Reports = evenly (scaled 20.0) (scaled 0.5) 50
      Ending = Ending.Finishes (scaled 0.1) }
