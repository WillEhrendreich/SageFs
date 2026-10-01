namespace SageFs.Simulation

open SageFs.Features.LiveTesting
open SageFs.Simulation.BuildConfirmationSim

/// Named invariants over a drained `BuildConfirmationSim.Trace`. Same stable-id / `Holds`-vs-`Violated`
/// shape as the other simulations.
module BuildConfirmationInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private replay (t: Trace) = sprintf "seed=%d, ops=%A" t.Scenario.Seed t.Scenario.Ops

  let private isOutcomeOf (marks: Map<TestId, ResultProvenance>) : bool =
    marks
    |> Map.exists (fun _ p ->
      match p with
      | ResultProvenance.VerifiedByBuild
      | ResultProvenance.BuildDisagrees _ -> true
      | ResultProvenance.Compiled
      | ResultProvenance.Evaluated -> false)

  let private marksOf (step: Step) : (AnalysisIdentity * Map<TestId, ResultProvenance>) list =
    step.Effects
    |> List.choose (function
      | ConfirmationEffect.Mark (content, marks) -> Some (content, marks)
      | _ -> None)

  let private firstViolation (t: Trace) (check: Step -> string voption) : Outcome =
    t.Steps
    |> List.tryPick (fun s -> match check s with | ValueSome message -> Some message | ValueNone -> None)
    |> function
       | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
       | None -> Outcome.Holds

  /// no-stale-confirmation: a build's answer (verified, disagrees, failed, unanswered) is only ever
  /// applied to the newest content. An answer about older content would put a green or a red from
  /// the wrong text on a row that has since been edited.
  let noStaleConfirmation : Invariant =
    { Id = "no-stale-confirmation"
      Description = "A confirmation outcome is only ever marked for the newest content the simulation has produced."
      Check = fun t ->
        firstViolation t (fun s ->
          marksOf s
          |> List.tryPick (fun (content, marks) ->
            match isOutcomeOf marks && content <> contentId s.Latest with
            | true -> Some (sprintf "step %A marked an outcome for %A while the newest content was c%d" s.Op content s.Latest)
            | false -> None)
          |> function Some m -> ValueSome m | None -> ValueNone) }

  /// single-flight: at most one build is ever active, and a build only starts from a quiet window.
  let singleFlight : Invariant =
    { Id = "single-flight"
      Description = "A build starts only from the quiet window, and no step leaves two builds in flight."
      Check = fun t ->
        firstViolation t (fun s ->
          let starts = s.Effects |> List.filter (function ConfirmationEffect.StartBuild _ -> true | _ -> false) |> List.length
          let fromQuiet =
            match s.Before.Phase with
            | ConfirmationPhase.Quiet _ -> true
            | _ -> false
          match starts with
          | 0 -> ValueNone
          | 1 when fromQuiet || s.Event = ConfirmationEvent.QuietElapsed -> ValueNone
          | n -> ValueSome (sprintf "step %A started %d build(s) from phase %A" s.Op n s.Before.Phase)) }

  /// bursts-coalesce: a build starts only at the moment the editing went quiet. A run of evaluated
  /// runs and edits with no quiet window between them costs no build at all.
  let burstsCoalesce : Invariant =
    { Id = "bursts-coalesce"
      Description = "A build starts only in a step whose event is the quiet window ending."
      Check = fun t ->
        firstViolation t (fun s ->
          let started = s.Effects |> List.exists (function ConfirmationEffect.StartBuild _ -> true | _ -> false)
          match started, s.Event with
          | true, ConfirmationEvent.QuietElapsed -> ValueNone
          | true, _ -> ValueSome (sprintf "step %A started a build without waiting for quiet" s.Op)
          | false, _ -> ValueNone) }

  /// eval-never-delayed: the evaluated result is on the rows in the same step it arrived, whatever
  /// else is going on, and nothing is confirmed for a run that evaluated nothing.
  let evalNeverDelayed : Invariant =
    { Id = "eval-never-delayed"
      Description = "An evaluated run marks every one of its tests Evaluated in the step it arrives, and a run of no tests changes nothing."
      Check = fun t ->
        firstViolation t (fun s ->
          match s.Event with
          | ConfirmationEvent.EvaluatedRunFinished confirmation when Map.isEmpty confirmation.Evaluated ->
            match s.Effects, s.Before = s.After with
            | [], true -> ValueNone
            | _ -> ValueSome (sprintf "step %A confirmed a run of nothing: %A" s.Op s.Effects)
          | ConfirmationEvent.EvaluatedRunFinished confirmation ->
            let marked =
              marksOf s
              |> List.exists (fun (content, marks) ->
                content = confirmation.Content
                && confirmation.Evaluated |> Map.forall (fun id _ -> Map.tryFind id marks = Some ResultProvenance.Evaluated))
            match marked with
            | true -> ValueNone
            | false -> ValueSome (sprintf "step %A did not mark its tests Evaluated" s.Op)
          | _ -> ValueNone) }

  /// failure-is-loud: a build that fails, or that does not answer in time, ends in a mark that says
  /// so, never in silence. (Unless a newer content has already replaced it, in which case the
  /// generation is no longer the one in flight and nothing is owed.)
  let failureIsLoud : Invariant =
    { Id = "failure-is-loud"
      Description = "A failed or unanswered build of the content in flight marks BuildDisagrees with why."
      Check = fun t ->
        firstViolation t (fun s ->
          let activeBefore =
            match s.Before.Phase with
            | ConfirmationPhase.Building (_, g) | ConfirmationPhase.RunningBuilt (_, g) -> ValueSome g
            | _ -> ValueNone
          let loud (matches: ResultProvenance -> bool) =
            marksOf s |> List.exists (fun (_, marks) -> marks |> Map.exists (fun _ p -> matches p))
          match s.Event, activeBefore with
          | ConfirmationEvent.BuildFinished (g, Error _), ValueSome active when g = active ->
            match loud (function ResultProvenance.BuildDisagrees (BuildDisagreement.BuildFailed _) -> true | _ -> false) with
            | true -> ValueNone
            | false -> ValueSome (sprintf "step %A: the build of the content in flight failed and nothing was said" s.Op)
          | ConfirmationEvent.DeadlineReached g, ValueSome active when g = active ->
            match loud (function ResultProvenance.BuildDisagrees (BuildDisagreement.BuildUnanswered _) -> true | _ -> false) with
            | true -> ValueNone
            | false -> ValueSome (sprintf "step %A: the build of the content in flight never answered and nothing was said" s.Op)
          | _ -> ValueNone) }

  /// every-confirmation-resolves: once every wait has been drained, nothing is left pending, and the
  /// newest evaluated content ended in an outcome (the one it earned) unless a still newer edit replaced it.
  let everyConfirmationResolves : Invariant =
    { Id = "every-confirmation-resolves"
      Description = "After the quiet window and every deadline have fired, the machine is idle."
      Check = fun t ->
        match t.Final.Phase with
        | ConfirmationPhase.Idle -> Outcome.Holds
        | phase -> Outcome.Violated (sprintf "the machine is still %A after every wait was drained (%s)" phase (replay t)) }

  let all : Invariant list =
    [ noStaleConfirmation; singleFlight; burstsCoalesce; evalNeverDelayed; failureIsLoud; everyConfirmationResolves ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
