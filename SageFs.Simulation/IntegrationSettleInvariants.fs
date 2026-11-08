namespace SageFs.Simulation

open System
open SageFs.Features.IntegrationSettle
open SageFs.Simulation.IntegrationSettleSim

/// Named invariants over a drained `IntegrationSettleSim.Trace`, in the shape of `WarmupInvariants`: a stable id, a sentence, and
/// a check whose antecedent is part of the check, so an invariant that does not apply to a trace holds trivially and says so
/// through `applies`.
module IntegrationSettleInvariants =

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

  let isStartupPhase (phase: Phase) : bool =
    match phase with
    | Phase.Restarting _
    | Phase.Starting _
    | Phase.Building _ -> true
    | Phase.BuildInFlight
    | Phase.Ready
    | Phase.Faulted _
    | Phase.HostCrashed
    | Phase.Stopped
    | Phase.Missing -> false

  let lastSegment (scenario: Scenario) : Segment option = scenario.Script |> List.tryLast

  /// Every step shorter than the window and a Ready at the end: this is a restart that is working. It must settle, however long
  /// it takes in all. This is the claim the fixed deadline broke.
  let progressIsNeverCut : Invariant =
    { Id = "progress-is-never-cut"
      Description = "A restart that changes phase within every window and ends Ready settles, however long it takes in total."
      Applies = fun t -> t.Scenario.Ending = Ending.BecomesReady && t.Scenario.Script |> List.forall (fun s -> s.Lasts < t.Scenario.Window)
      Check = fun t ->
        match t.Outcome with
        | Outcome.Settled -> Standing.Holds
        | other -> Standing.Violated (sprintf "a restart that was making progress ended as %A after %.0f s of virtual time" other t.EndedAt.TotalSeconds) }

  /// A hung worker in a startup phase is reported with the phase it hung in, within two windows of the moment it hung.
  let aStallIsCaughtWithItsPhase : Invariant =
    { Id = "a-stall-is-caught-with-its-phase"
      Description = "A worker that hangs in a startup phase ends the wait as StuckIn that phase, within two windows of the hang."
      Applies = fun t ->
        t.Scenario.Ending = Ending.Hangs
        && (match lastSegment t.Scenario with
            | Some segment -> isStartupPhase segment.Phase
            | None -> false)
      Check = fun t ->
        match lastSegment t.Scenario with
        | None -> Standing.Holds
        | Some last ->
          let hungAt = scriptLength t.Scenario - last.Lasts
          match t.Outcome with
          | Outcome.StuckIn (phase, _) when phase = last.Phase ->
            match t.EndedAt <= hungAt + t.Scenario.Window + t.Scenario.Window with
            | true -> Standing.Holds
            | false -> Standing.Violated (sprintf "stuck was reported %.0f s after the hang, more than two windows" (t.EndedAt - hungAt).TotalSeconds)
          | other -> Standing.Violated (sprintf "a worker hung in %A and the wait ended as %A" last.Phase other) }

  /// The session manager's own refusal reaches the caller word for word, whatever the phase history.
  let aRefusalPassesThroughVerbatim : Invariant =
    { Id = "a-refusal-passes-through-verbatim"
      Description = "When the session manager answers a parked caller with a refusal, the wait ends with that reason unchanged."
      Applies = fun t -> match t.Scenario.Ending with Ending.Refuses _ -> true | _ -> false
      Check = fun t ->
        match t.Scenario.Ending, t.Outcome with
        | Ending.Refuses expected, Outcome.Refused actual when actual = expected -> Standing.Holds
        | _, other -> Standing.Violated (sprintf "a refusal was reported as %A" other) }

  /// A rebuild's bound belongs to the build runner, so a long quiet rebuild is not a stall.
  let aLongRebuildIsNotAStall : Invariant =
    { Id = "a-long-rebuild-is-not-a-stall"
      Description = "A rebuild in flight for any number of windows that then finishes settles: the window does not judge it."
      Applies = fun t ->
        t.Scenario.Ending = Ending.BecomesReady
        && t.Scenario.Script |> List.exists (fun s -> s.Phase = Phase.BuildInFlight && s.Lasts >= t.Scenario.Window + t.Scenario.Window)
      Check = fun t ->
        match t.Outcome with
        | Outcome.Settled -> Standing.Holds
        | other -> Standing.Violated (sprintf "a long rebuild ended as %A" other) }

  let all : Invariant list =
    [ progressIsNeverCut; aStallIsCaughtWithItsPhase; aRefusalPassesThroughVerbatim; aLongRebuildIsNotAStall ]

  /// The invariants a trace violates, by id and message. A trace an invariant does not apply to is not a violation of it.
  let violations (trace: Trace) : (string * string) list =
    all
    |> List.collect (fun invariant ->
      match invariant.Applies trace, invariant.Check trace with
      | true, Standing.Violated message -> [ (invariant.Id, message) ]
      | _ -> [])
