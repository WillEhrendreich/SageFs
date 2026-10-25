namespace SageFs.Simulation

open SageFs.Simulation.LiveTestFileRoutingSim

/// Named invariants over a `LiveTestFileRoutingSim.Trace`. Each check compares what the router
/// decided with the expectation the sim recorded from its own bookkeeping, so none of them calls
/// the router under test.
module LiveTestFileRoutingInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private receivedBy (d: RoutingDecision) : int list =
    match d.Routed with
    | Routed.Fallback -> []
    | Routed.Sessions idxs -> idxs

  let private firstViolation (t: Trace) (describe: RoutingDecision -> string option) : Outcome =
    t.Final.Decisions
    |> List.tryPick describe
    |> function
       | Some msg -> Outcome.Violated (sprintf "%s (seed=%d, ops=%A)" msg t.Scenario.Seed t.Scenario.Ops)
       | None -> Outcome.Holds

  /// A save reaches exactly the owning sessions that have live testing on, each once.
  let reachesExactlyTheOwnersThatHaveItOn : Invariant =
    { Id = "a-save-reaches-exactly-the-owning-sessions-that-have-live-testing-on"
      Description =
        "For a save in a session's directory, the sessions that receive it are exactly the sessions owning that directory that have live testing on, each once. Which session is primary does not matter."
      Check = fun t ->
        firstViolation t (fun d ->
          let got = receivedBy d
          match List.length (List.distinct got) <> List.length got, Set.ofList got = d.ExpectedReceivers with
          | true, _ -> Some (sprintf "path %s: a session received the change more than once, got %A" d.Path got)
          | false, true -> None
          | false, false ->
            Some (sprintf "path %s (primary #%d): expected receivers %A, router decided %A" d.Path d.Primary (Set.toList d.ExpectedReceivers) got)) }

  /// The primary session's activation never gates another session. Stated on its own so the
  /// original defect names itself when it comes back.
  let primaryNeverGatesAnotherSession : Invariant =
    { Id = "the-primary-sessions-activation-never-gates-another-session"
      Description =
        "A session that has live testing on receives the saves in its directory even when the primary session has it off."
      Check = fun t ->
        firstViolation t (fun d ->
          let missing = d.ExpectedReceivers |> Set.filter (fun j -> not (List.contains j (receivedBy d)))
          match Set.isEmpty missing with
          | true -> None
          | false ->
            Some (sprintf "path %s: session(s) %A have live testing on but never received it (primary #%d)" d.Path (Set.toList missing) d.Primary)) }

  /// A session with live testing off, or one that does not own the path, never receives it.
  let aSessionWithItOffReceivesNothing : Invariant =
    { Id = "a-session-with-live-testing-off-or-not-owning-the-path-receives-nothing"
      Description =
        "No session receives a change unless it owns the path and has live testing on, and a file no session owns goes to the fallback."
      Check = fun t ->
        firstViolation t (fun d ->
          let extra = receivedBy d |> List.filter (fun j -> not (Set.contains j d.ExpectedReceivers))
          match extra, Set.isEmpty d.ExpectedOwners, d.Routed with
          | [], true, Routed.Sessions _ -> Some (sprintf "path %s is owned by no session but was not routed to the fallback" d.Path)
          | [], _, _ -> None
          | _ -> Some (sprintf "path %s: session(s) %A received it but own it with live testing off, or do not own it" d.Path extra)) }

  let all : Invariant list =
    [ reachesExactlyTheOwnersThatHaveItOn
      primaryNeverGatesAnotherSession
      aSessionWithItOffReceivesNothing ]

  /// The invariants that failed for a trace, as (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
