namespace SageFs.Simulation

open SageFs.Simulation.SessionReaperSim

/// Named invariants over a `SessionReaperSim.Trace`. Same stable-id / `Holds`-vs-`Violated` shape as the other
/// simulations.
module SessionReaperInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private firstViolation (t: Trace) (violates: int -> Snapshot -> string option) : Outcome =
    t.Snapshots
    |> List.mapi violates
    |> List.tryPick id
    |> function
       | Some message -> Outcome.Violated(sprintf "%s (seed=%d, ops=%A)" message t.Scenario.Seed t.Scenario.Ops)
       | None -> Outcome.Holds

  let private isSweep (s: Snapshot) : bool =
    match s.Op with
    | Op.Sweep -> true
    | _ -> false

  /// never-reaps-a-session-a-person-made: a session with no ledger record is never stopped, however old and idle.
  let neverReapsAPersonsSession : Invariant =
    { Id = "never-reaps-a-session-a-person-made"
      Description = "No sweep stops a session that has no owner record."
      Check = fun t ->
        firstViolation t (fun i s ->
          let hit = Set.intersect s.Reaped s.PersonMade
          match Set.isEmpty hit with
          | true -> None
          | false -> Some(sprintf "op #%d (%A) stopped person-made sessions %A" i s.Op (Set.toList hit))) }

  /// never-reaps-while-the-owner-is-here: a session whose owner connection is present is never stopped.
  let neverReapsWhileTheOwnerIsHere : Invariant =
    { Id = "never-reaps-while-the-owner-is-here"
      Description = "No sweep stops a session whose owner connection is connected."
      Check = fun t ->
        firstViolation t (fun i s ->
          let hit = Set.intersect s.Reaped s.OwnerHere
          match Set.isEmpty hit with
          | true -> None
          | false -> Some(sprintf "op #%d (%A) stopped sessions %A whose owner was connected" i s.Op (Set.toList hit))) }

  /// reaps-exactly-the-abandoned: a sweep stops exactly the sessions the independent truth calls abandoned: none that are
  /// not (sound), and none left that are (complete).
  let reapsExactlyTheAbandoned : Invariant =
    { Id = "reaps-exactly-the-abandoned"
      Description = "At every sweep the set stopped equals the set of sessions an agent made whose owner is gone and which are idle, both for the bound counted from no earlier than the daemon's start."
      Check = fun t ->
        firstViolation t (fun i s ->
          match isSweep s && s.Reaped <> s.Due with
          | false -> None
          | true -> Some(sprintf "op #%d (sweep at %A) stopped %A but the truth is %A" i s.Now (Set.toList s.Reaped) (Set.toList s.Due))) }

  /// a-restart-grants-a-grace-of-the-bound: nothing is stopped within the bound of the daemon's start, because presence
  /// is not persisted and a restart makes every owner look gone.
  let aRestartGrantsAGrace : Invariant =
    { Id = "a-restart-grants-a-grace-of-the-bound"
      Description = "A sweep within the bound of the daemon's start stops nothing."
      Check = fun t ->
        firstViolation t (fun i s ->
          match isSweep s && s.InGrace && not (Set.isEmpty s.Reaped) with
          | false -> None
          | true -> Some(sprintf "op #%d (sweep at %A) stopped %A inside the grace after a start" i s.Now (Set.toList s.Reaped))) }

  let all : Invariant list =
    [ neverReapsAPersonsSession; neverReapsWhileTheOwnerIsHere; reapsExactlyTheAbandoned; aRestartGrantsAGrace ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
