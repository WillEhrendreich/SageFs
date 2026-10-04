namespace SageFs.Simulation

open SageFs.Build.TierPlan
open SageFs.Simulation.TierSched

/// Named invariants over a `TierSched` trace. Same shape as `LeaseSimInvariants`: the oracle only inspects what each
/// start recorded (the machine as the scheduler saw it, how many ran) and never re-derives the decision itself.
module TierSchedInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: Trace -> Outcome }

  let private firstOf (found: string option list) : Outcome =
    match found |> List.tryPick id with
    | Some message -> Outcome.Violated message
    | None -> Outcome.Holds

  /// never-admit-past-the-reserve: a start the machine had room for left the reserve free after the unit's own peak.
  /// Judged on the snapshot the scheduler saw, not on how memory moved afterwards: a neighbour may take memory at any
  /// moment, and no rule on a snapshot can promise otherwise. A start by the starvation guard is exempt by design.
  let neverAdmitPastReserve : Invariant =
    { Id = "never-admit-past-the-reserve"
      Description = "A start that was not the starvation guard's left MemoryReserveBytes available after the unit's peak."
      Check = fun trace ->
        trace.Starts
        |> List.map (fun s ->
          match s.Basis, s.Reading.Memory with
          | Fits, AvailableBytes available when available - s.PeakBytes < simLimits.MemoryReserveBytes ->
            Some (sprintf "%s started at %.0fs with %d bytes available for a peak of %d, under the %d reserve" s.Label s.At available s.PeakBytes simLimits.MemoryReserveBytes)
          | _ -> None)
        |> firstOf }

  /// never-over-the-cap: no more tier processes than the hard cap, however idle the machine looks.
  let neverOverCap : Invariant =
    { Id = "never-over-the-cap"
      Description = "No start finds the cap already reached."
      Check = fun trace ->
        trace.Starts
        |> List.map (fun s ->
          match s.RunningBefore >= simLimits.MaxTierProcesses with
          | true -> Some (sprintf "%s started at %.0fs with %d already running (cap %d)" s.Label s.At s.RunningBefore simLimits.MaxTierProcesses)
          | false -> None)
        |> firstOf }

  /// every-unit-starts-once: none starts twice, and when the run drained none was dropped.
  let everyUnitStartsOnce : Invariant =
    { Id = "every-unit-starts-once"
      Description = "Each unit of the scenario starts exactly once (a cut-off run may leave some unstarted, which run-ends reports)."
      Check = fun trace ->
        let counts = trace.Starts |> List.countBy (fun s -> s.Label) |> Map.ofList
        trace.Scenario.Units
        |> List.map (fun u ->
          let label = u.Candidate.Label
          match counts.TryFind label, trace.RunEnd with
          | Some n, _ when n > 1 -> Some (sprintf "%s started %d times" label n)
          | None, RunEnd.Drained _ -> Some (sprintf "%s never started, yet the run drained" label)
          | _ -> None)
        |> firstOf }

  /// the-run-ends: with units that die and neighbours that never leave, the line still drains before the cut-off.
  let runEnds : Invariant =
    { Id = "the-run-ends"
      Description = "The run drains before the cut-off, whatever the neighbours do."
      Check = fun trace ->
        match trace.RunEnd with
        | RunEnd.Drained _ -> Outcome.Holds
        | RunEnd.CutOff at ->
          let unstarted = trace.Scenario.Units.Length - (trace.Starts |> List.distinctBy (fun s -> s.Label) |> List.length)
          Outcome.Violated (sprintf "still going at %.0fs with %d unit(s) never started" at unstarted) }

  /// starts-in-order: the order the caller chose is the order units start in.
  let startsInOrder : Invariant =
    { Id = "starts-in-order"
      Description = "Units start in the order of the scenario's list."
      Check = fun trace ->
        let wanted = trace.Scenario.Units |> List.map (fun u -> u.Candidate.Label)
        let got = trace.Starts |> List.map (fun s -> s.Label) |> List.distinct
        match got = List.truncate got.Length wanted with
        | true -> Outcome.Holds
        | false -> Outcome.Violated (sprintf "started %A, wanted a prefix of %A" got wanted) }

  /// starts-are-settled: two starts are never closer than the settle time while anything of ours runs.
  let startsAreSettled : Invariant =
    { Id = "starts-are-settled"
      Description = "A start with units running is at least SettleSeconds after the previous start."
      Check = fun trace ->
        trace.Starts
        |> List.map (fun s ->
          match s.PreviousStart with
          | AdmittedAt previous when s.RunningBefore > 0 && s.At - previous < simLimits.SettleSeconds ->
            Some (sprintf "%s started %.1fs after the previous start (settle %.1fs)" s.Label (s.At - previous) simLimits.SettleSeconds)
          | _ -> None)
        |> firstOf }

  /// blind-means-fallback: with no pressure reading the scheduler never runs more than the static fallback.
  let blindMeansFallback : Invariant =
    { Id = "blind-means-fallback"
      Description = "A start with an unreadable pressure finds fewer than FallbackConcurrency running."
      Check = fun trace ->
        trace.Starts
        |> List.map (fun s ->
          match s.Reading.Pressure with
          | NotMeasured _ when s.RunningBefore >= simLimits.FallbackConcurrency ->
            Some (sprintf "%s started at %.0fs blind with %d running (fallback %d)" s.Label s.At s.RunningBefore simLimits.FallbackConcurrency)
          | _ -> None)
        |> firstOf }

  /// guard-only-when-idle: the starvation guard fires only with none of our units running.
  let guardOnlyWhenIdle : Invariant =
    { Id = "guard-only-when-idle"
      Description = "A start by the starvation guard finds nothing of ours running."
      Check = fun trace ->
        trace.Starts
        |> List.map (fun s ->
          match s.Basis with
          | StarvationGuard when s.RunningBefore > 0 -> Some (sprintf "%s started by the guard with %d running" s.Label s.RunningBefore)
          | _ -> None)
        |> firstOf }

  let all : Invariant list =
    [ neverAdmitPastReserve; neverOverCap; everyUnitStartsOnce; runEnds; startsInOrder; startsAreSettled; blindMeansFallback; guardOnlyWhenIdle ]

  /// Every invariant that does not hold for `trace`, as "id: message".
  let violations (trace: Trace) : string list =
    all
    |> List.choose (fun i ->
      match i.Check trace with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some (sprintf "%s: %s" i.Id message))
