namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.Scenario

/// Seeded, hand-rolled generators. Chaos is data: a scenario is a pure
/// function of its seed, so `run (fromSeed n)` replays identically forever.
/// Kept dependency-free (System.Random only) so the harness stays a pure
/// SageFs.Core-only library; the FsCheck-driven property tests layer on top of
/// these in SageFs.Tests.
module Generators =

  /// A fixed, deterministic origin instant for all generated scenarios. Never
  /// `DateTime.Now` — time is injected.
  let epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

  /// Generate a clock-advance span biased across the three regimes that matter
  /// to the policy: inside the StartupCrashWindow (<10s, drives the circuit
  /// breaker), inside the ResetWindow (<5min, same window), and past the
  /// ResetWindow (>5min, forces a window reset).
  let private randomSpan (rnd: Random) : TimeSpan =
    match rnd.Next(0, 3) with
    | 0 -> TimeSpan.FromMilliseconds(float (rnd.Next(0, 9000)))    // < startup window
    | 1 -> TimeSpan.FromSeconds(float (rnd.Next(11, 290)))         // 11s .. ~5min
    | _ -> TimeSpan.FromSeconds(float (rnd.Next(301, 1200)))       // > reset window

  /// A general scenario: a mixed stream of crashes, graceful exits and clock
  /// advances against the default policy. Crashes dominate so the supervision
  /// core is actually exercised.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(1, 40)
    let events =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 10) with
          | 0 -> SimEvent.WorkerExitedGracefully
          | 1 | 2 -> SimEvent.ClockAdvance(randomSpan rnd)
          | _ -> SimEvent.WorkerCrashed ]
    { Seed = seed
      Policy = RestartPolicy.defaultPolicy
      StartTime = epoch
      Events = events }

  /// A pure crash storm: `count` back-to-back crashes with no clock advance,
  /// so every crash after the first is a rapid startup crash. Exercises the
  /// circuit breaker and the liveness invariant — it must reach GiveUp.
  let crashStorm (count: int) : Scenario =
    { Seed = -1
      Policy = RestartPolicy.defaultPolicy
      StartTime = epoch
      Events = List.replicate count SimEvent.WorkerCrashed }

  /// Spaced crashes: each crash separated by `gap`, so none is a startup crash
  /// (when gap > StartupCrashWindow) and, when gap < ResetWindow, they share a
  /// window — exercising the exponential-backoff monotonicity path.
  let spacedCrashes (count: int) (gap: TimeSpan) : Scenario =
    { Seed = -2
      Policy = RestartPolicy.defaultPolicy
      StartTime = epoch
      Events =
        [ for i in 1 .. count do
            if i > 1 then yield SimEvent.ClockAdvance gap
            yield SimEvent.WorkerCrashed ] }
