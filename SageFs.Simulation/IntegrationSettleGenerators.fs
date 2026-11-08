namespace SageFs.Simulation

open System
open SageFs
open SageFs.Features.IntegrationSettle
open SageFs.Simulation.IntegrationSettleSim

/// Seeded generators for `IntegrationSettleSim` scenarios. Chaos is data: `fromSeed n` replays identically forever.
module IntegrationSettleGenerators =

  /// The stall window the daemon is configured with. Every claim the sims make is stated in windows, so its value changes
  /// nothing about them, and the time that passes is virtual.
  let window : TimeSpan = Timeouts.cohortIntegrationStallWindow

  /// The phase of the i-th step of a restart. The progress text names the step, so two steps never read the same: a restart's
  /// worker says something new at each step, and a phase that reads the same twice is the signal the gate looks for.
  let phaseOfStep (step: int) : Phase =
    match step % 4 with
    | 0 -> Phase.Restarting (Some (sprintf "step %d" step))
    | 1 -> Phase.Starting (Some (sprintf "loading project %d" step))
    | 2 -> Phase.Building ("dotnet build", Some (sprintf "compiling %d" step))
    | _ -> Phase.BuildInFlight

  /// `count` steps, each lasting `fraction` of the window.
  let steps (count: int) (fraction: float) : Segment list =
    [ for i in 0 .. count - 1 -> { Phase = phaseOfStep i; Lasts = TimeSpan.FromSeconds (window.TotalSeconds * fraction) } ]

  /// A general scenario: a seeded number of steps of seeded length, each strictly shorter than the window, then Ready.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let count = rnd.Next(1, 41)
    let script =
      [ for i in 0 .. count - 1 ->
          { Phase = phaseOfStep i
            Lasts = TimeSpan.FromSeconds (window.TotalSeconds * (0.1 + 0.8 * rnd.NextDouble())) } ]
    { Seed = seed; Window = window; Script = script; Ending = Ending.BecomesReady }

  /// A slow restart that never goes quiet: eighty steps, each at nine tenths of the window, then Ready. Well over an hour of
  /// virtual time, and the old fixed 120 s deadline would have cut it off at the second step.
  let slowButProgressing : Scenario =
    { Seed = 1; Window = window; Script = steps 80 0.9; Ending = Ending.BecomesReady }

  /// A restart that makes progress for a while and then stops saying anything: it hangs in a startup phase.
  let hangsAfter (progressingSteps: int) : Scenario =
    let hungPhase = Phase.Starting (Some "loading project 7 of 11")
    let hung = { Phase = hungPhase; Lasts = TimeSpan.FromSeconds (window.TotalSeconds * 0.5) }
    { Seed = 100 + progressingSteps
      Window = window
      Script = steps progressingSteps 0.9 @ [ hung ]
      Ending = Ending.Hangs }

  /// A rebuild of the session's project that runs for twenty windows with nothing to say, then finishes. Its bound is the build
  /// runner's, so the gate must not call it stuck.
  let longRebuild : Scenario =
    { Seed = 2
      Window = window
      Script = [ { Phase = Phase.BuildInFlight; Lasts = TimeSpan.FromSeconds (window.TotalSeconds * 20.0) } ]
      Ending = Ending.BecomesReady }

  /// A restart that makes progress and then the session manager answers with a fault.
  let faultsAfter (progressingSteps: int) : Scenario =
    { Seed = 200 + progressingSteps
      Window = window
      Script = steps progressingSteps 0.9
      Ending = Ending.Refuses "worker faulted while loading Fantomas" }
