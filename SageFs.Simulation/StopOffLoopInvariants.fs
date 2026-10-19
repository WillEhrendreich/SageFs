namespace SageFs.Simulation

open SageFs.Simulation.StopOffLoopSim

/// Named invariants over a `StopOffLoopSim.Trace`, whichever subject produced it (the model, or the real mailbox).
/// The oracle reads only what the scenario posted and what came back; it never asks the subject how it decided.
module StopOffLoopInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private firstViolation (t: Trace) (violates: int -> Step -> string option) : Outcome =
    t.Steps
    |> List.mapi violates
    |> List.tryPick id
    |> function
       | Some message -> Outcome.Violated(sprintf "%s (seed=%d, ops=%A)" message t.Scenario.Seed t.Scenario.Ops)
       | None -> Outcome.Holds

  let private isCommand (s: Step) : bool =
    match s.Op with
    | Op.Release _ -> false
    | Op.Stop _ | Op.List | Op.Get _ | Op.Create | Op.Restart _ -> true

  /// a-busy-stop-never-delays-an-answer: while any worker is still leaving, every command (a list, a get, a create,
  /// a restart, another stop) is answered in the step it was posted in, not after the slow stop.
  let aBusyStopNeverDelaysAnAnswer : Invariant =
    { Id = "a-busy-stop-never-delays-an-answer"
      Description = "A command posted while a stopped session's worker is still leaving is answered at once."
      Check = fun t ->
        firstViolation t (fun i s ->
          match isCommand s && not (Set.isEmpty s.InFlightBefore) && s.Reply = Reply.Pending with
          | false -> None
          | true -> Some(sprintf "step #%d (%A) was left pending while workers %A were still leaving" i s.Op (Set.toList s.InFlightBefore))) }

  /// a-stop-is-answered-at-once: a stop is answered in the step it was posted in, whatever else is leaving.
  let aStopIsAnsweredAtOnce : Invariant =
    { Id = "a-stop-is-answered-at-once"
      Description = "A stop of a registered session is answered without waiting for its own worker to leave."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.Op, s.Reply with
          | Op.Stop session, Reply.Pending -> Some(sprintf "step #%d: the stop of session %d waited for its worker" i session)
          | _ -> None) }

  /// stops-of-different-sessions-overlap: a stop begun while another worker is still leaving leaves both leaving
  /// at once. Serialised stops are the 4 x 5 s = 22 s queue.
  let stopsOfDifferentSessionsOverlap : Invariant =
    { Id = "stops-of-different-sessions-overlap"
      Description = "A stop begun while another is in flight leaves both workers leaving together."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.Op with
          | Op.Stop session when not (Set.isEmpty s.InFlightBefore) && not (Set.contains session s.InFlightBefore) ->
            let together = Set.union s.InFlightBefore (Set.singleton session)
            match Set.isSubset together s.InFlightAfter with
            | true -> None
            | false -> Some(sprintf "step #%d: stop of %d began while %A were leaving but only %A were leaving after" i session (Set.toList s.InFlightBefore) (Set.toList s.InFlightAfter))
          | _ -> None) }

  /// the-registry-tells-the-truth: an answered list names every session created and not yet stopped, and no stopped one.
  let theRegistryTellsTheTruth : Invariant =
    { Id = "the-registry-tells-the-truth"
      Description = "An answered list is exactly the created sessions whose stop has not been answered."
      Check = fun t ->
        firstViolation t (fun i s ->
          match s.Op, s.Reply with
          | Op.List, Reply.Answered ->
            let truth = Set.difference s.Created s.StoppedAnswered
            match s.Listed = truth with
            | true -> None
            | false -> Some(sprintf "step #%d: listed %A but the truth is %A" i (Set.toList s.Listed) (Set.toList truth))
          | _ -> None) }

  let all : Invariant list =
    [ aBusyStopNeverDelaysAnAnswer; aStopIsAnsweredAtOnce; stopsOfDifferentSessionsOverlap; theRegistryTellsTheTruth ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
