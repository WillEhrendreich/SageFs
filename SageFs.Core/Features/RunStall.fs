namespace SageFs.Features.LiveTesting

open System
open System.Threading
open System.Threading.Tasks

/// When a test run is over because it stopped reporting, and when it is not over because it is slow.
///
/// A run used to end at a wall clock: the worker cancelled it `Timeouts.globalTestRun` (2 minutes) after it started, and the
/// cohort landing gate gave up `globalTestRun + 30 s` after it asked. One cohort run asked for 19067 tests and 18529 of them
/// never reported, because the run was cut at the clock while it was still reporting. The cap was in two places, so a fix to one
/// left the other cutting the run.
///
/// The rule here: a run ends when it finishes, or when it has stopped changing. The wait parks in slices of one stall window. At
/// the end of a slice it counts the requested tests that have reported, and the SAME count at two looks in a row is a stall. A
/// higher count is progress, so the wait goes on for another slice, however many slices that takes. It is the rule
/// `IntegrationSettle` already applies to the wait on the integration session; this module applies it to the run itself.
///
/// `judge` is pure over the looks. `await` is the loop around it with its two ports injected, so the simulation in
/// SageFs.Simulation folds the real loop through virtual time. `watchWith` is the loop around a real run: the worker uses it to
/// cancel a run that stopped reporting, and it names the count of tests that did not report. This file reads only the BCL.
module RunStall =

  // -- What a look at the run says --

  /// What one look at a requested run sees.
  [<RequireQualifiedAccess>]
  type Look =
    /// The run was asked for and has not started.
    | NotStarted
    /// The run is going: `reported` of the `requested` tests have a result of this run's own.
    | Reporting of reported: int * requested: int
    /// The run ended: its completion arrived.
    | Finished

  /// Why a run was called stalled.
  [<RequireQualifiedAccess>]
  type StallCause =
    /// Asked for, and the same not-started look a window later.
    | NeverStarted
    /// Started, and the same count of reports a window later. `unreported` is the requested tests that have no result.
    | StoppedReporting of reported: int * unreported: int

  [<RequireQualifiedAccess>]
  type Verdict =
    | Finished
    /// Moving, or not yet looked at twice: wait another slice.
    | KeepWaiting
    | Stalled of cause: StallCause

  /// What to make of the look seen now, given the look at the previous slice (none on the first look). Exhaustive on purpose: a
  /// new look must say which of the three it is.
  let judge (previous: Look option) (seen: Look) : Verdict =
    match seen with
    | Look.Finished -> Verdict.Finished
    | Look.NotStarted ->
      match previous with
      | Some Look.NotStarted -> Verdict.Stalled StallCause.NeverStarted
      | Some _
      | None -> Verdict.KeepWaiting
    | Look.Reporting (reported, requested) ->
      match previous with
      | Some (Look.Reporting (before, _)) when reported <= before ->
        Verdict.Stalled (StallCause.StoppedReporting (reported, max 0 (requested - reported)))
      | Some _
      | None -> Verdict.KeepWaiting

  /// The window a run is judged by: the configured stall window, and never less than twice the per-test timeout. A test may take
  /// the whole per-test timeout to report, so a window under that would call a run stalled while it is still working.
  let effectiveWindow (stallWindow: TimeSpan) (perTest: TimeSpan) : TimeSpan =
    max stallWindow (perTest + perTest)

  // -- The wait --

  /// What the wait is given: how to park for one slice, and how to look at the run.
  type Ports = {
    /// Park for at most one slice. `true` when the run finished while parked, so the wait ends the moment it does.
    Park: TimeSpan -> Async<bool>
    Observe: unit -> Async<Look>
  }

  [<RequireQualifiedAccess>]
  type Outcome =
    | Finished
    | Stalled of cause: StallCause * window: TimeSpan

  /// Park in slices of `window` and judge the run at the end of each. There is no deadline: the loop ends only through `judge`
  /// or a finish, and a finish arrives the instant it happens, because the slice is a park and not a poll.
  let await (window: TimeSpan) (ports: Ports) : Async<Outcome> =
    let rec loop (previous: Look option) : Async<Outcome> =
      async {
        match! ports.Park window with
        | true -> return Outcome.Finished
        | false ->
          let! seen = ports.Observe ()
          match judge previous seen with
          | Verdict.Finished -> return Outcome.Finished
          | Verdict.Stalled cause -> return Outcome.Stalled (cause, window)
          | Verdict.KeepWaiting -> return! loop (Some seen)
      }
    loop None

  // -- What is said about a stall --

  /// The stall in words, naming the count of tests that did not report.
  let describeCause (window: TimeSpan) (cause: StallCause) : string =
    match cause with
    | StallCause.NeverStarted ->
      sprintf "the run did not start within %.0f s of being asked for" window.TotalSeconds
    | StallCause.StoppedReporting (reported, 0) ->
      sprintf "all %d tests had reported and the run did not end within %.0f s" reported window.TotalSeconds
    | StallCause.StoppedReporting (reported, unreported) ->
      sprintf "no test reported for %.0f s: %d reported and %d did not" window.TotalSeconds reported unreported

  /// The tests that never reported, when the cause says how many. A run that never started has none to count here.
  let unreportedOf (cause: StallCause) : int option =
    match cause with
    | StallCause.NeverStarted -> None
    | StallCause.StoppedReporting (_, unreported) -> Some unreported

  // -- A real run under the watch --

  [<RequireQualifiedAccess>]
  type RunEnd =
    /// The work returned.
    | Completed
    /// The run stopped reporting and was cancelled.
    | Stalled of cause: StallCause * window: TimeSpan
    /// The caller's own token ended it.
    | Cancelled

  /// Run `work` under the stall watch. `work` is given a function to call as each requested test reports, and a token that
  /// the watch cancels when the run stalls. `sleep` parks for one slice (`Task.Delay` in production; a test hands in a task it
  /// completes itself, so no test waits on a clock). The work is left to finish what is in flight after the cancel: each test
  /// has its own timeout, so that is bounded.
  let watchWith
    (sleep: TimeSpan -> CancellationToken -> Task)
    (window: TimeSpan)
    (requested: int)
    (ct: CancellationToken)
    (work: (unit -> unit) -> CancellationToken -> Async<unit>)
    : Async<RunEnd> =
    async {
      use stop = CancellationTokenSource.CreateLinkedTokenSource ct
      let counter = [| 0 |]
      let note () = Interlocked.Increment(&counter.[0]) |> ignore
      let workTask : Task = Async.StartAsTask (work note stop.Token) :> Task
      let park (slice: TimeSpan) : Async<bool> =
        async {
          use parked = new CancellationTokenSource()
          let! winner = Task.WhenAny(workTask, sleep slice parked.Token) |> Async.AwaitTask
          parked.Cancel()
          return Object.ReferenceEquals(winner, workTask)
        }
      let observe () : Async<Look> =
        async {
          match workTask.IsCompleted with
          | true -> return Look.Finished
          | false -> return Look.Reporting (Volatile.Read(&counter.[0]), requested)
        }
      let! outcome = await window { Park = park; Observe = observe }
      // Awaited inside a `task`, where a cancelled work task is a catchable exception. `Async.AwaitTask` on a cancelled task
      // cancels the surrounding async instead, which nothing can catch.
      let ended : Task<bool> =
        task {
          try
            do! workTask
            return true
          with :? OperationCanceledException ->
            return false
        }
      match outcome with
      | Outcome.Stalled (cause, stalledWindow) ->
        stop.Cancel()
        let! _ = Async.AwaitTask ended
        return RunEnd.Stalled (cause, stalledWindow)
      | Outcome.Finished ->
        match! Async.AwaitTask ended with
        | true -> return RunEnd.Completed
        | false -> return RunEnd.Cancelled
    }

  /// `watchWith` over the real clock.
  let watch (window: TimeSpan) (requested: int) (ct: CancellationToken) (work: (unit -> unit) -> CancellationToken -> Async<unit>) : Async<RunEnd> =
    watchWith (fun slice token -> Task.Delay(slice, token)) window requested ct work
