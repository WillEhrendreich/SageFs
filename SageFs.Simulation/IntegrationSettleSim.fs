namespace SageFs.Simulation

open System
open SageFs.Features
open SageFs.Features.IntegrationSettle

/// Deterministic simulation of the landing gate's wait on the integration session (`SageFs.Core/Features/IntegrationSettle.fs`),
/// the fix for finding C-restart of the first cohort run: a landing was Blocked Inconclusive because the gate parked on the
/// session's ready signal for a fixed 120 s while the restart of the integration session (it loads the whole test project) was
/// still making progress on a machine shared by eighteen builds.
///
/// THE SUBJECT IS THE REAL CODE. `run` calls `IntegrationSettle.await`, the loop the daemon uses, with ports that read a seeded
/// script of what the session does and when, on a virtual clock. Nothing sleeps and nothing reads the wall clock, so a restart
/// that takes an hour of virtual time runs in microseconds, and the same seed replays the same run forever.
///
/// THE TWIN is the code this replaced: one park with a fixed deadline, no look at the phase. It runs over the same ports, so an
/// invariant can be shown to separate the fix from the bug it replaced and not just hold vacuously.
module IntegrationSettleSim =

  /// One stretch of the session's life: the phase it reads as, and for how long.
  type Segment = { Phase: Phase; Lasts: TimeSpan }

  /// What happens when the script has run out.
  [<RequireQualifiedAccess>]
  type Ending =
    /// The session becomes Ready and the session manager says so.
    | BecomesReady
    /// The session manager answers every parked caller with this refusal (a fault, a failed rebuild).
    | Refuses of reason: string
    /// The last phase goes on forever and nothing is ever said: a hung worker.
    | Hangs

  type Scenario = {
    Seed: int
    /// The stall window the gate is configured with.
    Window: TimeSpan
    Script: Segment list
    Ending: Ending
  }

  type Trace = {
    Scenario: Scenario
    Outcome: Outcome
    /// Virtual time at which the wait ended.
    EndedAt: TimeSpan
    /// How many times the phase was read.
    Looks: int
    /// Which implementation produced it.
    Reducer: string
  }

  /// When the script's segments end (the moment the ending takes effect).
  let scriptLength (scenario: Scenario) : TimeSpan =
    scenario.Script |> List.fold (fun total segment -> total + segment.Lasts) TimeSpan.Zero

  /// The phase the session reads as at virtual time `at`.
  let phaseAt (scenario: Scenario) (at: TimeSpan) : Phase =
    let rec walk (started: TimeSpan) (remaining: Segment list) (last: Phase option) : Phase =
      match remaining with
      | [] ->
        match scenario.Ending with
        | Ending.BecomesReady -> Phase.Ready
        | Ending.Refuses reason -> Phase.Faulted reason
        | Ending.Hangs -> last |> Option.defaultValue (Phase.Starting WorkerSaid.Nothing)
      | segment :: rest ->
        match at < started + segment.Lasts with
        | true -> segment.Phase
        | false -> walk (started + segment.Lasts) rest (Some segment.Phase)
    walk TimeSpan.Zero scenario.Script None

  type Clock() =
    member val Now = TimeSpan.Zero with get, set
    member val Looks = 0 with get, set

  /// The ports over the script: parking for a slice moves the virtual clock to the end of the slice, or to the moment the
  /// ending takes effect if that comes first.
  let portsFor (scenario: Scenario) (clock: Clock) : Ports =
    let endsAt = scriptLength scenario
    { AwaitReady =
        fun slice ->
          async {
            let sliceEnd = clock.Now + slice
            match scenario.Ending, endsAt <= sliceEnd with
            | Ending.BecomesReady, true ->
              clock.Now <- max clock.Now endsAt
              return Wake.Ready
            | Ending.Refuses reason, true ->
              clock.Now <- max clock.Now endsAt
              return Wake.Refused reason
            | _ ->
              clock.Now <- sliceEnd
              return Wake.SliceElapsed
          }
      Observe =
        fun () ->
          async {
            clock.Looks <- clock.Looks + 1
            return phaseAt scenario clock.Now
          } }

  /// Run through the REAL wait.
  let run (scenario: Scenario) : Trace =
    let clock = Clock()
    let outcome = IntegrationSettle.await scenario.Window (portsFor scenario clock) |> Async.RunSynchronously
    { Scenario = scenario
      Outcome = outcome
      EndedAt = clock.Now
      Looks = clock.Looks
      Reducer = "real (IntegrationSettle.await: slices of the stall window, judged by phase)" }

  // ---------------------------------------------------------------------
  // TWIN: one park with a fixed deadline and no look at the phase.
  // ---------------------------------------------------------------------

  /// The historical shape: park once for `deadline`; if the slice runs out, the session "timed out", whatever it was doing.
  let runFixedDeadline (deadline: TimeSpan) (scenario: Scenario) : Trace =
    let clock = Clock()
    let ports = portsFor scenario clock
    let outcome =
      async {
        match! ports.AwaitReady deadline with
        | Wake.Ready -> return Outcome.Settled
        | Wake.Refused reason -> return Outcome.Refused reason
        | Wake.SliceElapsed ->
          return Outcome.Refused (sprintf "timed out during restart after %.1fs" deadline.TotalSeconds)
      }
      |> Async.RunSynchronously
    { Scenario = scenario
      Outcome = outcome
      EndedAt = clock.Now
      Looks = clock.Looks
      Reducer = sprintf "twin-fixed-deadline (%.0fs, no look at the phase)" deadline.TotalSeconds }
