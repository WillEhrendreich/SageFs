namespace SageFs.Simulation

open System
open SageFs.WarmupSupervision
open SageFs.Simulation.WarmupSim

/// Seeded, dependency-free generators for `WarmupSim` scenarios (mirrors
/// `StreamingProxyGenerators`). Chaos is data: `run (fromSeed n)` replays
/// identically forever.
module WarmupGenerators =

  let defaultBounds : Bounds =
    { Absolute = TimeSpan.FromMinutes 10.0
      Inactivity = TimeSpan.FromSeconds 30.0 }

  /// A general single-run scenario: a short, varied mix of clock advances,
  /// progress/silence ticks, faults, and possibly a stop. Same seed =>
  /// identical scenario, forever.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(1, 14)
    let events =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 7) with
          | 0 -> LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds (float (rnd.Next(1, 12))))
          | 1 -> LifecycleEvent.PollTick PollObservation.Progressed
          | 2 -> LifecycleEvent.PollTick PollObservation.StillWarming
          | 3 -> LifecycleEvent.PollTick (PollObservation.ProbeFailed "transient transport error")
          | 4 -> LifecycleEvent.StopRequested
          | 5 -> LifecycleEvent.PollTick (PollObservation.Faulted (Some (sprintf "seed-%d fault" seed)))
          | _ -> LifecycleEvent.PollTick (PollObservation.Ready (sprintf "seed-%d-loaded" seed)) ]
    { Seed = seed; Bounds = defaultBounds; Events = events }

  // ── Named minimal scenarios — the shapes named in the brief ────────────

  /// Warmup that never finishes: silence well past the inactivity bound,
  /// no Ready/Faulted ever observed. fcs-trial-a's exact shape (20+
  /// minutes, no error) if this were left unbounded — see WarmupSim's
  /// header for why the historical flat-bound-only code let this through.
  let warmupNeverFinishes : Scenario =
    { Seed = 1
      Bounds = defaultBounds
      Events =
        [ LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds 10.0)
          LifecycleEvent.PollTick PollObservation.StillWarming
          LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds 25.0)
          LifecycleEvent.PollTick PollObservation.StillWarming ] }

  /// Warmup that faults halfway: real progress, then a real fault — the
  /// worker itself decides, the bound never has to.
  let warmupFaultsHalfway : Scenario =
    { Seed = 2
      Bounds = defaultBounds
      Events =
        [ LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds 5.0)
          LifecycleEvent.PollTick PollObservation.Progressed
          LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds 5.0)
          LifecycleEvent.PollTick (PollObservation.Faulted (Some "Not all DLLs are found (1 missing)")) ] }

  /// A stop arriving WHILE starting — no observation has even happened yet.
  let stopWhileStarting : Scenario =
    { Seed = 3
      Bounds = defaultBounds
      Events = [ LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds 2.0); LifecycleEvent.StopRequested ] }

  /// A stop arriving AFTER a fault — the exact "stop_session hangs on a
  /// Faulted session" shape fcs-trial-b/c both hit.
  let stopAfterFault : Scenario =
    { Seed = 4
      Bounds = defaultBounds
      Events =
        [ LifecycleEvent.PollTick (PollObservation.Faulted (Some "worker crashed during warmup"))
          LifecycleEvent.StopRequested ] }

  /// Discovery of N projects, modelled as N Progressed ticks (one per
  /// project resolved), each separated by a clock advance well under the
  /// inactivity bound, then Ready. Proves a genuinely large discovery
  /// survives on progress alone, independent of N.
  let discoveryOfNProjects (n: int) : Scenario =
    let events =
      [ for _ in 1 .. n do
          yield LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds 2.0)
          yield LifecycleEvent.PollTick PollObservation.Progressed
        yield LifecycleEvent.PollTick (PollObservation.Ready (sprintf "%d-projects-loaded" n)) ]
    { Seed = 1000 + n; Bounds = defaultBounds; Events = events }

  let discoveryOf1Project = discoveryOfNProjects 1
  let discoveryOf10Projects = discoveryOfNProjects 10
  let discoveryOf100Projects = discoveryOfNProjects 100

  /// A status poll racing a fault: the fault observation and a stray extra
  /// poll tick (and a stray late Ready) land back-to-back with no clock
  /// advance between them — proves ordering within a tight window can't
  /// produce two different final answers depending on who asks when.
  let statusPollRacingFault : Scenario =
    { Seed = 5
      Bounds = defaultBounds
      Events =
        [ LifecycleEvent.PollTick (PollObservation.Faulted (Some "race"))
          LifecycleEvent.PollTick PollObservation.StillWarming
          LifecycleEvent.PollTick (PollObservation.Ready "late-ready") ] }
