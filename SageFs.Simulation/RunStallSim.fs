namespace SageFs.Simulation

open System
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTesting.RunStall

/// Deterministic simulation of the wait on a test run (`SageFs.Core/Features/RunStall.fs`), the fix for the cap that was in two
/// places: the worker cancelled a run two minutes after it started, and the landing gate gave up two and a half minutes after it
/// asked. One cohort run asked for 19067 tests and 18529 never reported, because the clock ended a run that was still reporting.
///
/// THE SUBJECT IS THE REAL CODE. `run` calls `RunStall.await`, the loop both the worker and the landing gate use, with ports that
/// read a seeded script of when tests report, on a virtual clock. Nothing sleeps and nothing reads the wall clock, so a run of
/// hours of virtual time costs microseconds and the same seed replays the same run forever.
///
/// THE TWIN is the code this replaced: one park with a fixed deadline from the start of the run, no look at the count. It
/// runs over the same ports, so an invariant is shown to separate the fix from the bug and not to hold vacuously.
module RunStallSim =

  /// How the script ends once every report in it has happened.
  [<RequireQualifiedAccess>]
  type Ending =
    /// The run's completion arrives `after` the last report.
    | Finishes of after: TimeSpan
    /// Nothing more is said and no completion arrives: a hung test, a dead worker.
    | Hangs

  type Scenario = {
    Seed: int
    /// The stall window the run is judged by.
    Window: TimeSpan
    /// How many tests were requested.
    Requested: int
    /// When the run starts, from the moment it was asked for.
    StartsAt: TimeSpan
    /// When each report arrives, ascending, from the moment it was asked for. At most `Requested` of them.
    Reports: TimeSpan list
    Ending: Ending
  }

  type Trace = {
    Scenario: Scenario
    Outcome: Outcome
    /// Virtual time at which the wait ended.
    EndedAt: TimeSpan
    /// How many times the run was looked at.
    Looks: int
    /// Which implementation produced it.
    Reducer: string
  }

  /// When the script's completion arrives, if it does.
  let finishAt (scenario: Scenario) : TimeSpan option =
    match scenario.Ending with
    | Ending.Hangs -> None
    | Ending.Finishes after ->
      let last = scenario.Reports |> List.tryLast |> Option.defaultValue scenario.StartsAt
      Some (max last scenario.StartsAt + after)

  /// How many tests have reported by virtual time `at`.
  let reportedBy (scenario: Scenario) (at: TimeSpan) : int =
    scenario.Reports |> List.filter (fun time -> time <= at) |> List.length

  /// What a look at virtual time `at` sees.
  let lookAt (scenario: Scenario) (at: TimeSpan) : Look =
    match finishAt scenario with
    | Some finished when finished <= at -> Look.Finished
    | _ ->
      match at < scenario.StartsAt with
      | true -> Look.NotStarted
      | false -> Look.Reporting (reportedBy scenario at, scenario.Requested)

  type Clock() =
    member val Now = TimeSpan.Zero with get, set
    member val Looks = 0 with get, set

  /// The ports over the script: parking for a slice moves the virtual clock to the end of the slice, or to the moment the run
  /// finishes if that comes first.
  let portsFor (scenario: Scenario) (clock: Clock) : Ports =
    { Park =
        fun slice ->
          async {
            let sliceEnd = clock.Now + slice
            match finishAt scenario with
            | Some finished when finished <= sliceEnd ->
              clock.Now <- max clock.Now finished
              return true
            | _ ->
              clock.Now <- sliceEnd
              return false
          }
      Observe =
        fun () ->
          async {
            clock.Looks <- clock.Looks + 1
            return lookAt scenario clock.Now
          } }

  /// Run through the REAL wait.
  let run (scenario: Scenario) : Trace =
    let clock = Clock()
    let outcome = RunStall.await scenario.Window (portsFor scenario clock) |> Async.RunSynchronously
    { Scenario = scenario
      Outcome = outcome
      EndedAt = clock.Now
      Looks = clock.Looks
      Reducer = "real (RunStall.await: slices of the stall window, judged by the count that reported)" }

  // ---------------------------------------------------------------------
  // TWIN: one park with a fixed deadline from the start and no look at the count.
  // ---------------------------------------------------------------------

  /// The historical shape: park once for `deadline`; if the run is not finished by then it is cut, whatever it was reporting.
  /// The cut carries the count that had reported at that moment, as the old cancel left it.
  let runFixedDeadline (deadline: TimeSpan) (scenario: Scenario) : Trace =
    let clock = Clock()
    let ports = portsFor scenario clock
    let outcome =
      async {
        match! ports.Park deadline with
        | true -> return Outcome.Finished
        | false ->
          let reported = reportedBy scenario clock.Now
          return Outcome.Stalled (StallCause.StoppedReporting (reported, scenario.Requested - reported), deadline)
      }
      |> Async.RunSynchronously
    { Scenario = scenario
      Outcome = outcome
      EndedAt = clock.Now
      Looks = clock.Looks
      Reducer = sprintf "twin-fixed-deadline (%.0f s from the start, no look at the count)" deadline.TotalSeconds }
