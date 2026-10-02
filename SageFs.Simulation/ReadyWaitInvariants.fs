namespace SageFs.Simulation

open SageFs
open SageFs.Simulation.ReadyWaitSim

/// Named invariants over a drained `ReadyWaitSim.Trace`. Same stable-id / `Holds`-vs-`Violated`
/// shape as the other simulations.
module ReadyWaitInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private replay (t: Trace) = sprintf "seed=%d, start=%A, ops=%A" t.Scenario.Seed t.Scenario.Start t.Scenario.Ops

  let private firstViolation (t: Trace) (check: Step -> string voption) : Outcome =
    t.Steps
    |> List.tryPick (fun s -> match check s with | ValueSome message -> Some message | ValueNone -> None)
    |> function
       | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
       | None -> Outcome.Holds

  let private isRebuilding (rebuild: LastRebuild) : bool =
    match rebuild with
    | LastRebuild.Latest (RebuildOutcome.InProgress _) -> true
    | LastRebuild.Latest _
    | LastRebuild.NeverRebuilt -> false

  /// NEVER-NOT-NEEDED-DURING-REBUILD: a caller who asks while a rebuild is running is never told
  /// "nothing to wait for", and nobody is told the session became Ready while the rebuild runs. A
  /// rebuild in progress is a state that is waited on.
  let neverNotNeededDuringRebuild : Invariant =
    { Id = "NEVER-NOT-NEEDED-DURING-REBUILD"
      Description = "While a rebuild is running no caller is answered NotNeeded or BecameReady: it is parked."
      Check = fun t ->
        firstViolation t (fun s ->
          match isRebuilding s.Rebuild with
          | false -> ValueNone
          | true ->
            s.Answered
            |> List.tryPick (fun (id, answer) ->
              match answer with
              | Answer.NotNeeded | Answer.BecameReady -> Some (sprintf "step %A answered caller %d %A while a rebuild was running" s.Op id answer)
              | Answer.Faulted | Answer.TimedOut -> None)
            |> function Some m -> ValueSome m | None -> ValueNone) }

  /// WAKES-ON-READY: the moment the session is Ready with no rebuild running, nobody is still parked.
  /// The answer comes from the step that made it true, not from a later look.
  let wakesOnReady : Invariant =
    { Id = "WAKES-ON-READY"
      Description = "After every step, if the session is Ready and no rebuild is running, no caller is parked."
      Check = fun t ->
        firstViolation t (fun s ->
          let ready =
            match ReadyWait.ofSession s.Status s.Rebuild with
            | ReadyWait.Verdict.Ready -> true
            | ReadyWait.Verdict.KeepParked
            | ReadyWait.Verdict.Failed _ -> false
          match ready, s.Parked with
          | true, parked when not (List.isEmpty parked) ->
            ValueSome (sprintf "step %A left %A parked on a session that is Ready with no rebuild running" s.Op parked)
          | _ -> ValueNone) }

  /// TIMEOUT-NAMED: a caller whose deadline passes while it is parked is told TimedOut, and once every
  /// deadline has passed nobody is left parked.
  let timeoutNamed : Invariant =
    { Id = "TIMEOUT-NAMED"
      Description = "A deadline that passes on a parked caller answers it TimedOut, and no caller is parked forever."
      Check = fun t ->
        let atDeadline =
          firstViolation t (fun s ->
            match s.Op with
            | Op.Deadline _ when not (List.isEmpty s.ParkedBefore) ->
              let timedOut = s.Answered |> List.filter (fun (_, a) -> a = Answer.TimedOut)
              match timedOut with
              | [ _ ] -> ValueNone
              | other -> ValueSome (sprintf "step %A answered %A, expected exactly one TimedOut" s.Op other)
            | _ -> ValueNone)
        match atDeadline, List.tryLast t.Steps with
        | Outcome.Violated _, _ -> atDeadline
        | Outcome.Holds, Some last when not (List.isEmpty last.Parked) ->
          Outcome.Violated (sprintf "after every deadline passed callers %A are still parked (%s)" last.Parked (replay t))
        | Outcome.Holds, _ -> Outcome.Holds }

  /// FAILED-REBUILD-NOT-READY: a caller parked through a rebuild that fails is told Faulted, never
  /// BecameReady from the worker that kept serving.
  let failedRebuildNotReady : Invariant =
    { Id = "FAILED-REBUILD-NOT-READY"
      Description = "When a rebuild fails, every caller parked through it is answered Faulted in that same step."
      Check = fun t ->
        firstViolation t (fun s ->
          match s.Op, s.Rebuild with
          | Op.BuildFails, LastRebuild.Latest (RebuildOutcome.FailedStillServing _) when isRebuilding s.RebuildBefore ->
            let answers = s.ParkedBefore |> List.map (fun id -> id, s.Answered |> List.tryFind (fun (a, _) -> a = id))
            answers
            |> List.tryPick (fun (id, answered) ->
              match answered with
              | Some (_, Answer.Faulted) -> None
              | Some (_, other) -> Some (sprintf "caller %d parked through a failed rebuild was answered %A" id other)
              | None -> Some (sprintf "caller %d parked through a failed rebuild was not answered" id))
            |> function Some m -> ValueSome m | None -> ValueNone
          | _ -> ValueNone) }

  let all : Invariant list =
    [ neverNotNeededDuringRebuild; wakesOnReady; timeoutNamed; failedRebuildNotReady ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
