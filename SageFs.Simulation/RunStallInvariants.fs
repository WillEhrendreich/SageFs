namespace SageFs.Simulation

open System
open SageFs.Features.LiveTesting.RunStall
open SageFs.Simulation.RunStallSim

/// Named invariants over a drained `RunStallSim.Trace`, in the shape of `IntegrationSettleInvariants`: a stable id, a sentence,
/// and a check whose antecedent is part of the invariant, so one that does not apply to a trace holds trivially and says so
/// through `applies`.
module RunStallInvariants =

  [<RequireQualifiedAccess>]
  type Standing =
    | Holds
    | Violated of message: string

  type Invariant = {
    Id: string
    Description: string
    /// Whether this trace is the kind the invariant is about. A battery that never makes this true proves nothing.
    Applies: Trace -> bool
    Check: Trace -> Standing
  }

  /// The gaps of a run's life, from the moment it started: to the first report, between reports, and from the last report to
  /// the completion when there is one.
  let gaps (scenario: Scenario) : TimeSpan list =
    let times = scenario.StartsAt :: scenario.Reports
    let between = List.pairwise times |> List.map (fun (earlier, later) -> later - earlier)
    match scenario.Ending with
    | Ending.Finishes after -> between @ [ after ]
    | Ending.Hangs -> between

  let startedInTime (scenario: Scenario) : bool = scenario.StartsAt < scenario.Window

  let lastEventAt (scenario: Scenario) : TimeSpan =
    scenario.Reports |> List.tryLast |> Option.defaultValue scenario.StartsAt

  /// A run that reports inside every window and finishes is never ended early, however long it takes in all. This is the claim
  /// the wall-clock cap broke.
  let aRunThatKeepsReportingIsNeverCut : Invariant =
    { Id = "a-run-that-keeps-reporting-is-never-cut"
      Description = "A run that starts inside a window, reports inside every window and then finishes ends as Finished, however long it takes in total."
      Applies = fun t ->
        (match t.Scenario.Ending with Ending.Finishes _ -> true | Ending.Hangs -> false)
        && startedInTime t.Scenario
        && gaps t.Scenario |> List.forall (fun gap -> gap < t.Scenario.Window)
      Check = fun t ->
        match t.Outcome with
        | Outcome.Finished -> Standing.Holds
        | other -> Standing.Violated (sprintf "a run that kept reporting ended as %A after %.0f s of virtual time" other t.EndedAt.TotalSeconds) }

  /// A run that reports inside every window and then goes quiet is called stalled within two windows of its last report, naming
  /// how many reported and how many did not.
  let aStallIsCaughtWithItsCount : Invariant =
    { Id = "a-stall-is-caught-with-its-count"
      Description = "A started run that reports and then goes quiet ends as StoppedReporting with the count that reported, within two windows of its last report."
      Applies = fun t ->
        t.Scenario.Ending = Ending.Hangs
        && startedInTime t.Scenario
        && gaps t.Scenario |> List.forall (fun gap -> gap < t.Scenario.Window)
      Check = fun t ->
        let reported = t.Scenario.Reports.Length
        let expected = StallCause.StoppedReporting (reported, t.Scenario.Requested - reported)
        match t.Outcome with
        | Outcome.Stalled (cause, _) when cause = expected ->
          match t.EndedAt <= lastEventAt t.Scenario + t.Scenario.Window + t.Scenario.Window with
          | true -> Standing.Holds
          | false -> Standing.Violated (sprintf "the stall was reported %.0f s after the last report, more than two windows" (t.EndedAt - lastEventAt t.Scenario).TotalSeconds)
        | other -> Standing.Violated (sprintf "a run that reported %d of %d and went quiet ended as %A" reported t.Scenario.Requested other) }

  /// A run that never starts is called stalled before it starts, within two windows of being asked for.
  let aRunThatNeverStartsIsNamed : Invariant =
    { Id = "a-run-that-never-starts-is-named"
      Description = "A run that has not started two windows after it was asked for ends as NeverStarted, within two windows."
      Applies = fun t -> t.Scenario.StartsAt > t.Scenario.Window + t.Scenario.Window
      Check = fun t ->
        match t.Outcome with
        | Outcome.Stalled (StallCause.NeverStarted, _) when t.EndedAt <= t.Scenario.Window + t.Scenario.Window -> Standing.Holds
        | other -> Standing.Violated (sprintf "a run that had not started ended as %A at %.0f s" other t.EndedAt.TotalSeconds) }

  /// FAIL-CLOSED. A stall names exactly how many tests reported at the moment it ended, and every other requested test did not.
  /// The landing reads the rest as failing, so the count must be the count.
  let aStallNamesTheCountThatReported : Invariant =
    { Id = "a-stall-names-the-count-that-reported"
      Description = "A stall carries the count of tests that had reported when it ended, and the unreported are the rest of the requested."
      Applies = fun t -> match t.Outcome with Outcome.Stalled (StallCause.StoppedReporting _, _) -> true | _ -> false
      Check = fun t ->
        match t.Outcome with
        | Outcome.Stalled (StallCause.StoppedReporting (reported, unreported), _) ->
          match reported = reportedBy t.Scenario t.EndedAt, reported + unreported = t.Scenario.Requested with
          | true, true -> Standing.Holds
          | _ -> Standing.Violated (sprintf "the stall said %d reported and %d not, but %d had reported of %d requested" reported unreported (reportedBy t.Scenario t.EndedAt) t.Scenario.Requested)
        | _ -> Standing.Holds }

  /// A run is never ended while its count rises: when the stall is called the count is what it was a whole window before.
  let aRunIsNeverEndedWhileItsCountRises : Invariant =
    { Id = "a-run-is-never-ended-while-its-count-rises"
      Description = "When a run ends as stalled, no test reported in the window before it ended."
      Applies = fun t -> match t.Outcome with Outcome.Stalled (StallCause.StoppedReporting _, _) -> true | _ -> false
      Check = fun t ->
        let now = reportedBy t.Scenario t.EndedAt
        let before = reportedBy t.Scenario (t.EndedAt - t.Scenario.Window)
        match now = before with
        | true -> Standing.Holds
        | false -> Standing.Violated (sprintf "the run was called stalled at %.0f s with %d reported, but %d had reported a window earlier" t.EndedAt.TotalSeconds now before) }

  let all : Invariant list =
    [ aRunThatKeepsReportingIsNeverCut
      aStallIsCaughtWithItsCount
      aRunThatNeverStartsIsNamed
      aStallNamesTheCountThatReported
      aRunIsNeverEndedWhileItsCountRises ]

  /// The invariants a trace violates, by id and message. A trace an invariant does not apply to is not a violation of it.
  let violations (trace: Trace) : (string * string) list =
    all
    |> List.collect (fun invariant ->
      match invariant.Applies trace, invariant.Check trace with
      | true, Standing.Violated message -> [ (invariant.Id, message) ]
      | _ -> [])
